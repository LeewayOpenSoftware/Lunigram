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
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Supergroups;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Views.Supergroups
{
    public sealed partial class SupergroupAdministratorsPage : HostedPage, ISupergroupMembersDelegate
    {
        public SupergroupAdministratorsViewModel ViewModel => DataContext as SupergroupAdministratorsViewModel;

        public SupergroupAdministratorsPage()
        {
            InitializeComponent();
            Title = Strings.ChannelAdministrators;
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.EditMember(e.ClickedItem as ChatMember);
        }

        #region Context menu

        private void Member_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var member = ScrollingHost.ItemFromContainer(sender) as ChatMember;

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(ViewModel.EditMember, member, Strings.EditAdminRights, Icons.ShieldStar);
            flyout.CreateFlyoutItem(ViewModel.DismissMember, member, Strings.ChannelRemoveUserAdmin, Icons.SubtractCircle, destructive: true);
            flyout.ShowAt(sender, args);
        }

        #endregion

        #region Recycle

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += Member_ContextRequested;
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
            // OnChoosingItemContainer above never runs in Uno, so the edit / dismiss menu is
            // attached from here; the cell is reached with ContentRoot() because
            // ContentTemplateRoot is null for every templated container; and the inflated writer
            // is used because this callback is raised once, at Phase 0, and RegisterUpdateCallback
            // never comes back. See Telegram.Common.ProfileTabContainer.
            ProfileTabContainer.Bind<ProfileCell>(sender, args, Member_ContextRequested,
                (content, item) =>
                {
                    if (item is ChatMember member)
                    {
                        content.UpdateSupergroupMember(ViewModel.ClientService, member);
                    }
                });
#else
            else if (args.ItemContainer.ContentTemplateRoot is ProfileCell content)
            {
                content.UpdateSupergroupMember(ViewModel.ClientService, args, OnContainerContentChanging);
            }
#endif
        }

        #endregion

        #region Binding

        public void UpdateChat(Chat chat) { }
        public void UpdateChatTitle(Chat chat) { }
        public void UpdateChatPhoto(Chat chat) { }

        public void UpdateSupergroup(Chat chat, Supergroup group, SupergroupFullInfo fullInfo)
        {
            if (fullInfo != null)
            {
                ViewModel.UpdateIsAggressiveAntiSpamEnabled(fullInfo.HasAggressiveAntiSpamEnabled);
                AntiSpam.Visibility = fullInfo.CanToggleAggressiveAntiSpam ? Visibility.Visible : Visibility.Collapsed;
                HeaderPanel.Footer = fullInfo.CanToggleAggressiveAntiSpam ? Strings.ChannelAntiSpamInfo : string.Empty;
            }
            else
            {
                AntiSpam.Visibility = Visibility.Collapsed;
                HeaderPanel.Footer = string.Empty;
            }

            var canDeleteMessages = group.CanDeleteMessages();
            var canChangeInfo = group.CanChangeInfo(chat);
            var canPromoteMembers = group.CanPromoteMembers();

            AntiSpam.Visibility = canDeleteMessages && !group.IsChannel ? Visibility.Visible : Visibility.Collapsed;
            ChannelSignMessages.Visibility = canChangeInfo && group.IsChannel ? Visibility.Visible : Visibility.Collapsed;

            EventLog.Visibility = Visibility.Visible;
            AddNew.Visibility = canPromoteMembers ? Visibility.Visible : Visibility.Collapsed;
            Footer.Visibility = canPromoteMembers ? Visibility.Visible : Visibility.Collapsed;
            Footer.Text = group.IsChannel ? Strings.ChannelAdminsInfo : Strings.MegaAdminsInfo;

            HeaderPanel.Visibility = Visibility.Visible;

            ViewModel.UpdateSignMessages(group.SignMessages);
            ViewModel.UpdateShowMessageSender(group.ShowMessageSender);
        }

        public void UpdateBasicGroup(Chat chat, BasicGroup group, BasicGroupFullInfo fullInfo)
        {
            if (fullInfo != null)
            {
                ViewModel.UpdateIsAggressiveAntiSpamEnabled(false);
                AntiSpam.Visibility = fullInfo.CanToggleAggressiveAntiSpam ? Visibility.Visible : Visibility.Collapsed;
                HeaderPanel.Footer = fullInfo.CanToggleAggressiveAntiSpam ? Strings.ChannelAntiSpamInfo : string.Empty;
            }
            else
            {
                AntiSpam.Visibility = Visibility.Collapsed;
                HeaderPanel.Footer = string.Empty;
            }

            var canPromoteMembers = group.CanPromoteMembers();

            HeaderPanel.Footer = string.Empty;
            AntiSpam.Visibility = Visibility.Collapsed;
            ChannelSignMessages.Visibility = Visibility.Collapsed;

            EventLog.Visibility = Visibility.Collapsed;
            AddNew.Visibility = canPromoteMembers ? Visibility.Visible : Visibility.Collapsed;
            Footer.Visibility = canPromoteMembers ? Visibility.Visible : Visibility.Collapsed;
            Footer.Text = Strings.MegaAdminsInfo;

            HeaderPanel.Visibility = EventLog.Visibility == Visibility.Visible || AddNew.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public void UpdateMember(ChatMember member)
        {
            var container = ScrollingHost.ContainerFromItem(member) as SelectorItem;
            // ContentRoot() and not ContentTemplateRoot: in Uno the latter is null for every
            // templated container, so an UpdateChatMember arriving for a visible row would leave
            // the row showing the status it was bound with.
            var content = container?.ContentRoot() as ProfileCell;

            content?.UpdateSupergroupMember(ViewModel.ClientService, member);
        }

        private string ConvertSignMessagesFooter(bool showMessageSender)
        {
            if (showMessageSender)
            {
                return Strings.ChannelSignProfilesInfo;
            }

            return Strings.ChannelSignMessagesInfo;
        }

        #endregion

    }
}
