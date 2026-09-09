//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.ViewModels.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsAdvancedPage : HostedPage
    {
        public SettingsAdvancedViewModel ViewModel => DataContext as SettingsAdvancedViewModel;

        public SettingsAdvancedPage()
        {
            InitializeComponent();
            Title = Strings.PrivacyAdvanced;

            if (ApiInfo.IsPackagedRelease)
            {
                FindName(nameof(UpdatePanel));
            }

#if LINUX
            // Its only row is the "show peer ids" checkbox, which Uno's XAML generator cannot
            // compile (see the win: in the XAML), so what is left is a header and a footer over
            // nothing.
            Experimental.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
#endif
        }

        private void Shortcuts_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
#if !LINUX
            // The row that calls this is commented out upstream, and SettingsShortcutsPage is not
            // in the Linux subset.
            Frame.Navigate(typeof(SettingsShortcutsPage));
#endif
        }
    }
}
