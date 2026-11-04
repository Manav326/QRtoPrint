[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $DriverId,

    [string] $ManifestPath = (Join-Path $PSScriptRoot "printer-driver-manifest.json"),

    [string] $ReportDirectory = (Join-Path $PSScriptRoot "..\..\artifacts\printer-driver-qualification")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Report {
    param([string] $Path, [object] $Value)
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8
}

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

function Receive-ApprovedDriverPackage {
    param([uri] $Uri, [string] $Destination, [string[]] $AllowedHosts)

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd("QRtoPrint-DriverQualification/1.0")
    $response = $null

    try {
        $current = $Uri
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            if (-not (Test-AllowedVendorUri -Uri $current -AllowedHosts $AllowedHosts)) {
                throw "Package URL or redirect host '$($current.IdnHost)' is outside the approved HTTPS vendor-host allowlist."
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
                if ($null -eq $location) { throw "Package server returned a redirect without Location." }
                $current = [uri]::new($current, $location)
                continue
            }

            $response.EnsureSuccessStatusCode()
            if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt 1GB) {
                throw "Package exceeds the 1 GiB download limit."
            }

            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $target = [System.IO.File]::Create($Destination)
            try {
                $buffer = [byte[]]::new(65536)
                [long] $total = 0
                while (($count = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $total += $count
                    if ($total -gt 1GB) { throw "Package exceeded the 1 GiB download limit." }
                    $target.Write($buffer, 0, $count)
                }
            }
            finally {
                $target.Dispose()
                $source.Dispose()
            }
            return
        }
        throw "Package exceeded the five-redirect limit."
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
    }
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $null -eq $manifest.drivers) {
    throw "Unsupported printer driver manifest schema."
}

$ids = @($manifest.drivers | ForEach-Object { [string]$_.id })
if ($ids.Count -ne ($ids | Sort-Object -Unique).Count) {
    throw "Printer driver manifest contains duplicate IDs."
}

foreach ($item in $manifest.drivers) {
    if ([string]$item.id -notmatch '^[a-z0-9][a-z0-9-]{2,80}$') {
        throw "Invalid driver ID '$($item.id)'."
    }
    if ([string]$item.officialSupportUrl -notmatch '^https://') {
        throw "Driver '$($item.id)' has no HTTPS official support URL."
    }

    if ($item.ciApproval -eq "approved") {
        if (-not $item.packageUrl -or
            [string]$item.packageSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            $item.packageFormat -notin @("zip", "cab") -or
            [string]::IsNullOrWhiteSpace([string]$item.infRelativePath) -or
            [string]::IsNullOrWhiteSpace([string]$item.driverName) -or
            [string]::IsNullOrWhiteSpace([string]$item.catalogSignerContains) -or
            @($item.allowedDownloadHosts).Count -eq 0 -or
            $item.licenseReview -ne "approved") {
            throw "Approved driver '$($item.id)' lacks a pinned URL/hash, ZIP/CAB format, INF, driver name, trusted signer, host allowlist or license approval."
        }

        if (-not (Test-AllowedVendorUri -Uri ([uri]$item.packageUrl) -AllowedHosts @($item.allowedDownloadHosts))) {
            throw "Approved driver '$($item.id)' package URL is not on its allowlisted HTTPS vendor hosts."
        }
    }
}

if ($DriverId -eq "manifest-only") {
    Write-Host "Manifest validation passed: $($manifest.drivers.Count) driver entries."
    return
}

$selected = @($manifest.drivers | Where-Object { $_.id -ceq $DriverId })
if ($selected.Count -ne 1) {
    throw "Driver ID '$DriverId' was not found exactly once."
}
$driver = $selected[0]

if ($driver.ciApproval -ne "approved" -or $driver.licenseReview -ne "approved") {
    throw "Driver '$DriverId' is not approved for CI installation. CI approval: '$($driver.ciApproval)'; license review: '$($driver.licenseReview)'. No package was downloaded or installed."
}

if (-not (Test-AllowedVendorUri -Uri ([uri]$driver.packageUrl) -AllowedHosts @($driver.allowedDownloadHosts))) {
    throw "Package URL must use HTTPS and an explicitly approved vendor host."
}

if ([System.IO.Path]::IsPathRooted([string]$driver.infRelativePath) -or
    ([string]$driver.infRelativePath) -match '(^|[\\/])\.\.([\\/]|$)') {
    throw "INF path must be relative and cannot traverse outside the extraction directory."
}

$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("smartprint-driver-qa-" + [guid]::NewGuid().ToString("N"))
$extractRoot = Join-Path $runRoot "package"
$packagePath = Join-Path $runRoot ("vendor-driver." + $driver.packageFormat)
$portRoot = Join-Path $ReportDirectory "ports"
$queueName = $null
$portName = $null

New-Item -ItemType Directory -Force -Path $extractRoot, $portRoot, $ReportDirectory | Out-Null

