//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Common;
using Telegram.Composition;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.UI;
using Microsoft.UI.Composition;
#if !LINUX
using Microsoft.UI.Composition.Interactions;
#endif
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls
{
    public partial class ChatListSwipedEventArgs : EventArgs
    {
        public CarouselDirection Direction { get; }

        public ChatListSwipedEventArgs(CarouselDirection direction)
        {
            Direction = direction;
        }
    }

    public partial class ChatListListView : TopNavView
    {
        private ChatListViewModel _viewModel;
        public ChatListViewModel ViewModel => _viewModel ??= DataContext as ChatListViewModel;

        public MasterDetailState _viewState;

        private readonly Dictionary<long, SelectorItem> _itemToSelector = new();

        private ScrollViewer ScrollViewer;
        private Border Ghost;
        private ItemsPresenter ItemsPresenter;

        public ChatListListView()
        {
            DefaultStyleKey = typeof(ChatListListView);

            Connected += OnLoaded;
            Disconnected += OnUnloaded;
            ContainerContentChanging += OnContainerContentChanging;

#if !LINUX
            AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
#endif
            RegisterPropertyChangedCallback(SelectionModeProperty, OnSelectionModeChanged);
            RegisterPropertyChangedCallback(ItemsSourceProperty, OnItemsSourceChanged);
        }

        private ChatListViewModel.ItemsCollection _itemsSource;

        private void OnItemsSourceChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (IsConnected)
            {
                _itemsSource?.Moved -= OnCollectionChanged;
                _itemsSource = ItemsSource as ChatListViewModel.ItemsCollection;
                _itemsSource?.Moved += OnCollectionChanged;
            }
        }

        private async void OnCollectionChanged(object sender, ChatListMovedEventArgs args)
        {
            var panel = ItemsPanelRoot as ItemsStackPanel;
            if (panel == null || !PowerSavingPolicy.AreSmoothTransitionsEnabled || !IsChangeVisible(args.OldIndex, args.NewIndex, panel))
            {
                return;
            }

            // Snapshot before await because args are recycled
            var oldIndex = args.OldIndex;
            var newIndex = args.NewIndex;

            int start, end, direction;
            float offset;
            SelectorItem targetContainer;

            if (oldIndex == -1)
            {
                await panel.UpdateLayoutAsync();

                targetContainer = ContainerFromIndex(newIndex) as SelectorItem;
                if (targetContainer == null) return;

                offset = targetContainer.ActualSize.Y;
                start = newIndex + 1;
#if LINUX
                end = panel.LastVisibleIndex + 1;
#else
                end = panel.LastCacheIndex + 1;
#endif
                direction = -1;
            }
            else if (newIndex == -1)
            {
                targetContainer = ContainerFromIndex(oldIndex) as SelectorItem;
                if (targetContainer == null) return;

                offset = targetContainer.ActualSize.Y;
                start = oldIndex + 1;
#if LINUX
                end = panel.LastVisibleIndex + 1;
#else
                end = panel.LastCacheIndex + 1;
#endif
                direction = 1;
            }
            else
            {
                await panel.UpdateLayoutAsync();

                targetContainer = ContainerFromIndex(newIndex) as SelectorItem;
                if (targetContainer == null) return;

                offset = targetContainer.ActualSize.Y;
                direction = newIndex > oldIndex ? 1 : -1;
#if LINUX
                start = Math.Max(Math.Min(newIndex + 1, oldIndex), panel.FirstVisibleIndex);
                end = Math.Min(Math.Max(newIndex, oldIndex + 1), panel.LastVisibleIndex);
#else
                start = Math.Max(Math.Min(newIndex + 1, oldIndex), panel.FirstCacheIndex);
                end = Math.Min(Math.Max(newIndex, oldIndex + 1), panel.LastCacheIndex);
#endif
            }

            var batch = BootStrapper.Current.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            if (newIndex != -1)
            {
                var visual = ElementComposition.GetElementVisual(targetContainer);
                ElementCompositionPreview.SetIsTranslationEnabled(targetContainer, true);

                // One instance per target: Uno's Compositor.RegisterAnimation is
                // `_animations.Add(animation, visual)`, keyed BY THE ANIMATION, so starting the
                // same instance on the clip and on the visual is
                // `ArgumentException: An item with the same key has already been added`
                // (PORTING.md 6). This runs on every chat that moves in the list, i.e. on every
                // incoming message with the list on screen.
                ScalarKeyFrameAnimation Slide()
                {
                    var instance = visual.Compositor.CreateScalarKeyFrameAnimation();
                    instance.InsertKeyFrame(0, targetContainer.ActualSize.Y);
                    instance.InsertKeyFrame(1, 0);
                    return instance;
                }

#if LINUX
                // The row that MOVES does not get its grow-in clip animated, and this is not a
                // cosmetic decision: on Uno that animation never runs and leaves the row invisible.
                // Compositor.RegisterAnimation (Uno.UI.Composition 6.6.184, read from the IL) is
                //
                //     internal void RegisterAnimation(CompositionAnimation animation, CompositionObject visual)
                //     {
                //         if (!animation.IsTrackedByCompositor || !(visual is Visual visual2)) return;
                //         ...
                //     }
                //
                // - only a Visual can carry a running animation. `visual.Clip` is a CompositionClip,
                // so the animation is never registered, AnimationFrame never fires, and
                // CompositionObject.StartAnimation's single `SetAnimatableProperty(...)` with
                // animation.Start()'s return value -- the keyframe at progress 0 -- stands for ever.
                // Here that keyframe is the row's full height.
                //
                // Measured on a chat that moved up the list (saving a draft is enough to move one):
                // `ChatListListViewItem [4,202 481x64] c-clip=InsetClip(0,0,0,64)` -- a 64px row
                // clipped to nothing. Laid out, invisible, and it swallowed its own click: the row
                // was there, the click landed on its centre and no openChat ever left. And because
                // containers are recycled, the stuck clip poisons whatever chat lands in that
                // container next.
                //
                // So: no animation, and the inset is written back to 0 by hand for the recycled case.
                if (visual.Clip is InsetClip settled)
                {
                    settled.BottomInset = 0;
                }
#else
                visual.Clip ??= visual.Compositor.CreateInsetClip();
                visual.Clip.StartAnimation("BottomInset", Slide());
#endif

                if (direction > 0)
                {
                    visual.StartAnimation("Translation.Y", Slide());
                }
            }

            for (int i = start; i < end; i++)
            {
                if (ContainerFromIndex(i) is SelectorItem container)
                {
                    var visual = ElementComposition.GetElementVisual(container);
                    ElementCompositionPreview.SetIsTranslationEnabled(container, true);

                    var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
                    animation.InsertKeyFrame(0, offset * direction);
                    animation.InsertKeyFrame(1, 0);

                    visual.StartAnimation("Translation.Y", animation);
                }
            }

            batch.End();

            static bool IsChangeVisible(int oldIndex, int newIndex, ItemsStackPanel panel)
            {
                if (oldIndex == -1)
                {
                    return newIndex >= panel.FirstVisibleIndex && newIndex <= panel.LastVisibleIndex;
                }
                else if (newIndex == -1)
                {
                    return oldIndex >= panel.FirstVisibleIndex && oldIndex <= panel.LastVisibleIndex;
                }
                else
                {
                    return (oldIndex >= panel.FirstVisibleIndex) || (newIndex >= panel.FirstVisibleIndex && newIndex <= panel.LastVisibleIndex);
                }
            }
        }

        protected override void OnApplyTemplate()
        {
            ScrollViewer = GetTemplateChild(nameof(ScrollViewer)) as ScrollViewer;
            Ghost = GetTemplateChild(nameof(Ghost)) as Border;
            ItemsPresenter = GetTemplateChild(nameof(ItemsPresenter)) as ItemsPresenter;

            base.OnApplyTemplate();

#if !LINUX
            // Loaded does not imply the template has been applied: a control that is in the tree
            // but never measured raises it with the template parts still null. Whichever of the
            // two arrives second does the setup.
            TryInitialize();
#endif
        }

        private static ChatCell GetCell(SelectorItem container)
        {
#if LINUX
            // ContentControl.ContentTemplateRoot is never assigned in Uno for a templated control:
            // see Telegram.Linux/Xaml/ContentTemplateRootEx.cs.
            return container.ContentTemplateRootAs<ChatCell>();
#else
            return container.ContentTemplateRoot as ChatCell;
#endif
        }

        public bool TryGetChatAndCell(long chatId, out Chat chat, out ChatCell cell)
        {
            if (_itemToSelector.TryGetValue(chatId, out SelectorItem container))
            {
                chat = ViewModel.ClientService.GetChat(chatId);
                cell = GetCell(container);
                return chat != null && cell != null;
            }

            chat = null;
            cell = null;
            return false;
        }

        public bool TryGetContainer(long chatId, out SelectorItem container)
        {
            return _itemToSelector.TryGetValue(chatId, out container);
        }

        public bool TryGetCell(Chat chat, out ChatCell cell)
        {
            if (_itemToSelector.TryGetValue(chat.Id, out SelectorItem container))
            {
                cell = GetCell(container);
                return cell != null;
            }

            cell = null;
            return false;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not Chat chat)
            {
                return;
            }

            if (args.InRecycleQueue)
            {
                _itemToSelector.Remove(chat.Id);
            }
#if LINUX
            // Uno raises this once, with no phases, and from PrepareContainerForIndex - that is,
            // *before* the container enters the visual tree, which is also when Uno expands the
            // item template (ContentPresenter.EnterImpl). So a brand new container has no cell yet
            // and has to be bound again once it is loaded; a recycled one already has its cell and
            // is bound right here. Either way the cell is looked up with ContentTemplateRootEx,
            // because SelectorItem.ContentTemplateRoot is always null in Uno.
            else if (args.ItemContainer is SelectorItem container)
            {
                _itemToSelector[chat.Id] = container;

                container.Loaded -= OnContainerLoaded;
                container.Loaded += OnContainerLoaded;

                _ = UpdateContainer(container, chat);
            }
#else
            else if (args.Phase == 0)
            {
                _itemToSelector[chat.Id] = args.ItemContainer;

                args.RegisterUpdateCallback(2, OnContainerContentChanging);
                args.ItemContainer.ContentTemplateRoot.Opacity = 0;

                VisualStateManager.GoToState(args.ItemContainer, "DataPlaceholder", false);
            }
            else if (args.ItemContainer.ContentTemplateRoot is ChatCell content)
            {
                content.UpdateViewState(chat, _viewState == MasterDetailState.Compact, false);
                content.UpdateChat(ViewModel.ClientService, chat, ViewModel.Items.ChatList);
                content.Opacity = 1;

                VisualStateManager.GoToState(args.ItemContainer, "DataAvailable", false);
            }
#endif

            args.Handled = true;
        }

