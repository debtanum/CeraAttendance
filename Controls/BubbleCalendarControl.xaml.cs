using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CeraRegularize.Logging;
using CeraRegularize.Services;

namespace CeraRegularize.Controls
{
    /// <summary>
    /// A custom calendar control that presents a month view similar to the
    /// original BubbleCalendar in the PySide6 codebase. It features month
    /// navigation, day labels, a grid of circular day cells and a legend
    /// indicating counts of WFO and WFH selections. Users can click a day
    /// cell to cycle through assignment modes (WFO → WFH → clear).
    /// </summary>
    public partial class BubbleCalendarControl : System.Windows.Controls.UserControl
    {
        private readonly struct SelectionAllowance
        {
            public SelectionAllowance(bool allow, bool allowFull, bool allowFirst, bool allowSecond)
            {
                Allow = allow;
                AllowFull = allowFull;
                AllowFirst = allowFirst;
                AllowSecond = allowSecond;
            }

            public bool Allow { get; }
            public bool AllowFull { get; }
            public bool AllowFirst { get; }
            public bool AllowSecond { get; }

            public static SelectionAllowance AllowAll() =>
                new SelectionAllowance(true, true, true, true);

            public static SelectionAllowance DenyAll() =>
                new SelectionAllowance(false, false, false, false);
        }
        // Track modes (wfo/wfh) assigned to dates.
        private readonly Dictionary<DateTime, string> _dateModes = new();
        // Track day length selections per date.
        private readonly Dictionary<DateTime, string> _dateLengths = new();
        // Attendance overlays for historic data (morning/afternoon categories). Not
        // currently populated but can be used to pre‑color halves.
        private Dictionary<DateTime, Tuple<string?, string?>> _attendanceOverlays = new();
        private string _selectionMode = "popup";
        private bool _isCeragonView;

        public event EventHandler? SelectionChanged;

        public BubbleCalendarControl()
        {
            InitializeComponent();
            // Default to the current month.
            CurrentMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            RenderCalendar();
        }

        /// <summary>
        /// The first day of the month currently displayed. Setting this will
        /// re-render the calendar.
        /// </summary>
        public DateTime CurrentMonth { get; private set; }

        /// <summary>
        /// Map of date to overlay categories for the morning (Item1) and
        /// afternoon (Item2) halves. A null or empty dictionary clears any
        /// overlays.
        /// </summary>
        public Dictionary<DateTime, Tuple<string?, string?>> AttendanceOverlays
        {
            get => _attendanceOverlays;
            set
            {
                _attendanceOverlays = value ?? new Dictionary<DateTime, Tuple<string?, string?>>();
                RenderCalendar();
            }
        }

        /// <summary>
        /// Return a read‑only snapshot of the current selections. Each entry
        /// contains the date and assigned mode.
        /// </summary>
        public IReadOnlyDictionary<DateTime, string> DateModes => _dateModes;

        public IReadOnlyDictionary<DateTime, string> DateLengths => _dateLengths;

        public string SelectionMode => _selectionMode;

        public bool IsCeragonView => _isCeragonView;

        private void PrevMonthButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentMonth = CurrentMonth.AddMonths(-1);
            RenderCalendar();
        }

