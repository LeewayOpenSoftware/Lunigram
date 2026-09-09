//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Telegram.Services
{
    /// <summary>What the desktop is being told is going on. MPRIS spells these three exactly.</summary>
    public enum MprisPlaybackStatus
    {
        Stopped,
        Playing,
        Paused
    }

    /// <summary>MPRIS <c>LoopStatus</c>, which is upstream's three repeat modes under other names.</summary>
    public enum MprisLoopStatus
    {
        None,
        Track,
        Playlist
    }

    /// <summary>
    /// Everything the panel shows and every capability it enables, in one object so the player can
    /// diff a whole update at once and emit a single <c>PropertiesChanged</c> instead of one per
    /// field. Deliberately made of strings, numbers and booleans: nothing here knows what a chat or
    /// a message is.
    /// </summary>
    public sealed class MprisSnapshot
    {
        public MprisPlaybackStatus Status { get; set; }

        /// <summary>
        /// Opaque token identifying the track. Turned into the object path MPRIS wants; null or
        /// empty means "no track", which is the reserved path.
        /// </summary>
        public string TrackId { get; set; }

        public string Title { get; set; }

        public string Artist { get; set; }

        public string Album { get; set; }

        /// <summary>A local file path or a URI. A path is turned into <c>file://</c> here.</summary>
        public string ArtUrl { get; set; }

        public TimeSpan Length { get; set; }

        public bool CanGoNext { get; set; }

        public bool CanGoPrevious { get; set; }

        public bool CanPlay { get; set; }

        public bool CanPause { get; set; }

        public bool CanSeek { get; set; }

        public double Volume { get; set; } = 1;

        public double Rate { get; set; } = 1;

        public MprisLoopStatus Loop { get; set; }

        public bool Shuffle { get; set; }

        /// <summary>
        /// <see cref="TrackId"/> turned into the object path MPRIS wants. Kept inside the snapshot
        /// rather than beside it so a reader on the bus thread cannot pair the new path with the
        /// old title: the whole state is published in one reference assignment.
        /// </summary>
        internal string Path { get; set; }

        public MprisSnapshot Clone()
        {
            return (MprisSnapshot)MemberwiseClone();
        }
    }

    /// <summary>
    /// The media player of the desktop, over <c>org.mpris.MediaPlayer2</c> and
    /// <c>org.mpris.MediaPlayer2.Player</c> at <c>/org/mpris/MediaPlayer2</c>.
    ///
    /// <para>This is the Linux <c>SystemMediaTransportControls</c>. On Windows the shared
    /// <c>Services/PlaybackService.cs</c> hands the OS a title, an artist, a thumbnail and a set of
    /// enabled buttons, and the OS draws the flyout and routes the media keys. Here the same six
    /// things are D-Bus properties that GNOME Shell (the media section of the calendar popover),
    /// <c>gsd-media-keys</c> (the XF86Audio* keys) and bluez's <c>mpris-proxy</c> (the buttons on a
    /// headset) read and act on. The app draws nothing and grabs no key.</para>
    ///
    /// <para><b>Blind on purpose</b>, like the rest of <c>Platform/DBus/</c>: strings, numbers,
    /// booleans and callbacks. What knows about playlists and messages is the bridge in
    /// <c>Platform/MediaTransport.cs</c>, and what feeds it is upstream's own
    /// <c>PlaybackService</c> through the <c>SystemMediaTransportControls</c> declared in
    /// <c>Xaml/</c> — which is why not a line of the shared service had to learn about D-Bus. It is
    /// also what lets the whole object be exercised from a console
    /// (<c>unigram-linux/spikes/MprisSpike</c>).</para>
    ///
    /// <para><b>Two rules of the spec that shape the code.</b> Properties are read on demand, so
    /// they have to be answerable at any time from the bus thread — hence the immutable snapshot
    /// swapped in one reference assignment rather than a dozen fields. And <c>Position</c> is
    /// explicitly NOT part of <c>PropertiesChanged</c>: a player that emitted it would flood the
    /// bus at its own tick rate, so position is pulled through
    /// <see cref="PositionProvider"/> when someone asks and pushed only as the <c>Seeked</c> signal
    /// when it jumps.</para>
    /// </summary>
    public sealed class MprisPlayer : IPathMethodHandler
    {
        public const string ObjectPath = "/org/mpris/MediaPlayer2";

        private const string RootInterface = "org.mpris.MediaPlayer2";
        private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
        private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

        /// <summary>
        /// The reserved path for "nothing is playing". Any other track id has to be a valid object
        /// path, which is why <see cref="TrackPath"/> escapes instead of interpolating.
        /// </summary>
        private const string NoTrack = "/org/mpris/MediaPlayer2/TrackList/NoTrack";

        private const string TrackPrefix = "/org/unigram/Track/";

        public static MprisPlayer Current { get; } = new MprisPlayer();

        private readonly string _busName;
        private readonly object _dirtyLock = new();
        private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

        private MprisSnapshot _state = new() { Path = NoTrack };
        private bool _registered;

        private long _calls;
        private long _propertyReads;
        private string _lastSender;

        private MprisPlayer()
        {
            // The spec's own recommendation for an app that can run more than once: a well known
            // name that stays unique. Two Unigram sessions on one desktop then show up as two
            // players instead of fighting over one name.
            _busName = string.Format(CultureInfo.InvariantCulture, "org.mpris.MediaPlayer2.Unigram.instance{0}", Environment.ProcessId);
        }

        public string Path => ObjectPath;

        public bool HandlesChildPaths => false;

        /// <summary>The name this player owns on the session bus. Diagnostics and the spike.</summary>
        public string BusName => _busName;

        /// <summary>Whether the name was actually taken. False means no media controls, not an error.</summary>
        public bool IsRegistered => _registered;

        /// <summary>
        /// Current position, asked for at the moment the desktop wants it. Null answers zero, which
        /// is what a player with nothing loaded reports.
        /// <para>Called on the D-Bus reader thread: it must not block and must not touch the visual
        /// tree. Reading <c>IPlaybackService.Position</c> is exactly that.</para>
        /// </summary>
        public Func<TimeSpan> PositionProvider { get; set; }

        /// <summary>Raise: the panel wants the window in front.</summary>
        public event EventHandler Raised;

        /// <summary>Quit: the panel wants the app gone.</summary>
        public event EventHandler Quitted;

        public event EventHandler PlayPauseRequested;
        public event EventHandler PlayRequested;
        public event EventHandler PauseRequested;
        public event EventHandler StopRequested;
        public event EventHandler NextRequested;
        public event EventHandler PreviousRequested;

        /// <summary>Seek: a RELATIVE offset, which is what the spec sends (it can be negative).</summary>
        public event EventHandler<TimeSpan> SeekRequested;

        /// <summary>SetPosition: an ABSOLUTE position, already checked against the current track.</summary>
        public event EventHandler<TimeSpan> PositionRequested;

        public event EventHandler<double> VolumeRequested;
        public event EventHandler<double> RateRequested;
        public event EventHandler<MprisLoopStatus> LoopStatusRequested;
        public event EventHandler<bool> ShuffleRequested;

        /// <summary>
        /// Raised for every request that reaches the object, with the unique name of whoever sent
        /// it. Diagnostics: it is how the spike proves that GNOME Shell and <c>gsd-media-keys</c>
        /// really did find the player, which is the only observable difference between "the media
        /// keys will work" and "we exported an object nobody reads".
        /// </summary>
        public event EventHandler<string> Inspected;

        /// <summary>How many requests arrived, how many were property reads, and who asked last.</summary>
        public (long Calls, long PropertyReads, string LastSender) Diagnostics
            => (Interlocked.Read(ref _calls), Interlocked.Read(ref _propertyReads), _lastSender);

        /// <summary>
        /// Exports the object and takes the name. False means this desktop has no session bus, and
        /// then the app simply has no media controls — same contract as the tray icon.
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

                // ReplaceExisting because a previous run of the SAME pid cannot exist, but a stale
                // name from a crashed instance with a recycled pid can; losing the race here would
                // leave a player nobody can address.
                var owned = await connection.TryRequestNameAsync(_busName, RequestNameOptions.ReplaceExisting).ConfigureAwait(false);
                if (!owned)
                {
                    Logger.Warning($"MPRIS name {_busName} refused: media keys will not reach Unigram");
                }
            }).ConfigureAwait(false);

            _registered = ok;

            if (ok)
            {
                Logger.Info($"MPRIS player exported at {ObjectPath} as {_busName}");
            }

            return ok;
        }

        /// <summary>
        /// Takes a whole new state, works out what actually changed and emits one
        /// <c>PropertiesChanged</c> for it. Nothing changed means nothing is sent — which matters
        /// because upstream re-applies the same metadata on every album-cover update.
        /// </summary>
        public Task UpdateAsync(MprisSnapshot state)
        {
            state ??= new MprisSnapshot();

            var previous = _state;
            var next = state.Clone();

            next.Path = TrackPath(next.TrackId);

            var metadataChanged = next.Path != previous.Path
                || previous.Title != next.Title
                || previous.Artist != next.Artist
                || previous.Album != next.Album
                || previous.ArtUrl != next.ArtUrl
                || previous.Length != next.Length;

            lock (_dirtyLock)
            {
                if (previous.Status != next.Status)
                {
                    _dirty.Add("PlaybackStatus");
                }

                if (metadataChanged)
                {
                    _dirty.Add("Metadata");
                }

                if (previous.CanGoNext != next.CanGoNext)
                {
                    _dirty.Add("CanGoNext");
                }

                if (previous.CanGoPrevious != next.CanGoPrevious)
                {
                    _dirty.Add("CanGoPrevious");
                }

                if (previous.CanPlay != next.CanPlay)
                {
                    _dirty.Add("CanPlay");
                }

                if (previous.CanPause != next.CanPause)
                {
                    _dirty.Add("CanPause");
                }

                if (previous.CanSeek != next.CanSeek)
                {
                    _dirty.Add("CanSeek");
                }

                // Doubles compared exactly on purpose: both sides come from the same stored value,
                // so a difference here is a real change and not rounding.
                if (previous.Volume != next.Volume)
                {
                    _dirty.Add("Volume");
                }

                if (previous.Rate != next.Rate)
                {
                    _dirty.Add("Rate");
                }

                if (previous.Loop != next.Loop)
                {
                    _dirty.Add("LoopStatus");
                }

                if (previous.Shuffle != next.Shuffle)
                {
                    _dirty.Add("Shuffle");
                }
            }

            _state = next;

            return FlushAsync();
        }

        /// <summary>
        /// The <c>Seeked</c> signal: the ONLY way the spec has of saying "the position jumped".
        /// Emitted after a seek the desktop asked for and after one the user made inside the app,
        /// so a panel that draws a scrubber does not have to poll.
        /// </summary>
        public Task EmitSeekedAsync(TimeSpan position)
        {
            if (!_registered)
            {
                return Task.CompletedTask;
            }

            return DBusSession.EmitAsync(connection =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(null, ObjectPath, PlayerInterface, "Seeked", "x");
                writer.WriteInt64(ToMicroseconds(position));
                return writer.CreateMessage();
            });
        }

        private Task FlushAsync()
        {
            string[] names;

            // Checked BEFORE the set is drained: an update that arrives while the object is not
            // yet on the bus has to leave its dirt behind, so the first signal after registration
            // carries everything and not just whatever changed at that instant.
            if (!_registered)
            {
                return Task.CompletedTask;
            }

            lock (_dirtyLock)
            {
                if (_dirty.Count == 0)
                {
                    return Task.CompletedTask;
                }

                names = new string[_dirty.Count];
                _dirty.CopyTo(names);
                _dirty.Clear();
            }

            return DBusSession.EmitAsync(connection =>
            {
                // Not `using var`: a using local is read-only and MessageWriter has to go into
                // WritePlayerValue by ref (it is a ref struct -- by value the helper would advance
                // its own copy of the position and this signal would go out malformed).
                var writer = connection.GetMessageWriter();

                try
                {
                    writer.WriteSignalHeader(null, ObjectPath, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
                    writer.WriteString(PlayerInterface);

                    var dictionary = writer.WriteDictionaryStart();

                    for (int i = 0; i < names.Length; i++)
                    {
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString(names[i]);
                        WritePlayerValue(ref writer, names[i]);
                    }

                    writer.WriteDictionaryEnd(dictionary);

                    // No invalidated properties: everything this object has is cheap to send, and
                    // a host that had to re-Get them would do it on the bus thread of the panel.
                    writer.WriteArray(Array.Empty<string>());

                    return writer.CreateMessage();
                }
                finally
                {
                    writer.Dispose();
                }
            });
        }

        #region Requests

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;

            Interlocked.Increment(ref _calls);

            var sender = request.SenderAsString;
            if (sender != null)
            {
                _lastSender = sender;

                try
                {
                    Inspected?.Invoke(this, sender);
                }
                catch (Exception ex)
                {
                    Logger.Error("MPRIS diagnostics handler failed", ex);
                }
            }

            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml(new ReadOnlyMemory<byte>[] { RootIntrospectionXml, PlayerIntrospectionXml });
                return default;
            }

            if (context.IsPropertiesInterfaceRequest)
            {
                HandleProperties(context);
                return default;
            }

            var @interface = request.InterfaceAsString;

            if (@interface == RootInterface)
            {
                switch (request.MemberAsString)
                {
                    case "Raise":
                        Raise(Raised);
                        ReplyEmpty(context);
                        break;
                    case "Quit":
                        Raise(Quitted);
                        ReplyEmpty(context);
                        break;
                    default:
                        context.ReplyUnknownMethodError();
                        break;
                }

                return default;
            }

            if (@interface != PlayerInterface)
            {
                context.ReplyUnknownMethodError();
                return default;
            }

            switch (request.MemberAsString)
            {
                case "Play":
                    Raise(PlayRequested);
                    ReplyEmpty(context);
                    break;
                case "Pause":
                    Raise(PauseRequested);
                    ReplyEmpty(context);
                    break;
                case "PlayPause":
                    Raise(PlayPauseRequested);
                    ReplyEmpty(context);
                    break;
                case "Stop":
                    Raise(StopRequested);
                    ReplyEmpty(context);
                    break;
                case "Next":
                    Raise(NextRequested);
                    ReplyEmpty(context);
                    break;
                case "Previous":
                    Raise(PreviousRequested);
                    ReplyEmpty(context);
                    break;
                case "Seek":
                    Seek(context);
                    break;
                case "SetPosition":
                    SetPosition(context);
                    break;
                case "OpenUri":
                    // Answered rather than refused: the property that describes it
                    // (SupportedUriSchemes) is empty, so a host that calls this anyway is asking
                    // for something it was told does not exist.
                    ReplyEmpty(context);
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }

            return default;
        }

        private void Seek(MethodContext context)
        {
            var offset = context.Request.GetBodyReader().ReadInt64();
            ReplyEmpty(context);

            Raise(SeekRequested, FromMicroseconds(offset));
        }

        private void SetPosition(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var track = reader.ReadObjectPath().ToString();
            var position = reader.ReadInt64();

            ReplyEmpty(context);

            // The spec is explicit: a SetPosition naming a track that is no longer the current one
            // has to be IGNORED. Without this check a panel that clicked its scrubber a moment
            // after the track changed would seek the new track to the old track's offset.
            var current = _state.Path;

            if (track != current)
            {
                Logger.Warning($"MPRIS SetPosition for {track} ignored: the current track is {current}");
                return;
            }

            Raise(PositionRequested, FromMicroseconds(position));
        }

        private void Raise(EventHandler handler)
        {
            try
            {
                handler?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // The bus thread is the reader of the whole connection: letting this out would
                // take the tray icon and the notifications down with the media keys.
                Logger.Error("MPRIS handler failed", ex);
            }
        }

        private void Raise<T>(EventHandler<T> handler, T argument)
        {
            try
            {
                handler?.Invoke(this, argument);
            }
            catch (Exception ex)
            {
                Logger.Error("MPRIS handler failed", ex);
            }
        }

        private static void ReplyEmpty(MethodContext context)
        {
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }

        #endregion

        #region Properties

        private static readonly string[] RootPropertyNames =
        {
            "CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry",
            "SupportedUriSchemes", "SupportedMimeTypes", "Fullscreen", "CanSetFullscreen"
        };

        private static readonly string[] PlayerPropertyNames =
        {
            "PlaybackStatus", "LoopStatus", "Rate", "Shuffle", "Metadata", "Volume", "Position",
            "MinimumRate", "MaximumRate", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause",
            "CanSeek", "CanControl"
        };

        private void HandleProperties(MethodContext context)
        {
            var request = context.Request;
            var reader = request.GetBodyReader();
            var @interface = reader.ReadString();

            var root = @interface == RootInterface;
            if (!root && @interface != PlayerInterface)
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface", @interface);
                return;
            }

            var names = root ? RootPropertyNames : PlayerPropertyNames;

            switch (request.MemberAsString)
            {
                case "GetAll":
                    Interlocked.Increment(ref _propertyReads);
                    GetAll(context, root, names);
                    break;
                case "Get":
                    Interlocked.Increment(ref _propertyReads);
                    Get(context, root, names, reader.ReadString());
                    break;
                case "Set":
                    Set(context, root, reader.ReadString(), reader.ReadVariantValue());
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private void GetAll(MethodContext context, bool root, string[] names)
        {
            var writer = context.CreateReplyWriter("a{sv}");

            try
            {
                var dictionary = writer.WriteDictionaryStart();

                for (int i = 0; i < names.Length; i++)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(names[i]);

                    // MessageWriter is a ref struct: handed over by value the helper would write
                    // into its own copy of the position and this message would be silently
                    // malformed. Every one of these goes by ref.
                    if (root)
                    {
                        WriteRootValue(ref writer, names[i]);
                    }
                    else
                    {
                        WritePlayerValue(ref writer, names[i]);
                    }
                }

                writer.WriteDictionaryEnd(dictionary);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void Get(MethodContext context, bool root, string[] names, string name)
        {
            if (Array.IndexOf(names, name) < 0)
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                return;
            }

            var writer = context.CreateReplyWriter("v");

            try
            {
                if (root)
                {
                    WriteRootValue(ref writer, name);
                }
                else
                {
                    WritePlayerValue(ref writer, name);
                }

                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void Set(MethodContext context, bool root, string name, VariantValue value)
        {
            if (root)
            {
                // Fullscreen is the only writable property of the root interface and
                // CanSetFullscreen already says no.
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", name);
                return;
            }

            try
            {
                switch (name)
                {
                    case "Volume":
                        Raise(VolumeRequested, Math.Clamp(value.GetDouble(), 0, 1));
                        break;
                    case "Rate":
                        // Zero means Pause per the spec, and a rate outside the advertised
                        // Minimum/MaximumRate has to be clamped rather than refused.
                        var rate = value.GetDouble();
                        if (rate == 0)
                        {
                            Raise(PauseRequested);
                        }
                        else
                        {
                            Raise(RateRequested, Math.Clamp(rate, MinimumRate, MaximumRate));
                        }
                        break;
                    case "LoopStatus":
                        Raise(LoopStatusRequested, ParseLoopStatus(value.GetString()));
                        break;
                    case "Shuffle":
                        Raise(ShuffleRequested, value.GetBool());
                        break;
                    default:
                        context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", name);
                        return;
                }
            }
            catch (Exception ex)
            {
                // A host that sent the wrong variant type gets an error, not a dead connection.
                Logger.Warning($"MPRIS Set {name} refused: {ex.Message}");
                context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", name);
                return;
            }

            ReplyEmpty(context);
        }

        private const double MinimumRate = 0.5;
        private const double MaximumRate = 2.5;

        private static void WriteRootValue(ref MessageWriter writer, string name)
        {
            switch (name)
            {
                case "CanQuit":
                    writer.WriteVariantBool(true);
                    break;
                case "CanRaise":
                    writer.WriteVariantBool(true);
                    break;
                case "HasTrackList":
                    // The playlist exists, but org.mpris.MediaPlayer2.TrackList is a second object
                    // with its own contract; saying false is the honest answer, not a missing one.
                    writer.WriteVariantBool(false);
                    break;
                case "Identity":
                    writer.WriteVariantString("Unigram");
                    break;
                case "DesktopEntry":
                    // The basename of the .desktop file, without the extension: the panel uses it
                    // to pair the player with an icon and a name. DesktopEntry.Ensure() has
                    // already written that file by the time this object is exported.
                    writer.WriteVariantString(DesktopEntry.Id);
                    break;
                case "SupportedUriSchemes":
                case "SupportedMimeTypes":
                    // Empty because OpenUri is not a thing here: what plays is what the user opened
                    // inside a chat, never a file handed over by the desktop.
                    writer.WriteSignature("as");
                    writer.WriteArray(Array.Empty<string>());
                    break;
                case "Fullscreen":
                case "CanSetFullscreen":
                    writer.WriteVariantBool(false);
                    break;
            }
        }

        private void WritePlayerValue(ref MessageWriter writer, string name)
        {
            var state = _state;

            switch (name)
            {
                case "PlaybackStatus":
                    writer.WriteVariantString(state.Status switch
                    {
                        MprisPlaybackStatus.Playing => "Playing",
                        MprisPlaybackStatus.Paused => "Paused",
                        _ => "Stopped"
                    });
                    break;
                case "LoopStatus":
                    writer.WriteVariantString(state.Loop switch
                    {
                        MprisLoopStatus.Track => "Track",
                        MprisLoopStatus.Playlist => "Playlist",
                        _ => "None"
                    });
                    break;
                case "Rate":
                    writer.WriteVariantDouble(state.Rate);
                    break;
                case "MinimumRate":
                    writer.WriteVariantDouble(MinimumRate);
                    break;
                case "MaximumRate":
                    writer.WriteVariantDouble(MaximumRate);
                    break;
                case "Shuffle":
                    writer.WriteVariantBool(state.Shuffle);
                    break;
                case "Volume":
                    writer.WriteVariantDouble(state.Volume);
                    break;
                case "Position":
                    writer.WriteVariantInt64(ReadPosition());
                    break;
                case "Metadata":
                    WriteMetadata(ref writer, state);
                    break;
                case "CanGoNext":
                    writer.WriteVariantBool(state.CanGoNext);
                    break;
                case "CanGoPrevious":
                    writer.WriteVariantBool(state.CanGoPrevious);
                    break;
                case "CanPlay":
                    writer.WriteVariantBool(state.CanPlay);
                    break;
                case "CanPause":
                    writer.WriteVariantBool(state.CanPause);
                    break;
                case "CanSeek":
                    writer.WriteVariantBool(state.CanSeek);
                    break;
                case "CanControl":
                    // Constant true, and constant on purpose: the spec forbids emitting a change
                    // for it, and a player that answered false would have every button greyed out
                    // for as long as it lived.
                    writer.WriteVariantBool(true);
                    break;
            }
        }

        private void WriteMetadata(ref MessageWriter writer, MprisSnapshot state)
        {
            writer.WriteSignature("a{sv}");

            var dictionary = writer.WriteDictionaryStart();

            writer.WriteDictionaryEntryStart();
            writer.WriteString("mpris:trackid");
            writer.WriteVariantObjectPath(state.Path ?? NoTrack);

            if (state.Length > TimeSpan.Zero)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:length");
                writer.WriteVariantInt64(ToMicroseconds(state.Length));
            }

            if (!string.IsNullOrEmpty(state.Title))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:title");
                writer.WriteVariantString(state.Title);
            }

            if (!string.IsNullOrEmpty(state.Artist))
            {
                // xesam:artist is a LIST of strings, not a string. A panel reading it as one would
                // show nothing at all rather than the wrong thing, which is why this is spelled
                // out instead of going through WriteVariantString.
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:artist");
                writer.WriteSignature("as");
                writer.WriteArray(new[] { state.Artist });
            }

            if (!string.IsNullOrEmpty(state.Album))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:album");
                writer.WriteVariantString(state.Album);
            }

            var art = ToUri(state.ArtUrl);
            if (art != null)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:artUrl");
                writer.WriteVariantString(art);
            }

            writer.WriteDictionaryEnd(dictionary);
        }

        private long ReadPosition()
        {
            var provider = PositionProvider;
            if (provider == null)
            {
                return 0;
            }

            try
            {
                return ToMicroseconds(provider());
            }
            catch (Exception ex)
            {
                Logger.Error("MPRIS position provider failed", ex);
                return 0;
            }
        }

        #endregion

        #region Conversions

        private static long ToMicroseconds(TimeSpan value)
        {
            var microseconds = value.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
            return microseconds < 0 ? 0 : microseconds;
        }

        private static TimeSpan FromMicroseconds(long value)
        {
            return TimeSpan.FromTicks(value * (TimeSpan.TicksPerMillisecond / 1000));
        }

        private static MprisLoopStatus ParseLoopStatus(string value)
        {
            return value switch
            {
                "Track" => MprisLoopStatus.Track,
                "Playlist" => MprisLoopStatus.Playlist,
                _ => MprisLoopStatus.None
            };
        }

        /// <summary>
        /// A track id has to be a valid D-Bus object path, and the ids upstream has are message
        /// ids, file names and profile audio keys — anything but. Every byte outside
        /// <c>[A-Za-z0-9_]</c> is escaped to <c>_XX</c>, which is reversible enough to stay unique
        /// and never produces an empty element or a trailing slash.
        /// </summary>
        private static string TrackPath(string trackId)
        {
            if (string.IsNullOrEmpty(trackId))
            {
                return NoTrack;
            }

            var builder = new StringBuilder(TrackPrefix, TrackPrefix.Length + trackId.Length + 8);

            foreach (var b in Encoding.UTF8.GetBytes(trackId))
            {
                if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' || b == (byte)'_')
                {
                    builder.Append((char)b);
                }
                else
                {
                    builder.Append('_').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// <c>mpris:artUrl</c> is a URI, and what upstream has is the path of the file TDLib
        /// downloaded. Anything that already looks like a URI is passed through untouched.
        /// </summary>
        private static string ToUri(string pathOrUri)
        {
            if (string.IsNullOrEmpty(pathOrUri))
            {
                return null;
            }

            if (pathOrUri.StartsWith("file:", StringComparison.Ordinal)
                || pathOrUri.StartsWith("http:", StringComparison.Ordinal)
                || pathOrUri.StartsWith("https:", StringComparison.Ordinal))
            {
                return pathOrUri;
            }

            try
            {
                return new Uri(pathOrUri).AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        private static readonly ReadOnlyMemory<byte> RootIntrospectionXml = Encoding.UTF8.GetBytes(
            """
            <interface name="org.mpris.MediaPlayer2">
              <method name="Raise"/>
              <method name="Quit"/>
              <property name="CanQuit" type="b" access="read"/>
              <property name="Fullscreen" type="b" access="readwrite"/>
              <property name="CanSetFullscreen" type="b" access="read"/>
              <property name="CanRaise" type="b" access="read"/>
              <property name="HasTrackList" type="b" access="read"/>
              <property name="Identity" type="s" access="read"/>
              <property name="DesktopEntry" type="s" access="read"/>
              <property name="SupportedUriSchemes" type="as" access="read"/>
              <property name="SupportedMimeTypes" type="as" access="read"/>
            </interface>
            """);

        private static readonly ReadOnlyMemory<byte> PlayerIntrospectionXml = Encoding.UTF8.GetBytes(
            """
            <interface name="org.mpris.MediaPlayer2.Player">
              <method name="Next"/>
              <method name="Previous"/>
              <method name="Pause"/>
              <method name="PlayPause"/>
              <method name="Stop"/>
              <method name="Play"/>
              <method name="Seek">
                <arg name="Offset" type="x" direction="in"/>
              </method>
              <method name="SetPosition">
                <arg name="TrackId" type="o" direction="in"/>
                <arg name="Position" type="x" direction="in"/>
              </method>
              <method name="OpenUri">
                <arg name="Uri" type="s" direction="in"/>
              </method>
              <signal name="Seeked">
                <arg name="Position" type="x"/>
              </signal>
              <property name="PlaybackStatus" type="s" access="read"/>
              <property name="LoopStatus" type="s" access="readwrite"/>
              <property name="Rate" type="d" access="readwrite"/>
              <property name="Shuffle" type="b" access="readwrite"/>
              <property name="Metadata" type="a{sv}" access="read"/>
              <property name="Volume" type="d" access="readwrite"/>
              <property name="Position" type="x" access="read"/>
              <property name="MinimumRate" type="d" access="read"/>
              <property name="MaximumRate" type="d" access="read"/>
              <property name="CanGoNext" type="b" access="read"/>
              <property name="CanGoPrevious" type="b" access="read"/>
              <property name="CanPlay" type="b" access="read"/>
              <property name="CanPause" type="b" access="read"/>
              <property name="CanSeek" type="b" access="read"/>
              <property name="CanControl" type="b" access="read"/>
            </interface>
            """);
    }
}
