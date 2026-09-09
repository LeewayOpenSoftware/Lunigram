//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Microsoft.UI.Xaml.Data;
using System.Collections.Generic;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels.Drawers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Drawers
{
    public partial class StickerDrawerItemClickEventArgs : EventArgs
    {
        public StickerDrawerItemClickEventArgs(Sticker sticker, bool fromStickerSet)
        {
            Sticker = sticker;
            FromStickerSet = fromStickerSet;
        }

        public Sticker Sticker { get; }

        public bool FromStickerSet { get; }
    }

    public sealed partial class StickerDrawer : UserControlEx, IDrawer
    {
        public StickerDrawerViewModel ViewModel => DataContext as StickerDrawerViewModel;

        public event EventHandler<StickerDrawerItemClickEventArgs> ItemClick;
        public event EventHandler<ItemContextRequestedEventArgs<Sticker>> ItemContextRequested;
        public event EventHandler ChoosingItem;

        private readonly AnimatedListHandler _handler;
        private readonly ZoomableListHandler _zoomer;

        private readonly AnimatedListHandler _toolbarHandler;

        private readonly EventDebouncer<TextChangedEventArgs> _typing;

        // 12.10.2 pools SelectorItem here and reaches the Grid through it. This head keeps the
        // Grid map: on Uno the container is often still outside the visual tree when the item is
        // prepared, so DrawerContainers resolves the content root asynchronously and hands us the
        // Grid directly -- there is no SelectorItem to hold on to at that moment. Jim's
        // hunks-83e6956/README.md calls this the low-churn resolution; full adoption of
        // _itemIdToSelector is a follow-up, not a merge conflict.
        private readonly Dictionary<StickerViewModel, Grid> _itemIdToContent = new();
        private long _selectedSetId;

        private bool _isActive;

        public StickerDrawer()
        {
            InitializeComponent();

#if LINUX
            // The list's Header stacks BESIDE the items panel on Uno, not above it, so the search
            // box would take the row and leave the grid nothing to draw in. Move it into the row of
            // its own that the XAML keeps empty on Windows. See
            // Telegram.Linux/Xaml/DrawerHeaderHost.cs for the measurement.
            DrawerHeaderHost.MoveOutOfList(List, SearchHost);
#endif

            // The FluidGridView triggers used to be a <common:FluidGridView.Triggers> property
            // element in the XAML right here. They are set from code because Uno's XAML source
            // generator SILENTLY STOPS emitting the rest of the enclosing element's children as
            // soon as it meets an attached property written as a property element that holds a
            // collection -- measured on Uno 6.6.184, see PORTING.md 6. In this file that cost the
            // whole ToolbarContainer subtree: it was not merely unnamed, it was never constructed.
            // Setting them here is what the drawer already did for the ChatPhoto/UserPhoto modes,
            // and it runs on Windows too, so the two heads keep the same column counts.
            var trigger = new FluidGridViewTrigger { RowsOrColumns = 5 };
            FluidGridView.GetTriggers(List).Add(trigger);

            Instrumentation.Register(this);

            this.CreateInsetClip();


            // VisualUtilities.DropShadow returns NULL on this head, on purpose: Uno's
            // Compositor.CreateDropShadow throws rather than degrading, and it took a whole page
            // down once already (see the comment in Common/VisualUtilities.cs). Every caller in the
            // subset therefore has to survive a null, and this one did not -- it dereferenced the
            // result on the very next line, inside a CONSTRUCTOR, so the control could never be
            // built at all. It compiles either way, which is exactly why it had to be looked for.
            var header = VisualUtilities.DropShadow(Separator);
            if (header != null)
            {
                header.Clip = header.Compositor.CreateInsetClip(0, 40, 0, -40);
            }

            _handler = new AnimatedListHandler(List, AnimatedListType.Stickers);
            _toolbarHandler = new AnimatedListHandler(Toolbar, AnimatedListType.Stickers);

            _zoomer = new ZoomableListHandler(List);
            _zoomer.Opening += Zoomer_Opening;
            _zoomer.Closing += Zoomer_Closing;

            _typing = new EventDebouncer<TextChangedEventArgs>(Constants.TypingTimeout, handler => SearchField.TextChanged += new TextChangedEventHandler(handler));
            _typing.Invoked += async (s, args) =>
            {
                // u-091: an async void lambda, so a throw out of the item materialization inside
                // LoadMoreItemsAsync never reaches the debouncer that raised it. Uncaught it is
                // swallowed by NativeDispatcher.RunAction (see review/probes/AsyncVoidLanding.cs),
                // and because the debouncer keeps raising Invoked, every later keystroke takes the
                // same road: search-as-you-type is dead for the rest of this panel's session with
                // one anonymous console line to show for it.
                try
                {
                    var items = ViewModel?.SearchStickers as SearchStickerSetsCollection;
                    if (items != null && string.Equals(SearchField.Text, items.Query))
                    {
                        await items.LoadMoreItemsAsync(1);
                        await items.LoadMoreItemsAsync(2);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            };
        }

        private void Zoomer_Opening(object sender, EventArgs e)
        {
            _handler.Suspend();
        }

        private void Zoomer_Closing(object sender, EventArgs e)
        {
            _handler.Resume();
        }

        public Services.Settings.StickersTab Tab => Services.Settings.StickersTab.Stickers;

        public Thickness ScrollingHostPadding
        {
            get => List.Padding;
            set => List.Padding = new Thickness(2, value.Top, 2, value.Bottom);
        }

        public ListViewBase ScrollingHost => List;

        public void Activate(Chat chat, EmojiSearchType type = EmojiSearchType.Combined)
        {
            _isActive = true;
            _handler.Resume();
            _toolbarHandler.ThrottleVisibleItems();

            SearchField.SetType(ViewModel.ClientService, type);

#if LINUX
            // The view model has the stickers - measured on the running app, "filled SavedStickers
            // with 36 set(s), 2498 sticker(s)" - and the grid still shows nothing, because the
            // CollectionViewSource's Source is still null at that point: the OneWay x:Bind to
            // ViewModel.Stickers (StickerDrawer.xaml:17) never delivers here. ViewModel.Stickers
            // is a computed property (SearchStickers ?? SavedStickers) that raises no
            // PropertyChanged, so nothing ever re-evaluates that binding, and on this head it does
            // not run at bind time either.
            //
            // Handing the source over by hand is enough and is stable: SavedStickers is created
            // once in the view model's constructor and filled in place with ReplaceWith, so the
            // CollectionViewSource keeps the live instance and its CollectionChanged carries the
            // fill through to the grid.
            BindSourceLinux();
#endif

            ViewModel.Update(chat);

#if LINUX
            ProbeSource("after Activate");

            // Again a moment later: Update() fetches, so the interesting count is the one after
            // the collection has had a chance to fill. Guarded on _isActive because the panel
            // closes on its own and a probe of a deactivated drawer reads all-null and says
            // nothing - which is exactly what the first version of this measured.
            CompositionScopedBatchEx.QueueCompleted(TimeSpan.FromSeconds(3), () =>
            {
                ProbeSource($"3s after Activate (active={_isActive})");
            });
#endif
        }

#if LINUX
        private System.Collections.Specialized.INotifyCollectionChanged _boundLinux;

        private bool _repeaterHooked;

        // The item template's 64x64 FrameSize plus 2px either side, and the height of a title row.
        private const double StickerCell = 68;
        private const double TitleRow = 32;

        // Columns are capped so a very wide host cannot ask a row for an unbounded number of
        // cells; a row's cell count has to stay small and fixed for the invariant to mean anything.
        private const int MaxColumns = 12;

        private int _columns;
        private System.Collections.IEnumerable _pendingSets;

        /// <summary>
        /// Drives the grid's items by hand, because none of the three links that should do it work
        /// on this head.
        /// </summary>
        /// <remarks>
        /// Measured on the running app, with the panel held open so nothing is confused with the
        /// drawer deactivating:
        ///
        ///   vm.Stickers groups=36 stickers=2498 | source=set view=0 | List.ItemsSource=NULL
        ///
        /// Three separate breaks in one line. The view model has 2498 stickers. The OneWay x:Bind
        /// that should put them on the CollectionViewSource never delivers - ViewModel.Stickers is
        /// a computed property (SearchStickers ?? SavedStickers) that raises no PropertyChanged.
        /// The second OneWay x:Bind, GridView.ItemsSource to StickersSource.View, never delivers
        /// either, so the grid has no source at all and realizes no containers - which is the
        /// empty drawer. And the CollectionViewSource's View stays at 0 after the collection is
        /// filled, so even a correct binding would have shown nothing: the grouped view does not
        /// rebuild from the source's CollectionChanged here.
        ///
        /// So: assign the source, assign the grid's ItemsSource from the view it produces, and
        /// re-do both whenever the view model's collection changes - which is when the fill
        /// actually arrives, a fraction of a second after Activate returns.
        /// </remarks>
        private void BindSourceLinux()
        {
            var stickers = ViewModel?.Stickers;

            if (StickersSource == null || stickers == null)
            {
                return;
            }

            if (Repeater != null && !_repeaterHooked)
            {
                _repeaterHooked = true;
                // Prepared/Clearing are hooked ONLY to count what is live: see OnRowRealized.
                // Nothing about a row's size is decided there.
                Repeater.ElementPrepared += OnRowPrepared;
                Repeater.ElementClearing += OnRowCleared;

                if (RepeaterHost != null)
                {
                    RepeaterHost.SizeChanged += OnRepeaterHostSizeChanged;
                }
            }

            if (_boundLinux != null)
            {
                _boundLinux.CollectionChanged -= OnStickersChangedLinux;
            }

            _boundLinux = stickers;
            _boundLinux.CollectionChanged += OnStickersChangedLinux;

            Logger.Info($"sticker drawer: bound to collection #{stickers.GetHashCode()} (SavedStickers #{ViewModel?.SavedStickers?.GetHashCode()}, SearchStickers #{ViewModel?.SearchStickers?.GetHashCode().ToString() ?? "null"}), drawer #{GetHashCode()}, vm #{ViewModel?.GetHashCode()}");

            RefreshSourceLinux("Activate");
        }

        private void OnStickersChangedLinux(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Logged at ENTRY, before anything can throw or be marshalled away: the previous
            // version only logged at the end of the refresh, so "no line" could equally mean the
            // event never arrived or the work after it failed. This separates the two.
            Logger.Info($"sticker drawer: collection changed ({e.Action}) on collection #{sender?.GetHashCode()}, drawer #{GetHashCode()}");

            // ViewModel.Update is async void over TDLib calls, so ReplaceWith - and therefore this
            // handler - can arrive off the view thread. Touching the CollectionViewSource or the
            // GridView from there throws, and an exception raised inside a CollectionChanged
            // handler goes nowhere: the first version of this simply never logged and looked like
            // the event was not firing at all.
            this.BeginOnUIThread(() => RefreshSourceLinux("collection changed"));
        }

        private void RefreshSourceLinux(string when)
        {
            try
            {
                RefreshSourceCoreLinux(when);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"sticker drawer: re-binding the source on {when} failed");
            }
        }

        /// <summary>
        /// Drives the repeater that replaces the GridView on this head, and reports what the old
        /// panel actually did so the swap can be argued with rather than believed.
        /// </summary>
        private void BindRepeaterLinux(System.Collections.IEnumerable sets)
        {
            if (Repeater == null)
            {
                return;
            }

            _pendingSets = sets;

            // Shown BEFORE it is given anything, and that order matters: a collapsed element gets
            // no layout, so the width the rows are built from does not exist until the host has
            // been measured once. Empty, that pass costs nothing.
            if (RepeaterHost != null)
            {
                RepeaterHost.Visibility = Visibility.Visible;
            }

            if (_columns > 0)
            {
                ApplyRowsLinux();
            }
            else
            {
                // OnRepeaterHostSizeChanged builds them the moment it has a width; the posted
                // check is the net for a SizeChanged that never comes.
                this.BeginOnUIThread(EnsureColumnsLinux);
            }

            // The GridView stays in the tree because IDrawer.ScrollingHost is typed ListViewBase
            // and StickerPanel reads it, but it must hold nothing: a non-virtualizing panel with
            // 2498 containers in it costs nineteen seconds of UI thread whether it is visible or
            // not.
            if (List != null)
            {
                List.ItemsSource = null;
                List.Visibility = Visibility.Collapsed;
            }
        }

        // The outer repeater has nothing left to do when a set is realised: the set's own
        // ItemsControl builds and measures its cells, and each cell wires itself on Loaded. What
        // used to be here -- find the inner repeater, subscribe to it, tell it how tall to be --
        // was the entire first-open cascade.


        private int _realizedRows;
        private int _realizedRowsPeak;

        /// <summary>
        /// Counts the rows the outer repeater is holding live, and nothing else.
        /// </summary>
        /// <remarks>
        /// This exists to answer one question from outside the app, without a scroll primitive:
        /// does the OUTER repeater still virtualise? If it does, the live count settles at roughly
        /// a screenful and the peak stops climbing however many rows the source has. If it ever
        /// realised everything, the peak would equal the row count. Kept deliberately cheap -- two
        /// ints and a line per change -- because it is a diagnostic, not a feature.
        /// </remarks>
        private void OnRowPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            _realizedRows++;
            _realizedRowsPeak = Math.Max(_realizedRowsPeak, _realizedRows);

            OnRowRealized(sender, "prepared", args.Index);
        }

        private void OnRowCleared(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            _realizedRows--;

            OnRowRealized(sender, "cleared", -1);
        }

        private void OnRowRealized(ItemsRepeater sender, string what, int index)
        {
            var total = (sender.ItemsSource as System.Collections.ICollection)?.Count ?? -1;

            Logger.Info($"sticker drawer: row {what}{(index < 0 ? string.Empty : " " + index)} -- " +
                $"realized={_realizedRows} peak={_realizedRowsPeak} of {total} row(s)");
        }

        private void OnRepeaterHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var available = e.NewSize.Width;

            if (available <= 0)
            {
                return;
            }

            var columns = Math.Min(MaxColumns, Math.Max(1, (int)(available / StickerCell)));

            if (columns == _columns)
            {
                return;
            }

            _columns = columns;
            ApplyRowsLinux();
        }

        /// <summary>
        /// Flattens the sets into rows and hands them over.
        /// </summary>
        /// <remarks>
        /// Rebuilt rather than adjusted, because the rows ARE the column count: a different width
        /// slices the same stickers into different lines. It happens on a width change and on a
        /// collection change, neither of which is a layout-time event.
        /// </remarks>
        private void ApplyRowsLinux()
        {
            if (Repeater == null || _pendingSets == null || _columns <= 0)
            {
                return;
            }

            var rows = DrawerRow.Build(_pendingSets, _columns, StickerCell, TitleRow);
            Repeater.ItemsSource = rows;

            Logger.Info($"sticker drawer: {rows.Count} row(s) built, {_columns} column(s) of {StickerCell}");
        }

        private void EnsureColumnsLinux()
        {
            if (_columns > 0 || RepeaterHost == null)
            {
                return;
            }

            var available = RepeaterHost.ActualWidth;

            if (available <= 0)
            {
                Logger.Warning("sticker drawer: the repeater host still has no width; no rows can be built yet");
                return;
            }

            _columns = Math.Min(MaxColumns, Math.Max(1, (int)(available / StickerCell)));
            ApplyRowsLinux();
        }

        /// <summary>
        /// Gives one cell its sticker. An ItemsControl raises no element events, so the cell says
        /// when it is ready itself -- which needs no index arithmetic at all, because the cell's
        /// own DataContext IS its sticker.
        /// </summary>
        private void OnStickerCellLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid { DataContext: StickerViewModel sticker } content)
            {
                return;
            }

            _itemIdToContent[sticker] = content;

            // Same two jobs the GridView's ContainerContentChanging did: give the cell its source,
            // and fetch the set the first time one of its placeholders is realized. Neither the
            // Choosing* events nor GroupStyle headers exist on this path, so both hang off here.
            if (content.Children.Count > 0 && content.Children[0] is AnimatedImage animation)
            {
                animation.Source = sticker.StickerValue != null
                    ? new DelayedFileSource(ViewModel.ClientService, sticker)
                    : null;
            }

            // DrawerContainers.Prepare and ZoomableListHandler both take a SelectorItem, and this
            // path has none - a repeater's element IS the template root. The context menu only
            // needs the element, so it is hooked straight onto it and reads the sticker off the
            // DataContext instead of asking the GridView which item a container belongs to.
            // NOT carried over: the press-and-hold zoom preview. ZoomableListHandler.OnPointerPressed
            // does `sender as Control` and a Grid is not a Control, so wiring it here would be a
            // handler that silently does nothing. It needs the handler widened, which is shared
            // code and a separate change.
            content.ContextRequested -= OnRepeaterContextRequested;
            content.ContextRequested += OnRepeaterContextRequested;


            EnsureGroupLoaded(sticker);
        }

        private void OnRepeaterContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            if (sender is FrameworkElement { DataContext: StickerViewModel sticker })
            {
                ItemContextRequested?.Invoke(sender, new ItemContextRequestedEventArgs<Sticker>(sticker, args));
            }
        }

        private void OnStickerCellUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid { DataContext: StickerViewModel sticker } content)
            {
                return;
            }

            // Dropped together with the source: the map is what AnimatedListHandler walks, and a
            // cell that has left the tree must not be in it. An ItemsControl does not recycle, so
            // a set scrolling out of the outer repeater unloads its cells all at once.
            _itemIdToContent.Remove(sticker);

            if (content.Children.Count > 0 && content.Children[0] is AnimatedImage animation)
            {
                animation.Source = null;
            }
        }

        private void OnStickerTapped(object sender, TappedRoutedEventArgs e)
        {
            // Logged at ENTRY and unconditionally, so "no line at all" means the gesture never
            // reached this element, and a line that stops short names which guard rejected it.
            // The two are indistinguishable from the outside and need different fixes.
            var context = (sender as FrameworkElement)?.DataContext;

            Logger.Info($"sticker drawer: tapped, sender={sender?.GetType().Name ?? "null"}, " +
                $"dc={context?.GetType().Name ?? "null"}, " +
                $"value={(context is StickerViewModel s0 ? (s0.StickerValue == null ? "NULL" : "set") : "n/a")}, " +
                $"subscribed={(ItemClick != null)}");

            if (sender is FrameworkElement { DataContext: StickerViewModel sticker } && sticker.StickerValue != null)
            {
                // fromStickerSet said, on Windows, "this came out of a real set rather than the
                // recents": the group header container carried the set. Here the sticker names
                // its own set, so it can be answered without a container at all.
                ItemClick?.Invoke(this, new StickerDrawerItemClickEventArgs(sticker, sticker.SetId != 0));
            }
        }

        private void RefreshSourceCoreLinux(string when)
        {
            var stickers = ViewModel?.Stickers;

            if (StickersSource == null || stickers == null)
            {
                return;
            }

            // Re-assigned rather than left alone: the grouped view is built from Source when Source
            // is set, and it does not follow the collection afterwards, so the fill only lands if
            // the source is handed over again once it has arrived.
            StickersSource.Source = null;
            StickersSource.Source = stickers;

            // The sets go to the repeater whole - it is the repeater that groups now, one inner
            // wrap layout per set - so the CollectionViewSource is no longer in the path at all.
            BindRepeaterLinux(stickers);

            Logger.Info($"sticker drawer: source re-bound on {when} -- {stickers.Count} set(s), repeater={(Repeater?.ItemsSource == null ? "NULL" : "set")}");
        }

        /// <summary>
        /// Says, on the running app, whether this drawer has any items to show and where the chain
        /// breaks if it does not.
        /// </summary>
        /// <remarks>
        /// The tree dump showed both item panels correctly sized with ZERO containers realized -
        /// the GridView's VariableSizedWrapGrid and the toolbar's ItemsStackPanel alike - so the
        /// question is not which panel is used but whether anything ever reaches one. This prints
        /// the whole chain in one line: the view model's collection, the CollectionViewSource's
        /// Source and View, what the GridView actually has, and whether an incremental collection
        /// still claims more items.
        /// </remarks>
        private void ProbeSource(string when)
        {
            try
            {
                var vm = ViewModel;
                var groups = vm?.Stickers;
                var stickers = 0;

                if (groups != null)
                {
                    foreach (var group in groups)
                    {
                        stickers += group?.Stickers?.Count ?? 0;
                    }
                }

                var view = StickersSource?.View;
                var more = vm?.Stickers as ISupportIncrementalLoading;

                // The one number the previous probe was missing. List.Items said 2498 while the
                // screen stayed empty, so the break is between "the control knows the items" and
                // "the panel holds containers" - and only the panel's own child count says which.
                var panel = List?.ItemsPanelRoot;

                Logger.Info($"sticker drawer panel ({when}): grid={panel?.GetType().Name ?? "null"} " +
                    $"children={panel?.Children?.Count.ToString() ?? "n/a"} size={panel?.ActualWidth ?? 0}x{panel?.ActualHeight ?? 0} | " +
                    $"repeater sets={(Repeater?.ItemsSource == null ? "NULL" : "set")} " +
                    $"size={Repeater?.ActualWidth ?? 0}x{Repeater?.ActualHeight ?? 0} " +
                    $"cells={_itemIdToContent.Count}");

                Logger.Info($"sticker drawer probe ({when}): vm.Stickers groups={groups?.Count.ToString() ?? "null"} stickers={stickers} | " +
                    $"source={(StickersSource?.Source == null ? "null" : "set")} view={(view == null ? "null" : view.Count.ToString())} | " +
                    $"List.ItemsSource={(List?.ItemsSource == null ? "NULL" : "set")} List.Items={List?.Items?.Count.ToString() ?? "null"} | " +
                    $"Toolbar.ItemsSource={(Toolbar?.ItemsSource == null ? "NULL" : "set")} Toolbar.Items={Toolbar?.Items?.Count.ToString() ?? "null"} | " +
                    $"HasMoreItems={(more == null ? "n/a" : more.HasMoreItems.ToString())}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "sticker drawer probe failed");
            }
        }
#endif

        public void Deactivate()
        {
            _itemIdToContent.Clear();

            _isActive = false;
            _handler.UnloadItems();
            _toolbarHandler.UnloadItems();

            _typing.Cancel();

            // This is called only right before XamlMarkupHelper.UnloadObject
            // so we can safely clean up any kind of anything from here.
            Bindings.StopTracking();

            // Same as EmojiDrawer: the bindings' last value keeps a CollectionChanged handler
            // on the view model's collections alive past UnloadObject, and the view model can
            // still queue an Update from the aggregator.
            List.ItemsSource = null;
            StickersSource.Source = null;
            Toolbar.ItemsSource = null;

#if LINUX
            if (_boundLinux != null)
            {
                _boundLinux.CollectionChanged -= OnStickersChangedLinux;
                _boundLinux = null;
            }

            // There is nothing left to unsubscribe. The old note here was about the inner
            // repeaters surviving deactivation and therefore never re-raising ElementPrepared,
            // which is exactly the hazard that disappears when the cells hang off their own
            // Loaded instead of an event the outer repeater may never raise again.
#endif
        }

        public void LoadVisibleItems()
        {
            if (_isActive)
            {
                _handler.LoadVisibleItems();
                _toolbarHandler.LoadVisibleItems();
            }
        }

        public void ThrottleVisibleItems()
        {
            if (_isActive)
            {
                _handler.ThrottleVisibleItems();
                _toolbarHandler.ThrottleVisibleItems();
            }
        }

        public void UnloadVisibleItems()
        {
            _handler.UnloadVisibleItems();
            _toolbarHandler.UnloadVisibleItems();
        }

        private void Stickers_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StickerViewModel sticker && sticker.StickerValue != null)
            {
                var container = List.ContainerFromItem(e.ClickedItem);

                var groupContainer = List.GroupHeaderContainerFromItemContainer(container) as GridViewHeaderItem;
                if (groupContainer == null)
                {
                    ItemClick?.Invoke(this, new StickerDrawerItemClickEventArgs(sticker, false));
                    return;
                }

                var stickerSet = groupContainer.Content as StickerSetViewModel;
                if (stickerSet != null)
                {
                    ItemClick?.Invoke(this, new StickerDrawerItemClickEventArgs(sticker, stickerSet.Id != 0));
                }
            }
        }

        private void Stickers_Loaded(object sender, RoutedEventArgs e)
        {
            var scrollingHost = List.GetChild<ScrollViewer>();
            if (scrollingHost != null)
            {
                scrollingHost.VerticalSnapPointsType = SnapPointsType.None;

                // Syncronizes GridView with the toolbar ListView
                scrollingHost.ViewChanged += ScrollingHost_ViewChanged;
                ScrollingHost_ViewChanged(null, null);
            }
        }

        private void Toolbar_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StickerSetViewModel set && set.Stickers != null)
            {
                List.ScrollIntoView(e.ClickedItem, ScrollIntoViewAlignment.Leading);
            }
        }

        private void ScrollingHost_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
