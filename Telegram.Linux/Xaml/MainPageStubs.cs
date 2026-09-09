//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Controls that MainPage.xaml instantiates but that are out of the Phase 1 subset (stories
// strip, global search, call banner, Win2D confetti). They keep the surface MainPage.xaml(.cs)
// touches and render nothing.
//
// Two of them have graduated: SettingsPage since 2026-08-24, so that the touch-mode checkbox on
// the Appearance page can be reached, and PlaybackHeader - the "now playing" band - since
// 2026-08-25, now that there is a PlaybackService behind it to control.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Services.Calls;
using Windows.Foundation;

// SearchChatsView and ItemContextRequestedEventArgs used to be stubbed here too. They graduated
// on 2026-08-26: the real Controls/Views/SearchChatsView.xaml(.cs) is in the subset now, so keeping
// the stubs would merge two `partial class` halves with clashing members. The stub was also not
// signature-compatible with the real one - it had no ItemContextRequestedEventArgs.EventArgs, which
// is exactly what MainPage.DialogsSearchPanel_ItemContextRequested needs to place the flyout.

// The StoriesStrip stub that used to live here is gone: the real
// Controls/Stories/StoriesStrip.xaml(.cs) is in the subset since 2026-08-26, together with
// Controls/Cells/ActiveStoriesCell.xaml(.cs) and ViewModels/Stories/*. Keeping the stub would
// merge two `partial class` halves with clashing members, the way the SearchChatsView stub did.
//
// The arithmetic the stub carried is the real control's own (StoriesStrip.TopPadding /
// GetTopPadding: 40 for the in-app title bar, 32 for the search field, 4, plus 36 when the folder
// tabs are on top), so nothing was lost with it. What the stub's comment warned about still holds
// and is now MainPage's problem: TopPadding is a layout measurement, not a neutral zero, and on
// Linux the number that is actually right is the measured one - see MainPage.ChatListTopPadding.

namespace Telegram.Controls
{
    public partial class GroupCallActiveHeader : Control
    {
        public void Update(VoipCallBase call)
        {
        }
    }

    public partial class ConfettiView : Control
    {
        public void Start()
        {
        }
    }

    // Mini player overlay in the MasterDetailView template (Controls/PlaybackOverlay.xaml).
    public partial class PlaybackOverlay : Control
    {
    }
}

// AutocompleteTemplateSelector STUB DELETED in u-047. Its own comment said it was here
// because the real one "needs the drawer view models" -- and that stopped being true when
// ViewModels/Drawers/*DrawerViewModel.cs entered the subset in a later parcel. The real
// Selectors/AutocompleteTemplateSelector.cs is now compiled, and it is what makes a mention
// draw as a user row instead of as ItemTemplate's plain text.
