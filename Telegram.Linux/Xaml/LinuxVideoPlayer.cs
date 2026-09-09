//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.UI.Xaml;
using Telegram.Common;
using Telegram.Native;
using Telegram.Native.Media;
using Telegram.Services;
using Telegram.Streams;
using Telegram.ViewModels.Gallery;

namespace Telegram.Controls
{
    /// <summary>
    /// The gallery's video player on Linux: <b>mpv plays the sound and keeps the clock, our own
    /// FFmpeg decoder draws the pixels</b>, and the pixels are presented against mpv's
    /// <c>time-pos</c>.
    ///
    /// <para>That split is the measured recommendation of <c>unigram-linux/fase1/plan-media.md</c>
    /// and neither half of it is arbitrary. mpv's software renderer costs <b>55 ms a frame</b> at
    /// this screen's 2736x1554 and <b>31 ms</b> at 1080p, which is not 30 fps on a Skylake; the
    /// decoder already inside <c>libunigram-native.so</c> costs <b>22.8</b> and <b>12.5</b> for the
    /// same frames. The other way round, mpv gives away for free the three things a hand-written
    /// audio path would cost weeks: the output device, resampling, and speed with pitch correction
    /// (<c>scaletempo2</c>). So: <c>vid=no</c> on the mpv side, and a decode thread on ours.</para>
    ///
    /// <para>The clock is mpv's when the file has an audio stream — the audio clock is the master
    /// in any player — and a plain stopwatch when it does not, which is the case for a great many
    /// Telegram videos and for every GIF.</para>
    ///
    /// <para>What this is NOT: <c>NativeVideoPlayer</c>, which is a SwapChainPanel fed by libvlc's
    /// D3D11 output and stays Windows-only. The surface both expose (<see cref="VideoPlayerBase"/>)
    /// is the same, so <c>GalleryContent</c> and the transport controls above cannot tell them
    /// apart.</para>
    /// </summary>
    public sealed partial class LinuxVideoPlayer : VideoPlayerBase
    {
        // Absolute cap on the long side, for the case where the control has not been arranged yet
        // and there is no presentation size to go by. 2736 is this panel's long side: decoding to
        // more pixels than the screen has is work thrown away in any case.
        private const int MaxSide = 2736;

        private readonly VideoSurface _surface;

        private AsyncMediaPlayer _core;
        private VideoAnimation _animation;

        // The reader the decoder pulls its bytes through. Kept because Close() has to be able to
        // unblock a read that is waiting on a download -- including the one inside the open, which
        // happens before there is an _animation to Stop().
        private RemoteFileSource _pixels;

        private Thread _decoder;
        private readonly ManualResetEventSlim _consumed = new(true);
        private readonly ManualResetEventSlim _resumed = new(false);
        private readonly object _lock = new();

        private volatile bool _stopping;
        private volatile bool _pending;
        private double _pendingPosition;
        private double _seekTo = -1;

        // The video's own geometry, rotation applied. The surface is decoded smaller than this
        // whenever the panel shows it smaller -- see VideoSurface.Choose.
        private int _nativeWidth;
        private int _nativeHeight;

        // Set from the UI thread when the control is resized, applied by the decode thread, in the
        // same shape as _seekTo: the demuxer and the surface belong to that thread.
        private long _requestedSize;

        private GalleryMedia _video;
        private double _initialPosition;
        private long _bufferedToken;

        private bool _hasAudio;
        private bool _isPlaying;
        private bool _firstFrame;

        // The clip has run to its end and nothing has moved it since. It matters because the two
        // halves end up in different states: with keep-open=no mpv unloads the file, so the next
        // Play() reloads it and the sound starts over from zero, while the decode thread is parked
        // at end of stream and would stay there. Rewinding it is what Play() uses this for.
        private bool _ended;

        // Used only when the file has no audio stream, so mpv has nothing to keep time with.
        private readonly Stopwatch _fallback = new();
        private double _fallbackBase;

        private double _rate = 1;
        private bool _mute;
        private double _volume = 1;
        private double _position;
        private double _duration;
        private double _buffered;

        // Frames in and frames out, reported once a second when UNIGRAM_MEDIA_PROBE is on. A
        // player that shows one frame and stops looks exactly like one that never started, and
        // these two numbers say which it was.
        private static readonly bool Probing = Environment.GetEnvironmentVariable("UNIGRAM_MEDIA_PROBE") is string probe && probe.Length > 0 && probe != "0";
        private int _decoded;
        private int _presented;
        private int _dropped;
        private long _decodeTicks;
        private int _resynced;
        private long _presentTicks;
        private DateTime _reported;

