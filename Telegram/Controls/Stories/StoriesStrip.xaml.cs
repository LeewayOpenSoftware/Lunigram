//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Specialized;
using System.Numerics;
using Telegram.Common;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Controls.Messages;
using Telegram.Td.Api;
#if !LINUX
// Neither namespace exists in the Linux subset: Telegram.ViewModels.Drawers is the emoji/sticker
// drawers, Telegram.Views.Stories.Popups is StealthPopup and StoryInteractionsPopup. A using for a
// namespace that no compiled file declares is CS0246, so it goes behind the same guard as the two
// call sites that need it (Icon_Click and OpenAnonymously).
using Telegram.ViewModels.Drawers;
#endif
using Telegram.ViewModels.Stories;
#if !LINUX
using Telegram.Views.Stories.Popups;
#endif
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls.Stories
{
    public sealed partial class StoriesStrip : UserControlEx
    {
        public StoryListViewModel ViewModel => DataContext as StoryListViewModel;

        public StoriesStrip()
        {
            InitializeComponent();
#if LINUX
            // The strip rides in the 40px caption row next to the Telegram logo, not in a band of
            // its own (MainPage.MoveStoriesIntoTitleBar), so it is exactly as tall as that row:
            // a CompactCellWidth-wide cell holding a 36px ring and nothing else. Height and width
            // are both written rather than measured - the ListView's ScrollViewer takes every
            // pixel it is offered in either direction, and what it is offered here is the whole
            // caption row, which is also the window drag area.
            ScrollingHost.Height = CompactRowHeight;
            ScrollingHost.Padding = new Thickness();
#endif

            _scrollDebouncer = new EventDebouncer<NotifyCollectionChangedEventArgs>(100,
                handler => ViewModel.Items.CollectionChanged += new NotifyCollectionChangedEventHandler(handler),
                handler => ViewModel.Items.CollectionChanged -= new NotifyCollectionChangedEventHandler(handler));

            _scrollTracker = new DispatcherTimer();
            _scrollTracker.Interval = TimeSpan.FromMilliseconds(33);
            _scrollTracker.Tick += OnTick;

            Connected += OnConnected;
            Disconnected += OnDisconnected;
#if LINUX
            DataContextChanged += OnDataContextChanged;
#endif
        }

        private void OnConnected(object sender, RoutedEventArgs e)
        {
#if LINUX
            EnsureComposeButton();
            TrySubscribe();
            UpdateIndexes();
#else
            _scrollDebouncer.Invoked += OnCollectionChanged;
#endif
        }

        private void OnDisconnected(object sender, RoutedEventArgs e)
        {
#if LINUX
            if (_subscribed)
            {
                _subscribed = false;
                _scrollDebouncer.Invoked -= OnCollectionChanged;
            }
#else
            _scrollDebouncer.Invoked -= OnCollectionChanged;
#endif
        }

#if LINUX
        private bool _subscribed;

        /// <summary>
        /// Hooks the strip up to the collection, once both halves are there.
        /// </summary>
        /// <remarks>
        /// EventDebouncer runs its subscription lambda the first time somebody listens, and that
        /// lambda dereferences <c>ViewModel.Items</c> - so listening before MainPage has pushed
        /// ViewModel.Stories into the DataContext is a NullReferenceException raised from Loaded,
        /// which in Uno is a dead dispatcher and a frozen window, not a logged warning
        /// (PORTING.md 6). Called from both sides of the race, and idempotent.
        /// </remarks>
        private void TrySubscribe()
        {
            if (_subscribed || !IsConnected || ViewModel == null)
            {
                return;
            }

            _subscribed = true;
            _scrollDebouncer.Invoked += OnCollectionChanged;
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            // ItemsSource comes from `{x:Bind ViewModel.Items}`, which is OneTime and is evaluated
            // when this control loads - and nothing guarantees that MainPage has assigned our
            // DataContext by then, in which case the band would bind to null and stay empty for
            // the rest of the session with no error anywhere. Assigning it from here settles the
            // order whichever way it happens, and is a no-op when the binding already won.
            var items = ViewModel?.Items;
            if (!ReferenceEquals(ScrollingHost.ItemsSource, items))
            {
                ScrollingHost.ItemsSource = items;
            }

            EnsureComposeButton();
            TrySubscribe();
            UpdateIndexes();
        }
#endif

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
#if LINUX
            // The strip never collapses here (see SetControlledList), so "no stories left" is not
            // a Collapse() away: Collapse() returns at its own guard and the band would stay on
            // screen, 88 px of empty list. UpdateIndexes ends in UpdateVisibility, which is what
            // shows and hides it.
            UpdateIndexes();
#else
            if (_collapsed || ScrollingHost.Items.Count > 0)
            {
                UpdateIndexes();
            }
            else
            {
                Collapse();
            }
#endif
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _scrollDebouncer.Invoke();
        }

#if LINUX
        private GlyphButton _compose;

        /// <summary>Height of the caption row the strip shares with the logo.</summary>
        private const double CompactRowHeight = 40;

        /// <summary>A 36px ring plus 4px of air, which is what ActiveStoriesCell measures.</summary>
        private const double CompactCellWidth = 40;

        /// <summary>The "post a story" button, and the room the list starts after.</summary>
        private const double CompactComposeWidth = 32;
        private const double CompactComposeSlot = 36;

        /// <summary>
        /// How many avatars the caption row shows before the strip starts scrolling sideways.
        ///
        /// It is a cap and not the item count because this strip shares its row with the window
        /// drag handle: an unbounded strip would push TitleBarHandle off the right edge and the
        /// window would stop being draggable, which is the one thing the caption row has to keep
        /// doing. Six is 240px out of the ~1300 the row measures.
        /// </summary>
        private const int MaxCompactItems = 6;
#endif

        private int _first = 0;
        private int _last = -1;

        private void UpdateIndexes()
        {
#if LINUX
            // ControlledList is assigned from MainPage.ShowHideTopTabs as well as from x:Bind, and
            // the code path gets here before the DataContext binding has run at least once.
            if (ViewModel == null)
            {
                return;
            }
#endif
            if (ViewModel.Items.Count > 0)
            {
                if (ViewModel.Items[0].IsMyStory && ViewModel.Items.Count > 1)
                {
                    _first = 1;
                }
                else
                {
                    _first = 0;
                }

                _last = Math.Min(_first + 2, Math.Max(_first, ViewModel.Items.Count - 1));
            }
            else
            {
                _first = 0;
                _last = -1;
            }

            if (_progress != null)
            {
                _progress.InsertScalar("First", _first);
                _progress.InsertScalar("Last", _last);
                _progress.InsertScalar("Count", _last - _first + 1);

                ForEach(_progress, _progressAnimation);
            }

#if LINUX
            // Everything below belongs to the collapsed state: Show and Icon are the two or three
            // avatars that ride inside the title bar while the strip is folded away, and the
            // margins they write are the room the title bar drag handle gives them. On Linux the
            // strip is always expanded, so those two stay hidden and the title bar keeps the
            // margins MainPage.UpdateTitleBarMargins gives it - this control does not get a second
            // opinion on that geometry.
            Show.Visibility = Visibility.Collapsed;
            Icon.Visibility = Visibility.Collapsed;

            ScrollingHost.IsHitTestVisible = true;

            UpdateVisibility();
#else
            var count = _last - _first + 1;
            if (count > 0 && _collapsed && _isVisible)
            {
                Show.Width = count * 12 + 12 + 8;
                Show.Margin = new Thickness(_first > 0 ? 26 : 14, 20, 0, 0);
                Show.Visibility = Visibility.Visible;

                Icon.Margin = new Thickness(Show.Width + Show.Margin.Left + 8, 20, 0, 0);
                Icon.Visibility = Visibility.Visible;

                TitleBarrr?.IsHitTestVisible = false;
                TitleBarHandle?.Margin = new Thickness(SystemOverlayLeftInset > 0 ? SystemOverlayLeftInset + (count * 12 + 12 + 8) : TitleBarrr.Margin.Left + 40 + (count * 12 + 12 + 8), 0, SystemOverlayRightInset > 0 ? SystemOverlayRightInset : 88, 0);
            }
            else
            {
                Show.Visibility = Visibility.Collapsed;
                Icon.Visibility = Visibility.Collapsed;

                TitleBarrr?.IsHitTestVisible = true;
                TitleBarHandle?.Margin = new Thickness(SystemOverlayLeftInset > 0 ? SystemOverlayLeftInset : TitleBarrr.Margin.Left + 40, 0, SystemOverlayRightInset > 0 ? SystemOverlayRightInset : 88, 0);
            }

            ScrollingHost.IsHitTestVisible = !_collapsed;
#endif
        }

#if LINUX
        /// <summary>
        /// The band is only there when there is something in it. Upstream this is the difference
        /// between the collapsed and the expanded state; here it is plain visibility, because the
        /// strip sits in the chat list header and its height is what pushes the first chat row
        /// down (MainPage.UpdateChatListTopPadding follows ChatListHeader.ActualHeight).
        /// </summary>
        private void UpdateVisibility()
        {
            var count = ViewModel?.Items.Count ?? 0;

            // Nothing to show means nothing on screen: in the caption row an empty strip would be
            // a hole next to the logo and, worse, it would still push the window drag handle to
            // the right of it. Note that the "post a story" button rides inside the strip, so it
            // goes away with it - see the note on EnsureComposeButton.
            Visibility = _isVisible && ViewModel != null && count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Width, not stretch: see MaxCompactItems.
            var width = Math.Min(count, MaxCompactItems) * CompactCellWidth;
            if (ScrollingHost.Width != width)
            {
                ScrollingHost.Width = width;
            }
        }

        /// <summary>
        /// The "post a story" button, built in code so the shared XAML keeps its Windows shape.
        /// </summary>
        private void EnsureComposeButton()
        {
            if (_compose != null || ScrollingHost?.Parent is not Grid root)
            {
                return;
            }

            _compose = new GlyphButton
            {
                // Named so UNIGRAM_CLICK can aim at it by name: a bare @GlyphButton would take
                // whichever glyph button the tree happens to hold first (PORTING.md, "apuntar por
                // texto, no por tipo").
                Name = "StoryComposeButton",
                Glyph = Icons.Add,
                Width = CompactComposeWidth,
                Height = CompactComposeWidth,
                CornerRadius = new CornerRadius(CompactComposeWidth / 2),
                Margin = new Thickness(),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            _compose.Click += Compose_Click;

            root.Children.Add(_compose);

            // The list starts to the right of the button, so the two never overlap.
            ScrollingHost.Margin = new Thickness(CompactComposeSlot, 0, 0, 0);
        }

        private async void Compose_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = ViewModel;
            if (viewModel?.ClientService == null)
            {
                return;
            }

            var story = await StoryComposerPopup.ComposeAsync(viewModel.ClientService, XamlRoot);
            if (story != null)
            {
                // postStory answers with a temporary story; the real one arrives through
                // updateChatActiveStories. Asking for the list again makes the strip pick it up
                // without waiting for that push.
                viewModel.ClientService.Send(new LoadActiveStories(new StoryListMain()));
            }
        }
#endif

        // Dead code on Linux: Uno's ChoosingItemContainer add accessor is a
        // TryRaiseNotImplemented no-op, so this never runs and the ContextRequested handler it
        // attaches is wired from OnContainerContentChanging instead.
        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new ListViewItem();
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContextRequested += OnContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
#if LINUX
            if (args.InRecycleQueue)
            {
                return;
            }

            if (args.ItemContainer is SelectorItem container)
            {
                // ChoosingItemContainer never fires (see above), so the context menu handler is
                // attached here. Containers are recycled, hence the remove before the add.
                container.ContextRequested -= OnContextRequested;
                container.ContextRequested += OnContextRequested;

                // Uno raises this event from ItemsControl.PrepareContainerForIndex, that is
                // BEFORE the container enters the visual tree, and it is entering the tree that
                // expands the item template (ContentPresenter.EnterImpl). A brand new container
                // therefore has no cell to bind yet and has to be bound again from its Loaded;
                // a recycled one already has one and binds right here.
                container.Loaded -= OnContainerLoaded;
                container.Loaded += OnContainerLoaded;

                UpdateContainer(container);
            }
#else
            var i = args.ItemIndex;

            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.ItemContainer.ContentTemplateRoot is ActiveStoriesCell cell && args.Item is ActiveStoriesViewModel item)
            {
                UpdateIndexes();

                cell.Update(item);
                cell.Update(args.ItemContainer, args.ItemIndex, _first, _last, _progress, _progressAnimation);

                AutomationProperties.SetName(args.ItemContainer, cell.GetAutomationName());
                Canvas.SetZIndex(args.ItemContainer, i >= _first && i <= _last ? 5 - i : -i);
            }
