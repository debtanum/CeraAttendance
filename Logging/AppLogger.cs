using CeraRegularize.Stores;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CeraRegularize.Logging
{
    public enum AppLogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
        Critical,
    }

    public static class AppLogger
    {
        private const long MaxLogBytes = 10 * 1024 * 1024;
        private static readonly object Sync = new();
        private static readonly HashSet<AppLogLevel> EnabledLevels = new();
        private static bool _fileEnabled;
        private static string _logPath = string.Empty;
        private static bool _initialized;

        public static void Initialize()
        {
            Initialize(SettingsStore.Load());
        }

        public static void Initialize(SettingsState settings)
        {
            UpdateSettings(settings);
            _initialized = true;
            LogInfo("Logging initialized", nameof(AppLogger));
        }

        public static void UpdateSettings(SettingsState settings)
        {
            lock (Sync)
            {
                EnabledLevels.Clear();
                if (settings.LogLevels != null)
                {
                    if (GetLevel(settings, "debug")) EnabledLevels.Add(AppLogLevel.Debug);
                    if (GetLevel(settings, "info")) EnabledLevels.Add(AppLogLevel.Info);
                    if (GetLevel(settings, "warning")) EnabledLevels.Add(AppLogLevel.Warning);
                    if (GetLevel(settings, "error")) EnabledLevels.Add(AppLogLevel.Error);
                    if (GetLevel(settings, "critical")) EnabledLevels.Add(AppLogLevel.Critical);
                }

                _fileEnabled = settings.LogFileEnabled;
                _logPath = AppPaths.DataFile("app.log");
            }
        }

        public static void LogDebug(string message, string? category = null) => Log(AppLogLevel.Debug, message, category);
        public static void LogInfo(string message, string? category = null) => Log(AppLogLevel.Info, message, category);
        public static void LogWarning(string message, string? category = null) => Log(AppLogLevel.Warning, message, category);

        public static void LogError(string message, Exception? ex = null, string? category = null)
        {
            Log(AppLogLevel.Error, message, category, ex);
        }

        public static void LogCritical(string message, Exception? ex = null, string? category = null)
        {
            Log(AppLogLevel.Critical, message, category, ex);
        }

        private static void Log(AppLogLevel level, string message, string? category, Exception? ex = null)
        {
            if (!_initialized)
            {
                Initialize();
            }

            if (!EnabledLevels.Contains(level))
            {
                return;
            }

            var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var prefix = string.IsNullOrWhiteSpace(category) ? string.Empty : $"[{category}] ";
            var line = $"{stamp} [{level}] {prefix}{message}";
            if (ex != null)
            {
                line = $"{line} :: {ex}";
            }

            lock (Sync)
            {
                try
                {
                    Debug.WriteLine(line);
                }
                catch
                {
                }

                if (!_fileEnabled)
                {
                    return;
                }

                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
                catch
                {
                }
            }
        }

        private static void RotateIfNeeded()
        {
            if (string.IsNullOrWhiteSpace(_logPath))
            {
                return;
            }

            try
            {
                if (!File.Exists(_logPath))
                {
                    return;
                }

                var info = new FileInfo(_logPath);
                if (info.Length < MaxLogBytes)
                {
                    return;
                }

                var backupPath = Path.Combine(Path.GetDirectoryName(_logPath) ?? string.Empty, "app.log.bak");
                try
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                catch
                {
                }

                try
                {
                    File.Move(_logPath, backupPath);
                }
                catch
                {
                }
            }
            catch
            {
            }
        }

        private static bool GetLevel(SettingsState settings, string key)
        {
            return settings.LogLevels != null
                && settings.LogLevels.TryGetValue(key, out var value)
                && value;
        }
    }
}