#if LINUX
            // Two NotImplemented APIs meet in this handler (PORTING.md 6):
            // ItemsWrapGrid.FirstVisibleIndex, which THROWS -- and this runs on every scroll tick,
            // so untouched it takes the gesture down rather than degrading -- and
            // ItemsControl.GroupHeaderContainerFromItemContainer, which cannot hand back a group
            // header container at all. So neither is used. The first visible ITEM is found by
            // geometry (DrawerContainers.FirstVisibleItem) and the group is resolved from that
            // item's SetId, which is exactly the route the upstream author sketched in the
            // commented-out block in StickerDrawer.ScrollingHost_ViewChanged.
            if (_isActive && DrawerContainers.FirstVisibleItem(List) is StickerViewModel visible)
            {
                var setId = visible.SetId;
                var sets = ViewModel?.Stickers;

                if (setId != 0 && sets != null)
                {
                    foreach (var candidate in sets)
                    {
                        if (candidate.Id == setId)
                        {
                            if (candidate != Toolbar.SelectedItem)
                            {
                                Toolbar.SelectedItem = candidate;
                            }

                            break;
                        }
                    }
                }
            }
#else
            var scrollingHost = List.ItemsPanelRoot as ItemsWrapGrid;
            if (scrollingHost != null && _isActive && scrollingHost.FirstVisibleIndex >= 0)
            {
                //var item = List.Items[scrollingHost.FirstVisibleIndex];
                //if (item is StickerViewModel sticker && sticker.SetId != _selectedSetId)
                //{
                //    _selectedSetId = sticker.SetId;

                //    if (ViewModel != null && ViewModel.TryGetInstalledSet(sticker.SetId, out var stickerSet))
                //    {
                //        Toolbar.SelectedItem = stickerSet;
                //        Toolbar.ScrollIntoView(stickerSet);
                //    }
                //}

                var first = List.ContainerFromIndex(scrollingHost.FirstVisibleIndex);
                if (first != null)
                {
                    var header = List.GroupHeaderContainerFromItemContainer(first) as GridViewHeaderItem;
                    if (header != null && header.Content != Toolbar.SelectedItem)
                    {
                        Toolbar.SelectedItem = header.Content;
                    }
                }
            }
