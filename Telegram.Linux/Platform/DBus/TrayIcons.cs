//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Telegram.Native;

namespace Telegram.Services
{
    /// <summary>The three states the tray icon has had since the Windows stub.</summary>
    public enum TrayIconState
    {
        /// <summary>Nothing unread.</summary>
        Default,
        /// <summary>Unread messages, but all of them muted: the grey dot.</summary>
        Muted,
        /// <summary>Unread messages that are not muted: the red dot.</summary>
        Unmuted
    }

    /// <summary>
    /// The pixels of the tray icon, read from the SAME three icons the Windows tray uses
    /// (<c>Telegram.Stub/Resources/{Default,Muted,Unmuted}.ico</c>, deployed next to the binary as
    /// <c>Assets/Tray*.ico</c>): the plain logo, the logo with a grey dot and the logo with a red
    /// dot. Keeping the artwork means the Linux tray is recognisably the same app, and there is
    /// nothing to draw by hand.
    ///
    /// <para>The <c>.ico</c> is parsed here instead of being handed to a decoder. Every frame in
    /// those three files is an uncompressed 32-bit BMP, which is a fixed-layout format that fits in
    /// forty lines; the alternative is trusting <c>SKCodec</c>'s ICO support, and this port has
    /// already been bitten once by a Skia entry point that answers differently inside the app than
    /// outside it (see <c>SKPath.ParseSvgPathData</c> in PORTING.md §6). Skia is still used, but
    /// only for the part it cannot get wrong: scaling.</para>
    /// </summary>
    internal static class TrayIcons
    {
        private static readonly Dictionary<(TrayIconState, int), byte[]> _cache = new();
        private static readonly Dictionary<TrayIconState, SKBitmap> _sources = new();

        public static string FileName(TrayIconState state)
        {
            return state switch
            {
                TrayIconState.Muted => "TrayMuted.ico",
                TrayIconState.Unmuted => "TrayUnmuted.ico",
                _ => "TrayDefault.ico"
            };
        }

        /// <summary>
        /// ARGB32 in network byte order (A, R, G, B), which is what
        /// <c>org.kde.StatusNotifierItem.IconPixmap</c> asks for. Null when the icon is missing:
        /// the tray then falls back to the icon name and the item still appears.
        /// </summary>
        public static byte[] GetArgb32(TrayIconState state, int size)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue((state, size), out var cached))
                {
                    return cached;
                }

                byte[] pixels = null;

                try
                {
                    var source = GetSource(state);
                    if (source != null)
                    {
                        pixels = Scale(source, size);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Cannot load the tray icon for {state}", ex);
                }

                _cache[(state, size)] = pixels;
                return pixels;
            }
        }

        private static SKBitmap GetSource(TrayIconState state)
        {
            if (_sources.TryGetValue(state, out var bitmap))
            {
                return bitmap;
            }

            var path = NativeAssets.Resolve(FileName(state), null);
            if (path == null)
            {
                Logger.Warning($"Tray icon {FileName(state)} not found next to the binary");
                _sources[state] = null;
                return null;
            }

            bitmap = DecodeIco(File.ReadAllBytes(path));
            _sources[state] = bitmap;
            return bitmap;
        }

        private static byte[] Scale(SKBitmap source, int size)
        {
            using var scaled = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));

            using (var canvas = new SKCanvas(scaled))
            using (var image = SKImage.FromBitmap(source))
            using (var paint = new SKPaint { IsAntialias = true })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(image, new SKRect(0, 0, size, size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            }

            // RGBA in memory -> ARGB big endian on the wire: one rotation per pixel.
            var rgba = scaled.Bytes;
            var argb = new byte[rgba.Length];

            for (int i = 0; i < rgba.Length; i += 4)
            {
                argb[i + 0] = rgba[i + 3];
                argb[i + 1] = rgba[i + 0];
                argb[i + 2] = rgba[i + 1];
                argb[i + 3] = rgba[i + 2];
            }

            return argb;
        }

        /// <summary>
        /// Decodes the largest 32-bit frame of an ICO. Returns unpremultiplied RGBA, which is what
        /// both consumers want (the SNI pixmap and, if it ever comes up, a PNG on disk).
        /// </summary>
        public static SKBitmap DecodeIco(byte[] data)
        {
            if (data == null || data.Length < 6)
            {
                return null;
            }

            var type = BitConverter.ToUInt16(data, 2);
            var count = BitConverter.ToUInt16(data, 4);

            if (type != 1 || count == 0)
            {
                return null;
            }

            var bestOffset = 0;
            var bestSize = 0;
            var bestLength = 0;

            for (int i = 0; i < count; i++)
            {
                var entry = 6 + 16 * i;
                if (entry + 16 > data.Length)
                {
                    break;
                }

                int width = data[entry] == 0 ? 256 : data[entry];
                int height = data[entry + 1] == 0 ? 256 : data[entry + 1];
                var length = BitConverter.ToInt32(data, entry + 8);
                var offset = BitConverter.ToInt32(data, entry + 12);

                if (offset <= 0 || length <= 0 || offset + length > data.Length)
                {
                    continue;
                }

                var size = Math.Min(width, height);
                if (size > bestSize)
                {
                    bestSize = size;
                    bestOffset = offset;
                    bestLength = length;
                }
            }

            if (bestSize == 0)
            {
                return null;
            }

            // A PNG-compressed frame (Vista and later allow it) is handed to Skia, which decodes
            // PNG in every build this port has measured.
            if (bestLength > 8 && data[bestOffset] == 0x89 && data[bestOffset + 1] == 0x50)
            {
                var png = new byte[bestLength];
                Buffer.BlockCopy(data, bestOffset, png, 0, bestLength);
                return SKBitmap.Decode(png);
            }

            return DecodeBmpFrame(data, bestOffset, bestLength);
        }

        private static SKBitmap DecodeBmpFrame(byte[] data, int offset, int length)
        {
            if (length < 40)
            {
                return null;
            }

            var headerSize = BitConverter.ToInt32(data, offset);
            var width = BitConverter.ToInt32(data, offset + 4);
            // An ICO frame doubles the height to make room for the AND mask underneath.
            var height = BitConverter.ToInt32(data, offset + 8) / 2;
            var bitCount = BitConverter.ToUInt16(data, offset + 14);
            var compression = BitConverter.ToInt32(data, offset + 16);

            if (headerSize < 40 || width <= 0 || height <= 0 || bitCount != 32 || compression != 0)
            {
                Logger.Warning($"Unsupported ICO frame: {width}x{height}, {bitCount} bpp, compression {compression}");
                return null;
            }

            var stride = width * 4;
            var start = offset + headerSize;

            if (start + stride * height > data.Length)
            {
                return null;
            }

            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var pixels = new byte[stride * height];

            for (int y = 0; y < height; y++)
            {
                // BMP rows run bottom-up, and the pixels are BGRA.
                var source = start + (height - 1 - y) * stride;
                var destination = y * stride;

                for (int x = 0; x < width; x++)
                {
                    pixels[destination + x * 4 + 0] = data[source + x * 4 + 2];
                    pixels[destination + x * 4 + 1] = data[source + x * 4 + 1];
                    pixels[destination + x * 4 + 2] = data[source + x * 4 + 0];
                    pixels[destination + x * 4 + 3] = data[source + x * 4 + 3];
                }
            }

            // The AND mask that follows is deliberately ignored: every frame here is 32 bpp and
            // carries its own alpha, and Windows itself ignores the mask in that case.
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            return bitmap;
        }
    }
}
