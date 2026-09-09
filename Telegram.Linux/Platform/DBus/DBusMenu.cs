//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Telegram.Services
{
    /// <summary>One row of the tray menu.</summary>
    public sealed class DBusMenuItem
    {
        public DBusMenuItem(int id, string label, Action invoked)
        {
            Id = id;
            Label = label;
            Invoked = invoked;
        }

        public int Id { get; }
        public string Label { get; set; }
        public bool IsSeparator { get; init; }
        public bool IsEnabled { get; set; } = true;
        public bool IsVisible { get; set; } = true;
        public Action Invoked { get; }
    }

    /// <summary>
    /// The menu behind the tray icon, over <c>com.canonical.dbusmenu</c>.
    ///
    /// <para>On Windows the same two rows are a Win32 <c>HMENU</c> that
    /// <c>Telegram.Stub/NotifyIcon.cs</c> builds with <c>AppendMenu</c> and shows with
    /// <c>TrackPopupMenu</c>. Here the app does not draw the menu at all: it publishes its
    /// STRUCTURE on the bus and the panel draws it with its own widgets, which is why the tray menu
    /// looks native in GNOME, KDE and any dock — and also why every property has to be spelled out,
    /// since there is no control to ask.</para>
    ///
    /// <para>Version 3 of the protocol is what the hosts in the field speak. Only the parts of it
    /// this menu needs are implemented (a flat list of labels and separators); the rest is answered
    /// honestly rather than faked, so a host that asks for a submenu gets an empty one instead of a
    /// wrong one.</para>
    /// </summary>
    public sealed class DBusMenu : IPathMethodHandler
    {
        public const string Interface = "com.canonical.dbusmenu";

        private const string LayoutSignature = "(ia{sv}av)";

        private readonly List<DBusMenuItem> _items = new();
        private uint _revision = 1;

        public DBusMenu(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public bool HandlesChildPaths => false;

        public IReadOnlyList<DBusMenuItem> Items => _items;

        public void Add(DBusMenuItem item)
        {
            _items.Add(item);
        }

        /// <summary>
        /// Tells the host that the labels changed. Without it a host that has already drawn the
        /// menu keeps the old text forever — which is exactly what happens when the app language
        /// is set after the tray icon is created.
        /// </summary>
        public Task InvalidateAsync()
        {
            _revision++;

            return DBusSession.EmitAsync(connection =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(null, Path, Interface, "LayoutUpdated", "ui");
                writer.WriteUInt32(_revision);
                writer.WriteInt32(0);
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
                case "GetLayout":
                    GetLayout(context);
                    break;
                case "GetGroupProperties":
                    GetGroupProperties(context);
                    break;
                case "GetProperty":
                    GetProperty(context);
                    break;
                case "Event":
                    Event(context);
                    break;
                case "EventGroup":
                    EventGroup(context);
                    break;
                case "AboutToShow":
                    {
                        using var writer = context.CreateReplyWriter("b");
                        writer.WriteBool(false);
                        context.Reply(writer.CreateMessage());
                        break;
                    }
                case "AboutToShowGroup":
                    {
                        using var writer = context.CreateReplyWriter("aiai");
                        writer.WriteArray(Array.Empty<int>());
                        writer.WriteArray(Array.Empty<int>());
                        context.Reply(writer.CreateMessage());
                        break;
                    }
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }

            return default;
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

                    WriteProperty(ref writer, "Version");
                    WriteProperty(ref writer, "TextDirection");
                    WriteProperty(ref writer, "Status");
                    WriteProperty(ref writer, "IconThemePath");

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

                using var writer = context.CreateReplyWriter("v");

                switch (name)
                {
                    case "Version":
                        writer.WriteVariantUInt32(3);
                        break;
                    case "TextDirection":
                        writer.WriteVariantString(TextDirection);
                        break;
                    case "Status":
                        writer.WriteVariantString("normal");
                        break;
                    case "IconThemePath":
                        writer.WriteSignature("as");
                        writer.WriteArray(Array.Empty<string>());
                        break;
                    default:
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                        return;
                }

                context.Reply(writer.CreateMessage());
            }
            else
            {
                // Set: every property here is read only.
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", request.MemberAsString);
            }
        }

        /// <summary>
        /// "ltr" or "rtl". Set by whoever creates the menu from the app locale; the D-Bus layer
        /// deliberately knows nothing about Unigram's services, so that it can be driven from a
        /// console spike.
        /// </summary>
        public static string TextDirection { get; set; } = "ltr";

        private static void WriteProperty(ref MessageWriter writer, string name)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(name);

            switch (name)
            {
                case "Version":
                    writer.WriteVariantUInt32(3);
                    break;
                case "TextDirection":
                    writer.WriteVariantString(TextDirection);
                    break;
                case "Status":
                    writer.WriteVariantString("normal");
                    break;
                case "IconThemePath":
                    writer.WriteSignature("as");
                    writer.WriteArray(Array.Empty<string>());
                    break;
            }
        }

        private void GetLayout(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var parentId = reader.ReadInt32();

            var writer = context.CreateReplyWriter("u" + LayoutSignature);

            try
            {
                writer.WriteUInt32(_revision);

                if (parentId == 0)
                {
                    WriteRoot(ref writer);
                }
                else
                {
                    var item = Find(parentId);
                    if (item == null)
                    {
                        // The spec has no error for "no such item", and a host that asks for one
                        // it saw a moment ago should not be left waiting: an empty node is the
                        // answer.
                        WriteItem(ref writer, -1);
                    }
                    else
                    {
                        WriteItem(ref writer, item);
                    }
                }

                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void WriteRoot(ref MessageWriter writer)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(0);

            var properties = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("children-display");
            writer.WriteVariantString("submenu");
            writer.WriteDictionaryEnd(properties);

            var children = writer.WriteArrayStart(DBusType.Variant);

            foreach (var item in _items)
            {
                if (!item.IsVisible)
                {
                    continue;
                }

                writer.WriteSignature(LayoutSignature);
                WriteItem(ref writer, item);
            }

            writer.WriteArrayEnd(children);
        }

        private static void WriteItem(ref MessageWriter writer, DBusMenuItem item)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(item.Id);

            var properties = writer.WriteDictionaryStart();
            WriteItemProperties(ref writer, item);
            writer.WriteDictionaryEnd(properties);

            var children = writer.WriteArrayStart(DBusType.Variant);
            writer.WriteArrayEnd(children);
        }

        private static void WriteItem(ref MessageWriter writer, int id)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(id);

            var properties = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(properties);

            var children = writer.WriteArrayStart(DBusType.Variant);
            writer.WriteArrayEnd(children);
        }

        private static void WriteItemProperties(ref MessageWriter writer, DBusMenuItem item)
        {
            if (item.IsSeparator)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("type");
                writer.WriteVariantString("separator");
                return;
            }

            writer.WriteDictionaryEntryStart();
            writer.WriteString("label");
            writer.WriteVariantString(item.Label ?? string.Empty);

            writer.WriteDictionaryEntryStart();
            writer.WriteString("enabled");
            writer.WriteVariantBool(item.IsEnabled);

            writer.WriteDictionaryEntryStart();
            writer.WriteString("visible");
            writer.WriteVariantBool(item.IsVisible);
        }

        private void GetGroupProperties(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var ids = reader.ReadArrayOfInt32();

            var writer = context.CreateReplyWriter("a(ia{sv})");

            try
            {
                var array = writer.WriteArrayStart(DBusType.Struct);

                // An empty id list means "all of them", which is how most hosts ask.
                var all = ids == null || ids.Length == 0;

                foreach (var item in _items)
                {
                    if (!all && Array.IndexOf(ids, item.Id) < 0)
                    {
                        continue;
                    }

                    writer.WriteStructureStart();
                    writer.WriteInt32(item.Id);

                    var properties = writer.WriteDictionaryStart();
                    WriteItemProperties(ref writer, item);
                    writer.WriteDictionaryEnd(properties);
                }

                writer.WriteArrayEnd(array);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void GetProperty(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var id = reader.ReadInt32();
            var name = reader.ReadString();

            var item = Find(id);
            if (item == null)
            {
                context.ReplyError("com.canonical.dbusmenu.Error.UnknownItem", id.ToString());
                return;
            }

            using var writer = context.CreateReplyWriter("v");

            switch (name)
            {
                case "label":
                    writer.WriteVariantString(item.Label ?? string.Empty);
                    break;
                case "enabled":
                    writer.WriteVariantBool(item.IsEnabled);
                    break;
                case "visible":
                    writer.WriteVariantBool(item.IsVisible);
                    break;
                case "type":
                    writer.WriteVariantString(item.IsSeparator ? "separator" : "standard");
                    break;
                default:
                    context.ReplyError("com.canonical.dbusmenu.Error.UnknownProperty", name);
                    return;
            }

            context.Reply(writer.CreateMessage());
        }

        private void Event(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var id = reader.ReadInt32();
            var eventId = reader.ReadString();

            Invoke(id, eventId);

            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }

        private void EventGroup(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var array = reader.ReadArrayStart(DBusType.Struct);

            while (reader.HasNext(array))
            {
                reader.AlignStruct();

                var id = reader.ReadInt32();
                var eventId = reader.ReadString();
                reader.ReadVariantValue();
                reader.ReadUInt32();

                Invoke(id, eventId);
            }

            using var writer = context.CreateReplyWriter("ai");
            writer.WriteArray(Array.Empty<int>());
            context.Reply(writer.CreateMessage());
        }

        private void Invoke(int id, string eventId)
        {
            if (!string.Equals(eventId, "clicked", StringComparison.Ordinal))
            {
                // "hovered", "opened" and "closed" also arrive; none of them are actions.
                return;
            }

            var item = Find(id);
            if (item?.Invoked == null || !item.IsEnabled)
            {
                return;
            }

            try
            {
                item.Invoked();
            }
            catch (Exception ex)
            {
                // On the D-Bus reader thread: letting this out kills the connection, and with it
                // the tray icon and the notifications.
                Logger.Error("Tray menu handler failed", ex);
            }
        }

        private DBusMenuItem Find(int id)
        {
            foreach (var item in _items)
            {
                if (item.Id == id)
                {
                    return item;
                }
            }

            return null;
        }

        private static readonly ReadOnlyMemory<byte> IntrospectionXml = System.Text.Encoding.UTF8.GetBytes(
            """
            <interface name="com.canonical.dbusmenu">
              <property name="Version" type="u" access="read"/>
              <property name="TextDirection" type="s" access="read"/>
              <property name="Status" type="s" access="read"/>
              <property name="IconThemePath" type="as" access="read"/>
              <method name="GetLayout">
                <arg type="i" name="parentId" direction="in"/>
                <arg type="i" name="recursionDepth" direction="in"/>
                <arg type="as" name="propertyNames" direction="in"/>
                <arg type="u" name="revision" direction="out"/>
                <arg type="(ia{sv}av)" name="layout" direction="out"/>
              </method>
              <method name="GetGroupProperties">
                <arg type="ai" name="ids" direction="in"/>
                <arg type="as" name="propertyNames" direction="in"/>
                <arg type="a(ia{sv})" name="properties" direction="out"/>
              </method>
              <method name="GetProperty">
                <arg type="i" name="id" direction="in"/>
                <arg type="s" name="name" direction="in"/>
                <arg type="v" name="value" direction="out"/>
              </method>
              <method name="Event">
                <arg type="i" name="id" direction="in"/>
                <arg type="s" name="eventId" direction="in"/>
                <arg type="v" name="data" direction="in"/>
                <arg type="u" name="timestamp" direction="in"/>
              </method>
              <method name="EventGroup">
                <arg type="a(isvu)" name="events" direction="in"/>
                <arg type="ai" name="idErrors" direction="out"/>
              </method>
              <method name="AboutToShow">
                <arg type="i" name="id" direction="in"/>
                <arg type="b" name="needUpdate" direction="out"/>
              </method>
              <method name="AboutToShowGroup">
                <arg type="ai" name="ids" direction="in"/>
                <arg type="ai" name="updatesNeeded" direction="out"/>
                <arg type="ai" name="idErrors" direction="out"/>
              </method>
              <signal name="ItemsPropertiesUpdated">
                <arg type="a(ia{sv})" name="updatedProps"/>
                <arg type="a(ias)" name="removedProps"/>
              </signal>
              <signal name="LayoutUpdated">
                <arg type="u" name="revision"/>
                <arg type="i" name="parent"/>
              </signal>
              <signal name="ItemActivationRequested">
                <arg type="i" name="id"/>
                <arg type="u" name="timestamp"/>
              </signal>
            </interface>
            """);
    }
}
