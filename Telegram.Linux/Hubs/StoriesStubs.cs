//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The stories corner of the profile screen, inert.
//
// ProfileTabsViewModel resolves TWO ProfileStoriesTabViewModel from its constructor and adds them
// to Children unconditionally - it does that before it knows whether the chat has any stories at
// all - and ProfileTabItem.GetText names typeof(ProfileStoriesTabPage) for three of its tab kinds.
// So both types have to EXIST for the profile screen to compile, whether or not a stories tab is
// ever shown.
//
// Upstream they are two thin subclasses that pull in the whole stories subsystem:
// ProfileStoriesTabViewModel derives from ChatStoriesViewModel (673 lines: albums, pinning,
// archiving, the story viewer window) and ProfileStoriesTabPage binds StoryCell, StoryViewModel
// and StoriesWindow - Telegram/{ViewModels,Views,Controls}/Stories is 8 513 lines on its own, and
// it reaches ChooseChatsPopup, InputPopup and ChooseStoriesPopup on the way. That is a subsystem,
// not a dependency of the profile screen, and none of the group and channel work needs it: what
// the user asked for on a channel is the media gallery preview (ProfileMediaTabPage), the member
// list, and the management screens.
//
// So the Linux subset excludes both upstream files (see fase1/extra-files.txt) and compiles these
// instead. The view model answers an empty collection and does nothing else; the page is a real
// XAML page (Hubs/ProfileStoriesTabPage.xaml) rather than a bare class, because ProfileTabPage
// resolves ScrollingHost and Header through FindName and a class with no XAML would be a
// NullReferenceException in the base OnLoaded.
//
// The tab is also not offered: ProfileViewModel.UpdateTabsAsync does not add the Posts tab for a
// supergroup or channel under #if LINUX, so in practice the page above is never navigated to. It
// exists so that a tab added by some path this batch did not walk shows an empty list instead of
// taking the navigation down.
//
// ChatStoriesType lives here too. Upstream it is declared in Views/Chats/ChatStoriesPage.xaml.cs,
// next to the page, and ProfileTabsViewModel and ProfileViewModel both name it. Bringing that file
// in for an enum would bring the stories page with it.


using System.Threading.Tasks;
using Telegram.Collections;
using Telegram.Navigation;
using Telegram.Services;
using Microsoft.UI.Xaml.Data;

namespace Telegram.Views.Chats
{
    /// <summary>
    /// Which set of stories a stories tab shows. Same shape and same order as the upstream enum in
    /// Views/Chats/ChatStoriesPage.xaml.cs.
    /// </summary>
    public enum ChatStoriesType
    {
        Pinned,
        Archive
    }

    public partial class ChatStoriesArgs
    {
        public ChatStoriesArgs(long chatId, ChatStoriesType type)
        {
            ChatId = chatId;
            Type = type;
        }

        public long ChatId { get; set; }
        public ChatStoriesType Type { get; set; }
    }
}

