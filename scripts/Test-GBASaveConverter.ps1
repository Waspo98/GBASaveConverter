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
Start-Sleep -Milliseconds 1100
Write-Save "$stale.srm" "newer"
Invoke-Converter
Assert-True ((Read-Save "$stale.sav") -eq "newer") "Expected .srm update to copy to .sav."
Write-Save "$stale.sav" "base"
(Get-Item -LiteralPath "$stale.sav").LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(5)
Invoke-Converter
Assert-True ((Read-Save "$stale.sav") -eq "newer") "Expected known older .sav content to be overwritten by current .srm."
Assert-True ((Read-Save "$stale.srm") -eq "newer") "Expected current .srm content to survive stale .sav."

# A previously unseen stale file with an older timestamp becomes a conflict instead of winning.
$unknownStale = Join-Path $saveDirectory "UnknownStale"
Write-Save "$unknownStale.sav" "current"
Write-Save "$unknownStale.srm" "current"
Invoke-Converter
Write-Save "$unknownStale.sav" "unknown-old"
(Get-Item -LiteralPath "$unknownStale.sav").LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-4)
Invoke-Converter
Assert-True ((Read-Save "$unknownStale.sav") -eq "unknown-old") "Expected unknown stale .sav to remain for manual review."
Assert-True ((Read-Save "$unknownStale.srm") -eq "current") "Expected unknown stale .sav not to overwrite current .srm."

# A legitimate newer .sav still copies forward.
$newSav = Join-Path $saveDirectory "NewSav"
Write-Save "$newSav.sav" "base"
Write-Save "$newSav.srm" "base"
Invoke-Converter
Start-Sleep -Milliseconds 1100
Write-Save "$newSav.sav" "pizza-boy-new"
Invoke-Converter
Assert-True ((Read-Save "$newSav.srm") -eq "pizza-boy-new") "Expected legitimate newer .sav to copy to .srm."

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

# If a conflict is manually resolved, the losing conflict-side hash cannot return and win later.
$resolvedConflict = Join-Path $saveDirectory "ResolvedConflict"
Write-Save "$resolvedConflict.sav" "same"
Write-Save "$resolvedConflict.srm" "same"
Invoke-Converter
Start-Sleep -Milliseconds 1100
Write-Save "$resolvedConflict.sav" "losing-left"
Write-Save "$resolvedConflict.srm" "chosen-right"
Invoke-Converter
Write-Save "$resolvedConflict.sav" "chosen-right"
Invoke-Converter
Write-Save "$resolvedConflict.sav" "losing-left"
(Get-Item -LiteralPath "$resolvedConflict.sav").LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(5)
Invoke-Converter
Assert-True ((Read-Save "$resolvedConflict.sav") -eq "chosen-right") "Expected rejected conflict loser not to overwrite .sav."
Assert-True ((Read-Save "$resolvedConflict.srm") -eq "chosen-right") "Expected rejected conflict loser not to overwrite .srm."

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
