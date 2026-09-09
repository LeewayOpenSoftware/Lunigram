//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;

namespace Telegram.Common
{
    public enum DesktopTheme
    {
        /// <summary>
        /// Nobody answered, or the desktop has no preference: the caller decides on its own.
        /// </summary>
        Unknown,
        Light,
        Dark
    }

    /// <summary>
    /// The colour scheme the desktop asks applications to use, read before there is a window to
    /// theme - the same trick <see cref="DisplayScale"/> plays with Xft.dpi.
    /// </summary>
    /// <remarks>
    /// Uno's X11 host learns the scheme from xdg-desktop-portal over DBus, and that answer only
    /// lands seconds after startup: until it does <c>UISettings</c> reports Light, so on a dark
    /// desktop the window was born Light and flipped to Dark once the portal replied. Asking the
    /// portal ourselves - out of process, so no DBus stack has to be initialized first - costs
    /// about ten milliseconds and the read is started from Program.Main, which is well before the
    /// window is created, so by the time the theme is seeded the answer is already here.
    ///
    /// Sources, in order: the portal (org.freedesktop.appearance color-scheme, the cross-desktop
    /// setting), then GSettings (org.gnome.desktop.interface color-scheme, what GNOME itself
    /// stores and what the portal reads on this desktop). Neither answering is not an error - it
    /// means this desktop does not publish a preference, and the caller falls back to whatever it
    /// did before. UNIGRAM_COLOR_SCHEME=dark|light|none overrides the lot (none = behave as if
    /// nothing answered, which is the old behaviour).
    /// </remarks>
    public static class DesktopColorScheme
    {
        /// <summary>
        /// How long a single query may take. gdbus and gsettings answer in about ten milliseconds,
        /// so this is only here to make sure a portal that never answers is dropped rather than
        /// waited for.
        /// </summary>
        private const int QueryTimeout = 500;

        /// <summary>
        /// How long the first caller may wait for the read started at startup. It has been running
        /// since Program.Main, so in practice it finished long ago and nothing waits at all; this
        /// caps what a wedged desktop can add to the launch. Giving up is not losing the answer -
        /// the read goes on in the background and the next caller gets it.
        /// </summary>
        private const int FirstWait = 600;

        /// <summary>
        /// How long a caller may wait for a re-read after <see cref="Invalidate"/>. That only
        /// happens when the desktop says its colours changed, and the answer is needed to paint
        /// the new theme, so a short block is better than repainting twice.
        /// </summary>
        private const int RefreshWait = 300;

        private static readonly object _lock = new();

        private static Task<DesktopTheme> _reading;
        private static Task<DesktopTheme> _waited;
        private static DesktopTheme _value;
        private static bool _known;

        /// <summary>
        /// Starts reading the scheme in the background. Call it as early as possible - the point
        /// is that the answer is already here when the first window is created.
        /// </summary>
        public static void BeginRead()
        {
            Start();
        }

        /// <summary>
        /// Drops the cached answer and starts reading again. Call it when the desktop reports a
        /// colour change: the value it changed to is exactly what this reads.
        /// </summary>
        public static void Invalidate()
        {
            lock (_lock)
            {
                _reading = null;
            }

            Start();
        }

