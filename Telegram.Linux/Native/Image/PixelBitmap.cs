//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Telegram.Native
{
    /// <summary>
    /// A decoded image in memory: packed premultiplied BGRA plus its size. Stands in for the
    /// <c>Windows.Graphics.Imaging.SoftwareBitmap</c> that the Windows PlaceholderImageHelper hands
    /// back from <c>DrawBlurred</c>, and the shared code takes it under that name through a
    /// <c>using</c> alias (see Telegram/Common/ThumbnailController.cs).
    /// </summary>
    /// <remarks>
    /// Uno's own SoftwareBitmap is not the way through. Measured against Uno 6.6.184 (see
    /// unigram-linux/HANDOFF.md): <c>SoftwareBitmap.LockBuffer</c> throws NotImplementedException,
    /// <c>CopyToBuffer</c> is a silent no-op, its inner SKBitmap is internal to Uno.UI, and -- the
    /// one that settles it -- <c>SoftwareBitmapSource.SetBitmapAsync</c>, the only thing the shared
    /// code ever does with the result, throws too. So the pixels would have no way out of the
    /// bitmap and no way onto the screen.
    /// </remarks>
    public sealed partial class PixelBitmap : IDisposable
    {
        private byte[] _pixels;

        public PixelBitmap(byte[] pixels, int pixelWidth, int pixelHeight)
        {
            _pixels = pixels;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
        }

        /// <summary>Packed premultiplied BGRA, <c>PixelWidth * PixelHeight * 4</c> bytes.</summary>
        public byte[] Pixels => _pixels;

        public int PixelWidth { get; }

        public int PixelHeight { get; }

        public void Dispose()
        {
            _pixels = null;
        }
    }

    public static class PixelBitmapExtensions
    {
        /// <summary>
        /// What <c>SoftwareBitmapSource.SetBitmapAsync</c> was: put these pixels on screen. The
        /// shared code keeps calling it by that name, on what the alias made a
        /// <see cref="BitmapImage"/>.
        /// </summary>
        /// <remarks>
        /// The pixels go in as a PNG rather than as a raw buffer, which looks like a detour and is
        /// not: the callers create the ImageSource before they know how big the image will be
        /// (ImageView.GetSource, ThumbnailController.Blur), and the only Uno ImageSource that takes
        /// a raw buffer is WriteableBitmap, whose size is fixed by its constructor and cannot be
        /// changed afterwards -- there is no seam to subclass either, since ImageSource's
        /// TryOpenSourceSync/Async are private protected inside Uno.UI. BitmapImage +
        /// SetSourceAsync is the path this port already uses for TDLib minithumbnails in
        /// Controls/Cells/ChatCell.xaml.cs, and the images involved are small (a minithumbnail is a
        /// few dozen pixels a side).
        /// </remarks>
        public static async Task SetBitmapAsync(this BitmapImage source, PixelBitmap bitmap)
        {
            if (source == null || bitmap?.Pixels == null)
            {
                return;
            }

            var png = BlurredImage.Encode(bitmap.Pixels, bitmap.PixelWidth, bitmap.PixelHeight);
            if (png == null)
            {
                return;
            }

            using var stream = new InMemoryRandomAccessStream();
            PlaceholderImageHelper.WriteBytes(png, stream);

            await source.SetSourceAsync(stream);
        }
    }
}
