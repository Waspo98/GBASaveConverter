$ErrorActionPreference = "Stop"

$serviceName = "GBASaveConverter"
$root = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $PSScriptRoot "Build-GBASaveConverter.ps1"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated PowerShell prompt."
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
}

& $buildScript

if ($existing) {
    Start-Service -Name $serviceName
    Start-Sleep -Seconds 2
    Get-Service -Name $serviceName | Select-Object Name,Status,StartType
} else {
    Write-Host "Service $serviceName is not installed. Run Install-GBASaveConverterService.ps1 to install it."
}
