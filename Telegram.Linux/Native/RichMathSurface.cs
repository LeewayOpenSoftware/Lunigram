//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Windows.Storage.Streams;
using Windows.UI;

namespace Telegram.Native
{
    public sealed partial class RichMathSurface
    {
        public RichMathSurface(string formula)
        {
            Formula = formula;
        }

        public string Formula { get; }

        public int PixelWidth => 0;

        public int PixelHeight => 0;

        public float Baseline => 0;

        public void RenderSync(IBuffer buffer, double rasterizationScale, Color foreground)
        {
        }
    }
}
