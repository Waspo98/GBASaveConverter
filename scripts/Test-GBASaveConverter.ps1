$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$source = Join-Path $root "src\Program.cs"
$testRoot = Join-Path $root "tmp-test\automated"
$exe = Join-Path $testRoot "GBASaveConverter.test.exe"
$saveDirectory = Join-Path $testRoot "saves"
$configPath = Join-Path $testRoot "GBASaveConverter.ini"

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Reset-TestRoot {
    $safeRoot = [System.IO.Path]::GetFullPath((Join-Path $root "tmp-test"))
    $safeTarget = [System.IO.Path]::GetFullPath($testRoot)
    if (-not $safeTarget.StartsWith($safeRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete unexpected test path: $safeTarget"
    }

    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $saveDirectory | Out-Null
}

function Write-Save {
    param(
        [string] $Path,
        [string] $Value
    )

    [System.IO.File]::WriteAllText($Path, $Value, [System.Text.Encoding]::ASCII)
}

function Read-Save {
    param([string] $Path)

    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::ASCII)
}

function Invoke-Converter {
    param([switch] $DryRun)

    $arguments = @("--once", "--config", $configPath)
    if ($DryRun) {
        $arguments += "--dry-run"
    }

    & $exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Converter exited with $LASTEXITCODE"
    }
}

function New-TestConfig {
    @"
SaveDirectory=$saveDirectory
LogDirectory=logs
BackupDirectory=backups
StateDirectory=state
BackupBeforeOverwrite=true
BackupRetentionDays=0
SyncthingScanAfterWrite=false
DebounceSeconds=1
FullScanMinutes=10
IgnoreOwnWritesSeconds=1
StabilityCheckMilliseconds=100
LogToConsole=true
"@ | Set-Content -LiteralPath $configPath -Encoding UTF8
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "C# compiler not found at $compiler"
}

Reset-TestRoot
New-TestConfig

& $compiler `
    /nologo `
    /optimize+ `
    /platform:x64 `
    /target:exe `
    /out:$exe `
    /reference:System.Core.dll `
    /reference:System.ServiceProcess.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

# Missing .sav is created from .srm.
$onlySrm = Join-Path $saveDirectory "OnlySrm"
Write-Save "$onlySrm.srm" "srm-only"
Invoke-Converter
Assert-True (Test-Path -LiteralPath "$onlySrm.sav") "Expected missing .sav to be created."
Assert-True ((Read-Save "$onlySrm.sav") -eq "srm-only") "Expected .sav content to match .srm."

# Missing .srm is created from .sav.
$onlySav = Join-Path $saveDirectory "OnlySav"
Write-Save "$onlySav.sav" "sav-only"
Invoke-Converter
Assert-True (Test-Path -LiteralPath "$onlySav.srm") "Expected missing .srm to be created."
Assert-True ((Read-Save "$onlySav.srm") -eq "sav-only") "Expected .srm content to match .sav."

# A stale older hash touched later does not overwrite the real newest save.
$stale = Join-Path $saveDirectory "StaleTouch"
Write-Save "$stale.sav" "base"
Write-Save "$stale.srm" "base"
Invoke-Converter
Start-Sleep -Milliseconds 250
Write-Save "$stale.srm" "newer"
Invoke-Converter
Assert-True ((Read-Save "$stale.sav") -eq "newer") "Expected .srm update to copy to .sav."
Write-Save "$stale.sav" "base"
(Get-Item -LiteralPath "$stale.sav").LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(5)
Invoke-Converter
Assert-True ((Read-Save "$stale.sav") -eq "newer") "Expected known older .sav content to be overwritten by current .srm."
Assert-True ((Read-Save "$stale.srm") -eq "newer") "Expected current .srm content to survive stale .sav."

# If both sides changed differently, neither side is overwritten.
$conflict = Join-Path $saveDirectory "ConflictCase"
Write-Save "$conflict.sav" "same"
Write-Save "$conflict.srm" "same"
Invoke-Converter
Write-Save "$conflict.sav" "left"
Write-Save "$conflict.srm" "right"
Invoke-Converter
Assert-True ((Read-Save "$conflict.sav") -eq "left") "Expected conflict .sav to remain untouched."
Assert-True ((Read-Save "$conflict.srm") -eq "right") "Expected conflict .srm to remain untouched."

# Dry-run reports work without writing files.
$dryRun = Join-Path $saveDirectory "DryRun"
Write-Save "$dryRun.srm" "dry"
Invoke-Converter -DryRun
Assert-True (-not (Test-Path -LiteralPath "$dryRun.sav")) "Expected dry-run not to create .sav."

& $exe --status --config $configPath | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Status exited with $LASTEXITCODE"
}

Write-Host "GBASaveConverter automated tests passed."