        public LinuxVideoPlayer()
        {
            _surface = VideoSurface.Create();

            Content = _surface.Element;

            SizeChanged += OnSizeChanged;
            Connected += OnConnected;
            Disconnected += OnDisconnected;
        }

        #region Lifetime

        private void OnConnected(object sender, RoutedEventArgs e)
        {
            IsUnloadedExpected = false;

            CompositionRenderingClock.Rendering += OnRendering;

            if (_video != null)
            {
                var video = _video;
                var position = _initialPosition;

                _video = null;
                _initialPosition = 0;

                Play(video, position);
            }
        }

        /// <summary>
        /// The panel got bigger or smaller, so the number of pixels worth decoding changed. The
        /// decode thread is the one that owns the surface while playback is running, so this only
        /// leaves a request; <see cref="DecodeLoop"/> picks it up between frames.
        ///
        /// <para>With hysteresis, and asymmetric on purpose: a resize costs two frame-sized
        /// allocations and a dropped frame, and a window being dragged raises this event dozens of
        /// times a second. Growing is worth it as soon as the surface is noticeably soft (15%);
        /// shrinking only when it is wasting a lot (30%), because a surface that is too big only
        /// costs time, while one that is too small costs quality.</para>
        /// </summary>
        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_nativeWidth <= 0 || _surface == null || !_surface.SupportsLiveResize)
            {
                return;
            }

            var (width, height) = VideoSurface.Choose(_nativeWidth, _nativeHeight, e.NewSize.Width, e.NewSize.Height, RasterizationScale, MaxSide);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var current = _surface.PixelWidth;
            if (current > 0 && width < current * 1.15 && width > current * 0.7)
            {
                return;
            }

