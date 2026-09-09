//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Collections;
using Telegram.Common;
using Telegram.Controls.Messages;
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Delegates;
using Microsoft.UI.Input;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Controls.Chats
{
    public partial class ChatHistoryView : ListViewEx
    {
        public DialogViewModel ViewModel { get; set; }
        public IDialogDelegate Delegate { get; set; }

        public ScrollViewer ScrollingHost { get; private set; }
        public CompositionPropertySet ScrollingPropertySet { get; private set; }

#if LINUX
        // Uno Skia has no ItemsUpdatingScrollMode, no ChoosingItemContainer and no manipulation
        // property set: the scroll mode is tracked here and mapped onto scroll anchoring, the
        // containers are created/recycled through the ItemsControl overrides, and the property
        // set is fed from ViewChanged so the expression animations of ChatView keep working.
        private double _lastVerticalOffset;

        public ItemsUpdatingScrollMode CurrentScrollingMode => _currentMode;

        public event TypedEventHandler<ChatHistoryView, ChatHistoryViewItem> ContainerCreated;
        public event TypedEventHandler<ChatHistoryViewItem, object> ContainerRecycled;

        public Func<object, DataTemplate> TemplateResolver
        {
            set => ItemTemplateSelector = value != null ? new ResolverTemplateSelector(value) : null;
        }

        private sealed partial class ResolverTemplateSelector : DataTemplateSelector
        {
            private readonly Func<object, DataTemplate> _resolver;

            public ResolverTemplateSelector(Func<object, DataTemplate> resolver)
            {
                _resolver = resolver;
            }

            protected override DataTemplate SelectTemplateCore(object item)
            {
                return _resolver(item);
            }

            protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            {
                return _resolver(item);
            }
        }

        protected override DependencyObject GetContainerForItemOverride()
        {
            var container = new ChatHistoryViewItem(this, ChatHistoryViewItemType.Incoming);

            try
            {
                ContainerCreated?.Invoke(this, container);
            }
            catch (Exception ex)
            {
                // See the note on ClearContainerForItemOverride below: a throw here would cost the
                // whole history, for good.
                Logger.Error($"A handler threw while creating a message container: {ex}");
            }

            return container;
        }

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            // Uno hands the item template to the container as a ContentTemplateSelector, and the
            // ContentPresenter inside the container's own template only picks that selector up in
            // its OnApplyTemplate - which runs on Loaded, that is AFTER ContentPresenter.EnterImpl
            // has already run UpdateContentTemplateRoot once. On that first pass the presenter has
            // a Content (the MessageViewModel, bound by base.PrepareContainerForItemOverride) and
            // no template at all, and Uno's answer to "content that is not a UIElement and no
            // template" is SetContentTemplateRootToPlaceholder(): an ImplicitTextBlock bound to
            // Content, so the row is drawn as the literal text "Telegram.ViewModels.MessageViewModel".
            // It normally heals on the pass that follows, but any row that does not get that second
            // pass keeps the placeholder for good. ContentTemplate, unlike the selector, is
            // template-bound by the container's own template from the moment the presenter is
            // built, so resolving it here means the first pass already finds the right template and
            // no placeholder is ever created.
            //
            // **It has to be done BEFORE base**, and that is not a detail: measured on 2026-08-24,
            // assigning it after base cost the whole message history - see the note in PORTING.md.
            if (element is ChatHistoryViewItem selector && ItemTemplateSelector is DataTemplateSelector resolver)
            {
                selector.ContentTemplate = resolver.SelectTemplate(item, selector);
            }

            base.PrepareContainerForItemOverride(element, item);

            if (element is UIElement candidate)
            {
                ScrollingHost?.RegisterAnchorCandidate(candidate);
            }
        }

        protected override void ClearContainerForItemOverride(DependencyObject element, object item)
        {
            if (element is ChatHistoryViewItem container)
            {
                ScrollingHost?.UnregisterAnchorCandidate(container);

                // This override and the two above are called from inside
                // VirtualizingPanelLayout.UpdateLayout, which sets
                // ItemsStackPanel.ShouldInterceptInvalidate = true on entry and back to false on
                // exit with no try/finally - and InvalidateMeasure is a no-op while that flag is
                // set. An exception thrown here would therefore leave the panel unable to measure
                // again for the rest of the process: an empty history, no realized container, no
                // recovery when the next chat is opened (MasterDetailView reuses the ChatView and
                // its panel), and no exception anywhere near the symptom. See
                // ChatView.OnContainerContentChanging for the same guard on the third callback Uno
                // raises from that same window.
                try
                {
                    ContainerRecycled?.Invoke(container, item);
                }
                catch (Exception ex)
                {
                    Logger.Error($"A handler threw while recycling a message container: {ex}");
                }
            }

            base.ClearContainerForItemOverride(element, item);
        }
#endif

        public bool IsBottomReached
        {
            get
            {
                if (ScrollingHost != null)
                {
                    return ScrollingHost.VerticalOffset.AlmostEquals(ScrollingHost.ScrollableHeight);
                }

                return true;
            }
        }

        private readonly DispatcherTimer _scrollTracker = new();

        private TaskCompletionSource<bool> _waitItemsPanelRoot = new();

        public PanelScrollingDirection ScrollingDirection { get; private set; }

        public ChatHistoryView()
        {
            DefaultStyleKey = typeof(ListView);

            _scrollTracker = new();
            _scrollTracker.Interval = TimeSpan.FromMilliseconds(33);
            _scrollTracker.Tick += OnTick;

            Connected += OnLoaded;
            Disconnected += OnUnloaded;
        }

        private bool _raiseViewChanged;
        public event EventHandler<ScrollViewerViewChangedEventArgs> ViewChanged;

        public void ScrollToBottom()
        {
            HasBeenScrolled = true;
            ScrollingHost?.TryChangeView(null, ScrollingHost.ScrollableHeight, null);
#if LINUX
            // ScrollableHeight is an ESTIMATE here (see the OnBottomPinLayoutUpdated comment): it
            // is extrapolated from _averageLineHeight over the rows Uno happens to have
            // materialized, so a single jump to it lands wherever that guess pointed and stops.
            // Arm the walk to cover whatever the guess was short by.
            ArmBottomPin();
#endif
        }

        public bool IsSuspended => !_raiseViewChanged;

        public bool HasBeenScrolled { get; private set; }

        public void Suspend()
        {
            _raiseViewChanged = false;
            HasBeenScrolled = false;
        }

        public void Resume()
        {
            _raiseViewChanged = true;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ItemsPanelRoot != null)
            {
                ItemsPanelRoot.SizeChanged += OnSizeChanged;

                _waitItemsPanelRoot.TrySetResult(true);
                SetScrollingMode();
            }

#if LINUX
            if (ItemsSource is INotifyCollectionChanged observable)
            {
                observable.CollectionChanged -= OnSourceCollectionChanged;
                observable.CollectionChanged += OnSourceCollectionChanged;
            }

            AttachEmptyLayoutGuard();
#endif

            ViewChanging();
        }

