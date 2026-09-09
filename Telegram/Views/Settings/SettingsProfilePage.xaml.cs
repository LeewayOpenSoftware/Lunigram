//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Controls;
using Telegram.Converters;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Settings;
using Telegram.Views.Settings.Privacy;
using Microsoft.UI.Xaml;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsProfilePage : HostedPage, IUserDelegate
    {
        public SettingsProfileViewModel ViewModel => DataContext as SettingsProfileViewModel;

        public SettingsProfilePage()
        {
            InitializeComponent();
            Title = Strings.AccountSettings;

#if LINUX
            // Same rule as SettingsAppearancePage: the row navigates to
            // SettingsProfileColorPage, which draws its wedges with Win2D and is not in the
            // subset. Collapsed unconditionally rather than only when IsPremiumAvailable is
            // false, so a premium account never sees a row that opens nothing.
            NameColor.Visibility = Visibility.Collapsed;

            // Chat automation (personal channel, hours, location, automated replies): the last
            // three route through BusinessPage/Business*Page, none of which are in the subset.
            // The first, personal channel, does not need Business at all - but its own picker
            // (SettingsPersonalChatPopup) hits the ChoosingItemContainer/ContentTemplateRoot
            // pair from PORTING.md §6, unfixed, so its rows would render empty. Collapsed as one
            // block rather than three-of-four, same reasoning as Folders in SettingsPage: a
            // half-working feature reads as broken, not incomplete.
            Automation.Visibility = Visibility.Collapsed;
#endif
        }

        private async void LogOut_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ShowPopupAsync(new LogOutPopup());
        }

        #region Delegate

        public void UpdateUser(Chat chat, User user, UserFullInfo fullInfo, bool secret, bool accessToken)
        {
            Photo.Source = ProfilePictureSource.User(ViewModel.ClientService, user);

            if (AppSettings.Diagnostics.HidePhoneNumber)
            {
                PhoneNumber.Badge = "+42 --- --- ----";
            }
            else
            {
                PhoneNumber.Badge = Common.PhoneNumber.Format(user.PhoneNumber);
            }

            if (user.HasActiveUsername(out string username))
            {
                Username.Badge = username;
            }
            else
            {
                Username.Badge = Strings.UsernameEmpty;
            }

            if (ViewModel.IsPremiumAvailable)
            {
                ProfileColor.SetUser(ViewModel.ClientService, user);
            }
            else
            {
                NameColor.Visibility = Visibility.Collapsed;
            }

            if (fullInfo == null)
            {
                return;
            }

            if (fullInfo.Birthdate != null)
            {
                Birthdate.Badge = Formatter.Birthdate(fullInfo.Birthdate);
                BirthdateRemove.Visibility = Visibility.Visible;
            }
            else
            {
                Birthdate.Badge = Strings.EditProfileBirthdayAdd;
                BirthdateRemove.Visibility = Visibility.Collapsed;
            }

            if (ViewModel.ClientService.TryGetChat(fullInfo.PersonalChatId, out Chat personalChat))
            {
                PersonalChannel.Badge = personalChat.Title;
            }
            else
            {
                PersonalChannel.Badge = Strings.EditProfileChannelAdd;
            }
        }

        public void UpdateUserStatus(Chat chat, User user) { }

        #endregion

        #region Binding

        private string ConvertBirthdateFooter(bool contactsOnly)
        {
            return contactsOnly
                ? Strings.EditProfileBirthdayInfoContacts
                : Strings.EditProfileBirthdayInfo;
        }

        #endregion

        private void BioPrivacy_Click(object sender, TextUrlClickEventArgs e)
        {
            ViewModel.NavigationService.Navigate(typeof(SettingsPrivacyShowBioPage));
        }

        private void BirthdatePrivacy_Click(object sender, TextUrlClickEventArgs e)
        {
            ViewModel.NavigationService.Navigate(typeof(SettingsPrivacyShowBirthdatePage));
        }
    }
}
