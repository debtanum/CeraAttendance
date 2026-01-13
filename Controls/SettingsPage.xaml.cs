using CeraRegularize.Logging;
using CeraRegularize.Stores;
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CeraRegularize.Pages
{
    public partial class SettingsPage : System.Windows.Controls.UserControl
    {
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
        public event EventHandler<SelectionModeChangedEventArgs>? SelectionModeChanged;
        public event EventHandler<AutoRefreshChangedEventArgs>? AutoRefreshChanged;

        private SettingsState _state;
        private SettingsState _savedState;
        private bool _isApplyingState;

        public SettingsPage()
        {
            InitializeComponent();
            _state = SettingsStore.Load();
            _savedState = _state.Copy();
            WireAccordionLogging();
            WireEvents();
            ApplyState();
            ToggleSaveButton();
        }

        private void WireAccordionLogging()
        {
            AttachAccordionLogging(AppearanceExpander, "Appearance");
            AttachAccordionLogging(CalendarExpander, "Calendar Selection Style");
            AttachAccordionLogging(SyncExpander, "Attendance Sync");
            AttachAccordionLogging(DeveloperExpander, "Developer Tools");
        }

        private static void AttachAccordionLogging(Expander expander, string label)
        {
            expander.Expanded += (_, _) =>
                AppLogger.LogDebug($"Settings accordion expanded: {label}", nameof(SettingsPage));
            expander.Collapsed += (_, _) =>
                AppLogger.LogDebug($"Settings accordion collapsed: {label}", nameof(SettingsPage));
        }

        private void WireEvents()
        {
            ThemeSystemRadio.Checked += (_, _) => ToggleSaveButton();
            ThemeLightRadio.Checked += (_, _) => ToggleSaveButton();
            ThemeDarkRadio.Checked += (_, _) => ToggleSaveButton();

            LogEnableCheckBox.Checked += (_, _) => OnLogEnableToggled();
            LogEnableCheckBox.Unchecked += (_, _) => OnLogEnableToggled();

            LogDebugCheckBox.Checked += (_, _) => ToggleSaveButton();
            LogDebugCheckBox.Unchecked += (_, _) => ToggleSaveButton();
            LogInfoCheckBox.Checked += (_, _) => ToggleSaveButton();
            LogInfoCheckBox.Unchecked += (_, _) => ToggleSaveButton();
            LogWarningCheckBox.Checked += (_, _) => ToggleSaveButton();
            LogWarningCheckBox.Unchecked += (_, _) => ToggleSaveButton();
            LogErrorCheckBox.Checked += (_, _) => ToggleSaveButton();
            LogErrorCheckBox.Unchecked += (_, _) => ToggleSaveButton();
            LogCriticalCheckBox.Checked += (_, _) => ToggleSaveButton();
            LogCriticalCheckBox.Unchecked += (_, _) => ToggleSaveButton();

            SelectionPopupRadio.Checked += (_, _) => ToggleSaveButton();
            SelectionLoopRadio.Checked += (_, _) => ToggleSaveButton();

            AutoRefreshCheckBox.Checked += (_, _) => OnAutoRefreshToggled();
            AutoRefreshCheckBox.Unchecked += (_, _) => OnAutoRefreshToggled();

            AutoRefreshTextBox.TextChanged += (_, _) => OnIntervalChanged();
            AutoRefreshTextBox.PreviewTextInput += AutoRefreshTextBox_PreviewTextInput;
            System.Windows.DataObject.AddPastingHandler(AutoRefreshTextBox, AutoRefreshTextBox_Paste);
            AutoRefreshTextBox.LostFocus += (_, _) => ClampIntervalText();

            HeadlessCheckBox.Checked += (_, _) => ToggleSaveButton();
            HeadlessCheckBox.Unchecked += (_, _) => ToggleSaveButton();

            SaveButton.Click += (_, _) => OnSave();
            ResetButton.Click += (_, _) => OnReset();
        }

        private void ApplyState()
        {
            _isApplyingState = true;
            try
            {
                if (string.Equals(_state.ThemeMode, "light", StringComparison.OrdinalIgnoreCase))
                {
                    ThemeLightRadio.IsChecked = true;
                }
                else if (string.Equals(_state.ThemeMode, "dark", StringComparison.OrdinalIgnoreCase))
                {
                    ThemeDarkRadio.IsChecked = true;
                }
                else
                {
                    ThemeSystemRadio.IsChecked = true;
                }

                LogEnableCheckBox.IsChecked = _state.LogFileEnabled;
                LogDebugCheckBox.IsChecked = GetLogLevel(_state, "debug");
                LogInfoCheckBox.IsChecked = GetLogLevel(_state, "info");
                LogWarningCheckBox.IsChecked = GetLogLevel(_state, "warning");
                LogErrorCheckBox.IsChecked = GetLogLevel(_state, "error");
                LogCriticalCheckBox.IsChecked = GetLogLevel(_state, "critical");
                SetLogLevelEnabledState();

                if (string.Equals(_state.CalendarSelectionMode, "loop", StringComparison.OrdinalIgnoreCase))
                {
                    SelectionLoopRadio.IsChecked = true;
                }
                else
                {
                    SelectionPopupRadio.IsChecked = true;
                }

                AutoRefreshCheckBox.IsChecked = _state.AutoRefreshEnabled;
                AutoRefreshTextBox.Text = _state.AutoRefreshIntervalMin.ToString(CultureInfo.InvariantCulture);
                SetAutoRefreshEnabledState();
                HeadlessCheckBox.IsChecked = _state.HeadlessEnabled;
            }
            finally
            {
                _isApplyingState = false;
            }
        }

        private SettingsState CollectState()
        {
            var mode = "system";
            if (ThemeLightRadio.IsChecked == true)
            {
                mode = "light";
            }
            else if (ThemeDarkRadio.IsChecked == true)
            {
                mode = "dark";
            }

            var selectionMode = SelectionLoopRadio.IsChecked == true ? "loop" : "popup";

            return new SettingsState
            {
                ThemeMode = mode,
                LogFileEnabled = LogEnableCheckBox.IsChecked == true,
                LogLevels = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["debug"] = LogDebugCheckBox.IsChecked == true,
                    ["info"] = LogInfoCheckBox.IsChecked == true,
                    ["warning"] = LogWarningCheckBox.IsChecked == true,
                    ["error"] = LogErrorCheckBox.IsChecked == true,
                    ["critical"] = LogCriticalCheckBox.IsChecked == true,
                },
                CalendarSelectionMode = selectionMode,
                AutoRefreshEnabled = AutoRefreshCheckBox.IsChecked == true,
                AutoRefreshIntervalMin = GetIntervalValue(),
                HeadlessEnabled = HeadlessCheckBox.IsChecked == true,
            };
        }

        private void OnSave()
        {
            var newState = CollectState();
            _state = SettingsStore.Save(newState);
            _savedState = _state.Copy();

            AppLogger.UpdateSettings(_state);
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(_state.ThemeMode));
            SelectionModeChanged?.Invoke(this, new SelectionModeChangedEventArgs(_state.CalendarSelectionMode));
            AutoRefreshChanged?.Invoke(this, new AutoRefreshChangedEventArgs(_state.AutoRefreshEnabled, _state.AutoRefreshIntervalMin));

            ToggleSaveButton();
            AppLogger.LogInfo("Settings saved", nameof(SettingsPage));
        }

        private void OnReset()
        {
            _state = SettingsStore.DefaultSettings();
            ApplyState();
            ToggleSaveButton();
        }

        private void ToggleSaveButton()
        {
            if (_isApplyingState)
            {
                return;
            }

            var current = CollectState();
            SaveButton.IsEnabled = !current.ContentEquals(_savedState);
        }

        private void SetLogLevelEnabledState()
        {
            LogLevelsGroupBox.IsEnabled = LogEnableCheckBox.IsChecked == true;
        }

        private void OnLogEnableToggled()
        {
            if (_isApplyingState)
            {
                return;
            }

            SetLogLevelEnabledState();
            ToggleSaveButton();
        }

        private void SetAutoRefreshEnabledState()
        {
            var enabled = AutoRefreshCheckBox.IsChecked == true;
            AutoRefreshTextBox.IsEnabled = enabled;
            AutoRefreshLabel.IsEnabled = enabled;
        }

        private void OnAutoRefreshToggled()
        {
            if (_isApplyingState)
            {
                return;
            }

            SetAutoRefreshEnabledState();
            ToggleSaveButton();
        }

        private void OnIntervalChanged()
        {
            if (_isApplyingState)
            {
                return;
            }

            ToggleSaveButton();
        }

        private void AutoRefreshTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private void AutoRefreshTextBox_Paste(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.SourceDataObject.GetDataPresent(System.Windows.DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var text = e.SourceDataObject.GetData(System.Windows.DataFormats.Text) as string;
            if (string.IsNullOrWhiteSpace(text) || !text.All(char.IsDigit))
            {
                e.CancelCommand();
            }
        }

        private void ClampIntervalText()
        {
            AutoRefreshTextBox.Text = GetIntervalValue().ToString(CultureInfo.InvariantCulture);
        }

        private int GetIntervalValue()
        {
            if (!int.TryParse(AutoRefreshTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                value = 10;
            }

            if (value < 1)
            {
                value = 1;
            }
            else if (value > 60)
            {
                value = 60;
            }

            return value;
        }

        private static bool GetLogLevel(SettingsState state, string key)
        {
            return state.LogLevels != null
                && state.LogLevels.TryGetValue(key, out var value)
                && value;
        }
    }

    public sealed class ThemeChangedEventArgs : EventArgs
    {
        public ThemeChangedEventArgs(string themeMode)
        {
            ThemeMode = themeMode;
        }

        public string ThemeMode { get; }
    }

    public sealed class SelectionModeChangedEventArgs : EventArgs
    {
        public SelectionModeChangedEventArgs(string selectionMode)
        {
            SelectionMode = selectionMode;
        }

        public string SelectionMode { get; }
    }

    public sealed class AutoRefreshChangedEventArgs : EventArgs
    {
        public AutoRefreshChangedEventArgs(bool enabled, int intervalMinutes)
        {
            Enabled = enabled;
            IntervalMinutes = intervalMinutes;
        }

        public bool Enabled { get; }
        public int IntervalMinutes { get; }
    }
}