#if LINUX
        #region Keep position on prepend

        // What ItemsUpdatingScrollMode.KeepLastItemInView does in WinUI when a page of older
        // messages is inserted at index 0 - the view does not move - has no equivalent in Uno.
        // Measured on this port, with the whole page (24 messages) inserted one by one:
        //
        //   VerticalAnchorRatio = 1  ->  the view is thrown to the very bottom, 5429 px away
        //   VerticalAnchorRatio = 0  ->  the offset stays at 0, so the loader asks for another
        //                                page, and another, until the 200 message window churns
        //
        // So the position is kept here instead: remember which message is at the top of the
        // viewport and where it is, and put it back after the layout that follows the insertion.
        // The anchoring of 6.6 is left in place for what it does do well - keeping the view pinned
        // to the bottom while it is there, which is the other half of ItemsUpdatingScrollMode.
        private object _keepItem;
        private double _keepY;
        private double _keepOffset;
        private double _keepExtent;
        private bool _keepPending;

        /// <summary>
        /// Whether the view was at the far edge when the anchor was taken, i.e. immediately before
        /// the insertion this handler is compensating for.
        /// </summary>
        /// <remarks>
        /// Deliberately the same shape as the test in <see cref="OnBottomPinLayoutUpdated"/>, which
        /// is Uno's own: ScrollViewer.IsAnchoring reads
        /// <c>VerticalOffset + ViewportHeight - ExtentHeight &gt; -0.1</c> against the extent from
        /// the pass before. Asking it here with the pre-insertion pair keeps the two agreeing about
        /// where the bottom is, instead of leaving each with its own idea of it.
        /// </remarks>
        private bool WasAtFarEdgeBeforeChange()
        {
            return ScrollingHost != null
                && _keepExtent > 0
                && _keepOffset + ScrollingHost.ViewportHeight - _keepExtent > -0.1;
        }

        private void OnSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewStartingIndex != 0
                || ScrollingHost == null || ItemsPanelRoot is not ItemsStackPanel panel)
            {
                return;
            }

            if (_keepPending)
            {
                // A page arrives as N separate inserts; the anchor is the one taken before the
                // first of them, and it is restored once, after the layout of the whole batch.
                return;
            }

            _keepItem = null;
            _keepOffset = ScrollingHost.VerticalOffset;
            _keepExtent = ScrollingHost.ExtentHeight;

            var best = double.PositiveInfinity;

            for (int i = panel.FirstVisibleIndex; i <= panel.LastVisibleIndex; i++)
            {
                if (i < 0 || ContainerFromIndex(i) is not FrameworkElement container)
                {
                    continue;
                }

                var top = container.TransformToVisual(ScrollingHost).TransformPoint(new Point()).Y;

                if (top >= -0.5 && top < best)
                {
                    best = top;
                    _keepItem = ItemFromContainer(container);
                }
            }

            if (_keepItem == null && !WasAtFarEdgeBeforeChange())
            {
                // Nothing realized inside the viewport to hold on to. Compensating by how much the
                // content grew instead was tried and made it worse: on the first load, when the
                // extent goes from one viewport to the whole chat, the correction fights the
                // layout and the history ends up with 46 items and not one realized container.
                //
                // A view that was at the BOTTOM is the exception, and it is why this is not a bare
                // null check any more: it has nothing to hold on to by construction - the far edge
                // is not a message - but it is precisely the case that must not be left where the
                // insertion drops it. It is handled after the layout, in the handler below.
                return;
            }

            _keepY = best;
            _keepPending = true;

            ItemsPanelRoot.LayoutUpdated -= OnKeepLayoutUpdated;
            ItemsPanelRoot.LayoutUpdated += OnKeepLayoutUpdated;
        }

        private void OnKeepLayoutUpdated(object sender, object e)
        {
            if (ItemsPanelRoot != null)
            {
                ItemsPanelRoot.LayoutUpdated -= OnKeepLayoutUpdated;
            }

            _keepPending = false;

            if (ScrollingHost == null)
            {
                _keepItem = null;
                return;
            }

            // The far edge is decided first, and from the numbers taken BEFORE the insertion -
            // which is what _keepOffset and _keepExtent were captured for. A view sitting at the
            // bottom is not holding on to the message at the top of the viewport; it is holding on
            // to the far edge, and a prepend must not move it off.
            //
            // Restoring a mid-list anchor here would ALSO destroy the only evidence the bottom pin
            // has. The pin decides from the offset/extent pair of the PREVIOUS pass, so once this
            // handler has moved the offset, the pin's own "was I at the bottom" test answers no,
            // and the walk that exists to close that gap never re-arms. That is how a chat opened
            // at its last message ended 953 px above it and stayed there:
            //     "Kept the position on a prepend: 198.0px back, offset now 5279.0 of 6232.0"
            // one prepend after the mode had been raised to KeepLastItemInView, which by its own
            // gate only happens within 200 px of the bottom.
            if (WasAtFarEdgeBeforeChange())
            {
                _keepItem = null;

                Logger.Debug(string.Format(CultureInfo.InvariantCulture,
                    "Prepend while pinned to the bottom: walking back to it instead of keeping an anchor, offset {0:F1} of {1:F1}",
                    ScrollingHost.VerticalOffset, ScrollingHost.ScrollableHeight));

                ArmBottomPin();
                return;
            }

            if (_keepItem == null || ContainerFromItem(_keepItem) is not FrameworkElement container)
            {
                _keepItem = null;
                return;
            }

            var drift = container.TransformToVisual(ScrollingHost).TransformPoint(new Point()).Y - _keepY;

            _keepItem = null;

            if (Math.Abs(drift) < 0.5)
            {
                return;
            }

            var target = Math.Clamp(ScrollingHost.VerticalOffset + drift, 0, ScrollingHost.ScrollableHeight);
            ScrollingHost.ChangeView(null, target, null, true);

            Logger.Debug(string.Format(CultureInfo.InvariantCulture,
                "Kept the position on a prepend: {0:F1}px back, offset now {1:F1} of {2:F1}",
                drift, ScrollingHost.VerticalOffset, ScrollingHost.ScrollableHeight));
        }

        #endregion

        #region Hold the far edge ourselves, in steps Uno cannot mis-seed

        // Uno's far-edge anchoring is literally "add to the offset whatever the extent grew by".
        // ScrollViewer.AnchoringArrangeOverride, branch isAnchoringFarEdgeVertically:
        //
        //     double num6 = 0; if (num2 > unzoomedExtentHeight) num6 = num2 - unzoomedExtentHeight;
        //     ... PerformPositionAdjustment(false, num6, ...) -> ChangeView(null, y + num6, null, true)
        //
        // It never asks whether that new extent was measured or extrapolated. Measured on
        // 2026-08-26 in a forum topic of 27 messages: the first video row of the topic is measured,
        // _averageLineHeight jumps, EstimatePanelExtent extrapolates the rest of the list with the
        // new value, the extent goes from 1272 to 7778.5 px, and the anchor adds all 6506.5 px of
        // that to an offset of 648 - with a viewport of 624. That single move is longer than a
        // viewport, which is the one condition under which VirtualizingPanelLayout.OnScrollChanged
        // re-seeds the layout by arithmetic:
        //
        //     bool flag = Math.Abs(value) > ViewportExtent;
        //     ... int num3 = (int)(ScrollOffset / _averageLineHeight);
        //         SetDynamicSeed(IndexPath.FromRowSection(num3 - 1, 0), num3 * _averageLineHeight);
        //
        // and that is the only place in the layout with no clamp to NumberOfItems. Containers get
        // prepared for rows 185..212 of a list of 27, ItemsControl.ItemFromIndex answers null out
        // of range, and the whole screen is drawn as the ToString() of the view model.
        //
        // So the far edge is held here instead, and the difference that matters is that this one
        // moves in steps of half a viewport. Anything under a viewport goes down Uno's incremental
        // path, which fills forward and backward from the lines that already exist and stops at
        // NumberOfItems - 1: it cannot reach a row that does not exist however wrong the extent
        // is. Half rather than a whole because two steps can land in one ViewChanged.
        //
        // Three shapes were measured before this one, and are written down so the afternoon is not
        // spent twice:
        //   - "pin only when the extent is measured (the last item is materialized)" - lever 1 of
        //     the analysis - opens every chat at the TOP: reaching the bottom of a long chat is
        //     precisely a walk over estimated extents. Measured, the history stopped at offset
        //     191.5 of 3012.
        //   - a pin armed by a flag that scroll events clear disarms itself half way through its
        //     own walk, because the view is not at the bottom in the middle of one. Same 191.5.
        //     Hence the test below is stateless and asks Uno's own question with Uno's own numbers.
        //   - capping the target at NumberOfItems * _averageLineHeight - the last offset whose seed
        //     is inside the list - does not hold: the average is recomputed from scratch every pass
        //     and swings by a factor of four between two of them, so the number the cap is computed
        //     from is not the number Uno divides by. 1 opening in 8 still failed.
        //
        // u-093: PinStep 0.5 -> 0.9. Half a viewport per pass was chosen only because it is safely
        // under the 1-viewport threshold VirtualizingPanelLayout.OnScrollChanged uses to decide
        // whether to re-seed by (unclamped) arithmetic instead of filling incrementally - it was
        // never about needing eight-ish passes for an average chat, which is what a 12,000px
        // extent over a 900px viewport costs at 0.5 (~27 passes measured). 0.9 keeps each SINGLE
        // jump under that threshold too, cutting the walk to roughly half as many passes, but two
        // 0.9 steps stacked before either lands would total 1.8 - over the threshold - so a step
        // is only issued once the offset has actually reached the target of the one before it
        // (_pinTarget, checked below). That bounds what Uno ever sees to one PinStep-sized jump at
        // a time regardless of how many LayoutUpdated passes fire while a ChangeView is settling,
        // which is the "two steps can land in one ViewChanged" case the previous 0.5 was chosen to
        // dodge outright.
        private const double PinStep = 0.9;
        private const int MaxPinSteps = 256;

        private double _pinExtent = -1;
        private double _pinOffset = -1;
        private double _pinTarget = -1;
        private bool _pinnedLast;
        private int _pinSteps;

        private void OnBottomPinLayoutUpdated(object sender, object e)
        {
            if (ScrollingHost == null || ItemsPanelRoot is not ItemsStackPanel panel)
            {
                return;
            }

            var extent = ScrollingHost.ExtentHeight;
            var offset = ScrollingHost.VerticalOffset;

            if (extent == _pinExtent && offset == _pinOffset)
            {
                return;
            }

            // Uno's own question, asked with Uno's own numbers. ScrollViewer.IsAnchoring reads
            //
            //     VerticalOffset + ViewportHeight - ExtentHeight > -0.1
            //
            // BEFORE baseArrange, so the extent it compares against is the one from the pass
            // before - which is why the far edge follows a growth instead of losing it. Keeping
            // the previous pair here asks exactly the same thing, and keeps the decision
            // stateless: there is no flag to get stuck, so a jump to a reply or a search result
            // simply reads as "not at the bottom" on the pass after it lands and is left alone.
            var wasAtBottom = _pinExtent < 0 || _pinOffset + ScrollingHost.ViewportHeight - _pinExtent > -0.1;

            // And the other half of Uno's rule, which matters just as much: the adjustment it makes
            // is num2 - unzoomedExtentHeight, so it is zero unless the extent GREW. Without this
            // the pin would follow the view instead of the content - measured, ten scripted moves
            // of -800/-2500/-3000 px were each undone within five seconds and the offset was back
            // at 7245 of 7245 every time, which is a history that cannot be scrolled up.
            var grew = _pinExtent < 0 || extent > _pinExtent + 0.5;

            _pinExtent = extent;
            _pinOffset = offset;

            if (Items.Count == 0)
            {
                // Between two chats: forget the previous one entirely. A chat opens on its last
                // message, which is what a negative _pinExtent stands for.
                _pinExtent = -1;
                _pinOffset = -1;
                _pinTarget = -1;
                _pinnedLast = false;
                _pinSteps = 0;
                return;
            }

            // _pinnedLast keeps a walk that needs several passes armed: the middle of a walk is
            // not the bottom, and without it the pin would stop after its first step.
            if ((!(wasAtBottom && grew) && !_pinnedLast)
                || _currentMode != ItemsUpdatingScrollMode.KeepLastItemInView)
            {
                _pinnedLast = false;
                return;
            }

            var scrollable = ScrollingHost.ScrollableHeight;

            if (scrollable - offset <= 0.5)
            {
                // "offset == ScrollableHeight" is a statement about the EXTENT, and the extent is
                // a guess: ScrollableHeight is ExtentHeight - ViewportHeight, and ExtentHeight is
                // NumberOfItems * _averageLineHeight whenever the list is not fully materialized.
                // When that guess comes out SHORT of the real content, the scroller reports the
                // bottom while the tail of the last message is still below the viewport - and it
                // cannot be scrolled to, because the offset is already at its maximum. The
                // question worth asking is not "am I at the maximum offset" but "is the last
                // message actually visible", so ask that one instead, and only trust the extent
                // when the content agrees with it.
                //
                // The remedy is the one this file already uses for the panel that measures 0 px:
                // ask for another measure, bounded, and re-armed per situation rather than per
                // lifetime. A real measurement of the tail raises the extent, and the walk - still
                // armed - covers the rest on the next pass.
                if (LastItemOverhangsViewport(out var overhang))
                {
                    if (Items.Count != _bottomShortItems || ScrollingHost.ViewportHeight != _bottomShortViewport)
                    {
                        _bottomShort = 0;
                        _bottomShortItems = Items.Count;
                        _bottomShortViewport = ScrollingHost.ViewportHeight;
                    }

                    if (_bottomShort < MaxBottomShortMeasures)
                    {
                        _bottomShort++;

                        Logger.Debug(string.Format(CultureInfo.InvariantCulture,
                            "At the reported bottom but the last message overhangs it by {0:F1}px: asking the panel to measure again ({1}/{2}), offset {3:F1} of {4:F1}",
                            overhang, _bottomShort, MaxBottomShortMeasures, offset, scrollable));

                        // Not a step: there is nowhere further to scroll until the extent grows.
                        // Clearing the in-flight target keeps the next pass free to take a real
                        // step the moment it does.
                        _pinTarget = -1;
                        panel.InvalidateMeasure();
                        return;
                    }
                }

                _pinnedLast = false;
                _pinSteps = 0;
                _pinTarget = -1;
                _bottomShort = 0;
                return;
            }

            // One step in flight at a time (see the PinStep comment above): wait for the offset to
            // actually reach the last target before asking for another. Without this, a
            // LayoutUpdated that fires before the previous ChangeView has visually landed would
            // compute its step from a still-stale offset and queue a second 0.9-viewport jump on
            // top of the first.
            if (_pinTarget >= 0 && offset < _pinTarget - 0.5)
            {
                return;
            }

            if (_pinSteps >= MaxPinSteps)
            {
                // The extent is running away from the pin faster than the pin follows it. Stop
                // rather than spin; arriving at the bottom rearms it.
                _pinnedLast = false;
                _pinTarget = -1;
                return;
            }

            _pinSteps++;
            _pinnedLast = true;

            var step = Math.Max(1, ScrollingHost.ViewportHeight * PinStep);
            _pinTarget = Math.Min(scrollable, offset + step);

            ScrollingHost.ChangeView(null, _pinTarget, null, true);
        }

        // Bounded like MaxEmptyLayouts, and re-armed by the same rule: a counter that is a
        // lifetime total disarms itself on any chat busy enough to fire it a few times, which is
        // the half of that bug the note over there exists to describe.
        private const int MaxBottomShortMeasures = 8;

        private int _bottomShort;
        private int _bottomShortItems = -1;
        private double _bottomShortViewport = -1;

        /// <summary>
        /// Whether the last message extends below the bottom of the viewport - asked of the
        /// CONTAINER, not of the extent, so an extent that under-reports the content cannot answer
        /// it wrongly.
        /// </summary>
        /// <remarks>
        /// Returns false whenever there is nothing to measure - no items, no realized container
        /// for the last one, or a container that has not been measured yet. That is deliberate:
        /// with no evidence the walk should behave exactly as it did before this check existed,
        /// which means stopping. This is only allowed to keep the walk going on POSITIVE evidence
        /// that the last message is not fully on screen.
        /// </remarks>
        private bool LastItemOverhangsViewport(out double overhang)
        {
            overhang = 0;

            if (ScrollingHost == null || Items.Count == 0)
            {
                return false;
            }

            if (ContainerFromIndex(Items.Count - 1) is not FrameworkElement container
                || container.ActualHeight <= 0)
            {
                return false;
            }

            var bottom = container.TransformToVisual(ScrollingHost)
                .TransformPoint(new Point(0, container.ActualHeight)).Y;

            overhang = bottom - ScrollingHost.ViewportHeight;

            // A pixel of slack: sub-pixel layout noise is not an overhang, and treating it as one
            // would spend a measure on every settled chat.
            return overhang > 1;
        }

        /// <summary>
        /// Hand the far edge back to <see cref="OnBottomPinLayoutUpdated"/> after something else
        /// has already aimed at the bottom and may have fallen short of it.
        /// </summary>
        /// <remarks>
        /// The walk normally arms itself, from Uno's own stateless question: "was the view at the
        /// bottom on the previous pass, and did the extent grow". That question is the right one
        /// while the view is sitting at the bottom and content arrives underneath it - but it is
        /// the wrong one right after a programmatic jump, because a jump that lands SHORT of the
        /// bottom answers it with "no" and the walk that exists precisely to close that gap never
        /// starts. Opening a chat is exactly that case, which is why a chat could open two
        /// viewports above its last message and stay there.
        /// </remarks>
        private void ArmBottomPin()
        {
            if (ScrollingHost == null
                || _currentMode != ItemsUpdatingScrollMode.KeepLastItemInView
                || ItemsPanelRoot is not ItemsStackPanel
                || Items.Count == 0)
            {
                return;
            }

            // Already there: leave it alone rather than arm a walk with nowhere to walk. The same
            // test the handler itself uses to disarm, so the two cannot disagree.
            if (ScrollingHost.ScrollableHeight - ScrollingHost.VerticalOffset <= 0.5)
            {
                return;
            }

            // A fresh budget and no step in flight: this is the start of a walk, not the middle of
            // the one that may have been abandoned in some other chat.
            _pinnedLast = true;
            _pinSteps = 0;
            _pinTarget = -1;
        }

        #endregion

        #region A fill window wide enough for the average line height to mean something

        // Everything VirtualizingPanelLayout guesses, it guesses from _averageLineHeight, and that
        // number is the mean measured extent of the MATERIALIZED lines and nothing else:
        //
        //     _averageLineHeight = _materializedLines.Count > 0
        //         ? _materializedLines.Select(l => GetMeasuredExtent(l.FirstView)).Average() : 0.0
        //
        // recomputed from scratch on every pass. With Uno's default CacheLength of 1.0 the fill
        // window is viewport * (1 + 1) = 1248 px here, so a history of tall rows keeps two or
        // three lines materialized and that average is whatever those two or three rows happen to
        // be. Measured on 2026-08-26 in a forum topic of 28 messages: 383.8 px on one pass and
        // 95.8 px on the next, a factor of four, for the same content.
        //
        // That is not a cosmetic wobble, because the average is what turns an offset back into a
        // row. VirtualizingPanelLayout.OnScrollChanged, on any scroll longer than a viewport:
        //
        //     int num3 = (int)(ScrollOffset / _averageLineHeight);
        //     SetDynamicSeed(IndexPath.FromRowSection(num3 - 1, 0), (double)num3 * _averageLineHeight);
        //
        // with no Math.Min against NumberOfItems anywhere - and ItemsControl.GetIncrementedItemIndex
        // ends the walk on `currentItem.Row == NumberOfItems - 1`, an equality, so a seed already
        // past the end never terminates by index at all. ItemFromIndex returns null out of range,
        // PrepareContainerForItemOverride gets that null, and every row on screen is drawn as the
        // ToString() of the view model. Measured: offset 4685 with an average of 95.8 seeds row 47
        // of a list of 29, and the history sat there, cycling, for minutes.
        //
        // The other half of the same coin is EstimatePanelExtent, which adds
        // (NumberOfItems - lastFlat - 1) * _averageLineHeight to the measured content: with a
        // window this narrow the last item is almost never materialized, so the extent is mostly
        // that guess, and the far-edge anchoring of the ScrollViewer follows every change of it
        // (see PORTING.md §6: 1272 -> 7778.5 px in one arrange, and the offset with it).
        //
        // So: widen the window. viewport * (1 + 4) = 3120 px here, which is the whole of a short
        // history and a dozen lines of a long one, so the average is taken over enough rows to
        // mean something and the last item of a short history stays materialized - which makes the
        // extent a measurement rather than an extrapolation, and takes the anchoring with it.
        // CacheLength is a public DP that ItemsStackPanel propagates to its layout with
        // BindToEquivalentProperty, so assigning it after the panel exists is enough.
        //
        // It has to be assigned from a layout pass and not from OnLoaded: Uno creates
        // ItemsPanelRoot in the first measure, which is after Loaded (PORTING.md §6).
        private const double CacheWindow = 4;

        private void WidenTheFillWindow()
        {
            if (ItemsPanelRoot is ItemsStackPanel panel && panel.CacheLength != CacheWindow)
            {
                panel.CacheLength = CacheWindow;

                Logger.Debug(string.Format(CultureInfo.InvariantCulture,
                    "Widened the fill window to viewport x {0:F1}", 1 + CacheWindow));
            }
        }

        #endregion

        #region Ask for one more measure when a layout pass materialized nothing

        // The empty history, and why it never comes back.
        //
        // Measured on 2026-08-26, forum -1002346786357, with the probe reading the inside of
        // VirtualizingPanelLayout by reflection. A page of older messages arrives as one batch of
        // inserts at index 0 while the content is still shorter than the viewport, so
        // ScrollableHeight is 0 and Uno's IsScrolledToEnd() (VerticalOffset >= ScrollableHeight)
        // is trivially true. In the measure that follows, ApplyCollectionChanges moves the dynamic
        // seed forward by (inserted items x _averageLineHeight) and then ApplyScrollAdjustment
        // takes its IsScrolledToEnd() branch: it neither scrolls the ScrollViewer nor records the
        // adjustment. FillForward's one and only guard is
        //
        //     GetItemsEnd() < ExtendedViewportEnd + extentAdjustment
        //
        // and with no line materialized GetItemsEnd() is the seed start itself. Measured on the
        // failing pass: 24 items inserted, _averageLineHeight 235.5, so the seed sat at 5652 while
        // the fill window ended at 936 and the adjustment was 0. The loop never ran once;
        // FillBackward cannot start from nothing (it returns at once when there is no first
        // materialized line), so the pass ended with zero lines. Zero lines makes
        // EstimatePanelExtent return a literal 0, so the panel measures 0 px tall, the ScrollViewer
        // collapses its extent to the viewport, ScrollableHeight stays 0 - and from there nothing
        // is left that could invalidate the measure: no ViewChanged, no SizeChanged, no further
        // collection change. That is the "and it never comes back".
        //
        // The pass that would fix it is the very next one: UpdateLayout ends with
        // SetDynamicSeed(null, null), so a measure with no pending collection changes re-seeds from
        // scratch and fills forward from the first item. Proved with UNIGRAM_HISTORY_POKE: a bare
        // InvalidateMeasure on the collapsed panel refilled the history within 200 ms, twice in the
        // same session (items 41, extent 4256, 4 containers realized).
        //
        // So that is what this does: after any layout pass that leaves the history with messages
        // and a panel measuring nothing, ask for one more measure. The counter is what keeps it
        // honest - it resets as soon as the panel measures a height, and it stops after
        // MaxEmptyLayouts consecutive tries, so a panel that is empty for some other reason costs
        // a bounded number of measures instead of a spin.
        private const int MaxEmptyLayouts = 32;

        private int _emptyLayouts;
        private int _emptyLayoutsItems = -1;
        private double _emptyLayoutsViewport = -1;
        private bool _emptyLayoutsAttached;
        private bool _emptyLayoutsGaveUp;

        private void AttachEmptyLayoutGuard()
        {
            if (_emptyLayoutsAttached)
            {
                return;
            }

            // The ListView's own LayoutUpdated, not the panel's: Uno creates ItemsPanelRoot on the
            // first measure, which is after Connected, and the panel is replaced when the template
            // is reapplied. This one exists for the whole life of the view.
            _emptyLayoutsAttached = true;
            LayoutUpdated += OnEmptyLayoutUpdated;
            LayoutUpdated += OnBottomPinLayoutUpdated;
        }

        private void OnEmptyLayoutUpdated(object sender, object e)
        {
            WidenTheFillWindow();

            if (ItemsPanelRoot is not ItemsStackPanel panel
                || ScrollingHost == null
                || ScrollingHost.ViewportHeight <= 0
                || Items.Count == 0)
            {
                ResetEmptyLayouts();
                return;
            }

            if (panel.DesiredSize.Height > 0)
            {
                ResetEmptyLayouts();
                return;
            }

            // The cap counts consecutive tries at ONE situation, and anything that changes what
            // the next fill pass would do - a message arriving or leaving, the viewport resizing -
            // rearms it. Without this the counter is a lifetime total, and that puts back the half
            // of the bug this whole region exists to remove: LayoutUpdated is raised once per
            // layout pass of the WHOLE window, so a chat with an animated sticker in it, or the
            // container churn of the second defect, burns all 32 in well under a second, and from
            // there the guard is disarmed for as long as the panel stays collapsed - which is
            // exactly the state it is here to escape. Rearmed, the cap means what its name says:
            // an empty panel costs a bounded number of measures per situation, not a spin.
            if (Items.Count != _emptyLayoutsItems || ScrollingHost.ViewportHeight != _emptyLayoutsViewport)
            {
                _emptyLayouts = 0;
                _emptyLayoutsItems = Items.Count;
                _emptyLayoutsViewport = ScrollingHost.ViewportHeight;
                _emptyLayoutsGaveUp = false;
            }

            if (_emptyLayouts >= MaxEmptyLayouts)
            {
                ReportEmptyLayoutGaveUp(panel);
                return;
            }

            _emptyLayouts++;

            Logger.Warning(string.Format(CultureInfo.InvariantCulture,
                "The history measured 0 px with {0} messages: asking the panel to measure again ({1}/{2})",
                Items.Count, _emptyLayouts, MaxEmptyLayouts));

            panel.InvalidateMeasure();
        }

        private void ResetEmptyLayouts()
        {
            _emptyLayouts = 0;
            _emptyLayoutsItems = -1;
            _emptyLayoutsViewport = -1;
            _emptyLayoutsGaveUp = false;
        }

        // u-079: what reaching the cap costs, and why it now says so out loud.
        //
        // Reaching MaxEmptyLayouts is this guard giving up. The retry stops, nothing else is left
        // to invalidate the measure, and the history stays empty for as long as the situation
        // lasts - which is exactly the symptom the card for this was written from: a chat, and a
        // forum topic in particular, that "opens empty". Until now that terminal state was
        // indistinguishable in a log from the healthy case, because both write the same
        // "measured 0 px ... (n/32)" line and the give-up added nothing after it.
        //
        // Telling the two apart is the whole of what was left to do here, because the card's own
        // evidence did not survive checking:
        //   - "self-retries forever": across the ten session logs archived for this port the guard
        //     fires exactly ONCE per session and always as (1/32) - the panel fills on the first
        //     retry every time. No log reaches 2/32, let alone 32/32. The retry in those logs is
        //     this guard working, not a spin.
        //   - "44 messages at negative y": not a fault signature. A container scrolled above the
        //     viewport of an ItemsStackPanel sits at negative panel-relative y by construction, and
        //     61 of the 62 archived tree dumps of healthy, painted histories have them - 5 of those
        //     have every single item at negative y.
        //
        // So the mechanism this region was built for is still covered by it, and the useful thing
        // to leave behind is not another retry but the numbers that would identify a surviving
        // variant if one ever does reach the cap: they exist on this pass and are gone by the time
        // anyone could be asked for them.
        //
        // Once per situation, not per pass. LayoutUpdated is raised once per layout pass of the
        // WHOLE window and, once capped, this branch is taken on every one of them, so a bare log
        // here would write thousands of identical lines and bury itself. The latch is cleared by
        // the same re-arm that resets the counter, so a later situation reports again.
        private void ReportEmptyLayoutGaveUp(ItemsStackPanel panel)
        {
            if (_emptyLayoutsGaveUp)
            {
                return;
            }

            _emptyLayoutsGaveUp = true;

            var children = panel.Children.Count;
            var firstY = double.NaN;
            var lastY = double.NaN;

            if (children > 0)
            {
                try
                {
                    // Read against this view, the panel's own scrolling parent, so the number means
                    // "where the row was arranged relative to the history" - the measurement the
                    // card asserted rather than took.
                    if (panel.Children[0] is UIElement first)
                    {
                        firstY = first.TransformToVisual(this).TransformPoint(new Point()).Y;
                    }

                    if (panel.Children[children - 1] is UIElement last)
                    {
                        lastY = last.TransformToVisual(this).TransformPoint(new Point()).Y;
                    }
                }
                catch (Exception ex)
                {
                    // Diagnostics must not be able to break the thing they are measuring. This runs
                    // inside a LayoutUpdated handler, so an exception escaping here would take the
                    // layout pass with it - a worse failure than the empty history being reported.
                    Logger.Error("The history gave up, and the row positions could not be read: " + ex.Message);
                }
            }

            // Visible indices only. ItemsStackPanel.FirstCacheIndex/LastCacheIndex are a bare throw
            // in the deployed Uno (PORTING.md 6; ProfilePage.xaml.cs works around that same getter),
            // so reading them here - inside a layout handler, to describe a fault - would raise
            // NotImplementedException on the one pass that most needs to survive. FirstVisibleIndex
            // and LastVisibleIndex are [NotImplemented] by attribute but have real bodies and answer
            // correctly, which is why the prepend anchor above already relies on them.
            Logger.Error(string.Format(CultureInfo.InvariantCulture,
                "The history gave up after {0} measures and is still 0 px: items={1} children={2} " +
                "firstRowY={3:F1} lastRowY={4:F1} panelActual={5:F1} firstVisible={6} lastVisible={7} " +
                "offset={8:F1} extent={9:F1} viewport={10:F1} scrollable={11:F1} cacheLength={12:F1} mode={13}",
                MaxEmptyLayouts, Items.Count, children, firstY, lastY, panel.ActualHeight,
                panel.FirstVisibleIndex, panel.LastVisibleIndex,
                ScrollingHost.VerticalOffset, ScrollingHost.ExtentHeight,
                ScrollingHost.ViewportHeight, ScrollingHost.ScrollableHeight,
                panel.CacheLength, _currentMode));
        }

        #endregion
