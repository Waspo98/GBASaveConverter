$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "GBASaveConverter.exe"
$buildScript = Join-Path $PSScriptRoot "Build-GBASaveConverter.ps1"

if (-not (Test-Path -LiteralPath $exe)) {
    & $buildScript
}

& $exe --once
exit $LASTEXITCODE
