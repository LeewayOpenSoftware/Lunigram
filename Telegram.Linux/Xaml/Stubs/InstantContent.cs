//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

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

namespace Telegram.Controls.Messages.Content
{
    public partial class InstantContent : Control
    {
        public InstantContent()
        {
            // ChatView.CheckButtonsVisibility computes
            // `TextField.IsEmpty && DraftField.Visibility == Visibility.Collapsed`, and DraftField
            // is this control. A stub that renders nothing but defaults to Visible would keep that
            // expression false for ever: the blue send button would sit there on an empty box and
            // the record button would never come back. Upstream only ever collapses it explicitly
            // from UpdateChatDraft, which does run on every chat open -- this makes the starting
            // state right as well, instead of depending on that call having happened first.
            Visibility = Visibility.Collapsed;
        }

        public void UpdateView(IClientService clientService, IReadOnlyList<PageBlock> blocks, bool animate)
        {
        }

        // 2026-08-27. Lo llama TranslatePopup (que entra en el subconjunto en esta tanda) sobre
        // su RichBlock, en la rama del mensaje de vista instantanea. Esa rama no tiene ningun
        // camino vivo en Linux -- DialogViewModel.TranslateMessage la deja fuera y el propio
        // popup se sale antes de pedir la traduccion --, pero tiene que compilar.
        public void ShowHideSkeleton(bool show)
        {
        }
    }
}
