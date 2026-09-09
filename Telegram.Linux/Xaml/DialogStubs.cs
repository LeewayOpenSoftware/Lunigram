//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Types declared by Windows-only files that stay out of the Linux subset
// (Controls/Chats/ChatTextBox.cs) but whose names appear in the signatures of the
// shared view models and delegates.
//
// ReportChatSelection USED to be stubbed here too (Views/Popups/ReportChatPopup.xaml.cs
// was out of the subset). u-059 brought the real popup in, so its real `record
// ReportChatSelection` is now the only declaration -- this file's placeholder had to go,
// or the two would collide as duplicate types in Telegram.Views.Popups.

using System.Collections;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Chats
{
    public interface IAutocompleteCollection : ICollection, IEnumerable<object>
    {
        public string Query { get; }

        public Orientation Orientation { get; }

        public bool InsertOnKeyDown { get; }
    }

    // AutocompleteList MOVED to Telegram.Linux/Xaml/ChatTextBoxAutocomplete.cs in u-047: it
    // stopped being a name-only stub the moment something constructed it (the '/' branch of the
    // autocomplete driver hands it the filtered bot commands).
}

// u-066: Controls/ReplyMarkupPanel.cs entered the subset (it declares
// ReplyMarkupButtonClickEventArgs and ReplyMarkupInlineButtonClickEventArgs for real now), so the
// stub block that used to live here was removed rather than left as a duplicate definition.
