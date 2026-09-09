//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Telegram.Native
{
    /// <summary>
    /// Video module of <c>libunigram-native.so</c>, one to one with the <c>unigram_video_*</c> /
    /// <c>unigram_cached_video_*</c> section of the ABI (the header is
    /// <c>unigram-linux/native/engines/video/include/unigram_video.h</c>, pulled into
    /// unigram_native.h). It replaces Telegram.Native's VideoAnimation, CachedVideoAnimation and
    /// VideoAnimationStreamSource, which were C++/WinRT over FFmpeg.
    ///
    /// Nothing here is called unless <see cref="UnigramNative.IsAvailable"/>.
    ///
    /// Two things differ from the WinRT surface and shape everything below:
    ///  - the output is a caller-owned pointer plus a stride, not an IBuffer, so the bindings
    ///    render into a pooled array and copy into the IBuffer (same as the lottie one);
    ///  - the data source is a struct of function pointers instead of a projected interface, which
    ///    is what <see cref="VideoAnimationSourceBridge"/> exists to fill in.
    /// </summary>
    internal static partial class VideoNative
    {
        /// <summary>
        /// <c>unigram_video_source</c>: nine pointers, 72 bytes on LP64, in header order. Passed by
        /// value to the <c>_open</c> functions, which copy it and call <c>destroy</c> exactly once
        /// when they are done with it -- including on every failure path, so a source handed over
        /// is never leaked.
        ///
        /// The fields are IntPtr rather than <c>delegate*</c> on purpose: the LibraryImport source
        /// generator has to see a plainly blittable struct, and the function pointers are only ever
        /// produced here, from [UnmanagedCallersOnly] statics.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct VideoSource
        {
            public IntPtr UserData;
            public IntPtr FileSize;
            public IntPtr Offset;
            public IntPtr Seek;
            public IntPtr Available;

            /// <summary>
            /// Null for a file-backed source (the library opens <see cref="FilePath"/> itself and
            /// uses <see cref="Available"/> as flow control) and non-null for a pull source, which
            /// then does the copying and is never asked for a path. This one field is the whole
            /// difference between the two modes, and mirrors the <c>try_as&lt;VideoAnimation­Stream­Source&gt;</c>
            /// the Windows implementation did.
            /// </summary>
            public IntPtr Read;

            public IntPtr FilePath;
            public IntPtr Id;
            public IntPtr Destroy;
        }

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_open")]
        internal static partial VideoAnimationHandle Open(ref VideoSource source, int preview, int limitFps, int probe);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_close")]
        internal static partial void Close(IntPtr animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_render")]
        internal static unsafe partial int Render(VideoAnimationHandle animation, byte* pixels, int width, int height, int stride, int preview, double* seconds, int* completed);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_stop")]
        internal static partial void Stop(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_prepare_to_seek")]
        internal static partial void PrepareToSeek(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_seek_to_milliseconds")]
        internal static partial void SeekToMilliseconds(VideoAnimationHandle animation, long milliseconds, int precise);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_pixel_width")]
        internal static partial int PixelWidth(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_pixel_height")]
        internal static partial int PixelHeight(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_frame_rate")]
        internal static partial double FrameRate(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_duration")]
        internal static partial int Duration(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_rotation")]
        internal static partial int Rotation(VideoAnimationHandle animation);

        // Borrowed pointers, owned by the library and valid until close: marshalled by hand so the
        // UTF-8 string marshaller does not try to free them.
        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_title")]
        internal static partial IntPtr Title(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_artist")]
        internal static partial IntPtr Artist(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_has_video")]
        internal static partial int HasVideo(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_has_audio")]
        internal static partial int HasAudio(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_has_album_cover")]
        internal static partial int HasAlbumCover(VideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_animation_album_cover")]
        internal static partial int AlbumCover(VideoAnimationHandle animation, out IntPtr data, out int size);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_open")]
        internal static partial CachedVideoAnimationHandle CachedOpen(ref VideoSource source, int width, int height, int fit, int precache, int limitFps);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_close")]
        internal static partial void CachedClose(IntPtr animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_render")]
        internal static unsafe partial int CachedRender(CachedVideoAnimationHandle animation, byte* pixels, int stride, double* seconds, int* completed);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_stop")]
        internal static partial void CachedStop(CachedVideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_cache")]
        internal static partial void CachedCache(CachedVideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_seek")]
        internal static partial void CachedSeek(CachedVideoAnimationHandle animation, double seconds);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_frame_rate")]
        internal static partial double CachedFrameRate(CachedVideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_total_frame")]
        internal static partial int CachedTotalFrame(CachedVideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_is_caching")]
        internal static partial int CachedIsCaching(CachedVideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_is_ready_to_cache")]
        internal static partial int CachedIsReadyToCache(CachedVideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_pixel_width")]
        internal static partial int CachedPixelWidth(CachedVideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_pixel_height")]
        internal static partial int CachedPixelHeight(CachedVideoAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_cached_video_animation_rotation")]
        internal static partial int CachedRotation(CachedVideoAnimationHandle animation);
    }

    /// <summary>
    /// Owns an <c>unigram_video_animation*</c>. SafeHandle rather than a raw pointer for the same
    /// reason as the lottie one: the animation is disposed from the UI thread while a decode may
    /// still be running on a worker, and the handle count keeps the pointer alive until that call
    /// returns.
    /// </summary>
    internal sealed class VideoAnimationHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public VideoAnimationHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            // Also runs the source's destroy callback, which is what retires the bridge's cookie.
            VideoNative.Close(handle);
            return true;
        }
    }

    /// <inheritdoc cref="VideoAnimationHandle"/>
    internal sealed class CachedVideoAnimationHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public CachedVideoAnimationHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            VideoNative.CachedClose(handle);
            return true;
        }
    }

    /// <summary>
    /// A source that hands out the bytes itself instead of naming a file, which is the pull mode of
    /// the ABI (<c>unigram_video_source.read</c>). Implemented by
    /// <see cref="VideoAnimationStreamSource"/>, the replacement for the WinRT class of the same
    /// name; every other <see cref="IVideoAnimationSource"/> is file-backed and does not implement
    /// this.
    /// </summary>
    internal interface IVideoAnimationPullSource
    {
        /// <summary>
        /// Copies at most <paramref name="count"/> bytes into <paramref name="buffer"/> and returns
        /// how many were copied; 0 for end of stream, negative on error.
        /// </summary>
        long Read(IntPtr buffer, long count);
    }

    /// <summary>
    /// Bridges an <see cref="IVideoAnimationSource"/> to the <c>unigram_video_source</c> struct of
    /// function pointers the ABI wants.
    ///
    /// Lifetime, which is the whole difficulty here:
    ///
    ///  - the callbacks are <c>[UnmanagedCallersOnly]</c> STATICS, so there is no delegate for the
    ///    GC to collect and no <see cref="GC.KeepAlive"/> dance to get wrong. The identity of the
    ///    source travels as the <c>user_data</c> cookie instead: a
    ///    <see cref="NativeCookieTable{T}"/> entry, which is also what keeps the bridge (and
    ///    through it the C# source) alive while the library holds the struct;
    ///  - the library promises to call <c>destroy</c> exactly once, when the animation that took
    ///    the source is closed -- and also on every failure path of the <c>_open</c> functions,
    ///    which is why <see cref="Release"/> is idempotent and the open sites can call it blindly
    ///    when they get NULL back;
    ///  - that promise is NOT relied on for safety. The compression worker of the video engine has
    ///    been seen to run past the close of the animation it belongs to, so a callback can arrive
    ///    with a cookie that was already retired; the table answers null for it instead of
    ///    resolving a recycled slot to somebody else's bridge, which is what a
    ///    <see cref="GCHandle"/> cookie would have done (see NativeCookieTable for why
    ///    <c>IsAllocated</c> never filtered anything);
    ///  - <c>destroy</c> may arrive on the library's private compression thread, not only on the
    ///    thread that closed the animation. Retiring the cookie from any thread is fine; nothing
    ///    else happens in there.
    ///
    /// The callbacks themselves reach managed code from a native thread, so every one of them
    /// swallows its exceptions: an exception escaping into FFmpeg's demuxer would take the process
    /// down, and the ABI has a documented "this source failed" answer for each of them instead.
    /// </summary>
    internal sealed unsafe class VideoAnimationSourceBridge
    {
        private readonly IVideoAnimationSource _source;
        private readonly IVideoAnimationPullSource _pull;

        private readonly object _lock = new();

        // Every UTF-8 path ever handed out, freed together in Release(). FilePath is a property on
        // the C# side and RemoteFileSource's changes as TDLib finishes the download, so the pointer
        // is re-marshalled when the string changes; the old one is kept until the end rather than
        // freed under a native caller that may still be reading it.
        private readonly List<IntPtr> _paths = new();
        private string _path;
        private IntPtr _pathPtr;

        private IntPtr _cookie;
        private int _released;

        private VideoNative.VideoSource _descriptor;

        public VideoAnimationSourceBridge(IVideoAnimationSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _pull = source as IVideoAnimationPullSource;

            _cookie = NativeCookieTable<VideoAnimationSourceBridge>.Add(this);

            _descriptor = new VideoNative.VideoSource
            {
                UserData = _cookie,
                FileSize = (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnFileSize,
                Offset = (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnOffset,
                Seek = (IntPtr)(delegate* unmanaged<IntPtr, long, void>)&OnSeek,
                Available = (IntPtr)(delegate* unmanaged<IntPtr, long, long, long>)&OnAvailable,
                // Null unless the source can copy bytes itself: that is what selects the
                // file-backed path in the library (open()/read()/lseek() over FilePath).
                Read = _pull != null ? (IntPtr)(delegate* unmanaged<IntPtr, byte*, long, long>)&OnRead : IntPtr.Zero,
                FilePath = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&OnFilePath,
                Id = (IntPtr)(delegate* unmanaged<IntPtr, long>)&OnId,
                Destroy = (IntPtr)(delegate* unmanaged<IntPtr, void>)&OnDestroy
            };
        }

        /// <summary>
        /// The struct to hand to an <c>_open</c> function. It is copied by value there, so this
        /// stays a field and never a temporary.
        /// </summary>
        internal ref VideoNative.VideoSource Descriptor => ref _descriptor;

        internal IVideoAnimationSource Source => _source;

        /// <summary>
        /// Retires the cookie and frees the marshalled paths. Idempotent, and normally called by
        /// the library through <c>destroy</c>; the open sites call it only when they never managed
        /// to hand the source over (an exception before or during the P/Invoke), where a second
        /// call from the library can no longer happen.
        /// </summary>
        internal void Release()
        {
            if (System.Threading.Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            // Retired BEFORE the paths are freed, and not after: from here on a late callback
            // resolves to null instead of reaching a bridge whose UTF-8 paths are being handed
            // back to the allocator underneath it.
            NativeCookieTable<VideoAnimationSourceBridge>.Remove(_cookie);
            _cookie = IntPtr.Zero;

            lock (_lock)
            {
                foreach (var path in _paths)
                {
                    Marshal.FreeCoTaskMem(path);
                }

                _paths.Clear();
                _pathPtr = IntPtr.Zero;
                _path = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static VideoAnimationSourceBridge Resolve(IntPtr userData)
        {
            return NativeCookieTable<VideoAnimationSourceBridge>.Resolve(userData);
        }

        [UnmanagedCallersOnly]
        private static long OnFileSize(IntPtr userData)
        {
            try
            {
                return Resolve(userData)?.Source.FileSize ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        [UnmanagedCallersOnly]
        private static long OnOffset(IntPtr userData)
        {
            try
            {
                return Resolve(userData)?.Source.Offset ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        [UnmanagedCallersOnly]
        private static void OnSeek(IntPtr userData, long offset)
        {
            try
            {
                Resolve(userData)?.Source.SeekCallback(offset);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// <c>available</c>. Deliberately NOT a read: the WinRT contract this keeps is that
        /// ReadCallback reports how many of <paramref name="count"/> bytes may be read at the
        /// current offset, blocking as long as it likes -- which is how RemoteFileSource waits for
        /// TDLib to download the next chunk -- and the library then does the reading itself.
        /// <paramref name="window"/> is the prefetch hint the source is free to size its download
        /// from; upstream always passes 0 and so does the port.
        /// </summary>
        [UnmanagedCallersOnly]
        private static long OnAvailable(IntPtr userData, long count, long window)
        {
            try
            {
                var bridge = Resolve(userData);
                if (bridge == null)
                {
                    return -1;
                }

                bridge.Source.ReadCallback(count, window, out var bytesRead);
                return bytesRead;
            }
            catch (Exception)
            {
                // Negative is "closed or failed source", which unwinds the demuxer; 0 would mean a
                // clean end of stream and make a torn-down download look like a complete file.
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        private static long OnRead(IntPtr userData, byte* buffer, long count)
        {
            try
            {
                var bridge = Resolve(userData);
                if (bridge?._pull == null || buffer == null)
                {
                    return -1;
                }

                return bridge._pull.Read((IntPtr)buffer, count);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        [UnmanagedCallersOnly]
        private static IntPtr OnFilePath(IntPtr userData)
        {
            try
            {
                var bridge = Resolve(userData);
                if (bridge == null)
                {
                    return IntPtr.Zero;
                }

                var path = bridge.Source.FilePath;
                if (string.IsNullOrEmpty(path))
                {
                    // NULL is legal and means "no file": for a pull source it is expected, and for
                    // a file-backed one the library gives up, which is the right answer for a
                    // download that has not produced a path yet.
                    return IntPtr.Zero;
                }

                lock (bridge._lock)
                {
                    if (bridge._released != 0)
                    {
                        return IntPtr.Zero;
                    }

                    if (bridge._pathPtr != IntPtr.Zero && string.Equals(bridge._path, path, StringComparison.Ordinal))
                    {
                        return bridge._pathPtr;
                    }

                    var allocated = Marshal.StringToCoTaskMemUTF8(path);
                    bridge._paths.Add(allocated);
                    bridge._path = path;
                    bridge._pathPtr = allocated;
                    return allocated;
                }
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        [UnmanagedCallersOnly]
        private static long OnId(IntPtr userData)
        {
            try
            {
                return Resolve(userData)?.Source.Id ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        [UnmanagedCallersOnly]
        private static void OnDestroy(IntPtr userData)
        {
            try
            {
                Resolve(userData)?.Release();
            }
            catch (Exception)
            {
            }
        }
    }
}