#if LINUX
        private void OnContainerLoaded(object sender, RoutedEventArgs e)
        {
            // Containers are recycled, so the chat is re-read from the container rather than
            // captured when the handler was attached.
            if (sender is SelectorItem container && container.Content is Chat chat && !UpdateContainer(container, chat))
            {
                // Loaded and still no cell: the item template did not expand, nothing below can
                // bind, and the row would silently keep the placeholder values ChatCell.xaml
                // declares.
                Logger.Warning(string.Format("No ChatCell under the container of {0}", chat.Id));
            }
        }

        private bool UpdateContainer(SelectorItem container, Chat chat)
        {
            if (GetCell(container) is not ChatCell content || ViewModel == null)
            {
                return false;
            }

            content.UpdateViewState(chat, _viewState == MasterDetailState.Compact, false);
            content.UpdateChat(ViewModel.ClientService, chat, ViewModel.Items.ChatList);
            content.Opacity = 1;

            VisualStateManager.GoToState(container, "DataAvailable", false);
            return true;
        }
#endif

        private void OnSelectionModeChanged(DependencyObject sender, DependencyProperty dp)
        {
            UpdateVisibleChats();
        }

        public void UpdateViewState(MasterDetailState state)
        {
            _viewState = state;
            UpdateVisibleChats();
        }

        public void UpdateVisibleChats()
        {
            // TODO: supposedly, _itemToSelector should only contain cached items
            foreach (var item in _itemToSelector)
            {
                if (GetCell(item.Value) is ChatCell chatView && ViewModel.ClientService.TryGetChat(item.Key, out Chat chat))
                {
                    chatView.UpdateViewState(chat, _viewState == MasterDetailState.Compact, true);
                }
            }
        }

        protected override DependencyObject GetContainerForItemOverride()
        {
            var container = new ChatListListViewItem(this);

            // Same line as TopNavView.GetContainerForItemOverride, which this override replaces
            // without calling: without it the chat rows are the ONLY TopNavView rows that never
            // get their context menu wired. On Windows that went unnoticed because
            // MainPage.ChatsList_ChoosingItemContainer did the subscribing, and Uno never raises
            // ChoosingItemContainer (PORTING.md 6) - which is why right-clicking a chat reached
            // the row and did nothing.
            container.ContextRequested += RaiseItemContextRequested;

            return container;
        }

        private bool _changingView;
        private bool _fromAnimation;

        public void ChangeView(CarouselDirection direction, Action continuation)
        {
            void Continue(bool restore)
            {
#if !LINUX
                if (restore)
                {
                    _tracker?.TryUpdatePosition(Vector3.Zero);
                }
#endif

                if (continuation != null)
                {
                    continuation();
                }
                else
                {
                    Swiped?.Invoke(this, new ChatListSwipedEventArgs(direction));
                }
            }

#if LINUX
            // No InteractionTracker on Uno Skia: switch the folder without the slide.
            Continue(true);
            return;
#else
            if (_changingView || _tracker == null || direction == CarouselDirection.None || !IsConnected || !PowerSavingPolicy.AreSmoothTransitionsEnabled)
            {
                Continue(true);
                return;
            }

            _changingView = true;

            var child = VisualTreeHelper.GetChild(ScrollViewer, 0) as UIElement;
            var childSize = child.ActualSize.X > 0 && child.ActualSize.Y > 0 ? child.ActualSize : new Vector2(1, 1);

            var visual = BootStrapper.Current.Compositor.CreateRedirectBrush(child, Vector2.Zero, childSize, true);

            VisualUtilities.QueueCallbackForCompositionRendered(this, () =>
            {
                Continue(false);
                ConfigureAnimations(false);

                var position = ActualSize.X * (ActualSize.X / (ActualSize.X - 72));

                var w = continuation != null ? position : ActualSize.X;
                var x = direction == CarouselDirection.Previous ? w : -w;

                var translate = _tracker.Compositor.CreateVector3KeyFrameAnimation();
                translate.InsertKeyFrame(0, new Vector3(x, 0, 0));
                translate.InsertKeyFrame(1, new Vector3(0));

                _tracker.Properties.InsertBoolean("FromAnimation", _fromAnimation = true);
                _tracker.TryUpdatePositionWithAnimation(translate);

                _changingView = false;
                _redirect.Brush = visual;
            });
#endif
        }

        #region Swipe

        public bool CanGoNext { get; set; }
        public bool CanGoPrev { get; set; }

        public event EventHandler<ChatListSwipedEventArgs> Swiped;