#endif

            if (sender is ScrollViewer scrollViewer && scrollViewer.VerticalOffset > 0 && e.IsIntermediate)
            {
                ChoosingItem?.Invoke(this, EventArgs.Empty);
            }
        }

        private async void OnChoosingGroupHeaderContainer(ListViewBase sender, ChoosingGroupHeaderContainerEventArgs args)
        {
            if (args.GroupHeaderContainer == null)
            {
                args.GroupHeaderContainer = new GridViewHeaderItem();
                args.GroupHeaderContainer.Style = sender.GroupStyle[0].HeaderContainerStyle;
                args.GroupHeaderContainer.ContentTemplate = sender.GroupStyle[0].HeaderTemplate;
            }

            if (args.Group is StickerSetViewModel group && !group.IsLoaded)
            {
                group.IsLoaded = true;

                //Debug.WriteLine("Loading sticker set " + group.Id);

                var response = await ViewModel.ClientService.SendAsync(new GetStickerSet(group.Id));
                if (response is StickerSet full && IsConnected)
                {
                    group.Update(full, false);

                    //return;

                    foreach (var sticker in group.Stickers)
                    {
                        if (sticker.StickerValue != null && _itemIdToContent.TryGetValue(sticker, out Grid content))
                        {
                            var animation = content.Children[0] as AnimatedImage;
                            animation.Source = new DelayedFileSource(ViewModel.ClientService, sticker);
                        }
                    }
                }
            }
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                var item = new GridViewItem();
                item.ContentTemplate = sender.ItemTemplate;
                item.Style = sender.ItemContainerStyle;
                item.ContextRequested += OnContextRequested;
                args.ItemContainer = item;

                _zoomer.ElementPrepared(args.ItemContainer);
            }

            args.IsContainerPrepared = true;
        }

