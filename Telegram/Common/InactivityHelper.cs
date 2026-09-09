//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading;
using Telegram.Native;

namespace Telegram.Common
{
    public static class InactivityHelper
    {
        private static Timer _timer;
        private static int _timeout = 60 * 1000;

        private static uint _lastTime = uint.MinValue;

        public static void Initialize(int timeout)
        {
            if (ApiInfo.IsDesktop)
            {
                if (timeout > 0)
                {
                    _timeout = timeout * 1000;

                    if (_timer != null)
                    {
                        _timer.Change(0, 1000);
                    }
                    else
                    {
                        _timer = new Timer(OnTick, null, 0, 1000);
                    }
                }
                else
                {
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        }

#if LINUX
        // There is no GetLastInputInfo here: the idle time comes from the compositor, already
        // counted, and it resets to zero on the next keypress. So instead of remembering the last
        // input timestamp to fire once, the episode itself is the latch: raised on the tick that
        // crosses the timeout, and armed again by any activity. Same event, same once per absence.
        // See Telegram.Linux/Platform/DBus/IdleMonitor.cs.
        private static bool _detected;

        private static void OnTick(object state)
        {
            var idleTime = Services.IdleMonitor.IdleMilliseconds;

            if (idleTime >= _timeout)
            {
                if (!_detected)
                {
                    _detected = true;
                    Detected?.Invoke(null, EventArgs.Empty);
                }
            }
            else
            {
                _detected = false;
            }
        }

        public static event EventHandler Detected;

        public static bool IsActive => Services.IdleMonitor.IdleMilliseconds < 60 * 1000;
#else
        private static void OnTick(object state)
        {
            var lastInput = NativeUtils.GetLastInputTime();
            var idleTime = Environment.TickCount - lastInput;

            if (idleTime >= _timeout && _lastTime < lastInput)
            {
                _lastTime = lastInput;
                Detected?.Invoke(null, EventArgs.Empty);
            }
        }

        public static event EventHandler Detected;

        public static bool IsActive
        {
            get
            {
                var lastInput = NativeUtils.GetLastInputTime();
                var idleTime = Environment.TickCount - lastInput;

                if (idleTime >= 60 * 1000)
                {
                    return false;
                }

                return true;
            }
        }
#endif
    }
}
