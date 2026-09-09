//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Td.Api;
using Telegram.ViewModels.Gallery;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls
{
    public partial class ImageTextSelectionLinkClickedEventArgs : EventArgs
    {
        public ImageTextSelectionLinkClickedEventArgs(string link)
        {
            Link = link;
        }

        public string Link { get; }
    }

    /// <summary>
    /// Inert stand-in for <c>Controls/ImageTextSelection.cs</c>, the overlay that draws the words an
    /// OCR pass found on a picture and lets them be selected. The real one is built on
    /// <c>Telegram.Native.AI</c> (<c>RecognizedText</c>, <c>RecognizedTextSelectionManager</c>,
    /// <c>RecognizedTextSelectionType</c>), a Windows-only engine with no Linux side yet, so the
    /// whole "scan text" feature of the gallery is off - see the <c>#if LINUX</c> in
    /// <c>Controls/Gallery/GalleryContent.xaml.cs</c>.
    ///
    /// It keeps the exact members GalleryContent touches so nothing above it changes, and every one
    /// of them is a no-op that leaves the control collapsed: never a NotImplementedException on a
    /// path the gallery walks (PORTING.md rule 5).
    /// </summary>
    public partial class ImageTextSelection : Control
    {
        public ImageTextSelection()
        {
            IsTabStop = false;
            Visibility = Visibility.Collapsed;
        }

        public event EventHandler<ImageTextSelectionLinkClickedEventArgs> LinkClicked;

        public string Text => string.Empty;

        public string SelectedText => string.Empty;

        public Vector2 ImageSize { get; set; }

        public void SelectAll()
        {
        }

        public void ClearSelection()
        {
        }

        public void ShowSkeleton()
        {
        }
    }
}

namespace Telegram.Controls.Gallery
{
    /// <summary>
    /// Inert stand-in for the sponsored message card of the gallery
    /// (<c>Controls/Gallery/GallerySponsoredContent.xaml</c>): ads inside the media viewer, which
    /// need the whole sponsored-message plumbing and are not part of this phase.
    /// </summary>
    public sealed partial class GallerySponsoredContent : Control
    {
        public GallerySponsoredContent()
        {
            IsTabStop = false;
            Visibility = Visibility.Collapsed;
        }

        public Brush Stroke { get; set; }

        public void UpdateAdvertisement(VideoMessageAdvertisement advertisement)
        {
        }
    }

    /// <summary>
    /// Inert stand-in for the floating mini player (<c>Controls/Gallery/GalleryCompactOverlay.xaml</c>).
    /// This one is **not** coming back: it is an always-on-top window, and under Wayland a regular
    /// client cannot ask to stay on top (recorded as an accepted loss in unigram-linux/HANDOFF.md).
    /// The button that would open it lives in the transport controls, which are real since
    /// 2026-08-25, but it is the one button of theirs that stays collapsed: its visibility asks
    /// <c>ApplicationView.GetForCurrentView().IsViewModeSupported(CompactOverlay)</c>, and Uno's
    /// implementation of that answers true only for <c>Default</c> (measured on the deployed
    /// Uno.dll, <c>unigram-linux/spikes/UnoApiProbe</c>). So nothing reaches these methods.
    /// </summary>
    public static class GalleryCompactOverlay
    {
        public static void Pause()
        {
        }

        public static void CreateOrUpdate(GalleryViewModelBase viewModel, GalleryMedia item, VideoPlayerBase player)
        {
        }

        public static object DebugCurrent => null;

        public static IEnumerable<object> DebugChildrenOf(object node)
        {
            yield break;
        }
    }
}
