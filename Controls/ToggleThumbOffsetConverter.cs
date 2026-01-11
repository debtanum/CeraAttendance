using System;
using System.Globalization;
using System.Windows.Data;

namespace CeraRegularize.Controls
{
    public sealed class ToggleThumbOffsetConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 3)
            {
                return 0d;
            }

            if (!TryGetDouble(values[0], out var trackWidth)
                || !TryGetDouble(values[1], out var thumbSize)
                || !TryGetDouble(values[2], out var margin))
            {
                return 0d;
            }

            var offset = trackWidth - thumbSize - (margin * 2);
            return offset < 0 ? 0 : offset;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        private static bool TryGetDouble(object value, out double result)
        {
            switch (value)
            {
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case int i:
                    result = i;
                    return true;
                case string s:
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
                default:
                    result = 0d;
                    return false;
            }
        }
    }
}
