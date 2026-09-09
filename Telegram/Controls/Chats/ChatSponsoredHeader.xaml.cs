//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;
using Telegram.Common;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views;
using Telegram.Views.Monetization.Popups;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Chats
{
    public sealed partial class ChatSponsoredHeader : HyperlinkButton
    {
        public DialogViewModel ViewModel => DataContext as DialogViewModel;

        private IClientService _clientService;

        private ChatView _chatView;
        private UIElement _parent;

        private long _thumbnailToken;

        public ChatSponsoredHeader()
        {
            InitializeComponent();

            _collapsed = new SlidePanel.SlideState(this, false, 0);

        }

        public float AnimatedHeight => _collapsed ? 0 : ActualSize.Y;

        public void InitializeParent(ChatView chatView, UIElement parent)
        {
            _chatView = chatView;
            //_collapsed = new SlidePanel.SlideState(_parent = parent, false, 0);
        }

        public void UpdateSponsoredMessage(IClientService clientService, Chat chat, SponsoredMessage message)
        {
            _clientService = clientService;

            if (message == null || chat.Type is not ChatTypePrivate)
            {
                ShowHide(false);
                return;
            }

            ShowHide(true);

#if LINUX
            // The x calls DialogViewModel.HideSponsoredMessage(), which only hides for a Premium
            // account: the other branch goes to NavigationService.ShowPromo, and TLNavigationService
            // answers that with `Logger.Info("Premium promo is not available on Linux")` and nothing
            // else. So for a non-Premium account the x would be a button that silently does nothing;
            // it is not drawn instead. For a Premium account it hides the ad for real
            // (ToggleHasSponsoredMessagesEnabled) and is drawn.
            RemoveButton.Visibility = ViewModel?.IsPremium == true
                ? Visibility.Visible
                : Visibility.Collapsed;
#endif

            var caption = message.Content.GetCaption();

            TitleText.Text = message.Title;
            MessageText.SetText(_clientService, caption);

            var small = message.Sponsor.Photo?.GetSmall();
            if (small != null)
            {
                UpdateManager.Subscribe(this, _clientService, small.Photo, ref _thumbnailToken, UpdateFile, true);
                UpdateThumbnail(small.Photo);

                ThumbRoot.Visibility = Visibility.Visible;
            }
            else
            {
                ThumbRoot.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateFile(File file)
        {
            UpdateThumbnail(file);
        }

        private void UpdateThumbnail(File file)
        {
            if (file.Local.IsDownloadingCompleted)
            {
                ThumbImage.ImageSource = UriEx.ToBitmap(file.Local.Path);
            }
            else if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
            {
                _clientService.DownloadFile(file.Id, 1);
            }
        }

        private void SponsoredMessage_Click(object sender, RoutedEventArgs e)
        {
            var message = ViewModel.SponsoredMessage;
            if (message == null)
            {
                return;
            }

            ViewModel.ClientService.Send(new ClickChatSponsoredMessage(ViewModel.Chat.Id, message.MessageId, false, false));
            ViewModel.OpenUrl(message.Sponsor.Url, false);
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowPopup(new AboutAdsPopup(ViewModel, ViewModel.SponsoredMessage));
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.HideSponsoredMessage();
        }

        private SlidePanel.SlideState _collapsed;

        private void ShowHide(bool show)
        {
            if (_collapsed != show)
            {
                return;
            }

            if (show)
            {
                ViewModel.ViewSponsoredMessage();
            }

            _collapsed.IsVisible = show;
            _chatView.UpdateMessagesHeaderPadding();
        }

        public IEnumerable<UIElement> GetAnimatableVisuals()
        {
            if (_collapsed)
            {
                yield break;
            }

            if (ThumbRoot.Visibility == Visibility.Visible)
            {
                yield return ThumbRoot;
            }
            else
            {
                yield return RemoveButton;
            }
        }
    }
}
