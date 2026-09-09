//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;

namespace Telegram.Common
{
    public enum AutocompleteEntity
    {
        None,
        Emoji,
        Hashtag,
        Username,
        Command,
        Sticker
    }

    // Linux twin of Common/AutocompleteEntityFinder.cs, which is NOT in the compiled subset.
    //
    // The original walks backwards from the caret over an ITextRange, one SetRange per
    // character, because on Windows the composer is a RichEditBox and the run it walks can
    // contain HIDDEN characters -- the custom-emoji placeholders -- which have to be skipped
    // and then discounted from the offsets. None of that exists here: the Linux composer is a
    // plain TextBox (Stubs/ChatTextBox.cs), its content IS `Text`, and the caret IS
    // `SelectionStart`. So the same walk is an index walk over a string, and the whole
    // hidden-character bookkeeping disappears rather than being ported.
    //
    // The rules themselves are copied, not reinvented, because they are what makes the
    // trigger feel right and each one is load-bearing:
    //   * the trigger must start a word -- at index 0, or preceded by whitespace -- so an
    //     e-mail address does not open the mention list on its '@';
    //   * ':' also triggers after a surrogate pair, which is how ":" right after an emoji
    //     still opens the emoji list;
    //   * only letters, digits and '_' may sit between the trigger and the caret, so typing a
    //     space closes the popup instead of searching for a phrase.
    public static class AutocompleteEntityFinder
    {
        private static readonly HashSet<char> _symbols = new() { ':', '#', '@', '/' };

        public static AutocompleteEntity Search(string text, int caret, out string result, out int index)
        {
            TrySearch(text, caret, out AutocompleteEntity entity, out result, out index);
            return entity;
        }

        public static bool TrySearch(string text, int caret, out AutocompleteEntity entity, out string result, out int index)
        {
            entity = AutocompleteEntity.None;
            result = string.Empty;
            index = -1;

            if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
            {
                return false;
            }

            var found = true;

            for (int i = caret - 1; i >= 0; i--)
            {
                var character = text[i];

                if (_symbols.Contains(character))
                {
                    // A trigger only counts at the start of a word. `i > 0` is checked BEFORE
                    // text[i - 1] rather than relying on the loop bound: at i == 0 there is no
                    // preceding character to look at and the trigger is valid by definition.
                    if (i == 0 || text[i - 1] is ' ' or '\n' or '\r' or '\v')
                    {
                        index = i;
                    }
                    else if (character == ':'
                        && (text[i - 1] == '\uEA4F' || (i >= 2 && char.IsSurrogatePair(text, i - 2))))
                    {
                        index = i;
                    }
                    else
                    {
                        found = false;
                    }

                    break;
                }
                else if (i > 0 && IsValidSymbol(character))
                {
                    continue;
                }
                else
                {
                    // Includes i == 0 with a plain letter: the walk reached the start of the
                    // text without meeting a trigger, so there is nothing to complete.
                    found = false;
                    break;
                }
            }

            if (found && index >= 0)
            {
                result = text.Substring(index, caret - index);

                entity = text[index] switch
                {
                    ':' => AutocompleteEntity.Emoji,
                    '#' => AutocompleteEntity.Hashtag,
                    '@' => AutocompleteEntity.Username,
                    '/' => AutocompleteEntity.Command,
                    _ => AutocompleteEntity.None
                };

                if (entity != AutocompleteEntity.None && result.Length > 0)
                {
                    // Drop the trigger character: the collections query on the word, not on ":x".
                    result = result.Substring(1);
                }

                // ":D" and friends are emoticons being typed, not an emoji-by-name query.
                if (entity == AutocompleteEntity.Emoji
                    && (result.Length == 0 || (result.Length == 1 && result[0] == char.ToUpper(result[0]))))
                {
                    entity = AutocompleteEntity.None;
                    result = string.Empty;
                    index = -1;
                }
            }

            return entity != AutocompleteEntity.None;
        }

        public static bool IsValidSymbol(char symbol)
        {
            return char.IsLetter(symbol) || char.IsDigit(symbol) || symbol == '_';
        }
    }
}
