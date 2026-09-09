//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Microsoft.UI.Xaml;

namespace Telegram.Common
{
    /// <summary>
    /// Linux replacement for <c>CompositionTarget.Rendering</c>, the per-frame callback every
    /// animation of Unigram is driven from (AnimatedImageLoader, CompositionVSync,
    /// VisualUtilities.QueueCallbackForCompositionRendering, the tilt effect...).
    ///
    /// WinUI raises that event once per composed frame, at the display refresh rate. Uno's Skia
    /// backend raises it about **once a second** — measured on this port with a counter next to the
    /// subscription: 1 to 2 ticks per second, and it does not move no matter what invalidates the
    /// tree (a Background swap, an Opacity change), nor with
    /// <c>FeatureConfiguration.CompositionTarget.FrameRate</c> / <c>X11HostBuilder.RenderFrameRate</c>
    /// (both already 60). It is not the compositor that is slow: the window does repaint on demand
    /// — the same measurement showed the process going from 18% to 41% CPU when frames were pushed
    /// at 60 Hz instead of 1 Hz — it is only this event that is not a frame clock on Uno.
    ///
    /// The consequence was that every animation advanced one frame per second, which looks exactly
    /// like "the animations are frozen". This clock is what those subscriptions use instead: a UI
    /// thread <see cref="DispatcherTimer"/> ticking at <see cref="FrameRate"/>, with the same
    /// subscribe/unsubscribe shape as the event it replaces, so the shared code changes by one line
    /// per call site.
    ///
    /// Semantics kept from <c>CompositionTarget.Rendering</c>:
    ///  - handlers run on the thread that subscribed (the timer is per thread, like the WinUI event
    ///    and like AnimatedImageLoader.Current),
    ///  - nothing ticks while there is no subscriber,
    ///  - a handler may unsubscribe itself from inside the callback (that is exactly what
    ///    VisualUtilities.QueueCallbackForCompositionRendering does).
    ///
    /// The sibling event, <c>CompositionTarget.Rendered</c>, is worse off on Uno — it is never
    /// raised at all — and has its own replacement next door in
    /// <see cref="CompositionRenderedClock"/>, which is driven by this clock: "after the frame"
    /// is expressed as a tick of this one later.
    /// </summary>
    public static class CompositionRenderingClock
    {
        /// <summary>
        /// Ticks per second. 60 = the refresh rate of every panel this has run on, and the cap
        /// AnimatedImagePresentation already assumes (LimitFps clamps between 30 and 60). The
        /// clock is shared, so the fastest animation sets the pace and the slower ones skip ticks
        /// on their own, exactly as they do under vsync on Windows.
        /// </summary>
        public const double FrameRate = 60;

        [ThreadStatic]
        private static Clock _current;

        private static Clock Current => _current ??= new Clock();

        public static event EventHandler<object> Rendering
        {
            add => Current.Add(value);
            remove => Current.Remove(value);
        }

        private sealed class Clock
        {
            private readonly DispatcherTimer _timer;
            private EventHandler<object> _handlers;

            public Clock()
            {
                _timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1 / FrameRate)
                };

                _timer.Tick += OnTick;
            }

            public void Add(EventHandler<object> handler)
            {
                _handlers += handler;

                if (_handlers != null)
                {
                    _timer.Start();
                }
            }

            public void Remove(EventHandler<object> handler)
            {
                _handlers -= handler;

                if (_handlers == null)
                {
                    _timer.Stop();
                }
            }

            private void OnTick(object sender, object e)
            {
                // The multicast delegate is immutable, so this is a snapshot: a handler that
                // unsubscribes itself here still receives this tick and none of the following,
                // which is what the WinUI event does too.
                var handlers = _handlers;
                if (handlers == null)
                {
                    _timer.Stop();
                    return;
                }

                try
                {
                    // Both arguments are what CompositionTarget passes: no sender, and args that
                    // callers are told not to touch (see the comment in CompositionVSync).
                    handlers(null, null);
                }
                catch (Exception ex)
                {
                    // One broken handler must not take the clock down with it: every animation on
                    // screen hangs on this timer.
                    Logger.Error("CompositionRenderingClock", ex);
                }
            }
        }
    }
}
