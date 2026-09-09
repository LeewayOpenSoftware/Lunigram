//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;

namespace Telegram.Native.Composition
{
    public static class CompositionDevice
    {
        public static DirectRectangleClip2 CreateRectangleClip2(UIElement element)
        {
            return new DirectRectangleClip2();
        }

        public static DirectRectangleClip2 CreateRectangleClip2(Visual visual)
        {
            return new DirectRectangleClip2();
        }

        public static void SetClip(Visual visual, DirectRectangleClip2 clip)
        {
        }

        // LayerVisual is not implemented on Uno Skia: callers fall back to CornerRadius clipping.
        public static LayerVisual GetElementLayerVisual(UIElement element)
        {
            return null;
        }
    }
}
