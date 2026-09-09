//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Telegram.Native.Media
{
    /// <summary>
    /// <c>mpv_stream_cb_info</c>: a cookie plus five function pointers, filled in by the open
    /// callback. IntPtr rather than <c>delegate*</c> for the same reason as
    /// <c>VideoNative.VideoSource</c>: the struct has to stay plainly blittable, and the pointers
    /// only ever come from [UnmanagedCallersOnly] statics.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvStreamCbInfo
    {
        public IntPtr Cookie;
        public IntPtr Read;
        public IntPtr Seek;
        public IntPtr Size;
        public IntPtr Close;
        public IntPtr Cancel;
    }

    /// <summary>
    /// The <c>tg://</c> protocol handed to libmpv, so a track plays straight out of TDLib's
    /// partial download instead of waiting for a finished file.
    ///
    /// <para>This is the same shape the port already uses for the video engine
    /// (<c>Native/Video/VideoNative.cs</c>): the callbacks are
    /// <c>[UnmanagedCallersOnly]</c> STATICS -- so there is no delegate for the GC to collect --
    /// and identity travels as a <see cref="NativeCookieTable{T}"/> cookie, which unlike a
    /// <see cref="GCHandle"/> can tell a retired identity from a live one. Measured against libmpv before a
    /// line of this was written (plan-media.md 2.1, <c>media-bench/streamcb.c</c>): an ogg/opus
    /// took 5 reads and 1 seek, an mp4 4 reads and 4 seeks (mpv going to the end for the
    /// <c>moov</c> atom, exactly what <see cref="Telegram.Streams.RemoteFileSource"/>'s ranged
    /// requests exist for).</para>
    ///
    /// <para>A protocol cannot be unregistered from a live core, so one of these is created per
    /// mpv handle and lives as long as it does; individual tracks come and go in
    /// <see cref="_sources"/>.</para>
    /// </summary>
    internal sealed unsafe class MpvStreamProtocol : IDisposable
    {
        public const string Scheme = "tg";

        private static long _nextKey;

        private readonly ConcurrentDictionary<string, IAsyncMediaPlayerSource> _sources = new();
        private IntPtr _cookie;
        private int _disposed;
        private int _opened;

        /// <summary>
        /// How many streams libmpv has opened through this protocol. Diagnostics, and a real
        /// question: two open streams over one source would fight over its single offset, so the
        /// spike asserts that a track is opened exactly once.
        /// </summary>
        public int Opened => _opened;

        private MpvStreamProtocol()
        {
        }

        /// <summary>
        /// Registers <c>tg://</c> on <paramref name="client"/>. Null when libmpv refuses it, which
        /// leaves the player with no way to stream and is reported by the caller.
        /// </summary>
        public static MpvStreamProtocol Register(MpvClient client, out string error)
        {
            error = null;

            if (client == null)
            {
                error = "no mpv core";
                return null;
            }

            var protocol = new MpvStreamProtocol();
            protocol._cookie = NativeCookieTable<MpvStreamProtocol>.Add(protocol);

            // Registered through the client and not against a raw handle: the handle is only
            // valid while the client holds it against its own teardown, and a pointer copied out
            // of the client is a pointer nobody is keeping alive (see MpvClient.AddStreamProtocol).
            var result = client.AddStreamProtocol(Scheme, protocol._cookie,
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, MpvStreamCbInfo*, int>)&OnOpen);

            if (result < 0)
            {
                error = MpvClient.ErrorString(result);
                NativeCookieTable<MpvStreamProtocol>.Remove(protocol._cookie);
                return null;
            }

            return protocol;
        }

        /// <summary>
        /// Publishes <paramref name="source"/> under a fresh URI and returns it. The source is
        /// held until <see cref="Release"/>, so a track that mpv never gets round to opening (a
        /// user skipping fast through a playlist) is still let go of.
        /// </summary>
        public string Add(IAsyncMediaPlayerSource source)
        {
            var key = Interlocked.Increment(ref _nextKey).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _sources[key] = source;

            return Scheme + "://" + key;
        }

        public void Release(string uri)
        {
            var key = KeyOf(uri);
            if (key != null)
            {
                _sources.TryRemove(key, out _);
            }
        }

        private static string KeyOf(string uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return null;
            }

            var index = uri.IndexOf("://", StringComparison.Ordinal);
            return index < 0 ? uri : uri.Substring(index + 3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static MpvStreamProtocol Resolve(IntPtr userData)
        {
            return NativeCookieTable<MpvStreamProtocol>.Resolve(userData);
        }

        /// <summary>
        /// <c>mpv_stream_cb_open_ro_fn</c>. Runs on libmpv's demuxer thread and must not call back
        /// into libmpv (documented deadlock), so everything it touches is ours.
        /// </summary>
        [UnmanagedCallersOnly]
        private static int OnOpen(IntPtr userData, IntPtr uri, MpvStreamCbInfo* info)
        {
            try
            {
                var protocol = Resolve(userData);
                if (protocol == null || info == null)
                {
                    return MpvError.LoadingFailed;
                }

                var key = KeyOf(Marshal.PtrToStringUTF8(uri));
                if (key == null || !protocol._sources.TryGetValue(key, out var source))
                {
                    return MpvError.LoadingFailed;
                }

                Interlocked.Increment(ref protocol._opened);

                var stream = new MpvStreamSource(source);

                info->Cookie = stream.Cookie;
                info->Read = (IntPtr)(delegate* unmanaged<IntPtr, byte*, ulong, long>)&MpvStreamSource.OnRead;
                info->Seek = (IntPtr)(delegate* unmanaged<IntPtr, long, long>)&MpvStreamSource.OnSeek;
                info->Size = (IntPtr)(delegate* unmanaged<IntPtr, long>)&MpvStreamSource.OnSize;
                info->Close = (IntPtr)(delegate* unmanaged<IntPtr, void>)&MpvStreamSource.OnClose;
                info->Cancel = (IntPtr)(delegate* unmanaged<IntPtr, void>)&MpvStreamSource.OnCancel;

                return 0;
            }
            catch (Exception)
            {
                // Nothing may escape into libmpv's demuxer; the API has an answer for "cannot
                // open this", and that is what a failure here means.
                return MpvError.LoadingFailed;
            }
        }

        /// <summary>
        /// Drops every published source but KEEPS the cookie registered, for the case the core
        /// outlived the wait in <see cref="MpvClient.Terminate"/>.
        ///
        /// <para><c>stream_cb.h</c> is explicit that a protocol stays registered until the core is
        /// destroyed and that unregistration is only finished once <c>mpv_terminate_destroy</c> has
        /// RETURNED. A core that is still up can therefore still call the open callback with this
        /// cookie, so retiring it here would be the mistake; the player asks
        /// <see cref="MpvClient.WhenTerminated"/> to do it when the core really goes. Until then
        /// a late open resolves to a protocol with no sources and gets a clean "cannot open".</para>
        /// </summary>
        public void Abandon()
        {
            _sources.Clear();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _sources.Clear();

            // Only safe once the core is gone -- see Abandon for what "gone" means and for the
            // path taken when the player could not wait for it.
            NativeCookieTable<MpvStreamProtocol>.Remove(_cookie);
            _cookie = IntPtr.Zero;
        }
    }

    /// <summary>
    /// One open <c>tg://</c> stream: the bridge between libmpv's read/seek/size/close/cancel and
    /// an <see cref="IAsyncMediaPlayerSource"/> (in the app, always
    /// <see cref="Telegram.Streams.RemoteFileSource"/>).
    ///
    /// <para>The division of labour is the one the source already implements for the video engine
    /// and is easy to get backwards: <c>ReadCallback</c> does NOT copy bytes -- it blocks until
    /// that many bytes are downloadable at the current offset and reports how many are, which is
    /// how the download is flow-controlled against TDLib. Copying is this class's job, out of the
    /// partial file TDLib is still writing, which is why the handle is opened with
    /// <see cref="FileShare.ReadWrite"/>.</para>
    /// </summary>
    internal sealed unsafe class MpvStreamSource
    {
        private readonly IAsyncMediaPlayerSource _source;
        private readonly object _lock = new();

        private IntPtr _cookie;
        private FileStream _stream;
        private string _path;

        private volatile bool _canceled;
        private int _closed;

        public MpvStreamSource(IAsyncMediaPlayerSource source)
        {
            _source = source;
            _cookie = NativeCookieTable<MpvStreamSource>.Add(this);
        }

        public IntPtr Cookie => _cookie;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static MpvStreamSource Resolve(IntPtr cookie)
        {
            return NativeCookieTable<MpvStreamSource>.Resolve(cookie);
        }

        [UnmanagedCallersOnly]
        internal static long OnRead(IntPtr cookie, byte* buffer, ulong nbytes)
        {
            try
            {
                var stream = Resolve(cookie);
                if (stream == null || buffer == null)
                {
                    return -1;
                }

                return stream.Read(buffer, nbytes);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        internal static long OnSeek(IntPtr cookie, long offset)
        {
            try
            {
                var stream = Resolve(cookie);
                if (stream == null || stream._canceled)
                {
                    return -1;
                }

                stream._source.SeekCallback(offset);
                return offset;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        internal static long OnSize(IntPtr cookie)
        {
            try
            {
                var size = Resolve(cookie)?._source.FileSize ?? 0;

                // Negative is libmpv's "size unknown", which keeps it from seeking blindly to the
                // end of a file whose length TDLib has not told us yet.
                return size > 0 ? size : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        internal static void OnClose(IntPtr cookie)
        {
            try
            {
                Resolve(cookie)?.Close();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// <c>cancel_fn</c>: called from another thread to make a blocking read return NOW. The
        /// only lever the source offers for that is Close, which sets its closed flag and signals
        /// the event <c>ReadCallback</c> is parked on; it is idempotent, so the close callback
        /// that always follows is still correct.
        /// </summary>
        [UnmanagedCallersOnly]
        internal static void OnCancel(IntPtr cookie)
        {
            try
            {
                var stream = Resolve(cookie);
                if (stream != null)
                {
                    stream._canceled = true;
                    stream._source.Close();
                }
            }
            catch (Exception)
            {
            }
        }

        private long Read(byte* buffer, ulong nbytes)
        {
            if (_canceled || _closed != 0)
            {
                return -1;
            }

            var count = (long)Math.Min(nbytes, int.MaxValue);
            if (count <= 0)
            {
                return 0;
            }

            // Blocks until this many bytes are readable at the current offset, or until the source
            // is closed. window 0: the prefetch of RemoteFileSource sizes its own window when it
            // owns the download, and there is nothing better to say when it does not.
            _source.ReadCallback(count, 0, out var available);

            if (available < 0)
            {
                return -1;
            }

            if (available == 0)
            {
                // A genuine end of stream; RemoteFileSource only reports zero at the end of the
                // file, never for "not downloaded yet".
                return 0;
            }

            var offset = _source.Offset;
            var stream = Resolve();

            if (stream == null)
            {
                return -1;
            }

            var wanted = (int)Math.Min(count, available);
            var total = 0;

            lock (_lock)
            {
                if (stream.Position != offset)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                }

                while (total < wanted)
                {
                    var read = stream.Read(new Span<byte>(buffer + total, wanted - total));
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }
            }

            if (total <= 0)
            {
                // The bytes were promised but are not on disk: a short read is legal for libmpv
                // (it retries), and reporting 0 here would be a false end of file.
                return -1;
            }

            _source.SeekCallback(offset + total);
            return total;
        }

        /// <summary>
        /// The handle on the file TDLib is writing. Reopened when the path changes, which it does
        /// exactly once per file: TDLib names a partial download and then renames it on
        /// completion.
        /// </summary>
        private FileStream Resolve()
        {
            var path = _source.FilePath;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (_lock)
            {
                if (_stream != null && string.Equals(_path, path, StringComparison.Ordinal))
                {
                    return _stream;
                }

                _stream?.Dispose();
                _stream = null;
                _path = null;

                try
                {
                    _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    _path = path;
                }
                catch (Exception)
                {
                    return null;
                }

                return _stream;
            }
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                _source.Close();
            }
            catch (Exception)
            {
            }

            lock (_lock)
            {
                _stream?.Dispose();
                _stream = null;
                _path = null;
            }

            // libmpv promises never to use this cookie again once close returns, so this is the
            // one place it can be retired. Unlike the protocol's, this promise is per stream and
            // holds even when the core outlives the player (H6): close_fn always precedes the
            // core's own teardown.
            NativeCookieTable<MpvStreamSource>.Remove(_cookie);
            _cookie = IntPtr.Zero;
        }
    }
}
