//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.IO;
using SkiaSharp;

namespace Telegram.Services
{
    /// <summary>
    /// Draws the round avatar that goes on the notification, as the <c>image-data</c> hint wants
    /// it: straight (non premultiplied) RGBA.
    ///
    /// <para>On Windows this is one attribute — <c>&lt;image placement='appLogoOverride'
    /// hint-crop='circle' src='ms-appdata:///local/...'/&gt;</c> — and the shell does the rest:
    /// finds the file inside the package's local folder, decodes it, and crops it to a circle.
    /// None of those three exist here. The freedesktop spec has no "crop" and no app-relative URI;
    /// it takes either a path (square, as TDLib stores it) or the pixels. So the pixels are drawn
    /// here, and this is the one place in the port that hands out straight alpha instead of the
    /// premultiplied BGRA everything else speaks.</para>
    ///
    /// <para>When there is no photo the same gradient-and-initials placeholder the chat list draws
    /// is reproduced, from the colours the caller already computed with
    /// <c>ProfilePicture.Chat(...)</c>: a notification whose icon is the app logo tells you nothing
    /// about which chat it came from, and the placeholder does.</para>
    /// </summary>
    public static class NotificationAvatar
    {
        /// <summary>
        /// Side of the avatar in pixels. Big enough for a 2x panel (GNOME draws the icon at 48
        /// logical points here) and small enough that the hint stays around 36 KB.
        /// </summary>
        public const int Size = 96;

        /// <summary>
        /// The chat photo, cropped to a circle. Null when the file cannot be decoded, so the caller
        /// can fall back to <see cref="FromInitials"/>.
        /// </summary>
        public static NotificationImage FromFile(string path, int size = Size)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using var source = SKBitmap.Decode(path);
                if (source == null)
                {
                    return null;
                }

                return Render(size, (canvas, radius) =>
                {
                    // Cover, not fit: a chat photo is square already, but a stretched one would
                    // look wrong the day TDLib hands out something else.
                    var scale = Math.Max((float)size / source.Width, (float)size / source.Height);
                    var width = source.Width * scale;
                    var height = source.Height * scale;

                    var destination = SKRect.Create((size - width) / 2, (size - height) / 2, width, height);

                    using var image = SKImage.FromBitmap(source);
                    using var paint = new SKPaint { IsAntialias = true };
                    canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"Cannot draw the notification avatar from {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The placeholder: a vertical gradient circle with the initials on it, exactly the two
        /// colours the chat list uses for that chat.
        /// </summary>
        /// <param name="text">Initials, or a glyph from Telegram.ttf when <paramref name="isGlyph"/>.</param>
        /// <param name="top">Top gradient stop, 0xAARRGGBB.</param>
        /// <param name="bottom">Bottom gradient stop, 0xAARRGGBB.</param>
        /// <param name="isGlyph">The text is an icon codepoint (Saved Messages, the replies bot, a deleted account).</param>
        /// <param name="glyphFont">Full path of Telegram.ttf, or null to fall back to the default face.</param>
        public static NotificationImage FromInitials(string text, uint top, uint bottom, bool isGlyph, string glyphFont, int size = Size)
        {
            try
            {
                return Render(size, (canvas, radius) =>
                {
                    using (var shader = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0),
                        new SKPoint(0, size),
                        new[] { new SKColor(top), new SKColor(bottom) },
                        null,
                        SKShaderTileMode.Clamp))
                    using (var paint = new SKPaint { IsAntialias = true, Shader = shader })
                    {
                        canvas.DrawRect(SKRect.Create(0, 0, size, size), paint);
                    }

                    if (string.IsNullOrEmpty(text))
                    {
                        return;
                    }

                    using var typeface = LoadTypeface(isGlyph, glyphFont);
                    using var font = new SKFont(typeface, size * (isGlyph ? 0.5f : 0.42f));
                    using var paintText = new SKPaint { IsAntialias = true, Color = SKColors.White };

                    font.MeasureText(text, out var bounds, paintText);

                    // Centred on the ink, not on the baseline: initials have no descenders and
                    // centring on the metrics would sit them too high.
                    var x = size / 2f - bounds.MidX;
                    var y = size / 2f - bounds.MidY;

                    canvas.DrawText(text, x, y, SKTextAlign.Left, font, paintText);
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"Cannot draw the notification placeholder: {ex.Message}");
                return null;
            }
        }

        private static SKTypeface LoadTypeface(bool isGlyph, string glyphFont)
        {
            if (isGlyph && !string.IsNullOrEmpty(glyphFont) && File.Exists(glyphFont))
            {
                var typeface = SKTypeface.FromFile(glyphFont);
                if (typeface != null)
                {
                    return typeface;
                }
            }

            return SKTypeface.CreateDefault();
        }

        private static NotificationImage Render(int size, Action<SKCanvas, float> draw)
        {
            // Drawing happens premultiplied because that is the only alpha type a Skia canvas
            // composites in; the conversion at the end is what the hint asks for.
            using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);

                using (var circle = new SKPath())
                {
                    circle.AddCircle(size / 2f, size / 2f, size / 2f);

                    canvas.Save();
                    canvas.ClipPath(circle, SKClipOperation.Intersect, true);

                    draw(canvas, size / 2f);

                    canvas.Restore();
                }
            }

            return new NotificationImage(Unpremultiply(bitmap.Bytes), size, size);
        }

        private static byte[] Unpremultiply(byte[] pixels)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                if (alpha == 0 || alpha == 255)
                {
                    continue;
                }

                pixels[i + 0] = (byte)Math.Min(255, pixels[i + 0] * 255 / alpha);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
            }

            return pixels;
        }
    }
}
