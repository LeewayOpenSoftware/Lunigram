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
    /// The unread counter on the launcher icon, over <c>com.canonical.Unity.LauncherEntry</c>.
    /// This is what replaces <c>BadgeUpdateManager.CreateBadgeUpdaterForApplication</c>.
    ///
    /// <para>The protocol is a single broadcast signal — there is no object to call, no service to
    /// find and nothing that answers. Whoever draws the counter (Ubuntu's dock and Dash to Dock,
    /// Plank, KDE's task manager, Latte) listens for it and matches the app by the
    /// <c>application://&lt;desktop file id&gt;</c> URI, which is why <see cref="DesktopEntry"/>
    /// has to have written the file first.</para>
    ///
    /// <para>Consequence worth knowing: emitting a signal always "succeeds", so this class can
    /// never report that the counter is not being drawn. What can be checked, and what the spike
    /// checks, is that the signal leaves with the right body.</para>
    /// </summary>
    public static class LauncherEntry
    {
        private const string Interface = "com.canonical.Unity.LauncherEntry";
        private const string Member = "Update";

        private static readonly string Path = "/com/canonical/unity/launcherentry/" + Hash(DesktopEntry.ApplicationUri).ToString();

        private static int _count = -1;
        private static bool _subscribed;

        /// <summary>
        /// Sets the number on the icon. Zero hides it, which is what <c>updater.Clear()</c> did.
        /// Repeated values cost nothing: the bus is only touched when the number changes.
        /// </summary>
        public static Task SetCountAsync(int count)
        {
            if (count < 0)
            {
                count = 0;
            }

            if (_count == count)
            {
                return Task.CompletedTask;
            }

            _count = count;
            return EmitAsync(count);
        }

        /// <summary>
        /// Re-sends the current value. Needed because the protocol has no state: a dock that starts
        /// after the app (or a shell that restarts) has never heard the last Update.
        /// </summary>
        public static Task RefreshAsync()
        {
            return _count < 0 ? Task.CompletedTask : EmitAsync(_count);
        }

        private static async Task EmitAsync(int count)
        {
            await SubscribeAsync().ConfigureAwait(false);

            await DBusSession.EmitAsync(connection =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(null, Path, Interface, Member, "sa{sv}");
                writer.WriteString(DesktopEntry.ApplicationUri);

                var properties = writer.WriteDictionaryStart();

                writer.WriteDictionaryEntryStart();
                writer.WriteString("count");
                writer.WriteVariantInt64(count);

                writer.WriteDictionaryEntryStart();
                writer.WriteString("count-visible");
                writer.WriteVariantBool(count > 0);

                writer.WriteDictionaryEnd(properties);

                return writer.CreateMessage();
            }).ConfigureAwait(false);
        }

        private static async Task SubscribeAsync()
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;

            // When the thing that draws the counter appears (the shell restarting, a dock being
            // enabled), it has missed every Update sent so far. This re-sends the last one.
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
                        Arg0 = "com.canonical.Unity"
                    },
                    static (Message message, object _) =>
                    {
                        var reader = message.GetBodyReader();
                        reader.ReadString();                // name
                        reader.ReadString();                // old owner
                        return reader.ReadString();         // new owner
                    },
                    static (Exception ex, string owner, object _, object __) =>
                    {
                        if (ex == null && !string.IsNullOrEmpty(owner))
                        {
                            _ = RefreshAsync();
                        }
                    },
                    ObserverFlags.None, null, null, false).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// The object path a libunity client would use: the same djb2 hash of the application URI
        /// that <c>g_str_hash</c> computes. Listeners match on the interface, not the path, but
        /// using the canonical one keeps the traffic indistinguishable from any other launcher
        /// client's.
        /// </summary>
        private static uint Hash(string value)
        {
            uint hash = 5381;

            foreach (var c in value)
            {
                hash = hash * 33 + c;
            }

            return hash;
        }
    }
}
