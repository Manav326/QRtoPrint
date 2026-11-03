[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$key = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($key)
$value = [Convert]::ToBase64String($key)
[System.Security.Cryptography.CryptographicOperations]::ZeroMemory($key)

Write-Host "QRtoPrint document encryption key generated." -ForegroundColor Green
Write-Host "Store this value in your deployment secret store as Documents__EncryptionKey." -ForegroundColor Yellow
Write-Host ""
Write-Output $value
Write-Host ""
Write-Host "Do not commit this key, send it over chat, or reuse it between environments." -ForegroundColor Yellow
