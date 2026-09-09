//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of Views/ProfilePage.xaml.cs.
//
// Two things the page does with the scroll cannot be done the way upstream does them, and both are
// measured against the deployed Uno 6.6.184 (PORTING.md §6):
//
//   * The rounded clip over the chat-background card. CompositionGeometricClip does not override
//     SetAnimatableProperty, so StartAnimation("Offset.Y", ...) writes into the clip's generic
//     property bag and its own _offset field never moves. It compiles, it does not throw, and the
//     clip stays where it was put. Driven by hand below instead - CompositionClip.Offset is an
//     ordinary setter and goes through SetProperty -> OnPropertyChanged -> PropagateChanged, which
//     is what invalidates the visual that owns the clip.
//
//   * The snapping of the collapsing header. Upstream decides it in ScrollViewer.ViewChanging, and
//     ViewChanging is [NotImplemented] for __SKIA__: subscribing logs a line at Debug and the
//     handler never runs. So does DirectManipulationStarted/Completed. ViewChanged is the only one
//     Uno raises, and the decision is re-taken there.
//
// The pulling itself is NOT reimplemented, and does not need to be: Uno's ScrollViewer really does
// implement mandatory snap points over an IScrollSnapPointsInfo content (ProfileSnapGrid). It
// re-reads them from a one-shot timer started right after ViewChanged is raised - and ViewChanged
// is raised synchronously from inside Update(), so a Snap() called from the handler below is
// already published by the time the timer fires. What is reimplemented is only the choice of WHICH
// snap point to publish.
//
// One hazard to keep in mind when touching this: with Mandatory snapping, publishing a single snap
// point pulls the view to it from ANYWHERE in the document that is within [min, max] - Uno's
// AdjustOffsetWithMandatorySnapPoints picks the nearest published point and assigns it, with no
// notion of "too far to bother". An empty list is the safe state (it returns immediately), which is
// what ProfileSnapGrid.Unsnap() produces. Hence the guards below: outside the band where the header
// collapses, the points are cleared rather than left standing.

