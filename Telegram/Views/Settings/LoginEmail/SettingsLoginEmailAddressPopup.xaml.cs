//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Services;
using Telegram.Td.Api;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views.Settings.LoginEmail
{
    public sealed partial class SettingsLoginEmailAddressPopup : ContentPopup
    {
        private readonly IClientService _clientService;

        public SettingsLoginEmailAddressPopup(IClientService clientService)
        {
            InitializeComponent();

            _clientService = clientService;

            PrimaryButtonText = Strings.Continue;
            SecondaryButtonText = Strings.Cancel;
        }

#if LINUX
        // internal, not public, and the reason is a defect in Uno's BindableTypeProviders
        // generator, not a design choice.
        //
        // That generator walks the PUBLIC properties of every type XAML registers and emits
        // bindableType.AddProperty("X", typeof(global::TheType), ...). For a type that is itself
        // produced by ANOTHER source generator it cannot resolve the containing namespace and
        // emits the bare name - here literally typeof(global::PasswordState) - which is CS0400,
        // "not found in the global namespace", pointing at generated code with no obvious cause.
        // Telegram.Td.Api splits exactly along that line: the hand-written types in Telegram/Td/Api
        // (FormattedText, MessageEffect...) come out fully qualified, and the ones the
        // Telegram.Generators generator builds from Libraries/tdjson/td_api.tl (PasswordState,
        // EmailAddressAuthenticationCodeInfo, Birthdate) come out bare.
        //
        // internal keeps the property out of that walk. Every caller is in this same assembly
        // (TLNavigationService and SettingsPrivacyAndSecurityViewModel), so nothing else changes,
        // on either platform.
        internal EmailAddressAuthenticationCodeInfo CodeInfo { get; private set; }
#else
        public EmailAddressAuthenticationCodeInfo CodeInfo { get; private set; }
#endif

        private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var address = PrimaryInput.Text;

            if (string.IsNullOrEmpty(address) || !address.IsValidEmailAddress())
            {
                VisualUtilities.ShakeView(PrimaryInput);
                args.Cancel = true;

                return;
            }

            var deferral = args.GetDeferral();

            var response = await _clientService.SendAsync(new SetLoginEmailAddress(address));
            if (response is EmailAddressAuthenticationCodeInfo codeInfo)
            {
                CodeInfo = codeInfo;
            }
            else if (response is Error error)
            {
                VisualUtilities.ShakeView(PrimaryInput);
                args.Cancel = true;

                if (error.MessageEquals(ErrorType.EMAIL_INVALID))
                {
                    await MessagePopup.ShowAsync(XamlRoot, target: null, Strings.EmailAddressInvalid, Strings.RestorePasswordNoEmailTitle, Strings.OK);
                }
                else if (error.MessageEquals(ErrorType.EMAIL_NOT_ALLOWED))
                {
                    await MessagePopup.ShowAsync(XamlRoot, target: null, Strings.EmailNotAllowed, Strings.RestorePasswordNoEmailTitle, Strings.OK);
                }
            }

            deferral.Complete();
        }

        private void PrimaryInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Hide(ContentDialogResult.Primary);
                e.Handled = true;
            }
        }
    }
}
