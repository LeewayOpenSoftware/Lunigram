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

namespace Telegram.Controls.Chats
{
    // Plain TextBox composer: the Windows one is a RichEditBox with custom emoji, autocomplete
    // and formatting, none of which Uno Skia can host (RichEditBox.Document is unimplemented).
    // Sending plain text is NOT part of that gap and is live -- see ChatTextBoxSend.cs.
    public partial class ChatTextBox : TextBox
    {
        public ChatTextBox()
        {
            Document = new ChatTextDocument();

            // The 48px of left padding are the hole the attach button sits in. In ChatView.xaml
            // TextFieldPanel and btnAttach share a cell on purpose (Grid.Column="1" with
            // ColumnSpan="2"), and what keeps the caret, the text and the placeholder clear of
            // the clip glyph is not the layout: it is `<Setter Property="Padding" Value="48,0,0,0"/>`
            // of DefaultChatTextBoxStyle (Themes/Generic.xaml). That style is win:-only, so on
            // Skia this control falls back to Uno's Fluent TextBox template and its
            // TextControlThemePadding (10,5,6,6) - which is why "Mensaje" was drawn UNDER the clip.
            //
            // 13/15 top/bottom are upstream's own vertical centering (the template's
            // `ScrollViewer Padding="0,13,0,15"` inside a 48px pill); Uno's default template
            // template-binds Padding into both ContentElement and PlaceholderTextContentPresenter,
            // which is exactly the two places that need it.
            Padding = new Thickness(48, 13, 0, 15);

            // Wires TextChanged -> TextChanging (the blue button and the typing indicator hang off
            // it) and the focus resets of the modifier backstop. See ChatTextBoxSend.cs.
            InitializeSend();

            // RE-2: el flyout de formato (marcadores markdown) y sus atajos.
            InitializeMarkdown();
        }

        public ChatTextDocument Document { get; }

        public ListViewBase ControlledList { get; set; }

        public FrameworkElement CreateLinkTarget { get; set; }

        public bool IsMenuExpanded { get; set; }

        public bool IsReplaceEmojiEnabled { get; set; }

        public bool IsEmpty => string.IsNullOrEmpty(Text);

        public MessageEffect Effect { get; set; }

        public MessageComposerHeader Reply { get; set; }

        public event TappedEventHandler Capture;

        public event EventHandler Sending;

        public new event TypedEventHandler<RichEditBox, RichEditBoxTextChangingEventArgs> TextChanging;

        public void InsertText(string text)
        {
            var start = SelectionStart;
            Text = Text.Insert(start, text);
            SelectionStart = start + text.Length;
        }

        public void InsertEmoji(Sticker sticker)
        {
        }

        public void SetText(string text, IList<TextEntity> entities = null)
        {
            var value = text ?? string.Empty;

            // Same trap as DialogViewModel.SetText: Uno's Text coerce callback drops everything
            // after the first line while AcceptsReturn is false. ChatTextBoxSend lowers it again
            // once the box is empty.
            if (value.Length > 0 && (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0))
            {
                AcceptsReturn = true;
            }

            Text = value;
            SelectionStart = Text.Length;
        }

        public void ClearText()
        {
            Text = string.Empty;
        }

        public FormattedText GetFormattedText()
        {
            return new FormattedText(Text, Array.Empty<TextEntity>());
        }

        // Send() and Schedule() are NOT here any more: sending is no longer out of scope, so it
        // lives in its own file, Telegram.Linux/Xaml/ChatTextBoxSend.cs, along with the Enter /
        // Shift+Enter handling and the TextChanging bridge that drives the blue button.
    }
}
