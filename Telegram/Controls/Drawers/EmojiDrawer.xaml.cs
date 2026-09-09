//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Telegram.Collections;
using Telegram.Common;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels.Drawers;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using StickerSetViewModel = Telegram.ViewModels.Drawers.StickerSetViewModel;

namespace Telegram.Controls.Drawers
{
    public partial class TopicsEmojiDrawer : EmojiDrawer
    {
        public TopicsEmojiDrawer()
            : base(EmojiDrawerMode.Topics)
        {

        }
    }

    public partial class ChatPhotoEmojiDrawer : EmojiDrawer
    {
        public ChatPhotoEmojiDrawer()
            : base(EmojiDrawerMode.ChatPhoto)
        {

        }
    }

    public partial class EmojiDrawerItemClickEventArgs : EventArgs
    {
        public EmojiDrawerItemClickEventArgs(object clickedItem)
        {
            ClickedItem = clickedItem;
        }

        public Sticker Sticker { get; }

        public object ClickedItem { get; }
    }

    public partial class EmojiDrawer : UserControlEx, IDrawer
    {
        public EmojiDrawerViewModel ViewModel => DataContext as EmojiDrawerViewModel;

        public event EventHandler<EmojiDrawerItemClickEventArgs> ItemClick;
        public event TypedEventHandler<UIElement, ItemContextRequestedEventArgs<StickerViewModel>> ItemContextRequested;

        private EmojiDrawerMode _mode;

        private bool _isActive;

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

        public EmojiDrawer()
            : this(EmojiDrawerMode.Chat)
        {

        }

        public EmojiDrawer(EmojiDrawerMode mode)
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
            var defaultTrigger = new FluidGridViewTrigger { RowsOrColumns = 8 };
            defaultTrigger.Activated += FluidGridViewTrigger_Activated;
            FluidGridView.GetTriggers(List).Add(defaultTrigger);

#if LINUX
            List.ItemTemplateSelector = new EmojiTemplateSelector(this);

            // The repeater replaces the grid on this head; see the comment on RepeaterHost in the
            // XAML. Cell is 40 in the composer (8 to a row across a ~320px drawer, which is what
            // the RowsOrColumns=8 trigger above asks for) and 36 in the picker modes, which set a
            // FixedGridViewTrigger of exactly that below.
            _cell = mode == EmojiDrawerMode.Chat ? 40 : 36;

            // The two templates differ only in the UniformGridLayout minimums, which have to be
            // in the markup rather than assigned in ElementPrepared -- see the comment above them.
            // The 40 one is the markup default, so the composer never depends on this lookup; the
            // picker modes swap it, and keep the 40 cell rather than no template if that fails.
            // (Resources holds a WeakResourceInitializer, not the DataTemplate, until it is read.)
            if (_cell != 40)
            {
                if (Resources["LinuxEmojiRowTemplate36"] is DataTemplate picker)
                {
                    Repeater.ItemTemplate = picker;
                }
                else
                {
                    Logger.Warning("emoji drawer: LinuxEmojiRowTemplate36 did not resolve; keeping the 40 cell");
                }
            }

            // Prepared/Clearing are hooked ONLY to count what is live: see OnRowRealized.
            // Nothing about a row's size is decided there.
            Repeater.ElementPrepared += OnRowPrepared;
            Repeater.ElementClearing += OnRowCleared;

            // Only the width is watched: the rows are BUILT from the column count, so they have
            // to be rebuilt when it changes.
            RepeaterHost.SizeChanged += OnRepeaterHostSizeChanged;
            // No width watcher: each group's ItemsControl wraps itself to whatever width it is
            // handed, so the column count nobody computes any more cannot go stale either.
#endif

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

            _handler = new AnimatedListHandler(List, AnimatedListType.Emoji);
            _toolbarHandler = new AnimatedListHandler(Toolbar2, AnimatedListType.Emoji);

            _zoomer = new ZoomableListHandler(List);
            _zoomer.Opening += Zoomer_Opening;
            _zoomer.Closing += Zoomer_Closing;

            _typeToItemHashSetMapping.Add("EmojiSkinTemplate", new HashSet<SelectorItem>());
            _typeToItemHashSetMapping.Add("EmojiTemplate", new HashSet<SelectorItem>());
            _typeToItemHashSetMapping.Add("ItemTemplate", new HashSet<SelectorItem>());
            _typeToItemHashSetMapping.Add("MoreTemplate", new HashSet<SelectorItem>());

            _mode = mode;

            if (mode == EmojiDrawerMode.Topics)
            {
                TopicIconRoot.Visibility = Visibility.Visible;
            }
            else if (mode is EmojiDrawerMode.EmojiStatus or EmojiDrawerMode.ChatEmojiStatus)
            {
                EmojiStatusIconRoot.Visibility = Visibility.Visible;
            }

