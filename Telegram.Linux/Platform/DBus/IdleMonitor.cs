//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Telegram.Services
{
    /// <summary>
    /// How long the user has been away from the machine, which is what the passcode's auto-lock
    /// counts.
    ///
    /// <para>On Windows this is <c>GetLastInputInfo</c> (through
    /// <c>Telegram.Native.NativeUtils.GetLastInputTime</c>), a call that answers for the whole
    /// session. There is no such call here: an X11 client can only see the input delivered to its
    /// own windows, and under Wayland not even that — the compositor decides what a client is told
    /// and never mentions input that went somewhere else. So the number has to come from the
    /// compositor, and on GNOME the compositor publishes it:
    /// <c>org.gnome.Mutter.IdleMonitor.GetIdletime</c>, in milliseconds, counting keyboard,
    /// pointer and touch across every window of the session, Wayland and XWayland alike.</para>
    ///
    /// <para>The value is polled once a second and handed out from a field, because the caller
    /// (<c>Common/InactivityHelper.cs</c>) is synchronous and already ticks at that rate, and
    /// because the threshold it compares against is a minute at the very least: a sample that is
    /// up to a second old cannot change the answer. Polling is also what keeps this reconnect-proof
    /// without a watch to re-arm — <c>AddIdleWatch</c> would be one call instead of sixty, and one
    /// piece of state to lose every time gnome-shell restarts.</para>
    ///
    /// <para>Without the service (KDE, a plain window manager, no bus) the idle time is reported as
    /// zero, which reads as "the user is here" and leaves the app unlocked. That is the same
    /// behaviour the phase 1 stub had, and it is the safe direction to fail in for a feature whose
    /// other outcome is locking somebody out of their session's messages by accident.</para>
    /// </summary>
    public static class IdleMonitor
    {
        private const string Service = "org.gnome.Mutter.IdleMonitor";
        private const string ObjectPath = "/org/gnome/Mutter/IdleMonitor/Core";
        private const string Interface = "org.gnome.Mutter.IdleMonitor";

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

        private static Timer _timer;
        private static long _idle;
        private static int _polling;
        private static bool _started;
        private static bool _available;
        private static bool _reported;

        /// <summary>
        /// Milliseconds since the last input anywhere in the session, or 0 when the desktop does
        /// not publish it. Up to one second stale by design.
        /// </summary>
        public static long IdleMilliseconds
        {
            get
            {
                Start();
                return Interlocked.Read(ref _idle);
            }
        }

        /// <summary>
        /// Whether the number above is coming from the desktop. False before the first successful
        /// poll, so it is a report and not a precondition.
        /// </summary>
        public static bool IsAvailable => _available;

        /// <summary>
        /// Begins polling. Called on its own by the first read, so nothing has to remember to start
        /// it, and cheap to call again.
        /// </summary>
        public static void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;

            if (!DBusSession.IsConfigured)
            {
                Logger.Warning("No session bus: the idle time is unknown and auto-lock will not fire");
                return;
            }

            _timer = new Timer(OnTick, null, TimeSpan.Zero, Interval);
        }

        /// <summary>Stops polling. The last sample stays readable.</summary>
        public static void Stop()
        {
            var timer = Interlocked.Exchange(ref _timer, null);
            timer?.Dispose();

            _started = false;
        }

        private static void OnTick(object state)
        {
            // A bus that is slow to answer must not queue a poll per second behind the one in
            // flight: one sample outstanding at a time, and a missed tick just means the previous
            // number stands for another second.
            if (Interlocked.Exchange(ref _polling, 1) == 1)
            {
                return;
            }

            _ = PollAsync();
        }

        private static async Task PollAsync()
        {
            try
            {
                var connection = await DBusSession.ConnectAsync().ConfigureAwait(false);
                if (connection == null)
                {
                    return;
                }

                var idle = await connection.CallMethodAsync(Compose(connection),
                    static (Message message, object _) => message.GetBodyReader().ReadUInt64()).ConfigureAwait(false);

                Interlocked.Exchange(ref _idle, (long)idle);

                if (!_available)
                {
                    _available = true;
                    Logger.Info($"Idle time from {Service} ({idle} ms right now)");
                }
            }
            catch (Exception ex)
            {
                if (!_reported)
                {
                    _reported = true;

                    // Said once. A desktop that is not GNOME simply does not have this, and a line
                    // a second would bury the log for the rest of the session.
                    Logger.Warning($"No {Service}: the idle time is unknown and auto-lock will not fire ({ex.Message})");
                }

                // Not zeroing what was already measured: an answer that stops arriving is a reason
                // to stop trusting the number, not a reason to claim the user just touched the
                // machine.
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        private static MessageBuffer Compose(DBusConnection connection)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(Service, ObjectPath, Interface, "GetIdletime");
            return writer.CreateMessage();
        }
    }
}
