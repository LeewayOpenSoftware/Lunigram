//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;

namespace Telegram.Common
{
    /// <summary>
    /// Linux replacement for <c>CompositionTarget.Rendered</c>, the sibling of
    /// <see cref="CompositionRenderingClock"/>: <c>Rendering</c> runs <i>before</i> the frame is
    /// composed, <c>Rendered</c> runs <i>after</i> it has been presented. WinUI raises it from
    /// <c>ICompositionTargetStatics3.add_Rendered</c> once per presented frame (see
    /// <c>Telegram.Common.CompositionTargetImpl</c> in <c>Common/Interop.cs</c>), which is what
    /// gives its callers the one guarantee they are after: <b>whatever the app changed in the pass
    /// that just ended is now on screen</b>.
    ///
    /// Uno raises it <b>never</b> — a counter next to the subscription stayed at zero for a whole
    /// run of the app, where the same counter on <c>Rendering</c> at least reached one per second.
    /// So both callers of it in the shared code are dead on Linux:
    /// <see cref="VisualUtilities.QueueCallbackForCompositionRendered"/> never runs its callback,
    /// and <c>Collections/SynchronizedList</c> — the collection the chat history is built on —
    /// never flushes on a frame.
    ///
    /// <para><b>The approximation, and why it is honest.</b> "The frame is on the glass" cannot be
    /// observed on Uno: its Skia render loop exposes no post-present callback, and the one event
    /// that would have been it is the one that does not fire. What is reproducible is the
    /// <i>budget</i>: this clock dispatches a handler on the <b>second</b> tick of
    /// <see cref="CompositionRenderingClock"/> after the one it subscribed on, so at least one full
    /// frame period (1/<see cref="CompositionRenderingClock.FrameRate"/>, 16.7 ms) has gone by —
    /// and with Uno's render loop at 60 Hz that is at least one composed frame. Dispatching on the
    /// <i>next</i> tick would not do: a subscription that lands microseconds before a tick boundary
    /// would be called back with no frame in between at all, and that is exactly the case that
    /// matters (a removal handled at the tail of a frame).</para>
    ///
    /// <para><b>Why one frame's worth of time is enough for the two callers.</b>
    /// <list type="bullet">
    /// <item><description><c>SynchronizedList</c> holds a removal back so that the
    /// <c>CompositionVisualSurface</c> armed in <c>ISynchronizedListDelegate.Capturing</c> — while
    /// the row is still realized — has a commit to capture at before the row leaves the list. That
    /// is a "wait one frame" requirement, not a "read back the presented pixels" one, and the list
    /// does not lean on the trigger for correctness: it re-checks its count against the source on
    /// every flush and resyncs on a mismatch, so an approximate trigger can cost latency and
    /// nothing else. It already carries a 100 ms <c>DispatcherTimer</c> fallback for the case WinUI
    /// itself cannot cover (an occluded window presents no frames), which is what has been flushing
    /// the queue on Linux so far — this clock takes that from 100 ms back down to ~17-33 ms and
    /// puts the dust burst back on the frame the bubble actually leaves on.</description></item>
    /// <item><description><c>QueueCallbackForCompositionRendered</c> callers want "run this once the
    /// change I just made has settled": <c>ContentPopup</c> completing the task its
    /// <c>ShowQueuedAsync</c> awaits, <c>MainPage</c> assigning <c>ChatsList.SelectedItem</c>
    /// outside the layout pass that is running, the popups re-measuring their input to the maximum.
    /// None of them reads <c>RenderedEventArgs</c> (Unigram cannot even marshal it — see the note
    /// on <c>EventHandler&lt;object&gt;</c> in <c>CompositionTargetImpl</c>), and none can tell
    /// "presented" from "a frame later". What they cannot survive is the callback not running at
    /// all: a dropped <c>ContentPopup.Test</c> leaves every dialog on the view awaiting
    /// forever.</description></item>
    /// </list></para>
    ///
    /// <para><b>Where it is deliberately more generous than WinUI.</b> WinUI raises nothing while
    /// no frame is produced (minimized, occluded); this ticks as long as somebody is subscribed.
    /// Both callers want the callback to run in that case — it is the reason the list has a
    /// fallback timer at all — so erring that way is the safe direction.</para>
    ///
    /// Subscription contract is the one <see cref="CompositionRenderingClock"/> already
    /// established, so each shared call site changes by one line: handlers run on the thread that
    /// subscribed, nothing ticks with no subscriber, and a handler may unsubscribe itself from
    /// inside the callback (which is what every caller here does).
    /// </summary>
    public static class CompositionRenderedClock
    {
        /// <summary>
        /// Ticks a handler has to sit through before it is dispatched. Two, not one: see the
        /// paragraph on the approximation above — one would let a handler that subscribed just
        /// before a tick boundary be called back with no frame period in between.
        /// </summary>
        private const long Delay = 2;

