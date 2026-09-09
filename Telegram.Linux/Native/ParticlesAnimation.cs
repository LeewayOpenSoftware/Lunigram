//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Win32.SafeHandles;
using Windows.Storage.Streams;
using Windows.UI;

namespace Telegram.Native
{
    /// <summary>
    /// Mirrors the ParticlesType enum of the Windows .idl; the values cross the ABI as-is, so the
    /// order is part of the contract.
    /// </summary>
    public enum ParticlesType
    {
        Media,
        Text,
        Status,
        Premium
    }

    /// <summary>
    /// Linux replacement for Telegram.Native.ParticlesAnimation, binding the particles module of
    /// <c>libunigram-native.so</c>. It is the dust Unigram paints on media and text spoilers, on
    /// status emoji and on premium features -- a CPU rasteriser with no platform dependency, which
    /// is why the port is a near-literal copy of the original .cpp (see
    /// unigram-linux/native/engines/misc/README.md).
    ///
    /// <para>Created and driven by AnimatedImage.LoadParticles / ParticlesAnimatedImageTask, one
    /// instance per presenter, always from the animation thread. The object is NOT thread-safe: it
    /// carries the particle state between frames, exactly as on Windows.</para>
    ///
    /// <para>When the native library is missing the animation is inert (a render is a no-op) and
    /// the spoiler simply shows no dust, which is how phase 1 behaved.</para>
    /// </summary>
    public sealed partial class ParticlesAnimation : IDisposable
    {
        private readonly ParticlesHandle _handle;

        // width * height * 4, packed BGRA: what the ABI writes and what WriteableBitmap holds.
        private readonly int _frameSize;

        // A render failure repeats 30 times a second; one line in the log is enough.
        private bool _reported;

        public ParticlesAnimation(int width, int height, double rasterizationScale, ParticlesType type, Color foreground, Color background)
        {
            PixelWidth = width;
            PixelHeight = height;

            if (!UnigramNative.IsAvailable || width <= 0 || height <= 0)
            {
                return;
            }

            try
            {
                var handle = unigram_particles_create(width, height, rasterizationScale, (int)type,
                    ParticlesColor.From(foreground), ParticlesColor.From(background));

                if (handle == null || handle.IsInvalid)
                {
                    UnigramNative.Report(Logger.LogLevel.Warning,
                        $"particles: cannot create {width}x{height}: {UnigramNative.LastError()}");
                    handle?.Dispose();
                    return;
                }

                _handle = handle;

                // Straight from the library rather than recomputed here: it is the size _render
                // validates against, and disagreeing with it is the one way to get a partial
                // frame.
                _frameSize = (int)unigram_particles_buffer_size(handle);
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"particles: create({width}x{height})", ex);
            }
        }

        /// <summary>
        /// Paints one frame and advances the state. No-op when the module is unavailable, which
        /// leaves the caller's buffer untouched (the spoiler shows its solid colour).
        /// </summary>
        public unsafe void RenderSync(IBuffer bitmap)
        {
            if (bitmap == null || _handle == null || _frameSize <= 0)
            {
                return;
            }

            // PixelBuffer wraps a WriteableBitmap and Uno's buffer extensions only accept its own
            // Buffer type; same unwrap as BufferSurface and the lottie binding.
            var target = PixelBuffer.Unwrap(bitmap);
            if (target.Capacity < _frameSize)
            {
                return;
            }

            // One staging copy, for the same reason as the lottie binding: Uno keeps the bytes
            // behind IBuffer internal, so there is no supported way to hand the library the
            // destination directly.
            var pixels = ArrayPool<byte>.Shared.Rent(_frameSize);

            try
            {
                int rendered;
                fixed (byte* buffer = pixels)
                {
                    rendered = unigram_particles_render(_handle, buffer, (nuint)_frameSize);
                }

                if (rendered == 0)
                {
                    if (!_reported)
                    {
                        _reported = true;
                        UnigramNative.Report(Logger.LogLevel.Warning,
                            $"particles: render failed: {UnigramNative.LastError()}");
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
                UnigramNative.Report(Logger.LogLevel.Error, "particles: render", ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }

        public int PixelWidth { get; }

        public int PixelHeight { get; }

        /// <summary>
        /// Nothing in the shared code disposes the animation -- ParticlesAnimatedImageTask just
        /// drops it, the way it dropped the Windows runtimeclass -- so the handle is a SafeHandle
        /// and the finaliser is what actually frees the particles. This is here for the callers
        /// that can do better.
        /// </summary>
        public void Dispose()
        {
            _handle?.Dispose();
        }

        /// <summary>
        /// <c>unigram_particles_color</c>: four bytes, A R G B, the same order and meaning as
        /// Windows.UI.Color. Passed BY VALUE, which for a 4-byte struct is a single register on
        /// SysV -- hence a blittable struct and no [MarshalAs] anywhere.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ParticlesColor
        {
            public byte A;
            public byte R;
            public byte G;
            public byte B;

            public static ParticlesColor From(Color color)
            {
                return new ParticlesColor { A = color.A, R = color.R, G = color.G, B = color.B };
            }
        }

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_particles_create")]
        private static partial ParticlesHandle unigram_particles_create(int width, int height,
            double rasterizationScale, int type, ParticlesColor foreground, ParticlesColor background);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_particles_render")]
        private static unsafe partial int unigram_particles_render(ParticlesHandle handle, byte* pixels, nuint sizeInBytes);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_particles_buffer_size")]
        private static partial nuint unigram_particles_buffer_size(ParticlesHandle handle);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_particles_destroy")]
        internal static partial void unigram_particles_destroy(IntPtr handle);
    }

    /// <summary>
    /// Keeps the particles alive while a render started on the animation thread is still inside the
    /// library, and frees them from the finaliser when nobody disposes the animation.
    /// </summary>
    internal sealed class ParticlesHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ParticlesHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            ParticlesAnimation.unigram_particles_destroy(handle);
            return true;
        }
    }
}
