//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
#if LINUX
// DrawerContainers.WithContentRoot: the substitute for ContentTemplateRoot (PORTING.md 6).
using Telegram.Controls.Drawers;
#endif
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Views.Popups
{
    public sealed partial class StickersPopup : ContentPopup
    {
        public StickersViewModel ViewModel => DataContext as StickersViewModel;

        private readonly AnimatedListHandler _handler;
        private readonly ZoomableListHandler _zoomer;

        private StickersPopup(INavigationService navigationService)
        {
            InitializeComponent();

#if LINUX
            // Tapping a sticker here sends it straight into the open chat, which goes
            // through ChatView.Stickers_ItemClick -> TextField/SendSticker: the composer,
            // which is not in this subset. The click is switched OFF rather than left
            // hanging, so the grid reads as the preview it is. Everything the popup is
            // actually for -- seeing the pack, Add/Remove, copying the link -- is intact.
            ScrollingHost.IsItemClickEnabled = false;

            // The repeater replaces the grid on this head: see the comment on RepeaterHost in the
            // XAML. These two touch no view model, so they stay ahead of the DataContext.
            Repeater.ElementPrepared += OnSetElementPrepared;
            RepeaterHost.SizeChanged += OnRepeaterHostSizeChanged;
#endif
            DataContext = navigationService.Session.Resolve<StickersViewModel>();

#if LINUX
            // BELOW the assignment above, and it has to be: ViewModel READS DataContext, so
            // subscribing before it was set dereferenced null and threw out of the constructor --
            // the popup did not render wrong, it never opened. Still ahead of everything that can
            // make the view model fetch, which is the reason this was placed early to begin with:
            // ShowAsyncInternal empties Items and only THEN awaits the download, so the first fill
            // has to land on a subscription that already exists.
            ViewModel.Items.CollectionChanged += OnItemsChangedLinux;
#endif

            VerticalContentAlignment = VerticalAlignment.Center;

            ViewModel.NavigationService = navigationService;
            ViewModel.Dispatcher = navigationService.Dispatcher;
            ViewModel.PropertyChanged += OnPropertyChanged;

            // TODO: this might need to change depending on context
            _handler = new AnimatedListHandler(ScrollingHost, AnimatedListType.Stickers);

            _zoomer = new ZoomableListHandler(ScrollingHost);
            _zoomer.Opening += Zoomer_Opening;
            _zoomer.Closing += Zoomer_Closing;
        }

        private void Zoomer_Opening(object sender, EventArgs e)
        {
            _handler.Suspend();
        }

        private void Zoomer_Closing(object sender, EventArgs e)
        {
            _handler.Resume();
        }

        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            ViewModel.PropertyChanged -= OnPropertyChanged;

#if LINUX
            ViewModel.Items.CollectionChanged -= OnItemsChangedLinux;
            Repeater.ItemsSource = null;
#endif

            _handler.UnloadItems();
        }

        private void OnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName.Equals("STICKERSET_INVALID"))
            {
                Hide();
                ViewModel.NavigationService.ShowToast(Strings.AddStickersNotFound, ToastPopupIcon.Info);
            }
        }

        #region Show

        public static Task<ContentDialogResult> ShowAsync(INavigationService navigation, StickerSet parameter)
        {
            return ShowAsyncInternal(navigation, parameter);
        }

        public static Task<ContentDialogResult> ShowAsync(INavigationService navigation, HashSet<long> parameter)
        {
            return ShowAsyncInternal(navigation, parameter);
        }

        public static Task<ContentDialogResult> ShowAsync(INavigationService navigation, long parameter)
        {
            return ShowAsyncInternal(navigation, parameter);
        }

        public static Task<ContentDialogResult> ShowAsync(INavigationService navigation, InputFileId parameter)
        {
            return ShowAsyncInternal(navigation, parameter);
        }

        public static Task<ContentDialogResult> ShowAsync(INavigationService navigation, string parameter)
        {
            return ShowAsyncInternal(navigation, parameter);
        }

        private static Task<ContentDialogResult> ShowAsyncInternal(INavigationService navigation, object parameter)
        {
            var popup = new StickersPopup(navigation);

            popup.ViewModel.IsLoading = true;
            popup.ViewModel.Items.Clear();

            RoutedEventHandler handler = null;
            handler = new RoutedEventHandler(async (s, args) =>
            {
                popup.Loaded -= handler;
                await popup.ViewModel.NavigatedToAsync(parameter, NavigationMode.New, null);
            });

            popup.Loaded += handler;
            return popup.ShowQueuedAsync(navigation.XamlRoot);
        }

        #endregion

        #region Recycle

        private void OnChoosingGroupHeaderContainer(ListViewBase sender, ChoosingGroupHeaderContainerEventArgs args)
        {
            if (args.GroupHeaderContainer == null)
            {
                args.GroupHeaderContainer = new GridViewHeaderItem
                {
                    Style = sender.GroupStyle[0].HeaderContainerStyle,
                    ContentTemplate = sender.GroupStyle[0].HeaderTemplate
                };
            }

            args.GroupHeaderContainer.Padding = new Thickness(0, args.GroupIndex > 0 ? 16 : 0, 0, 0);
            args.GroupHeaderContainer.Visibility = ViewModel.Items.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new GridViewItem();
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContextRequested += OnContextRequested;

                _zoomer.ElementPrepared(args.ItemContainer);
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
#if LINUX
            // ContentTemplateRoot is always null on Uno once the container has a template, and
            // every GridViewItem here has one (PORTING.md 6), so the upstream body below would
            // bail on its own null check and every sticker in the pack would stay blank. The two
            // conditions that do NOT depend on the template are still answered synchronously, and
            // the drawers' helper delivers the root now or as soon as it exists -- re-checking the
            // item first, because these containers are pooled.
            var sticker = args.Item as ViewModels.Drawers.StickerViewModel;

            if (args.InRecycleQueue || sticker?.StickerValue == null)
            {
                return;
            }

            var service = ViewModel.ClientService;
            DrawerContainers.WithContentRoot<Grid>(sender, args.ItemContainer, args.Item, content =>
            {
                if (content.Children[0] is AnimatedImage late)
                {
                    using (late.BeginBatchUpdate())
                    {
                        late.FrameSize = sticker.FullType is StickerFullTypeCustomEmoji
                            ? new Size(40, 40)
                            : new Size(64, 64);

                        late.Source = new DelayedFileSource(service, sticker);
                    }
                }
            });
#else
            var content = args.ItemContainer.ContentTemplateRoot as Grid;
            var sticker = args.Item as ViewModels.Drawers.StickerViewModel;

            if (args.InRecycleQueue || content == null || sticker == null)
            {
                return;
            }

            var file = sticker.StickerValue;
            if (file == null)
            {
                return;
            }

            var animated = content.Children[0] as AnimatedImage;
            using (animated.BeginBatchUpdate())
            {
                if (sticker.FullType is StickerFullTypeCustomEmoji)
                {
                    animated.FrameSize = new Size(40, 40);
                }
                else
                {
                    animated.FrameSize = new Size(64, 64);
                }

                animated.Source = new DelayedFileSource(ViewModel.ClientService, sticker);
            }
#endif

            args.Handled = true;
        }

        #endregion

        #region Binding

        private int ConvertItemsPerRow(StickerType type)
        {
            return type is StickerTypeCustomEmoji ? 8 : 5;
        }

        private string ConvertIsInstalled(bool installed, bool archived, StickerType type)
        {
            if (ViewModel == null || ViewModel.IsLoading)
            {
                return string.Empty;
            }

            if (ViewModel.Items.Count > 1)
            {
                MoreButton.Visibility = Visibility.Collapsed;

                if (installed && !archived)
                {
                    PrimaryButtonStyle = BootStrapper.Current.Resources["DangerButtonStyle"] as Style;
                    return Locale.Declension(Strings.R.RemoveManyEmojiPacksCount, ViewModel.Items.Count(x => x.IsInstalled));
                }

                PrimaryButtonStyle = BootStrapper.Current.Resources["AccentButtonStyle"] as Style;
                return Locale.Declension(Strings.R.AddManyEmojiPacksCount, ViewModel.Items.Count(x => !x.IsInstalled));

            }
            else
            {
                MoreButton.Visibility = Visibility.Visible;

                if (installed && !archived)
                {
                    PrimaryButtonStyle = BootStrapper.Current.Resources["DangerButtonStyle"] as Style;
                    return Locale.Declension(type is StickerTypeCustomEmoji ? Strings.R.RemoveManyEmojiCount : Strings.R.RemoveManyStickersCount, ViewModel.Count);
                }

                PrimaryButtonStyle = BootStrapper.Current.Resources["AccentButtonStyle"] as Style;
                return Locale.Declension(type is StickerTypeCustomEmoji ? Strings.R.AddManyEmojiCount : Strings.R.AddManyStickersCount, ViewModel.Count);
            }
        }

        #endregion

        private void List_ItemClick(object sender, ItemClickEventArgs e)
        {
#if !LINUX
            if (ViewModel.NavigationService?.Content is Page { Content: ChatView view } && e.ClickedItem is ViewModels.Drawers.StickerViewModel sticker)
            {
                if (sticker.FullType is StickerFullTypeCustomEmoji)
                {
                    view.Emojis_ItemClick((Sticker)sticker);
                }
                else
                {
                    view.Stickers_ItemClick((Sticker)sticker);
                }

                Hide();
            }
#endif
        }

        private void Player_Ready(object sender, EventArgs e)
        {
            _handler.ThrottleVisibleItems();
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var sticker = ScrollingHost.ItemFromContainer(sender) as ViewModels.Drawers.StickerViewModel;
            if (sticker?.FullType is not StickerFullTypeCustomEmoji customEmoji)
            {
                return;
            }

            void Copy(Sticker sticker)
            {
                MessageHelper.CopyText(XamlRoot, sticker.ToFormattedText());
            }

            void SetAsStatus(Sticker sticker)
            {
                ViewModel.ClientService.Send(new SetEmojiStatus(new EmojiStatus(new EmojiStatusTypeCustomEmoji(customEmoji.CustomEmojiId), 0)));
                ViewModel.ShowToast(Strings.SetAsEmojiStatusInfo, DelayedFileSource.FromSticker(ViewModel.ClientService, sticker));
            }

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(Copy, (Sticker)sticker, Strings.CopyEmojiPreview, Icons.Copy);
            flyout.CreateFlyoutItem(SetAsStatus, (Sticker)sticker, Strings.SetAsEmojiStatus, Icons.Emoji);

            flyout.ShowAt(sender as UIElement, args);
        }

        private void More_ContextRequested(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();
            // CC-2: CC-1 ported ChooseChatsPopup; "Send to..." uses ChooseChatsConfigurationPostLink,
            // one of the 13 configs CC-1 kept live.
            flyout.CreateFlyoutItem(Share, Strings.ShareFile, Icons.Share);
            flyout.CreateFlyoutItem(CopyLink, Strings.CopyLink, Icons.Link);

            flyout.ShowAt(sender as UIElement, FlyoutPlacementMode.BottomEdgeAlignedRight);
        }

        private void Share()
        {
            Hide();
            ViewModel.ShowPopup(new ChooseChatsPopup(), new ChooseChatsConfigurationPostLink(new InternalLinkTypeStickerSet(ViewModel.Items[0].Name, ViewModel.StickerType is StickerTypeCustomEmoji)));
        }

        private void CopyLink()
        {
            MessageHelper.CopyLink(ViewModel.ClientService, XamlRoot, new InternalLinkTypeStickerSet(ViewModel.Items[0].Name, ViewModel.StickerType is StickerTypeCustomEmoji));
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            ViewModel.Execute();
        }

