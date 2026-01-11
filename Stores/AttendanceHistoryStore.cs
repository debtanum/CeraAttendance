using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CeraRegularize.Stores
{
    public sealed class AttendanceHistoryEntry
    {
        public string? First { get; set; }
        public string? Second { get; set; }
        public string? Source { get; set; }
        public bool HasAbsent { get; set; }
    }

    public sealed class AttendanceHistorySnapshot
    {
        public string? FetchedAt { get; set; }
        public string? RangeStart { get; set; }
        public string? RangeEnd { get; set; }
        public Dictionary<string, AttendanceHistoryEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static class AttendanceHistoryStore
    {
        private const string StoreFileName = "attendance.data";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };
        private static readonly JsonSerializerOptions JsonReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static AttendanceHistorySnapshot? LoadSnapshot()
        {
            var path = AppPaths.DataFile(StoreFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var raw = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AttendanceHistorySnapshot>(raw, JsonReadOptions);
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<DateTime, Tuple<string?, string?>> LoadOverlays()
        {
            var snapshot = LoadSnapshot();
            if (snapshot == null)
            {
                return new Dictionary<DateTime, Tuple<string?, string?>>();
            }

            return ToOverlayMap(snapshot);
        }

        public static void SaveSnapshot(AttendanceHistorySnapshot snapshot)
        {
            var path = AppPaths.DataFile(StoreFileName);
            try
            {
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(path, json);
            }
            catch
            {
            }
        }

        public static Dictionary<DateTime, Tuple<string?, string?>> ToOverlayMap(AttendanceHistorySnapshot snapshot)
        {
            var result = new Dictionary<DateTime, Tuple<string?, string?>>();
            if (snapshot.Entries == null)
            {
                return result;
            }

            foreach (var entry in snapshot.Entries)
            {
                if (!DateTime.TryParseExact(entry.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                var first = NormalizeOverlayValue(entry.Value?.First);
                var second = NormalizeOverlayValue(entry.Value?.Second);
                result[date.Date] = Tuple.Create(first, second);
            }

            return result;
        }

        private static string? NormalizeOverlayValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var lowered = value.Trim().ToLowerInvariant();
            if (lowered == "none")
            {
                return null;
            }

            return lowered;
        }
    }
}
