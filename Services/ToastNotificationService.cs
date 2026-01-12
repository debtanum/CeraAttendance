using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CeraRegularize.Services
{
    public static class ToastNotificationService
    {
        private const string AppId = "CeraRegularize.Desktop";
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                SetCurrentProcessExplicitAppUserModelID(AppId);
            }
            catch
            {
            }

            try
            {
                EnsureShortcut();
                _initialized = true;
            }
            catch
            {
            }
        }

        public static bool TryShow(string title, string message)
        {
            try
            {
                Initialize();
                var toastXml = BuildToastXml(title, message);
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(toastXml);
                var toast = new ToastNotification(xmlDoc);
                ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildToastXml(string title, string message)
        {
            return $@"<toast>
  <visual>
    <binding template=""ToastGeneric"">
      <text>{EscapeXml(title)}</text>
      <text>{EscapeXml(message)}</text>
    </binding>
  </visual>
</toast>";
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static void EnsureShortcut()
        {
            var shortcutPath = GetShortcutPath();
            if (File.Exists(shortcutPath))
            {
                return;
            }

            var exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Unable to resolve executable path.");
            var workingDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;

            var shellLink = (IShellLinkW)new ShellLink();
            shellLink.SetPath(exePath);
            shellLink.SetWorkingDirectory(workingDir);
            shellLink.SetDescription("CeraRegularize");

            var propertyStore = (IPropertyStore)shellLink;
            using (var propVariant = new PropVariant(AppId))
            {
                var key = PropertyKey.AppUserModelId;
                propertyStore.SetValue(ref key, propVariant);
                propertyStore.Commit();
            }

            var persistFile = (IPersistFile)shellLink;
            persistFile.Save(shortcutPath, true);
        }

        private static string GetShortcutPath()
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            return Path.Combine(startMenu, "Programs", "CeraRegularize.lnk");
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PropertyKey pkey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, [In] PropVariant pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;

            public static PropertyKey AppUserModelId => new PropertyKey
            {
                fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                pid = 5,
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private sealed class PropVariant : IDisposable
        {
            private ushort vt;
            private ushort wReserved1;
            private ushort wReserved2;
            private ushort wReserved3;
            private IntPtr pointerValue;

            public PropVariant(string value)
            {
                vt = 31; // VT_LPWSTR
                pointerValue = Marshal.StringToCoTaskMemUni(value);
            }

            public void Dispose()
            {
                PropVariantClear(this);
                GC.SuppressFinalize(this);
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear([In, Out] PropVariant pvar);
    }
}
