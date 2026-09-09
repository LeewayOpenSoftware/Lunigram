//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;

namespace Telegram.Native
{
    public sealed partial class QrBuffer
    {
        private QrBuffer(int size, IList<bool> values, int replaceFrom, int replaceTill)
        {
            Size = size;
            Values = values;
            ReplaceFrom = replaceFrom;
            ReplaceTill = replaceTill;
        }

        public int Size { get; }

        public IList<bool> Values { get; }

        public int ReplaceFrom { get; }

        public int ReplaceTill { get; }

        public static QrBuffer FromString(string text)
        {
            return FromString(text, 1, 40);
        }

        public static QrBuffer FromString(string text, int minVersion, int maxVersion)
        {
            QrCodeGenerator qr;
            try
            {
                qr = QrCodeGenerator.EncodeText(text ?? string.Empty, QrCodeGenerator.Ecc.Medium, minVersion, maxVersion);
            }
            catch
            {
                // Data too long for the requested versions: an empty grid rather than a crash
                return new QrBuffer(21, new bool[21 * 21], 0, 0);
            }

            var size = qr.Size;
            var values = new bool[size * size];

            // GetModule takes (x, y) - column first, the qrcodegen convention - and QrCode.cs
            // reads Values[row * Size + column] to draw at x = column, y = row. Passing the row
            // as x transposes the grid: the finder, timing and format patterns all survive it
            // symmetrically, so the result looks like a perfectly good QR and no scanner can
            // read it, because the data modules are traversed along the wrong axis.
            for (int row = 0; row < size; row++)
            {
                for (int column = 0; column < size; column++)
                {
                    values[row * size + column] = qr.GetModule(column, row);
                }
            }

            var replaceElements = ReplaceElements(size);
            var replaceFrom = (size - replaceElements) / 2;
            var replaceTill = size - replaceFrom;

            return new QrBuffer(size, values, replaceFrom, replaceTill);
        }

        private static int ReplaceElements(int size)
        {
            var elements = size / 4;
            var shift = (size - elements) % 2;
            return elements - shift;
        }
    }
}
