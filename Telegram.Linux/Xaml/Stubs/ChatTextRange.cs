//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Return type of ChatTextDocument's members. Reads as unreferenced from outside this pair --
// it is not: dropping it takes ChatTextDocument down with it.

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
    public partial class ChatTextRange
    {
        public int StartPosition { get; set; }

        public int EndPosition { get; set; }

        public void SetRange(int startPosition, int endPosition)
        {
            StartPosition = startPosition;
            EndPosition = endPosition;
        }
    }
}
