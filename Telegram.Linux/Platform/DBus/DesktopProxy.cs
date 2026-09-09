//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Telegram.Services
{
    /// <summary>One proxy, in the terms of the desktop and not of TDLib.</summary>
    public sealed class DesktopProxySettings
    {
        public DesktopProxySettings(bool socks, string server, int port, string username, string password)
        {
            IsSocks = socks;
            Server = server;
            Port = port;
            Username = username ?? string.Empty;
            Password = password ?? string.Empty;
        }

        /// <summary>SOCKS5 when true, HTTP (CONNECT) when false. Those are the two TDLib can use.</summary>
        public bool IsSocks { get; }

        public string Server { get; }

        public int Port { get; }

        public string Username { get; }

        public string Password { get; }

        public override string ToString()
        {
            return $"{(IsSocks ? "socks5" : "http")}://{(Username.Length > 0 ? "***@" : string.Empty)}{Server}:{Port}";
        }
    }

    /// <summary>
    /// The proxy the desktop is configured with, and a way to hear about it changing.
    ///
    /// <para>On Windows this is the registry through WinHTTP, watched by
    /// <c>HttpProxyWatcher</c>. On Linux the environment variables the phase 1 port already read
    /// (<c>https_proxy</c> and friends) are only half the story, and the smaller half: a GNOME
    /// desktop keeps its proxy in GSettings, under <c>org.gnome.system.proxy</c>, and a session
    /// started from the display manager exports nothing. So a user who set a proxy in the Settings
    /// app had "use system proxy" do nothing at all.</para>
    ///
    /// <para>The values are read by running <c>gsettings list-recursively</c> once — the whole tree,
    /// children included, in one process. The alternative was P/Invoking libgio, and
    /// <c>g_settings_new</c> on a schema that is not installed does not fail, it calls
    /// <c>g_error</c> and <b>aborts the process</b>: a desktop without
    /// gsettings-desktop-schemas would take Unigram down with it. Reading is also rare — once when
    /// a session is created, and again when the setting changes.</para>
    ///
    /// <para>The change itself does come over D-Bus: dconf announces every write as
    /// <c>ca.desrt.dconf.Writer.Notify</c>, so there is no polling and no helper process.</para>
    /// </summary>
    public static class DesktopProxy
    {
        private const string Schema = "org.gnome.system.proxy";

        /// <summary>The dconf path of the tree above. Signals are filtered on it.</summary>
        private const string DconfPath = "/system/proxy/";

        private const string WriterService = "ca.desrt.dconf";
        private const string WriterPath = "/ca/desrt/dconf/Writer/user";
        private const string WriterInterface = "ca.desrt.dconf.Writer";

        private static bool _watching;

        /// <summary>
        /// The proxy the desktop is configured with, or false when there is none, when the mode is
        /// automatic (a PAC script, which TDLib cannot evaluate) or when this is not a GNOME
        /// desktop.
        /// </summary>
        public static bool TryGet(out DesktopProxySettings proxy)
        {
            proxy = null;

            var settings = Read();
            if (settings == null)
            {
                return false;
            }

            return TryParse(settings, out proxy);
        }

        /// <summary>
        /// The whole subtree as <c>gsettings</c> prints it, or null when there is no such schema.
        /// Separate from <see cref="TryParse"/> so the parsing can be exercised against desktops
        /// that are not this one.
        /// </summary>
        public static string Read()
        {
            try
            {
                var info = new ProcessStartInfo("gsettings")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                info.ArgumentList.Add("list-recursively");
                info.ArgumentList.Add(Schema);

                using var process = Process.Start(info);
                if (process == null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd();

                if (!process.WaitForExit(5000))
                {
                    Logger.Warning("gsettings did not answer, ignoring the system proxy");
                    return null;
                }

                if (process.ExitCode != 0)
                {
                    // No schema: not a GNOME desktop, or the desktop schemas are not installed.
                    // Normal, and the environment variables are still there to fall back on.
                    Logger.Info($"No {Schema} on this desktop");
                    return null;
                }

                return output;
            }
            catch (Exception ex)
            {
                Logger.Info($"Cannot read {Schema}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Turns the output of <c>gsettings list-recursively org.gnome.system.proxy</c> into the
        /// one proxy TDLib can be given.
        ///
        /// <para>The order is socks, then https, then http, and it is not arbitrary: Telegram's
        /// traffic is not HTTP, so a SOCKS5 proxy carries it as it is while an HTTP one has to
        /// tunnel it with CONNECT. GNOME's own dialog writes the same host into all of them when
        /// "use the same proxy for all protocols" is ticked, so the order only decides between
        /// proxies the user set apart on purpose. Authentication lives on the http child alone —
        /// the https and socks children have no keys for it — which is also how GLib reads
        /// it.</para>
        /// </summary>
        public static bool TryParse(string settings, out DesktopProxySettings proxy)
        {
            proxy = null;

            if (string.IsNullOrEmpty(settings))
            {
                return false;
            }

            var values = Parse(settings);

            if (!values.TryGetValue(Schema + " mode", out var mode))
            {
                return false;
            }

            if (mode == "none")
            {
                return false;
            }

            if (mode == "auto")
            {
                // A PAC script. TDLib takes a host and a port, not a JavaScript function, and
                // guessing one out of the other would be a proxy the user did not ask for.
                Logger.Info("The desktop proxy is automatic (PAC), which TDLib cannot use");
                return false;
            }

            if (mode != "manual")
            {
                Logger.Warning($"Unknown proxy mode {mode}");
                return false;
            }

            var user = string.Empty;
            var password = string.Empty;

            if (values.TryGetValue(Schema + ".http use-authentication", out var authenticate) && authenticate == "true")
            {
                values.TryGetValue(Schema + ".http authentication-user", out user);
                values.TryGetValue(Schema + ".http authentication-password", out password);
            }

            if (TryEndpoint(values, ".socks", out var host, out var port))
            {
                // The socks child has no credentials of its own, and reusing the http ones on a
                // different server would be handing them to somebody else.
                proxy = new DesktopProxySettings(true, host, port, string.Empty, string.Empty);
                return true;
            }

            if (TryEndpoint(values, ".https", out host, out port)
                || TryEndpoint(values, ".http", out host, out port))
            {
                proxy = new DesktopProxySettings(false, host, port, user, password);
                return true;
            }

            Logger.Info("The desktop proxy is manual but no host is set");
            return false;
        }

        private static bool TryEndpoint(Dictionary<string, string> values, string child, out string host, out int port)
        {
            host = null;
            port = 0;

            if (!values.TryGetValue(Schema + child + " host", out var value) || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!values.TryGetValue(Schema + child + " port", out var number)
                || !int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port <= 0
                || port > 65535)
            {
                return false;
            }

            host = value.Trim();
            return host.Length > 0;
        }

        /// <summary>
        /// One line of <c>gsettings list-recursively</c> is "schema key value", where the value is
        /// in GVariant text form. Only the scalar forms are unwrapped; the one list in the tree
        /// (ignore-hosts) is not used, and is left as written.
        /// </summary>
        private static Dictionary<string, string> Parse(string settings)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var line in settings.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var schema = trimmed.IndexOf(' ');
                if (schema <= 0)
                {
                    continue;
                }

                var key = trimmed.IndexOf(' ', schema + 1);
                if (key <= 0)
                {
                    continue;
                }

                values[trimmed.Substring(0, key)] = Unquote(trimmed.Substring(key + 1));
            }

            return values;
        }

        private static string Unquote(string value)
        {
            value = value.Trim();

            if (value.Length < 2 || value[0] != '\'' || value[^1] != '\'')
            {
                return value;
            }

            var inner = value.Substring(1, value.Length - 2);

            // GVariant's text form escapes the quote and the backslash, and nothing else appears in
            // a host name or a password.
            return inner.Replace("\\'", "'").Replace("\\\\", "\\");
        }

        #region Watching

        /// <summary>
        /// Calls back when the desktop's proxy changes. Once per process; failures are logged and
        /// cost the watch, not the proxy.
        /// </summary>
        public static async Task WatchAsync(Action changed)
        {
            if (_watching || changed == null)
            {
                return;
            }

            _watching = true;

            await DBusSession.AddRegistrarAsync(async connection =>
            {
                await connection.AddMatchAsync(
                    new MatchRule
                    {
                        Type = MessageType.Signal,
                        Sender = WriterService,
                        Path = WriterPath,
                        Interface = WriterInterface,
                        Member = "Notify"
                    },
                    static (Message message, object _) =>
                    {
                        var reader = message.GetBodyReader();
                        var prefix = reader.ReadString();
                        var changes = reader.ReadArrayOfString();

                        return AffectsProxy(prefix, changes);
                    },
                    static (Exception ex, bool affects, object _, object state) =>
                    {
                        if (ex == null && affects && state is Action callback)
                        {
                            try
                            {
                                callback();
                            }
                            catch (Exception failure)
                            {
                                // The D-Bus reader thread: an exception here takes the connection
                                // down, and with it notifications and the tray icon.
                                Logger.Error("The proxy change handler failed", failure);
                            }
                        }
                    },
                    ObserverFlags.None, null, changed, false).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether a dconf notification touches the proxy tree. dconf sends a common prefix and the
        /// keys below it, and it is free to split them wherever it likes — measured in
        /// unigram-linux/spikes/ActivationSpike: a single key arrives as the whole path with an
        /// <b>empty</b> array of changes (<c>/unigram-spike/probe []</c>), not as the directory
        /// plus the key name, while a write to several keys at once names the directory and lists
        /// them. So the test is that the two paths overlap in either direction, not that one starts
        /// with the other, and the empty-array case has to be handled on its own.
        /// </summary>
        public static bool AffectsProxy(string prefix, string[] changes)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return false;
            }

            if (changes == null || changes.Length == 0)
            {
                return Overlaps(prefix, DconfPath);
            }

            foreach (var change in changes)
            {
                if (Overlaps(prefix + (change ?? string.Empty), DconfPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Overlaps(string path, string root)
        {
            return path.StartsWith(root, StringComparison.Ordinal)
                || root.StartsWith(path, StringComparison.Ordinal);
        }

        #endregion
    }
}
