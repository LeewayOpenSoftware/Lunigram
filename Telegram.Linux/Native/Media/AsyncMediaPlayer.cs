//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.Foundation;

namespace Telegram.Native.Media
{
    /// <summary>
    /// The Linux <c>AsyncMediaPlayer</c>: same C# surface as the libvlc-backed C++/WinRT class of
    /// Telegram.Native, libmpv underneath. Keeping the surface is the whole point -- the shared
    /// <c>Services/PlaybackService.cs</c> (962 lines of playlist logic that has nothing Windows
    /// about it) compiles against this unchanged, instead of being duplicated in
    /// <c>Telegram.Linux/Hubs/</c> and condemned to diverge on every rebase (plan-media.md 5.1,
    /// option B).
    ///
    /// <para><b>Audio only, on purpose.</b> <c>vid=no</c>: phase 5 decodes video pixels with our
    /// own FFmpeg engine and presents them against mpv's clock, because mpv's software renderer was
    /// measured at 55 ms/frame at 2736x1554 against our 22,8 (plan-media.md 2.1). What mpv is here
    /// for is the three things that would have cost weeks: the output device, resampling, and
    /// variable speed with pitch correction (<c>scaletempo2</c>) for 1,5x/2x voice notes.</para>
    ///
    /// <para><b>Threading.</b> <see cref="MpvClient"/> raises everything on its own pump thread.
    /// Every event this class exposes is marshalled to the dispatcher of the thread that built the
    /// player first -- the same rule the port applies to TDLib updates -- so the content controls
    /// and <c>PlaybackService</c> see them where they expect to. In a console (no dispatcher) they
    /// are raised inline, which is what the spike exercises.</para>
    /// </summary>
    public sealed partial class AsyncMediaPlayer
    {
        private readonly AsyncMediaPlayerOptions _options;
        private readonly DispatcherQueue _queue;
        private readonly object _lock = new();

        private MpvClient _client;
        private MpvStreamProtocol _protocol;

        private IAsyncMediaPlayerSource _source;
        private string _uri;

        private AsyncMediaPlayerState _state = AsyncMediaPlayerState.NothingSpecial;
        private bool _loaded;
        private bool _closed;

        private double _position;
        private double _duration;
        private double _rate = 1;
        private double _volume = 1;
        private bool _mute;
        private bool _paused;

        // Last position actually reported. libmpv observes time-pos at roughly playloop rate;
        // libvlc reported ~4 Hz and everything downstream is built for that (PlaybackSlider
        // extrapolates between updates with a composition animation and has its own 1 s stale
        // timer), so the flood is thinned here rather than on the dispatcher.
        private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(250);
        private DateTime _positionReported = DateTime.MinValue;

        // Set when Position is assigned before the file is loaded, which is exactly what
        // PlaybackService.ClearImpl does when it restores the music a voice note interrupted.
        private double _pendingPosition = double.NaN;

        public AsyncMediaPlayer(AsyncMediaPlayerOptions options)
            : this(options, null)
        {
        }

        public AsyncMediaPlayer(AsyncMediaPlayerOptions options, AsyncMediaPlayerSwapChain context)
        {
            _options = options ?? new AsyncMediaPlayerOptions();
            Context = context ?? new AsyncMediaPlayerSwapChain();

            _rate = _options.Rate;
            _volume = _options.Volume;
            _mute = _options.Mute;

            _queue = DispatcherQueue.GetForCurrentThread();
        }

        public AsyncMediaPlayerSwapChain Context { get; }

        /// <summary>
        /// How many events the mpv pump has seen and how many of them were property observations.
        /// Diagnostics only (the spike prints it to explain the CPU of a playing track); zero
        /// before the first Play.
        /// </summary>
        public (long Events, long PropertyEvents, int StreamOpens) Diagnostics
        {
            get
            {
                var client = _client;
                return client == null ? (0, 0, 0) : (client.Events, client.PropertyEvents, _protocol?.Opened ?? 0);
            }
        }