            Volatile.Write(ref _requestedSize, ((long)width << 32) | (uint)height);
            _resumed.Set();
        }

        /// <summary>
        /// The scale the panel really rasterizes at. <c>XamlRoot.RasterizationScale</c> answers 1
        /// on a 2x screen and never corrects itself (PORTING.md section 6), so it only wins when it
        /// says something better than 1 -- same rule, and the same fallback, as
        /// <c>AnimatedImageBase.ResolveRasterizationScale</c>.
        /// </summary>
        private double RasterizationScale
        {
            get
            {
                var scale = XamlRoot?.RasterizationScale ?? 0;
                if (scale > 1)
                {
                    return scale;
                }

                var display = DisplayScale.Current;
                return display > 1 ? display : scale > 0 ? scale : 1;
            }
        }

        private void OnDisconnected(object sender, RoutedEventArgs e)
        {
            if (IsUnloadedExpected)
            {
                return;
            }

            CompositionRenderingClock.Rendering -= OnRendering;
            Close();
        }

        private void Close()
        {
            _stopping = true;
            _resumed.Set();
            _consumed.Set();

            // Unblocks a read that is waiting on a download that may never finish; without it the
            // decode thread would not come back until TDLib did. Both are needed: Stop() aborts a
            // decode in flight, and closing the reader aborts a read that is blocked BEFORE there
            // is an animation at all, which is where the open of a streamed file spends its time.
            _animation?.Stop();

            var pixels = _pixels;
            _pixels = null;
            pixels?.Close();

            var decoder = _decoder;
            _decoder = null;

            if (decoder != null && decoder.IsAlive && decoder != Thread.CurrentThread)
            {
                decoder.Join(2000);
            }

            lock (_lock)
            {
                _animation?.Dispose();
                _animation = null;
            }

            if (_core != null)
            {
                _core.PositionChanged -= OnCorePositionChanged;
                _core.DurationChanged -= OnCoreDurationChanged;
                _core.EndReached -= OnCoreEndReached;
                _core.Playing -= OnCorePlaying;
                _core.Paused -= OnCorePaused;
                _core.EncounteredError -= OnCoreFailed;

                _core.Close();
                _core = null;
            }

            _fallback.Reset();
            _isPlaying = false;
            _ended = false;

            UpdateManager.Unsubscribe(this, ref _bufferedToken);
            OnClosed();
        }

        #endregion

        #region VideoPlayerBase

        public override bool IsUnloadedExpected { get; set; }

        public override void Play(GalleryMedia video, double position)
        {
            if (video == null)
            {
                return;
            }

            if (!IsConnected)
            {
                // Nothing can be measured or drawn before the control is in the tree; the same
                // deferral NativeVideoPlayer does.
                _video = video;
                _initialPosition = position;
                return;
            }

            Close();
            _stopping = false;

            UpdateManager.Subscribe(this, video.ClientService, video.File, ref _bufferedToken, UpdateBuffered);

            _position = position;
            _fallbackBase = position;
            _firstFrame = false;
            _pending = false;
            _consumed.Set();

            if (position > 0)
            {
                _seekTo = position;
            }

            Volatile.Write(ref _requestedSize, 0);

            // Two readers of the same file, one each: the two want different ranges -- mpv goes to
            // the end for the moov atom, we walk forward through the frames.
            //
            // NOTE (see the streaming section of PORTING.md): TDLib keeps ONE download window per
            // file, and both of these readers move it with every DownloadFile they send, so on a
            // file that is still downloading they take it off each other. The reader that loses is
            // the one that needs the most bytes per second, which is this one.
            _pixels = new RemoteFileSource(video.ClientService, video.File, video.Duration);

            // Opening is a READ, and on a file that is still downloading that read blocks until
            // TDLib has the bytes -- for an mp4 whose moov atom is at the end, bytes hundreds of
            // megabytes away. It used to happen right here, on the UI thread, inside the click
            // that opened the gallery: nothing else could run, no frame could be composed and no
            // DispatcherTimer could tick until the download caught up. Everything from the open
            // onwards now happens on the decode thread, and Close() unblocks it through
            // RemoteFileSource.Close().
            _decoder = new Thread(() => OpenAndDecode(video, position))
            {
                IsBackground = true,
                Name = "Unigram video"
            };

            _ended = false;
            _isPlaying = true;
            _resumed.Set();
            _decoder.Start();
        }

        /// <summary>
        /// Decode thread. Opens the file, hands the geometry back to the UI thread, and then runs
        /// <see cref="DecodeLoop"/> for the rest of playback.
        /// </summary>
        private void OpenAndDecode(GalleryMedia video, double position)
        {
            VideoAnimation animation;

            try
            {
                animation = VideoAnimation.LoadFromFile(_pixels, false, false, false);
            }
            catch (Exception ex)
            {
                Logger.Error("video: opening the file", ex);
                animation = null;
            }

            if (animation == null)
            {
                if (!_stopping)
                {
                    Logger.Error("video: the decoder could not open the file");
                    DispatcherQueue.TryEnqueue(OnFailed);
                }

                return;
            }

            var width = animation.PixelWidth;
            var height = animation.PixelHeight;

            if (width <= 0 || height <= 0)
            {
                Logger.Error($"video: the decoder reports {width}x{height}");
                animation.Dispose();

                if (!_stopping)
                {
                    DispatcherQueue.TryEnqueue(OnFailed);
                }

                return;
            }

            // Rotation is metadata the decoder applies itself when it scales, so the surface is
            // the rotated geometry.
            if (animation.Rotation is 90 or 270)
            {
                (width, height) = (height, width);
            }

            lock (_lock)
            {
                if (_stopping)
                {
                    animation.Dispose();
                    return;
                }

                _animation = animation;
            }

            _hasAudio = animation.HasAudio;
            _duration = animation.Duration / 1000d;
            _nativeWidth = width;
            _nativeHeight = height;

            // The surface, mpv and every event go back to the UI thread. Nothing here waits for
            // that to finish: Close() runs on the UI thread and joins this one, so a wait would be
            // a deadlock. The loop just does not decode until the surface has a size.
            DispatcherQueue.TryEnqueue(() => StartCore(video, position, animation, width, height));

            DecodeLoop();
        }

        /// <summary>
        /// UI thread. Everything the open produced that only the UI thread may touch: the surface
        /// (a WriteableBitmap has to be built here), mpv, and the events the gallery listens to.
        /// </summary>
        private void StartCore(GalleryMedia video, double position, VideoAnimation animation, int width, int height)
        {
            if (_stopping || _animation != animation)
            {
                return;
            }

            // The size the frame is SHOWN at, not the size the file is: decoding to more pixels
            // than the panel has costs swscale on the way in and the compositor on the way out,
            // and both are measured (VideoSurface.Choose).
            var (surfaceWidth, surfaceHeight) = VideoSurface.Choose(width, height, ActualWidth, ActualHeight, RasterizationScale, MaxSide);

            _surface.Configure(surfaceWidth, surfaceHeight);

            OnTrackChanged(width, height);
            OnDurationChanged(_duration);

            if (_hasAudio)
            {
                _core = new AsyncMediaPlayer(new AsyncMediaPlayerOptions
                {
                    Mute = _mute,
                    Volume = _volume,
                    Rate = _rate,
                    Debug = AppSettings.VerbosityLevel >= 4
                });

                _core.PositionChanged += OnCorePositionChanged;
                _core.DurationChanged += OnCoreDurationChanged;
                _core.EndReached += OnCoreEndReached;
                _core.Playing += OnCorePlaying;
                _core.Paused += OnCorePaused;
                _core.EncounteredError += OnCoreFailed;

                _core.Play(new RemoteFileSource(video.ClientService, video.File, video.Duration));

                if (position > 0)
                {
                    _core.Position = position;
                }
            }
            else
            {
                _fallback.Restart();
            }

            Logger.Info($"video: playing {_surface.PixelWidth}x{_surface.PixelHeight} on {_surface.GetType().Name} "
                + $"({animation.PixelWidth}x{animation.PixelHeight} rotated {animation.Rotation}), "
                + $"{animation.FrameRate:F2} fps, {_duration:F2}s, audio {_hasAudio}, from {position:F2}s");

            OnReady(true);
            OnIsPlayingChanged(true);
        }

        public override void Play()
        {
            // _decoder as well as _animation: while a streamed file is being opened there is no
            // animation yet, and the transport controls are already on screen and clickable.
            if (_animation == null && _decoder == null)
            {
                return;
            }

            // Playing again after the clip ended is playing it again from the start, and both
            // halves have to be told: mpv reloads the file by itself (AsyncMediaPlayer.Play finds
            // it unloaded), but the decode thread is sitting at end of stream producing nothing.
            // Without this the sound came back and the picture stayed frozen on the last frame --
            // measured in the app: 21 s of audio against 0 frames decoded.
            if (_ended)
            {
                _ended = false;

                _position = 0;
                _fallbackBase = 0;
                _fallback.Reset();

                Volatile.Write(ref _seekTo, 0);
                OnPositionChanged(0);
            }

            _isPlaying = true;

            if (_core != null)
            {
                _core.Play();
            }
            else
            {
                _fallback.Start();
            }

            _resumed.Set();
            OnIsPlayingChanged(true);
        }

        public override void Pause()
        {
            if (_animation == null && _decoder == null)
            {
                return;
            }

            _isPlaying = false;
            _resumed.Reset();

            if (_core != null)
            {
                _core.Pause();
            }
            else
            {
                _fallbackBase = Clock();
                _fallback.Reset();
            }

            OnIsPlayingChanged(false);
        }

        public override void Toggle()
        {
            if (_isPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        public override void Clear()
        {
            Close();

            _surface.Dispose();

            _nativeWidth = 0;
            _nativeHeight = 0;
        }

        /// <summary>
        /// <b>Relative</b>, like the two Windows players: NativeVideoPlayer forwards to
        /// <c>_core.Seek(value, relative: true)</c> and WebVideoPlayer to <c>playerAddTime(value)</c>.
        /// Every caller is a skip button or a keyboard accelerator asking for +/-5, +/-10 or +/-30
        /// seconds; the absolute one is <see cref="Position"/>. This used to assign Position, which
        /// was invisible while the transport controls were an inert stub - the only callers live in
        /// them - and would have turned "back 10 seconds" into "jump to second 10" and
        /// "forward 10 seconds" (from anywhere) into the same second 10.
        /// </summary>
        public override void Seek(double value)
        {
            Position = Math.Max(0, _position + value);
        }

        public override double Position
        {
            get => _position;
            set
            {
                _position = value;
                _ended = false;

                if (_core != null)
                {
                    _core.Position = value;
                }
                else
                {
                    _fallbackBase = value;
                    _fallback.Restart();

                    if (!_isPlaying)
                    {
                        _fallback.Reset();
                    }
                }

                // The decode thread does the seek: the demuxer is not thread safe and it is the
                // only one allowed to touch it.
                Volatile.Write(ref _seekTo, value);
                _resumed.Set();

                OnPositionChanged(value);
            }
        }

        public override double Duration => _duration;

        public override double Buffered => _buffered;

        public override bool IsPlaying => _isPlaying;

        public override double Rate
        {
            get => _rate;
            set
            {
                _rate = value;

                if (_core != null)
                {
                    _core.Rate = value;
                }
            }
        }

        public override bool Mute
        {
            get => _mute;
            set
            {
                _mute = value;

                if (_core != null)
                {
                    _core.Mute = value;
                }
            }
        }

        public override double Volume
        {
            get => _volume;
            set
            {
                _volume = value;

                if (_core != null)
                {
                    _core.Volume = value;
                }

                OnVolumeChanged(value);
            }
        }

        #endregion

        #region The clock

        /// <summary>
        /// Where playback is, in seconds. mpv's <c>time-pos</c> when there is sound to keep time
        /// with — the audio clock is what everything else follows — and a stopwatch when there is
        /// not.
        /// </summary>
        private double Clock()
        {
            if (_core != null)
            {
                return _core.Position;
            }

            return _fallbackBase + _fallback.Elapsed.TotalSeconds * (_rate <= 0 ? 1 : _rate);
        }

        private void OnCorePositionChanged(AsyncMediaPlayer sender, AsyncMediaPlayerPositionChangedEventArgs args)
        {
            _position = args.Position;
            OnPositionChanged(args.Position);
        }

        private void OnCoreDurationChanged(AsyncMediaPlayer sender, AsyncMediaPlayerDurationChangedEventArgs args)
        {
            // The container's duration as mpv read it is the better of the two; ours comes from
            // the same file but is rounded to milliseconds.
            if (args.Duration > 0)
            {
                _duration = args.Duration;
                OnDurationChanged(args.Duration);
            }
        }

        private void OnCoreEndReached(AsyncMediaPlayer sender, object args)
        {
            Restart();
        }

        private void OnCorePlaying(AsyncMediaPlayer sender, object args)
        {
            _isPlaying = true;
            _resumed.Set();
            OnIsPlayingChanged(true);
        }

        private void OnCorePaused(AsyncMediaPlayer sender, object args)
        {
            _isPlaying = false;
            _resumed.Reset();
            OnIsPlayingChanged(false);
        }

        private void OnCoreFailed(AsyncMediaPlayer sender, object args)
        {
            OnFailed();
        }

        private void Restart()
        {
            if (IsLoopingEnabled)
            {
                Position = 0;
                Play();
            }
            else
            {
                _ended = true;
                _isPlaying = false;
                _resumed.Reset();
                OnIsPlayingChanged(false);
            }
        }

        // 12.10.2 dropped the subscriber argument from UpdateHandler<T>: it is now void (T update).
        private void UpdateBuffered(Td.Api.File update)
        {
            var offset = update.Local.DownloadOffset + update.Local.DownloadedPrefixSize;
            OnBufferedChanged(_buffered = update.Size > 0 ? (double)offset / update.Size : 0);
        }

        #endregion

        #region Decode and present

        /// <summary>
        /// Decodes one frame ahead and hands it over. It does not sleep against a frame rate: the
        /// handover is what paces it, because <see cref="OnRendering"/> only takes the frame when
        /// the clock has reached its timestamp. So a stall in the download or a slow frame costs
        /// exactly itself and nothing accumulates.
        /// </summary>
        private void DecodeLoop()
        {
            while (!_stopping)
            {
                try
                {
                    if (!_resumed.Wait(100))
                    {
                        continue;
                    }

                    // Wait for the last frame to be taken before overwriting the buffer it is in.
                    if (!_consumed.Wait(100))
                    {
                        continue;
                    }

                    if (_stopping)
                    {
                        return;
                    }

                    // The surface is configured on the UI thread once the open has come back, so
                    // the first turns of this loop can arrive before it exists.
                    if (_surface.PixelWidth == 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    var seek = Volatile.Read(ref _seekTo);
                    var resize = Interlocked.Exchange(ref _requestedSize, 0);

                    lock (_lock)
                    {
                        if (_animation == null)
                        {
                            return;
                        }

                        if (resize != 0)
                        {
                            _surface.Configure((int)(resize >> 32), (int)(resize & 0xFFFFFFFF));
                        }

                        if (seek >= 0)
                        {
                            Volatile.Write(ref _seekTo, -1);

                            _animation.PrepareToSeek();
                            _animation.SeekToMilliseconds((long)(seek * 1000), true);
                        }

                        var decoding = Stopwatch.GetTimestamp();
                        var produced = _surface.Render(_animation, out double seconds);

                        _decodeTicks += Stopwatch.GetTimestamp() - decoding;

                        if (!produced)
                        {
                            Report("render produced no frame");

                            // End of stream, or a frame the decoder could not build. Either way
                            // there is nothing to hand over; the audio side decides what "the end"
                            // means.
                            if (_core == null)
                            {
                                DispatcherQueue.TryEnqueue(Restart);
                                return;
                            }

                            Thread.Sleep(30);
                            continue;
                        }

                        _pendingPosition = seconds;
                        _decoded++;

                        // Frame dropping, and it belongs here rather than in the presenter,
                        // because the presenter cannot un-see a frame it has already put up.
                        // (The original reason was different and no longer holds: a late frame
                        // used to cost a 3.7 MB copy, a texture upload and a composition pass.
                        // With VideoSurface presenting is Invalidate() and nothing else, so what
                        // is left is the only reason that mattered anyway -- showing a frame whose
                        // timestamp has already gone past is showing the wrong picture.)
                        var late = Clock() - seconds;

                        if (_isPlaying && late > 0.25)
                        {
                            _dropped++;

                            // Far enough behind that catching up frame by frame never will: ask
                            // the demuxer to jump. Not precise -- landing on the nearest keyframe
                            // is the point, and the frames after it are the ones we want anyway.
                            if (late > 1.5)
                            {
                                _animation.PrepareToSeek();
                                _animation.SeekToMilliseconds((long)(Clock() * 1000), false);
                                _resynced++;
                            }

                            continue;
                        }

                        _surface.Publish();
                    }

                    _consumed.Reset();
                    _pending = true;
                }
                catch (Exception ex)
                {
                    Logger.Error("video: decode loop", ex);
                    return;
                }
            }
        }

        /// <summary>
        /// One tick of the real frame clock (<see cref="CompositionRenderingClock"/>, because
        /// <c>CompositionTarget.Rendering</c> is not one in Uno — PORTING.md §6). Presents the
        /// waiting frame when the clock has caught up with its timestamp.
        /// </summary>
        private void OnRendering(object sender, object e)
        {
            if (!_pending || _surface.PixelWidth == 0)
            {
                return;
            }

            var clock = Clock();

            // A frame whose timestamp is still ahead waits; one that is late goes out now, and the
            // next pass through the loop will decode the following one immediately, which is the
            // frame dropping.
            //
            // Paused counts as ahead, and that is not a detail: while paused the clock does not
            // move, so with an "and playing" in this test every frame the decoder produced went
            // straight to the screen and the picture ran away on its own. Measured in the app
            // after a seek while paused: the sound sat at 6,42 s while the picture galloped to the
            // end and round again, 5 800 frames of a 640 frame clip, with the decode thread at
            // full tilt the whole time. Holding the frame here is also what parks that thread:
            // OnRendering is the only thing that sets _consumed.
            if (_pendingPosition > clock + 0.004 && _firstFrame)
            {
                return;
            }

            try
            {
                var started = Stopwatch.GetTimestamp();

                _surface.Present();

                _presentTicks += Stopwatch.GetTimestamp() - started;
            }
            catch (Exception ex)
            {
                Logger.Error("video: present", ex);
            }

            _pending = false;
            _consumed.Set();
            _presented++;

            Report(null);

            if (!_firstFrame)
            {
                _firstFrame = true;
                OnFirstFrameReady(true);
            }

            if (_core == null)
            {
                _position = _pendingPosition;
                OnPositionChanged(_position);
            }
        }

        private void Report(string what)
        {
            if (!Probing)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (what == null && now - _reported < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _reported = now;

            Logger.Info($"video probe: {_surface.PixelWidth}x{_surface.PixelHeight}, decoded {_decoded}, presented {_presented}, dropped {_dropped}, resynced {_resynced}, "
                + $"decode {(_decoded > 0 ? _decodeTicks * 1000d / Stopwatch.Frequency / _decoded : 0):F2} ms/frame, "
                + $"clock {Clock():F2}s, frame {_pendingPosition:F2}s, present {(_presented > 0 ? _presentTicks * 1000d / Stopwatch.Frequency / _presented : 0):F2} ms/frame, playing {_isPlaying}"
                + (what == null ? string.Empty : $" -- {what}"));
        }

        #endregion
    }
}
