//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Messages
{
    // Inert twin: the real CustomEmojiCanvas renders animated custom-emoji glyphs positioned
    // against a RichEditBox.Document, which CaptionTextBox (Stubs/CaptionTextBox.cs) does not
    // have. ChooseChatsPopup.xaml only ever declares one, as a plain empty layer
    // (`<messages:CustomEmojiCanvas x:Name="CustomEmoji" IsHitTestVisible="False" .../>`) that
    // CaptionTextBox.CustomEmoji stores and never reads back.
    public partial class CustomEmojiCanvas : Canvas
    {
    }
}
