//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.IO;
using SkiaSharp;

namespace Telegram.Native
{
    /// <summary>
    /// The blur behind <see cref="PlaceholderImageHelper.DrawBlurred(string, float)"/>: decode an
    /// image and Gaussian-blur it at its own size, giving back premultiplied BGRA.
    ///
    /// <para>What Telegram blurs is almost always a <c>minithumbnail</c>: a JPEG of a few dozen
    /// pixels a side that ships inline with the message, blurred here and then stretched by the
    /// <c>ImageBrush</c> to whatever size the bubble is. That is why nothing is scaled up first --
    /// the Windows implementation (Telegram.Native/PlaceholderImageHelper.cpp,
    /// <c>DrawBlurredImpl</c>) also blurs at the source size and lets the brush do the rest, so a
    /// 40x40 thumbnail costs 40x40 worth of blur no matter how big it ends up on screen.</para>
    ///
    /// <para>It lives here, and not in PlaceholderImageHelper, so that the path a thumbnail takes
    /// can be exercised from a console spike (unigram-linux/spikes/AvatarSpike) -- the helper
    /// itself drags in Uno.</para>
    /// </summary>
    internal static class BlurredImage
    {
        /// <summary>
        /// Blurs the image in <paramref name="fileName"/>. Null means "could not read or decode
        /// it", which is not exceptional: the callers (ThumbnailController, ImageView) ask this of
        /// files that may still be downloading, and they swallow failures.
        /// </summary>
        public static byte[] Decode(string fileName, float blurAmount, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            try
            {
                using var stream = new SKFileStream(fileName);
                if (!stream.IsValid)
                {
                    return null;
                }

                using var bitmap = SKBitmap.Decode(stream);
                return Blur(bitmap, blurAmount, out pixelWidth, out pixelHeight);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Blurs an encoded image held in memory -- the minithumbnail case, where the bytes come
        /// straight from TDLib and never touch the disk.
        /// </summary>
        public static byte[] Decode(byte[] encoded, float blurAmount, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            if (encoded == null || encoded.Length == 0)
            {
                return null;
            }

            try
            {
                using var data = SKData.CreateCopy(encoded);
                using var bitmap = SKBitmap.Decode(data);
                return Blur(bitmap, blurAmount, out pixelWidth, out pixelHeight);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// <paramref name="blurAmount"/> is the standard deviation the Windows code handed to
        /// D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, and Skia's sigma means the same thing, so the
        /// callers' numbers (3 for a minithumbnail, 15 for a spoiler) carry over unchanged.
        /// </summary>
        /// <remarks>
        /// One deliberate difference from Direct2D: the blur clamps at the edges instead of
        /// treating the outside as transparent black (D2D1_BORDER_MODE_SOFT, the default the C++
        /// left in place). With a sigma of 3 on a 40px thumbnail a soft border eats an eighth of
        /// the image into transparency, and the result is stretched to fill a bubble, so the
        /// bubble's background would show through a halo all around it -- and for a spoiler, whose
        /// whole job is to cover the picture, that halo leaks the picture. Clamping is what every
        /// other Telegram client does with the same thumbnails.
        /// </remarks>
        private static byte[] Blur(SKBitmap bitmap, float blurAmount, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = 0;
            pixelHeight = 0;

            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return null;
            }

            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(info);
            if (surface == null)
            {
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            if (image == null)
            {
                return null;
            }

            surface.Canvas.Clear(SKColors.Transparent);

            if (blurAmount > 0)
            {
                using var filter = SKImageFilter.CreateBlur(blurAmount, blurAmount, SKShaderTileMode.Clamp);
                using var paint = new SKPaint { ImageFilter = filter };

                surface.Canvas.DrawImage(image, SKPoint.Empty, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
            }
            else
            {
                surface.Canvas.DrawImage(image, SKPoint.Empty, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }

            var pixels = new byte[info.BytesSize];

            unsafe
            {
                fixed (byte* target = pixels)
                {
                    // Packed BGRA, the layout WriteableBitmap.PixelBuffer and the rest of the
                    // port's frame buffers use.
                    if (!surface.ReadPixels(info, (IntPtr)target, info.RowBytes, 0, 0))
                    {
                        return null;
                    }
                }
            }

            pixelWidth = info.Width;
            pixelHeight = info.Height;
            return pixels;
        }

        /// <summary>
        /// PNG for the bytes above. Used to hand the pixels to Uno, which has no way to take a raw
        /// buffer for an <c>ImageSource</c> whose size is not known when it is created -- see
        /// <see cref="PixelBitmap"/>.
        /// </summary>
        public static byte[] Encode(byte[] pixels, int pixelWidth, int pixelHeight)
        {
            if (pixels == null || pixelWidth <= 0 || pixelHeight <= 0)
            {
                return null;
            }

            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (pixels.Length < info.BytesSize)
            {
                return null;
            }

            using var image = SKImage.FromPixelCopy(info, pixels);
            if (image == null)
            {
                return null;
            }

            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }

        /// <summary>Writes <paramref name="pixels"/> out as a PNG. Spikes and diagnostics only.</summary>
        public static void Save(byte[] pixels, int pixelWidth, int pixelHeight, string path)
        {
            var png = Encode(pixels, pixelWidth, pixelHeight);
            if (png != null)
            {
                File.WriteAllBytes(path, png);
            }
        }
    }
}