#endif
        }

#if LINUX
        private void OnContainerLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is SelectorItem container && !UpdateContainer(container))
            {
                // Loaded and still no cell: the item template did not expand, and the avatar and
                // the name would silently stay at whatever ActiveStoriesCell.xaml declares, which
                // is nothing. Same failure and same warning as ChatListListView.OnContainerLoaded.
                Logger.Warning("No ActiveStoriesCell under a stories strip container");
            }
        }

        private bool UpdateContainer(SelectorItem container)
        {
            // SelectorItem.ContentTemplateRoot is always null in Uno for a templated control
            // (Telegram.Linux/Xaml/ContentTemplateRootEx.cs), and every container has a template.
            var cell = container.ContentTemplateRootAs<ActiveStoriesCell>();
            if (cell == null || container.Content is not ActiveStoriesViewModel item)
            {
                return false;
            }

            UpdateIndexes();

            cell.Update(item);

            // The second Update overload is deliberately NOT called. It drives the collapse
            // choreography from a CompositionPropertySet this port never creates, so its first
            // line - `if (tracker == null) return;` - would send it straight back anyway; and what
            // lives past that guard is Compositor.CreatePathKeyFrameAnimation, which throws
            // NotImplementedException in Uno, plus animations started on a CompositionPathGeometry
            // and on a clip, which Uno silently drops because it only registers animations whose
            // target is a Visual. Canvas.SetZIndex goes with it: the fanned overlap it orders only
            // exists while the strip is folded into the title bar.

            AutomationProperties.SetName(container, cell.GetAutomationName());
            return true;
        }
