using CeraRegularize.Logging;
using Microsoft.Playwright;
using CeraRegularize.Stores;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CeraRegularize.Services
{
    /// <summary>
    /// Playwright-backed automation that mirrors the Python AttendanceAutomator
    /// workflow for CERAGON HRMS (login, regularize WFO, apply WFH).
    /// </summary>
    public class AttendanceAutomator : IAsyncDisposable
    {
        private const string DefaultPortalUrl = "https://in.megasoftsol.com/eHRMS/CERAGON/Login.aspx?CID=CERAGON";
        private const string InvalidCredentialPrompt = "Invalid Login Credentials - Please provide the updated password";

        public const string DayLengthFull = "full";
        public const string DayLengthFirstHalf = "first_half";
        public const string DayLengthSecondHalf = "second_half";

        private static readonly Dictionary<string, (string InTime, string OutTime)> ShiftDefaults =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["S01"] = ("0800", "2000"),
                ["S03"] = ("2000", "0800"),
                ["S02"] = ("0900", "1800"),
                ["GEN"] = ("0900", "1800"),
            };

        private static readonly Dictionary<string, string> DayLengthToStatus =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [DayLengthFull] = "PP",
                [DayLengthFirstHalf] = "PA",
                [DayLengthSecondHalf] = "AP",
            };

        private static readonly Dictionary<string, string> DayLengthToAvailability =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [DayLengthFull] = "0",
                [DayLengthFirstHalf] = "1",
                [DayLengthSecondHalf] = "2",
            };

        private readonly SemaphoreSlim _sessionLock = new(1, 1);
        private readonly string _cookiesPath;
        private IBrowser? _browser;
        private IBrowserContext? _context;
        private IPage? _page;
        private IPlaywright? _playwright;
        private bool? _browserHeadless;
        private bool _sessionBootstrapped;
        private readonly Dictionary<string, IPage> _historyPages = new(StringComparer.OrdinalIgnoreCase);
        private string? _pendingWfhRemarks;

        public AttendanceAutomator(bool? headless = null, int? slowMoMs = null)
        {
            PortalUrl = Environment.GetEnvironmentVariable("PORTAL_URL") ?? DefaultPortalUrl;
            Headless = GetBoolEnv("ATT_HEADLESS") ?? false;
            SlowMoMs = GetIntEnv("ATT_SLOW_MO_MS") ?? GetIntEnv("ATT_SLOW_MO") ?? 0;

            if (headless.HasValue)
            {
                Headless = headless.Value;
            }
            if (slowMoMs.HasValue)
            {
                SlowMoMs = slowMoMs.Value;
            }

            _cookiesPath = Path.Combine(AppContext.BaseDirectory, "hrms_session.json");
            ShiftValue = "S02";
            var defaults = ShiftDefaults["S02"];
            InTime = defaults.InTime;
            OutTime = defaults.OutTime;
            WfoRemarks = "Working from Office";
            WfhRemarks = "Working from Home";
            LoginState = "login_not_verified";
            LoginMessage = "Not tested";
            RefreshConfigFromEnvironment();
            AppLogger.LogInfo("AttendanceAutomator initialized", nameof(AttendanceAutomator));
        }

        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool LoginVerified { get; set; }
        public string LoginState { get; private set; }
        public string LoginMessage { get; private set; }
        public string LastSessionMessage { get; private set; } = string.Empty;
        public string PortalUrl { get; set; }
        public bool Headless { get; set; }
        public int SlowMoMs { get; set; }
        public string ShiftValue { get; set; }
        public string InTime { get; set; }
        public string OutTime { get; set; }
        public string WfoRemarks { get; set; }
        public string WfhRemarks { get; set; }

        public void RefreshConfigFromEnvironment()
        {
            var settings = SettingsStore.Load();
            var envHeadless = GetBoolEnv("ATT_HEADLESS");
            Headless = envHeadless ?? settings.HeadlessEnabled;
            var envUser = Environment.GetEnvironmentVariable("ATT_USERNAME");
            var envPwd = Environment.GetEnvironmentVariable("ATT_PASSWORD");

            if (string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(envUser))
            {
                Username = envUser.Trim();
            }
            if (string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(envPwd))
            {
                Password = envPwd;
            }

            if (string.IsNullOrWhiteSpace(ShiftValue))
            {
                ShiftValue = "S02";
            }
            var defaults = ShiftDefaults.TryGetValue(ShiftValue, out var shift)
                ? shift
                : ShiftDefaults["S02"];
            if (string.IsNullOrWhiteSpace(InTime))
            {
                InTime = defaults.InTime;
            }
            if (string.IsNullOrWhiteSpace(OutTime))
            {
                OutTime = defaults.OutTime;
            }
            if (string.IsNullOrWhiteSpace(WfoRemarks))
            {
                WfoRemarks = "Working from Office";
            }
            if (string.IsNullOrWhiteSpace(WfhRemarks))
            {
                WfhRemarks = "Working from Home";
            }
        }

        public async Task InitializeAsync(bool headless = true)
        {
            Headless = headless;
            await GetActivePageAsync(headless).ConfigureAwait(false);
        }

        public async Task<bool> TestLoginAsync(bool? headless = null)
        {
            RefreshConfigFromEnvironment();
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                UpdateLoginState(false, "Credentials missing");
                AppLogger.LogWarning("Test login failed: missing credentials", nameof(AttendanceAutomator));
                return false;
            }

            AppLogger.LogInfo("Test login started", nameof(AttendanceAutomator));
            await _sessionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var page = await GetActivePageAsync(headless ?? Headless).ConfigureAwait(false);
                await EnsureSessionAsync(page, forceLogin: true).ConfigureAwait(false);
                UpdateLoginState(true, "Login successful");
                if (_context != null)
                {
                    await SaveCookiesAsync(_context).ConfigureAwait(false);
                }
                AppLogger.LogInfo("Test login succeeded", nameof(AttendanceAutomator));
                return true;
            }
            catch (Exception ex)
            {
                var message = IsInvalidLoginError(ex.Message)
                    ? HandleInvalidLoginCredentials(ex.Message)
                    : ex.Message;
                UpdateLoginState(false, message);
                AppLogger.LogWarning($"Test login failed: {message}", nameof(AttendanceAutomator));
                return false;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public Task<bool> EnsureSessionAliveAsync(bool forceLogin = false)
        {
            return EnsureSessionAliveAsync(null, null, forceLogin, false);
        }

        public async Task<bool> EnsureSessionAliveAsync(
            Action<string, string, bool>? statusCallback,
            bool? headless = null,
            bool forceLogin = false,
            bool allowUnverified = false)
        {
            RefreshConfigFromEnvironment();
            AppLogger.LogDebug("EnsureSessionAlive requested", nameof(AttendanceAutomator));
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                LastSessionMessage = "Credentials missing";
                AppLogger.LogWarning("EnsureSessionAlive failed: missing credentials", nameof(AttendanceAutomator));
                return false;
            }
            if (!LoginVerified && !allowUnverified)
            {
                LastSessionMessage = "Credentials not verified. Run Test Login.";
                AppLogger.LogWarning("EnsureSessionAlive blocked: login not verified", nameof(AttendanceAutomator));
                return false;
            }

            var desiredHeadless = headless ?? Headless;
            Exception? lastExc = null;
            await _sessionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var page = await GetActivePageAsync(desiredHeadless).ConfigureAwait(false);
                        Emit(statusCallback, "Checking HRMS session", "info", false);
                        var expired = false;
                        try
                        {
                            expired = await IsSessionExpiredAsync(page).ConfigureAwait(false);
                        }
                        catch
                        {
                            expired = false;
                        }

                        await EnsureSessionAsync(page, forceLogin || expired).ConfigureAwait(false);
                        if (_context != null)
                        {
                            await SaveCookiesAsync(_context).ConfigureAwait(false);
                        }
                        LastSessionMessage = "Session active";
                        AppLogger.LogDebug("EnsureSessionAlive succeeded", nameof(AttendanceAutomator));
                        return true;
                    }
                    catch (Exception ex)
                    {
                        lastExc = ex;
                        await ResetBrowserAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _sessionLock.Release();
            }

            LastSessionMessage = lastExc?.Message ?? "Failed to ensure session";
            AppLogger.LogWarning($"EnsureSessionAlive failed: {LastSessionMessage}", nameof(AttendanceAutomator));
            return false;
        }

        public async Task<AttendanceHistorySnapshot?> CollectHistorySnapshotAsync(CancellationToken cancellationToken = default)
        {
            RefreshConfigFromEnvironment();
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                AppLogger.LogWarning("History snapshot skipped: missing credentials", nameof(AttendanceAutomator));
                return null;
            }
            if (!LoginVerified)
            {
                AppLogger.LogWarning("History snapshot skipped: login not verified", nameof(AttendanceAutomator));
                return null;
            }

            var (rangeStart, rangeEnd) = AttendanceHistoryParser.ComputeHistoryRange(DateTime.Today);
            var parser = new AttendanceHistoryParser(rangeStart, rangeEnd);
            var entries = new Dictionary<string, AttendanceHistoryEntry>(StringComparer.OrdinalIgnoreCase);

            await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var regPage = await EnsureHistoryTabAsync("regularize").ConfigureAwait(false);
                var regEntries = await parser.CollectRegularizeEntriesAsync(regPage, cancellationToken).ConfigureAwait(false);
                foreach (var entry in regEntries)
                {
                    entries[entry.Key] = entry.Value;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var leavePage = await EnsureHistoryTabAsync("leave_status").ConfigureAwait(false);
                var leaveEntries = await parser.CollectLeaveStatusEntriesAsync(leavePage, cancellationToken).ConfigureAwait(false);
                parser.MergeEntries(entries, leaveEntries);
            }
            finally
            {
                _sessionLock.Release();
            }

            var snapshot = new AttendanceHistorySnapshot
            {
                FetchedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                RangeStart = rangeStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                RangeEnd = rangeEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Entries = entries,
            };

            AppLogger.LogInfo($"History snapshot collected: {entries.Count} entries", nameof(AttendanceAutomator));
            return snapshot;
        }

        public async Task<string> GetUserInitialsAsync(bool? headless = null)
        {
            RefreshConfigFromEnvironment();
            var desiredHeadless = headless ?? Headless;
            await _sessionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                AppLogger.LogDebug("Fetching user initials", nameof(AttendanceAutomator));
                var page = await GetActivePageAsync(desiredHeadless).ConfigureAwait(false);
                await EnsureSessionAsync(page, forceLogin: false).ConfigureAwait(false);
                string? name = null;
                try
                {
                    var text = await page.EvalOnSelectorAsync<string>(
                        "#lblLoginInfo",
                        "el => (el.textContent || '').trim()").ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var afterComma = text.Contains(",", StringComparison.Ordinal)
                            ? text.Split(',', 2)[1]
                            : text;
                        var cleaned = afterComma.Split('(')[0].Trim();
                        name = cleaned;
                    }
                }
                catch
                {
                    try
                    {
                        var info = page.Locator("#lblLoginInfo");
                        var text = (await info.InnerTextAsync().ConfigureAwait(false))?.Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var afterComma = text.Contains(",", StringComparison.Ordinal)
                                ? text.Split(',', 2)[1]
                                : text;
                            var cleaned = afterComma.Split('(')[0].Trim();
                            name = cleaned;
                        }
                    }
                    catch
                    {
                    }
                }

                var initials = InitialsFromName(name);
                AppLogger.LogDebug($"Initials resolved: {initials}", nameof(AttendanceAutomator));
                return string.IsNullOrWhiteSpace(initials) ? ".." : initials;
            }
            catch
            {
                await ResetBrowserAsync().ConfigureAwait(false);
                AppLogger.LogWarning("Initials fetch failed", nameof(AttendanceAutomator));
                return "..";
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public async Task<ProfileSummary?> CollectProfileSummaryAsync(bool? headless = null)
        {
            RefreshConfigFromEnvironment();
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                AppLogger.LogWarning("Profile summary skipped: missing credentials", nameof(AttendanceAutomator));
                return null;
            }
            if (!LoginVerified)
            {
                AppLogger.LogWarning("Profile summary skipped: login not verified", nameof(AttendanceAutomator));
                return null;
            }

            var desiredHeadless = headless ?? Headless;
            await _sessionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                AppLogger.LogInfo("Profile summary fetch started", nameof(AttendanceAutomator));
                await GetActivePageAsync(desiredHeadless).ConfigureAwait(false);
                if (_context == null)
                {
                    throw new InvalidOperationException("Browser context unavailable.");
                }

                IPage? profilePage = null;
                try
                {
                    profilePage = await _context.NewPageAsync().ConfigureAwait(false);
                    await EnsureSessionAsync(profilePage, forceLogin: false).ConfigureAwait(false);
                    await GoToProfileGeneralDetailAsync(profilePage).ConfigureAwait(false);
                    var summary = await ParseProfileSummaryAsync(profilePage).ConfigureAwait(false);
                    await SaveCookiesAsync(_context).ConfigureAwait(false);

                    if (summary == null || summary.IsEmpty)
                    {
                        AppLogger.LogWarning("Profile summary empty", nameof(AttendanceAutomator));
                        return null;
                    }

                    AppLogger.LogInfo("Profile summary collected", nameof(AttendanceAutomator));
                    return summary;
                }
                finally
                {
                    if (profilePage != null)
                    {
                        try
                        {
                            await profilePage.CloseAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                AppLogger.LogError("Profile summary fetch failed", ex, nameof(AttendanceAutomator));
                await ResetBrowserAsync().ConfigureAwait(false);
                return null;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        public async Task RegularizeDatesAsync(
            IEnumerable<(DateTime date, string mode, string span)> assignments,
            Action<string, string, bool>? statusCallback = null)
        {
            if (assignments == null)
            {
                return;
            }

            AppLogger.LogInfo("RegularizeDatesAsync (grouped) started", nameof(AttendanceAutomator));
            foreach (var group in assignments.GroupBy(a => a.mode, StringComparer.OrdinalIgnoreCase))
            {
                var entries = group.Select(item => (item.date, item.span)).ToList();
                await RegularizeDatesAsync(entries, group.Key, statusCallback).ConfigureAwait(false);
            }
        }

        public async Task RegularizeDatesAsync(
            IEnumerable<(DateTime date, string span)> targetEntries,
            string mode,
            Action<string, string, bool>? statusCallback = null,
            bool? headless = null)
        {
            RefreshConfigFromEnvironment();
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                throw new InvalidOperationException("Username or password is missing. Set credentials in Config.");
            }
            if (!LoginVerified)
            {
                throw new InvalidOperationException("Credentials not verified. Run Test Login before proceeding.");
            }

            AppLogger.LogInfo($"RegularizeDatesAsync started: mode={mode}", nameof(AttendanceAutomator));
            var modeKey = (mode ?? string.Empty).ToLowerInvariant();
            if (modeKey != "wfo" && modeKey != "wfh")
            {
                throw new ArgumentException("Only 'wfo' or 'wfh' automation modes are supported right now.");
            }

            var lengthMap = new Dictionary<DateTime, string>();
            foreach (var (date, span) in targetEntries)
            {
                lengthMap[date.Date] = NormalizeSpan(span);
            }

            var sortedDates = lengthMap.Keys.OrderBy(d => d).ToList();
            var allowedDates = FilterAllowedDates(sortedDates);
            if (allowedDates.Count == 0)
            {
                Emit(statusCallback, "No dates are allowed based on the attendance cycle window.", "error", false);
                throw new InvalidOperationException("No dates are allowed based on the attendance cycle window.");
            }
            if (allowedDates.Count != sortedDates.Count)
            {
                var skipped = sortedDates.Except(allowedDates).OrderBy(d => d).ToList();
                Emit(
                    statusCallback,
                    $"Skipping dates outside allowed window: [{string.Join(", ", skipped.Select(d => d.ToString("yyyy-MM-dd")))}]",
                    "warning",
                    false);
            }

            var desiredHeadless = headless ?? Headless;

            await _sessionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var page = await GetActivePageAsync(desiredHeadless).ConfigureAwait(false);
                await EnsureSessionAsync(page, forceLogin: false).ConfigureAwait(false);

                if (modeKey == "wfo")
                {
                    await EnsureHomePageAsync(page).ConfigureAwait(false);
                    if (await DismissDisabledPopupAsync(page).ConfigureAwait(false))
                    {
                        Emit(statusCallback, "Closed leftover disabled popup before starting WFO flow", "warning", false);
                    }

                    foreach (var date in allowedDates)
                    {
                        Emit(statusCallback, "Opening Regularize Attendance screen", "info", false);
                        await GoToRegularizeAsync(page).ConfigureAwait(false);
                        Emit(statusCallback, "Regularize page loaded", "info", true);

                        var span = lengthMap.TryGetValue(date, out var length) ? length : DayLengthFull;
                        Emit(statusCallback, $"Processing {date:yyyy-MM-dd} [{span}]", "info", false);
                        await SwitchMonthForDateAsync(page, date).ConfigureAwait(false);
                        if (!await OpenPopupForDateAsync(page, date).ConfigureAwait(false))
                        {
                            Emit(statusCallback, $"Skipping {date:yyyy-MM-dd}: date cell not found or disabled.", "warning", true);
                            continue;
                        }
                        if (await DismissDisabledPopupAsync(page).ConfigureAwait(false))
                        {
                            Emit(statusCallback, $"Skipping {date:yyyy-MM-dd}: popup fields are locked by portal.", "warning", true);
                            continue;
                        }

                        Emit(statusCallback, $"Filling popup for {date:yyyy-MM-dd}", "info", false);
                        await FillRegularizePopupAsync(page, span).ConfigureAwait(false);
                        await page.WaitForTimeoutAsync(5000).ConfigureAwait(false);
                        Emit(statusCallback, $"Filled {date:yyyy-MM-dd} (waiting 5s for review)", "info", true);
                        await SubmitPopupAsync(page).ConfigureAwait(false);
                        Emit(statusCallback, $"Submitted {date:yyyy-MM-dd}", "info", true);
                    }
                    await EnsureHomePageAsync(page).ConfigureAwait(false);
                }
                else
                {
                    foreach (var date in allowedDates)
                    {
                        await EnsureHomePageAsync(page).ConfigureAwait(false);
                        if (await DismissDisabledPopupAsync(page).ConfigureAwait(false))
                        {
                            Emit(statusCallback, "Closed leftover disabled popup before WFH flow", "warning", false);
                        }

                        Emit(statusCallback, "Opening Apply Leave screen", "info", false);
                        await GoToApplyLeaveAsync(page).ConfigureAwait(false);
                        Emit(statusCallback, "Apply Leave page loaded", "info", true);

                        var span = lengthMap.TryGetValue(date, out var length) ? length : DayLengthFull;
                        Emit(statusCallback, $"Applying WFH for {date:yyyy-MM-dd} [{span}]", "info", false);
                        if (!await FillWorkFromHomeFormAsync(page, date, span).ConfigureAwait(false))
                        {
                            Emit(statusCallback, $"Skipping {date:yyyy-MM-dd}: unable to fill WFH form.", "warning", true);
                            continue;
                        }
                        var submitted = await SubmitApplyLeaveAsync(page).ConfigureAwait(false);
                        if (submitted)
                        {
                            Emit(statusCallback, $"Submitted WFH for {date:yyyy-MM-dd}", "info", true);
                        }
                        else
                        {
                            Emit(statusCallback, $"Apply Leave submission may have failed for {date:yyyy-MM-dd} (check portal).", "warning", true);
                        }
                    }
                }

                Emit(statusCallback, "Run completed", "info", true);
                if (_context != null)
                {
                    await SaveCookiesAsync(_context).ConfigureAwait(false);
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private static List<DateTime> FilterAllowedDates(IReadOnlyList<DateTime> sortedDates)
        {
            var today = DateTime.Today;
            int cycleStartMonth;
            int cycleStartYear;
            if (today.Day >= 21)
            {
                cycleStartMonth = today.Month;
                cycleStartYear = today.Year;
            }
            else
            {
                if (today.Month == 1)
                {
                    cycleStartMonth = 12;
                    cycleStartYear = today.Year - 1;
                }
                else
                {
                    cycleStartMonth = today.Month - 1;
                    cycleStartYear = today.Year;
                }
            }

            var currentCycleStart = new DateTime(cycleStartYear, cycleStartMonth, 21);
            return sortedDates.Where(d => d.Date >= currentCycleStart).ToList();
        }

        private async Task EnsureSessionAsync(IPage page, bool forceLogin)
        {
            var needLogin = forceLogin;
            if (!forceLogin && !_sessionBootstrapped)
            {
                try
                {
                    if (!await IsLoginPageAsync(page).ConfigureAwait(false) && await HasHomeLinkAsync(page).ConfigureAwait(false))
                    {
                        _sessionBootstrapped = true;
                        return;
                    }
                }
                catch
                {
                    needLogin = true;
                }
            }

            try
            {
                if (await IsLoginPageAsync(page).ConfigureAwait(false))
                {
                    needLogin = true;
                }
                else if (!await HasHomeLinkAsync(page).ConfigureAwait(false))
                {
                    needLogin = true;
                }
                else if (await IsSessionExpiredAsync(page).ConfigureAwait(false))
                {
                    needLogin = true;
                }
            }
            catch
            {
                needLogin = true;
            }

            if (!needLogin)
            {
                return;
            }

            await page.GotoAsync(PortalUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = 20000,
            }).ConfigureAwait(false);

            if (await IsLoginPageAsync(page).ConfigureAwait(false) || !await HasHomeLinkAsync(page).ConfigureAwait(false))
            {
                await LoginWithFormAsync(page).ConfigureAwait(false);
                await ValidateLoginResultAsync(page).ConfigureAwait(false);
            }

            _sessionBootstrapped = true;
        }

        private async Task LoginWithFormAsync(IPage page)
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                throw new InvalidOperationException("Credentials missing.");
            }

            var usernameField = page.Locator("input[type='text']").First;
            var passwordField = page.Locator("input[type='password']").First;
            await usernameField.FillAsync(Username).ConfigureAwait(false);
            await passwordField.FillAsync(Password).ConfigureAwait(false);
            var loginButton = page.Locator("input[type='submit'], button:has-text('Login')").First;
            await loginButton.ClickAsync().ConfigureAwait(false);
            await page.WaitForTimeoutAsync(3000).ConfigureAwait(false);
        }

        private async Task ValidateLoginResultAsync(IPage page)
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(500).ConfigureAwait(false);

            try
            {
                var errorSpan = page.Locator("#ucLogin_cvLogin");
                if (await errorSpan.IsVisibleAsync().ConfigureAwait(false))
                {
                    var msg = (await errorSpan.InnerTextAsync().ConfigureAwait(false))?.Trim();
                    msg = string.IsNullOrWhiteSpace(msg) ? "Invalid login credentials." : msg;
                    if (IsInvalidLoginError(msg))
                    {
                        msg = HandleInvalidLoginCredentials(msg);
                    }
                    throw new InvalidOperationException(msg);
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
                // ignore missing error span
            }

            var currentUrl = page.Url ?? string.Empty;
            if (currentUrl.Contains("Home.aspx", StringComparison.OrdinalIgnoreCase))
            {
                UpdateLoginState(true, "Login successful");
                return;
            }

            try
            {
                var homeLink = page.Locator("a#hlHome").First;
                if (await homeLink.IsVisibleAsync().ConfigureAwait(false))
                {
                    UpdateLoginState(true, "Login successful");
                    return;
                }
            }
            catch
            {
            }

            if (await IsLoginPageAsync(page).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Login failed or credentials invalid.");
            }
            throw new InvalidOperationException("Login did not reach Home.aspx; credentials may be invalid.");
        }

        private async Task EnsureHomePageAsync(IPage page)
        {
            var invalidAttempts = 0;
            while (true)
            {
                try
                {
                    if (await TryClickHomeAsync(page).ConfigureAwait(false) && await HasHomeLinkAsync(page).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch
                {
                }

                if (await HasHomeLinkAsync(page).ConfigureAwait(false))
                {
                    return;
                }

                try
                {
                    await EnsureSessionAsync(page, forceLogin: true).ConfigureAwait(false);
                    if (await HasHomeLinkAsync(page).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (IsInvalidLoginError(ex.Message))
                    {
                        invalidAttempts++;
                        var logged = HandleInvalidLoginCredentials(ex.Message);
                        if (invalidAttempts >= 2)
                        {
                            throw new InvalidOperationException(logged, ex);
                        }
                        continue;
                    }
                    throw;
                }
            }
        }

        private async Task<bool> TryClickHomeAsync(IPage page)
        {
            try
            {
                var homeLink = page.Locator("a#hlHome").First;
                await homeLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 }).ConfigureAwait(false);
                await homeLink.ClickAsync(new LocatorClickOptions { Timeout = 5000 }).ConfigureAwait(false);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(600).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task EnsureOnHomeAsync(IPage page)
        {
            try
            {
                await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            }
            catch
            {
            }

            if (await IsLoginPageAsync(page).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Session is on login page; monitor must login.");
            }

            var homeLink = page.Locator("a#hlHome");
            try
            {
                await homeLink.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 6000 }).ConfigureAwait(false);
            }
            catch
            {
                await page.GotoAsync(PortalUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 20000 }).ConfigureAwait(false);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                await homeLink.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 6000 }).ConfigureAwait(false);
            }

            try
            {
                await homeLink.First.ClickAsync(new LocatorClickOptions { Timeout = 5000 }).ConfigureAwait(false);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                _sessionBootstrapped = true;
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Home link not found; ensure the session is logged in and on the dashboard before submit.", ex);
            }
        }

        private async Task GoToRegularizeAsync(IPage page)
        {
            if (await IsRegularizePageAsync(page).ConfigureAwait(false))
            {
                try
                {
                    await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle }).ConfigureAwait(false);
                    await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                    return;
                }
                catch
                {
                }
            }

            await EnsureOnHomeAsync(page).ConfigureAwait(false);
            var leaveLink = page.Locator("a#tvwMenut1");
            var employeeLink = page.Locator("a#tvwMenut0").First;
            var regLink = page.Locator("a#tvwMenut6").First;

            await leaveLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 }).ConfigureAwait(false);
            await leaveLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 15000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);

            await employeeLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 }).ConfigureAwait(false);
            await employeeLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 15000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);

            await regLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 }).ConfigureAwait(false);
            await regLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 15000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(1200).ConfigureAwait(false);
        }

        private async Task GoToApplyLeaveAsync(IPage page)
        {
            await EnsureOnHomeAsync(page).ConfigureAwait(false);

            var leaveLink = page.Locator("a#tvwMenut1");
            var employeeLink = page.Locator("a#tvwMenut0").First;
            var applyLink = page.Locator("a#tvwMenut1").First;

            await leaveLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 }).ConfigureAwait(false);
            await leaveLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);

            await employeeLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 }).ConfigureAwait(false);
            await employeeLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);

            await applyLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 }).ConfigureAwait(false);
            await applyLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 15000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
        }

        private async Task GoToLeaveStatusAsync(IPage page)
        {
            if (await IsLeaveStatusPageAsync(page).ConfigureAwait(false))
            {
                try
                {
                    await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle }).ConfigureAwait(false);
                    await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                    return;
                }
                catch
                {
                }
            }

            await EnsureOnHomeAsync(page).ConfigureAwait(false);

            var leaveLink = page.Locator("a#tvwMenut1").First;
            var employeeLink = page.Locator("a#tvwMenut0").First;
            var statusLink = page.Locator("a#tvwMenut3");
            if (await statusLink.CountAsync().ConfigureAwait(false) == 0)
            {
                statusLink = page.Locator("a:has-text('Leave Status')").First;
            }

            await leaveLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 }).ConfigureAwait(false);
            await leaveLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);

            await employeeLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 }).ConfigureAwait(false);
            await employeeLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);

            await statusLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 }).ConfigureAwait(false);
            await statusLink.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5000 }).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(1200).ConfigureAwait(false);
        }

        private async Task GoToProfileGeneralDetailAsync(IPage page)
        {
            if (await IsProfilePageAsync(page).ConfigureAwait(false))
            {
                try
                {
                    await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle }).ConfigureAwait(false);
                    await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                    return;
                }
                catch
                {
                }
            }

            await EnsureOnHomeAsync(page).ConfigureAwait(false);
            await ClickMenuLinkAsync(page, "Profile", "a[title='Profile']", "a:has-text('Profile')").ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
            await ClickMenuLinkAsync(page, "Personal Detail", "a[title='Personal Detail']", "a:has-text('Personal Detail')").ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
            await ClickMenuLinkAsync(page, "General Detail", "a[title='General Detail']", "a:has-text('General Detail')").ConfigureAwait(false);
            await page.WaitForTimeoutAsync(1200).ConfigureAwait(false);
        }

        private async Task<bool> IsRegularizePageAsync(IPage page)
        {
            var currentUrl = page.Url ?? string.Empty;
            if (currentUrl.Contains("AttRequest.aspx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                var grid = page.Locator("#MiddleContent_gvRep");
                var dropdown = page.Locator("#MiddleContent_ddlMonth");
                if (await grid.CountAsync().ConfigureAwait(false) > 0 && await dropdown.CountAsync().ConfigureAwait(false) > 0)
                {
                    if (await grid.First.IsVisibleAsync().ConfigureAwait(false) && await dropdown.First.IsVisibleAsync().ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private async Task<bool> IsProfilePageAsync(IPage page)
        {
            var currentUrl = page.Url ?? string.Empty;
            if (currentUrl.Contains("MyProfile.aspx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                var table = page.Locator("#MiddleContent_ucGeneralInfo_lstGeneralInfo");
                if (await table.CountAsync().ConfigureAwait(false) > 0 && await table.First.IsVisibleAsync().ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private async Task<bool> IsLeaveStatusPageAsync(IPage page)
        {
            var currentUrl = page.Url ?? string.Empty;
            if (currentUrl.Contains("LvAppStatus.aspx", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                var dropdown = page.Locator("#MiddleContent_ddlStatus");
                var grid = page.Locator("#MiddleContent_gvRep");
                if (await dropdown.CountAsync().ConfigureAwait(false) > 0 && await grid.CountAsync().ConfigureAwait(false) > 0)
                {
                    if (await dropdown.First.IsVisibleAsync().ConfigureAwait(false) && await grid.First.IsVisibleAsync().ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static async Task ClickMenuLinkAsync(IPage page, string label, params string[] selectors)
        {
            foreach (var selector in selectors)
            {
                if (string.IsNullOrWhiteSpace(selector))
                {
                    continue;
                }

                try
                {
                    var locator = page.Locator(selector).First;
                    await locator.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 15000,
                    }).ConfigureAwait(false);
                    await locator.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 15000 }).ConfigureAwait(false);
                    return;
                }
                catch
                {
                }
            }

            throw new InvalidOperationException($"{label} link not found.");
        }

        private static async Task<ProfileSummary?> ParseProfileSummaryAsync(IPage page)
        {
            try
            {
                var table = page.Locator("#MiddleContent_ucGeneralInfo_lstGeneralInfo");
                await table.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 15000,
                }).ConfigureAwait(false);

                var labels = await table.Locator("span[id*='lblField_']").AllAsync().ConfigureAwait(false);
                if (labels.Count == 0)
                {
                    return null;
                }

                var summary = new ProfileSummary();
                foreach (var label in labels)
                {
                    var labelText = (await label.InnerTextAsync().ConfigureAwait(false))?.Trim();
                    var id = await label.GetAttributeAsync("id").ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var valueId = id.Replace("lblField", "txtField", StringComparison.OrdinalIgnoreCase);
                    var valueLocator = page.Locator($"#{valueId}");
                    if (await valueLocator.CountAsync().ConfigureAwait(false) == 0)
                    {
                        continue;
                    }

                    var valueText = (await valueLocator.First.InnerTextAsync().ConfigureAwait(false))?.Trim() ?? string.Empty;
                    switch (NormalizeProfileLabel(labelText))
                    {
                        case "employee id":
                            summary.EmployeeId = valueText;
                            break;
                        case "employee name":
                            summary.EmployeeName = valueText;
                            break;
                        case "designation":
                            summary.Designation = valueText;
                            break;
                        case "reporting manager":
                            summary.ReportingManager = valueText;
                            break;
                    }
                }

                return summary;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Profile summary parse failed: {ex.Message}", nameof(AttendanceAutomator));
                return null;
            }
        }

        private static string NormalizeProfileLabel(string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return string.Empty;
            }

            var cleaned = new string(label.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray());
            return cleaned.Trim().ToLowerInvariant();
        }

        private async Task<IPage> EnsureHistoryTabAsync(string tabName)
        {
            await GetActivePageAsync(Headless).ConfigureAwait(false);
            if (_context == null)
            {
                throw new InvalidOperationException("Browser context unavailable.");
            }

            if (!_historyPages.TryGetValue(tabName, out var page) || page.IsClosed)
            {
                page = await _context.NewPageAsync().ConfigureAwait(false);
                _historyPages[tabName] = page;
            }

            await EnsureSessionAsync(page, forceLogin: false).ConfigureAwait(false);

            if (string.Equals(tabName, "regularize", StringComparison.OrdinalIgnoreCase))
            {
                await GoToRegularizeAsync(page).ConfigureAwait(false);
            }
            else if (string.Equals(tabName, "leave_status", StringComparison.OrdinalIgnoreCase))
            {
                await GoToLeaveStatusAsync(page).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentException($"Unsupported history tab '{tabName}'.", nameof(tabName));
            }

            return page;
        }

        private async Task SwitchMonthForDateAsync(IPage page, DateTime targetDate)
        {
            var date = targetDate.Date;
            int selYear;
            int selMonth;
            if (date.Day >= 21)
            {
                (selYear, selMonth) = AddMonth(date.Year, date.Month, 1);
            }
            else
            {
                selYear = date.Year;
                selMonth = date.Month;
            }

            var lastDay = DateTime.DaysInMonth(selYear, selMonth);
            var optionValue = $"{selYear}-{selMonth:00}-{lastDay:00}";

            try
            {
                var dropdown = page.Locator("#MiddleContent_ddlMonth");
                await dropdown.SelectOptionAsync(optionValue).ConfigureAwait(false);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static (int year, int month) AddMonth(int year, int month, int delta)
        {
            var newMonth = month + delta;
            var newYear = year + (newMonth - 1) / 12;
            newMonth = ((newMonth - 1) % 12) + 1;
            return (newYear, newMonth);
        }

        private async Task<bool> OpenPopupForDateAsync(IPage page, DateTime targetDate)
        {
            var dateText = targetDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            var locator = page.Locator("#MiddleContent_gvRep a")
                .Filter(new LocatorFilterOptions { HasText = dateText });

            try
            {
                await locator.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 }).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            var count = await locator.CountAsync().ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                var link = locator.Nth(i);
                var disabled = await link.GetAttributeAsync("disabled").ConfigureAwait(false);
                var href = await link.GetAttributeAsync("href").ConfigureAwait(false);
                if (disabled == null && !string.IsNullOrWhiteSpace(href))
                {
                    try
                    {
                        await link.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 5000 }).ConfigureAwait(false);
                        await page.WaitForTimeoutAsync(1200).ConfigureAwait(false);
                        return true;
                    }
                    catch
                    {
                    }
                }
            }

            return false;
        }

        private async Task FillRegularizePopupAsync(IPage page, string length)
        {
            length = NormalizeSpan(length);
            var shiftValue = ShiftValue;
            if (!ShiftDefaults.ContainsKey(shiftValue))
            {
                shiftValue = "S02";
            }

            var inTimeValue = InTime;
            var outTimeValue = OutTime;
            var remarksValue = WfoRemarks;
            if (!DayLengthToStatus.TryGetValue(length, out var statusValue))
            {
                statusValue = DayLengthToStatus[DayLengthFull];
            }

            try
            {
                var shiftDropdown = page.Locator("#MiddleContent_ddlShift");
                var currentShift = await shiftDropdown.InputValueAsync().ConfigureAwait(false);
                if (!string.Equals(currentShift, shiftValue, StringComparison.OrdinalIgnoreCase))
                {
                    await shiftDropdown.SelectOptionAsync(shiftValue).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try
            {
                var inTime = page.Locator("#MiddleContent_txtIn_Time_txtTime");
                await inTime.FillAsync(inTimeValue).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                var outTime = page.Locator("#MiddleContent_txtOut_Time_txtTime");
                await outTime.FillAsync(outTimeValue).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await page.Locator("#MiddleContent_ddlLvType").SelectOptionAsync(statusValue).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                var remarksBox = page.Locator("#MiddleContent_txtRemarks");
                await remarksBox.FillAsync(remarksValue).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task<bool> FillWorkFromHomeFormAsync(IPage page, DateTime targetDate, string length)
        {
            length = NormalizeSpan(length);
            var dateStr = targetDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var availabilityValue = DayLengthToAvailability.TryGetValue(length, out var availability)
                ? availability
                : DayLengthToAvailability[DayLengthFull];

            try
            {
                await page.Locator("#MiddleContent_ddlLvType").SelectOptionAsync("G").ConfigureAwait(false);
                await WaitForSubmissionProgressAsync(page).ConfigureAwait(false);
                await WaitForPostbackAsync(page).ConfigureAwait(false);
                await DismissPortalMessageAsync(page, 1500).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            try
            {
                var fromField = page.Locator("#MiddleContent_calLvFrom_textBox");
                await fromField.FillAsync(dateStr).ConfigureAwait(false);
                await WaitForSubmissionProgressAsync(page).ConfigureAwait(false);
                await fromField.PressAsync("Tab").ConfigureAwait(false);
                await WaitForSubmissionProgressAsync(page).ConfigureAwait(false);
                await WaitForPostbackAsync(page).ConfigureAwait(false);
                await DismissPortalMessageAsync(page, 1200).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            try
            {
                var toField = page.Locator("#MiddleContent_calLvTo_textBox");
                await toField.FillAsync(dateStr).ConfigureAwait(false);
                await WaitForSubmissionProgressAsync(page).ConfigureAwait(false);
                await toField.PressAsync("Tab").ConfigureAwait(false);
                await WaitForSubmissionProgressAsync(page).ConfigureAwait(false);
                await WaitForPostbackAsync(page).ConfigureAwait(false);
                await DismissPortalMessageAsync(page, 1200).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            try
            {
                await page.Locator("#MiddleContent_ddlLvFromHDy").SelectOptionAsync(availabilityValue).ConfigureAwait(false);
                await WaitForSubmissionProgressAsync(page).ConfigureAwait(false);
                await WaitForPostbackAsync(page).ConfigureAwait(false);
                await DismissPortalMessageAsync(page, 1200).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(WfhRemarks))
            {
                try
                {
                    var remarksField = page.Locator("#MiddleContent_txtReason");
                    await remarksField.FillAsync(WfhRemarks).ConfigureAwait(false);
                    await WaitForPostbackAsync(page).ConfigureAwait(false);
                    var currentValue = await ReadLocatorValueAsync(remarksField).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(currentValue))
                    {
                        _pendingWfhRemarks = currentValue;
                    }
                }
                catch
                {
                }
            }

            return true;
        }

        private static async Task WaitForPostbackAsync(IPage page, int timeoutMs = 15000)
        {
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = timeoutMs }).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(200).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task WaitForSubmissionProgressAsync(IPage page, int timeoutMs = 70000)
        {
            var selectors = new[]
            {
                "#UpdateProgress1",
                "#UpdateProgress",
                "#MiddleContent_UpdateProgress1",
                "div[id*='UpdateProgress']",
                ".updateProgress",
                ".ajax__updateProgress",
                "text=/Loading,?\\s*please wait/i",
            };

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            foreach (var selector in selectors)
            {
                try
                {
                    var locator = page.Locator(selector).First;
                    await locator.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 1200
                    }).ConfigureAwait(false);

                    var remaining = (int)Math.Max(1000, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    await locator.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Hidden,
                        Timeout = remaining
                    }).ConfigureAwait(false);
                    return;
                }
                catch
                {
                }
            }

            try
            {
                var remaining = (int)Math.Max(2000, (deadline - DateTime.UtcNow).TotalMilliseconds);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
                {
                    Timeout = remaining
                }).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task<bool> SubmitApplyLeaveAsync(IPage page)
        {
            var submitBtn = page.Locator("#MiddleContent_btnSubmit");
            EventHandler<IDialog> handler = (_, dialog) =>
            {
                _ = dialog.AcceptAsync();
            };

            page.Dialog += handler;
            try
            {
                AppLogger.LogInfo("Submitting WFH Apply Leave", nameof(AttendanceAutomator));
                await EnsureWfhReasonBeforeSubmitAsync(page).ConfigureAwait(false);
                await submitBtn.ClickAsync(new LocatorClickOptions { Timeout = 45000 }).ConfigureAwait(false);
                try
                {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 45000 }).ConfigureAwait(false);
            }
            catch
            {
            }
            await WaitForSubmissionProgressAsync(page).ConfigureAwait(false);
            await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
            var portalMessage = await DismissPortalMessageAsync(page, 70000).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(portalMessage)
                && portalMessage.Contains("Reason is mandatory for Work from home", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.LogWarning("WFH submit blocked: reason is mandatory for Work from home", nameof(AttendanceAutomator));
            }
            _pendingWfhRemarks = null;
            AppLogger.LogInfo("WFH submit completed", nameof(AttendanceAutomator));
            return true;
            }
            catch
            {
                AppLogger.LogWarning("WFH submit failed", nameof(AttendanceAutomator));
                return false;
            }
            finally
            {
                page.Dialog -= handler;
            }
        }

        private async Task SubmitPopupAsync(IPage page)
        {
            var submitBtn = page.Locator("#MiddleContent_btnOK");
            EventHandler<IDialog> handler = (_, dialog) =>
            {
                _ = dialog.AcceptAsync();
            };

            page.Dialog += handler;
            try
            {
                AppLogger.LogInfo("Submitting WFO regularize popup", nameof(AttendanceAutomator));
                await submitBtn.ClickAsync(new LocatorClickOptions { Timeout = 45000 }).ConfigureAwait(false);
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 45000 }).ConfigureAwait(false);
                }
                catch
                {
                }
                await WaitForSubmissionProgressAsync(page).ConfigureAwait(false);
                await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                await DismissPortalMessageAsync(page, 70000).ConfigureAwait(false);
                AppLogger.LogInfo("WFO submit completed", nameof(AttendanceAutomator));
            }
            finally
            {
                page.Dialog -= handler;
            }
        }

        private async Task<bool> DismissDisabledPopupAsync(IPage page, int timeoutMs = 1500)
        {
            try
            {
                var popup = page.Locator("#MiddleContent_pnlPopup");
                await popup.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs }).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            async Task<bool> IsDisabledAsync(string selector)
            {
                try
                {
                    var element = page.Locator(selector).First;
                    var disabled = await element.GetAttributeAsync("disabled").ConfigureAwait(false);
                    if (disabled != null)
                    {
                        return true;
                    }
                    var aria = await element.GetAttributeAsync("aria-disabled").ConfigureAwait(false);
                    return string.Equals(aria, "true", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            var guardedInputs = new[]
            {
                "#MiddleContent_txtIn_Time_txtTime",
                "#MiddleContent_txtOut_Time_txtTime",
                "#MiddleContent_ddlShift",
                "#MiddleContent_ddlLvType",
            };

            var locked = false;
            foreach (var selector in guardedInputs)
            {
                if (await IsDisabledAsync(selector).ConfigureAwait(false))
                {
                    locked = true;
                    break;
                }
            }
            if (!locked)
            {
                return false;
            }

            var cancelSelectors = new[]
            {
                "#MiddleContent_btnCancel",
                "input[value='Cancel']",
                "button:has-text('Cancel')",
            };

            foreach (var selector in cancelSelectors)
            {
                try
                {
                    var button = page.Locator(selector).First;
                    await button.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 800 }).ConfigureAwait(false);
                    await button.ClickAsync().ConfigureAwait(false);
                    await page.WaitForTimeoutAsync(600).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private async Task<string?> DismissPortalMessageAsync(IPage page, int timeoutMs = 4000)
        {
            ILocator panel = page.Locator("#MsgBox_pnlMsgBox");
            try
            {
                await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs }).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            string? messageText = null;
            try
            {
                messageText = (await panel.Locator("#MsgBox_MsgBoxMessageText").InnerTextAsync().ConfigureAwait(false))?.Trim();
            }
            catch
            {
            }

            var selectors = new[]
            {
                "#MsgBox_MsgBoxCancel",
                "#MsgBox_MsgBoxClose",
                "a#MsgBox_MsgBoxClose",
                "input[id*='MsgBox'][value='Close']",
                "button:has-text('Close')",
            };

            foreach (var selector in selectors)
            {
                try
                {
                    var control = page.Locator(selector).First;
                    await control.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 800 }).ConfigureAwait(false);
                    await control.ClickAsync().ConfigureAwait(false);
                    await page.WaitForTimeoutAsync(800).ConfigureAwait(false);
                    return messageText ?? string.Empty;
                }
                catch
                {
                }
            }

            return messageText;
        }

        private async Task EnsureWfhReasonBeforeSubmitAsync(IPage page)
        {
            if (string.IsNullOrWhiteSpace(_pendingWfhRemarks))
            {
                return;
            }

            try
            {
                var field = page.Locator("#MiddleContent_txtReason");
                var current = await ReadLocatorValueAsync(field).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(current))
                {
                    return;
                }
                await field.FillAsync(_pendingWfhRemarks).ConfigureAwait(false);
                await WaitForPostbackAsync(page).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static async Task<string> ReadLocatorValueAsync(ILocator locator)
        {
            try
            {
                return await locator.InputValueAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                return await locator.EvaluateAsync<string>("el => (el.value || '').toString()").ConfigureAwait(false);
            }
            catch
            {
                return string.Empty;
            }
        }

        private Task<bool> IsLoginPageAsync(IPage page)
        {
            try
            {
                return Task.FromResult(page.Url?.Contains("Login.aspx", StringComparison.OrdinalIgnoreCase) == true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        private async Task<bool> HasHomeLinkAsync(IPage page)
        {
            try
            {
                if (page.Url?.Contains("Home.aspx", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                var homeLink = page.Locator("a#hlHome").First;
                return await homeLink.IsVisibleAsync().ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> IsSessionExpiredAsync(IPage page)
        {
            try
            {
                var banner = page.Locator("#txtSessionTime");
                if (await banner.IsVisibleAsync().ConfigureAwait(false))
                {
                    var text = (await banner.InnerTextAsync().ConfigureAwait(false))?.Trim().ToLowerInvariant();
                    return text != null && text.Contains("expired", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }

            return false;
        }

        private async Task<IPage> GetActivePageAsync(bool headless)
        {
            if (_browserHeadless.HasValue && _browserHeadless != headless)
            {
                await ResetBrowserAsync().ConfigureAwait(false);
            }

            try
            {
                if (_playwright == null)
                {
                    await PlaywrightInstaller.EnsureInstalledAsync().ConfigureAwait(false);
                    _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
                }

                if (_browser == null || !_browser.IsConnected)
                {
                    _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = headless,
                        SlowMo = SlowMoMs,
                    }).ConfigureAwait(false);
                    _browserHeadless = headless;
                }

                if (_context == null)
                {
                    _context = await _browser.NewContextAsync().ConfigureAwait(false);
                    await LoadCookiesAsync(_context).ConfigureAwait(false);
                    _sessionBootstrapped = false;
                    _historyPages.Clear();
                }

                if (_page == null || _page.IsClosed)
                {
                    _page = await _context.NewPageAsync().ConfigureAwait(false);
                    _sessionBootstrapped = false;
                }

                return _page;
            }
            catch
            {
                await ResetBrowserAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task LoadCookiesAsync(IBrowserContext context)
        {
            try
            {
                if (!File.Exists(_cookiesPath))
                {
                    return;
                }
                var json = await File.ReadAllTextAsync(_cookiesPath).ConfigureAwait(false);
                var cookies = JsonSerializer.Deserialize<List<Cookie>>(json);
                if (cookies != null)
                {
                    await context.AddCookiesAsync(cookies).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }

        private async Task SaveCookiesAsync(IBrowserContext context)
        {
            try
            {
                var cookies = await context.CookiesAsync().ConfigureAwait(false);
                var json = JsonSerializer.Serialize(cookies, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_cookiesPath, json).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task ResetBrowserAsync()
        {
            try
            {
                if (_page != null)
                {
                    await _page.CloseAsync().ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try
            {
                if (_context != null)
                {
                    await _context.CloseAsync().ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try
            {
                if (_browser != null)
                {
                    await _browser.CloseAsync().ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try
            {
                _playwright?.Dispose();
            }
            catch
            {
            }

            _page = null;
            _context = null;
            _browser = null;
            _playwright = null;
            _browserHeadless = null;
            _sessionBootstrapped = false;
            _historyPages.Clear();
        }

        private static bool? GetBoolEnv(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int? GetIntEnv(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
        }

        private static string NormalizeSpan(string span)
        {
            if (string.IsNullOrWhiteSpace(span))
            {
                return DayLengthFull;
            }

            var key = span.Trim().ToLowerInvariant();
            return key switch
            {
                "first" => DayLengthFirstHalf,
                "second" => DayLengthSecondHalf,
                DayLengthFirstHalf => DayLengthFirstHalf,
                DayLengthSecondHalf => DayLengthSecondHalf,
                DayLengthFull => DayLengthFull,
                _ => key,
            };
        }

        private static string InitialsFromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var parts = name.Replace("_", " ").Replace(".", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return string.Empty;
            }
            if (parts.Length == 1)
            {
                return parts[0].Length <= 2
                    ? parts[0].ToUpperInvariant()
                    : parts[0][..2].ToUpperInvariant();
            }

            return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
        }

        private static bool IsInvalidLoginError(string message)
        {
            var text = message?.ToLowerInvariant() ?? string.Empty;
            return text.Contains("invalid") && text.Contains("credential");
        }

        private string HandleInvalidLoginCredentials(string? rawMessage)
        {
            var message = LoginState == "login_successful"
                ? InvalidCredentialPrompt
                : (string.IsNullOrWhiteSpace(rawMessage) ? "Invalid login credentials." : rawMessage);
            LoginState = "login_unsuccessful";
            LoginVerified = false;
            LoginMessage = message;
            return message;
        }

        private void UpdateLoginState(bool success, string message)
        {
            LoginState = success ? "login_successful" : "login_unsuccessful";
            LoginVerified = success;
            LoginMessage = message;
        }

        private static void Emit(Action<string, string, bool>? statusCallback, string message, string level, bool advance)
        {
            if (statusCallback == null)
            {
                return;
            }
            try
            {
                statusCallback(message, level, advance);
            }
            catch
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ResetBrowserAsync().ConfigureAwait(false);
        }
    }
}
