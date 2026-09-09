//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Telegram.Native
{
    /// <summary>
    /// Image module of <c>libunigram-native.so</c>: libwebp decoding, one to one with the IMAGE
    /// section of unigram_native.h. It replaces the two libwebp entry points of
    /// Telegram.Native.PlaceholderImageHelper (DrawWebP and IsWebP), which
    /// <see cref="PlaceholderImageHelper"/> now forwards here.
    ///
    /// <para>Telegram hands out WebP for stickers and for a good share of photos, so this runs on
    /// the animation thread for every sticker in view. The shape of the API follows from that: one
    /// handle holds the file's bytes and its demuxer, so asking for the size and decoding are one
    /// read of the file, not two.</para>
    ///
    /// <para>Decode only, first frame only -- the same thing the Windows implementation did. See
    /// the header for why animation is out of scope.</para>
    /// </summary>
    internal sealed partial class WebpImage : IDisposable
    {
        private readonly WebpImageHandle _handle;

        private WebpImage(WebpImageHandle handle, WebpInfo info)
        {
            _handle = handle;

            PixelWidth = info.Width;
            PixelHeight = info.Height;
            FrameCount = info.FrameCount;
            IsAnimated = info.HasAnimation != 0;
        }

        /// <summary>Size of frame 1: what the Windows code reported and what the callers lay out.</summary>
        public int PixelWidth { get; }

        public int PixelHeight { get; }

        public int FrameCount { get; }

        public bool IsAnimated { get; }

        /// <summary>
        /// Opens and demuxes <paramref name="fileName"/>, or returns null when it cannot be read or
        /// is not WebP. Not exceptional: MessageFactory asks this of every file the user attaches,
        /// and AnimatedImage falls back to the platform decoders.
        /// </summary>
        public static WebpImage Open(string fileName)
        {
            if (!UnigramNative.IsAvailable || string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            WebpImageHandle handle = null;

            try
            {
                handle = unigram_image_webp_open_file(fileName);
                if (handle == null || handle.IsInvalid)
                {
                    handle?.Dispose();
                    return null;
                }

                var info = new WebpInfo
                {
                    StructSize = Marshal.SizeOf<WebpInfo>()
                };

                if (unigram_image_webp_get_info(handle, ref info) != 0 || info.Width <= 0 || info.Height <= 0)
                {
                    handle.Dispose();
                    return null;
                }

                return new WebpImage(handle, info);
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"webp: open({fileName})", ex);
                handle?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Size of frame 1 without decoding anything, for
        /// <see cref="PlaceholderImageHelper.IsWebP"/>. False means "not a WebP", which is the
        /// answer that decides whether a file is sent as a sticker.
        /// </summary>
        public static bool Probe(string fileName, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            if (!UnigramNative.IsAvailable || string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            try
            {
                var info = new WebpInfo
                {
                    StructSize = Marshal.SizeOf<WebpInfo>()
                };

                if (unigram_image_webp_probe_file(fileName, ref info) != 0)
                {
                    return false;
                }

                pixelWidth = info.Width;
                pixelHeight = info.Height;
                return info.Width > 0 && info.Height > 0;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"webp: probe({fileName})", ex);
                return false;
            }
        }

        /// <summary>
        /// Decodes frame 1 into a fresh array of <paramref name="width"/> * <paramref name="height"/>
        /// * 4 bytes, premultiplied BGRA, packed. Null on failure.
        /// <para>The array is allocated here and handed to the caller because that is what
        /// DrawWebP's contract is: it returns an IBuffer that the presenter keeps. Pooling it would
        /// mean the pool outlives the frame it is showing.</para>
        /// </summary>
        public unsafe byte[] Decode(int width, int height)
        {
            if (_handle == null || width <= 0 || height <= 0)
            {
                return null;
            }

            try
            {
                var pixels = new byte[width * height * 4];

                fixed (byte* buffer = pixels)
                {
                    // stride 0 = packed, which is the layout every IBuffer in the port holds.
                    var status = unigram_image_webp_decode(_handle, width, height, buffer, 0, pixels.Length);
                    if (status != 0)
                    {
                        UnigramNative.Report(Logger.LogLevel.Warning,
                            $"webp: decode {width}x{height} failed with {status}: {UnigramNative.LastError()}");
                        return null;
                    }
                }

                return pixels;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"webp: decode({width}x{height})", ex);
                return null;
            }
        }

        /// <summary>
        /// The whole of <see cref="PlaceholderImageHelper.DrawWebP"/> except wrapping the result in
        /// an IBuffer: opens <paramref name="fileName"/>, shrinks it to fit
        /// <paramref name="maxWidth"/> and decodes it. Null when the file is not a WebP this can
        /// decode, which is the caller's cue to fall back to the platform decoders.
        ///
        /// <para>The scaling is upstream's policy to the letter, quirk included: maxWidth is
        /// compared against BOTH sides and divides both, so it is really a bounding box; the ratio
        /// is truncated; and an image already smaller than the box is decoded at its native size,
        /// never enlarged.</para>
        ///
        /// <para>It lives here, and not in PlaceholderImageHelper, so that the path a sticker
        /// actually takes can be exercised from a console spike -- PlaceholderImageHelper drags in
        /// half of WinUI.</para>
        /// </summary>
        public static byte[] DecodeToFit(string fileName, int maxWidth, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            using var image = Open(fileName);
            if (image == null)
            {
                return null;
            }

            var width = image.PixelWidth;
            var height = image.PixelHeight;

            if (maxWidth > 0 && (width > maxWidth || height > maxWidth))
            {
                var ratio = Math.Min((double)maxWidth / width, (double)maxWidth / height);

                // Truncating, like upstream, but never to zero: libwebp rejects a 0-wide target and
                // a 1x1 thumbnail still beats no image at all.
                width = Math.Max(1, (int)(width * ratio));
                height = Math.Max(1, (int)(height * ratio));
            }

            var pixels = image.Decode(width, height);
            if (pixels == null)
            {
                return null;
            }

            pixelWidth = width;
            pixelHeight = height;
            return pixels;
        }

        public void Dispose()
        {
            _handle?.Dispose();
        }

        /// <summary>
        /// <c>unigram_webp_info</c>: eight int32 fields, 32 bytes, no padding. The caller fills
        /// <see cref="StructSize"/> so the library knows how much of it to write.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct WebpInfo
        {
            public int StructSize;
            public int Width;
            public int Height;
            public int CanvasWidth;
            public int CanvasHeight;
            public int FrameCount;
            public int HasAlpha;
            public int HasAnimation;
        }

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_image_webp_open_file", StringMarshalling = StringMarshalling.Utf8)]
        private static partial WebpImageHandle unigram_image_webp_open_file(string path);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_image_webp_get_info")]
        private static partial int unigram_image_webp_get_info(WebpImageHandle image, ref WebpInfo info);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_image_webp_probe_file", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int unigram_image_webp_probe_file(string path, ref WebpInfo info);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_image_webp_decode")]
        private static unsafe partial int unigram_image_webp_decode(WebpImageHandle image, int width, int height, byte* pixels, int stride, int capacity);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_image_webp_close")]
        internal static partial void unigram_image_webp_close(IntPtr image);
    }

    /// <summary>
    /// Holds the file's bytes and the demuxer that points into them; releasing it is what frees
    /// both. SafeHandle so a decode in flight on the animation thread cannot be pulled out from
    /// under the library.
    /// </summary>
    internal sealed class WebpImageHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WebpImageHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            WebpImage.unigram_image_webp_close(handle);
            return true;
        }
    }
}
