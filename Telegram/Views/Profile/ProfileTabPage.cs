//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Chats;
using Telegram.Controls.Media;
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Profile;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
#if LINUX
using System;
using Windows.Foundation;
using Microsoft.UI.Xaml.Data;
#endif

namespace Telegram.Views.Profile
{
    public partial class ProfileTabPage : PageEx, INavigablePage
    {
        public MediaTabsViewModelBase ViewModel
        {
            get
            {
                try
                {
                    return DataContext as MediaTabsViewModelBase;
                }
                catch
                {
                    return null;
                }
            }
        }

        public bool IsProfile { get; private set; }

        public ProfileTabPage()
        {
            Connected += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ScrollingHost.ItemsSource = _itemsSource;
        }

        private object _itemsSource;
        public object ItemsSource
        {
            get => ScrollingHost.ItemsSource;
            set
            {
                if (IsConnected)
                {
                    ScrollingHost.ItemsSource = value;
                }
                else
                {
                    _itemsSource = value;
                }
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            IsProfile = DataContext is ProfileViewModel;
#if LINUX
            AttachIncrementalLoader();
#endif
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
#if LINUX
            DetachIncrementalLoader();
#endif
            ScrollingHost.ItemsSource = null;
        }

        /// <summary>
        /// Appends the entrance transition the five media tabs use for their first fill.
        /// </summary>
        protected void AddEntranceTransition()
        {
#if LINUX
            // TableListView's default style sets ItemContainerTransitions; a bare GridView's does
            // not have to. A null here is a NullReferenceException raised from inside
            // OnNavigatedTo, which takes the navigation down with it and leaves the profile page
            // showing an empty frame. The transitions are inert in Uno anyway -- the four
            // *ThemeTransition types carry properties and no behaviour -- so this only has to not
            // throw.
            ScrollingHost.ItemContainerTransitions ??= new TransitionCollection();
#endif
            ScrollingHost.ItemContainerTransitions.Add(new EntranceThemeTransition { IsStaggeringEnabled = false });
        }

        public void OnBackRequested(BackRequestedRoutedEventArgs args)
        {
            if (ViewModel?.SelectedItems.Count > 0)
            {
                ViewModel.UnselectMessages();
                args.Handled = true;
            }
#if !LINUX
            else if (DataContext is ProfileStoriesTabViewModel stories && stories.SelectedItems.Count > 0)
            {
                stories.UnselectStories();
                args.Handled = true;
            }
#endif
        }

        #region Context menu

        private async void Message_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var message = ScrollingHost.ItemFromContainer(sender) as MessageWithOwner;
            if (message == null)
            {
                return;
            }

            var flyout = new MenuFlyout();

