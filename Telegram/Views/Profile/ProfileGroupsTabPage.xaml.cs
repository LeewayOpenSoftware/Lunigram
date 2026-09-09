//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileGroupsTabPage : ProfileTabPage
    {
        public new ProfileViewModel ViewModel => DataContext as ProfileViewModel;

        public ProfileGroupsTabPage()
        {
            InitializeComponent();
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Chat chat)
            {
                ViewModel.NavigationService.NavigateToChat(chat);
            }
        }

        protected override void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = ScrollingHost.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = ScrollingHost.ItemTemplate;
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
            // ContentTemplateRoot is always null in Uno and the phased overload only ever gets its
            // Phase 0, so the row would come out as a title on a blank line. See
            // ProfileTabContainer and ProfileCell.Linux.cs. This tab has no context menu upstream,
            // hence the null handler.
            ProfileTabContainer.Bind<ProfileCell>(sender, args, null,
                (content, item) => content.UpdateChatInflated(ViewModel.ClientService, item as Chat));
#else
            else if (args.ItemContainer.ContentTemplateRoot is ProfileCell content)
            {
                content.UpdateChat(ViewModel.ClientService, args, OnContainerContentChanging);
            }
#endif
        }
    }
}
