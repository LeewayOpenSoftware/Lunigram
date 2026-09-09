//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;

namespace Telegram.Controls.Cells
{
    public sealed partial class FileDownloadCell
    {
        // Same missing codegen as StickerPanelLinux: WinUI's XAML compiler emits an
        // UnloadObject(DependencyObject) next to the FindName() that materializes an
        // x:Load="False" element and Uno's does not, so the shared code-behind fails to compile
        // (CS0103 on the one call site in UpdateFirst). See PORTING.md, the u-003 entry.
        //
        // But the substitute there -- a deliberate no-op -- would be WRONG here, and copying it
        // without reading why it was safe is the trap. In StickerPanel the caller had already done
        // every part of the teardown that has an observable effect, so all UnloadObject added was
        // releasing memory. Here it is the whole point: Header carries "Downloading" or "Recently
        // downloaded", and UpdateFirst calls this precisely when the cell has STOPPED being the
        // first of its section. A no-op would leave that heading sitting on a cell in the middle
        // of the list -- a visible defect, not a memory one.
        //
        // So it collapses instead of unloading. The element stays materialized, which costs the
        // same handful of bytes the StickerPanel note already accepts, and the visible behaviour is
        // exactly what the caller asked for. The other half is in UpdateFirst, which sets it back
        // to Visible under #if LINUX when the cell becomes first again -- on WinUI that is what
        // FindName does implicitly, and here the element is never unloaded to be found again.
        private void UnloadObject(DependencyObject element)
        {
            if (element is UIElement visible)
            {
                visible.Visibility = Visibility.Collapsed;
            }
        }
    }
}
