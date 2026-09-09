//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The half of the plain-TextBox composer that is NOT a stub: what turns Enter, or a click on the
// blue button, into a TDLib sendMessage.
//
// Everything downstream of here is upstream code that is already inside the subset and already
// compiles, so this file only closes the last jump, and it closes it through the same calls, in
// the same order, as Telegram/Controls/Chats/ChatTextBox.cs:1042 (Send) and :1088 (Schedule):
//
//   ChatTextBox.Send()
//     -> DialogViewModel.PickMessageSendOptionsAsync   Telegram.Linux/Hubs/DialogViewModel.Linux.cs:42
//     -> DialogViewModel.GetLinkPreviewOptions         Telegram/ViewModels/DialogViewModel.cs:3192
//     -> DialogViewModel.GetFormattedText(clear: true) Telegram/ViewModels/DialogViewModel.cs:698
//        (its own #if LINUX branch reads TextField.Text, empties it and runs ClientEx.ParseMarkdown)
//     -> ComposeViewModel.SendMessageAsync             Telegram/ViewModels/ComposeViewModel.cs:1127
//     -> ComposeViewModel.CreateSendMessage            -> ClientService.SendAsync(new SendMessage(...))
//
// Going through DialogViewModel rather than around it is the whole point: replies (ComposerHeader /
// GetReply), drafts (SaveDraft / ShowDraftMessage), link previews and the pending/sent state
// updates all hang off those same members, so the day they are wired the composer needs no second
// pass.
//
// What is deliberately NOT here, because it is the rich-editor batch and it is built on
// RichEditBox.Document, which Uno Skia does not implement: inline-bot queries
// (SearchInlineBotResults), autocomplete, formatting, custom emoji, paste of rich content.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
// No `using` for VirtualKey / VirtualKeyModifiers: Telegram/CsWinRT.cs declares both as
// `global using` aliases for the whole project (and CsWinRT.cs is in the Linux csproj, line 606),
// so repeating them file-locally is a duplicate alias, not a harmless redundancy.

namespace Telegram.Controls.Chats
{
    public partial class ChatTextBox
    {
        // Same shape as upstream ChatTextBox.ViewModel (ChatTextBox.cs:50). ChatView.Activate sets
        // DataContext on the ChatView and, under #if LINUX, on this control too, so the composer
        // never has to depend on how (or when) Uno propagates DataContext down the tree.
        public DialogViewModel ViewModel => DataContext as DialogViewModel;

        // Upstream ChatTextBox.CanAccept(): Enter belongs to the inline-bot results while one is
        // active. CurrentInlineBot lives in DialogViewModel.Inline.cs, which IS in the subset, and
        // is always null here because nothing sets it yet -- kept so the guard is already right.
        private bool CanAccept()
        {
            return ViewModel?.CurrentInlineBot == null;
        }

        // Backstop for WindowContext.KeyModifiers(). That one reads
        // Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread, which Uno 6.6 does
        // implement (it is NOT in Uno.UI.dll's not-implemented table, unlike GetKeyState and
        // GetCurrentKeyState, and the X11 host carries XModifierMaskToVirtualKeyModifiers feeding
        // KeyboardStateTracker) -- but no compiled path of this port has ever been measured
        // reading a *modifier* out of it. If it ever answered None, Enter and Shift+Enter would be
        // indistinguishable and every newline would leave as a message, which is the one failure
        // mode worth paying two booleans to avoid. Reset on focus changes so a KeyUp lost to a
        // window switch can never wedge the composer shut.
        private bool _shiftDown;
        private bool _controlDown;

        private ulong _lastKeystroke;
        private bool _wasEmpty = true;

        // UNIGRAM_COMPOSER_PROBE=1: one line per Enter with the two modifier readings side by side,
        // which is the only way to tell "WindowContext.KeyModifiers() reports Shift on X11" from
        // "the local key backstop is carrying it".
        private static readonly bool _probe =
            Environment.GetEnvironmentVariable("UNIGRAM_COMPOSER_PROBE") == "1";

