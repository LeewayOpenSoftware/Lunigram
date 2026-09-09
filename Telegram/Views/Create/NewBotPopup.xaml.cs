//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.Converters;
using Telegram.Td.Api;
using Telegram.ViewModels.Create;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Telegram.Views.Create
{
    public record NewBotArgs(long BotUserId, bool ViaLink, string SuggestedName, string SuggestedUsername)
    {
        public NewBotArgs(long botUserId, bool viaLink, KeyboardButtonTypeRequestManagedBot managedBot)
            : this(managedBot.Id, viaLink, managedBot.SuggestedName, managedBot.SuggestedUsername)
        {
        }
    }

    public sealed partial class NewBotPopup : ContentPopup
    {
        public NewBotViewModel ViewModel => DataContext as NewBotViewModel;

        public NewBotPopup()
        {
            InitializeComponent();

            PrimaryButtonText = Strings.CreateManagedBotButton;
            SecondaryButtonText = Strings.Cancel;

            var debouncer = new EventDebouncer<TextChangedEventArgs>(Constants.TypingTimeout, handler => Username.TextChanged += new TextChangedEventHandler(handler));
            debouncer.Invoked += (s, args) =>
            {
                if (ViewModel.UpdateIsValid(Username.Text))
                {
                    ViewModel.CheckAvailability(Username.Text);
                }
            };
        }

        public override void OnNavigatedTo(object parameter)
        {
            if (parameter is not NewBotArgs args
                || !ViewModel.ClientService.TryGetUser(args.BotUserId, out User user)
                || !user.HasActiveUsername(out string username))
            {
                return;
            }

            Photo.Source = ProfilePictureSource.User(ViewModel.ClientService, user);

            TextBlockHelper.SetMarkdown(Message, string.Format(Strings.CreateManagedBotText, username));
        }

        #region Binding

        private ProfilePictureSource ConvertPhoto(string title, BitmapImage preview)
        {
            if (preview != null)
            {
                return new ProfilePictureSourceBitmap(preview);
            }
            else if (string.IsNullOrWhiteSpace(title))
            {
                return ProfilePictureSourceText.GetGlyph(Icons.CameraAddFilled);
            }

            return ProfilePictureSourceText.GetNameForChat(title);
        }

        private string ConvertAvailable(string username)
        {
            return string.Format(Strings.UsernameAvailable, username);
        }

        private string ConvertUsername(string username)
        {
            return MeUrlPrefixConverter.Convert(ViewModel.ClientService, username);
        }

        private string UsernameHelpLink => string.Format(Strings.UsernameHelpLink, string.Empty).TrimEnd();

        private string BotUsernameHelpLink => Strings.BotUsernameHelp.Replace("*Fragment*", "[Fragment](https://fragment.com)");

        #endregion

        private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            IsPrimaryButtonPending = true;

            var deferral = args.GetDeferral();

            var result = await ViewModel.SendAsync();
            if (result)
            {
                deferral.Complete();
                return;
            }

            args.Cancel = true;
            deferral.Complete();

            IsPrimaryButtonPending = false;
        }
    }
}
