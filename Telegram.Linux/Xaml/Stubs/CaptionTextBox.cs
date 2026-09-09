//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Navigation;
using Telegram.Services;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Controls
{
    // ChooseChatsPopup's comment box (CaptionInput). Upstream inherits CaptionTextBox from
    // FormattedTextBox (FormattedTextBox.cs:62 -> CaptionTextBox.cs:29).
    // The shared implementation of text handling, formatting and selection lives in
    // FormattedTextBox (Stubs/FormattedTextBox.cs). This subclass retains the caption-specific
    // Enter key handling and the HandwritingView stub.
    public partial class CaptionTextBox : FormattedTextBox
    {
        // Real FormattedTextBox.HandwritingView is IsOpen-gated and Accept() (ChooseChatsPopup.
        // xaml.cs) reads it unconditionally on every accept. Always-false-IsOpen keeps that read
        // safe without a handwriting panel to ask -- there is none on Skia.
        public LinuxHandwritingView HandwritingView { get; } = new LinuxHandwritingView();

        public event TypedEventHandler<CaptionTextBox, EventArgs> Accept;

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                var modifiers = Navigation.WindowContext.KeyModifiers();

                var send = AppSettings.IsSendByEnterEnabled
                    ? modifiers == VirtualKeyModifiers.None
                    : modifiers == VirtualKeyModifiers.Control;

                // Same Uno gotcha as ChatTextBoxSend.cs's Send path: e.Handled alone does NOT stop
                // Uno's TextBox from inserting the newline -- it does that in OnPostKeyDown ->
                // OnKeyDownSkia, which runs after this class handler and never looks at Handled.
                // AcceptsReturn is the only property that gate consults, so it is what decides,
                // and the two branches below write it at the only moments where it is safe to.
                e.Handled = send;

                if (send)
                {
                    // RE-3. The ORDER of these two is the whole point, and writing them the other
                    // way round -- as this file did, and as upstream writes it -- SILENTLY ATE
                    // every line but the first of a multi-line caption.
                    //
                    // Uno's `OnAcceptsReturnChanged(false)` truncates Text to its first line the
                    // instant the property goes false (WinUI-conformant for a TextBox, and
                    // harmless upstream, whose CaptionTextBox is a RichEditBox whose document is
                    // the source of truth). This twin is a plain TextBox, so Text IS the caption:
                    // lowering AcceptsReturn before the Accept handler reads it threw the rest of
                    // the message away with no error and no warning. Exactly the trap
                    // ChatTextBoxSend.cs measured on the composer's Enter, arriving here through
                    // the upstream statement order rather than through a second experiment.
                    //
                    // It only bites when the box currently ACCEPTS returns, which is only after a
                    // Shift+Enter -- that is, precisely when there is a second line to lose.
                    Accept?.Invoke(this, EventArgs.Empty);

                    // And the guard, not a bare write: the property may only go false when there
                    // is nothing left to truncate. A handler that leaves the box full (a refused
                    // send, a validation error) keeps AcceptsReturn true and at worst gets a stray
                    // newline, which is the cheaper half of the trade -- same call
                    // ChatTextBoxSend.cs makes with its `if (IsEmpty)`.
                    if (Text.Length == 0)
                    {
                        AcceptsReturn = false;
                    }

                    return;
                }

                // A newline instead: the TextBox only inserts one if it accepts returns, and this
                // has to be written BEFORE it sees the key, which is why this is an OnKeyDown
                // override and not a KeyDown subscription.
                AcceptsReturn = true;
            }

            base.OnKeyDown(e);
        }
    }

    // Inert: there is no handwriting panel on Skia to open, so IsOpen never needs to answer true.
    public sealed class LinuxHandwritingView
    {
        public bool IsOpen => false;

        public event RoutedEventHandler Unloaded { add { } remove { } }

        public void TryClose()
        {
        }
    }
}
