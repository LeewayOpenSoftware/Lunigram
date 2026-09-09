//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls.Cells;
using Telegram.Td.Api;
using Telegram.ViewModels.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsWebSessionsPage : HostedPage
    {
        public SettingsWebSessionsViewModel ViewModel => DataContext as SettingsWebSessionsViewModel;

        public SettingsWebSessionsPage()
        {
            InitializeComponent();
            Title = Strings.WebSessionsTitle;
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.Terminate(e.ClickedItem as ConnectedWebsite);
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
#if LINUX
            // Same as SettingsSessionsPage: ContentTemplateRoot is always null in Uno, so the type
            // test below never matches and every row draws the empty WebSessionCell of its
            // template (PORTING.md 6).
            var clientService = ViewModel.ClientService;
            ProfileTabContainer.Bind<WebSessionCell>(sender, args, null,
                (cell, item) => cell.UpdateConnectedWebsite(clientService, item as ConnectedWebsite));
#else
            else if (args.ItemContainer.ContentTemplateRoot is WebSessionCell cell)
            {
                cell.UpdateConnectedWebsite(ViewModel.ClientService, args.Item as ConnectedWebsite);
                args.Handled = true;
            }
#endif
        }
    }
}
