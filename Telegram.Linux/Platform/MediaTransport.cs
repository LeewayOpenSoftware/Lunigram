//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Globalization;
using System.Threading.Tasks;
using WM = Windows.Media;

namespace Telegram.Services
{
    /// <summary>
    /// The half that knows about Unigram, between upstream's <c>SystemMediaTransportControls</c>
    /// and the MPRIS object on the bus.
    ///
    /// <para>Why it exists at all is the blindness rule of <c>Platform/DBus/</c>:
    /// <see cref="MprisPlayer"/> speaks strings, numbers and callbacks and must stay linkable from
    /// a console, so somebody else has to turn "a voice note is playing" into a title, a length and
    /// a set of enabled buttons. That somebody is this file — the media equivalent of what
    /// <c>NotificationsService</c> is to <c>DesktopNotifications</c>.</para>
    ///
    /// <para><b>Two channels, and the split is deliberate.</b> Everything upstream already tells
    /// the transport — status, title, artist, album art, which buttons make sense — arrives through
    /// <see cref="WM.IMediaTransportBackend.Update"/>, so <c>Telegram/Services/PlaybackService.cs</c>
    /// feeds the GNOME panel without one line of it knowing what D-Bus is. The rest — position,
    /// seek, volume, speed, repeat, shuffle — is read from <see cref="IPlaybackService"/> directly,
    /// because upstream's transport block has those calls commented out (<c>Execute(player =>
    /// player.SystemMediaTransportControls...)</c>) and never sets them. Adding them to the shared
    /// file would be divergence for something MPRIS can read from the service itself.</para>
    ///
    /// <para><b>Threading.</b> Requests arrive on the D-Bus reader thread and every one of them
    /// ends in the playlist logic, which raises <c>SourceChanged</c> into the visual tree — so they
    /// are marshalled to the UI dispatcher first. In the other direction updates arrive on the UI
    /// thread and are coalesced into one bus message per turn, because a single track change makes
    /// upstream touch the transport four times (IsEnabled, ClearAll, the properties, Update) and
    /// the panel would otherwise see the title blink out and back.</para>
    /// </summary>
    public sealed class MediaTransport : WM.IMediaTransportBackend
    {
        public static MediaTransport Current { get; } = new MediaTransport();

        private readonly object _lock = new();

        private DispatcherQueue _queue;
        private IPlaybackService _playback;
        private MprisSnapshot _snapshot = new();

        private WM.SystemMediaTransportControls _controls;
        private bool _pending;
        private bool _started;

        private TimeSpan _position;

        private MediaTransport()
        {
        }

        /// <summary>
        /// Puts the player on the bus and makes upstream's transport feed it. False means this
        /// desktop has no session bus and the app simply has no media controls, exactly like the
        /// tray icon.
        /// </summary>
        public static async Task<bool> InitializeAsync()
        {
            var transport = Current;

            if (transport._started)
            {
                return MprisPlayer.Current.IsRegistered;
            }

            transport._started = true;

            try
            {
                transport._queue ??= DispatcherQueue.GetForCurrentThread();

                var controls = WM.SystemMediaTransportControls.GetForCurrentView();
                transport._controls = controls;
                controls.Backend = transport;

                transport.Attach(MprisPlayer.Current);

                return await MprisPlayer.Current.RegisterAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("MPRIS media transport failed to start", ex);
                return false;
            }
        }

        #region The desktop asks

