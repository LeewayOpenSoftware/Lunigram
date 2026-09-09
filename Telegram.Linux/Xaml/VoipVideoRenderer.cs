//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Telegram.Common;
using Telegram.Native.Calls;

namespace Telegram.Controls.Calls
{
    /// <summary>
    /// P17-R1 — THE THING THAT PAINTS. Until this class, received call video reached the managed
    /// side and stopped there: <c>VoipVideoOutputSink</c> copies the three I420 planes out of
    /// WebRTC's decoding thread and exposes <c>CopyFrameTo</c> as the hand-off point, and
    /// <c>CopyFrameTo</c> had <b>zero callers in the whole tree</b>. Its only consumer,
    /// <c>Services/Calls/VoipVideoOutput.OnFrameReceived</c>, read the frame's width and height for
    /// the active/inactive state and threw the pixels away.
    ///
    /// <para><b>Why there was nothing to fix, only something to write.</b> On Windows the painting
    /// was <c>VoipVideoOutput.h</c>: 844 lines of Direct2D plus an HLSL shader doing I420 to RGB on
    /// the GPU. None of that ports. What does port is the decision <c>VideoSurface</c> already
    /// made and measured for the video player: an <c>SKCanvasElement</c> over two native BGRA
    /// planes, with <c>WriteableBitmap</c> as the fallback for a host where Skia elements are not
    /// available. This class is that surface driven by a call sink instead of by mpv.</para>
    ///
    /// <para><b>The threading, which is the part that matters.</b> Three threads, on purpose:
    /// <list type="bullet">
    /// <item>WebRTC's decoding thread raises <c>FrameReceived</c>. It does the least possible: sets
    /// a flag and pokes the worker. Nothing here may block it — it is the thread that also feeds
    /// the jitter buffer.</item>
    /// <item>A worker thread of our own does the I420 to BGRA expansion — about 900k pixels at 720p,
    /// per frame — into the surface's back plane, then publishes. <c>CopyFrameTo</c>'s own doc says
    /// it runs on the caller's thread on purpose so the decoding thread does not pay for it; this
    /// is that caller.</item>
    /// <item>The UI thread only invalidates. That is <c>Present</c>, and it is the whole of what
    /// the UI thread does per frame.</item>
    /// </list>
    /// Frames are <b>coalesced, not queued</b>: if the worker is still converting when the next one
    /// arrives, the newer frame simply replaces the older in the sink and the older is never drawn.
    /// A queue here would turn a slow host into a growing latency instead of a lower frame rate,
    /// which for a live call is the wrong trade.</para>
    ///
    /// <para><b>What this does NOT do, and why it is not drawn as if it did.</b>
    /// <list type="bullet">
    /// <item><b>Mirroring</b> (<c>VoipVideoOutputSink.IsMirrored</c>, the selfie flip on the local
    /// preview) is not applied. It needs a horizontal transform inside the shared Skia draw path,
    /// and that path is the one the video player uses and this batch cannot run. Left for the batch
    /// that can see it on screen.</item>
    /// <item><b>UniformToFill</b> is likewise not applied: <c>VideoSurface</c> letterboxes Uniform,
    /// which is right for a call window and wrong for the story viewport that asks for fill.</item>
    /// </list></para>
    /// </summary>
    public sealed class VoipVideoRenderer : IDisposable
    {
        private readonly VoipVideoOutputSink _sink;
        private readonly VideoSurface _surface;
        private readonly DispatcherQueue _dispatcher;
        private readonly Func<IntPtr, int, int, bool> _fill;

        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _worker;

        private int _frameWidth;
        private int _frameHeight;
        private int _configuring;
        private volatile bool _disposed;

        // Counters, so that "is it painting?" has an answer that does not depend on someone
        // looking at the screen. Read them from the debugger or from a diagnostic overlay.
        private long _framesReceived;
        private long _framesPainted;
        private long _framesDropped;