#if LINUX
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_itemsSource == null)
            {
                _itemsSource = ItemsSource as ChatListViewModel.ItemsCollection;
                _itemsSource?.Moved += OnCollectionChanged;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _itemToSelector.Clear();

            if (_itemsSource != null)
            {
                _itemsSource.Moved -= OnCollectionChanged;
                _itemsSource = null;
            }
        }
#else
        private SpriteVisual _hitTest;
        private ContainerVisual _container;
        private Visual _visual;
        private SpriteVisual _redirect;
        private ContainerVisual _indicator;

        private WeakInteractionTrackerOwner _trackerOwner;
        private InteractionTracker _tracker;
        private VisualInteractionSource _interactionSource;

        private void TryInitialize()
        {
            if (_trackerOwner != null || ItemsPresenter == null || !IsConnected)
            {
                return;
            }

            _visual = ElementComposition.GetElementVisual(ItemsPresenter);

            _redirect = _visual.Compositor.CreateSpriteVisual();
            _redirect.RelativeSizeAdjustment = Vector2.One;

            _hitTest = _visual.Compositor.CreateSpriteVisual();
            _hitTest.Brush = _visual.Compositor.CreateColorBrush(Colors.Transparent);
            _hitTest.RelativeSizeAdjustment = Vector2.One;

            _container = _visual.Compositor.CreateContainerVisual();
            _container.Children.InsertAtBottom(_hitTest);
            _container.RelativeSizeAdjustment = Vector2.One;

            ElementCompositionPreview.SetElementChildVisual(Ghost, _redirect);
            ElementCompositionPreview.SetElementChildVisual(this, _container);
            ConfigureInteractionTracker();

            // OnLoaded may already have run and found nothing to subscribe to.
            AttachTracker();
        }

        private void AttachTracker()
        {
            if (_trackerOwner != null)
            {
                _trackerOwner.ValuesChanged += OnValuesChanged;
                _trackerOwner.InertiaStateEntered += OnInertiaStateEntered;
                _trackerOwner.InteractingStateEntered += OnInteractingStateEntered;
                _trackerOwner.IdleStateEntered += OnIdleStateEntered;
                _trackerOwner.CustomAnimationStateEntered += OnCustomAnimationStateEntered;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_trackerOwner == null)
            {
                // Subscribes as well, if the template got here first.
                TryInitialize();
            }
            else
            {
                AttachTracker();
            }

            if (_itemsSource == null)
            {
                _itemsSource = ItemsSource as ChatListViewModel.ItemsCollection;
                _itemsSource?.Moved += OnCollectionChanged;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _itemToSelector.Clear();

            if (_trackerOwner != null)
            {
                _trackerOwner.ValuesChanged -= OnValuesChanged;
                _trackerOwner.InertiaStateEntered -= OnInertiaStateEntered;
                _trackerOwner.InteractingStateEntered -= OnInteractingStateEntered;
                _trackerOwner.IdleStateEntered -= OnIdleStateEntered;
                _trackerOwner.CustomAnimationStateEntered -= OnCustomAnimationStateEntered;
            }

            if (_itemsSource != null)
            {
                _itemsSource.Moved -= OnCollectionChanged;
                _itemsSource = null;
            }
        }

        private void ConfigureInteractionTracker()
        {
            _interactionSource = VisualInteractionSource.Create(_hitTest);

            //Configure for x-direction panning
            _interactionSource.ManipulationRedirectionMode = VisualInteractionSourceRedirectionMode.CapableTouchpadOnly;
            _interactionSource.PositionXSourceMode = InteractionSourceMode.EnabledWithInertia;
            _interactionSource.PositionXChainingMode = InteractionChainingMode.Never;
            _interactionSource.IsPositionXRailsEnabled = true;

            _trackerOwner = new WeakInteractionTrackerOwner();

            //Create tracker and associate interaction source
            _tracker = InteractionTracker.CreateWithOwner(_visual.Compositor, _trackerOwner);
            _tracker.InteractionSources.Add(_interactionSource);

            _tracker.MaxPosition = new Vector3(72);
            _tracker.MinPosition = new Vector3(-72);

            _tracker.Properties.InsertBoolean("FromAnimation", false);
            _tracker.Properties.InsertBoolean("CanGoNext", CanGoNext);
            _tracker.Properties.InsertBoolean("CanGoPrev", CanGoPrev);

            //ConfigureAnimations(_visual, null);
            ConfigureRestingPoints();
        }

        private void ConfigureRestingPoints()
        {
            var neutralX = InteractionTrackerInertiaRestingValue.Create(_visual.Compositor);
            neutralX.Condition = _visual.Compositor.CreateExpressionAnimation("true");
            neutralX.RestingValue = _visual.Compositor.CreateExpressionAnimation("0");

            _tracker.ConfigurePositionXInertiaModifiers(new InteractionTrackerInertiaModifier[] { neutralX });
        }

        private void ConfigureAnimations(bool interacting)
        {
            if (interacting)
            {
                _tracker.Properties.InsertBoolean("FromAnimation", _fromAnimation = false);
            }

            _redirect.Brush = _tracker.Compositor.CreateColorBrush(Colors.Transparent);

            _tracker.Properties.InsertBoolean("CanGoNext", CanGoNext);
            _tracker.Properties.InsertBoolean("CanGoPrev", CanGoPrev);
            _tracker.MaxPosition = new Vector3(CanGoNext ? 72 : 0);
            _tracker.MinPosition = new Vector3(CanGoPrev ? -72 : 0);

            // This should be enough: tracker.FromAnimation ? -tracker.Position.X : Clamp(-tracker.Position.X, -72, 72)
            var offsetExp = _visual.Compositor.CreateExpressionAnimation("tracker.FromAnimation || Abs(tracker.Position.X) >= this.Target.Size.X ? -tracker.Position.X : Clamp(-tracker.Position.X, -72, 72)");
            offsetExp.SetReferenceParameter("tracker", _tracker);

            var offsetExp2 = _visual.Compositor.CreateExpressionAnimation("tracker.Position.X < 0 ? Min(-tracker.Position.X / (source.Size.X) * (source.Size.X - 72), source.Size.X) - source.Size.X : tracker.Position.X >= 0 ? Min(-tracker.Position.X / (source.Size.X) * (source.Size.X - 72), source.Size.X) + source.Size.X : 0");
            offsetExp2.SetReferenceParameter("tracker", _tracker);
            offsetExp2.SetReferenceParameter("source", _visual);

            _visual.StartAnimation("Offset.X", offsetExp);
            _redirect.StartAnimation("Offset.X", offsetExp2);
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                try
                {
                    _interactionSource.TryRedirectForManipulation(e.GetCurrentPoint(this));
                }
                catch { }
            }
        }

        private void OnValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
        {
            if (_indicator == null && (_tracker.Position.X > 0.0001f || _tracker.Position.X < -0.0001f) /*&& Math.Abs(e.Cumulative.Translation.X) >= 45*/)
            {
                var sprite = _visual.Compositor.CreateSpriteVisual();
                sprite.Size = new Vector2(30, 30);
                sprite.CenterPoint = new Vector3(15);

                var surface = LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/Images/ArrowLeft.png"));
                void handler(LoadedImageSurface s, LoadedImageSourceLoadCompletedEventArgs args)
                {
                    s.LoadCompleted -= handler;
                    sprite.Brush = _visual.Compositor.CreateSurfaceBrush(s);
                }

                surface.LoadCompleted += handler;

                var circle = _visual.Compositor.CreateSpriteVisual();
                circle.Size = new Vector2(30, 30);
                circle.Brush = SolidGaussianBrush.CreateCircleBrush(_visual.Compositor, 15,
                    (Windows.UI.Color)Navigation.BootStrapper.Current.Resources["MessageServiceBackgroundColor"]);

                _indicator = _visual.Compositor.CreateContainerVisual();
                _indicator.Children.InsertAtBottom(circle);
                _indicator.Children.InsertAtTop(sprite);
                _indicator.Size = new Vector2(30, 30);
                _indicator.CenterPoint = new Vector3(15);
                _indicator.Scale = new Vector3();

                _container.Children.InsertAtTop(_indicator);
            }

            var offset = (_tracker.Position.X > 0 && !CanGoNext) || (_tracker.Position.X <= 0 && !CanGoPrev) ? 0 : Math.Clamp(Math.Abs(_tracker.Position.X), 0, 72);

            var abs = Math.Abs(offset);
            var percent = _fromAnimation ? 0 : abs / 72f;

            var width = ActualSize.X;
            var height = ActualSize.Y;

            if (_indicator != null)
            {
                _indicator.Offset = new Vector3(_tracker.Position.X > 0 ? width - percent * 60 : -30 + percent * 55, (height - 30) / 2, 0);
                _indicator.Scale = new Vector3(_tracker.Position.X > 0 ? 0.8f + percent * 0.2f : -(0.8f + percent * 0.2f), 0.8f + percent * 0.2f, 1);
                _indicator.Opacity = percent;
            }
        }

        private void OnInertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args)
        {
            var position = _tracker.Position;
            if (position.X >= 72 && CanGoNext || position.X <= -72 && CanGoPrev)
            {
                sender.TryUpdatePosition(sender.Position);

                var direction = position.X <= -72 && CanGoPrev
                    ? CarouselDirection.Previous
                    : CarouselDirection.Next;

                ChangeView(direction, null);
            }
        }

        private void OnIdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
        {
            ConfigureAnimations(false);
        }

        private void OnInteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args)
        {
            ConfigureAnimations(true);
        }

        private void OnCustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args)
        {
            _tracker.Properties.InsertBoolean("FromAnimation", _fromAnimation = true);
        }
