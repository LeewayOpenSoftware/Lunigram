//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;

namespace Telegram.Common
{
    /// <summary>
    /// Drives the LIVE signals - the ones that are supposed to change while nobody touches the
    /// screen - without needing a second person on the other end.
    ///
    ///     UNIGRAM_LIVE_TEST=&lt;seconds&gt;[:&lt;step seconds&gt;]
    ///
    /// <para><b>Why it has to exist.</b> "Online", "last seen", "typing...", "recording audio..."
    /// are all somebody else's doing: the only way to see them arrive is for another account to
    /// come online or start typing. That makes the one property that matters - does the control
    /// repaint when the update lands, or only when the screen is opened - the one property nobody
    /// can measure alone. Half the audit of this area is written as "not measured" for exactly that
    /// reason.</para>
    ///
    /// <para><b>What it does instead.</b> It hands <c>ClientService.OnResult</c> - the very
    /// method the TDLib receiver thread calls, and the only entry point there is - a
    /// <c>updateUserStatus</c> or a <c>updateChatAction</c> naming a real chat of the account.
    /// Everything from that call onwards is shipping code: the caches
    /// (<c>User.Status</c>, <c>_chatActions</c>), the unfiltered <c>_aggregator.Publish</c>, every
    /// <c>Subscribe&lt;T&gt;</c> handler, and whatever the handler then does to the control. So it
    /// answers the question the audit could not: an update that arrives while the window sits
    /// still, and a screenshot on either side of it.</para>
    ///
    /// <para><b>What it is not.</b> It sends NOTHING. No TDLib function is called except the read
    /// only <c>GetChats</c> used to find a chat, no message is written, no chat is created or
    /// deleted, and nobody on the other end sees anything. The states it writes into the local
    /// cache are overwritten by the server's own next update for that user - which for a status is
    /// at most a few minutes away, and immediately on the next reconnect. It logs ids and types,
    /// never titles or message text.</para>
    /// </summary>
    public static class LiveTest
    {
        public static void Schedule(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_LIVE_TEST");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var parts = value.Split(':');

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                Logger.Error($"live test: UNIGRAM_LIVE_TEST must start with the seconds to wait: \"{value}\"");
                return;
            }

            var step = 8d;

            if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                step = parsed;
            }

            _ = RunAsync(seconds, step);
        }

        private static async Task RunAsync(double seconds, double step)
        {
            await Task.Delay((int)Math.Max(seconds * 1000, 1)).ConfigureAwait(false);

            try
            {
                await RunCoreAsync(step).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("live test: failed", ex);
            }
        }

        private static async Task RunCoreAsync(double step)
        {
            var session = LifetimeService.Current?.ActiveItem;
            if (session?.ClientService == null)
            {
                Logger.Error("live test: no active session");
                return;
            }

            var clientService = session.ClientService;
            var delay = (int)Math.Max(step * 1000, 500);

            var response = await clientService.SendAsync(new GetChats(new ChatListMain(), 60));
            if (response is not Td.Api.Chats chats || chats.ChatIds.Count < 1)
            {
                Logger.Error($"live test: GetChats answered {response}");
                return;
            }

            long chatId = 0;
            long userId = 0;

            foreach (var id in chats.ChatIds)
            {
                var chat = clientService.GetChat(id);
                if (chat?.Type is ChatTypePrivate privata
                    && privata.UserId != clientService.Options.MyId
                    && privata.UserId != 777000)
                {
                    chatId = id;
                    userId = privata.UserId;
                    break;
                }
            }

            if (chatId == 0)
            {
                Logger.Error("live test: no private chat in the main list to drive");
                return;
            }

            if (clientService is not Td.ClientResultHandler handler)
            {
                Logger.Error("live test: the client service is not a ClientResultHandler");
                return;
            }

            var before = clientService.GetUser(userId)?.Status?.GetType().Name;
            Logger.Info($"live test: driving chat {chatId}, user {userId}, status now {before ?? "unknown"}, step {delay} ms");

            var sender = new MessageSenderUser(userId);

            // 1. online. The chat list should grow a green dot, the chat header should read online.
            Logger.Info("live test: step 1 - updateUserStatus online");
            handler.OnResult(new UpdateUserStatus(userId, new UserStatusOnline((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300)));
            await Task.Delay(delay).ConfigureAwait(false);

            // 2. typing. Both the row and the header should swap their subtitle for the action.
            Logger.Info("live test: step 2 - updateChatAction typing");
            handler.OnResult(new UpdateChatAction(chatId, null, sender, new ChatActionTyping()));
            await Task.Delay(delay).ConfigureAwait(false);

            // 3. recording a voice note, to prove the string changes and not just appears.
            Logger.Info("live test: step 3 - updateChatAction recording voice note");
            handler.OnResult(new UpdateChatAction(chatId, null, sender, new ChatActionRecordingVoiceNote()));
            await Task.Delay(delay).ConfigureAwait(false);

            // 4. cancel. Row and header must go back to the subtitle, not keep the last action.
            Logger.Info("live test: step 4 - updateChatAction cancel");
            handler.OnResult(new UpdateChatAction(chatId, null, sender, new ChatActionCancel()));
            await Task.Delay(delay).ConfigureAwait(false);

            // 5. offline. This is the one that used to leave the green dot's Border behind.
            Logger.Info("live test: step 5 - updateUserStatus offline");
            handler.OnResult(new UpdateUserStatus(userId, new UserStatusOffline((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120)));
            await Task.Delay(delay).ConfigureAwait(false);

            Logger.Info("live test: done");
        }
    }
}
