using System;
using System.Linq;
using System.Windows;

namespace CeraRegularize.Themes
{
    public static class ThemeManager
    {
        private static ResourceDictionary? _activeTheme;

        public static void ApplyTheme(string? mode)
        {
            var app = System.Windows.Application.Current;
            if (app == null)
            {
                return;
            }

            var themeUri = ResolveThemeUri(mode);
            if (themeUri == null)
            {
                return;
            }

            var dictionaries = app.Resources.MergedDictionaries;
            if (_activeTheme != null)
            {
                dictionaries.Remove(_activeTheme);
            }
            else
            {
                var existing = dictionaries.FirstOrDefault(IsThemeDictionary);
                if (existing != null)
                {
                    dictionaries.Remove(existing);
                }
            }

            var next = new ResourceDictionary { Source = themeUri };
            dictionaries.Add(next);
            _activeTheme = next;
        }

        private static Uri? ResolveThemeUri(string? mode)
        {
            var key = mode?.Trim().ToLowerInvariant();
            return key switch
            {
                "dark" => new Uri("Themes/Dark.xaml", UriKind.Relative),
                "light" => new Uri("Themes/Light.xaml", UriKind.Relative),
                "system" => new Uri("Themes/Light.xaml", UriKind.Relative),
                null or "" => new Uri("Themes/Light.xaml", UriKind.Relative),
                _ => new Uri("Themes/Light.xaml", UriKind.Relative),
            };
        }

        private static bool IsThemeDictionary(ResourceDictionary dictionary)
        {
            var source = dictionary.Source?.OriginalString ?? string.Empty;
            return source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase);
        }
    }
}