#endif

        #endregion
    }

    public partial class ChatListListViewItem : TopNavViewItem
    {
        private readonly ChatListListView _owner;

        private readonly bool _multi;
        private bool _selected;

        public ChatListListViewItem(ChatListListView list)
        {
            DefaultStyleKey = typeof(ChatListListViewItem);

            _multi = true;
            _owner = list;

            _recognizer = new GestureRecognizer();
            _recognizer.GestureSettings = GestureSettings.HoldWithMouse;

            AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
            AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), true);
            AddHandler(PointerCanceledEvent, new PointerEventHandler(OnPointerCanceled), true);
            AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);

            Connected += OnLoaded;
            Disconnected += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _recognizer.Holding += OnHolding;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _recognizer.Holding -= OnHolding;
        }

        public bool IsSingle => !_multi;

        public void UpdateState(bool selected)
        {
            if (_selected == selected)
            {
                return;
            }

            if (this.ContentRoot() is IMultipleElement test)
            {
                _selected = selected;
                test.UpdateState(selected, true, _owner.SelectionMode == ListViewSelectionMode.Multiple);
            }
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            // Reactivate focus visuals that may have been deactivated by ChatListListView.OnGettingFocus.
            UseSystemFocusVisuals = true;

            base.OnLostFocus(e);
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new ChatListListViewItemAutomationPeer(this);
        }

        private void OnSelectedChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (this.ContentRoot() is ChatCell content)
            {
                content?.UpdateViewState(_owner.ItemFromContainer(this) as Chat, _owner._viewState == MasterDetailState.Compact, false);
            }
        }

        private readonly GestureRecognizer _recognizer;

        private void OnHolding(GestureRecognizer sender, HoldingEventArgs args)
        {
            Logger.Info(args.HoldingState);

            if (args.HoldingState == HoldingState.Started && args.Position.X is >= 8 and <= 56 && args.Position.Y is >= 8 and <= 56 && this.ContentRoot() is ChatCell chatCell)
            {
                ReleasePointerCaptures();
                chatCell.ShowPreview(args.Position);
            }
            else if (args.HoldingState == HoldingState.Started && args.Position.X is >= 8 and <= 56 && args.Position.Y is >= 8 and <= 56 && this.ContentRoot() is ForumTopicCell forumTopicCell)
            {
                ReleasePointerCaptures();
                forumTopicCell.ShowPreview(args.Position);
            }
            else if (args.HoldingState == HoldingState.Started && args.Position.X is >= 8 and <= 56 && args.Position.Y is >= 8 and <= 56 && this.ContentRoot() is ForumTopicVerticalCell forumTopicVerticalCell)
            {
                ReleasePointerCaptures();
                forumTopicVerticalCell.ShowPreview(args.Position);
            }
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _recognizer.TryProcessDownEvent(e.GetCurrentPoint(this));
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _recognizer.TryProcessMoveEvents(e.GetIntermediatePoints(this));
        }

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _recognizer.TryCompleteGesture();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _recognizer.TryProcessUpEvent(e.GetCurrentPoint(this));
        }
    }

    public partial class ChatListVisualStateManager : VisualStateManager
    {
        private bool _multi;

        protected override bool GoToStateCore(Control control, FrameworkElement templateRoot, string stateName, VisualStateGroup group, VisualState state, bool useTransitions)
        {
            var selector = control as ChatListListViewItem;
            if (selector == null)
            {
                return false;
            }

            if (group.Name == "MultiSelectStates")
            {
                _multi = stateName == "MultiSelectEnabled";
                selector.UpdateState((_multi || selector.IsSingle) && selector.IsSelected);
            }
            else if ((_multi || selector.IsSingle) && stateName.EndsWith("Selected"))
            {
                stateName = stateName.Replace("Selected", string.Empty);

                if (string.IsNullOrEmpty(stateName))
                {
                    stateName = "Normal";
                }
            }

            return base.GoToStateCore(control, templateRoot, stateName, group, state, useTransitions);
        }
    }

    public partial class ChatListListViewItemAutomationPeer : ListViewItemAutomationPeer
    {
        private readonly ChatListListViewItem _owner;

        public ChatListListViewItemAutomationPeer(ChatListListViewItem owner)
            : base(owner)
        {
            _owner = owner;
        }

        protected override string GetNameCore()
        {
            var name = _owner.ContentRoot() switch
            {
                ChatCell chat => chat.GetAutomationName(),
                ForumTopicCell topic => topic.GetAutomationName(),
                ForumTopicVerticalCell vertical => vertical.GetAutomationName(),
                _ => null
            };

            return name ?? base.GetNameCore();
        }
    }
}
