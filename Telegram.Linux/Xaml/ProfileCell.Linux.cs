//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of Controls/Cells/ProfileCell.xaml.cs.
//
// Every Update* method of ProfileCell that a list calls from ContainerContentChanging is written
// as a three-phase state machine: phase 0 writes the title, phase 1 the subtitle, phase 2 the
// photo and the identity icon, and each phase asks for the next one with
// args.RegisterUpdateCallback(callback).
//
// Uno raises ContainerContentChanging ONCE per container, with Phase always 0 and no second pass
// - measured by the chat-list batch and recorded in Controls/ChatListListView.cs
// ("Uno raises this once, with no phases, and from PrepareContainerForIndex"). Registering an
// update callback there does not throw and does not warn; the callback simply never runs. The
// visible result of leaving the phased path alone would be every row of every profile tab drawn
// with its title and NOTHING else: no "last seen", no admin tag, no avatar - and no error
// anywhere, because phase 0 did succeed.
//
// So the Linux path calls an inflated version instead: the same writes as phases 0, 1 and 2 of the
// upstream method, in the same order, with the phase branching removed. They are kept here rather
// than #if'd into the shared file so that the upstream state machine stays readable and so that a
// future Uno that does raise phases needs one call site changed, not three methods rewritten.
//
// The one member method upstream already ships inflated (UpdateChatSharedMembers(IClientService,
// ChatMember), used by SupergroupMembersPage to refresh a single row) is reused as is.


using System.Text;
using Telegram.Common;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;

namespace Telegram.Controls.Cells
{
    public sealed partial class ProfileCell
    {
        /// <summary>
        /// Phases 0 to 2 of the phased <c>UpdateChat</c> in one pass. Used by the groups-in-common
        /// tab.
        /// </summary>
        public void UpdateChatInflated(IClientService clientService, Chat chat)
        {
            if (chat == null)
            {
                return;
            }

            UpdateStyleNoSubtitle();

            Tag = chat;

            TitleLabel.Text = chat.Title;

            Photo.Source = ProfilePictureSource.Chat(clientService, chat);
            Identity.SetStatus(clientService, chat, BotVerified);
        }

        /// <summary>
        /// Phases 0 to 2 of <c>UpdateSimilarChannel</c> in one pass.
        /// </summary>
        public void UpdateSimilarChannelInflated(IClientService clientService, Chat chat)
        {
            if (chat == null)
            {
                return;
            }

            TitleLabel.Text = chat.Title;

            if (clientService.TryGetSupergroup(chat, out Supergroup supergroup))
            {
                SubtitleLabel.Text = Locale.Declension(Strings.R.Subscribers, supergroup.MemberCount);
            }

            Photo.Source = ProfilePictureSource.Chat(clientService, chat);
            Identity.SetStatus(clientService, chat, BotVerified);
        }

        /// <summary>
        /// Phases 0 to 2 of <c>UpdateSimilarBot</c> in one pass.
        /// </summary>
        public void UpdateSimilarBotInflated(IClientService clientService, User user)
        {
            if (user == null)
            {
                return;
            }

            TitleLabel.Text = user.FullName();

            if (user.Type is UserTypeBot typeBot)
            {
                SubtitleLabel.Text = typeBot.ActiveUserCount > 0
                    ? Locale.Declension(Strings.R.BotDAU, typeBot.ActiveUserCount)
                    : Strings.Bot;
            }

            Photo.Source = ProfilePictureSource.User(clientService, user);
            Identity.SetStatus(clientService, user, BotVerified);
        }

        /// <summary>
        /// Phases 0 to 2 of the phased <c>UpdateUser</c> in one pass. Used by the contacts list
        /// (<c>Views/Popups/ContactsPopup</c>, u-056).
        /// </summary>
        public void UpdateUserInflated(IClientService clientService, User user)
        {
            if (user == null)
            {
                return;
            }

            TitleLabel.Text = user.FullName();

            SubtitleLabel.Text = LastSeenConverter.GetLabel(user, false);
            SubtitleLabel.Style = BootStrapper.Current.Resources[user.Status is UserStatusOnline ? "AccentCaptionTextBlockStyle" : "InfoCaptionTextBlockStyle"] as Microsoft.UI.Xaml.Style;

            Photo.Source = ProfilePictureSource.User(clientService, user);
            Identity.SetStatus(clientService, user, BotVerified);
        }

