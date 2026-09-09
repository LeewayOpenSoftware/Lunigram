//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Contacts
{
    public sealed partial class ContactsSortedByHeader : UserControl
    {
        public ContactsViewModel ViewModel => DataContext as ContactsViewModel;

        public ContactsSortedByHeader()
        {
            InitializeComponent();

            // The XAML markup never set AutomationProperties.Name/ToolTipService.ToolTip on
            // SortButton at all -- there is no {CustomResource} extension here for Windows to
            // fall back on, so this was never a Linux-only workaround (PORTING.md §6 covers
            // {CustomResource} on attached properties not resolving under Uno Skia, which does
            // not apply here). Unconditional so both platforms get the accessible name/tooltip.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SortButton, Strings.AccDescrContactSorting);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(SortButton, Strings.AccDescrContactSorting);
        }

        private string ConvertSortedBy(bool epoch)
        {
            return epoch ? Strings.SortedByLastSeen : Strings.SortedByName;
        }
    }
}
