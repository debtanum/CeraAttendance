using CeraRegularize.Logging;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace CeraRegularize.Pages
{
    public partial class HomePage : System.Windows.Controls.UserControl
    {
        public event EventHandler? SubmitRequested;
        public event EventHandler? CancelRequested;
        private bool _ceragonView;

        public HomePage()
        {
            InitializeComponent();
            Calendar.SelectionChanged += (_, _) => UpdateActionButtons();
            ViewToggle.Checked += (_, _) => SetCeragonView(true);
            ViewToggle.Unchecked += (_, _) => SetCeragonView(false);
            ViewToggle.IsChecked = true;
            SetCeragonView(true);
            UpdateActionButtons();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            ClearSelections();
            CancelRequested?.Invoke(this, EventArgs.Empty);
            AppLogger.LogDebug("Cancel clicked", nameof(HomePage));
        }

        private void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            var selections = GetSelections();
            if (selections.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "Select at least one date and mode before submitting.",
                    "CeraRegularize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            SubmitRequested?.Invoke(this, EventArgs.Empty);
            AppLogger.LogDebug("Submit clicked", nameof(HomePage));
        }

        public IReadOnlyList<(DateTime date, string mode, string span)> GetSelections()
        {
            return Calendar.GetSelections();
        }

        public void SetActionsEnabled(bool enabled)
        {
            SubmitButton.IsEnabled = enabled;
            CancelButton.IsEnabled = enabled;
        }

        public void ClearSelections()
        {
            Calendar.ClearSelections();
            UpdateActionButtons();
            AppLogger.LogDebug("Selections cleared", nameof(HomePage));
        }

        public void RefreshActionButtons()
        {
            UpdateActionButtons();
        }

        public void SetSelectionMode(string mode)
        {
            Calendar.SetSelectionMode(mode);
        }

        public void ApplyAttendanceOverlays(Dictionary<DateTime, Tuple<string?, string?>> overlays)
        {
            Calendar.AttendanceOverlays = overlays ?? new Dictionary<DateTime, Tuple<string?, string?>>();
        }

        private void UpdateActionButtons()
        {
            var enabled = Calendar.DateModes.Count > 0;
            SubmitButton.IsEnabled = enabled;
            CancelButton.IsEnabled = enabled;
        }

        private void Calendar_Loaded(object sender, RoutedEventArgs e)
        {

        }

        private void SetCeragonView(bool enabled)
        {
            _ceragonView = enabled;
            Calendar.SetCeragonView(enabled);
            ViewToggle.ToolTip = enabled ? "Switch to normal view" : "Switch to Ceragon view";
        }
    }
}