#if LINUX
        // ListViewBase.ChoosingGroupHeaderContainer is NotImplemented in Uno, exactly like
        // ChoosingItemContainer. Measured against the deployed Uno.UI.dll (6.6.184,
        // bin/Debug/net10.0-desktop): its NotImplemented table carries the literal
        //   "event TypedEventHandler<ListViewBase, ChoosingGroupHeaderContainerEventArgs>
        //    ListViewBase.ChoosingGroupHeaderContainer"
        // and so does the whole ChoosingGroupHeaderContainerEventArgs surface (Group,
        // GroupHeaderContainer, GroupIndex), while ContainerContentChanging carries no such
        // literal. So OnChoosingGroupHeaderContainer never runs.
        //
        // It did two jobs. Building the header container is not one that is lost: the XAML already
        // declares GroupStyle.HeaderTemplate and HeaderContainerStyle, and Uno implements both --
        // only GroupStyle.ContainerStyle, .Panel and GroupStyleSelector are NotImplemented, and
        // none is used here. The job that IS lost is the second one: fetching a sticker set the
        // first time its header scrolled into view. Without a substitute every installed set stays
        // a row of unfilled placeholders -- a panel that draws and shows nothing, which is the
        // outcome the porting rules single out as worse than not having it.
        //
        // The substitute takes the same trigger -- something belonging to this set became visible
        // -- from the event Uno does raise. An unloaded set is materialized as StickerViewModels
        // that carry nothing but their SetId (StickerDrawerViewModel.cs:616-621), so the first
        // placeholder of a set to be realized names the set to fetch. The body below is the body
        // of OnChoosingGroupHeaderContainer, unchanged.
        private async void EnsureGroupLoaded(StickerViewModel sticker)
        {
            if (sticker == null || sticker.StickerValue != null)
            {
                return;
            }

            var group = FindGroup(sticker.SetId);
            if (group == null || group.IsLoaded)
            {
                return;
            }

            group.IsLoaded = true;

            var response = await ViewModel.ClientService.SendAsync(new GetStickerSet(group.Id));
            if (response is StickerSet full)
            {
                group.Update(full, false);

                foreach (var item in group.Stickers)
                {
                    if (item.StickerValue != null && _itemIdToContent.TryGetValue(item, out Grid content))
                    {
                        if (content.Children[0] is AnimatedImage animation)
                        {
                            animation.Source = new DelayedFileSource(ViewModel.ClientService, item);
                        }
                    }
                }
            }
        }

        private StickerSetViewModel FindGroup(long setId)
        {
            if (setId == 0)
            {
                return null;
            }

            foreach (var candidate in ViewModel?.Stickers ?? (System.Collections.IEnumerable)System.Array.Empty<object>())
            {
                if (candidate is StickerSetViewModel set && set.Id == setId)
                {
                    return set;
                }
            }

            return null;
        }
