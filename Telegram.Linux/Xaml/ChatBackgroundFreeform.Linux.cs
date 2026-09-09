//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Telegram.Common;
using Telegram.Native;
using Telegram.Td.Api;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Telegram.Controls.Chats
{
    /// <summary>
    /// The Linux ChatBackgroundFreeform.
    ///
    /// Upstream's (<c>Telegram/Controls/Chats/ChatBackgroundControl.cs</c>, from line 308) is
    /// excluded here by an <c>#if !LINUX</c>: it paints straight into
    /// <c>WriteableBitmap.Buffer</c>, an IBufferByteAccess QI that Uno does not answer. The note
    /// left at that <c>#if</c> says the algorithm lives in
    /// <c>Telegram.Linux/Graphics/ChatBackgroundRenderer.cs</c> instead, and that its two callers
    /// were out of the subset. The call page is the third caller and it IS in the subset now, so
    /// this is the missing bridge -- the same eight anchors and the same 50-pixel working size,
    /// drawn by Skia and handed over as a <see cref="BitmapImage"/>.
    ///
    /// Only <c>Create</c> is here. <c>Update</c> and <c>Next</c> take a WriteableBitmap the
    /// caller owns and repaint it in place, which is precisely the buffer access that does not
    /// work; nothing in the subset calls them.
    /// </summary>
    public static partial class ChatBackgroundFreeform
    {
        public static ImageSource Create(BackgroundFillFreeformGradient freeform, int offset = 0)
        {
            var image = new BitmapImage();

            try
            {
                // GetColors() da Windows.UI.Color[]; el renderer de Skia toma enteros 0xRRGGBB,
                // que es como TDLib entrega los colores del fondo.
                var colors = new List<int>();
                foreach (var color in freeform.GetColors())
                {
                    colors.Add((color.R << 16) | (color.G << 8) | color.B);
                }

                // The second way into the fallback, and the one with no canvas behind it: an empty
                // colour list here reaches ToRgb exactly like an unbound canvas does, and would be
                // indistinguishable in a log that only watched the canvas.
                if (colors.Count == 0)
                {
                    Logger.Warning("ChatBackgroundFreeform: GetColors() returned nothing, the default palette will stand in");
                }

                using var bitmap = ChatBackgroundRenderer.CreateFreeformGradient(50, 50, colors, offset % 8);

                var pixels = new PixelBitmap(bitmap.Bytes, bitmap.Width, bitmap.Height);

                // Fire and forget on purpose: the shared caller assigns the result to an
                // ImageBrush synchronously and the brush simply shows nothing until the source
                // is decoded, which is the same behaviour every other image in this port has.
                _ = image.SetBitmapAsync(pixels);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }

            return image;
        }
    }
}
