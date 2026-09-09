//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Telegram.Native
{
    // The animated four-point gradient was rendered in Direct2D; for now the brush is a flat
    // colour brush painted with the average of the palette, which is what ChatBackgroundBrush
    // assigns to its sprite visual.
    public sealed partial class FreeformGradientSurface : IDisposable
    {
        private IList<int> _colors;
        private CompositionBrush _brush;

        internal FreeformGradientSurface(IList<int> colors)
        {
            _colors = colors;
        }

        public IList<int> Colors
        {
            get => _colors;
            set
            {
                _colors = value;

                if (_brush is CompositionColorBrush brush)
                {
                    brush.Color = Average(value);
                }
            }
        }

        public CompositionBrush Brush
        {
            get
            {
                if (_brush == null)
                {
                    var compositor = Window.Current?.Compositor;
                    if (compositor != null)
                    {
                        _brush = compositor.CreateColorBrush(Average(_colors));
                    }
                }

                return _brush;
            }
        }

        public void Next()
        {
        }

        public void Dispose()
        {
            _brush = null;
        }

        private static Color Average(IList<int> colors)
        {
            if (colors == null || colors.Count == 0)
            {
                return Color.FromArgb(0xFF, 0, 0, 0);
            }

            long r = 0;
            long g = 0;
            long b = 0;

            foreach (var color in colors)
            {
                r += (color >> 16) & 0xFF;
                g += (color >> 8) & 0xFF;
                b += color & 0xFF;
            }

            return Color.FromArgb(0xFF, (byte)(r / colors.Count), (byte)(g / colors.Count), (byte)(b / colors.Count));
        }
    }
}
