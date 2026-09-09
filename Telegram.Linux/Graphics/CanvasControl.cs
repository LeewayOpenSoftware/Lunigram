//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// Telegram.Linux -- stand-in for the sliver of Win2D's XAML surface that Controls/DiceView.cs
// needs: a control it can draw bitmaps into every frame. Uno Skia has no CanvasControl (see
// PORTING.md, section 6, "Windows.Data.Json, Win2D..."), and there is no drop-in Skia-hosting
// control anywhere in the subset yet either, so this reuses the SAME technique
// Controls/AnimatedImage.cs already proved out for Lottie/WebM stickers on Linux: a plain Image
// over a WriteableBitmap, composited with Telegram.Native.PixelBuffer.SourceOver (the same
// premultiplied-BGRA blend AnimatedImage's own frame composition already uses).
//
// Deliberately narrow: only what DiceView.cs's OnCreateResources/OnDraw/Invalidate call sites
// need (CreateResources once, then Draw -> DrawImage per layer). No device-lost handling, no DPI,
// no effects graph -- Win2D's CanvasControl has all of that and DiceView does not touch any of it.
// If a second caller ever needs more of the real surface, it needs a real port of this file, not
// an extension.
//
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Telegram.Native;

namespace Microsoft.Graphics.Canvas.UI
{
    // Win2D's real CanvasCreateResourcesReason also has ResourcesLost/DpiChanged -- this port
    // never sees a lost device, so FirstTime is the only value DiceView.cs ever compares against.
    public enum CanvasCreateResourcesReason
    {
        FirstTime,
        Other,
    }

    public sealed class CanvasCreateResourcesEventArgs
    {
        public CanvasCreateResourcesReason Reason { get; }

        internal CanvasCreateResourcesEventArgs(CanvasCreateResourcesReason reason)
        {
            Reason = reason;
        }
    }
}

namespace Microsoft.Graphics.Canvas.UI.Xaml
{
    public sealed class CanvasDrawEventArgs
    {
        public CanvasDrawingSession DrawingSession { get; }

        internal CanvasDrawEventArgs(CanvasDrawingSession session)
        {
            DrawingSession = session;
        }
    }

    public sealed class CanvasControl : Image, ICanvasResourceCreator
    {
        private PixelBuffer _target;
        private bool _resourcesCreated;

        public CanvasControl()
        {
            Stretch = Stretch.Fill;
        }

        public CanvasDevice Device => CanvasDevice.GetSharedDevice();

        // Win2D's CanvasControl.Size reports the control's DIP size, which DiceView.cs only uses
        // to build the destination Rect it hands to DrawImage. This substitute's DrawImage ignores
        // that rect (see the comment there) and always paints at the source bitmap's own pixel
        // size, so Size exists only to satisfy the call site's shape.
        public new Size Size => new(ActualWidth, ActualHeight);

        public event TypedEventHandler<CanvasControl, CanvasCreateResourcesEventArgs> CreateResources;

        public event TypedEventHandler<CanvasControl, CanvasDrawEventArgs> Draw;

        public void Invalidate()
        {
            if (!_resourcesCreated)
            {
                _resourcesCreated = true;
                CreateResources?.Invoke(this, new CanvasCreateResourcesEventArgs(CanvasCreateResourcesReason.FirstTime));
            }

            Draw?.Invoke(this, new CanvasDrawEventArgs(new CanvasDrawingSession(this)));
        }

        // Called by CanvasDrawingSession on the first DrawImage of a frame, once the real pixel
        // size is known (DiceView's layers are always 256x256, but nothing here assumes that).
        internal PixelBuffer EnsureTarget(int width, int height)
        {
            if (_target == null || _target.PixelWidth != width || _target.PixelHeight != height)
            {
                var bitmap = new WriteableBitmap(width, height);
                _target = new PixelBuffer(bitmap);
                Source = bitmap;
            }

            return _target;
        }

        // Real Win2D's CanvasControl.RemoveFromVisualTree() detaches the control from its host so
        // it can be garbage-collected outside the normal remove-from-Children dance; DiceView.cs's
        // Unload() calls it on both the template-provided Canvas and one it created itself
        // (Controls/DiceView.cs:159-180), so this just does the generic version of that: drop
        // itself from whichever panel currently parents it, if any.
        public void RemoveFromVisualTree()
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(this) is Panel parent)
            {
                parent.Children.Remove(this);
            }
        }
    }
}

