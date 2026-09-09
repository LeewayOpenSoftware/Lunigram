//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsBlockedChatsPage : HostedPage
    {
        public SettingsBlockedChatsViewModel ViewModel => DataContext as SettingsBlockedChatsViewModel;

        public SettingsBlockedChatsPage()
        {
            InitializeComponent();
            Title = Strings.BlockedUsers;

        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MessageSender messageSender)
            {
                ViewModel.NavigationService.NavigateToSender(messageSender);
            }
        }

        #region Recycle

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += User_ContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
#if LINUX
            // Three separate PORTING.md 6 traps meet on this page, and every one of them is
            // silent - the page would render in full and do nothing.
            //
            // 1. OnChoosingItemContainer above is dead code here: Uno never raises
            //    ChoosingItemContainer. The style and the item template arrive anyway (from the
            //    ListView and from TableListView.GetContainerForItemOverride), but the
            //    ContextRequested subscription does not, and that subscription IS the "Unblock"
            //    menu. Without it, right-clicking a blocked user lands and nothing happens - the
            //    exact defect already caught on the chat list.
            // 2. ContentTemplateRoot is always null for a templated container, so the type test
            //    in the #else branch never matches and every blocked row draws empty.
            // 3. ContainerContentChanging is raised once, with Phase always 0, and
            //    RegisterUpdateCallback never comes back - so the phased UpdateMessageSender would
            //    write the name and leave the avatar blank. Hence the inflated writer.
            var clientService = ViewModel.ClientService;
            ProfileTabContainer.Bind<ProfileCell>(sender, args, User_ContextRequested,
                (content, item) =>
                {
                    if (item is MessageSender member)
                    {
                        content.UpdateMessageSenderInflated(clientService, member);
                    }
                });
#else
            else if (args.ItemContainer.ContentTemplateRoot is ProfileCell content)
            {
                content.UpdateMessageSender(ViewModel.ClientService, args, OnContainerContentChanging);
            }
#endif
        }

        #endregion

        private void User_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var messageSender = ScrollingHost.ItemFromContainer(sender) as MessageSender;

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(ViewModel.Unblock, messageSender, Strings.Unblock, Icons.SubtractCircle);
            flyout.ShowAt(sender, args);
        }
    }
}