#if LINUX
        private readonly HashSet<ItemsRepeater> _innerRepeaters = new();

        // The cell the repeater lays out in, and the height each cell is given. A sticker pack
        // packs 5 to a row at a 64x64 frame plus 2px either side; a custom-emoji pack packs 8, at
        // 40x40 plus the same 2px. Same arithmetic ConvertItemsPerRow does for the GridView.
        private double _cell = 68;
        private int _columns;

        /// <summary>
        /// Points the repeater at the packs and hides the GridView, which cannot render them.
        /// </summary>
        /// <remarks>
        /// Called on every change of ViewModel.Items rather than once, because the popup is shown
        /// before its content is fetched: ShowAsyncInternal clears Items and only then awaits
        /// NavigatedToAsync, so the collection this binds to is empty at construction and filled a
        /// network round trip later. Binding once would show an empty popup for good, which is the
        /// same shape of bug as the x:Bind without Mode=OneWay next door.
        /// </remarks>
        private void OnItemsChangedLinux(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Items is filled from an async void handler over TDLib, so this can arrive off the
            // view thread; touching the repeater from there throws, and an exception raised inside
            // a CollectionChanged handler goes nowhere at all.
            this.BeginOnUIThread(BindRepeaterLinux);
        }

        private void BindRepeaterLinux()
        {
            try
            {
                _cell = ViewModel.StickerType is StickerTypeCustomEmoji ? 44 : 68;

                Repeater.ItemsSource = ViewModel.Items;
                RepeaterHost.Visibility = Visibility.Visible;

                // The GridView stays in the tree (ScrollViewerScrim binds to it by name) but must
                // hold nothing: a non-virtualizing panel builds a container per sticker whether it
                // is visible or not.
                ScrollingHost.ItemsSource = null;
                ScrollingHost.Visibility = Visibility.Collapsed;

                Logger.Info($"StickersPopup: repeater bound to {ViewModel.Items.Count} pack(s), cell {_cell}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "StickersPopup: binding the repeater failed");
            }
        }

        private void OnSetElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is not FrameworkElement root)
            {
                return;
            }

            // A single pack shows no title: that is what the GridView did by collapsing the group
            // header container when Items.Count was 1, and the pack's name is already the popup's
            // own heading.
            if (FindDescendant<TextBlock>(root, "SetTitle") is TextBlock title)
            {
                title.Visibility = ViewModel.Items.Count > 1
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (FindDescendant<ItemsRepeater>(root, "SetStickers") is not ItemsRepeater inner)
            {
                return;
            }

            if (inner.Layout is UniformGridLayout layout)
            {
                layout.MinItemWidth = _cell;
                layout.MinItemHeight = _cell;
            }

            if (_innerRepeaters.Add(inner))
            {
                inner.ElementPrepared += OnStickerElementPrepared;
                inner.ElementClearing += OnStickerElementClearing;
            }

            SizeInnerRepeater(inner);
        }

        /// <summary>
        /// Gives one pack's wrap repeater the height its stickers will actually need.
        /// </summary>
        /// <remarks>
        /// A nested ItemsRepeater sizes itself from what it realizes and realizes from its
        /// effective viewport, so an inner repeater whose height nobody has decided realizes one
        /// item, reports one item's height, and the outer stack then squeezes every pack into that
        /// sliver. The cells are a fixed square, so the answer does not need discovering: rows are
        /// ceil(count / columns). Measured and named first on the sticker drawer (78711ff).
        /// </remarks>
        private void SizeInnerRepeater(ItemsRepeater inner)
        {
            if (inner?.ItemsSource is not System.Collections.ICollection items || _columns <= 0)
            {
                return;
            }

            var rows = (items.Count + _columns - 1) / _columns;
            var height = Math.Max(_cell, rows * _cell);

            if (inner.Height == height)
            {
                return;
            }

            // Posted, not written here. Both callers run inside a layout pass -- ElementPrepared
            // is raised WHILE the outer repeater is measuring -- and a Height written from there
            // reaches the property but no pass ever consumes it. Measured on the emoji tab, whose
            // write was identical: the tree showed the inner repeater at 0x0 and every group
            // stacked on one y while the log line beside it read the correct height. This popup
            // was never going to behave differently. Posting puts the write in the NEXT pass,
            // where it is an ordinary invalidation -- as StickerDrawer.SizeInnerRepeater does.
            this.BeginOnUIThread(() =>
            {
                if (inner.Height != height)
                {
                    inner.Height = height;
                }
            });
        }

        private void OnRepeaterHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var available = e.NewSize.Width;

            if (available <= 0)
            {
                return;
            }

            var columns = Math.Max(1, (int)(available / _cell));

            if (columns == _columns)
            {
                return;
            }

            _columns = columns;

            foreach (var inner in _innerRepeaters)
            {
                SizeInnerRepeater(inner);
            }
        }

        private void OnStickerElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is not Grid content || content.DataContext is not ViewModels.Drawers.StickerViewModel sticker)
            {
                return;
            }

            content.Height = _cell;

            if (content.Children.Count > 0 && content.Children[0] is AnimatedImage animated)
            {
                using (animated.BeginBatchUpdate())
                {
                    var side = _cell - 4;

                    animated.Width = side;
                    animated.Height = side;
                    animated.FrameSize = new Size(side, side);
                    animated.Source = sticker.StickerValue != null
                        ? new DelayedFileSource(ViewModel.ClientService, sticker)
                        : null;
                }
            }

            // The GridView hung this off OnChoosingItemContainer, which never fires on Uno, and a
            // repeater has no container to hang it off anyway: the element IS the template root,
            // so it carries the handler and reads the sticker off its own DataContext.
            content.ContextRequested -= OnContextRequested;
            content.ContextRequested += OnContextRequested;
        }

        private void OnStickerElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (args.Element is Grid content && content.Children.Count > 0 && content.Children[0] is AnimatedImage animated)
            {
                animated.Source = null;
            }
        }

        private void OnStickerTapped(object sender, TappedRoutedEventArgs e)
        {
            // Deliberately inert, exactly as List_ItemClick is under #if !LINUX: tapping a sticker
            // here sends it into the open chat through the composer, which is not in this subset.
            // The handler exists so the cell is hit-testable and the grid reads as the preview it
            // is; see the note in the constructor.
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
    }
}
