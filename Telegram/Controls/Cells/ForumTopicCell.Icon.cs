//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Td.Api;
using Windows.UI;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Cells
{
    // The topic icon palette lives apart from the cell because it is the only part of
    // ForumTopicCell that its eleven callers need: the forum topic header of the chat, the
    // sticky header, the emoji drawer, the profile header and the share cells all ask for a
    // gradient, none of them for a list cell. The Linux subset compiles this file without the
    // cell itself, which is why the colored circle shows up there at all.
    public sealed partial class ForumTopicCell
    {
        public static Color[] ServerSupportedColors = new Color[6]
        {
            Color.FromArgb(0xFF, 0x6F, 0xB9, 0xF0), // blue
            Color.FromArgb(0xFF, 0xFF, 0xD6, 0x7E), // yellow
            Color.FromArgb(0xFF, 0xCB, 0x86, 0xDB), // violet
            Color.FromArgb(0xFF, 0x8E, 0xEE, 0x98), // green
            Color.FromArgb(0xFF, 0xFF, 0x93, 0xB2), // rose
            Color.FromArgb(0xFF, 0xFB, 0x6F, 0x5F), // orange
        };

        private static readonly Color[] _colorsTop = new Color[6]
        {
            Color.FromArgb(0xFF, 0x8A, 0xD3, 0xF9), // blue
            Color.FromArgb(0xFF, 0xF7, 0xCE, 0x79), // yellow
            Color.FromArgb(0xFF, 0x8C, 0xAF, 0xF9), // violet
            Color.FromArgb(0xFF, 0xAC, 0xDC, 0x89), // green
            Color.FromArgb(0xFF, 0xFF, 0xAF, 0xC7), // rose
            Color.FromArgb(0xFF, 0xEF, 0x8E, 0x67), // orange
        };

        private static readonly Color[] _colors = new Color[6]
        {
            Color.FromArgb(0xFF, 0x51, 0x9D, 0xEA), // blue
            Color.FromArgb(0xFF, 0xF2, 0xAC, 0x6A), // yellow
            Color.FromArgb(0xFF, 0x65, 0x60, 0xF6), // violet
            Color.FromArgb(0xFF, 0x75, 0xC8, 0x73), // green
            Color.FromArgb(0xFF, 0xF2, 0x74, 0x9A), // rose
            Color.FromArgb(0xFF, 0xEC, 0x5F, 0x6D), // orange
        };

        public static int FindIconColorIndex(int color)
        {
            static int Distance(Color a, Color b)
            {
                return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            }

            var value = color.ToColor();

            int distance = Distance(ServerSupportedColors[0], value);
            var index = 0;

            for (int i = 0; i < ServerSupportedColors.Length; i++)
            {
                int distanceLocal = Distance(ServerSupportedColors[i], value);
                if (distanceLocal < distance)
                {
                    distance = distanceLocal;
                    index = i;
                }
            }

            return index;
        }

        public static LinearGradientBrush GetIconGradient(ForumTopicIcon icon)
        {
            var index = FindIconColorIndex(icon.Color);

            var top = _colorsTop[index];
            var bottom = _colors[index];

            return new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop
                {
                    Color = top,
                    Offset = 0
                },
                new GradientStop
                {
                    Color = bottom,
                    Offset = 1
                }
            }, 90);
        }
    }
}
