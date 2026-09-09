//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

namespace Telegram.Native
{
    public struct MaxLinesMetrics
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public double TruncatedHeight;
        public int TruncatedOffset;
    }

    public struct TextStylePart
    {
        public int Offset;
        public int Length;
        public TextStyle Type;
    }
}
