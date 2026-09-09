//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
#if !LINUX
using Telegram.Controls.Drawers;
#endif
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels.Create;
using Telegram.Views.Popups;
#if !LINUX
using Telegram.ViewModels.Drawers;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Telegram.Views.Create
{
    public sealed partial class NewGroupPopup : ContentPopup
    {
        public NewGroupViewModel ViewModel => DataContext as NewGroupViewModel;

        private readonly TaskCompletionSource<Chat> _completion;
        // string.Empty and not null: the parameterless constructor never assigns it, and
        // OnNavigatedTo pushes it straight into ViewModel.Title and from there into TitleLabel.Text.
        private readonly string _defaultTitle = string.Empty;

        public NewGroupPopup(TaskCompletionSource<Chat> completion = null, string defaultTitle = "")
        {
            InitializeComponent();
            InitializeAccessibleNames();

            _completion = completion;
            _defaultTitle = defaultTitle;

            PrimaryButtonText = Strings.OK;
            SecondaryButtonText = Strings.Cancel;
        }

        public NewGroupPopup()
        {
            InitializeComponent();
            InitializeAccessibleNames();

            PrimaryButtonText = Strings.OK;
            SecondaryButtonText = Strings.Cancel;
        }

        private void InitializeAccessibleNames()
        {
            AutomationProperties.SetName(PhotoButton, Strings.AccDescrProfilePicture);
            AutomationProperties.SetName(EmojiButton, Strings.Emoji);
        }

        public override void OnNavigatedTo(object parameter)
        {
#if !LINUX
            EmojiPanel.DataContext = EmojiDrawerViewModel.Create(ViewModel.Session, EmojiDrawerMode.Text);
#endif

            ViewModel.Title = _defaultTitle;
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
            // Linux: the flyout is dropped from the XAML with win:, and the button that would
            // open it is Collapsed upstream, so this handler only exists to satisfy the binding.
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

        // CC-2: CC-1 ported ChooseChatsPopup, and PickUsersAsync (its simple Task<IList<User>>
        // surface, not the Configuration/SendExecute dispatch) needs none of the 6 configs CC-1
        // left guarded. Same body on both platforms now. Its pair, SupergroupMembersPage's
        // "Add member" row, un-collapses in the same tanda -- the picker lands both at once, as
        // the collapsed note here used to promise.
        private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_completion != null)
            {
                ViewModel.Create(_completion);
            }
            else
            {
                var users = await ChooseChatsPopup.PickUsersAsync(ViewModel.ClientService, ViewModel.NavigationService, Strings.SelectContacts, allowEmptySelection: true);
                if (users == null)
                {
                    await this.ShowQueuedAsync(XamlRoot);
                    return;
                }

                ViewModel.Create(users);
            }
        }
    }
}
