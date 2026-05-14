using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace GBASaveConverter
{
    internal static class Program
    {
        private const string ServiceName = "GBASaveConverter";

        private static int Main(string[] args)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = GetConfigPath(args, Path.Combine(baseDirectory, "GBASaveConverter.ini"));
            var config = ConverterConfig.Load(configPath);
            var dryRun = args.Any(IsDryRunArg);
            var status = args.Any(IsStatusArg);
            var logger = new Logger(config.LogDirectory, config.LogToConsole || args.Any(IsConsoleArg) || dryRun || status);
            var converter = new SaveConverter(config, logger, dryRun);

            try
            {
                if (status)
                {
                    converter.WriteStatus();
                    return 0;
                }

                if (args.Any(IsOnceArg) || dryRun)
                {
                    logger.Info(dryRun ? "Running one dry-run reconciliation pass." : "Running one reconciliation pass.");
                    var success = converter.ReconcileAll(dryRun ? "manual dry run" : "manual once");
                    converter.FlushSyncthingScans();
                    return success ? 0 : 1;
                }

                if (Environment.UserInteractive || args.Any(IsConsoleArg))
                {
                    logger.Info("Starting in console mode. Press Ctrl+C to stop.");
                    using (var runner = new ConverterRunner(converter, logger))
                    {
                        runner.Start();
                        var stop = new ManualResetEvent(false);
                        Console.CancelKeyPress += (sender, eventArgs) =>
                        {
                            eventArgs.Cancel = true;
                            stop.Set();
                        };
                        stop.WaitOne();
                        runner.Stop();
                    }

                    return 0;
                }

                ServiceBase.Run(new ConverterService(converter, logger, ServiceName));
                return 0;
            }
            finally
            {
                converter.Dispose();
            }
        }

        private static bool IsConsoleArg(string arg)
        {
            return arg.Equals("--console", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOnceArg(string arg)
        {
            return arg.Equals("--once", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDryRunArg(string arg)
        {
            return arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStatusArg(string arg)
        {
            return arg.Equals("--status", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetConfigPath(string[] args, string fallback)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring("--config=".Length).Trim('"');
                }

                if (arg.Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1].Trim('"');
                }
            }

            return fallback;
        }
    }

    internal sealed class ConverterService : ServiceBase
    {
        private readonly ConverterRunner runner;
        private readonly Logger logger;

        public ConverterService(SaveConverter converter, Logger logger, string serviceName)
        {
            ServiceName = serviceName;
            CanStop = true;
            AutoLog = true;
            this.logger = logger;
            runner = new ConverterRunner(converter, logger);
        }

        protected override void OnStart(string[] args)
        {
            logger.Info("Service starting.");
            runner.Start();
        }

        protected override void OnStop()
        {
            logger.Info("Service stopping.");
            runner.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                runner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class ConverterRunner : IDisposable
    {
        private readonly SaveConverter converter;
        private readonly Logger logger;
        private readonly object gate = new object();
        private readonly Dictionary<string, DateTime> pendingPairs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> ignoredWrites = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher watcher;
        private Timer debounceTimer;
        private Timer fullScanTimer;
        private bool running;
        private bool disposed;

        public ConverterRunner(SaveConverter converter, Logger logger)
        {
            this.converter = converter;
            this.logger = logger;
        }

        public void Start()
        {
            lock (gate)
            {
                if (running) return;

                Directory.CreateDirectory(converter.Config.SaveDirectory);
                watcher = new FileSystemWatcher(converter.Config.SaveDirectory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = "*.*",
                    EnableRaisingEvents = true,
                };

                watcher.Created += OnFileEvent;
                watcher.Changed += OnFileEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;

                debounceTimer = new Timer(OnDebounceTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                fullScanTimer = new Timer(
                    _ => converter.ReconcileAll("periodic scan"),
                    null,
                    converter.Config.FullScanInterval,
                    converter.Config.FullScanInterval);

                running = true;
            }

            converter.ReconcileAll("startup scan");
            logger.Info("Watching " + converter.Config.SaveDirectory);
        }

        public void Stop()
        {
            lock (gate)
            {
                if (!running) return;
                running = false;

                if (watcher != null)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnFileEvent;
                    watcher.Changed -= OnFileEvent;
                    watcher.Renamed -= OnRenamed;
                    watcher.Error -= OnWatcherError;
                    watcher.Dispose();
                    watcher = null;
                }

                if (debounceTimer != null)
                {
                    debounceTimer.Dispose();
                    debounceTimer = null;
                }

                if (fullScanTimer != null)
                {
                    fullScanTimer.Dispose();
                    fullScanTimer = null;
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
        }

        private void OnFileEvent(object sender, FileSystemEventArgs args)
        {
            QueuePath(args.FullPath);
        }

        private void OnRenamed(object sender, RenamedEventArgs args)
        {
            QueuePath(args.OldFullPath);
            QueuePath(args.FullPath);
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            logger.Error("File watcher error. A periodic scan will recover missed changes.", args.GetException());
            converter.ReconcileAll("watcher recovery scan");
        }

        private void QueuePath(string path)
        {
            if (!SavePair.IsSaveFile(path)) return;

            var now = DateTime.UtcNow;
            lock (gate)
            {
                DateTime ignoreUntil;
                if (ignoredWrites.TryGetValue(path, out ignoreUntil) && ignoreUntil > now)
                {
                    return;
                }

                pendingPairs[SavePair.PairKey(path)] = now.Add(converter.Config.DebounceInterval);
            }
        }

        private void OnDebounceTimer(object state)
        {
            List<string> duePairs;
            var now = DateTime.UtcNow;

            lock (gate)
            {
                if (!running) return;

                duePairs = pendingPairs
                    .Where(pair => pair.Value <= now)
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (var pair in duePairs)
                {
                    pendingPairs.Remove(pair);
                }

                foreach (var key in ignoredWrites.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
                {
                    ignoredWrites.Remove(key);
                }
            }

            foreach (var pair in duePairs)
            {
                converter.ReconcilePair(pair, "watcher event", MarkIgnoredWrite);
            }
        }

        private void MarkIgnoredWrite(string path)
        {
            lock (gate)
            {
                ignoredWrites[path] = DateTime.UtcNow.Add(converter.Config.IgnoreOwnWritesInterval);
            }
        }
    }

    internal sealed class SaveConverter : IDisposable
    {
        private readonly Logger logger;
        private readonly SyncthingScanner syncthingScanner;
        private readonly SaveJournal journal;
        private readonly object reconcileGate = new object();
        private readonly bool dryRun;
        private DateTime lastBackupCleanupUtc = DateTime.MinValue;
        private bool disposed;

        public ConverterConfig Config { get; private set; }

        public SaveConverter(ConverterConfig config, Logger logger, bool dryRun)
        {
            Config = config;
            this.logger = logger;
            this.dryRun = dryRun;
            journal = new SaveJournal(config.StateFilePath, logger);
            syncthingScanner = new SyncthingScanner(config, logger);
        }

        public bool ReconcileAll(string reason)
        {
            lock (reconcileGate)
            {
                try
                {
                    Directory.CreateDirectory(Config.SaveDirectory);
                    CleanupBackupsIfNeeded();

                    var pairs = Directory.EnumerateFiles(Config.SaveDirectory, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(SavePair.IsSaveFile)
                        .Select(SavePair.PairKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    logger.Info("Reconciling " + pairs.Count + " save pair(s): " + reason + ".");

                    foreach (var pair in pairs)
                    {
                        ReconcilePairCore(pair, reason, null);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    logger.Error("Failed to reconcile save folder.", ex);
                    return false;
                }
            }
        }

        public void ReconcilePair(string pairKey, string reason, Action<string> markIgnoredWrite)
        {
            lock (reconcileGate)
            {
                ReconcilePairCore(pairKey, reason, markIgnoredWrite);
            }
        }

        public void WriteStatus()
        {
            lock (reconcileGate)
            {
                Directory.CreateDirectory(Config.SaveDirectory);
                var pairs = Directory.EnumerateFiles(Config.SaveDirectory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(SavePair.IsSaveFile)
                    .Select(SavePair.PairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var matched = 0;
                var missingSav = 0;
                var missingSrm = 0;
                var differing = 0;
                var recordedConflicts = 0;

                foreach (var pairKey in pairs)
                {
                    var pair = SavePair.FromKey(pairKey);
                    var savExists = File.Exists(pair.SavPath);
                    var srmExists = File.Exists(pair.SrmPath);

                    if (savExists && !srmExists)
                    {
                        missingSrm++;
                        continue;
                    }

                    if (!savExists && srmExists)
                    {
                        missingSav++;
                        continue;
                    }

                    if (!savExists && !srmExists) continue;

                    var sav = SaveFileSnapshot.FromFile(pair.SavPath);
                    var srm = SaveFileSnapshot.FromFile(pair.SrmPath);
                    if (HashEquals(sav.Hash, srm.Hash))
                    {
                        matched++;
                    }
                    else
                    {
                        differing++;
                        var state = journal.Get(pairKey);
                        if (state != null && state.IsCurrentConflict(sav, srm))
                        {
                            recordedConflicts++;
                        }
                    }
                }

                logger.Info("Status for " + Config.SaveDirectory);
                logger.Info("Pairs: " + pairs.Count + "; matched: " + matched + "; missing .sav: " + missingSav + "; missing .srm: " + missingSrm + "; differing: " + differing + "; recorded conflicts: " + recordedConflicts + ".");
                logger.Info("State file: " + Config.StateFilePath);
                logger.Info("Backup directory: " + Config.BackupDirectory + "; retention days: " + Config.BackupRetentionDays + ".");
                logger.Info("Syncthing scan hook: " + (Config.SyncthingScanAfterWrite ? "enabled" : "disabled") + ".");
            }
        }

        public void FlushSyncthingScans()
        {
            syncthingScanner.Flush();
        }

        public void Dispose()
        {
            if (disposed) return;
            syncthingScanner.Dispose();
            disposed = true;
        }

        private void ReconcilePairCore(string pairKey, string reason, Action<string> markIgnoredWrite)
        {
            try
            {
                var pair = SavePair.FromKey(pairKey);
                var savExists = File.Exists(pair.SavPath);
                var srmExists = File.Exists(pair.SrmPath);

                if (!savExists && !srmExists)
                {
                    if (!dryRun && journal.Remove(pairKey))
                    {
                        journal.Save();
                    }

                    return;
                }

                if (savExists && !IsFileStable(pair.SavPath))
                {
                    logger.Info("Skipping unstable file for now: " + pair.SavPath);
                    return;
                }

                if (srmExists && !IsFileStable(pair.SrmPath))
                {
                    logger.Info("Skipping unstable file for now: " + pair.SrmPath);
                    return;
                }

                var oldState = journal.Get(pairKey);

                if (savExists && !srmExists)
                {
                    if (CopySave(pair.SavPath, pair.SrmPath, false, reason, markIgnoredWrite))
                    {
                        SaveCleanState(pairKey, ".sav", oldState);
                    }

                    return;
                }

                if (!savExists && srmExists)
                {
                    if (CopySave(pair.SrmPath, pair.SavPath, false, reason, markIgnoredWrite))
                    {
                        SaveCleanState(pairKey, ".srm", oldState);
                    }

                    return;
                }

                var savSnapshot = SaveFileSnapshot.FromFile(pair.SavPath);
                var srmSnapshot = SaveFileSnapshot.FromFile(pair.SrmPath);

                if (HashEquals(savSnapshot.Hash, srmSnapshot.Hash))
                {
                    SaveCleanState(pairKey, savSnapshot, srmSnapshot, "", oldState);
                    return;
                }

                var decision = ChooseSource(savSnapshot, srmSnapshot, oldState);
                if (decision.Type == ReconcileDecisionType.ExistingConflict)
                {
                    logger.Warn("Conflict still unresolved for " + Path.GetFileName(pairKey) + ": " + decision.Reason);
                    return;
                }

                if (decision.Type == ReconcileDecisionType.Conflict)
                {
                    RecordConflict(pairKey, savSnapshot, srmSnapshot, oldState, decision.Reason);
                    return;
                }

                var source = decision.Type == ReconcileDecisionType.CopySavToSrm ? pair.SavPath : pair.SrmPath;
                var target = decision.Type == ReconcileDecisionType.CopySavToSrm ? pair.SrmPath : pair.SavPath;
                var sourceExtension = decision.Type == ReconcileDecisionType.CopySavToSrm ? ".sav" : ".srm";

                if (CopySave(source, target, true, reason + "; " + decision.Reason, markIgnoredWrite))
                {
                    SaveCleanState(pairKey, sourceExtension, oldState);
                }
            }
            catch (IOException ex)
            {
                logger.Error("Save pair was busy and will be retried by the next scan: " + pairKey, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.Error("No permission to sync save pair: " + pairKey, ex);
            }
            catch (Exception ex)
            {
                logger.Error("Failed to sync save pair: " + pairKey, ex);
            }
        }

        private ReconcileDecision ChooseSource(SaveFileSnapshot sav, SaveFileSnapshot srm, PairState oldState)
        {
            if (oldState != null)
            {
                if (oldState.IsCurrentConflict(sav, srm))
                {
                    return ReconcileDecision.ExistingConflict("both sides still differ exactly as previously recorded");
                }

                var savChanged = oldState.Sav == null || !HashEquals(sav.Hash, oldState.Sav.Hash);
                var srmChanged = oldState.Srm == null || !HashEquals(srm.Hash, oldState.Srm.Hash);
                var savMatchesLastSync = !string.IsNullOrEmpty(oldState.SyncedHash) && HashEquals(sav.Hash, oldState.SyncedHash);
                var srmMatchesLastSync = !string.IsNullOrEmpty(oldState.SyncedHash) && HashEquals(srm.Hash, oldState.SyncedHash);
                var savIsKnownOlder = oldState.IsKnownOlderHash(sav.Hash);
                var srmIsKnownOlder = oldState.IsKnownOlderHash(srm.Hash);
                var savIsRejected = oldState.IsRejectedHash(sav.Hash);
                var srmIsRejected = oldState.IsRejectedHash(srm.Hash);

                if ((savIsRejected || savIsKnownOlder) && srmMatchesLastSync)
                {
                    return ReconcileDecision.CopySrmToSav("the .sav content is an older known save touched after the last sync");
                }

                if ((srmIsRejected || srmIsKnownOlder) && savMatchesLastSync)
                {
                    return ReconcileDecision.CopySavToSrm("the .srm content is an older known save touched after the last sync");
                }

                if (savChanged && !srmChanged)
                {
                    if (savIsRejected || savIsKnownOlder)
                    {
                        return ReconcileDecision.CopySrmToSav("the changed .sav content matches older journal history");
                    }

                    if (!IsPlausibleNewChange(sav, srm, oldState))
                    {
                        return ReconcileDecision.Conflict("only .sav changed, but its timestamp is not newer than the journal and unchanged .srm");
                    }

                    return ReconcileDecision.CopySavToSrm("only .sav changed since the last journal state");
                }

                if (!savChanged && srmChanged)
                {
                    if (srmIsRejected || srmIsKnownOlder)
                    {
                        return ReconcileDecision.CopySavToSrm("the changed .srm content matches older journal history");
                    }

                    if (!IsPlausibleNewChange(srm, sav, oldState))
                    {
                        return ReconcileDecision.Conflict("only .srm changed, but its timestamp is not newer than the journal and unchanged .sav");
                    }

                    return ReconcileDecision.CopySrmToSav("only .srm changed since the last journal state");
                }

                if (savMatchesLastSync && !srmMatchesLastSync)
                {
                    return ReconcileDecision.CopySrmToSav(".sav still matches the last synced content");
                }

                if (srmMatchesLastSync && !savMatchesLastSync)
                {
                    return ReconcileDecision.CopySavToSrm(".srm still matches the last synced content");
                }

                if (savChanged && srmChanged)
                {
                    return ReconcileDecision.Conflict("both .sav and .srm changed differently since the last journal state");
                }

                return ReconcileDecision.Conflict("both files differ, but neither side changed since the last journal state");
            }

            return sav.LastWriteUtc >= srm.LastWriteUtc
                ? ReconcileDecision.CopySavToSrm("no journal yet; newest modified time wins")
                : ReconcileDecision.CopySrmToSav("no journal yet; newest modified time wins");
        }

        private static bool IsPlausibleNewChange(SaveFileSnapshot changed, SaveFileSnapshot unchanged, PairState oldState)
        {
            return changed.LastWriteUtcTicks > oldState.UpdatedUtc.Ticks &&
                changed.LastWriteUtcTicks > unchanged.LastWriteUtcTicks;
        }

        private bool IsFileStable(string path)
        {
            if (!File.Exists(path)) return false;

            var first = new FileInfo(path);
            var length = first.Length;
            var writeTime = first.LastWriteTimeUtc;
            Thread.Sleep(Config.StabilityCheckInterval);

            var second = new FileInfo(path);
            second.Refresh();
            return second.Exists && second.Length == length && second.LastWriteTimeUtc == writeTime;
        }

        private bool CopySave(string source, string target, bool targetExists, string reason, Action<string> markIgnoredWrite)
        {
            if (dryRun)
            {
                logger.Info("[dry-run] Would sync " + Path.GetFileName(source) + " -> " + Path.GetFileName(target) + " (" + reason + ").");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (markIgnoredWrite != null)
            {
                markIgnoredWrite(target);
            }

            if (targetExists && Config.BackupBeforeOverwrite)
            {
                BackupFile(target);
            }

            File.Copy(source, target, true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            logger.Info("Synced " + Path.GetFileName(source) + " -> " + Path.GetFileName(target) + " (" + reason + ").");
            syncthingScanner.QueueFile(source);
            syncthingScanner.QueueFile(target);
            return true;
        }

        private void SaveCleanState(string pairKey, string sourceExtension, PairState oldState)
        {
            var pair = SavePair.FromKey(pairKey);
            SaveCleanState(
                pairKey,
                SaveFileSnapshot.FromFile(pair.SavPath),
                SaveFileSnapshot.FromFile(pair.SrmPath),
                sourceExtension,
                oldState);
        }

        private void SaveCleanState(string pairKey, SaveFileSnapshot sav, SaveFileSnapshot srm, string sourceExtension, PairState oldState)
        {
            if (dryRun) return;

            var syncedHash = sav.Exists && srm.Exists && HashEquals(sav.Hash, srm.Hash) ? sav.Hash : "";
            var state = PairState.CreateClean(pairKey, sav, srm, syncedHash, sourceExtension, oldState);
            journal.Set(state);
            journal.Save();
        }

        private void RecordConflict(string pairKey, SaveFileSnapshot sav, SaveFileSnapshot srm, PairState oldState, string reason)
        {
            var pairName = Path.GetFileName(pairKey);
            var fingerprint = PairState.BuildConflictFingerprint(sav, srm);
            var alreadyRecorded = oldState != null && string.Equals(oldState.ConflictFingerprint, fingerprint, StringComparison.Ordinal);

            if (alreadyRecorded)
            {
                logger.Warn("Conflict still unresolved for " + pairName + ": " + reason);
                return;
            }

            if (dryRun)
            {
                logger.Warn("[dry-run] Would record conflict for " + pairName + ": " + reason);
                return;
            }

            if (Config.BackupBeforeOverwrite)
            {
                BackupFile(sav.Path);
                BackupFile(srm.Path);
            }

            logger.Warn("Conflict detected for " + pairName + ": " + reason + ". Both files were left untouched.");
            journal.Set(PairState.CreateConflict(pairKey, sav, srm, reason, oldState));
            journal.Save();
        }

        private void BackupFile(string path)
        {
            Directory.CreateDirectory(Config.BackupDirectory);
            var dateDirectory = Path.Combine(Config.BackupDirectory, DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dateDirectory);

            var backupName = Path.GetFileName(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".bak";
            var backupPath = Path.Combine(dateDirectory, backupName);
            File.Copy(path, backupPath, false);
            logger.Info("Backed up " + Path.GetFileName(path) + " to " + backupPath + ".");
        }

        private void CleanupBackupsIfNeeded()
        {
            if (dryRun || Config.BackupRetentionDays <= 0) return;
            if (DateTime.UtcNow.Subtract(lastBackupCleanupUtc) < TimeSpan.FromHours(1)) return;
            lastBackupCleanupUtc = DateTime.UtcNow;

            if (!Directory.Exists(Config.BackupDirectory)) return;

            var cutoff = DateTime.UtcNow.AddDays(-Config.BackupRetentionDays);
            var deleted = 0;
            foreach (var backup in Directory.EnumerateFiles(Config.BackupDirectory, "*.bak", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(backup) < cutoff)
                    {
                        File.Delete(backup);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error("Failed to delete expired backup: " + backup, ex);
                }
            }

            if (deleted > 0)
            {
                logger.Info("Deleted " + deleted + " expired backup file(s).");
            }
        }

        private static bool HashEquals(string first, string second)
        {
            return string.Equals(first ?? "", second ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal enum ReconcileDecisionType
    {
        CopySavToSrm,
        CopySrmToSav,
        Conflict,
        ExistingConflict
    }

    internal sealed class ReconcileDecision
    {
        private ReconcileDecision(ReconcileDecisionType type, string reason)
        {
            Type = type;
            Reason = reason;
        }

        public ReconcileDecisionType Type { get; private set; }
        public string Reason { get; private set; }

        public static ReconcileDecision CopySavToSrm(string reason)
        {
            return new ReconcileDecision(ReconcileDecisionType.CopySavToSrm, reason);
        }

        public static ReconcileDecision CopySrmToSav(string reason)
        {
            return new ReconcileDecision(ReconcileDecisionType.CopySrmToSav, reason);
        }

        public static ReconcileDecision Conflict(string reason)
        {
            return new ReconcileDecision(ReconcileDecisionType.Conflict, reason);
        }

        public static ReconcileDecision ExistingConflict(string reason)
        {
            return new ReconcileDecision(ReconcileDecisionType.ExistingConflict, reason);
        }
    }

    internal sealed class SaveFileSnapshot
    {
        private SaveFileSnapshot()
        {
        }

        public string Path { get; private set; }
        public bool Exists { get; private set; }
        public long Length { get; private set; }
        public long LastWriteUtcTicks { get; private set; }
        public string Hash { get; private set; }

        public DateTime LastWriteUtc
        {
            get { return new DateTime(LastWriteUtcTicks, DateTimeKind.Utc); }
        }

        public static SaveFileSnapshot Missing(string path)
        {
            return new SaveFileSnapshot
            {
                Path = path,
                Exists = false,
                Length = 0,
                LastWriteUtcTicks = 0,
                Hash = "",
            };
        }

        public static SaveFileSnapshot FromFile(string path)
        {
            if (!File.Exists(path))
            {
                return Missing(path);
            }

            var info = new FileInfo(path);
            return new SaveFileSnapshot
            {
                Path = path,
                Exists = true,
                Length = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Hash = HashFile(path),
            };
        }

        public static SaveFileSnapshot FromState(long length, long lastWriteUtcTicks, string hash)
        {
            return new SaveFileSnapshot
            {
                Path = "",
                Exists = true,
                Length = length,
                LastWriteUtcTicks = lastWriteUtcTicks,
                Hash = hash ?? "",
            };
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return BytesToHex(sha.ComputeHash(stream));
            }
        }

        private static string BytesToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }
    }

    internal sealed class SavePair
    {
        private SavePair(string key)
        {
            SavPath = key + ".sav";
            SrmPath = key + ".srm";
        }

        public string SavPath { get; private set; }
        public string SrmPath { get; private set; }

        public static SavePair FromKey(string key)
        {
            return new SavePair(key);
        }

        public static bool IsSaveFile(string path)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.IndexOf(".sync-conflict-", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return extension.Equals(".sav", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".srm", StringComparison.OrdinalIgnoreCase);
        }

        public static string PairKey(string path)
        {
            return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
        }
    }

    internal sealed class SaveJournal
    {
        private readonly string path;
        private readonly Logger logger;
        private readonly Dictionary<string, PairState> states = new Dictionary<string, PairState>(StringComparer.OrdinalIgnoreCase);

        public SaveJournal(string path, Logger logger)
        {
            this.path = path;
            this.logger = logger;
            Load();
        }

        public PairState Get(string pairKey)
        {
            PairState state;
            return states.TryGetValue(pairKey, out state) ? state : null;
        }

        public void Set(PairState state)
        {
            states[state.PairKey] = state;
        }

        public bool Remove(string pairKey)
        {
            return states.Remove(pairKey);
        }

        public void Save()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var tempPath = path + ".tmp";
            var lines = new List<string>
            {
                "# GBASaveConverter state v1",
            };

            foreach (var state in states.Values.OrderBy(item => item.PairKey, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(state.ToLine());
            }

            File.WriteAllLines(tempPath, lines.ToArray(), Encoding.UTF8);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }

        private void Load()
        {
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Trim().Length == 0 || line.StartsWith("#")) continue;

                try
                {
                    var state = PairState.FromLine(line);
                    if (state != null)
                    {
                        states[state.PairKey] = state;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error("Skipped invalid journal line in " + path + ".", ex);
                }
            }
        }
    }

    internal sealed class PairState
    {
        private const int MaxKnownHashes = 100;

        public string PairKey { get; private set; }
        public SaveFileSnapshot Sav { get; private set; }
        public SaveFileSnapshot Srm { get; private set; }
        public string SyncedHash { get; private set; }
        public string LastSourceExtension { get; private set; }
        public string ConflictFingerprint { get; private set; }
        public string ConflictReason { get; private set; }
        public DateTime UpdatedUtc { get; private set; }
        public List<string> KnownHashes { get; private set; }
        public List<string> RejectedHashes { get; private set; }

        public bool IsConflict
        {
            get { return !string.IsNullOrEmpty(ConflictFingerprint); }
        }

        public bool IsCurrentConflict(SaveFileSnapshot sav, SaveFileSnapshot srm)
        {
            return IsConflict && string.Equals(ConflictFingerprint, BuildConflictFingerprint(sav, srm), StringComparison.Ordinal);
        }

        public bool IsKnownOlderHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            if (string.Equals(hash, SyncedHash, StringComparison.OrdinalIgnoreCase)) return false;
            return KnownHashes != null && KnownHashes.Any(item => string.Equals(item, hash, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsRejectedHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;
            if (string.Equals(hash, SyncedHash, StringComparison.OrdinalIgnoreCase)) return false;
            return RejectedHashes != null && RejectedHashes.Any(item => string.Equals(item, hash, StringComparison.OrdinalIgnoreCase));
        }

        public static PairState CreateClean(
            string pairKey,
            SaveFileSnapshot sav,
            SaveFileSnapshot srm,
            string syncedHash,
            string sourceExtension,
            PairState oldState)
        {
            return new PairState
            {
                PairKey = pairKey,
                Sav = sav,
                Srm = srm,
                SyncedHash = syncedHash ?? "",
                LastSourceExtension = sourceExtension ?? "",
                ConflictFingerprint = "",
                ConflictReason = "",
                UpdatedUtc = DateTime.UtcNow,
                KnownHashes = BuildKnownHashes(syncedHash, sav, srm, oldState),
                RejectedHashes = BuildRejectedHashes(syncedHash, oldState),
            };
        }

        public static PairState CreateConflict(
            string pairKey,
            SaveFileSnapshot sav,
            SaveFileSnapshot srm,
            string reason,
            PairState oldState)
        {
            var syncedHash = oldState == null ? "" : oldState.SyncedHash;
            return new PairState
            {
                PairKey = pairKey,
                Sav = sav,
                Srm = srm,
                SyncedHash = syncedHash,
                LastSourceExtension = oldState == null ? "" : oldState.LastSourceExtension,
                ConflictFingerprint = BuildConflictFingerprint(sav, srm),
                ConflictReason = reason ?? "",
                UpdatedUtc = DateTime.UtcNow,
                KnownHashes = BuildKnownHashes(syncedHash, sav, srm, oldState),
                RejectedHashes = BuildRejectedHashes(syncedHash, oldState, sav, srm),
            };
        }

        public static string BuildConflictFingerprint(SaveFileSnapshot sav, SaveFileSnapshot srm)
        {
            return (sav.Hash ?? "") + "|" + (srm.Hash ?? "");
        }

        public string ToLine()
        {
            var fields = new[]
            {
                Encode(PairKey),
                SnapshotToFields(Sav),
                SnapshotToFields(Srm),
                SyncedHash ?? "",
                LastSourceExtension ?? "",
                ConflictFingerprint ?? "",
                Encode(ConflictReason ?? ""),
                UpdatedUtc.Ticks.ToString(),
                string.Join(",", (KnownHashes ?? new List<string>()).ToArray()),
                string.Join(",", (RejectedHashes ?? new List<string>()).ToArray()),
            };

            return string.Join("\t", fields);
        }

        public static PairState FromLine(string line)
        {
            var parts = line.Split('\t');
            if (parts.Length < 9) return null;

            return new PairState
            {
                PairKey = Decode(parts[0]),
                Sav = SnapshotFromFields(parts[1]),
                Srm = SnapshotFromFields(parts[2]),
                SyncedHash = parts[3],
                LastSourceExtension = parts[4],
                ConflictFingerprint = parts[5],
                ConflictReason = Decode(parts[6]),
                UpdatedUtc = new DateTime(ParseLong(parts[7]), DateTimeKind.Utc),
                KnownHashes = ParseHashList(parts[8]),
                RejectedHashes = parts.Length >= 10 ? ParseHashList(parts[9]) : new List<string>(),
            };
        }

        private static List<string> BuildKnownHashes(string syncedHash, SaveFileSnapshot sav, SaveFileSnapshot srm, PairState oldState)
        {
            var hashes = new List<string>();
            AddHash(hashes, syncedHash);
            AddSnapshotHash(hashes, sav);
            AddSnapshotHash(hashes, srm);

            if (oldState != null)
            {
                AddHash(hashes, oldState.SyncedHash);
                AddSnapshotHash(hashes, oldState.Sav);
                AddSnapshotHash(hashes, oldState.Srm);
                if (oldState.KnownHashes != null)
                {
                    foreach (var hash in oldState.KnownHashes)
                    {
                        AddHash(hashes, hash);
                    }
                }

                if (oldState.RejectedHashes != null)
                {
                    foreach (var hash in oldState.RejectedHashes)
                    {
                        AddHash(hashes, hash);
                    }
                }
            }

            return hashes.Take(MaxKnownHashes).ToList();
        }

        private static List<string> BuildRejectedHashes(string acceptedHash, PairState oldState, params SaveFileSnapshot[] rejectedSnapshots)
        {
            var hashes = new List<string>();
            if (oldState != null && oldState.RejectedHashes != null)
            {
                foreach (var hash in oldState.RejectedHashes)
                {
                    AddRejectedHash(hashes, hash, acceptedHash);
                }
            }

            foreach (var snapshot in rejectedSnapshots ?? new SaveFileSnapshot[0])
            {
                AddRejectedHash(hashes, snapshot == null ? "" : snapshot.Hash, acceptedHash);
            }

            return hashes.Take(MaxKnownHashes).ToList();
        }

        private static void AddHash(List<string> hashes, string hash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            if (hashes.Any(item => string.Equals(item, hash, StringComparison.OrdinalIgnoreCase))) return;
            hashes.Add(hash);
        }

        private static void AddRejectedHash(List<string> hashes, string hash, string acceptedHash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            if (!string.IsNullOrEmpty(acceptedHash) && string.Equals(hash, acceptedHash, StringComparison.OrdinalIgnoreCase)) return;
            AddHash(hashes, hash);
        }

        private static void AddSnapshotHash(List<string> hashes, SaveFileSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Exists) return;
            AddHash(hashes, snapshot.Hash);
        }

        private static List<string> ParseHashList(string value)
        {
            return (value ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxKnownHashes)
                .ToList();
        }

        private static string SnapshotToFields(SaveFileSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Exists)
            {
                return "0|0|0|";
            }

            return "1|" + snapshot.Length + "|" + snapshot.LastWriteUtcTicks + "|" + (snapshot.Hash ?? "");
        }

        private static SaveFileSnapshot SnapshotFromFields(string value)
        {
            var parts = value.Split('|');
            if (parts.Length < 4 || parts[0] != "1")
            {
                return SaveFileSnapshot.Missing("");
            }

            return SaveFileSnapshot.FromState(ParseLong(parts[1]), ParseLong(parts[2]), parts[3]);
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static long ParseLong(string value)
        {
            long parsed;
            return long.TryParse(value, out parsed) ? parsed : 0;
        }
    }

    internal sealed class ConverterConfig
    {
        public string SaveDirectory { get; private set; }
        public string LogDirectory { get; private set; }
        public string BackupDirectory { get; private set; }
        public string StateDirectory { get; private set; }
        public string StateFilePath { get; private set; }
        public bool BackupBeforeOverwrite { get; private set; }
        public int BackupRetentionDays { get; private set; }
        public bool LogToConsole { get; private set; }
        public bool SyncthingScanAfterWrite { get; private set; }
        public string SyncthingApiBaseUrl { get; private set; }
        public string SyncthingApiKey { get; private set; }
        public string SyncthingFolderId { get; private set; }
        public string SyncthingScanSubdirectory { get; private set; }
        public TimeSpan SyncthingScanDebounceInterval { get; private set; }
        public TimeSpan SyncthingRequestTimeout { get; private set; }
        public TimeSpan DebounceInterval { get; private set; }
        public TimeSpan FullScanInterval { get; private set; }
        public TimeSpan IgnoreOwnWritesInterval { get; private set; }
        public TimeSpan StabilityCheckInterval { get; private set; }

        public static ConverterConfig Load(string path)
        {
            EnsureDefaultConfig(path);
            var appDirectory = Path.GetDirectoryName(path);
            var values = File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("#"))
                .Select(line => line.Split(new[] { '=' }, 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

            var stateDirectory = ResolvePath(appDirectory, Get(values, "StateDirectory", "state"));
            return new ConverterConfig
            {
                SaveDirectory = Get(values, "SaveDirectory", @"C:\RetroArch\saves\mGBA"),
                LogDirectory = ResolvePath(appDirectory, Get(values, "LogDirectory", "logs")),
                BackupDirectory = ResolvePath(appDirectory, Get(values, "BackupDirectory", "backups")),
                StateDirectory = stateDirectory,
                StateFilePath = Path.Combine(stateDirectory, "pairs.tsv"),
                BackupBeforeOverwrite = GetBool(values, "BackupBeforeOverwrite", true),
                BackupRetentionDays = GetInt(values, "BackupRetentionDays", 0, 0, 3650),
                LogToConsole = GetBool(values, "LogToConsole", true),
                SyncthingScanAfterWrite = GetBool(values, "SyncthingScanAfterWrite", false),
                SyncthingApiBaseUrl = Get(values, "SyncthingApiBaseUrl", "http://127.0.0.1:8384/rest"),
                SyncthingApiKey = Get(values, "SyncthingApiKey", ""),
                SyncthingFolderId = Get(values, "SyncthingFolderId", ""),
                SyncthingScanSubdirectory = Get(values, "SyncthingScanSubdirectory", ""),
                SyncthingScanDebounceInterval = TimeSpan.FromSeconds(GetInt(values, "SyncthingScanDebounceSeconds", 2, 0, 300)),
                SyncthingRequestTimeout = TimeSpan.FromSeconds(GetInt(values, "SyncthingRequestTimeoutSeconds", 5, 1, 60)),
                DebounceInterval = TimeSpan.FromSeconds(GetInt(values, "DebounceSeconds", 5, 1, 120)),
                FullScanInterval = TimeSpan.FromMinutes(GetInt(values, "FullScanMinutes", 10, 1, 1440)),
                IgnoreOwnWritesInterval = TimeSpan.FromSeconds(GetInt(values, "IgnoreOwnWritesSeconds", 10, 1, 300)),
                StabilityCheckInterval = TimeSpan.FromMilliseconds(GetInt(values, "StabilityCheckMilliseconds", 500, 100, 10000)),
            };
        }

        private static void EnsureDefaultConfig(string path)
        {
            if (File.Exists(path)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,
@"# GBASaveConverter
# Keeps Pizza Boy .sav and RetroArch .srm battery saves paired.
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
", Encoding.UTF8);
        }

        private static string Get(IDictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) && value.Length > 0 ? value : fallback;
        }

        private static bool GetBool(IDictionary<string, string> values, string key, bool fallback)
        {
            string value;
            if (!values.TryGetValue(key, out value)) return fallback;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetInt(IDictionary<string, string> values, string key, int fallback, int minimum, int maximum)
        {
            string value;
            int parsed;
            if (!values.TryGetValue(key, out value) || !int.TryParse(value, out parsed)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, parsed));
        }

        private static string ResolvePath(string baseDirectory, string path)
        {
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));
        }
    }

    internal sealed class SyncthingScanner : IDisposable
    {
        private readonly ConverterConfig config;
        private readonly Logger logger;
        private readonly object gate = new object();
        private readonly Dictionary<string, DateTime> pendingPaths = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer timer;
        private bool disposed;
        private int scanning;

        public SyncthingScanner(ConverterConfig config, Logger logger)
        {
            this.config = config;
            this.logger = logger;
            timer = new Timer(OnTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        public void QueueFile(string path)
        {
            if (!config.SyncthingScanAfterWrite) return;

            lock (gate)
            {
                pendingPaths[path] = DateTime.UtcNow.Add(config.SyncthingScanDebounceInterval);
            }
        }

        public void Flush()
        {
            if (Interlocked.Exchange(ref scanning, 1) == 1) return;

            try
            {
                List<string> paths;
                lock (gate)
                {
                    paths = pendingPaths.Keys.ToList();
                    pendingPaths.Clear();
                }

                ScanPaths(paths);
            }
            finally
            {
                Interlocked.Exchange(ref scanning, 0);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            Flush();
            timer.Dispose();
            disposed = true;
        }

        private void OnTimer(object state)
        {
            if (Interlocked.Exchange(ref scanning, 1) == 1) return;

            try
            {
                List<string> duePaths;
                var now = DateTime.UtcNow;

                lock (gate)
                {
                    duePaths = pendingPaths
                        .Where(item => item.Value <= now)
                        .Select(item => item.Key)
                        .ToList();

                    foreach (var path in duePaths)
                    {
                        pendingPaths.Remove(path);
                    }
                }

                ScanPaths(duePaths);
            }
            finally
            {
                Interlocked.Exchange(ref scanning, 0);
            }
        }

        private void ScanPaths(IEnumerable<string> paths)
        {
            if (!config.SyncthingScanAfterWrite) return;
            if (string.IsNullOrWhiteSpace(config.SyncthingApiKey) || string.IsNullOrWhiteSpace(config.SyncthingFolderId))
            {
                if (paths.Any())
                {
                    logger.Info("Syncthing scan is enabled but SyncthingApiKey or SyncthingFolderId is missing.");
                }

                return;
            }

            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ScanFile(path);
            }
        }

        private void ScanFile(string path)
        {
            try
            {
                var subPath = BuildSyncthingSubPath(path);
                var url = config.SyncthingApiBaseUrl.TrimEnd('/') +
                    "/db/scan?folder=" + Uri.EscapeDataString(config.SyncthingFolderId) +
                    "&sub=" + Uri.EscapeDataString(subPath);

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.Timeout = (int)config.SyncthingRequestTimeout.TotalMilliseconds;
                request.ReadWriteTimeout = (int)config.SyncthingRequestTimeout.TotalMilliseconds;
                request.Headers["X-API-Key"] = config.SyncthingApiKey;
                request.ContentLength = 0;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    logger.Info("Requested Syncthing scan for " + subPath + " (" + (int)response.StatusCode + ").");
                }
            }
            catch (Exception ex)
            {
                logger.Error("Failed to request Syncthing scan for " + path + ".", ex);
            }
        }

        private string BuildSyncthingSubPath(string path)
        {
            var relativePath = GetPathRelativeToSaveDirectory(path).Replace('\\', '/');
            var subdirectory = (config.SyncthingScanSubdirectory ?? string.Empty)
                .Trim()
                .Trim('/', '\\')
                .Replace('\\', '/');

            return subdirectory.Length == 0 ? relativePath : subdirectory + "/" + relativePath;
        }

        private string GetPathRelativeToSaveDirectory(string path)
        {
            var root = Path.GetFullPath(config.SaveDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(root.Length);
            }

            return Path.GetFileName(path);
        }
    }

    internal sealed class Logger
    {
        private readonly object gate = new object();
        private readonly string logDirectory;
        private readonly bool console;

        public Logger(string logDirectory, bool console)
        {
            this.logDirectory = logDirectory;
            this.console = console;
            Directory.CreateDirectory(logDirectory);
        }

        public void Info(string message)
        {
            Write("INFO", message, null);
        }

        public void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        private void Write(string level, string message, Exception exception)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            lock (gate)
            {
                var logPath = Path.Combine(logDirectory, "GBASaveConverter.log");
                RotateIfNeeded(logPath);
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);

                if (console)
                {
                    Console.WriteLine(line);
                }
            }
        }

        private static void RotateIfNeeded(string logPath)
        {
            if (!File.Exists(logPath)) return;

            var info = new FileInfo(logPath);
            if (info.Length < 5 * 1024 * 1024) return;

            var rotatedPath = Path.Combine(
                info.DirectoryName,
                Path.GetFileNameWithoutExtension(logPath) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            File.Move(logPath, rotatedPath);
        }
    }
}
