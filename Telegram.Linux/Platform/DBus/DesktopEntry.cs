//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using SkiaSharp;

namespace Telegram.Services
{
    /// <summary>
    /// The <c>.desktop</c> file and the icons that go with it.
    ///
    /// <para>On Windows all of this is the MSIX manifest: the app identity, the icon the taskbar
    /// and the toasts use, the <c>tg://</c> protocol registration. On Linux the equivalent is a
    /// text file in <c>~/.local/share/applications</c>, and it is not decoration — two of the three
    /// pieces of this phase do not work without it:</para>
    /// <list type="bullet">
    /// <item>The launcher counter (<c>com.canonical.Unity.LauncherEntry</c>) addresses the app as
    /// <c>application://&lt;desktop file id&gt;</c>. No file, no badge.</item>
    /// <item>The notification server uses the <c>desktop-entry</c> hint to show the app name and
    /// icon on the banner, and GNOME uses it to group the notifications of one app.</item>
    /// <item><c>StartupWMClass</c> is what makes the dock match the running X11 window to this
    /// entry; the window's <c>WM_CLASS</c> is <c>Unigram</c> (measured, see PORTING.md).</item>
    /// </list>
    ///
    /// <para>Writing it is idempotent and skipped when the contents already match, so a normal
    /// start does no I/O. <c>UNIGRAM_NO_DESKTOP_ENTRY=1</c> turns it off entirely for anyone who
    /// installs the file themselves.</para>
    /// </summary>
    public static class DesktopEntry
    {
        /// <summary>Desktop file id without the extension. Also the icon name and the app id.</summary>
        public const string Id = "org.unigram.linux";

        /// <summary>What goes in the <c>Icon=</c> key and in the notification's <c>app_icon</c>.</summary>
        public const string IconName = Id;

        /// <summary>The <c>application://</c> URI the launcher protocol identifies the app with.</summary>
        public const string ApplicationUri = "application://" + Id + ".desktop";

        /// <summary>The file name of the entry, both in <c>applications</c> and in <c>autostart</c>.</summary>
        public const string FileName = Id + ".desktop";

        /// <summary>Themes exported by Unigram. Registered so a double click on one opens the app.</summary>
        public const string ThemeMimeType = "application/x-unigram-theme";

        /// <summary>
        /// The types this app claims. The two schemes are what <c>tg://</c> links in a browser and
        /// <c>tonsite://</c> links in a chat need; the third is the exported theme file.
        /// </summary>
        public static readonly string[] MimeTypes =
        {
            "x-scheme-handler/tg",
            "x-scheme-handler/tonsite",
            ThemeMimeType
        };

        private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128 };

        private static bool _ensured;