        /// <summary>
        /// The scheme the desktop asks for, or <see cref="DesktopTheme.Unknown"/> when nothing
        /// answered. Blocks at most once per read, and never longer than <see cref="FirstWait"/>:
        /// a desktop that does not answer costs that once, not on every call, and a slow re-read
        /// gives back the previous answer rather than nothing.
        /// </summary>
        public static DesktopTheme Current
        {
            get
            {
                Task<DesktopTheme> reading;
                bool known;

                lock (_lock)
                {
                    reading = Start();

                    if (reading.IsCompleted)
                    {
                        return Complete(reading);
                    }

                    if (_waited == reading)
                    {
                        // Somebody already spent the budget waiting for this one and it is still
                        // running. It will be picked up above, by whoever asks after it lands.
                        return _known ? _value : DesktopTheme.Unknown;
                    }

                    _waited = reading;
                    known = _known;
                }

                try
                {
                    if (reading.Wait(known ? RefreshWait : FirstWait))
                    {
                        lock (_lock)
                        {
                            return Complete(reading);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Read swallows its own failures, so this is the wait itself going wrong.
                    Logger.Error(ex.ToString());
                }

                // Still reading: the last answer, or Unknown when there has not been one yet.
                lock (_lock)
                {
                    return _known ? _value : DesktopTheme.Unknown;
                }
            }
        }

        // Caller holds _lock.
        private static DesktopTheme Complete(Task<DesktopTheme> reading)
        {
            if (reading.IsCompletedSuccessfully)
            {
                var result = reading.Result;

                // A read that found nothing does not overwrite an answer we already had: the
                // desktop did not stop having a preference, the query just failed this time.
                if (result != DesktopTheme.Unknown)
                {
                    _value = result;
                    _known = true;
                }
            }

            return _known ? _value : DesktopTheme.Unknown;
        }

        private static Task<DesktopTheme> Start()
        {
            lock (_lock)
            {
                return _reading ??= Task.Run(Read);
            }
        }

        private static DesktopTheme Read()
        {
            try
            {
                var forced = Environment.GetEnvironmentVariable("UNIGRAM_COLOR_SCHEME");
                if (!string.IsNullOrEmpty(forced))
                {
                    // Set at all means it decides: anything that is not a scheme (none, default,
                    // 0, off) turns the whole thing off and leaves the old behaviour in place.
                    return FromWord(forced);
                }

                var portal = ReadPortal();
                if (portal != DesktopTheme.Unknown)
                {
                    return portal;
                }

                return ReadGSettings();
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
                return DesktopTheme.Unknown;
            }
        }

        private static DesktopTheme ReadPortal()
        {
            // (<<uint32 1>>,) - 0 no preference, 1 dark, 2 light. Read is the interface every
            // portal has; ReadOne is its replacement and answers (<uint32 1>,), same parsing.
            var output = Query("gdbus", "call", "--session", "--timeout", "1",
                "--dest", "org.freedesktop.portal.Desktop",
                "--object-path", "/org/freedesktop/portal/desktop",
                "--method", "org.freedesktop.portal.Settings.Read",
                "org.freedesktop.appearance", "color-scheme");

            var theme = FromPortal(output);
            if (theme != DesktopTheme.Unknown)
            {
                return theme;
            }

            output = Query("gdbus", "call", "--session", "--timeout", "1",
                "--dest", "org.freedesktop.portal.Desktop",
                "--object-path", "/org/freedesktop/portal/desktop",
                "--method", "org.freedesktop.portal.Settings.ReadOne",
                "org.freedesktop.appearance", "color-scheme");

            return FromPortal(output);
        }

        private static DesktopTheme ReadGSettings()
        {
            // 'prefer-dark' | 'prefer-light' | 'default'
            var theme = FromWord(Query("gsettings", "get", "org.gnome.desktop.interface", "color-scheme"));
            if (theme != DesktopTheme.Unknown)
            {
                return theme;
            }

            // 'default' on a desktop that predates the setting (or never sets it) still says what
            // it wants through the name of the theme it runs: Yaru-dark, Adwaita-dark, Mint-Y-Dark.
            var name = Query("gsettings", "get", "org.gnome.desktop.interface", "gtk-theme");
            if (name != null && name.Contains("dark", StringComparison.OrdinalIgnoreCase))
            {
                return DesktopTheme.Dark;
            }

            return DesktopTheme.Unknown;
        }

        private static DesktopTheme FromPortal(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return DesktopTheme.Unknown;
            }

            var index = output.IndexOf("uint32", StringComparison.Ordinal);
            if (index < 0)
            {
                return DesktopTheme.Unknown;
            }

            var value = output.AsSpan(index + 6).TrimStart();

            var length = 0;
            while (length < value.Length && char.IsAsciiDigit(value[length]))
            {
                length++;
            }

            if (length == 0 || !int.TryParse(value[..length], NumberStyles.None, CultureInfo.InvariantCulture, out int scheme))
            {
                return DesktopTheme.Unknown;
            }

            return scheme switch
            {
                1 => DesktopTheme.Dark,
                2 => DesktopTheme.Light,
                _ => DesktopTheme.Unknown
            };
        }

        private static DesktopTheme FromWord(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return DesktopTheme.Unknown;
            }

            if (value.Contains("dark", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1")
            {
                return DesktopTheme.Dark;
            }
            else if (value.Contains("light", StringComparison.OrdinalIgnoreCase) || value.Trim() == "2")
            {
                return DesktopTheme.Light;
            }

            return DesktopTheme.Unknown;
        }

        /// <summary>
        /// Runs a command and returns its output, or null if it is not installed, fails, or takes
        /// longer than <see cref="QueryTimeout"/>.
        /// </summary>
        private static string Query(string fileName, params string[] arguments)
        {
            try
            {
                var info = new ProcessStartInfo(fileName)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var argument in arguments)
                {
                    info.ArgumentList.Add(argument);
                }

                using var process = Process.Start(info);
                if (process == null)
                {
                    return null;
                }

                // Read while it runs: a pipe that fills up would deadlock the wait, and the error
                // stream has to be drained for the same reason even though nothing reads it.
                var output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(QueryTimeout))
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                        // Already gone.
                    }

                    return null;
                }

                if (process.ExitCode != 0)
                {
                    return null;
                }

                return output.Wait(QueryTimeout) ? output.Result : null;
            }
            catch (Exception ex)
            {
                // Not installed (Win32Exception), no session bus, sandboxed: all just mean this
                // source has no answer.
                Logger.Warning(ex.Message);
                return null;
            }
        }
    }
}
