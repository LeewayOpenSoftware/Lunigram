//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Services.Settings;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels.Drawers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Controls.Drawers
{
    public partial class ItemContextRequestedEventArgs<T> : EventArgs
    {
        private readonly ContextRequestedEventArgs _args;

        public ItemContextRequestedEventArgs(T item, ContextRequestedEventArgs args)
        {
            _args = args;
            Item = item;
        }

        public bool TryGetPosition(UIElement relativeTo, out Point point)
        {
            return _args.TryGetPosition(relativeTo, out point);
        }

        public T Item { get; }

        public bool Handled
        {
            get => _args.Handled;
            set => _args.Handled = value;
        }
    }

    public sealed partial class AnimationDrawer : UserControl, IDrawer
    {
        public AnimationDrawerViewModel ViewModel => DataContext as AnimationDrawerViewModel;

        public event EventHandler<ItemClickEventArgs> ItemClick;
        public event EventHandler<ItemContextRequestedEventArgs<Animation>> ItemContextRequested;

        private readonly AnimatedListHandler _handler;
        private readonly ZoomableListHandler _zoomer;

        private readonly EventDebouncer<TextChangedEventArgs> _typing;

        private bool _isActive;

        public AnimationDrawer()
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
            var trigger = new FluidGridViewTrigger { RowsOrColumns = 3 };
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

            _handler = new AnimatedListHandler(List, AnimatedListType.Animations);

            _zoomer = new ZoomableListHandler(List);
            _zoomer.Opening += Zoomer_Opening;
            _zoomer.Closing += Zoomer_Closing;

            _typing = new EventDebouncer<TextChangedEventArgs>(Constants.TypingTimeout, handler => SearchField.TextChanged += new TextChangedEventHandler(handler));
            _typing.Invoked += (s, args) =>
            {
                ViewModel?.Search(SearchField.Text);
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

        public StickersTab Tab => StickersTab.Animations;

        public Thickness ScrollingHostPadding
        {
            get => List.Padding;
            set => List.Padding = new Thickness(2, value.Top, 0, value.Bottom);
        }

        public ListViewBase ScrollingHost => List;

        public void Activate(Chat chat, EmojiSearchType type = EmojiSearchType.Default)
        {
            _isActive = true;
            _handler.ThrottleVisibleItems();

            SearchField.SetType(ViewModel.ClientService, type);

            ViewModel.Update();
        }

        public void Deactivate()
        {
            _isActive = false;
            _handler.UnloadItems();

            _typing.Cancel();

            // This is called only right before XamlMarkupHelper.UnloadObject
            // so we can safely clean up any kind of anything from here.
            Bindings.StopTracking();
        }

        public void LoadVisibleItems()
        {
            if (_isActive)
            {
                _handler.LoadVisibleItems();
            }
        }

        public void UnloadVisibleItems()
        {
            _handler.UnloadVisibleItems();
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Animation animation)
            {
                ItemClick?.Invoke(sender, e);
            }
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new GridViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += OnContextRequested;

                _zoomer.ElementPrepared(args.ItemContainer);
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            var animation = args.Item as Animation;

            if (args.InRecycleQueue)
            {
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

            var file = animation.AnimationValue;
            if (file == null)
            {
                return;
            }

#if LINUX
            // ContentTemplateRoot is always null once the container has a template (PORTING.md 6);
            // WithContentRoot goes through the presenter and, if the container has not entered the
            // visual tree yet, binds on Loaded / SizeChanged instead of losing the cell silently.
            var service = ViewModel.ClientService;
            DrawerContainers.WithContentRoot<Border>(sender, args.ItemContainer, args.Item, content =>
            {
                if (content.Child is AnimatedImage animated)
                {
                    animated.Source = new DelayedFileSource(service, file);
                }
            });
#else
            var content = args.ItemContainer.ContentRoot() as Border;
            var animated = content.Child as AnimatedImage;
            animated.Source = new DelayedFileSource(ViewModel.ClientService, file);
#endif

            args.Handled = true;
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var animation = List.ItemFromContainer(sender) as Animation;
            if (animation == null)
            {
                return;
            }

            ItemContextRequested?.Invoke(sender, new ItemContextRequestedEventArgs<Animation>(animation, args));
        }

        private void SearchField_TextChanged(object sender, TextChangedEventArgs e)
        {
            //ViewModel.Search(SearchField.Text, false);
        }

        private void SearchField_CategorySelected(object sender, EmojiCategorySelectedEventArgs e)
        {
            if (e.Category.Source is EmojiCategorySourceSearch search)
            {
                ViewModel.Search(string.Join(" ", search.Emojis));
            }
        }

        private object ConvertItems(object items)
        {
            _handler.ThrottleVisibleItems();
            return items;
        }

        private void Player_Ready(object sender, EventArgs e)
        {
            _handler.ThrottleVisibleItems();
        }

        private void Toolbar_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.Item is AnimationsCollection collection)
            {
                Automation.SetToolTip(args.ItemContainer, collection.Title);

                var content = args.ItemContainer.ContentRoot() as Grid;
                if (content?.Children[0] is FontIcon icon)
                {
                    icon.Glyph = collection.Name switch
                    {
                        "tg/recentlyUsed" => Icons.EmojiRecents,
                        "tg/trending" => Icons.Trending,
                        _ => string.Empty
                    };
                }
                else if (content?.Children[0] is TextBlock textBlock)
                {
                    textBlock.Text = collection.Name;
                }

                args.Handled = true;
            }
        }
    }
}
