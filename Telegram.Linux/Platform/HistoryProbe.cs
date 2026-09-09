//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Telegram.Controls.Chats;
using Telegram.ViewModels;
using Windows.Foundation;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: with UNIGRAM_HISTORY_PROBE=&lt;seconds&gt; the message history is
    /// instrumented and every measurement the scoping asked for is logged, so the choice between
    /// Uno's ListView and a panel of our own rests on numbers rather than on how it looks.
    ///
    /// What it records:
    /// <list type="bullet">
    /// <item><c>anchor</c> - for every batch of changes to the item collection: the item that was
    /// at the top of the viewport before the change and how far it moved after the layout that
    /// followed. This is the criterion for loading older messages upwards: the visible position
    /// must not jump.</item>
    /// <item><c>bottom</c> - after a batch that appended at the end while the view was pinned to
    /// the bottom: the distance left to the bottom, which must stay zero.</item>
    /// <item><c>view</c> - a snapshot of offset, extent, realized range, container count and
    /// created/recycled totals, once a second and after every scroll.</item>
    /// <item><c>scroll</c> - one line per non intermediate ViewChanged, to count the settling
    /// passes of a single change and catch a ChangeView loop.</item>
    /// </list>
    ///
    /// The probe never touches the list; it only reads it.
    /// </summary>
    public static class HistoryProbe
    {
        private static ChatHistoryView _view;
        private static ScrollViewer _scroll;
        private static INotifyCollectionChanged _source;

        private static int _created;
        private static int _recycled;
        private static int _changes;
        private static int _batch;
        private static int _viewChanged;
        private static int _intermediate;

        private static object _anchorItem;
        private static double _anchorY;
        private static double _anchorOffset;
        private static double _anchorExtent;
        private static double _anchorTotal;
        private static FrameworkElement _anchorContainer;
        private static int _anchorCount;
        private static bool _pending;
        private static string _reason;

        public static void Schedule(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_HISTORY_PROBE");
            if (string.IsNullOrEmpty(value)
                || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                return;
            }

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(seconds, 0.5))
            };

            timer.Tick += (s, args) =>
            {
                // The history only exists once a chat has been opened, so this keeps looking - and
                // it keeps looking afterwards too, because opening another chat builds a new
                // ChatView while the old one is still in the tree for a while, and measuring the
                // one that is on its way out reports a list of 19 items with a single container
                // and no scrollable height at all.
                var found = Find(window.Content);

                if (found != null && found != _view)
                {
                    Detach();
                    Attach(found);
                }

                if (_view != null)
                {
                    timer.Interval = TimeSpan.FromSeconds(1);
                    Snapshot("view");
                }
            };

            timer.Start();

            ScheduleGoto(window);
            ScheduleScroll(window);
        }

        /// <summary>
        /// UNIGRAM_HISTORY_SCROLL=&lt;seconds&gt;:&lt;pixels&gt;[,…] moves the history by that many
        /// pixels (negative goes up, towards the older messages) at that instant.
        /// <see cref="ScrollTest"/> cannot be used for this: it picks the tallest scrollable in
        /// the window, which is the chat list, and a real wheel event over the history turned out
        /// to move the selection of the chat list instead - see the note in PORTING.md.
        /// </summary>
        private static void ScheduleScroll(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_HISTORY_SCROLL");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var steps = new List<(double Seconds, double Pixels)>();

            foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':');

                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels))
                {
                    steps.Add((seconds, pixels));
                }
                else
                {
                    Logger.Error($"UNIGRAM_HISTORY_SCROLL must be <seconds>:<pixels>: \"{entry}\"");
                }
            }

            steps.Sort((x, y) => x.Seconds.CompareTo(y.Seconds));

            var started = Stopwatch.StartNew();
            var timer = new DispatcherTimer();
            var next = 0;

            void Schedule()
            {
                if (next >= steps.Count)
                {
                    return;
                }

                timer.Interval = TimeSpan.FromMilliseconds(Math.Max((steps[next].Seconds - started.Elapsed.TotalSeconds) * 1000, 1));
                timer.Start();
            }

            timer.Tick += (s, args) =>
            {
                timer.Stop();
                var step = steps[next++];

                try
                {
                    if (_scroll == null)
                    {
                        Logger.Warning("probe: scroll with no history on screen");
                    }
                    else
                    {
                        var before = _scroll.VerticalOffset;
                        var target = Math.Clamp(before + step.Pixels, 0, _scroll.ScrollableHeight);
                        var accepted = _scroll.ChangeView(null, target, null, true);

                        Logger.Info(string.Format(CultureInfo.InvariantCulture,
                            "probe: move {0:F0}px, offset {1:F1} -> {2:F1} of {3:F1} (accepted: {4})",
                            step.Pixels, before, _scroll.VerticalOffset, _scroll.ScrollableHeight, accepted));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("probe: scroll threw", ex);
                }

                Schedule();
            };

            Schedule();
        }

        /// <summary>
        /// UNIGRAM_HISTORY_GOTO=&lt;seconds&gt;:&lt;index&gt;[,…] jumps to the message at that index of
        /// the loaded window - a negative index counts from the end - through the very code path
        /// a reply or a search result uses (ChatHistoryView.ScrollToItem), and logs how far the
        /// message ended up from the top of the viewport.
        /// </summary>
        private static void ScheduleGoto(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_HISTORY_GOTO");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var steps = new List<(double Seconds, int Index)>();

            foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':');

                if (parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                {
                    steps.Add((seconds, index));
                }
                else
                {
                    Logger.Error($"UNIGRAM_HISTORY_GOTO must be <seconds>:<index>: \"{entry}\"");
                }
            }

            steps.Sort((x, y) => x.Seconds.CompareTo(y.Seconds));

            var started = Stopwatch.StartNew();
            var timer = new DispatcherTimer();
            var next = 0;

            void Schedule()
            {
                if (next >= steps.Count)
                {
                    return;
                }

                timer.Interval = TimeSpan.FromMilliseconds(Math.Max((steps[next].Seconds - started.Elapsed.TotalSeconds) * 1000, 1));
                timer.Start();
            }

            timer.Tick += (s, args) =>
            {
                timer.Stop();
                var step = steps[next++];

                try
                {
                    Goto(step.Index);
                }
                catch (Exception ex)
                {
                    Logger.Error("probe: goto threw", ex);
                }

                Schedule();
            };

            Schedule();
        }

        /// <summary>
        /// Bring the message with that TDLib id into view, whether or not its container exists yet.
        ///
        /// <para><c>ScrollIntoView</c>/<c>ScrollToItem</c> answer nothing at all for an index the
        /// panel has not realized (PORTING.md §6), so this walks there instead: it estimates the
        /// height of a row from the realized ones, moves the viewport that far towards the target,
        /// waits a layout pass, and looks again. The estimate does not have to be right — every
        /// step re-reads the realized range, so a bad guess costs one more step, not the jump.</para>
        ///
        /// <para>Returns the pixels from the top of the viewport, or NaN if the message is not in
        /// the loaded window at all.</para>
        /// </summary>
        public static async Task<double> GotoMessageAsync(long messageId, int steps = 24)
        {
            if (_view == null)
            {
                Logger.Warning("probe: goto-message with no history on screen");
                return double.NaN;
            }

            var count = Count();
            var index = -1;

            for (int i = 0; i < count; i++)
            {
                if (_view.Items[i] is MessageViewModel candidate && candidate.Id == messageId)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                Logger.Warning($"probe: message {messageId} is not among the {count} loaded");
                return double.NaN;
            }

            for (int step = 0; step < steps; step++)
            {
                var (first, last, height) = VisibleRange();

                if (first >= 0 && index >= first && index <= last
                    && _view.ContainerFromIndex(index) is FrameworkElement realized)
                {
                    var y = YOf(realized);

                    if (y >= 0 && y + realized.ActualHeight <= _scroll.ViewportHeight)
                    {
                        Logger.Info(string.Format(CultureInfo.InvariantCulture,
                            "probe: message {0} (item {1} of {2}) is at {3:F1}px after {4} steps",
                            messageId, index, count, y, step));
                        return y;
                    }

                    _scroll.ChangeView(null, Math.Clamp(_scroll.VerticalOffset + y - 8, 0, _scroll.ScrollableHeight), null, true);
                }
                else if (first < 0 || height <= 0)
                {
                    // Nothing on screen to measure a row from: aim by proportion of the extent and
                    // look again.
                    _scroll.ChangeView(null, _scroll.ScrollableHeight * index / Math.Max(count - 1, 1), null, true);
                }
                else
                {
                    // Half the distance each time rather than all of it: the rows in between are
                    // not all the size of the ones on screen (a photo is ten times a line of text),
                    // so overshooting and coming back costs more steps than creeping.
                    var away = index < first ? index - first : index - last;
                    var move = away * height * 0.5;

                    // …but never less than a row, or a bad estimate stalls instead of converging.
                    if (Math.Abs(move) < height)
                    {
                        move = away < 0 ? -height : height;
                    }

                    _scroll.ChangeView(null, Math.Clamp(_scroll.VerticalOffset + move, 0, _scroll.ScrollableHeight), null, true);
                }

                await PointerTest.DelayAsync(250);
            }

            Logger.Warning($"probe: gave up bringing message {messageId} (item {index} of {count}) into view");
            return double.NaN;
        }

        /// <summary>
        /// The index range the history is actually showing, and the average height of one of those
        /// rows.
        ///
        /// <para><c>ContainerFromIndex</c> alone is not enough: it answers for containers Uno keeps
        /// around off-screen too, and while scrolling this list it kept answering for items 0..3
        /// with the viewport 24 000 px down the extent — a range that sends any estimate the wrong
        /// way. So a container only counts when it is <b>positioned inside the viewport</b>.</para>
        /// </summary>
        private static (int First, int Last, double Height) VisibleRange()
        {
            var count = Count();
            var first = -1;
            var last = -1;
            var total = 0d;
            var seen = 0;

            for (int i = 0; i < count; i++)
            {
                if (_view.ContainerFromIndex(i) is not FrameworkElement container || container.ActualHeight <= 0)
                {
                    continue;
                }

                var y = YOf(container);

                if (y + container.ActualHeight < 0 || y > _scroll.ViewportHeight)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                }

                last = i;
                total += container.ActualHeight;
                seen++;
            }

            return (first, last, seen > 0 ? total / seen : 0);
        }

        private static async void Goto(int index)
        {
            if (_view == null)
            {
                Logger.Warning("probe: goto with no history on screen");
                return;
            }

            var count = Count();
            var at = index < 0 ? count + index : index;

            if (at < 0 || at >= count || _view.Items[at] is not MessageViewModel item)
            {
                Logger.Warning($"probe: goto {index} is outside the {count} loaded messages");
                return;
            }

            var before = _scroll.VerticalOffset;
            var tsc = new TaskCompletionSource<bool>();

            _view.ScrollToItem(item, VerticalAlignment.Top, null, null, ScrollIntoViewAlignment.Leading, true, tsc);

            await tsc.Task;

            // One more layout pass, so what is measured is where the message came to rest.
            await PointerTest.DelayAsync(400);

            var container = _view.ContainerFromItem(item) as FrameworkElement;

            Logger.Info(string.Format(CultureInfo.InvariantCulture,
                "probe: goto item {0} of {1}: offset {2:F1} -> {3:F1} of {4:F1}, the message is {5} from the top of the viewport",
                at, count, before, _scroll.VerticalOffset, _scroll.ScrollableHeight,
                container == null ? "not realized at all" : $"{YOf(container):F2}px"));
        }

        /// <summary>
        /// The history that is on screen: the one with an items source and a scrolling host, and
        /// the tallest of those when a chat that is being left still has its own in the tree.
        /// </summary>
        private static ChatHistoryView Find(DependencyObject root)
        {
            ChatHistoryView best = null;

            var pending = new Stack<DependencyObject>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var element = pending.Pop();

                if (element is ChatHistoryView view)
                {
                    // The one with the most messages in it, and the taller of two that tie. A chat
                    // that is being left keeps its ChatHistoryView in the tree for a while, and
                    // MainPage has one of its own that never fills up; measuring either of those
                    // reports a handful of items, one container and no scrollable height at all.
                    if (view.ItemsSource != null && view.ScrollingHost != null
                        && (best == null
                            || view.Items.Count > best.Items.Count
                            || (view.Items.Count == best.Items.Count && view.ActualHeight > best.ActualHeight)))
                    {
                        best = view;
                    }

                    continue;
                }

                var count = VisualTreeHelper.GetChildrenCount(element);

                for (int i = 0; i < count; i++)
                {
                    pending.Push(VisualTreeHelper.GetChild(element, i));
                }
            }

            return best;
        }

        private static void Detach()
        {
            if (_view == null)
            {
                return;
            }

            _view.ContainerCreated -= OnContainerCreated;
            _view.ContainerRecycled -= OnContainerRecycled;
            _scroll.ViewChanged -= OnViewChanged;

            if (_source != null)
            {
                _source.CollectionChanged -= OnCollectionChanged;
            }

            _view = null;
            _scroll = null;
            _source = null;
            _pending = false;
            _anchorItem = null;
        }

        private static void OnContainerCreated(ChatHistoryView sender, ChatHistoryViewItem container)
        {
            _created++;
        }

        private static void OnContainerRecycled(ChatHistoryViewItem sender, object item)
        {
            _recycled++;
        }

        private static void Attach(ChatHistoryView view)
        {
            if (view == null || view.ScrollingHost == null)
            {
                return;
            }

            _view = view;
            _scroll = view.ScrollingHost;

            _view.ContainerCreated += OnContainerCreated;
            _view.ContainerRecycled += OnContainerRecycled;

            _scroll.ViewChanged += OnViewChanged;

            _source = view.ItemsSource as INotifyCollectionChanged;

            if (_source != null)
            {
                _source.CollectionChanged += OnCollectionChanged;
            }

            Logger.Info($"probe: attached to the history of {view.ViewModel?.ChatId}, {view.Items.Count} items,"
                + $" ItemsSource is {view.ItemsSource?.GetType().Name ?? "null"}"
                + $" ({(_source != null ? "observable" : "NOT observable, no anchor measurements")}),"
                + $" VerticalAnchorRatio={_scroll.VerticalAnchorRatio.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        private static void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (e.IsIntermediate)
            {
                _intermediate++;
                return;
            }

            _viewChanged++;
            Snapshot("scroll");
        }

        private static void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _changes++;
            _batch++;

            if (_pending)
            {
                return;
            }

            // Where the topmost visible message sits right now. Measured before the change, so the
            // number after the layout that follows says whether the view stayed put.
            _anchorItem = TopVisibleItem(out _anchorY);
            _anchorContainer = _anchorItem == null ? null : _view.ContainerFromItem(_anchorItem) as FrameworkElement;
            _anchorOffset = _scroll.VerticalOffset;
            _anchorExtent = _scroll.ScrollableHeight;
            _anchorTotal = _scroll.ExtentHeight;
            _anchorCount = Count();
            _reason = e.Action + (e.NewStartingIndex >= 0 ? $"@{e.NewStartingIndex}" : string.Empty);
            _batch = 1;
            _pending = true;

            if (_view.ItemsPanelRoot != null)
            {
                _view.ItemsPanelRoot.LayoutUpdated += OnLayoutUpdated;
            }
        }

        private static void OnLayoutUpdated(object sender, object e)
        {
            if (_view.ItemsPanelRoot != null)
            {
                _view.ItemsPanelRoot.LayoutUpdated -= OnLayoutUpdated;
            }

            _pending = false;

            var count = Count();
            var offset = _scroll.VerticalOffset;
            var extent = _scroll.ScrollableHeight;
            var bottom = extent - offset;
            var mode = _view.CurrentScrollingMode;

            if (_anchorItem == null)
            {
                Logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "probe: anchor {0} x{1} items {2}->{3} - nothing was visible to anchor on, offset {4:F1}/{5:F1}, mode {6}",
                    _reason, _batch, _anchorCount, count, offset, extent, mode)
                    + AnchorPanelState());
                return;
            }

            // Everything that was inserted went above the viewport, so keeping the position means
            // the offset grew by exactly as much as the content did. This number is available even
            // when the anchor container has been recycled, which the drift needs.
            var grew = (offset - _anchorOffset) - (_scroll.ExtentHeight - _anchorTotal);

            var container = _view.ContainerFromItem(_anchorItem) as FrameworkElement;

            if (container == null && _anchorContainer != null && _anchorContainer.Parent != null)
            {
                container = _anchorContainer;
            }

            if (container == null)
            {
                Logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "probe: anchor {0} x{1} items {2}->{3} - the anchor lost its container, offset grew {4,8:F2}px less than the content, offset {5:F1}->{6:F1} of {7:F1}, mode {8}",
                    _reason, _batch, _anchorCount, count, grew, _anchorOffset, offset, extent, mode)
                    + AnchorPanelState());
                return;
            }

            var y = YOf(container);
            var drift = y - _anchorY;

            Logger.Info(string.Format(CultureInfo.InvariantCulture,
                "probe: anchor {0} x{1} items {2}->{3} drift {4,8:F2}px (top {5:F1}->{6:F1}), offset vs content {7,8:F2}px, offset {8:F1}->{9:F1} of {10:F1}->{11:F1}, bottom {12:F1}, mode {13}",
                _reason, _batch, _anchorCount, count, drift, _anchorY, y, grew, _anchorOffset, offset, _anchorExtent, extent, bottom, mode));
        }

        /// <summary>
        /// The state of the panel right after the layout that followed a batch of collection
        /// changes - which is the instant the history either comes back or stays empty, and the
        /// one instant the once-a-second snapshot is guaranteed to miss.
        /// </summary>
        private static string AnchorPanelState()
        {
            return _view.ItemsPanelRoot is ItemsStackPanel panel ? PanelState(panel) : string.Empty;
        }

        private static object TopVisibleItem(out double y)
        {
            y = 0;

            if (_view.ItemsPanelRoot is not ItemsStackPanel panel)
            {
                return null;
            }

            object best = null;
            var bestY = double.NegativeInfinity;

            for (int i = panel.FirstVisibleIndex; i <= panel.LastVisibleIndex; i++)
            {
                if (i < 0 || _view.ContainerFromIndex(i) is not FrameworkElement container)
                {
                    continue;
                }

                var top = YOf(container);

                // The first container whose top is at or below the top edge of the viewport: it is
                // the one a reader would say is "the message at the top of the screen".
                if (top >= -0.5 && (best == null || top < bestY))
                {
                    best = _view.ItemFromContainer(container);
                    bestY = top;
                }
            }

            if (best == null)
            {
                return null;
            }

            y = bestY;
            return best;
        }

        private static double YOf(FrameworkElement container)
        {
            return container.TransformToVisual(_scroll).TransformPoint(new Point(0, 0)).Y;
        }

        private static int Count()
        {
            return _view.Items?.Count ?? 0;
        }

        /// <summary>
        /// Why the panel stopped measuring, when it stops.
        ///
        /// <c>VirtualizingPanelLayout.UpdateLayout</c> sets <c>OwnerPanel.ShouldInterceptInvalidate
        /// = true</c> on entry and back to false on exit, with no try/finally - and
        /// <c>UIElement.InvalidateMeasure</c> returns immediately while that flag is set. So one
        /// exception escaping anything Uno raises from inside that window (PrepareContainerForIndex,
        /// ContainerContentChanging, the container's Loaded, ClearContainerForItem) leaves the panel
        /// deaf to every InvalidateMeasure for the rest of the process: 0 realized containers, an
        /// extent equal to the viewport, and no recovery even when another chat is opened, because
        /// the ChatView and its panel are reused. The other way to measure nothing is
        /// <c>MeasureOverride</c> returning Size(0,0) because the layout lost its
        /// <c>ItemsControl</c> (it is nulled in the panel's Unloaded and only restored in Loading).
        /// Both are internal to Uno and invisible from the outside, so they are read by reflection:
        /// the line tells the two apart in one run.
        /// </summary>
        private static string PanelState(ItemsStackPanel panel)
        {
            try
            {
                _interceptProperty ??= typeof(UIElement).GetProperty("ShouldInterceptInvalidate",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                _layoutField ??= typeof(ItemsStackPanel).GetField("_layout",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                var intercept = _interceptProperty?.GetValue(panel) as bool?;

                var layout = _layoutField?.GetValue(panel);
                if (layout != null)
                {
                    _itemsControlProperty ??= layout.GetType().GetProperty("ItemsControl",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }

                var itemsControl = _itemsControlProperty?.GetValue(layout);

                return $", intercepting invalidate {intercept?.ToString() ?? "?"}"
                    + $", layout ItemsControl {(itemsControl != null ? "set" : "NULL")}"
                    + $", panel desired {panel.DesiredSize.Height.ToString("F1", CultureInfo.InvariantCulture)}"
                    + LayoutState(panel, layout);
            }
            catch (Exception ex)
            {
                return $", panel state unreadable ({ex.GetType().Name})";
            }
        }

        /// <summary>
        /// The inside of <c>VirtualizingPanelLayout</c>, read by reflection, because the whole
        /// question "why did the measure return a height of zero" is decided by fields that are
        /// private to Uno and by one comparison that is never logged.
        ///
        /// <para>The chain that matters, all of it read out of the IL of Uno 6.6.184:
        /// <c>MeasureOverride</c> ends with <c>EstimatePanelSize</c>, whose height is
        /// <c>EstimatePanelExtent()</c> when the available height is infinite - which it is inside
        /// a ScrollViewer - and <c>EstimatePanelExtent</c> returns a literal <c>0.0</c> when no
        /// line is materialized. So "panel desired 0.0" means, exactly and only, that the fill
        /// pass of that measure materialized nothing. Whether it materializes anything hangs on a
        /// single comparison in <c>FillLayout.FillForward</c>:
        /// <c>GetItemsEnd() &lt; ExtendedViewportEnd + extentAdjustment</c>, and with no line
        /// materialized <c>GetItemsEnd()</c> is just <c>_dynamicSeedStart</c>. Those three terms
        /// are <c>_dynamicSeedStart</c>, the private property <c>ExtendedViewportEnd</c> and the
        /// field <c>_scrollAdjustmentForCollectionChanges</c>, and this is what prints them.</para>
        ///
        /// <para>Two of them are only meaningful at particular moments. <c>UpdateLayout</c> ends
        /// with <c>SetDynamicSeed(null, null)</c>, so the seed read from a timer is always null:
        /// the seed of the failing pass has to come from Uno's own Debug log
        /// (<c>UNIGRAM_UNO_LOG=Debug UNIGRAM_UNO_LOG_ONLY=VirtualizingPanelLayout</c>, the
        /// <c>ScrapLayout() seed index=… seed start=…</c> line). <c>_scrollAdjustmentForCollectionChanges</c>
        /// on the other hand is only ever cleared inside <c>OnScrollChanged</c>, so a stale value
        /// there survives and is worth reading at any time.</para>
        /// </summary>
        private static string LayoutState(ItemsStackPanel panel, object layout)
        {
            if (layout == null)
            {
                return string.Empty;
            }

            var lines = FieldValue(layout, "_materializedLines");
            var pending = FieldValue(layout, "_pendingCollectionChanges");

            return ", layout"
                + $" lines {CountOf(lines)}"
                + $" seed {Text(FieldValue(layout, "_dynamicSeedIndex"))}@{Text(FieldValue(layout, "_dynamicSeedStart"))}"
                + $" adj {Text(FieldValue(layout, "_scrollAdjustmentForCollectionChanges"))}"
                + $" avgLine {Text(FieldValue(layout, "_averageLineHeight"))}"
                + $" window {Text(PropertyValue(layout, "ExtendedViewportStart"))}..{Text(PropertyValue(layout, "ExtendedViewportEnd"))}"
                + $" (viewportExtent {Text(PropertyValue(layout, "ViewportExtent"))}, cacheLength {Text(panel.CacheLength)})"
                + $" itemsStart {Text(MethodValue(layout, "GetItemsStart"))}"
                + $" itemsEnd {Text(MethodValue(layout, "GetItemsEnd"))}"
                + $" scrollOffset {Text(PropertyValue(layout, "ScrollOffset"))}"
                + $" available {Text(FieldValue(layout, "_availableSize"))}"
                + $" lastMeasured {Text(FieldValue(layout, "_lastMeasuredSize"))}"
                + $" clearingLines {Text(FieldValue(layout, "_clearingLines"))}"
                + $" pendingChanges {CountOf(pending)}"
                + $" measureDirty {Text(PropertyValue(panel, "IsMeasureDirty"))}";
        }

        private static readonly Dictionary<string, System.Reflection.MemberInfo> _members = new();

        private static System.Reflection.MemberInfo Member(Type type, string name, bool field)
        {
            var key = type.FullName + "|" + name + "|" + field;

            if (_members.TryGetValue(key, out var cached))
            {
                return cached;
            }

            System.Reflection.MemberInfo found = null;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly;

            for (var walk = type; walk != null && found == null; walk = walk.BaseType)
            {
                found = field
                    ? walk.GetField(name, flags)
                    : (System.Reflection.MemberInfo)walk.GetProperty(name, flags);
            }

            _members[key] = found;
            return found;
        }

        private static object FieldValue(object instance, string name)
        {
            return (Member(instance.GetType(), name, true) as System.Reflection.FieldInfo)?.GetValue(instance);
        }

        private static object PropertyValue(object instance, string name)
        {
            try
            {
                return (Member(instance.GetType(), name, false) as System.Reflection.PropertyInfo)?.GetValue(instance);
            }
            catch (Exception ex)
            {
                return "!" + ex.GetType().Name;
            }
        }

        private static object MethodValue(object instance, string name)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly;

            for (var walk = instance.GetType(); walk != null; walk = walk.BaseType)
            {
                var method = walk.GetMethod(name, flags, Type.EmptyTypes);
                if (method != null)
                {
                    try
                    {
                        return method.Invoke(instance, null);
                    }
                    catch (Exception ex)
                    {
                        return "!" + ex.GetType().Name;
                    }
                }
            }

            return null;
        }

        private static string CountOf(object collection)
        {
            if (collection == null)
            {
                return "?";
            }

            return Text(PropertyValue(collection, "Count"));
        }

        private static string Text(object value)
        {
            return value switch
            {
                null => "null",
                double number => number.ToString("F1", CultureInfo.InvariantCulture),
                Size size => size.Width.ToString("F0", CultureInfo.InvariantCulture) + "x"
                    + (double.IsInfinity(size.Height) ? "inf" : size.Height.ToString("F0", CultureInfo.InvariantCulture)),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            };
        }

        private static System.Reflection.PropertyInfo _interceptProperty;
        private static System.Reflection.FieldInfo _layoutField;
        private static System.Reflection.PropertyInfo _itemsControlProperty;

        private static void Snapshot(string what)
        {
            if (_view.ItemsPanelRoot is not ItemsStackPanel panel)
            {
                return;
            }

            var realized = 0;

            for (int i = 0; i < Count(); i++)
            {
                if (_view.ContainerFromIndex(i) != null)
                {
                    realized++;
                }
            }

            Logger.Info(string.Format(CultureInfo.InvariantCulture,
                "probe: {0} offset {1:F1} of {2:F1} (viewport {3:F1}, extent {4:F1}), items {5}, realized [{6}..{7}] = {8}, "
                + "containers {9} created - {10} recycled, changes {11}, settled {12} + {13} intermediate",
                what, _scroll.VerticalOffset, _scroll.ScrollableHeight, _scroll.ViewportHeight, _scroll.ExtentHeight,
                Count(), panel.FirstVisibleIndex, panel.LastVisibleIndex, realized,
                _created, _recycled, _changes, _viewChanged, _intermediate)
                + $", oldest loaded {_view.ViewModel?.IsOldestSliceLoaded?.ToString() ?? "?"}"
                + $", newest loaded {_view.ViewModel?.IsNewestSliceLoaded?.ToString() ?? "?"}"
                + (IsCollapsed(panel, realized) ? PanelState(panel) : string.Empty));

            Poke(panel, realized);
        }

        /// <summary>
        /// The empty history, as a predicate: there are messages and the panel measured a height
        /// of zero, or materialized nothing. Both halves are needed - the panel can measure zero
        /// with a container still hanging around from the pass before.
        /// </summary>
        private static bool IsCollapsed(ItemsStackPanel panel, int realized)
        {
            return Count() > 0 && (realized == 0 || panel.DesiredSize.Height <= 0);
        }

        private static int _pokes;
        private static bool _poked;

        /// <summary>
        /// UNIGRAM_HISTORY_POKE=&lt;n&gt; asks the panel to measure again, up to n times, whenever
        /// the history is found collapsed. It is an experiment, not a fix: it separates the two
        /// candidates for "and it never comes back". If a plain InvalidateMeasure refills the
        /// history, then the panel had simply stopped being measured and the fill logic is fine
        /// once it runs again; if the next snapshot is identical, the measure does run and still
        /// materializes nothing, and the reason has to be in the fill itself.
        /// </summary>
        private static void Poke(ItemsStackPanel panel, int realized)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_HISTORY_POKE");
            if (string.IsNullOrEmpty(value) || !int.TryParse(value, out int limit) || limit <= 0)
            {
                return;
            }

            if (!IsCollapsed(panel, realized))
            {
                if (_poked)
                {
                    _poked = false;
                    Logger.Info("probe: poke - the history came back after the extra measure");
                }

                return;
            }

            if (_pokes >= limit)
            {
                return;
            }

            _pokes++;
            _poked = true;

            Logger.Info($"probe: poke {_pokes}/{limit} - asking the collapsed panel to measure again");
            panel.InvalidateMeasure();
        }
    }
}
