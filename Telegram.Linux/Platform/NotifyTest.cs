//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: a desktop notification without waiting for someone to write.
    ///
    ///     UNIGRAM_NOTIFY_TEST=&lt;seconds&gt;[:&lt;chat title substring&gt;[:&lt;other chat title substring&gt;]]
    ///
    /// <para>What is simulated is exactly one thing — the <c>UpdateNotificationGroup</c> that TDLib
    /// would push. It is published on the real <see cref="IEventAggregator"/>, so from there on
    /// every step is the shipping code: <c>NotificationsService.Handle</c>, the "is that chat on
    /// screen?" check, the caption and the preview built by <c>ChatCell</c>, the avatar of the real
    /// chat, the launch string, and the D-Bus <c>Notify</c> with its <c>replaces_id</c>. The chats
    /// are real chats of the account, read with <c>GetChats</c>; only the message is made up, and
    /// it is made up rather than taken from the chat so that nothing of the user's own
    /// conversations ends up on a banner or in a log.</para>
    ///
    /// <para>The sequence answers the four questions the desktop cannot be asked from outside
    /// (see PORTING.md §7: in GNOME a screenshot is not available to us, and the banner is drawn by
    /// the compositor anyway):</para>
    /// <list type="number">
    /// <item>a banner for chat A — the server hands out an id, which is proof it took it;</item>
    /// <item>a second message in chat A — same id, so the banner is UPDATED and not piled on;</item>
    /// <item>a message in chat B — a different id, so one banner per chat;</item>
    /// <item>the banner's default action replayed — the window comes up and the chat that opens is
    /// A. This is the one step the notification server would normally start, and it is replayed
    /// here because the match rule of <c>ActionInvoked</c> is bound to the server's name: a signal
    /// faked from another connection is dropped by the bus, so a human tap on the banner is the
    /// only other way in.</item>
    /// </list>
    ///
    /// <para>Last it publishes one more message for chat A, now that A is open, which must NOT
    /// reach the desktop ("Chat is open" in the log): the dedupe of the shared code.</para>
    /// </summary>
    public static class NotifyTest
    {
        // Far away from any group id TDLib hands out, so that closing ours cannot close a real one.
        private const int GroupA = 990001;
        private const int GroupB = 990002;

        private const string TextOne = "Prueba de notificacion 1/2";
        private const string TextTwo = "Prueba de notificacion 2/2";
        private const string TextThree = "Prueba con el chat abierto";

        public static void Schedule(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_NOTIFY_TEST");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var parts = value.Split(':');

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                Logger.Error($"UNIGRAM_NOTIFY_TEST must start with the seconds to wait: \"{value}\"");
                return;
            }

            _ = RunAsync(seconds,
                parts.Length > 1 ? parts[1] : null,
                parts.Length > 2 ? parts[2] : null);
        }

        private static async Task RunAsync(double seconds, string wantA, string wantB)
        {
            await Task.Delay((int)Math.Max(seconds * 1000, 1)).ConfigureAwait(false);

            try
            {
                await RunCoreAsync(wantA, wantB).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("notify test: failed", ex);
            }
        }

        private static async Task RunCoreAsync(string wantA, string wantB)
        {
            var session = LifetimeService.Current?.ActiveItem;
            if (session?.ClientService == null)
            {
                Logger.Error("notify test: no active session");
                return;
            }

            var clientService = session.ClientService;
            var aggregator = session.Aggregator;

            Logger.Info($"notify test: server capabilities [{string.Join(", ", DesktopNotifications.Current.Capabilities)}], "
                + $"tray active {DesktopIntegration.IsTrayActive}, autostart {AutoStart.IsEnabled} at {AutoStart.Path}");

            var response = await clientService.SendAsync(new GetChats(new ChatListMain(), 40));
            if (response is not Td.Api.Chats chats || chats.ChatIds.Count < 1)
            {
                Logger.Error($"notify test: GetChats answered {response}");
                return;
            }

            var chatA = Pick(clientService, session.Id, chats.ChatIds, wantA, 0);
            var chatB = Pick(clientService, session.Id, chats.ChatIds, wantB, chatA?.Id ?? 0);

            if (chatA == null)
            {
                Logger.Error("notify test: no closed chat to notify about");
                return;
            }

            // Ids and types only: the titles are the user's business.
            Logger.Info($"notify test: chat A {chatA.Id} ({chatA.Type?.GetType().Name}), "
                + $"chat B {(chatB == null ? "none" : chatB.Id + " (" + chatB.Type?.GetType().Name + ")")}");

            Publish(aggregator, GroupA, chatA, clientService, 1, TextOne);
            await Task.Delay(2500).ConfigureAwait(false);
            var first = IdOf(session.Id, GroupA);

            Publish(aggregator, GroupA, chatA, clientService, 2, TextTwo);
            await Task.Delay(2500).ConfigureAwait(false);
            var second = IdOf(session.Id, GroupA);

            uint other = 0;
            if (chatB != null)
            {
                Publish(aggregator, GroupB, chatB, clientService, 3, TextOne);
                await Task.Delay(2500).ConfigureAwait(false);
                other = IdOf(session.Id, GroupB);
            }

            Logger.Info($"notify test: ids A1={first} A2={second} B={other} "
                + $"-- shown {(first != 0 ? "OK" : "FAILED")}, "
                + $"replaces {(first != 0 && first == second ? "OK" : "FAILED")}, "
                + $"per-chat {(chatB == null ? "skipped" : other != 0 && other != first ? "OK" : "FAILED")}");

            if (first == 0)
            {
                return;
            }

            // The banner's default action, as if the user had clicked it.
            Logger.Info("notify test: replaying the default action of A");
            DesktopNotifications.Current.ReplayAction(first, "default", null);

            await Task.Delay(4000).ConfigureAwait(false);

            var opened = IsOpen(session.Id, chatA.Id);
            Logger.Info($"notify test: chat {chatA.Id} open after the action: {opened} -- activation {(opened ? "OK" : "FAILED")}");

            // And now the other half of the same contract: no banner for the chat on screen.
            Publish(aggregator, GroupA, chatA, clientService, 4, TextThree);
            await Task.Delay(2500).ConfigureAwait(false);

            var third = IdOf(session.Id, GroupA);
            Logger.Info($"notify test: id A3={third} (unchanged {third == second}); "
                + "look for \"Chat is open\" above to confirm it was suppressed");

            await DesktopNotifications.Current.CloseAsync(Key(session.Id, GroupA)).ConfigureAwait(false);

            if (chatB != null)
            {
                await DesktopNotifications.Current.CloseAsync(Key(session.Id, GroupB)).ConfigureAwait(false);
            }

            Logger.Info("notify test: done");
        }

        /// <summary>
        /// A chat of the account that is not the one on screen (a notification for the open chat is
        /// dropped on purpose, which would make the test measure nothing) and not the one already
        /// used.
        /// </summary>
        private static Chat Pick(IClientService clientService, int sessionId, IReadOnlyList<long> ids, string want, long avoid)
        {
            foreach (var id in ids)
            {
                if (id == avoid || !clientService.TryGetChat(id, out Chat chat))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(want)
                    && chat.Title?.Contains(want, StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                if (IsOpen(sessionId, id))
                {
                    continue;
                }

                return chat;
            }

            return null;
        }

        private static bool IsOpen(int sessionId, long chatId)
        {
            var window = WindowContext.Active ?? WindowContext.Main;
            var service = window?.NavigationServices?.GetByFrameId($"Main{sessionId}");

            return service != null && service.IsChatOpen(chatId, true);
        }

        private static void Publish(IEventAggregator aggregator, int group, Chat chat, IClientService clientService, int index, string text)
        {
            var date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var message = new Message
            {
                Id = (long)(900000 + index) << 20,
                ChatId = chat.Id,
                SenderId = Sender(chat, clientService),
                Date = date,
                IsOutgoing = false,
                Content = new MessageText(new FormattedText(text, Array.Empty<TextEntity>()), null, null)
            };

            var update = new UpdateNotificationGroup
            {
                NotificationGroupId = group,
                Type = new NotificationGroupTypeMessages(),
                ChatId = chat.Id,
                NotificationSettingsChatId = chat.Id,
                NotificationSoundId = -1,
                TotalCount = 1,
                AddedNotifications = new[]
                {
                    new Notification(900000 + index, date, false, new NotificationTypeNewMessage(message, false))
                },
                RemovedNotificationIds = Array.Empty<int>()
            };

            Logger.Info($"notify test: publishing group {group} for chat {chat.Id}");
            aggregator.Publish(update);
        }

        /// <summary>
        /// Someone other than us, so that the preview reads like an incoming message: the other
        /// party in a private chat, the chat itself anywhere else.
        /// </summary>
        private static MessageSender Sender(Chat chat, IClientService clientService)
        {
            if (chat.Type is ChatTypePrivate privata && privata.UserId != clientService.Options.MyId)
            {
                return new MessageSenderUser(privata.UserId);
            }
            else if (chat.Type is ChatTypeSecret secret)
            {
                return new MessageSenderUser(secret.UserId);
            }

            return new MessageSenderChat(chat.Id);
        }

        /// <summary>
        /// The same key <c>NotificationsService.GetNotificationKey</c> builds. Repeated here rather
        /// than exposed, because a diagnostic must not widen the surface of the code it measures.
        /// </summary>
        private static string Key(int sessionId, int group)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}_{1}", sessionId, group);
        }

        private static uint IdOf(int sessionId, int group)
        {
            return DesktopNotifications.Current.TryGetId(Key(sessionId, group), out uint id) ? id : 0;
        }
    }
}
