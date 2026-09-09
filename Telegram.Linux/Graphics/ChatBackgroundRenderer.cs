//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using SkiaSharp;

namespace Telegram.Native
{
    /// <summary>
    /// The Telegram chat wallpaper -- freeform gradient plus the tiled doodle pattern -- painted
    /// with Skia in a single pass.
    /// </summary>
    /// <remarks>
    /// <para>On Windows this is a graph of composition effects: the pattern lives in a
    /// <c>CompositionDrawingSurface</c> filled by Direct2D from the SVG, a <c>BorderEffect</c> with
    /// <c>CanvasEdgeBehavior.Wrap</c> tiles it, an <c>OpacityEffect</c> applies the intensity, and a
    /// <c>BlendEffect</c> (SoftLight) or an <c>AlphaMaskEffect</c> composes it over the gradient
    /// (see Controls/Media/ChatBackgroundBrush.cs). Uno has no <c>CompositionGraphicsDevice</c>, so
    /// the pattern surface cannot even be created, and the whole graph goes with it.</para>
    /// <para>The degrade is exact rather than approximate, because the algebra collapses. The
    /// pattern surface is drawn in <b>pure black</b>, so for the positive case SoftLight with a
    /// black backdrop is identically zero
    /// (<c>B(0, Cs) = 0</c> for both halves of the W3C formula), and the composite reduces to
    /// <c>Co = (1 - a)*Cs</c>: <b>black painted over the gradient at alpha = intensity</b>. For the
    /// negative case the mask is opaque everywhere and <c>1 - intensity</c> inside the pattern, and
    /// the presenter paints black behind, so the result is the same black-over-gradient with the
    /// gradient itself dimmed by intensity. Both are one <see cref="SKCanvas.DrawRect"/> with a
    /// repeating shader.</para>
    /// <para>Everything here is SkiaSharp + BCL on purpose: no Uno, no XAML, no TDLib types, so the
    /// whole path can be rendered to a PNG and looked at from a console spike
    /// (unigram-linux/spikes/ChatBackgroundSpike).</para>
    /// </remarks>
    public static class ChatBackgroundRenderer
    {
        /// <summary>
        /// The factor the Windows helper rasterizes the pattern at: <c>dpi = 0.25 * scale</c>
        /// (PlaceholderImageHelper.cpp, DrawSvg). The 1440x2960 viewBox of Background.tgv becomes a
        /// 360x740 tile in layout units, which is the size ChatBackgroundBrush tiles at.
        /// </summary>
        public const float PatternScale = 0.25f;

        #region Pattern (.tgv)

        /// <summary>
        /// A parsed <c>.tgv</c>: the doodles as one filled path in viewBox coordinates.
        /// </summary>
        public sealed class Pattern : IDisposable
        {
            internal Pattern(SKPath path, float width, float height)
            {
                Path = path;
                Width = width;
                Height = height;
            }

            public SKPath Path { get; private set; }

            /// <summary>ViewBox width, in SVG user units (1440 for Background.tgv).</summary>
            public float Width { get; }

            /// <summary>ViewBox height, in SVG user units (2960 for Background.tgv).</summary>
            public float Height { get; }

            public void Dispose()
            {
                Path?.Dispose();
                Path = null;
            }
        }

        /// <summary>
        /// Reads a <c>.tgv</c> (a gzipped SVG; Telegram serves the pattern of a
        /// <c>BackgroundTypePattern</c> with mime type <c>application/x-tgwallpattern</c>) and
        /// returns its shapes as one path. Null when the file is missing, still downloading or not
        /// a pattern -- the callers ask this of files TDLib may not have finished writing, so that
        /// is an answer, not a failure.
        /// </summary>
        public static Pattern LoadPattern(string fileName)
        {
            return LoadPattern(fileName, out _);
        }

        /// <summary>
        /// The same, saying why when it answers null. "Could not read the pattern" on its own is
        /// not a diagnosis: missing file, half-written file, a .tgv that is not a pattern and a
        /// Skia that refused the path data all look identical from the caller, and the caller only
        /// has a log line to show for it.
        /// </summary>
        public static Pattern LoadPattern(string fileName, out string failure)
        {
            failure = null;

            try
            {
                if (string.IsNullOrEmpty(fileName))
                {
                    failure = "no path";
                    return null;
                }

                if (!File.Exists(fileName))
                {
                    failure = "the file is not there";
                    return null;
                }

                var svg = Decompress(fileName);
                if (svg == null)
                {
                    failure = $"empty after decompressing {new FileInfo(fileName).Length} bytes";
                    return null;
                }

                var pattern = ParsePattern(svg, out failure);
                if (pattern != null)
                {
                    failure = null;
                }

                return pattern;
            }
            catch (Exception ex)
            {
                failure = ex.ToString();
                return null;
            }
        }