            var selected = ViewModel.SelectedItems;
            if (selected.Count > 0)
            {
                if (selected.Contains(message))
                {
                    // CC-3b (2026-09-04): Reenviar seleccionados vuelve a estar viva -- el picker
                    // que este menu abre (ChooseChatsPopup) ya esta en el subconjunto desde CC-1.
                    flyout.CreateFlyoutItem(ViewModel.ForwardSelectedMessages, Strings.ForwardSelected, Icons.Share);

                    //if (chat.CanBeReported)
                    //{
                    //    flyout.CreateFlyoutItem(ViewModel.MessagesReportCommand, "Report Selected", Icons.ShieldError);
                    //}

#if !LINUX
                    // Eliminar seleccionados SIGUE fuera: el popup de confirmacion
                    // (DeleteMessagesPopup, «para todos / solo para mi») no esta en el
                    // subconjunto PARA ESTE ViewModel, y ViewModel.DeleteSelectedMessages se
                    // niega en Linux por eso mismo (ver la region hermana ForwardMessages en
                    // MediaTabsViewModelBase.cs). Un boton que no puede preguntar "para todos o
                    // solo para mi" antes de borrar de la cuenta real no se dibuja.
                    flyout.CreateFlyoutItem(ViewModel.DeleteSelectedMessages, Strings.DeleteSelected, Icons.Delete, destructive: true);
#endif
                    flyout.CreateFlyoutItem(ViewModel.UnselectMessages, Strings.ClearSelection);
                    //flyout.CreateFlyoutSeparator();
                    //flyout.CreateFlyoutItem(ViewModel.MessagesCopyCommand, "Copy Selected as Text", Icons.DocumentCopy);
                }
                else
                {
                    flyout.CreateFlyoutItem(MessageSelect_Loaded, ViewModel.SelectMessage, message, Strings.Select, Icons.CheckmarkCircle);
                }
            }
            else
            {
                var properties = await message.ClientService.SendAsync(new GetMessageProperties(message.ChatId, message.Id)) as MessageProperties;
                if (properties == null || ViewModel == null)
                {
                    return;
                }

                flyout.CreateFlyoutItem(MessageView_Loaded, ViewModel.ViewMessage, message, Strings.ShowInChat2, Icons.ChatEmpty);

#if !LINUX
                // Mismo motivo que la region hermana de arriba: DeleteMessagesPopup no esta en
                // el subconjunto para este ViewModel.
                if (MessageDelete_Loaded(message, properties))
                {
                    flyout.CreateFlyoutItem(ViewModel.DeleteMessage, message, Strings.Delete, Icons.Delete, destructive: true);
                }
#endif

                // CC-3b (2026-09-04): Reenviar vuelve a estar vivo, mismo motivo que arriba.
                if (MessageForward_Loaded(message, properties))
                {
                    flyout.CreateFlyoutItem(ViewModel.ForwardMessage, message, Strings.Forward, Icons.Share);
                }

                flyout.CreateFlyoutItem(MessageSelect_Loaded, ViewModel.SelectMessage, message, Strings.Select, Icons.CheckmarkCircle);
                flyout.CreateFlyoutItem(MessageSaveMedia_Loaded, ViewModel.CopyMessagePath, message, Strings.CopyAsPath, Icons.CopyAsPath);
                flyout.CreateFlyoutItem(MessageSaveMedia_Loaded, ViewModel.SaveMessageMedia, message, Strings.SaveAs, Icons.SaveAs);
                flyout.CreateFlyoutItem(MessageOpenMedia_Loaded, ViewModel.OpenMessageWith, message, Strings.OpenWith, Icons.OpenWith);
                flyout.CreateFlyoutItem(MessageOpenFolder_Loaded, ViewModel.OpenMessageFolder, message, Strings.ShowInFolder, Icons.FolderOpen);
            }

            flyout.ShowAt(sender, args);
        }

        private bool MessageView_Loaded(MessageWithOwner message)
        {
            return true;
        }

        private bool MessageSaveMedia_Loaded(MessageWithOwner message)
        {
            if (message.SelfDestructType is not null || !message.CanBeSaved)
            {
                return false;
            }

            var file = message.GetFile();
            if (file != null)
            {
                return file.Local.IsDownloadingCompleted;
            }

            return false;

            return message.Content switch
            {
                MessagePhoto photo => photo.Photo.GetBig()?.Photo.Local.IsDownloadingCompleted ?? false,
                MessageAudio audio => audio.Audio.AudioValue.Local.IsDownloadingCompleted,
                MessageDocument document => document.Document.DocumentValue.Local.IsDownloadingCompleted,
                MessageVideo video => video.Video.VideoValue.Local.IsDownloadingCompleted,
                _ => false
            };
        }

        private bool MessageOpenMedia_Loaded(MessageWithOwner message)
        {
            if (message.SelfDestructType is not null || !message.CanBeSaved)
            {
                return false;
            }

            return message.Content switch
            {
                MessageAudio audio => audio.Audio.AudioValue.Local.IsDownloadingCompleted,
                MessageDocument document => document.Document.DocumentValue.Local.IsDownloadingCompleted,
                MessageVideo video => video.Video.VideoValue.Local.IsDownloadingCompleted,
                _ => false
            };
        }

        private bool MessageOpenFolder_Loaded(MessageWithOwner message)
        {
            if (message.SelfDestructType is not null || !message.CanBeSaved)
            {
                return false;
            }

            return message.Content switch
            {
                MessagePhoto photo => ViewModel.StorageService.CheckAccessToFolder(photo.Photo.GetBig()?.Photo),
                MessageAudio audio => ViewModel.StorageService.CheckAccessToFolder(audio.Audio.AudioValue),
                MessageDocument document => ViewModel.StorageService.CheckAccessToFolder(document.Document.DocumentValue),
                MessageVideo video => ViewModel.StorageService.CheckAccessToFolder(video.Video.VideoValue),
                _ => false
            };
        }

        private bool MessageDelete_Loaded(MessageWithOwner message, MessageProperties properties)
        {
            return properties.CanBeDeletedOnlyForSelf || properties.CanBeDeletedForAllUsers;
        }

        private bool MessageForward_Loaded(MessageWithOwner message, MessageProperties properties)
        {
            return properties.CanBeForwarded;
        }