#endif

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            var sticker = args.Item as StickerViewModel;

            if (args.InRecycleQueue || sticker == null)
            {
                if (sticker != null)
                {
                    _itemIdToContent.Remove(sticker);
                }

                return;
            }

#if LINUX
            // OnChoosingItemContainer never runs on Uno: ListViewBase.ChoosingItemContainer is
            // NotImplemented and its add accessor only calls TryRaiseNotImplemented, so the handler
            // is not even stored (PORTING.md 6). Style and ContentTemplate survive because the XAML
            // declares both and the default container path applies them; the ContextRequested hook
            // and the zoomable-preview registration do not, so they are re-attached here, from the
            // event Uno DOES raise. Telegram.Linux/Xaml/DrawerContainers.cs keeps it idempotent
            // across container recycling.
            DrawerContainers.Prepare(args.ItemContainer, OnContextRequested, _zoomer.ElementPrepared);
#endif

#if LINUX
            // The lazy sticker-set fetch that OnChoosingGroupHeaderContainer used to do; see
            // EnsureGroupLoaded above for why it has to hang off this event instead.
            EnsureGroupLoaded(sticker);
#endif

#if LINUX
            // See AnimationDrawer for why the content root is reached this way. _itemIdToContent is
            // filled inside the callback rather than before it, because until the presenter has
            // expanded there is no Grid to register -- and the group-header path looks the sticker
            // up in that map to swap its source in once the set finishes loading.
            var service = ViewModel.ClientService;
            DrawerContainers.WithContentRoot<Grid>(sender, args.ItemContainer, args.Item, content =>
            {
                _itemIdToContent[sticker] = content;

                if (content.Children[0] is AnimatedImage animation)
                {
                    animation.Source = sticker.StickerValue != null
                        ? new DelayedFileSource(service, sticker)
                        : null;
                }
            });
