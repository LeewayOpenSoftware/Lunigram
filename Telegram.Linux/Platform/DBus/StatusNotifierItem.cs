//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Telegram.Services
{
    /// <summary>
    /// The tray icon, over <c>org.kde.StatusNotifierItem</c>, with its menu on
    /// <c>com.canonical.dbusmenu</c>.
    ///
    /// <para>This is the piece that deletes a whole process. On Windows the tray icon belongs to
    /// <c>Telegram.Stub</c>, a separate Win32 program launched with
    /// <c>FullTrustProcessLauncher</c> that talks to the app through an
    /// <c>AppServiceConnection</c> named <c>org.telegram.bridge</c>, and whose 24 P/Invokes to
    /// <c>Shell_NotifyIcon</c>/<c>TrackPopupMenu</c> exist because a UWP app cannot own a tray
    /// icon. On Linux the tray is just another D-Bus object, so it lives INSIDE the app: no second
    /// process, no bridge protocol, no keep-alive, and no relaunch.</para>
    ///
    /// <para>The item is not drawn by us either: an object is exported, the watcher is told about
    /// it, and whoever implements the host (GNOME with the AppIndicator extension, KDE, most docks)
    /// reads the properties and draws it. Two consequences that shape the code: the host may
    /// re-read everything at any time, so the properties have to be answerable on demand rather
    /// than pushed; and there is no callback for "the user sees it", so a missing host is silent —
    /// hence the explicit check of <c>IsStatusNotifierHostRegistered</c> in the log.</para>
    /// </summary>
    public sealed class StatusNotifierItem : IPathMethodHandler
    {
        private const string Interface = "org.kde.StatusNotifierItem";
        private const string WatcherService = "org.kde.StatusNotifierWatcher";
        private const string WatcherPath = "/StatusNotifierWatcher";
        private const string WatcherInterface = "org.kde.StatusNotifierWatcher";

        public const string ItemPath = "/StatusNotifierItem";
        public const string MenuPath = "/StatusNotifierItem/Menu";

        // The sizes handed to the host. It picks the closest one and scales; giving it three
        // spares it from upscaling a 24 px icon on a HiDPI panel, which is this very machine.
        private static readonly int[] PixmapSizes = { 24, 32, 48 };

        public static StatusNotifierItem Current { get; } = new StatusNotifierItem();

        private readonly string _busName;

        private TrayIconState _state = TrayIconState.Default;
        private string _toolTipTitle = "Unigram";
        private string _toolTipDescription = string.Empty;
        private bool _registered;
        private bool _visible = true;
        private bool _watching;

        private StatusNotifierItem()
        {
            _busName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
            Menu = new DBusMenu(MenuPath);
        }

        /// <summary>The menu the host shows on right click (and on left click if <see cref="ItemIsMenu"/>).</summary>
        public DBusMenu Menu { get; }

        /// <summary>Left click. The Windows stub relaunched or focused the app here.</summary>
        public event EventHandler Activated;

        /// <summary>Middle click.</summary>
        public event EventHandler SecondaryActivated;

        /// <summary>
        /// False means "left click activates, right click opens the menu", which is what the
        /// Windows tray icon does.
        /// </summary>
        public bool ItemIsMenu { get; set; }

        /// <summary>
        /// The XDG activation token the host hands over before Activate, so the window can be
        /// raised without the compositor calling it focus stealing.
        /// </summary>
        public string ActivationToken { get; private set; }

        public string Path => ItemPath;

        public bool HandlesChildPaths => false;

        /// <summary>
        /// Owns the name, exports the item and its menu, and registers with the watcher. False
        /// means there is no tray on this desktop; the app carries on without one.
        /// </summary>
        public async Task<bool> RegisterAsync()
        {
            if (_registered)
            {
                return true;
            }

            var ok = await DBusSession.AddRegistrarAsync(async connection =>
            {
                connection.AddMethodHandler(this);
                connection.AddMethodHandler(Menu);

                // A well-known name is what the spec asks for; the watcher also accepts a unique
                // name, which is the fallback if a stale name from a previous run is still owned.
                var owned = await connection.TryRequestNameAsync(_busName, RequestNameOptions.ReplaceExisting).ConfigureAwait(false);
                var service = owned ? _busName : connection.UniqueName;

                await RegisterWithWatcherAsync(connection, service).ConfigureAwait(false);
            }).ConfigureAwait(false);

            if (ok)
            {
                _registered = true;
                await WatchWatcherAsync().ConfigureAwait(false);
            }

            return ok;
        }

        private static MessageBuffer CreateRegister(DBusConnection connection, string service)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(WatcherService, WatcherPath, WatcherInterface, "RegisterStatusNotifierItem", "s");
            writer.WriteString(service);
            return writer.CreateMessage();
        }

        private static MessageBuffer CreateIsHostRegistered(DBusConnection connection)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(WatcherService, WatcherPath, "org.freedesktop.DBus.Properties", "Get", "ss");
            writer.WriteString(WatcherInterface);
            writer.WriteString("IsStatusNotifierHostRegistered");
            return writer.CreateMessage();
        }

        private static async Task RegisterWithWatcherAsync(DBusConnection connection, string service)
        {
            try
            {
                await connection.CallMethodAsync(CreateRegister(connection, service)).ConfigureAwait(false);

                var hosted = await IsHostRegisteredAsync(connection).ConfigureAwait(false);
                if (hosted)
                {
                    Logger.Info($"Tray item registered as {service}");
                }
                else
                {
                    // The watcher exists but nobody draws its items: GNOME without the
                    // AppIndicator extension is exactly this. Worth saying out loud, because the
                    // symptom is an icon that never appears and no error anywhere.
                    Logger.Warning($"Tray item registered as {service}, but no status notifier host is listening");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"No org.kde.StatusNotifierWatcher: no tray icon ({ex.Message})");
            }
        }

        private static async Task<bool> IsHostRegisteredAsync(DBusConnection connection)
        {
            try
            {
                return await connection.CallMethodAsync(CreateIsHostRegistered(connection),
                    static (Message message, object _) => message.GetBodyReader().ReadVariantValue().GetBool()).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        private async Task WatchWatcherAsync()
        {
            if (_watching)
            {
                return;
            }

            _watching = true;

            // GNOME Shell owns the watcher, so restarting the shell or toggling the extension
            // takes the watcher away and brings a new one. Without this the icon would be gone for
            // the rest of the session.
            await DBusSession.AddRegistrarAsync(async connection =>
            {
                await connection.AddMatchAsync(
                    new MatchRule
                    {
                        Type = MessageType.Signal,
                        Sender = "org.freedesktop.DBus",
                        Path = "/org/freedesktop/DBus",
                        Interface = "org.freedesktop.DBus",
                        Member = "NameOwnerChanged",
                        Arg0 = WatcherService
                    },
                    static (Message message, object _) =>
                    {
                        var reader = message.GetBodyReader();
                        reader.ReadString();
                        reader.ReadString();
                        return reader.ReadString();
                    },
                    static (Exception ex, string owner, object _, object state) =>
                    {
                        if (ex == null && !string.IsNullOrEmpty(owner))
                        {
                            var item = (StatusNotifierItem)state;
                            _ = item.ReregisterAsync();
                        }
                    },
                    ObserverFlags.None, null, this, false).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        private async Task ReregisterAsync()
        {
            var connection = await DBusSession.ConnectAsync().ConfigureAwait(false);
            if (connection == null)
            {
                return;
            }

            // The name is still ours -- it was never released -- so the new watcher only has to be
            // told about it again.
            await RegisterWithWatcherAsync(connection, _busName).ConfigureAwait(false);
        }

        /// <summary>Switches the icon between plain, muted-unread and unread.</summary>
        public Task SetStateAsync(TrayIconState state)
        {
            if (_state == state)
            {
                return Task.CompletedTask;
            }

            _state = state;
            return EmitAsync("NewIcon", null);
        }

        /// <summary>
        /// Whether the host draws the icon, which is what the tray checkbox of Settings > Advanced
        /// switches. Passive rather than unregistering: the name stays ours and the icon can come
        /// back with one signal.
        /// </summary>
        public Task SetVisibleAsync(bool visible)
        {
            if (_visible == visible)
            {
                return Task.CompletedTask;
            }

            _visible = visible;
            return EmitAsync("NewStatus", visible ? "Active" : "Passive");
        }

        /// <summary>Sets the hover text. Empty description means title only.</summary>
        public Task SetToolTipAsync(string title, string description)
        {
            _toolTipTitle = title ?? string.Empty;
            _toolTipDescription = description ?? string.Empty;

            return EmitAsync("NewToolTip", null);
        }

        private static Task EmitAsync(string member, string argument)
        {
            return DBusSession.EmitAsync(connection =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(null, ItemPath, Interface, member, argument == null ? null : "s");

                if (argument != null)
                {
                    writer.WriteString(argument);
                }

                return writer.CreateMessage();
            });
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;

            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml(new ReadOnlyMemory<byte>[] { IntrospectionXml });
                return default;
            }

            if (context.IsPropertiesInterfaceRequest)
            {
                HandleProperties(context);
                return default;
            }

            if (request.InterfaceAsString != Interface)
            {
                context.ReplyUnknownMethodError();
                return default;
            }

            switch (request.MemberAsString)
            {
                case "Activate":
                    Raise(Activated);
                    ReplyEmpty(context);
                    break;
                case "SecondaryActivate":
                case "XAyatanaSecondaryActivate":
                    Raise(SecondaryActivated);
                    ReplyEmpty(context);
                    break;
                case "ContextMenu":
                    // The host draws the menu from the dbusmenu object; nothing to do here, but
                    // answering is not optional or the host waits for a reply that never comes.
                    ReplyEmpty(context);
                    break;
                case "Scroll":
                    ReplyEmpty(context);
                    break;
                case "ProvideXdgActivationToken":
                    ActivationToken = request.GetBodyReader().ReadString();
                    ReplyEmpty(context);
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }

            return default;
        }

        private static void ReplyEmpty(MethodContext context)
        {
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }

        private void Raise(EventHandler handler)
        {
            try
            {
                handler?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Error("Tray icon handler failed", ex);
            }
        }

        private void HandleProperties(MethodContext context)
        {
            var request = context.Request;
            var reader = request.GetBodyReader();
            var @interface = reader.ReadString();

            if (@interface != Interface)
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface", @interface);
                return;
            }

            if (request.MemberAsString == "GetAll")
            {
                var writer = context.CreateReplyWriter("a{sv}");

                try
                {
                    var dictionary = writer.WriteDictionaryStart();

                    foreach (var name in PropertyNames)
                    {
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString(name);
                        WriteValue(ref writer, name);
                    }

                    writer.WriteDictionaryEnd(dictionary);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }
            }
            else if (request.MemberAsString == "Get")
            {
                var name = reader.ReadString();

                if (Array.IndexOf(PropertyNames, name) < 0)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                    return;
                }

                var writer = context.CreateReplyWriter("v");

                try
                {
                    WriteValue(ref writer, name);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }
            }
            else
            {
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", request.MemberAsString);
            }
        }

        private static readonly string[] PropertyNames =
        {
            "Category", "Id", "Title", "Status", "WindowId", "IconThemePath", "Menu", "ItemIsMenu",
            "IconName", "IconPixmap", "OverlayIconName", "OverlayIconPixmap",
            "AttentionIconName", "AttentionIconPixmap", "AttentionMovieName", "ToolTip"
        };

        private void WriteValue(ref MessageWriter writer, string name)
        {
            switch (name)
            {
                case "Category":
                    writer.WriteVariantString("Communications");
                    break;
                case "Id":
                    writer.WriteVariantString("unigram");
                    break;
                case "Title":
                    writer.WriteVariantString("Unigram");
                    break;
                case "Status":
                    // Active unless the user turned the icon off in Settings > Advanced. Passive is
                    // the spec's way of leaving the panel without dropping the object, so the
                    // checkbox can put it back with a NewStatus signal instead of a registration.
                    writer.WriteVariantString(_visible ? "Active" : "Passive");
                    break;
                case "WindowId":
                    writer.WriteVariantInt32(0);
                    break;
                case "IconThemePath":
                    writer.WriteVariantString(string.Empty);
                    break;
                case "Menu":
                    writer.WriteVariantObjectPath(MenuPath);
                    break;
                case "ItemIsMenu":
                    writer.WriteVariantBool(ItemIsMenu);
                    break;
                case "IconName":
                    // The pixmaps below carry the three states; a themed name could only ever be
                    // the plain logo, and a host that preferred it would show the wrong state.
                    writer.WriteVariantString(string.Empty);
                    break;
                case "IconPixmap":
                    WritePixmaps(ref writer, _state);
                    break;
                case "AttentionIconPixmap":
                case "OverlayIconPixmap":
                    WritePixmaps(ref writer, null);
                    break;
                case "OverlayIconName":
                case "AttentionIconName":
                case "AttentionMovieName":
                    writer.WriteVariantString(string.Empty);
                    break;
                case "ToolTip":
                    WriteToolTip(ref writer);
                    break;
            }
        }

        private static void WritePixmaps(ref MessageWriter writer, TrayIconState? state)
        {
            writer.WriteSignature("a(iiay)");

            var array = writer.WriteArrayStart(DBusType.Struct);

            if (state.HasValue)
            {
                foreach (var size in PixmapSizes)
                {
                    var pixels = TrayIcons.GetArgb32(state.Value, size);
                    if (pixels == null)
                    {
                        continue;
                    }

                    writer.WriteStructureStart();
                    writer.WriteInt32(size);
                    writer.WriteInt32(size);
                    writer.WriteArray(pixels);
                }
            }

            writer.WriteArrayEnd(array);
        }

        private void WriteToolTip(ref MessageWriter writer)
        {
            writer.WriteSignature("(sa(iiay)ss)");
            writer.WriteStructureStart();
            writer.WriteString(string.Empty);

            var array = writer.WriteArrayStart(DBusType.Struct);
            writer.WriteArrayEnd(array);

            writer.WriteString(_toolTipTitle);
            writer.WriteString(_toolTipDescription);
        }

        private static readonly ReadOnlyMemory<byte> IntrospectionXml = System.Text.Encoding.UTF8.GetBytes(
            """
            <interface name="org.kde.StatusNotifierItem">
              <property name="Category" type="s" access="read"/>
              <property name="Id" type="s" access="read"/>
              <property name="Title" type="s" access="read"/>
              <property name="Status" type="s" access="read"/>
              <property name="WindowId" type="i" access="read"/>
              <property name="IconThemePath" type="s" access="read"/>
              <property name="Menu" type="o" access="read"/>
              <property name="ItemIsMenu" type="b" access="read"/>
              <property name="IconName" type="s" access="read"/>
              <property name="IconPixmap" type="a(iiay)" access="read"/>
              <property name="OverlayIconName" type="s" access="read"/>
              <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
              <property name="AttentionIconName" type="s" access="read"/>
              <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
              <property name="AttentionMovieName" type="s" access="read"/>
              <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
              <method name="ContextMenu">
                <arg name="x" type="i" direction="in"/>
                <arg name="y" type="i" direction="in"/>
              </method>
              <method name="Activate">
                <arg name="x" type="i" direction="in"/>
                <arg name="y" type="i" direction="in"/>
              </method>
              <method name="SecondaryActivate">
                <arg name="x" type="i" direction="in"/>
                <arg name="y" type="i" direction="in"/>
              </method>
              <method name="XAyatanaSecondaryActivate">
                <arg name="timestamp" type="u" direction="in"/>
              </method>
              <method name="ProvideXdgActivationToken">
                <arg name="token" type="s" direction="in"/>
              </method>
              <method name="Scroll">
                <arg name="delta" type="i" direction="in"/>
                <arg name="orientation" type="s" direction="in"/>
              </method>
              <signal name="NewTitle"/>
              <signal name="NewIcon"/>
              <signal name="NewAttentionIcon"/>
              <signal name="NewOverlayIcon"/>
              <signal name="NewToolTip"/>
              <signal name="NewStatus">
                <arg name="status" type="s"/>
              </signal>
            </interface>
            """);
    }
}
