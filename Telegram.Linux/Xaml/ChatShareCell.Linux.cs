//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of Controls/Cells/ChatShareCell.xaml.cs, same shape as ProfileCell.Linux.cs:
// upstream's UpdateChat is a two-phase state machine (phase 0 the title, phase 2 the photo,
// identity and selection outline) driven by args.RegisterUpdateCallback. Uno raises
// ContainerContentChanging once, always at phase 0, and never calls the registered callback
// (ProfileTabContainer.cs), so the phased path alone would leave every row titled with no photo.
// This is the same two writes, unconditional, in one pass.

using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Cells
{
    public sealed partial class ChatShareCell
    {
        public void UpdateChatInflated(IClientService clientService, Chat chat)
        {
            if (chat == null)
            {
                return;
            }

            Tag = chat;

            if (chat.Type is ChatTypeSecret)
            {
                TitleLabel.Foreground = BootStrapper.Current.Resources["TelegramSecretChatForegroundBrush"] as Brush;
                TitleLabel.Text = Icons.LockClosedFilled14 + " " + clientService.GetTitle(chat);
            }
            else
            {
                TitleLabel.ClearValue(TextBlock.ForegroundProperty);
                TitleLabel.Text = clientService.GetTitle(chat);
            }

            Photo.Source = ProfilePictureSource.Chat(clientService, chat);
            Identity.SetStatus(clientService, chat, BotVerified);

            SelectionOutline.RadiusX = Photo.ComputedShape == ProfilePictureShape.Superellipse ? 9 : 18;
            SelectionOutline.RadiusY = Photo.ComputedShape == ProfilePictureShape.Superellipse ? 9 : 18;
        }

        // Used by ChatInviteFallbackPopup's user list (InviteToChat's failed-to-add fallback).
        public void UpdateUserInflated(IClientService clientService, User user)
        {
            if (user == null)
            {
                return;
            }

            Tag = user;

            TitleLabel.ClearValue(TextBlock.ForegroundProperty);
            TitleLabel.Text = user.FullName();

            Photo.Source = ProfilePictureSource.User(clientService, user);
            Identity.SetStatus(clientService, user, BotVerified);

            SelectionOutline.RadiusX = 18;
            SelectionOutline.RadiusY = 18;
        }
    }
}
