using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Media = System.Windows.Media;
using System.Windows.Shapes;

namespace CeraRegularize.Controls
{
    /// <summary>
    /// Interaction logic for BubbleDayControl.xaml
    /// </summary>
    public partial class BubbleDayControl : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        // Colors representing modes and default bubble appearance. Theme resources
        // provide the actual palette so light/dark stay consistent.
        private static readonly Media.Color ModeWFO = (Media.Color)Media.ColorConverter.ConvertFromString("#B5EF8A");
        private static readonly Media.Color ModeWFH = (Media.Color)Media.ColorConverter.ConvertFromString("#BBE6E4");
        private static readonly Media.Color AttendanceOther = (Media.Color)Media.ColorConverter.ConvertFromString("#EAEFB1");

        private DateTime? _date;
        private bool _isCurrentMonth;
        private bool _isSelectable = true;
        private bool _isHovered;
        private string? _mode;
        private string _length = "full";
        private Tuple<string?, string?>? _attendanceState;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raised when the user clicks on this day. The payload is the associated
        /// date (or null when no valid date is present).
        /// </summary>
        public event EventHandler<DateTime?>? DayClicked;

        public BubbleDayControl()
        {
            InitializeComponent();
            // Set DataContext to this so that bindings in XAML resolve to our
            // properties.
            DataContext = this;
            // Hook into size changes to recompute overlay geometries.
            SizeChanged += (_, __) =>
            {
                UpdateAttendanceGeometry();
                UpdateSelectionGeometry();
            };
            // Attach click handler on the root grid.
            RootGrid.MouseLeftButtonUp += OnMouseLeftButtonUp;
            RootGrid.MouseEnter += (_, __) =>
            {
                _isHovered = true;
                UpdateVisuals();
            };
            RootGrid.MouseLeave += (_, __) =>
            {
                _isHovered = false;
                UpdateVisuals();
            };
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Only raise click if a valid date is set and the cell is enabled.
            if (_date != null && _isCurrentMonth && _isSelectable)
            {
                DayClicked?.Invoke(this, _date);
            }
        }

        public DateTime? Date
        {
            get => _date;
            set
            {
                if (_date != value)
                {
                    _date = value;
                    OnPropertyChanged(nameof(DayString));
                    UpdateVisuals();
                }
            }
        }

        /// <summary>
        /// Whether this day belongs to the currently displayed month. Days outside
        /// of the current month are shown dimmed and are not interactive.
        /// </summary>
        public bool IsCurrentMonth
        {
            get => _isCurrentMonth;
            set
            {
                if (_isCurrentMonth != value)
                {
                    _isCurrentMonth = value;
                    UpdateVisuals();
                }
            }
        }

        public bool IsSelectable
        {
            get => _isSelectable;
            set
            {
                if (_isSelectable != value)
                {
                    _isSelectable = value;
                    UpdateVisuals();
                }
            }
        }

        /// <summary>
        /// The mode assigned to this date: "wfo", "wfh" or null/empty for none.
        /// </summary>
        public string? Mode
        {
            get => _mode;
            set
            {
                if (_mode != value)
                {
                    _mode = value;
                    UpdateVisuals();
                }
            }
        }

        /// <summary>
        /// The length assignment ("full", "first_half", "second_half"). Not
        /// currently used to alter rendering beyond full/haf.
        /// </summary>
        public string Length
        {
            get => _length;
            set
            {
                if (_length != value)
                {
                    _length = value;
                    UpdateVisuals();
                    UpdateSelectionGeometry();
                    UpdateAttendanceGeometry();
                }
            }
        }

        /// <summary>
        /// Optional attendance overlay state, where Item1 is the morning half and
        /// Item2 is the afternoon half. A category of "wfo", "wfh" or
        /// "other" will tint the respective half with a transparent color.
        /// </summary>
        public Tuple<string?, string?>? AttendanceState
        {
            get => _attendanceState;
            set
            {
                if (_attendanceState != value)
                {
                    _attendanceState = value;
                    UpdateVisuals();
                    UpdateAttendanceGeometry();
                }
            }
        }

        /// <summary>
        /// Text representation of the day number, or empty when Date is null.
        /// </summary>
        public string DayString => _date?.Day.ToString() ?? string.Empty;

