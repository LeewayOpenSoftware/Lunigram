//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Views.Popups;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public partial class SettingsComboBox : ComboBox
    {
        private TextBlock PlaceholderTextBlock;

        public SettingsComboBox()
        {
#if LINUX
            // The <Style TargetType="local:SettingsComboBox"> of Themes/Generic.xaml is win:-only
            // (it derives from DefaultComboBoxStyle), so asking for this type's default style here
            // would find none at all and the control would come out without a template. The plain
            // ComboBox style is what that style was based on anyway.
            DefaultStyleKey = typeof(ComboBox);
#else
            DefaultStyleKey = typeof(SettingsComboBox);
#endif
            SelectionChanged += OnSelectionChanged;
        }

        protected override void OnApplyTemplate()
        {
            PlaceholderTextBlock = GetTemplateChild(nameof(PlaceholderTextBlock)) as TextBlock;
            PlaceholderTextBlock?.Padding = new Thickness(0, 0, 6, 0);

            base.OnApplyTemplate();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedItem is SettingsOptionItem item)
            {
                PlaceholderText = item.Text;
            }
        }
    }
}
