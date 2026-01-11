using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Media = System.Windows.Media;

namespace CeraRegularize.Controls
{
    /// <summary>
    /// Interaction logic for TopBar.xaml
    ///
    /// This control replicates the title bar from the PySide6 implementation. It
    /// exposes routed events to signal when the user selects one of the
    /// navigation actions or clicks the close button. The menu button shows
    /// the user's initials and has a colored border reflecting the current
    /// status (online, offline or unknown).
    /// </summary>
    public partial class TopBar : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // Events raised when a menu option is selected
        public event EventHandler? HomeSelected;
        public event EventHandler? ApplyLeaveSelected;
        public event EventHandler? ConfigSelected;
        public event EventHandler? LoginSelected;
        public event EventHandler? ProfileSelected;
        public event EventHandler? SettingsSelected;
        public event EventHandler? CloseClicked;
        public event EventHandler? SyncClicked;

        public TopBar()
        {
            InitializeComponent();
            // Set default values
            Status = "unknown";
            Initials = "..";
            SyncButtonBehavior.SetIsSyncing(SyncButton, false);
        }

        /// <summary>
        /// Initials displayed in the menu button. Should be two uppercase characters.
        /// </summary>
        public string Initials
        {
            get { return (string)GetValue(InitialsProperty); }
            set { SetValue(InitialsProperty, value); }
        }

        public static readonly DependencyProperty InitialsProperty =
            DependencyProperty.Register(
                nameof(Initials),
                typeof(string),
                typeof(TopBar),
                new PropertyMetadata(".."));

        /// <summary>
        /// Represents the current status (online/offline/unknown). Changing this
        /// value automatically updates the border color around the menu button.
        /// </summary>
        public string Status
        {
            get { return (string)GetValue(StatusProperty); }
            set { SetValue(StatusProperty, value); }
        }

        public static readonly DependencyProperty StatusProperty =
            DependencyProperty.Register(
                nameof(Status),
                typeof(string),
                typeof(TopBar),
                new PropertyMetadata("unknown", OnStatusChanged));

        private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = (TopBar)d;
            bar.UpdateStatusBorder((string)e.NewValue);
        }

        private Media.Brush _statusBorderBrush = Media.Brushes.Goldenrod;

        /// <summary>
        /// Brush used for the menu button's border. It changes based on status.
        /// </summary>
        public Media.Brush StatusBorderBrush
        {
            get => _statusBorderBrush;
            private set
            {
                if (_statusBorderBrush != value)
                {
                    _statusBorderBrush = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBorderBrush)));
                }
            }
        }

        /// <summary>
        /// Map status strings to coloured brushes. Defaults to yellow.
        /// </summary>
        private void UpdateStatusBorder(string status)
        {
            string colour = status?.Trim().ToLower() switch
            {
                "online" => "#22C55E",
                "offline" => "#EF4444",
                _ => "#FACC15",
            };
            try
            {
                var converted = new Media.BrushConverter().ConvertFromString(colour) as Media.Brush;
                StatusBorderBrush = converted ?? Media.Brushes.Goldenrod;
            }
            catch
            {
                // fallback to yellow if conversion fails
                StatusBorderBrush = Media.Brushes.Goldenrod;
            }
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            // Show the context menu relative to the menu button
            if (MenuButton.ContextMenu != null)
            {
                MenuButton.ContextMenu.PlacementTarget = MenuButton;
                MenuButton.ContextMenu.IsOpen = true;
            }
        }

        private void HomeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            HomeSelected?.Invoke(this, EventArgs.Empty);
        }

        private void LeaveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ApplyLeaveSelected?.Invoke(this, EventArgs.Empty);
        }

        private void ConfigMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ConfigSelected?.Invoke(this, EventArgs.Empty);
        }

        private void LoginMenuItem_Click(object sender, RoutedEventArgs e)
        {
            LoginSelected?.Invoke(this, EventArgs.Empty);
        }

        private void ProfileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ProfileSelected?.Invoke(this, EventArgs.Empty);
        }

        private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SettingsSelected?.Invoke(this, EventArgs.Empty);
        }

        public void SetMenuState(bool loggedIn)
        {
            HomeMenuItem.IsEnabled = loggedIn;
            LeaveMenuItem.IsEnabled = loggedIn;
            ConfigMenuItem.IsEnabled = loggedIn;
            SettingsMenuItem.IsEnabled = true;
            ProfileMenuItem.IsEnabled = loggedIn;
            ProfileMenuItem.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
            LoginMenuItem.IsEnabled = !loggedIn;
            LoginMenuItem.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseClicked?.Invoke(this, EventArgs.Empty);
        }

        private void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            SyncClicked?.Invoke(this, EventArgs.Empty);
        }

        public void SetSyncing(bool isSyncing)
        {
            SyncButtonBehavior.SetIsSyncing(SyncButton, isSyncing);
            SyncButton.IsEnabled = !isSyncing;
        }
    }
}
