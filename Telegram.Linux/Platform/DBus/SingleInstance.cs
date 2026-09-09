//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Telegram.Services
{
    /// <summary>What the desktop asked the app to do when it activated it.</summary>
    public sealed class DesktopActivation
    {
        public DesktopActivation(string action, string[] uris, string token)
        {
            Action = action;
            Uris = uris ?? Array.Empty<string>();
            Token = token;
        }

        /// <summary>Name of the desktop action (a <c>[Desktop Action …]</c> group), or null for a plain activation.</summary>
        public string Action { get; }

        /// <summary>The URIs to open. Empty for a plain activation.</summary>
        public string[] Uris { get; }

        /// <summary>
        /// The XDG activation token the launcher handed over, so raising the window is a handover
        /// and not focus stealing. Null when the launcher gave none.
        /// </summary>
        public string Token { get; }

        public override string ToString()
        {
            if (Action != null)
            {
                return $"action {Action}";
            }

            return Uris.Length == 0 ? "activate" : $"open {Uris.Length} uri(s)";
        }
    }

    /// <summary>
    /// One running Unigram per session, and the way the desktop talks to the one that is running.
    ///
    /// <para>On Windows both come free with the UWP application model: the platform never starts a
    /// second instance, and it delivers <c>tg://</c> links, jump list items and file activations to
    /// the live one as <c>IActivatedEventArgs</c>. On Linux nothing is free — a second
    /// <c>./Unigram</c> is simply a second process, and every launcher, browser and file manager
    /// starts one. The equivalent is a well known bus name plus the
    /// <c>org.freedesktop.Application</c> interface: whoever owns <c>org.unigram.linux</c> is the
    /// app, and everybody else forwards and exits.</para>
    ///
    /// <para>This runs at the very top of <c>Main</c>, before Uno builds anything, for two reasons:
    /// a second instance must cost a bus round trip and not a XAML host, and the D-Bus name has to
    /// be owned before the bus delivers the <c>Open</c> that started us — when the launcher
    /// activates the app through D-Bus the bus spawns the process and queues the call until the
    /// name appears. Activations that arrive before there is a window are kept in
    /// <see cref="_pending"/> and replayed by <see cref="Attach"/>.</para>
    ///
    /// <para>No session bus means no single instance: the app runs. That is the same trade the rest
    /// of <see cref="DBusSession"/> makes, and the alternative — refusing to start — would make a
    /// TTY or a broken bus address fatal.</para>
    /// </summary>
    public sealed class SingleInstance : IPathMethodHandler
    {
        /// <summary>The bus name, which is also the desktop file id and the application id of the csproj.</summary>
        public const string BusName = DesktopEntry.Id;

        /// <summary>The object path the spec derives from the name: dots become slashes.</summary>
        public const string ObjectPath = "/org/unigram/linux";

        private const string Interface = "org.freedesktop.Application";

        /// <summary>The desktop action that opens Saved Messages, the Windows jump list's only item.</summary>
        public const string SavedMessagesAction = "saved-messages";

        public static SingleInstance Current { get; } = new SingleInstance();

        private readonly object _lock = new();
        private readonly List<DesktopActivation> _pending = new();

        private Action<DesktopActivation> _handler;

        private SingleInstance()
        {
        }

        /// <summary>
        /// Whether this process owns the name. False only when there is no bus: an instance that
        /// did not get the name has already exited by the time anybody can ask.
        /// </summary>
        public bool IsPrimary { get; private set; } = true;

        /// <summary>Whether the name is actually owned, as opposed to "there is no bus to own it on".</summary>
        public bool IsRegistered { get; private set; }

        /// <summary>Set by <c>--minimized</c>, which is what the autostart entry passes.</summary>
        public bool LaunchMinimized { get; private set; }

        /// <summary>
        /// Takes the name if it is free, and otherwise hands this launch to the instance that has
        /// it. <c>false</c> means "do not boot": the work has been forwarded and the process must
        /// exit with 0, exactly as a second click on a launcher icon does nothing visible but bring
        /// the window forward.
        /// </summary>
        public static bool Startup(string[] args)
        {
            try
            {
                return Current.StartupAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Anything unexpected here has to fall on the side of starting: a bug in the
                // hand-off must not be a bug that stops the app from opening.
                Logger.Error("Single instance check failed, starting anyway", ex);
                return true;
            }
        }

        private async Task<bool> StartupAsync(string[] args)
        {
            var activation = Parse(args, out var minimized);
            LaunchMinimized = minimized;

            if (minimized && activation.Action == null && activation.Uris.Length == 0)
            {
                // A launch that asked to start out of the way has nothing to activate, and every
                // activation means "put me in front": keeping this one would make the autostart
                // entry show the window and then hide it again, and leave it up altogether when
                // there is no tray icon to hide into.
                activation = null;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("UNIGRAM_NO_SINGLE_INSTANCE"), "1", StringComparison.Ordinal))
            {
                // Two Unigrams on purpose: comparing two builds side by side, or a second account
                // in a second data directory. The name is not taken either, so the running app
                // keeps answering the desktop.
                Logger.Info("UNIGRAM_NO_SINGLE_INSTANCE is set, not taking the bus name");
                Enqueue(activation);
                return true;
            }

            if (!DBusSession.IsConfigured)
            {
                Logger.Warning("No session bus: single instance and desktop activation are off");
                Enqueue(activation);
                return true;
            }

            var connection = await DBusSession.ConnectAsync().ConfigureAwait(false);
            if (connection == null)
            {
                Enqueue(activation);
                return true;
            }

            var owned = false;

            // Through the registrar so it is replayed if the connection is ever remade: a
            // connection that comes back without this object exported is an app that owns the name
            // and answers nothing, which is worse than not owning it.
            await DBusSession.AddRegistrarAsync(async c =>
            {
                c.AddMethodHandler(this);

                // Deliberately not ReplaceExisting: stealing the name from the running Unigram
                // would leave two processes, one of them unreachable. Not queueing either — being
                // told "you are second in line" is exactly the answer we act on.
                owned = await c.TryRequestNameAsync(BusName, RequestNameOptions.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (owned)
            {
                IsPrimary = true;
                IsRegistered = true;
                Logger.Info($"Owning {BusName} at {ObjectPath}");

                Enqueue(activation);
                return true;
            }

            IsPrimary = false;

            if (activation == null)
            {
                // The session manager started us while Unigram was already running. Nothing to say
                // and nothing to raise.
                Logger.Info($"{BusName} is already owned: nothing to hand over, exiting");
                return false;
            }

            Logger.Info($"{BusName} is already owned: forwarding {activation} and exiting");

            var sent = await ForwardAsync(connection, activation).ConfigureAwait(false);
            if (!sent)
            {
                // The owner did not answer. Rather than leaving the user with a click that did
                // nothing, start: two windows are recoverable, a dead launcher icon is confusing.
                Logger.Warning("The running instance did not answer, starting a second one");
                IsPrimary = true;
                Enqueue(activation);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Hands activations to whoever can act on them — <see cref="DesktopIntegration"/>, once
        /// there is a window — and replays the ones that arrived before there was one, which
        /// includes the URI this very process was started with.
        /// </summary>
        public void Attach(Action<DesktopActivation> handler)
        {
            DesktopActivation[] pending;

            lock (_lock)
            {
                _handler = handler;
                pending = _pending.ToArray();
                _pending.Clear();
            }

            foreach (var item in pending)
            {
                Dispatch(item);
            }
        }

        private void Enqueue(DesktopActivation activation)
        {
            if (activation == null)
            {
                return;
            }

            lock (_lock)
            {
                if (_handler == null)
                {
                    _pending.Add(activation);
                    return;
                }
            }

            Dispatch(activation);
        }

        private void Dispatch(DesktopActivation activation)
        {
            Action<DesktopActivation> handler;

            lock (_lock)
            {
                handler = _handler;
            }

            try
            {
                handler?.Invoke(activation);
            }
            catch (Exception ex)
            {
                // This runs on the D-Bus reader thread: an exception here would take the whole
                // connection down, and with it notifications and the tray icon.
                Logger.Error("Desktop activation handler failed", ex);
            }
        }

        #region Command line

        /// <summary>
        /// What this process was started with, in the shape the running instance would have been
        /// told over D-Bus. A launch with nothing to do is a plain activation, and that is not
        /// nothing: it is what makes the second start raise the first window.
        /// </summary>
        public static DesktopActivation Parse(string[] args, out bool minimized)
        {
            minimized = false;

            var uris = new List<string>();
            string action = null;

            for (var i = 0; i < (args?.Length ?? 0); i++)
            {
                var arg = args[i];

                if (string.IsNullOrEmpty(arg))
                {
                    continue;
                }

                if (arg is "--minimized" or "-minimized" or "--startup")
                {
                    minimized = true;
                }
                else if (arg.StartsWith("--action=", StringComparison.Ordinal))
                {
                    action = arg.Substring("--action=".Length);
                }
                else if (arg == "--action" && i + 1 < args.Length)
                {
                    action = args[++i];
                }
                else if (arg.StartsWith("-", StringComparison.Ordinal))
                {
                    // Uno and the runtime take their own switches; anything unknown is not ours.
                }
                else if (IsPath(arg))
                {
                    // Asked before Uri.TryCreate on purpose. On Unix an absolute path parses as an
                    // absolute URI — "/tmp/a.unigram-theme" comes back with Scheme "file" — so the
                    // obvious order smuggles a bare path into a field that has to hold URIs, and
                    // hands "/does/not/exist" to the app as something to open. Measured in
                    // unigram-linux/spikes/ActivationSpike.
                    AddPath(uris, arg);
                }
                else if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
                {
                    uris.Add(arg);
                }
                else
                {
                    // A relative name with nothing in front of it: a path if it is one, dropped
                    // otherwise.
                    AddPath(uris, arg);
                }
            }

            return new DesktopActivation(action, uris.ToArray(), ActivationToken());
        }

        private static bool IsPath(string arg)
        {
            return arg[0] == '/'
                || arg[0] == '~'
                || arg.StartsWith("./", StringComparison.Ordinal)
                || arg.StartsWith("../", StringComparison.Ordinal);
        }

        /// <summary>
        /// A file manager that does not speak URIs (or a shell) hands over a plain path; the
        /// interface speaks URIs, so it becomes one here and only here. A path that is not there
        /// is dropped: opening it would fail later and further from the cause.
        /// </summary>
        private static void AddPath(List<string> uris, string arg)
        {
            try
            {
                var path = arg;

                if (path.Length > 1 && path[0] == '~' && path[1] == '/')
                {
                    path = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
                }

                if (File.Exists(path))
                {
                    // Fully qualified: the Path property of IPathMethodHandler is the object path,
                    // and it shadows System.IO.Path inside this type.
                    uris.Add(new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri);
                }
                else
                {
                    Logger.Warning($"Nothing at {arg}, ignoring it");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Cannot make a uri out of {arg}: {ex.Message}");
            }
        }

        private static string ActivationToken()
        {
            var token = Environment.GetEnvironmentVariable("XDG_ACTIVATION_TOKEN");
            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }

            token = Environment.GetEnvironmentVariable("DESKTOP_STARTUP_ID");
            return string.IsNullOrEmpty(token) ? null : token;
        }

        #endregion

        #region Forwarding

        private static async Task<bool> ForwardAsync(DBusConnection connection, DesktopActivation activation)
        {
            try
            {
                await connection.CallMethodAsync(Compose(connection, activation)).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Cannot forward the activation: {ex.Message}");
                return false;
            }
        }

        private static MessageBuffer Compose(DBusConnection connection, DesktopActivation activation)
        {
            var writer = connection.GetMessageWriter();

            try
            {
                if (activation.Action != null)
                {
                    writer.WriteMethodCallHeader(BusName, ObjectPath, Interface, "ActivateAction", "sava{sv}");
                    writer.WriteString(activation.Action);
                    writer.WriteArray(Array.Empty<VariantValue>());
                }
                else if (activation.Uris.Length > 0)
                {
                    writer.WriteMethodCallHeader(BusName, ObjectPath, Interface, "Open", "asa{sv}");
                    writer.WriteArray(activation.Uris);
                }
                else
                {
                    writer.WriteMethodCallHeader(BusName, ObjectPath, Interface, "Activate", "a{sv}");
                }

                // MessageWriter is a ref struct: this has to take it by ref or the platform data
                // would be written into the shared buffer without moving the caller's position,
                // and the body would end up shorter than what was written. See PORTING.md 7.
                WritePlatformData(ref writer, activation.Token);

                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void WritePlatformData(ref MessageWriter writer, string token)
        {
            var dictionary = writer.WriteDictionaryStart();

            if (!string.IsNullOrEmpty(token))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("activation-token");
                writer.WriteVariantString(token);

                // The X11 spelling of the same thing. Cheap, and it is what a window manager that
                // predates the Wayland protocol looks for.
                writer.WriteDictionaryEntryStart();
                writer.WriteString("desktop-startup-id");
                writer.WriteVariantString(token);
            }

            writer.WriteDictionaryEnd(dictionary);
        }

        #endregion

        #region Serving

        public string Path => ObjectPath;

        // Only this object, not a tree under it: everything the interface can be asked lives here.
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;

            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml(new ReadOnlyMemory<byte>[] { IntrospectionXml });
                return default;
            }

            if (request.InterfaceAsString != Interface)
            {
                context.ReplyUnknownMethodError();
                return default;
            }

            try
            {
                switch (request.MemberAsString)
                {
                    case "Activate":
                        {
                            var reader = request.GetBodyReader();
                            var token = ReadToken(ref reader);

                            Enqueue(new DesktopActivation(null, null, token));
                        }
                        break;
                    case "Open":
                        {
                            var reader = request.GetBodyReader();
                            var uris = reader.ReadArrayOfString();
                            var token = ReadToken(ref reader);

                            Enqueue(new DesktopActivation(null, uris, token));
                        }
                        break;
                    case "ActivateAction":
                        {
                            var reader = request.GetBodyReader();
                            var name = reader.ReadString();

                            // The parameter is an "av" holding zero or one value. Nothing here
                            // takes a parameter, but it has to be read past to reach the platform
                            // data behind it.
                            reader.ReadArrayOfVariantValue();

                            var token = ReadToken(ref reader);

                            Enqueue(new DesktopActivation(name, null, token));
                        }
                        break;
                    default:
                        context.ReplyUnknownMethodError();
                        return default;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Malformed desktop activation", ex);
            }

            // Every one of the three is a void method, but the reply is not optional: the launcher
            // waits for it, and GLib reports a launch that never answered as a failure.
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());

            return default;
        }

        private static string ReadToken(ref Reader reader)
        {
            var data = reader.ReadDictionaryOfStringToVariantValue();

            if (data != null)
            {
                if (data.TryGetValue("activation-token", out var token) && token.Type == VariantValueType.String)
                {
                    return token.GetString();
                }

                if (data.TryGetValue("desktop-startup-id", out var startup) && startup.Type == VariantValueType.String)
                {
                    return startup.GetString();
                }
            }

            return null;
        }

        private static readonly ReadOnlyMemory<byte> IntrospectionXml = Encoding.UTF8.GetBytes(
            """
            <interface name="org.freedesktop.Application">
              <method name="Activate">
                <arg type="a{sv}" name="platform_data" direction="in"/>
              </method>
              <method name="Open">
                <arg type="as" name="uris" direction="in"/>
                <arg type="a{sv}" name="platform_data" direction="in"/>
              </method>
              <method name="ActivateAction">
                <arg type="s" name="action_name" direction="in"/>
                <arg type="av" name="parameter" direction="in"/>
                <arg type="a{sv}" name="platform_data" direction="in"/>
              </method>
            </interface>
            """);

        #endregion
    }
}
