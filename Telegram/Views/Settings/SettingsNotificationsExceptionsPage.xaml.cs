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
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels.Settings;
using Telegram.Views.Popups;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsNotificationsExceptionsPage : HostedPage
    {
        public SettingsNotificationsExceptionsViewModel ViewModel => DataContext as SettingsNotificationsExceptionsViewModel;

        public SettingsNotificationsExceptionsPage()
        {
            InitializeComponent();
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Chat chat)
            {
                ViewModel.ShowPopup(new ChatNotificationsPopup(), chat.Id);
            }
        }

        #region Recycle

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += Exception_ContextRequested;
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
            // The three usual Uno facts, all silent (PORTING.md 6):
            //   - ChoosingItemContainer never fires, so OnChoosingItemContainer above is dead code
            //     and with it the ContextRequested subscription, which is the per-exception
            //     "Delete" menu - the only way to take a chat off this list one at a time.
            //   - ContentTemplateRoot is always null for a templated container, so the test in the
            //     #else branch never matches and every row draws empty.
            //   - ContainerContentChanging is raised once with Phase 0 and RegisterUpdateCallback
            //     never comes back, so the phased writer would print the chat name and drop the
            //     line that says whether it is muted - which is the entire content of this screen.
            var clientService = ViewModel.ClientService;
            ProfileTabContainer.Bind<ProfileCell>(sender, args, Exception_ContextRequested,
                (content, item) =>
                {
                    if (item is Chat chat)
                    {
                        content.UpdateNotificationExceptionInflated(clientService, chat);
                    }
                });
#else
            else if (args.ItemContainer.ContentTemplateRoot is ProfileCell content)
            {
                content.UpdateNotificationException(ViewModel.ClientService, args, OnContainerContentChanging);
            }
#endif
        }

        #endregion

        #region Context menu

        private void Exception_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var exception = ScrollingHost.ItemFromContainer(sender) as Chat;
            if (exception is null)
            {
                return;
            }

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(ViewModel.Remove, exception, Strings.Delete, Icons.Delete, destructive: true);
            flyout.ShowAt(sender, args);
        }

        #endregion

        private void Alert_Click(object sender, RoutedEventArgs e)
        {
            var muted = !ViewModel.Scope.Alert;
            if (muted)
            {
                ViewModel.Scope.Alert = true;
                ViewModel.Scope.Save();
            }
            else
            {
                var flyout = new MenuFlyout();

                //if (muted is false)
                //{
                //    var silent = chat.DefaultDisableNotification;
                //    flyout.CreateFlyoutItem(true, () => { },
                //        silent ? Strings.SoundOn : Strings.SoundOff,
                //        silent ? Icons.MusicNote2 : Icons.MusicNoteOff2);
                //}

                flyout.CreateFlyoutItem<int?>(ViewModel.MuteFor, 60 * 60, Strings.MuteFor1h, Icons.ClockAlarmHour);
                flyout.CreateFlyoutItem<int?>(ViewModel.MuteFor, null, Strings.MuteForPopup, Icons.AlertSnooze);

                var toggle = flyout.CreateFlyoutItem(
                    muted ? ViewModel.Unmute : ViewModel.Mute,
                    muted ? Strings.UnmuteNotifications : Strings.MuteNotifications,
                    muted ? Icons.Speaker3 : Icons.SpeakerOff);

                if (muted is false)
                {
                    toggle.Foreground = BootStrapper.Current.Resources["DangerButtonBackground"] as Brush;
                }

                flyout.ShowAt(sender as UIElement, FlyoutPlacementMode.BottomEdgeAlignedLeft);
            }
        }
    }
}