#endif

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Logger.Info($"ItemsPanelRoot.Children.Count: {ItemsPanelRoot?.Children.Count}");
            Logger.Info($"Items.Count: {Items.Count}");

            if (ItemsPanelRoot != null)
            {
                ItemsPanelRoot.SizeChanged -= OnSizeChanged;
#if LINUX
                ItemsPanelRoot.LayoutUpdated -= OnKeepLayoutUpdated;
#endif
            }

#if LINUX
            if (ItemsSource is INotifyCollectionChanged observable)
            {
                observable.CollectionChanged -= OnSourceCollectionChanged;
            }

            _keepPending = false;
            _keepItem = null;

            _pinExtent = -1;
            _pinOffset = -1;
            _pinnedLast = false;
            _pinSteps = 0;

            ResetEmptyLayouts();
#endif

            _waitItemsPanelRoot.TrySetResult(false);
            _waitItemsPanelRoot = new();

            _raiseViewChanged = false;
        }

        public void Disconnect()
        {
            // Note, this is done because of the following:
            // In some conditions (always?) ListView starts to store
            // all the created containers in the ItemsPanelRoot (on Unload presumably).
            // This causes an enormous overhead when moving from a different page to ChatPage,
            // as all the SelectorItem (some times they can be hundreds) will be measured arranged
            // right before all of them get unloaded again.
            // Setting ItemsSource to null seems to prevent this from happening.
            // IMPORTANT: this must only happen on Unload (so when closing the chat page).
            if (ItemsSource is ISynchronizedList source)
            {
                ItemsSource = null;
                source.Disconnect();
            }
        }

        protected override void OnApplyTemplate()
        {
            // TODO: Name
            ScrollingHost = (ScrollViewer)GetTemplateChild("ScrollViewer");

            // Used by saved messages tab
            ScrollingHost ??= this.GetParent<ScrollViewer>();
#if !LINUX
            ScrollingHost.ViewChanging += OnViewChanging;
#endif
            ScrollingHost.ViewChanged += OnViewChanged;
#if !LINUX
            ScrollingHost.DirectManipulationStarted += OnDirectManipulationStarted;
            ScrollingHost.DirectManipulationCompleted += OnDirectManipulationCompleted;
#endif
            ScrollingHost.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);

