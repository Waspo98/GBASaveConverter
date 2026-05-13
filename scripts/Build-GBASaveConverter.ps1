param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$source = Join-Path $root "src\Program.cs"
$output = Join-Path $root "GBASaveConverter.exe"

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "C# compiler not found at $compiler"
}

if (-not (Test-Path -LiteralPath $source)) {
    throw "Source file not found at $source"
}

$optimize = if ($Configuration -ieq "Debug") { "/optimize-" } else { "/optimize+" }

& $compiler `
    /nologo `
    $optimize `
    /platform:x64 `
    /target:exe `
    /out:$output `
    /reference:System.Core.dll `
    /reference:System.ServiceProcess.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Built $output"
