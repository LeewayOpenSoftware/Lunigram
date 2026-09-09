//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

namespace Telegram.Common
{
    public static partial class ConnectedAnimationServiceEx
    {
        // Same seam as ConnectedAnimationServiceEx.Win32.cs, same reason: there is no
        // ConnectedAnimationService on Uno/Skia - ConnectedAnimationService.GetForCurrentView()
        // throws NotImplementedException. Without this partial, IsSupported defaults to true (the
        // `var supported = true;` in the shared file never gets flipped) and every caller -
        // ChatView, EditMediaPopup, SendFilesPopup, ProfileHeader, GalleryWindow, VoipWindow's own
        // encryption-emoji morph - reaches that raw call. Measured via check-porting-traps.py: it
        // flagged the raw GetForCurrentView() call sites in ConnectedAnimationServiceEx.cs as
        // reachable, and this is the missing per-host guard that makes them dead code instead.
        static partial void Unsupported(ref bool supported)
        {
            supported = false;
        }
    }
}
