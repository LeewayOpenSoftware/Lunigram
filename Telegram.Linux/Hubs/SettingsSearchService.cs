//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;

namespace Telegram.Services
{
    // The interface of Telegram/Services/SettingsSearchService.cs; the Windows implementation
    // indexes every settings page, none of which is in the Linux subset yet. MainViewModel only
    // takes the service in its constructor, so the entry type is the minimum the interface needs.
    public interface ISettingsSearchService
    {
        IEnumerable<SettingsSearchEntry> Search(string query);
    }

    public abstract class SettingsSearchEntry
    {
        protected SettingsSearchEntry(string text)
        {
            Text = text;
        }

        public string Text { get; set; }

        public SettingsSearchEntry Parent { get; set; }

        public abstract SettingsSearchEntry Clone();
    }

    public partial class SettingsSearchService : ISettingsSearchService
    {
        public IEnumerable<SettingsSearchEntry> Search(string query)
        {
            return Array.Empty<SettingsSearchEntry>();
        }
    }
}