#else
            var content = args.ItemContainer.ContentRoot() as Grid;

            _itemIdToContent[sticker] = content;

            if (sticker?.StickerValue != null)
            {
                var animation = content.Children[0] as AnimatedImage;
                animation.Source = new DelayedFileSource(ViewModel.ClientService, sticker);
            }
            else
            {
                _itemIdToContent[sticker] = content;

                var animation = content.Children[0] as AnimatedImage;
                animation.Source = null;
            }
#endif

            args.Handled = true;
        }

        private void Toolbar_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.Item is SupergroupStickerSetViewModel supergroup)
            {
                Automation.SetToolTip(args.ItemContainer, supergroup.Title);

                var chat = ViewModel.ClientService.GetChat(supergroup.ChatId);
                if (chat == null)
                {
                    return;
                }

                var content = args.ItemContainer.ContentRoot() as Border;
                if (content?.Child is not ProfilePicture photo)
                {
                    return;
                }

                photo.Source = ProfilePictureSource.Chat(ViewModel.ClientService, chat);
                args.Handled = true;
            }
            else if (args.Item is StickerSetViewModel sticker)
            {
                Automation.SetToolTip(args.ItemContainer, sticker.Title);

                var content = args.ItemContainer.ContentRoot() as Grid;
                if (content?.Children[0] is FontIcon icon)
                {
                    icon.Glyph = sticker.Name switch
                    {
                        "tg/favedStickers" => Icons.Bookmark,
                        "tg/recentlyUsed" => Icons.EmojiRecents,
                        "tg/collectibles" => Icons.Diamond,
                        _ => string.Empty
                    };
                }
                else if (content?.Children[0] is AnimatedImage animated)
                {
                    animated.Source = DelayedFileSource.FromStickerSetInfo(ViewModel.ClientService, sticker);
                }

                args.Handled = true;
            }
        }

        private void SearchField_TextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.Search(SearchField.Text, false);
        }

        private void SearchField_CategorySelected(object sender, EmojiCategorySelectedEventArgs e)
        {
            ViewModel.Search(e.Category.Source);
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var sticker = List.ItemFromContainer(sender) as StickerViewModel;
            if (sticker == null)
            {
                return;
            }

            ItemContextRequested?.Invoke(sender, new ItemContextRequestedEventArgs<Sticker>(sticker, args));
        }

        private void Player_Ready(object sender, EventArgs e)
        {
            _handler.ThrottleVisibleItems();
        }

        private void Toolbar_Ready(object sender, EventArgs e)
        {
            _toolbarHandler.ThrottleVisibleItems();
        }

        private void Toolbar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingSelectedItem)
            {
                return;
            }

            _updatingSelectedItem = true;
            VisualUtilities.QueueCallbackForCompositionRendering(UpdateSelectedItem);
        }

        private bool _updatingSelectedItem;

        private void UpdateSelectedItem()
        {
            _updatingSelectedItem = false;

            if (Toolbar.SelectedItem != null)
            {
                _ = Toolbar.ScrollToItem2(Toolbar.SelectedItem, VerticalAlignment.Center);
            }
        }
    }
}
