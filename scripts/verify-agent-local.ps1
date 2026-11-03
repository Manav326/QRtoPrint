[CmdletBinding()]
param(
    [ValidateRange(10, 180)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$statusPath = '/api/v1/local/status'
$loopbackUri = "http://127.0.0.1:17842$statusPath"
$loopbackStatus = $null

function Test-PrivateIpv4Address {
    param([Parameter(Mandatory = $true)][string]$Address)

    $parsed = [System.Net.IPAddress]::None
    if (-not [System.Net.IPAddress]::TryParse($Address, [ref]$parsed) -or
        $parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        return $false
    }

    $octets = $parsed.GetAddressBytes()
    return $octets[0] -eq 10 -or
        ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or
        ($octets[0] -eq 192 -and $octets[1] -eq 168)
}

do {
    try {
        $status = Invoke-RestMethod -Uri $loopbackUri -Method Get -TimeoutSec 3
        if ($status.status -eq 'LOCAL_ENDPOINT_READY' -and $status.agentDeviceId) {
            $loopbackStatus = $status
            break
        }
    }
    catch {
        # The Windows Service may still be starting. Retry until the bounded deadline.
    }

    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)

if ($null -eq $loopbackStatus) {
    throw "SmartPrint did not become ready on local port 17842 within $TimeoutSeconds seconds. Check the Windows service and its pairing."
}

$privateAddresses = @(
    Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred -ErrorAction SilentlyContinue |
        Where-Object { Test-PrivateIpv4Address -Address $_.IPAddress } |
        Select-Object -ExpandProperty IPAddress -Unique
)

foreach ($address in $privateAddresses) {
    try {
        $lanUri = "http://{0}:17842{1}" -f $address, $statusPath
        $lanStatus = Invoke-RestMethod -Uri $lanUri -Method Get -TimeoutSec 3
        if ($lanStatus.status -eq 'LOCAL_ENDPOINT_READY' -and
            $lanStatus.agentDeviceId -eq $loopbackStatus.agentDeviceId) {
            Write-Host ("SmartPrint Agent is paired and responding on local address {0}:17842." -f $address) -ForegroundColor Green
            Write-Host "This confirms the selected local interface. Use the Shop Connection phone test to verify another device can reach it." -ForegroundColor Yellow
            exit 0
        }
    }
    catch {
        # The listener accepts only the adapter selected in the Shop Connection panel.
        # Other private addresses on this computer are expected to be rejected.
    }
}

throw "SmartPrint responds on this computer, but not on any private network address. Open the Shop Connection panel, select the Wi-Fi or network customers use, and retry. A second-phone test is still required to check router/device isolation."
