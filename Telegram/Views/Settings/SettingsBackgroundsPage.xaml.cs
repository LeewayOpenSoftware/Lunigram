//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
#if LINUX
// DrawerContainers: the ChoosingItemContainer substitute. It lives in the drawers'
// namespace because u-003 needed it first; it is not drawer-specific.
using Telegram.Controls.Drawers;
#endif
using Telegram.Controls.Chats;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsBackgroundsPage : HostedPage
    {
        public SettingsBackgroundsViewModel ViewModel => DataContext as SettingsBackgroundsViewModel;

        public SettingsBackgroundsPage()
        {
            InitializeComponent();

#if LINUX
            // Uno's ItemsPresenter stacks GridView.Header/Footer BESIDE a horizontal wrap grid
            // (PhysicalOrientation reads the item-flow axis, not the scroll axis -- see
            // Telegram.Linux/Xaml/DrawerHeaderHost.cs), and both of this page's Header and Footer
            // are real content (two SettingsButtons; a "reset backgrounds" button), not width-less
            // spacers, so leaving the panel swap to work alone would leave them eating the row and
            // the grid at ~0. DrawerHeaderHost handles the Header move; the Footer here has a real
            // button in it (unlike a drawer's bare spacer footer, which DrawerHeaderHost deletes
            // and folds into padding), so it gets the same treatment as the Header instead: moved
            // into its own row, not discarded.
            DrawerHeaderHost.MoveOutOfList(ScrollingHost, HeaderHost);

            if (ScrollingHost.Footer is UIElement footer)
            {
                ScrollingHost.Footer = null;
                FooterHost.Child = footer;
            }
#endif

            // These three FluidGridView triggers were a <common:FluidGridView.Triggers> property
            // element in the XAML. They are set here because Uno's XAML generator SILENTLY ABANDONS
            // the rest of the enclosing element's children as soon as it meets an attached property
            // written as a property element holding a collection -- measured on Uno 6.6.184 during
            // u-003, see PORTING.md 6. The subtree it drops is not merely unnamed, it is never
            // constructed. Set from code it behaves identically on both heads.
            var triggers = FluidGridView.GetTriggers(ScrollingHost);
            triggers.Add(new FluidGridViewTrigger { MinLength = 0, RowsOrColumns = 3 });
            triggers.Add(new FluidGridViewTrigger { MinLength = 600, RowsOrColumns = 4 });
            triggers.Add(new FluidGridViewTrigger { MinLength = 800, RowsOrColumns = 5 });

#if LINUX
            // A button that draws and does nothing is worse than not having it, so this one is
            // collapsed rather than left to fail: picking a wallpaper off disk needs
            // FileOpenPicker, which FALTA 1.5 records as never having been seen to open on this
            // head, and the upload behind it needs the attach parcel's media-generation stack. The
            // reason is written in full at BackgroundViewModel.GetBackgroundAsync. XAML takes no
            // #if, so it is collapsed here; "set a colour" next to it needs no picker and stays.
            SelectFromGallery.Visibility = Visibility.Collapsed;
#endif
            Title = Strings.ChatBackground;
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new GridViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += OnContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var background = ScrollingHost.ItemFromContainer(sender) as Background;
            if (background == null || background.Id == Constants.WallpaperLocalId)
            {
                return;
            }

            var flyout = new MenuFlyout();
            // CC-2: ViewModel.Share's ChooseChatsPopup is ported now.
            flyout.CreateFlyoutItem(ViewModel.Share, background, Strings.ShareFile, Icons.Share);
            flyout.CreateFlyoutItem(ViewModel.Delete, background, Strings.Delete, Icons.Delete, destructive: true);
            flyout.ShowAt(sender, args);
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {

#if LINUX
            // OnChoosingItemContainer never runs: ListViewBase.ChoosingItemContainer is
            // NotImplemented on Uno and its add accessor only calls TryRaiseNotImplemented, so the
            // handler is not even stored (PORTING.md 6). Style and ContentTemplate survive because
            // this list declares both in its XAML and the default container path applies them; the
            // ContextRequested hook does not, so it is re-attached here, from the event Uno DOES
            // raise. Without it the right-click menu on a background is simply gone.
            DrawerContainers.Prepare(args.ItemContainer, OnContextRequested, null);
#endif
            if (args.InRecycleQueue)
            {
                return;
            }

            if (args.Item is not Background background)
            {
                return;
            }

            if (args.ItemContainer.ContentRoot() is AspectView content)
            {
                BindTile(content, background);
            }
#if LINUX
            else
            {
                // ContentRoot() answers null on a brand new container: ContainerContentChanging
                // fires from PrepareContainerForIndex, before the container enters the visual tree,
                // which is also when Uno expands the item template (PORTING.md 6). This tile's
                // ChatBackgroundPresenter never got UpdateSource on that first pass, so its
                // ChatBackgroundCanvas kept painting the fallback gradient ChatBackgroundRenderer
                // falls back to when nothing is set -- identically on every tile, which is why the
                // whole grid showed one gradient instead of each background's own preview -- and the
                // checkmark stayed at its XAML default (Visible) because the line that collapses it
                // never ran either. WithContentRoot is the drawers' fix for exactly this race (retry
                // on Loaded, then SizeChanged); nothing here is drawer-specific.
                DrawerContainers.WithContentRoot<AspectView>(sender, args.ItemContainer, background, later => BindTile(later, background));
            }
#endif

            args.Handled = true;
        }

        private void Tile_Loaded(object sender, RoutedEventArgs e)
        {
            BindFromDataContext(sender);
        }

        private void Tile_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            BindFromDataContext(sender);
        }

        /// <summary>
        /// Bind a tile from the item it already holds, without waiting to be told about it.
        /// </summary>
        /// <remarks>
        /// ContainerContentChanging is raised from the VIRTUALIZING container-preparation path, and
        /// this grid has no virtualizing panel: ItemsWrapGrid is [NotImplemented] on Uno Skia, so
        /// the ItemsPanel resolves to VariableSizedWrapGrid - see this page's own XAML comment,
        /// which says in as many words that it "does not virtualize". No virtualizing panel means
        /// no PrepareContainerForIndex, which means ContainerContentChanging never fires, which
        /// means BindTile was never called for a single tile. Every ChatBackgroundPresenter kept
        /// the empty options its canvas starts with, and ChatBackgroundRenderer covered for that
        /// with a fallback palette that happens to be the predefined wallpaper's own colours - so
        /// all ~75 tiles looked identical and looked, wrongly, like a rendering bug.
        ///
        /// The delivery6 log is unambiguous that the handler was never entered: not one "tile
        /// painted" line, AND not one "drawer cell has no content root" warning. The retry inside
        /// the handler cannot have failed, because nothing ever reached it. That silence is also
        /// why eb0d14c changed nothing here: it repaired a race inside a handler this page never
        /// runs.
        ///
        /// A tile knows which item it is showing, so it binds itself. This does not depend on which
        /// callbacks the host does or does not raise, and it is idempotent with the handler above,
        /// which still does the work wherever there IS a virtualizing panel.
        /// </remarks>
        private void BindFromDataContext(object sender)
        {
            if (sender is AspectView content && content.DataContext is Background background)
            {
                BindTile(content, background);
            }
        }

        private void BindTile(AspectView content, Background background)
        {
            var preview = content.Children[0] as ChatBackgroundPresenter;
            var check = content.Children[1];

            preview.UpdateSource(ViewModel.ClientService, background, true);
            content.Constraint = background;
            check.Visibility = background == ViewModel.SelectedItem
                ? Visibility.Visible
                : Visibility.Collapsed;

#if LINUX
            // Same open question BackgroundPopup had (eb0d14c's WithContentRoot use is unconfirmed
            // by any positive log line): does the wiring reach UpdateSource at all, and with which
            // background per tile. If this line fires once per distinct background.Id and the grid
            // still shows one gradient on screen, the render path (ChatBackgroundRenderer /
            // ChatBackgroundCanvas), not this wiring, is the bug.
            Logger.Debug(string.Format("SettingsBackgroundsPage: tile painted, background {0} type {1}", background.Id, background.Type?.GetType().Name));
#endif
        }

        private void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Background background)
            {
                ViewModel.Change(background);
            }
        }
    }
}
