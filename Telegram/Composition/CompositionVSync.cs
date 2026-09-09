//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Diagnostics;

namespace Telegram.Composition
{
    public partial class CompositionVSync
    {
        // Stopwatch ticks rather than the frame's RenderingTime: reading that means casting the
        // event args to RenderingEventArgs, and the args cross the ABI as a bare IInspectable, so
        // on .NET 9+ the cast is a QueryInterface and a new wrapper on every frame, per subscriber.
        // The args are passed as object for a reason.
        private readonly long _interval;
        private long _elapsed;

        public CompositionVSync(double framerate)
        {
            _interval = (long)(Stopwatch.Frequency / framerate);
        }

        private event EventHandler _rendering;
        public event EventHandler Rendering
        {
            add
            {
                if (_rendering == null)
                {
#if LINUX
                    // Uno's CompositionTarget.Rendering is a frame counter, not a frame clock: its
                    // rate follows whatever happens to be invalidating the window, so an animation
                    // that draws from it is its own metronome. See
                    // Telegram.Linux/Xaml/CompositionRenderingClock.cs.
                    Common.CompositionRenderingClock.Rendering += OnRendering;
#else
                    CompositionTarget.Rendering += OnRendering;
#endif
                }

                _rendering += value;
            }
            remove
            {
                _rendering -= value;

                if (_rendering == null)
                {
#if LINUX
                    Common.CompositionRenderingClock.Rendering -= OnRendering;
#else
                    CompositionTarget.Rendering -= OnRendering;
#endif
                }
            }
        }

        private void OnRendering(object sender, object e)
        {
            var timestamp = Stopwatch.GetTimestamp();

            if (timestamp - _elapsed < _interval)
            {
                return;
            }

            _elapsed = timestamp;
            _rendering?.Invoke(sender, EventArgs.Empty);
        }
    }
}