        /// <summary>
        /// The options every core is created with. Fixed, and every one of them earns its place:
        /// <list type="bullet">
        /// <item><c>config=no</c> -- measured: libmpv does not read <c>~/.config/mpv</c> anyway,
        /// but saying so explicitly is what guarantees a user's <c>mpv.conf</c> (a stray
        /// <c>speed=2.0</c>, say) can never alter playback inside Unigram.</item>
        /// <item><c>terminal=no</c>, <c>osc=no</c>, <c>load-scripts=no</c>, <c>ytdl=no</c> -- no
        /// console output, no on-screen controller, no user scripts, no network resolver: this is
        /// a library, not a player.</item>
        /// <item><c>vid=no</c>, <c>vo=null</c>, <c>audio-display=no</c> -- no video decoding, no
        /// window, and no cover art rendered as a video track.</item>
        /// <item><c>ao=pipewire,pulse,alsa</c> -- PipeWire natively (compiled in, checked in the
        /// "List of enabled features"), with the two usual fallbacks so a box without it still has
        /// sound. This is the plan's <c>ao=pipewire</c> plus a fallback list, not a different
        /// decision.</item>
        /// <item><c>audio-pitch-correction=yes</c> -- <c>scaletempo2</c>, the whole reason the
        /// audio engine is mpv and not ours: 1,5x and 2x voice notes that do not sound like a
        /// chipmunk.</item>
        /// <item><c>keep-open=no</c> -- end of file means end of file, which is what
        /// <c>EndReached</c> and the playlist advance are built on.</item>
        /// <item><c>idle=yes</c> -- NOT in the plan's list and required: without it the core shuts
        /// itself down when the playlist runs out, and the next track would find a dead handle.</item>
        /// </list>
        /// </summary>
        private static IReadOnlyList<KeyValuePair<string, string>> BuildOptions(bool debug)
        {
            var options = new List<KeyValuePair<string, string>>
            {
                new("config", "no"),
                new("terminal", "no"),
                new("osc", "no"),
                new("load-scripts", "no"),
                new("ytdl", "no"),
                new("audio-display", "no"),
                new("vid", "no"),
                new("vo", "null"),
                new("ao", "pipewire,pulse,alsa"),
                new("audio-pitch-correction", "yes"),
                new("keep-open", "no"),
                new("idle", "yes"),
                new("msg-level", debug ? "all=v" : "all=error"),
            };

            return options;
        }

        private MpvClient EnsureClient()
        {
            lock (_lock)
            {
                if (_closed)
                {
                    return null;
                }

                if (_client != null)
                {
                    return _client;
                }

                var client = MpvClient.Create(BuildOptions(_options.Debug), out var error);
                if (client == null)
                {
                    UnigramNative.Report(Logger.LogLevel.Error, $"mpv core could not be created ({error}): no audio playback");
                    SetState(AsyncMediaPlayerState.Error);
                    return null;
                }

                client.PropertyChanged += OnPropertyChanged;
                client.EndFile += OnEndFile;
                client.FileLoaded += OnFileLoaded;
                client.LogMessage += OnLogMessage;

                client.ObserveProperty("time-pos", MpvFormat.Double, ObservedTimePos);
                client.ObserveProperty("duration", MpvFormat.Double, ObservedDuration);
                client.ObserveProperty("pause", MpvFormat.Flag, ObservedPause);
                client.ObserveProperty("paused-for-cache", MpvFormat.Flag, ObservedPausedForCache);

                if (_options.Debug)
                {
                    client.RequestLogMessages("v");
                }

                client.SetProperty("speed", _rate <= 0 ? 1 : _rate);
                client.SetProperty("volume", MpvVolume(_volume));
                client.SetProperty("mute", _mute);

                _protocol = MpvStreamProtocol.Register(client, out var protocolError);
                if (_protocol == null)
                {
                    UnigramNative.Report(Logger.LogLevel.Error, $"mpv refused the tg:// protocol ({protocolError}): streaming playback disabled");
                }

                _client = client;
                return client;
            }
        }

