//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Telegram.Services
{
    /// <summary>
    /// Whether the machine is asking applications to go easy on the battery, which is what
    /// <c>PowerSavingPolicy</c> with <c>Mode == Auto</c> turns into "stop autoplaying videos, GIFs
    /// and stickers, and stop animating".
    ///
    /// <para>On Windows the answer is <c>PowerManager.EnergySaverStatus</c>, one property with
    /// three states. Uno does not implement it, and the placeholder that stood in its place was a
    /// literal <c>false</c>: <c>PowerSavingPolicy.Status</c> answered <c>Off</c> for the whole life
    /// of the process, so the setting existed in the interface and commanded nothing.</para>
    ///
    /// <para>The Linux equivalent of "battery saver is on" is <b>not</b> "the machine is on
    /// battery" — that would be more aggressive than Windows, which keeps animating on battery
    /// until the saver actually engages. It is power-profiles-daemon's <c>ActiveProfile</c> being
    /// <c>power-saver</c>: the profile the user picks from the battery menu, and the one GNOME
    /// switches to on its own at low battery
    /// (<c>org.gnome.settings-daemon.plugins.power power-saver-profile-on-low-battery</c>), which
    /// is the same automatic behaviour Windows has. That daemon ships on Fedora, Ubuntu, Debian
    /// and openSUSE and is what GNOME and KDE both drive.</para>
    ///
    /// <para>Two things make this its own connection rather than a user of
    /// <see cref="DBusSession"/>: power-profiles-daemon lives on the <b>system</b> bus, and nothing
    /// here is exported, owned or subscribed on behalf of the app — so there is no registrar to
    /// replay and no reason to widen the session helper's contract. What is kept from it is the
    /// rule that matters: <b>nothing here throws at the caller</b>. A machine with no system bus,
    /// no daemon, or a daemon that refuses to answer reports "not supported" and "not saving",
    /// which is exactly the behaviour that was there before this file existed.</para>
    ///
    /// <para><c>UNIGRAM_POWER_SAVING=on|off</c> forces the answer without touching the machine's
    /// real profile, which is how this gets exercised without changing a setting of the user's.
    /// Anything else (including unset) means "ask the daemon".</para>
    /// </summary>
    public static class PowerProfile
    {
        // power-profiles-daemon owns both names from 0.20 on and only the first one before it, so
        // they are tried in that order: the newer name on a new daemon, the older one everywhere
        // else. The interface name is the bus name in both cases.
        private const string ServiceNew = "org.freedesktop.UPower.PowerProfiles";
        private const string PathNew = "/org/freedesktop/UPower/PowerProfiles";

        private const string ServiceOld = "net.hadess.PowerProfiles";
        private const string PathOld = "/net/hadess/PowerProfiles";

        private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
        private const string ActiveProfile = "ActiveProfile";

        // The profile name is part of the daemon's public API and is not localized.
        private const string PowerSaver = "power-saver";

        private static readonly SemaphoreSlim _gate = new(1, 1);

        private static DBusConnection _connection;
        private static bool _disabled;
        private static bool _started;

        private static string _service;
        private static string _path;

        private static volatile bool _isEnergySaver;
        private static volatile bool _isSupported;

        private static bool? _forced;
        private static bool _forcedRead;

        /// <summary>
        /// True while the desktop is asking for power saving. False before the first answer and on
        /// every machine that does not publish a power profile, which is the safe direction to be
        /// wrong in: it animates.
        /// </summary>
        public static bool IsEnergySaver
        {
            get
            {
                Start();
                return _isEnergySaver;
            }
        }

        /// <summary>
        /// Whether the number above is coming from the desktop at all. False until the first
        /// successful read, so it is a report and not a precondition — the same shape
        /// <see cref="IdleMonitor.IsAvailable"/> has.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                Start();
                return _isSupported;
            }
        }

        /// <summary>
        /// Raised, off the UI thread, whenever the answer changes. Subscribers have to marshal.
        /// </summary>
        public static event EventHandler Changed;

        /// <summary>
        /// Begins watching. Called on its own by the first read, so nothing has to remember to
        /// start it, and cheap to call again.
        /// </summary>
        public static void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;

            if (Forced is bool forced)
            {
                Logger.Info($"UNIGRAM_POWER_SAVING forces power saving {(forced ? "on" : "off")}");

                _isSupported = true;
                _isEnergySaver = forced;
                return;
            }

            _ = InitializeAsync();
        }

        private static bool? Forced
        {
            get
            {
                if (_forcedRead)
                {
                    return _forced;
                }

                _forcedRead = true;

                try
                {
                    var value = Environment.GetEnvironmentVariable("UNIGRAM_POWER_SAVING");
                    if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) || value == "1")
                    {
                        _forced = true;
                    }
                    else if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) || value == "0")
                    {
                        _forced = false;
                    }
                }
                catch
                {
                    // No environment to read: nothing is forced.
                }

                return _forced;
            }
        }

        private static async Task InitializeAsync()
        {
            try
            {
                var connection = await ConnectAsync().ConfigureAwait(false);
                if (connection == null)
                {
                    return;
                }

                // Which of the two names answers is decided once, by asking for the property.
                if (await TryAdoptAsync(connection, ServiceNew, PathNew).ConfigureAwait(false)
                    || await TryAdoptAsync(connection, ServiceOld, PathOld).ConfigureAwait(false))
                {
                    await SubscribeAsync(connection).ConfigureAwait(false);
                    return;
                }

                // Said once, and as information rather than as an error: a desktop without
                // power-profiles-daemon is the normal case on a machine that is not a laptop.
                Logger.Info("No power-profiles-daemon: \"Power saving -> Automatic\" has nothing to follow");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Cannot read the power profile: {ex.Message}");
            }
        }

        private static async Task<DBusConnection> ConnectAsync()
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

                var address = DBusAddress.System;
                if (string.IsNullOrEmpty(address))
                {
                    _disabled = true;
                    return null;
                }

                // AutoConnect off for the same reason DBusSession keeps it off: a silent reconnect
                // under the caller would come back without the match rule below, and a match rule
                // that is gone is a value that stops changing without anybody noticing.
                var options = new DBusConnectionOptions(address) { AutoConnect = false };
                var candidate = new DBusConnection(options);

                await candidate.ConnectAsync().ConfigureAwait(false);

                _connection = candidate;
                return candidate;
            }
            catch (Exception ex)
            {
                Logger.Info($"No system bus, so no power profile: {ex.Message}");
                _disabled = true;
                return null;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task<bool> TryAdoptAsync(DBusConnection connection, string service, string path)
        {
            try
            {
                var profile = await connection.CallMethodAsync(Compose(connection, service, path),
                    static (Message message, object _) => message.GetBodyReader().ReadVariantValue().GetString()).ConfigureAwait(false);

                _service = service;
                _path = path;

                _isSupported = true;
                Logger.Info($"Power profile from {service}: {profile}");

                Apply(profile);
                return true;
            }
            catch
            {
                // Not this name. The caller tries the other one, and if neither answers the
                // feature reports itself unsupported.
                return false;
            }
        }

        private static async Task SubscribeAsync(DBusConnection connection)
        {
            // The body is deliberately not parsed. PropertiesChanged carries a{sv} and the only
            // key that matters here is a string, so re-reading the property costs one round trip
            // on a signal that arrives when somebody switches profile -- a handful of times a day
            // at most -- and removes a hand-written dictionary parser from the path.
            await connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = _service,
                    Path = _path,
                    Interface = PropertiesInterface,
                    Member = "PropertiesChanged",
                    Arg0 = _service
                },
                static (Message message, object _) => (object)null,
                static (Exception ex, object value, object _, object __) =>
                {
                    if (ex == null)
                    {
                        _ = RefreshAsync();
                    }
                },
                ObserverFlags.None, null, null, false).ConfigureAwait(false);

            // A daemon that restarts comes back with a profile we never read. This is the same
            // watch StatusNotifierItem keeps on its own service, for the same reason.
            await connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = "org.freedesktop.DBus",
                    Path = "/org/freedesktop/DBus",
                    Interface = "org.freedesktop.DBus",
                    Member = "NameOwnerChanged",
                    Arg0 = _service
                },
                static (Message message, object _) =>
                {
                    var reader = message.GetBodyReader();
                    reader.ReadString();
                    reader.ReadString();
                    return reader.ReadString();
                },
                static (Exception ex, string owner, object _, object __) =>
                {
                    if (ex == null && !string.IsNullOrEmpty(owner))
                    {
                        _ = RefreshAsync();
                    }
                },
                ObserverFlags.None, null, null, false).ConfigureAwait(false);
        }

        private static async Task RefreshAsync()
        {
            try
            {
                var connection = _connection;
                if (connection == null || _service == null)
                {
                    return;
                }

                var profile = await connection.CallMethodAsync(Compose(connection, _service, _path),
                    static (Message message, object _) => message.GetBodyReader().ReadVariantValue().GetString()).ConfigureAwait(false);

                Apply(profile);
            }
            catch (Exception ex)
            {
                // Not zeroing what was already measured: an answer that stops arriving is a reason
                // to stop trusting the number, not a reason to claim the saver just turned off.
                Logger.Warning($"Cannot re-read the power profile: {ex.Message}");
            }
        }

        private static void Apply(string profile)
        {
            var value = string.Equals(profile, PowerSaver, StringComparison.Ordinal);
            if (value == _isEnergySaver)
            {
                return;
            }

            _isEnergySaver = value;
            Logger.Info($"Power saving is now {(value ? "on" : "off")} (profile: {profile})");

            try
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // A subscriber that throws must not take the bus reader thread down with it.
                Logger.Error("Power profile subscriber failed", ex);
            }
        }

        private static MessageBuffer Compose(DBusConnection connection, string service, string path)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(service, path, PropertiesInterface, "Get", "ss");
            writer.WriteString(service);
            writer.WriteString(ActiveProfile);
            return writer.CreateMessage();
        }
    }
}