        /// <summary>
        /// Phases 0 to 2 of the phased <c>UpdateSearchResult</c> in one pass. Used by the global
        /// search results list (<c>Controls/Views/SearchChatsView</c>).
        /// </summary>
        /// <remarks>
        /// Unlike the other inflated methods here this one does not repeat the bodies: the three
        /// phases of <c>UpdateSearchResult</c> were lifted into
        /// <c>UpdateSearchResultPhase0/1/2</c> in the shared file, so both platforms run exactly
        /// the same code and only the order of the calls differs.
        /// </remarks>
        public void UpdateSearchResultInflated(IClientService clientService, SearchResult result)
        {
            if (result == null)
            {
                return;
            }

            UpdateSearchResultPhase0(clientService, result);
            UpdateSearchResultPhase1(clientService, result);
            UpdateSearchResultPhase2(clientService, result);
        }

        /// <summary>
        /// Phases 0 to 2 of the phased <c>UpdateMessageSender</c> in one pass. Used by the blocked
        /// users list (<c>Views/Settings/SettingsBlockedChatsPage</c>).
        /// </summary>
        /// <remarks>
        /// Phase 1 of the upstream method is commented out there (the creator / administrator tag
        /// is not shown on this list), so this is phases 0 and 2: the name, then the avatar and the
        /// identity icon. Without it a blocked row would show the name and no avatar at all, which
        /// on a list whose only content IS name plus avatar is most of the row.
        /// </remarks>
        public void UpdateMessageSenderInflated(IClientService clientService, MessageSender member)
        {
            var messageSender = clientService.GetMessageSender(member);
            if (messageSender == null)
            {
                return;
            }

            UpdateStyleNoSubtitle();

            Tag = member;

            if (messageSender is User user)
            {
                TitleLabel.Text = user.FullName();

                Photo.Source = ProfilePictureSource.User(clientService, user);
                Identity.SetStatus(clientService, user, BotVerified);
            }
            else if (messageSender is Chat chat)
            {
                TitleLabel.Text = chat.Title;

                Photo.Source = ProfilePictureSource.Chat(clientService, chat);
                Identity.SetStatus(clientService, chat, BotVerified);
            }
        }

        /// <summary>
        /// Phases 0 to 2 of the phased <c>UpdateNotificationException</c> in one pass. Used by the
        /// per-chat notification exceptions list.
        /// </summary>
        /// <remarks>
        /// Phase 1 is the whole point of that screen - it is the line that says whether the chat is
        /// muted, and whether it overrides the preview or the sound - so losing it to the missing
        /// second phase would leave a list of chat names with nothing to say why they are on it.
        /// </remarks>
        public void UpdateNotificationExceptionInflated(IClientService clientService, Chat chat)
        {
            if (chat == null)
            {
                return;
            }

            TitleLabel.Text = clientService.GetTitle(chat);

            var value = clientService.Notifications.GetMuteFor(chat);
            if (value == 0)
            {
                var builder = new StringBuilder(Strings.NotificationExceptionsAlwaysOn);

                if (!chat.NotificationSettings.UseDefaultShowPreview)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(chat.NotificationSettings.ShowPreview
                        ? Strings.NotificationExceptionsPreviewShow
                        : Strings.NotificationExceptionsPreviewHide);
                }

                if (!chat.NotificationSettings.UseDefaultSound)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(Strings.NotificationExceptionsSoundCustom);
                }

                SubtitleLabel.Text = builder.ToString();
            }
            else
            {
                SubtitleLabel.Text = Strings.NotificationExceptionsAlwaysOff;
            }

            Photo.Source = ProfilePictureSource.Chat(clientService, chat);
            Identity.SetStatus(clientService, chat, BotVerified);
        }

        /// <summary>
        /// Phases 0 to 2 of the phased <c>UpdateStatisticsByChat</c> in one pass. Used by the
        /// storage usage list.
        /// </summary>
        /// <remarks>
        /// Phase 1 here is the SIZE - the only number that makes this list worth opening - so the
        /// phased path would draw a list of chat names with no sizes next to them, which reads
        /// exactly like a screen that failed to load.
        /// </remarks>
        public void UpdateStatisticsByChatInflated(IClientService clientService, StorageStatisticsByChat statistics)
        {
            if (statistics == null)
            {
                return;
            }

            var chat = clientService.GetChat(statistics.ChatId);

            TitleLabel.Text = chat == null ? "Other Chats" : clientService.GetTitle(chat);
            SubtitleLabel.Text = FileSizeConverter.Convert(statistics.Size, true);

            if (chat == null)
            {
                Photo.Source = null;
                Photo.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                Identity.ClearStatus(BotVerified);
            }
            else
            {
                Photo.Source = ProfilePictureSource.Chat(clientService, chat);
                Photo.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                Identity.SetStatus(clientService, chat, BotVerified);
            }
        }
    }
}
