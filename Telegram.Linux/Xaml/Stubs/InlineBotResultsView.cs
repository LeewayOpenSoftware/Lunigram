//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Inline-bot (@gif ...) result strip. The query pipeline is out of the subset.

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

namespace Telegram.Controls
{
    public partial class InlineBotResultsView : Control
    {
        public event ItemClickEventHandler ItemClick;

        public void UpdateChatPermissions(Chat chat)
        {
        }

        public void UpdateCornerRadius(double radius)
        {
        }
    }
}
