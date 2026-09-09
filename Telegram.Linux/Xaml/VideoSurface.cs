//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Telegram.Native;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Telegram.Controls
{
    /// <summary>
    /// Where a decoded video frame lands on its way to the screen.
    ///
    /// <para><b>Why this exists.</b> Presenting, not decoding, was the bottleneck of the gallery's
    /// video: the same frame was walked four times between the decoder and the compositor — into
    /// the decoder's ArrayPool scratch, into a staging <c>IBuffer</c>, into
    /// <c>WriteableBitmap.PixelBuffer</c>, and finally into a fresh <c>SKImage</c>, because
    /// <c>WriteableBitmap.Invalidate</c> in Uno 6.6.184 ends in
    /// <c>SkiaCompositionSurface.CopyPixels</c> → <c>SKImage.FromPixelCopy</c> (read from the IL).
    /// At 1539x2736 that was 12.4 ms of UI thread per frame and a 16 MB allocation per frame, and
    /// the pipeline topped out at 30 fps. Measured in
    /// <c>unigram-linux/spikes/VideoPresentSpike</c>.</para>
    ///
    /// <para><b>What replaces it.</b> <see cref="Skia"/>: an <see cref="SKCanvasElement"/> over two
    /// plain planes of native memory. The decoder writes a frame straight into the back plane —
    /// no managed array, no copy — and the render callback draws the front one. Presenting becomes
    /// <c>Invalidate()</c> and nothing else, and nothing is allocated per frame. Same spike, same
    /// size: 0.06 ms of UI thread and 53 fps.</para>
    ///
    /// <para><b>And a fallback.</b> <see cref="Bitmap"/> keeps the old <c>Image</c> +
    /// <c>WriteableBitmap</c>, for a host where <c>SKCanvasElement.IsSupportedOnCurrentPlatform()</c>
    /// is false — the same defensive shape <c>ChatBackgroundCanvas</c> already uses, because an
    /// exception out of that constructor would cost the whole gallery. It still gains the first two
    /// copies (the decoder writes into the bitmap's own bytes through
    /// <see cref="BufferAccess"/>), just not the third.</para>
    /// </summary>
    public abstract class VideoSurface : IDisposable
    {
        /// <summary>The element to put in the tree. Stable for the lifetime of the surface.</summary>
        public abstract FrameworkElement Element { get; }

        /// <summary>The size frames are decoded at. Read from the decode thread.</summary>
        public int PixelWidth => _pixelWidth;

        public int PixelHeight => _pixelHeight;

        protected volatile int _pixelWidth;
        protected volatile int _pixelHeight;

        /// <summary>
        /// Decode thread. Decodes one frame into the back plane; does not publish it, so a frame
        /// that turns out to be late can be dropped without having disturbed what is on screen.
        /// </summary>
        public abstract bool Render(VideoAnimation animation, out double seconds);

        /// <summary>
        /// Worker thread. Same contract as <see cref="Render(VideoAnimation, out double)"/> — fill
        /// the back plane, do not publish — but for a producer that is not an mpv
        /// <see cref="VideoAnimation"/>. <paramref name="fill"/> receives the plane, its width and
        /// its height, and returns whether it wrote a frame.
        ///
        /// <para>This exists because call video arrives as three I420 planes from WebRTC's decoding
        /// thread, not from a demuxer this class can pull from. It is <b>additive</b> on purpose:
        /// the mpv path through <c>Render(VideoAnimation, …)</c> is measured and working, and a
        /// batch that cannot run the app has no business refactoring it to share code.</para>
        /// </summary>
        public abstract bool RenderInto(Func<IntPtr, int, int, bool> fill);

        /// <summary>Decode thread. Makes the frame just decoded the one the next draw will use.</summary>
        public abstract void Publish();

        /// <summary>UI thread. What <c>LinuxVideoPlayer</c>'s present stopwatch brackets.</summary>
        public abstract void Present();

        /// <summary>
        /// Sets the size frames are decoded at — see <see cref="Choose"/>. Always called from the
        /// UI thread when playback starts; called again from the <b>decode thread</b> while playing
        /// only where <see cref="SupportsLiveResize"/> says that is legal.
        /// </summary>
        public abstract bool Configure(int width, int height);

        /// <summary>
        /// Whether the surface can change size while the decode thread is running. False for
        /// <see cref="Bitmap"/>, whose <c>WriteableBitmap</c> has to be built and swapped into
        /// <c>Image.Source</c> on the UI thread.
        /// </summary>
        public abstract bool SupportsLiveResize { get; }

        public abstract void Dispose();

        public static VideoSurface Create()
        {
            try
            {
                if (SKCanvasElement.IsSupportedOnCurrentPlatform())
                {
                    return new Skia();
                }

                Logger.Warning("video: SKCanvasElement is not available on this host, falling back to WriteableBitmap");
            }
            catch (Exception ex)
            {
                Logger.Error("video: cannot create the SKCanvasElement surface", ex);
            }

            return new Bitmap();
        }

        /// <summary>
        /// The size to decode at: <b>the size the frame is shown at</b>, not the size the file
        /// happens to be.
        ///
        /// <para>Decoding to more pixels than the panel shows is work paid for twice — swscale
        /// writes them and the compositor throws them away — and the numbers say so
        /// (<c>unigram-linux/spikes/VideoScaleSpike</c>, 2160x3840 clip, median of three):
        /// 97.8 ms a frame decoding at 1539x2736 and 32.7 ms at 810x1440, which is what the same
        /// clip occupies in a 2200x1440 window.</para>
        ///
        /// <para><b>The width must be even.</b> That is not tidiness, it is the single biggest
        /// number in the whole measurement: the same frame, the same scaler, costs 97.8 ms at
        /// 1539x2736 and 40.0 ms at 1538x2736 — swscale drops off its fast paths on an odd output
        /// width. Height parity does not matter (810x1441 measured the same as 810x1440), and
        /// aligning further than 2 buys nothing (1536, 1538 and 1540 are within noise of each
        /// other). <c>Math.Round(width * scale)</c>, which is what this used to be, lands on an odd
        /// width about half the time.</para>
        /// </summary>
        /// <param name="maxSide">
        /// Absolute cap on the long side, for the case where the element size is not known yet.
        /// </param>
        public static (int Width, int Height) Choose(int nativeWidth, int nativeHeight, double availableWidth, double availableHeight, double scale, int maxSide)
        {
            if (nativeWidth <= 0 || nativeHeight <= 0)
            {
                return (0, 0);
            }

            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                scale = 1;
            }

            // Never upscale in the decoder: blowing a 480p clip up to the panel would cost the full
            // price of a 4K frame for pixels swscale invented.
            var fit = 1d;

            var width = availableWidth * scale;
            var height = availableHeight * scale;

            if (width > 0 && height > 0 && !double.IsNaN(width) && !double.IsNaN(height))
            {
                // Uniform letterboxing, the fit both surfaces draw with.
                fit = Math.Min(fit, Math.Min(width / nativeWidth, height / nativeHeight));
            }

            if (maxSide > 0)
            {
                fit = Math.Min(fit, (double)maxSide / Math.Max(nativeWidth, nativeHeight));
            }

            return (Even(nativeWidth * fit), Even(nativeHeight * fit));
        }

        private static int Even(double value)
        {
            var rounded = (int)Math.Round(value);
            return Math.Max(2, rounded - (rounded & 1));
        }

        /// <summary>
        /// Two planes of native memory and an <see cref="SKCanvasElement"/> over them.
        ///
        /// <para>The <see cref="SKImage"/> wrapper is rebuilt for every frame on purpose.
        /// <c>SKImage.FromPixels</c> does not copy — it is a few dozen bytes of wrapper over the
        /// plane — but Skia treats an image as immutable and is free to cache it by identity, so a
        /// wrapper reused over mutated pixels is how a player ends up showing one frame for
        /// ever.</para>
        ///
        /// <para>Retired planes are not freed until <see cref="Dispose"/>: a resize can land while
        /// the render thread is between picking up the front image and drawing it, and a freed
        /// plane there is a crash rather than a stale frame. There is at most one resize per window
        /// change and each plane is one frame, so the arithmetic is small.</para>
        /// </summary>
        private sealed class Skia : VideoSurface
        {
            private readonly Painter _painter;
            private readonly object _lock = new();
            private readonly List<IntPtr> _retired = new();

            private IntPtr _frontPlane;
            private IntPtr _backPlane;
            private SKImage _front;
            private SKImage _pending;
            private SKImageInfo _info;
            private int _stride;

            public Skia()
            {
                _painter = new Painter(Render)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
            }

            public override FrameworkElement Element => _painter;

            public override bool SupportsLiveResize => true;

            public override bool Configure(int width, int height)
            {
                if (width <= 0 || height <= 0)
                {
                    return false;
                }

                lock (_lock)
                {
                    if (width == _pixelWidth && height == _pixelHeight)
                    {
                        return false;
                    }

                    Retire(_frontPlane);
                    Retire(_backPlane);

                    var size = (long)width * height * 4;

                    _frontPlane = Marshal.AllocHGlobal((IntPtr)size);
                    _backPlane = Marshal.AllocHGlobal((IntPtr)size);

                    _info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    _stride = width * 4;

                    _pixelWidth = width;
                    _pixelHeight = height;

                    // The frame on screen belongs to a plane that is now retired; keep drawing it
                    // until the decoder publishes one at the new size, which is one frame away.
                    _pending?.Dispose();
                    _pending = null;

                    return true;
                }
            }

            private void Retire(IntPtr plane)
            {
                if (plane != IntPtr.Zero)
                {
                    _retired.Add(plane);
                }
            }

            public override bool Render(VideoAnimation animation, out double seconds)
            {
                seconds = 0;

                IntPtr plane;
                int width;
                int height;

                lock (_lock)
                {
                    plane = _backPlane;
                    width = _pixelWidth;
                    height = _pixelHeight;
                }

                if (plane == IntPtr.Zero || width <= 0 || height <= 0)
                {
                    return false;
                }

                // Outside the lock: this is the 30 ms of swscale and the UI thread must not wait
                // on it. A Configure landing here only means this frame is decoded at the old size
                // and dropped by the generation check in Publish.
                if (!animation.RenderSync(plane, width, height, false, out seconds))
                {
                    return false;
                }

                lock (_lock)
                {
                    if (plane != _backPlane)
                    {
                        // The surface was resized while this frame was being decoded.
                        return false;
                    }

                    _pending?.Dispose();
                    _pending = SKImage.FromPixels(_info, plane, _stride);
                }

                return true;
            }

            public override bool RenderInto(Func<IntPtr, int, int, bool> fill)
            {
                IntPtr plane;
                int width;
                int height;

                lock (_lock)
                {
                    plane = _backPlane;
                    width = _pixelWidth;
                    height = _pixelHeight;
                }

                if (plane == IntPtr.Zero || width <= 0 || height <= 0)
                {
                    return false;
                }

                // Outside the lock, for the same reason the mpv path is: this is the I420 to BGRA
                // expansion, ~900k pixels at 720p, and the UI thread must not wait on it.
                if (!fill(plane, width, height))
                {
                    return false;
                }

                lock (_lock)
                {
                    if (plane != _backPlane)
                    {
                        // Resized while this frame was being converted; it is the wrong size now.
                        return false;
                    }

                    _pending?.Dispose();
                    _pending = SKImage.FromPixels(_info, plane, _stride);
                }

                return true;
            }

            public override void Publish()
            {
                lock (_lock)
                {
                    if (_pending == null)
                    {
                        return;
                    }

                    _front?.Dispose();
                    _front = _pending;
                    _pending = null;

                    (_frontPlane, _backPlane) = (_backPlane, _frontPlane);
                }
            }

            public override void Present()
            {
                _painter.Invalidate();
            }

            private void Render(SKCanvas canvas, Size area)
            {
                lock (_lock)
                {
                    if (_front == null || area.Width <= 0 || area.Height <= 0)
                    {
                        return;
                    }

                    var width = _front.Width;
                    var height = _front.Height;

                    // Uniform letterboxing: the same fit Stretch.Uniform gave the Image.
                    var scale = Math.Min(area.Width / width, area.Height / height);

                    var w = (float)(width * scale);
                    var h = (float)(height * scale);
                    var x = (float)(area.Width - w) / 2;
                    var y = (float)(area.Height - h) / 2;

                    canvas.DrawImage(_front, new SKRect(x, y, x + w, y + h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                }
            }

            public override void Dispose()
            {
                lock (_lock)
                {
                    _front?.Dispose();
                    _front = null;

                    _pending?.Dispose();
                    _pending = null;

                    Retire(_frontPlane);
                    Retire(_backPlane);

                    _frontPlane = IntPtr.Zero;
                    _backPlane = IntPtr.Zero;

                    foreach (var plane in _retired)
                    {
                        Marshal.FreeHGlobal(plane);
                    }

                    _retired.Clear();

                    _pixelWidth = 0;
                    _pixelHeight = 0;
                }
            }

            private sealed partial class Painter : SKCanvasElement
            {
                private readonly Action<SKCanvas, Size> _render;

                public Painter(Action<SKCanvas, Size> render)
                {
                    _render = render;
                }

                protected override void RenderOverride(SKCanvas canvas, Size area)
                {
                    _render(canvas, area);
                }
            }
        }

        /// <summary>
        /// The old surface, minus two of its four walks over the frame: the decoder writes into the
        /// bitmap's own bytes (<see cref="BufferAccess"/>) instead of into a scratch array that is
        /// then copied into a staging buffer that is then copied here.
        ///
        /// <para>One buffer is enough, and the reason is Uno's: <c>Invalidate()</c> copies the
        /// whole frame into an <c>SKImage</c> before it returns, so the moment
        /// <see cref="Present"/> is back the bytes belong to the decoder again — which is exactly
        /// the handshake <c>LinuxVideoPlayer</c> already enforces with <c>_consumed</c>.</para>
        ///
        /// <para>It does not resize while playing: a <c>WriteableBitmap</c> has to be built on the
        /// UI thread and swapped into <c>Image.Source</c> there, and the decode thread cannot do
        /// that. The size chosen when playback starts is the one it keeps, which is what the player
        /// did before this class existed.</para>
        /// </summary>
        private sealed class Bitmap : VideoSurface
        {
            private readonly Image _image;

            private WriteableBitmap _bitmap;

            public Bitmap()
            {
                _image = new Image
                {
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
            }

            public override FrameworkElement Element => _image;

            public override bool SupportsLiveResize => false;

            public override bool Configure(int width, int height)
            {
                if (width <= 0 || height <= 0 || (width == _pixelWidth && height == _pixelHeight))
                {
                    return false;
                }

                _bitmap = new WriteableBitmap(width, height);
                _image.Source = _bitmap;

                _pixelWidth = width;
                _pixelHeight = height;

                return true;
            }

            public override bool Render(VideoAnimation animation, out double seconds)
            {
                seconds = 0;

                var bitmap = _bitmap;
                if (bitmap == null)
                {
                    return false;
                }

                return animation.RenderSync(bitmap.PixelBuffer, _pixelWidth, _pixelHeight, false, out seconds);
            }

            public override bool RenderInto(Func<IntPtr, int, int, bool> fill)
            {
                var bitmap = _bitmap;
                if (bitmap == null || _pixelWidth <= 0 || _pixelHeight <= 0)
                {
                    return false;
                }

                // A scratch plane and then a copy into the bitmap, rather than handing `fill` the
                // PixelBuffer's own memory: nothing in this tree exposes a raw pointer into an Uno
                // IBuffer, and inventing one for the FALLBACK surface would be the wrong place to
                // take that risk. The extra copy is affordable precisely here — this path already
                // re-uploads the whole frame on every Present, which is the measured reason it is
                // the fallback and not the choice (see the class remarks).
                var size = _pixelWidth * _pixelHeight * 4;
                if (_scratch == null || _scratch.Length < size)
                {
                    _scratch = new byte[size];
                }

                var pin = GCHandle.Alloc(_scratch, GCHandleType.Pinned);
                try
                {
                    if (!fill(pin.AddrOfPinnedObject(), _pixelWidth, _pixelHeight))
                    {
                        return false;
                    }
                }
                finally
                {
                    pin.Free();
                }

                _scratch.CopyTo(0, Telegram.Native.PixelBuffer.Unwrap(bitmap.PixelBuffer), 0, size);
                return true;
            }

            private byte[] _scratch;

            public override void Publish()
            {
            }

            public override void Present()
            {
                _bitmap?.Invalidate();
            }

            public override void Dispose()
            {
                _image.Source = null;
                _bitmap = null;

                _pixelWidth = 0;
                _pixelHeight = 0;
            }
        }
    }
}
