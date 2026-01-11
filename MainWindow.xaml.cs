using CeraRegularize.Logging;
using CeraRegularize.Services;
using CeraRegularize.Stores;
using CeraRegularize.Themes;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CeraRegularize
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    ///
    /// The MainWindow coordinates navigation between the different pages in the
    /// application. It responds to events raised by the TopBar to switch
    /// content and closes the application when requested. Dragging the top
    /// panel moves the window since the native window chrome is disabled.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Pages.HomePage _homePage;
        private readonly Pages.ApplyLeavePage _applyLeavePage;
        private readonly Pages.ConfigPage _configPage;
        private readonly Pages.LoginPage _loginPage;
        private readonly Pages.ProfilePage _profilePage;
        private readonly Pages.SettingsPage _settingsPage;
        private readonly AttendanceAutomator _automator;
        private Forms.NotifyIcon? _trayIcon;
        private bool _exitRequested;
        private DispatcherTimer? _sessionTimer;
        private bool _sessionCheckRunning;
        private bool _initialsFetchRunning;
        private DispatcherTimer? _historyTimer;
        private bool _historyRefreshRunning;
        private bool _loginVerified;
        private bool _profileFetchRunning;
        private ProfileSummary? _profileSummary;
        private bool _playwrightReady;

        public MainWindow()
        {
            InitializeComponent();
            AppLogger.LogInfo("MainWindow initializing", nameof(MainWindow));
            // Instantiate pages once and reuse them when switching
            _loginPage = new Pages.LoginPage();
            _homePage = new Pages.HomePage();
            _applyLeavePage = new Pages.ApplyLeavePage();
            _configPage = new Pages.ConfigPage();
            _profilePage = new Pages.ProfilePage();
            _settingsPage = new Pages.SettingsPage();
            _automator = new AttendanceAutomator();
            var settings = SettingsStore.Load();
            ThemeManager.ApplyTheme(settings.ThemeMode);
            _homePage.SetSelectionMode(settings.CalendarSelectionMode);
            _homePage.ApplyAttendanceOverlays(AttendanceHistoryStore.LoadOverlays());
            ConfigureHistoryRefresh(settings.AutoRefreshEnabled, settings.AutoRefreshIntervalMin);
            // Default page is the login page until credentials are verified
            ContentArea.Content = _loginPage;
            // Wire up top bar events
            TopBarControl.HomeSelected += (_, _) => NavigateToHome();
            TopBarControl.ApplyLeaveSelected += (_, _) => NavigateToApplyLeave();
            TopBarControl.ConfigSelected += (_, _) => NavigateToConfig();
            TopBarControl.LoginSelected += (_, _) => ShowLoginPage(false);
            TopBarControl.ProfileSelected += (_, _) => NavigateToProfile();
            TopBarControl.SettingsSelected += (_, _) => NavigateToSettings();
            TopBarControl.CloseClicked += (s, e) => Close();
            // Allow dragging the window by the top bar
            TopBarControl.MouseLeftButtonDown += TopBarControl_MouseLeftButtonDown;

            _loginPage.LoginRequested += LoginPage_LoginRequested;
            _profilePage.LogoutRequested += ProfilePage_LogoutRequested;
            _settingsPage.ThemeChanged += (_, args) => ThemeManager.ApplyTheme(args.ThemeMode);
            _settingsPage.SelectionModeChanged += (_, args) => _homePage.SetSelectionMode(args.SelectionMode);
            _settingsPage.AutoRefreshChanged += SettingsPage_AutoRefreshChanged;
            _homePage.SubmitRequested += HomePage_SubmitRequested;
            _homePage.CancelRequested += HomePage_CancelRequested;
            TopBarControl.SyncClicked += TopBarControl_SyncClicked;
            Loaded += MainWindow_Loaded;
            InitializeTrayIcon();
            _loginVerified = false;
            _playwrightReady = false;
            TopBarControl.SetMenuState(false);
            TopBarControl.Status = "offline";
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            PositionBottomRight();
            AppLogger.LogInfo("MainWindow loaded", nameof(MainWindow));
            await EnsurePlaywrightReadyAsync().ConfigureAwait(true);
            if (!_playwrightReady)
            {
                return;
            }

            StartSessionTimer();
            await AttemptAutoLoginAsync().ConfigureAwait(true);
        }

        private void TopBarControl_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Ignore errors thrown if the window is not ready for dragging
                }
            }
        }

        private void ShowOverlay(string message)
        {
            LoadingOverlay.Message = message;
            LoadingOverlay.IsActive = true;
        }

        private void HideOverlay()
        {
            LoadingOverlay.IsActive = false;
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "CeraRegularize",
                Visible = true,
            };

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => ShowFromTray());
            menu.Items.Add("Exit", null, (_, _) => ExitFromTray());
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.MouseClick += TrayIcon_MouseClick;
        }

        private void TrayIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
        {
            if (e.Button != Forms.MouseButtons.Left)
            {
                return;
            }

            ToggleTrayVisibility();
        }

        private void ToggleTrayVisibility()
        {
            if (!IsVisible || WindowState == WindowState.Minimized || !ShowInTaskbar)
            {
                ShowFromTray();
            }
            else
            {
                HideToTray();
            }
        }

        private void ShowFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
            PositionBottomRight();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        private void ExitFromTray()
        {
            _exitRequested = true;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
            }
            Close();
        }

        private void PositionBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - 12);
            Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - 12);
        }

        private async Task EnsurePlaywrightReadyAsync()
        {
            if (PlaywrightInstaller.IsInstalled())
            {
                _playwrightReady = true;
                return;
            }

            ShowOverlay("Setting Up...");
            try
            {
                AppLogger.LogInfo("Playwright setup started", nameof(MainWindow));
                await PlaywrightInstaller.EnsureInstalledAsync().ConfigureAwait(true);
                AppLogger.LogInfo("Playwright setup completed", nameof(MainWindow));
                _playwrightReady = true;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Playwright setup failed", ex, nameof(MainWindow));
                System.Windows.MessageBox.Show(
                    $"Unable to setup browser automation: {ex.Message}",
                    "CeraRegularize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                _playwrightReady = false;
            }
            finally
            {
                HideOverlay();
            }
        }

        private void NavigateToHome()
        {
            if (!EnsureLoggedIn())
            {
                return;
            }

            ContentArea.Content = _homePage;
        }

        private void NavigateToApplyLeave()
        {
            if (!EnsureLoggedIn())
            {
                return;
            }

            ContentArea.Content = _applyLeavePage;
        }

        private void NavigateToConfig()
        {
            if (!EnsureLoggedIn())
            {
                return;
            }

            _configPage.ReloadState();
            ContentArea.Content = _configPage;
        }

        private async void NavigateToProfile()
        {
            if (!EnsureLoggedIn())
            {
                return;
            }

            if (_profileSummary != null)
            {
                _profilePage.SetProfileSummary(_profileSummary);
            }
            else
            {
                _profilePage.ClearProfile();
                _profilePage.SetStatus("Loading profile...", false);
            }
            ContentArea.Content = _profilePage;

            if (_profileSummary == null)
            {
                await StartProfilePrefetchAsync().ConfigureAwait(true);
            }
        }

        private void NavigateToSettings()
        {
            ContentArea.Content = _settingsPage;
        }

        private bool EnsureLoggedIn()
        {
            if (_loginVerified)
            {
                return true;
            }

            ShowLoginPage(false);
            return false;
        }

        private void ShowLoginPage(bool reloadState)
        {
            if (reloadState)
            {
                _loginPage.ReloadState();
            }
            TopBarControl.SetMenuState(false);
            TopBarControl.Status = "offline";
            TopBarControl.Initials = "..";
            ContentArea.Content = _loginPage;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exitRequested)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                return;
            }

            base.OnClosing(e);
        }

        private async void LoginPage_LoginRequested(object? sender, Pages.LoginRequestedEventArgs e)
        {
            if (!_playwrightReady)
            {
                System.Windows.MessageBox.Show(
                    "Please wait while we finish setting up the browser automation.",
                    "CeraRegularize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            AppLogger.LogInfo("Login requested", nameof(MainWindow));
            ShowOverlay("Logging...");
            try
            {
                await HandleLoginAsync(e.Username, e.Password, e.Remember, persistCredentials: true, navigateOnSuccess: true);
            }
            finally
            {
                HideOverlay();
            }
        }

        private async void ProfilePage_LogoutRequested(object? sender, EventArgs e)
        {
            AppLogger.LogInfo("Logout requested", nameof(MainWindow));
            await HandleLogoutAsync().ConfigureAwait(true);
        }

        private async Task HandleLogoutAsync()
        {
            Environment.SetEnvironmentVariable("ATT_USERNAME", string.Empty);
            Environment.SetEnvironmentVariable("ATT_PASSWORD", string.Empty);
            await _automator.DisposeAsync();
            _automator.LoginVerified = false;
            _automator.Username = null;
            _automator.Password = null;
            _profileSummary = null;
            _profilePage.ClearProfile();

            const string message = "Logged out";
            PersistLogoutState(message);
            UpdateSessionIndicators(false, message, persistConfig: false, navigateOnSuccess: false, navigateOnFailure: false);
            ShowLoginPage(true);
        }

        private void HomePage_CancelRequested(object? sender, EventArgs e)
        {
            AppLogger.LogInfo("Selections cleared", nameof(MainWindow));
            _homePage.RefreshActionButtons();
        }

        private async void TopBarControl_SyncClicked(object? sender, EventArgs e)
        {
            await RefreshAttendanceHistoryAsync("sync", true).ConfigureAwait(true);
        }

        private async void HomePage_SubmitRequested(object? sender, EventArgs e)
        {
            var selections = _homePage.GetSelections();
            if (selections.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "Select at least one date and mode before submitting.",
                    "CeraRegularize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var cfg = ConfigStore.Load();
            if (!TryResolveCredentials(cfg, out var username, out var password, out var errorMessage))
            {
                UpdateSessionIndicators(false, errorMessage, persistConfig: false, navigateOnSuccess: false, navigateOnFailure: true);
                System.Windows.MessageBox.Show(errorMessage, "CeraRegularize", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ApplyConfigToAutomator(cfg);
            ApplyCredentials(username, password);

            if (!_loginVerified)
            {
                System.Windows.MessageBox.Show(
                    "Please login before submitting attendance.",
                    "CeraRegularize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _homePage.SetActionsEnabled(false);
            try
            {
                AppLogger.LogInfo($"Submitting attendance: {selections.Count} date(s)", nameof(MainWindow));
                await _automator.RegularizeDatesAsync(selections, statusCallback: null).ConfigureAwait(true);
                _homePage.ClearSelections();
                await RefreshAttendanceHistoryAsync("submit", true).ConfigureAwait(true);
                System.Windows.MessageBox.Show(
                    "Attendance submitted for selected dates.",
                    "CeraRegularize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Submit failed", ex, nameof(MainWindow));
                System.Windows.MessageBox.Show(
                    $"Submit failed: {ex.Message}",
                    "CeraRegularize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _homePage.RefreshActionButtons();
            }
        }

        private void SettingsPage_AutoRefreshChanged(object? sender, Pages.AutoRefreshChangedEventArgs e)
        {
            ConfigureHistoryRefresh(e.Enabled, e.IntervalMinutes);
        }

        private void ConfigureHistoryRefresh(bool enabled, int intervalMinutes)
        {
            if (_historyTimer == null)
            {
                _historyTimer = new DispatcherTimer();
                _historyTimer.Tick += HistoryTimer_Tick;
            }

            if (!enabled)
            {
                _historyTimer.Stop();
                return;
            }

            if (intervalMinutes < 1)
            {
                intervalMinutes = 1;
            }

            _historyTimer.Interval = TimeSpan.FromMinutes(intervalMinutes);
            if (!_historyTimer.IsEnabled)
            {
                _historyTimer.Start();
            }
        }

        private async void HistoryTimer_Tick(object? sender, EventArgs e)
        {
            await RefreshAttendanceHistoryAsync("auto", false).ConfigureAwait(true);
        }

        private async Task RefreshAttendanceHistoryAsync(string reason, bool force)
        {
            if (_historyRefreshRunning)
            {
                return;
            }

            if (!force && !_automator.LoginVerified)
            {
                return;
            }

            _historyRefreshRunning = true;
            TopBarControl.SetSyncing(true);
            try
            {
                AppLogger.LogInfo($"Refreshing attendance history ({reason})", nameof(MainWindow));
                var snapshot = await _automator.CollectHistorySnapshotAsync().ConfigureAwait(true);
                if (snapshot == null)
                {
                    AppLogger.LogWarning("Attendance history refresh skipped", nameof(MainWindow));
                    return;
                }

                AttendanceHistoryStore.SaveSnapshot(snapshot);
                _homePage.ApplyAttendanceOverlays(AttendanceHistoryStore.ToOverlayMap(snapshot));
                AppLogger.LogInfo("Attendance history refreshed", nameof(MainWindow));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Attendance history refresh failed ({reason})", ex, nameof(MainWindow));
            }
            finally
            {
                TopBarControl.SetSyncing(false);
                _historyRefreshRunning = false;
            }
        }

        private async Task AttemptAutoLoginAsync()
        {
            if (!_playwrightReady)
            {
                return;
            }

            if (_sessionCheckRunning)
            {
                return;
            }

            _sessionCheckRunning = true;
            var overlayShown = false;
            try
            {
                var cfg = ConfigStore.Load();
                if (!cfg.AutoLogin)
                {
                    return;
                }

                AppLogger.LogInfo("Attempting auto-login", nameof(MainWindow));
                if (!TryResolveStoredCredentials(cfg, out var username, out var password, out var errorMessage))
                {
                    UpdateSessionIndicators(false, errorMessage, persistConfig: false, navigateOnSuccess: false, navigateOnFailure: true);
                    return;
                }

                ApplyConfigToAutomator(cfg);
                ApplyCredentials(username, password);
                ShowOverlay("Logging...");
                overlayShown = true;
                var (ok, message) = await EnsureSessionAsync(forceLogin: true, allowUnverified: true).ConfigureAwait(true);
                UpdateSessionIndicators(ok, message, persistConfig: true, navigateOnSuccess: true, navigateOnFailure: true);
                if (ok)
                {
                    if (overlayShown)
                    {
                        HideOverlay();
                        overlayShown = false;
                    }
                    await RefreshAttendanceHistoryAsync("auto-login", true).ConfigureAwait(true);
                }
            }
            finally
            {
                if (overlayShown)
                {
                    HideOverlay();
                }
                _sessionCheckRunning = false;
            }
        }

        private void StartSessionTimer()
        {
            if (_sessionTimer == null)
            {
                _sessionTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(15),
                };
                _sessionTimer.Tick += SessionTimer_Tick;
            }

            if (!_sessionTimer.IsEnabled)
            {
                _sessionTimer.Start();
            }
        }

        private async void SessionTimer_Tick(object? sender, EventArgs e)
        {
            if (!_playwrightReady)
            {
                return;
            }

            if (_sessionCheckRunning)
            {
                return;
            }

            _sessionCheckRunning = true;
            try
            {
                await CheckSessionAsync().ConfigureAwait(true);
            }
            finally
            {
                _sessionCheckRunning = false;
            }
        }

        private async Task CheckSessionAsync()
        {
            var cfg = ConfigStore.Load();
            if (!_loginVerified && !cfg.AutoLogin)
            {
                return;
            }

            AppLogger.LogDebug("Session heartbeat started", nameof(MainWindow));
            if (!_loginVerified
                ? !TryResolveStoredCredentials(cfg, out var username, out var password, out var errorMessage)
                : !TryResolveCredentials(cfg, out username, out password, out errorMessage))
            {
                UpdateSessionIndicators(false, errorMessage, persistConfig: false, navigateOnSuccess: false, navigateOnFailure: true);
                return;
            }

            ApplyConfigToAutomator(cfg);
            ApplyCredentials(username, password);
            var wantsAutoLogin = cfg.AutoLogin && !_loginVerified;
            var overlayShown = false;
            if (wantsAutoLogin)
            {
                ShowOverlay("Logging...");
                overlayShown = true;
            }
            try
            {
                var (ok, message) = await EnsureSessionAsync(forceLogin: wantsAutoLogin, allowUnverified: wantsAutoLogin)
                    .ConfigureAwait(true);
                UpdateSessionIndicators(ok, message, persistConfig: true, navigateOnSuccess: false, navigateOnFailure: true);
            }
            finally
            {
                if (overlayShown)
                {
                    HideOverlay();
                }
            }
        }

        private async Task<bool> HandleLoginAsync(
            string username,
            string password,
            bool remember,
            bool persistCredentials,
            bool navigateOnSuccess)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                UpdateSessionIndicators(false, "Credentials missing", persistConfig: persistCredentials, navigateOnSuccess: false, navigateOnFailure: true);
                if (persistCredentials)
                {
                    PersistLoginState(false, "Credentials missing", username, password, remember);
                }
                return false;
            }

            ApplyCredentials(username, password);

            bool ok;
            string message;
            try
            {
                ok = await _automator.TestLoginAsync().ConfigureAwait(true);
                message = _automator.LoginMessage;
            }
            catch (Exception ex)
            {
                ok = false;
                message = $"Login failed: {ex.Message}";
            }

            AppLogger.LogInfo(ok ? "Login succeeded" : "Login failed", nameof(MainWindow));
            UpdateSessionIndicators(ok, message, persistConfig: false, navigateOnSuccess: navigateOnSuccess, navigateOnFailure: true);
            if (persistCredentials)
            {
                PersistLoginState(ok, message, username, password, remember);
            }
            else
            {
                PersistSessionStatus(ok, message);
            }

            if (LoadingOverlay.IsActive)
            {
                HideOverlay();
            }
            if (ok)
            {
                await RefreshAttendanceHistoryAsync("login", true).ConfigureAwait(true);
            }
            return ok;
        }

        private async Task<(bool Ok, string Message)> EnsureSessionAsync(bool forceLogin, bool allowUnverified)
        {
            try
            {
                var ok = await _automator.EnsureSessionAliveAsync(null, null, forceLogin, allowUnverified).ConfigureAwait(true);
                var message = string.IsNullOrWhiteSpace(_automator.LastSessionMessage)
                    ? (ok ? "Session active" : "Unable to refresh session")
                    : _automator.LastSessionMessage;
                AppLogger.LogDebug($"Session check result: {message}", nameof(MainWindow));
                return (ok, message);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Session check failed: {ex.Message}", nameof(MainWindow));
                return (false, $"Session check failed: {ex.Message}");
            }
        }

        private static void PersistLoginState(bool ok, string message, string username, string password, bool remember)
        {
            var cfg = ConfigStore.Load();
            cfg.RememberMe = remember;
            if (remember)
            {
                cfg.Username = username;
                cfg.Password = password;
            }
            else
            {
                cfg.Username = string.Empty;
                cfg.Password = string.Empty;
            }
            ConfigStore.Save(cfg);
        }

        private static void PersistSessionStatus(bool ok, string message)
        {
        }

        private static void PersistLogoutState(string message)
        {
        }

        private bool TryResolveCredentials(
            ConfigState config,
            out string username,
            out string password,
            out string errorMessage)
        {
            username = config.Username?.Trim() ?? string.Empty;
            password = config.Password ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                errorMessage = string.Empty;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_automator.Username) && !string.IsNullOrWhiteSpace(_automator.Password))
            {
                username = _automator.Username!;
                password = _automator.Password!;
                errorMessage = string.Empty;
                return true;
            }

            var envUser = Environment.GetEnvironmentVariable("ATT_USERNAME");
            var envPwd = Environment.GetEnvironmentVariable("ATT_PASSWORD");
            if (!string.IsNullOrWhiteSpace(envUser) && !string.IsNullOrWhiteSpace(envPwd))
            {
                username = envUser.Trim();
                password = envPwd;
                errorMessage = string.Empty;
                return true;
            }

            errorMessage = "Credentials missing";
            return false;
        }

        private static bool TryResolveStoredCredentials(
            ConfigState config,
            out string username,
            out string password,
            out string errorMessage)
        {
            username = config.Username?.Trim() ?? string.Empty;
            password = config.Password ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                errorMessage = string.Empty;
                return true;
            }

            errorMessage = "Credentials missing";
            return false;
        }

        private void ApplyCredentials(string username, string password)
        {
            Environment.SetEnvironmentVariable("ATT_USERNAME", username);
            Environment.SetEnvironmentVariable("ATT_PASSWORD", password);
            _automator.Username = username;
            _automator.Password = password;
        }

        private void ApplyConfigToAutomator(ConfigState config)
        {
            _automator.ShiftValue = config.Shift;
            _automator.InTime = config.InTime;
            _automator.OutTime = config.OutTime;
            _automator.WfoRemarks = config.WfoRemarks;
            _automator.WfhRemarks = config.WfhRemarks;
        }

        private void UpdateSessionIndicators(
            bool ok,
            string message,
            bool persistConfig,
            bool navigateOnSuccess,
            bool navigateOnFailure)
        {
            var wasLoggedIn = _loginVerified;
            _loginVerified = ok;
            _loginPage.SetLoginStatus(ok, message);
            _automator.LoginVerified = ok;
            TopBarControl.Status = ok ? "online" : "offline";
            if (!ok)
            {
                TopBarControl.Initials = "..";
            }
            TopBarControl.SetMenuState(ok);
            if (persistConfig)
            {
                PersistSessionStatus(ok, message);
            }

            if (ok)
            {
                if (navigateOnSuccess || ContentArea.Content == _loginPage)
                {
                    ContentArea.Content = _homePage;
                }
                var initials = TopBarControl.Initials?.Trim();
                if (!wasLoggedIn || string.IsNullOrWhiteSpace(initials) || initials == "..")
                {
                    _ = UpdateInitialsAsync(force: true);
                }
                if (!wasLoggedIn)
                {
                    _ = StartProfilePrefetchAsync();
                }
            }
            else if (navigateOnFailure)
            {
                ShowLoginPage(false);
            }
        }

        private async Task UpdateInitialsAsync(bool force)
        {
            if (_initialsFetchRunning)
            {
                return;
            }

            var current = TopBarControl.Initials?.Trim();
            if (!force && !string.IsNullOrWhiteSpace(current) && current != "..")
            {
                return;
            }

            _initialsFetchRunning = true;
            try
            {
                var initials = await _automator.GetUserInitialsAsync().ConfigureAwait(true);
                TopBarControl.Initials = string.IsNullOrWhiteSpace(initials) ? ".." : initials;
            }
            finally
            {
                _initialsFetchRunning = false;
            }
        }

        private async Task StartProfilePrefetchAsync()
        {
            if (_profileFetchRunning || _profileSummary != null)
            {
                return;
            }

            _profileFetchRunning = true;
            try
            {
                var summary = await _automator.CollectProfileSummaryAsync().ConfigureAwait(true);
                if (summary != null)
                {
                    _profileSummary = summary;
                    if (ContentArea.Content == _profilePage)
                    {
                        _profilePage.SetProfileSummary(summary);
                    }
                }
                else if (ContentArea.Content == _profilePage)
                {
                    _profilePage.SetStatus("Profile not available.", true);
                }
            }
            finally
            {
                _profileFetchRunning = false;
            }
        }

        protected override async void OnClosed(EventArgs e)
        {
            if (_sessionTimer != null)
            {
                _sessionTimer.Stop();
                _sessionTimer.Tick -= SessionTimer_Tick;
                _sessionTimer = null;
            }
            if (_historyTimer != null)
            {
                _historyTimer.Stop();
                _historyTimer.Tick -= HistoryTimer_Tick;
                _historyTimer = null;
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            await _automator.DisposeAsync();
            base.OnClosed(e);
        }
    }
}
