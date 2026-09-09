//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Telegram.Native
{
    public interface IVideoAnimationSource
    {
        void SeekCallback(long offset);
        void ReadCallback(long count, long buffer, out long bytesRead);

        string FilePath { get; }
        long FileSize { get; }

        long Offset { get; }

        long Id { get; }
    }

    /// <summary>
    /// Linux replacement for Telegram.Native.VideoAnimation (C++/WinRT over FFmpeg), binding the
    /// video module of <c>libunigram-native.so</c>. Same surface the shared code expects:
    /// ImageHelper's preview and crop paths, StorageVideo/StorageAudio probing, GenerationService
    /// and EditMediaPopup.
    ///
    /// <see cref="LoadFromFile"/> returns null on any failure -- including "the native library is
    /// not there yet" -- which is what the WinRT one did too; the call sites dereference the result
    /// straight away and their own try/catch turns that into the fallback they already have.
    /// </summary>
    public sealed partial class VideoAnimation : IDisposable
    {
        private readonly VideoAnimationHandle _handle;

        // The bridge is kept alive by its own GCHandle while the library holds the source; this
        // field only documents the pairing and keeps it reachable for diagnostics. It may already
        // have been released: a cached animation served from disk drops the source at open time.
        private readonly VideoAnimationSourceBridge _bridge;

        // Read once at load: the geometry never changes, and Title/Artist are borrowed pointers
        // that stop being valid at close, so they are copied here and not on demand.
        private readonly int _pixelWidth;
        private readonly int _pixelHeight;
        private readonly double _frameRate;
        private readonly int _duration;
        private readonly int _rotation;
        private readonly string _title;
        private readonly string _artist;
        private readonly bool _hasVideo;
        private readonly bool _hasAudio;
        private readonly bool _hasAlbumCover;

        // A decode failure repeats on every tick; it is only worth one line in the log.
        private bool _reported;

        private VideoAnimation(VideoAnimationHandle handle, VideoAnimationSourceBridge bridge)
        {
            _handle = handle;
            _bridge = bridge;

            _pixelWidth = VideoNative.PixelWidth(handle);
            _pixelHeight = VideoNative.PixelHeight(handle);
            _frameRate = VideoNative.FrameRate(handle);
            _duration = VideoNative.Duration(handle);
            _rotation = VideoNative.Rotation(handle);
            _title = Marshal.PtrToStringUTF8(VideoNative.Title(handle)) ?? string.Empty;
            _artist = Marshal.PtrToStringUTF8(VideoNative.Artist(handle)) ?? string.Empty;
            _hasVideo = VideoNative.HasVideo(handle) != 0;
            _hasAudio = VideoNative.HasAudio(handle) != 0;
            _hasAlbumCover = VideoNative.HasAlbumCover(handle) != 0;
        }

        /// <param name="preview">
        /// Sets AVFMT_FLAG_NOBUFFER: what the header-only probes want, and what the frame grabbers
        /// deliberately do not (unbuffered often fails to produce the first frame).
        /// </param>
        /// <param name="probe">
        /// Accept a file with no video stream. That is how StorageAudio reads the tags and the
        /// album cover out of an audio file.
        /// </param>
        public static VideoAnimation LoadFromFile(IVideoAnimationSource file, bool preview, bool limitFps, bool probe)
        {
            if (!UnigramNative.IsAvailable || file == null)
            {
                return null;
            }

            VideoAnimationSourceBridge bridge = null;

            // Outside the try, so the catch can tell the two cases apart. Once open() has returned
            // a live handle the source belongs to the library -- it copied the struct by value and
            // its destructor calls destroy -- so releasing the bridge from here would free the
            // cookie the library is still holding, and dropping the handle on the floor would
            // leave the animation to the finalizer, which then destroys the same source a second
            // time. Closing the handle does both jobs, in the library's order.
            VideoAnimationHandle handle = null;

            try
            {
                bridge = new VideoAnimationSourceBridge(file);

                handle = VideoNative.Open(ref bridge.Descriptor, preview ? 1 : 0, limitFps ? 1 : 0, probe ? 1 : 0);
                if (handle == null || handle.IsInvalid)
                {
                    // The library destroyed the source on its way out, so Release() is a no-op
                    // here; it is called anyway for the case where the P/Invoke never ran.
                    handle?.Dispose();
                    handle = null;
                    bridge.Release();

                    // Not exceptional: a partially downloaded file, a container FFmpeg does not
                    // recognise, an audio file opened without probe.
                    UnigramNative.Report(Logger.LogLevel.Debug, $"video: cannot open {Describe(file)}: {UnigramNative.LastError()}");
                    return null;
                }

                return new VideoAnimation(handle, bridge);
            }
            catch (Exception ex)
            {
                if (handle != null)
                {
                    handle.Dispose();
                }
                else
                {
                    bridge?.Release();
                }

                UnigramNative.Report(Logger.LogLevel.Error, $"video: LoadFromFile({Describe(file)})", ex);
                return null;
            }
        }

        /// <summary>
        /// Aborts a decode in flight. Callable from another thread: it flips an atomic flag the
        /// read and seek callbacks check, so the demuxer unwinds instead of blocking on a download
        /// that is never going to finish.
        /// </summary>
        public void Stop()
        {
            try
            {
                VideoNative.Stop(_handle);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "video: stop", ex);
            }
        }

        public void PrepareToSeek()
        {
            try
            {
                VideoNative.PrepareToSeek(_handle);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "video: prepareToSeek", ex);
            }
        }

        public void SeekToMilliseconds(long ms, bool precise)
        {
            try
            {
                VideoNative.SeekToMilliseconds(_handle, ms, precise ? 1 : 0);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"video: seek({ms})", ex);
            }
        }

        /// <summary>
        /// The attached picture of an audio file, as the bytes stored in the container (JPEG or
        /// PNG). WinRT returned an IRandomAccessStream over the same bytes; the ABI hands out a
        /// borrowed pointer that dies with the handle, so it is copied here first.
        /// </summary>
        public IRandomAccessStream GetAlbumCover()
        {
            if (!_hasAlbumCover)
            {
                return null;
            }

            try
            {
                if (VideoNative.AlbumCover(_handle, out var data, out var size) != 1 || data == IntPtr.Zero || size <= 0)
                {
                    return null;
                }

                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, size);

                return System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(new MemoryStream(bytes));
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "video: albumCover", ex);
                return null;
            }
        }

        /// <summary>
        /// Decodes the next frame, scaled to <paramref name="width"/> x <paramref name="height"/>,
        /// as premultiplied BGRA. False leaves <paramref name="bitmap"/> untouched.
        ///
        /// <para>Two ways in. When <see cref="BufferAccess"/> can reach the array behind the
        /// destination -- which it can for every Uno buffer, including
        /// <c>WriteableBitmap.PixelBuffer</c> -- the ABI decodes <b>straight into it</b> and the
        /// frame is walked once. Otherwise it decodes into an ArrayPool array and copies, which is
        /// what this always used to do. At 1539x2736 that copy alone measured 3.9 ms
        /// (unigram-linux/spikes/VideoPresentSpike), so the difference is a third of a frame at
        /// 60 Hz.</para>
        /// </summary>
        public unsafe bool RenderSync(IBuffer bitmap, int width, int height, bool preview, out double seconds)
        {
            seconds = 0;

            if (bitmap == null || width <= 0 || height <= 0)
            {
                return false;
            }

            var frameSize = (long)width * height * 4;

            // PixelBuffer wraps a WriteableBitmap; the extension methods below only accept Uno's
            // own Buffer, so unwrap first (same as BufferSurface).
            var target = PixelBuffer.Unwrap(bitmap);
            if (target.Capacity < frameSize)
            {
                return false;
            }

            if (BufferAccess.TryGetArray(target, out var array, out var offset, out var count) && count >= frameSize)
            {
                fixed (byte* buffer = &array[offset])
                {
                    if (!RenderSync((IntPtr)buffer, width, height, preview, out seconds))
                    {
                        return false;
                    }
                }

                if (target.Length < frameSize)
                {
                    target.Length = (uint)frameSize;
                }

                return true;
            }

            var pixels = ArrayPool<byte>.Shared.Rent((int)frameSize);

            try
            {
                bool rendered;

                fixed (byte* buffer = pixels)
                {
                    rendered = RenderSync((IntPtr)buffer, width, height, preview, out seconds);
                }

                if (!rendered)
                {
                    return false;
                }

                pixels.CopyTo(0, target, 0, (int)frameSize);

                if (target.Length < frameSize)
                {
                    target.Length = (uint)frameSize;
                }

                return true;
            }
            catch (ObjectDisposedException)
            {
                // Disposed from another thread while this frame was in flight.
                return false;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"video: render({width}x{height})", ex);
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }

        /// <summary>
        /// The same decode, into memory the caller owns and has already pinned: the shape the ABI
        /// wants and the one <c>LinuxVideoPlayer</c> uses, where the destination is an SKImage's
        /// plane and there is no managed array anywhere in the path.
        ///
        /// <para>The buffer must hold <paramref name="width"/> * <paramref name="height"/> * 4
        /// bytes; the caller owns that check, because a pointer cannot be asked its size.</para>
        /// </summary>
        public unsafe bool RenderSync(IntPtr buffer, int width, int height, bool preview, out double seconds)
        {
            seconds = 0;

            if (buffer == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return false;
            }

            try
            {
                double position = 0;

                // stride 0 = packed (width * 4), which is the WriteableBitmap and SKImage layout.
                var rendered = VideoNative.Render(_handle, (byte*)buffer, width, height, 0, preview ? 1 : 0, &position, null);

                if (rendered != 1)
                {
                    if (!_reported)
                    {
                        _reported = true;
                        UnigramNative.Report(Logger.LogLevel.Warning, $"video: render({width}x{height}) produced no frame: {UnigramNative.LastError()}");
                    }

                    return false;
                }

                seconds = position;
                return true;
            }
            catch (ObjectDisposedException)
            {
                // Disposed from another thread while this frame was in flight.
                return false;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"video: render({width}x{height})", ex);
                return false;
            }
        }

        public int PixelWidth => _pixelWidth;

        public int PixelHeight => _pixelHeight;

        public double FrameRate => _frameRate;

        public int Duration => _duration;

        public int Rotation => _rotation;

        public string Title => _title;

        public string Artist => _artist;

        public bool HasVideo => _hasVideo;

        public bool HasAudio => _hasAudio;

        public bool HasAlbumCover => _hasAlbumCover;

        public void Dispose()
        {
            // SafeHandle: idempotent, and it will not release the animation while a decode started
            // on another thread is still inside the library. Closing is also what runs the source's
            // destroy callback and frees the bridge.
            _handle.Dispose();
        }

        private static string Describe(IVideoAnimationSource file)
        {
            try
            {
                var path = file.FilePath;
                return string.IsNullOrEmpty(path) ? $"#{file.Id}" : path;
            }
            catch (Exception)
            {
                return "<source>";
            }
        }
    }

    /// <summary>
    /// Linux replacement for Telegram.Native.VideoAnimationStreamSource: an
    /// <see cref="IVideoAnimationSource"/> over an in-memory or file stream that has no path of its
    /// own (ImageHelper and the storage entities open the picked file and hand the stream over).
    ///
    /// It is the one source that copies bytes itself -- see
    /// <see cref="IVideoAnimationPullSource"/> -- which is exactly what the Windows implementation
    /// did when VideoAnimation's read callback recognised this type and read from its IStream
    /// instead of opening a file.
    /// </summary>
    public sealed partial class VideoAnimationStreamSource : IVideoAnimationSource, IVideoAnimationPullSource
    {
        private readonly IRandomAccessStream _stream;

        // The same stream as a System.IO.Stream: the ABI reads synchronously from FFmpeg's demuxer
        // thread, and IRandomAccessStream only offers ReadAsync, which would mean blocking on a
        // task from a native callback.
        private readonly Stream _reader;

        private readonly object _lock = new();

        private long _offset;

        public VideoAnimationStreamSource(IRandomAccessStream stream)
        {
            _stream = stream;

            try
            {
                _reader = stream != null
                    ? System.IO.WindowsRuntimeStreamExtensions.AsStream(stream)
                    : null;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "video: cannot adapt IRandomAccessStream", ex);
            }
        }

        public void SeekCallback(long offset)
        {
            lock (_lock)
            {
                _offset = offset;

                if (_reader != null && _reader.CanSeek)
                {
                    _reader.Seek(offset, SeekOrigin.Begin);
                }
            }
        }

        /// <summary>
        /// Never called for this source: providing <see cref="Read"/> puts the library in pull mode
        /// and it stops asking how much is available. Reports everything as available so that a
        /// caller that goes through the interface anyway is not made to wait.
        /// </summary>
        public void ReadCallback(long count, long buffer, out long bytesRead)
        {
            bytesRead = count;
        }

        long IVideoAnimationPullSource.Read(IntPtr buffer, long count)
        {
            if (_reader == null || buffer == IntPtr.Zero || count <= 0)
            {
                return 0;
            }

            lock (_lock)
            {
                var total = 0L;

                unsafe
                {
                    // Read() is allowed to return short (it is a stream over an IInputStream);
                    // loop until the request is filled or the stream ends, which is what the
                    // demuxer expects from a blocking source.
                    while (total < count)
                    {
                        var chunk = new Span<byte>((byte*)buffer + total, (int)Math.Min(count - total, int.MaxValue));

                        var read = _reader.Read(chunk);
                        if (read <= 0)
                        {
                            break;
                        }

                        total += read;
                    }
                }

                _offset += total;
                return total;
            }
        }

        public string FilePath => string.Empty;

        public long FileSize => (long)(_stream?.Size ?? 0);

        public long Offset => _offset;

        public long Id => 0;
    }
}