namespace Microsoft.Graphics.Canvas
{
    // Also an IBuffer/IPixelBufferOwner: DiceView.cs reuses the same CanvasBitmap instance across
    // frames as RLottie's own render target (`animations[i].RenderSync(_bitmaps[i], index[i])`,
    // Controls/DiceView.cs:292) -- Telegram.Linux/Native/RLottie/LottieAnimation.cs's RenderSync
    // takes an IBuffer and unwraps it via Telegram.Native.PixelBuffer.Unwrap to reach the real
    // Uno-native buffer before writing decoded pixels into it. IPixelBufferOwner is the extra hop
    // that lets Unwrap reach through THIS wrapper to the WriteableBitmap-backed PixelBuffer it
    // owns, the same way it already reaches through PixelBuffer itself.
    public sealed class CanvasBitmap : global::Windows.Storage.Streams.IBuffer, IPixelBufferOwner
    {
        public PixelBuffer Pixels { get; }

        internal int Width { get; }

        internal int Height { get; }

        public uint Capacity => Pixels.Capacity;

        public uint Length
        {
            get => Pixels.Length;
            set => Pixels.Length = value;
        }

        private CanvasBitmap(PixelBuffer pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }

        // DiceView.cs's only call site always passes B8G8R8A8UIntNormalized, which is the same
        // premultiplied-BGRA8 layout WriteableBitmap already uses natively on Uno -- so the format
        // argument is accepted for shape parity but not otherwise consulted.
        /// <summary>
        /// Replaces this bitmap's pixels in place. 12.10.2 renders a lottie frame straight over an
        /// existing CanvasBitmap instead of building a new one each time; the copy is the same one
        /// CreateFromBytes does, and the caller invalidates the canvas afterwards.
        /// </summary>
        public void SetPixelBytes(byte[] bytes)
        {
            if (bytes == null)
            {
                return;
            }

            // The caller rents from ArrayPool, which may over-allocate: copy only what belongs here.
            var count = Math.Min((int)Pixels.Capacity, bytes.Length);
            if (count > 0)
            {
                bytes.CopyTo(0, Pixels, 0, count);
                Pixels.Source?.Invalidate();
            }
        }

        public static CanvasBitmap CreateFromBytes(ICanvasResourceCreator resourceCreator, byte[] bytes, int widthInPixels, int heightInPixels, global::Windows.Graphics.DirectX.DirectXPixelFormat format)
        {
            var bitmap = new WriteableBitmap(widthInPixels, heightInPixels);
            var pixels = new PixelBuffer(bitmap);

            // The caller (DiceView.OnCreateResources) rents the buffer from ArrayPool, which may
            // over-allocate; only copy the pixels that actually belong to this bitmap.
            var count = Math.Min((int)pixels.Capacity, widthInPixels * heightInPixels * 4);
            if (count > 0)
            {
                bytes.CopyTo(0, pixels, 0, count);
            }

            return new CanvasBitmap(pixels, widthInPixels, heightInPixels);
        }
    }

    public sealed class CanvasDrawingSession
    {
        private readonly Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl _owner;
        private bool _cleared;

        internal CanvasDrawingSession(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl owner)
        {
            _owner = owner;
        }

        // Win2D draws `bitmap` scaled into `destinationRect`. DiceView.cs always passes the whole
        // control's own rect (0,0,Size.Width,Size.Height) for every layer of a given frame, and
        // every layer shares the same native size (DiceView._frameSize, fixed at 256x256) -- so
        // painting each layer at its own native resolution and letting CanvasControl's Image host
        // (Stretch=Fill) scale the composited result to the control's layout size is exactly
        // equivalent here, without needing a resampling blit for a rect this substitute never
        // actually sees vary.
        public void DrawImage(CanvasBitmap bitmap, Rect destinationRect)
        {
            if (bitmap?.Pixels == null)
            {
                return;
            }

            var target = _owner.EnsureTarget(bitmap.Width, bitmap.Height);

            if (!_cleared)
            {
                _cleared = true;
                PixelBuffer.Clear(target);
            }

            PixelBuffer.SourceOver(target, bitmap.Pixels, bitmap.Width, bitmap.Height);
            target.Source.Invalidate();
        }
    }
}
