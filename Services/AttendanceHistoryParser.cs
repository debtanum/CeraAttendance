using CeraRegularize.Logging;
using CeraRegularize.Stores;
using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CeraRegularize.Services
{
    public sealed class AttendanceHistoryParser
    {
        private static readonly HashSet<char> AllowedCodes = new("ACDEHLOPWRTBGS".ToCharArray());

        public AttendanceHistoryParser(DateTime rangeStart, DateTime rangeEnd)
        {
            RangeStart = rangeStart.Date;
            RangeEnd = rangeEnd.Date;
        }

        public DateTime RangeStart { get; }
        public DateTime RangeEnd { get; }

        public static (DateTime start, DateTime end) ComputeHistoryRange(DateTime? reference = null)
        {
            var today = (reference ?? DateTime.Today).Date;
            int prevYear;
            int prevMonth;
            if (today.Month == 1)
            {
                prevYear = today.Year - 1;
                prevMonth = 12;
            }
            else
            {
                prevYear = today.Year;
                prevMonth = today.Month - 1;
            }

            var startDay = Math.Min(21, DateTime.DaysInMonth(prevYear, prevMonth));
            var rangeStart = new DateTime(prevYear, prevMonth, startDay);
            var rangeEnd = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
            return (rangeStart, rangeEnd);
        }

        public async Task<Dictionary<string, AttendanceHistoryEntry>> CollectRegularizeEntriesAsync(IPage page, CancellationToken token)
        {
            var entries = new Dictionary<string, AttendanceHistoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in CycleOptionValues())
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(option))
                {
                    continue;
                }

                await SelectCycleAsync(page, option).ConfigureAwait(false);
                var chunk = await ParseRegularizeTableAsync(page, token).ConfigureAwait(false);
                AppLogger.LogDebug($"Regularize cycle {option} yielded {chunk.Count} entries", nameof(AttendanceHistoryParser));
                foreach (var item in chunk)
                {
                    entries[item.Key] = item.Value;
                }
            }

            return entries;
        }

        public async Task<Dictionary<string, AttendanceHistoryEntry>> CollectLeaveStatusEntriesAsync(IPage page, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return await ParseLeaveStatusTableAsync(page, token).ConfigureAwait(false);
        }

        public void MergeEntries(Dictionary<string, AttendanceHistoryEntry> baseEntries, Dictionary<string, AttendanceHistoryEntry> overlay)
        {
            foreach (var item in overlay)
            {
                if (item.Value == null)
                {
                    continue;
                }

                if (!baseEntries.TryGetValue(item.Key, out var existing) || existing == null)
                {
                    continue;
                }

                if (!HasAbsent(existing))
                {
                    continue;
                }

                var overlayEntry = item.Value;
                var merged = new AttendanceHistoryEntry
                {
                    First = MergeHalf(existing.First, overlayEntry.First),
                    Second = MergeHalf(existing.Second, overlayEntry.Second),
                    Source = "leave_status",
                    HasAbsent = false,
                };

                baseEntries[item.Key] = merged;
            }
        }

        private static string? MergeHalf(string? original, string? overlay)
        {
            if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(overlay))
            {
                return original;
            }

            return original.Equals(AttendanceHistoryCategories.Absent, StringComparison.OrdinalIgnoreCase)
                ? overlay
                : original;
        }

        private IEnumerable<string> CycleOptionValues()
        {
            var values = new[]
            {
                DropdownValueForDate(RangeStart),
                DropdownValueForDate(RangeEnd)
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (seen.Add(value))
                {
                    yield return value;
                }
            }
        }

        private static string DropdownValueForDate(DateTime targetDate)
        {
            var year = targetDate.Year;
            var month = targetDate.Month;
            if (targetDate.Day >= 21)
            {
                (year, month) = AddMonth(year, month, 1);
            }

            var lastDay = DateTime.DaysInMonth(year, month);
            return $"{year:D4}-{month:D2}-{lastDay:D2}";
        }

        private static (int year, int month) AddMonth(int year, int month, int delta)
        {
            var newMonth = month + delta;
            var newYear = year + (newMonth - 1) / 12;
            newMonth = ((newMonth - 1) % 12) + 1;
            return (newYear, newMonth);
        }

        private static async Task SelectCycleAsync(IPage page, string optionValue)
        {
            var dropdown = page.Locator("#MiddleContent_ddlMonth");
            await dropdown.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 4000 }).ConfigureAwait(false);
            await dropdown.SelectOptionAsync(optionValue).ConfigureAwait(false);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
            AppLogger.LogDebug($"Selected regularize cycle {optionValue}", nameof(AttendanceHistoryParser));
        }

        private async Task<Dictionary<string, AttendanceHistoryEntry>> ParseRegularizeTableAsync(IPage page, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var entries = new Dictionary<string, AttendanceHistoryEntry>(StringComparer.OrdinalIgnoreCase);
            var cells = page.Locator("#MiddleContent_gvRep td");
            int count;
            try
            {
                count = await cells.CountAsync().ConfigureAwait(false);
            }
            catch
            {
                return entries;
            }

            if (count == 0)
            {
                return entries;
            }

            var sample = new List<string>(3);
            AppLogger.LogDebug($"Regularize table cells found: {count}", nameof(AttendanceHistoryParser));
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var cell = cells.Nth(i);
                var text = await ReadCellAsync(cell).ConfigureAwait(false);
                var title = await ReadAttributeAsync(cell, "title").ConfigureAwait(false);
                if (sample.Count < 3)
                {
                    sample.Add(FormatRecordSample(text, title));
                }
                var normalizedText = (text ?? string.Empty)
                    .Replace("\r", string.Empty)
                    .Replace("\u00A0", " ");
                var segments = normalizedText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(segment => segment.Trim())
                    .Where(segment => !string.IsNullOrWhiteSpace(segment))
                    .ToList();
                title = (title ?? string.Empty).Replace("\r", string.Empty);
                if (!TryExtractDateFromTitle(title, out var date))
                {
                    if (!TryExtractDateFromSegments(segments, out date))
                    {
                        continue;
                    }
                }
                if (segments.Count == 0 && string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }
                if (date < RangeStart || date > RangeEnd)
                {
                    continue;
                }

                var code = ExtractCode(segments, title);
                if (string.IsNullOrWhiteSpace(code))
                {
                    continue;
                }

                var (first, second) = CodeToCategories(code);
                entries[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = new AttendanceHistoryEntry
                {
                    First = first,
                    Second = second,
                    Source = "regularize",
                    HasAbsent = CodeHasAbsent(code),
                };
            }

            if (entries.Count == 0 && sample.Count > 0)
            {
                AppLogger.LogWarning($"Regularize parse yielded 0 entries. Sample cells: {string.Join(" | ", sample)}", nameof(AttendanceHistoryParser));
            }
            return entries;
        }

        private async Task<Dictionary<string, AttendanceHistoryEntry>> ParseLeaveStatusTableAsync(IPage page, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var entries = new Dictionary<string, AttendanceHistoryEntry>(StringComparer.OrdinalIgnoreCase);
            var rows = page.Locator("#MiddleContent_gvRep tr");
            int count;
            try
            {
                count = await rows.CountAsync().ConfigureAwait(false);
            }
            catch
            {
                return entries;
            }

            if (count <= 1)
            {
                return entries;
            }

            for (var i = 1; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = rows.Nth(i);
                var cells = row.Locator("td");
                var cellCount = await cells.CountAsync().ConfigureAwait(false);
                if (cellCount < 10)
                {
                    continue;
                }

                var fromRaw = await ReadCellAsync(cells.Nth(4)).ConfigureAwait(false);
                var toRaw = await ReadCellAsync(cells.Nth(5)).ConfigureAwait(false);
                var leaveType = await ReadCellAsync(cells.Nth(7)).ConfigureAwait(false);

                if (!TryParseLeaveStatusDate(fromRaw, out var start) || !TryParseLeaveStatusDate(toRaw, out var end))
                {
                    continue;
                }

                var category = CategoryForLeaveType(leaveType);
                if (category == null)
                {
                    continue;
                }

                foreach (var day in DateRange(start, end))
                {
                    if (day < RangeStart || day > RangeEnd)
                    {
                        continue;
                    }

                    entries[day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = new AttendanceHistoryEntry
                    {
                        First = category,
                        Second = category,
                        Source = "leave_status",
                    };
                }
            }

            return entries;
        }

        private static async Task<string> ReadCellAsync(ILocator cell)
        {
            try
            {
                var text = await cell.InnerTextAsync().ConfigureAwait(false);
                return text?.Replace("\u00A0", " ").Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<string> ReadAttributeAsync(ILocator cell, string name)
        {
            try
            {
                var value = await cell.GetAttributeAsync(name).ConfigureAwait(false);
                return value?.Replace("\u00A0", " ").Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryParseCalendarDate(string value, out DateTime date)
        {
            return DateTime.TryParseExact(
                value.Trim(),
                new[] { "dd MMM yyyy", "d MMM yyyy" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private static bool TryExtractDateFromSegments(IEnumerable<string> segments, out DateTime date)
        {
            date = default;
            foreach (var segment in segments)
            {
                if (TryParseCalendarDate(segment, out date))
                {
                    return true;
                }

                var tokens = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3)
                {
                    continue;
                }

                for (var i = 0; i + 2 < tokens.Length; i++)
                {
                    var candidate = $"{tokens[i]} {tokens[i + 1]} {tokens[i + 2]}";
                    if (TryParseCalendarDate(candidate, out date))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string FormatRecordSample(string text, string title)
        {
            var safeText = (text ?? string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", " ")
                .Replace("\u00A0", " ")
                .Trim();
            var safeTitle = (title ?? string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", " ")
                .Replace("\u00A0", " ")
                .Trim();
            return $"text='{safeText}' title='{safeTitle}'";
        }

        private static bool TryExtractDateFromTitle(string title, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            const string marker = "date :";
            var lowered = title.ToLowerInvariant();
            var idx = lowered.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            var segment = title[(idx + marker.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                return false;
            }

            var firstLine = segment.Split('\n')[0].Trim();
            return TryParseCalendarDate(firstLine, out date);
        }

        private static bool TryParseLeaveStatusDate(string value, out DateTime date)
        {
            var cleaned = value.Trim();
            if (DateTime.TryParseExact(
                cleaned,
                new[] { "dd.MMM.yyyy", "d.MMM.yyyy" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
            {
                return true;
            }
            return DateTime.TryParseExact(
                cleaned,
                new[] { "dd MMM yyyy", "d MMM yyyy" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private static string? ExtractCode(IEnumerable<string> lines, string title)
        {
            foreach (var line in lines)
            {
                var token = ExtractCodeFromFragment(line);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
            return CodeFromTitle(title);
        }

        private static string? ExtractCodeFromFragment(string fragment)
        {
            var tokens = fragment.Replace(":", " ").Replace("/", " ")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                var cleaned = new string(token.ToUpperInvariant().Where(char.IsLetter).ToArray());
                if (cleaned.Length == 0 || cleaned.Length > 2)
                {
                    continue;
                }
                if (cleaned.All(ch => AllowedCodes.Contains(ch)))
                {
                    if (cleaned.Length == 1)
                    {
                        cleaned = cleaned + cleaned;
                    }
                    return cleaned[..2];
                }
            }
            return null;
        }

        private static string? CodeFromTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var lowered = title.ToLowerInvariant();
            const string marker = "leave type :";
            var idx = lowered.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            var segment = title[(idx + marker.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                return null;
            }

            var firstLine = segment.Split('\n')[0].Trim();
            var loweredLine = firstLine.ToLowerInvariant();
            var halfDayCode = HalfDayCodeFromLeaveType(loweredLine);
            if (!string.IsNullOrWhiteSpace(halfDayCode))
            {
                return halfDayCode;
            }
            if (loweredLine.Contains("work from home"))
            {
                return "GG";
            }
            if (loweredLine.Contains("weekly off"))
            {
                return "WW";
            }
            if (loweredLine.Contains("present"))
            {
                return "PP";
            }
            if (loweredLine.Contains("optional leave"))
            {
                return "RR";
            }
            if (loweredLine.Contains("absent"))
            {
                return "AA";
            }
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return null;
            }

            var letter = char.ToUpperInvariant(firstLine[0]);
            if (!AllowedCodes.Contains(letter))
            {
                return null;
            }
            return new string(letter, 2);
        }

        private static string? HalfDayCodeFromLeaveType(string loweredLine)
        {
            if (string.IsNullOrWhiteSpace(loweredLine) || !loweredLine.Contains("1/2"))
            {
                return null;
            }

            var matches = new List<(int Index, char Code)>();
            var absentIdx = loweredLine.IndexOf("absent", StringComparison.Ordinal);
            if (absentIdx >= 0)
            {
                matches.Add((absentIdx, 'A'));
            }

            var casualIdx = loweredLine.IndexOf("casual", StringComparison.Ordinal);
            var sicknessIdx = loweredLine.IndexOf("sickness", StringComparison.Ordinal);
            var leaveIdx = -1;
            if (casualIdx >= 0 && sicknessIdx >= 0)
            {
                leaveIdx = Math.Min(casualIdx, sicknessIdx);
            }
            else if (casualIdx >= 0)
            {
                leaveIdx = casualIdx;
            }
            else if (sicknessIdx >= 0)
            {
                leaveIdx = sicknessIdx;
            }
            if (leaveIdx >= 0)
            {
                matches.Add((leaveIdx, 'C'));
            }

            var wfhIdx = loweredLine.IndexOf("work from home", StringComparison.Ordinal);
            if (wfhIdx >= 0)
            {
                matches.Add((wfhIdx, 'G'));
            }

            var presentIdx = loweredLine.IndexOf("present", StringComparison.Ordinal);
            if (presentIdx >= 0)
            {
                matches.Add((presentIdx, 'P'));
            }

            if (matches.Count < 2)
            {
                return null;
            }

            matches.Sort((a, b) => a.Index.CompareTo(b.Index));
            return $"{matches[0].Code}{matches[1].Code}";
        }

        private static (string first, string second) CodeToCategories(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return (AttendanceHistoryCategories.None, AttendanceHistoryCategories.None);
            }
            if (code.Length == 1)
            {
                code = code + code;
            }
            var first = LetterToCategory(code[0]);
            var second = LetterToCategory(code[1]);
            return (first, second);
        }

        private static string LetterToCategory(char letter)
        {
            var upper = char.ToUpperInvariant(letter);
            if (upper == 'A')
            {
                return AttendanceHistoryCategories.Absent;
            }
            if (upper == 'W')
            {
                return AttendanceHistoryCategories.Weekend;
            }
            if (upper == 'H')
            {
                return AttendanceHistoryCategories.Holiday;
            }
            if (upper == 'P')
            {
                return AttendanceHistoryCategories.Wfo;
            }
            if (upper == 'G')
            {
                return AttendanceHistoryCategories.Wfh;
            }
            return AttendanceHistoryCategories.Other;
        }

        private static string? CategoryForLeaveType(string value)
        {
            var lowered = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(lowered))
            {
                return null;
            }
            if (lowered.Contains("work from home"))
            {
                return AttendanceHistoryCategories.Wfh;
            }
            if (lowered.Contains("present"))
            {
                return AttendanceHistoryCategories.Wfo;
            }
            if (lowered.Contains("weekly off"))
            {
                return AttendanceHistoryCategories.Weekend;
            }
            if (lowered.Contains("holiday"))
            {
                return AttendanceHistoryCategories.Holiday;
            }
            if (lowered.Contains("absent"))
            {
                return AttendanceHistoryCategories.Absent;
            }
            return AttendanceHistoryCategories.Other;
        }

        private static IEnumerable<DateTime> DateRange(DateTime start, DateTime end)
        {
            var cursor = start.Date;
            var last = end.Date;
            while (cursor <= last)
            {
                yield return cursor;
                cursor = cursor.AddDays(1);
            }
        }

        private static bool HasAbsent(AttendanceHistoryEntry entry)
        {
            return entry != null && entry.HasAbsent;
        }

        private static bool CodeHasAbsent(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            foreach (var ch in code)
            {
                if (char.ToUpperInvariant(ch) == 'A')
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static class AttendanceHistoryCategories
    {
        public const string None = "none";
        public const string Absent = "absent";
        public const string Weekend = "weekend";
        public const string Holiday = "holiday";
        public const string Wfo = "wfo";
        public const string Wfh = "wfh";
        public const string Other = "other";
    }
}
