$ErrorActionPreference = "Stop"

$serviceName = "GBASaveConverter"
$displayName = "GBA Save Converter"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "GBASaveConverter.exe"
$buildScript = Join-Path $PSScriptRoot "Build-GBASaveConverter.ps1"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    throw "Run this script from an elevated PowerShell prompt."
}

& $buildScript

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Service executable not found at $exe"
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(20))
    }

    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

$binPath = '"' + $exe + '"'
& sc.exe create $serviceName binPath= $binPath start= auto DisplayName= $displayName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create service $serviceName"
}

& sc.exe description $serviceName "Keeps GBA .sav and .srm battery save files paired for Pizza Boy and RetroArch." | Out-Null

Start-Service -Name $serviceName
Start-Sleep -Seconds 2

Get-Service -Name $serviceName | Select-Object Name,Status,StartType
