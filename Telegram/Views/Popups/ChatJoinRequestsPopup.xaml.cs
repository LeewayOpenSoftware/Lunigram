//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Specialized;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Converters;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views.Popups
{
    public sealed partial class ChatJoinRequestsPopup : ContentPopup
    {
        public ChatJoinRequestsViewModel ViewModel => DataContext as ChatJoinRequestsViewModel;

        public ChatJoinRequestsPopup(IClientService clientService, INavigationService navigationService, ISettingsService settingsService, IEventAggregator aggregator, Chat chat, string inviteLink)
        {
            InitializeComponent();
            DataContext = new ChatJoinRequestsViewModel(chat, inviteLink, clientService, settingsService, aggregator);

            ViewModel.NavigationService = navigationService;
            ViewModel.Items.CollectionChanged += OnCollectionChanged;

            Title = Strings.MemberRequests;
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Remove && ViewModel.Items.Empty())
            {
                Hide();
            }
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

#if LINUX
            // PORTING.md §6: SelectorItem.ContentTemplateRoot is always null on Uno. The item
            // template IS applied -- it is the property handing you its root that is not
            // implemented -- so every `content.Children[n]` below would have been a
            // NullReferenceException the first time a row was realised, i.e. the popup would have
            // thrown on open. Extensions.ContentRoot() walks to the real root; same substitute
            // ReportAdsPopup and the drawers already use.
            var content = args.ItemContainer.ContentRoot() as Grid;
#else
            var content = args.ItemContainer.ContentTemplateRoot as Grid;
#endif
            var request = args.Item as ChatJoinRequest;

            // u-042 (Jim's u-041 caveat c): the two buttons carry their request in Tag, and that
            // was assigned inside `if (args.Phase == 0)` -- AFTER the `user == null` return just
            // below. A RECYCLED container that took that return kept the PREVIOUS row's Tag, so
            // "Add" would have added the wrong person. Assigned here instead: before every way out
            // of this method, so a container can never be left carrying a stale request.
            if (content?.Children[4] is StackPanel buttons)
            {
                if (buttons.Children[0] is Button approve)
                {
                    approve.Tag = request;
                }

                if (buttons.Children[1] is HyperlinkButton dismiss)
                {
                    dismiss.Tag = request;
                }
            }

            var user = ViewModel.ClientService.GetUser(request.UserId);
            if (user == null)
            {
                AutomationProperties.SetName(args.ItemContainer, string.Empty);
                return;
            }

            if (args.Phase == 0)
            {
                var fullName = user.FullName();

                var title = content.Children[1] as TextBlock;
                title.Text = fullName;

                var stack = content.Children[4] as StackPanel;
                var primary = stack.Children[0] as Button;
                var secondary = stack.Children[1] as HyperlinkButton;

                var action = ViewModel.IsChannel
                    ? Strings.AddToChannel
                    : Strings.AddToGroup;

                primary.Content = action;

                // Every row repeats the same two labels, so the name is the only thing
                // telling a screen reader which request a button belongs to.
                AutomationProperties.SetName(primary, action + ", " + fullName);
                AutomationProperties.SetName(secondary, Strings.Dismiss + ", " + fullName);
                //}
                //else if (args.Phase == 1)
                //{
                var time = content.Children[2] as TextBlock;
                time.Text = Formatter.DateExtended(request.Date);

                // The visible date is abbreviated to fit the row ("Thu", "Aug 5"), which
                // reads poorly, so the name spells it out instead.
                var date = Formatter.LongDateAt(request.Date);

                if (string.IsNullOrEmpty(request.Bio))
                {
                    var subtitle = content.Children[3] as TextBlock;
                    subtitle.Visibility = Visibility.Collapsed;

                    Grid.SetRow(content.Children[4] as StackPanel, 1);

                    AutomationProperties.SetName(args.ItemContainer, fullName + ", " + date);
                }
                else
                {
                    var subtitle = content.Children[3] as TextBlock;
                    subtitle.Text = request.Bio;
                    subtitle.Visibility = Visibility.Visible;

                    Grid.SetRow(content.Children[4] as StackPanel, 2);

                    AutomationProperties.SetName(args.ItemContainer, fullName + ", " + date + ", " + request.Bio);
                }
            }
            else if (args.Phase == 2)
            {
                var photo = content.Children[0] as ProfilePicture;
                photo.Source = ProfilePictureSource.User(ViewModel.ClientService, user);
            }

            if (args.Phase < 2)
            {
                args.RegisterUpdateCallback(OnContainerContentChanging);
            }

            args.Handled = true;
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ChatJoinRequest request)
            {
                Hide();
                ViewModel.NavigationService.NavigateToUser(request.UserId);
            }
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            ProcessRequest(sender, true);
        }

        private void Dismiss_Click(object sender, RoutedEventArgs e)
        {
            ProcessRequest(sender, false);
        }

        private void ProcessRequest(object sender, bool approve)
        {
            if (sender is not FrameworkElement button || button.Tag is not ChatJoinRequest request)
            {
                return;
            }

            var index = ViewModel.Items.IndexOf(request);
            if (index >= 0)
            {
                Control neighbor = null;

                if (index + 1 < ViewModel.Items.Count)
                {
                    neighbor = ScrollingHost.ContainerFromIndex(index + 1) as Control;
                }

                if (neighbor == null && index > 0)
                {
                    neighbor = ScrollingHost.ContainerFromIndex(index - 1) as Control;
                }

                neighbor?.Focus(FocusState.Programmatic);
            }

            ViewModel.Process(request, approve);
        }
    }
}
