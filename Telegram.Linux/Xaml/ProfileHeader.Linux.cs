//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of Controls/ProfileHeader.xaml.cs.
//
// The collapsing header of a profile is fifteen ExpressionAnimations reading one scroll position.
// In Uno that position cannot live where WinUI puts it, for two separate reasons measured on the
// deployed Uno 6.6.184 (both in PORTING.md §6):
//
//   1. ElementCompositionPreview.GetScrollViewerManipulationPropertySet does not exist. The port's
//      shim answers with a property set that nobody feeds.
//   2. Even fed by hand it would not work. CompositionPropertySet.InsertScalar/InsertVector3 call a
//      private SetValue that writes the dictionary and returns — no OnPropertyChanged, so the
//      contexts an ExpressionAnimation registers on every object it references are never walked,
//      AnimationFrame is never raised, and the expression is evaluated once and frozen. (The A/B is
//      in HANDOFF.md: an expression reading a property set does not notice InsertScalar; the same
//      expression restarted does.)
//
// So the position is carried by a Visual — ElementCompositionPreview.GetScrollingCarrier — whose
// Offset.Y holds what WinUI would have read as Translation.Y. A Visual's setters DO notify:
// SetProperty -> OnPropertyChanged -> PropagateChanged walks the contexts and raises AnimationFrame
// on every expression that reads it. Values are then read live: an identifier caches the resolved
// object, never the value, and member access goes through GetAnimatableProperty each time.
//
// That is also why this file exists: ProfileHeader.Properties is still a property set, and the
// scalars in it (RemovedHeight, ActualHeight, HeaderActualHeight) change on layout, not on scroll.
// Writing them notifies nobody, so after a size change the expressions have to be poked once.

using Microsoft.UI.Composition;

namespace Telegram.Controls
{
    public sealed partial class ProfileHeader
    {
        /// <summary>
        /// The scroll-position carrier handed over by ProfilePage through InitializeScrolling.
        /// Null until then, which is the whole of the control's life before it is on screen.
        /// </summary>
        private Visual _scrollingCarrier;

        /// <summary>
        /// Forces one re-evaluation of every expression built on the carrier. Needed after anything
        /// the expressions read changes without the scroll position changing — that is, after a size
        /// change, which is when OnSizeChanged rewrites Properties.
        /// </summary>
        /// <remarks>
        /// Steps away and back because CompositionObject.SetProperty compares before it notifies:
        /// writing the same Offset is dropped, and dropping it is exactly what we cannot afford.
        /// One layout unit is used rather than a fraction so the intermediate value cannot be lost
        /// to float equality.
        /// </remarks>
        private void InvalidateScrolling()
        {
            if (_scrollingCarrier is Visual carrier)
            {
                var offset = carrier.Offset;

                carrier.Offset = offset + new System.Numerics.Vector3(0, 1, 0);
                carrier.Offset = offset;
            }
        }
    }
}
