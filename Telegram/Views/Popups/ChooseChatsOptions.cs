//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Split out of Views/Popups/ChooseChatsPopup.xaml.cs WITHOUT changing a line of code, the same way
// ViewModels/Business/BusinessHours.cs was split off for ProfileHoursCell (Regla 8).
//
// SearchChatsViewModel, SearchChannelsViewModel and SearchWebAppsViewModel each hold a
// ChooseChatsTracker whose Options are a ChooseChatsOptions, so the global search cannot compile
// without these two types - but ChooseChatsPopup.xaml.cs itself is 2.310 lines that drag the whole
// stories subsystem, which extra-files.txt deliberately keeps out.
//
// Nothing here touches a Windows API: it is TDLib data and booleans.

using Telegram.Services;
using Telegram.Td.Api;

namespace Telegram.Views.Popups
{
    public enum ChooseChatsMode
    {
        Chats,
        Contacts
    }

    public record ChooseChatsOptions
    {
        public bool AllowAll => AllowChannelChats && AllowGroupChats && AllowBotChats && AllowUserChats && AllowSecretChats && AllowSelf && !CanPostMessages && !CanInviteUsers && !CanShareContact;

        public bool AllowChannelChats { get; set; } = true;
        public bool AllowGroupChats { get; set; } = true;
        public bool AllowBotChats { get; set; } = true;
        public bool AllowUserChats { get; set; } = true;
        public bool AllowSecretChats { get; set; } = true;

        public bool AllowSelf { get; set; } = true;

        public bool CanPostMessages { get; set; } = false;
        public bool CanInviteUsers { get; set; } = false;
        public bool CanShareContact { get; set; } = false;
        public bool CanPromoteMembers { get; set; } = false;

        public ChooseChatsMode Mode { get; set; } = ChooseChatsMode.Chats;

        public bool ShowMessages { get; set; } = false;

        #region Predefined

        public static readonly ChooseChatsOptions All = new()
        {
            AllowChannelChats = true,
            AllowGroupChats = true,
            AllowBotChats = true,
            AllowUserChats = true,
            AllowSecretChats = true,
            AllowSelf = true,
            CanPostMessages = false,
            CanInviteUsers = false,
            CanShareContact = false,
            Mode = ChooseChatsMode.Chats,
            ShowMessages = true
        };

        public static readonly ChooseChatsOptions ChannelsCanPromoteMembers = new()
        {
            AllowChannelChats = true,
            AllowGroupChats = false,
            AllowBotChats = false,
            AllowUserChats = false,
            AllowSecretChats = false,
            AllowSelf = false,
            CanPostMessages = false,
            CanInviteUsers = false,
            CanPromoteMembers = true,
            CanShareContact = false,
            Mode = ChooseChatsMode.Chats,
            ShowMessages = false
        };

        public static readonly ChooseChatsOptions GroupsAndChannels = new()
        {
            AllowChannelChats = true,
            AllowGroupChats = true,
            AllowBotChats = false,
            AllowUserChats = false,
            AllowSecretChats = false,
            AllowSelf = false,
            CanPostMessages = true,
            CanInviteUsers = false,
            CanShareContact = false,
            Mode = ChooseChatsMode.Chats,
            ShowMessages = false
        };

        public static readonly ChooseChatsOptions UsersAndChannels = new()
        {
            AllowChannelChats = true,
            AllowGroupChats = false,
            AllowBotChats = false,
            AllowUserChats = true,
            AllowSecretChats = false,
            AllowSelf = true,
            CanPostMessages = true,
            CanInviteUsers = false,
            CanShareContact = false,
            Mode = ChooseChatsMode.Chats,
            ShowMessages = false
        };

        public static readonly ChooseChatsOptions Contacts = new()
        {
            AllowChannelChats = false,
            AllowGroupChats = false,
            AllowBotChats = false,
            AllowUserChats = true,
            AllowSecretChats = false,
            AllowSelf = false,
            CanPostMessages = false,
            CanInviteUsers = false,
            CanShareContact = false,
            Mode = ChooseChatsMode.Contacts,
            ShowMessages = false
        };

        public static readonly ChooseChatsOptions ContactsOnly = new()
        {
            AllowChannelChats = false,
            AllowGroupChats = false,
            AllowBotChats = false,
            AllowUserChats = true,
            AllowSecretChats = false,
            AllowSelf = false,
            CanPostMessages = false,
            CanInviteUsers = false,
            CanShareContact = true,
            Mode = ChooseChatsMode.Contacts,
            ShowMessages = false
        };

