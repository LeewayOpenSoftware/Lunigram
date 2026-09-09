//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.IO;
using Telegram.Navigation;
using Telegram.Td.Api;
using Windows.Storage;

namespace Telegram.Services
{
    /// <summary>
    /// The half of the activation that knows about Unigram: turns what the desktop asked for — a
    /// <c>tg://</c> link, a desktop action, a theme file — into navigation.
    ///
    /// <para><see cref="SingleInstance"/> deliberately knows none of this; it speaks URIs and
    /// action names so it can be exercised from a console. Everything below runs on the window's
    /// dispatcher, because that is the only thread that may touch a page.</para>
    ///
    /// <para>The URI itself needs no work: <c>MainPage.Activate(string)</c> and
    /// <c>MessageHelper.OpenTelegramUrl</c> are the same portable code that handles a link tapped
    /// inside a chat, and they already understand every <c>tg://</c> and <c>t.me</c> form. What is
    /// new on Linux is only how the string gets here.</para>
    /// </summary>
    public static class DesktopActivator
    {
        private static bool _attached;

        /// <summary>
        /// Starts answering activations, and replays the one this process was started with — the
        /// <c>tg://</c> link that made the launcher start Unigram in the first place arrives
        /// before there is a window, and has been waiting since.
        /// </summary>
        public static void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;
            SingleInstance.Current.Attach(OnActivation);

            ApplyLaunchMinimized();
        }

        /// <summary>
        /// Honours the <c>--minimized</c> of the autostart entry, which is what
        /// <c>SettingsService.IsLaunchMinimized</c> writes there. Only the command line is read, so
        /// a launch the user asked for by hand always shows a window whatever the setting says.
        /// </summary>
        private static void ApplyLaunchMinimized()
        {
            if (!SingleInstance.Current.LaunchMinimized)
            {
                return;
            }

            var window = WindowContext.Main;
            if (window == null)
            {
                return;
            }

            window.Dispatcher.Dispatch(() =>
            {
                if (DesktopIntegration.TryHideWindow(window))
                {
                    Logger.Info("Started minimized to the tray");
                }
                else
                {
                    // Hiding a window with nothing to bring it back from is how an app gets lost.
                    Logger.Warning("--minimized with no tray icon: leaving the window up");
                }
            });
        }

        private static void OnActivation(DesktopActivation activation)
        {
            Logger.Info(activation.ToString());

            var window = WindowContext.Main ?? WindowContext.Active;
            if (window == null)
            {
                Logger.Warning("Activation with no window");
                return;
            }

            // The token is what lets the compositor treat this as a handover rather than as an app
            // stealing focus behind the user's back. Set before the window is raised, and only for
            // this process: it is single use and the next launcher will send another.
            if (!string.IsNullOrEmpty(activation.Token))
            {
                try
                {
                    Environment.SetEnvironmentVariable("XDG_ACTIVATION_TOKEN", activation.Token);
                    Environment.SetEnvironmentVariable("DESKTOP_STARTUP_ID", activation.Token);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Cannot set the activation token: {ex.Message}");
                }
            }

            // Every activation means "put me in front", whatever else it carries. That alone is
            // what a second click on the dock icon does, and what makes the app feel like one.
            DesktopIntegration.ShowWindow();

            if (activation.Action != null)
            {
                window.Dispatcher.Dispatch(() => Invoke(window, activation.Action));
                return;
            }

            foreach (var uri in activation.Uris)
            {
                var value = uri;
                window.Dispatcher.Dispatch(() => Open(window, value));
            }
        }

        private static void Invoke(WindowContext window, string action)
        {
            try
            {
                if (string.Equals(action, SingleInstance.SavedMessagesAction, StringComparison.Ordinal))
                {
                    OpenSavedMessages(window);
                }
                else
                {
                    Logger.Warning($"Unknown desktop action {action}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Desktop action {action} failed", ex);
            }
        }

        /// <summary>
        /// The Linux jump list, which on Windows is the single item
        /// <c>ContactsService.JumpListAsync</c> puts there. Saved Messages is the private chat with
        /// oneself, so the id is the account's own — asked for rather than looked up, because the
        /// chat may not be in the cache when the app was started by this very click.
        /// </summary>
        private static async void OpenSavedMessages(WindowContext window)
        {
            var session = LifetimeService.Current.ActiveItem;
            var clientService = session?.Resolve<IClientService>();

            if (clientService == null || clientService.Options.MyId == 0)
            {
                Logger.Warning("Saved Messages before there is an account");
                return;
            }

            var response = await clientService.SendAsync(new CreatePrivateChat(clientService.Options.MyId, false));
            if (response is Chat chat)
            {
                // Through the same string a notification or a tg:// link would use, so there is
                // one way in and not two.
                window.ActivateArguments($"chat_id={chat.Id}");
            }
            else
            {
                Logger.Warning($"Cannot open Saved Messages: {response}");
            }
        }

        private static void Open(WindowContext window, string uri)
        {
            try
            {
                if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(uri, UriKind.Absolute, out var file)
                    && file.IsFile)
                {
                    OpenFile(file.LocalPath);
                    return;
                }

                window.ActivateArguments(uri);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot open the activation uri", ex);
            }
        }

        /// <summary>
        /// A file handed over by the file manager. Only <c>.unigram-theme</c> is claimed in the
        /// desktop entry, and it goes to the same importer the in-app theme list uses.
        /// </summary>
        private static async void OpenFile(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".unigram-theme", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning($"Nothing to do with {Path.GetFileName(path)}");
                return;
            }

            try
            {
                var session = LifetimeService.Current.ActiveItem;
                var themeService = session?.Resolve<IThemeService>();

                if (themeService == null)
                {
                    return;
                }

                var file = await StorageFile.GetFileFromPathAsync(path);
                await themeService.InstallThemeAsync(file);
            }
            catch (Exception ex)
            {
                // Windows shows ThemePreviewPopup first and lets the user say no; that popup is
                // outside the Linux subset, so this applies the theme straight away. Worth
                // revisiting when the popup comes in.
                Logger.Error($"Cannot install {Path.GetFileName(path)}", ex);
            }
        }
    }
}
