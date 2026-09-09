//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Telegram.Navigation;

namespace Telegram.Services
{
    /// <summary>
    /// Everything the desktop shows of Unigram when its window is not in front: the tray icon and
    /// its menu, the unread counter on the launcher, and the desktop entry the other two are
    /// addressed through.
    ///
    /// <para>On Windows this is spread across three places — <c>BridgeApplicationContext</c> talking
    /// to the <c>Telegram.Stub</c> process over an <c>AppServiceConnection</c>,
    /// <c>BadgeUpdateManager</c>, and the MSIX manifest. Here it is one static object inside the
    /// app, because on Linux all three are D-Bus and D-Bus needs no helper process.</para>
    ///
    /// <para>Nothing here is required for the app to work: with no session bus, no notification
    /// server or no tray host, every method logs and returns. The one visible difference is that
    /// closing the window then quits instead of hiding.</para>
    /// </summary>
    public static class DesktopIntegration
    {
        private const int MenuOpen = 1;
        private const int MenuSeparator = 2;
        private const int MenuQuit = 3;

        private static bool _initialized;
        private static bool _trayStarted;
        private static bool _quitting;

        /// <summary>Whether the tray icon is up, and therefore whether closing hides the window.</summary>
        public static bool IsTrayActive { get; private set; }

        /// <summary>
        /// Raised when a notification is clicked or one of its buttons is pressed. Handled by
        /// <see cref="NotificationsService"/>, which is the half that knows about chats.
        /// <para>Raised on the D-Bus reader thread.</para>
        /// </summary>
        public static event EventHandler<DesktopNotificationActivatedEventArgs> NotificationActivated;

        /// <summary>
        /// Writes the desktop entry, connects to the notification server and puts the tray icon up.
        /// Safe to call more than once and safe to call off the UI thread.
        /// </summary>
        public static async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            try
            {
                // The launcher counter and the notification's app name both address the app
                // through this file, so it goes first.
                DesktopEntry.Ensure();

                // Before the first await of this method, and that is not incidental: the media
                // transport captures the dispatcher of the thread it is started on, and everything
                // the media keys end up calling has to run there. After the ConfigureAwait(false)
                // below there is no UI thread left to capture.
                await MediaTransport.InitializeAsync().ConfigureAwait(false);

                DBusMenu.TextDirection = LocaleService.Current.FlowDirection == FlowDirection.RightToLeft ? "rtl" : "ltr";

                DesktopNotifications.Current.Activated += OnNotificationActivated;
                await DesktopNotifications.Current.InitializeAsync().ConfigureAwait(false);

                if (AppSettings.IsTrayVisible)
                {
                    await InitializeTrayAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Desktop integration failed to start", ex);
            }

            // Last, because it replays the tg:// link this process may have been started with and
            // because starting minimized needs the tray icon to already be up — but OUTSIDE the
            // try, because a notification server that refuses to talk must not swallow the link
            // the user clicked to get here. See Platform/DBus/DesktopActivator.cs.
            DesktopActivator.Attach();
        }

        /// <summary>
        /// The "show tray icon" checkbox of Settings &gt; Advanced. On Windows the same switch
        /// launches or kills <c>Telegram.Stub</c>; here there is no second process, so the icon
        /// goes Passive and comes back Active. <see cref="IsTrayActive"/> follows it, because it is
        /// what decides whether closing the window may hide it: with no icon on the panel a hidden
        /// window has no way back.
        /// </summary>
        public static async Task SetTrayVisibleAsync(bool visible)
        {
            try
            {
                if (visible && !_trayStarted)
                {
                    await InitializeTrayAsync().ConfigureAwait(false);
                    return;
                }

                if (!_trayStarted)
                {
                    return;
                }

                await StatusNotifierItem.Current.SetVisibleAsync(visible).ConfigureAwait(false);
                IsTrayActive = visible;
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot change the tray icon visibility", ex);
            }
        }

        private static async Task InitializeTrayAsync()
        {
            _trayStarted = true;

            var item = StatusNotifierItem.Current;

            item.Menu.Add(new DBusMenuItem(MenuOpen, Strings.NotifyIconOpen, ShowWindow));
            item.Menu.Add(new DBusMenuItem(MenuSeparator, null, null) { IsSeparator = true });
            item.Menu.Add(new DBusMenuItem(MenuQuit, Strings.NotifyIconExit, Quit));

            item.Activated += (s, e) => ShowWindow();

            await item.SetToolTipAsync(Strings.AppName, string.Empty).ConfigureAwait(false);

            IsTrayActive = await item.RegisterAsync().ConfigureAwait(false);
        }

        private static void OnNotificationActivated(object sender, DesktopNotificationActivatedEventArgs e)
        {
            if (e.Action != DesktopNotificationAction.MarkAsRead)
            {
                // Reply and a plain click both mean "put me in that chat": on GNOME there is no
                // text field on the banner, and even on KDE the user may have clicked the body.
                ShowWindow();
            }

            NotificationActivated?.Invoke(sender, e);
        }

        /// <summary>
        /// Brings the window back, from the tray or from a notification. Can be called from any
        /// thread — the window can only be touched from its dispatcher.
        /// </summary>
        public static void ShowWindow()
        {
            var window = WindowContext.Main ?? WindowContext.Active;
            if (window == null)
            {
                return;
            }

            window.Dispatcher.Dispatch(() =>
            {
                try
                {
                    window.Show();
                    window.Activate();
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot bring the window back", ex);
                }
            });
        }

        /// <summary>
        /// "Close to tray": the window goes away, the process stays. Answers false when there is no
        /// tray icon to bring it back from, and then the caller must let the close happen — a
        /// hidden window with no way back is a lost app.
        /// </summary>
        public static bool TryHideWindow(WindowContext window)
        {
            if (_quitting || !IsTrayActive || window == null || !window.IsInMainView)
            {
                return false;
            }

            try
            {
                return window.Hide();
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot hide the window", ex);
                return false;
            }
        }

        /// <summary>Quit from the tray menu: the close is not intercepted this time.</summary>
        public static void Quit()
        {
            _quitting = true;

            var window = WindowContext.Main ?? WindowContext.Active;
            if (window == null)
            {
                Environment.Exit(0);
                return;
            }

            window.Dispatcher.Dispatch(async () =>
            {
                try
                {
                    await DesktopNotifications.Current.CloseAllAsync();
                    await LauncherEntry.SetCountAsync(0);
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot clean up before quitting", ex);
                }

                try
                {
                    Application.Current?.Exit();
                }
                catch (Exception ex)
                {
                    // Quit is the one tray action that must always work: if it did not, the window
                    // would be hidden with no way to reach the app but killing it from a terminal.
                    Logger.Error("Application.Exit failed, leaving the hard way", ex);
                    Environment.Exit(0);
                }
            });
        }

        /// <summary>
        /// The tray icon state, from the same two booleans the Windows bridge sent to the stub:
        /// plain when there is nothing unread, the grey dot when everything unread is muted, the
        /// red dot otherwise. The number itself goes to the launcher, not here.
        /// </summary>
        public static async Task SetTrayStateAsync(bool unread, bool unmuted)
        {
            if (!IsTrayActive)
            {
                return;
            }

            try
            {
                var state = unmuted
                    ? TrayIconState.Unmuted
                    : unread
                    ? TrayIconState.Muted
                    : TrayIconState.Default;

                await StatusNotifierItem.Current.SetStateAsync(state).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot update the tray icon", ex);
            }
        }
    }
}
