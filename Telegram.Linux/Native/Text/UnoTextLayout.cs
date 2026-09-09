//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Telegram.Native
{
    /// <summary>
    /// The text measurements <see cref="PlaceholderImageHelper"/> answers with, taken from Uno's
    /// own layout of the same string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows these come from DirectWrite: <c>PlaceholderImageHelper.cpp</c> builds an
    /// <c>IDWriteTextLayout</c> for the string and hit-tests it. The number that matters
    /// (<see cref="ContentEnd"/>) is then compared against the size the XAML text block reported
    /// for the *same* string, so the two engines have to agree - on Windows they do, because XAML
    /// text is DirectWrite too.
    /// </para>
    /// <para>
    /// On Skia they would not. Uno lays text out in
    /// <c>Microsoft.UI.Xaml.Documents.UnicodeText</c>: ICU for the bidi runs and the line-break
    /// opportunities, HarfBuzz for shaping, and a per-codepoint font fallback chain
    /// (<c>FeatureConfiguration.Font.SymbolsFont</c>, then the service
    /// <see cref="Telegram.Common.LinuxFontFallback"/> registers, then
    /// <c>SKFontManager.MatchCharacter</c>). Re-implementing that on bare <c>SKFont</c> would
    /// drift from the pixels Uno actually paints - and a footer placed from a measurement that
    /// disagrees with the painted line is exactly the bug this replaces. So the measurement is
    /// taken from Uno: a detached <see cref="TextBlock"/> is configured like the one that renders
    /// the message, measured, and its parsed layout is queried.
    /// </para>
    /// <para>
    /// That last step is the only unofficial part. <c>TextBlock.ParsedText</c> is
    /// <c>internal</c>, and so is <c>IParsedText</c>, whose two useful members are
    /// <c>Rect GetRectForIndex(int)</c> (the caret rectangle at a character index: x, top of the
    /// line, cluster width, line height) and
    /// <c>(int start, int length, bool firstLine, bool lastLine, int lineIndex) GetLineAt(int)</c>.
    /// They are reached by reflection, resolved once, and every entry point degrades to "no
    /// answer" if the lookup fails, which is what the callers already tolerate. Measured against
    /// Uno 6.6.184.
    /// </para>
    /// <para>Everything here is deliberately free of Telegram types other than the two structs of
    /// <c>Native/TextMetrics.cs</c>, so <c>unigram-linux/spikes/MetricsSpike</c> can link this
    /// file and exercise the very code the app runs.</para>
    /// </remarks>
    internal static class UnoTextLayout
    {
        // The family the message text is rendered with. PlaceholderImageHelper fills these in
        // from Theme.Current; the spike sets them itself. Null means "whatever a bare TextBlock
        // inherits", which is only right for a test. Changing either one invalidates every
        // measurement taken so far, hence the generation counter.
        private static FontFamily _fontFamily;
        private static FontFamily _monospaceFontFamily;
        private static int _generation;

        public static FontFamily FontFamily
        {
            get => _fontFamily;
            set
            {
                if (!ReferenceEquals(_fontFamily, value))
                {
                    _fontFamily = value;
                    _generation++;
                }
            }
        }

        public static FontFamily MonospaceFontFamily
        {
            get => _monospaceFontFamily;
            set
            {
                if (!ReferenceEquals(_monospaceFontFamily, value))
                {
                    _monospaceFontFamily = value;
                    _generation++;
                }
            }
        }

        [ThreadStatic]
        private static TextBlock _scratchPlain;

        [ThreadStatic]
        private static TextBlock _scratchStyled;

        private static PropertyInfo _parsedTextProperty;
        private static MethodInfo _getRectForIndex;
        private static MethodInfo _getLineAt;
        private static PropertyInfo _isRightToLeft;
        private static bool _probed;

        /// <summary>
        /// Whether the reflection handles resolved. False means every method here answers with
        /// the neutral value and the callers fall back the way they do when DirectWrite fails.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                Probe();
                return _parsedTextProperty != null;
            }
        }

        private static void Probe()
        {
            if (_probed)
            {
                return;
            }

            _probed = true;

            try
            {
                _parsedTextProperty = typeof(TextBlock).GetProperty("ParsedText", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch
            {
                _parsedTextProperty = null;
            }
        }

        #region The five PlaceholderImageHelper metrics

        // Same signatures and same answers as the helper, which only adds the font resolution on
        // top - so unigram-linux/spikes/MetricsSpike exercises the code the app runs, not a
        // paraphrase of it.

        /// <summary>
        /// Where the last line ends (X) and how tall the text is down to the bottom of that line
        /// (Y). The neutral answer is <c>(0, float.MaxValue)</c>, which is how the caller is told
        /// the measurement failed.
        /// </summary>
        public static Vector2 ContentEnd(string text, IList<TextStylePart> entities, double fontSize, double width)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Vector2.Zero;
            }

            // Memoised, and this one only: it is the single metric that runs inside a
            // MeasureOverride, once per bubble and per layout pass, and a fresh Uno layout of a
            // message-sized string costs about 300 us (measured in
            // unigram-linux/spikes/MetricsSpike). Everything else here is a hover, a highlight or
            // a quote, none of them on a hot path.
            var cache = Cache();
            var key = new ContentEndKey(text, entities, fontSize, width);

            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // No reading direction here on purpose: the DirectWrite original does not set one
            // either (unlike RangeMetrics, LayoutMetrics and MaxLines), and the caller only wants
            // to know how far right the last line reaches.
            var answer = TryMeasure(text, entities, fontSize, width, false, true, 0, out var measurement)
                ? measurement.ContentEnd()
                : new Vector2(0, float.MaxValue);

            if (cache.Count >= CacheSize)
            {
                cache.Clear();
            }

            cache[key] = answer;
            return answer;
        }

        public static IList<Rect> LineMetrics(string text, IList<TextStylePart> entities, double fontSize, double width, bool rtl)
        {
            return RangeMetrics(text, 0, text?.Length ?? 0, entities, fontSize, width, rtl, true);
        }

        public static IList<Rect> RangeMetrics(string text, int offset, int length, IList<TextStylePart> entities, double fontSize, double width, bool rtl, bool wrap)
        {
            if (string.IsNullOrEmpty(text) || length <= 0)
            {
                return new List<Rect>();
            }

            if (TryMeasure(text, entities, fontSize, width, rtl, wrap, 0, out var measurement))
            {
                return measurement.Range(offset, length);
            }

            return new List<Rect>();
        }

        /// <summary>
        /// The box the whole text occupies. <paramref name="offset"/> and
        /// <paramref name="length"/> are ignored, as they are in the DirectWrite original.
        /// </summary>
        public static Rect LayoutMetrics(string text, int offset, int length, IList<TextStylePart> entities, double fontSize, double width, bool rtl)
        {
            if (string.IsNullOrEmpty(text))
            {
                return default;
            }

            if (TryMeasure(text, entities, fontSize, width, rtl, true, 0, out var measurement))
            {
                return new Rect(0, 0, measurement.Size.Width, measurement.Size.Height);
            }

            return default;
        }

        public static MaxLinesMetrics MaxLines(string text, int offset, int length, IList<TextStylePart> entities, double fontSize, double width, bool rtl, int maxLines)
        {
            if (string.IsNullOrEmpty(text))
            {
                return default;
            }

            if (!TryMeasure(text, entities, fontSize, width, rtl, true, 0, out var full))
            {
                return default;
            }

            var metrics = new MaxLinesMetrics
            {
                X = 0,
                Y = 0,
                Width = full.Size.Width,
                Height = full.Size.Height,
                TruncatedHeight = full.Size.Height,
                TruncatedOffset = length
            };

            if (maxLines <= 0)
            {
                return metrics;
            }

            if (TryMeasure(text, entities, fontSize, width, rtl, true, maxLines, out var capped))
            {
                metrics.TruncatedHeight = capped.Size.Height;
                metrics.TruncatedOffset = capped.TruncatedOffset(text);
            }

            return metrics;
        }

        private const int CacheSize = 512;

        [ThreadStatic]
        private static Dictionary<ContentEndKey, Vector2> _contentEnds;

        [ThreadStatic]
        private static int _cachedGeneration;

        private static Dictionary<ContentEndKey, Vector2> Cache()
        {
            if (_contentEnds == null || _cachedGeneration != _generation)
            {
                _contentEnds = new Dictionary<ContentEndKey, Vector2>();
                _cachedGeneration = _generation;
            }

            return _contentEnds;
        }

        /// <summary>
        /// The entity list is compared by reference on purpose: <c>StyledParagraph.GetParts</c>
        /// hands back the same instance until the paragraph itself is rebuilt, which is exactly
        /// when the answer can change.
        /// </summary>
        private readonly struct ContentEndKey : IEquatable<ContentEndKey>
        {
            private readonly string _text;
            private readonly IList<TextStylePart> _entities;
            private readonly double _fontSize;
            private readonly double _width;

            public ContentEndKey(string text, IList<TextStylePart> entities, double fontSize, double width)
            {
                _text = text;
                _entities = entities;
                _fontSize = fontSize;
                _width = width;
            }

            public bool Equals(ContentEndKey other)
            {
                return _fontSize == other._fontSize
                    && _width == other._width
                    && ReferenceEquals(_entities, other._entities)
                    && string.Equals(_text, other._text, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is ContentEndKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_text, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_entities), _fontSize, _width);
            }
        }

        #endregion

        /// <summary>
        /// Lays <paramref name="text"/> out the way Uno would at <paramref name="width"/> and
        /// hands back a handle on the result. The handle is only valid until the next call on
        /// this thread: there is one scratch block per thread and the next measurement re-parses
        /// it.
        /// </summary>
        public static bool TryMeasure(string text, IList<TextStylePart> entities, double fontSize, double width, bool rtl, bool wrap, int maxLines, out Measurement measurement)
        {
            measurement = default;

            if (text == null || !IsSupported)
            {
                return false;
            }

            try
            {
                // Two blocks, and which one is used matters: a TextBlock whose text was set
                // through Text takes Uno's "no inlines" fast path
                // (TextBlock.UseInlinesFastPath), and touching Inlines even once turns it off for
                // good on that instance. Measured on a 126 character message: 317 us through the
                // Text property against 647 us through an Inlines rebuild. Most messages carry no
                // bold/italic/monospace at all, so they get the cheap block.
                var mask = BuildMask(text, entities);

                TextBlock scratch;

                if (mask == null)
                {
                    scratch = _scratchPlain ??= new TextBlock();
                    scratch.Text = text;
                }
                else
                {
                    scratch = _scratchStyled ??= new TextBlock();
                    SetInlines(scratch, text, mask);
                }

                scratch.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
                scratch.TextAlignment = TextAlignment.Left;
                scratch.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

                // No wrapping means "one line with an ellipsis" in the DirectWrite original
                // (DWRITE_TRIMMING granularity character, '.', 3).
                scratch.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
                scratch.MaxLines = maxLines > 0 ? maxLines : 0;
                scratch.FontSize = fontSize > 0 ? fontSize : 14;

                if (_fontFamily != null)
                {
                    scratch.FontFamily = _fontFamily;
                }

                scratch.Measure(new Size(Sanitize(width), double.PositiveInfinity));

                var parsed = _parsedTextProperty.GetValue(scratch);
                if (parsed == null)
                {
                    return false;
                }

                if (_getRectForIndex == null || _getLineAt == null)
                {
                    var type = parsed.GetType();
                    _getRectForIndex = type.GetMethod("GetRectForIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _getLineAt = type.GetMethod("GetLineAt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _isRightToLeft = type.GetProperty("IsBaseDirectionRightToLeft", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (_getRectForIndex == null || _getLineAt == null)
                    {
                        return false;
                    }
                }

                measurement = new Measurement(parsed, text.Length, scratch.DesiredSize);
                return true;
            }
            catch
            {
                // A layout that cannot be taken is an answer the callers already handle (the
                // Windows helper returns the default of the type when DirectWrite fails), and
                // several of them run inside MeasureOverride, where throwing would leave the
                // whole message tree unmeasured.
                return false;
            }
        }

        private static double Sanitize(double width)
        {
            if (double.IsNaN(width) || width <= 0 || width > 1e6)
            {
                return double.PositiveInfinity;
            }

            return width;
        }

        /// <summary>
        /// Rebuilds the scratch block's inlines: one <see cref="Run"/> per stretch of text that
        /// shares the same style, which is how the real block is built too (see
        /// <c>Telegram.Linux/Xaml/TextElementDirect.cs</c>). Bold is SemiBold, exactly as
        /// <c>AddRunToCollection</c> and the DirectWrite original both apply it.
        /// </summary>
        private static void SetInlines(TextBlock scratch, string text, TextStyle[] mask)
        {
            var inlines = scratch.Inlines;
            inlines.Clear();

            var start = 0;
            for (int i = 1; i <= text.Length; i++)
            {
                if (i < text.Length && mask[i] == mask[start])
                {
                    continue;
                }

                inlines.Add(CreateRun(text.Substring(start, i - start), mask[start]));
                start = i;
            }
        }

        // Null when nothing styles the text, which is the common case and skips the whole
        // per-character pass.
        private static TextStyle[] BuildMask(string text, IList<TextStylePart> entities)
        {
            if (entities == null || entities.Count == 0)
            {
                return null;
            }

            TextStyle[] mask = null;

            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                var style = entity.Type & (TextStyle.Bold | TextStyle.Italic | TextStyle.Monospace | TextStyle.Strikethrough | TextStyle.Underline);

                if (style == TextStyle.None)
                {
                    continue;
                }

                var from = Math.Max(0, entity.Offset);
                var to = Math.Min(text.Length, entity.Offset + entity.Length);

                if (to <= from)
                {
                    continue;
                }

                mask ??= new TextStyle[text.Length];

                for (int j = from; j < to; j++)
                {
                    mask[j] |= style;
                }
            }

            return mask;
        }

        private static Run CreateRun(string text, TextStyle style)
        {
            var run = new Run
            {
                Text = text
            };

            if ((style & TextStyle.Bold) != TextStyle.None)
            {
                run.FontWeight = FontWeights.SemiBold;
            }

            if ((style & TextStyle.Italic) != TextStyle.None)
            {
                run.FontStyle = FontStyle.Italic;
            }

            var decorations = TextDecorations.None;

            if ((style & TextStyle.Underline) != TextStyle.None)
            {
                decorations |= TextDecorations.Underline;
            }

            if ((style & TextStyle.Strikethrough) != TextStyle.None)
            {
                decorations |= TextDecorations.Strikethrough;
            }

            if (decorations != TextDecorations.None)
            {
                run.TextDecorations = decorations;
            }

            if ((style & TextStyle.Monospace) != TextStyle.None && _monospaceFontFamily != null)
            {
                run.FontFamily = _monospaceFontFamily;
            }

            return run;
        }

        /// <summary>
        /// A laid out string. Everything is in the text block's own coordinates, with the origin
        /// at the top left of the first line, which is what the DirectWrite hit-test metrics the
        /// callers expect are in too.
        /// </summary>
        internal readonly struct Measurement
        {
            private readonly object _parsed;
            private readonly object[] _one;

            public readonly int Length;
            public readonly Size Size;

            public Measurement(object parsed, int length, Size size)
            {
                _parsed = parsed;
                _one = new object[1];
                Length = length;
                Size = size;
            }

            public bool IsValid => _parsed != null;

            /// <summary>
            /// Whether the paragraph came out right to left. Uno decides this from the text
            /// itself (ICU's <c>ubidi_setPara</c>), not from the FlowDirection it was given, so
            /// an Arabic message is right to left even when it is measured in a left to right
            /// block.
            /// </summary>
            public bool IsRightToLeft
            {
                get
                {
                    try
                    {
                        return _parsed != null && _isRightToLeft != null && (bool)_isRightToLeft.GetValue(_parsed);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            /// <summary>
            /// The caret rectangle at <paramref name="index"/>: X is the leading edge of the
            /// cluster there (the trailing edge of the whole text when index == Length), Y the
            /// top of its line, Height the line height.
            /// </summary>
            public Rect RectForIndex(int index)
            {
                if (_parsed == null)
                {
                    return default;
                }

                _one[0] = Math.Max(0, Math.Min(index, Length));

                try
                {
                    return (Rect)_getRectForIndex.Invoke(_parsed, _one);
                }
                catch
                {
                    return default;
                }
            }

            public bool LineAt(int index, out int start, out int length, out int lineIndex, out bool lastLine)
            {
                start = 0;
                length = 0;
                lineIndex = 0;
                lastLine = true;

                if (_parsed == null || Length == 0)
                {
                    return false;
                }

                _one[0] = Math.Max(0, Math.Min(index, Length - 1));

                try
                {
                    var line = ((int start, int length, bool firstLine, bool lastLine, int lineIndex))_getLineAt.Invoke(_parsed, _one);

                    start = line.start;
                    length = line.length;
                    lineIndex = line.lineIndex;
                    lastLine = line.lastLine;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            /// <summary>
            /// Where the last line ends and how tall the text is up to and including it - the
            /// pair <c>MessageBubblePanel</c> squeezes the timestamp into.
            /// </summary>
            public Vector2 ContentEnd()
            {
                if (Length == 0)
                {
                    return Vector2.Zero;
                }

                var end = RectForIndex(Length);
                var x = end.X;

                // A right to left paragraph *ends* on the left: GetRectForIndex(Length) answers
                // with the left edge of the last line, and the caller would read that as "the
                // whole width is free" and drop the timestamp on top of the text - which for a
                // right to left line, kept flush to the right edge, is exactly where the text
                // is. There is no cheap right answer either, because Uno's per-index rectangles
                // are not dependable inside a bidi run (the last character of an Arabic line
                // reports a position past the layout width). So say the last line reaches as far
                // right as the text goes, which is the conservative answer: the footer gets a
                // line of its own. Messages in the other direction than the UI never reach here
                // anyway - FormattedTextBlock.HasLineEnding already sends them down that path.
                if (IsRightToLeft)
                {
                    x = Math.Max(x, Size.Width);
                }

                return new Vector2((float)x, (float)(end.Y + end.Height));
            }

            /// <summary>
            /// One rectangle per line the range [<paramref name="offset"/>,
            /// <paramref name="offset"/> + <paramref name="length"/>) covers.
            /// </summary>
            /// <remarks>
            /// DirectWrite's <c>HitTestTextRange</c> returns one rectangle per bidi run per line;
            /// this collapses each line to the span between its two endpoints, so a range that
            /// mixes directions inside one line comes back as a single rectangle covering both.
            /// The consumers - the hover clip of the forwarded-from header, the quote highlight,
            /// the loading skeleton - draw a rounded outline around the result, where that is a
            /// slightly wider outline rather than a wrong one.
            /// </remarks>
            public List<Rect> Range(int offset, int length)
            {
                var result = new List<Rect>();

                if (_parsed == null || Length == 0)
                {
                    return result;
                }

                var from = Math.Max(0, Math.Min(offset, Length));
                var to = Math.Max(from, Math.Min(offset + length, Length));

                var index = from;

                while (index < to)
                {
                    if (!LineAt(index, out int lineStart, out int lineLength, out _, out _))
                    {
                        break;
                    }

                    var lineEnd = lineStart + lineLength;
                    if (lineEnd <= index)
                    {
                        // Truncated layouts clamp GetLineAt to the last line they kept; without
                        // this the walk would never advance.
                        break;
                    }

                    var first = Math.Max(index, lineStart);
                    var last = Math.Min(to, lineEnd);

                    if (last > first)
                    {
                        // Every index of the slice, not just its two ends: within one line the
                        // visual order is not the logical one as soon as there is a bidi run in
                        // it, so the leftmost and the rightmost character are not necessarily the
                        // first and the last.
                        var left = double.MaxValue;
                        var right = double.MinValue;
                        var top = double.MaxValue;
                        var bottom = double.MinValue;

                        for (int i = first; i < last; i++)
                        {
                            var rect = RectForIndex(i);

                            left = Math.Min(left, rect.X);
                            right = Math.Max(right, rect.X + rect.Width);
                            top = Math.Min(top, rect.Y);
                            bottom = Math.Max(bottom, rect.Y + rect.Height);
                        }

                        if (right > left && bottom > top)
                        {
                            result.Add(new Rect(left, top, right - left, bottom - top));
                        }
                    }

                    index = lineEnd;
                }

                return result;
            }

            /// <summary>
            /// The end of the last line kept by a layout capped to N lines, with the trailing
            /// whitespace and the line break of that line taken off - the same correction
            /// <c>MaxLines</c> applies on Windows before handing the offset back.
            /// </summary>
            public int TruncatedOffset(string text)
            {
                if (Length == 0)
                {
                    return 0;
                }

                if (!LineAt(Length - 1, out int start, out int length, out _, out _))
                {
                    return Length;
                }

                var offset = Math.Min(Length, start + length);

                while (offset > start && offset <= text.Length && offset > 0 && char.IsWhiteSpace(text[offset - 1]))
                {
                    offset--;
                }

                return offset;
            }
        }
    }
}
