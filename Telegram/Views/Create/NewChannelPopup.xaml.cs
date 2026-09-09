//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
#if !LINUX
using Telegram.Controls.Drawers;
#endif
using Telegram.Controls.Media;
using Telegram.ViewModels.Create;
#if !LINUX
using Telegram.ViewModels.Drawers;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Telegram.Views.Create
{
    public sealed partial class NewChannelPopup : ContentPopup
    {
        public NewChannelViewModel ViewModel => DataContext as NewChannelViewModel;

        public NewChannelPopup()
        {
            InitializeComponent();

            AutomationProperties.SetName(PhotoButton, Strings.AccDescrProfilePicture);
            AutomationProperties.SetName(EmojiButton, Strings.Emoji);

            PrimaryButtonText = Strings.OK;
            SecondaryButtonText = Strings.Cancel;
        }

        public override void OnNavigatedTo(object parameter)
        {
#if !LINUX
            EmojiPanel.DataContext = EmojiDrawerViewModel.Create(ViewModel.Session, EmojiDrawerMode.Text);
#endif
        }

        private void Title_Loaded(object sender, RoutedEventArgs e)
        {
            TitleLabel.Focus(FocusState.Keyboard);
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

        #endregion

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
#if !LINUX
            // We don't want to unfocus the text are when the context menu gets opened
            EmojiPanel.ViewModel.Update();
            EmojiFlyout.ShowAt(TitleLabel, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient });
#endif
            // Linux: see NewGroupPopup. The flyout is dropped from the XAML and the button that
            // would open it is Collapsed, so this handler only exists to satisfy the binding.
        }

#if !LINUX
        private void Emoji_ItemClick(object sender, EmojiDrawerItemClickEventArgs e)
        {
            if (e.ClickedItem is EmojiData emoji)
            {
                EmojiFlyout.Hide();

                var text = TitleLabel.Text;
                var index = TitleLabel.SelectionStart;

                if (TitleLabel.SelectionLength > 0)
                {
                    text = text.Remove(TitleLabel.SelectionStart, TitleLabel.SelectionLength);
                }

                text = text.Insert(index, emoji.Value);

                TitleLabel.Text = text;
                TitleLabel.Focus(FocusState.Programmatic);
                TitleLabel.SelectionStart = index;
            }
        }
#endif

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            ViewModel.Create();
        }
    }
}
