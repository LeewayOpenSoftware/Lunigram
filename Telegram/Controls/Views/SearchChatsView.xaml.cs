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
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views;
using Telegram.Views.Profile;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Controls.Views
{
    public partial class ItemContextRequestedEventArgs : EventArgs
    {
        public ItemContextRequestedEventArgs(object item, ContextRequestedEventArgs eventArgs)
        {
            Item = item;
            EventArgs = eventArgs;
        }

        public object Item { get; }

        public ContextRequestedEventArgs EventArgs { get; }
    }

    public sealed partial class SearchChatsView : UserControl, INavigablePage
    {
        private SearchChatsViewModel _viewModel;
        public SearchChatsViewModel ViewModel => _viewModel ??= DataContext as SearchChatsViewModel;

        public SearchChatsView()
        {
            InitializeComponent();

#if LINUX
            // ItemsHost has NO ItemTemplate in the XAML: every row's template, style and
            // ContextRequested hook are handed out by OnChoosingItemContainer, and Uno never
            // raises ChoosingItemContainer (PORTING.md 6). Left alone the results list draws a
            // column of blank rows and no row has a context menu - the screen renders and nothing
            // works, which is the whole point of this pass.
            //
            // The template is the half that MUST be in place before the container is populated
            // (PORTING.md 6, "todo lo que toque plantillas va antes de base"), so it goes through
            // the framework's own ItemTemplateSelector, which Uno applies from its
            // PrepareContainerForItemOverride. The ContextRequested hook touches the already-built
            // container, so it rides ContainerContentChanging, which Uno DOES raise - once per
            // container, on prepare. See there for the one piece deliberately left undone.
            ItemsHost.ItemTemplateSelector = new SearchChatsTemplateSelector(this);
#endif
        }

#if LINUX
        private partial class SearchChatsTemplateSelector : DataTemplateSelector
        {
            private readonly SearchChatsView _owner;

            public SearchChatsTemplateSelector(SearchChatsView owner)
            {
                _owner = owner;
            }

            protected override DataTemplate SelectTemplateCore(object item)
            {
                // Same three-way match, and the same Resources[...] lookup, that
                // OnChoosingItemContainer does on Windows. Two of the three are declared with
                // x:Name rather than x:Key; Uno's generator registers those under that name all
                // the same (see the generated ForumView: Resources["ListTemplate"] = ...).
                var typeName = item switch
                {
                    IKeyedCollection => "HeaderTemplate",
                    SearchResult => "ProfileTemplate",
                    Message => "MessageTemplate",
                    _ => null
                };

                // TryGetValue rather than the indexer: a miss here must degrade to the default
                // container, not throw from inside a layout pass.
                return typeName != null && _owner.Resources.TryGetValue(typeName, out object template)
                    ? template as DataTemplate
                    : null;
            }

            protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            {
                return SelectTemplateCore(item);
            }
        }
#endif

        public Thickness PaddingImpl
        {
            get => TopChats.Padding;
            set
            {
                ItemsHost.Padding = value;
                TopChats.Padding = value;
                TopChats.Margin = new Thickness(-value.Left, 0, -value.Right, 0);
            }
        }

        public void Activate()
        {
            ViewModel.Activate();

            TopChats.ForEach<Chat>((selector, chat) =>
            {
                // ContentRoot(), not ContentTemplateRoot: the latter is always null in Uno for a
                // templated container, and the very next line dereferences it (PORTING.md 6).
                var content = selector.ContentRoot() as StackPanel;
                if (content == null)
                {
                    return;
                }

                var grid = content.Children[0] as Grid;

                var badge = grid.Children[1] as BadgeControl;
                badge.Visibility = chat.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                badge.Text = chat.UnreadCount.ToString();

                var user = ViewModel.ClientService.GetUser(chat);
                if (user != null)
                {
                    var online = grid.Children[2] as Border;
                    online.Visibility = user.Status is UserStatusOnline ? Visibility.Visible : Visibility.Collapsed;
                }
            });

            ItemsHost.ForEach<ProfileCell, SearchResult>((content, result) =>
            {
                var badge = content.Content as BadgeControl;
                if (badge != null)
                {
                    if (result.Chat != null)
                    {
                        var muted = ViewModel.ClientService.Notifications.IsMuted(result.Chat);
                        badge.IsUnmuted = !muted;
                        badge.Text = result.Chat.UnreadCount > 0 ? result.Chat.UnreadCount.ToString() : string.Empty;
                        badge.Visibility = result.Chat.UnreadCount > 0 || result.Chat.IsMarkedAsUnread
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                    else
                    {
                        badge.Visibility = Visibility.Collapsed;
                    }
                }
            });
        }

        public void Deactivate()
        {
            ViewModel.Deactivate();
        }

        public event ItemClickEventHandler ItemClick;

        public void RaiseItemClick(ItemClickEventArgs e)
        {
            ItemClick?.Invoke(this, e);
        }

        public event TypedEventHandler<UIElement, ItemContextRequestedEventArgs> ItemContextRequested;

        public ListView Root => ItemsHost;

        public bool AreTabsVisible
        {
            get => ChatFolders.Visibility == Visibility.Visible;
            set => ChatFolders.Visibility = value
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        #region Recycle

        private readonly Dictionary<string, HashSet<SelectorItem>> _typeToItemHashSetMapping = new()
        {
            { "HeaderTemplate", new HashSet<SelectorItem>() },
            { "ProfileTemplate", new HashSet<SelectorItem>() },
            { "MessageTemplate", new HashSet<SelectorItem>() },
        };

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            var typeName = args.Item switch
            {
                IKeyedCollection => "HeaderTemplate",
                SearchResult => "ProfileTemplate",
                Message => "MessageTemplate",
                _ => null
            };

            var relevantHashSet = _typeToItemHashSetMapping[typeName];

            // args.ItemContainer is used to indicate whether the ListView is proposing an
            // ItemContainer (ListViewItem) to use. If args.Itemcontainer != null, then there was a
            // recycled ItemContainer available to be reused.
            if (args.ItemContainer is SearchListViewItem container)
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
                    var item = new SearchListViewItem(typeName);
                    item.ContentTemplate = Resources[typeName] as DataTemplate;
                    item.Style = args.Item is IKeyedCollection ? Resources["HeaderListViewItemStyle"] as Style : sender.ItemContainerStyle;
                    item.ContextRequested += OnContextRequested;
                    args.ItemContainer = item;
                }
            }

            // Indicate to XAML that we picked a container for it
            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                if (args.ItemContainer.ContentRoot() is ProfileCell content)
                {
                    content.RecycleSearchResult();
                }

                if (args.ItemContainer is SearchListViewItem container)
                {
                    // XAML has indicated that the item is no longer being shown, so add it to the recycle queue
                    _typeToItemHashSetMapping[container.TypeName].Add(args.ItemContainer);
                }

                return;
            }

#if LINUX
            // Everything below this point is the Windows shape of the callback and does not
            // survive here, for two separate reasons, so Linux takes its own route and returns.
            //
            // 1. ContextRequested is what makes ItemContextRequested - and with it MainPage's
            //    context menu over a search result - exist at all. On Windows it is handed out by
            //    OnChoosingItemContainer, which Uno never raises.
            // 2. Every branch below reads args.ItemContainer.ContentTemplateRoot, which is
            //    ALWAYS null in Uno for a templated container (PORTING.md 6). Two of the three
            //    branches then `return` on the null, so the row draws blank; the message branch
            //    and TopChats dereference it outright. On top of that Uno raises this once, from
            //    PrepareContainerForIndex - i.e. BEFORE the container enters the visual tree,
            //    which is also when Uno expands the item template - so a brand new container has
            //    no cell to find yet and has to be bound again on Loaded. Same shape as
            //    Controls/ChatListListView.OnContainerContentChanging, which is the measured one.
            //
            // NOT done here: the per-row HeaderListViewItemStyle. It carries a Template, and
            // swapping a Template on a container that base has already populated is the exact
            // move that PORTING.md 6 measured as costing the whole list. The cost of leaving it
            // is cosmetic: a header row keeps the default ListViewItem padding, min height and
            // selection visual. The proper fix is ItemContainerStyleSelector, which nothing in
            // the subset uses yet, so it needs measuring before it is trusted.
            if (args.ItemContainer is SelectorItem prepared)
            {
                prepared.ContextRequested -= OnContextRequested;
                prepared.ContextRequested += OnContextRequested;

                prepared.Loaded -= OnContainerLoaded;
                prepared.Loaded += OnContainerLoaded;

                UpdateContainer(prepared, args.Item);
            }

            args.Handled = true;
            return;
#endif

            if (args.Item is IKeyedCollection header)
            {
                var content = args.ItemContainer.ContentTemplateRoot as Grid;
                if (content == null)
                {
                    return;
                }

                var text = content.Children[0] as TextBlock;
                var clear = content.Children[1] as Button;

                text.Text = header.Key;
                clear.Visibility = header.Key == Strings.Recent
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            else if (args.Item is SearchResult result)
            {
                var content = args.ItemContainer.ContentTemplateRoot as ProfileCell;
                if (content == null)
                {
                    return;
                }

                content.UpdateSearchResult(ViewModel.ClientService, args, OnContainerContentChanging);

                var badge = content.Content as BadgeControl;
                if (badge != null)
                {
                    if (result.Chat != null)
                    {
                        var muted = ViewModel.ClientService.Notifications.IsMuted(result.Chat);
                        badge.IsUnmuted = !muted;
                        badge.Text = result.Chat.UnreadCount > 0 ? result.Chat.UnreadCount.ToString() : string.Empty;
                        badge.Visibility = result.Chat.UnreadCount > 0 || result.Chat.IsMarkedAsUnread
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                    else
                    {
                        badge.Visibility = Visibility.Collapsed;
                    }
                }
            }
            else if (args.Item is Message message)
            {
                if (args.Phase == 0)
                {
                    args.RegisterUpdateCallback(2, OnContainerContentChanging);
                }
                else
                {
                    var content = args.ItemContainer.ContentTemplateRoot as ChatCell;
                    if (content == null)
                    {
                        return;
                    }

                    content.UpdateMessage(ViewModel.ClientService, message, false);
                }
            }

            args.Handled = true;
        }

#if LINUX
        // Same shape as Controls/ChatListListView.OnContainerLoaded: containers are recycled, so
        // the item is re-read from the container rather than captured when the handler was
        // attached. This is the second half of the bind - ContainerContentChanging runs before the
        // container is in the visual tree, so ContentRoot() has nothing to find yet on a brand new
        // one, and this is where it finally does.
        private void OnContainerLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is SelectorItem container && !UpdateContainer(container, container.Content))
            {
                // Loaded and still no cell. Measured: with an ItemTemplateSelector the presenter
                // can still be a pass behind here, so this is not yet a failure - SizeChanged is
                // raised after the pass that finally expands the template, and that is the last
                // chance. Only a miss THERE is a row that will stay blank.
                container.SizeChanged -= OnContainerSized;
                container.SizeChanged += OnContainerSized;
            }
        }

        private void OnContainerSized(object sender, SizeChangedEventArgs e)
        {
            if (sender is SelectorItem container)
            {
                container.SizeChanged -= OnContainerSized;

                if (!UpdateContainer(container, container.Content))
                {
                    Logger.Warning(string.Format("No cell under the search container of {0}", container.Content?.GetType().Name ?? "null"));
                }
            }
        }

        private bool UpdateContainer(SelectorItem container, object item)
        {
            try
            {
                return UpdateContainerImpl(container, item);
            }
            catch (Exception ex)
            {
                // This runs from ItemsControl.PrepareContainerForIndex, which Uno calls from
                // inside MeasureOverride: an exception here does not just lose one row, it takes
                // the whole layout pass and leaves the list half built. Log it and let the rest
                // of the list draw.
                Logger.Error(string.Format("Search row bind failed for {0}: {1}", item?.GetType().Name ?? "null", ex));
                return true;
            }
        }

        private bool UpdateContainerImpl(SelectorItem container, object item)
        {
            if (ViewModel == null)
            {
                return false;
            }

            if (item is IKeyedCollection header)
            {
                if (container.ContentRoot() is not Grid content)
                {
                    return false;
                }

                var text = content.Children[0] as TextBlock;
                var clear = content.Children[1] as Button;

                text.Text = header.Key;
                clear.Visibility = header.Key == Strings.Recent
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                return true;
            }
            else if (item is SearchResult result)
            {
                if (container.ContentRoot() is not ProfileCell content)
                {
                    return false;
                }

                // ProfileCell is a ContentControl and its named parts (TitleLabel, Identity,
                // Photo...) are assigned from OnApplyTemplate, which Uno runs on the FIRST
                // MEASURE - not when the presenter expands the item template. So the cell can be
                // found here with every one of its parts still null, and UpdateSearchResultPhase0
                // writes TitleLabel.Style on its second line: measured on 2026-08-26 as a
                // NullReferenceException thrown from inside VirtualizingPanelLayout.MeasureOverride,
                // i.e. the mortal kind (PORTING.md 6). It happens on a RECYCLED container whose
                // presenter has just rebuilt its content for a different row type, which is what
                // the ItemTemplateSelector makes routine. ApplyTemplate() is idempotent and runs
                // OnApplyTemplate now.
                content.ApplyTemplate();

                // Uno raises ContainerContentChanging once with Phase always 0 and
                // RegisterUpdateCallback is a no-op, so the phased call would draw the title and
                // leave the subtitle and the avatar empty. Tag is what the phased method sets
                // before branching, and ChatCell/ItemClick read it.
                container.Tag = result.Chat;
                content.UpdateSearchResultInflated(ViewModel.ClientService, result);

                if (content.Content is BadgeControl badge)
                {
                    if (result.Chat != null)
                    {
                        var muted = ViewModel.ClientService.Notifications.IsMuted(result.Chat);
                        badge.IsUnmuted = !muted;
                        badge.Text = result.Chat.UnreadCount > 0 ? result.Chat.UnreadCount.ToString() : string.Empty;
                        badge.Visibility = result.Chat.UnreadCount > 0 || result.Chat.IsMarkedAsUnread
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                    else
                    {
                        badge.Visibility = Visibility.Collapsed;
                    }
                }

                return true;
            }
            else if (item is Message message)
            {
                if (container.ContentRoot() is not ChatCell content)
                {
                    return false;
                }

                // Same as the ProfileCell branch above: templated cell, named parts assigned on
                // first measure.
                content.ApplyTemplate();
                content.UpdateMessage(ViewModel.ClientService, message, false);
                return true;
            }

            // Nothing to bind for this item type: not a failure, so Loaded must not log for it.
            return true;
        }
#endif

        private void TopChats_ChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TextListViewItem();
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContextRequested += TopChat_ContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void TopChats_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

#if LINUX
            // TextListView.GetContainerForItemOverride already gives the right container and the
            // list has its own ItemTemplate in the XAML, so what is lost with
            // TopChats_ChoosingItemContainer is the context menu hook - and, as everywhere else in
            // this file, ContentTemplateRoot: it is null here (PORTING.md 6) and the body below
            // dereferences it two lines down, so this one was a NullReferenceException raised from
            // inside container preparation, not a blank row. Bound again on Loaded because
            // ContainerContentChanging runs before the container is in the tree.
            if (args.ItemContainer is SelectorItem preparedTop)
            {
                preparedTop.ContextRequested -= TopChat_ContextRequested;
                preparedTop.ContextRequested += TopChat_ContextRequested;

                preparedTop.Loaded -= OnTopChatContainerLoaded;
                preparedTop.Loaded += OnTopChatContainerLoaded;

                UpdateTopChatContainer(preparedTop, args.Item as Chat);
            }

            args.Handled = true;
            return;
#endif

            var content = args.ItemContainer.ContentTemplateRoot as StackPanel;
            var chat = args.Item as Chat;

            var grid = content.Children[0] as Grid;

            var photo = grid.Children[0] as ProfilePicture;
            var title = content.Children[1] as TextBlock;

            photo.Source = ProfilePictureSource.Chat(ViewModel.ClientService, chat);
            title.Text = ViewModel.ClientService.GetTitle(chat, true);

            var badge = grid.Children[1] as BadgeControl;
            badge.Visibility = chat.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            badge.Text = chat.UnreadCount.ToString();

            var user = ViewModel.ClientService.GetUser(chat);
            if (user != null)
            {
                var online = grid.Children[2] as Border;
                online.Visibility = user.Status is UserStatusOnline ? Visibility.Visible : Visibility.Collapsed;
            }

            args.Handled = true;
        }

