$ErrorActionPreference = "Stop"

$serviceName = "GBASaveConverter"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated PowerShell prompt."
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service $serviceName is not installed."
    return
}

if ($existing.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
}

& sc.exe delete $serviceName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to delete service $serviceName"
}

Write-Host "Removed service $serviceName"
