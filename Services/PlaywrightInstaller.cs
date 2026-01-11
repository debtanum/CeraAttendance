using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CeraRegularize.Logging;

namespace CeraRegularize.Services
{
    internal static class PlaywrightInstaller
    {
        private static readonly SemaphoreSlim InstallLock = new(1, 1);
        private static bool _installed;

        public static bool IsInstalled()
        {
            if (_installed)
            {
                return true;
            }

            var cachePath = ResolveBrowserCachePath();
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(cachePath))
                {
                    return false;
                }

                var entries = Directory.GetFileSystemEntries(cachePath);
                if (entries.Length == 0)
                {
                    return false;
                }

                _installed = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task EnsureInstalledAsync()
        {
            if (IsInstalled())
            {
                return;
            }

            await InstallLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsInstalled())
                {
                    return;
                }

                AppLogger.LogInfo("Ensuring Playwright browsers are installed", nameof(PlaywrightInstaller));
                var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install" })).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"Playwright installation failed with exit code {exitCode}.");
                }

                _installed = true;
                AppLogger.LogInfo("Playwright browsers verified", nameof(PlaywrightInstaller));
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Failed to install Playwright browsers", ex, nameof(PlaywrightInstaller));
                throw;
            }
            finally
            {
                InstallLock.Release();
            }
        }

        private static string ResolveBrowserCachePath()
        {
            var env = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
            if (string.IsNullOrWhiteSpace(env))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrWhiteSpace(local)
                    ? string.Empty
                    : Path.Combine(local, "ms-playwright");
            }

            env = env.Trim();
            if (string.Equals(env, "0", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(AppContext.BaseDirectory, "ms-playwright");
            }

            try
            {
                return Path.GetFullPath(env);
            }
            catch
            {
                return env;
            }
        }
    }
}
