//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

#if LINUX
using System;
#endif
using Telegram.Controls.Cells;
using Telegram.Navigation;
using Telegram.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileLinksTabPage : ProfileTabPage
    {
        public ProfileLinksTabPage()
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

            if (ViewModel.Links.Empty())
            {
                AddEntranceTransition();
            }
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
#if LINUX
            // The four sibling tabs all wrap this in try/catch, this one did not. An exception
            // raised from a container callback is not a lost row in Uno: VirtualizingPanelLayout
            // sets ShouldInterceptInvalidate around the whole fill pass with no try/finally, so it
            // leaves the panel deaf to InvalidateMeasure for the rest of the process (PORTING.md 6).
            try
            {
                if (args.InRecycleQueue || ViewModel == null)
                {
                    return;
                }

                if (!IsProfile)
                {
                    args.ItemContainer.BorderThickness = new Thickness(0);
                    args.ItemContainer.Background = null;
                }

                var navigationService = ViewModel.NavigationService;

                BindContainer<SharedLinkCell>(args, (cell, item) =>
                {
                    if (item is MessageWithOwner message)
                    {
                        cell.UpdateMessage(navigationService, message);
                    }
                });

                args.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
#else
            if (args.InRecycleQueue || ViewModel == null)
            {
                return;
            }
            else if (args.ItemContainer.ContentTemplateRoot is SharedLinkCell cell && args.Item is MessageWithOwner message)
            {
                if (!IsProfile)
                {
                    args.ItemContainer.BorderThickness = new Thickness(0);
                    args.ItemContainer.Background = null;
                }

                cell.UpdateMessage(ViewModel.NavigationService, message);
                args.Handled = true;
            }
#endif
        }
    }
}