        /// <summary>
        /// Refresh the colors and visibility of the visual parts based on the
        /// current state (date, mode, attendance, current month).
        /// </summary>
        private void UpdateVisuals()
        {
            // If no date, disable the control entirely.
            bool hasDate = _date.HasValue;
            RootGrid.IsEnabled = hasDate && _isCurrentMonth && _isSelectable;
            DayText.Visibility = hasDate ? Visibility.Visible : Visibility.Collapsed;

            // Background color: dim when out of month or date is null.
            Media.Color bg;
            Media.Color fg;
            Media.Color border;
            if (!hasDate)
            {
                bg = Media.Colors.Transparent;
                fg = Media.Colors.Transparent;
                border = Media.Colors.Transparent;
            }
            else if (!_isCurrentMonth)
            {
                bg = ResolveThemeColor("BubbleBackgroundColor", Media.Colors.Transparent);
                fg = ResolveThemeColor("BubbleDimForegroundColor", Media.Colors.Gray);
                border = ResolveThemeColor("BubbleBorderColor", Media.Colors.Transparent);
            }
            else
            {
                bg = _isHovered && _isSelectable
                    ? ResolveThemeColor("BubbleBackgroundHoverColor", ResolveThemeColor("BubbleBackgroundColor", Media.Colors.Transparent))
                    : ResolveThemeColor("BubbleBackgroundColor", Media.Colors.Transparent);
                fg = ResolveThemeColor("BubbleForegroundColor", Media.Colors.Black);
                border = ResolveThemeColor("BubbleBorderColor", Media.Colors.Transparent);
            }

            BackgroundEllipse.Fill = new Media.SolidColorBrush(bg);
            BackgroundEllipse.Stroke = new Media.SolidColorBrush(border);
            DayText.Foreground = new Media.SolidColorBrush(fg);

            // Mode ring: show only when mode is assigned and date exists and in month.
            if (hasDate && _isCurrentMonth && !string.IsNullOrEmpty(_mode))
            {
                Media.Color ringColor = _mode?.ToLower() switch
                {
                    "wfo" => ModeWFO,
                    "wfh" => ModeWFH,
                    _ => Media.Colors.Transparent,
                };
                var ringBrush = new Media.SolidColorBrush(ringColor);
                var lengthKey = (_length ?? "full").ToLowerInvariant();
                if (lengthKey == "first_half")
                {
                    ModeRing.Visibility = Visibility.Collapsed;
                    ModeRingBottom.Visibility = Visibility.Collapsed;
                    ModeRingTop.Stroke = ringBrush;
                    ModeRingTop.Visibility = Visibility.Visible;
                }
                else if (lengthKey == "second_half")
                {
                    ModeRing.Visibility = Visibility.Collapsed;
                    ModeRingTop.Visibility = Visibility.Collapsed;
                    ModeRingBottom.Stroke = ringBrush;
                    ModeRingBottom.Visibility = Visibility.Visible;
                }
                else
                {
                    ModeRingTop.Visibility = Visibility.Collapsed;
                    ModeRingBottom.Visibility = Visibility.Collapsed;
                    ModeRing.Stroke = ringBrush;
                    ModeRing.Visibility = ringColor == Media.Colors.Transparent ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else
            {
                ModeRing.Visibility = Visibility.Collapsed;
                ModeRingTop.Visibility = Visibility.Collapsed;
                ModeRingBottom.Visibility = Visibility.Collapsed;
            }

            // Attendance overlay: update geometry separately; here toggle visibility.
            if (hasDate && _attendanceState != null)
            {
                TopHalf.Visibility = _attendanceState.Item1 != null ? Visibility.Visible : Visibility.Collapsed;
                BottomHalf.Visibility = _attendanceState.Item2 != null ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                TopHalf.Visibility = Visibility.Collapsed;
                BottomHalf.Visibility = Visibility.Collapsed;
            }

            // Notify any bound property changed (for DayString etc.)
            OnPropertyChanged(nameof(DayString));
        }

        private void UpdateSelectionGeometry()
        {
            double width = RootGrid.ActualWidth;
            double height = RootGrid.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var thickness = ModeRing.StrokeThickness;
            double radius = Math.Min(width, height) / 2 - (thickness / 2) - 1;
            if (radius <= 0)
            {
                return;
            }
            System.Windows.Point center = new System.Windows.Point(width / 2, height / 2);

            Media.PathGeometry BuildArc(double startAngleDeg, double sweepAngleDeg)
            {
                var startRad = (startAngleDeg - 90) * Math.PI / 180.0;
                var endRad = (startAngleDeg + sweepAngleDeg - 90) * Math.PI / 180.0;
                var startPt = new System.Windows.Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
                var endPt = new System.Windows.Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));
                var figure = new Media.PathFigure
                {
                    StartPoint = startPt,
                    IsClosed = false,
                };
                var isLargeArc = Math.Abs(sweepAngleDeg) > 180;
                var sweep = sweepAngleDeg >= 0 ? Media.SweepDirection.Clockwise : Media.SweepDirection.Counterclockwise;
                figure.Segments.Add(new Media.ArcSegment(endPt, new System.Windows.Size(radius, radius), 0, isLargeArc, sweep, true));
                var geometry = new Media.PathGeometry();
                geometry.Figures.Add(figure);
                return geometry;
            }

            // First half is left, second half is right.
            ModeRingTop.Data = BuildArc(180, 180);
            ModeRingBottom.Data = BuildArc(0, 180);
        }

