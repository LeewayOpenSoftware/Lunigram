//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Native;
using Telegram.Native.Media;

namespace Telegram.Common
{
    public enum SoundEffect
    {
        Sent,
        Received,
        VoipIncoming,
        VoipRingback,
        VoipBusy,
        VoipFailed,
        VoipEnd,
        VoipConnecting,
        VideoChatJoin,
        VideoChatLeave
    }

    /// <summary>
    /// The short sounds: the tick when a message is sent, the chime when one arrives, the custom
    /// notification sound the user picked in Telegram, and (when phase 6 lands) the call tones.
    ///
    /// <para>Same static surface as <c>Telegram/Common/SoundEffects.cs</c>, which is excluded from
    /// the Linux subset because it is built on <c>Windows.Media.Audio.AudioGraph</c> — an API Uno
    /// Skia does not implement, and one whose whole job here (open a file, decode it, push it at
    /// the default device) is a single <c>loadfile</c> for the audio engine phase 5 already
    /// brought in.</para>
    ///
    /// <para><b>One throwaway libmpv core per sound</b>, not the shared player. Three reasons, and
    /// the last is the one that matters: a notification must not stop the music, must not inherit
    /// its speed or its volume slider, and must not have to wait for the playlist to be free. A
    /// core costs a handful of milliseconds to build and destroys itself when the file ends
    /// (<c>idle=once</c>: stay up until the first file, quit when the playlist runs out), so
    /// nothing is left holding a stream open in the sound mixer between sounds.</para>
    ///
    /// <para>Nothing here throws and nothing here blocks the caller: a machine without libmpv, a
    /// missing asset or a busy device costs the sound, never the notification.</para>
    /// </summary>
    public static class SoundEffects
    {
        /// <summary>
        /// The slots of the Windows original: one sound of each kind at a time, so a second call
        /// replaces the first instead of playing over it. Their identity is what makes
        /// <c>Stop()</c> able to silence a ringtone without touching the message chime.
        /// </summary>
        private enum EffectType
        {
            Generic,
            Voip,
            VideoChat,
            Custom
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<EffectType, MpvClient> _playing = new();

        private static bool _suspended;

        /// <summary>Called when the app goes to the background: no sound from a window nobody sees.</summary>
        public static void Suspend()
        {
            _suspended = true;
            Stop();
        }

        public static void Resume()
        {
            _suspended = false;
        }

        /// <summary>Silences everything currently playing. Safe from any thread.</summary>
        public static void Stop()
        {
            MpvClient[] clients;

            lock (_lock)
            {
                if (_playing.Count == 0)
                {
                    return;
                }

                clients = new MpvClient[_playing.Count];
                _playing.Values.CopyTo(clients, 0);
                _playing.Clear();
            }

            foreach (var client in clients)
            {
                Dispose(client);
            }
        }

        public static void Play(SoundEffect effect)
        {
            switch (effect)
            {
                case SoundEffect.Sent:
                    Play("sent.mp3", EffectType.Generic);
                    break;
                case SoundEffect.Received:
                    Play("received.mp3", EffectType.Generic);
                    break;
                case SoundEffect.VoipIncoming:
                    Play("voip_incoming.mp3", EffectType.Voip, loop: true);
                    break;
                case SoundEffect.VoipRingback:
                    Play("voip_ringback.mp3", EffectType.Voip, loop: true);
                    break;
                case SoundEffect.VoipConnecting:
                    Play("voip_connecting.mp3", EffectType.Voip);
                    break;
                case SoundEffect.VoipBusy:
                    // Upstream asks AudioGraph for four EXTRA loops, i.e. five plays. mpv's
                    // loop-file counts plays, not extra loops -- and this has NOT been measured,
                    // because the call tones belong to phase 6 and nothing raises them yet.
                    Play("voip_busy.mp3", EffectType.Voip, loops: 5);
                    break;
                case SoundEffect.VoipEnd:
                    Play("voip_end.mp3", EffectType.Voip);
                    break;
                case SoundEffect.VoipFailed:
                    Play("voip_failed.mp3", EffectType.Voip);
                    break;
                case SoundEffect.VideoChatJoin:
                    Play("voicechat_join.mp3", EffectType.VideoChat);
                    break;
                case SoundEffect.VideoChatLeave:
                    Play("voicechat_leave.mp3", EffectType.VideoChat);
                    break;
            }
        }

