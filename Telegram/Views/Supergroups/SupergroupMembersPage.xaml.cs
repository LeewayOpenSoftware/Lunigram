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
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Supergroups;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Views.Supergroups
{
    public sealed partial class SupergroupMembersPage : HostedPage, ISupergroupMembersDelegate, ISearchablePage
    {
        public SupergroupMembersViewModel ViewModel => DataContext as SupergroupMembersViewModel;

        public SupergroupMembersPage()
        {
            InitializeComponent();
            Title = Strings.ChannelSubscribers;
        }

        public void Search()
        {
            SearchField.StartBringIntoView();
            SearchField.Focus(FocusState.Keyboard);
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ChatMember member)
            {
                ViewModel.NavigationService.NavigateToSender(member.MemberId);
            }
        }

        #region Context menu

        private void Member_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var member = ScrollingHost.ItemFromContainer(sender) as ChatMember;
            var chat = ViewModel.Chat;

            if (chat == null || member == null)
            {
                return;
            }

            ChatMemberStatus status = null;
            if (chat.Type is ChatTypeBasicGroup basic)
            {
                status = ViewModel.ClientService.GetBasicGroup(basic.BasicGroupId)?.Status;
            }
            else if (chat.Type is ChatTypeSupergroup super)
            {
                status = ViewModel.ClientService.GetSupergroup(super.SupergroupId)?.Status;
            }

            if (status == null)
            {
                return;
            }

            var flyout = new MenuFlyout();

            if (ViewModel.ClientService.CanEditTag(chat, member))
            {
                flyout.CreateFlyoutItem(ViewModel.EditTag, member, member.Status is ChatMemberStatusAdministrator or ChatMemberStatusCreator ? Strings.EditAdminTag : Strings.EditMemberTag, Icons.PersonTag);
            }

            if (chat.Type is ChatTypeSupergroup)
            {
                flyout.CreateFlyoutItem(MemberPromote_Loaded, ViewModel.PromoteMember, chat.Type, status, member, member.Status is ChatMemberStatusAdministrator ? Strings.EditAdminRights : Strings.SetAsAdmin, Icons.ShieldStar);
                flyout.CreateFlyoutItem(MemberRestrict_Loaded, ViewModel.RestrictMember, chat.Type, status, member, member.Status is ChatMemberStatusRestricted ? Strings.ChangePermissions : Strings.KickFromSupergroup, Icons.LockClosed);
            }

            flyout.CreateFlyoutItem(MemberRemove_Loaded, ViewModel.RemoveMember, chat.Type, status, member, chat.Type is ChatTypeSupergroup { IsChannel: true } ? Strings.ChannelRemoveUser : Strings.KickFromGroup, Icons.Block);

            flyout.ShowAt(sender, args);
        }

        private bool MemberPromote_Loaded(ChatType chatType, ChatMemberStatus status, ChatMember member)
        {
            if (member.Status is ChatMemberStatusCreator)
            {
                return false;
            }

            if (member.MemberId.IsUser(ViewModel.ClientService.Options.MyId))
            {
                return false;
            }

            return status is ChatMemberStatusCreator || status is ChatMemberStatusAdministrator administrator && administrator.Rights.CanPromoteMembers;
        }

        private bool MemberRestrict_Loaded(ChatType chatType, ChatMemberStatus status, ChatMember member)
        {
            if (member.Status is ChatMemberStatusCreator or ChatMemberStatusAdministrator { CanBeEdited: false })
            {
                return false;
            }

            if (member.MemberId.IsUser(ViewModel.ClientService.Options.MyId))
            {
                return false;
            }

            if (chatType is ChatTypeSupergroup supergroup && supergroup.IsChannel)
            {
                return false;
            }

            return status is ChatMemberStatusCreator or ChatMemberStatusAdministrator { Rights.CanRestrictMembers: true };
        }

        private bool MemberRemove_Loaded(ChatType chatType, ChatMemberStatus status, ChatMember member)
        {
            if (member.Status is ChatMemberStatusCreator or ChatMemberStatusAdministrator { CanBeEdited: false })
            {
                return false;
            }

            if (member.MemberId.IsUser(ViewModel.ClientService.Options.MyId))
            {
                return false;
            }

            if (chatType is ChatTypeBasicGroup && status is ChatMemberStatusAdministrator)
            {
                return member.InviterUserId == ViewModel.ClientService.Options.MyId;
            }

            return status is ChatMemberStatusCreator or ChatMemberStatusAdministrator { Rights.CanRestrictMembers: true };
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
            // OnChoosingItemContainer above never runs in Uno: the promote / restrict / remove
            // menu is attached here, the cell is found with ContentRoot(), and the row is written
            // in one pass because there is only ever a Phase 0. See
            // Telegram.Common.ProfileTabContainer.
            ProfileTabContainer.Bind<ProfileCell>(sender, args, Member_ContextRequested,
                (content, item) =>
                {
                    if (item is ChatMember member)
                    {
                        content.UpdateChatSharedMembers(ViewModel.ClientService, member);
                    }
                });
#else
            else if (args.ItemContainer.ContentTemplateRoot is ProfileCell cell)
            {
                cell.UpdateChatSharedMembers(ViewModel.ClientService, args, OnContainerContentChanging);
            }
#endif
        }

        #endregion

        #region Delegate

        public void UpdateSupergroup(Chat chat, Supergroup group, SupergroupFullInfo fullInfo)
        {
            if (fullInfo != null)
            {
                ViewModel.UpdateHiddenMembers(fullInfo.HasHiddenMembers);
                HideMembers.Visibility = fullInfo.CanHideMembers ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                HideMembers.Visibility = Visibility.Collapsed;
            }

            ViewModel.UpdateEditTags(chat.Permissions.CanEditTag);

            Title = group.IsChannel ? Strings.ChannelSubscribers : Strings.ChannelMembers;

            AddNew.Content = group.IsChannel ? Strings.AddSubscriber : Strings.AddMember;
            // CC-2: SupergroupMembersViewModel.Add's picker (ChooseChatsPopup) is ported now.
            AddNewPanel.Visibility = group.CanInviteUsers(chat) ? Visibility.Visible : Visibility.Collapsed;

            EditTags.Visibility = group.CanRestrictMembers() && !group.IsChannel ? Visibility.Visible : Visibility.Collapsed;

            Footer.Visibility = group.IsChannel ? Visibility.Visible : Visibility.Collapsed;
        }

        public void UpdateBasicGroup(Chat chat, BasicGroup group, BasicGroupFullInfo fullInfo)
        {
            if (fullInfo != null)
            {
                ViewModel.UpdateHiddenMembers(false);
                HideMembers.Visibility = fullInfo.CanHideMembers ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                HideMembers.Visibility = Visibility.Collapsed;
            }

            ViewModel.UpdateEditTags(chat.Permissions.CanEditTag);

            AddNew.Content = Strings.AddMember;
            // CC-2: see UpdateSupergroup.
            AddNewPanel.Visibility = group.CanInviteUsers(chat) ? Visibility.Visible : Visibility.Collapsed;

            EditTags.Visibility = group.CanRestrictMembers() ? Visibility.Visible : Visibility.Collapsed;

            Footer.Visibility = Visibility.Collapsed;
        }

        public void UpdateChat(Chat chat) { }
        public void UpdateChatTitle(Chat chat) { }
        public void UpdateChatPhoto(Chat chat) { }

        public void UpdateMember(ChatMember member)
        {
            var container = ScrollingHost.ContainerFromItem(member) as SelectorItem;
            // ContentRoot() and not ContentTemplateRoot, which is null for every templated
            // container in Uno: without this an UpdateChatMember for a visible row is a no-op.
            var content = container?.ContentRoot() as ProfileCell;

            content?.UpdateChatSharedMembers(ViewModel.ClientService, member);
        }

        #endregion
    }
}
