//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Telegram.Native
{
    // The Direct2D/DirectWrite helper of Telegram.Native, on Skia. Blurred thumbnails
    // (DrawBlurred), the clip geometries whose callers are in the phase 1 subset and the text
    // metrics (ContentEnd, LineMetrics, RangeMetrics, LayoutMetrics, MaxLines - all of them over
    // Uno's own text layout, see Native/Text/UnoTextLayout.cs) are real; the tail brush, SVG
    // patterns and Encode are still answering with values the callers already tolerate, and are
    // what to implement next.
    public sealed partial class PlaceholderImageHelper : IDisposable
    {
        public PlaceholderImageHelper(Window window)
        {
        }

        public void HandleDeviceLost()
        {
        }

        public CompositionGraphicsDevice Device => null;

        public static void WriteBytes(IList<byte> hash, IRandomAccessStream randomAccessStream)
        {
            var bytes = hash as byte[] ?? hash.ToArray();

            var stream = randomAccessStream.AsStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            randomAccessStream.Seek(0);
        }

        /// <summary>
        /// Decodes a WebP into premultiplied BGRA for AnimatedImage.LoadWebP, shrinking it to fit
        /// <paramref name="maxWidth"/>. Null means "not a WebP I could decode", and the caller then
        /// retries with the platform image decoders -- so this must not throw and must not report a
        /// size it did not produce.
        /// <para>Everything below the IBuffer is <see cref="WebpImage.DecodeToFit"/>, including the
        /// scaling policy, so that the path a sticker takes can be tested outside the app.</para>
        /// </summary>
        public static IBuffer DrawWebP(string fileName, int maxWidth, out int pixelWidth, out int pixelHeight)
        {
            var pixels = WebpImage.DecodeToFit(fileName, maxWidth, out pixelWidth, out pixelHeight);
            if (pixels == null)
            {
                return null;
            }

            // AsBuffer wraps the array, no copy: the presenter keeps this buffer as the frame.
            return BufferSurface.Create(pixels);
        }

        /// <summary>
        /// Whether <paramref name="fileName"/> is a WebP, and how big its first frame is, without
        /// decoding it. MessageFactory uses it to decide whether an attachment can be sent as a
        /// sticker.
        /// </summary>
        public static bool IsWebP(string fileName, out int pixelWidth, out int pixelHeight)
        {
            return WebpImage.Probe(fileName, out pixelWidth, out pixelHeight);
        }

        public IList<string> GetSystemFontFamilies(IList<string> localeNames)
        {
            return new List<string>();
        }

        public FreeformGradientSurface CreateFreeformGradient(IList<int> colors)
        {
            return new FreeformGradientSurface(colors);
        }

        // 12.10 takes the XamlRoot (the tail is cached per window there) and splits the mask
        // out. Both stay inert on this head, as GetTail always has been; every caller null-checks.
        public CompositionEffectBrush GetTail(XamlRoot xamlRoot, float topLeftRadius, float topRightRadius, float bottomRightRadius, float bottomLeftRadius)
        {
            return null;
        }

        public CompositionBrush GetTailMask(XamlRoot xamlRoot, float topLeftRadius, float topRightRadius, float bottomRightRadius, float bottomLeftRadius)
        {
            return null;
        }

        public CompositionPath GetEllipticalClip(float width, float height, float radius, float x, float y)
        {
            return null;
        }

        /// <summary>
        /// One rounded rectangle per inline-keyboard button, as a single path: the clip that keeps
        /// the ripple of ReplyMarkupInlinePanel inside its buttons. Only the outer corners of the
        /// last row take the bubble's radii; everything else is the flat 4.
        /// </summary>
        public CompositionPath GetReplyMarkupClip(IList<IList<Rect>> buttons, float bottomRightRadius, float bottomLeftRadius)
        {
            if (buttons == null || buttons.Count == 0)
            {
                return null;
            }

            var path = ClipGeometry.CreatePath();

            for (int j = 0; j < buttons.Count; j++)
            {
                var row = buttons[j];
                if (row == null)
                {
                    continue;
                }

                for (int i = 0; i < row.Count; i++)
                {
                    var button = row[i];

                    var bottomRight = 4f;
                    var bottomLeft = 4f;

                    // Only the last row sits on the bottom of the bubble, so only its first and
                    // last button follow the bubble's own corners.
                    if (j == buttons.Count - 1)
                    {
                        if (i == 0)
                        {
                            bottomLeft = bottomLeftRadius;
                        }

                        if (i == row.Count - 1)
                        {
                            bottomRight = bottomRightRadius;
                        }
                    }

                    ClipGeometry.AppendButton(path, (float)button.X, (float)button.Y, (float)button.Width, (float)button.Height, 4, 4, bottomRight, bottomLeft);
                }
            }

            return new CompositionPath(new CanvasGeometry(path));
        }

        /// <summary>
        /// The bars of a voice note's waveform, as the clip <see cref="Telegram.Controls.ProgressVoice"/>
        /// puts on its root grid. Null when there is nothing to draw -- the caller assigns it to a
        /// <c>CompositionGeometricClip</c>, and no clip is the right answer for no waveform.
        /// </summary>
        public CompositionPath GetVoiceNoteClip(IList<byte> waveform, double waveformWidth)
        {
            if (waveform == null || waveform.Count == 0)
            {
                return null;
            }

            // A path with no figures is returned rather than null when there is no width yet
            // (ProgressVoice asks once from ArrangeOverride before it has one): the caller feeds
            // the result straight into CreatePathGeometry and a null there is a crash waiting for
            // the first frame, while an empty clip is simply an invisible waveform for one pass.
            var path = ClipGeometry.CreatePath();
            ClipGeometry.AppendVoiceNote(path, waveform, waveformWidth);

            return new CompositionPath(new CanvasGeometry(path));
        }

        /// <summary>
        /// Wraps each run of line rectangles in one outline whose corners round into each other:
        /// the shape behind a quoted block, a highlighted range and the loading skeleton.
        /// </summary>
        public CompositionPath GetRoundedPolygon(IList<IList<Rect>> shapes)
        {
            if (shapes == null || shapes.Count == 0)
            {
                return null;
            }

            var path = ClipGeometry.CreatePath();

            for (int j = 0; j < shapes.Count; j++)
            {
                var rectangles = shapes[j];
                if (rectangles == null || rectangles.Count == 0)
                {
                    continue;
                }

                var lines = new SKRect[rectangles.Count];

                for (int i = 0; i < rectangles.Count; i++)
                {
                    var rect = rectangles[i];
                    lines[i] = new SKRect((float)rect.X, (float)rect.Y, (float)(rect.X + rect.Width), (float)(rect.Y + rect.Height));
                }

                ClipGeometry.AppendRoundedPolygon(path, lines);
            }

            return new CompositionPath(new CanvasGeometry(path));
        }

        public void Encode(IBuffer source, IRandomAccessStream destination, int width, int height, int rotation)
        {
        }

        public Task<ChatBackgroundPattern> DrawSvgAsync(Compositor compositor, string path, float intensity, bool negative, double dpi)
        {
            return Task.FromResult<ChatBackgroundPattern>(null);
        }

        public ChatBackgroundPattern DrawSvg(Compositor compositor, string path, float intensity, bool negative, double dpi)
        {
            return null;
        }

        /// <summary>
        /// Decodes <paramref name="fileName"/> and Gaussian-blurs it at its own size, the way
        /// DrawBlurredImpl did on Direct2D. Null when the file cannot be read or decoded; the
        /// callers (ThumbnailController, ImageView) ask this of files that may still be
        /// downloading, so that is an answer, not a failure.
        /// <para>Everything below the pixels is <see cref="BlurredImage"/>, so the path a
        /// thumbnail takes can be tested outside the app.</para>
        /// </summary>
        public PixelBitmap DrawBlurred(string fileName, float blurAmount)
        {
            var pixels = BlurredImage.Decode(fileName, blurAmount, out int pixelWidth, out int pixelHeight);
            if (pixels == null)
            {
                return null;
            }

            return new PixelBitmap(pixels, pixelWidth, pixelHeight);
        }

        /// <summary>
        /// The same, for an encoded image already in memory: the minithumbnail TDLib ships inline
        /// with a message, which never touches the disk.
        /// </summary>
        public PixelBitmap DrawBlurred(IList<byte> bytes, float blurAmount)
        {
            if (bytes == null)
            {
                return null;
            }

            var encoded = bytes as byte[] ?? bytes.ToArray();
            var pixels = BlurredImage.Decode(encoded, blurAmount, out int pixelWidth, out int pixelHeight);
            if (pixels == null)
            {
                return null;
            }

            return new PixelBitmap(pixels, pixelWidth, pixelHeight);
        }

        /// <summary>
        /// Where the last line of <paramref name="text"/> ends (X) and how tall the text is down
        /// to the bottom of that line (Y), laid out at <paramref name="width"/>.
        /// <para>This is what puts the timestamp at the end of the last line instead of on top of
        /// the text: <see cref="Telegram.Controls.Messages.MessageBubblePanel"/> subtracts X from
        /// the bubble width and, if the footer does not fit in what is left, either indents the
        /// text or gives the footer a line of its own. A Y beyond the block's own height is the
        /// panel's "I could not measure this" signal, so that is what a failed measurement
        /// returns.</para>
        /// </summary>
        public Vector2 ContentEnd(string text, IList<TextStylePart> entities, double fontSize, double width)
        {
            ResolveFonts();
            return UnoTextLayout.ContentEnd(text, entities, fontSize, width);
        }

        /// <summary>
        /// One rectangle per line of <paramref name="text"/>: the shape the forwarded-from header
        /// rounds its hover ripple to, and the one the loading skeleton is clipped with.
        /// </summary>
        public IList<Rect> LineMetrics(string text, IList<TextStylePart> entities, double fontSize, double width, bool rtl)
        {
            ResolveFonts();
            return UnoTextLayout.LineMetrics(text, entities, fontSize, width, rtl);
        }

        /// <summary>
        /// The same, for the slice [<paramref name="offset"/>, <paramref name="offset"/> +
        /// <paramref name="length"/>) only: the geometry behind a search hit, a manual quote and
        /// a spoiler. <paramref name="wrap"/> false measures the text as a single trimmed line.
        /// </summary>
        public IList<Rect> RangeMetrics(string text, int offset, int length, IList<TextStylePart> entities, double fontSize, double width, bool rtl, bool wrap)
        {
            ResolveFonts();
            return UnoTextLayout.RangeMetrics(text, offset, length, entities, fontSize, width, rtl, wrap);
        }

        /// <summary>
        /// The box the whole text occupies at <paramref name="width"/>.
        /// </summary>
        public Rect LayoutMetrics(string text, int offset, int length, IList<TextStylePart> entities, double fontSize, double width, bool rtl)
        {
            ResolveFonts();
            return UnoTextLayout.LayoutMetrics(text, offset, length, entities, fontSize, width, rtl);
        }

        /// <summary>
        /// How tall the text is, how tall its first <paramref name="maxLines"/> lines are, and
        /// where to cut it to keep only those. <c>TruncatedHeight &lt; Height</c> is the test
        /// <see cref="Telegram.Controls.FormattedTextBlock"/> uses to decide whether an
        /// expandable quote needs its "show more" chevron.
        /// </summary>
        public MaxLinesMetrics MaxLines(string text, int offset, int length, IList<TextStylePart> entities, double fontSize, double width, bool rtl, int maxLines)
        {
            ResolveFonts();
            return UnoTextLayout.MaxLines(text, offset, length, entities, fontSize, width, rtl, maxLines);
        }

        private static bool _fontsResolved;

        /// <summary>
        /// Hands the layout engine the font the message text is actually painted with, so that
        /// what is measured is the text the bubble lays out. The Windows helper hardcodes
        /// "Segoe UI Emoji" over a private font collection; here the theme resource keys are the
        /// source of truth (see <c>Theme.UpdateEmojiSet</c>, which on Linux resolves each key to
        /// a single family, because Uno reads <c>FontFamily.Source</c> as one name and never
        /// splits the comma separated list Windows uses).
        /// </summary>
        private static void ResolveFonts()
        {
            if (_fontsResolved)
            {
                return;
            }

            // Theme.Current is [ThreadStatic] and is published by the Theme constructor; a
            // measurement taken before that just uses the inherited font and asks again next
            // time, rather than caching a guess.
            var theme = Telegram.Common.Theme.Current;
            if (theme == null)
            {
                return;
            }

            _fontsResolved = true;

            if (theme.TryGetValue("EmojiThemeFontFamily", out var family) && family is FontFamily fontFamily)
            {
                UnoTextLayout.FontFamily = fontFamily;
            }

            UnoTextLayout.MonospaceFontFamily = Telegram.Common.Theme.MonospaceFontFamily;
        }

        public void Dispose()
        {
        }
    }
}