#if LINUX
            // Not 1 for KeepLastItemInView any more: see "Hold the far edge ourselves" above. NaN
            // turns Uno's anchoring off, and the far edge is held from OnBottomPinLayoutUpdated in
            // steps its seed arithmetic cannot turn into a row that does not exist.
            ScrollingHost.VerticalAnchorRatio = _currentMode == ItemsUpdatingScrollMode.KeepLastItemInView ? double.NaN : 0;

            ScrollingPropertySet = BootStrapper.Current.Compositor.CreatePropertySet();
            ScrollingPropertySet.InsertVector3("Translation", new System.Numerics.Vector3(0, -(float)ScrollingHost.VerticalOffset, 0));
#else
            ScrollingPropertySet = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(ScrollingHost);
#endif

            base.OnApplyTemplate();
        }

        private void OnDirectManipulationStarted(object sender, object e)
        {
            HasBeenScrolled = true;

            // TODO: only start timer if close to bottom
            if (ViewModel.PendingSponsoredMessage != null)
            {
                _scrollTracker.Start();
            }
        }

        private void OnDirectManipulationCompleted(object sender, object e)
        {
            _scrollTracker.Stop();
        }

        private void OnTick(object sender, object e)
        {
            var message = ViewModel.PendingSponsoredMessage;
            if (message == null)
            {
                _scrollTracker.Stop();
                return;
            }

            var offset = GetOverscrollOffset();
            if (offset < -1)
            {
                _scrollTracker.Stop();
                SetScrollingMode(ItemsUpdatingScrollMode.KeepItemsInView, true);

                ViewModel.PendingSponsoredMessage = null;
#if LINUX
                ViewModel.InsertMessageInOrder(ViewModel.CreateMessage(new Message(message.MessageId, null, null, ViewModel.ChatId, null, null, false, false, false, false, false, true, false, false, false, false, 0, 0, null, null, null, null, null, null, null, null, null, 0, 0, 0, null, 0, 0, string.Empty, 0, string.Empty, 0, 0, null, string.Empty, new MessageSponsored(message), null, 0)));
#else
                ViewModel.InsertMessageInOrder(ViewModel.CreateMessage(new Message(message.MessageId, null, null, ViewModel.ChatId, null, null, false, false, false, false, false, true, false, false, false, false, 0, 0, null, null, null, null, null, null, null, null, null, 0, 0, 0, null, 0, 0, string.Empty, 0, string.Empty, 0, 0, null, string.Empty, new MessageSponsored(message), null, null)));
#endif
            }
        }

        private float GetOverscrollOffset()
        {
            if (ScrollingHost.VerticalOffset < ScrollingHost.ScrollableHeight || ViewModel.IsNewestSliceLoaded is not true)
            {
                return 1;
            }

            var itemsPanel = ItemsPanelRoot;
            if (itemsPanel != null)
            {
                var transform = itemsPanel.TransformToVisual(this);
                var point = transform.TransformVector2();

                return point.Y + itemsPanel.ActualSize.Y - ActualSize.Y;
            }

            return 1;
        }


        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            HasBeenScrolled = true;

            var modifiers = WindowContext.KeyModifiers();
            if (modifiers == VirtualKeyModifiers.Control)
            {
                try
                {
#if LINUX
                    var point = e.GetCurrentPoint(ScrollingHost);
                    if (point.Properties.MouseWheelDelta < 0)
                    {
                        ScrollingHost.ChangeView(null, ScrollingHost.VerticalOffset + ScrollingHost.ViewportHeight, null);
                    }
                    else
                    {
                        ScrollingHost.ChangeView(null, ScrollingHost.VerticalOffset - ScrollingHost.ViewportHeight, null);
                    }
#else
                    var presenter = ScrollingHost.GetChild<ScrollContentPresenter>();

                    var point = e.GetCurrentPoint(ScrollingHost);
                    if (point.Properties.MouseWheelDelta < 0)
                    {
                        presenter.PageDown();
                    }
                    else
                    {
                        presenter.PageUp();
                    }
#endif
                }
                catch
                {
                    // All the remote procedure calls must be wrapped in a try-catch block
                }
            }

            var message = ViewModel.PendingSponsoredMessage;
            if (message != null && ViewModel.IsNewestSliceLoaded is true && ScrollingHost.VerticalOffset.AlmostEquals(ScrollingHost.ScrollableHeight, 1e-02))
            {
                var point = e.GetCurrentPoint(ScrollingHost);
                if (point.Properties.MouseWheelDelta < 0)
                {
                    SetScrollingMode(ItemsUpdatingScrollMode.KeepItemsInView, true);

                    ViewModel.PendingSponsoredMessage = null;
#if LINUX
                    ViewModel.InsertMessageInOrder(ViewModel.CreateMessage(new Message(message.MessageId, null, null, ViewModel.ChatId, null, null, false, false, false, false, false, true, false, false, false, false, 0, 0, null, null, null, null, null, null, null, null, null, 0, 0, 0, null, 0, 0, string.Empty, 0, string.Empty, 0, 0, null, string.Empty, new MessageSponsored(message), null, 0)));
#else
                    ViewModel.InsertMessageInOrder(ViewModel.CreateMessage(new Message(message.MessageId, null, null, ViewModel.ChatId, null, null, false, false, false, false, false, true, false, false, false, false, 0, 0, null, null, null, null, null, null, null, null, null, 0, 0, 0, null, 0, 0, string.Empty, 0, string.Empty, 0, 0, null, string.Empty, new MessageSponsored(message), null, null)));
#endif
                }
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ScrollingHost?.ScrollableHeight < ScrollingHost?.ViewportHeight)
            {
                ViewChanging();
            }
        }

        private void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
