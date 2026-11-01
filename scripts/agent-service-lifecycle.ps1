# This script is embedded in the SmartPrint installer and runs elevated.
# Exit code 10 means the service was running and was stopped by this installer.
# Exit code 0 means there was no service to stop, or it was already stopped.
[CmdletBinding()]
param(
    [Parameter()]
    [switch]$Start
)

$ErrorActionPreference = "Stop"
$serviceName = "QRtoPrintSmartPrintAgent"

try {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $service) {
        exit 0
    }

    if ($Start) {
        if ($service.Status -ne "Running") {
            Start-Service -Name $serviceName
            (Get-Service -Name $serviceName).WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(30)
            )
        }
        exit 0
    }

    if ($service.Status -eq "Stopped") {
        exit 0
    }

    Stop-Service -Name $serviceName -Force
    $stoppedService = Get-Service -Name $serviceName
    $stoppedService.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30)
    )

    if ((Get-Service -Name $serviceName).Status -ne "Stopped") {
        throw "The SmartPrint Windows service did not stop."
    }

    exit 10
}
catch {
    Write-Error $_
    exit 1
}
