using System.Windows;

namespace CeraRegularize.Controls
{
    public static class SyncButtonBehavior
    {
        public static readonly DependencyProperty IsSyncingProperty =
            DependencyProperty.RegisterAttached(
                "IsSyncing",
                typeof(bool),
                typeof(SyncButtonBehavior),
                new FrameworkPropertyMetadata(false));

        public static bool GetIsSyncing(DependencyObject element)
        {
            return (bool)element.GetValue(IsSyncingProperty);
        }

        public static void SetIsSyncing(DependencyObject element, bool value)
        {
            element.SetValue(IsSyncingProperty, value);
        }
    }
}