        private void InitializeSend()
        {
            TextChanged += OnTextChangedLinux;
            GotFocus += OnFocusChangedLinux;
            LostFocus += OnFocusChangedLinux;

            // KeyUp has to be registered by hand, and this is NOT belt-and-braces: Uno's
            // ImplementedRoutedEventsGenerator emits for ChatTextBox
            // `RegisterImplementedRoutedEvents(..., KeyDown | ...)` with **no KeyUp** -- the flags
            // come from what the base type implements, and TextBox implements OnKeyDown only. With
            // the flag missing the OnKeyUp override below is dead code, _shiftDown would latch true
            // after the first Shift and every later Enter would insert a newline instead of
            // sending. An explicit instance handler is raised regardless of the flag. If both do
            // run, clearing the two booleans twice costs nothing.
            AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnKeyUpLinux), true);
        }

        private void OnFocusChangedLinux(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _shiftDown = false;
            _controlDown = false;
        }

        private void OnTextChangedLinux(object sender, TextChangedEventArgs e)
        {
            // ChatView.xaml binds TextChanging="TextField_TextChanging", and this control shadows
            // that event with the RichEditBox signature so the XAML keeps compiling. Nothing raised
            // it until now, which is why the blue send button never appeared: CheckMessageBoxEmpty
            // -> CheckButtonsVisibility is only ever reached from there. The arguments are null on
            // purpose: Uno leaves RichEditBoxTextChangingEventArgs.IsContentChanging unimplemented
            // (it always answers false), so ChatView has a matching #if LINUX branch that reads
            // neither of them.
            TextChanging?.Invoke(null, null);

            var empty = IsEmpty;

            if (empty)
            {
                // Safety net for every other way the box can end up empty (a draft that clears it,
                // ClearText, select-all + delete): an empty box has no line to lose, so this is the
                // one moment when lowering AcceptsReturn cannot truncate anything. The send path
                // does not rely on it -- TextChanged is queued in Uno, so it would arrive too late
                // to stop the Enter that is already on its way to OnKeyDownSkia.
                AcceptsReturn = false;
            }

            // Upstream ChatTextBox.OnTextChanged (ChatTextBox.cs:401): the typing indicator, timed
            // on milliseconds since boot rather than on the clock.
            if (!empty)
            {
                var timestamp = Logger.TickCount;

                if (timestamp - _lastKeystroke > 4000 || _wasEmpty)
                {
                    _lastKeystroke = timestamp;
                    ViewModel?.ChatActionManager.SetTyping(new ChatActionTyping());
                }
            }

            // u-047: the autocomplete popup. Runs on every keystroke because the QUERY is the
            // word under the caret, so it changes with each one; the sources themselves are
            // reused while the query is unchanged (TryGetAutocompleteLinux), which is what keeps
            // this from firing a TDLib request per character.
            UpdateAutocompleteLinux();

            _wasEmpty = empty;
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Shift)
            {
                _shiftDown = true;
            }
            else if (e.Key == VirtualKey.Control)
            {
                _controlDown = true;
            }
            else if (TryHandleMarkdownAccelerator(e.Key))
            {
                // RE-2. Marcado como manejado para que Ctrl+B no llegue a la TextBox de Uno:
                // Ctrl+I y Ctrl+K no hacen nada ahi, pero Ctrl+B tampoco tiene por que llegar,
                // y dejar pasar la tecla despues de haber reescrito el texto duplicaria trabajo.
                e.Handled = true;
                return;
            }
            else if (TryPasteImage(e))
            {
                // u-050. Handled para el resto de la cadena enrutada; lo que NO hace es frenar el
                // pegado de Uno, que va por OnPostKeyDown y no mira Handled -- por eso TryPasteImage
                // solo se queda la tecla cuando no hay texto que pegar. Ver ChatTextBoxPaste.cs.
                e.Handled = true;
                return;
            }
            else if (e.Key == VirtualKey.Enter && CanAccept())
            {
                var modifiers = Navigation.WindowContext.KeyModifiers();

                var shift = _shiftDown || modifiers.HasFlag(VirtualKeyModifiers.Shift);
                var control = _controlDown || modifiers.HasFlag(VirtualKeyModifiers.Control);
                var menu = modifiers.HasFlag(VirtualKeyModifiers.Menu);

                // Upstream FormattedTextBox.cs:418, same setting, same meaning: with send-by-enter
                // on (the default) a bare Enter sends and anything else makes a line; with it off
                // only Ctrl+Enter sends.
                var send = AppSettings.IsSendByEnterEnabled
                    ? !shift && !control && !menu
                    : control && !shift && !menu;

                if (_probe)
                {
                    Logger.Info($"composer: Enter, KeyModifiers()={modifiers}, backstop shift={_shiftDown} control={_controlDown} -> {(send ? "send" : "newline")}");
                }

                if (send)
                {
                    // The ORDER of the next three statements is the whole trap, and both halves of
                    // it were measured in the app.
                    //
                    // 1. Neither `e.Handled = true` nor skipping base.OnKeyDown stops Uno's TextBox
                    //    from inserting the newline. Uno does its text insertion in
                    //    `OnPostKeyDown` -> `OnKeyDownSkia`, which the routed-event machinery runs
                    //    AFTER the class handler and which never looks at args.Handled. Its one and
                    //    only gate for this key is
                    //        flag6 = ... || args.Key == VirtualKey.Enter;
                    //        if (!flag6 || AcceptsReturn) { ...insert... }
                    //    (Uno.UI 6.6.184, read from the IL). So `AcceptsReturn = false` is not
                    //    upstream's idiom being copied for tidiness: on Uno it is the ONLY thing
                    //    that keeps Enter from leaving a "\r" in the box after the message is gone.
                    //    Measured with it removed: the composer sat on `text="\r"` with the blue
                    //    button stuck on and the record button never coming back.
                    //
                    // 2. But it must be written AFTER the box has been read and emptied. Uno's
                    //        private void OnAcceptsReturnChanged(bool newValue)
                    //        { if (!newValue) { var text = Text; var firstLine = GetFirstLine(text);
                    //                           if (text != firstLine) { Text = firstLine; } } ... }
                    //    truncates Text to its first line the instant the property goes false --
                    //    WinUI-conformant for a TextBox, and harmless upstream because upstream's
                    //    composer is a RichEditBox whose document is the source of truth. Written
                    //    before the read, as upstream writes it, it threw away every line but the
                    //    first: a box holding "holaprueba 01\rsegunda linea" produced
                    //    `sendMessage { text = "holaprueba 01" }`. No error, no warning, half the
                    //    message gone.
                    //
                    // Send() reads and empties the box synchronously (its awaits are all on
                    // already-completed tasks up to that point), so IsEmpty is true here on the
                    // normal path. The guard makes that ordering a requirement instead of an
                    // accident: the day PickMessageSendOptionsAsync grows a real dialog and Send()
                    // returns with the box still full, this leaves a stray newline rather than
                    // eating the message.
                    e.Handled = true;
                    Send();

                    if (IsEmpty)
                    {
                        AcceptsReturn = false;
                    }

                    return;
                }

                // A newline instead. The TextBox only inserts one if it accepts returns, and this
                // has to be written BEFORE the TextBox sees the key -- which is why this is an
                // OnKeyDown override (a class handler, ahead of the control's own) rather than a
                // KeyDown subscription, and why ChatView.xaml can keep declaring
                // AcceptsReturn="False". It is never written back to false here: see above.
                // OnTextChangedLinux puts it back once the box is empty, where there is nothing
                // left to truncate.
                AcceptsReturn = true;
                e.Handled = false;
            }

            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyRoutedEventArgs e)
        {
            OnKeyUpLinux(this, e);
            base.OnKeyUp(e);
        }

        private void OnKeyUpLinux(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Shift)
            {
                _shiftDown = false;
            }
            else if (e.Key == VirtualKey.Control)
            {
                _controlDown = false;
            }
        }

        public async void Send(bool disableNotification = false)
        {
            var viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            // Upstream runs SearchInlineBotResults first; inline bots are the rich-editor batch and
            // CurrentInlineBot is always null here, so that branch is left out rather than faked.
            // The scheduled-messages branch IS kept, because dropping it would send a scheduled
            // message immediately: the Linux PickMessageSendOptionsAsync ignores its schedule
            // argument and hands back options with SchedulingState left null.
            if (viewModel.Type == DialogType.ScheduledMessages && viewModel.ComposerHeader?.Editing == null)
            {
                Schedule(false);
                return;
            }

            var options = await viewModel.PickMessageSendOptionsAsync(1, SchedulingState.Auto, disableNotification, false);
            if (options == null)
            {
                return;
            }

            options.EffectId = Effect?.Id ?? 0;

            Sending?.Invoke(this, EventArgs.Empty);
            Effect = null;

            var linkPreview = viewModel.GetLinkPreviewOptions();

            // clear: true empties the box in the same call that reads it, so a second Enter while
            // the request is in flight cannot send the same text twice.
            var text = viewModel.GetFormattedText(true);

            try
            {
                await viewModel.SendMessageAsync(text, linkPreview, options);
            }
            catch (Exception ex)
            {
                // The box is already cleared above -- deliberately, to beat a second Enter racing
                // the request -- so a throw here used to just lose whatever the user typed. This is
                // an async void UI handler: nothing above catches a throw out of it, so restoring
                // has to happen here or not at all. Put the text back rather than reordering the
                // clear, which is what the anti-double-Enter behavior depends on.
                Logger.Error("send failed, restoring composed text", ex);
                viewModel.SetText(text);
            }
        }

        // Inert on purpose, and it is NOT an oversight to be fixed by copying Send(): the Linux
        // PickMessageSendOptionsAsync (Telegram.Linux/Hubs/DialogViewModel.Linux.cs:42) is the
        // no-dialog substitute and returns MessageSendOptions with SchedulingState null whatever is
        // asked of it, so a Schedule() written like Send() would quietly send NOW instead of at the
        // requested time. Both callers -- Send_ContextRequested and Send_RightTapped -- are empty
        // on Linux today, so nothing reaches this. It needs the real send-options dialog first;
        // until then, doing nothing is the honest answer.
        public void Schedule(bool whenOnline)
        {
        }
    }
}