#if LINUX
            // No ViewChanging nor DirectManipulation* on Uno Skia: the direction, the
            // "scrolled by the user" flag and the sponsored tracker come from the offset delta.
            var offset = ScrollingHost.VerticalOffset;
            if (offset != _lastVerticalOffset)
            {
                ScrollingPropertySet?.InsertVector3("Translation", new System.Numerics.Vector3(0, -(float)offset, 0));

                if (_raiseViewChanged)
                {
                    HasBeenScrolled = true;
                }

                ViewChanging(offset < _lastVerticalOffset
                    ? PanelScrollingDirection.Backward
                    : PanelScrollingDirection.Forward);

                _lastVerticalOffset = offset;
            }

            if (e.IsIntermediate)
            {
                if (!_scrollTracker.IsEnabled && ViewModel?.PendingSponsoredMessage != null)
                {
                    _scrollTracker.Start();
                }
            }
            else
            {
                _scrollTracker.Stop();
            }
#endif

            if (_raiseViewChanged)
            {
                ViewChanged?.Invoke(sender, e);
            }

            if (e.IsIntermediate)
            {
                return;
            }

            ScrollingDirection = PanelScrollingDirection.None;
        }

#if !LINUX
        private void OnViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
        {
            var finalOffset = e.FinalView.VerticalOffset;
            var nextOffset = e.NextView.VerticalOffset;

            if (finalOffset == nextOffset && !e.IsInertial)
            {
                nextOffset = ScrollingHost.VerticalOffset;
            }

            ViewChanging(e.FinalView.VerticalOffset != nextOffset ?
                finalOffset < nextOffset
                ? PanelScrollingDirection.Backward
                : PanelScrollingDirection.Forward
                : PanelScrollingDirection.None);
        }
