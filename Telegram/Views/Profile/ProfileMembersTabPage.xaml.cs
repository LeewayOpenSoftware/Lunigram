//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileMembersTabPage : ProfileTabPage
    {
        public new ProfileViewModel ViewModel => DataContext as ProfileViewModel;

        public ProfileMembersTabPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
#if LINUX
            // Upstream does not chain here, and on Windows that costs nothing. On Linux the base
            // OnNavigatedTo is what starts the tab's own incremental loader: these pages have no
            // ScrollViewer of their own and ProfilePage cannot pump them, because LastCacheIndex
            // throws NotImplementedException on both Uno panels. Without this call the members
            // list stops at whatever ChatMemberCollection happened to load first and never asks
            // for another page.
            base.OnNavigatedTo(e);
#endif
            if (ViewModel.ClientService.TryGetSupergroup(ViewModel.Chat, out Supergroup supergroup))
            {
                AddNew.Content = supergroup.IsChannel ? Strings.AddSubscriber : Strings.AddMember;
                AddNewPanel.Visibility = supergroup.CanInviteUsers(ViewModel.Chat) ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (ViewModel.ClientService.TryGetBasicGroup(ViewModel.Chat, out BasicGroup basicGroup))
            {
                AddNew.Content = Strings.AddMember;
                AddNewPanel.Visibility = basicGroup.CanInviteUsers(ViewModel.Chat) ? Visibility.Visible : Visibility.Collapsed;
            }
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
            var chat = ViewModel.Chat;
            var member = ScrollingHost.ItemFromContainer(sender) as ChatMember;

            if (chat == null || member == null)
            {
                return;
            }

            var flyout = new MenuFlyout();

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

            if (ViewModel.ClientService.CanEditTag(chat, member))
            {
                flyout.CreateFlyoutItem(ViewModel.MembersTab.EditTag, member, member.Status is ChatMemberStatusAdministrator or ChatMemberStatusCreator ? Strings.EditAdminTag : Strings.EditMemberTag, Icons.PersonTag);
            }

            if (chat.Type is ChatTypeSupergroup)
            {
                flyout.CreateFlyoutItem(MemberPromote_Loaded, ViewModel.MembersTab.PromoteMember, chat, status, member, member.Status is ChatMemberStatusAdministrator ? Strings.EditAdminRights : Strings.SetAsAdmin, Icons.ShieldStar);
                flyout.CreateFlyoutItem(MemberRestrict_Loaded, ViewModel.MembersTab.RestrictMember, chat, status, member, member.Status is ChatMemberStatusRestricted ? Strings.ChangePermissions : Strings.KickFromSupergroup, Icons.LockClosed);
            }

            flyout.CreateFlyoutItem(MemberRemove_Loaded, ViewModel.MembersTab.RemoveMember, chat, status, member, chat.Type is ChatTypeSupergroup { IsChannel: true } ? Strings.ChannelRemoveUser : Strings.KickFromGroup, Icons.Block);

            flyout.ShowAt(sender, args);
        }

        private bool MemberPromote_Loaded(Chat chat, ChatMemberStatus status, ChatMember member)
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

        private bool MemberRestrict_Loaded(Chat chat, ChatMemberStatus status, ChatMember member)
        {
            if (member.Status is ChatMemberStatusCreator or ChatMemberStatusAdministrator { CanBeEdited: false })
            {
                return false;
            }

            if (member.MemberId.IsUser(ViewModel.ClientService.Options.MyId))
            {
                return false;
            }

            if (chat.Type is ChatTypeSupergroup supergroup && supergroup.IsChannel)
            {
                return false;
            }

            return status is ChatMemberStatusCreator or ChatMemberStatusAdministrator { Rights.CanRestrictMembers: true };
        }

        private bool MemberRemove_Loaded(Chat chat, ChatMemberStatus status, ChatMember member)
        {
            if (member.Status is ChatMemberStatusCreator or ChatMemberStatusAdministrator { CanBeEdited: false })
            {
                return false;
            }

            if (member.MemberId.IsUser(ViewModel.ClientService.Options.MyId))
            {
                return false;
            }

            if (chat.Type is ChatTypeBasicGroup && status is ChatMemberStatusAdministrator)
            {
                return member.InviterUserId == ViewModel.ClientService.Options.MyId;
            }

            return status is ChatMemberStatusCreator or ChatMemberStatusAdministrator { Rights.CanRestrictMembers: true };
        }

        #endregion

        #region Recycle

        protected override void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContextRequested += Member_ContextRequested;

                if (sender.ItemTemplateSelector == null)
                {
                    args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                }
            }

            if (sender.ItemTemplateSelector != null)
            {
                args.ItemContainer.ContentTemplate = sender.ItemTemplateSelector.SelectTemplate(args.Item, args.ItemContainer);
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            try
            {
                if (args.InRecycleQueue || ViewModel == null)
                {
                    return;
                }
#if LINUX
                // OnChoosingItemContainer above never runs in Uno, so the ContextRequested
                // subscription - the promote / restrict / remove / edit-tag menu - is attached
                // here instead, and the cell is reached through ContentRoot() rather than through
                // ContentTemplateRoot, which is always null. See ProfileTabContainer.
                ProfileTabContainer.Bind<ProfileCell>(sender, args, Member_ContextRequested,
                    (content, item) =>
                    {
                        if (item is ChatMember member)
                        {
                            // The inflated writer: Uno raises this callback once, with Phase 0 and
                            // no second pass, so the phased overload would leave every row with a
                            // name and nothing else.
                            content.UpdateChatSharedMembers(ViewModel.ClientService, member);
                        }
                    });
#else
                else if (args.ItemContainer.ContentTemplateRoot is ProfileCell content)
                {
                    content.UpdateChatSharedMembers(ViewModel.ClientService, args, OnContainerContentChanging);
                }
#endif
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
        }

        #endregion
    }
}
