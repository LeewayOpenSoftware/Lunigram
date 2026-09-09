//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Controls.Cells;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Chats;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileAnimationsTabPage : ProfileTabPage
    {
        public ProfileAnimationsTabPage()
        {
            InitializeComponent();
        }

#if LINUX
        // This tab's ItemsPanel is a VariableSizedWrapGrid, so the profile header's height has to
        // travel as top Padding rather than as the height of the ListView.Header spacer. See
        // ProfileTabPage.HeaderHeight for the measurement.
        protected override bool HeaderStacksBesideItems => true;
#endif

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (!IsProfile)
            {
                ScrollingHost.Padding = new Thickness(12, 0, 4, 8);
            }

            if (ViewModel.Animations.Empty())
            {
                AddEntranceTransition();
            }
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            try
            {
                if (args.InRecycleQueue || ViewModel == null)
                {
                    return;
                }
#if LINUX
                BindContainer<SharedMediaCell>(args, static (cell, item) =>
                {
                    if (item is MessageWithOwner message)
                    {
                        cell.UpdateMessage(message, true, false);
                    }
                    else
                    {
                        cell.Hide();
                    }
                });

                args.Handled = true;
#else
                else if (args.ItemContainer.ContentTemplateRoot is SharedMediaCell cell)
                {
                    if (args.Item is MessageWithOwner message)
                    {
                        cell.UpdateMessage(message, true, false);
                    }
                    else
                    {
                        cell.Hide();
                    }

                    args.Handled = true;
                }
#endif
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
        }

        private async void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MessageWithOwner message)
            {
                var response = await ViewModel.ClientService.SendAsync(new GetMessageProperties(message.ChatId, message.Id));
                if (response is not MessageProperties properties)
                {
                    return;
                }

                var element = ScrollingHost.ContainerFromItem(e.ClickedItem);

                var viewModel = new ChatGalleryViewModel(ViewModel.ClientService, ViewModel.StorageService, ViewModel.Aggregator, message.ChatId, ViewModel.Topic, message, properties, true);
                ViewModel.NavigationService.ShowGallery(viewModel, element as SelectorItem);
            }
        }
    }
}
