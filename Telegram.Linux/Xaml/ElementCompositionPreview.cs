//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoPreview = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview;

namespace Telegram
{
    /// <summary>
    /// Shadows <see cref="Microsoft.UI.Xaml.Hosting.ElementCompositionPreview"/> for every file in a
    /// Telegram.* namespace (enclosing-namespace lookup wins over the using directive), so the shared
    /// sources keep calling ElementCompositionPreview.* unchanged. Uno Skia's version throws on a null
    /// child visual (the idiom Unigram uses to clear one) and lacks GetElementChildVisual and
    /// GetScrollViewerManipulationPropertySet entirely.
    /// </summary>
    public static class ElementCompositionPreview
    {
        private static readonly ConditionalWeakTable<UIElement, Visual> _children = new();

        public static Visual GetElementVisual(UIElement element)
        {
            return UnoPreview.GetElementVisual(element);
        }

        public static void SetElementChildVisual(UIElement element, Visual visual)
        {
            if (visual == null)
            {
                // Uno keeps the child in a named container and has no removal API: an empty
                // container replaces whatever was there.
                if (_children.TryGetValue(element, out _))
                {
                    _children.Remove(element);
                    UnoPreview.SetElementChildVisual(element, GetElementVisual(element).Compositor.CreateContainerVisual());
                }

                return;
            }

            _children.AddOrUpdate(element, visual);
            UnoPreview.SetElementChildVisual(element, visual);
        }

        public static Visual GetElementChildVisual(UIElement element)
        {
            return _children.TryGetValue(element, out var visual) ? visual : null;
        }

        public static void SetIsTranslationEnabled(UIElement element, bool value)
        {
            UnoPreview.SetIsTranslationEnabled(element, value);
        }

        private static readonly ConditionalWeakTable<ScrollViewer, CompositionPropertySet> _manipulation = new();
        private static readonly ConditionalWeakTable<CompositionPropertySet, Visual> _carriers = new();

        /// <summary>
        /// Uno exposes no manipulation property set. This returns a stand-in, <b>one per
        /// ScrollViewer</b>: callers that ask twice (ProfilePage asks three times, and hands one of
        /// the answers to ProfileHeader) have to end up with the same object, or an update reaches
        /// only a third of the animations built on it.
        /// </summary>
        /// <remarks>
        /// Its Translation is kept up to date by <see cref="UpdateScrollingPosition"/>, but writing
        /// it is NOT enough to move anything: measured on the deployed Uno 6.6.184,
        /// <c>CompositionPropertySet.InsertXxx</c> goes straight into its dictionary and never calls
        /// OnPropertyChanged, so the contexts registered by an ExpressionAnimation that reads it are
        /// never notified and the animation never re-evaluates (PORTING.md §6, and the A/B in
        /// unigram-linux/HANDOFF.md "una CompositionPropertySet no se anima en Uno"). Values ARE read
        /// live once something else re-evaluates the expression, which is what
        /// <see cref="GetScrollingCarrier"/> is for.
        /// </remarks>
        public static CompositionPropertySet GetScrollViewerManipulationPropertySet(ScrollViewer scrollViewer)
        {
            if (_manipulation.TryGetValue(scrollViewer, out var existing))
            {
                return existing;
            }

            var set = GetElementVisual(scrollViewer).Compositor.CreatePropertySet();
            set.InsertVector3("Translation", Vector3.Zero);

            _manipulation.Add(scrollViewer, set);
            return set;
        }

        /// <summary>
        /// The Visual that carries the scroll position for expression animations, paired one to one
        /// with the property set above. Read it as <c>carrier.Offset.Y</c> where WinUI reads
        /// <c>scrollViewer.Translation.Y</c>: same value, same sign (negative going down).
        /// </summary>
        /// <remarks>
        /// A Visual and not a property set because only the Visual notifies. Its setters go through
        /// CompositionObject.SetProperty -> OnPropertyChanged -> PropagateChanged, which walks the
        /// contexts an ExpressionAnimation registers for every identifier it resolves and raises
        /// AnimationFrame on them; that is the whole mechanism by which an expression re-evaluates
        /// in Uno. The carrier is deliberately unparented: InvalidateRender on a visual with no
        /// CompositionTarget is a null-safe no-op, and the visuals that matter are invalidated by
        /// the re-evaluation itself.
        /// </remarks>
        public static Visual GetScrollingCarrier(CompositionPropertySet properties)
        {
            if (_carriers.TryGetValue(properties, out var existing))
            {
                return existing;
            }

            var carrier = properties.Compositor.CreateContainerVisual();
            _carriers.Add(properties, carrier);
            return carrier;
        }

        /// <summary>
        /// Publishes a new scroll position to both halves. Call it from ViewChanged, which is the
        /// only scroll event Uno actually raises: ViewChanging, DirectManipulationStarted and
        /// DirectManipulationCompleted are all [NotImplemented] for __SKIA__ and never fire.
        /// </summary>
        public static void UpdateScrollingPosition(ScrollViewer scrollViewer, double verticalOffset)
        {
            var properties = GetScrollViewerManipulationPropertySet(scrollViewer);
            var translation = new Vector3(0, -(float)verticalOffset, 0);

            properties.InsertVector3("Translation", translation);
            GetScrollingCarrier(properties).Offset = translation;
        }

        /// <summary>
        /// Forces one re-evaluation of every expression built on the carrier, for the case where the
        /// scroll position did not change but something the expressions read did — a size, or one of
        /// the scalars in the property set. Writing the same Offset would be dropped by SetProperty's
        /// equality check, so this steps away and back.
        /// </summary>
        public static void InvalidateScrollingPosition(ScrollViewer scrollViewer)
        {
            var carrier = GetScrollingCarrier(GetScrollViewerManipulationPropertySet(scrollViewer));
            var offset = carrier.Offset;

            carrier.Offset = offset + new Vector3(0, 1, 0);
            carrier.Offset = offset;
        }
    }
}
