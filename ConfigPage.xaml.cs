using CeraRegularize.Logging;
using CeraRegularize.Stores;
using System;

namespace CeraRegularize.Pages
{
    public partial class ConfigPage : System.Windows.Controls.UserControl
    {
        private ConfigState _state;
        private ConfigState _savedState;
        private bool _isApplyingState;

        public ConfigPage()
        {
            InitializeComponent();
            _state = ConfigStore.Load();
            _savedState = CollectConfigSnapshot(_state);
            WireEvents();
            ApplyState();
            ToggleButtons();
        }

        public void ReloadState()
        {
            _state = ConfigStore.Load();
            _savedState = CollectConfigSnapshot(_state);
            ApplyState();
            ToggleButtons();
        }

        private void WireEvents()
        {
            ShiftComboBox.SelectionChanged += (_, _) => OnShiftChanged();
            InTimeBox.TextChanged += (_, _) => OnFieldChanged();
            OutTimeBox.TextChanged += (_, _) => OnFieldChanged();
            WfoRemarksBox.TextChanged += (_, _) => OnFieldChanged();
            WfhRemarksBox.TextChanged += (_, _) => OnFieldChanged();

            SaveButton.Click += (_, _) => OnSave();
            ResetButton.Click += (_, _) => OnReset();
        }

        private void ApplyState()
        {
            _isApplyingState = true;
            try
            {
                ShiftComboBox.SelectedValue = _state.Shift;
                InTimeBox.Text = _state.InTime;
                OutTimeBox.Text = _state.OutTime;
                WfoRemarksBox.Text = _state.WfoRemarks;
                WfhRemarksBox.Text = _state.WfhRemarks;
            }
            finally
            {
                _isApplyingState = false;
            }
        }

        private void OnShiftChanged()
        {
            if (_isApplyingState)
            {
                return;
            }

            var shift = ShiftComboBox.SelectedValue as string;
            if (!string.IsNullOrWhiteSpace(shift) && ConfigStore.ShiftDefaults.TryGetValue(shift, out var defaults))
            {
                InTimeBox.Text = defaults.InTime;
                OutTimeBox.Text = defaults.OutTime;
            }

            ToggleSaveButton();
        }

        private void OnSave()
        {
            var newState = CollectState();
            _state = ConfigStore.Save(newState);
            _savedState = CollectConfigSnapshot(_state);
            ToggleButtons();
            AppLogger.LogInfo("Config saved", nameof(ConfigPage));
        }

        private void OnReset()
        {
            var defaults = ConfigStore.DefaultConfigState();
            _state.Shift = defaults.Shift;
            _state.InTime = defaults.InTime;
            _state.OutTime = defaults.OutTime;
            _state.WfoRemarks = defaults.WfoRemarks;
            _state.WfhRemarks = defaults.WfhRemarks;
            ApplyState();
            ToggleButtons();
        }

        private void OnFieldChanged()
        {
            if (_isApplyingState)
            {
                return;
            }

            ToggleButtons();
        }

        private void ToggleButtons()
        {
            ToggleSaveButton();
        }

        private bool ToggleSaveButton()
        {
            var current = CollectConfigSnapshot();
            var enabled = !ConfigEquals(current, _savedState);
            SaveButton.IsEnabled = enabled;
            return enabled;
        }

        private ConfigState CollectState()
        {
            var snapshot = ConfigStore.Load();
            snapshot.Shift = ShiftComboBox.SelectedValue as string ?? "S02";
            snapshot.InTime = InTimeBox.Text.Trim();
            snapshot.OutTime = OutTimeBox.Text.Trim();
            snapshot.WfoRemarks = string.IsNullOrWhiteSpace(WfoRemarksBox.Text) ? "Working from Office" : WfoRemarksBox.Text.Trim();
            snapshot.WfhRemarks = string.IsNullOrWhiteSpace(WfhRemarksBox.Text) ? "Working from Home" : WfhRemarksBox.Text.Trim();
            return snapshot;
        }

        private ConfigState CollectConfigSnapshot()
        {
            return new ConfigState
            {
                Shift = ShiftComboBox.SelectedValue as string ?? "S02",
                InTime = InTimeBox.Text.Trim(),
                OutTime = OutTimeBox.Text.Trim(),
                WfoRemarks = string.IsNullOrWhiteSpace(WfoRemarksBox.Text) ? "Working from Office" : WfoRemarksBox.Text.Trim(),
                WfhRemarks = string.IsNullOrWhiteSpace(WfhRemarksBox.Text) ? "Working from Home" : WfhRemarksBox.Text.Trim(),
            };
        }

        private static ConfigState CollectConfigSnapshot(ConfigState state)
        {
            return new ConfigState
            {
                Shift = state.Shift,
                InTime = state.InTime,
                OutTime = state.OutTime,
                WfoRemarks = state.WfoRemarks,
                WfhRemarks = state.WfhRemarks,
            };
        }

        private static bool ConfigEquals(ConfigState left, ConfigState right)
        {
            return string.Equals(left.Shift, right.Shift, StringComparison.Ordinal)
                && string.Equals(left.InTime, right.InTime, StringComparison.Ordinal)
                && string.Equals(left.OutTime, right.OutTime, StringComparison.Ordinal)
                && string.Equals(left.WfoRemarks, right.WfoRemarks, StringComparison.Ordinal)
                && string.Equals(left.WfhRemarks, right.WfhRemarks, StringComparison.Ordinal);
        }

        private void WfhRemarksBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {

        }
    }
}
