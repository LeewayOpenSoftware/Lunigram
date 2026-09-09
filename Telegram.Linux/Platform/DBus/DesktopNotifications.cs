//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Telegram.Services
{
    /// <summary>What the user did on a desktop notification.</summary>
    public enum DesktopNotificationAction
    {
        /// <summary>The banner itself was clicked: open the chat.</summary>
        Default,
        /// <summary>The reply button. On a server without inline reply this still means "open the chat".</summary>
        Reply,
        /// <summary>The mark as read button.</summary>
        MarkAsRead
    }

    /// <summary>
    /// Raw pixels for the <c>image-data</c> hint: straight (NOT premultiplied) RGBA, 8 bits per
    /// sample. The whole port speaks premultiplied BGRA (Skia, rlottie, the video engine), and this
    /// is the one place that does not — see <c>NotificationAvatar</c>, which is where the
    /// conversion happens.
    /// </summary>
    public sealed class NotificationImage
    {
        public NotificationImage(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }

        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }
        public int RowStride => Width * 4;
    }

    /// <summary>One notification to show. Plain data: nothing here knows about TDLib.</summary>
    public sealed class NotificationRequest
    {
        /// <summary>Bold first line. The chat title, as in the Windows toast.</summary>
        public string Summary { get; set; }

        /// <summary>Second line: sender and message preview.</summary>
        public string Body { get; set; }

        /// <summary>The chat avatar, already circular. Null falls back to the app icon.</summary>
        public NotificationImage Image { get; set; }

        /// <summary>Do not let the server play a sound (the chat is muted, or Unigram plays its own).</summary>
        public bool Silent { get; set; }

        /// <summary>Whether a reply makes sense at all (false for channels).</summary>
        public bool CanReply { get; set; }

        /// <summary>Opaque string handed back with the action. Unigram puts the toast launch arguments here.</summary>
        public string Payload { get; set; }
    }

    /// <summary>
    /// Desktop notifications over <c>org.freedesktop.Notifications</c>, replacing the UWP toast XML
    /// of <c>NotificationsService.UpdateToast</c>.
    ///
    /// <para>The mapping, one Windows concept at a time:</para>
    /// <list type="bullet">
    /// <item><c>&lt;image placement='appLogoOverride' hint-crop='circle'&gt;</c> → the
    /// <c>image-data</c> hint, which takes the pixels themselves (a path would work too, but the
    /// avatar has to arrive already round and TDLib stores a square JPEG).</item>
    /// <item>Toast <c>Tag</c>/<c>Group</c> → the <paramref name="key"/> of <see cref="ShowAsync"/>,
    /// which maps to the server id used as <c>replaces_id</c>: the second message of a chat
    /// REPLACES the first instead of stacking a second banner.</item>
    /// <item><c>ToastNotificationManager.History.Remove</c> → <c>CloseNotification</c>, so a
    /// message read on the phone takes its banner away here too.</item>
    /// <item>The toast's text input → the <c>inline-reply</c> capability, which only KDE
    /// implements. Where it is missing (GNOME, which is what this port runs on) the reply button
    /// stays, and pressing it opens the chat. That is the graceful degradation: a button that
    /// disappears is worse than one that takes you where you can answer.</item>
    /// </list>
    ///
    /// <para><b>Threading</b>: <see cref="Activated"/> is raised on the D-Bus reader thread.
    /// Anything that touches the UI has to hop to the dispatcher itself.</para>
    /// </summary>
    public sealed class DesktopNotifications
    {
        private const string Service = "org.freedesktop.Notifications";
        private const string Path = "/org/freedesktop/Notifications";
        private const string Interface = "org.freedesktop.Notifications";

        private const string ActionDefault = "default";
        private const string ActionReply = "reply";
        private const string ActionMarkAsRead = "markasread";
        private const string ActionInlineReply = "inline-reply";

        public static DesktopNotifications Current { get; } = new DesktopNotifications();

        private readonly SemaphoreSlim _gate = new(1, 1);

        // key (one chat, one notification group) -> the id the server gave us, so the next message
        // of that chat replaces the banner instead of adding one.
        private readonly Dictionary<string, uint> _byKey = new();
        // The reverse, plus what the action has to be routed with. A server id we do not know is a
        // notification from a previous run of the app: ignored, never guessed.
        private readonly Dictionary<uint, Entry> _byId = new();

        private HashSet<string> _capabilities;
        private bool _initialized;
        private bool _unavailable;

        private sealed class Entry
        {
            public string Key;
            public string Payload;
        }

        /// <summary>Fired when the user clicks the banner or one of its buttons.</summary>
        public event EventHandler<DesktopNotificationActivatedEventArgs> Activated;

        /// <summary>The app name the server shows and logs. Also the fallback icon lookup name.</summary>
        public string ApplicationName { get; set; } = "Unigram";

        /// <summary>Whether the server takes action buttons at all (GNOME and KDE do).</summary>
        public bool SupportsActions => _capabilities != null && _capabilities.Contains("actions");

        /// <summary>Whether the server can take the reply text itself. KDE yes, GNOME no.</summary>
        public bool SupportsInlineReply => _capabilities != null && _capabilities.Contains("inline-reply");

        /// <summary>Whether the body may carry markup. Unigram sends plain text, so this is only informative.</summary>
        public bool SupportsBodyMarkup => _capabilities != null && _capabilities.Contains("body-markup");

        /// <summary>The capabilities exactly as the server reported them, for the log.</summary>
        public IReadOnlyCollection<string> Capabilities => _capabilities ?? (IReadOnlyCollection<string>)Array.Empty<string>();

        /// <summary>
        /// Reads the server capabilities and subscribes to its signals. Idempotent, and false means
        /// "no notification server": the caller keeps working, it just does not notify.
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            if (_initialized)
            {
                return !_unavailable;
            }

            await _gate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_initialized)
                {
                    return !_unavailable;
                }

                _initialized = true;

                var connection = await DBusSession.ConnectAsync().ConfigureAwait(false);
                if (connection == null)
                {
                    _unavailable = true;
                    return false;
                }

                _capabilities = await GetCapabilitiesAsync(connection).ConfigureAwait(false);
                if (_capabilities == null)
                {
                    Logger.Warning("No org.freedesktop.Notifications on the session bus");
                    _unavailable = true;
                    return false;
                }

                await SubscribeAsync(connection).ConfigureAwait(false);

                Logger.Info($"Notification server capabilities: {string.Join(", ", _capabilities)}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot initialize desktop notifications", ex);
                _unavailable = true;
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static MessageBuffer CreateGetCapabilities(DBusConnection connection)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(Service, Path, Interface, "GetCapabilities", null);
            return writer.CreateMessage();
        }

        private static async Task<HashSet<string>> GetCapabilitiesAsync(DBusConnection connection)
        {
            try
            {
                var capabilities = await connection.CallMethodAsync(CreateGetCapabilities(connection),
                    static (Message message, object _) => message.GetBodyReader().ReadArrayOfString()).ConfigureAwait(false);

                return new HashSet<string>(capabilities, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Logger.Warning($"GetCapabilities failed: {ex.Message}");
                return null;
            }
        }

        private async Task SubscribeAsync(DBusConnection connection)
        {
            // The three signals that matter. ActivationToken arrives just before ActionInvoked on
            // GNOME and is what lets the window come to the front without the compositor treating
            // it as focus stealing; it is stashed and used by whoever raises the window.
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = Path, Interface = Interface, Member = "ActionInvoked" },
                static (Message message, object _) =>
                {
                    var reader = message.GetBodyReader();
                    return (reader.ReadUInt32(), reader.ReadString());
                },
                (Exception ex, (uint Id, string Action) value, object _, object state) =>
                {
                    if (ex == null)
                    {
                        ((DesktopNotifications)state).OnActionInvoked(value.Id, value.Action, null);
                    }
                },
                ObserverFlags.None, this, this, false).ConfigureAwait(false);

            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = Path, Interface = Interface, Member = "NotificationClosed" },
                static (Message message, object _) =>
                {
                    var reader = message.GetBodyReader();
                    return (reader.ReadUInt32(), reader.ReadUInt32());
                },
                (Exception ex, (uint Id, uint Reason) value, object _, object state) =>
                {
                    if (ex == null)
                    {
                        ((DesktopNotifications)state).OnClosed(value.Id);
                    }
                },
                ObserverFlags.None, this, this, false).ConfigureAwait(false);

            // KDE's inline reply lands here, with the text the user typed in the banner.
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = Path, Interface = Interface, Member = "NotificationReplied" },
                static (Message message, object _) =>
                {
                    var reader = message.GetBodyReader();
                    return (reader.ReadUInt32(), reader.ReadString());
                },
                (Exception ex, (uint Id, string Text) value, object _, object state) =>
                {
                    if (ex == null)
                    {
                        ((DesktopNotifications)state).OnActionInvoked(value.Id, ActionInlineReply, value.Text);
                    }
                },
                ObserverFlags.None, this, this, false).ConfigureAwait(false);

            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = Path, Interface = Interface, Member = "ActivationToken" },
                static (Message message, object _) =>
                {
                    var reader = message.GetBodyReader();
                    return (reader.ReadUInt32(), reader.ReadString());
                },
                (Exception ex, (uint Id, string Token) value, object _, object state) =>
                {
                    if (ex == null)
                    {
                        ActivationToken = value.Token;
                    }
                },
                ObserverFlags.None, this, this, false).ConfigureAwait(false);
        }

        /// <summary>
        /// The last XDG activation token the notification server handed out. Whoever raises the
        /// window should pass it on, and clear it after.
        /// </summary>
        public string ActivationToken { get; private set; }

        /// <summary>
        /// Shows or updates the notification identified by <paramref name="key"/>. Returns the
        /// server id, or 0 if there is no server.
        /// </summary>
        public async Task<uint> ShowAsync(string key, NotificationRequest request)
        {
            if (!await InitializeAsync().ConfigureAwait(false))
            {
                return 0;
            }

            var connection = await DBusSession.ConnectAsync().ConfigureAwait(false);
            if (connection == null)
            {
                return 0;
            }

            uint replaces = 0;
            lock (_byKey)
            {
                if (key != null)
                {
                    _byKey.TryGetValue(key, out replaces);
                }
            }

            try
            {
                var message = Compose(connection, replaces, request);

                var id = await connection.CallMethodAsync(message,
                    static (Message reply, object _) => reply.GetBodyReader().ReadUInt32()).ConfigureAwait(false);

                if (key != null && id != 0)
                {
                    lock (_byKey)
                    {
                        // A replaced notification keeps its id, so this is usually a no-op; when
                        // the server hands out a new one the old entry has to go, or a stale id
                        // would keep answering for a banner that is not on screen any more.
                        if (replaces != 0 && replaces != id)
                        {
                            _byId.Remove(replaces);
                        }

                        _byKey[key] = id;
                        _byId[id] = new Entry { Key = key, Payload = request.Payload };
                    }
                }

                // The id, and whether it replaced one, is the only trace a banner leaves that this
                // process can see: what the server draws cannot be read back or captured.
                Logger.Info($"Notification {key} shown as {id}"
                    + (replaces != 0 ? $" (replacing {replaces})" : string.Empty)
                    + (request.Image != null ? $", avatar {request.Image.Width}x{request.Image.Height}" : ", no avatar"));

                return id;
            }
            catch (Exception ex)
            {
                Logger.Error("Notify failed", ex);
                return 0;
            }
        }

        private MessageBuffer Compose(DBusConnection connection, uint replaces, NotificationRequest request)
        {
            // Not `using var`: the hint helpers below take the writer by ref (see their comment),
            // and C# will not hand out a ref to a using variable.
            var writer = connection.GetMessageWriter();

            try
            {
                writer.WriteMethodCallHeader(Service, Path, Interface, "Notify", "susssasa{sv}i");

                writer.WriteString(ApplicationName);
                writer.WriteUInt32(replaces);
                writer.WriteString(DesktopEntry.IconName);
                writer.WriteString(request.Summary ?? string.Empty);
                writer.WriteString(request.Body ?? string.Empty);

                var actions = BuildActions(request);
                writer.WriteArray(actions);

                var hints = writer.WriteDictionaryStart();

                WriteHintByte(ref writer, "urgency", 1);            // normal; low would let GNOME hide it
                WriteHintString(ref writer, "category", "im.received");
                WriteHintString(ref writer, "desktop-entry", DesktopEntry.Id);

                if (request.Silent)
                {
                    WriteHintBool(ref writer, "suppress-sound", true);
                }

                if (request.CanReply && SupportsInlineReply)
                {
                    WriteHintString(ref writer, "x-kde-reply-placeholder-text", Strings.Reply);
                    WriteHintString(ref writer, "x-kde-reply-submit-button-text", Strings.Send);
                }

                if (request.Image != null)
                {
                    WriteHintImage(ref writer, request.Image);
                }

                writer.WriteDictionaryEnd(hints);

                // -1 = the server decides. With the "persistence" capability (GNOME, KDE) the
                // banner then lives on in the notification list, which is what a chat client
                // wants: the Windows toast did the same through the action center.
                writer.WriteInt32(-1);

                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }

        private string[] BuildActions(NotificationRequest request)
        {
            if (!SupportsActions)
            {
                return Array.Empty<string>();
            }

            var actions = new List<string>(6);

            // "default" has no visible button: it is what the server invokes when the banner
            // itself is clicked, which is how "open that chat" arrives.
            actions.Add(ActionDefault);
            actions.Add(Strings.Open);

            if (request.CanReply)
            {
                actions.Add(SupportsInlineReply ? ActionInlineReply : ActionReply);
                actions.Add(Strings.Reply);

                actions.Add(ActionMarkAsRead);
                actions.Add(Strings.MarkAsRead);
            }

            return actions.ToArray();
        }

        // Every one of these takes the writer BY REF, and that is not a style choice.
        // MessageWriter is a ref struct whose position lives in its own fields (_span, _offset,
        // _buffered), so a by-value copy writes into the shared buffer while leaving the caller's
        // position where it was: the next hint overwrites the previous one and the body ends up
        // shorter than what was actually written. With three small hints the message still parsed
        // and the notification appeared, which is what made this hard to see; adding the 36 KB
        // avatar made the mismatch big enough that dbus-daemon stopped parsing and DISCONNECTED
        // the process -- the symptom was "Connection closed by peer" on a message the daemon never
        // logged. Measured on Tmds.DBus.Protocol 0.92.0.
        private static void WriteHintByte(ref MessageWriter writer, string key, byte value)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteVariantByte(value);
        }

        private static void WriteHintBool(ref MessageWriter writer, string key, bool value)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteVariantBool(value);
        }

        private static void WriteHintString(ref MessageWriter writer, string key, string value)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteVariantString(value ?? string.Empty);
        }

        private static void WriteHintImage(ref MessageWriter writer, NotificationImage image)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("image-data");

            // The variant is written by hand because the struct (iiibiiay) has no helper:
            // signature first, then the value, which is what WriteVariantXxx does internally.
            writer.WriteSignature("(iiibiiay)");
            writer.WriteStructureStart();
            writer.WriteInt32(image.Width);
            writer.WriteInt32(image.Height);
            writer.WriteInt32(image.RowStride);
            writer.WriteBool(true);     // has alpha
            writer.WriteInt32(8);       // bits per sample
            writer.WriteInt32(4);       // channels: RGBA
            writer.WriteArray(image.Pixels);
        }

        /// <summary>
        /// The server id of the banner behind that key, if there is one. Diagnostics only
        /// (UNIGRAM_NOTIFY_TEST): the id is what tells apart a banner that was updated from one
        /// that was piled on top, and it is not observable anywhere else — GNOME lets nobody
        /// screenshot it and the compositor draws it outside our window.
        /// </summary>
        internal bool TryGetId(string key, out uint id)
        {
            lock (_byKey)
            {
                return _byKey.TryGetValue(key, out id);
            }
        }

        /// <summary>
        /// Plays back an action as if the server had sent <c>ActionInvoked</c>. Diagnostics only
        /// (UNIGRAM_NOTIFY_TEST): the match rule of that signal names the server as sender, so the
        /// bus drops a faked one from any other connection and the only other way to walk this path
        /// is a human clicking the banner.
        /// </summary>
        internal void ReplayAction(uint id, string action, string text)
        {
            OnActionInvoked(id, action, text);
        }

        /// <summary>Closes the notification of that key, if it is still up.</summary>
        public async Task CloseAsync(string key)
        {
            uint id;
            lock (_byKey)
            {
                if (!_byKey.TryGetValue(key, out id))
                {
                    return;
                }
            }

            await CloseAsync(id).ConfigureAwait(false);
        }

        /// <summary>Closes a notification by server id.</summary>
        public async Task CloseAsync(uint id)
        {
            if (id == 0)
            {
                return;
            }

            await DBusSession.CallAsync(connection =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(Service, Path, Interface, "CloseNotification", "u");
                writer.WriteUInt32(id);
                return writer.CreateMessage();
            }).ConfigureAwait(false);

            // The server answers with NotificationClosed, which is where the maps are cleaned;
            // doing it here as well keeps them right even if that signal never arrives.
            OnClosed(id);
        }

        /// <summary>Closes everything this process put on screen. Used on shutdown.</summary>
        public async Task CloseAllAsync()
        {
            uint[] ids;
            lock (_byKey)
            {
                ids = new uint[_byId.Count];
                _byId.Keys.CopyTo(ids, 0);
            }

            foreach (var id in ids)
            {
                await CloseAsync(id).ConfigureAwait(false);
            }
        }

        private void OnClosed(uint id)
        {
            lock (_byKey)
            {
                if (_byId.Remove(id, out var entry) && entry.Key != null)
                {
                    if (_byKey.TryGetValue(entry.Key, out var current) && current == id)
                    {
                        _byKey.Remove(entry.Key);
                    }
                }
            }
        }

        private void OnActionInvoked(uint id, string action, string text)
        {
            Entry entry;
            lock (_byKey)
            {
                if (!_byId.TryGetValue(id, out entry))
                {
                    // Not ours, or from a previous run: the notification server keeps banners
                    // across an app restart when it has the persistence capability.
                    return;
                }
            }

            var kind = action switch
            {
                ActionMarkAsRead => DesktopNotificationAction.MarkAsRead,
                ActionReply or ActionInlineReply => DesktopNotificationAction.Reply,
                _ => DesktopNotificationAction.Default
            };

            try
            {
                Activated?.Invoke(this, new DesktopNotificationActivatedEventArgs(entry.Payload, kind, text));
            }
            catch (Exception ex)
            {
                // This runs on the D-Bus reader thread: an exception here would take the whole
                // connection down and with it the tray icon.
                Logger.Error("Notification action handler failed", ex);
            }
        }
    }

    public sealed class DesktopNotificationActivatedEventArgs : EventArgs
    {
        public DesktopNotificationActivatedEventArgs(string payload, DesktopNotificationAction action, string text)
        {
            Payload = payload;
            Action = action;
            Text = text;
        }

        /// <summary>The opaque string the notification was shown with.</summary>
        public string Payload { get; }

        public DesktopNotificationAction Action { get; }

        /// <summary>The text typed in the banner, on the servers that support it. Null otherwise.</summary>
        public string Text { get; }
    }
}