            if (mode != EmojiDrawerMode.Chat)
            {
                SearchField.Margin = new Thickness(0, 8, 8, 8);
                Toolbar3.Visibility = Visibility.Collapsed;
                Toolbar2.Header = null;

                if (mode is not EmojiDrawerMode.ChatPhoto and not EmojiDrawerMode.UserPhoto)
                {
                    List.Padding = new Thickness(8, 0, 0, 0);
                    List.ItemContainerStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 4, 4)));
                    List.GroupStyle[0].HeaderContainerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0, 0, 8, 0)));

                    var trigger = new FixedGridViewTrigger { ItemLength = 36 };
                    trigger.Activated += FluidGridViewTrigger_Activated;

                    FluidGridView.GetTriggers(List).Clear();
                    FluidGridView.GetTriggers(List).Add(trigger);
                }
            }
            else
            {
                UpdateView();
            }

            _typing = new EventDebouncer<TextChangedEventArgs>(Constants.TypingTimeout, handler => SearchField.TextChanged += new TextChangedEventHandler(handler));
            _typing.Invoked += (s, args) =>
            {
                if (string.IsNullOrWhiteSpace(SearchField.Text))
                {
#if LINUX
                    RefreshRepeaterLinux("search cleared");
#else
                    List.ItemsSource = EmojiCollection.View;
#endif
                }
                else if (ViewModel != null)
                {
                    var results = new SearchEmojiCollection(ViewModel.ClientService, SearchField.Text, _mode);
#if LINUX
                    ShowFlatLinux(results);
#else
                    List.ItemsSource = results;
#endif
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

        public void HideNavigation()
        {
            ToolbarContainer.Visibility = Visibility.Collapsed;
            SearchField.Visibility = Visibility.Collapsed;
            List.Padding = new Thickness(8, 8, 0, 0);
        }

        public void UpdateTopicIcon(string name, int color)
        {
            var brush = ForumTopicCell.GetIconGradient(new ForumTopicIcon(color, 0));

            TopicIconPath.Fill = brush;
            TopicIconPath.Stroke = new SolidColorBrush(brush.GradientStops[1].Color);
            TopicIconText.Text = InitialNameStringConverter.Convert(name);
        }

        public bool IsShadowVisible
        {
            get => Separator.Visibility == Visibility.Visible;
            set => Separator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public StickersTab Tab => StickersTab.Emoji;

        public Thickness ScrollingHostPadding
        {
            get => List.Padding;
            set => List.Padding = new Thickness(value.Left, value.Top, value.Right, value.Bottom);
        }

        public ListViewBase ScrollingHost => List;

        public void Activate(Chat chat, EmojiSearchType type = EmojiSearchType.Default)
        {
            _isActive = true;
            _handler.Resume();
            _toolbarHandler.ThrottleVisibleItems();

            if (ViewModel.IsPremium)
            {
                SearchField.SetType(ViewModel.ClientService, _mode switch
                {
                    EmojiDrawerMode.ChatPhoto => EmojiSearchType.ChatPhoto,
                    EmojiDrawerMode.UserPhoto => EmojiSearchType.ChatPhoto,
                    EmojiDrawerMode.EmojiStatus => EmojiSearchType.EmojiStatus,
                    EmojiDrawerMode.ChatEmojiStatus => EmojiSearchType.EmojiStatus,
                    EmojiDrawerMode.Reactions => EmojiSearchType.Combined,
                    _ => EmojiSearchType.Default
                });
            }

            ViewModel.OpenChat(chat);
            ViewModel.Update();

#if LINUX
            BindSourceLinux();
#endif
        }

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

            // StopTracking doesn't clear what the bindings already pushed: the collection view
            // source and the two toolbars still hold a CollectionChanged handler on the view
            // model's collections, and UnloadObject is about to destroy their native peers.
            // The view model outlives us (it stays subscribed to the aggregator, and Handle
            // queues Update on the UI thread), so leaving them attached means the next
            // ReplaceWith raises into a dead RCW.
            List.ItemsSource = null;
            EmojiCollection.Source = null;
            Toolbar.ItemsSource = null;
            Toolbar2.ItemsSource = null;

#if LINUX
            UnbindRepeaterLinux();
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var scrollingHost = List.GetChild<ScrollViewer>();
            if (scrollingHost != null)
            {
                scrollingHost.VerticalSnapPointsType = SnapPointsType.None;

                // Syncronizes GridView with the toolbar ListView
                scrollingHost.ViewChanged += ScrollingHost_ViewChanged;
                ScrollingHost_ViewChanged(null, null);
            }

            UpdateToolbar(true);
        }

        public void UpdateView()
        {
            if (_mode is not EmojiDrawerMode.ChatPhoto and not EmojiDrawerMode.UserPhoto and not EmojiDrawerMode.Chat)
            {
                return;
            }

            UpdateToolbar();
        }

        private void ScrollingHost_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
#if LINUX
            // Same pair of NotImplemented APIs as StickerDrawer.ScrollingHost_ViewChanged:
            // ItemsWrapGrid.FirstVisibleIndex THROWS on Uno and this runs on every scroll tick, and
            // ItemsControl.GroupHeaderContainerFromItemContainer cannot return a header container.
            // The first visible ITEM is found by geometry and the group resolved from its SetId.
            //
            // HONEST LIMIT, and the reason this is narrower than StickerDrawer's: this list mixes
            // three kinds of group -- EmojiGroup, RecentEmoji and StickerSetViewModel -- and only
            // the last is reachable from an item, because only StickerViewModel carries a SetId. A
            // plain EmojiData says nothing about which EmojiGroup holds it, and the only way back
            // would be scanning every group's contents on every scroll tick. So while the custom
            // emoji sets scroll the bottom toolbar along with them, the standard emoji categories
            // do NOT highlight as you scroll past them. Clicking a category still works -- that is
            // Toolbar_ItemClick, which does not go through here. Written down rather than papered
            // over; see FALTA 1.4.
            if (_isActive && DrawerContainers.FirstVisibleItem(List) is StickerViewModel visible)
            {
                var setId = visible.SetId;

                if (setId != 0 && ViewModel != null && ViewModel.TryGetInstalledSet(setId, out var installed))
                {
                    if (installed != Toolbar2.SelectedItem)
                    {
                        Toolbar2.SelectedItem = installed;
                        Toolbar.SelectedItem = null;
                        UpdateToolbar();
                    }
                }
            }
#else
            var scrollingHost = List.ItemsPanelRoot as ItemsWrapGrid;
            if (scrollingHost != null && _isActive && scrollingHost.FirstVisibleIndex >= 0)
            {
                var first = List.ContainerFromIndex(scrollingHost.FirstVisibleIndex);
                if (first != null)
                {
                    var header = List.GroupHeaderContainerFromItemContainer(first) as GridViewHeaderItem;
                    if (header != null && header != Toolbar.SelectedItem)
                    {
                        if (header.Content is EmojiGroup)
                        {
                            Toolbar2.SelectedItem = null;
                            Toolbar.SelectedItem = header.Content;
                        }
                        else
                        {
                            Toolbar2.SelectedItem = header.Content;
                            Toolbar.SelectedItem = null;
                        }

                        UpdateToolbar();
                    }
                }
            }
#endif
        }

        private void Toolbar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is GridView toolbar)
            {
                if (toolbar.SelectedItem != null)
                {
                    if (sender == Toolbar2 && Toolbar.SelectedItem != null)
                    {
                        toolbar.ScrollToTop();
                    }
                    else
                    {
                        _ = toolbar.ScrollToItem2(toolbar.SelectedItem, VerticalAlignment.Center);
                    }
                }
                else
                {
                    toolbar.ScrollToTop();
                }
            }
        }

        private void Toolbar_ItemClick(object sender, ItemClickEventArgs e)
        {
            List.ScrollIntoView(e.ClickedItem, ScrollIntoViewAlignment.Leading);
        }

        public void InsertEmoji(EmojiSkinData emoji)
        {
            AppSettings.Emoji.SetEmojiSkinTone(emoji);
            ViewModel.Settings.RecentEmoji.AddRecentEmoji(emoji);
            ItemClick?.Invoke(this, new EmojiDrawerItemClickEventArgs(emoji));
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            HandleItemClicked(e.ClickedItem, ScrollingHost.ContainerFromItem(e.ClickedItem) as UIElement);
        }

        // The body of ListView_ItemClick, taking the clicked item and the element the skin-tone
        // flyout anchors to, so the repeater path on Linux runs the very same code. A repeater
        // has no container to ask for -- its element IS the template root -- so the caller there
        // passes the tapped element itself.
        private async void HandleItemClicked(object clickedItem, UIElement container)
        {
            if (clickedItem is EmojiData data)
            {
                if (data is EmojiSkinData skin && !AppSettings.Emoji.HasSkinTone(skin))
                {
                    if (container != null)
                    {
                        var flyout = new Flyout
                        {
                            FlyoutPresenterStyle = BootStrapper.Current.Resources["CommandFlyoutPresenterStyle"] as Style,
                        };

                        flyout.Content = new EmojiSkinFlyout(this, flyout, skin);
                        flyout.ShowAt(container, FlyoutPlacementMode.Top);
                        return;
                    }
                }

                ViewModel.Settings.RecentEmoji.AddRecentEmoji(data);
                // NOT e.ClickedItem (upstream): this body was extracted out of ListView_ItemClick
                // so the Linux repeater path can call it too, and there is no ItemClickEventArgs here.
                ItemClick?.Invoke(this, new EmojiDrawerItemClickEventArgs(clickedItem));
            }
            else if (clickedItem is StickerViewModel sticker)
            {
                if (sticker is MoreStickerViewModel)
                {
#if LINUX
                    // ItemsControl.GroupHeaderContainerFromItemContainer is NotImplemented on Uno
                    // and hands back null, and the line below dereferenced it with no check at all:
                    // clicking the "more" tile of a custom emoji set crashed the app. Same
                    // substitute as ScrollingHost_ViewChanged above -- resolve the group by SetId
                    // -- except that here it is not a degradation but the shorter road: the tile
                    // that was clicked already knows which set it belongs to, because a
                    // MoreStickerViewModel is constructed with that set's own Id
                    // (StickerDrawerViewModel:481). Walking up the container tree only ever
                    // recovered what the item already carried.
                    //
                    // Looked up in ViewModel.Items and deliberately NOT through
                    // TryGetInstalledSet: _installedSets holds separately fetched
                    // StickerSetViewModel instances, and Update() below has to land on the one
                    // this GridView is showing -- the one whose Stickers are the items registered
                    // in _itemIdToContent -- or the tiles it just refreshed are not the tiles on
                    // screen.
                    var group = ViewModel.Items.OfType<StickerSetViewModel>().FirstOrDefault(x => x.Id == sticker.SetId);
#else
                    var groupContainer = List.GroupHeaderContainerFromItemContainer(List.ContainerFromItem(sticker)) as GridViewHeaderItem;

                    // `?.`: the container is also null here whenever the header has been
                    // virtualised away, which was the same unguarded dereference on Windows.
                    var group = groupContainer?.Content as StickerSetViewModel;
#endif
                    if (group != null)
                    {
                        var response = await ViewModel.ClientService.SendAsync(new GetStickerSet(group.Id));
                        if (response is StickerSet full)
                        {
                            group.Update(full, false);

                            foreach (var item in group.Stickers)
                            {
                                if (_itemIdToContent.TryGetValue(item, out Grid content))
                                {
                                    var animation = content.Children[0] as AnimatedImage;
                                    animation.Source = new DelayedFileSource(ViewModel.ClientService, item);
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (sticker.FullType is StickerFullTypeCustomEmoji customEmoji)
                    {
                        ViewModel.Settings.RecentEmoji.AddRecentEmoji(sticker.Emoji, customEmoji.CustomEmojiId);
                    }

                    ItemClick?.Invoke(this, new EmojiDrawerItemClickEventArgs(clickedItem));
                }
            }
        }

        private async void SearchField_CategorySelected(object sender, EmojiCategorySelectedEventArgs e)
        {
            if (e.Category.Source is EmojiCategorySourceSearch search)
            {
                var results = await Emoji.SearchAsync(ViewModel.ClientService, search.Emojis);

#if LINUX
                ShowFlatLinux(results);
#else
                List.ItemsSource = results;
#endif
            }
        }

#if LINUX

        private System.Collections.Specialized.INotifyCollectionChanged _boundLinux;

        private double _cell = 40;

        // A title row, and the cap that keeps a row's cell count small and fixed however wide the
        // host gets - without it the invariant this design holds would mean nothing.
        private const double TitleRow = 30;
        private const int MaxColumns = 12;

        private int _columns;
        private System.Collections.IEnumerable _pendingGroups;

        /// <summary>
        /// One group for a FLAT source, so search results can go through the same repeater the
        /// grouped list uses.
        /// </summary>
        /// <remarks>
        /// The search paths hand the list a plain collection rather than groups, and the outer
        /// repeater's template reads Title and Stickers off its item. A null Title is what hides
        /// the header, exactly as the GroupStyle's own template did with its
        /// NullToVisibilityConverter.
        /// </remarks>
        private sealed class FlatGroup
        {
            public FlatGroup(object items)
            {
                Stickers = items;
            }

            public string Title => null;

            public object Stickers { get; }
        }

        /// <summary>
        /// Points the repeater at the drawer's groups and empties the GridView, which cannot
        /// render them.
        /// </summary>
        /// <remarks>
        /// Three separate breaks, the same three Oscar measured on the sticker drawer (ddb6d40).
        /// The CollectionViewSource's Source is an x:Bind with NO Mode, so it delivers ONCE while
        /// ViewModel.Items is still empty and never again. The GridView's own ItemsSource x:Bind
        /// to EmojiCollection.View is OneWay but the grouped view does not rebuild when the source
        /// collection fills. And no panel Uno ships both wraps and virtualizes. So the source is
        /// assigned by hand, re-assigned whenever the collection changes, and handed to a repeater
        /// rather than to the grid.
        /// </remarks>
        private void BindSourceLinux()
        {
            var items = ViewModel?.Items;

            if (items == null)
            {
                return;
            }

            if (!ReferenceEquals(_boundLinux, items))
            {
                if (_boundLinux != null)
                {
                    _boundLinux.CollectionChanged -= OnItemsChangedLinux;
                }

                _boundLinux = items;
                _boundLinux.CollectionChanged += OnItemsChangedLinux;
            }

            RefreshRepeaterLinux("activate");
        }

        private void UnbindRepeaterLinux()
        {
            if (_boundLinux != null)
            {
                _boundLinux.CollectionChanged -= OnItemsChangedLinux;
                _boundLinux = null;
            }

            Repeater.ItemsSource = null;
            _pendingGroups = null;
        }

        private void OnItemsChangedLinux(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // ViewModel.Update is async void over TDLib, so this can arrive off the view thread;
            // touching the repeater from there throws, and an exception raised inside a
            // CollectionChanged handler goes nowhere at all.
            this.BeginOnUIThread(() => RefreshRepeaterLinux("collection changed"));
        }

        private void RefreshRepeaterLinux(string when)
        {
            try
            {
                var items = ViewModel?.Items;

                if (items == null)
                {
                    return;
                }

                _pendingGroups = items;

                // Shown BEFORE it is given anything: a collapsed element gets no layout, so the
                // width the rows are built from does not exist until the host has been measured
                // once. Empty, that pass costs nothing.
                RepeaterHost.Visibility = Visibility.Visible;
                ApplyOrDeferRowsLinux();

                // The GridView stays in the tree because IDrawer.ScrollingHost is typed
                // ListViewBase and StickerPanel reads it, but it must hold nothing: a
                // non-virtualizing panel builds a container per emoji whether it is visible or not.
                List.ItemsSource = null;
                List.Visibility = Visibility.Collapsed;

                Logger.Info($"emoji drawer: repeater bound on {when} -- {items.Count} group(s), cell {_cell}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"emoji drawer: binding the repeater on {when} failed");
            }
        }

        /// <summary>
        /// Shows a FLAT result set (the two search paths) through the same repeater.
        /// </summary>
        private void ShowFlatLinux(object items)
        {
            try
            {
                _pendingGroups = new[] { new FlatGroup(items) };
                RepeaterHost.Visibility = Visibility.Visible;
                ApplyOrDeferRowsLinux();

                List.ItemsSource = null;
                List.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "emoji drawer: showing a flat result set failed");
            }
        }


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

            Logger.Info($"emoji drawer: row {what}{(index < 0 ? string.Empty : " " + index)} -- " +
                $"realized={_realizedRows} peak={_realizedRowsPeak} of {total} row(s)");
        }

        private void OnRepeaterHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var available = e.NewSize.Width;

            if (available <= 0)
            {
                return;
            }

            var columns = Math.Min(MaxColumns, Math.Max(1, (int)(available / _cell)));

            if (columns == _columns)
            {
                return;
            }

            _columns = columns;
            ApplyRowsLinux();
        }

        private void ApplyOrDeferRowsLinux()
        {
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
        }

        /// <summary>
        /// Flattens the groups into rows and hands them over.
        /// </summary>
        /// <remarks>
        /// Rebuilt rather than adjusted, because the rows ARE the column count: a different width
        /// slices the same items into different lines. It happens on a width change and on a
        /// collection change, neither of which is a layout-time event.
        /// </remarks>
        private void ApplyRowsLinux()
        {
            if (Repeater == null || _pendingGroups == null || _columns <= 0)
            {
                return;
            }

            var rows = DrawerRow.Build(_pendingGroups, _columns, _cell, TitleRow);
            Repeater.ItemsSource = rows;

            Logger.Info($"emoji drawer: {rows.Count} row(s) built, {_columns} column(s) of {_cell}");
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
                Logger.Warning("emoji drawer: the repeater host still has no width; no rows can be built yet");
                return;
            }

            _columns = Math.Min(MaxColumns, Math.Max(1, (int)(available / _cell)));
            ApplyRowsLinux();
        }

        /// <summary>
        /// Gives one cell the face its item needs. This is what the four DataTemplates and their
        /// selector did; see the comment on LinuxEmojiCellTemplate for why a selector cannot be
        /// used on a repeater here.
        /// </summary>
        /// <summary>
        /// Gives one cell the face its item needs. An ItemsControl raises no element events, so
        /// the cell reports for duty itself; its DataContext is already the item.
        /// </summary>
        private void OnEmojiCellLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid content)
            {
                return;
            }

            // Both axes, in code, because one cell template serves both modes and a row lays its
            // cells out side by side - an unsized cell would measure to its glyph and the line
            // would come out ragged.
            content.Width = _cell;
            content.Height = _cell;

            var glyph = FindDescendant<TextBlock>(content, "Glyph");
            var player = FindDescendant<AnimatedImage>(content, "Player");
            var more = FindDescendant<Grid>(content, "More");

            if (glyph == null || player == null || more == null)
            {
                return;
            }

            var item = content.DataContext;

            if (item is MoreStickerViewModel moreSticker)
            {
                glyph.Visibility = Visibility.Collapsed;
                player.Visibility = Visibility.Collapsed;
                more.Visibility = Visibility.Visible;

                if (FindDescendant<TextBlock>(more, "MoreText") is TextBlock text)
                {
                    text.Text = $"+{moreSticker.TotalCount}";
                }
            }
            else if (item is StickerViewModel sticker)
            {
                glyph.Visibility = Visibility.Collapsed;
                more.Visibility = Visibility.Collapsed;
                player.Visibility = Visibility.Visible;

                _itemIdToContent[sticker] = content;

                using (player.BeginBatchUpdate())
                {
                    var side = _cell - 8;

                    player.Width = side;
                    player.Height = side;
                    player.FrameSize = new Size(side, side);
                    player.Source = sticker.StickerValue != null
                        ? new DelayedFileSource(ViewModel.ClientService, sticker)
                        : null;
                }

                // The lazy sticker-set fetch OnChoosingGroupHeaderContainer used to do, and which
                // OnContainerContentChanging carries on the GridView path.
                EnsureGroupLoaded(sticker);
            }
            else if (item is EmojiData emoji)
            {
                player.Visibility = Visibility.Collapsed;
                more.Visibility = Visibility.Collapsed;
                glyph.Visibility = Visibility.Visible;
                glyph.Text = emoji.Value;
            }

            // OnChoosingItemContainer is dead on Uno and a repeater has no container anyway, so the
            // context menu hangs off the element itself and reads its item off the DataContext.
            content.ContextRequested -= OnRepeaterContextRequested;
            content.ContextRequested += OnRepeaterContextRequested;
        }

        private void OnRepeaterContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            if (sender is FrameworkElement { DataContext: StickerViewModel sticker })
            {
                ItemContextRequested?.Invoke(sender, new ItemContextRequestedEventArgs<StickerViewModel>(sticker, args));
            }
        }

        private void OnEmojiCellUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Grid content && FindDescendant<AnimatedImage>(content, "Player") is AnimatedImage player)
            {
                player.Source = null;
            }
        }

        private void OnEmojiTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: { } item } element)
            {
                HandleItemClicked(item, element);
            }
        }

        private static T FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root is T self && self.Name == name)
            {
                return self;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i), name);

                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
