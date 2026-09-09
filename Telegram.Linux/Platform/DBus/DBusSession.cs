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
    /// <summary>
    /// The one session bus connection of the process, and the only place that knows whether there
    /// is a bus at all.
    /// <para>Everything the desktop integration needs on Linux — notifications, the launcher
    /// counter, the tray icon and its menu — is D-Bus, and on Windows all of it was either a UWP
    /// API or the <c>Telegram.Stub</c> helper process talking over an
    /// <c>AppServiceConnection</c>. Here it is one connection inside this same process: there is no
    /// second process to keep alive, to relaunch or to kill.</para>
    /// <para>Two rules for callers, both of which the whole layer above depends on:</para>
    /// <list type="bullet">
    /// <item>Nothing here throws at the caller. A machine without a session bus (a TTY, a broken
    /// <c>DBUS_SESSION_BUS_ADDRESS</c>, a sandbox) has to lose notifications, not the app.</item>
    /// <item>Whatever has to exist on the bus for the app to be reachable — exported objects, owned
    /// names, subscriptions — is registered through <see cref="AddRegistrarAsync"/>, so it can be
    /// replayed if the connection is ever remade. A connection that comes back without the tray
    /// object exported is a tray icon that is on screen and answers nothing.</item>
    /// </list>
    /// </summary>
    public static class DBusSession
    {
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static readonly List<Func<DBusConnection, Task>> _registrars = new();

        private static DBusConnection _connection;
        private static bool _disabled;

        /// <summary>
        /// Whether the environment names a session bus at all. Cheap, and false long before any
        /// connection is attempted, so callers can skip building payloads nobody will read.
        /// </summary>
        public static bool IsConfigured
        {
            get
            {
                try
                {
                    return !string.IsNullOrEmpty(DBusAddress.Session);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The connected session bus, or null if there is none. Safe to call from anywhere and as
        /// often as wanted: the connection is made once and handed out afterwards.
        /// </summary>
        public static async Task<DBusConnection> ConnectAsync()
        {
            if (_disabled)
            {
                return null;
            }

            var connection = _connection;
            if (connection != null)
            {
                return connection;
            }

            await _gate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_connection != null)
                {
                    return _connection;
                }

                if (_disabled)
                {
                    return null;
                }

                var address = DBusAddress.Session;
                if (string.IsNullOrEmpty(address))
                {
                    Logger.Warning("No session bus address: desktop integration is off");
                    _disabled = true;
                    return null;
                }

                // AutoConnect is deliberately off. It reconnects under the caller, which is fine
                // for a client that only makes calls, and wrong for us: the tray item and its menu
                // are objects EXPORTED on this connection, and a silent reconnect would bring back
                // a connection that owns no name and answers no method. Connecting here, once,
                // keeps the replay of the registrars below in one place.
                var options = new DBusConnectionOptions(address) { AutoConnect = false };
                var candidate = new DBusConnection(options);

                await candidate.ConnectAsync().ConfigureAwait(false);

                Func<DBusConnection, Task>[] registrars;
                lock (_registrars)
                {
                    registrars = _registrars.ToArray();
                }

                foreach (var registrar in registrars)
                {
                    try
                    {
                        await registrar(candidate).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("D-Bus registrar failed", ex);
                    }
                }

                _connection = candidate;
                Logger.Info($"Session bus connected as {candidate.UniqueName}");

                return candidate;
            }
            catch (Exception ex)
            {
                // A bus that refuses the handshake is not going to accept the next call either;
                // retrying on every notification would cost a socket per message.
                Logger.Error("Cannot connect to the session bus, desktop integration is off", ex);
                _disabled = true;
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Registers something that has to exist on the bus (an exported object, an owned name, a
        /// match rule) and runs it against the current connection. Kept so it can be replayed.
        /// </summary>
        public static async Task<bool> AddRegistrarAsync(Func<DBusConnection, Task> registrar)
        {
            // The connection is made BEFORE the registrar is recorded, and the order is the whole
            // point: ConnectAsync replays every registrar it finds on a connection it has just
            // made, so recording first would run this one TWICE -- once in that replay and once
            // below. Nothing noticed for a while because AddMatchAsync and TryRequestNameAsync
            // survive being called twice, but AddMethodHandler does not: Tmds throws
            // "A method handler is already registered for the path ...". The exception is caught
            // here, so the symptom was never a crash -- it was AddRegistrarAsync answering FALSE
            // for something that had in fact been exported by the replay, which is a caller
            // believing it has no tray icon, no MPRIS player and no single-instance object.
            // Measured on a cold connection in unigram-linux/spikes/MprisSpike.
            var connection = await ConnectAsync().ConfigureAwait(false);

            lock (_registrars)
            {
                _registrars.Add(registrar);
            }

            if (connection == null)
            {
                return false;
            }

            try
            {
                await registrar(connection).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("D-Bus registrar failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Calls a method and drops the reply. Every failure is a log line: a desktop that does not
        /// implement the interface is the normal case, not an error the app has to handle.
        /// </summary>
        public static async Task<bool> CallAsync(Func<DBusConnection, MessageBuffer> compose)
        {
            var connection = await ConnectAsync().ConfigureAwait(false);
            if (connection == null)
            {
                return false;
            }

            try
            {
                await connection.CallMethodAsync(compose(connection)).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"D-Bus call failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Emits a signal. Signals have no reply, so this is fire and forget by design.
        /// </summary>
        public static async Task<bool> EmitAsync(Func<DBusConnection, MessageBuffer> compose)
        {
            var connection = await ConnectAsync().ConfigureAwait(false);
            if (connection == null)
            {
                return false;
            }

            try
            {
                connection.TrySendMessage(compose(connection));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"D-Bus signal failed: {ex.Message}");
                return false;
            }
        }
    }
}
