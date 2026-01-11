using System;
using System.Windows;
using System.Windows.Controls;

namespace CeraRegularize.Controls
{
    public static class GridSpacing
    {
        public static readonly DependencyProperty RowSpacingProperty =
            DependencyProperty.RegisterAttached(
                "RowSpacing",
                typeof(double),
                typeof(GridSpacing),
                new PropertyMetadata(0d, OnSpacingChanged));

        public static readonly DependencyProperty ColumnSpacingProperty =
            DependencyProperty.RegisterAttached(
                "ColumnSpacing",
                typeof(double),
                typeof(GridSpacing),
                new PropertyMetadata(0d, OnSpacingChanged));

        private static readonly DependencyProperty BaseMarginProperty =
            DependencyProperty.RegisterAttached(
                "BaseMargin",
                typeof(Thickness),
                typeof(GridSpacing),
                new PropertyMetadata(default(Thickness)));

        private static readonly DependencyProperty IsHookedProperty =
            DependencyProperty.RegisterAttached(
                "IsHooked",
                typeof(bool),
                typeof(GridSpacing),
                new PropertyMetadata(false));

        public static void SetRowSpacing(DependencyObject element, double value)
            => element.SetValue(RowSpacingProperty, value);

        public static double GetRowSpacing(DependencyObject element)
            => (double)element.GetValue(RowSpacingProperty);

        public static void SetColumnSpacing(DependencyObject element, double value)
            => element.SetValue(ColumnSpacingProperty, value);

        public static double GetColumnSpacing(DependencyObject element)
            => (double)element.GetValue(ColumnSpacingProperty);

        private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Grid grid)
            {
                return;
            }

            if (!(bool)grid.GetValue(IsHookedProperty))
            {
                grid.Loaded += Grid_Loaded;
                grid.Unloaded += Grid_Unloaded;
                grid.SetValue(IsHookedProperty, true);
            }

            ApplySpacing(grid);
        }

        private static void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                ApplySpacing(grid);
            }
        }

        private static void Grid_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                grid.Loaded -= Grid_Loaded;
                grid.Unloaded -= Grid_Unloaded;
                grid.SetValue(IsHookedProperty, false);
            }
        }

        private static void ApplySpacing(Grid grid)
        {
            var rowSpacing = Math.Max(0, GetRowSpacing(grid));
            var columnSpacing = Math.Max(0, GetColumnSpacing(grid));

            foreach (UIElement child in grid.Children)
            {
                if (child is not FrameworkElement element)
                {
                    continue;
                }

                var baseMargin = (Thickness)element.GetValue(BaseMarginProperty);
                if (baseMargin.Equals(default(Thickness)))
                {
                    baseMargin = element.Margin;
                    element.SetValue(BaseMarginProperty, baseMargin);
                }

                var row = Grid.GetRow(element);
                var column = Grid.GetColumn(element);
                var margin = new Thickness(
                    baseMargin.Left + (column > 0 ? columnSpacing : 0),
                    baseMargin.Top + (row > 0 ? rowSpacing : 0),
                    baseMargin.Right,
                    baseMargin.Bottom);

                element.Margin = margin;
            }
        }
    }
}