try {
    Receive-ApprovedDriverPackage -Uri ([uri]$driver.packageUrl) -Destination $packagePath -AllowedHosts @($driver.allowedDownloadHosts)

    $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string]$driver.packageSha256).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Driver package SHA-256 mismatch. Expected $expectedHash; received $actualHash."
    }

    switch ($driver.packageFormat) {
        "zip" {
            Expand-Archive -LiteralPath $packagePath -DestinationPath $extractRoot -Force
        }
        "cab" {
            $expand = Join-Path $env:WINDIR "System32\expand.exe"
            & $expand "-F:*" $packagePath $extractRoot
            if ($LASTEXITCODE -ne 0) {
                throw "CAB extraction failed with exit code $LASTEXITCODE."
            }
        }
        default {
            throw "Only approved ZIP and CAB INF packages are accepted. Vendor setup EXEs are never executed."
        }
    }

    $infPath = [System.IO.Path]::GetFullPath((Join-Path $extractRoot $driver.infRelativePath))
    $prefix = [System.IO.Path]::GetFullPath($extractRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $infPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $infPath -PathType Leaf) -or
        [System.IO.Path]::GetExtension($infPath) -ne ".inf") {
        throw "Pinned INF path is missing or escapes the extracted package."
    }

    $catalogs = @(Get-ChildItem -LiteralPath $extractRoot -Filter "*.cat" -File -Recurse)
    $verifiedCatalogs = @()
    foreach ($catalog in $catalogs) {
        $signature = Get-AuthenticodeSignature -LiteralPath $catalog.FullName
        if ($signature.Status -eq "Valid" -and
            $signature.SignerCertificate.Subject -like "*$($driver.catalogSignerContains)*") {
            $verifiedCatalogs += [pscustomobject]@{
                file = $catalog.Name
                signer = $signature.SignerCertificate.Subject
                thumbprint = $signature.SignerCertificate.Thumbprint
            }
        }
    }
    if ($verifiedCatalogs.Count -eq 0) {
        throw "No valid signed driver catalog matched approved publisher '$($driver.catalogSignerContains)'."
    }

    $pnputil = Join-Path $env:WINDIR "System32\pnputil.exe"
    $pnputilOutput = & $pnputil "/add-driver" $infPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "PnPUtil staging failed: $($pnputilOutput -join ' ')"
    }

    Add-PrinterDriver -Name $driver.driverName -InfPath $infPath -ErrorAction Stop
    $installed = Get-PrinterDriver -Name $driver.driverName -ErrorAction Stop
    if ($installed.Name -ne $driver.driverName) {
        throw "Installed driver name does not match the approved manifest."
    }

    $suffix = [guid]::NewGuid().ToString("N").Substring(0, 8)
    $queueName = "SmartPrint QA $DriverId $suffix"
    $portName = Join-Path $portRoot ("driver-output-" + $suffix + ".prn")

    Add-PrinterPort -Name $portName -ErrorAction Stop
    Add-Printer -Name $queueName -DriverName $driver.driverName -PortName $portName -ErrorAction Stop

    $queue = Get-Printer -Name $queueName -ErrorAction Stop
    if ($queue.DriverName -ne $driver.driverName -or $queue.PortName -ne $portName) {
        throw "Queue driver/port did not match the approved test configuration."
    }

    $installReport = [pscustomobject]@{
        schemaVersion = 1
        status = "DRIVER_INSTALLED_QUEUE_CREATED"
        driverId = $driver.id
        manufacturer = $driver.manufacturer
        family = $driver.family
        version = $driver.version
        packageFileName = [System.IO.Path]::GetFileName($packagePath)
        packageSha256 = $actualHash
        packageSizeBytes = (Get-Item -LiteralPath $packagePath).Length
        infRelativePath = $driver.infRelativePath
        driverName = $installed.Name
        driverVersion = $installed.DriverVersion
        driverManufacturer = $installed.Manufacturer
        catalogs = $verifiedCatalogs
        queueName = $queue.Name
        portName = $queue.PortName
        windowsVersion = [System.Environment]::OSVersion.Version.ToString()
        qualificationScope = "SIGNED_DRIVER_INSTALL_AND_VIRTUAL_SPOOLER_RENDERING"
        physicalPrinterVerified = $false
        completedAtUtc = [DateTimeOffset]::UtcNow
    }
    Write-Report -Path (Join-Path $ReportDirectory "driver-install.json") -Value $installReport

    @(
        "SMARTPRINT_DRIVER_QUALIFICATION_QUEUE=$queueName"
        "SMARTPRINT_DRIVER_QUALIFICATION_PORT=$portName"
        "SMARTPRINT_DRIVER_QUALIFICATION_DRIVER=$($driver.driverName)"
        "SMARTPRINT_DRIVER_QUALIFICATION_ID=$($driver.id)"
        "SMARTPRINT_DRIVER_QUALIFICATION_OUTPUT=$portName"
        "SMARTPRINT_DRIVER_QUALIFICATION_REPORT=$(Join-Path $ReportDirectory 'driver-rendering.json')"
    ) | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8

    Write-Host "Installed approved driver '$($driver.driverName)' and created isolated queue '$queueName'."
    Write-Host "No physical printer was contacted; the following test renders to a local file-backed port."
}
catch {
    Write-Report -Path (Join-Path $ReportDirectory "driver-install-failure.json") -Value ([pscustomobject]@{
        schemaVersion = 1
        status = "FAILED"
        driverId = $driver.id
        error = $_.Exception.Message
        completedAtUtc = [DateTimeOffset]::UtcNow
        physicalPrinterVerified = $false
    })
    throw
}
finally {
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
