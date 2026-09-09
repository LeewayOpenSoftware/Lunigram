//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Common base of the seven header bands that stack above the message list. Collapsed and
// zero-height, so ChatView's band arithmetic adds nothing for the ones still stubbed.

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
    public abstract partial class ChatHeaderStub : Control
    {
        protected ChatHeaderStub()
        {
            Visibility = Visibility.Collapsed;
        }

        public float AnimatedHeight => 0;

        public void InitializeParent(ChatView chatView)
        {
        }

        public IEnumerable<UIElement> GetAnimatableVisuals()
        {
            yield break;
        }
    }
}