        private bool MessageSelect_Loaded(MessageWithOwner message)
        {
            return true;
        }

        #endregion

        #region Linux

#if LINUX

        /// <summary>
        /// What <see cref="OnChoosingItemContainer"/> would have done, from a callback that Uno
        /// actually raises.
        /// </summary>
        /// <remarks>
        /// ListViewBase.ChoosingItemContainer never fires in Uno: add_ChoosingItemContainer and
        /// remove_ChoosingItemContainer share one method body, and that body is a call to
        /// ApiInformation.TryRaiseNotImplemented (measured in the deployed Uno.UI.dll 6.6.184 -
        /// the two accessors are literally the same RVA, 0x11bc6d, next to a real add/remove pair
        /// for ContainerContentChanging). Nothing throws, nothing warns beyond one log line: the
        /// containers are simply the default ones and nobody hooks them up.
        ///
        /// Two of the three things upstream does there survive without help: TableListView
        /// already returns a TableListViewItem from GetContainerForItemOverride, and the item
        /// template travels to the presenter through the TemplateBinding that Uno's ListViewItem
        /// template does carry. What is lost is the ContextRequested subscription -- that is the
        /// entire context menu of every media tab -- and the accessibility peer, which is not
        /// reachable on this host yet anyway.
        /// </remarks>
        protected void PrepareContainer(ContainerContentChangingEventArgs args)
        {
            var container = args?.ItemContainer;
            if (container == null || GetContextRequestedAttached(container))
            {
                return;
            }

            SetContextRequestedAttached(container, true);
            container.ContextRequested += Message_ContextRequested;
        }

        /// <summary>
        /// Hooks the container up and hands the expanded cell to <paramref name="bind"/>, waiting
        /// for the template to expand if it has not yet.
        /// </summary>
        /// <remarks>
        /// Two Uno facts meet here. ContentTemplateRoot is never assigned for a container that
        /// has a control template, so the type test upstream writes
        /// (<c>args.ItemContainer.ContentTemplateRoot is SharedMediaCell cell</c>) silently never
        /// matches and the cell is never filled - that is the empty-row failure the chat list had.
        /// And ContentTemplateRootEx, which does find it, only answers once the container is in
        /// the visual tree, while ContainerContentChanging is raised before that. Giving up on the
        /// first miss is what left 26 rows of the chat history drawn as empty bubbles after a jump
        /// (PORTING.md 6). So: try now, and if it is too early, try again on the container's own
        /// layout passes, checking each time that it has not been recycled onto another item.
        /// </remarks>
        protected void BindContainer<TContent>(ContainerContentChangingEventArgs args, Action<TContent, object> bind) where TContent : class
        {
            var container = args?.ItemContainer;
            if (container == null || bind == null)
            {
                return;
            }

            var item = args.Item;

            PrepareContainer(args);

            if (container.ContentRoot() is TContent content)
            {
                bind(content, item);
                return;
            }

            var attempts = 0;

            void OnLayoutUpdated(object sender, object e)
            {
                attempts++;

                if (attempts > 8 || !ReferenceEquals(ScrollingHost?.ItemFromContainer(container), item))
                {
                    container.LayoutUpdated -= OnLayoutUpdated;
                    return;
                }

                if (container.ContentRoot() is TContent expanded)
                {
                    container.LayoutUpdated -= OnLayoutUpdated;
                    bind(expanded, item);
                }
            }

            container.LayoutUpdated += OnLayoutUpdated;
        }

        private static bool GetContextRequestedAttached(DependencyObject obj)
        {
            return (bool)obj.GetValue(ContextRequestedAttachedProperty);
        }

        private static void SetContextRequestedAttached(DependencyObject obj, bool value)
        {
            obj.SetValue(ContextRequestedAttachedProperty, value);
        }

        // Containers are recycled, so the subscription has to happen once per container and not
        // once per item: a flag on the container itself, which travels with it through the
        // recycle queue.
        private static readonly DependencyProperty ContextRequestedAttachedProperty =
            DependencyProperty.RegisterAttached("ContextRequestedAttached", typeof(bool), typeof(ProfileTabPage), new PropertyMetadata(false));

        // ------------------------------------------------------------------------------------
        // Incremental loading.
        //
        // These tab pages have no ScrollViewer of their own: their control template is a bare
        // Border around an ItemsPresenter, because ProfilePage owns the single ScrollViewer that
        // scrolls the header and the tab together. Upstream compensates by pumping the pages from
        // ProfilePage.LoadMore, which asks the panel for LastCacheIndex - and Uno throws
        // NotImplementedException from that getter on ItemsStackPanel and on ItemsWrapGrid alike
        // (measured: both getters are `ldstr; newobj NotImplementedException; throw`). So the tab
        // pulls its own pages here, off the ancestor ScrollViewer, and does not care whether
        // ProfilePage manages to ask for any.
        // ------------------------------------------------------------------------------------

