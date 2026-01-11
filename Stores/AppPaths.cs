using System;
using System.IO;

namespace CeraRegularize.Stores
{
    public static class AppPaths
    {
        private const string AppFolderName = "CeraRegularize";

        public static string AppDataDirectory
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }

                var target = Path.Combine(root, AppFolderName);
                try
                {
                    Directory.CreateDirectory(target);
                }
                catch
                {
                }

                return target;
            }
        }

        public static string DataFile(string name)
        {
            return Path.Combine(AppDataDirectory, name);
        }
    }
}
