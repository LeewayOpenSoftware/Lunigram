//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections;
using System.Collections.Generic;
using Telegram.Common;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Chats
{
    // The autocomplete DRIVER: caret -> query -> IAutocompleteCollection -> the popup.
    //
    // Everything around this was already in place and had nothing driving it. ChatView.xaml
    // declares the list (OrientableListView x:Name="ListAutocomplete", :1085) with its template
    // selector, its ItemClick and its container hooks; Themes/Messages.xaml:18 declares the
    // templates; DialogViewModel.Autocomplete (:2981) already calls
    // Delegate.UpdateAutocomplete, and ChatView.UpdateAutocomplete (:7233) already shows and
    // fills the list. The one missing link was that nobody ever ASSIGNED
    // DialogViewModel.Autocomplete on this platform, so the list was never handed a collection
    // and never became visible.
    //
    // Upstream does this from ChatTextBox.OnTextChanged over ITextRange. Here it hangs off the
    // plain TextBox: `Text` and `SelectionStart` are the whole state.
    public partial class ChatTextBox
    {
        private void UpdateAutocompleteLinux()
        {
            var viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            // The caret, not the end of the text: typing inside an already written message has
            // to complete the word being touched, not the last one on the line.
            if (!TryGetAutocompleteLinux(Text, SelectionStart, viewModel.Autocomplete, out var autocomplete))
            {
                // Assigning null is what CLOSES the popup -- UpdateAutocomplete collapses the
                // list on a null collection. Guarded so that every keystroke in a normal message
                // does not re-raise the property and re-run the delegate for no change.
                if (viewModel.Autocomplete != null)
                {
                    viewModel.Autocomplete = null;
                }

                return;
            }

            viewModel.Autocomplete = autocomplete;
        }

        private bool TryGetAutocompleteLinux(string text, int caret, IAutocompleteCollection prev, out IAutocompleteCollection autocomplete)
        {
            autocomplete = null;

            var viewModel = ViewModel;
            var chat = viewModel?.Chat;
            if (chat == null)
            {
                return false;
            }

            if (!AutocompleteEntityFinder.TrySearch(text, caret, out AutocompleteEntity entity, out string result, out int index))
            {
                return false;
            }

            if (entity == AutocompleteEntity.Username)
            {
                // Reusing the live collection when the query has not changed is not an
                // optimisation, it is what stops the list from flickering and losing the
                // highlighted row while TDLib answers.
                if (prev is UsernameCollection && prev.Query.Equals(result))
                {
                    autocomplete = prev;
                    return true;
                }

                var guestBots = chat.Type is not ChatTypeSecret;
                var members = chat.Type is ChatTypeBasicGroup or ChatTypeSupergroup { IsChannel: false };

                // index == 0 means the '@' opens the message, which upstream reads as "the user
                // is addressing an inline bot", so top inline bots join the results.
                autocomplete = new UsernameCollection(viewModel.ClientService, chat.Id, viewModel.TopicId, result, index == 0, guestBots, members, false);
                return true;
            }
            else if (entity == AutocompleteEntity.Hashtag)
            {
                if (prev is SearchHashtagsCollection && prev.Query.Equals(result))
                {
                    autocomplete = prev;
                    return true;
                }

                autocomplete = new SearchHashtagsCollection(viewModel.ClientService, result);
                return true;
            }
            else if (entity == AutocompleteEntity.Emoji)
            {
                if (prev is EmojiCollection && prev.Query.Equals(result))
                {
                    autocomplete = prev;
                    return true;
                }

                autocomplete = new EmojiCollection(viewModel.ClientService, result, chat.Id);
                return true;
            }
            else if (entity == AutocompleteEntity.Command && index == 0)
            {
                // `index == 0` is upstream's rule and it is deliberate: a command only counts
                // when it OPENS the message, so a '/' typed mid-sentence (a URL, a date) does
                // not pop the command list.
                //
                // Commands are also the one source that is NOT a TDLib query: the chat's bot
                // commands are already on the view model, so this is a local filter and the list
                // is complete the moment it opens.
                if (!viewModel.HasBotCommands)
                {
                    return false;
                }

                var commands = new List<object>();

                foreach (var command in viewModel.BotCommands)
                {
                    if (command.Command.StartsWith(result, System.StringComparison.OrdinalIgnoreCase))
                    {
                        commands.Add(command);
                    }
                }

                if (commands.Count == 0)
                {
                    return false;
                }

                autocomplete = new AutocompleteList(result, commands);
                return true;
            }

            return false;
        }

        // Replaces the word under the caret with what the user picked. Upstream does the same
        // with Document.GetRange(index, selection.StartPosition).SetText; on a plain TextBox the
        // equivalent is a string splice plus moving the caret, and the caret move matters --
        // without it the cursor jumps to the start and the next character is typed in front.
        public void InsertAutocompleteLinux(string insert)
        {
            var text = Text;
            var caret = SelectionStart;

            if (!AutocompleteEntityFinder.TrySearch(text, caret, out _, out _, out int index) || index < 0)
            {
                return;
            }

            var before = text.Substring(0, index);
            var after = text.Substring(caret);

            SetText(before + insert + after);
            SelectionStart = index + insert.Length;
        }
    }

    // Moved here from Telegram.Linux/Xaml/DialogStubs.cs, where it was a stub only because
    // nothing constructed it. It is upstream's own implementation (ChatTextBox.cs:1337) and it
    // is now a real source: the '/' branch above hands it the filtered bot commands.
    public partial class AutocompleteList : List<object>, IAutocompleteCollection
    {
        public string Query { get; }

        public Orientation Orientation { get; set; } = Orientation.Vertical;

        public bool InsertOnKeyDown { get; } = true;

        public AutocompleteList(string query, IEnumerable<object> collection)
            : base(collection)
        {
            Query = query;
        }
    }
}