        public static readonly ChooseChatsOptions Users = new()
        {
            AllowChannelChats = false,
            AllowGroupChats = false,
            AllowBotChats = true,
            AllowUserChats = true,
            AllowSecretChats = false,
            AllowSelf = false,
            CanPostMessages = false,
            CanInviteUsers = false,
            CanShareContact = false,
            Mode = ChooseChatsMode.Chats,
            ShowMessages = false
        };

        public static readonly ChooseChatsOptions PostMessages = new()
        {
            AllowChannelChats = true,
            AllowGroupChats = true,
            AllowBotChats = true,
            AllowUserChats = true,
            AllowSecretChats = true,
            AllowSelf = true,
            CanPostMessages = true,
            CanInviteUsers = false,
            CanShareContact = false,
            Mode = ChooseChatsMode.Chats,
            ShowMessages = false
        };

        public static readonly ChooseChatsOptions InviteUsers = new()
        {
            AllowChannelChats = false,
            AllowGroupChats = false,
            AllowBotChats = true,
            AllowUserChats = true,
            AllowSecretChats = false,
            AllowSelf = false,
            CanPostMessages = false,
            CanInviteUsers = true,
            CanShareContact = false,
            Mode = ChooseChatsMode.Chats,
            ShowMessages = false
        };

        public static readonly ChooseChatsOptions Privacy = new()
        {
            AllowChannelChats = false,
            AllowGroupChats = true,
            AllowBotChats = true,
            AllowUserChats = true,
            AllowSecretChats = false,
            AllowSelf = false,
            CanPostMessages = false,
            CanInviteUsers = false,
            CanShareContact = false,
            Mode = ChooseChatsMode.Contacts,
            ShowMessages = false
        };

        #endregion

        public virtual bool Allow(IClientService clientService, Chat chat)
        {
            if (AllowAll)
            {
                return true;
            }

            switch (chat.Type)
            {
                case ChatTypeBasicGroup:
                    if (AllowGroupChats)
                    {
                        if (CanPostMessages)
                        {
                            return clientService.CanPostMessages(chat);
                        }
                        else if (CanInviteUsers)
                        {
                            return clientService.CanInviteUsers(chat);
                        }
                        else if (CanPromoteMembers)
                        {
                            return clientService.CanPromoteMembers(chat);
                        }

                        return true;
                    }
                    return false;
                case ChatTypePrivate privata:
                    if (privata.UserId == clientService.Options.MyId)
                    {
                        return AllowSelf;
                    }
                    else if (clientService.TryGetUser(privata.UserId, out User user))
                    {
                        if (user.Type is UserTypeDeleted)
                        {
                            return false;
                        }
                        else if (user.Type is UserTypeBot)
                        {
                            return AllowBotChats
                                && !CanShareContact
                                && chat.Id != clientService.Options.RepliesBotChatId
                                && chat.Id != clientService.Options.VerificationCodesBotChatId;
                        }
                        else if (CanShareContact)
                        {
                            return user.PhoneNumber.Length > 0;
                        }
                    }
                    return AllowUserChats;
                case ChatTypeSecret:
                    return AllowSecretChats;
                case ChatTypeSupergroup supergroup:
                    if ((supergroup.IsChannel ? AllowChannelChats : AllowGroupChats) && clientService.TryGetSupergroup(supergroup.SupergroupId, out Supergroup super))
                    {
                        if (super.IsDirectMessagesGroup && !CanPostMessages)
                        {
                            return false;
                        }

                        if (CanPostMessages)
                        {
                            return super.CanPostMessages();
                        }
                        else if (CanInviteUsers)
                        {
                            return super.CanInviteUsers(chat);
                        }
                        else if (CanPromoteMembers)
                        {
                            return super.CanPromoteMembers();
                        }

                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        public virtual bool Allow(IClientService clientService, User user)
        {
            if (AllowAll)
            {
                return true;
            }

            if (user.Id == clientService.Options.MyId)
            {
                return AllowSelf;
            }
            else if (user.Type is UserTypeBot)
            {
                return AllowBotChats && !CanShareContact;
            }
            else if (CanShareContact)
            {
                return user.PhoneNumber.Length > 0;
            }

            return AllowUserChats;
        }
    }

}
