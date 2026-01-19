using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace CeraRegularize.Controls
{
    public partial class LoadingOverlay : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(
                nameof(IsActive),
                typeof(bool),
                typeof(LoadingOverlay),
                new PropertyMetadata(false, OnIsActiveChanged));

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(
                nameof(Message),
                typeof(string),
                typeof(LoadingOverlay),
                new PropertyMetadata("Loading...", OnMessageChanged));

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CornerRadius),
                typeof(CornerRadius),
                typeof(LoadingOverlay),
                new PropertyMetadata(new CornerRadius(0)));

        private Storyboard? _spinStoryboard;

        public LoadingOverlay()
        {
            InitializeComponent();
            Loaded += (_, _) => UpdateState();
        }

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public string Message
        {
            get => (string)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LoadingOverlay overlay)
            {
                overlay.UpdateState();
            }
        }

        private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LoadingOverlay overlay)
            {
                overlay.SetMessageText(e.NewValue as string);
            }
        }

        private void UpdateState()
        {
            Root.Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
            Root.IsHitTestVisible = IsActive;
            SetMessageText(Message);
            if (IsActive)
            {
                StartSpinner();
            }
            else
            {
                StopSpinner();
            }
        }

        private void StartSpinner()
        {
            _spinStoryboard ??= (Storyboard)Resources["SpinStoryboard"];
            _spinStoryboard.Begin(this, true);
        }

        private void StopSpinner()
        {
            _spinStoryboard?.Stop(this);
        }

        private void SetMessageText(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                MessageText.Text = string.Empty;
                MessageText.Visibility = Visibility.Collapsed;
                return;
            }

            MessageText.Text = message;
            MessageText.Visibility = Visibility.Visible;
        }
    }
}