        [ThreadStatic]
        private static Clock _current;

        private static Clock Current => _current ??= new Clock();

        public static event EventHandler<object> Rendered
        {
            add => Current.Add(value);
            remove => Current.Remove(value);
        }

        private sealed class Clock
        {
            // A list rather than a multicast delegate: each handler carries the tick it subscribed
            // on, which is the whole point of this class.
            private readonly List<Entry> _handlers = new();

            // Reused across ticks. Dispatch cannot reenter — a timer tick does not fire inside
            // another one — and this runs on one thread by construction.
            private readonly List<Entry> _due = new();

            private long _tick;
            private bool _running;

            public void Add(EventHandler<object> handler)
            {
                if (handler == null)
                {
                    return;
                }

                _handlers.Add(new Entry(handler, _tick));

                if (!_running)
                {
                    _running = true;

                    // One timer per UI thread for both clocks: a frame is a frame, and expressing
                    // "after the frame" as "a tick of the frame clock later" is what keeps the two
                    // in step. It also inherits the rendering clock's start/stop, so nothing ticks
                    // while neither clock has a subscriber.
                    CompositionRenderingClock.Rendering += OnRendering;
                }
            }

            public void Remove(EventHandler<object> handler)
            {
                if (handler == null)
                {
                    return;
                }

                // Last match, like -= on a multicast delegate. Nothing subscribes the same handler
                // twice, but the shape should not be the thing that surprises someone who does.
                for (int i = _handlers.Count - 1; i >= 0; i--)
                {
                    if (_handlers[i].Handler == handler)
                    {
                        _handlers.RemoveAt(i);
                        break;
                    }
                }

                Idle();
            }

            private void Idle()
            {
                if (_running && _handlers.Count == 0)
                {
                    _running = false;
                    CompositionRenderingClock.Rendering -= OnRendering;
                }
            }

            private void OnRendering(object sender, object e)
            {
                _tick++;

                // Snapshot: a handler is free to unsubscribe itself — or anything else — from
                // inside its callback, which is what every caller of this does. Removals are
                // honoured mid-dispatch by re-checking membership, additions wait for a tick, and
                // both match what the multicast delegate of the event this replaces does.
                _due.Clear();

                for (int i = 0; i < _handlers.Count; i++)
                {
                    if (_tick - _handlers[i].Tick >= Delay)
                    {
                        _due.Add(_handlers[i]);
                    }
                }

                for (int i = 0; i < _due.Count; i++)
                {
                    var entry = _due[i];

                    if (!_handlers.Contains(entry))
                    {
                        continue;
                    }

                    try
                    {
                        // Same arguments CompositionTarget passes: no sender, and args no caller
                        // is allowed to touch.
                        entry.Handler(null, null);
                    }
                    catch (Exception ex)
                    {
                        // One broken handler must not take the clock down with it: the chat
                        // history's removals and every queued dialog callback hang on this.
                        Logger.Error("CompositionRenderedClock", ex);
                    }
                }

                // Not left populated between ticks: it would root the delegate — and through it the
                // popup or the chat view it closes over — for a frame after the last unsubscribe.
                _due.Clear();

                Idle();
            }

            private readonly struct Entry : IEquatable<Entry>
            {
                public readonly EventHandler<object> Handler;

                /// <summary>
                /// Value of the tick counter when this handler subscribed.
                /// </summary>
                public readonly long Tick;

                public Entry(EventHandler<object> handler, long tick)
                {
                    Handler = handler;
                    Tick = tick;
                }

                public bool Equals(Entry other)
                {
                    return Handler == other.Handler && Tick == other.Tick;
                }

                public override bool Equals(object obj)
                {
                    return obj is Entry other && Equals(other);
                }

                public override int GetHashCode()
                {
                    return HashCode.Combine(Handler, Tick);
                }
            }
        }
    }
}
