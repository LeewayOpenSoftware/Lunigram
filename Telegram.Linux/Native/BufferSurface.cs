//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Telegram.Native
{
    public static class BufferSurface
    {
        public static IBuffer Create(uint size)
        {
            var buffer = new Windows.Storage.Streams.Buffer(size);
            buffer.Length = size;
            return buffer;
        }

        public static IBuffer Create(byte[] data)
        {
            return data.AsBuffer();
        }

        public static void Copy(IBuffer source, IBuffer destination)
        {
            source = PixelBuffer.Unwrap(source);
            destination = PixelBuffer.Unwrap(destination);

            var count = Math.Min(source.Length, destination.Capacity);
            if (count == 0)
            {
                return;
            }

            source.CopyTo(0, destination, 0, count);

            if (destination.Length < count)
            {
                destination.Length = count;
            }
        }

        internal static void Write(IBuffer destination, byte[] bytes, int count)
        {
            destination = PixelBuffer.Unwrap(destination);

            count = (int)Math.Min(count, destination.Capacity);
            if (count > 0)
            {
                bytes.CopyTo(0, destination, 0, count);
            }

            destination.Length = (uint)count;
        }
    }
}