using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views
{
    public sealed partial class ProfilePage
    {
        #region Background card clip

        private CompositionGeometricClip _backgroundClip;
        private float _backgroundClipHeaderHeight;

        /// <summary>
        /// What the expression <c>max(4, _.Translation.Y + HeaderHeight + 4)</c> would have kept on
        /// the clip's Offset.Y. Translation.Y of the scroll is minus the vertical offset, so the
        /// clip's top edge follows the header down and stops 4px in.
        /// </summary>
        private void UpdateBackgroundClip()
        {
            if (_backgroundClip is CompositionGeometricClip clip)
            {
                var y = Math.Max(4, -ScrollingHost.VerticalOffset + _backgroundClipHeaderHeight + 4);
                clip.Offset = new Vector2(0, (float)y);
            }
        }

        #endregion

        #region Header snapping

        private double _linuxPreviousOffset;

        /// <summary>
        /// The decision half of <c>OnViewChanging</c>, taken from ViewChanged because ViewChanging
        /// never fires. Reads the settled offset rather than the predicted final one, and the
        /// previous settled offset rather than the drag's starting offset, which are the two things
        /// ScrollViewerViewChangingEventArgs would have supplied.
        /// </summary>
        private void LinuxSnap(ScrollViewerViewChangedEventArgs e)
        {
            // Same two guards as upstream: Saved Messages has no collapsing header at all, and a
            // page the user has not scrolled is still being positioned by ScrollToContent.
            if (ViewModel == null || ViewModel.IsSavedMessages || !_hasBeenScrolled)
            {
                return;
            }

            // Nothing to decide while the view is still moving: without a predicted final offset
            // there is no better answer mid-gesture than the one taken when it stops.
            if (e.IsIntermediate)
            {
                return;
            }

            var offset = ScrollingHost.VerticalOffset;
            var previous = _linuxPreviousOffset;

            _linuxPreviousOffset = offset;

            var headerHeight = ProfileHeader.HeaderHeight;
            var fullHeight = ProfileHeader.ActualSize.Y;

            // 48 is the collapsed header, 48 + 10 its minimum resting height; both are upstream's.
            var collapsed = headerHeight - 48;
            var contentOffset = Math.Max(fullHeight - 24, 48 + 10);

            if (offset <= collapsed)
            {
                // Inside the band where the header collapses. Upstream picks forward or backward
                // from the direction of travel, with a 24px bias towards finishing the collapse.
                var forward = offset > previous || Math.Abs(offset - collapsed) > 24;

                var snap = fullHeight > headerHeight
                    ? previous >= contentOffset
                        ? contentOffset
                        : collapsed
                    : contentOffset;

                RootGrid.Snap((float)(forward ? snap : 0), true);
            }
            else if (offset <= fullHeight)
            {
                // Between the collapsed header and the top of the content: the one place upstream
                // still snaps, and only when the offset is already within 32px of the content edge.
                var diff = offset - (fullHeight - 48);

                RootGrid.Snap(diff >= -1 && diff <= 32 ? (float)Math.Max(fullHeight - 24, 48 + 10) : -1, false);
            }
            else
            {
                // Past the header, i.e. scrolling the tab itself. Publishing anything here would
                // drag the user back up from wherever they are, so publish nothing.
                RootGrid.Unsnap();
            }
        }

        #endregion

        #region DataContext hand-off

        /// <summary>
        /// Hands the page's DataContext to <c>ProfileHeader</c> the moment the page is given one.
        /// </summary>
        /// <remarks>
        /// MEASURED (see PORTING.md §6). NavigationService sets <c>page.DataContext = viewModel</c>
        /// before it raises OnNavigatedTo, and in WinUI that value has reached every descendant by
        /// the time the page's own code runs. In Uno it has not reached anything nested under a
        /// ScrollViewer. Uno propagates DataContext down <c>_childrenBindable</c> in
        /// <c>DependencyObjectStore.ApplyChildrenBindable</c>, and that walk SKIPS any candidate
        /// whose <c>GetParent()</c> is not the instance doing the walking
        /// (<c>if (obj2 != null &amp;&amp; obj2 != ActualInstance) continue;</c>). A ScrollViewer
        /// does not own its content directly: the content is reparented into the
        /// ScrollContentPresenter when the template is applied, which happens on the first measure
        /// — after navigation. So at OnNavigatedTo the branch under ScrollingHost is still an
        /// island with no DataContext, and <c>ProfileHeader.ViewModel</c>, which is just
        /// <c>DataContext as ProfileViewModel</c>, is null.
        ///
        /// The symptom is not subtle and it is not a rendering glitch: ProfileHeader.UpdateChat and
        /// ProfileHeader.InitializeScrolling both dereference ViewModel on their first line, so the
        /// NullReferenceException escapes from inside OnNavigatedTo and takes the whole navigation
        /// down — the page is constructed and then never shown, and what stays on screen is the
        /// chat you tapped the header of.
        ///
        /// The fix is the same hand-off ChatView already does for itself
        /// (<c>DataContext = _viewModel</c>): assign it explicitly rather than wait for a walk that
        /// will not arrive in time. DataContextChanged fires when NavigationService assigns it,
        /// which is earlier than both OnNavigatedTo and the view model's OnNavigatedToAsync, so
        /// every caller downstream sees a live ViewModel. ProfileHeader is a ContentControl, so
        /// from there the value reaches its own subtree (ProfileGiftsCover, ProfileHoursCell, the
        /// action buttons) the ordinary way.
        ///
        /// The tab pages need nothing here: ProfilePage assigns their DataContext by hand already.
        /// </remarks>
        private void LinuxInitialize()
        {
            DataContextChanged += OnLinuxDataContextChanged;

            // Collapsed until a tab that wants a chat background asks for one. See the comment on
            // the `else` branch of OnNavigated: an un-updated ChatBackgroundControl paints
            // Telegram's default green wallpaper here rather than nothing.
            BackgroundRoot.Visibility = Visibility.Collapsed;
        }

        private void OnLinuxDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (ProfileHeader != null)
            {
                ProfileHeader.DataContext = args.NewValue;
            }
        }

        #endregion
    }
}
