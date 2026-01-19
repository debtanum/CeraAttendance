using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CeraRegularize.Stores
{
    public sealed class SettingsState
    {
        public string ThemeMode { get; init; } = "system";
        public bool AutoUpdateEnabled { get; init; }
        public bool LogFileEnabled { get; init; }
        public Dictionary<string, bool> LogLevels { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string CalendarSelectionMode { get; init; } = "popup";
        public bool CalendarViewEnabled { get; init; } = true;
        public bool AutoRefreshEnabled { get; init; }
        public int AutoRefreshIntervalMin { get; init; } = 10;
        public bool HeadlessEnabled { get; init; } = true;

        public SettingsState Copy()
        {
            return new SettingsState
            {
                ThemeMode = ThemeMode,
                AutoUpdateEnabled = AutoUpdateEnabled,
                LogFileEnabled = LogFileEnabled,
                LogLevels = new Dictionary<string, bool>(LogLevels ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase),
                CalendarSelectionMode = CalendarSelectionMode,
                CalendarViewEnabled = CalendarViewEnabled,
                AutoRefreshEnabled = AutoRefreshEnabled,
                AutoRefreshIntervalMin = AutoRefreshIntervalMin,
                HeadlessEnabled = HeadlessEnabled,
            };
        }

        public bool ContentEquals(SettingsState? other)
        {
            if (other is null)
            {
                return false;
            }

            if (!string.Equals(ThemeMode, other.ThemeMode, StringComparison.OrdinalIgnoreCase)
                || AutoUpdateEnabled != other.AutoUpdateEnabled
                || LogFileEnabled != other.LogFileEnabled
                || !string.Equals(CalendarSelectionMode, other.CalendarSelectionMode, StringComparison.OrdinalIgnoreCase)
                || CalendarViewEnabled != other.CalendarViewEnabled
                || AutoRefreshEnabled != other.AutoRefreshEnabled
                || AutoRefreshIntervalMin != other.AutoRefreshIntervalMin
                || HeadlessEnabled != other.HeadlessEnabled)
            {
                return false;
            }

            var levels = LogLevels ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var otherLevels = other.LogLevels ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (levels.Count != otherLevels.Count)
            {
                return false;
            }

            foreach (var (key, value) in levels)
            {
                if (!otherLevels.TryGetValue(key, out var otherValue) || value != otherValue)
                {
                    return false;
                }
            }

            return true;
        }

    }

    public static class SettingsStore
    {
        private const string StoreFileName = "settings_store.json";
        private static readonly string[] LogLevelKeys = { "debug", "info", "warning", "error", "critical" };
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public static SettingsState DefaultSettings()
        {
            var defaults = new SettingsState
            {
                ThemeMode = "system",
                AutoUpdateEnabled = false,
                LogFileEnabled = false,
                CalendarSelectionMode = "popup",
                CalendarViewEnabled = true,
                AutoRefreshEnabled = false,
                AutoRefreshIntervalMin = 10,
                HeadlessEnabled = true,
            };

            foreach (var key in LogLevelKeys)
            {
                defaults.LogLevels[key] = key != "debug";
            }

            return defaults;
        }

        public static SettingsState Load()
        {
            var state = DefaultSettings();
            var path = AppPaths.DataFile(StoreFileName);
            if (!File.Exists(path))
            {
                return Normalize(state);
            }

            try
            {
                var raw = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<SettingsState>(raw);
                if (loaded != null)
                {
                    state = Merge(state, loaded);
                }
            }
            catch
            {
                return state;
            }

            return Normalize(state);
        }

        public static SettingsState Save(SettingsState data)
        {
            var snapshot = Normalize(data);
            var path = AppPaths.DataFile(StoreFileName);
            try
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(path, json);
            }
            catch
            {
            }

            return snapshot;
        }

        private static SettingsState Merge(SettingsState baseState, SettingsState other)
        {
            var logLevels = new Dictionary<string, bool>(baseState.LogLevels, StringComparer.OrdinalIgnoreCase);
            foreach (var key in LogLevelKeys)
            {
                if (other.LogLevels != null && other.LogLevels.TryGetValue(key, out var value))
                {
                    logLevels[key] = value;
                }
            }

            return new SettingsState
            {
                ThemeMode = string.IsNullOrWhiteSpace(other.ThemeMode) ? baseState.ThemeMode : other.ThemeMode,
                AutoUpdateEnabled = other.AutoUpdateEnabled,
                LogFileEnabled = other.LogFileEnabled,
                LogLevels = logLevels,
                CalendarSelectionMode = string.IsNullOrWhiteSpace(other.CalendarSelectionMode)
                    ? baseState.CalendarSelectionMode
                    : other.CalendarSelectionMode,
                CalendarViewEnabled = other.CalendarViewEnabled,
                AutoRefreshEnabled = other.AutoRefreshEnabled,
                AutoRefreshIntervalMin = other.AutoRefreshIntervalMin,
                HeadlessEnabled = other.HeadlessEnabled,
            };
        }

        private static SettingsState Normalize(SettingsState state)
        {
            var themeMode = NormalizeThemeMode(state.ThemeMode);
            var selectionMode = NormalizeSelectionMode(state.CalendarSelectionMode);
            var sourceLevels = state.LogLevels ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var logLevels = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in LogLevelKeys)
            {
                logLevels[key] = sourceLevels.TryGetValue(key, out var value)
                    ? value
                    : key != "debug";
            }

            return new SettingsState
            {
                ThemeMode = themeMode,
                AutoUpdateEnabled = state.AutoUpdateEnabled,
                LogFileEnabled = state.LogFileEnabled,
                LogLevels = logLevels,
                CalendarSelectionMode = selectionMode,
                CalendarViewEnabled = state.CalendarViewEnabled,
                AutoRefreshEnabled = state.AutoRefreshEnabled,
                AutoRefreshIntervalMin = ClampInterval(state.AutoRefreshIntervalMin),
                HeadlessEnabled = state.HeadlessEnabled,
            };
        }

        private static string NormalizeThemeMode(string? mode)
        {
            return mode?.ToLowerInvariant() switch
            {
                "light" => "light",
                "dark" => "dark",
                _ => "system",
            };
        }

        private static string NormalizeSelectionMode(string? mode)
        {
            return mode?.ToLowerInvariant() switch
            {
                "loop" => "loop",
                _ => "popup",
            };
        }

        private static int ClampInterval(int value)
        {
            if (value < 1)
            {
                return 1;
            }
            if (value > 60)
            {
                return 60;
            }
            return value;
        }
    }
}
