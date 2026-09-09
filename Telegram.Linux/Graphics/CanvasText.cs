//
// Telegram.Linux — Skia stand-in for the sliver of Win2D's text API that Unigram uses to turn a
// string into a GEOMETRY (not into pixels).
//
// One caller in the phase-1 subset: Controls/ProfileRating.cs. It lays a number out in a 72x68
// box and then does CanvasGeometry.CreateText(layout), so it can XOR the glyph outlines out of
// the badge shape — the digit is a hole in the badge, not text drawn on top of it. That is why a
// TextBlock is not a substitute here: there is no text to draw, there is a path to subtract.
//
// The Skia equivalent is SKFont.GetTextPath(string, SKPoint origin), which returns the filled
// outline of the string with `origin` as the BASELINE START. Everything below is the arithmetic
// that turns Win2D's "box plus alignment" into that one point.
//
// Deliberately small: this is not a DirectWrite port. No wrapping, no bidi, no per-glyph
// formatting, no hit-testing — Win2D's CanvasTextLayout has all of that and Unigram does not use
// any of it from here. If a second caller ever needs a line break, it needs a real layout, not an
// extension of this file.
//
using System;
using System.Numerics;
using SkiaSharp;

namespace Microsoft.Graphics.Canvas.Text
{
    /// <summary>Win2D's <c>CanvasHorizontalAlignment</c>, same member order.</summary>
    public enum CanvasHorizontalAlignment
    {
        Left,
        Right,
        Center,
        Justified,
    }

    /// <summary>Win2D's <c>CanvasVerticalAlignment</c>, same member order.</summary>
    public enum CanvasVerticalAlignment
    {
        Top,
        Bottom,
        Center,
    }

    /// <summary>
    /// The properties of Win2D's <c>CanvasTextFormat</c> that this port reads. It is a plain
    /// property bag: the typeface is not resolved until a layout asks for it.
    /// </summary>
    public sealed partial class CanvasTextFormat : IDisposable
    {
        public float FontSize { get; set; } = 20f;

        public string FontFamily { get; set; }

        public global::Windows.UI.Text.FontWeight FontWeight { get; set; } = global::Microsoft.UI.Text.FontWeights.Normal;

        public global::Windows.UI.Text.FontStyle FontStyle { get; set; } = global::Windows.UI.Text.FontStyle.Normal;

        public CanvasHorizontalAlignment HorizontalAlignment { get; set; } = CanvasHorizontalAlignment.Left;

        public CanvasVerticalAlignment VerticalAlignment { get; set; } = CanvasVerticalAlignment.Top;

        public void Dispose()
        {
        }

        internal SKTypeface CreateTypeface()
        {
            // Weight travels straight through: Win2D and Skia both use the OpenType 1..1000 scale,
            // so SemiBold is 600 in either. Width is always Normal — nothing in Unigram sets a
            // stretch on a geometry format.
            var weight = (SKFontStyleWeight)Math.Clamp((int)FontWeight.Weight, 1, 1000);
            var slant = FontStyle == global::Windows.UI.Text.FontStyle.Normal
                ? SKFontStyleSlant.Upright
                : SKFontStyleSlant.Italic;

            // "Segoe UI" does not exist here. SKTypeface.FromFamilyName does not fail on an unknown
            // family, it falls back through fontconfig to the default sans, which is what we want:
            // asking for the Windows family name and taking whatever the desktop answers keeps the
            // shape as close to upstream as this machine can get, and never returns null.
            if (!string.IsNullOrEmpty(FontFamily))
            {
                var named = SKTypeface.FromFamilyName(FontFamily, weight, SKFontStyleWidth.Normal, slant);
                if (named != null)
                {
                    return named;
                }
            }

            return SKTypeface.FromFamilyName(null, weight, SKFontStyleWidth.Normal, slant)
                ?? SKTypeface.CreateDefault();
        }
    }

    /// <summary>
    /// A single run of text measured inside a box, enough to hand
    /// <see cref="Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateText"/> a path.
    /// </summary>
    public sealed partial class CanvasTextLayout : IDisposable
    {
        private readonly string _text;
        private readonly float _requestedWidth;
        private readonly float _requestedHeight;
        private readonly CanvasHorizontalAlignment _horizontal;
        private readonly CanvasVerticalAlignment _vertical;

        private SKTypeface _typeface;
        private SKFont _font;

        public CanvasTextLayout(ICanvasResourceCreator resourceCreator, string text, CanvasTextFormat format, float requestedWidth, float requestedHeight)
        {
            _text = text ?? string.Empty;
            _requestedWidth = requestedWidth;
            _requestedHeight = requestedHeight;
            _horizontal = format?.HorizontalAlignment ?? CanvasHorizontalAlignment.Left;
            _vertical = format?.VerticalAlignment ?? CanvasVerticalAlignment.Top;

            _typeface = format?.CreateTypeface() ?? SKTypeface.CreateDefault();
            _font = new SKFont(_typeface, format?.FontSize ?? 20f)
            {
                Subpixel = true,
            };
        }

        public float RequestedWidth => _requestedWidth;

        public float RequestedHeight => _requestedHeight;

        /// <summary>
        /// The outline of the whole run, positioned inside the requested box according to the
        /// format's alignment. Never null; empty text gives an empty path.
        /// </summary>
        internal SKPath BuildPath()
        {
            if (_font == null || _text.Length == 0)
            {
                return new SKPath();
            }

            var advance = _font.MeasureText(_text, out var ink);

            var x = _horizontal switch
            {
                CanvasHorizontalAlignment.Right => _requestedWidth - advance,
                // Justified has no meaning for a single run that is not being wrapped; Win2D
                // renders it left-aligned in that case and so does this.
                CanvasHorizontalAlignment.Center => (_requestedWidth - advance) / 2f,
                _ => 0f,
            };

            // GetTextPath takes a BASELINE origin, so vertical placement is expressed against the
            // font metrics rather than against the ink box. Centring on the ink (ink.MidY) is what
            // looks right for digits, which have no descender: centring on the metrics would sit
            // them visibly high. This is the same choice NotificationAvatar makes.
            var metrics = _font.Metrics;
            var y = _vertical switch
            {
                CanvasVerticalAlignment.Bottom => _requestedHeight + metrics.Ascent,
                CanvasVerticalAlignment.Center => _requestedHeight / 2f - ink.MidY,
                _ => -metrics.Ascent,
            };

            return _font.GetTextPath(_text, new SKPoint(x, y)) ?? new SKPath();
        }

        public void Dispose()
        {
            _font?.Dispose();
            _font = null;

            _typeface?.Dispose();
            _typeface = null;
        }
    }
}

namespace Microsoft.Graphics.Canvas.Geometry
{
    using Microsoft.Graphics.Canvas.Text;

    public sealed partial class CanvasGeometry
    {
        /// <summary>
        /// Win2D's <c>CanvasGeometry.CreateText</c>: the filled outline of a laid-out run.
        /// </summary>
        public static CanvasGeometry CreateText(CanvasTextLayout textLayout)
        {
            return new CanvasGeometry(textLayout?.BuildPath() ?? new SKPath());
        }
    }
}
