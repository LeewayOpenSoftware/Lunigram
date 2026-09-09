//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Telegram.Native
{
    // Implemented by an IBuffer wrapper that isn't a PixelBuffer itself but owns one internally
    // (Microsoft.Graphics.Canvas.CanvasBitmap, the Win2D CanvasBitmap stand-in RLottie's
    // RenderSync writes decoded frames into) -- lets Unwrap reach the real Uno-native buffer
    // through one more layer of indirection.
    public interface IPixelBufferOwner
    {
        PixelBuffer Pixels { get; }
    }

    // Wraps WriteableBitmap.PixelBuffer. Uno only accepts its own Buffer class in the buffer
    // extension methods, so BufferSurface and friends unwrap this before touching the bytes.
    public sealed partial class PixelBuffer : IBuffer
    {
        private readonly WriteableBitmap _bitmap;
        private readonly IBuffer _buffer;

        public PixelBuffer(WriteableBitmap bitmap)
        {
            _bitmap = bitmap;
            _buffer = bitmap.PixelBuffer;
        }

        public int PixelWidth => _bitmap.PixelWidth;

        public int PixelHeight => _bitmap.PixelHeight;

        public WriteableBitmap Source => _bitmap;

        public uint Capacity => _buffer.Capacity;

        public uint Length
        {
            get => _buffer.Length;
            set => _buffer.Length = value;
        }

        internal static IBuffer Unwrap(IBuffer buffer)
        {
            if (buffer is PixelBuffer pixels)
            {
                return pixels._buffer;
            }

            if (buffer is IPixelBufferOwner owner)
            {
                return owner.Pixels._buffer;
            }

            return buffer;
        }

        public static void Clear(IBuffer buffer)
        {
            var inner = Unwrap(buffer);
            if (inner.Length == 0)
            {
                return;
            }

            var zeros = new byte[inner.Length];
            zeros.CopyTo(0, inner, 0, zeros.Length);
        }

        // Premultiplied BGRA source-over, the blend the native side used for frame composition.
        public static void SourceOver(IBuffer destination, IBuffer source, int width, int height)
        {
            var dst = Unwrap(destination);
            var src = Unwrap(source);

            var length = Math.Min((int)Math.Min(dst.Length, src.Length), width * height * 4);
            if (length <= 0)
            {
                return;
            }

            var d = dst.ToArray();
            var s = src.ToArray();

            for (int i = 0; i + 3 < length; i += 4)
            {
                var alpha = s[i + 3];
                if (alpha == 255)
                {
                    d[i] = s[i];
                    d[i + 1] = s[i + 1];
                    d[i + 2] = s[i + 2];
                    d[i + 3] = 255;
                }
                else if (alpha != 0)
                {
                    var inverse = 255 - alpha;
                    d[i] = (byte)(s[i] + d[i] * inverse / 255);
                    d[i + 1] = (byte)(s[i + 1] + d[i + 1] * inverse / 255);
                    d[i + 2] = (byte)(s[i + 2] + d[i + 2] * inverse / 255);
                    d[i + 3] = (byte)(alpha + d[i + 3] * inverse / 255);
                }
            }

            d.CopyTo(0, dst, 0, length);
        }
    }
}
