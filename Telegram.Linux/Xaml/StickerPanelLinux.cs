//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;

namespace Telegram.Controls
{
    public sealed partial class StickerPanel
    {
        // WinUI's XAML compiler emits an UnloadObject(DependencyObject) next to the FindName() that
        // materializes an x:Load="False" element; Uno's does not. Measured on Uno 6.6.184 against
        // the generated StickerPanel partial: x:Load itself IS supported -- the three drawers come
        // out as Microsoft.UI.Xaml.ElementStub and FindName materializes them -- but the string
        // "UnloadObject" appears zero times in the generated code, so the shared code-behind does
        // not compile (CS0103, three call sites in UnloadAtIndex).
        //
        // Uno does expose ElementStub.Dematerialize(), and neither it nor Materialize() is on the
        // NotImplemented list, so undoing the load is possible in principle. What is NOT reachable
        // from here is the stub itself: once it materializes, Uno swaps the real element into the
        // parent's content and the generated partial keeps only an ElementNameSubject holding the
        // materialized instance -- there is no supported path back to the ElementStub that owns it.
        //
        // Guessing at one is the wrong trade. The caller (UnloadAtIndex) has already done every
        // part of the teardown that has an observable effect: Deactivate(), DataContext = null, the
        // handlers unsubscribed and the tab collapsed. All UnloadObject adds on top is releasing the
        // element, which is memory, not behaviour. So this is a deliberate no-op, and the one thing
        // it changes -- the drawer staying materialized -- is compensated in LoadAtIndex, which on
        // Linux re-enters the init branch when the DataContext is gone instead of when the field is
        // null. The honest cost, written down rather than hidden: each of the three drawers stays
        // allocated after its first visit for the life of the panel.
        private void UnloadObject(DependencyObject element)
        {
        }
    }
}
