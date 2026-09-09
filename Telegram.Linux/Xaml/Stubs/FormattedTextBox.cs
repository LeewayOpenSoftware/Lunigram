//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Telegram.Common;
using Telegram.Controls.Chats;
using Telegram.Controls.Messages;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td;
using Telegram.Td.Api;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls
{
    [Flags]
    public enum FormattedTextEntity
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strikethrough = 8,
        Mono = 16,
        Spoiler = 32,
        Quote = 64,
        TextUrl = 128,
        CustomEmoji = 256,
        Mention = 512,
        Date = 1024,
        All = Bold | Italic | Underline | Strikethrough | Mono | Spoiler | Quote | TextUrl | CustomEmoji | Mention | Date,
        Checklist = Bold | Italic | Underline | Strikethrough | Spoiler | CustomEmoji
    }

    // Plain TextBox stub for FormattedTextBox: upstream derives from RichEditBox with custom emoji
    // and rich text document formatting, neither of which Uno Skia implements.
    // CaptionTextBox derives from this (matching upstream's inheritance hierarchy).
    public partial class FormattedTextBox : TextBox
    {
        public FormattedTextBox()
        {
            Document = new ChatTextDocument();
        }

        public ChatTextDocument Document { get; }

        public ViewModelBase ViewModel { get; set; }

        public CustomEmojiCanvas CustomEmoji { get; set; }

        public FormattedTextEntity AllowedEntities { get; set; }

        public bool IsEmpty => string.IsNullOrEmpty(Text);

        public void SetText(string text)
        {
            SetText(text, null);
        }

        public void SetText(FormattedText text)
        {
            if (text == null)
            {
                SetText(null, null);
            }
            else
            {
                SetText(text.Text, text.Entities);
            }
        }

        public void SetText(string text, IReadOnlyList<TextEntity> entities)
        {
            var value = text ?? string.Empty;

            if (value.Length > 0 && entities != null && entities.Count > 0)
            {
                var markdown = ClientEx.GetMarkdownText(value, entities.ToVector());
                if (markdown != null)
                {
                    value = markdown.Text ?? value;
                }
            }

            if (value.Length > 0 && (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0))
            {
                AcceptsReturn = true;
            }

            Text = value;
            SelectionStart = Text.Length;
        }

        public void InsertText(string text)
        {
            var value = text ?? string.Empty;
            var current = Text ?? string.Empty;

            var start = Math.Clamp(SelectionStart, 0, current.Length);
            var length = Math.Clamp(SelectionLength, 0, current.Length - start);

            if (length > 0)
            {
                current = current.Remove(start, length);
            }

            Text = current.Insert(start, value);
            SelectionStart = start + value.Length;
        }

        public void InsertEmoji(Sticker sticker)
        {
        }

        // u-groupcall-compile-clean: `clear` mirrors the shared FormattedTextBox
        // (Telegram/Controls/FormattedTextBox.cs:1258), whose signature is
        // GetFormattedText(bool clear = false, ...). GroupCallWindow sends a chat message
        // with GetFormattedText(true) and relies on the box emptying itself as part of
        // the send -- without the parameter the text would stay behind after sending.
        // Optional, so the existing no-argument callers on this head are unchanged.
        public FormattedText GetFormattedText(bool clear = false)
        {
            var text = Text ?? string.Empty;
            text = text.Replace('\v', '\n').Replace('\r', '\n');

            if (clear)
            {
                ClearText();
            }

            return ClientEx.ParseMarkdown(text, Array.Empty<TextEntity>());
        }

        // Matches Telegram/Controls/FormattedTextBox.cs:1620. The stub keeps no rich
        // document, so emptying Text and collapsing the selection is the whole of it.
        public void ClearText()
        {
            Text = string.Empty;
            SelectionStart = 0;
            SelectionLength = 0;
        }

        public bool CanPasteClipboardContent
        {
            get
            {
                try
                {
                    return Clipboard.GetContent()?.Contains(StandardDataFormats.Text) ?? false;
                }
                catch
                {
                    return false;
                }
            }
        }

        public async void PasteFromClipboard()
        {
            try
            {
                var package = Clipboard.GetContent();
                if (package != null && package.Contains(StandardDataFormats.Text))
                {
                    InsertText(await package.GetTextAsync());
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }
}