#endif

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var element = sender as FrameworkElement;
            var activeStories = ScrollingHost.ItemFromContainer(sender) as ActiveStoriesViewModel;

            if (activeStories == null || activeStories.IsMyStory || (activeStories.ClientService.TryGetUser(activeStories.Chat, out User user) && user.IsSupport))
            {
                return;
            }

            var muted = ViewModel.Settings.Notifications.GetMuteStories(activeStories.Chat);
            var archived = activeStories.List is StoryListArchive;

            var flyout = new MenuFlyout();

            if (activeStories.Chat.Type is ChatTypePrivate)
            {
                flyout.CreateFlyoutItem(ViewModel.SendMessage, activeStories, Strings.SendMessage, Icons.ChatEmpty);
                flyout.CreateFlyoutItem(ViewModel.OpenProfile, activeStories, Strings.OpenProfile, Icons.Person);
            }
            else if (activeStories.Chat.Type is ChatTypeSupergroup typeSupergroup && typeSupergroup.IsChannel)
            {
                flyout.CreateFlyoutItem(ViewModel.OpenProfile, activeStories, Strings.OpenChannel2, Icons.Megaphone);
            }

            if (activeStories.Chat.Type is ChatTypePrivate && !activeStories.IsMyStory)
            {
                flyout.CreateFlyoutItem(ViewModel.MuteProfile, activeStories, muted ? Strings.NotificationsStoryUnmute2 : Strings.NotificationsStoryMute2, muted ? Icons.Alert : Icons.AlertOff);
            }

            flyout.CreateFlyoutItem(OpenAnonymously, activeStories, Strings.ViewAnonymously, Icons.EyeOff);

            if (archived)
            {
                flyout.CreateFlyoutItem(ViewModel.ShowProfile, activeStories, Strings.UnarchiveStories, Icons.Unarchive);
            }
            else
            {
                flyout.CreateFlyoutItem(ViewModel.HideProfile, activeStories, Strings.ArchivePeerStories, Icons.Archive);
            }

            flyout.ShowAt(element, args);
        }

        private async void OpenAnonymously(ActiveStoriesViewModel activeStories)
        {
            if (ViewModel.ClientService.StealthMode.ActiveUntilDate > 0)
            {
                OpenStory(activeStories);
            }
#if !LINUX
            else if (ViewModel.ClientService.IsPremium)
            {
                var popup = new StealthPopup(ViewModel.ClientService, true);
                await ViewModel.ShowPopupAsync(popup);

                if (popup.Activated)
                {
                    OpenStory(activeStories);
                }
            }
#endif
            else if (ViewModel.IsPremiumAvailable && !ViewModel.IsPremium)
            {
                ViewModel.NavigationService.ShowPromo(new PremiumFeatureUpgradedStories(), new PremiumSourceStoryFeature(new PremiumStoryFeatureStealthMode()));
            }
        }

        public void ForEach(CompositionPropertySet tracker, ExpressionAnimation expression)
        {
            var panel = ScrollingHost.ItemsPanelRoot as ItemsStackPanel;
            if (panel == null || panel.FirstVisibleIndex != 0)
            {
                return;
            }

            for (int i = panel.FirstCacheIndex; i <= panel.LastCacheIndex; i++)
            {
                var container = ScrollingHost.ContainerFromIndex(i) as SelectorItem;
                if (container != null)
                {
                    if (container.ContentTemplateRoot is ActiveStoriesCell cell)
                    {
                        if (i >= panel.FirstVisibleIndex && i <= panel.LastVisibleIndex)
                        {
                            cell.Update(container, i, _first, _last, tracker, expression);
                            Canvas.SetZIndex(container, i >= _first && i <= _last ? 5 - i : -i);
                        }
                        else
                        {
                            cell.Disconnect(container);
                        }
                    }
                }
            }
        }

        private void Show_Click(object sender, RoutedEventArgs e)
        {
            Expand();
        }

        private void ScrollingHost_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ActiveStoriesViewModel activeStories)
            {
                if (_collapsed)
                {
                    Expand();
                }
                else
                {
                    OpenStory(activeStories);
                }
            }

            //UpdateIndexes();
            //ForEach(_progress, _progressAnimation);

            //ScrollingHost.ItemsSource = null;
            //ScrollingHost.ItemsSource = ViewModel.Items;
        }

        private void OpenStory(ActiveStoriesViewModel activeStories)
        {
            var container = ScrollingHost.ContainerFromItem(activeStories) as SelectorItem;
            if (container == null)
            {
                return;
            }

            var transform = container.TransformToVisual(null);
            var point = transform.TransformPoint(new Point());

            var origin = new Rect(point.X + 12 + 4, point.Y + 12 + 4, 40, 40);

            ViewModel.OpenStory(activeStories, origin, GetOrigin);
        }

        private Rect GetOrigin(ActiveStoriesViewModel activeStories)
        {
            var container = ScrollingHost.ContainerFromItem(activeStories) as SelectorItem;
            if (container != null)
            {
                var transform = container.TransformToVisual(null);
                var point = transform.TransformPoint(new Point());

                return new Rect(point.X + 12 + 4, point.Y + 12 + 4, 40, 40);
            }

            return Rect.Empty;
        }

        public void ScrollToTop()
        {
            ScrollingHost.ScrollToTop();
        }

        // Declared, never raised and never subscribed to. StoryEventArgs is upstream in
        // Controls/Stories/StoryContent.xaml.cs and on Linux in
        // Telegram.Linux/Stories/StoryViewerTypes.cs, so it resolves on both heads.
        public event EventHandler<StoryEventArgs> ItemClick;

        #region Manipulation

        private CompositionPropertySet _progress;
        private ExpressionAnimation _progressAnimation;

        private DispatcherTimer _scrollTracker;
        private EventDebouncer<NotifyCollectionChangedEventArgs> _scrollDebouncer;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_controlledList != null && _scrollViewer == null)
            {
                SetControlledList(_controlledList);
            }
        }

        public bool IsCollapsed => _collapsed;

        private ScrollViewer _scrollViewer;

        private FrameworkElement _chatTabs;
        public FrameworkElement ChatTabs
        {
            get => _chatTabs;
            set
            {
                _chatTabs = value;

                if (_chatTabs != null && _progress != null)
                {
                    SetControlledList(_controlledList);
                }
            }
        }

        public FrameworkElement TitleBarrr { get; set; }
        public Border TitleBarHandle { get; set; }
        public FrameworkElement Header { get; set; }

        private bool _tabsLeftCollapsed = true;
        public bool TabsLeftCollapsed
        {
            get => _tabsLeftCollapsed;
            set
            {
                _tabsLeftCollapsed = value;
                UpdatePadding();
            }
        }

        private float _systemOverlayLeftInset;
        public float SystemOverlayLeftInset
        {
            get => _systemOverlayLeftInset;
            set
            {
                _systemOverlayLeftInset = value;
                UpdatePadding();
            }
        }

        private float _systemOverlayRightInset;
        public float SystemOverlayRightInset
        {
            get => _systemOverlayRightInset;
            set
            {
                _systemOverlayRightInset = value;
                UpdateIndexes();
            }
        }

        private void UpdatePadding()
        {
            _progress?.InsertBoolean("RightToLeft", _systemOverlayLeftInset > 0);
            _progress?.InsertScalar("Padding", _systemOverlayLeftInset > 0 ? _systemOverlayLeftInset : _tabsLeftCollapsed ? 32 : 0);
        }

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                _progress?.InsertBoolean("Visible", value);

                UpdateIndexes();
            }
        }

        public bool TabsTopCollapsed { get; set; } = true;

        private ListView _controlledList;
        public ListView ControlledList
        {
            get => _controlledList;
            set => SetControlledList(_controlledList = value);
        }

        public double TopPadding
        {
            get
            {
                var title = 40;
                var search = 32;
                var padding = 4 + (TabsTopCollapsed || !TabsLeftCollapsed ? 0 : 36);
                //var padding = 8 + (ChatTabs == null || ChatTabs.Visibility == Visibility.Collapsed || !TabsLeftCollapsed ? 0 : 32);
                //var padding = ChatTabs == null || ChatTabs.Visibility == Visibility.Collapsed || !TabsLeftCollapsed ? 0 : 40;

                return title + search + padding;
            }
        }

        public double GetTopPadding(bool collapsed)
        {
            var title = 40;
            var search = 32;
            var padding = 4 + (collapsed /*|| !TabsLeftCollapsed*/ ? 0 : 36);
            //var padding = 8 + (ChatTabs == null || ChatTabs.Visibility == Visibility.Collapsed || !TabsLeftCollapsed ? 0 : 32);
            //var padding = ChatTabs == null || ChatTabs.Visibility == Visibility.Collapsed || !TabsLeftCollapsed ? 0 : 40;

            return title + search + padding;
        }

        private void SetControlledList(Control value)
        {
#if LINUX
            // Everything this method does on Windows is set up the collapse/expand choreography,
            // and every API it needs for that is missing in Uno 6.6.184 - measured on the
            // assemblies this project deploys:
            //
            //  * ElementCompositionPreview.GetScrollViewerManipulationPropertySet THROWS
            //    NotImplementedException. It is the first thing the original body reaches, so on
            //    Linux this method used to be one assignment away from taking the page down.
            //  * ScrollViewer.ViewChanging, DirectManipulationStarted and
            //    DirectManipulationCompleted are TryRaiseNotImplemented no-ops: subscribing is
            //    harmless and they never fire, so the overscroll gesture that expands the strip
            //    could never happen anyway.
            //  * `_progress.StartAnimation("Progress", ...)` targets a CompositionPropertySet and
            //    `clip.StartAnimation("RightInset", ...)` an InsetClip. Compositor.RegisterAnimation
            //    starts with `if (!animation.IsTrackedByCompositor || !(visual is Visual v))
            //    return;`, so neither is ever registered and Progress would stay frozen at its
            //    frame zero - the same trap that left a moved chat row invisible (PORTING.md 6).
            //
            // So on Linux the strip is a plain band that is always expanded, MainPage hosts it
            // inside the chat list header instead of over the title bar, and _progress stays null.
            // That null is also the seam that keeps ActiveStoriesCell's Win2D geometry and
            // Compositor.CreatePathKeyFrameAnimation - which throws too - out of reach: the
            // overload that uses them starts with `if (tracker == null) return;`.
            //
            // ControlledList.Margin is deliberately NOT written here: on Linux the chat list top
            // inset is measured from the header by MainPage.UpdateChatListTopPadding, and a second
            // writer of that margin is exactly what put a folder pill on top of the first chat row
            // once already.
            _collapsed = false;
            UpdateIndexes();
            return;
#else
            var scrollViewer = value as ScrollViewer;
            if (scrollViewer == null && value != null)
            {
                if (value.IsLoaded)
                {
                    value.ApplyTemplate();
                    scrollViewer = ControlledList.GetScrollViewer();
                }
                else
                {
                    value.Loaded += OnLoaded;
                    return;
                }
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
                _scrollViewer.ViewChanging -= Scroller_ViewChanging;

                _scrollViewer.DirectManipulationStarted -= Scroller_DirectManipulationStarted;
                _scrollViewer.DirectManipulationCompleted -= Scroller_DirectManipulationCompleted;

                ControlledList.SizeChanged -= ControlledList_SizeChanged;
            }

            _scrollViewer = scrollViewer;

            if (_scrollViewer == null)
            {
                Logger.Info("_scrollViewer is null");
                return;
            }

            _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
            _scrollViewer.ViewChanging += Scroller_ViewChanging;

            _scrollViewer.DirectManipulationStarted += Scroller_DirectManipulationStarted;
            _scrollViewer.DirectManipulationCompleted += Scroller_DirectManipulationCompleted;

            _collapsed = true;

            ControlledList.SizeChanged += ControlledList_SizeChanged;
            ControlledList.Margin = new Thickness(0, TopPadding, 0, 0);
            ControlledList.Padding = new Thickness();

            var properties = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scrollViewer);
            var compositor = properties.Compositor;

            var clip = compositor.CreateInsetClip();

            _progressAnimation = compositor.CreateExpressionAnimation($"Clamp((1 - (((This.Collapsed ? 88 : 0) + -(interactionTracker.Translation.Y > -1 && interactionTracker.Translation.Y < 1 ? 0 : interactionTracker.Translation.Y)) / 88)) * (This.Collapsed ? 0.5 : 1), 0, 1)");
            _progressAnimation.SetReferenceParameter("interactionTracker", properties);
            _progressAnimation.Properties.InsertBoolean("Collapsed", true);

            _progress = compositor.CreatePropertySet();
            _progress.InsertBoolean("Visible", _isVisible);
            _progress.InsertBoolean("RightToLeft", false);
            _progress.InsertScalar("Padding", _systemOverlayLeftInset > 0 ? _systemOverlayLeftInset - (_tabsLeftCollapsed ? 0 : 72) : _tabsLeftCollapsed ? 32 : 0);
            _progress.InsertScalar("First", _first);
            _progress.InsertScalar("Last", _last);
            _progress.InsertScalar("Count", _last - _first + 1);
            _progress.InsertScalar("Total", _last + 1);
            _progress.InsertScalar("Progress", 0);
            _progress.StartAnimation("Progress", _progressAnimation);

            //            m_progress?.InsertScalar("Padding", _tabsLeftCollapsed ? 48 : 0);


            ForEach(_progress, _progressAnimation);

            // >= 0.5 : above
            // <  0.5 : below
            var offsetExpandedX = "-(_.Padding - _.First * 12) * (_.Progress)";
            var offsetExpandedX2 = "24 + (12 * _.Count) + (((72 * _.Total) - (40 + (12 * _.Total))) * _.Progress)";
            var offsetExpandedY = "48 * _.Progress";

            var offsetExpressionX = $"_.Progress < 0.5 ? ({offsetExpandedX}) + ({offsetExpandedX2}) : 0";
            var offsetExpressionY = $"_.Progress < 0.5 ? {offsetExpandedY} : 0";
            var scaleExpression = "_.Progress < 0.5 ? 1 : 0.5 + (_.Progress - 0.5)";
            var opacityExpression = "_.Progress < 0.5 ? 1 - _.Progress * 2 : (_.Progress - 0.5) * 2";

            var titleVisualOffsetAnimation = compositor.CreateExpressionAnimation($"_.Visible && _.Count > 0 ? Vector3({offsetExpressionX}, {offsetExpressionY}, 0) : Vector3(0, 0, 0)");
            var titleVisualScaleAnimation = compositor.CreateExpressionAnimation($"_.Visible && _.Count > 0 ? Vector3(Clamp({scaleExpression}, 0.5, 1), Clamp({scaleExpression}, 0.5, 1), 1) : Vector3(1, 1, 1)");
            var titleVisualOpacityAnimation = compositor.CreateExpressionAnimation($"_.Visible && _.Count > 0 ? Clamp({opacityExpression}, 0, 1) : 1");
            var titleVisualOpacityInverseAnimation = compositor.CreateExpressionAnimation("Clamp(1 - _.Progress * 2, 0, 1)");

            var storiesVisualOffsetAnimationX = compositor.CreateExpressionAnimation(
                "(_.Padding - _.First * 12) * (1 - _.Progress)");

            var storiesVisualOffsetAnimation = compositor.CreateExpressionAnimation(
                "-24 + (48 * _.Progress)");

            var headerVisualOffsetAnimation = compositor.CreateExpressionAnimation(
                "84 * _.Progress");

            titleVisualOffsetAnimation.SetReferenceParameter("_", _progress);
            titleVisualScaleAnimation.SetReferenceParameter("_", _progress);
            titleVisualOpacityAnimation.SetReferenceParameter("_", _progress);
            titleVisualOpacityInverseAnimation.SetReferenceParameter("_", _progress);
            storiesVisualOffsetAnimationX.SetReferenceParameter("_", _progress);
            storiesVisualOffsetAnimation.SetReferenceParameter("_", _progress);
            headerVisualOffsetAnimation.SetReferenceParameter("_", _progress);

            var titleVisual = ElementComposition.GetElementVisual(TitleBarrr);
            var storiesVisual = ElementComposition.GetElementVisual(this);
            var headerVisual = ElementComposition.GetElementVisual(Header);

            titleVisual.CenterPoint = new Vector3(0, 10, 0);
            storiesVisual.Clip = clip;

            titleVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            storiesVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            headerVisual.Properties.InsertVector3("Translation", Vector3.Zero);

            ElementCompositionPreview.SetIsTranslationEnabled(TitleBarrr, true);
            ElementCompositionPreview.SetIsTranslationEnabled(this, true);
            ElementCompositionPreview.SetIsTranslationEnabled(Header, true);

            titleVisual.StartAnimation("Translation", titleVisualOffsetAnimation);
            titleVisual.StartAnimation("Scale", titleVisualScaleAnimation);
            titleVisual.StartAnimation("Opacity", titleVisualOpacityAnimation);
            storiesVisual.StartAnimation("Translation.X", storiesVisualOffsetAnimationX);
            clip.StartAnimation("RightInset", storiesVisualOffsetAnimationX);
            storiesVisual.StartAnimation("Translation.Y", storiesVisualOffsetAnimation);
            headerVisual.StartAnimation("Translation.Y", headerVisualOffsetAnimation);

            if (ChatTabs != null)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(ChatTabs, true);

                var tabsVisualOffsetAnimation = compositor.CreateExpressionAnimation(
                    "-80 + 84 * _.Progress");
                tabsVisualOffsetAnimation.SetReferenceParameter("_", _progress);

                var tabsVisual = ElementComposition.GetElementVisual(ChatTabs);
                tabsVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                tabsVisual.StartAnimation("Translation.Y", tabsVisualOffsetAnimation);
            }
