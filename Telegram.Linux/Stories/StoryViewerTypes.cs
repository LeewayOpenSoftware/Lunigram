//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The small types the story viewer publishes, lifted out of the two upstream code-behinds that
// declare them (Controls/Stories/StoriesWindow.xaml.cs and Controls/Stories/StoryContent.xaml.cs).
//
// Why they are here and not linked from ../Telegram: the Linux head does NOT compile those two
// files. They are 3 575 lines whose XAML names a SwapChainPanel for the video, a FormattedTextBox
// (RichEditBox.Document) for the reply box, a StickerPanel, and the live-broadcast half that is
// VoipGroupCall -- four things this head does not have, three of them owned by other batches. The
// viewer is rebuilt next door in StoryContent.cs / StoriesWindow.cs against the pieces this port
// does have (LinuxVideoPlayer, ImageView, the manipulation-based gestures), keeping the same class
// names in the same namespace, so every shared file that says `new StoriesWindow()` compiles
// unchanged. These enums are part of that surface, so they have to exist exactly as upstream
// spells them -- same names, same members, same [Flags] values.

using System;
using Telegram.ViewModels.Stories;

namespace Telegram.Controls.Stories
{
    /// <summary>
    /// Where the viewer was opened from. Upstream: StoriesWindow.xaml.cs. Named by every caller of
    /// <c>StoriesWindow.Update</c> (ActiveStoriesSegments, MessageBubble, MessageHelper,
    /// DialogViewModel.Delegate, ChatStoriesViewModel, StoryListViewModel).
    /// </summary>
    public enum StoryOpenOrigin
    {
        ProfilePhoto,
        Mention,
        Card
    }

    /// <summary>
    /// Upstream: StoryContent.xaml.cs.
    /// </summary>
    public partial class StoryEventArgs : EventArgs
    {
        public ActiveStoriesViewModel ActiveStories { get; }

        public StoryEventArgs(ActiveStoriesViewModel activeStories)
        {
            ActiveStories = activeStories;
        }
    }

    /// <summary>
    /// Upstream: StoryContent.xaml.cs. <c>Live</c> is not in the upstream enum (there it is a third
    /// branch tested on the TDLib content type) and is not added here either, so the shape stays
    /// identical.
    /// </summary>
    public enum StoryType
    {
        Photo,
        Video,
    }

    /// <summary>
    /// Every reason a story can be paused, so that two of them overlapping (a flyout opened while
    /// the finger is held down) still only resume once both are gone. Values copied verbatim from
    /// StoriesWindow.xaml.cs, out-of-order <c>Stickers = 1 &lt;&lt; 10</c> included: they are a
    /// bitmask, the order is irrelevant, and keeping the numbers identical means a file that moves
    /// between the two heads keeps meaning the same thing.
    /// </summary>
    [Flags]
    public enum StoryPauseSource
    {
        None = 0,
        Stickers = 1 << 10,
        Record = 1 << 1,
        Text = 1 << 2,
        Toast = 1 << 3,
        Flyout = 1 << 4,
        Popup = 1 << 5,
        Interaction = 1 << 6,
        Caption = 1 << 7,
        Window = 1 << 8,
        Live = 1 << 9
    }

    /// <summary>
    /// Upstream this is an internal enum at the bottom of StoriesWindow.xaml.cs.
    /// </summary>
    internal enum Direction
    {
        Forward,
        Backward
    }
}