#if LINUX
        private void OnTopChatContainerLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is SelectorItem container)
            {
                UpdateTopChatContainer(container, container.Content as Chat);
            }
        }

        private bool UpdateTopChatContainer(SelectorItem container, Chat chat)
        {
            if (chat == null || ViewModel == null || container.ContentRoot() is not StackPanel content)
            {
                return false;
            }

            var grid = content.Children[0] as Grid;

            var photo = grid.Children[0] as ProfilePicture;
            var title = content.Children[1] as TextBlock;

            photo.Source = ProfilePictureSource.Chat(ViewModel.ClientService, chat);
            title.Text = ViewModel.ClientService.GetTitle(chat, true);

            var badge = grid.Children[1] as BadgeControl;
            badge.Visibility = chat.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            badge.Text = chat.UnreadCount.ToString();

            var user = ViewModel.ClientService.GetUser(chat);
            if (user != null)
            {
                var online = grid.Children[2] as Border;
                online.Visibility = user.Status is UserStatusOnline ? Visibility.Visible : Visibility.Collapsed;
            }

            return true;
        }
#endif

        #endregion

        #region Context menu

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var result = ItemsHost.ItemFromContainer(sender) as SearchResult;
            if (result != null)
            {
                if (result.Type == SearchResultType.Recent && ViewModel.SelectedTab == 0)
                {
                    var flyout = new MenuFlyout();
                    flyout.CreateFlyoutItem(ViewModel.RemoveRecentChat, result, Strings.DeleteFromRecent, Icons.Delete);
                    flyout.ShowAt(sender, args);
                }
                else
                {
                    // TODO: forward ContextRequested event to parent
                    ItemContextRequested?.Invoke(sender, new ItemContextRequestedEventArgs(result, args));
                }
            }
        }

        private void TopChat_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var chat = TopChats.ItemFromContainer(sender) as Chat;

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(ViewModel.RemoveTopChat, chat, Strings.Delete, Icons.Delete);
            flyout.ShowAt(sender, args);
        }

        #endregion

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResult result && result.Type == SearchResultType.RecentWebApps)
            {
                var user = result.User ?? ViewModel.ClientService.GetUser(result.Chat);
                if (user == null)
                {
                    return;
                }

                if (user.Type is UserTypeBot { HasMainWebApp: true })
                {
#if LINUX
                    // MessageHelper.NavigateToMainWebApp lives inside the #if !LINUX block of
                    // Common/MessageHelper.cs (1337-1700): opening a mini app needs WebAppPopup,
                    // which is not in the subset. Falling through opens the bot's CHAT instead of
                    // its mini app, which is a real destination rather than a dead row.
                    Logger.Info(string.Format("Main web app of {0} not available on Linux, opening the chat instead", user.Id));
#else
                    MessageHelper.NavigateToMainWebApp(ViewModel.ClientService, ViewModel.NavigationService, user, string.Empty, new WebAppOpenModeFullSize());
                    ItemClick?.Invoke(this, null);
                    return;
#endif
                }
            }

            ItemClick?.Invoke(this, e);
        }

        #region Filters (not implemented yet)

        private void Search_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            //if (rpMasterTitlebar.SelectedIndex == 0 && e.Key == Windows.System.VirtualKey.Back)
            //{
            //    if (SearchField.SelectionStart == 0 && SearchField.SelectionLength == 0)
            //    {
            //        if (ViewModel.Chats.SearchFilters?.Count > 0)
            //        {
            //            e.Handled = true;
            //            ViewModel.Chats.SearchFilters.RemoveAt(ViewModel.Chats.SearchFilters.Count - 1);
            //            ViewModel.Chats.Search.UpdateQuery(SearchField.Text);
            //            return;
            //        }
            //    }
            //}
        }

        private void SearchFilters_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            //if (args.Item is ISearchChatsFilter filter)
            //{
            //    var content = args.ItemContainer.ContentTemplateRoot as StackPanel;
            //    if (content == null)
            //    {
            //        return;
            //    }

            //    var glyph = content.Children[0] as TextBlock;
            //    glyph.Text = filter.Glyph ?? string.Empty;

            //    var title = content.Children[1] as TextBlock;
            //    title.Text = filter.Text ?? string.Empty;
            //}
        }

        private void SearchFilters_ItemClick(object sender, ItemClickEventArgs e)
        {
            //if (e.ClickedItem is ISearchChatsFilter filter)
            //{
            //    ViewModel.Chats.SearchFilters.Add(filter);
            //    SearchField.Text = string.Empty;

            //    //ViewModel.Chats.Search = new SearchChatsCollection(ViewModel.ClientService, SearchField.Text, ViewModel.Chats.SearchFilters);
            //}
        }

        #endregion

        private void ClearRecentChats_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearRecentChats();
        }

        private void EmptyState_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                textBlock.Text = string.Format(Strings.NoResultFoundFor2, ViewModel.Query);
            }
        }

        private int _prevSelectedIndex;

        private void ChatFolders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChatFolders.SelectedItem is SearchChatsTabItem page /*&& page.Type != MediaFrame.Content?.GetType()*/)
            {
                NavigationTransitionInfo transition = _prevSelectedIndex == -1
                ? new SuppressNavigationTransitionInfo()
                : new SlideNavigationTransitionInfo
                {
                    Effect = _prevSelectedIndex < ChatFolders.SelectedIndex
                        ? SlideNavigationTransitionEffect.FromRight
                        : SlideNavigationTransitionEffect.FromLeft
                };

                _prevSelectedIndex = ChatFolders.SelectedIndex;
                MediaFrame.Navigate(page.Type, null, transition);
                ShowHideSearch(page.Type == typeof(BlankPage));
            }
        }

        public void OnBackRequested(BackRequestedRoutedEventArgs args)
        {
            if (MediaFrame.Content is INavigablePage tabPage)
            {
                tabPage.OnBackRequested(args);
            }
        }

        private bool _searchCollapsed;

        private void ShowHideSearch(bool show)
        {
            if (_searchCollapsed != show)
            {
                return;
            }

            _searchCollapsed = !show;
            SearchRoot.Visibility = Visibility.Visible;
            SearchRoot.IsHitTestVisible = false;

            MediaRoot.Visibility = Visibility.Visible;
            MediaRoot.IsHitTestVisible = false;

            var effect = show
                ? SlideNavigationTransitionEffect.FromLeft
                : SlideNavigationTransitionEffect.FromRight;

            // Ported from https://github.com/microsoft/microsoft-ui-xaml/blob/d37afef65a0fc3219ba6b349301d685099fb129d/src/dxaml/phone/lib/ThemeTransitions.cpp#L1543
            float translationExitOffset = 150;
            float translationEntranceOffset = -200;
            var inControlPoint1 = new Vector2(0.1f, 0.9f);
            var inControlPoint2 = new Vector2(0.2f, 1.0f);
            var outControlPoint1 = new Vector2(0.7f, 0.0f);
            var outControlPoint2 = new Vector2(1.0f, .5f);
            uint outDuration = 150;
            uint inDuration = 300;
            float reverseTranslationFactor = effect == SlideNavigationTransitionEffect.FromLeft ? 1 : -1;

            ElementCompositionPreview.SetIsTranslationEnabled(SearchRoot, true);
            var visual = ElementComposition.GetElementVisual(SearchRoot);

            var compositor = BootStrapper.Current.Compositor;

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            var translation = compositor.CreateScalarKeyFrameAnimation();

            if (show)
            {
                var easing = compositor.CreateCubicBezierEasingFunction(inControlPoint1, inControlPoint2);

                opacity.InsertKeyFrame(0, 0);
                opacity.InsertKeyFrame(1, 1);
                opacity.Duration = TimeSpan.FromMilliseconds(outDuration);

                translation.InsertKeyFrame(0, translationEntranceOffset * reverseTranslationFactor);
                translation.InsertKeyFrame(1, 0, easing);
                translation.Duration = TimeSpan.FromMilliseconds(outDuration + inDuration);

                opacity.DelayTime = TimeSpan.FromMilliseconds(outDuration);
                opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

                translation.DelayTime = TimeSpan.FromMilliseconds(outDuration);
                translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            }
            else
            {
                var easing = compositor.CreateCubicBezierEasingFunction(outControlPoint1, outControlPoint2);

                opacity.InsertKeyFrame(0, 1);
                opacity.InsertKeyFrame(1, 0);
                opacity.Duration = TimeSpan.FromMilliseconds(outDuration);

                translation.InsertKeyFrame(0, 0);
                translation.InsertKeyFrame(1, translationExitOffset * reverseTranslationFactor, easing);
                translation.Duration = TimeSpan.FromMilliseconds(outDuration);
            }

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            void completed()
            {
                if (_searchCollapsed)
                {
                    SearchRoot.Visibility = Visibility.Collapsed;
                    MediaRoot.IsHitTestVisible = true;
                }
                else
                {
                    MediaRoot.Visibility = Visibility.Collapsed;
                    SearchRoot.IsHitTestVisible = true;
                }
            }

#if !LINUX
            batch.Completed += (s, args) => completed();
#endif

            visual.StartAnimation("Opacity", opacity);
            visual.StartAnimation("Translation.X", translation);

#if LINUX
            // CompositionScopedBatch.Completed never fires in Uno (PORTING.md 6). This handler is
            // the one that collapses whichever of the two roots lost, and - the half that bites -
            // puts IsHitTestVisible back on the one that won: without it the search results draw
            // but do not take a click, which is exactly the "renders and does nothing" this pass
            // is here to remove. Longest of the two animations, which is what decides when the
            // batch would have ended.
            batch.EndWithCompleted(translation.Duration, completed);
#else
            batch.End();
#endif
        }

        #region Media

        private long _itemsSourceToken;
        private long _selectionModeToken;

        private void OnNavigating(object sender, NavigatingCancelEventArgs e)
        {
            if (MediaFrame.Content is ProfileTabPage tabPage)
            {
                tabPage.ScrollingHost.UnregisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, ref _itemsSourceToken);
                tabPage.ScrollingHost.UnregisterPropertyChangedCallback(ListViewBase.SelectionModeProperty, ref _selectionModeToken);
            }
        }

        private void OnNavigated(object sender, NavigationEventArgs e)
        {
            if (e.Content is not ProfileTabPage tabPage)
            {
                return;
            }

            if (tabPage is SearchPostsTabPage)
            {
                tabPage.DataContext = ViewModel.Posts;
            }

            if (tabPage.ScrollingHost.ItemsSource != null)
            {
                LoadMore(tabPage.ScrollingHost);
            }
            else
            {
                tabPage.ScrollingHost.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, OnItemsSourceChanged, ref _itemsSourceToken);
            }

            tabPage.ScrollingHost.RegisterPropertyChangedCallback(ListViewBase.SelectionModeProperty, OnSelectionModeChanged, ref _selectionModeToken);
        }

        private void OnItemsSourceChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (MediaFrame.Content is not ProfileTabPage tabPage || tabPage.ScrollingHost is not ListViewBase scrollingHost)
            {
                return;
            }

            LoadMore(scrollingHost);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            MediaFrame.MinHeight = e.NewSize.Height;

            if (MediaFrame.Content is not ProfileTabPage tabPage || tabPage.ScrollingHost is not ListViewBase scrollingHost)
            {
                return;
            }

            LoadMore(scrollingHost);
        }

        private void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            //BackButton.RequestedTheme = ScrollingHost.VerticalOffset < ProfileHeader.ActualHeight - 16
            //    ? ProfileHeader.HeaderTheme
            //    : ElementTheme.Default;

            //if (ProfileHeader.Visibility == Visibility.Visible)
            //{
            //    ProfileHeader.ViewChanged(ScrollingHost.VerticalOffset);
            //}

            if (MediaFrame.Content is not ProfileTabPage tabPage || tabPage.ScrollingHost is not ListViewBase scrollingHost)
            {
                return;
            }

            LoadMore(scrollingHost);

            var index = scrollingHost.ItemsPanelRoot switch
            {
                ItemsStackPanel stackPanel => stackPanel.FirstVisibleIndex,
                ItemsWrapGrid wrapGrid => wrapGrid.FirstVisibleIndex,
                _ => -1
            };

            if (index < 0 || index >= scrollingHost.Items.Count)
            {
                return;
            }

            //var container = scrollingHost.Items[index];
            //if (container is MessageWithOwner message)
            //{
            //    DateHeaderLabel.Text = Formatter.Date(message.Date, Strings.formatterMonthYear);
            //}
            //else if (container is StoryViewModel story)
            //{
            //    DateHeaderLabel.Text = Formatter.Date(story.Date, Strings.formatterMonthYear);
            //}
            //else
            //{
            //    return;
            //}

            //_dateHeaderTimer.Stop();
            //_dateHeaderTimer.Start();
            //ShowHideDateHeader(ScrollingHost.VerticalOffset > ProfileHeader.ActualHeight, true);
        }

        private bool _loadingMore;

        private async void LoadMore(ListViewBase scrollingHost)
        {
            if (_loadingMore)
            {
                return;
            }

            _loadingMore = true;

            uint loadedMore = 0;
#if LINUX
            // Byte for byte the same method as ProfilePage.LoadMore, and the same fix: LastCacheIndex
            // THROWS NotImplementedException on both ItemsStackPanel and ItemsWrapGrid in Uno. This
            // one is called from OnNavigated, OnViewChanged, OnSizeChanged, OnItemsSourceChanged and
            // OnCollectionChanged, so leaving it would take the search tab down the moment it opens.
            //
            // Same substitute: the question is "is the tab list scrolled to its end", asked of the
            // outer ScrollViewer, which is the one that actually scrolls - the tab list is stretched
            // to its whole content inside MediaFrame. One viewport of slack, in place of the cache.
            var needsMore = ScrollingHost.ScrollableHeight > 0
                && ScrollingHost.VerticalOffset >= ScrollingHost.ScrollableHeight - ScrollingHost.ViewportHeight;
#else
            int lastCacheIndex = scrollingHost.ItemsPanelRoot switch
            {
                ItemsStackPanel stackPanel => stackPanel.LastCacheIndex,
                ItemsWrapGrid wrapGrid => wrapGrid.LastCacheIndex,
                _ => -1
            };

            var needsMore = lastCacheIndex == scrollingHost.Items.Count - 1;
#endif
            needsMore |= scrollingHost.ActualHeight < ScrollingHost.ActualHeight;

            if (needsMore && scrollingHost.ItemsSource is ISupportIncrementalLoading supportIncrementalLoading && supportIncrementalLoading.HasMoreItems)
            {
                var result = await supportIncrementalLoading.LoadMoreItemsAsync(50);
                loadedMore = result.Count;
            }

            _loadingMore = false;

            if (loadedMore > 0)
            {
                LoadMore(scrollingHost);
            }
        }

        #endregion

        #region Selection

        private string ConvertSelection(int count)
        {
            return Locale.Declension(Strings.R.messages, count);
        }

        private void OnSelectionModeChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (sender is ListViewBase selector)
            {
                ShowHideManagePanel(selector.SelectionMode == ListViewSelectionMode.Multiple);
            }
        }

        private bool _manageCollapsed = true;

        private void ShowHideManagePanel(bool show)
        {
            if (_manageCollapsed != show)
            {
                return;
            }

            _manageCollapsed = !show;
            ManagePanel.Visibility = Visibility.Visible;

            var manage = ElementComposition.GetElementVisual(ManagePanel);
            ElementCompositionPreview.SetIsTranslationEnabled(ManagePanel, true);
            manage.Opacity = show ? 0 : 1;

            var batch = manage.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            void completed()
            {
                ManagePanel.Visibility = _manageCollapsed
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

#if !LINUX
            batch.Completed += (s, args) => completed();
#endif

            var offset1 = manage.Compositor.CreateVector3KeyFrameAnimation();
            offset1.InsertKeyFrame(show ? 0 : 1, new Vector3(0, 48, 0));
            offset1.InsertKeyFrame(show ? 1 : 0, new Vector3(0, 0, 0));

            var opacity1 = manage.Compositor.CreateScalarKeyFrameAnimation();
            opacity1.InsertKeyFrame(show ? 0 : 1, 0);
            opacity1.InsertKeyFrame(show ? 1 : 0, 1);

            manage.StartAnimation("Translation", offset1);
            manage.StartAnimation("Opacity", opacity1);

#if LINUX
            // Same as ShowHideSearch: Completed is where ManagePanel is collapsed again, so
            // without it the multi-select bar stays on top of the list for good. Neither of the
            // two animations above sets a Duration, which in Uno means TimeSpan.Zero and an
            // instant jump to the final value, so EndWithCompleted falls back to one frame.
            batch.EndWithCompleted(offset1.Duration, completed);
#else
            batch.End();
#endif
        }

        #endregion
    }

    public partial class SearchListViewItem : TextListViewItem
    {
        public string TypeName { get; }

        public SearchListViewItem(string typeName)
        {
            TypeName = typeName;
        }
    }
}