        private void Attach(MprisPlayer player)
        {
            // Position is pulled, never pushed: the spec keeps it out of PropertiesChanged on
            // purpose, and this reads one TimeSpan field of the playback service.
            player.PositionProvider = () => _position;

            player.Raised += (s, e) => DesktopIntegration.ShowWindow();
            player.Quitted += (s, e) => DesktopIntegration.Quit();

            // The four that upstream already handles go back in through the transport, so the
            // shared Transport_ButtonPressed decides what Previous means (restart the track under
            // five seconds in, move back after that) instead of this file deciding it twice.
            player.PlayRequested += (s, e) => Press(WM.SystemMediaTransportControlsButton.Play);
            player.PauseRequested += (s, e) => Press(WM.SystemMediaTransportControlsButton.Pause);
            player.NextRequested += (s, e) => Press(WM.SystemMediaTransportControlsButton.Next);
            player.PreviousRequested += (s, e) => Press(WM.SystemMediaTransportControlsButton.Previous);

            // Stop is the one that cannot go through the transport: upstream's
            // Transport_ButtonPressed has no case for it (Windows never enabled the button), so a
            // press would be swallowed. Clear() is what the app itself does when a voice note
            // ends -- stop, and give back the music it interrupted.
            player.StopRequested += (s, e) => Dispatch(() => Playback?.Clear());

            // PlayPause has no Windows button because SMTC never had one: the OS sends Play or
            // Pause. On a keyboard there is one key for both, so the state decides here.
            player.PlayPauseRequested += (s, e) => Press(_snapshot.Status == MprisPlaybackStatus.Playing
                ? WM.SystemMediaTransportControlsButton.Pause
                : WM.SystemMediaTransportControlsButton.Play);

            player.SeekRequested += (s, offset) => Dispatch(() => Seek(_position + offset));
            player.PositionRequested += (s, position) => Dispatch(() => Seek(position));

            player.VolumeRequested += (s, volume) => Dispatch(() =>
            {
                var playback = Playback;
                if (playback != null)
                {
                    playback.Volume = volume;
                    Invalidate();
                }
            });

            player.RateRequested += (s, rate) => Dispatch(() =>
            {
                var playback = Playback;

                // Only where the app itself allows it: a short voice note or a song under ten
                // minutes has no speed control in the UI, and honouring the panel there would put
                // the player at a rate no button could undo.
                if (playback?.CurrentItem is { CanChangePlaybackRate: true })
                {
                    playback.PlaybackSpeed = rate;
                    Invalidate();
                }
            });

            player.LoopStatusRequested += (s, loop) => Dispatch(() =>
            {
                var playback = Playback;
                if (playback != null)
                {
                    // Upstream's tri-state: true is the whole list, null is the single track,
                    // false is off. Same three values MPRIS spells Playlist/Track/None.
                    playback.IsRepeatEnabled = loop switch
                    {
                        MprisLoopStatus.Playlist => (bool?)true,
                        MprisLoopStatus.Track => (bool?)null,
                        _ => (bool?)false
                    };

                    Invalidate();
                }
            });

            player.ShuffleRequested += (s, shuffle) => Dispatch(() =>
            {
                var playback = Playback;
                if (playback != null)
                {
                    playback.IsShuffleEnabled = shuffle;
                    Invalidate();
                }
            });
        }

        private void Press(WM.SystemMediaTransportControlsButton button)
        {
            Dispatch(() => _controls?.RaiseButtonPressed(button));
        }

        private void Seek(TimeSpan position)
        {
            var playback = Playback;
            if (playback == null)
            {
                return;
            }

            var duration = playback.Duration;
            if (duration > TimeSpan.Zero && position > duration)
            {
                position = duration;
            }

            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            playback.Seek(position);

            // The spec's only way of saying "the position jumped". Without it a panel with a
            // scrubber would keep drawing the old offset until it happened to poll again.
            _ = MprisPlayer.Current.EmitSeekedAsync(position);
        }

        /// <summary>
        /// Runs on the thread the playlist logic expects. Everything below this point raises
        /// <c>SourceChanged</c>/<c>StateChanged</c> into bindings and animations, so the D-Bus
        /// reader thread is the one thread it must never run on.
        /// </summary>
        private void Dispatch(Action action)
        {
            var queue = _queue;

            if (queue == null || queue.HasThreadAccess)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.Error("MPRIS request failed", ex);
                }

                return;
            }

            queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Logger.Error("MPRIS request failed", ex);
                }
            });
        }

        #endregion

        #region The app tells

        /// <summary>
        /// <see cref="IPlaybackService"/>, resolved late and never cached before it exists.
        /// Touching <c>LifetimeService.Current</c> builds every session, so it is only reached for
        /// once something has actually been played — which is guaranteed here, because the only
        /// caller of <see cref="Update"/> is the playback service itself.
        /// </summary>
        private IPlaybackService Playback
        {
            get
            {
                var playback = _playback;
                if (playback != null)
                {
                    return playback;
                }

                try
                {
                    playback = LifetimeService.Current?.Playback;
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot reach the playback service from the media transport", ex);
                    return null;
                }

                if (playback == null)
                {
                    return null;
                }

                lock (_lock)
                {
                    if (_playback == null)
                    {
                        _playback = playback;

                        // Volume, speed, repeat and shuffle are changed from the app's own UI and
                        // never touch the transport, so this is what keeps the panel honest about
                        // them. It is also where the position that MPRIS pulls comes from: reading
                        // IPlaybackService.Position from the bus thread would be a torn read of a
                        // field written by the mpv pump.
                        playback.PositionChanged += OnPositionChanged;
                        playback.StateChanged += OnStateChanged;
                    }
                }

                return _playback;
            }
        }

        private void OnPositionChanged(IPlaybackService sender, PlaybackPositionChangedEventArgs args)
        {
            _position = args.Position;

            // Four times a second while playing, and all it costs when nothing changed is a
            // handful of comparisons: UpdateAsync sends nothing unless a property really moved.
            Invalidate();
        }

        private void OnStateChanged(IPlaybackService sender, object args)
        {
            Invalidate();
        }

        /// <summary>
        /// Called by upstream's transport whenever it is told something. Coalesced, because one
        /// track change is four of these.
        /// </summary>
        public void Update(WM.SystemMediaTransportControls controls)
        {
            _controls ??= controls;
            _queue ??= DispatcherQueue.GetForCurrentThread();

            Invalidate();
        }

        private void Invalidate()
        {
            var queue = _queue;

            if (queue == null || !queue.HasThreadAccess)
            {
                Publish();
                return;
            }

            lock (_lock)
            {
                if (_pending)
                {
                    return;
                }

                _pending = true;
            }

            if (!queue.TryEnqueue(Publish))
            {
                // A dispatcher that refuses the callback (shutting down) must not leave the flag
                // set, or the panel would freeze on whatever it last saw.
                lock (_lock)
                {
                    _pending = false;
                }
            }
        }

        private void Publish()
        {
            lock (_lock)
            {
                _pending = false;
            }

            try
            {
                _snapshot = Build();
                _ = MprisPlayer.Current.UpdateAsync(_snapshot);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot describe what is playing to MPRIS", ex);
            }
        }

        private MprisSnapshot Build()
        {
            var controls = _controls;

            // The property, not the field: this is the one place that runs on the UI thread with
            // the playback service guaranteed to exist, so it is where the subscription to its
            // position and state is made.
            var playback = Playback;

            var snapshot = new MprisSnapshot();

            if (controls == null)
            {
                return snapshot;
            }

            snapshot.Status = controls.PlaybackStatus switch
            {
                WM.MediaPlaybackStatus.Playing => MprisPlaybackStatus.Playing,
                WM.MediaPlaybackStatus.Paused => MprisPlaybackStatus.Paused,
                _ => MprisPlaybackStatus.Stopped
            };

            // IsEnabled false is upstream saying "there is no track": Windows then empties the
            // flyout, and here the panel has to be told the same or it would keep offering
            // transport buttons for something that is gone.
            if (controls.IsEnabled)
            {
                var display = controls.DisplayUpdater;

                snapshot.Title = display.MusicProperties.Title;
                snapshot.Artist = display.MusicProperties.Artist;
                snapshot.Album = display.MusicProperties.AlbumArtist;

                snapshot.CanPlay = controls.IsPlayEnabled;
                snapshot.CanPause = controls.IsPauseEnabled;
                snapshot.CanGoNext = controls.IsNextEnabled;
                snapshot.CanGoPrevious = controls.IsPreviousEnabled;
                snapshot.CanSeek = true;
            }

            var item = playback?.CurrentItem;
            if (item != null && controls.IsEnabled)
            {
                // The file id is the only thing about a track that is stable for as long as it
                // plays; message ids do not exist for profile audio, and titles repeat.
                snapshot.TrackId = item.Document?.Id.ToString(CultureInfo.InvariantCulture);

                // The cover is the file the shared UpdateAlbumCover already downloads and hands to
                // the transport as a stream reference. MPRIS wants a URI instead, and the path is
                // right here -- which is why nothing had to be added to the shared code for it.
                var cover = item.AlbumCover?.File;
                if (cover != null && cover.Local.IsDownloadingCompleted)
                {
                    snapshot.ArtUrl = cover.Local.Path;
                }

                // The duration mpv reports once the file is open, falling back to the one the
                // sender declared -- which is all there is until the first frame is decoded.
                var duration = playback.Duration;
                snapshot.Length = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(item.Duration);
            }

            if (playback != null)
            {
                // Refreshed here as well as from PositionChanged, because a track change does NOT
                // raise that event -- CurrentItem's setter zeroes the position and raises
                // SourceChanged instead -- and MPRIS would answer the old track's offset for the
                // quarter second until the next tick.
                _position = playback.Position;

                snapshot.Volume = Math.Clamp(playback.Volume, 0, 1);
                snapshot.Rate = playback.PlaybackSpeed <= 0 ? 1 : playback.PlaybackSpeed;
                snapshot.Shuffle = playback.IsShuffleEnabled;
                snapshot.Loop = playback.IsRepeatEnabled switch
                {
                    true => MprisLoopStatus.Playlist,
                    null => MprisLoopStatus.Track,
                    _ => MprisLoopStatus.None
                };
            }

            return snapshot;
        }

        #endregion
    }
}
