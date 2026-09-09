//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Telegram.Native
{
    /// <summary>
    /// Linux replacement for Telegram.Native.CachedVideoAnimation, binding the video module of
    /// <c>libunigram-native.so</c>. This is the one the chat list actually uses: AnimatedImage's
    /// VideoAnimatedImageTask drives it for every .webm sticker and GIF on screen.
    ///
    /// It is FFmpeg plus the LZ4 frame cache, in the same on-disk format as Windows
    /// (<c>&lt;path&gt;.&lt;w&gt;x&lt;h&gt;[.fit].cache</c>, version 7): the first pass decodes and
    /// compresses every frame on a background thread, and from then on the animation replays from
    /// the file instead of running a decoder per sticker. The three-state protocol the caller polls
    /// -- <see cref="IsReadyToCache"/>, <see cref="Cache"/>, <see cref="IsCaching"/> -- is
    /// unchanged.
    /// </summary>
    public sealed partial class CachedVideoAnimation : IDisposable
    {
        private readonly CachedVideoAnimationHandle _handle;

        // Kept for the pairing only; the bridge owns its own lifetime through the cookie the
        // library holds, and an animation served from an existing cache file has already had its
        // source destroyed by the time this object exists.
        private readonly VideoAnimationSourceBridge _bridge;

        private readonly int _pixelWidth;
        private readonly int _pixelHeight;
        private readonly int _rotation;
        private readonly double _frameRate;

        // Packed BGRA, which is what the ABI writes and what WriteableBitmap.PixelBuffer holds.
        private readonly int _frameSize;

        private bool _reported;

        private CachedVideoAnimation(CachedVideoAnimationHandle handle, VideoAnimationSourceBridge bridge, int pixelWidth, int pixelHeight)
        {
            _handle = handle;
            _bridge = bridge;

            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
            _rotation = VideoNative.CachedRotation(handle);
            _frameRate = VideoNative.CachedFrameRate(handle);
            _frameSize = pixelWidth * pixelHeight * 4;
        }

        /// <param name="fit">Scale to fit the box instead of filling it.</param>
        /// <param name="precache">
        /// Write (or reuse) the frame cache next to the source file. Ignored for a source with no
        /// path, which then decodes directly.
        /// </param>
        public static CachedVideoAnimation LoadFromFile(IVideoAnimationSource file, int width, int height, bool fit, bool precache, bool limitFps)
        {
            if (!UnigramNative.IsAvailable || file == null)
            {
                return null;
            }

            VideoAnimationSourceBridge bridge = null;

            // Outside the try for the same reason as VideoAnimation.LoadFromFile: after a
            // successful open the source is the library's, and the geometry calls below can still
            // throw. Closing the handle is what runs destroy; releasing the bridge behind the
            // library's back is a cookie freed under a source it is still holding.
            CachedVideoAnimationHandle handle = null;

            try
            {
                bridge = new VideoAnimationSourceBridge(file);

                handle = VideoNative.CachedOpen(ref bridge.Descriptor, width, height, fit ? 1 : 0, precache ? 1 : 0, limitFps ? 1 : 0);
                if (handle == null || handle.IsInvalid)
                {
                    handle?.Dispose();
                    handle = null;
                    bridge.Release();

                    UnigramNative.Report(Logger.LogLevel.Debug, $"video: cannot open cached {Describe(file)}: {UnigramNative.LastError()}");
                    return null;
                }

                // The engine rounds the requested size up to FFmpeg's 16-pixel alignment, so the
                // frame is not necessarily width x height: the caller sizes its bitmap from these
                // properties (AnimatedImage does), and the render below trusts them, not the
                // request.
                var pixelWidth = VideoNative.CachedPixelWidth(handle);
                var pixelHeight = VideoNative.CachedPixelHeight(handle);

                if (pixelWidth <= 0 || pixelHeight <= 0)
                {
                    UnigramNative.Report(Logger.LogLevel.Debug, $"video: no usable geometry for {Describe(file)}: {UnigramNative.LastError()}");
                    handle.Dispose();
                    return null;
                }

                return new CachedVideoAnimation(handle, bridge, pixelWidth, pixelHeight);
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

                UnigramNative.Report(Logger.LogLevel.Error, $"video: CachedVideoAnimation.LoadFromFile({Describe(file)})", ex);
                return null;
            }
        }

        /// <summary>
        /// Renders the next frame into <paramref name="bitmap"/>, at
        /// <see cref="PixelWidth"/> x <see cref="PixelHeight"/>. <paramref name="completed"/> is
        /// set when the animation wrapped around or gave up, which is how the caller knows a
        /// non-looping sticker is done.
        /// </summary>
        public unsafe void RenderSync(IBuffer bitmap, out double seconds, out bool completed)
        {
            seconds = 0;
            completed = false;

            if (bitmap == null || _frameSize <= 0)
            {
                return;
            }

            var target = PixelBuffer.Unwrap(bitmap);
            if (target.Capacity < _frameSize)
            {
                if (!_reported)
                {
                    _reported = true;
                    UnigramNative.Report(Logger.LogLevel.Warning, $"video: frame buffer is {target.Capacity} bytes, {_pixelWidth}x{_pixelHeight} needs {_frameSize}");
                }

                return;
            }

            var pixels = ArrayPool<byte>.Shared.Rent(_frameSize);

            try
            {
                int rendered;
                double position = 0;
                int finished = 0;

                fixed (byte* buffer = pixels)
                {
                    // stride 0 = packed (pixel_width * 4).
                    rendered = VideoNative.CachedRender(_handle, buffer, 0, &position, &finished);
                }

                seconds = position;
                completed = finished != 0;

                if (rendered != 1)
                {
                    // Normal while the cache is being written (the caller skips those ticks through
                    // IsCaching) and at the end of a one-shot animation; anything else is reported
                    // once, because this runs up to 60 times a second per animation.
                    if (!completed && !_reported)
                    {
                        _reported = true;
                        UnigramNative.Report(Logger.LogLevel.Warning, $"video: cached render produced no frame: {UnigramNative.LastError()}");
                    }

                    return;
                }

                pixels.CopyTo(0, target, 0, _frameSize);

                if (target.Length < _frameSize)
                {
                    target.Length = (uint)_frameSize;
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed from another thread while this frame was in flight.
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "video: cached render", ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }

        /// <summary>Rewinds to the first frame.</summary>
        public void Stop()
        {
            try
            {
                VideoNative.CachedStop(_handle);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "video: cached stop", ex);
            }
        }

        /// <summary>
        /// Queues this animation on the library's compression thread and returns immediately. The
        /// handle stays the caller's and may be disposed at any point afterwards.
        /// </summary>
        public void Cache()
        {
            try
            {
                VideoNative.CachedCache(_handle);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "video: cache", ex);
            }
        }

        public void Seek(double seconds)
        {
            try
            {
                VideoNative.CachedSeek(_handle, seconds);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"video: cached seek({seconds})", ex);
            }
        }

        public double FrameRate => _frameRate;

        // Live: it is 0 until the cache has been written, and the caller polls it.
        public int TotalFrame
        {
            get
            {
                try
                {
                    return VideoNative.CachedTotalFrame(_handle);
                }
                catch (ObjectDisposedException)
                {
                    return 0;
                }
            }
        }

        // Both are polled once per frame by VideoAnimatedImageTask.NextFrame, so they go straight
        // to the library: no cache, no allocation.
        public bool IsCaching
        {
            get
            {
                try
                {
                    return VideoNative.CachedIsCaching(_handle) != 0;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public bool IsReadyToCache
        {
            get
            {
                try
                {
                    return VideoNative.CachedIsReadyToCache(_handle) != 0;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public int PixelWidth => _pixelWidth;

        public int PixelHeight => _pixelHeight;

        public int Rotation => _rotation;

        public void Dispose()
        {
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
}
