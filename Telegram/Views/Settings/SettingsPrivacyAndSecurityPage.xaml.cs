//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.ComponentModel;
using Telegram.Common;
using Telegram.Td.Api;
using Telegram.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsPrivacyAndSecurityPage : HostedPage
    {
        public SettingsPrivacyAndSecurityViewModel ViewModel => DataContext as SettingsPrivacyAndSecurityViewModel;

        public SettingsPrivacyAndSecurityPage()
        {
            InitializeComponent();
            Title = Strings.PrivacySettings;

#if LINUX
            // Passcode (u-020): SettingsPasscodePage, PasscodePage and the three
            // SettingsPasscode*Popup files are in the subset now, and
            // TLNavigationService.NavigateToPasscode() has the real implementation, so the row
            // is left drawn.
            //
            // Passkey stays removed: it goes through Common/BridgeApplicationContext.cs, which is
            // AppServiceConnection plus FullTrustProcessLauncher talking to the Telegram.Stub
            // Windows helper process. Not portable, and not a matter of adding files.
            //
            // Visibility rather than Items.Remove: HeaderedControlPanel.MeasureOverride already
            // skips children that are not Visible when it hands out the row borders and corner
            // radii, so a collapsed row costs nothing and leaves the shared XAML alone but for the
            // x:Name.
            Passkey.Visibility = Visibility.Collapsed;
#endif
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
                {
#if LINUX
            // Uno raises OnNavigatedTo from Frame.ChangeContent, i.e. inside Frame.Navigate and
            // BEFORE the Navigated event that NavigationService uses to assign the DataContext, so
            // on the first navigation to this page ViewModel is null here and every line below
            // would be a NullReferenceException (measured on this very page, 2026-08-26).
            // See PageEx.WhenViewModelReady in Telegram.Linux/Xaml/FrameworkElementEx.Linux.cs.
            if (ViewModel == null)
            {
                WhenViewModelReady(() => ViewModel != null, () => OnNavigatedTo(e));
                return;
            }
#endif
            ViewModel.PropertyChanged += OnPropertyChanged;

            if (ViewModel.ClientService.Options.IgnoreSensitiveContentRestrictions || ViewModel.ClientService.Options.CanIgnoreSensitiveContentRestrictions)
            {
                FindName(nameof(SensitiveContent));
            }

            UpdateEmailAddressPattern(ViewModel.EmailAddressPattern);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.PropertyChanged += OnPropertyChanged;
        }

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.EmailAddressPattern))
            {
                UpdateEmailAddressPattern(ViewModel.EmailAddressPattern);
            }
        }

        private void UpdateEmailAddressPattern(FormattedText formatted)
        {
            ChangeEmailPattern?.SetText(ViewModel.ClientService, formatted);
        }

        #region Binding

        private string ConvertNewChat(bool? value)
        {
            return value switch
            {
                true => Strings.LastSeenEverybody,
                false => Strings.PrivacyMessagesContactsAndPremium,
                _ => null
            };
        }

        private string ConvertOnOff(bool value)
        {
            return value ? Strings.NotificationsOn : Strings.NotificationsOff;
        }

        private string ConvertSync(bool sync)
        {
            return sync ? Strings.SyncContactsInfoOn : Strings.SyncContactsInfoOff;
        }

        private string ConvertP2P(int mode)
        {
            switch (mode)
            {
                case 0:
                default:
                    return Strings.LastSeenEverybody;
                case 1:
                    return Strings.LastSeenContacts;
                case 2:
                    return Strings.LastSeenNobody;
            }
        }

        private string ConvertTtl(int days)
        {
            if (days == 0)
            {
                return Strings.NotificationsOff;
            }

            return Locale.FormatTtl(days);
        }

        #endregion

        private void ChangeEmailPattern_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateEmailAddressPattern(ViewModel.EmailAddressPattern);
        }
    }
}
