//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Stands in for RichEditBox.Document, which Uno Skia does not implement. See ChatTextBox.

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Chats;
using Telegram.Views;
using Windows.Foundation;

namespace Telegram.Controls.Chats
{
    public partial class ChatTextDocument
    {
        public ChatTextRange Selection { get; } = new ChatTextRange();

        public ChatTextRange GetRange(int startPosition, int endPosition)
        {
            return new ChatTextRange();
        }
    }
}
