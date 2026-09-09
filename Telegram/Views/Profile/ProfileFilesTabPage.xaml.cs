//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Controls.Cells;
using Telegram.Navigation;
using Telegram.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileFilesTabPage : ProfileTabPage
    {
        public ProfileFilesTabPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (IsProfile)
            {
#if LINUX
                // The Linux twin of SearchRoot is declared loaded and collapsed, so there is
                // nothing to materialize -- just something to show. See the comment in the XAML.
                SearchRoot.Visibility = Visibility.Visible;
#else
                FindName(nameof(SearchRoot));
#endif
            }
            else
            {
                ScrollingHost.Style = BootStrapper.Current.Resources["DefaultListViewStyle"] as Style;
                ScrollingHost.Padding = new Thickness(0);
                ScrollingHost.ItemContainerCornerRadius = new CornerRadius(0);
            }

            if (ViewModel.Files.Empty())
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
                if (!IsProfile)
                {
                    args.ItemContainer.BorderThickness = new Thickness(0);
                    args.ItemContainer.Background = null;
                }

                var viewModel = ViewModel;

                BindContainer<SharedFileCell>(args, (cell, item) =>
                {
                    if (item is MessageWithOwner message)
                    {
                        cell.UpdateMessage(viewModel, message);
                    }
                    else
                    {
                        cell.Hide();
                    }
                });

                args.Handled = true;
#else
                else if (args.ItemContainer.ContentTemplateRoot is SharedFileCell cell)
                {
                    if (!IsProfile)
                    {
                        args.ItemContainer.BorderThickness = new Thickness(0);
                        args.ItemContainer.Background = null;
                    }

                    if (args.Item is MessageWithOwner message)
                    {
                        cell.UpdateMessage(ViewModel, message);
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
    }
}
