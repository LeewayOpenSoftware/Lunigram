//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Messages.Service
{
    public sealed partial class MessageChatHasProtectedContentDisableRequestedContent : MessageService
    {
        public MessageChatHasProtectedContentDisableRequestedContent()
        {
            InitializeComponent();

            Features.Text = Strings.SharingOfferEnable1.Replace(">", "\u2713")
                + "\n\n" + Strings.SharingOfferEnable2.Replace(">", "\u2713")
                + "\n\n" + Strings.SharingOfferEnable3.Replace(">", "\u2713")
                + "\n\n" + Strings.SharingOfferEnable4.Replace(">", "\u2713");
        }

        protected override void UpdateContent(MessageViewModel message)
        {
            if (message.Content is not MessageChatHasProtectedContentDisableRequested chatHasProtectedContentDisableRequested)
            {
                return;
            }

            if (!chatHasProtectedContentDisableRequested.IsExpired && !message.IsOutgoing)
            {
                Accept.Visibility = Visibility.Visible;
                Reject.Visibility = Visibility.Visible;
                Service.CornerRadius = new CornerRadius(11, 11, 4, 4);
            }
            else
            {
                Accept.Visibility = Visibility.Collapsed;
                Reject.Visibility = Visibility.Collapsed;
                Service.CornerRadius = new CornerRadius(11);
            }
        }

        private async void Reject_Click(object sender, RoutedEventArgs e)
        {
            Message.ClientService.TryGetUser(Message.Chat, out User user);

            var confirm = await Message.Delegate.NavigationService.ShowPopupAsync(Strings.SharingOfferDisableCancelText, Strings.SharingOfferDisableCancelTitle, Strings.SharingOfferCancelYes, Strings.Cancel);
            if (confirm == ContentDialogResult.Primary)
            {
                Message.ClientService.Send(new ProcessChatHasProtectedContentDisableRequest(Message.ChatId, Message.Id, false));
            }
        }

        private async void Accept_Click(object sender, RoutedEventArgs e)
        {
            var confirm = await Message.Delegate.NavigationService.ShowPopupAsync(Strings.SharingOfferEnableConfirmText, Strings.SharingOfferEnableCancelTitle, Strings.SharingOfferCancelYes, Strings.Cancel);
            if (confirm == ContentDialogResult.Primary)
            {
                Message.ClientService.Send(new ProcessChatHasProtectedContentDisableRequest(Message.ChatId, Message.Id, true));
            }
        }
    }
}
