//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Td;
using Telegram.ViewModels.Settings;
using Telegram.ViewModels.Settings.Privacy;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Views.Settings.Privacy
{
    public sealed partial class SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesPage : HostedPage
    {
        public SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesViewModel ViewModel => DataContext as SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesViewModel;

        public SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesPage()
        {
            InitializeComponent();
            Title = Strings.PrivacyVoiceMessages;

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
            if (ViewModel.IsPremium)
            {
                Group.Footer = Strings.PrivacyVoiceMessagesInfo;
            }
            else
            {
                var formatted = Extensions.ReplacePremiumLink(Strings.PrivacyVoiceMessagesPremiumOnly2, null);
                var markdown = ClientEx.GetMarkdownText(formatted);

                Group.Footer = markdown.Text;
            }
        }

        #region Binding

        private Visibility ConvertNever(PrivacyValue value)
        {
            return value is PrivacyValue.AllowAll or PrivacyValue.AllowContacts ? Visibility.Visible : Visibility.Collapsed;
        }

        private Visibility ConvertAlways(PrivacyValue value)
        {
            return value is PrivacyValue.AllowContacts or PrivacyValue.DisallowAll ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

    }
}
