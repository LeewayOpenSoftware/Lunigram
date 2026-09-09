//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

namespace Telegram.Native
{
    public enum AnimationEffectsState
    {
        Auto = -1,
        Disabled = 0,
        Enabled = 1
    }

    public static class AnimationEffects
    {
        public static bool Supported => false;

        public static bool Enabled => false;

        public static void Initialize()
        {
        }

        public static AnimationEffectsState State { get; set; } = AnimationEffectsState.Auto;
    }
}