        private ScrollViewer _outerScrollingHost;
        private long _itemsSourceToken;
        private bool _loadingMore;
        private double _lastKnownExtent;
        private int _fruitlessPages;

        // A page that does not make the list any taller is a page that will not make the next one
        // taller either. Sixteen of those in a row and the tab stops asking, the same shape of
        // guard ChatHistoryView uses for the panel that measures 0.
        private const int MaxFruitlessPages = 16;

        private void AttachIncrementalLoader()
        {
            var host = ScrollingHost;
            if (host == null)
            {
                return;
            }

            _outerScrollingHost ??= this.GetParent<ScrollViewer>();

            if (_outerScrollingHost != null)
            {
                _outerScrollingHost.ViewChanged -= OnOuterViewChanged;
                _outerScrollingHost.ViewChanged += OnOuterViewChanged;
            }

            host.SizeChanged -= OnScrollingHostSizeChanged;
            host.SizeChanged += OnScrollingHostSizeChanged;

            host.UnregisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, _itemsSourceToken);
            _itemsSourceToken = host.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, OnItemsSourceChanged);

            _lastKnownExtent = 0;
            _fruitlessPages = 0;

            LoadMoreItems();
        }

        private void DetachIncrementalLoader()
        {
            if (_outerScrollingHost != null)
            {
                _outerScrollingHost.ViewChanged -= OnOuterViewChanged;
                _outerScrollingHost = null;
            }

            var host = _scrollingHost;
            if (host != null)
            {
                host.SizeChanged -= OnScrollingHostSizeChanged;
                host.UnregisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, _itemsSourceToken);
            }

            _itemsSourceToken = 0;
            _lastKnownExtent = 0;
            _fruitlessPages = 0;
        }

        private void OnItemsSourceChanged(DependencyObject sender, DependencyProperty dp)
        {
            _lastKnownExtent = 0;
            _fruitlessPages = 0;

            LoadMoreItems();
        }

        private void OnScrollingHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            LoadMoreItems();
        }

        private void OnOuterViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            LoadMoreItems();
        }

        private async void LoadMoreItems()
        {
            if (_loadingMore)
            {
                return;
            }

            var host = _scrollingHost;
            if (host == null || _fruitlessPages >= MaxFruitlessPages)
            {
                return;
            }

            if (host.ItemsSource is not ISupportIncrementalLoading source || !source.HasMoreItems)
            {
                return;
            }

            if (!NeedsMoreItems(host))
            {
                _fruitlessPages = 0;
                return;
            }

            _loadingMore = true;

            try
            {
                var extent = host.ActualHeight;
                await source.LoadMoreItemsAsync(50);

                // The list only grows on the next layout pass, so comparing here would always say
                // "no growth". What this counts is pages that left the previous pass's height
                // untouched, which is the runaway to stop.
                if (extent > 0 && extent <= _lastKnownExtent)
                {
                    _fruitlessPages++;
                }
                else
                {
                    _fruitlessPages = 0;
                }

                _lastKnownExtent = extent;
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
            finally
            {
                _loadingMore = false;
            }
        }

        private bool NeedsMoreItems(ListViewBase host)
        {
            var outer = _outerScrollingHost;
            if (outer == null || outer.ViewportHeight <= 0)
            {
                // No viewport to measure against yet: the first page is always worth having.
                return host.Items.Count == 0;
            }

            // The tab fills the scroll content underneath the profile header, so its bottom edge
            // in the scroller's own coordinates is close enough to its height for this decision.
            var seen = outer.VerticalOffset + outer.ViewportHeight;
            return host.ActualHeight - seen < outer.ViewportHeight;
        }

#endif

        #endregion

        protected virtual void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                if (sender is ListView)
                {
                    args.ItemContainer = new TableAccessibleChatListViewItem(sender);
                }
                else
                {
                    args.ItemContainer = new ChatGridViewItem(sender);
                }

                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;

                if (args.Item is MessageWithOwner or null)
                {
                    args.ItemContainer.ContextRequested += Message_ContextRequested;
                }
            }

            if (sender.ItemTemplateSelector != null)
            {
                args.ItemContainer.ContentTemplate = sender.ItemTemplateSelector.SelectTemplate(args.Item, args.ItemContainer);
            }

            args.IsContainerPrepared = true;
        }



        private ListViewBase _scrollingHost;
        public ListViewBase ScrollingHost => _scrollingHost ??= FindName(nameof(ScrollingHost)) as ListViewBase;

        private FrameworkElement _header;
