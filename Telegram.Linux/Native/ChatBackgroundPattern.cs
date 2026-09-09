//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Telegram.Native
{
    public struct ChatBackgroundSymbol
    {
        public Vector2 Offset;
        public Vector2 Size;
        public float RotationAngle;
    }

    public sealed partial class ChatBackgroundPattern
    {
        private readonly ICompositionSurface _surface;
        private readonly Vector2 _renderSize;
        private readonly Vector2 _renderPhysicalSize;
        private readonly IList<ChatBackgroundSymbol> _symbols;

        public ChatBackgroundPattern(ICompositionSurface surface)
        {
            _surface = surface;
            _renderSize = Vector2.One;
            _renderPhysicalSize = Vector2.One;
            _symbols = new List<ChatBackgroundSymbol>();
        }

        public ChatBackgroundPattern(ICompositionSurface surface, float width, float height, float rasterizationScale, IList<ChatBackgroundSymbol> patterns)
        {
            _surface = surface;
            _renderSize = new Vector2(width, height);
            _renderPhysicalSize = new Vector2(width * rasterizationScale, height * rasterizationScale);
            _symbols = patterns ?? new List<ChatBackgroundSymbol>();
        }

        public ICompositionSurface Surface => _surface;

        public Vector2 RenderSize
        {
            get
            {
                if (_surface is LoadedImageSurface loaded)
                {
                    return NonZero(loaded.DecodedSize, _renderSize);
                }

                return _renderSize;
            }
        }

        public Vector2 RenderPhysicalSize
        {
            get
            {
                if (_surface is LoadedImageSurface loaded)
                {
                    return NonZero(loaded.DecodedPhysicalSize, _renderPhysicalSize);
                }

                return _renderPhysicalSize;
            }
        }

        public Vector2 NaturalSize
        {
            get
            {
                if (_surface is LoadedImageSurface loaded)
                {
                    return NonZero(loaded.NaturalSize, _renderSize);
                }

                return _renderSize;
            }
        }

        public IList<ChatBackgroundSymbol> Symbols => _symbols;

        // ChatBackgroundBrush divides logical by physical size: never hand it a zero.
        private static Vector2 NonZero(Size size, Vector2 fallback)
        {
            if (size.Width > 0 && size.Height > 0)
            {
                return new Vector2((float)size.Width, (float)size.Height);
            }

            return fallback;
        }
    }
}
