using CeraRegularize.Logging;
using CeraRegularize.Stores;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Media = System.Windows.Media;

namespace CeraRegularize.Pages
{
    public partial class LoginPage : System.Windows.Controls.UserControl
    {
        private const string HiddenIconUri = "pack://application:,,,/Artifacts/monkey_hidden.png";
        private const string VisibleIconUri = "pack://application:,,,/Artifacts/monkey_visible.png";
        public event EventHandler<LoginRequestedEventArgs>? LoginRequested;

        private ConfigState _state;
        private bool _syncingPassword;
        private bool _isApplyingState;
        private string _loginState;
        private string _loginMessage;
        private bool _loginVerified;

        public LoginPage()
        {
            InitializeComponent();
            _state = ConfigStore.Load();
            _loginState = "login_not_verified";
            _loginMessage = "Session Inactive";
            _loginVerified = false;
            WireEvents();
            ApplyState();
            ToggleButtons();
        }

        public void ReloadState()
        {
            _state = ConfigStore.Load();
            _loginState = "login_not_verified";
            _loginMessage = "Session Inactive";
            _loginVerified = false;
            ApplyState();
            ToggleButtons();
        }

        public void SetLoginStatus(bool ok, string? message = null)
        {
            _loginVerified = ok;
            _loginState = ok ? "login_successful" : "login_unsuccessful";
            _loginMessage = message ?? (ok ? "Login Successful" : "Login Unsuccessful");
            SetStatus(_loginMessage, ResolveBrush(null, ok ? "#22C55E" : "#EF4444"));
            ToggleButtons();
            AppLogger.LogInfo(ok ? "Login status updated: verified" : "Login status updated: not verified", nameof(LoginPage));
        }

        private void WireEvents()
        {
            PasswordToggle.Checked += (_, _) => TogglePasswordVisibility(true);
            PasswordToggle.Unchecked += (_, _) => TogglePasswordVisibility(false);
            PasswordBox.PasswordChanged += (_, _) => OnPasswordChanged();
            PasswordTextBox.TextChanged += (_, _) => OnPasswordTextChanged();
            UsernameBox.TextChanged += (_, _) => OnFieldChanged();

            RememberCheckBox.Checked += (_, _) => OnRememberToggled(true);
            RememberCheckBox.Unchecked += (_, _) => OnRememberToggled(false);
            AutoLoginCheckBox.Checked += (_, _) => OnAutoLoginToggled(true);
            AutoLoginCheckBox.Unchecked += (_, _) => OnAutoLoginToggled(false);

            LoginButton.Click += (_, _) => OnLoginClicked();
        }

        private void ApplyState()
        {
            _isApplyingState = true;
            try
            {
                if (_state.RememberMe)
                {
                    UsernameBox.Text = _state.Username;
                    PasswordBox.Password = _state.Password;
                    PasswordTextBox.Text = _state.Password;
                }
                else
                {
                    UsernameBox.Text = string.Empty;
                    PasswordBox.Password = string.Empty;
                    PasswordTextBox.Text = string.Empty;
                }

                RememberCheckBox.IsChecked = _state.RememberMe;
                AutoLoginCheckBox.IsChecked = _state.AutoLogin;

                SetStatusFromState();
            }
            finally
            {
                _isApplyingState = false;
            }
        }

        private void SetStatusFromState()
        {
            if (_loginState == "login_successful")
            {
                SetStatus(string.IsNullOrWhiteSpace(_loginMessage) ? "Login Successful" : _loginMessage,
                    ResolveBrush(null, "#22C55E"));
                return;
            }
            else if (_loginState == "login_unsuccessful")
            {
                SetStatus(string.IsNullOrWhiteSpace(_loginMessage) ? "Login Unsuccessful" : _loginMessage,
                    ResolveBrush(null, "#EF4444"));
                return;
            }

            var message = string.IsNullOrWhiteSpace(_loginMessage) ? "Session Inactive" : _loginMessage;
            LoginStatusText.Text = message;
            LoginStatusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            _loginMessage = message;
        }

