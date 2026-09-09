//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Numerics;
using Microsoft.UI.Composition;
using Windows.UI;

namespace Telegram.Native.Composition
{
    public sealed partial class WindowVisual
    {
        public static bool IsValid(WindowId windowId, out string title)
        {
            title = null;
            return false;
        }

        public static WindowVisual Create(WindowId windowId)
        {
            return null;
        }

        public static uint GetWindowProcessId(WindowId windowId)
        {
            return 0;
        }

        public static WindowId GetCurrentWindowId()
        {
            return default;
        }

        public Visual Child => null;

        public Vector2 Size { get; set; }
    }
}