        /// <summary>
        /// The same, for bytes already in memory. Exposed so the spike can feed a fixture without
        /// touching the disk.
        /// </summary>
        public static Pattern ParsePattern(string svg)
        {
            return ParsePattern(svg, out _);
        }

        /// <inheritdoc cref="ParsePattern(string)"/>
        public static Pattern ParsePattern(string svg, out string failure)
        {
            failure = null;

            if (string.IsNullOrEmpty(svg))
            {
                failure = "the svg is empty";
                return null;
            }

            if (!TryReadViewBox(svg, out float width, out float height))
            {
                failure = $"no usable viewBox in {svg.Length} characters of svg";
                return null;
            }

            // The pattern is a flat list of <path d="..."/> with no fill, no transform and no
            // groups -- checked over the whole of Assets/Background.tgv: 745 paths, the only
            // attribute is "d", and the only commands are M/L/H/V/C/S and their relative forms.
            // Anything richer would need a real SVG parser; a .tgv that grows one will fail the
            // "did I get any geometry" test below rather than draw something wrong.
            var result = new SKPath
            {
                FillType = SKPathFillType.Winding
            };

            var count = 0;
            var seen = 0;
            var first = (string)null;

            foreach (var data in EnumeratePathData(svg))
            {
                seen++;
                first ??= data;

                using var path = ParseSvgPath(data);
                if (path == null)
                {
                    continue;
                }

                result.AddPath(path);
                count++;
            }

            if (count == 0)
            {
                result.Dispose();
                failure = $"{seen} <path> elements in the svg, none of which could be parsed"
                    + $" (first d: \"{Excerpt(first)}\")";
                return null;
            }

            if (count < seen)
            {
                // Not fatal - the wallpaper is still drawn with what did parse - but it means the
                // pattern is missing shapes, which is worth a line rather than a silent difference.
                failure = $"{seen - count} of {seen} <path> elements could not be parsed";
            }

            return new Pattern(result, width, height);
        }

        private static string Decompress(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || !File.Exists(fileName))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(fileName);
            if (bytes.Length == 0)
            {
                return null;
            }

            // Same test as the Windows helper: gzip magic, otherwise the bytes are the SVG.
            if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(bytes.Length * 4);

