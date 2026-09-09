//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using IOFile = System.IO.File;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: builds the throwaway test bank the action passes need, and
    /// writes down the chat ids it created.
    ///
    ///     UNIGRAM_BANK=&lt;seconds&gt;:&lt;json path&gt;[:&lt;media folder&gt;]
    ///
    /// <para>It exists because the port has NO shipping UI for creating a group or a channel:
    /// <c>Views/Create/NewGroupPopup</c> and <c>NewChannelPopup</c> are outside the Linux subset,
    /// and the two <c>RootDestination</c> branches that would open them in
    /// <c>MainPage.xaml.cs</c> sit under <c>#if !LINUX</c>. So the three chats are created by the
    /// same TDLib functions those view models call — <c>CreateNewBasicGroupChat</c> and
    /// <c>CreateNewSupergroupChat</c> — on the real <c>IClientService</c> of the active session.
    /// Everything downstream (the update pipeline, the caches, the chat list) is shipping code.</para>
    ///
    /// <para>This helper only ever CREATES and SENDS. It never deletes and never touches a chat it
    /// did not just create: every id it prints came out of the response to its own create call.
    /// The files it uploads are the ones in the media folder, which the harness generates; nothing
    /// is forwarded from an existing conversation.</para>
    /// </summary>
    public static class BankSetup
    {
        private const string TitleGroup = "[TEST] Unigram Linux — grupo";
        private const string TitleSupergroup = "[TEST] Unigram Linux — supergrupo";
        private const string TitleChannel = "[TEST] Unigram Linux — canal";

        private const string Description = "Banco de pruebas del port a Linux. Se borra al terminar.";

        private sealed class Entry
        {
            public string Key;
            public string Title;
            public long ChatId;
            public long TypeId;      // basic_group_id or supergroup_id
            public string Kind;      // basicGroup / supergroup / channel
            public string Status;
            public readonly List<string> Sent = new();
        }

        #region Teardown

        private const string Prefix = "[TEST] Unigram Linux";

        /// <summary>
        /// Deletes the throwaway bank again, and only it.
        ///
        ///     UNIGRAM_BANK_TEARDOWN=&lt;seconds&gt;:&lt;json path&gt;[:&lt;keys to skip, comma separated&gt;]
        ///
        /// <para>The whitelist is the <c>chat_id</c> list inside the json the create pass wrote,
        /// and it is compared LITERALLY: an id that is not one of those numbers is never passed to
        /// <c>DeleteChat</c>, whatever it is called. Two names can look alike, two ids cannot. On
        /// top of the id check the chat is fetched first and its title has to still start with the
        /// bank prefix, so a recycled id cannot be hit either.</para>
        ///
        /// <para><c>DeleteChat</c> and not <c>LeaveChat</c>: leaving a supergroup you created
        /// leaves the supergroup itself behind, with nobody in it and no way back in to delete it.
        /// <c>DeleteChat</c> on a chat whose <c>can_be_deleted_for_all_users</c> is true removes
        /// the supergroup for everyone, which is what "no orphans" means.</para>
        ///
        /// <para>With no path it does nothing. It never creates and never sends.</para>
        /// </summary>
        public static void ScheduleTeardown(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_BANK_TEARDOWN");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var parts = value.Split(':');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || parts.Length < 2 || string.IsNullOrEmpty(parts[1]))
            {
                Logger.Error($"teardown: UNIGRAM_BANK_TEARDOWN must be <seconds>:<json>[:<skip>]: \"{value}\"");
                return;
            }

            var skip = parts.Length > 2 ? parts[2] : string.Empty;

            _ = TeardownAsync(seconds, parts[1], skip);
        }

        private static async Task TeardownAsync(double seconds, string json, string skip)
        {
            await Task.Delay((int)Math.Max(seconds * 1000, 1)).ConfigureAwait(false);

            try
            {
                await TeardownCoreAsync(json, skip).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("teardown: failed", ex);
            }
        }

        private static async Task TeardownCoreAsync(string json, string skip)
        {
            var session = LifetimeService.Current?.ActiveItem;
            if (session?.ClientService == null)
            {
                Logger.Error("teardown: no active session");
                return;
            }

            var clientService = session.ClientService;

            if (!IOFile.Exists(json))
            {
                Logger.Error($"teardown: whitelist \"{json}\" not found, refusing to delete anything");
                return;
            }

            // The whitelist, parsed by hand out of the json the create pass wrote: every
            // "chat_id": <number> that sits next to a "titulo" carrying the prefix.
            var text = IOFile.ReadAllText(json);
            var allowed = new List<long>();
            var titles = new Dictionary<long, string>();
            var keys = new Dictionary<long, string>();

            foreach (var block in text.Split('{'))
            {
                var id = ReadLong(block, "\"chat_id\":");
                if (id == 0)
                {
                    continue;
                }

                var title = ReadString(block, "\"titulo\":");
                var key = ReadString(block, "\"clave\":");

                if (title == null || !title.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    Logger.Error($"teardown: {id} in the whitelist does not carry the prefix, ignored");
                    continue;
                }

                allowed.Add(id);
                titles[id] = title;
                keys[id] = key ?? "?";
            }

            if (allowed.Count == 0)
            {
                Logger.Error("teardown: whitelist is empty, nothing to do");
                return;
            }

            Logger.Info($"teardown: whitelist is [{string.Join(", ", allowed)}] from {json}");

            var skipped = new List<string>(skip.Split(',', StringSplitOptions.RemoveEmptyEntries));

            foreach (var id in allowed)
            {
                if (skipped.Contains(keys[id]))
                {
                    Logger.Info($"teardown: {id} ({keys[id]}) skipped on request");
                    continue;
                }

                // Check by id, and only then look at anything else.
                if (!allowed.Contains(id))
                {
                    Logger.Error($"teardown: {id} is not in the whitelist, REFUSED");
                    continue;
                }

                var chat = await clientService.SendAsync(new GetChat(id)).ConfigureAwait(false) as Chat;
                if (chat == null)
                {
                    Logger.Info($"teardown: {id} ({keys[id]}) getChat answered nothing, already gone");
                    continue;
                }

                if (chat.Id != id || !chat.Title.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    Logger.Error($"teardown: {id} came back as chat {chat.Id} with a title that is not the bank's, REFUSED");
                    continue;
                }

                Logger.Info($"teardown: {id} ({keys[id]}) type {chat.Type?.GetType().Name} "
                    + $"can_be_deleted_for_all_users {chat.CanBeDeletedForAllUsers} -> deleteChat");

                var response = await clientService.SendAsync(new DeleteChat(id)).ConfigureAwait(false);
                if (response is Error error)
                {
                    Logger.Error($"teardown: deleteChat({id}) failed: {error.Code} {error.Message}");
                    continue;
                }

                Logger.Info($"teardown: deleteChat({id}) answered {response?.GetType().Name}");
            }

            await Task.Delay(2000).ConfigureAwait(false);
            await VerifyAsync(clientService, allowed, titles).ConfigureAwait(false);
        }

        /// <summary>
        /// The "nothing left" pass: what TDLib still knows about each id, and a sweep of the main
        /// and archived lists for anything still carrying the prefix. Ids only, never contents.
        /// </summary>
        private static async Task VerifyAsync(IClientService clientService, List<long> allowed, Dictionary<long, string> titles)
        {
            foreach (var id in allowed)
            {
                var chat = await clientService.SendAsync(new GetChat(id)).ConfigureAwait(false);

                if (chat is Error error)
                {
                    Logger.Info($"teardown: verify {id} getChat -> error {error.Code} {error.Message} (gone)");
                    continue;
                }

                if (chat is Chat still)
                {
                    var status = "?";
                    if (still.Type is ChatTypeSupergroup super)
                    {
                        var group = await clientService.SendAsync(new GetSupergroup(super.SupergroupId)).ConfigureAwait(false) as Supergroup;
                        status = $"supergroup {super.SupergroupId} status {group?.Status?.GetType().Name} members {group?.MemberCount}";
                    }
                    else if (still.Type is ChatTypeBasicGroup basic)
                    {
                        var group = await clientService.SendAsync(new GetBasicGroup(basic.BasicGroupId)).ConfigureAwait(false) as BasicGroup;
                        status = $"basicGroup {basic.BasicGroupId} status {group?.Status?.GetType().Name} members {group?.MemberCount}";
                    }

                    Logger.Info($"teardown: verify {id} still cached, positions {still.Positions?.Count ?? 0}, {status}");
                }
            }

            await SweepAsync(clientService, new ChatListMain(), "main").ConfigureAwait(false);
            await SweepAsync(clientService, new ChatListArchive(), "archive").ConfigureAwait(false);
        }

        private static async Task SweepAsync(IClientService clientService, ChatList list, string name)
        {
            var response = await clientService.SendAsync(new LoadChats(list, 500)).ConfigureAwait(false);
            Logger.Info($"teardown: loadChats({name}) answered {response?.GetType().Name}");

            await Task.Delay(1500).ConfigureAwait(false);

            var chats = await clientService.SendAsync(new GetChats(list, 500)).ConfigureAwait(false) as Telegram.Td.Api.Chats;
            if (chats == null)
            {
                Logger.Error($"teardown: sweep {name} could not list");
                return;
            }

            var hits = new List<long>();

            foreach (var id in chats.ChatIds)
            {
                var chat = clientService.GetChat(id);
                if (chat != null && chat.Title != null && chat.Title.Contains(Prefix, StringComparison.Ordinal))
                {
                    hits.Add(id);
                }
            }

            Logger.Info($"teardown: sweep {name} over {chats.ChatIds.Count} chats -> "
                + (hits.Count == 0 ? "NOTHING carries the prefix" : $"STILL THERE: [{string.Join(", ", hits)}]"));
        }

        private static long ReadLong(string block, string key)
        {
            var at = block.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return 0;
            }

            var from = at + key.Length;
            var to = from;

            while (to < block.Length && (char.IsDigit(block[to]) || block[to] == '-' || block[to] == ' '))
            {
                to++;
            }

            return long.TryParse(block[from..to].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : 0;
        }

        private static string ReadString(string block, string key)
        {
            var at = block.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            var open = block.IndexOf('"', at + key.Length);
            if (open < 0)
            {
                return null;
            }

            var close = block.IndexOf('"', open + 1);
            return close < 0 ? null : block[(open + 1)..close];
        }

        #endregion

        public static void Schedule(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_BANK");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var parts = value.Split(':');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                Logger.Error($"bank: UNIGRAM_BANK must start with the seconds to wait: \"{value}\"");
                return;
            }

            var json = parts.Length > 1 ? parts[1] : null;
            var media = parts.Length > 2 ? parts[2] : null;

            if (string.IsNullOrEmpty(json))
            {
                Logger.Error("bank: UNIGRAM_BANK needs a path to write the ids to");
                return;
            }

            _ = RunAsync(seconds, json, media);
        }

        private static async Task RunAsync(double seconds, string json, string media)
        {
            await Task.Delay((int)Math.Max(seconds * 1000, 1)).ConfigureAwait(false);

            try
            {
                await RunCoreAsync(json, media).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("bank: failed", ex);
            }
        }

        private static async Task RunCoreAsync(string json, string media)
        {
            var session = LifetimeService.Current?.ActiveItem;
            if (session?.ClientService == null)
            {
                Logger.Error("bank: no active session");
                return;
            }

            var clientService = session.ClientService;
            var aggregator = session.Aggregator;

            var me = clientService.Options.MyId;
            Logger.Info($"bank: start, my id {me}");

            // The update side of the evidence: TDLib pushes updateNewChat for every chat it
            // creates, and updateMessageSendSucceeded once the server has taken a message.
            var subscriber = new object();
            var newChats = new List<long>();
            var succeeded = new List<string>();
            var failed = new List<string>();

            aggregator.Subscribe<UpdateNewChat>(subscriber, update =>
            {
                lock (newChats)
                {
                    newChats.Add(update.Chat.Id);
                }
            });

            aggregator.Subscribe<UpdateMessageSendSucceeded>(subscriber, update =>
            {
                lock (succeeded)
                {
                    succeeded.Add($"{update.Message.ChatId}/{update.OldMessageId}->{update.Message.Id}");
                }
            });

            aggregator.Subscribe<UpdateMessageSendFailed>(subscriber, update =>
            {
                lock (failed)
                {
                    failed.Add($"{update.Message.ChatId}/{update.OldMessageId} {update.Error?.Code} {update.Error?.Message}");
                }
            });

            var entries = new List<Entry>();

            try
            {
                entries.Add(await CreateBasicGroupAsync(clientService).ConfigureAwait(false));
                entries.Add(await CreateSupergroupAsync(clientService, "supergrupo", TitleSupergroup, false).ConfigureAwait(false));
                entries.Add(await CreateSupergroupAsync(clientService, "canal", TitleChannel, true).ConfigureAwait(false));

                // Give updateNewChat time to land before it is used as proof.
                await Task.Delay(1500).ConfigureAwait(false);

                lock (newChats)
                {
                    foreach (var entry in entries)
                    {
                        if (entry.ChatId == 0)
                        {
                            continue;
                        }

                        var pushed = newChats.Contains(entry.ChatId);
                        var cached = clientService.GetChat(entry.ChatId);
                        Logger.Info($"bank: {entry.Key} {entry.ChatId} updateNewChat {(pushed ? "seen" : "NOT seen")}, "
                            + $"cached {(cached != null ? "yes" : "NO")}, title matches {(cached != null && cached.Title == entry.Title ? "yes" : "NO")}");
                    }
                }

                if (!string.IsNullOrEmpty(media) && Directory.Exists(media))
                {
                    foreach (var entry in entries)
                    {
                        if (entry.ChatId != 0)
                        {
                            await FillAsync(clientService, entry, media).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    Logger.Error($"bank: media folder \"{media}\" not found, nothing uploaded");
                }

                // The send updates arrive after the upload, which is the slow part.
                for (int i = 0; i < 60; i++)
                {
                    int done;
                    lock (succeeded)
                    {
                        done = succeeded.Count;
                    }

                    int expected = 0;
                    foreach (var entry in entries)
                    {
                        expected += entry.Sent.Count;
                    }

                    if (done >= expected && expected > 0)
                    {
                        break;
                    }

                    await Task.Delay(1000).ConfigureAwait(false);
                }

                lock (succeeded)
                {
                    Logger.Info($"bank: updateMessageSendSucceeded x{succeeded.Count} [{string.Join(", ", succeeded)}]");
                }

                lock (failed)
                {
                    if (failed.Count > 0)
                    {
                        Logger.Error($"bank: updateMessageSendFailed x{failed.Count} [{string.Join(", ", failed)}]");
                    }
                }

                foreach (var entry in entries)
                {
                    if (entry.ChatId != 0)
                    {
                        await CountAsync(clientService, entry).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                aggregator.Unsubscribe(subscriber);
                Write(json, entries, me);
            }

            Logger.Info("bank: done");
        }

        private static async Task<Entry> CreateBasicGroupAsync(IClientService clientService)
        {
            var entry = new Entry { Key = "grupo", Title = TitleGroup, Kind = "basicGroup" };

            var response = await clientService.SendAsync(new CreateNewBasicGroupChat(Array.Empty<long>(), TitleGroup, 0)).ConfigureAwait(false);
            Logger.Info($"bank: CreateNewBasicGroupChat answered {response?.GetType().Name}");

            if (response is Error error)
            {
                entry.Status = $"error {error.Code} {error.Message}";
                Logger.Error($"bank: CreateNewBasicGroupChat failed: {error.Code} {error.Message}");
                return entry;
            }

            if (response is not CreatedBasicGroupChat created)
            {
                entry.Status = $"unexpected {response?.GetType().Name}";
                return entry;
            }

            entry.ChatId = created.ChatId;
            entry.Status = "created";

            var chat = await clientService.SendAsync(new GetChat(created.ChatId)).ConfigureAwait(false) as Chat;
            if (chat?.Type is ChatTypeBasicGroup basic)
            {
                entry.TypeId = basic.BasicGroupId;

                var group = await clientService.SendAsync(new GetBasicGroup(basic.BasicGroupId)).ConfigureAwait(false) as BasicGroup;
                Logger.Info($"bank: grupo chat {entry.ChatId} basicGroup {entry.TypeId} "
                    + $"members {group?.MemberCount} status {group?.Status?.GetType().Name} "
                    + $"title matches {(chat.Title == TitleGroup ? "yes" : "NO")}");
            }
            else
            {
                Logger.Error($"bank: grupo {entry.ChatId} came back as {chat?.Type?.GetType().Name}");
            }

            return entry;
        }

        private static async Task<Entry> CreateSupergroupAsync(IClientService clientService, string key, string title, bool isChannel)
        {
            var entry = new Entry { Key = key, Title = title, Kind = isChannel ? "channel" : "supergroup" };

            var response = await clientService.SendAsync(new CreateNewSupergroupChat(title, false, isChannel, isChannel ? Description : string.Empty, null, 0, false)).ConfigureAwait(false);
            Logger.Info($"bank: CreateNewSupergroupChat({key}, isChannel {isChannel}) answered {response?.GetType().Name}");

            if (response is Error error)
            {
                entry.Status = $"error {error.Code} {error.Message}";
                Logger.Error($"bank: CreateNewSupergroupChat({key}) failed: {error.Code} {error.Message}");
                return entry;
            }

            if (response is not Chat chat)
            {
                entry.Status = $"unexpected {response?.GetType().Name}";
                return entry;
            }

            entry.ChatId = chat.Id;
            entry.Status = "created";

            if (chat.Type is ChatTypeSupergroup supergroupType)
            {
                entry.TypeId = supergroupType.SupergroupId;

                var supergroup = await clientService.SendAsync(new GetSupergroup(supergroupType.SupergroupId)).ConfigureAwait(false) as Supergroup;
                Logger.Info($"bank: {key} chat {entry.ChatId} supergroup {entry.TypeId} "
                    + $"isChannel {supergroupType.IsChannel} members {supergroup?.MemberCount} "
                    + $"status {supergroup?.Status?.GetType().Name} "
                    + $"title matches {(chat.Title == title ? "yes" : "NO")}");

                if (supergroupType.IsChannel != isChannel)
                {
                    Logger.Error($"bank: {key} asked for isChannel {isChannel} and got {supergroupType.IsChannel}");
                }
            }
            else
            {
                Logger.Error($"bank: {key} {entry.ChatId} came back as {chat.Type?.GetType().Name}");
            }

            return entry;
        }

        private static async Task FillAsync(IClientService clientService, Entry entry, string media)
        {
            // Two photos, one video and two documents per chat, plus a text message: enough for the
            // Media, Files and Posts tabs to have something to show. The files are the harness's
            // own, generated next to the report.
            var photos = new[] { $"foto-{Pick(entry, 1)}.jpg", $"foto-{Pick(entry, 2)}.jpg" };
            var documents = new[] { $"doc-{Pick(entry, 1)}.txt", "paquete-01.zip" };

            await SendAsync(clientService, entry, new InputMessageText(
                new FormattedText($"[TEST] Unigram Linux — mensaje de prueba en el {entry.Key}.", Array.Empty<TextEntity>()), null, false), "text").ConfigureAwait(false);

            foreach (var name in photos)
            {
                var path = Path.Combine(media, name);
                if (!IOFile.Exists(path))
                {
                    Logger.Error($"bank: {path} missing");
                    continue;
                }

                var size = Measure(path);
                await SendAsync(clientService, entry, new InputMessagePhoto(
                    new InputPhoto(new InputFileLocal(path), null, null, Array.Empty<int>(), size.Item1, size.Item2),
                    new FormattedText($"[TEST] {name}", Array.Empty<TextEntity>()), false, null, false), $"photo {name} {size.Item1}x{size.Item2} {new FileInfo(path).Length}B").ConfigureAwait(false);
            }

            var video = Path.Combine(media, "video-01.mp4");
            if (IOFile.Exists(video))
            {
                await SendAsync(clientService, entry, new InputMessageVideo(
                    new InputVideo(new InputFileLocal(video), null, null, 0, Array.Empty<int>(), 6, 640, 360, true),
                    new FormattedText("[TEST] video-01.mp4", Array.Empty<TextEntity>()), false, null, false), $"video video-01.mp4 {new FileInfo(video).Length}B").ConfigureAwait(false);
            }

            foreach (var name in documents)
            {
                var path = Path.Combine(media, name);
                if (!IOFile.Exists(path))
                {
                    Logger.Error($"bank: {path} missing");
                    continue;
                }

                await SendAsync(clientService, entry, new InputMessageDocument(
                    new InputDocument(new InputFileLocal(path), null, false),
                    new FormattedText($"[TEST] {name}", Array.Empty<TextEntity>())), $"document {name} {new FileInfo(path).Length}B").ConfigureAwait(false);
            }
        }

        private static string Pick(Entry entry, int slot)
        {
            var offset = entry.Key switch
            {
                "grupo" => 0,
                "supergrupo" => 2,
                _ => 4
            };

            return (offset + slot).ToString("00");
        }

        private static async Task SendAsync(IClientService clientService, Entry entry, InputMessageContent content, string label)
        {
            var response = await clientService.SendAsync(new SendMessage(entry.ChatId, null, null, null, content)).ConfigureAwait(false);

            if (response is Error error)
            {
                Logger.Error($"bank: SendMessage({entry.Key}, {label}) failed: {error.Code} {error.Message}");
                return;
            }

            if (response is Message message)
            {
                entry.Sent.Add(label);
                Logger.Info($"bank: SendMessage({entry.Key}, {label}) -> chat {message.ChatId} message {message.Id} "
                    + $"state {message.SendingState?.GetType().Name ?? "none"} content {message.Content?.GetType().Name}");
            }
            else
            {
                Logger.Error($"bank: SendMessage({entry.Key}, {label}) answered {response?.GetType().Name}");
            }

            // A local file upload is serialized by TDLib anyway; spacing them out keeps the log
            // readable and the flood limits happy.
            await Task.Delay(700).ConfigureAwait(false);
        }

        private static async Task CountAsync(IClientService clientService, Entry entry)
        {
            var response = await clientService.SendAsync(new GetChatHistory(entry.ChatId, 0, 0, 60, false)).ConfigureAwait(false);
            if (response is not Messages messages)
            {
                Logger.Error($"bank: GetChatHistory({entry.ChatId}) answered {response?.GetType().Name}");
                return;
            }

            var kinds = new List<string>();
            var pending = 0;

            foreach (var message in messages.MessagesValue)
            {
                if (message.SendingState is MessageSendingStatePending)
                {
                    pending++;
                }

                kinds.Add($"{message.Id}:{message.Content?.GetType().Name?.Replace("Message", string.Empty)}");
            }

            Logger.Info($"bank: history of {entry.ChatId} -> {messages.TotalCount} total, {kinds.Count} loaded, {pending} still pending [{string.Join(", ", kinds)}]");
        }

        private static Tuple<int, int> Measure(string path)
        {
            try
            {
                using var stream = IOFile.OpenRead(path);
                var buffer = new byte[2];

                // Minimal JPEG SOF walk: enough for the two sizes the harness generates.
                if (stream.Read(buffer, 0, 2) == 2 && buffer[0] == 0xFF && buffer[1] == 0xD8)
                {
                    while (stream.Position < stream.Length)
                    {
                        if (stream.ReadByte() != 0xFF)
                        {
                            continue;
                        }

                        int marker = stream.ReadByte();
                        if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                        {
                            stream.Seek(3, SeekOrigin.Current);
                            int height = (stream.ReadByte() << 8) | stream.ReadByte();
                            int width = (stream.ReadByte() << 8) | stream.ReadByte();
                            return Tuple.Create(width, height);
                        }

                        if (marker is 0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7))
                        {
                            continue;
                        }

                        int length = (stream.ReadByte() << 8) | stream.ReadByte();
                        stream.Seek(length - 2, SeekOrigin.Current);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"bank: could not measure {path}", ex);
            }

            return Tuple.Create(0, 0);
        }

        private static void Write(string path, List<Entry> entries, long me)
        {
            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("{");
                builder.AppendLine($"  \"generado\": \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\",");
                builder.AppendLine($"  \"cuenta\": {me},");
                builder.AppendLine("  \"prefijo\": \"[TEST] Unigram Linux\",");
                builder.AppendLine("  \"chats\": [");

                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    builder.AppendLine("    {");
                    builder.AppendLine($"      \"clave\": \"{entry.Key}\",");
                    builder.AppendLine($"      \"titulo\": \"{Escape(entry.Title)}\",");
                    builder.AppendLine($"      \"chat_id\": {entry.ChatId},");
                    builder.AppendLine($"      \"tipo\": \"{entry.Kind}\",");
                    builder.AppendLine($"      \"tipo_id\": {entry.TypeId},");
                    builder.AppendLine($"      \"estado\": \"{Escape(entry.Status)}\",");
                    builder.AppendLine($"      \"enviados\": {entry.Sent.Count}");
                    builder.AppendLine(i < entries.Count - 1 ? "    }," : "    }");
                }

                builder.AppendLine("  ]");
                builder.AppendLine("}");

                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                IOFile.WriteAllText(path, builder.ToString());
                Logger.Info($"bank: ids written to {path}");
            }
            catch (Exception ex)
            {
                Logger.Error($"bank: could not write {path}", ex);
            }
        }

        private static string Escape(string value)
        {
            return value?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? string.Empty;
        }
    }
}
