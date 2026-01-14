using System.Windows;
using System.Windows.Controls;

namespace CeraRegularize.Controls
{
    public sealed class MenuItemStyleSelector : StyleSelector
    {
        public Style? MenuItemStyle { get; set; }
        public Style? SeparatorStyle { get; set; }

        public override Style? SelectStyle(object item, DependencyObject container)
        {
            if (item is Separator || container is Separator)
            {
                return SeparatorStyle;
            }

            return MenuItemStyle;
        }
    }
}
