//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Delegates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileSavedChatsTabPage : ProfileTabPage, ISavedMessagesChatsDelegate
    {
        public new ProfileViewModel ViewModel => DataContext as ProfileViewModel;

        public ProfileSavedChatsTabPage()
        {
            InitializeComponent();

            Connected += OnConnected;
            Disconnected += OnDisconnected;
        }

        private void OnConnected(object sender, RoutedEventArgs e)
        {
            ViewModel?.SavedChatsTab.Delegate = this;
        }

        private void OnDisconnected(object sender, RoutedEventArgs e)
        {
            ViewModel?.SavedChatsTab.Delegate = null;
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SavedMessagesTopic topic)
            {
                ViewModel.OpenSavedMessagesTopic(topic);
            }
        }

        protected override void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = ScrollingHost.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = ScrollingHost.ItemTemplate;
                args.ItemContainer.ContextRequested += OnContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
#if LINUX
            // The pin / unpin / delete menu of a saved chat lives on the container, and upstream
            // subscribes it from ChoosingItemContainer, which Uno never raises. See
            // ProfileTabContainer.
            ProfileTabContainer.Bind<ChatCell>(sender, args, OnContextRequested,
                (content, item) =>
                {
                    if (item is SavedMessagesTopic savedMessagesTopic)
                    {
                        content.UpdateSavedMessagesTopic(ViewModel.ClientService, savedMessagesTopic);
                    }
                });
#else
            else if (args.ItemContainer.ContentTemplateRoot is ChatCell content && args.Item is SavedMessagesTopic savedMessagesTopic)
            {
                content.UpdateSavedMessagesTopic(ViewModel.ClientService, savedMessagesTopic);
                args.Handled = true;
            }
#endif
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var topic = ScrollingHost.ItemFromContainer(sender) as SavedMessagesTopic;
            var flyout = new MenuFlyout();

            if (topic.IsPinned)
            {
                flyout.CreateFlyoutItem(ViewModel.SavedChatsTab.UnpinTopic, topic, Strings.UnpinFromTop, Icons.PinOff);
            }
            else
            {
                flyout.CreateFlyoutItem(ViewModel.SavedChatsTab.PinTopic, topic, Strings.PinToTop, Icons.Pin);
            }

            flyout.CreateFlyoutItem(ViewModel.SavedChatsTab.DeleteTopic, topic, Strings.Delete, Icons.Delete, destructive: true);

            flyout.ShowAt(sender, args);
        }

        public void UpdateSavedMessagesTopicLastMessage(SavedMessagesTopic topic)
        {
            this.BeginOnUIThread(() =>
            {
                var clientService = ViewModel?.ClientService;
                if (clientService == null)
                {
                    return;
                }

                var container = ScrollingHost.ContainerFromItem(topic) as SelectorItem;
                // ContentRoot() and not ContentTemplateRoot: in Uno the latter is null for every
                // container that has a control template, so this refresh would silently do
                // nothing and the row would keep the message it was bound with.
                if (container?.ContentRoot() is ChatCell cell)
                {
                    cell.UpdateSavedMessagesTopic(clientService, topic);
                }
            });
        }
    }
}
