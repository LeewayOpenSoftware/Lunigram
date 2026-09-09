//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Telegram.Native
{
    /// <summary>
    /// The path building behind the clip geometries of <see cref="PlaceholderImageHelper"/>, one
    /// for one with the Direct2D geometry sinks of Telegram.Native/PlaceholderImageHelper.cpp.
    ///
    /// <para>It lives here, and not in the helper, so the shapes can be drawn and looked at from a
    /// console spike (unigram-linux/spikes/AvatarSpike) -- the helper drags in Uno, and a clip that
    /// comes out wrong hides message content instead of failing loudly, so "it compiled" is not
    /// evidence of anything.</para>
    /// </summary>
    /// <remarks>
    /// Direct2D fills with the alternate (even-odd) rule unless told otherwise, and these shapes
    /// were designed against that, so every path starts out <see cref="SKPathFillType.EvenOdd"/>.
    /// Skia's ArcTo takes the same endpoint parameterization as D2D1_ARC_SEGMENT (end point,
    /// radii, sweep direction, large/small), which is what makes these a transcription rather than
    /// a reimplementation.
    /// </remarks>
    internal static class ClipGeometry
    {
        public static SKPath CreatePath()
        {
            return new SKPath { FillType = SKPathFillType.EvenOdd };
        }

        private static void ArcTo(SKPath path, float endX, float endY, float radius, SKPathDirection sweep)
        {
            // Every arc here is circular, so the x-axis rotation is a no-op and stays at 0. (The
            // C++ passes MathFEx.ToRadians(-90) in one place, into a parameter Direct2D reads as
            // degrees -- for a circle it changed nothing either way.)
            path.ArcTo(new SKPoint(radius, radius), 0, SKPathArcSize.Small, sweep, new SKPoint(endX, endY));
        }

        /// <summary>
        /// One rounded rectangle, corner radii given separately -- the <c>AppendButton</c> of the
        /// C++, used for every button of an inline keyboard.
        /// </summary>
        public static void AppendButton(SKPath path, float x, float y, float width, float height, float topLeftRadius, float topRightRadius, float bottomRightRadius, float bottomLeftRadius)
        {
            path.MoveTo(x + topLeftRadius, y);

            // Top edge
            path.LineTo(x + width - topRightRadius, y);

            // Top-right corner
            if (topRightRadius > 0)
            {
                ArcTo(path, x + width, y + topRightRadius, topRightRadius, SKPathDirection.Clockwise);
            }

            // Right edge
            path.LineTo(x + width, y + height - bottomRightRadius);

            // Bottom-right corner
            if (bottomRightRadius > 0)
            {
                ArcTo(path, x + width - bottomRightRadius, y + height, bottomRightRadius, SKPathDirection.Clockwise);
            }

            // Bottom edge
            path.LineTo(x + bottomLeftRadius, y + height);

            // Bottom-left corner
            if (bottomLeftRadius > 0)
            {
                ArcTo(path, x, y + height - bottomLeftRadius, bottomLeftRadius, SKPathDirection.Clockwise);
            }

            // Left edge
            path.LineTo(x, y + topLeftRadius);

            // Top-left corner
            if (topLeftRadius > 0)
            {
                ArcTo(path, x + topLeftRadius, y, topLeftRadius, SKPathDirection.Clockwise);
            }

            path.Close();
        }

        /// <summary>
        /// Wraps a run of line rectangles, top to bottom, in one closed outline whose corners round
        /// into each other: the shape behind a quoted block, a highlighted range and the loading
        /// skeleton. The right edge is walked down and the left edge back up, and at each step the
        /// radius is capped at how far the neighbouring line actually juts out, so a 2px difference
        /// gets a 2px corner instead of a kink.
        /// </summary>
        public static void AppendRoundedPolygon(SKPath path, SKRect[] rectangles)
        {
            if (rectangles == null || rectangles.Length == 0)
            {
                return;
            }

            for (int i = 0; i < rectangles.Length; i++)
            {
                var rect = rectangles[i];
                var right = rect.Right;
                var bottom = rect.Bottom;

                if (i == 0)
                {
                    path.MoveTo(right - 4, rect.Top);
                    ArcTo(path, right, rect.Top + 4, 4, SKPathDirection.Clockwise);
                }
                else
                {
                    var y1diff = right - rectangles[i - 1].Right;
                    var y1radius = MathF.Min(4, MathF.Abs(y1diff));

                    if (y1diff < 0)
                    {
                        path.LineTo(right + y1radius, rect.Top);
                        ArcTo(path, right, rect.Top + y1radius, y1radius, SKPathDirection.CounterClockwise);
                    }
                    else if (y1diff > 0)
                    {
                        path.LineTo(right - y1radius, rect.Top);
                        ArcTo(path, right, rect.Top + y1radius, y1radius, SKPathDirection.Clockwise);
                    }
                }

                var y2diff = i < rectangles.Length - 1 ? right - rectangles[i + 1].Right : 4;
                var y2radius = MathF.Min(4, MathF.Abs(y2diff));

                path.LineTo(right, bottom - y2radius);

                if (y2diff < 0)
                {
                    ArcTo(path, right + y2radius, rectangles[i + 1].Top, y2radius, SKPathDirection.CounterClockwise);
                }
                else if (y2diff > 0)
                {
                    ArcTo(path, right - y2radius, bottom, y2radius, SKPathDirection.Clockwise);
                }
            }

            for (int i = rectangles.Length - 1; i >= 0; i--)
            {
                var rect = rectangles[i];
                var bottom = rect.Bottom;

                var y1diff = i < rectangles.Length - 1 ? rect.Left - rectangles[i + 1].Left : -4;
                var y1radius = MathF.Min(4, MathF.Abs(y1diff));

                if (y1diff > 0)
                {
                    path.LineTo(rect.Left - y1radius, bottom);
                    ArcTo(path, rect.Left, bottom - y1radius, y1radius, SKPathDirection.CounterClockwise);
                }
                else if (y1diff < 0)
                {
                    path.LineTo(rect.Left + y1radius, bottom);
                    ArcTo(path, rect.Left, bottom - y1radius, y1radius, SKPathDirection.Clockwise);
                }

                var y2diff = i > 0 ? rect.Left - rectangles[i - 1].Left : -4;
                var y2radius = MathF.Min(4, MathF.Abs(y2diff));

                path.LineTo(rect.Left, rect.Top + y2radius);

                if (y2diff > 0)
                {
                    ArcTo(path, rect.Left - y2radius, rect.Top, y2radius, SKPathDirection.CounterClockwise);
                }
                else if (y2diff < 0)
                {
                    ArcTo(path, rect.Left + y2radius, rect.Top, y2radius, SKPathDirection.Clockwise);
                }
            }

            path.Close();
        }

        /// <summary>
        /// The waveform of a voice note: one rounded 2 px bar per column, as ONE path used as a
        /// clip -- the rectangle behind it is what gets painted, so the played part of the bar can
        /// be revealed with an inset clip instead of redrawing anything.
        /// <c>AppendVoiceNote</c> of the C++, transcribed arc for arc (the D2D and Skia arc
        /// segments take the same endpoint parameterization).
        /// </summary>
        /// <param name="waveform">
        /// Telegram's 5-bits-per-sample packing, straight out of TDLib: sample <c>i</c> lives at
        /// bit <c>i * 5</c> of the byte stream, little-endian, and is scaled 0..31.
        /// </param>
        /// <param name="waveformWidth">Width to spread the samples over, in logical pixels.</param>
        public static void AppendVoiceNote(SKPath path, IList<byte> waveform, double waveformWidth)
        {
            if (path == null || waveform == null || waveform.Count == 0 || waveformWidth <= 0)
            {
                return;
            }

            var lines = waveform.Count * 8 / 5;
            if (lines <= 0)
            {
                return;
            }

            var samples = new double[lines];

            for (int i = 0; i < lines; i++)
            {
                int j = i * 5 / 8, shift = i * 5 % 8;
                var pair = waveform[j] | ((j + 1 < waveform.Count ? waveform[j + 1] : 0) << 8);
                samples[i] = (pair >> shift & 0x1F) / 31.0;
            }

            const int imageHeight = 20;
            const double space = 1.0;
            const double lineWidth = 2.0;

            var maxLines = (waveformWidth - space) / (lineWidth + space);
            if (maxLines < 1)
            {
                return;
            }

            var maxWidth = lines / maxLines;

            for (int index = 0; index < maxLines; index++)
            {
                // Clamped: index * maxWidth is below lines by construction, but it is a double
                // multiplication and the last column must not fall off the end of the samples.
                var lineIndex = Math.Min(lines - 1, (int)(index * maxWidth));
                var lineHeight = samples[lineIndex] * (imageHeight - 2.0) + 2.0;

                // The integer truncation is the C++'s and it is what keeps every bar on a whole
                // pixel; taking it out makes the waveform look blurred at 1x.
                float x1 = (int)(index * (lineWidth + space));
                float y1 = (imageHeight - (int)lineHeight) / 2;
                float x2 = (int)(index * (lineWidth + space) + lineWidth);
                float y2 = imageHeight - y1;

                if (lineHeight > 2)
                {
                    // A 2 px capsule: half-circle cap, straight sides, half-circle cap.
                    path.MoveTo(x1, y1 + 1);
                    ArcTo(path, x2, y1 + 1, 1, SKPathDirection.Clockwise);
                    path.LineTo(x2, y2 - 1);
                    ArcTo(path, x1, y2 - 1, 1, SKPathDirection.Clockwise);
                }
                else
                {
                    // Silence: a single 2 px dot on the centre line.
                    path.MoveTo(x1, 10);
                    ArcTo(path, x2, 10, 1, SKPathDirection.Clockwise);
                    ArcTo(path, x1, 10, 1, SKPathDirection.Clockwise);
                }

                path.Close();
            }
        }
    }
}
