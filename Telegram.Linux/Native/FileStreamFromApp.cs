//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.IO;
using Windows.Storage.Streams;

namespace Telegram.Native
{
    public sealed partial class FileStreamFromApp : IDisposable
    {
        private FileStream _stream;

        public FileStreamFromApp(string path)
        {
            try
            {
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch
            {
                _stream = null;
            }
        }

        public bool IsValid => _stream != null;

        public bool Seek(long offset)
        {
            if (_stream == null)
            {
                return false;
            }

            try
            {
                _stream.Seek(offset, SeekOrigin.Begin);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public unsafe int Read(long pointer, uint length)
        {
            if (_stream == null || pointer == 0 || length == 0)
            {
                return 0;
            }

            try
            {
                return _stream.Read(new Span<byte>((void*)pointer, (int)length));
            }
            catch
            {
                return 0;
            }
        }

        public int Read(IBuffer buffer, uint length)
        {
            if (_stream == null || buffer == null)
            {
                return 0;
            }

            try
            {
                var bytes = new byte[Math.Min(length, buffer.Capacity)];
                var read = _stream.Read(bytes, 0, bytes.Length);

                BufferSurface.Write(buffer, bytes, read);
                return read;
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
