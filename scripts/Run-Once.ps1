$DryRun = $false
$Status = $false
foreach ($arg in $args) {
    if ($arg -ieq "--dry-run") {
        $DryRun = $true
    }
    if ($arg -ieq "--status") {
        $Status = $true
    }
}

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "GBASaveConverter.exe"
$buildScript = Join-Path $PSScriptRoot "Build-GBASaveConverter.ps1"

if (-not (Test-Path -LiteralPath $exe)) {
    & $buildScript
}

$converterArgs = if ($Status) { @("--status") } else { @("--once") }
if ($DryRun) {
    $converterArgs += "--dry-run"
}

& $exe @converterArgs
exit $LASTEXITCODE
