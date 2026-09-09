//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Telegram.Controls.Chats
{
    // RE-2: formatting on the plain TextBox composer, as MARKDOWN MARKERS.
    //
    // Windows formats by setting CharacterFormat on a RichEditBox run. There is no run here, so
    // this writes the markers TDLib itself will turn into entities at send time. That is not a
    // workaround bolted on the side: the Linux send path ALREADY parses them --
    // DialogViewModel.GetFormattedText's `#if LINUX` branch (:729) ends in
    // `ClientEx.ParseMarkdown(text, ...)`, which is td_api's parseMarkdown. So a `**` typed by
    // hand and a `**` inserted by Ctrl+B travel exactly the same road, and the road is already
    // paved and in use.
    //
    // THE MARKER TABLE IS TDLIB'S, READ OUT OF TDLIB, NOT ASSUMED. The splittable set is at
    // Libraries/tdlib/td/telegram/MessageEntity.cpp:2656 and it is DOUBLED characters:
    //     **bold**   __italic__   ~~strike~~   ||spoiler||
    // and, elsewhere in the same parser, `code` (:2824, one backtick; three make Pre) and
    // [text](url) (:2506, TextUrl).
    //
    // WHAT CANNOT BE DONE, AND IS NOT FAKED: **underline**. The brief asked for it, and
    // markdown v3 has no marker for it -- the switch at :2666 maps '_' '*' '~' '|' and nothing
    // else, so there is no character sequence that produces MessageEntity::Type::Underline. A
    // button for it would insert something that comes out as literal text in the sent message,
    // which is worse than not offering it. It is absent from the flyout on purpose.
    public partial class ChatTextBox
    {
        // Ctrl+B / Ctrl+I / Ctrl+K, checked from OnKeyDown. Returns true when it consumed the
        // key, so the caller can mark the event handled and Uno's TextBox never sees it.
        private bool TryHandleMarkdownAccelerator(VirtualKey key)
        {
            var modifiers = Navigation.WindowContext.KeyModifiers();
            var control = _controlDown || modifiers.HasFlag(VirtualKeyModifiers.Control);

            if (!control || modifiers.HasFlag(VirtualKeyModifiers.Menu))
            {
                return false;
            }

            switch (key)
            {
                case VirtualKey.B:
                    WrapSelection("**");
                    return true;
                case VirtualKey.I:
                    WrapSelection("__");
                    return true;
                case VirtualKey.K:
                    WrapSelectionAsLink();
                    return true;
            }

            return false;
        }

        // Wraps the selection in `marker`, or -- with nothing selected -- drops the pair in and
        // parks the caret between the halves, so Ctrl+B then typing behaves the way every editor
        // has taught people to expect.
        public void WrapSelection(string marker)
        {
            var text = Text ?? string.Empty;
            var start = SelectionStart;
            var length = SelectionLength;

            if (start < 0 || start > text.Length)
            {
                return;
            }

            length = Math.Min(length, text.Length - start);

            var selected = text.Substring(start, length);
            var before = text.Substring(0, start);
            var after = text.Substring(start + length);

            SetText(before + marker + selected + marker + after);

            if (length == 0)
            {
                SelectionStart = start + marker.Length;
                SelectionLength = 0;
            }
            else
            {
                // Reselect the TEXT, not the markers: pressing Ctrl+B twice should be visibly
                // undoable by the user, and leaving the selection on the content is what lets a
                // second shortcut wrap the same words again rather than the markers.
                SelectionStart = start + marker.Length;
                SelectionLength = length;
            }
        }

        // `[text](url)` with the URL half selected, instead of a modal asking for it.
        //
        // Deliberate: a URL prompt would be a new popup, and a popup is the one thing that needs
        // DI registration plus a ViewModelForPage entry to open at all on this platform. Leaving
        // the placeholder selected means the very next keystroke -- or a paste, which is how a
        // link actually arrives -- replaces it. No dialog to wire, nothing that can fail to open.
        public void WrapSelectionAsLink()
        {
            var text = Text ?? string.Empty;
            var start = SelectionStart;
            var length = SelectionLength;

            if (start < 0 || start > text.Length)
            {
                return;
            }

            length = Math.Min(length, text.Length - start);

            const string placeholder = "url";

            var selected = text.Substring(start, length);
            var before = text.Substring(0, start);
            var after = text.Substring(start + length);

            SetText(before + "[" + selected + "](" + placeholder + ")" + after);

            SelectionStart = start + 1 + length + 2;
            SelectionLength = placeholder.Length;
        }

        // The flyout. Hung on BOTH SelectionFlyout and ContextFlyout on purpose: SelectionFlyout
        // is the nice one (it appears on selecting text) but it is Uno's implementation, not
        // ours, and this batch cannot put it on a screen to confirm it fires. ContextFlyout is
        // the plain right-click menu and is exercised everywhere else in the app. If the first
        // does nothing on Skia, formatting is still reachable by right-click and by the
        // accelerators, so the feature degrades to "less convenient" rather than to "absent".
        private void InitializeMarkdown()
        {
            var flyout = new MenuFlyout();

            void Add(string text, string marker)
            {
                var item = new MenuFlyoutItem { Text = text };
                item.Click += (s, args) => WrapSelection(marker);
                flyout.Items.Add(item);
            }

            Add(Strings.Bold, "**");
            Add(Strings.Italic, "__");
            Add(Strings.Strike, "~~");
            Add(Strings.Spoiler, "||");
            Add(Strings.Mono, "`");

            var link = new MenuFlyoutItem { Text = Strings.CreateLink };
            link.Click += (s, args) => WrapSelectionAsLink();
            flyout.Items.Add(link);

            SelectionFlyout = flyout;
            ContextFlyout = flyout;
        }
    }
}