        public VoipVideoRenderer(VoipVideoOutputSink sink, DispatcherQueue dispatcher = null)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));

            _surface = VideoSurface.Create();
            _dispatcher = dispatcher ?? DispatcherQueue.GetForCurrentThread();
            if (_dispatcher == null)
            {
                Logger.Warning("VoipVideoRenderer: DispatcherQueue is null on current thread; UI frame presentation may not occur");
            }
            _fill = Fill;

            _worker = new Thread(Work)
            {
                IsBackground = true,
                Name = "VoipVideoRenderer"
            };
            _worker.Start();

            _sink.FrameReceived += OnFrameReceived;
        }

        /// <summary>The element to put in the tree. Stretches; the surface letterboxes inside it.</summary>
        public FrameworkElement Element => _surface.Element;

        public long FramesReceived => Interlocked.Read(ref _framesReceived);

        public long FramesPainted => Interlocked.Read(ref _framesPainted);

        public long FramesDropped => Interlocked.Read(ref _framesDropped);

        /// <summary>
        /// Puts a renderer for <paramref name="sink"/> into <paramref name="host"/> directly on top
        /// of <paramref name="placeholder"/>, matching its cell and its visibility.
        ///
        /// <para>Upstream hangs a <c>SpriteVisual</c> off the placeholder rectangle with
        /// <c>ElementCompositionPreview.SetElementChildVisual</c>. On Skia that call reaches the
        /// Linux shim and paints nothing, which is the other half of why no video ever appeared:
        /// even a renderer would have had nowhere to draw. A real element in the real tree does not
        /// depend on child visuals working.</para>
        /// </summary>
        public static VoipVideoRenderer Attach(Panel host, FrameworkElement placeholder, VoipVideoOutputSink sink)
        {
            if (host == null || placeholder == null || sink == null)
            {
                return null;
            }

            var renderer = new VoipVideoRenderer(sink);
            var element = renderer.Element;

            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            element.VerticalAlignment = VerticalAlignment.Stretch;

            // Same cell as the placeholder: the call page lays these out with Grid.RowSpan, and a
            // renderer that landed in row 0 would be a thin strip at the top instead of the window.
            Grid.SetRow(element, Grid.GetRow(placeholder));
            Grid.SetRowSpan(element, Grid.GetRowSpan(placeholder));
            Grid.SetColumn(element, Grid.GetColumn(placeholder));
            Grid.SetColumnSpan(element, Grid.GetColumnSpan(placeholder));

            element.Visibility = placeholder.Visibility;

            var index = host.Children.IndexOf(placeholder);
            if (index < 0)
            {
                host.Children.Add(element);
            }
            else
            {
                // Immediately above the placeholder, so the dark rectangle stays behind it as the
                // background it was drawn to be.
                host.Children.Insert(index + 1, element);
            }

            // The page drives the placeholder's Visibility from the call state; following it keeps
            // one owner for that decision instead of two that can disagree.
            placeholder.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,
                (sender, property) => element.Visibility = ((UIElement)sender).Visibility);

            return renderer;
        }

        private void OnFrameReceived(VoipVideoOutputSink sender, FrameReceivedEventArgs args)
        {
            // WebRTC decoding thread. Do nothing that can block it.
            Interlocked.Increment(ref _framesReceived);
            Volatile.Write(ref _frameWidth, args.PixelWidth);
            Volatile.Write(ref _frameHeight, args.PixelHeight);

            if (!_disposed)
            {
                _wake.Set();
            }
        }

        private void Work()
        {
            while (!_disposed)
            {
                _wake.WaitOne();

                if (_disposed)
                {
                    return;
                }

                try
                {
                    Step();
                }
                catch (Exception ex)
                {
                    // A throw here would take the worker down and the call would go black with no
                    // trace of why. Frames are cheap; the thread is not.
                    Logger.Error("voip video: frame dropped", ex);
                    Interlocked.Increment(ref _framesDropped);
                }
            }
        }

        private void Step()
        {
            var width = Volatile.Read(ref _frameWidth);
            var height = Volatile.Read(ref _frameHeight);

            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (_surface.PixelWidth != width || _surface.PixelHeight != height)
            {
                // Configure allocates the planes and, on the WriteableBitmap surface, builds a whole
                // new bitmap: SupportsLiveResize is what says whether it may happen off the UI
                // thread, and the fallback says no. Going through the dispatcher for both keeps one
                // path instead of two, at the cost of dropping the frame that noticed the change.
                if (Interlocked.CompareExchange(ref _configuring, 1, 0) == 0)
                {
                    if (_dispatcher != null)
                    {
                        _dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                _surface.Configure(width, height);
                            }
                            finally
                            {
                                Volatile.Write(ref _configuring, 0);
                                _wake.Set();
                            }
                        });
                    }
                    else
                    {
                        Volatile.Write(ref _configuring, 0);
                    }
                }

                Interlocked.Increment(ref _framesDropped);
                return;
            }

            if (!_surface.RenderInto(_fill))
            {
                Interlocked.Increment(ref _framesDropped);
                return;
            }

            _surface.Publish();
            var painted = Interlocked.Increment(ref _framesPainted);

            // A log line, throttled, because on this project "it paints" has to have an answer that
            // does not depend on somebody looking at the screen -- the same reason an action is
            // proved by TDLib's reply and not by the button going down. The first frame is the one
            // that matters (it says the path is joined end to end); after that every 100th is
            // enough to show it did not stall.
            if (painted == 1 || painted % 100 == 0)
            {
                Logger.Info($"voip video: painted {painted} frames, {width}x{height}, "
                    + $"received {Interlocked.Read(ref _framesReceived)}, dropped {Interlocked.Read(ref _framesDropped)}");
            }

            _dispatcher?.TryEnqueue(_surface.Present);
        }

        private unsafe bool Fill(IntPtr plane, int width, int height)
        {
            var destination = new Span<byte>((void*)plane, width * height * 4);

            if (!_sink.CopyFrameTo(destination, out var frameWidth, out var frameHeight, out _))
            {
                return false;
            }

            // CopyFrameTo writes frameWidth x frameHeight and refuses a destination too small for
            // that, so it cannot overrun -- but a frame SMALLER than the plane would leave the rest
            // of the plane holding the previous image, and publishing that is a torn frame. Drop it
            // instead; the resize path above will have the right size a frame later.
            return frameWidth == width && frameHeight == height;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sink.FrameReceived -= OnFrameReceived;

            // Wake the worker so it sees _disposed and returns, instead of sitting on the event
            // until the process ends.
            _wake.Set();

            if (_worker.IsAlive && !_worker.Join(TimeSpan.FromSeconds(1)))
            {
                Logger.Warning("voip video: the render worker did not stop within a second");
            }

            _surface.Dispose();
            _wake.Dispose();
        }
    }
}

