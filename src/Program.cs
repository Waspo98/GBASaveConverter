using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            var logger = new Logger(config.LogDirectory, config.LogToConsole || args.Any(IsConsoleArg));
            var converter = new SaveConverter(config, logger);

            if (args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase)))
            {
                logger.Info("Running one reconciliation pass.");
                return converter.ReconcileAll("manual once") ? 0 : 1;
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

        private static bool IsConsoleArg(string arg)
        {
            return arg.Equals("--console", StringComparison.OrdinalIgnoreCase);
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

    internal sealed class SaveConverter
    {
        private readonly Logger logger;
        private readonly SyncthingScanner syncthingScanner;

        public ConverterConfig Config { get; private set; }

        public SaveConverter(ConverterConfig config, Logger logger)
        {
            Config = config;
            this.logger = logger;
            syncthingScanner = new SyncthingScanner(config, logger);
        }

        public bool ReconcileAll(string reason)
        {
            try
            {
                Directory.CreateDirectory(Config.SaveDirectory);
                var pairs = Directory.EnumerateFiles(Config.SaveDirectory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(SavePair.IsSaveFile)
                    .Select(SavePair.PairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                logger.Info("Reconciling " + pairs.Count + " save pair(s): " + reason + ".");

                foreach (var pair in pairs)
                {
                    ReconcilePair(pair, reason, null);
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.Error("Failed to reconcile save folder.", ex);
                return false;
            }
        }

        public void ReconcilePair(string pairKey, string reason, Action<string> markIgnoredWrite)
        {
            try
            {
                var pair = SavePair.FromKey(pairKey);
                var sav = pair.SavPath;
                var srm = pair.SrmPath;
                var savExists = File.Exists(sav);
                var srmExists = File.Exists(srm);

                if (!savExists && !srmExists) return;

                if (savExists && !IsFileStable(sav))
                {
                    logger.Info("Skipping unstable file for now: " + sav);
                    return;
                }

                if (srmExists && !IsFileStable(srm))
                {
                    logger.Info("Skipping unstable file for now: " + srm);
                    return;
                }

                if (savExists && !srmExists)
                {
                    CopySave(sav, srm, false, reason, markIgnoredWrite);
                    return;
                }

                if (!savExists && srmExists)
                {
                    CopySave(srm, sav, false, reason, markIgnoredWrite);
                    return;
                }

                if (FilesMatch(sav, srm))
                {
                    return;
                }

                var savInfo = new FileInfo(sav);
                var srmInfo = new FileInfo(srm);
                var source = savInfo.LastWriteTimeUtc >= srmInfo.LastWriteTimeUtc ? sav : srm;
                var target = source.Equals(sav, StringComparison.OrdinalIgnoreCase) ? srm : sav;

                CopySave(source, target, true, reason, markIgnoredWrite);
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

        private bool IsFileStable(string path)
        {
            var first = new FileInfo(path);
            var length = first.Length;
            var writeTime = first.LastWriteTimeUtc;
            Thread.Sleep(Config.StabilityCheckInterval);

            var second = new FileInfo(path);
            second.Refresh();
            return second.Exists && second.Length == length && second.LastWriteTimeUtc == writeTime;
        }

        private void CopySave(string source, string target, bool targetExists, string reason, Action<string> markIgnoredWrite)
        {
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
            syncthingScanner.ScanFile(source);
            syncthingScanner.ScanFile(target);
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

        private static bool FilesMatch(string firstPath, string secondPath)
        {
            var first = new FileInfo(firstPath);
            var second = new FileInfo(secondPath);
            if (first.Length != second.Length) return false;

            using (var sha = SHA256.Create())
            {
                var firstHash = HashFile(sha, firstPath);
                var secondHash = HashFile(sha, secondPath);
                return firstHash.SequenceEqual(secondHash);
            }
        }

        private static byte[] HashFile(HashAlgorithm algorithm, string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return algorithm.ComputeHash(stream);
            }
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

    internal sealed class ConverterConfig
    {
        public string SaveDirectory { get; private set; }
        public string LogDirectory { get; private set; }
        public string BackupDirectory { get; private set; }
        public bool BackupBeforeOverwrite { get; private set; }
        public bool LogToConsole { get; private set; }
        public bool SyncthingScanAfterWrite { get; private set; }
        public string SyncthingApiBaseUrl { get; private set; }
        public string SyncthingApiKey { get; private set; }
        public string SyncthingFolderId { get; private set; }
        public string SyncthingScanSubdirectory { get; private set; }
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

            return new ConverterConfig
            {
                SaveDirectory = Get(values, "SaveDirectory", @"C:\RetroArch\saves\mGBA"),
                LogDirectory = ResolvePath(appDirectory, Get(values, "LogDirectory", "logs")),
                BackupDirectory = ResolvePath(appDirectory, Get(values, "BackupDirectory", "backups")),
                BackupBeforeOverwrite = GetBool(values, "BackupBeforeOverwrite", true),
                LogToConsole = GetBool(values, "LogToConsole", true),
                SyncthingScanAfterWrite = GetBool(values, "SyncthingScanAfterWrite", false),
                SyncthingApiBaseUrl = Get(values, "SyncthingApiBaseUrl", "http://127.0.0.1:8384/rest"),
                SyncthingApiKey = Get(values, "SyncthingApiKey", ""),
                SyncthingFolderId = Get(values, "SyncthingFolderId", ""),
                SyncthingScanSubdirectory = Get(values, "SyncthingScanSubdirectory", ""),
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
BackupBeforeOverwrite=true
SyncthingScanAfterWrite=false
SyncthingApiBaseUrl=http://127.0.0.1:8384/rest
SyncthingApiKey=
SyncthingFolderId=
SyncthingScanSubdirectory=
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

    internal sealed class SyncthingScanner
    {
        private readonly ConverterConfig config;
        private readonly Logger logger;

        public SyncthingScanner(ConverterConfig config, Logger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public void ScanFile(string path)
        {
            if (!config.SyncthingScanAfterWrite) return;
            if (string.IsNullOrWhiteSpace(config.SyncthingApiKey) || string.IsNullOrWhiteSpace(config.SyncthingFolderId))
            {
                logger.Info("Syncthing scan is enabled but SyncthingApiKey or SyncthingFolderId is missing.");
                return;
            }

            try
            {
                var subPath = BuildSyncthingSubPath(path);
                var url = config.SyncthingApiBaseUrl.TrimEnd('/') +
                    "/db/scan?folder=" + Uri.EscapeDataString(config.SyncthingFolderId) +
                    "&sub=" + Uri.EscapeDataString(subPath);

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
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
            var fileName = Path.GetFileName(path);
            var subdirectory = (config.SyncthingScanSubdirectory ?? string.Empty)
                .Trim()
                .Trim('/', '\\')
                .Replace('\\', '/');

            return subdirectory.Length == 0 ? fileName : subdirectory + "/" + fileName;
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