                gzip.CopyTo(output);
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static bool TryReadViewBox(string svg, out float width, out float height)
        {
            width = 0;
            height = 0;

            var index = svg.IndexOf("viewBox", StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var open = svg.IndexOf('"', index);
            if (open < 0)
            {
                return false;
            }

            var close = svg.IndexOf('"', open + 1);
            if (close < 0)
            {
                return false;
            }

            var parts = svg.Substring(open + 1, close - open - 1)
                .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 4)
            {
                return false;
            }

            if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out width) ||
                !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out height))
            {
                return false;
            }

            return width > 0 && height > 0;
        }


        /// <summary>
        /// <see cref="SKPath.ParseSvgPathData"/> answers <b>null for every path</b> inside Unigram,
        /// while returning the geometry of the very same bytes from a console spike that links this
        /// same file against the very same SkiaSharp 3.119.2 and the very same
        /// <c>runtimes/linux-x64/native/libSkiaSharp.so</c> (md5 checked, and the app's
        /// <c>/proc/self/maps</c> confirms it loads that one and no other). Measured on
        /// <c>Assets/Background.tgv</c>: 745 &lt;path&gt; elements found, 745 nulls back, so the
        /// wallpaper came up as the bare gradient with no doodles on it.
        ///
        /// <para>So the port stops asking. This reads the same subset the file uses -- the commands
        /// the comment in <see cref="ParsePattern(string, out string)"/> lists, plus the quadratics
        /// for safety -- and hands Skia points instead of a string. It is the only thing in the
        /// chain that needed Skia to parse anything: everything after this is
        /// <see cref="SKPath.MoveTo"/> / <see cref="SKPath.LineTo"/> / <see cref="SKPath.CubicTo"/>,
        /// which do work.</para>
        ///
        /// <para>Elliptical arcs (<c>A</c>/<c>a</c>) are not implemented: no Telegram pattern uses
        /// them (they are Illustrator exports of filled outlines, all M/L/H/V/C/S), and drawing an
        /// arc as a line would be a wrong shape rather than a missing one. A path that has one is
        /// dropped, and the count in the failure message says how many were dropped.</para>
        /// </summary>
        internal static SKPath ParseSvgPath(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return null;
            }

            var path = new SKPath();

            float currentX = 0, currentY = 0;
            float startX = 0, startY = 0;
            float cubicX = 0, cubicY = 0;
            float quadX = 0, quadY = 0;
            bool afterCubic = false, afterQuad = false;
            bool any = false;

            var command = '\0';
            var index = 0;

            while (true)
            {
                SkipSeparators(data, ref index);

                if (index >= data.Length)
                {
                    break;
                }

                var c = data[index];

                if (char.IsLetter(c))
                {
                    command = c;
                    index++;
                }
                else if (command == '\0')
                {
                    // Numbers before any command: not a path.
                    path.Dispose();
                    return null;
                }

                switch (command)
                {
                    case 'M':
                    case 'm':
                        {
                            if (!TryNumber(data, ref index, out float x) || !TryNumber(data, ref index, out float y))
                            {
                                path.Dispose();
                                return null;
                            }

                            if (command == 'm')
                            {
                                x += currentX;
                                y += currentY;
                            }

                            path.MoveTo(x, y);
                            currentX = startX = x;
                            currentY = startY = y;
                            afterCubic = afterQuad = false;
                            any = true;

                            // A second coordinate pair after M is an implicit L (SVG 1.1, 8.3.2).
                            command = command == 'M' ? 'L' : 'l';
                            break;
                        }
                    case 'L':
                    case 'l':
                        {
                            if (!TryNumber(data, ref index, out float x) || !TryNumber(data, ref index, out float y))
                            {
                                path.Dispose();
                                return null;
                            }

                            if (command == 'l')
                            {
                                x += currentX;
                                y += currentY;
                            }

                            path.LineTo(x, y);
                            currentX = x;
                            currentY = y;
                            afterCubic = afterQuad = false;
                            break;
                        }
                    case 'H':
                    case 'h':
                        {
                            if (!TryNumber(data, ref index, out float x))
                            {
                                path.Dispose();
                                return null;
                            }

                            if (command == 'h')
                            {
                                x += currentX;
                            }

                            path.LineTo(x, currentY);
                            currentX = x;
                            afterCubic = afterQuad = false;
                            break;
                        }
                    case 'V':
                    case 'v':
                        {
                            if (!TryNumber(data, ref index, out float y))
                            {
                                path.Dispose();
                                return null;
                            }

                            if (command == 'v')
                            {
                                y += currentY;
                            }

                            path.LineTo(currentX, y);
                            currentY = y;
                            afterCubic = afterQuad = false;
                            break;
                        }
                    case 'C':
                    case 'c':
                        {
                            if (!TryNumber(data, ref index, out float x1) || !TryNumber(data, ref index, out float y1)
                                || !TryNumber(data, ref index, out float x2) || !TryNumber(data, ref index, out float y2)
                                || !TryNumber(data, ref index, out float x) || !TryNumber(data, ref index, out float y))
                            {
                                path.Dispose();
                                return null;
                            }

                            if (command == 'c')
                            {
                                x1 += currentX; y1 += currentY;
                                x2 += currentX; y2 += currentY;
                                x += currentX; y += currentY;
                            }

                            path.CubicTo(x1, y1, x2, y2, x, y);
                            cubicX = x2; cubicY = y2;
                            currentX = x; currentY = y;
                            afterCubic = true;
                            afterQuad = false;
                            break;
                        }
                    case 'S':
                    case 's':
                        {
                            if (!TryNumber(data, ref index, out float x2) || !TryNumber(data, ref index, out float y2)
                                || !TryNumber(data, ref index, out float x) || !TryNumber(data, ref index, out float y))
                            {
                                path.Dispose();
                                return null;
                            }

                            if (command == 's')
                            {
                                x2 += currentX; y2 += currentY;
                                x += currentX; y += currentY;
                            }

                            // The first control point is the reflection of the previous second one,
                            // or the current point when the previous command was not a cubic.
                            var x1 = afterCubic ? 2 * currentX - cubicX : currentX;
                            var y1 = afterCubic ? 2 * currentY - cubicY : currentY;

                            path.CubicTo(x1, y1, x2, y2, x, y);
                            cubicX = x2; cubicY = y2;
                            currentX = x; currentY = y;
                            afterCubic = true;
                            afterQuad = false;
                            break;
                        }
                    case 'Q':
                    case 'q':
                        {
                            if (!TryNumber(data, ref index, out float x1) || !TryNumber(data, ref index, out float y1)
                                || !TryNumber(data, ref index, out float x) || !TryNumber(data, ref index, out float y))
                            {
                                path.Dispose();
                                return null;
                            }

                            if (command == 'q')
                            {
                                x1 += currentX; y1 += currentY;
                                x += currentX; y += currentY;
                            }

                            path.QuadTo(x1, y1, x, y);
                            quadX = x1; quadY = y1;
                            currentX = x; currentY = y;
                            afterQuad = true;
                            afterCubic = false;
                            break;
                        }
                    case 'T':
                    case 't':
                        {
                            if (!TryNumber(data, ref index, out float x) || !TryNumber(data, ref index, out float y))
                            {
                                path.Dispose();
                                return null;
                            }

                            if (command == 't')
                            {
                                x += currentX; y += currentY;
                            }

                            var x1 = afterQuad ? 2 * currentX - quadX : currentX;
                            var y1 = afterQuad ? 2 * currentY - quadY : currentY;

                            path.QuadTo(x1, y1, x, y);
                            quadX = x1; quadY = y1;
                            currentX = x; currentY = y;
                            afterQuad = true;
                            afterCubic = false;
                            break;
                        }
                    case 'Z':
                    case 'z':
                        {
                            path.Close();
                            currentX = startX;
                            currentY = startY;
                            afterCubic = afterQuad = false;
                            break;
                        }
                    default:
                        // 'A'/'a' and anything else: see the remarks, the path is dropped whole.
                        path.Dispose();
                        return null;
                }
            }

            if (!any)
            {
                path.Dispose();
                return null;
            }

            return path;
        }

        private static string Excerpt(string data)
        {
            if (data == null)
            {
                return "<none>";
            }

            return data.Length <= 60 ? data : data.Substring(0, 60) + "...";
        }

        private static void SkipSeparators(string data, ref int index)
        {
            while (index < data.Length && (data[index] == ' ' || data[index] == ',' || data[index] == '\t'
                || data[index] == '\r' || data[index] == '\n'))
            {
                index++;
            }
        }

        /// <summary>
        /// One SVG number. A '-' is a separator as well as a sign ("10-20" is two numbers), which
        /// is why this cannot be a Split.
        /// </summary>
        private static bool TryNumber(string data, ref int index, out float value)
        {
            value = 0;

            SkipSeparators(data, ref index);

            var start = index;

            if (index < data.Length && (data[index] == '-' || data[index] == '+'))
            {
                index++;
            }

            var digits = 0;

            while (index < data.Length && data[index] >= '0' && data[index] <= '9')
            {
                index++;
                digits++;
            }

            if (index < data.Length && data[index] == '.')
            {
                index++;

                while (index < data.Length && data[index] >= '0' && data[index] <= '9')
                {
                    index++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                index = start;
                return false;
            }

            if (index < data.Length && (data[index] == 'e' || data[index] == 'E'))
            {
                var exponent = index + 1;

                if (exponent < data.Length && (data[exponent] == '-' || data[exponent] == '+'))
                {
                    exponent++;
                }

                if (exponent < data.Length && data[exponent] >= '0' && data[exponent] <= '9')
                {
                    index = exponent;

                    while (index < data.Length && data[index] >= '0' && data[index] <= '9')
                    {
                        index++;
                    }
                }
            }

            return float.TryParse(data.AsSpan(start, index - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        private static IEnumerable<string> EnumeratePathData(string svg)
        {
            var index = 0;

            while (true)
            {
                index = svg.IndexOf("<path", index, StringComparison.Ordinal);
                if (index < 0)
                {
                    yield break;
                }

                var end = svg.IndexOf('>', index);
                if (end < 0)
                {
                    yield break;
                }

                var tag = svg.Substring(index, end - index);
                index = end;

                var data = tag.IndexOf(" d=\"", StringComparison.Ordinal);
                if (data < 0)
                {
                    continue;
                }

                var open = data + 4;
                var close = tag.IndexOf('"', open);
                if (close < 0)
                {
                    continue;
                }

                yield return tag.Substring(open, close - open);
            }
        }

        #endregion

        #region Freeform gradient

        // The four-anchor gradient of Telegram: eight anchors on a ring, four of them live at any
        // one time, and "next" walks the ring. Same numbers as ChatBackgroundFreeform in
        // Telegram/Controls/Chats/ChatBackgroundControl.cs, which stays Windows-only because it
        // writes its pixels through WriteableBitmap.Buffer (an IBufferByteAccess QI that Uno does
        // not answer, see Telegram/Common/Extensions.cs).
        private static readonly Vector2[] _positions =
        {
            new(0.80f, 0.10f),
            new(0.60f, 0.20f),
            new(0.35f, 0.25f),
            new(0.25f, 0.60f),
            new(0.20f, 0.90f),
            new(0.40f, 0.80f),
            new(0.65f, 0.75f),
            new(0.75f, 0.40f),
        };

        private static readonly uint[] _defaultColors =
        {
            0xDBDDBB, 0x6BA587, 0xD5D88D, 0x88B884
        };

        /// <summary>
        /// The gradient at its native working size. Upstream renders it into a bitmap whose
        /// shorter side is 50 pixels and lets the brush stretch it (UniformToFill), which is what
        /// gives the wallpaper its soft look; the same ratio is used here so the result matches.
        /// </summary>
        public static SKBitmap CreateFreeformGradient(int width, int height, IReadOnlyList<int> colors, int phase)
        {
            var ratio = Math.Max(50.0 / Math.Max(width, 1), 50.0 / Math.Max(height, 1));

            var w = Math.Max(1, (int)(width * ratio));
            var h = Math.Max(1, (int)(height * ratio));

            var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            GenerateGradient(bitmap, ToRgb(colors), Gather(phase));

            return bitmap;
        }

        private static bool _defaultColorsReported;

        private static uint[] ToRgb(IReadOnlyList<int> colors)
        {
            if (colors == null || colors.Count == 0)
            {
                // This palette is BYTE-FOR-BYTE the predefined wallpaper's own light-mode colors
                // (SettingsBackgroundsViewModel builds it with 0xDBDDBB, 0x6BA587, 0xD5D88D,
                // 0x88B884). So "everything shows the same green-yellow gradient" looks EXACTLY
                // the same whether this fallback was taken or the predefined background was
                // rendered perfectly - the pixels cannot tell the two apart, and neither can
                // anyone reasoning from them. Say it out loud instead.
                //
                // Once per process: this sits on the paint path, and a line per frame would bury
                // the answer it exists to give.
                if (!_defaultColorsReported)
                {
                    _defaultColorsReported = true;
                    Telegram.Logger.Warning("ChatBackgroundRenderer: no colors supplied, painting the DEFAULT palette (this is the fallback, not a wallpaper)");
                }

                return _defaultColors;
            }

            var result = new uint[colors.Count];
            for (int i = 0; i < colors.Count; i++)
            {
                result[i] = (uint)colors[i] & 0xFFFFFF;
            }

            return result;
        }

        private static Vector2[] Gather(int phase)
        {
            var offset = ((phase % 8) + 8) % 8;

            var result = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                result[i] = _positions[(i * 2 + offset) % 8];
            }

            return result;
        }

        private static unsafe void GenerateGradient(SKBitmap bitmap, uint[] colors, Vector2[] positions)
        {
            var width = bitmap.Width;
            var height = bitmap.Height;
            var pixels = (byte*)bitmap.GetPixels().ToPointer();

            for (int y = 0; y < height; y++)
            {
                var directPixelY = y / (float)height;
                var centerDistanceY = directPixelY - 0.5f;
                var centerDistanceY2 = centerDistanceY * centerDistanceY;

                var lineBytes = pixels + width * 4 * y;

                for (int x = 0; x < width; x++)
                {
                    var directPixelX = x / (float)width;

                    var centerDistanceX = directPixelX - 0.5f;
                    var centerDistance = MathF.Sqrt(centerDistanceX * centerDistanceX + centerDistanceY2);

                    var swirlFactor = 0.35f * centerDistance;
                    var theta = swirlFactor * swirlFactor * 0.8f * 8.0f;
                    var sinTheta = MathF.Sin(theta);
                    var cosTheta = MathF.Cos(theta);

                    var pixelX = MathF.Max(0.0f, MathF.Min(1.0f, 0.5f + centerDistanceX * cosTheta - centerDistanceY * sinTheta));
                    var pixelY = MathF.Max(0.0f, MathF.Min(1.0f, 0.5f + centerDistanceX * sinTheta + centerDistanceY * cosTheta));

                    var distanceSum = 0.0f;

                    var r = 0.0f;
                    var g = 0.0f;
                    var b = 0.0f;

                    for (int i = 0; i < colors.Length; i++)
                    {
                        var position = positions[i % positions.Length];

                        var distanceX = pixelX - position.X;
                        var distanceY = pixelY - position.Y;

                        var distance = MathF.Max(0.0f, 0.9f - MathF.Sqrt(distanceX * distanceX + distanceY * distanceY));
                        distance = distance * distance * distance * distance;
                        distanceSum += distance;

                        r += distance * ((colors[i] >> 16) & 0xFF) / 255f;
                        g += distance * ((colors[i] >> 8) & 0xFF) / 255f;
                        b += distance * (colors[i] & 0xFF) / 255f;
                    }

                    var pixelBytes = lineBytes + x * 4;

                    if (distanceSum <= 0)
                    {
                        pixelBytes[0] = 0;
                        pixelBytes[1] = 0;
                        pixelBytes[2] = 0;
                        pixelBytes[3] = 0xFF;
                        continue;
                    }

                    pixelBytes[0] = (byte)(b / distanceSum * 255.0f);
                    pixelBytes[1] = (byte)(g / distanceSum * 255.0f);
                    pixelBytes[2] = (byte)(r / distanceSum * 255.0f);
                    pixelBytes[3] = 0xFF;
                }
            }

            bitmap.NotifyPixelsChanged();
        }

        #endregion

        #region Compose

        /// <summary>
        /// What to paint: the fill underneath and, optionally, the pattern on top. Mirrors the
        /// fields ChatBackgroundPresenter reads out of a <c>Background</c> so the XAML side stays a
        /// translation and the drawing has no TDLib types in it.
        /// </summary>
        public sealed class Options
        {
            /// <summary>Freeform palette (0xRRGGBB), the four colours of a
            /// <c>BackgroundFillFreeformGradient</c>. Wins over the other two fills.</summary>
            public IReadOnlyList<int> FreeformColors { get; set; }

            /// <summary>Two-stop fill of a <c>BackgroundFillGradient</c>, and its angle in
            /// degrees, clockwise from the top.</summary>
            public int? GradientTopColor { get; set; }

            public int? GradientBottomColor { get; set; }

            public int GradientRotationAngle { get; set; }

            /// <summary><c>BackgroundFillSolid</c>.</summary>
            public int? SolidColor { get; set; }

            /// <summary>Which of the eight freeform phases to draw.</summary>
            public int Phase { get; set; }

            public Pattern Pattern { get; set; }

            /// <summary><c>BackgroundTypePattern.Intensity / 100</c>.</summary>
            public float Intensity { get; set; } = 1;

            /// <summary><c>BackgroundTypePattern.IsInverted</c>: the pattern is cut out of the
            /// gradient over black instead of darkening it.</summary>
            public bool IsNegative { get; set; }
        }

        /// <summary>
        /// Paints one full wallpaper into <paramref name="canvas"/>, covering
        /// <paramref name="width"/> x <paramref name="height"/> <b>in the canvas' own units</b>.
        /// <paramref name="scale"/> is the number of device pixels per unit, and only decides how
        /// finely the pattern is rasterized.
        /// </summary>
        public static void Draw(SKCanvas canvas, float width, float height, float scale, Options options)
        {
            if (canvas == null || options == null || width <= 0 || height <= 0)
            {
                return;
            }

            if (scale <= 0)
            {
                scale = 1;
            }

            var intensity = Math.Clamp(options.Intensity, 0, 1);
            var bounds = new SKRect(0, 0, width, height);

            // Negative patterns are holes: the black has to come from somewhere, and upstream puts
            // it on the presenter itself (ChatBackgroundPresenter.UpdateSource sets a black
            // Background when _negative). Painting it here keeps the two halves together.
            if (options.IsNegative)
            {
                canvas.DrawRect(bounds, new SKPaint { Color = SKColors.Black, IsAntialias = false });
            }

            DrawFill(canvas, bounds, options, options.IsNegative ? intensity : 1f);

            if (options.Pattern?.Path != null && intensity > 0)
            {
                DrawPattern(canvas, bounds, scale, options.Pattern, intensity);
            }
        }

        private static void DrawFill(SKCanvas canvas, SKRect bounds, Options options, float opacity)
        {
            var alpha = (byte)Math.Clamp((int)MathF.Round(opacity * 255), 0, 255);

            if (options.FreeformColors is { Count: > 0 } || (options.SolidColor == null && options.GradientTopColor == null))
            {
                using var gradient = CreateFreeformGradient((int)MathF.Ceiling(bounds.Width), (int)MathF.Ceiling(bounds.Height), options.FreeformColors, options.Phase);
                using var paint = new SKPaint
                {
                    IsAntialias = false,
                    Color = new SKColor(0xFF, 0xFF, 0xFF, alpha)
                };

                // UniformToFill/Center, the stretch ChatBackgroundPreview uses for the same bitmap.
                var sourceRatio = gradient.Width / (float)gradient.Height;
                var targetRatio = bounds.Width / bounds.Height;

                SKRect destination;
                if (sourceRatio > targetRatio)
                {
                    var scaled = bounds.Height * sourceRatio;
                    destination = new SKRect((bounds.Width - scaled) / 2, 0, (bounds.Width + scaled) / 2, bounds.Height);
                }
                else
                {
                    var scaled = bounds.Width / sourceRatio;
                    destination = new SKRect(0, (bounds.Height - scaled) / 2, bounds.Width, (bounds.Height + scaled) / 2);
                }

                using var image = SKImage.FromBitmap(gradient);
                canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
                return;
            }

            if (options.GradientTopColor is int top && options.GradientBottomColor is int bottom)
            {
                // The same eight buckets TdBackground.GetGradient maps the angle to, as relative
                // points of the element's box; anything else falls into the 45 degree case, exactly
                // like the switch upstream.
                var (from, to) = options.GradientRotationAngle switch
                {
                    0 or 360 => (new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
                    90 => (new SKPoint(1, 0.5f), new SKPoint(0, 0.5f)),
                    135 => (new SKPoint(1, 1), new SKPoint(0, 0)),
                    180 => (new SKPoint(0.5f, 1), new SKPoint(0.5f, 0)),
                    225 => (new SKPoint(0, 1), new SKPoint(1, 0)),
                    270 => (new SKPoint(0, 0.5f), new SKPoint(1, 0.5f)),
                    315 => (new SKPoint(0, 0), new SKPoint(1, 1)),
                    _ => (new SKPoint(1, 0), new SKPoint(0, 1)),
                };

                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(bounds.Left + from.X * bounds.Width, bounds.Top + from.Y * bounds.Height),
                    new SKPoint(bounds.Left + to.X * bounds.Width, bounds.Top + to.Y * bounds.Height),
                    new[] { ToColor(top, alpha), ToColor(bottom, alpha) },
                    null, SKShaderTileMode.Clamp);

                using var paint = new SKPaint { Shader = shader, IsAntialias = false };
                canvas.DrawRect(bounds, paint);
                return;
            }

            if (options.SolidColor is int solid)
            {
                using var paint = new SKPaint { Color = ToColor(solid, alpha), IsAntialias = false };
                canvas.DrawRect(bounds, paint);
            }
        }

        // Rasterizing the 745 doodles of Background.tgv costs ~170 ms, so the tile is kept: a
        // repaint has to be able to run inside a frame (SKCanvasElement.RenderOverride is on the
        // render path, not on a "once per resize" path). One entry is enough -- there is one
        // wallpaper on screen -- and it is keyed by the geometry AND the device size, so a DPI
        // change or a different pattern re-rasterizes instead of stretching.
        private static readonly object _tileLock = new();
        private static Pattern _tilePattern;
        private static int _tileWidth;
        private static int _tileHeight;
        private static SKImage _tileImage;

        private static SKImage GetPatternTile(Pattern pattern, int width, int height)
        {
            lock (_tileLock)
            {
                if (_tileImage != null && _tilePattern == pattern && _tileWidth == width && _tileHeight == height)
                {
                    return _tileImage;
                }

                using var tile = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var tileCanvas = new SKCanvas(tile))
                {
                    tileCanvas.Clear(SKColors.Transparent);
                    tileCanvas.Scale(width / pattern.Width, height / pattern.Height);

                    // Widen(1) + Fill, the pair of geometries DrawSvg fills: the doodles are
                    // hairline outlines and a plain fill loses them at small scales.
                    using var paint = new SKPaint
                    {
                        Color = SKColors.Black,
                        IsAntialias = true,
                        Style = SKPaintStyle.StrokeAndFill,
                        StrokeWidth = 1
                    };

                    tileCanvas.DrawPath(pattern.Path, paint);
                }

                // The previous image is dropped, not disposed: a paint on another thread may still
                // be blitting it, and disposing an SKImage out from under Skia is a use-after-free
                // rather than an exception. The finalizer reclaims it.
                _tileImage = SKImage.FromBitmap(tile);
                _tilePattern = pattern;
                _tileWidth = width;
                _tileHeight = height;

                return _tileImage;
            }
        }

        private static void DrawPattern(SKCanvas canvas, SKRect bounds, float scale, Pattern pattern, float intensity)
        {
            // The tile is rasterized at device resolution and repeated by the shader, which is what
            // BorderEffect(Wrap) did. At 1x the default pattern is a 360x740 tile in layout units,
            // the size ChatBackgroundBrush gives it through surfaceBrush.Scale.
            var tileWidth = Math.Max(1, (int)MathF.Round(pattern.Width * PatternScale * scale));
            var tileHeight = Math.Max(1, (int)MathF.Round(pattern.Height * PatternScale * scale));

            var tile = GetPatternTile(pattern, tileWidth, tileHeight);
            if (tile == null)
            {
                return;
            }

            // The shader repeats in device pixels, so it has to be scaled back into the canvas'
            // units: the tile is tileWidth device pixels wide and must cover
            // pattern.Width * PatternScale units.
            var matrix = SKMatrix.CreateScale(1f / scale, 1f / scale);

            using var shader = tile.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), matrix);

            using var fill = new SKPaint
            {
                Shader = shader,
                IsAntialias = false,
                Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)Math.Clamp((int)MathF.Round(intensity * 255), 0, 255))
            };

            canvas.DrawRect(bounds, fill);
        }

        private static SKColor ToColor(int color, byte alpha)
        {
            return new SKColor((byte)((color >> 16) & 0xFF), (byte)((color >> 8) & 0xFF), (byte)(color & 0xFF), alpha);
        }

        #endregion

        #region Cached surface

        /// <summary>
        /// One wallpaper, kept as a device-resolution image and blitted on every paint.
        /// </summary>
        /// <remarks>
        /// <see cref="Draw"/> costs ~70 ms for a full-screen chat pane on this hardware, which is
        /// fine once and hopeless per frame -- and per frame is what it would be, because Uno's
        /// Skia renderer repaints the visual tree and calls back into the element every time
        /// something above it moves (a scrolling history, for one). Blitting an opaque image of the
        /// same size is a copy. Invalidate by bumping <see cref="Revision"/>: the surface only
        /// re-renders when the revision, the size or the scale change.
        /// </remarks>
        public sealed class Surface : IDisposable
        {
            private SKImage _image;
            private int _revision = -1;
            private int _width;
            private int _height;
            private float _scale;

            /// <summary>Bump this whenever the options change.</summary>
            public int Revision { get; set; }

            public void Invalidate()
            {
                Revision++;
            }

            public void Draw(SKCanvas canvas, float width, float height, float scale, Options options)
            {
                if (canvas == null || options == null || width <= 0 || height <= 0)
                {
                    return;
                }

                if (scale <= 0)
                {
                    scale = 1;
                }

                var pixelWidth = Math.Max(1, (int)MathF.Ceiling(width * scale));
                var pixelHeight = Math.Max(1, (int)MathF.Ceiling(height * scale));

                if (_image == null || _revision != Revision || _width != pixelWidth || _height != pixelHeight || _scale != scale)
                {
                    // Dropped, not disposed: see GetPatternTile.
                    _image = null;

                    using var bitmap = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                    using (var offscreen = new SKCanvas(bitmap))
                    {
                        offscreen.Clear(SKColors.Transparent);
                        offscreen.Scale(scale);

                        ChatBackgroundRenderer.Draw(offscreen, width, height, scale, options);
                    }

                    _image = SKImage.FromBitmap(bitmap);
                    _revision = Revision;
                    _width = pixelWidth;
                    _height = pixelHeight;
                    _scale = scale;
                }

                // Device resolution into a box of that many layout units: one to one after the
                // canvas' own scale, so nothing is resampled.
                canvas.DrawImage(_image, new SKRect(0, 0, width, height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }

            public void Dispose()
            {
                _image = null;
            }
        }

        #endregion
    }
}