        /// <summary>
        /// The sound the user chose for this chat, as TDLib downloaded it
        /// (<c>GetSavedNotificationSound</c>). Windows hands the same file to the toast; a
        /// freedesktop server has no equivalent hint, so the app plays it and asks the server to
        /// stay quiet — see <c>NotificationsService.UpdateToast</c>.
        /// </summary>
        public static void Play(Td.Api.File file)
        {
            if (file != null && file.Local.IsDownloadingCompleted && !string.IsNullOrEmpty(file.Local.Path))
            {
                PlayFile(file.Local.Path, EffectType.Custom, 1);
            }
        }

        private static void Play(string fileName, EffectType type, bool loop = false, int loops = 1)
        {
            // Anchored on AppContext.BaseDirectory, never on the working directory: the app is
            // started from a .desktop launcher, whose working directory is the user's home.
            var path = NativeAssets.Resolve(Path.Combine("Audio", fileName), null);
            if (path == null)
            {
                Logger.Warning($"Sound effect {fileName} is not next to the binary: no sound");
                return;
            }

            PlayFile(path, type, loop ? 0 : loops);
        }

        /// <param name="loops">How many times to play it; 0 means forever (a ringtone).</param>
        private static void PlayFile(string path, EffectType type, int loops)
        {
            if (_suspended || !MpvClient.IsAvailable)
            {
                return;
            }

            // mpv_create + mpv_initialize opens the audio output, which is the one part of this
            // that can take a few milliseconds and can block. The callers are a notification
            // arriving and a message being sent -- both on the UI thread.
            Task.Run(() =>
            {
                try
                {
                    Start(path, type, loops);
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot play a sound effect", ex);
                }
            });
        }

        private static void Start(string path, EffectType type, int loops)
        {
            var options = new List<KeyValuePair<string, string>>
            {
                // Same isolation the media player takes, and for the same measured reason: libmpv
                // does not read ~/.config/mpv, and saying so explicitly is what keeps a stray
                // speed= or volume= in the user's mpv.conf out of Unigram (plan-media.md 2.1).
                new("config", "no"),
                new("terminal", "no"),
                new("osc", "no"),
                new("load-scripts", "no"),
                new("ytdl", "no"),
                new("audio-display", "no"),
                new("vid", "no"),
                new("vo", "null"),
                new("ao", "pipewire,pulse,alsa"),

                // Stay up until the first file, then quit when the playlist runs out: the core
                // destroys ITSELF when the sound ends, which is what makes this a throwaway handle
                // instead of a stream left open in the sound mixer.
                new("idle", "once"),
                new("keep-open", "no"),
                new("msg-level", "all=error"),
            };

            if (loops == 0)
            {
                options.Add(new("loop-file", "inf"));
            }
            else if (loops > 1)
            {
                options.Add(new("loop-file", loops.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            var client = MpvClient.Create(options, out var error);
            if (client == null)
            {
                Logger.Warning($"No mpv core for the sound effect ({error})");
                return;
            }

            // One sound per slot: a second notification while the first is still ringing replaces
            // it, exactly like the AudioGraph version did.
            MpvClient previous = null;

            lock (_lock)
            {
                if (_suspended)
                {
                    Dispose(client);
                    return;
                }

                _playing.Remove(type, out previous);
                _playing[type] = client;
            }

            Dispose(previous);

            client.EndFile += (s, e) =>
            {
                // Raised on the core's own pump thread. Dropping the slot here is what stops Stop()
                // from later talking to a core that has already shut itself down.
                lock (_lock)
                {
                    if (_playing.TryGetValue(type, out var current) && ReferenceEquals(current, client))
                    {
                        _playing.Remove(type);
                    }
                }
            };

            var result = client.Command("loadfile", path);
            if (result < 0)
            {
                Logger.Warning($"mpv refused the sound effect {Path.GetFileName(path)}: {MpvClient.ErrorString(result)}");

                lock (_lock)
                {
                    if (_playing.TryGetValue(type, out var current) && ReferenceEquals(current, client))
                    {
                        _playing.Remove(type);
                    }
                }

                Dispose(client);
            }
        }

        private static void Dispose(MpvClient client)
        {
            if (client == null)
            {
                return;
            }

            // Off the caller's thread: Dispose waits (bounded) for the pump to destroy the handle,
            // and this is called from a notification and from the core's own EndFile.
            ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                try
                {
                    ((MpvClient)state).Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot dispose a sound effect core", ex);
                }
            }, client);
        }
    }
}