        /// <summary>
        /// Writes the desktop entry and the icons if they are missing or out of date. Never throws:
        /// a read-only home costs the badge, not the app.
        /// </summary>
        public static void Ensure()
        {
            if (_ensured)
            {
                return;
            }

            _ensured = true;

            if (string.Equals(Environment.GetEnvironmentVariable("UNIGRAM_NO_DESKTOP_ENTRY"), "1", StringComparison.Ordinal))
            {
                Logger.Info("UNIGRAM_NO_DESKTOP_ENTRY is set, leaving ~/.local/share alone");
                return;
            }

            try
            {
                var applications = Path.Combine(DataHome, "applications");
                Directory.CreateDirectory(applications);

                var path = Path.Combine(applications, FileName);
                var written = WriteIfChanged(path, Build());

                EnsureIcons();

                // The bus has to know how to START the app, not only how to reach it once it is
                // up. Without this file a tg:// link clicked while Unigram is closed is a D-Bus
                // call to a name nobody owns, and GLib reports the launch as failed rather than
                // falling back to Exec.
                written |= EnsureServiceFile();

                // The theme type does not exist in the shared MIME database, so it is declared
                // before anything is associated with it: an association to an unknown type is
                // silently dropped.
                var mime = EnsureMimePackage();
                written |= mime;

                written |= EnsureAssociations();

                if (written)
                {
                    // Both caches are read by the file manager and the browser, and neither is
                    // rebuilt on its own. Only when something actually changed: a normal start
                    // writes nothing and spawns nothing.
                    Run("update-desktop-database", applications);

                    if (mime)
                    {
                        Run("update-mime-database", Path.Combine(DataHome, "mime"));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot write the desktop entry", ex);
            }
        }

        /// <summary>
        /// Writes only when the contents differ, and says whether it did. Everything here is
        /// idempotent so a normal start does no I/O and rebuilds no cache.
        /// </summary>
        internal static bool WriteIfChanged(string path, string contents)
        {
            try
            {
                if (File.Exists(path) && string.Equals(File.ReadAllText(path), contents, StringComparison.Ordinal))
                {
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, contents);

                Logger.Info($"Wrote {path}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Cannot write {path}", ex);
                return false;
            }
        }

        private static void Run(string command, string argument)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(command)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                info.ArgumentList.Add(argument);

                using var process = System.Diagnostics.Process.Start(info);
                if (process == null)
                {
                    return;
                }

                // Bounded: these are cache rebuilds, and a desktop where one of them hangs must
                // not be a desktop where Unigram does not start.
                if (!process.WaitForExit(5000))
                {
                    Logger.Warning($"{command} did not finish, leaving it running");
                }
            }
            catch (Exception ex)
            {
                // Not installed is the normal case on a minimal system: the entry is still there,
                // only the cache is stale until something else rebuilds it.
                Logger.Warning($"Cannot run {command}: {ex.Message}");
            }
        }

