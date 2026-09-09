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
using Telegram.Controls.Gallery;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Delegates;
using Telegram.Views.Settings;
using Telegram.Views.Folders;
#if !LINUX
using Telegram.Views.Business;
using Telegram.Views.Stars;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views
{
    public sealed partial class SettingsPage : Page, ISettingsDelegate, IDisposable
    {
        public SettingsViewModel ViewModel => DataContext as SettingsViewModel;

        public SettingsPage()
        {
            InitializeComponent();

            AutomationProperties.SetName(PhotoButton, Strings.AccDescrProfilePicture);
            AutomationProperties.SetName(ShareQrCode, Strings.QrCode);

            _settings = new Dictionary<Type, object>
            {
                { typeof(SettingsAppearancePage), Appearance },
                { typeof(SettingsAdvancedPage), Advanced },
#if LINUX
                { typeof(SettingsProfilePage), Profile },
                { typeof(SettingsLanguagePage), Language },
                { typeof(SettingsPrivacyAndSecurityPage), Privacy },
                { typeof(SettingsSessionsPage), Sessions },
                { typeof(SettingsNotificationsPage), Notifications },
                { typeof(SettingsDataAndStoragePage), Data },
                { typeof(SettingsPowerSavingPage), PowerSaving },
                { typeof(FoldersPage), Folders },
#else
                { typeof(SettingsProfilePage), Profile },
                { typeof(SettingsPrivacyAndSecurityPage), Privacy },
                { typeof(SettingsNotificationsPage), Notifications },
                { typeof(SettingsDataAndStoragePage), Data },
                { typeof(SettingsPowerSavingPage), PowerSaving },
                { typeof(FoldersPage), Folders },
                { typeof(SettingsSessionsPage), Sessions },
                { typeof(SettingsLanguagePage), Language },
#endif
            };

#if LINUX
            // Rows removed rather than left drawn: a row that navigates nowhere reads as a bug,
            // and Navigate() would put the detail frame on a page type that does not exist here.
            // The XAML still declares them, so this stays a single block instead of a conditional
            // per entry and the rebase over upstream is mechanical.
            //
            // The removal list keeps shrinking as parcels land, so it is worth saying what is IN
            // rather than what is out: Appearance, Advanced, Language, Privacy and security,
            // Sessions, Notifications, Data and storage, Power saving (u-020, wired in the u-039
            // consolidation), -- since u-045 -- Profile, and -- since F-1/F-2 -- Folders (ChooseChatsPopup
            // AddExecute un-fenced in F-1). Passcode and Proxy are in too but are not rows of this list:
            // they hang off Privacy and security and off Data and storage.

            // QrCodePopup and InstantPage are out of the subset too.
            ShareQrCode.Visibility = Visibility.Collapsed;
            Help.Children.Remove(Questions);
            Help.Children.Remove(PrivacyPolicy);
#endif
        }

        private readonly Dictionary<Type, object> _settings;

        public void Dispose()
        {
            Bindings?.StopTracking();
        }

        public void UpdateSelection(bool clearBackStack = true)
        {
            object FindRoot()
            {
                if (_settings.TryGetValue(ViewModel.NavigationService.CurrentPageType, out object item))
                {
                    return item;
                }

                for (int i = ViewModel.NavigationService.Frame.BackStack.Count - 1; i >= 0; i--)
                {
                    if (_settings.TryGetValue(ViewModel.NavigationService.Frame.BackStack[i].SourcePageType, out item))
                    {
                        return item;
                    }
                }

                return null;
            }

            if (clearBackStack)
            {
                ViewModel.NavigationService.GoBackAt(0, false);
            }

            Navigation.SelectedItem = FindRoot();
        }

        private void Navigate(Type type)
        {
            if (ViewModel.NavigationService.Navigate(type))
            {
                UpdateSelection();
            }
        }

        private void Profile_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(SettingsProfilePage));
        }

        private void Privacy_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(SettingsPrivacyAndSecurityPage));
        }

        private void PowerSaving_Click(object sender, RoutedEventArgs e)
        {
            // u-053: this was under #if !LINUX even though the row itself is not collapsed (it
            // has been in the _settings dictionary and un-removed since u-039) - a visible,
            // clickable row that silently did nothing on Linux.
            Navigate(typeof(SettingsPowerSavingPage));
        }

        private void Data_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(SettingsDataAndStoragePage));
        }

        private void Folders_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(FoldersPage));
        }

        private void Notifications_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(SettingsNotificationsPage));
        }

        private void Appearance_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(SettingsAppearancePage));
        }

        private void Sessions_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(SettingsSessionsPage));
        }

        private void Language_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(SettingsLanguagePage));
        }

        private void Advanced_Click(object sender, RoutedEventArgs e)
        {
            Navigate(typeof(SettingsAdvancedPage));
        }

        private void Questions_Click(object sender, RoutedEventArgs e)
        {
#if !LINUX
            ViewModel.NavigationService.NavigateToInstant(Strings.TelegramFaqUrl);
#endif
        }

        private void PrivacyPolicy_Click(object sender, RoutedEventArgs e)
        {
#if !LINUX
            ViewModel.NavigationService.NavigateToInstant(Strings.PrivacyPolicyUrl);
#endif
        }

        private void Features_Click(object sender, RoutedEventArgs e)
        {
            if (Uri.TryCreate(Strings.TelegramFeaturesUrl, UriKind.Absolute, out Uri tipsUri))
            {
                MessageHelper.OpenTelegramUrl(ViewModel.ClientService, ViewModel.NavigationService, tipsUri);
            }
        }

        private void Premium_Click(object sender, RoutedEventArgs e)
        {
#if !LINUX
            ViewModel.NavigationService.ShowPromo(new PremiumSourceSettings());
#endif
        }

        private void Stars_Click(object sender, RoutedEventArgs e)
        {
#if !LINUX
            ViewModel.NavigationService.Navigate(typeof(StarsPage));
#endif
        }

        private void Business_Click(object sender, RoutedEventArgs e)
        {
#if !LINUX
            ViewModel.NavigationService.Navigate(typeof(BusinessPage));
#endif
        }

        private async void Photo_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ClientService.TryGetUser(ViewModel.ClientService.Options.MyId, out User user))
            {
                await GalleryWindow.ShowAsync(ViewModel, ViewModel.StorageService, user, Photo);
            }
        }

        #region Binding

        public void UpdateUser(Chat chat, User user, UserFullInfo fullInfo, bool secret, bool accessToken)
        {
            Title.Text = user.FullName();
            Photo.Source = ProfilePictureSource.User(ViewModel.ClientService, user);
            Identity.SetStatus(ViewModel.ClientService, user, BotVerified);
        }

        public void UpdateUserStatus(Chat chat, User user)
        {
        }

        #endregion

        private void VersionLabel_Navigate(object sender, RoutedEventArgs e)
        {
            // u-094b: same class of bug as u-053's PowerSaving_Click -- the 10-tap gesture that
            // raises this event lives in VersionLabel itself, unconditionally, so the row was
            // already a silently-do-nothing tap on Linux. DiagnosticsPage is in the subset now.
            ViewModel.NavigationService.Navigate(typeof(DiagnosticsPage));
        }
    }
}