        #region Dispatching

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
                    UnigramNative.Report(Logger.LogLevel.Error, "AsyncMediaPlayer event handler threw", ex);
                }
            }
            else
            {
                queue.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        UnigramNative.Report(Logger.LogLevel.Error, "AsyncMediaPlayer event handler threw", ex);
                    }
                });
            }
        }

        #endregion

        #region mpv events

        // reply_userdata of each observation. Switching on a number rather than on the property
        // name keeps the hot path (time-pos, ~200 events a second) free of any marshalling.
        private const ulong ObservedTimePos = 1;
        private const ulong ObservedDuration = 2;
        private const ulong ObservedPause = 3;
        private const ulong ObservedPausedForCache = 4;

        private void OnPropertyChanged(object sender, MpvPropertyChangedEventArgs args)
        {
            switch (args.Id)
            {
                case ObservedTimePos:
                    if (args.HasValue)
                    {
                        _position = args.Number;
                        ReportPosition(false);
                    }
                    break;
                case ObservedDuration:
                    if (args.HasValue && Math.Abs(args.Number - _duration) > 0.001)
                    {
                        _duration = args.Number;
                        var duration = _duration;
                        Dispatch(() => DurationChanged?.Invoke(this, new AsyncMediaPlayerDurationChangedEventArgs(duration)));
                    }
                    break;
                case ObservedPause:
                    if (args.HasValue)
                    {
                        _paused = args.Number != 0;

                        // Only reflects a state that is already about playback: while the file is
                        // still opening, pause=no is not "playing".
                        if (_loaded)
                        {
                            SetState(_paused ? AsyncMediaPlayerState.Paused : AsyncMediaPlayerState.Playing);
                        }
                    }
                    break;
                case ObservedPausedForCache:
                    if (args.HasValue && _loaded)
                    {
                        if (args.Number != 0)
                        {
                            SetState(AsyncMediaPlayerState.Buffering);
                        }
                        else
                        {
                            SetState(_paused ? AsyncMediaPlayerState.Paused : AsyncMediaPlayerState.Playing);
                        }
                    }
                    break;
            }
        }

        private void OnFileLoaded(object sender, EventArgs e)
        {
            _loaded = true;

            var pending = _pendingPosition;
            _pendingPosition = double.NaN;

            if (!double.IsNaN(pending) && pending > 0)
            {
                SeekImpl(pending);
            }

            SetState(_paused ? AsyncMediaPlayerState.Paused : AsyncMediaPlayerState.Playing);

            // The libvlc player reported buffering up to 100 and PlaybackService reads exactly one
            // thing out of it: a cache of 100 is when an unlistened voice note or video note is
            // marked as opened. With mpv there is no percentage to report -- the file is either
            // playing or it is not -- so "playback actually started" is the honest equivalent.
            Dispatch(() => Buffering?.Invoke(this, new AsyncMediaPlayerBufferingEventArgs(100)));
        }

        private void OnEndFile(object sender, MpvEndFileEventArgs e)
        {
            _loaded = false;

            switch (e.Reason)
            {
                case MpvEndFileReason.Eof:
                    _position = _duration;
                    ReportPosition(true);
                    SetState(AsyncMediaPlayerState.Ended);
                    Dispatch(() => EndReached?.Invoke(this, null));
                    break;
                case MpvEndFileReason.Error:
                    UnigramNative.Report(Logger.LogLevel.Error, $"mpv could not play the file: {MpvClient.ErrorString(e.Error)}");
                    SetState(AsyncMediaPlayerState.Error);
                    Dispatch(() => EncounteredError?.Invoke(this, null));
                    break;
                case MpvEndFileReason.Stop:
                    // Ours: Stop() or a replacing loadfile. Neither is the end of anything, and
                    // reporting it as one would make PlaybackService advance the playlist.
                    break;
            }
        }

        private void OnLogMessage(object sender, MpvLogEventArgs e)
        {
            var handler = Log;
            if (handler == null)
            {
                return;
            }

            var level = e.Level switch
            {
                MpvLogLevel.Fatal or MpvLogLevel.Error => AsyncMediaPlayerLogLevel.Error,
                MpvLogLevel.Warn => AsyncMediaPlayerLogLevel.Warning,
                MpvLogLevel.Info => AsyncMediaPlayerLogLevel.Notice,
                _ => AsyncMediaPlayerLogLevel.Debug
            };

            var args = new AsyncMediaPlayerLogEventArgs(level, e.Text, e.Prefix, string.Empty, 0);
            Dispatch(() => handler.Invoke(this, args));
        }

        private void ReportPosition(bool force)
        {
            var now = DateTime.UtcNow;

            if (!force && now - _positionReported < PositionInterval)
            {
                return;
            }

            _positionReported = now;

            var position = _position;
            Dispatch(() => PositionChanged?.Invoke(this, new AsyncMediaPlayerPositionChangedEventArgs(position)));
        }

        private void SetState(AsyncMediaPlayerState state)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;

            Dispatch(() =>
            {
                StateChanged?.Invoke(this, new AsyncMediaPlayerStateChangedEventArgs(state));

                switch (state)
                {
                    case AsyncMediaPlayerState.Playing:
                        Playing?.Invoke(this, null);
                        break;
                    case AsyncMediaPlayerState.Paused:
                        Paused?.Invoke(this, null);
                        break;
                    case AsyncMediaPlayerState.Stopped:
                        Stopped?.Invoke(this, null);
                        break;
                }
            });
        }

        #endregion

        public void Play(IAsyncMediaPlayerSource stream)
        {
            if (stream == null)
            {
                return;
            }

            var client = EnsureClient();
            if (client == null)
            {
                return;
            }

            ReleaseSource();

            _source = stream;
            _loaded = false;
            _paused = false;
            _position = 0;
            _duration = 0;
            _positionReported = DateTime.MinValue;

            // A position assigned while the previous track was unloaded must not leak into this
            // one; the only legal pending seek is the one ClearImpl sets right AFTER this call.
            _pendingPosition = double.NaN;

            try
            {
                stream.Open();
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "media source refused to open", ex);
            }

            SetState(AsyncMediaPlayerState.Opening);

            if (_protocol == null)
            {
                // No custom protocol: the only thing left is the file on disk, which for a fully
                // downloaded track is perfectly playable.
                LoadFile(client, stream.FilePath);
                return;
            }

            _uri = _protocol.Add(stream);
            LoadFile(client, _uri);
        }

        public void Play(Uri uri)
        {
            var client = EnsureClient();
            if (client == null || uri == null)
            {
                return;
            }

            ReleaseSource();

            _loaded = false;
            _paused = false;
            _position = 0;
            _duration = 0;
            _pendingPosition = double.NaN;

            SetState(AsyncMediaPlayerState.Opening);
            LoadFile(client, uri.IsFile ? uri.LocalPath : uri.ToString());
        }

        private void LoadFile(MpvClient client, string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                SetState(AsyncMediaPlayerState.Error);
                return;
            }

            client.SetProperty("speed", _rate <= 0 ? 1 : _rate);
            client.SetProperty("pause", false);

            var start = _pendingPosition;
            var result = -1;

            if (!double.IsNaN(start) && start > 0)
            {
                // The four-argument form (url, flags, index, options) is mpv >= 0.38; when it is
                // refused the position is applied on file-loaded instead, which costs a few
                // milliseconds of audio from the start of the track and nothing else.
                result = client.Command("loadfile", target, "replace", "0",
                    "start=" + start.ToString("0.###", CultureInfo.InvariantCulture));

                if (result >= 0)
                {
                    _pendingPosition = double.NaN;
                }
            }

            if (result < 0)
            {
                result = client.Command("loadfile", target, "replace");
            }

            if (result < 0)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"mpv loadfile failed: {MpvClient.ErrorString(result)}");
                SetState(AsyncMediaPlayerState.Error);
            }
        }

        /// <summary>
        /// Resume, or restart the track that just ended. <c>PlaybackService.PlayImpl</c> calls
        /// <c>Stop()</c> first when the state is Ended, so by the time this runs there is nothing
        /// loaded and the source has to be handed to mpv again.
        /// </summary>
        public void Play()
        {
            var client = _client;
            if (client == null)
            {
                if (_source != null)
                {
                    Play(_source);
                }

                return;
            }

            if (_loaded)
            {
                client.SetProperty("pause", false);
                return;
            }

            if (_source != null)
            {
                Play(_source);
            }
        }

        public void Stop()
        {
            var client = _client;
            if (client == null)
            {
                return;
            }

            _loaded = false;
            _position = 0;

            client.Command("stop");

            SetState(AsyncMediaPlayerState.Stopped);
            ReportPosition(true);
        }

        public void Pause()
        {
            Pause(true);
        }

        public void Pause(bool pause)
        {
            var client = _client;
            if (client == null)
            {
                return;
            }

            client.SetProperty("pause", pause);
        }

        public void Toggle()
        {
            Pause(!_paused);
        }

        public void Close()
        {
            MpvClient client;
            MpvStreamProtocol protocol;

            lock (_lock)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                client = _client;
                protocol = _protocol;

                _client = null;
                _protocol = null;
            }

            _loaded = false;

            // Order matters: the core has to be gone before the protocol retires its cookie,
            // because mpv_terminate_destroy is what closes any stream still open -- and stream_cb.h
            // says the protocol stays registered until that call RETURNS. Terminate's wait is
            // bounded (a download stuck behind a dead network outlasts it), so "gone" is what it
            // answers, not what the call order suggests: when it says no, the protocol is kept
            // whole and released later, because a core that is still up can still call into it.
            if (client == null || client.Terminate())
            {
                protocol?.Dispose();
            }
            else if (protocol != null)
            {
                protocol.Abandon();
                client.WhenTerminated(protocol.Dispose);
            }

            ReleaseSource();
            SetState(AsyncMediaPlayerState.NothingSpecial);
        }

        private void ReleaseSource()
        {
            var uri = _uri;
            _uri = null;

            if (uri != null)
            {
                _protocol?.Release(uri);
            }

            // The source itself is closed by the stream's close callback (or by nobody, if mpv
            // never opened it), so it is not closed here: a Play() that replaces a track would
            // otherwise close the source of the track mpv is still tearing down.
            _source = null;
        }

        public AsyncMediaPlayerState State => _state;

        public bool IsPlaying => _state == AsyncMediaPlayerState.Playing;

        public bool CanPause => _loaded;

        public double Duration => _duration;

        public double Position
        {
            get => _position;
            set => SeekImpl(value);
        }

        public void Seek(double value, bool relative)
        {
            SeekImpl(relative ? _position + value : value);
        }

        private void SeekImpl(double position)
        {
            if (double.IsNaN(position) || double.IsInfinity(position))
            {
                return;
            }

            position = Math.Max(0, position);

            var client = _client;
            if (client == null || !_loaded)
            {
                // Assigned before the file is loaded: applied by LoadFile as a start= option, or
                // by OnFileLoaded if this build of mpv refused it.
                _pendingPosition = position;
                _position = position;
                return;
            }

            _position = position;

            // Exact, and exactly this cheap: backwards seeks in .ogg/opus were measured at 4-7 ms
            // with time-pos landing on the target to the millisecond, which is why the shared
            // SeekImpl drops its stop-and-restart workaround under #if LINUX.
            client.SetProperty("time-pos", position);
            ReportPosition(true);
        }

        public double Rate
        {
            get => _rate;
            set
            {
                _rate = value <= 0 ? 1 : value;
                _client?.SetProperty("speed", _rate);
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                _client?.SetProperty("volume", MpvVolume(value));

                Dispatch(() => VolumeChanged?.Invoke(this, null));
            }
        }

        /// <summary>
        /// <c>SettingsService.VolumeLevel</c> (0..1, linear in amplitude, which is what libvlc took
        /// upstream) to mpv's <c>volume</c> percentage.
        ///
        /// <para><b>mpv's volume is CUBIC</b>, and that is measured, not assumed: raising it from
        /// 60 to 100 multiplied the signal at the output device by <b>4,59</b>, where linear would
        /// be 1,67 and a cube 4,63 (spikes/AudioSpike, <c>volumeGainRatio</c>). Passing the slider
        /// through unchanged would make the user's 50 % sound like 12,5 %, so the cube is undone
        /// here and the slider keeps meaning what it meant on Windows.</para>
        /// </summary>
        private static double MpvVolume(double value)
        {
            return Math.Cbrt(Math.Clamp(value, 0, 1)) * 100;
        }

        public bool Mute
        {
            get => _mute;
            set
            {
                _mute = value;
                _client?.SetProperty("mute", value);
            }
        }

        public event TypedEventHandler<AsyncMediaPlayer, AsyncMediaPlayerStateChangedEventArgs> StateChanged;
        public event TypedEventHandler<AsyncMediaPlayer, object> VideoOut;
        public event TypedEventHandler<AsyncMediaPlayer, AsyncMediaPlayerStreamSelectedEventArgs> StreamSelected;
        public event TypedEventHandler<AsyncMediaPlayer, object> EndReached;
        public event TypedEventHandler<AsyncMediaPlayer, AsyncMediaPlayerBufferingEventArgs> Buffering;
        public event TypedEventHandler<AsyncMediaPlayer, AsyncMediaPlayerPositionChangedEventArgs> PositionChanged;
        public event TypedEventHandler<AsyncMediaPlayer, AsyncMediaPlayerDurationChangedEventArgs> DurationChanged;
        public event TypedEventHandler<AsyncMediaPlayer, object> Playing;
        public event TypedEventHandler<AsyncMediaPlayer, object> Paused;
        public event TypedEventHandler<AsyncMediaPlayer, object> Stopped;
        public event TypedEventHandler<AsyncMediaPlayer, object> VolumeChanged;
        public event TypedEventHandler<AsyncMediaPlayer, object> EncounteredError;
        public event TypedEventHandler<AsyncMediaPlayer, AsyncMediaPlayerLogEventArgs> Log;
    }
}