        internal static string DataHome
        {
            get
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
                {
                    return xdg;
                }

                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            }
        }

        /// <summary>
        /// The command that starts this very build, quoted for the <c>Exec=</c> key. When the app
        /// runs through the framework-dependent host the process is <c>dotnet</c> and the assembly
        /// has to come with it, which is exactly the shape a developer runs it in today.
        /// </summary>
        public static string ExecCommand()
        {
            var process = Environment.ProcessPath;
            var assembly = Assembly.GetEntryAssembly()?.Location;

            if (!string.IsNullOrEmpty(process)
                && !string.IsNullOrEmpty(assembly)
                && string.Equals(Path.GetFileName(process), "dotnet", StringComparison.Ordinal))
            {
                return Quote(process) + " " + Quote(assembly);
            }

            // Started through the apphost. That binary finds the runtime through the environment,
            // and the one environment it is NOT started with is the bus's: a D-Bus activation
            // inherits almost nothing, so the apphost of a framework-dependent build whose .NET
            // lives outside /usr/share/dotnet — ~/.dotnet, which is this machine — dies with "You
            // must install .NET", exit 131, before a single line of Unigram runs. Measured in
            // unigram-linux/spikes/ActivationSpike. The muxer needs no environment at all, so it is
            // preferred whenever there is one to point at; a self-contained build has none, and
            // does not need one.
            var muxer = Muxer();
            if (muxer != null && !string.IsNullOrEmpty(assembly))
            {
                return Quote(muxer) + " " + Quote(assembly);
            }

            if (!string.IsNullOrEmpty(process))
            {
                return Quote(process);
            }

            return "unigram";
        }

        /// <summary>
        /// The <c>dotnet</c> that is hosting this process, or null when there is none to point at
        /// (a self-contained build, where the runtime sits in the app's own directory).
        /// </summary>
        private static string Muxer()
        {
            try
            {
                // .../shared/Microsoft.NETCore.App/10.0.x/  ->  .../dotnet
                var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
                if (string.IsNullOrEmpty(runtime))
                {
                    return null;
                }

                var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
                var muxer = Path.Combine(root, "dotnet");

                return File.Exists(muxer) ? muxer : null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Cannot locate the dotnet muxer: {ex.Message}");
                return null;
            }
        }

        private static string Quote(string value)
        {
            if (value.IndexOfAny(new[] { ' ', '\t', '"', '\'', '\\' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        internal static string Build()
        {
            var builder = new StringBuilder();
            builder.Append("[Desktop Entry]\n");
            builder.Append("Type=Application\n");
            builder.Append("Version=1.5\n");
            builder.Append("Name=Unigram\n");
            builder.Append("GenericName=Telegram client\n");
            builder.Append("Comment=Fast and secure messaging\n");
            builder.Append("Exec=").Append(ExecCommand()).Append(" %u\n");
            builder.Append("Icon=").Append(IconName).Append('\n');
            builder.Append("Terminal=false\n");
            builder.Append("StartupNotify=true\n");
            // The dock matches the running window to this entry by WM_CLASS, and Uno gives the
            // X11 window the class "Unigram" (PORTING.md, "Raton"). Without this the counter has
            // nothing to sit on.
            builder.Append("StartupWMClass=Unigram\n");
            builder.Append("Categories=Network;InstantMessaging;Chat;\n");
            builder.Append("MimeType=").Append(string.Join(';', MimeTypes)).Append(";\n");
            builder.Append("Keywords=telegram;chat;im;messaging;messenger;sms;tdlib;\n");
            builder.Append("X-GNOME-UsesNotifications=true\n");

            // What replaces the UWP application model: the launcher calls org.freedesktop.Application
            // on the running process instead of spawning a second one, and the tg:// link arrives as
            // an Open() rather than as argv of a new instance. See Platform/DBus/SingleInstance.cs.
            builder.Append("DBusActivatable=true\n");

            // The Linux jump list. On Windows this is ContactsService.JumpListAsync, one item
            // pointing at Saved Messages; here it is a Desktop Action, which the dock shows on the
            // icon's context menu and GNOME on the app grid entry.
            builder.Append("Actions=").Append(SingleInstance.SavedMessagesAction).Append(";\n");
            builder.Append('\n');
            builder.Append("[Desktop Action ").Append(SingleInstance.SavedMessagesAction).Append("]\n");
            builder.Append("Name=").Append(Sanitize(Strings.SavedMessages)).Append('\n');
            builder.Append("Icon=").Append(IconName).Append('\n');
            // The spec makes Exec optional once DBusActivatable is set, because the action arrives
            // as ActivateAction(). It is written anyway for the launcher that does not do D-Bus,
            // and --action is parsed by the same code that reads the D-Bus name.
            builder.Append("Exec=").Append(ExecCommand()).Append(" --action=").Append(SingleInstance.SavedMessagesAction).Append('\n');

            return builder.ToString();
        }

        /// <summary>
        /// A desktop entry value is one line. The localized strings come from the .resw files and
        /// have never had a newline in them, but a value that did would silently corrupt the file.
        /// </summary>
        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\r", string.Empty).Replace("\n", " ").Trim();
        }

        /// <summary>
        /// The D-Bus service file, which is what makes <c>DBusActivatable=true</c> work from a cold
        /// start: it tells the bus which command owns <see cref="SingleInstance.BusName"/>.
        /// </summary>
        internal static bool EnsureServiceFile()
        {
            var path = Path.Combine(DataHome, "dbus-1", "services", SingleInstance.BusName + ".service");

            var builder = new StringBuilder();
            builder.Append("[D-BUS Service]\n");
            builder.Append("Name=").Append(SingleInstance.BusName).Append('\n');
            builder.Append("Exec=").Append(ExecCommand()).Append('\n');

            return WriteIfChanged(path, builder.ToString());
        }

        /// <summary>
        /// Declares <c>application/x-unigram-theme</c> in the user's share of the shared MIME
        /// database. Windows gets this from the MSIX manifest's file type association.
        /// </summary>
        internal static bool EnsureMimePackage()
        {
            var path = Path.Combine(DataHome, "mime", "packages", Id + ".xml");

            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            builder.Append("<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">\n");
            builder.Append("  <mime-type type=\"").Append(ThemeMimeType).Append("\">\n");
            builder.Append("    <comment>Unigram theme</comment>\n");
            builder.Append("    <glob pattern=\"*.unigram-theme\"/>\n");
            builder.Append("    <sub-class-of type=\"text/plain\"/>\n");
            builder.Append("  </mime-type>\n");
            builder.Append("</mime-info>\n");

            return WriteIfChanged(path, builder.ToString());
        }

        /// <summary>
        /// Makes this entry the default for the types above, by adding the keys that are missing
        /// from <c>mimeapps.list</c>.
        ///
        /// <para>Only the missing ones. This is the user's own file and it is shared by every app
        /// on the machine, so an association that already names somebody else — another Telegram
        /// client, a browser — is left exactly as it is: registering a handler is worth doing,
        /// taking one away from an app the user chose is not. Everything outside the
        /// <c>[Default Applications]</c> group is copied through byte for byte.</para>
        /// </summary>
        internal static bool EnsureAssociations()
        {
            const string Group = "[Default Applications]";

            try
            {
                var path = Path.Combine(ConfigHome, "mimeapps.list");
                var lines = File.Exists(path)
                    ? new List<string>(File.ReadAllLines(path))
                    : new List<string>();

                var start = lines.FindIndex(x => string.Equals(x.Trim(), Group, StringComparison.Ordinal));
                if (start < 0)
                {
                    if (lines.Count > 0 && lines[^1].Length > 0)
                    {
                        lines.Add(string.Empty);
                    }

                    lines.Add(Group);
                    start = lines.Count - 1;
                }

                // The group ends at the next group header, or at the end of the file.
                var end = lines.Count;
                for (var i = start + 1; i < lines.Count; i++)
                {
                    if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
                    {
                        end = i;
                        break;
                    }
                }

                var added = 0;

                foreach (var type in MimeTypes)
                {
                    var claimed = false;

                    for (var i = start + 1; i < end; i++)
                    {
                        var separator = lines[i].IndexOf('=');
                        if (separator > 0 && string.Equals(lines[i].Substring(0, separator).Trim(), type, StringComparison.Ordinal))
                        {
                            claimed = true;

                            if (lines[i].IndexOf(FileName, StringComparison.Ordinal) < 0)
                            {
                                Logger.Info($"{type} already belongs to {lines[i].Substring(separator + 1).Trim()}, leaving it");
                            }

                            break;
                        }
                    }

                    if (!claimed)
                    {
                        lines.Insert(end++, type + "=" + FileName);
                        added++;
                    }
                }

                if (added == 0)
                {
                    return false;
                }

                File.WriteAllText(path, string.Join('\n', lines).TrimEnd('\n') + "\n");
                Logger.Info($"Registered {added} mime association(s) in {path}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot register the mime associations", ex);
                return false;
            }
        }

        internal static string ConfigHome
        {
            get
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
                {
                    return xdg;
                }

                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
        }

        private static void EnsureIcons()
        {
            var source = TrayIcons.DecodeIco(ReadIcon());
            if (source == null)
            {
                return;
            }

            using (source)
            {
                foreach (var size in IconSizes)
                {
                    var directory = Path.Combine(DataHome, "icons", "hicolor", $"{size}x{size}", "apps");
                    var path = Path.Combine(directory, IconName + ".png");

                    if (File.Exists(path))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(directory);

                    using var scaled = source.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    if (scaled == null)
                    {
                        continue;
                    }

                    using var image = SKImage.FromBitmap(scaled);
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                    using var stream = File.Create(path);

                    encoded.SaveTo(stream);
                }
            }
        }

        private static byte[] ReadIcon()
        {
            try
            {
                var path = Native.NativeAssets.Resolve(TrayIcons.FileName(TrayIconState.Default), null);
                return path == null ? null : File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot read the app icon", ex);
                return null;
            }
        }
    }
}
