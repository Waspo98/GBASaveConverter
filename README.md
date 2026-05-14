# GBASaveConverter

GBASaveConverter is a small Windows service that keeps Game Boy Advance battery save files paired between RetroArch/mGBA and standalone GBA emulators.

RetroArch's mGBA core commonly uses `.srm` battery saves. Some standalone emulators use `.sav` for the same underlying save data. GBASaveConverter watches a save folder and keeps matching files in sync:

```text
Game Name.srm
Game Name.sav
```

When one side changes, the service waits briefly, compares the two files, and uses a small state journal to decide which side really changed. If both sides changed differently, it records a conflict and leaves both files untouched.

This was built for a Syncthing setup where RetroArch devices and an Android standalone emulator share the same save folder. It has only been tested with Pizza Boy GBA as the standalone Android emulator.

## What It Does

- Watches one folder for `.srm` and `.sav` files.
- Creates the missing twin when only one extension exists.
- Syncs `.srm` to `.sav` or `.sav` to `.srm`, depending on journal history and file hashes.
- Compares file hashes before copying, so identical pairs are left alone.
- Tracks up to 100 recent known save hashes per game in `state\pairs.tsv` to avoid old saves winning just because they were touched later.
- Records true conflicts instead of guessing when both sides changed differently.
- Debounces file events before syncing so it does not copy half-written saves.
- Ignores its own recent writes to avoid event loops.
- Runs a startup reconciliation scan and periodic safety scans.
- Backs up overwritten files before replacing them.
- Optionally queues debounced Syncthing local API scans after writes so Docker-hosted Syncthing notices converted files without blocking save conversion.
- Runs as an automatic Windows service.

## Default Layout

The default config watches:

```text
C:\RetroArch\saves\mGBA
```

With a save named `Pokemon FireRed`, the folder can contain:

```text
C:\RetroArch\saves\mGBA\Pokemon FireRed.srm
C:\RetroArch\saves\mGBA\Pokemon FireRed.sav
```

RetroArch can use the `.srm` file, while Pizza Boy GBA can use the `.sav` file.

## Safety Model

GBASaveConverter is intentionally conservative:

- It waits `DebounceSeconds` before acting on changes.
- It checks that the file appears stable before reading it.
- It hashes both files before copying.
- It only overwrites when the contents differ.
- If only one side changed since the last journal state, that side wins.
- If a file is touched later but its hash matches older journal history, it is treated as stale and does not overwrite the current save.
- If a one-sided changed file has an unknown hash but an older timestamp than the journal and unchanged twin, it is recorded as a conflict instead of winning.
- If both files changed differently, both are backed up and left untouched for manual review.
- If there is no journal history yet, newest modified time wins as the fallback.
- Before overwriting an existing file, it writes a timestamped backup under `backups\YYYYMMDD`.
- Files with `.sync-conflict-` in the name are ignored so Syncthing conflict copies are not treated as normal save pairs.

The main edge case is playing the same game on two devices before Syncthing has finished syncing both sides. In that case, GBASaveConverter records a conflict if both sides changed differently. The app does not merge save data.

## Requirements

- Windows
- .NET Framework 4.x, included with modern Windows installs
- PowerShell for the helper scripts

No .NET SDK is required. The build script uses the built-in .NET Framework C# compiler at:

```text
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

## Build

```powershell
.\scripts\Build-GBASaveConverter.ps1
```

This creates:

```text
GBASaveConverter.exe
```

## Configure

Copy the example config:

```powershell
Copy-Item .\GBASaveConverter.ini.example .\GBASaveConverter.ini
```

Edit `GBASaveConverter.ini` as needed:

```ini
SaveDirectory=C:\RetroArch\saves\mGBA
LogDirectory=logs
BackupDirectory=backups
StateDirectory=state
BackupBeforeOverwrite=true
BackupRetentionDays=0
SyncthingScanAfterWrite=false
SyncthingApiBaseUrl=http://127.0.0.1:8384/rest
SyncthingApiKey=
SyncthingFolderId=
SyncthingScanSubdirectory=
SyncthingScanDebounceSeconds=2
SyncthingRequestTimeoutSeconds=5
DebounceSeconds=5
FullScanMinutes=10
IgnoreOwnWritesSeconds=10
StabilityCheckMilliseconds=500
LogToConsole=true
```

## Syncthing Scan Hook

If Syncthing runs in Docker on Windows, filesystem watcher events from host bind mounts may not reliably reach the Syncthing container. In that setup, Syncthing might not notice that GBASaveConverter created or updated a paired save until a periodic rescan.

Enable the scan hook to queue Syncthing scans for the source and converted target after each conversion:

```ini
SyncthingScanAfterWrite=true
SyncthingApiBaseUrl=http://127.0.0.1:8384/rest
SyncthingApiKey=your-local-syncthing-api-key
SyncthingFolderId=your-syncthing-folder-id
SyncthingScanSubdirectory=mGBA
SyncthingScanDebounceSeconds=2
SyncthingRequestTimeoutSeconds=5
```

For a Syncthing folder rooted at `C:\RetroArch\saves` and saves in `C:\RetroArch\saves\mGBA`, use `SyncthingScanSubdirectory=mGBA`.

Keep `GBASaveConverter.ini` private. It can contain your Syncthing API key and is ignored by Git.

## Run One Reconciliation Scan

Useful before installing the service:

```powershell
.\scripts\Run-Once.ps1
```

Preview what would happen without writing files:

```powershell
.\scripts\Run-Once.ps1 --dry-run
```

Show a quick folder summary:

```powershell
.\scripts\Run-Once.ps1 --status
```

## Test

Run the disposable test harness:

```powershell
.\scripts\Test-GBASaveConverter.ps1
```

The tests compile to `tmp-test\automated`, use temporary save files, and cover missing twins, stale touched saves, conflict detection, dry-run, and status output.

## Install As A Windows Service

Run PowerShell as Administrator:

```powershell
.\scripts\Install-GBASaveConverterService.ps1
```

The service is installed as:

```text
GBASaveConverter
```

It starts automatically on boot.

Useful service commands:

```powershell
Get-Service GBASaveConverter
Stop-Service GBASaveConverter
Start-Service GBASaveConverter
```

## Deploy An Update

Run PowerShell as Administrator:

```powershell
.\scripts\Deploy-GBASaveConverterService.ps1
```

This stops the service, rebuilds `GBASaveConverter.exe`, and starts the service again.

## Uninstall The Service

Run PowerShell as Administrator:

```powershell
.\scripts\Uninstall-GBASaveConverterService.ps1
```

## Logs And Backups

By default:

```text
logs\GBASaveConverter.log
backups\YYYYMMDD\*.bak
state\pairs.tsv
```

Logs, backups, local state, local config, and build output are ignored by Git.

`BackupRetentionDays=0` keeps backups forever. Set it to a positive number to delete `.bak` files older than that many days during reconciliation.

## License

MIT License. See [LICENSE](LICENSE).