#if LINUX
        // MEASURED (PORTING.md §6): Uno's FindName is a VISUAL TREE search, not a namescope
        // lookup. IFrameworkElementHelper.FindName compares the element's own Name and then walks
        // FindLastChild over the children; the XAML generator does emit
        // __nameScope.RegisterName("Header", ...) but nothing ever reads it back. "Header" is
        // declared inside <ListView.Header>, i.e. it is a PROPERTY VALUE, and it only enters the
        // tree once the ItemsPresenter builds its header presenter — well after navigation. So
        // FindName answers null here and ProfilePage.OnNavigated's `tabPage.HeaderHeight = ...`
        // is a NullReferenceException raised from inside the Frame's Navigated event: the tab
        // page is dropped and the profile shows its header with an empty body underneath.
        //
        // ScrollingHost is found because it IS a child of the page's Grid. The header is reached
        // instead through the ListViewBase.Header property, which holds the subtree the XAML
        // built whether or not anything has been parented yet.
        public FrameworkElement Header => _header ??= FindName(nameof(Header)) as FrameworkElement
            ?? FindByName(ScrollingHost?.Header, nameof(Header));

        /// <summary>
        /// Depth-first search for an <c>x:Name</c> through an unparented subtree, over the three
        /// containers these headers are built from.
        /// </summary>
        private static FrameworkElement FindByName(object root, string name)
        {
            if (root is not FrameworkElement element)
            {
                return null;
            }

            if (string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }

            if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (FindByName(child, name) is FrameworkElement found)
                    {
                        return found;
                    }
                }
            }
            else if (element is Border border)
            {
                return FindByName(border.Child, name);
            }
            else if (element is ContentControl content)
            {
                return FindByName(content.Content, name);
            }

            return null;
        }

        /// <summary>
        /// True for a tab whose ItemsPanel is a <see cref="VariableSizedWrapGrid"/>, i.e. the
        /// media, GIF and gifts grids. See <see cref="HeaderHeight"/>.
        /// </summary>
        protected virtual bool HeaderStacksBesideItems => false;

        public virtual double HeaderHeight
        {
            get => Header?.Height ?? 0;
            set
            {
                // MEASURED (PORTING.md §6). Uno's ItemsPresenter decides whether to stack
                // Header / Panel / Footer along X or along Y from `(Panel as Panel).PhysicalOrientation`,
                // and for VariableSizedWrapGrid that internal property returns the ITEM FLOW axis,
                // not the scroll axis: `internal override Orientation? PhysicalOrientation => Orientation;`.
                // Orientation="Horizontal" on a wrap grid means "rows, wrapping downwards", so it
                // scrolls VERTICALLY - but the ItemsPresenter reads Horizontal and lays the header
                // out BESIDE the grid. (The same type's explicit IOrientedPanel.PhysicalOrientation
                // inverts it and is correct; the ItemsPresenter reads the other one.) The header is
                // a bare spacer with no width, so it collapses to nothing on the left and the
                // thumbnails are drawn from the top of the page, straight over the profile header,
                // the info card and the tab strip. ItemsStackPanel is unaffected: for a stack panel
                // the flow axis IS the scroll axis.
                //
                // PhysicalOrientation is `internal` on Panel, so it cannot be overridden from here.
                // The offset moves to the list's top Padding instead, which ItemsPresenter applies
                // as the origin of its layout rect in BOTH orientations
                // (`finalRect = new Rect(new Point(padding.Left, padding.Top), ...)`). The spacer
                // Border is left at height 0 so it contributes no desired size in either axis.
                if (HeaderStacksBesideItems)
                {
                    if (ScrollingHost is ListViewBase host)
                    {
                        var padding = host.Padding;
                        host.Padding = new Thickness(padding.Left, value, padding.Right, padding.Bottom);
                    }

                    return;
                }

                if (Header is FrameworkElement header)
                {
                    header.Height = value;
                }
            }
        }
#else
        public FrameworkElement Header => _header ??= FindName(nameof(Header)) as FrameworkElement;

        public virtual double HeaderHeight
        {
            get => Header.Height;
            set => Header.Height = value;
        }
#endif
    }
}
