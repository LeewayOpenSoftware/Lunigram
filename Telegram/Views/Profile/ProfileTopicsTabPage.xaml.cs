//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
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
    public sealed partial class ProfileTopicsTabPage : ProfileTabPage, ITopicListDelegate
    {
        public new ProfileViewModel ViewModel => DataContext as ProfileViewModel;

        public ProfileTopicsTabPage()
        {
            InitializeComponent();

            Connected += OnConnected;
            Disconnected += OnDisconnected;
        }

        private void OnConnected(object sender, RoutedEventArgs e)
        {
            ViewModel?.TopicsTab.Delegate = this;
        }

        private void OnDisconnected(object sender, RoutedEventArgs e)
        {
            ViewModel?.TopicsTab.Delegate = null;
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ForumTopic topic)
            {
                ViewModel.OpenForumTopic(topic);
            }
        }

        private readonly Dictionary<int, SelectorItem> _itemToSelector = new();

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
            if (args.Item is not ForumTopic forumTopic)
            {
                return;
            }

            if (args.InRecycleQueue)
            {
                _itemToSelector.Remove(forumTopic.Info.ForumTopicId);
                return;
            }
#if LINUX
            // OnChoosingItemContainer above never runs in Uno, so the pin / delete menu is
            // attached here, and the cell is reached with ContentRoot() because
            // ContentTemplateRoot is null for every templated container. The map from topic id to
            // container is filled straight away rather than inside the bind callback: the delegate
            // methods below look a container up by id and it has to be there even during the pass
            // where the template has not expanded yet. See Telegram.Common.ProfileTabContainer.
            _itemToSelector[forumTopic.Info.ForumTopicId] = args.ItemContainer;

            ProfileTabContainer.Bind<ForumTopicCell>(sender, args, OnContextRequested,
                (content, item) =>
                {
                    if (item is ForumTopic topic)
                    {
                        content.UpdateForumTopic(ViewModel.TopicsTab, topic);
                    }
                });
#else
            else if (args.ItemContainer.ContentTemplateRoot is ForumTopicCell content)
            {
                _itemToSelector[forumTopic.Info.ForumTopicId] = args.ItemContainer;
                content.UpdateForumTopic(ViewModel.TopicsTab, forumTopic);
                args.Handled = true;
            }
#endif
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            if (ScrollingHost.ItemFromContainer(sender) is not ForumTopic topic)
            {
                return;
            }

            var viewModel = ViewModel?.TopicsTab;
            if (viewModel?.Chat is not Chat chat)
            {
                return;
            }

            // ToggleForumTopicIsPinned and DeleteForumTopic both require CanManageTopics,
            // as in ForumView's own topic menu.
            var canManage = viewModel.ClientService.TryGetSupergroup(chat, out Supergroup supergroup)
                && supergroup.CanManageTopics();

            var flyout = new MenuFlyout();

            if (canManage)
            {
                // PinTopic toggles, there's no separate unpin command on the topics side.
                flyout.CreateFlyoutItem(viewModel.PinTopic, topic, topic.IsPinned ? Strings.UnpinFromTop : Strings.PinToTop, topic.IsPinned ? Icons.PinOff : Icons.Pin);
                flyout.CreateFlyoutItem(viewModel.DeleteTopic, topic, Strings.Delete, Icons.Delete, destructive: true);
            }

            flyout.ShowAt(sender, args);
        }

        public void SetSelectedItem(object topic)
        {
            // Do nothing
        }

        public void SetSelectedItems(IList<object> topics)
        {
            // Do nothing
        }

        public void UpdateForumTopicLastMessage(ForumTopic topic)
        {
            HandleForumTopic(topic, (chatView, chat) =>
            {
                chatView.UpdateForumTopicReadInbox(chat);
                chatView.UpdateForumTopicLastMessage(chat);
            });
        }

        public void HandleForumTopic(int forumTopicId, Action<IForumTopicDelegate, ForumTopic> action)
        {
            if (TryGetTopicAndCell(forumTopicId, out ForumTopic chat, out IForumTopicDelegate cell))
            {
                action(cell, chat);
            }
        }

        public void HandleForumTopic(ForumTopic topic, Action<IForumTopicDelegate, ForumTopic> action)
        {
            if (TryGetCell(topic, out IForumTopicDelegate cell))
            {
                action(cell, topic);
            }
        }

        public void UpdateDirectMessagesChatTopicLastMessage(DirectMessagesChatTopic topic)
        {
            // Do nothing
        }

        public void HandleDirectMessagesChatTopic(long topicId, Action<IDirectMessagesTopicDelegate, DirectMessagesChatTopic> action)
        {
            // Do nothing
        }

        public void HandleDirectMessagesChatTopic(DirectMessagesChatTopic topic, Action<IDirectMessagesTopicDelegate, DirectMessagesChatTopic> action)
        {
            // Do nothing
        }

        private bool TryGetTopicAndCell(int topicId, out ForumTopic topic, out IForumTopicDelegate cell)
        {
            if (_itemToSelector.TryGetValue(topicId, out SelectorItem container))
            {
                topic = ScrollingHost.ItemFromContainer(container) as ForumTopic;
                // ContentRoot() and not ContentTemplateRoot: the latter is null for every
                // templated container in Uno, so every incremental topic update - a new last
                // message, a read counter - would silently do nothing.
                cell = container.ContentRoot() as IForumTopicDelegate;
                return topic != null && cell != null;
            }

            topic = null;
            cell = null;
            return false;
        }

        private bool TryGetCell(ForumTopic topic, out IForumTopicDelegate cell)
        {
            if (_itemToSelector.TryGetValue(topic.Info.ForumTopicId, out SelectorItem container))
            {
                // See TryGetTopicAndCell above.
                cell = container.ContentRoot() as IForumTopicDelegate;
                return cell != null;
            }

            cell = null;
            return false;
        }
    }
}
