//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Microsoft.UI.Composition;

namespace Telegram.Common
{
    /// <summary>
    /// Linux replacement for <c>CompositionScopedBatch.Completed</c>, the third member of the
    /// family <see cref="CompositionRenderingClock"/> and <see cref="CompositionRenderedClock"/>
    /// already cover: an event that compiles, that a batch is dutifully opened and closed around,
    /// and that <b>never fires</b> on Uno.
    ///
    /// <para><b>What it costs when it does not fire.</b> The completion handler is where the
    /// animation's own scaffolding is taken down and the final state written by hand - offsets
    /// reset to zero, clips removed, an element finally collapsed. Skip it and the visual is left
    /// exactly halfway: <c>MainPage.ShowHideTopTabs</c> leaves <c>ChatTabs</c> visible whether it
    /// was showing or hiding them, the chat list keeps the padding the animation borrowed, and
    /// <c>DialogsPanel</c> keeps its -40 bottom margin, so switching chat folder layouts is a
    /// one-way trip.</para>
    ///
    /// <para><b>The replacement.</b> A batch's <c>Completed</c> means "every animation started
    /// inside it has ended", and the only thing an animation's end depends on is time:
    /// <c>KeyFrameAnimation.Duration</c>. So the callback is queued on
    /// <see cref="CompositionRenderingClock"/> - the same 60 Hz UI-thread tick every animation in
    /// this port is driven from - and runs on the first tick at or past that duration. Ticking with
    /// the frame clock rather than a timer of its own is what keeps the callback on a frame
    /// boundary, which is where the handler's assignments belong.</para>
    ///
    /// <para><b>The zero-duration case, which is most of them.</b> Unigram usually leaves
    /// <c>Duration</c> unset. On WinUI that means one second; on Uno,
    /// <c>KeyFrameAnimation.Duration</c> is a plain auto-property with no default, so it is
    /// <see cref="TimeSpan.Zero"/> and <c>KeyFrameEvaluator</c> reports <c>Progress = 1</c> on its
    /// first evaluation - the animation snaps to its end value and stops. Waiting a second there
    /// would leave the app looking stuck for a second on something that already finished, so the
    /// wait is <c>max(duration, one frame)</c>: never zero (the handler has to run <i>after</i> the
    /// visual has been given its animated value at least once) and never longer than the animation
    /// really takes.</para>
    ///
    /// <para><b>Applied at</b> <c>Views/MainPage.xaml.cs</c> (<c>ShowHideTopTabs</c>),
    /// <c>Controls/Gallery/GalleryWindow.xaml.cs</c> (the bottom bar and the closing animation) and
    /// <c>Controls/TopNavView.cs</c> (<c>AnimateSelectionChanged</c> - the folder tab bar of
    /// MainPage and the section list of Settings; its completed handler is what turns the outgoing
    /// selection indicator off and clears <c>_prevIndicator</c>/<c>_nextIndicator</c>).
    /// Since 2026-08-26 also in <c>Controls/Cells/ChatCell.xaml.cs</c>
    /// (<c>ShowHideOnlineStatus</c> and <c>ShowHideAutoDelete</c>: their handler is the only place
    /// the green "online" dot and the auto-delete badge are collapsed, so without it a 12x12
    /// Border with a Background stays Visible over the corner of every avatar that was ever
    /// online - invisible, but Uno's hit testing ignores Opacity) and in
    /// <c>Controls/Chats/ChatHistoryArrows.xaml.cs</c> (all four: unread, mentions, reactions,
    /// poll votes - same shape, worse place, since what stays behind sits on top of the last
    /// bubbles of the history). Those four also need a <c>StopAnimation("Translation.Y")</c>
    /// inside the handler: it writes <c>Translation</c> by hand on visuals it has just animated,
    /// and on Uno a finished KeyFrameAnimation keeps driving its property.
    /// <b>Pending</b>, all outside the phase 1 subset and all with the same shape:
    /// <c>Controls/DownloadsIndicator.cs</c>, <c>Controls/RecentUserHeads.cs</c>,
    /// <c>Controls/Stories/StoriesStrip.xaml.cs</c> and
    /// <c>Composition/CompositionDustVisual.cs</c>.</para>
    /// </summary>
    public static class CompositionScopedBatchEx
    {
        /// <summary>
        /// Run <paramref name="completed"/> once the animations of this batch have had their time,
        /// and end the batch. Stands in for
        /// <c>batch.Completed += (s, e) =&gt; completed(); batch.End();</c>.
        /// </summary>
        /// <param name="batch">
        /// The batch, ended here so the call site reads like the one it replaces. May be null: a
        /// caller that only wants the delay does not have to open one.
        /// </param>
        /// <param name="duration">
        /// How long the animations inside the batch last. <see cref="TimeSpan.Zero"/> - which is
        /// what an animation with no explicit <c>Duration</c> takes on Uno - means "one frame".
        /// </param>
        public static void EndWithCompleted(this CompositionScopedBatch batch, TimeSpan duration, Action completed)
        {
            batch?.End();
            QueueCompleted(duration, completed);
        }

        /// <summary>
        /// The waiting half on its own, for a call site that has no batch to end.
        /// </summary>
        public static void QueueCompleted(TimeSpan duration, Action completed)
        {
            if (completed == null)
            {
                return;
            }

            var frame = TimeSpan.FromSeconds(1 / CompositionRenderingClock.FrameRate);
            var wait = duration > frame ? duration : frame;

            // Wall clock, not a count of ticks: a tick that arrives late (a layout pass ran long)
            // must not make the wait longer than the animation it is standing in for.
            var started = DateTime.UtcNow;

            void handler(object sender, object e)
            {
                if (DateTime.UtcNow - started < wait)
                {
                    return;
                }

                CompositionRenderingClock.Rendering -= handler;
                completed();
            }

            CompositionRenderingClock.Rendering += handler;
        }
    }
}
