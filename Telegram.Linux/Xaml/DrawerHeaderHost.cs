//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Drawers
{
    /// <summary>
    /// Takes a drawer list's <c>Header</c> and <c>Footer</c> out of the list and puts the header in
    /// a host of its own, above it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY, measured. Uno's <c>ItemsPresenter</c> decides whether to stack Header / Panel / Footer
    /// along X or along Y from <c>(Panel as Panel)?.PhysicalOrientation ?? Orientation.Horizontal</c>,
    /// and for a wrapping panel that internal property is the ITEM-FLOW axis, not the scroll axis:
    /// <c>Orientation="Horizontal"</c> means "rows, wrapping downwards", so the list scrolls
    /// vertically while the presenter reads Horizontal and lays the header out BESIDE the grid.
    /// It is not theory: in a live capture of an EmojiDrawer the search box sits at
    /// <c>[58,84 292x248]</c> -- full list height, its own natural width -- and the items panel
    /// starts at <c>x=314</c> with <c>0</c> width. So the search box eats the row and the grid gets
    /// nothing, which is the drawer rendering as an empty panel.
    /// </para>
    /// <para>
    /// The profile media / GIF / gifts tabs meet the same trap and fix it by folding the offset into
    /// the list's Padding (<c>ProfileTabPage.HeaderStacksBesideItems</c>), but that only works there
    /// because their header is a width-less spacer. A drawer's header is a real search box, so it
    /// has to leave the list entirely. The XAML gives it a row of its own that is
    /// <c>Height="Auto"</c> and empty on Windows, i.e. zero-high and invisible there; only Linux
    /// fills it.
    /// </para>
    /// <para>
    /// The footer of these lists is a bare spacer <c>Border</c>, and it stacks beside the panel for
    /// exactly the same reason, so it is removed and its height added to the list's bottom padding,
    /// which <c>ItemsPresenter</c> applies as the origin of its rect in BOTH axes.
    /// </para>
    /// </remarks>
    internal static class DrawerHeaderHost
    {
        public static void MoveOutOfList(ListViewBase list, Border host)
        {
            if (list == null || host == null)
            {
                return;
            }

            if (list.Header is UIElement header)
            {
                // Read before the presenter exists (call this from the constructor) so the element
                // still has no parent and can simply be handed over.
                list.Header = null;
                host.Child = header;
            }

            if (list.Footer is FrameworkElement footer)
            {
                list.Footer = null;

                var height = double.IsNaN(footer.Height) ? 0 : footer.Height;
                if (height > 0)
                {
                    var padding = list.Padding;
                    list.Padding = new Thickness(padding.Left, padding.Top, padding.Right, padding.Bottom + height);
                }
            }
        }
    }
}
