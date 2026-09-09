//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.IO;
using System.Text;

namespace Telegram.Services
{
    /// <summary>
    /// "Launch Unigram when the system starts", which on Windows is a UWP <c>StartupTask</c>
    /// declared in the manifest and switched on with <c>RequestEnableAsync</c>.
    ///
    /// <para>On Linux it is a second <c>.desktop</c> file, in <c>~/.config/autostart</c>, read by
    /// the session manager (gnome-session here) when it starts the session. Two differences the
    /// UI has to live with, both visible in <c>Controls/StartupSwitch.xaml.cs</c>:</para>
    /// <list type="bullet">
    /// <item>There is no <c>DisabledByUser</c> state. Windows lets the user veto a startup task
    /// from Settings and the app can only report it; here the file is the whole truth, so the
    /// switch is never disabled and <c>Strings.AutoStartDisabledInfo</c> never applies.</item>
    /// <item>"Start minimized" is an argument on the command line rather than a flag the app reads
    /// back from the platform, because that is the one thing a D-Bus activation cannot carry: the
    /// session manager runs <c>Exec</c>, so the switch writes <c>--minimized</c> into it.</item>
    /// </list>
    ///
    /// <para>Deliberately not <c>DBusActivatable</c>, unlike the entry in
    /// <c>~/.local/share/applications</c>: an activation has no argv, and this is the one launch
    /// that needs one.</para>
    /// </summary>
    public static class AutoStart
    {
        private const string MinimizedArgument = "--minimized";

        /// <summary>The autostart entry of this app, whether or not it exists.</summary>
        public static string Path => System.IO.Path.Combine(DesktopEntry.ConfigHome, "autostart", DesktopEntry.FileName);

        /// <summary>Whether the app starts with the session.</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    var contents = Read();
                    if (contents == null)
                    {
                        return false;
                    }

                    // GNOME's own switch does not delete the file, it writes this key. Honouring it
                    // is what keeps the two in agreement instead of each undoing the other.
                    return !Contains(contents, "X-GNOME-Autostart-enabled=false")
                        && !Contains(contents, "Hidden=true");
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot read the autostart entry", ex);
                    return false;
                }
            }
        }

        /// <summary>Whether the autostart entry asks for a window that starts out of the way.</summary>
        public static bool IsMinimized
        {
            get
            {
                try
                {
                    var contents = Read();
                    return contents != null && contents.Contains(MinimizedArgument, StringComparison.Ordinal);
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot read the autostart entry", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// Writes (or rewrites) the entry. Answers whether the file is now what was asked for, so
        /// the switch can put itself back if a read-only home refused it.
        /// </summary>
        public static bool Enable(bool minimized)
        {
            try
            {
                DesktopEntry.WriteIfChanged(Path, Build(minimized));
                return IsEnabled;
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot enable autostart", ex);
                return false;
            }
        }

        /// <summary>Removes the entry. A missing file is already disabled, so this is idempotent.</summary>
        public static bool Disable()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                    Logger.Info($"Removed {Path}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot disable autostart", ex);
                return false;
            }
        }

        private static string Read()
        {
            var path = Path;
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static bool Contains(string contents, string key)
        {
            return contents.Contains(key, StringComparison.OrdinalIgnoreCase);
        }

        private static string Build(bool minimized)
        {
            var builder = new StringBuilder();
            builder.Append("[Desktop Entry]\n");
            builder.Append("Type=Application\n");
            builder.Append("Version=1.5\n");
            builder.Append("Name=Unigram\n");
            builder.Append("Comment=Fast and secure messaging\n");
            builder.Append("Exec=").Append(DesktopEntry.ExecCommand());

            if (minimized)
            {
                builder.Append(' ').Append(MinimizedArgument);
            }

            builder.Append('\n');
            builder.Append("Icon=").Append(DesktopEntry.IconName).Append('\n');
            builder.Append("Terminal=false\n");
            builder.Append("StartupWMClass=Unigram\n");
            builder.Append("X-GNOME-Autostart-enabled=true\n");

            // A session that starts the app before the tray is up would leave a window that cannot
            // be hidden. Two seconds is what the GNOME autostart entries of other tray apps use.
            builder.Append("X-GNOME-Autostart-Delay=2\n");

            return builder.ToString();
        }
    }
}
