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
using Telegram.ViewModels;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for phase 5: open the history at a message of a given media kind, so that the
    /// player can be exercised without anybody browsing the account by hand.
    ///
    ///     UNIGRAM_MEDIA_TEST=&lt;seconds&gt;:&lt;kind&gt;[:&lt;chats to scan&gt;[:&lt;shortest, in seconds&gt;]]
    ///
    /// <para><c>kind</c> is <c>voice</c>, <c>audio</c>, <c>video</c>, <c>videonote</c>, <c>photo</c>
    /// or <c>animation</c>. The search is TDLib's own (<c>SearchChatMessages</c> with the matching
    /// <c>SearchMessagesFilter</c> and an empty query) over the top chats of the main list, and the
    /// navigation is the shipping "jump to message" path, the same one a reply or a search result
    /// takes. Nothing is sent and nothing is written.</para>
    ///
    /// <para>Everything it logs is <b>ids, types and sizes</b> — never a chat title, a caption or a
    /// file name: this runs against the user's real account.</para>
    ///
    /// <para>Pair it with <c>UNIGRAM_CLICK</c> to press the control it just put on screen — the
    /// <c>@Type</c> target form exists for exactly this
    /// (<c>UNIGRAM_CLICK=30:@VoiceNoteContent#Button</c>).</para>
    /// </summary>
    public static class MediaTest
    {
        public static long ChatId { get; private set; }

        public static long MessageId { get; private set; }

        /// <summary>
        /// <c>UNIGRAM_MEDIA_PROBE=1</c>: one line per playback state change and one per second while
        /// something plays, so that a run leaves behind what the service was doing and when. Types
        /// and numbers only.
        /// </summary>
        public static void ScheduleProbe(Window window)
        {
            if (Environment.GetEnvironmentVariable("UNIGRAM_MEDIA_PROBE") is not string on || on.Length == 0 || on == "0")
            {
                return;
            }

            _ = ProbeAsync();
        }

        private static async Task ProbeAsync()
        {
            IPlaybackService playback = null;

            for (int i = 0; i < 120 && playback == null; i++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                playback = LifetimeService.Current?.Playback;
            }

            if (playback == null)
            {
                Logger.Error("media probe: no playback service");
                return;
            }

            playback.StateChanged += (s, e) => Describe(s, "state");
            playback.SourceChanged += (s, e) => Describe(s, "source");
            playback.MediaFailed += (s, e) => Logger.Error("media probe: MediaFailed");

            Logger.Info("media probe: attached");

            var last = string.Empty;

            while (true)
            {
                await Task.Delay(1000).ConfigureAwait(false);

                try
                {
                    if (playback.CurrentItem == null)
                    {
                        continue;
                    }

                    var line = $"media probe: {playback.PlaybackState} {playback.Position.TotalSeconds:F2}/{playback.Duration.TotalSeconds:F2}s "
                        + $"speed {playback.PlaybackSpeed:F2} volume {playback.Volume:F2} "
                        + $"item {Describe(playback.CurrentItem)} of {playback.Items.Count}";

                    if (line != last)
                    {
                        Logger.Info(line);
                        last = line;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("media probe: threw", ex);
                }
            }
        }

        private static string Describe(PlaybackItem item)
        {
            if (item == null)
            {
                return "none";
            }

            // The kind of thing playing and how long it is. Never its title: that is the name of a
            // song or of a file in the user's account.
            var kind = item is PlaybackItemMessage message
                ? message.Message?.Content?.GetType().Name ?? "message"
                : item.GetType().Name;

            return $"{kind}({item.Duration}s)";
        }

        private static void Describe(IPlaybackService playback, string what)
        {
            try
            {
                Logger.Info($"media probe: {what} -> {playback.PlaybackState}, "
                    + $"item {Describe(playback.CurrentItem)}, "
                    + $"{playback.Position.TotalSeconds:F2}/{playback.Duration.TotalSeconds:F2}s, "
                    + $"speed {playback.PlaybackSpeed:F2}");
            }
            catch (Exception ex)
            {
                Logger.Error($"media probe: {what} threw", ex);
            }
        }

        public static void Schedule(Window window)
        {
            ScheduleProbe(window);

            var value = Environment.GetEnvironmentVariable("UNIGRAM_MEDIA_TEST");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var parts = value.Split(':');

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                Logger.Error($"UNIGRAM_MEDIA_TEST must start with the seconds to wait: \"{value}\"");
                return;
            }

            var kind = parts.Length > 1 ? parts[1] : "voice";
            var scan = parts.Length > 2 && int.TryParse(parts[2], out int count) ? count : 60;
            var least = parts.Length > 3 && int.TryParse(parts[3], out int shortest) ? shortest : 0;
            var skip = parts.Length > 4 && int.TryParse(parts[4], out int first) ? first : 0;

            // ":play" also starts it, through the very call the play button makes
            // (DialogViewModel.PlayMessage -> IPlaybackService.Play). For the messages the history
            // will not scroll to, that is the only way in.
            var play = parts.Length > 5 && parts[5] == "play";

            _ = RunAsync(window, seconds, kind, scan, least, skip, play);
        }

        private static async Task RunAsync(Window window, double seconds, string kind, int scan, int least, int skip, bool play)
        {
            await Task.Delay((int)Math.Max(seconds * 1000, 1)).ConfigureAwait(false);

            try
            {
                await RunCoreAsync(window, kind, scan, least, skip, play).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("media test: failed", ex);
            }
        }

        private static SearchMessagesFilter FilterOf(string kind)
        {
            return kind switch
            {
                "voice" => new SearchMessagesFilterVoiceNote(),
                "audio" => new SearchMessagesFilterAudio(),
                "video" => new SearchMessagesFilterVideo(),
                "videonote" => new SearchMessagesFilterVideoNote(),
                "photo" => new SearchMessagesFilterPhoto(),
                "animation" => new SearchMessagesFilterAnimation(),
                _ => null
            };
        }

        private static async Task RunCoreAsync(Window window, string kind, int scan, int least, int skip, bool play)
        {
            var session = LifetimeService.Current?.ActiveItem;
            if (session?.ClientService == null)
            {
                Logger.Error("media test: no active session");
                return;
            }

            var filter = FilterOf(kind);
            if (filter == null)
            {
                Logger.Error($"media test: unknown kind \"{kind}\"");
                return;
            }

            var clientService = session.ClientService;

            var response = await clientService.SendAsync(new GetChats(new ChatListMain(), scan));
            if (response is not Td.Api.Chats chats || chats.ChatIds.Count < 1)
            {
                Logger.Error($"media test: GetChats answered {response}");
                return;
            }

            Logger.Info($"media test: looking for a \"{kind}\" of at least {least}s in the first {chats.ChatIds.Count} chats");

            var scanned = 0;
            var matched = 0;

            foreach (var chatId in chats.ChatIds)
            {
                scanned++;

                var found = await clientService.SendAsync(new SearchChatMessages(chatId, null, string.Empty, null, 0, 0, 20, FilterOf(kind)));
                if (found is not FoundChatMessages messages || messages.Messages.Count < 1)
                {
                    continue;
                }

                // Already on disk first: downloading 50 MB of somebody else's account to prove that
                // a player plays is neither quick nor polite. `least` counts seconds, and a
                // negative one is a maximum instead of a minimum -- which is what you want when
                // looking for something short.
                var candidates = new List<Message>();

                foreach (var candidate in messages.Messages)
                {
                    var media = FileOf(candidate.Content);
                    var duration = DurationOf(candidate.Content);

                    if (media == null || (least >= 0 ? duration < least : duration > -least))
                    {
                        continue;
                    }

                    if (media.Local?.IsDownloadingCompleted == true)
                    {
                        candidates.Insert(0, candidate);
                    }
                    else
                    {
                        candidates.Add(candidate);
                    }
                }

                foreach (var message in candidates)
                {
                    var file = FileOf(message.Content);

                    if (matched++ < skip)
                    {
                        // Skipped on purpose: the first chat that has one of these is not always the
                        // one that can be looked at.
                        Logger.Info($"media test: skipping match {matched} in chat {chatId}");
                        break;
                    }

                    // Ids, types and sizes. Never a title, a caption or a file name.
                    Logger.Info($"media test: found {message.Content.GetType().Name} after {scanned} chats -- "
                        + $"chat {chatId}, message {message.Id}, file {file.Id}, {file.Size} bytes, "
                        + $"downloaded {file.Local?.IsDownloadingCompleted == true} "
                        + $"({file.Local?.DownloadedSize ?? 0} bytes local), duration {DurationOf(message.Content)}s");

                    ChatId = chatId;
                    MessageId = message.Id;

                    // In a forum the general history does not contain the topics' messages, so a
                    // jump without the topic lands in a window the message is not in at all.
                    Navigate(window, session, chatId, message.Id, message.TopicId);

                    if (play)
                    {
                        await Task.Delay(6000).ConfigureAwait(false);
                        Start(window, clientService, message);
                    }

                    return;
                }
            }

            Logger.Error($"media test: no \"{kind}\" of at least {least}s in the first {scanned} chats");
        }

        private static File FileOf(MessageContent content)
        {
            return content switch
            {
                MessageVoiceNote voice => voice.VoiceNote?.Voice,
                MessageAudio audio => audio.Audio?.AudioValue,
                MessageVideo video => video.Video?.VideoValue,
                MessageVideoNote note => note.VideoNote?.Video,
                MessageAnimation animation => animation.Animation?.AnimationValue,
                MessagePhoto photo => photo.Photo?.Sizes?.Count > 0 ? photo.Photo.Sizes[^1].Photo : null,
                _ => null
            };
        }

        private static int DurationOf(MessageContent content)
        {
            return content switch
            {
                MessageVoiceNote voice => voice.VoiceNote?.Duration ?? 0,
                MessageAudio audio => audio.Audio?.Duration ?? 0,
                MessageVideo video => video.Video?.Duration ?? 0,
                MessageVideoNote note => note.VideoNote?.Duration ?? 0,
                MessageAnimation animation => animation.Animation?.Duration ?? 0,
                _ => 0
            };
        }

        /// <summary>
        /// Plays the message the way the play button does: the shared
        /// <c>IPlaybackService.Play(XamlRoot, MessageWithOwner)</c> that
        /// <c>DialogViewModel.PlayMessage</c> calls, playlist and transport controls included.
        /// </summary>
        private static void Start(Window window, IClientService clientService, Message message)
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    Logger.Info($"media test: playing message {message.Id} ({message.Content.GetType().Name})");
                    LifetimeService.Current.Playback.Play(window.Content.XamlRoot, new MessageWithOwner(clientService, message));
                }
                catch (Exception ex)
                {
                    Logger.Error("media test: play threw", ex);
                }
            });
        }

        private static void Navigate(Window window, ISession session, long chatId, long messageId, MessageTopic topic)
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var context = WindowContext.Active ?? WindowContext.Main;
                    var service = context?.NavigationServices?.GetByFrameId($"Main{session.Id}");

                    if (service == null)
                    {
                        Logger.Error("media test: no navigation service");
                        return;
                    }

                    Logger.Info($"media test: navigating to chat {chatId} at message {messageId} (topic {topic?.GetType().Name ?? "none"})");
                    service.NavigateToChat(chatId, message: messageId, topic: topic);
                }
                catch (Exception ex)
                {
                    Logger.Error("media test: navigation threw", ex);
                }
            });
        }
    }
}
