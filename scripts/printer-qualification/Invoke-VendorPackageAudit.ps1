[CmdletBinding()]
param(
    [string] $ManifestPath = (Join-Path $PSScriptRoot "printer-driver-manifest.json"),
    [string[]] $DriverIds = @("ricoh-universal-pcl6-x64", "kyocera-kx-universal-x64"),
    [string] $ReportDirectory = (Join-Path $PSScriptRoot "..\..\artifacts\printer-driver-package-audit")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$ProgressPreference = "SilentlyContinue"

function Test-AllowedVendorUri {
    param([uri] $Uri, [string[]] $AllowedHosts)
    if ($Uri.Scheme -ne "https") { return $false }
    foreach ($hostName in $AllowedHosts) {
        $allowed = $hostName.Trim().TrimStart(".").ToLowerInvariant()
        $actual = $Uri.IdnHost.ToLowerInvariant()
        if ($actual -eq $allowed -or $actual.EndsWith("." + $allowed, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Receive-OfficialPackage {
    param([uri] $Uri, [string] $Destination, [string[]] $AllowedHosts)

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(15)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd("QRtoPrint-NonExecutingPackageAudit/1.0")
    $response = $null

    try {
        $current = $Uri
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            if (-not (Test-AllowedVendorUri -Uri $current -AllowedHosts $AllowedHosts)) {
                throw "Download URL or redirect host '$($current.IdnHost)' is outside the explicit vendor host allowlist."
            }

            $response = $client.GetAsync($current, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            if ($response.StatusCode -in @(
                [System.Net.HttpStatusCode]::MovedPermanently,
                [System.Net.HttpStatusCode]::Found,
                [System.Net.HttpStatusCode]::SeeOther,
                [System.Net.HttpStatusCode]::TemporaryRedirect,
                [System.Net.HttpStatusCode]::PermanentRedirect
            )) {
                $location = $response.Headers.Location
                $response.Dispose()
                $response = $null
                if ($null -eq $location) { throw "Official package host returned a redirect without Location." }
                $current = [uri]::new($current, $location)
                continue
            }

            $response.EnsureSuccessStatusCode()
            if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt 1GB) {
                throw "Official package exceeds the 1 GiB audit limit."
            }

            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $target = [System.IO.File]::Create($Destination)
            try {
                $buffer = [byte[]]::new(65536)
                [long] $total = 0
                while (($count = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $total += $count
                    if ($total -gt 1GB) { throw "Official package exceeded the 1 GiB audit limit." }
                    $target.Write($buffer, 0, $count)
                }
            }
            finally {
                $target.Dispose()
                $source.Dispose()
            }

            return [pscustomobject]@{
                finalUrl = $current.AbsoluteUri
                contentType = [string]$response.Content.Headers.ContentType
                contentLengthHeader = $response.Content.Headers.ContentLength
            }
        }

        throw "Official package exceeded the five-redirect limit."
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
    }
}

function Get-ArchiveExtractor {
    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidate = Join-Path $env:ProgramFiles "7-Zip\7z.exe"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    return $null
}

function Get-InfSummary {
    param([string] $Path, [string] $Root)
    $relative = $Path.Substring($Root.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
    $text = Get-Content -LiteralPath $Path -TotalCount 250
    $metadata = @()
    foreach ($line in $text) {
        if ($line -match '^\s*(Provider|Class|ClassGuid|DriverVer|CatalogFile(?:\.[A-Za-z0-9_]+)?)\s*=\s*(.+?)\s*$') {
            $metadata += [pscustomobject]@{ key = $matches[1]; value = $matches[2] }
        }
    }
    return [pscustomobject]@{ path = $relative; metadata = $metadata }
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw "Unsupported printer driver manifest schema." }
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null

$failures = @()
foreach ($driverId in $DriverIds) {
    $matches = @($manifest.drivers | Where-Object { $_.id -ceq $driverId })
    if ($matches.Count -ne 1) { throw "Driver '$driverId' must exist exactly once in the manifest." }
    $driver = $matches[0]

    $runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("smartprint-package-audit-" + [guid]::NewGuid().ToString("N"))
    $extractRoot = Join-Path $runRoot "extracted"
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    $packagePath = Join-Path $runRoot ([string]$driver.packageFileName)
    $reportPath = Join-Path $ReportDirectory ($driverId + ".json")

    try {
        if ([string]$driver.ciApproval -notlike "pending_*" -or [string]$driver.packageUrl -notmatch '^https://') {
            throw "Package audit is restricted to explicit pending entries with an official HTTPS URL."
        }
        if (-not (Test-AllowedVendorUri -Uri ([uri]$driver.packageUrl) -AllowedHosts @($driver.allowedDownloadHosts))) {
            throw "Manifest package URL is not on its allowlisted official vendor host."
        }

        $download = Receive-OfficialPackage -Uri ([uri]$driver.packageUrl) -Destination $packagePath -AllowedHosts @($driver.allowedDownloadHosts)
        $fileInfo = Get-Item -LiteralPath $packagePath
        $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $packageSignature = Get-AuthenticodeSignature -LiteralPath $packagePath
        $packageSigner = if ($packageSignature.SignerCertificate) {
            [pscustomobject]@{
                subject = $packageSignature.SignerCertificate.Subject
                thumbprint = $packageSignature.SignerCertificate.Thumbprint
                status = [string]$packageSignature.Status
            }
        } else { $null }

        $extractStatus = "NOT_ATTEMPTED"
        $extractTool = $null
        switch ([string]$driver.packageFormat) {
            "zip" {
                Expand-Archive -LiteralPath $packagePath -DestinationPath $extractRoot -Force
                $extractTool = "Expand-Archive"
                $extractStatus = "EXTRACTED"
            }
            "exe-archive" {
                $extractTool = Get-ArchiveExtractor
                if ($null -eq $extractTool) {
                    $extractStatus = "BLOCKED_7ZIP_NOT_AVAILABLE"
                } else {
                    & $extractTool "x" $packagePath "-o$extractRoot" "-y" "-bd" 2>&1 | Out-Null
                    if ($LASTEXITCODE -ne 0) {
                        $extractStatus = "EXTRACTION_FAILED"
                    } else {
                        $extractStatus = "EXTRACTED_WITH_7ZIP"
                    }
                }
            }
            default {
                throw "Package audit supports only ZIP or non-executing 7-Zip inspection of an EXE archive."
            }
        }

        $files = @(Get-ChildItem -LiteralPath $extractRoot -File -Recurse -ErrorAction SilentlyContinue)
        $rootPrefix = [System.IO.Path]::GetFullPath($extractRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $escapedFiles = @($files | Where-Object {
            -not ([System.IO.Path]::GetFullPath($_.FullName)).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($escapedFiles.Count -gt 0) { throw "Archive extracted a file outside the isolated extraction directory." }

        $infs = @($files | Where-Object { $_.Extension -ieq ".inf" } | ForEach-Object { Get-InfSummary -Path $_.FullName -Root $extractRoot })
        $catalogs = @($files | Where-Object { $_.Extension -ieq ".cat" } | ForEach-Object {
            $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
            [pscustomobject]@{
                path = $_.FullName.Substring($extractRoot.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
                status = [string]$signature.Status
                signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
                thumbprint = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { $null }
                signerMatchesVendor = ($null -ne $signature.SignerCertificate -and $signature.SignerCertificate.Subject -match [regex]::Escape([string]$driver.catalogSignerContains))
            }
        })

        $signedCatalogs = @($catalogs | Where-Object { $_.status -eq "Valid" -and $_.signerMatchesVendor })
        $result = [pscustomobject]@{
            schemaVersion = 1
            driverId = $driverId
            manufacturer = $driver.manufacturer
            family = $driver.family
            version = $driver.version
            packageFileName = $fileInfo.Name
            packageSizeBytes = $fileInfo.Length
            packageSha256 = $hash
            packageUrl = $download.finalUrl
            packageContentType = $download.contentType
            embeddedAuthenticode = [string]$packageSignature.Status
            embeddedSigner = $packageSigner
            extractionStatus = $extractStatus
            extractionTool = $extractTool
            extractedFileCount = $files.Count
            infFiles = $infs
            catalogCount = $catalogs.Count
            validVendorCatalogCount = $signedCatalogs.Count
            catalogs = $catalogs
            physicalPrinterVerified = $false
            packageExecuted = $false
            completedAtUtc = [DateTimeOffset]::UtcNow
        }
        $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reportPath -Encoding utf8
        Write-Host "$driverId package SHA-256: $hash"
        Write-Host "$driverId extraction: $extractStatus; INF files: $($infs.Count); valid vendor catalogs: $($signedCatalogs.Count)"

        if ($extractStatus -notin @("EXTRACTED", "EXTRACTED_WITH_7ZIP") -or $infs.Count -eq 0 -or $signedCatalogs.Count -eq 0) {
            $failures += "$driverId did not produce an extracted INF and a valid vendor-signed catalog."
        }
        if ($packageSignature.Status -eq "HashMismatch") {
            $failures += "$driverId package Authenticode signature reported a hash mismatch."
        }
    }
    catch {
        $failure = [pscustomobject]@{
            schemaVersion = 1
            driverId = $driverId
            status = "PACKAGE_AUDIT_FAILED"
            error = $_.Exception.Message
            packageExecuted = $false
            physicalPrinterVerified = $false
            completedAtUtc = [DateTimeOffset]::UtcNow
        }
        $failure | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
        $failures += "$driverId audit failed: $($_.Exception.Message)"
    }
    finally {
        if (Test-Path -LiteralPath $runRoot) {
            Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

Write-Host "All selected official vendor packages passed the non-executing package audit."