        /// <summary>
        /// Compute and assign the geometry for the attendance overlay arcs based
        /// on the current size of the control and attendance state.
        /// </summary>
        private void UpdateAttendanceGeometry()
        {
            if (_date == null || _attendanceState == null)
            {
                // Hide overlays when no data.
                TopHalf.Visibility = Visibility.Collapsed;
                BottomHalf.Visibility = Visibility.Collapsed;
                return;
            }
            double width = RootGrid.ActualWidth;
            double height = RootGrid.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }
            var borderInset = BackgroundEllipse.StrokeThickness;
            double radius = Math.Min(width, height) / 2 - borderInset - 1; // keep inside bubble border
            if (radius <= 0)
            {
                return;
            }
            System.Windows.Point center = new System.Windows.Point(width / 2, height / 2);

            // Helper to build a sector path from start angle to end angle (in degrees).
            Media.PathGeometry BuildSector(double startAngleDeg, double sweepAngleDeg)
            {
                Media.PathFigure pf = new Media.PathFigure();
                // Move to center
                pf.StartPoint = center;
                // Compute first point on circumference
                double startRad = (startAngleDeg - 90) * Math.PI / 180.0; // subtract 90° so 0° is at top
                System.Windows.Point startPt = new System.Windows.Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
                pf.Segments.Add(new Media.LineSegment(startPt, true));
                // Create arc
                double endRad = (startAngleDeg + sweepAngleDeg - 90) * Math.PI / 180.0;
                System.Windows.Point endPt = new System.Windows.Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));
                bool isLargeArc = Math.Abs(sweepAngleDeg) > 180;
                Media.SweepDirection sweep = sweepAngleDeg >= 0 ? Media.SweepDirection.Clockwise : Media.SweepDirection.Counterclockwise;
                pf.Segments.Add(new Media.ArcSegment(endPt, new System.Windows.Size(radius, radius), 0, isLargeArc, sweep, true));
                // Close back to center
                pf.Segments.Add(new Media.LineSegment(center, true));
                Media.PathGeometry geo = new Media.PathGeometry();
                geo.Figures.Add(pf);
                return geo;
            }

            // Determine colors for attendance halves
            Media.Color? firstCol = ResolveAttendanceColor(_attendanceState.Item1);
            Media.Color? secondCol = ResolveAttendanceColor(_attendanceState.Item2);

            // Build left (first) half geometry (180° to 360°) if color defined
            if (firstCol.HasValue)
            {
                TopHalf.Data = BuildSector(180, 180);
                TopHalf.Fill = new Media.SolidColorBrush(firstCol.Value);
                TopHalf.Visibility = Visibility.Visible;
            }
            else
            {
                TopHalf.Visibility = Visibility.Collapsed;
            }
            // Build right (second) half geometry (0° to 180°)
            if (secondCol.HasValue)
            {
                BottomHalf.Data = BuildSector(0, 180);
                BottomHalf.Fill = new Media.SolidColorBrush(secondCol.Value);
                BottomHalf.Visibility = Visibility.Visible;
            }
            else
            {
                BottomHalf.Visibility = Visibility.Collapsed;
            }
        }

        private static Media.Color? ResolveAttendanceColor(string? category)
        {
            if (string.IsNullOrEmpty(category))
            {
                return null;
            }
            var normalized = category.ToLowerInvariant();
            if (normalized == "none" || normalized == "absent" || normalized == "weekend" || normalized == "holiday")
            {
                return null;
            }
            Media.Color baseColor = normalized switch
            {
                "wfo" => ModeWFO,
                "wfh" => ModeWFH,
                "other" => AttendanceOther,
                _ => AttendanceOther,
            };
            return baseColor;
        }

        private static Media.Color ResolveThemeColor(string key, Media.Color fallback)
        {
            try
            {
                if (System.Windows.Application.Current?.Resources[key] is Media.Color color)
                {
                    return color;
                }
                if (System.Windows.Application.Current?.Resources[key] is Media.SolidColorBrush brush)
                {
                    return brush.Color;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
