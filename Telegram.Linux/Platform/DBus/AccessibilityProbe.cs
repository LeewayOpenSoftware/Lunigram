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
    /// One-run, measured record of whether anything is listening for accessibility events on this
    /// machine, so that nobody builds a screen-reader-facing feature on
    /// <c>AutomationPeer.RaiseNotificationEvent</c> (already used by
    /// <c>Telegram.Controls.ToastPopup.Announce</c> and
    /// <c>Telegram.Charts.BaseChartView.AnnounceSelection</c>) believing it reaches one here.
    ///
    /// <para>It does not, and this probe cannot make it otherwise report: <c>strings</c> over the
    /// shipped <c>Uno.UI.Runtime.Skia.X11.dll</c> (measured 2026-09-05, see
    /// <c>unigram-linux/review/ATSPI-SCOPE.md</c>) turns up zero accessibility strings -- no
    /// AT-SPI, no <c>org.a11y</c> D-Bus, no <c>AccessibilityImpl</c>. Uno's peer layer
    /// (<see cref="Microsoft.UI.Xaml.Automation.Peers.AutomationPeer"/>) is platform-agnostic and
    /// keeps compiling and running everywhere; its Skia announcer just has no bridge to hand
    /// anything to on this host, unlike the Win32 Skia head which does.</para>
    ///
    /// <para>What THIS logs is the other half, and it is measured rather than assumed: whether an
    /// AT-SPI registry (<c>org.a11y.Bus</c>) is even reachable on the session bus of the machine
    /// this build happens to run on right now. Either answer ends the same way -- nothing gets
    /// announced -- but a machine that DOES have one running is the more surprising case, and the
    /// one where a future reader would most plausibly (and wrongly) assume the call above is
    /// doing something.</para>
    /// </summary>
    public static class AccessibilityProbe
    {
        private const string Service = "org.a11y.Bus";
        private const string ObjectPath = "/org/a11y/bus";
        private const string Interface = "org.a11y.Bus";

        private static int _ran;

        /// <summary>
        /// Safe to call from anywhere, as many times as wanted -- only the first call does
        /// anything, the rest are no-ops. Fire-and-forget: this is a log line, not a feature, and
        /// nothing here should ever be worth blocking startup over.
        /// </summary>
        public static void RunOnce()
        {
            if (Interlocked.Exchange(ref _ran, 1) != 0)
            {
                return;
            }

            _ = LogAsync();
        }

        private static async Task LogAsync()
        {
            const string noBridge = "AccessibilityAnnouncer is a no-op on this build's Skia/X11 backend regardless (Uno.UI.Runtime.Skia.X11.dll ships no AT-SPI bridge -- see unigram-linux/review/ATSPI-SCOPE.md). Do not build screen-reader-facing behaviour on RaiseNotificationEvent reaching a screen reader here.";

            try
            {
                if (!DBusSession.IsConfigured)
                {
                    Logger.Warning($"AT-SPI probe: no session bus at all. {noBridge}");
                    return;
                }

                var connection = await DBusSession.ConnectAsync().ConfigureAwait(false);
                if (connection == null)
                {
                    Logger.Warning($"AT-SPI probe: could not connect to the session bus. {noBridge}");
                    return;
                }

                var address = await connection.CallMethodAsync(Compose(connection),
                    static (Message message, object _) => message.GetBodyReader().ReadString()).ConfigureAwait(false);

                Logger.Warning($"AT-SPI probe: {Service} IS reachable on the session bus (address: {address}). {noBridge}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"AT-SPI probe: {Service} is not reachable ({ex.Message}). {noBridge}");
            }
        }

        private static MessageBuffer Compose(DBusConnection connection)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(Service, ObjectPath, Interface, "GetAddress");
            return writer.CreateMessage();
        }
    }
}