#endif

        private bool _emojiCollapsed = false;

        private void UpdateToolbar(bool collapse = false)
        {
            if (_mode is not EmojiDrawerMode.ChatPhoto and not EmojiDrawerMode.UserPhoto and not EmojiDrawerMode.Chat)
            {
                return;
            }

            if (Toolbar2.ItemsPanelRoot == null)
            {
                return;
            }

            var collapsed = Toolbar.SelectedItem == null;
            if (collapsed != _emojiCollapsed || collapse)
            {
                _emojiCollapsed = collapsed;

                var show = !_emojiCollapsed;

                var toolbar = ElementComposition.GetElementVisual(Toolbar3);
                var pill = ElementComposition.GetElementVisual(ToolbarPill);
                var panel = ElementComposition.GetElementVisual(Toolbar2.ItemsPanelRoot);

                ElementCompositionPreview.SetIsTranslationEnabled(Toolbar2.ItemsPanelRoot, true);

                var clip = toolbar.Compositor.CreateInsetClip();
                var offset = 144 - 32;

                var ellipse = toolbar.Compositor.CreateRoundedRectangleGeometry();
                ellipse.CornerRadius = new Vector2(4);

                pill.Clip = toolbar.Compositor.CreateGeometricClip(ellipse);
                toolbar.Clip = clip;
                Toolbar3.Width = 144;

                var animClip = toolbar.Compositor.CreateScalarKeyFrameAnimation();
                animClip.InsertKeyFrame(show ? 1 : 0, 0);
                animClip.InsertKeyFrame(show ? 0 : 1, offset);

                var animOffset = toolbar.Compositor.CreateScalarKeyFrameAnimation();
                animOffset.InsertKeyFrame(show ? 0 : 1, -offset);
                animOffset.InsertKeyFrame(show ? 1 : 0, 0);

                var animSize = toolbar.Compositor.CreateVector2KeyFrameAnimation();
                animSize.InsertKeyFrame(show ? 0 : 1, new Vector2(32, 32));
                animSize.InsertKeyFrame(show ? 1 : 0, new Vector2(32 + offset, 32));

                var animOpacity = toolbar.Compositor.CreateScalarKeyFrameAnimation();
                animOpacity.InsertKeyFrame(show ? 0 : 1, 0);
                animOpacity.InsertKeyFrame(show ? 1 : 0, 1);

                var batch = toolbar.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                batch.Completed += (s, args) =>
                {
                    panel.Properties.InsertVector3("Translation", Vector3.Zero);

                    toolbar.Clip = null;
                    Toolbar3.Width = show ? 144 : 32;
                };

                clip.StartAnimation("RightInset", animClip);
                panel.StartAnimation("Translation.X", animOffset);
                ellipse.StartAnimation("Size", animSize);
                pill.StartAnimation("Opacity", animOpacity);

                batch.End();
            }
        }

        #region Recycle

        private readonly Dictionary<string, HashSet<SelectorItem>> _typeToItemHashSetMapping = new();

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            var typeName = args.Item is MoreStickerViewModel
                ? "MoreTemplate"
                : args.Item is StickerViewModel sticker
                    ? "ItemTemplate"
                    : args.Item is EmojiSkinData ? "EmojiSkinTemplate" : "EmojiTemplate";

            var relevantHashSet = _typeToItemHashSetMapping[typeName];

            // args.ItemContainer is used to indicate whether the ListView is proposing an
            // ItemContainer (ListViewItem) to use. If args.Itemcontainer != null, then there was a
            // recycled ItemContainer available to be reused.
            if (args.ItemContainer is EmojiGridViewItem container)
            {
                if (container.TypeName.Equals(typeName))
                {
                    // Suggestion matches what we want, so remove it from the recycle queue
                    relevantHashSet.Remove(args.ItemContainer);
                }
                else
                {
                    // The ItemContainer's datatemplate does not match the needed
                    // datatemplate.
                    // Don't remove it from the recycle queue, since XAML will resuggest it later
                    args.ItemContainer = null;
                }
            }

            // If there was no suggested container or XAML's suggestion was a miss, pick one up from the recycle queue
            // or create a new one
            if (args.ItemContainer == null)
            {
                // See if we can fetch from the correct list.
                if (relevantHashSet.Count > 0)
                {
                    // Unfortunately have to resort to LINQ here. There's no efficient way of getting an arbitrary
                    // item from a hashset without knowing the item. Queue isn't usable for this scenario
                    // because you can't remove a specific element (which is needed in the block above).
                    args.ItemContainer = relevantHashSet.First();
                    relevantHashSet.Remove(args.ItemContainer);
                }
                else
                {
                    // There aren't any (recycled) ItemContainers available. So a new one
                    // needs to be created.
                    var item = new EmojiGridViewItem(typeName);
                    item.ContentTemplate = Resources[typeName] as DataTemplate;
                    item.Style = List.ItemContainerStyle;
                    item.ContextRequested += OnContextRequested;
                    args.ItemContainer = item;

                    _zoomer.ElementPrepared(args.ItemContainer);
                }
            }

            // Indicate to XAML that we picked a container for it
            args.IsContainerPrepared = true;
        }

        private async void OnChoosingGroupHeaderContainer(ListViewBase sender, ChoosingGroupHeaderContainerEventArgs args)
        {
            if (args.GroupHeaderContainer == null)
            {
                args.GroupHeaderContainer = new GridViewHeaderItem();
                args.GroupHeaderContainer.Style = List.GroupStyle[0].HeaderContainerStyle;
                args.GroupHeaderContainer.ContentTemplate = List.GroupStyle[0].HeaderTemplate;
            }

            if (args.Group is StickerSetViewModel group && !group.IsLoaded)
            {
                group.IsLoaded = true;

                //Debug.WriteLine("Loading sticker set " + group.Id);

                var response = await ViewModel.ClientService.SendAsync(new GetStickerSet(group.Id));
                if (response is StickerSet full && IsConnected)
                {
                    group.Update(full, false);

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

            foreach (var candidate in ViewModel?.Items ?? (System.Collections.IEnumerable)System.Array.Empty<object>())
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

            if (args.InRecycleQueue)
            {
                if (sticker != null)
                {
                    _itemIdToContent.Remove(sticker);
                }

                if (args.ItemContainer is EmojiGridViewItem container)
                {
                    // XAML has indicated that the item is no longer being shown, so add it to the recycle queue
                    var tag = container.TypeName;
                    var added = _typeToItemHashSetMapping[tag].Add(args.ItemContainer);
                }

                return;
            }
            else if (sticker != null)
            {
#if LINUX
                // OnChoosingItemContainer is dead on Uno (PORTING.md 6), so its two side effects --
                // the context menu subscription and the zoomable-preview registration -- are
                // re-attached here. Its third job, picking one of the four item templates, is done
                // by EmojiTemplateSelector instead, because a template has to be in place before
                // the container is populated.
                DrawerContainers.Prepare(args.ItemContainer, OnContextRequested, _zoomer.ElementPrepared);
#endif

#if LINUX
                // The lazy sticker-set fetch that OnChoosingGroupHeaderContainer used to do; see
                // EnsureGroupLoaded above for why it has to hang off this event instead.
                EnsureGroupLoaded(sticker);
#endif
                var content = args.ItemContainer.ContentRoot() as Grid;
                if (content == null)
                {
                    // Presenter not expanded yet: bind on the later pass rather than dropping the
                    // cell silently, which is what reading ContentTemplateRoot used to do.
                    var pending = sticker;
                    var index = args.ItemIndex;
                    DrawerContainers.WithContentRoot<Grid>(sender, args.ItemContainer, args.Item, late => OnStickerContentReady(late, pending, index));
                    return;
                }

                OnStickerContentReady(content, sticker, args.ItemIndex);
                args.Handled = true;
            }
        }


        // The tail of OnContainerContentChanging, split out so the deferred path (used when
        // the presenter has not expanded the item template yet) runs the very same code.
        // Unchanged apart from taking its three inputs as parameters.
        private void OnStickerContentReady(Grid content, StickerViewModel sticker, int itemIndex)
        {
            _itemIdToContent[sticker] = content;

            if (content.Children[0] is TextBlock textBlock && sticker is MoreStickerViewModel more)
            {
                textBlock.Text = $"+{more.TotalCount}";
            }
            else
            {
                if (sticker?.StickerValue != null)
                {
                    var animation = content.Children[0] as AnimatedImage;
                    animation.Source = new DelayedFileSource(ViewModel.ClientService, sticker);
                }
                else
                {
                    var animation = content.Children[0] as AnimatedImage;
                    animation.Source = null;
                }

                if (false && _mode == EmojiDrawerMode.Reactions && itemIndex > 5 && itemIndex < 8 * 6)
                {
                    var x1 = 4;
                    var y1 = 0;
                    var x2 = (int)(itemIndex % 8);
                    var y2 = (int)(itemIndex / 8d);

                    if (y2 >= 2)
                    {
                        y2++;
                    }

                    var xd = Math.Abs(x1 - x2);
                    var yd = Math.Abs(y1 - y2);

                    var distance = xd + yd - 1;
                    distance = yd;

                    var visual = ElementComposition.GetElementVisual(content);
                    var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
                    scale.InsertKeyFrame(0, Vector3.Zero);
                    scale.InsertKeyFrame(1, Vector3.One);
                    scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                    scale.DelayTime = TimeSpan.FromMilliseconds(33 * distance);
                    scale.Duration = Constants.FastAnimation;

                    var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
                    opacity.InsertKeyFrame(0, 0);
                    opacity.InsertKeyFrame(1, 1);
                    opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                    opacity.DelayTime = TimeSpan.FromMilliseconds(33 * distance);
                    opacity.Duration = Constants.FastAnimation;

                    visual.CenterPoint = new Vector3(16, 0, 0);
                    visual.StartAnimation("Opacity", opacity);
                    visual.StartAnimation("Scale", scale);
                }
            }
        }

        #endregion

        private void Toolbar_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.Item is StickerSetViewModel sticker)
            {
                Automation.SetToolTip(args.ItemContainer, sticker.Title);

                var content = args.ItemContainer.ContentRoot() as Grid;
                if (content?.Children[0] is FontIcon icon)
                {
                    icon.Glyph = sticker.Name switch
                    {
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

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var item = List.ItemFromContainer(sender);
            if (item is StickerViewModel sticker)
            {
                ItemContextRequested?.Invoke(sender, new ItemContextRequestedEventArgs<StickerViewModel>(sticker, args));
            }
            else if (item is EmojiSkinData emoji)
            {
                var flyout = new Flyout
                {
                    FlyoutPresenterStyle = BootStrapper.Current.Resources["CommandFlyoutPresenterStyle"] as Style,
                };

                flyout.Content = new EmojiSkinFlyout(this, flyout, emoji);
                flyout.ShowAt(sender, FlyoutPlacementMode.Top);
            }
        }

        private void Player_Ready(object sender, System.EventArgs e)
        {
            _handler.ThrottleVisibleItems();
        }

        private void Toolbar_Ready(object sender, System.EventArgs e)
        {
            _toolbarHandler.ThrottleVisibleItems();
        }

        private void FluidGridViewTrigger_Activated(object sender, double e)
        {
            DefaultIcon.Width = e;
            DefaultIcon.Height = e;
            DefaultIcon.Margin = new Thickness(0, 0, 0, -e);
        }
    }

#if LINUX
    /// <summary>
    /// The four item templates of the emoji grid, chosen the way OnChoosingItemContainer chose
    /// them. That handler is the ONLY place upstream sets an item template on this list -- the
    /// GridView declares none -- and Uno never raises ChoosingItemContainer (PORTING.md 6), so
    /// without this every cell in the panel comes up with no template at all: a grid of empty
    /// boxes, which is the "drawn and dead" outcome the porting rules single out. The framework
    /// applies ItemTemplateSelector from PrepareContainerForItemOverride, which Uno does raise;
    /// this is the same split SearchChatsView already uses for the identical problem.
    /// </summary>
    public partial class EmojiTemplateSelector : DataTemplateSelector
    {
        private readonly EmojiDrawer _owner;

        public EmojiTemplateSelector(EmojiDrawer owner)
        {
            _owner = owner;
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            return SelectTemplateCore(item);
        }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            // Same ladder, same order, same keys as OnChoosingItemContainer's typeName.
            var typeName = item is MoreStickerViewModel
                ? "MoreTemplate"
                : item is StickerViewModel
                    ? "ItemTemplate"
                    : item is EmojiSkinData ? "EmojiSkinTemplate" : "EmojiTemplate";

            return _owner.Resources[typeName] as DataTemplate;
        }
    }

#endif
    public partial class EmojiGridViewItem : GridViewItem
    {
        private readonly string _typeName;

        public EmojiGridViewItem(string typeName)
        {
            _typeName = typeName;
        }

        public string TypeName => _typeName;
    }
}
