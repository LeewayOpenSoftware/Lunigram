//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Numerics;
using Microsoft.UI.Composition;

namespace Telegram.Native.Composition
{
    public sealed partial class DirectRectangleClip2
    {
        public float Left { get; set; }

        public float Top { get; set; }

        public float Right { get; set; }

        public float Bottom { get; set; }

        public Vector2 TopLeft { get; set; }

        public Vector2 TopRight { get; set; }

        public Vector2 BottomRight { get; set; }

        public Vector2 BottomLeft { get; set; }

        public void Set(Vector2 uniform)
        {
            TopLeft = uniform;
            TopRight = uniform;
            BottomRight = uniform;
            BottomLeft = uniform;
        }

        public void Set(Vector2 topLeft, Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft)
        {
            TopLeft = topLeft;
            TopRight = topRight;
            BottomRight = bottomRight;
            BottomLeft = bottomLeft;
        }

        public void SetInset(float uniform)
        {
            Left = uniform;
            Top = uniform;
            Right = uniform;
            Bottom = uniform;
        }

        public void SetInset(float left, float top, float right, float bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public void AnimateTop(Compositor compositor, float from, float to, double duration)
        {
            Top = to;
        }

        public void AnimateBottom(Compositor compositor, float from, float to, double duration)
        {
            Bottom = to;
        }

        public void AnimateBottomLeft(Compositor compositor, Vector2 from, Vector2 to, double duration)
        {
            BottomLeft = to;
        }

        public void AnimateBottomRight(Compositor compositor, Vector2 from, Vector2 to, double duration)
        {
            BottomRight = to;
        }
    }
}
