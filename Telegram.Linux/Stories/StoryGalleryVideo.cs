//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The adapter that lets a story video go through this port's video player.
//
// Upstream a story video does NOT go through the gallery player: StoryContent builds an
// AsyncMediaPlayer of its own with CreateSwapChain = true and hands it a
// <SwapChainPanel x:Name="Video"> (StoryContent.xaml:84). Neither half of that survives here:
// SwapChainPanel is on the not-implemented list of PORTING.md 6, and Telegram.Linux's
// AsyncMediaPlayerSwapChain is an inert stub whose Attach() is an empty method
// (Native/Media/AsyncMediaPlayerTypes.cs:100) because on Linux AsyncMediaPlayer runs mpv with
// vid=no -- it is the sound and the clock, never the pixels.
//
// The pixels come from LinuxVideoPlayer (Xaml/LinuxVideoPlayer.cs), the same player the gallery
// uses: our FFmpeg decoder for the frames, presented against mpv's time-pos. Its entry point is
// VideoPlayerBase.Play(GalleryMedia, double), so the story video has to arrive dressed as a
// GalleryMedia. Everything LinuxVideoPlayer.Play actually reads off it is three properties --
// ClientService, File and Duration (LinuxVideoPlayer.cs:291 and :312) -- plus IsLoopingEnabled,
// which the player checks when the file ends.
//
// storyVideo carries exactly those: `storyVideo duration:double width:int32 height:int32
// has_stickers:Bool is_animation:Bool minithumbnail:minithumbnail thumbnail:thumbnail
// preload_prefix_size:int32 cover_frame_timestamp:double video:file` (td_api.tl:6537).

using System;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels.Gallery;

namespace Telegram.Controls.Stories
{
    /// <summary>
    /// A <see cref="StoryVideo"/> seen as a <see cref="GalleryMedia"/>, so that
    /// <c>LinuxVideoPlayer.Play</c> can take it.
    /// </summary>
    public partial class StoryGalleryVideo : GalleryMedia
    {
        private readonly StoryVideo _video;
        private readonly FormattedText _caption;

        public StoryGalleryVideo(IClientService clientService, StoryVideo video, FormattedText caption)
            : base(clientService)
        {
            _video = video;
            _caption = caption;

            File = video.Video;

            // Same guard GalleryVideo uses: a thumbnail that is not a JPEG is a minithumbnail-shaped
            // blob the image path cannot decode.
            if (video.Thumbnail is { Format: ThumbnailFormatJpeg })
            {
                Thumbnail = video.Thumbnail.File;
            }

            Minithumbnail = video.Minithumbnail;
        }

        /// <summary>
        /// What the aspect-ratio helpers measure against. <see cref="StoryVideo"/> has Width and
        /// Height, which is all Constraint is ever asked for.
        /// </summary>
        public override object Constraint => _video;

        public override FormattedText Caption => _caption;

        public override bool IsVideo => true;

        public override bool HasStickers => _video.HasStickers;

        // IsStreamable is not overridden: GalleryMedia already defaults it to true, and a story is
        // streamed from the first byte -- TDLib hands out a preload prefix precisely so the viewer
        // can start before the file is complete.

        /// <summary>
        /// An "animation" story (is_animation, i.e. a muted GIF-like clip) loops the way a GIF does;
        /// a real video plays once and the viewer moves on. This is the property LinuxVideoPlayer
        /// reads in Restart() to decide between rewinding and raising IsPlayingChanged(false).
        /// </summary>
        public override bool IsLoopingEnabled => _video.IsAnimation;

        /// <summary>
        /// Seconds. <c>GalleryMedia.Duration</c> is an int and storyVideo.duration is a double, so
        /// this rounds up: it is used for the streaming heuristics of RemoteFileSource, where
        /// under-reporting the length is the harmful direction.
        /// </summary>
        public override int Duration => (int)Math.Ceiling(_video.Duration);

        /// <summary>
        /// The exact length, unrounded. The progress bar needs the real number: rounding a 4,2 s
        /// story up to 5 leaves the bar three quarters of a second short of the end when the video
        /// finishes.
        /// </summary>
        public double PreciseDuration => _video.Duration;

        public int PreloadPrefixSize => _video.PreloadPrefixSize;

        public bool IsAnimation => _video.IsAnimation;
    }
}
