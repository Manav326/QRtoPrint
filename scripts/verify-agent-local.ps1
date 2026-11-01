[CmdletBinding()]
param(
    [ValidateRange(10, 180)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$statusUri = 'http://127.0.0.1:17842/api/v1/local/status'

do {
    try {
        $status = Invoke-RestMethod -Uri $statusUri -Method Get -TimeoutSec 3
        if ($status.status -eq 'LOCAL_ENDPOINT_READY' -and $status.agentDeviceId) {
            Write-Host 'SmartPrint local Agent is paired and responding.' -ForegroundColor Green
            exit 0
        }
    }
    catch {
        # The Windows Service may still be starting. Retry until the bounded deadline.
    }

    Start-Sleep -Seconds 2
} while ([DateTime]::UtcNow -lt $deadline)

throw "SmartPrint did not become ready on local port 17842 within $TimeoutSeconds seconds. Check the Windows service and firewall configuration."