        private void NextMonthButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentMonth = CurrentMonth.AddMonths(1);
            RenderCalendar();
        }

        /// <summary>
        /// Clear all selections from the calendar. Called externally by the
        /// containing HomePage when the Cancel button is clicked.
        /// </summary>
        public void ClearSelections()
        {
            _dateModes.Clear();
            _dateLengths.Clear();
            RenderCalendar();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Toggle mode for the given date, cycling through WFO → WFH → clear.
        /// </summary>
        private void ToggleMode(DateTime dt, SelectionAllowance allowance)
        {
            string? current = _dateModes.ContainsKey(dt) ? _dateModes[dt] : null;
            if (current == null)
            {
                ApplyMode(dt, "wfo", allowance);
            }
            else if (current == "wfo")
            {
                ApplyMode(dt, "wfh", allowance);
            }
            else
            {
                ClearSelection(dt);
            }
            RenderCalendar();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            AppLogger.LogDebug($"Toggled mode: {dt:yyyy-MM-dd}", nameof(BubbleCalendarControl));
        }

        /// <summary>
        /// Rebuild the day grid based on the current month and selection state.
        /// </summary>
        private void RenderCalendar()
        {
            DateTime? rangeStart = null;
            DateTime? rangeEnd = null;
            if (_isCeragonView)
            {
                rangeStart = GetCeragonRangeStart(CurrentMonth);
                rangeEnd = GetCeragonRangeEnd(CurrentMonth);
                MonthLabel.Text = BuildCeragonLabel(rangeStart.Value, rangeEnd.Value).ToUpperInvariant();
            }
            else
            {
                MonthLabel.Text = CurrentMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
            }

            // Clear any existing children.
            DayGrid.Children.Clear();

            // Determine the first date to display (Monday of the week containing the 1st/range start).
            DayOfWeek firstDayOfWeek = DayOfWeek.Monday;
            DateTime anchorDate;
            if (_isCeragonView && rangeStart.HasValue)
            {
                anchorDate = rangeStart.Value;
            }
            else
            {
                anchorDate = new DateTime(CurrentMonth.Year, CurrentMonth.Month, 1);
            }
            int diff = ((int)anchorDate.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
            DateTime startDate = anchorDate.AddDays(-diff);
            // Fill 42 cells (6 weeks).
            for (int i = 0; i < 42; i++)
            {
                DateTime cellDate = startDate.AddDays(i);
                bool inActiveRange = _isCeragonView && rangeStart.HasValue && rangeEnd.HasValue
                    ? cellDate >= rangeStart.Value && cellDate <= rangeEnd.Value
                    : cellDate.Month == CurrentMonth.Month;
                var allowance = inActiveRange ? GetSelectionAllowance(cellDate) : SelectionAllowance.DenyAll();
                if (inActiveRange)
                {
                    NormalizeSelectionForDate(cellDate, allowance);
                }
                var cell = new BubbleDayControl
                {
                    Date = cellDate,
                    IsCurrentMonth = inActiveRange,
                    IsSelectable = inActiveRange && allowance.Allow,
                    Mode = inActiveRange && _dateModes.ContainsKey(cellDate) ? _dateModes[cellDate] : null,
                    Length = inActiveRange && _dateLengths.TryGetValue(cellDate, out var length) ? length : AttendanceAutomator.DayLengthFull,
                    AttendanceState = inActiveRange && _attendanceOverlays.ContainsKey(cellDate) ? _attendanceOverlays[cellDate] : null
                };
                cell.DayClicked += (_, dt) =>
                {
                    if (dt.HasValue)
                    {
                        HandleDayClick(cell, dt.Value);
                    }
                };
                cell.MouseRightButtonUp += (_, args) =>
                {
                    if (!cell.Date.HasValue || !cell.IsCurrentMonth || !cell.IsSelectable)
                    {
                        return;
                    }
                    args.Handled = true;
                    HandleLengthAction(cell, cell.Date.Value);
                };
                DayGrid.Children.Add(cell);
            }
            UpdateCounts();
        }

        /// <summary>
        /// Update the legend counts for WFO/WFH based on current selections.
        /// </summary>
        private void UpdateCounts()
        {
            int wfo = 0;
            int wfh = 0;
            foreach (var mode in _dateModes.Values)
            {
                switch (mode)
                {
                    case "wfo":
                        wfo++;
                        break;
                    case "wfh":
                        wfh++;
                        break;
                }
            }
            WfoCountText.Text = $"WFO: {wfo}";
            WfhCountText.Text = $"WFH: {wfh}";
        }

        public void SetSelectionMode(string mode)
        {
            var normalized = string.Equals(mode, "loop", StringComparison.OrdinalIgnoreCase)
                ? "loop"
                : "popup";
            _selectionMode = normalized;
            AppLogger.LogInfo($"Selection mode set: {_selectionMode}", nameof(BubbleCalendarControl));
        }

        public void SetCeragonView(bool enabled)
        {
            if (_isCeragonView == enabled)
            {
                return;
            }

            _isCeragonView = enabled;
            RenderCalendar();
        }

        public List<(DateTime date, string mode, string span)> GetSelections()
        {
            if (_dateModes.Count == 0)
            {
                return new List<(DateTime date, string mode, string span)>();
            }

            var results = new List<(DateTime date, string mode, string span)>();
            foreach (var entry in _dateModes)
            {
                var length = _dateLengths.TryGetValue(entry.Key, out var span)
                    ? span
                    : AttendanceAutomator.DayLengthFull;
                results.Add((entry.Key, entry.Value, length));
            }

            results.Sort((a, b) => a.date.CompareTo(b.date));
            return results;
        }

        private void HandleDayClick(BubbleDayControl cell, DateTime date)
        {
            var allowance = GetSelectionAllowance(date);
            if (!allowance.Allow)
            {
                return;
            }
            if (_selectionMode == "loop")
            {
                ToggleMode(date, allowance);
                return;
            }

            OpenModeMenu(cell, date, allowance);
        }

        private void HandleLengthAction(BubbleDayControl cell, DateTime date)
        {
            if (!_dateModes.ContainsKey(date))
            {
                return;
            }

            var allowance = GetSelectionAllowance(date);
            if (!allowance.Allow)
            {
                return;
            }

            if (_selectionMode == "loop")
            {
                CycleLength(date, allowance);
                return;
            }

            OpenLengthMenu(cell, date, allowance);
        }

        private void OpenModeMenu(BubbleDayControl cell, DateTime date, SelectionAllowance allowance)
        {
            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = cell,
                Placement = PlacementMode.Bottom,
            };

            var setWfo = new System.Windows.Controls.MenuItem { Header = "Set WFO" };
            var setWfh = new System.Windows.Controls.MenuItem { Header = "Set WFH" };
            var clear = new System.Windows.Controls.MenuItem { Header = "Clear Selection" };

            setWfo.Click += (_, _) =>
            {
                ApplyMode(date, "wfo", allowance);
                RenderCalendar();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            setWfh.Click += (_, _) =>
            {
                ApplyMode(date, "wfh", allowance);
                RenderCalendar();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            clear.Click += (_, _) =>
            {
                ClearSelection(date);
                RenderCalendar();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };

            menu.Items.Add(setWfo);
            menu.Items.Add(setWfh);
            menu.Items.Add(new Separator());
            menu.Items.Add(clear);
            menu.IsOpen = true;
        }

        private void OpenLengthMenu(BubbleDayControl cell, DateTime date, SelectionAllowance allowance)
        {
            var current = _dateLengths.TryGetValue(date, out var length)
                ? length
                : AttendanceAutomator.DayLengthFull;

            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = cell,
                Placement = PlacementMode.MousePoint,
            };

            var full = BuildLengthMenuItem("Full day", AttendanceAutomator.DayLengthFull, current, date, allowance.AllowFull);
            var first = BuildLengthMenuItem("1st half", AttendanceAutomator.DayLengthFirstHalf, current, date, allowance.AllowFirst);
            var second = BuildLengthMenuItem("2nd half", AttendanceAutomator.DayLengthSecondHalf, current, date, allowance.AllowSecond);

            menu.Items.Add(full);
            menu.Items.Add(first);
            menu.Items.Add(second);
            menu.IsOpen = true;
        }

        private System.Windows.Controls.MenuItem BuildLengthMenuItem(string label, string lengthKey, string current, DateTime date, bool enabled)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = string.Equals(current, lengthKey, StringComparison.OrdinalIgnoreCase),
                IsEnabled = enabled,
            };
            item.Click += (_, _) =>
            {
                _dateLengths[date] = lengthKey;
                RenderCalendar();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            return item;
        }

        private void ApplyMode(DateTime date, string mode, SelectionAllowance allowance)
        {
            _dateModes[date] = mode;
            _dateLengths[date] = ResolveAllowedLength(date, allowance);
            AppLogger.LogDebug($"Applied mode {mode} on {date:yyyy-MM-dd}", nameof(BubbleCalendarControl));
        }

        private void ApplyMode(DateTime date, string mode)
        {
            ApplyMode(date, mode, SelectionAllowance.AllowAll());
        }

        private void ClearSelection(DateTime date)
        {
            _dateModes.Remove(date);
            _dateLengths.Remove(date);
            AppLogger.LogDebug($"Cleared selection {date:yyyy-MM-dd}", nameof(BubbleCalendarControl));
        }

        private void CycleLength(DateTime date, SelectionAllowance allowance)
        {
            if (!_dateModes.ContainsKey(date))
            {
                return;
            }

            var allowed = GetAllowedLengths(allowance);
            if (allowed.Count == 0)
            {
                return;
            }
            var current = _dateLengths.TryGetValue(date, out var length)
                ? length
                : AttendanceAutomator.DayLengthFull;
            if (!allowed.Contains(current))
            {
                current = allowed[0];
            }
            var idx = allowed.IndexOf(current);
            var next = allowed[(idx + 1) % allowed.Count];
            _dateLengths[date] = next;
            RenderCalendar();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            AppLogger.LogDebug($"Length cycled to {next} on {date:yyyy-MM-dd}", nameof(BubbleCalendarControl));
        }

        private void NormalizeSelectionForDate(DateTime date, SelectionAllowance allowance)
        {
            if (!_dateModes.ContainsKey(date))
            {
                return;
            }

            if (!allowance.Allow)
            {
                ClearSelection(date);
                return;
            }

            _dateLengths[date] = ResolveAllowedLength(date, allowance);
        }

        private SelectionAllowance GetSelectionAllowance(DateTime date)
        {
            if (!_attendanceOverlays.TryGetValue(date, out var state) || state == null)
            {
                return SelectionAllowance.AllowAll();
            }

            var first = NormalizeCategory(state.Item1);
            var second = NormalizeCategory(state.Item2);
            var firstMarked = IsMarked(first);
            var secondMarked = IsMarked(second);
            if (!firstMarked && !secondMarked)
            {
                return SelectionAllowance.AllowAll();
            }

            if (IsWeekend(first) || IsWeekend(second) || IsHoliday(first) || IsHoliday(second))
            {
                return SelectionAllowance.DenyAll();
            }

            var firstAbsent = IsAbsent(first);
            var secondAbsent = IsAbsent(second);
            if (firstAbsent || secondAbsent)
            {
                if (firstAbsent && secondAbsent)
                {
                    return new SelectionAllowance(true, true, true, true);
                }

                return new SelectionAllowance(true, false, firstAbsent, secondAbsent);
            }

            return SelectionAllowance.DenyAll();
        }

        private static string NormalizeCategory(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return string.Empty;
            }

            return category.Trim().ToLowerInvariant();
        }

        private static bool IsMarked(string category)
        {
            return !string.IsNullOrWhiteSpace(category) && category != AttendanceHistoryCategories.None;
        }

        private static bool IsAbsent(string category)
        {
            return category == AttendanceHistoryCategories.Absent;
        }

        private static bool IsWeekend(string category)
        {
            return category == AttendanceHistoryCategories.Weekend;
        }

        private static bool IsHoliday(string category)
        {
            return category == AttendanceHistoryCategories.Holiday;
        }

        private static List<string> GetAllowedLengths(SelectionAllowance allowance)
        {
            var allowed = new List<string>(3);
            if (allowance.AllowFull)
            {
                allowed.Add(AttendanceAutomator.DayLengthFull);
            }
            if (allowance.AllowFirst)
            {
                allowed.Add(AttendanceAutomator.DayLengthFirstHalf);
            }
            if (allowance.AllowSecond)
            {
                allowed.Add(AttendanceAutomator.DayLengthSecondHalf);
            }
            return allowed;
        }

        private static bool IsLengthAllowed(string length, SelectionAllowance allowance)
        {
            if (string.Equals(length, AttendanceAutomator.DayLengthFull, StringComparison.OrdinalIgnoreCase))
            {
                return allowance.AllowFull;
            }
            if (string.Equals(length, AttendanceAutomator.DayLengthFirstHalf, StringComparison.OrdinalIgnoreCase))
            {
                return allowance.AllowFirst;
            }
            if (string.Equals(length, AttendanceAutomator.DayLengthSecondHalf, StringComparison.OrdinalIgnoreCase))
            {
                return allowance.AllowSecond;
            }
            return allowance.AllowFull;
        }

        private string ResolveAllowedLength(DateTime date, SelectionAllowance allowance)
        {
            var current = _dateLengths.TryGetValue(date, out var length)
                ? length
                : AttendanceAutomator.DayLengthFull;
            if (IsLengthAllowed(current, allowance))
            {
                return current;
            }
            if (allowance.AllowFull)
            {
                return AttendanceAutomator.DayLengthFull;
            }
            if (allowance.AllowFirst)
            {
                return AttendanceAutomator.DayLengthFirstHalf;
            }
            if (allowance.AllowSecond)
            {
                return AttendanceAutomator.DayLengthSecondHalf;
            }
            return AttendanceAutomator.DayLengthFull;
        }

        private static DateTime GetCeragonRangeStart(DateTime currentMonth)
        {
            var previous = currentMonth.AddMonths(-1);
            return new DateTime(previous.Year, previous.Month, 21);
        }

        private static DateTime GetCeragonRangeEnd(DateTime currentMonth)
        {
            return new DateTime(currentMonth.Year, currentMonth.Month, 20);
        }

        private static string BuildCeragonLabel(DateTime rangeStart, DateTime rangeEnd)
        {
            var startMonth = rangeStart.ToString("MMMM", CultureInfo.InvariantCulture);
            var endMonth = rangeEnd.ToString("MMMM", CultureInfo.InvariantCulture);
            if (rangeStart.Year == rangeEnd.Year)
            {
                return $"{startMonth} - {endMonth} {rangeEnd:yyyy}";
            }

            return $"{startMonth} {rangeStart:yyyy} - {endMonth} {rangeEnd:yyyy}";
        }
    }
}