        private void SetStatus(string text, Media.Brush brush)
        {
            LoginStatusText.Text = text;
            LoginStatusText.Foreground = brush;
            _loginMessage = text;
        }

        private void TogglePasswordVisibility(bool show)
        {
            if (_syncingPassword)
            {
                return;
            }

            _syncingPassword = true;
            if (show)
            {
                PasswordTextBox.Text = PasswordBox.Password;
                PasswordTextBox.Visibility = Visibility.Visible;
                PasswordBox.Visibility = Visibility.Collapsed;
                UpdatePasswordToggleIcon(true);
            }
            else
            {
                PasswordBox.Password = PasswordTextBox.Text;
                PasswordBox.Visibility = Visibility.Visible;
                PasswordTextBox.Visibility = Visibility.Collapsed;
                UpdatePasswordToggleIcon(false);
            }
            _syncingPassword = false;
        }

        private void OnPasswordChanged()
        {
            if (_syncingPassword || PasswordBox.Visibility != Visibility.Visible)
            {
                return;
            }

            _syncingPassword = true;
            PasswordTextBox.Text = PasswordBox.Password;
            _syncingPassword = false;
            OnFieldChanged();
        }

        private void OnPasswordTextChanged()
        {
            if (_syncingPassword || PasswordTextBox.Visibility != Visibility.Visible)
            {
                return;
            }

            _syncingPassword = true;
            PasswordBox.Password = PasswordTextBox.Text;
            _syncingPassword = false;
            OnFieldChanged();
        }

        private void OnRememberToggled(bool checkedValue)
        {
            if (_isApplyingState)
            {
                return;
            }

            var snapshot = ConfigStore.Load();
            snapshot.RememberMe = checkedValue;
            if (checkedValue)
            {
                var user = UsernameBox.Text.Trim();
                var pwd = GetPassword();
                if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pwd))
                {
                    snapshot.Username = user;
                    snapshot.Password = pwd;
                }
            }
            else
            {
                snapshot.Username = string.Empty;
                snapshot.Password = string.Empty;
            }

            ConfigStore.Save(snapshot);
            _state.RememberMe = checkedValue;
            _state.Username = snapshot.Username;
            _state.Password = snapshot.Password;
            ToggleButtons();
            AppLogger.LogDebug($"Remember me toggled: {checkedValue}", nameof(LoginPage));
        }

        private void OnAutoLoginToggled(bool checkedValue)
        {
            if (_isApplyingState)
            {
                return;
            }

            var snapshot = ConfigStore.Load();
            snapshot.AutoLogin = checkedValue;
            ConfigStore.Save(snapshot);
            _state.AutoLogin = checkedValue;
            ToggleButtons();
            AppLogger.LogDebug($"Auto-login toggled: {checkedValue}", nameof(LoginPage));
        }

        private void OnLoginClicked()
        {
            var username = UsernameBox.Text.Trim();
            var password = GetPassword();
            var remember = RememberCheckBox.IsChecked == true;
            LoginRequested?.Invoke(this, new LoginRequestedEventArgs(username, password, remember));
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
            var user = UsernameBox.Text.Trim();
            var pwd = GetPassword();
            var credsOk = !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pwd);
            LoginButton.IsEnabled = credsOk;
        }

        private string GetPassword()
        {
            return PasswordBox.Visibility == Visibility.Visible
                ? PasswordBox.Password
                : PasswordTextBox.Text;
        }

        private static Media.Brush ResolveBrush(string? resourceKey, string fallbackHex)
        {
            if (!string.IsNullOrWhiteSpace(resourceKey))
            {
                try
                {
                    if (System.Windows.Application.Current?.Resources[resourceKey] is Media.Brush resourceBrush)
                    {
                        return resourceBrush;
                    }
                }
                catch
                {
                }
            }

            return (Media.Brush)new Media.BrushConverter().ConvertFromString(fallbackHex)!;
        }

        private void UpdatePasswordToggleIcon(bool visible)
        {
            var uri = visible ? VisibleIconUri : HiddenIconUri;
            PasswordToggleIcon.Source = new BitmapImage(new Uri(uri, UriKind.Absolute));
        }
    }
}