#endif

        public void ViewChanging(PanelScrollingDirection direction = PanelScrollingDirection.None)
        {
            ScrollingDirection = direction;

            if (ScrollingHost == null || ItemsPanelRoot is not ItemsStackPanel panel || ViewModel == null || IsDisconnected)
            {
                return;
            }

            var lastSlice = ViewModel.IsSavedMessagesTab ? ViewModel.IsNewestSliceLoaded != true : ViewModel.IsOldestSliceLoaded != true;
            var firstSlice = ViewModel.IsSavedMessagesTab ? ViewModel.IsOldestSliceLoaded != true : ViewModel.IsNewestSliceLoaded != true;

#if LINUX
            if (direction != PanelScrollingDirection.Backward && panel.LastVisibleIndex == ViewModel.Items.Count - 1)
#else
            if (direction != PanelScrollingDirection.Backward && panel.LastCacheIndex == ViewModel.Items.Count - 1)
#endif
            {
                LoadPreviousSlice(direction, firstSlice);
            }
        }

        private void LoadPreviousSlice(PanelScrollingDirection direction, bool firstSlice)
        {
            if (firstSlice)
            {
                return;
            }

            SetScrollingMode(ItemsUpdatingScrollMode.KeepLastItemInView, true);
        }

        private ItemsUpdatingScrollMode _currentMode;
        private ItemsUpdatingScrollMode? _pendingMode;
        private bool? _pendingForce;

        public void SetScrollingMode()
        {
            if (_pendingMode is ItemsUpdatingScrollMode mode && _pendingForce is bool force)
            {
                _pendingMode = null;
                _pendingForce = null;

                SetScrollingMode(mode, force);
            }
        }

        public void SetScrollingMode(ItemsUpdatingScrollMode mode, bool force)
        {
            var panel = ItemsPanelRoot as ItemsStackPanel;
            var scroll = ScrollingHost;

            if (panel == null || scroll == null)
            {
                _pendingMode = mode;
                _pendingForce = force;

                return;
            }

            if (_currentMode == _pendingMode)
            {
                _pendingMode = null;
                _pendingForce = null;
                return;
            }

            if (ViewModel.IsSavedMessagesTab)
            {
                mode = mode == ItemsUpdatingScrollMode.KeepLastItemInView
                    ? ItemsUpdatingScrollMode.KeepItemsInView
                    : ItemsUpdatingScrollMode.KeepLastItemInView;
            }

            if (mode == ItemsUpdatingScrollMode.KeepItemsInView && (force || scroll.VerticalOffset < 200))
            {
#if LINUX
                if (_currentMode != mode)
                {
                    Logger.Debug("Changed scrolling mode to KeepItemsInView");
                    _currentMode = ItemsUpdatingScrollMode.KeepItemsInView;

                    scroll.VerticalAnchorRatio = 0;
                    _pinnedLast = false;
                }
#else
                if (panel.ItemsUpdatingScrollMode != mode)
                {
                    Logger.Debug("Changed scrolling mode to KeepItemsInView");
                    panel.ItemsUpdatingScrollMode = _currentMode = ItemsUpdatingScrollMode.KeepItemsInView;
                }
#endif
            }
            else if (mode == ItemsUpdatingScrollMode.KeepLastItemInView && (force || scroll.ScrollableHeight - scroll.VerticalOffset < 200))
            {
#if LINUX
                if (_currentMode != mode)
                {
                    Logger.Debug("Changed scrolling mode to KeepLastItemInView");
                    _currentMode = ItemsUpdatingScrollMode.KeepLastItemInView;

                    // Uno's far-edge anchoring (1.0) is off: it added to the offset whatever the
                    // extent grew by without asking whether that extent was measured or guessed,
                    // and an extrapolated extent turned into an offset outside the list. The far
                    // edge is held by OnBottomPinLayoutUpdated instead.
                    scroll.VerticalAnchorRatio = double.NaN;
                    _pinnedLast = false;
                    _pinSteps = 0;
                }
#else
                if (panel.ItemsUpdatingScrollMode != mode)
                {
                    Logger.Debug("Changed scrolling mode to KeepLastItemInView");
                    panel.ItemsUpdatingScrollMode = _currentMode = ItemsUpdatingScrollMode.KeepLastItemInView;
                }
#endif
            }
        }

        public async void ScrollToItem(MessageViewModel item, VerticalAlignment alignment, MessageBubbleHighlightOptions options, double? pixel = null, ScrollIntoViewAlignment direction = ScrollIntoViewAlignment.Leading, bool? disableAnimation = null, TaskCompletionSource<bool> tsc = null)
        {
            Suspend();

#if LINUX
            // A jump to a reply, a search result or the first unread one is the view being put
            // somewhere on purpose: the pin lets go before the first ChangeView, not after the
            // layout pass it causes.
            _pinnedLast = false;
#endif

            var scrollViewer = ScrollingHost;
            var handler = Delegate;

            if (scrollViewer == null || handler == null)
            {
                Logger.Debug("ScrollingHost == null");
                goto Exit;
            }

            await ScrollIntoViewAsync(item, direction, true);

            var selectorItem = handler.ContainerFromItem(item.Id);
            if (selectorItem == null)
            {
                // TODO: experimental
                if (ViewModel.Items.ContainsKey(item.Id))
                {
                    Logger.Debug("selectorItem == null, but item is known, retry");

                    await ScrollIntoViewAsync(item, direction, false);
                    selectorItem = handler.ContainerFromItem(item.Id);
                }

                if (selectorItem == null)
                {
                    Logger.Debug("selectorItem == null, abort");
                    goto Exit;
                }
            }

            // calculate the position object in order to know how much to scroll to
            var transform = selectorItem.TransformToVisual(scrollViewer.ContentRoot());
            var position = transform.TransformPoint(new Point());

            if (alignment == VerticalAlignment.Top)
            {
                if (pixel is double adjust)
                {
                    position.Y -= adjust;
                }
            }
            else if (alignment == VerticalAlignment.Center)
            {
                Rect GetHighlightArea()
                {
                    if (options != null && options.Highlight)
                    {
                        if (selectorItem.ContentRoot() is MessageSelector selector && selector.Content is MessageBubble bubble)
                        {
                            return bubble.Highlight(options);
                        }
                    }

                    return new Rect(0, 0, selectorItem.ActualWidth, selectorItem.ActualHeight);
                }

                var occludedHeight = Delegate.AnimatedHeight;
                var highlightArea = GetHighlightArea();

                if (highlightArea.Height < ActualHeight - occludedHeight)
                {
                    position.Y -= (ActualHeight / 2 - (highlightArea.Bottom - highlightArea.Height / 2)) + occludedHeight / 2;

                    if (Delegate.HasMessagesPadding)
                    {
                        position.Y += occludedHeight;
                    }
                }
                else
                {
                    position.Y -= occludedHeight;
                }
            }
            else if (alignment == VerticalAlignment.Bottom)
            {
                position.Y -= ActualHeight - selectorItem.ActualHeight;

                if (pixel is double adjust)
                {
                    position.Y += adjust;
                }
            }

            if (scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight || position.Y < scrollViewer.ScrollableHeight)
            {
                if (scrollViewer.VerticalOffset.AlmostEquals(position.Y))
                {
                    TryFocus(selectorItem, options);

                    goto Exit;
                }

                await scrollViewer.ChangeViewAsync(null, position.Y, disableAnimation ?? alignment != VerticalAlignment.Center, false);
            }

            TryFocus(selectorItem, options);

        Exit:
            Resume();

#if LINUX
            // The pin was let go on the way in so it would not fight the jump. Now that the jump
            // has landed, take it back IF the place we were asked for was the bottom: the jump
            // above aims at a position computed from an extrapolated layout, so it routinely stops
            // short, and the disarmed pin has no way to notice. Any other target - a reply, a
            // search result, the first unread one - is left exactly where it was put.
            if (alignment == VerticalAlignment.Bottom && Items.IndexOf(item) == Items.Count - 1)
            {
                ArmBottomPin();
            }
#endif

            if (scrollViewer != null)
            {
                ViewChanging();
                ViewChanged?.Invoke(scrollViewer, null);
            }

            tsc?.TrySetResult(true);
        }

        private void TryFocus(SelectorItem selectorItem, MessageBubbleHighlightOptions options)
        {
            try
            {
                if ((options == null || options.MoveFocus) && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                {
                    selectorItem.Focus(FocusState.Keyboard);
                }
            }
            catch
            {
                // Focus cannot be moved while getting or losing focus.
            }
        }

        private async Task ScrollIntoViewAsync(MessageViewModel item, ScrollIntoViewAlignment alignment, bool fastPath)
        {
            if (ItemsPanelRoot == null)
            {
                // Some actions cause IsItemsHostInvalid to become true.
                // If this is the case, ItemsPanelRoot will return null, and scrolling may not work.
                // The current code should not invalidate the ItemsHost, but if this happens, we try to be prepared.
                if (_waitItemsPanelRoot.Task.Status == TaskStatus.RanToCompletion)
                {
                    Logger.Info("ItemsPanelRoot == null, UpdateLayout");
                    ScrollingHost.UpdateLayout();
                }
                else
                {
                    Logger.Info("ItemsPanelRoot == null, Await");
                    await _waitItemsPanelRoot.Task;
                }
            }

            var index = Items.IndexOf(item);
            var panel = ItemsPanelRoot as ItemsStackPanel;

#if LINUX
            if (panel == null || ContainerFromIndex(index) != null)
#else
            if (panel == null || index >= panel.FirstCacheIndex && index <= panel.LastCacheIndex)
#endif
            {
                Logger.Info("Skipping because " + (panel == null ? "null" : "cached"));
                return;
            }

            // Judging from WinUI 3 source code, calling UpdateLayout on the panel should be
            // enough to guarantee that the container we are scrolling to gets realized 
            // 1.4-stable/dxaml/xcp/dxaml/lib/ModernCollectionBasePanel_WindowManagement_Partial.cpp#L2138
            if (fastPath)
            {
                ScrollIntoView(item, alignment);
                panel.UpdateLayout();

                //if (index < panel.FirstCacheIndex || index > panel.LastCacheIndex)
                //{
                //    panel.UpdateLayout();
                //}
                //else
                //{
                //    Logger.Info("Item is in the cached range");
                //}

                return;
            }

            var tcs = new TaskCompletionSource<object>();

            void layoutUpdated(object s1, object e1)
            {
                tcs.TrySetResult(null);
            }

            void viewChanged(object s1, ScrollViewerViewChangedEventArgs e1)
            {
                panel.LayoutUpdated -= layoutUpdated;

                if (e1.IsIntermediate is false)
                {
                    panel.LayoutUpdated += layoutUpdated;
                    ScrollingHost.ViewChanged -= viewChanged;
                }
            }

            try
            {
                ScrollIntoView(item, alignment);
                panel.LayoutUpdated += layoutUpdated;
                ScrollingHost.ViewChanged += viewChanged;

                await tcs.Task;
            }
            finally
            {
                panel.LayoutUpdated -= layoutUpdated;
                ScrollingHost.ViewChanged -= viewChanged;
            }
        }

        #region Selection

        public bool IsSelectionEnabled
        {
            get { return (bool)GetValue(IsSelectionEnabledProperty); }
            set { SetValue(IsSelectionEnabledProperty, value); }
        }

        public static readonly DependencyProperty IsSelectionEnabledProperty =
            DependencyProperty.Register("IsSelectionEnabled", typeof(bool), typeof(ChatHistoryView), new PropertyMetadata(false, OnSelectionEnabledChanged));

        private static void OnSelectionEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ChatHistoryView)d).OnSelectionEnabledChanged((bool)e.OldValue, (bool)e.NewValue);
        }

        private void OnSelectionEnabledChanged(bool oldValue, bool newValue)
        {
            var panel = ItemsPanelRoot as ItemsStackPanel;
            if (panel == null)
            {
                return;
            }

#if LINUX
            for (int i = panel.FirstVisibleIndex; i <= panel.LastVisibleIndex; i++)
#else
            for (int i = panel.FirstCacheIndex; i <= panel.LastCacheIndex; i++)
#endif
            {
                var container = ContainerFromIndex(i) as SelectorItem;
                if (container == null)
                {
                    continue;
                }

                var content = container.ContentRoot() as MessageSelector;
                content?.UpdateSelectionEnabled(newValue, true);
            }
        }

        private MessageViewModel _firstItem;
        private MessageViewModel _lastItem;
        private bool _operation;
        private SelectionDirection _direction;

        private bool _pressed;
        private Point _position;

        internal void OnDoubleTapped(MessageViewModel message, DoubleTappedRoutedEventArgs e)
        {
            _pressed = false;

            if (message != null && !ViewModel.IsSelectionEnabled)
            {
                e.Handled = true;
                ViewModel.DoubleTapped(message, WindowContext.IsKeyDown(VirtualKey.Control));
            }
        }

        internal void OnPointerPressed(MessageSelector item, PointerRoutedEventArgs e)
        {
            _pressed = true;
        }

        internal void OnPointerEntered(MessageSelector item, PointerRoutedEventArgs e)
        {
            if (_firstItem == null || !_pressed || !e.Pointer.IsInContact /*|| SelectionMode != ListViewSelectionMode.Multiple*/ || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            {
                return;
            }

            var point = e.GetCurrentPoint(item);
            if (!point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;

            if (IsSelectionEnabled is false)
            {
                IsSelectionEnabled = true;
            }

            var message = item.Message;
            if (message == null)
            {
                return;
            }

            if (_direction == SelectionDirection.None)
            {
                _direction = message.Id > _firstItem.Id
                    ? SelectionDirection.Down
                    : SelectionDirection.Up;
            }

            var direction = message.Id > _lastItem.Id
                ? SelectionDirection.Down
                : SelectionDirection.Up;

            if (direction != SelectionDirection.None)
            {
                var begin = Items.IndexOf(_firstItem);
                if (begin < 0)
                {
                    return;
                }

                var index = Items.IndexOf(message);
                var first = Math.Min(begin, index);
                var last = Math.Max(begin, index);

                for (int i = first; i <= last; i++)
                {
                    var current = Items[i] as MessageViewModel;

                    if (_operation)
                    {
                        ViewModel.Select(current);
                    }
                    else if (!_operation)
                    {
                        ViewModel.Unselect(current);
                    }
                }

                if (direction != _direction)
                {
                    if (_operation)
                    {
                        ViewModel.Unselect(_lastItem);
                    }
                    else if (!_operation)
                    {
                        ViewModel.Select(_lastItem);
                    }
                }
            }

            _lastItem = message;
        }

        internal void OnPointerMoved(MessageSelector item, PointerRoutedEventArgs e)
        {
            if (!_pressed || !e.Pointer.IsInContact || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
            {
                return;
            }

            var message = item.Message;
            if (message == null)
            {
                return;
            }

            if (_firstItem != null && _firstItem != message)
            {
                return;
            }

            var point = e.GetCurrentPoint(item);
            if (!point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;

            if (_firstItem == null)
            {
                _firstItem = _lastItem = message;
                _operation = !ViewModel.SelectedItems.ContainsKey(message.Id);

                _position = point.Position;
            }
            else if (_firstItem == message)
            {
                var contains = ViewModel.SelectedItems.ContainsKey(message.Id);

                var delta = Math.Abs(point.Position.Y - _position.Y);
                if (delta > 10)
                {
                    if (_operation && !contains)
                    {
                        ViewModel.Select(message);
                    }
                    else if (!_operation && contains)
                    {
                        ViewModel.Unselect(message);
                    }

                    IsSelectionEnabled = true;
                    item.ReleasePointerCapture(e.Pointer);
                }
                else
                {
                    if (_operation && contains)
                    {
                        ViewModel.Unselect(message);
                    }
                    else if (!_operation && !contains)
                    {
                        ViewModel.Select(message);
                    }

                    _direction = SelectionDirection.None;
                    _lastItem = message;
                }
            }
        }

        internal void OnPointerReleased(MessageSelector item, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(XamlRoot.Content);
            var handled = _firstItem != null && ViewModel.SelectedItems.ContainsKey(_firstItem.Id) == _operation;

            _firstItem = null;
            _lastItem = null;

            _pressed = false;
            _position = new Point();

            if (IsSelectionEnabled is false)
            {
                return;
            }

            if (ViewModel.SelectedItems.Count < 1 && ViewModel.IsReportingMessages == null)
            {
                IsSelectionEnabled = false;
            }

            e.Handled = handled;
        }

        enum SelectionDirection
        {
            None,
            Up,
            Down,
        }

        #endregion
    }

    public class BidirectionalIncrementalLoader
    {
        private readonly ChatHistoryView _listView;
        private ScrollViewer _scrollViewer;
        private ItemsStackPanel _itemsPanel;

        private int _activeLoadOperations;
        private CancellationTokenSource _cts;

        private bool _isMonitoring;
        private bool _isInitialized;

        private readonly double _baseTopTriggerThreshold;
        private readonly double _baseBottomTriggerThreshold;
        private readonly double _minThresholdMultiplier;
        private readonly double _maxThresholdMultiplier;
        private readonly TimeSpan _checkInterval;
        private DateTime _lastCheckTime = DateTime.MinValue;

        private int _consecutiveSizeChangedChecks;
        private const int MaxConsecutiveSizeChangedChecks = 20;
        private double _lastPanelHeight;

        private DialogViewModel _viewModel;

        public BidirectionalIncrementalLoader(
            ChatHistoryView listView,
            double baseTopTriggerThreshold = 800.0,
            double baseBottomTriggerThreshold = 800.0,
            double minThresholdMultiplier = 0.5,
            double maxThresholdMultiplier = 2.0,
            TimeSpan? checkInterval = null)
        {
            _listView = listView ?? throw new ArgumentNullException(nameof(listView));

            _baseTopTriggerThreshold = baseTopTriggerThreshold;
            _baseBottomTriggerThreshold = baseBottomTriggerThreshold;
            _minThresholdMultiplier = minThresholdMultiplier;
            _maxThresholdMultiplier = maxThresholdMultiplier;
            _checkInterval = checkInterval ?? TimeSpan.FromMilliseconds(150);

            _listView.Loaded += OnListViewLoaded;
            _listView.Unloaded += OnListViewUnloaded;
        }

        public void Initialize(DialogViewModel viewModel)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            if (_viewModel != null)
            {
                _viewModel.MessagesLoaded -= OnMessagesLoaded;
            }

            _isInitialized = false;
            _activeLoadOperations = 0;
            _consecutiveSizeChangedChecks = 0;

            _viewModel = viewModel;
            _viewModel.MessagesLoaded += OnMessagesLoaded;
        }

        private void OnMessagesLoaded(object sender, MessagesLoadedEventArgs e)
        {
            if (e.Direction == PanelScrollingDirection.None)
            {
                _isInitialized = true;

                if (_isMonitoring && _scrollViewer != null)
                {
                    CheckNonScrollableState();
                }
            }
            else if (e.Direction == PanelScrollingDirection.Backward)
            {
                //_listView.SetScrollingMode(ItemsUpdatingScrollMode.KeepItemsInView, force: false);
            }
            else if (e.Direction == PanelScrollingDirection.Forward)
            {
                //_listView.SetScrollingMode(ItemsUpdatingScrollMode.KeepLastItemInView, force: false);
            }
        }

        private void OnListViewLoaded(object sender, RoutedEventArgs e)
        {
            _scrollViewer = _listView.ScrollingHost;
            if (_scrollViewer == null)
            {
                return;
            }

            _itemsPanel = _listView.ItemsPanelRoot as ItemsStackPanel;
            if (_itemsPanel == null)
            {
#if LINUX
                // Uno builds the items panel on the first measure, which happens after Loaded, so
                // the loader gave up here once and for all: the history never asked for older
                // messages, scrolling to the very top simply stopped with IsOldestSliceLoaded
                // still false. Wait for the panel instead of giving up.
                _listView.LayoutUpdated -= OnListViewLayoutUpdated;
                _listView.LayoutUpdated += OnListViewLayoutUpdated;
#endif
                return;
            }

            StartMonitoring();
        }

#if LINUX
        private void OnListViewLayoutUpdated(object sender, object e)
        {
            _itemsPanel = _listView.ItemsPanelRoot as ItemsStackPanel;

            if (_itemsPanel != null)
            {
                _listView.LayoutUpdated -= OnListViewLayoutUpdated;
                StartMonitoring();
            }
        }
#endif

        private void OnListViewUnloaded(object sender, RoutedEventArgs e)
        {
#if LINUX
            _listView.LayoutUpdated -= OnListViewLayoutUpdated;
#endif
            StopMonitoring();
        }

        private void StartMonitoring()
        {
            if (_isMonitoring) return;

#if LINUX
            Logger.Debug($"Incremental loader monitoring, initialized: {_isInitialized}");
#endif

            _isMonitoring = true;
            _scrollViewer.ViewChanged += OnViewChanged;
            _itemsPanel.SizeChanged += OnItemsPanelSizeChanged;
            _lastPanelHeight = _itemsPanel.ActualHeight;

            if (_isInitialized)
            {
                CheckNonScrollableState();
            }
        }

        private void StopMonitoring()
        {
            if (!_isMonitoring) return;

            _isMonitoring = false;
            _scrollViewer.ViewChanged -= OnViewChanged;
            _itemsPanel.SizeChanged -= OnItemsPanelSizeChanged;
            _consecutiveSizeChangedChecks = 0;
        }

        private void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!_isInitialized || _viewModel == null)
                return;

            _consecutiveSizeChangedChecks = 0;

            if (!e.IsIntermediate)
            {
                _lastCheckTime = DateTime.MinValue;
                CheckAndLoad();
            }
            else
            {
                var now = DateTime.UtcNow;
                if (now - _lastCheckTime >= _checkInterval)
                {
                    _lastCheckTime = now;
                    CheckAndLoad();
                }
            }
        }

        private void OnItemsPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isInitialized || _viewModel == null)
                return;

            if (Math.Abs(e.NewSize.Height - _lastPanelHeight) < 0.1)
                return;

            _lastPanelHeight = e.NewSize.Height;
            CheckNonScrollableState();
        }

        private void CheckNonScrollableState()
        {
            if (_activeLoadOperations > 0 || _viewModel == null)
            {
                _consecutiveSizeChangedChecks = 0;
                return;
            }

            if (!_viewModel.HasMoreItemsAtTop && !_viewModel.HasMoreItemsAtBottom)
            {
                _consecutiveSizeChangedChecks = 0;
                return;
            }

            bool isScrollable = _scrollViewer.ScrollableHeight > 0;
            if (!isScrollable)
            {
                _consecutiveSizeChangedChecks++;

                if (_consecutiveSizeChangedChecks > MaxConsecutiveSizeChangedChecks)
                {
                    _consecutiveSizeChangedChecks = 0;
                    return;
                }

                if (_viewModel.HasMoreItemsAtTop)
                {
                    LoadItems(PanelScrollingDirection.Backward);
                }
                else if (_viewModel.HasMoreItemsAtBottom)
                {
                    LoadItems(PanelScrollingDirection.Forward);
                }
            }
            else
            {
                _consecutiveSizeChangedChecks = 0;
                CheckAndLoad();
            }
        }

        private void CheckAndLoad()
        {
            if (!_isInitialized || _scrollViewer == null || _itemsPanel == null || _viewModel == null)
                return;

            var verticalOffset = _scrollViewer.VerticalOffset;
            var viewportHeight = _scrollViewer.ViewportHeight;
            var scrollableHeight = _scrollViewer.ScrollableHeight;

            if (scrollableHeight == 0)
                return;

            var (topThreshold, bottomThreshold) = CalculateDynamicThresholds(
                verticalOffset,
                viewportHeight,
                scrollableHeight
            );

            if (_viewModel.HasMoreItemsAtTop && verticalOffset < topThreshold)
            {
                LoadItems(PanelScrollingDirection.Backward);
            }

            var distanceFromBottom = scrollableHeight - verticalOffset;
            if (_viewModel.HasMoreItemsAtBottom && distanceFromBottom < bottomThreshold)
            {
                LoadItems(PanelScrollingDirection.Forward);
            }
        }

        private (double topThreshold, double bottomThreshold) CalculateDynamicThresholds(
            double verticalOffset,
            double viewportHeight,
            double scrollableHeight)
        {
            double relativePosition = scrollableHeight > 0
                ? verticalOffset / scrollableHeight
                : 0.5;

            double totalContentHeight = scrollableHeight + viewportHeight;

            double contentRatio = totalContentHeight / viewportHeight;
            double sizeMultiplier = Math.Clamp(
                contentRatio / 5.0,
                _minThresholdMultiplier,
                _maxThresholdMultiplier
            );

            double topPositionMultiplier = 1.0 + (1.0 - relativePosition) * 0.5;
            double bottomPositionMultiplier = 1.0 + relativePosition * 0.5;

            double topThreshold = Math.Min(
                _baseTopTriggerThreshold * sizeMultiplier * topPositionMultiplier,
                viewportHeight * 2.0
            );

            double bottomThreshold = Math.Min(
                _baseBottomTriggerThreshold * sizeMultiplier * bottomPositionMultiplier,
                viewportHeight * 2.0
            );

            return (topThreshold, bottomThreshold);
        }

        private void LoadItems(PanelScrollingDirection direction)
        {
            if (_viewModel == null)
                return;

            Interlocked.Increment(ref _activeLoadOperations);

            //if (direction == PanelScrollingDirection.Backward)
            //{
            //    _listView.SetScrollingMode(ItemsUpdatingScrollMode.KeepItemsInView, force: true);
            //}
            //else if (direction == PanelScrollingDirection.Forward)
            //{
            //    _listView.SetScrollingMode(ItemsUpdatingScrollMode.KeepLastItemInView, force: true);
            //}

            _ = LoadItemsInternalAsync(direction);
        }

        private async Task LoadItemsInternalAsync(PanelScrollingDirection direction)
        {
            var ct = _cts.Token;

            try
            {
                if (ct.IsCancellationRequested || _viewModel == null)
                    return;

                await _viewModel.LoadNextSliceAsync(direction);
                await _scrollViewer.UpdateLayoutAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation or view model swap
            }
            catch (Exception)
            {
                // View model should handle its own errors
                // We just track the operation completion
            }
            finally
            {
                Interlocked.Decrement(ref _activeLoadOperations);

                if (!ct.IsCancellationRequested && _viewModel != null)
                {
                    CheckContinueLoading(direction);
                }
            }
        }

        private void CheckContinueLoading(PanelScrollingDirection direction)
        {
            var verticalOffset = _scrollViewer.VerticalOffset;
            var viewportHeight = _scrollViewer.ViewportHeight;
            var scrollableHeight = _scrollViewer.ScrollableHeight;

            if (scrollableHeight == 0)
            {
                return;
            }

            var (topThreshold, bottomThreshold) = CalculateDynamicThresholds(
                verticalOffset,
                viewportHeight,
                scrollableHeight
            );

            if (direction == PanelScrollingDirection.Backward)
            {
                if (_viewModel.HasMoreItemsAtTop && verticalOffset < topThreshold)
                {
                    LoadItems(PanelScrollingDirection.Backward);
                }
            }
            else if (direction == PanelScrollingDirection.Forward)
            {
                var distanceFromBottom = scrollableHeight - verticalOffset;
                if (_viewModel.HasMoreItemsAtBottom && distanceFromBottom < bottomThreshold)
                {
                    LoadItems(PanelScrollingDirection.Forward);
                }
            }
        }
    }
}