#endif
        }

        private void ControlledList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMinHeight();
        }

        private void UpdateMinHeight()
        {
            if (ControlledList.ItemsPanelRoot == null)
            {
                return;
            }

            if (_collapsed)
            {
                ControlledList.ItemsPanelRoot.MinHeight = 0;
            }
            else
            {
                ControlledList.ItemsPanelRoot.MinHeight = ControlledList.ActualHeight;
            }
        }

        private bool _directManipulation;

        private void Scroller_ViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
        {
            if (e.IsInertial || !e.FinalView.VerticalOffset.AlmostEqualsToZero(0.5))
            {
                _scrollTracker.Stop();
            }
            else if (_collapsed && _directManipulation && !_scrollTracker.IsEnabled)
            {
                _scrollTracker.Start();
            }
        }

        private void OnTick(object sender, object e)
        {
            var offset = GetOverscrollOffset();
            if (offset >= 0 && _collapsed)
            {
                if (offset > 48 && _collapsed && _scrollViewer.VerticalOffset.AlmostEqualsToZero(0.5))
                {
                    _scrollTracker.Stop();
                    Expand(true, offset);
                }
            }
            else
            {
                _scrollTracker.Stop();
            }
        }

        private float GetOverscrollOffset()
        {
            var itemsPanel = ControlledList.Header switch
            {
                FrameworkElement header => header.Visibility == Visibility.Visible ? header : ControlledList.ItemsPanelRoot,
                _ => ControlledList.ItemsPanelRoot
            };

            if (itemsPanel != null)
            {
                var transform = itemsPanel.TransformToVisual(ControlledList);
                var point = transform.TransformVector2();

                return point.Y;
            }

            return -1;
        }

        private void Batch_Completed(object sender, CompositionBatchCompletedEventArgs args)
        {
            if (sender is CompositionScopedBatch batch)
            {
                batch.Completed -= Batch_Completed;
            }

            _progress.StartAnimation("Progress", _progressAnimation);
        }

        private void Scroller_DirectManipulationStarted(object sender, object e)
        {
            _directManipulation = true;

            UpdateIndexes();

            if (_collapsed && _scrollViewer.VerticalOffset.AlmostEqualsToZero(0.5))
            {
                _scrollTracker.Start();
            }
        }

        private void Scroller_DirectManipulationCompleted(object sender, object e)
        {
            _directManipulation = false;
            _scrollTracker.Stop();
        }

        private bool _collapsed;

        private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_collapsed && _scrollViewer.VerticalOffset.AlmostEqualsToZero() && !e.IsIntermediate)
            {
                //_collapsed = false;

                //ChatsList.Padding = new Thickness(0, 88, 0, 0);
                //ComposeButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Red);
                //m_progressAnimation.Properties.InsertBoolean("Collapsed", false);
                //scrollViewer.ChangeView(null, 88, null, true);
                //scrollViewer.SetVerticalPadding(88);
            }
            else if (_scrollViewer.VerticalOffset > 88 && !_collapsed)
            {
                Collapse(false);
            }
            else if (_scrollViewer.VerticalOffset < 88 && !_collapsed)
            {
                if (e.IsIntermediate)
                {

                }
                else if (_scrollViewer.VerticalOffset >= 40)
                {
                    _scrollViewer.TryChangeView(null, 88, null, false);
                }
                else
                {
                    _scrollViewer.TryChangeView(null, 0, null, false);
                }

                ScrollToTop();
            }
            else if (_scrollViewer.VerticalOffset.AlmostEquals(88, 0.5) && !_collapsed && !e.IsIntermediate)
            {
                Collapse(false);
            }
        }

        public void Toggle()
        {
            if (_collapsed)
            {
                Expand();
            }
            else
            {
                Collapse();
            }
        }

        public void Collapse(bool animated = true)
        {
            if (_collapsed || _scrollViewer == null)
            {
                return;
            }

            Logger.Info();

            _collapsed = true;
            Collapsing?.Invoke(this, EventArgs.Empty);

            ControlledList.Padding = new Thickness(0);
            _progressAnimation.Properties.InsertBoolean("Collapsed", true);
            _scrollViewer.SetVerticalPadding(0, 0);

            UpdateMinHeight();
            UpdateIndexes();

            if (animated)
            {
                var batch = _progress.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                batch.Completed += Batch_Completed;

                var animation = _progress.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, 1);
                animation.InsertKeyFrame(1, 0);
                animation.Duration = Constants.FastAnimation;

                var translation = _progress.Compositor.CreateScalarKeyFrameAnimation();
                translation.InsertKeyFrame(0, 88);
                translation.InsertKeyFrame(1, 0);
                translation.Duration = Constants.FastAnimation;

                ElementCompositionPreview.SetIsTranslationEnabled(ControlledList, true);
                var boh = ElementComposition.GetElementVisual(ControlledList);

                _progress.StartAnimation("Progress", animation);
                boh.StartAnimation("Translation.Y", translation);
                batch.End();
            }
            else if (_directManipulation)
            {
                _scrollViewer.TryChangeView(null, _scrollViewer.VerticalOffset - 88, null, true);
            }
            else
            {
                _scrollViewer.CancelDirectManipulations();
                _scrollViewer.TryChangeView(null, 0, null, true);
            }
        }

        public event EventHandler Expanding;
        public event EventHandler Collapsing;

        public void Expand(bool animated = true, float offset = 0)
        {
            if (!_collapsed || _scrollViewer == null || ScrollingHost.Items.Count < 1)
            {
                return;
            }

            Logger.Info();

            _collapsed = false;
            Expanding?.Invoke(this, EventArgs.Empty);

            ControlledList.Padding = new Thickness(0, 88, 0, 0);
            _progressAnimation.Properties.InsertBoolean("Collapsed", false);
            _scrollViewer.SetVerticalPadding(88, 0);

            UpdateMinHeight();
            UpdateIndexes();

            if (animated)
            {
                var batch = _progress.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                batch.Completed += Batch_Completed;

                var animation = _progress.Compositor.CreateScalarKeyFrameAnimation();
                if (offset == 0)
                {
                    animation.InsertKeyFrame(0, 0);
                }
                animation.InsertKeyFrame(1, 1);
                animation.Duration = Constants.FastAnimation;

                var translation = _progress.Compositor.CreateScalarKeyFrameAnimation();
                translation.InsertKeyFrame(0, -(88 - offset));
                translation.InsertKeyFrame(1, 0);
                translation.Duration = Constants.FastAnimation;

                ElementCompositionPreview.SetIsTranslationEnabled(ControlledList, true);
                var boh = ElementComposition.GetElementVisual(ControlledList);

                _progress.StartAnimation("Progress", animation);
                boh.StartAnimation("Translation.Y", translation);
                batch.End();

                _scrollViewer.TryChangeView(null, 0, null, true);
            }
        }



        #endregion

        private void Icon_Click(object sender, RoutedEventArgs e)
        {
#if !LINUX
            // Icon is the emoji status button that only exists while the strip is folded into the
            // title bar, and the strip never folds on Linux, so this is unreachable there. The
            // guard is for the compiler: EmojiMenuFlyout and the emoji drawer are not in the
            // subset.
            if (ViewModel.IsPremium)
            {
                EmojiMenuFlyout.ShowAt(ViewModel.ClientService, EmojiDrawerMode.EmojiStatus, Icon, EmojiFlyoutAlignment.TopLeft);
            }
#endif
        }
    }
}
