//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Telegram.Native.Media
{
    internal enum MpvFormat
    {
        None = 0,
        String = 1,
        OsdString = 2,
        Flag = 3,
        Int64 = 4,
        Double = 5,
        Node = 6
    }

    internal enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        LogMessage = 2,
        GetPropertyReply = 3,
        SetPropertyReply = 4,
        CommandReply = 5,
        StartFile = 6,
        EndFile = 7,
        FileLoaded = 8,
        Idle = 11,
        Tick = 14,
        ClientMessage = 16,
        VideoReconfig = 17,
        AudioReconfig = 18,
        Seek = 20,
        PlaybackRestart = 21,
        PropertyChange = 22,
        QueueOverflow = 24,
        Hook = 25
    }

    internal enum MpvEndFileReason
    {
        Eof = 0,
        Stop = 2,
        Quit = 3,
        Error = 4,
        Redirect = 5
    }

    internal enum MpvLogLevel
    {
        None = 0,
        Fatal = 10,
        Error = 20,
        Warn = 30,
        Info = 40,
        Verbose = 50,
        Debug = 60,
        Trace = 70
    }

    internal static class MpvError
    {
        public const int Success = 0;
        public const int LoadingFailed = -13;
        public const int AoInitFailed = -14;
        public const int NothingToPlay = -16;
        public const int UnknownFormat = -17;
        public const int Generic = -20;
    }

    /// <summary>
    /// The one piece of the C library this port needs: the process-wide locale, which libmpv
    /// insists on before it will build a core. <c>setlocale</c> answers the current value when
    /// <paramref name="locale"/> is null and sets it otherwise; the string it returns belongs to
    /// the C library and must not be freed, so it comes back as a pointer.
    /// </summary>
    internal static partial class Libc
    {
        // glibc's <locale.h>: LC_NUMERIC is 1 on Linux (it is 4 on macOS and 2 on the BSDs, which
        // is why this constant is worth naming rather than inlining).
        internal const int LC_NUMERIC = 1;

        [LibraryImport("libc", EntryPoint = "setlocale", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr SetLocale(int category, string locale);
    }

    /// <summary>
    /// The flat C client API of libmpv, one to one with <c>/usr/include/mpv/client.h</c> (API
    /// 2.5 / mpv 0.41 on this machine). Twenty-odd plain functions, which is the reason phase 5
    /// picked mpv over GStreamer: no GObject, no refcounting, no pods -- and no new native code,
    /// so the audio engine never needs a trip to the Mint box.
    ///
    /// <para>The library name is resolved by <see cref="NativeLibraryResolver"/>, which tries
    /// <c>libmpv.so.2</c> and degrades to "no audio" instead of taking the process down when the
    /// package is missing (soname 2 is mpv >= 0.35; Ubuntu 22.04 ships <c>libmpv.so.1</c> and is
    /// not usable, see plan-media.md 7.2).</para>
    /// </summary>
    internal static partial class MpvNative
    {
        internal const string Library = NativeLibraryResolver.Mpv;

        // unsigned long, which is EIGHT bytes in LP64 -- not uint. Declaring it 32 bits reads only
        // EAX out of RAX, and works today purely because the value is major << 16 | minor and mpv
        // has never used the top half; the day it does, the version check silently sees garbage.
        [LibraryImport(Library, EntryPoint = "mpv_client_api_version")]
        internal static partial nuint ClientApiVersion();

        [LibraryImport(Library, EntryPoint = "mpv_create")]
        internal static partial IntPtr Create();

        [LibraryImport(Library, EntryPoint = "mpv_initialize")]
        internal static partial int Initialize(IntPtr ctx);

        [LibraryImport(Library, EntryPoint = "mpv_terminate_destroy")]
        internal static partial void TerminateDestroy(IntPtr ctx);

        [LibraryImport(Library, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SetOptionString(IntPtr ctx, string name, string data);

        [LibraryImport(Library, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SetPropertyString(IntPtr ctx, string name, string data);

        [LibraryImport(Library, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SetProperty(IntPtr ctx, string name, MpvFormat format, ref double data);

        [LibraryImport(Library, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SetProperty(IntPtr ctx, string name, MpvFormat format, ref int data);

        [LibraryImport(Library, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int GetProperty(IntPtr ctx, string name, MpvFormat format, out double data);

        [LibraryImport(Library, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int GetProperty(IntPtr ctx, string name, MpvFormat format, out int data);

        // The string flavour hands back a pointer libmpv allocated: it has to come back through
        // mpv_free, never through the UTF-8 marshaller's own free.
        [LibraryImport(Library, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int GetPropertyString(IntPtr ctx, string name, MpvFormat format, out IntPtr data);

        [LibraryImport(Library, EntryPoint = "mpv_free")]
        internal static partial void Free(IntPtr data);

        [LibraryImport(Library, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int ObserveProperty(IntPtr ctx, ulong replyUserdata, string name, MpvFormat format);

        [LibraryImport(Library, EntryPoint = "mpv_command")]
        internal static unsafe partial int Command(IntPtr ctx, IntPtr* args);

        [LibraryImport(Library, EntryPoint = "mpv_wait_event")]
        internal static partial IntPtr WaitEvent(IntPtr ctx, double timeout);

        [LibraryImport(Library, EntryPoint = "mpv_wakeup")]
        internal static partial void Wakeup(IntPtr ctx);

        [LibraryImport(Library, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int RequestLogMessages(IntPtr ctx, string minLevel);

        [LibraryImport(Library, EntryPoint = "mpv_error_string")]
        internal static partial IntPtr ErrorString(int error);

        [LibraryImport(Library, EntryPoint = "mpv_stream_cb_add_ro", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial int StreamCbAddRo(IntPtr ctx, string protocol, IntPtr userData, IntPtr openFn);

        /// <summary><c>mpv_event</c>: 24 bytes on LP64.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Event
        {
            public MpvEventId EventId;
            public int Error;
            public ulong ReplyUserdata;
            public IntPtr Data;
        }

        /// <summary><c>mpv_event_property</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EventProperty
        {
            public IntPtr Name;
            public MpvFormat Format;
            public IntPtr Data;
        }

        /// <summary>
        /// <c>mpv_event_end_file</c>. Both playlist ids are <c>int64_t</c> in client.h 2.5 -- an
        /// older release had the second one as <c>int</c>, which would shift the last field.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EventEndFile
        {
            public MpvEndFileReason Reason;
            public int Error;
            public long PlaylistEntryId;
            public long PlaylistInsertId;
            public int PlaylistInsertNumEntries;
        }

        /// <summary><c>mpv_event_log_message</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct EventLogMessage
        {
            public IntPtr Prefix;
            public IntPtr Level;
            public IntPtr Text;
            public MpvLogLevel LogLevel;
        }
    }

    /// <summary>
    /// One property observation.
    ///
    /// <para>The instance is REUSED: exactly one of these exists per client, it is written by the
    /// pump thread and it is only valid for the duration of the handler call. mpv observes
    /// <c>time-pos</c> at playloop rate (measured at ~200 events a second for an audio-only file),
    /// and allocating an object plus a marshalled name string for each of them was 16 % of a core
    /// -- the same trick <c>PlaybackService</c> already uses with its single
    /// <c>PlaybackPositionChangedEventArgs</c>.</para>
    /// </summary>
    internal sealed class MpvPropertyChangedEventArgs : EventArgs
    {
        /// <summary>
        /// The <c>reply_userdata</c> the property was observed with. Switching on a number is what
        /// lets the whole hot path avoid <c>PtrToStringUTF8</c> on the property name.
        /// </summary>
        public ulong Id { get; internal set; }

        public string Name { get; internal set; }

        public MpvFormat Format { get; internal set; }

        /// <summary>
        /// The numeric payload of a DOUBLE, INT64 or FLAG observation. NaN when the property is
        /// currently unavailable, which mpv reports as a NONE-formatted change -- that is the
        /// answer for <c>time-pos</c> and <c>duration</c> while nothing is loaded, and it must not
        /// be mistaken for zero.
        /// </summary>
        public double Number { get; internal set; }

        public string Text { get; internal set; }

        public bool HasValue => Format != MpvFormat.None && !double.IsNaN(Number);
    }

    internal sealed class MpvEndFileEventArgs : EventArgs
    {
        public MpvEndFileEventArgs(MpvEndFileReason reason, int error)
        {
            Reason = reason;
            Error = error;
        }

        public MpvEndFileReason Reason { get; }

        public int Error { get; }
    }

    internal sealed class MpvLogEventArgs : EventArgs
    {
        public MpvLogEventArgs(MpvLogLevel level, string prefix, string text)
        {
            Level = level;
            Prefix = prefix;
            Text = text;
        }

        public MpvLogLevel Level { get; }

        public string Prefix { get; }

        public string Text { get; }
    }

    /// <summary>
    /// One libmpv core, with its event pump.
    ///
    /// <para>Threading, which is the whole difficulty: libmpv delivers everything through
    /// <c>mpv_wait_event</c> on a thread the caller provides, and the returned pointer is only
    /// valid until the next call on the same handle. So there is exactly one pump thread per
    /// client, it owns the handle, and it is also the thread that destroys it -- which is what
    /// makes <see cref="Dispose"/> free of use-after-free. Everything raised from here therefore
    /// arrives on that thread and NOT on the dispatcher; marshalling is
    /// <see cref="AsyncMediaPlayer"/>'s job, the same rule the port already applies to TDLib
    /// updates.</para>
    ///
    /// <para>The other calls (<c>set_property</c>, <c>command</c>, ...) are thread safe per the
    /// client API and are made from whatever thread the player is driven from.</para>
    /// </summary>
    internal sealed class MpvClient : IDisposable
    {
        /// <summary>
        /// Whether libmpv could be loaded at all. Probed once and never throws: a machine without
        /// the package gets an inert player (no sound), not a crash.
        /// </summary>
        internal static bool IsAvailable { get; } = Probe();

        private static ulong _apiVersion;

        /// <summary>Client API version, as <c>major &lt;&lt; 16 | minor</c>; 0 when unavailable.</summary>
        internal static ulong ApiVersion => _apiVersion;

        /// <summary>
        /// libmpv REFUSES TO START unless the C runtime's LC_NUMERIC is "C". Its very first check
        /// in <c>mpv_create</c> is <c>strcmp(setlocale(LC_NUMERIC, NULL), "C")</c>, and when it does
        /// not match it prints "Non-C locale detected. This is not supported." on stderr and returns
        /// NULL — no error code, no handle, and the player is silently mute.
        ///
        /// <para>It bit here and not in the spike because a .NET process starts in the "C" locale
        /// and something inside the app moves it: by the time a voice note is pressed the process
        /// is in the user's own locale (measured here: es_ES.UTF-8), set by one of the natives that
        /// initialize before the first sound — Uno's X11 host, fontconfig or GTK all call
        /// <c>setlocale(LC_ALL, "")</c> on the way up. Any Linux desktop in a locale that writes
        /// decimals with a comma would have had audio and not had it, with the reason only on
        /// stderr.</para>
        ///
        /// <para>Only LC_NUMERIC is moved, and only to "C", which is what every other native in
        /// this process already expects: FFmpeg, TDLib and Skia all parse numbers with '.'.
        /// .NET's own formatting does not go through the C locale at all (it is ICU/managed), so
        /// nothing the app prints or parses changes.</para>
        /// </summary>
        private static void ForceNumericLocale()
        {
            try
            {
                var current = Marshal.PtrToStringUTF8(Libc.SetLocale(Libc.LC_NUMERIC, null));

                if (current is "C" or "POSIX")
                {
                    return;
                }

                var applied = Marshal.PtrToStringUTF8(Libc.SetLocale(Libc.LC_NUMERIC, "C"));

                UnigramNative.Report(Logger.LogLevel.Info,
                    $"mpv needs a C numeric locale: LC_NUMERIC was \"{current}\", now \"{applied ?? "?"}\"");
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Warning, $"could not read or set LC_NUMERIC: {ex.Message}");
            }
        }

        private static bool Probe()
        {
            try
            {
                // Explicit widening, so the intent survives a language-version change: the
                // symbol is 64-bit and this is the one place where that has to be admitted.
                _apiVersion = (ulong)MpvNative.ClientApiVersion();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"libmpv is not usable ({ex.GetType().Name}: {ex.Message}): audio playback disabled");
                return false;
            }

            // Everything this binding uses (stream_cb included) is API 1.x/2.x; the soname is what
            // actually gates it, so a successful call is the whole test.
            UnigramNative.Report(Logger.LogLevel.Info, $"libmpv client API {_apiVersion >> 16}.{_apiVersion & 0xFFFFUL}");
            return true;
        }

        /// <summary>
        /// What keeps every call into libmpv from overlapping <c>mpv_terminate_destroy</c>.
        ///
        /// <para>client.h is explicit about it: because <c>mpv_terminate_destroy</c> deallocates
        /// the context on its way through, no other function may be called concurrently on that
        /// same context. Reading <c>_handle</c> into a local and testing it for zero does NOT
        /// establish that: the pump
        /// can publish the zero and start destroying between the test and the call, and the caller
        /// then drives a context that talloc has already handed back -- <c>mpv_wakeup</c> takes a
        /// pthread mutex out of it, which on reused memory corrupts in silence instead of
        /// crashing.</para>
        ///
        /// <para>So the handle is only ever touched while the shared side is held, and the pump
        /// zeroes it while holding the exclusive side. Once the exclusive side has been released
        /// the handle is unreachable -- every later caller reads the zero -- and every earlier
        /// caller has finished its native call, which is exactly the precondition destroying it
        /// needs; the destroy itself is then done OUTSIDE the lock, so a stuck stream callback
        /// blocks nobody but the pump.</para>
        ///
        /// <para>The shared side is never taken recursively: nothing on this boundary calls back
        /// into the client (libmpv's own stream callbacks are forbidden to, by contract), and
        /// event handlers run on the pump, which holds nothing while it dispatches.</para>
        ///
        /// <para>Deliberately never disposed: it outlives the client by design (a late caller must
        /// still find a lock to take and a zero to read), and it owns nothing the process needs
        /// back.</para>
        /// </summary>
        private readonly ReaderWriterLockSlim _gate = new();

        private readonly ManualResetEventSlim _terminated = new(false);

        // Written and read only by the pump thread; see MpvPropertyChangedEventArgs for why it is
        // one instance and not one per event.
        private readonly MpvPropertyChangedEventArgs _property = new();

        private long _events;
        private long _propertyEvents;

        private IntPtr _handle;
        private Thread _pump;
        private int _disposed;

        public event EventHandler<MpvPropertyChangedEventArgs> PropertyChanged;
        public event EventHandler<MpvEndFileEventArgs> EndFile;
        public event EventHandler StartFile;
        public event EventHandler FileLoaded;
        public event EventHandler PlaybackRestart;
        public event EventHandler Seeked;
        public event EventHandler<MpvLogEventArgs> LogMessage;

        private MpvClient(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>
        /// Creates and initializes a core with <paramref name="options"/> applied BEFORE
        /// <c>mpv_initialize</c> -- which is mandatory for the ones that pick a subsystem
        /// (<c>ao</c>, <c>config</c>, <c>terminal</c>). Returns null when libmpv is missing or the
        /// core refuses to start; it never throws.
        /// </summary>
        public static MpvClient Create(IReadOnlyList<KeyValuePair<string, string>> options, out string error)
        {
            error = null;

            if (!IsAvailable)
            {
                error = "libmpv is not available";
                return null;
            }

            // Before every core, not once: whoever moves the locale does it while the app starts,
            // and this can be the first core or the hundredth.
            ForceNumericLocale();

            var handle = IntPtr.Zero;

            try
            {
                handle = MpvNative.Create();
                if (handle == IntPtr.Zero)
                {
                    error = "mpv_create returned NULL";
                    return null;
                }

                if (options != null)
                {
                    for (int i = 0; i < options.Count; i++)
                    {
                        var result = MpvNative.SetOptionString(handle, options[i].Key, options[i].Value);
                        if (result < 0)
                        {
                            // Not fatal on its own: an option this build does not know about is
                            // better skipped than allowed to abort the whole player.
                            UnigramNative.Report(Logger.LogLevel.Warning, $"mpv option {options[i].Key}={options[i].Value} refused: {ErrorString(result)}");
                        }
                    }
                }

                var initialized = MpvNative.Initialize(handle);
                if (initialized < 0)
                {
                    error = $"mpv_initialize: {ErrorString(initialized)}";
                    MpvNative.TerminateDestroy(handle);
                    return null;
                }

                var client = new MpvClient(handle);
                client.StartPump();
                return client;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        MpvNative.TerminateDestroy(handle);
                    }
                    catch (Exception destroyException)
                    {
                        Logger.Error("mpv_terminate_destroy after initialization failure", destroyException);
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Takes the shared side of <see cref="_gate"/> and hands out the handle; false, with
        /// nothing held, once the core is gone. EVERY use of <c>_handle</c> goes through here, and
        /// the caller must release it in a <c>finally</c>.
        /// </summary>
        private bool TryAcquire(out IntPtr handle)
        {
            _gate.EnterReadLock();
            handle = _handle;

            if (handle == IntPtr.Zero)
            {
                _gate.ExitReadLock();
                return false;
            }

            return true;
        }

        private void Release()
        {
            _gate.ExitReadLock();
        }

        // There is deliberately no public Handle: handing the raw pointer out is what let a caller
        // hold it across the destroy. Anything that needs one takes it through a method here.

        public bool IsDisposed => _disposed != 0;

        /// <summary>Diagnostics: how many events the pump has seen, and how many were property
        /// observations. Read by the spike to explain the cost of a playing track.</summary>
        public long Events => Interlocked.Read(ref _events);

        /// <inheritdoc cref="Events"/>
        public long PropertyEvents => Interlocked.Read(ref _propertyEvents);

        public static string ErrorString(int error)
        {
            try
            {
                return Marshal.PtrToStringUTF8(MpvNative.ErrorString(error)) ?? error.ToString();
            }
            catch
            {
                return error.ToString();
            }
        }

        private void StartPump()
        {
            _pump = new Thread(Pump)
            {
                Name = "mpv events",
                IsBackground = true
            };

            _pump.Start();
        }

        private void Pump()
        {
            // Read without the gate, and that is sound here and only here: this thread is the one
            // that will zero the field, so nobody else can take the handle away from under it.
            var handle = _handle;

            while (true)
            {
                IntPtr pointer;

                try
                {
                    pointer = MpvNative.WaitEvent(handle, -1);
                }
                catch (Exception)
                {
                    break;
                }

                if (pointer == IntPtr.Zero)
                {
                    continue;
                }

                var e = Marshal.PtrToStructure<MpvNative.Event>(pointer);

                if (e.EventId == MpvEventId.None)
                {
                    continue;
                }

                _events++;

                if (e.EventId == MpvEventId.Shutdown)
                {
                    break;
                }

                try
                {
                    Dispatch(e);
                }
                catch (Exception ex)
                {
                    // A handler that throws must not kill the pump: the core would then never see
                    // its quit and Dispose would block for its whole timeout.
                    UnigramNative.Report(Logger.LogLevel.Error, "mpv event handler threw", ex);
                }
            }

            // Under the exclusive side, which is what makes "published as gone" mean something:
            // waiting for it drains the callers that already read a non-zero handle, and every
            // caller after it reads the zero. Publishing it with a plain write, as this did, only
            // narrowed the window -- it never closed it.
            _gate.EnterWriteLock();

            try
            {
                _handle = IntPtr.Zero;
            }
            finally
            {
                _gate.ExitWriteLock();
            }

            // Outside the lock on purpose: the handle is already unreachable, so nothing can race
            // this, and mpv_terminate_destroy blocks until every open stream_cb stream has been
            // closed -- holding the gate across it would park callers behind a stuck download.
            try
            {
                MpvNative.TerminateDestroy(handle);
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "mpv_terminate_destroy threw", ex);
            }
            finally
            {
                _terminated.Set();
            }
        }

        private void Dispatch(MpvNative.Event e)
        {
            switch (e.EventId)
            {
                case MpvEventId.PropertyChange:
                    _propertyEvents++;

                    if (e.Data != IntPtr.Zero)
                    {
                        var handler = PropertyChanged;
                        if (handler != null)
                        {
                            ReadProperty(e.ReplyUserdata, e.Data, _property);
                            handler.Invoke(this, _property);
                        }
                    }
                    break;
                case MpvEventId.EndFile:
                    if (e.Data != IntPtr.Zero)
                    {
                        var end = Marshal.PtrToStructure<MpvNative.EventEndFile>(e.Data);
                        EndFile?.Invoke(this, new MpvEndFileEventArgs(end.Reason, end.Error));
                    }
                    break;
                case MpvEventId.StartFile:
                    StartFile?.Invoke(this, EventArgs.Empty);
                    break;
                case MpvEventId.FileLoaded:
                    FileLoaded?.Invoke(this, EventArgs.Empty);
                    break;
                case MpvEventId.PlaybackRestart:
                    PlaybackRestart?.Invoke(this, EventArgs.Empty);
                    break;
                case MpvEventId.Seek:
                    Seeked?.Invoke(this, EventArgs.Empty);
                    break;
                case MpvEventId.LogMessage:
                    if (e.Data != IntPtr.Zero && LogMessage != null)
                    {
                        var log = Marshal.PtrToStructure<MpvNative.EventLogMessage>(e.Data);
                        LogMessage.Invoke(this, new MpvLogEventArgs(log.LogLevel,
                            Marshal.PtrToStringUTF8(log.Prefix),
                            Marshal.PtrToStringUTF8(log.Text)?.TrimEnd('\n')));
                    }
                    break;
            }
        }

        private static void ReadProperty(ulong id, IntPtr data, MpvPropertyChangedEventArgs args)
        {
            var property = Marshal.PtrToStructure<MpvNative.EventProperty>(data);

            args.Id = id;
            args.Format = property.Format;
            args.Number = double.NaN;
            args.Text = null;

            // The name is only materialized when nobody registered an id, which is the debugging
            // case: on the hot path (time-pos, ~200 events a second) it stays a pointer.
            args.Name = id == 0 ? Marshal.PtrToStringUTF8(property.Name) : null;

            // format NONE (or a null payload) is "currently unavailable", not zero: reporting it
            // as 0 would snap the scrubber to the start every time a track is swapped.
            if (property.Format == MpvFormat.None || property.Data == IntPtr.Zero)
            {
                args.Format = MpvFormat.None;
                return;
            }

            switch (property.Format)
            {
                case MpvFormat.Double:
                    args.Number = Marshal.PtrToStructure<double>(property.Data);
                    break;
                case MpvFormat.Int64:
                    args.Number = Marshal.PtrToStructure<long>(property.Data);
                    break;
                case MpvFormat.Flag:
                    args.Number = Marshal.ReadInt32(property.Data);
                    break;
                case MpvFormat.String:
                case MpvFormat.OsdString:
                    args.Text = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data));
                    break;
                default:
                    args.Format = MpvFormat.None;
                    break;
            }
        }

        /// <param name="id">
        /// <c>reply_userdata</c>, echoed back on every change. Non-zero ids are what the player
        /// switches on; 0 means "I will read the name instead", which costs a string per event.
        /// </param>
        public int ObserveProperty(string name, MpvFormat format, ulong id = 0)
        {
            if (!TryAcquire(out var handle))
            {
                return MpvError.Generic;
            }

            try
            {
                return MpvNative.ObserveProperty(handle, id, name, format);
            }
            finally
            {
                Release();
            }
        }

        public int RequestLogMessages(string minLevel)
        {
            if (!TryAcquire(out var handle))
            {
                return MpvError.Generic;
            }

            try
            {
                return MpvNative.RequestLogMessages(handle, minLevel);
            }
            finally
            {
                Release();
            }
        }

        public int SetOption(string name, string value)
        {
            if (!TryAcquire(out var handle))
            {
                return MpvError.Generic;
            }

            try
            {
                return MpvNative.SetOptionString(handle, name, value);
            }
            finally
            {
                Release();
            }
        }

        /// <summary>
        /// <c>mpv_stream_cb_add_ro</c>. Here rather than on the caller's side because the raw
        /// handle must not leave this class: a protocol registered against a pointer copied out of
        /// it would be registering against something the pump may be destroying.
        /// </summary>
        public int AddStreamProtocol(string scheme, IntPtr userData, IntPtr openFn)
        {
            if (!TryAcquire(out var handle))
            {
                return MpvError.Generic;
            }

            try
            {
                return MpvNative.StreamCbAddRo(handle, scheme, userData, openFn);
            }
            finally
            {
                Release();
            }
        }

        public int SetProperty(string name, string value)
        {
            if (!TryAcquire(out var handle))
            {
                return MpvError.Generic;
            }

            try
            {
                return MpvNative.SetPropertyString(handle, name, value);
            }
            finally
            {
                Release();
            }
        }

        public int SetProperty(string name, double value)
        {
            if (!TryAcquire(out var handle))
            {
                return MpvError.Generic;
            }

            try
            {
                return MpvNative.SetProperty(handle, name, MpvFormat.Double, ref value);
            }
            finally
            {
                Release();
            }
        }

        public int SetProperty(string name, bool value)
        {
            var flag = value ? 1 : 0;

            if (!TryAcquire(out var handle))
            {
                return MpvError.Generic;
            }

            try
            {
                return MpvNative.SetProperty(handle, name, MpvFormat.Flag, ref flag);
            }
            finally
            {
                Release();
            }
        }

        public bool TryGetProperty(string name, out double value)
        {
            if (!TryAcquire(out var handle))
            {
                value = 0;
                return false;
            }

            try
            {
                return MpvNative.GetProperty(handle, name, MpvFormat.Double, out value) >= 0;
            }
            finally
            {
                Release();
            }
        }

        public bool TryGetProperty(string name, out bool value)
        {
            if (!TryAcquire(out var handle))
            {
                value = false;
                return false;
            }

            try
            {
                var result = MpvNative.GetProperty(handle, name, MpvFormat.Flag, out int flag);
                value = flag != 0;
                return result >= 0;
            }
            finally
            {
                Release();
            }
        }

        public string GetPropertyString(string name)
        {
            if (!TryAcquire(out var handle))
            {
                return null;
            }

            try
            {
                if (MpvNative.GetPropertyString(handle, name, MpvFormat.String, out var pointer) < 0 || pointer == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUTF8(pointer);
                }
                finally
                {
                    // mpv_free, still inside the gate: the allocation belongs to the core and
                    // giving it back after the core is gone is a free against a dead allocator.
                    MpvNative.Free(pointer);
                }
            }
            finally
            {
                Release();
            }
        }

        /// <summary>
        /// <c>mpv_command</c>, which wants a NULL-terminated array of UTF-8 pointers. Every string
        /// is marshalled and freed here rather than handed to the source generator, because the
        /// generator has no shape for "array of strings, NULL terminated".
        /// </summary>
        public unsafe int Command(params string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return MpvError.Generic;
            }

            var pointers = stackalloc IntPtr[args.Length + 1];
            var allocated = new IntPtr[args.Length];

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    allocated[i] = Marshal.StringToCoTaskMemUTF8(args[i] ?? string.Empty);
                    pointers[i] = allocated[i];
                }

                pointers[args.Length] = IntPtr.Zero;

                if (!TryAcquire(out var handle))
                {
                    return MpvError.Generic;
                }

                try
                {
                    return MpvNative.Command(handle, pointers);
                }
                finally
                {
                    Release();
                }
            }
            finally
            {
                for (int i = 0; i < allocated.Length; i++)
                {
                    if (allocated[i] != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(allocated[i]);
                    }
                }
            }
        }

        /// <summary>
        /// <c>mpv_wakeup</c>: makes the pump's <c>mpv_wait_event</c> return now. Its own method
        /// because it must take the gate itself -- the bug this replaces was calling it with a
        /// handle read before the zero test, so the test protected the command and not this.
        /// </summary>
        public void Wakeup()
        {
            if (!TryAcquire(out var handle))
            {
                return;
            }

            try
            {
                MpvNative.Wakeup(handle);
            }
            finally
            {
                Release();
            }
        }

        /// <summary>
        /// Asks the core to quit and waits (bounded) for the pump thread to destroy the handle.
        /// Idempotent. Everything the core still owns -- open stream_cb streams above all -- is
        /// released inside <c>mpv_terminate_destroy</c>, which is why this waits at all instead of
        /// firing and forgetting.
        /// </summary>
        /// <returns>
        /// Whether <c>mpv_terminate_destroy</c> actually RETURNED. False means the core is still
        /// up and still owns whatever it was given: it is not a diagnostic, it is the answer to
        /// "may I now free what I lent it?", and the answer is no. Callers that lent the core
        /// something -- the tg:// protocol above all -- must hold on to it and hand the release
        /// to <see cref="WhenTerminated"/> instead.
        /// </returns>
        public bool Terminate()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    // "quit" makes the core emit MPV_EVENT_SHUTDOWN, which is what ends the pump
                    // loop; the wakeup covers the case where the core is already gone.
                    Command("quit");
                    Wakeup();
                }
                catch (Exception ex)
                {
                    Logger.Error(nameof(Terminate), ex);
                }
            }

            // Disposing from inside a handler is only possible when there is no dispatcher to
            // marshal to (a console); waiting there would be the pump thread waiting for itself,
            // so it reports "not terminated" and the caller defers instead of blocking.
            if (Thread.CurrentThread == _pump)
            {
                return false;
            }

            // Five seconds is far more than a teardown takes (measured in single-digit ms) and
            // still an escape hatch: a stuck stream callback must not hang the app on exit. Note
            // that a SECOND caller waits too -- it needs the same answer as the first one, and
            // returning early would have told it the core was gone when it is not.
            return _terminated.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Terminate();
        }

        /// <summary>
        /// Runs <paramref name="continuation"/> once <c>mpv_terminate_destroy</c> has returned,
        /// immediately if it already has.
        ///
        /// <para>This is what makes the bounded wait in <see cref="Terminate"/> safe rather than
        /// merely survivable. Giving up on the wait used to be followed by freeing the cookies the
        /// core still holds, which is a use-after-free the moment it emits one more callback;
        /// leaking them instead would be correct but permanent. Deferring the release to the
        /// moment the core is actually gone is neither.</para>
        /// </summary>
        public void WhenTerminated(Action continuation)
        {
            if (continuation == null)
            {
                return;
            }

            if (_terminated.IsSet)
            {
                Run(continuation);
                return;
            }

            // executeOnlyOnce, so the registration retires itself the first time it fires. If the
            // core never finishes dying, it never fires and the continuation stays pending -- the
            // bounded leak, which is the outcome that was acceptable all along.
            ThreadPool.RegisterWaitForSingleObject(_terminated.WaitHandle,
                static (state, timedOut) => Run((Action)state), continuation, Timeout.Infinite, true);
        }

        private static void Run(Action continuation)
        {
            try
            {
                continuation();
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "mpv termination continuation threw", ex);
            }
        }
    }
}
