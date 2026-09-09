//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Composition;
using Telegram.Native;
using Windows.Foundation;
using Windows.Graphics.Capture;

namespace Telegram.Native.Calls
{
    /// <summary>
    /// Video sink: what WebRTC hands decoded frames to.
    ///
    /// On Windows this is <c>VoipVideoOutput.h</c>, 844 lines of Direct2D plus an HLSL shader
    /// that converts I420 to RGB on the GPU and draws straight into the composition surface.
    /// None of that ports. Here the native side only delivers the three I420 planes, this class
    /// keeps the most recent frame, and the drawing is left to whoever owns the visual --
    /// <see cref="CopyFrameTo"/> is the hand-off point.
    ///
    /// The <see cref="CompositionGraphicsDevice"/> and <see cref="SpriteVisual"/> arguments are
    /// kept so that the shared XAML keeps compiling and so that the eventual Skia renderer has
    /// them, but nothing is drawn from here: this class is deliberately free of any UI thread
    /// affinity, because <see cref="FrameReceived"/> is raised from a WebRTC decoding thread.
    /// Subscribers must dispatch.
    /// </summary>
    public sealed unsafe partial class VoipVideoOutputSink : IDisposable
    {
        private IntPtr _handle;
        private GCHandle _self;

        // Latest frame, as I420. Copied out of the native callback (a memcpy of three planes)
        // rather than converted there: conversion is the renderer's business and its cost
        // should not be paid on WebRTC's decoding thread.
        private readonly object _frameLock = new();
        private byte[] _frame;
        private int _frameWidth;
        private int _frameHeight;
        private int _frameRotation;
        private bool _frameValid;

        public VoipVideoOutputSink(CompositionGraphicsDevice device, SpriteVisual visual, bool mirrored, bool uniformToFill)
        {
            Device = device;
            Visual = visual;
            IsMirrored = mirrored;
            UniformToFill = uniformToFill;

            if (!UnigramCalls.IsAvailable)
            {
                return;
            }

            _self = GCHandle.Alloc(this, GCHandleType.Normal);
            _handle = UnigramCalls.unigram_voip_sink_create(&OnFrameStatic, GCHandle.ToIntPtr(_self));

            if (_handle == IntPtr.Zero)
            {
                _self.Free();
            }
        }

        internal IntPtr Handle => _handle;

        internal CompositionGraphicsDevice Device { get; }

        internal SpriteVisual Visual { get; }

        public bool IsMirrored { get; set; }

        public bool UniformToFill { get; }

        public int PixelWidth => _handle == IntPtr.Zero ? 0 : UnigramCalls.unigram_voip_sink_width(_handle);

        public int PixelHeight => _handle == IntPtr.Zero ? 0 : UnigramCalls.unigram_voip_sink_height(_handle);

        public event TypedEventHandler<VoipVideoOutputSink, FrameReceivedEventArgs> FrameReceived;

        /// <summary>
        /// Stops delivery and releases the native sink. Safe to call twice, and safe to call
        /// while frames are in flight: the native side flips a flag before returning, so no
        /// callback can be running against a freed <see cref="GCHandle"/> afterwards.
        /// </summary>
        public void Stop()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_sink_release(handle);
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Converts the most recent frame to BGRA8888 into <paramref name="destination"/>, which
        /// must hold <c>PixelWidth * PixelHeight * 4</c> bytes. Returns false when no frame has
        /// arrived yet or the buffer is too small. This is the only place I420 is expanded, and
        /// it runs on the caller's thread on purpose: the decoding thread must not pay for it.
        ///
        /// u-038-a: the expansion itself is now <c>unigram_video_i420_to_bgra</c> (vendored
        /// swscale, in libunigram-native.so — not libunigram-calls.so, hence the separate
        /// <see cref="UnigramNative.IsAvailable"/> gate below) instead of the scalar loop that
        /// used to be the whole method body. The scalar loop stays as the fallback: it runs when
        /// libunigram-native.so is not built yet (the same "degrades instead of crashing"
        /// contract every other native binding in this port keeps), and for frames under the
        /// native function's 16x16 safety floor, which no real call ever sends but which the
        /// native side refuses rather than risk (see unigram_video_i420_to_bgra's own comment for
        /// why that floor exists — measured, not guessed).
        /// </summary>
        public bool CopyFrameTo(Span<byte> destination, out int width, out int height, out int rotation)
        {
            lock (_frameLock)
            {
                width = _frameWidth;
                height = _frameHeight;
                rotation = _frameRotation;

                if (!_frameValid || _frame == null || destination.Length < width * height * 4)
                {
                    return false;
                }

                var ySize = width * height;
                var chromaWidth = (width + 1) / 2;
                var chromaHeight = (height + 1) / 2;
                var uOffset = ySize;
                var vOffset = ySize + chromaWidth * chromaHeight;

                if (UnigramNative.IsAvailable)
                {
                    fixed (byte* framePtr = _frame)
                    fixed (byte* destinationPtr = destination)
                    {
                        int status = VideoFrameConversion.unigram_video_i420_to_bgra(
                            framePtr, width,
                            framePtr + uOffset, chromaWidth,
                            framePtr + vOffset, chromaWidth,
                            width, height,
                            destinationPtr, width * 4, destination.Length);

                        if (status == VideoFrameConversion.UnigramOk)
                        {
                            return true;
                        }

                        // Falls through to the scalar loop below - the only expected reasons are
                        // the safety floor (a frame no real call sends) or the library not being
                        // usable, neither of which should drop the frame.
                    }
                }

                for (int row = 0; row < height; row++)
                {
                    var chromaRow = row / 2;
                    for (int column = 0; column < width; column++)
                    {
                        var y = _frame[row * width + column] - 16;
                        var u = _frame[uOffset + chromaRow * chromaWidth + column / 2] - 128;
                        var v = _frame[vOffset + chromaRow * chromaWidth + column / 2] - 128;

                        // BT.601 limited range, the same matrix the Windows shader uses -- and the
                        // same one unigram_video_i420_to_bgra hardcodes, above, on purpose: this
                        // is a fallback for the SAME conversion, not a different one.
                        var c = 298 * y;
                        var b = (c + 516 * u + 128) >> 8;
                        var g = (c - 100 * u - 208 * v + 128) >> 8;
                        var r = (c + 409 * v + 128) >> 8;

                        var offset = (row * width + column) * 4;
                        destination[offset + 0] = Clamp(b);
                        destination[offset + 1] = Clamp(g);
                        destination[offset + 2] = Clamp(r);
                        destination[offset + 3] = 255;
                    }
                }

                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Clamp(int value)
        {
            return value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
        }

        /// <summary>
        /// unigram_video_i420_to_bgra, from unigram_native.h's "Frame conversion (calls)"
        /// section: the one function of libunigram-native.so this class needs. Kept as its own
        /// tiny static class rather than folded into <see cref="Telegram.Native.VideoNative"/>,
        /// which is scoped one to one with the unigram_video_* / unigram_cached_video_* sticker
        /// and GIF engine ABI (unigram_video.h) -- this symbol is declared directly in
        /// unigram_native.h instead, a sibling section, not a member of that engine's ABI.
        /// </summary>
        private static unsafe partial class VideoFrameConversion
        {
            /// <summary>UNIGRAM_OK from unigram_native.h's unigram_status.</summary>
            internal const int UnigramOk = 0;

            [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_video_i420_to_bgra")]
            internal static partial int unigram_video_i420_to_bgra(
                byte* y, int strideY,
                byte* u, int strideU,
                byte* v, int strideV,
                int width, int height,
                byte* pixels, int dstStride, int capacity);
        }

        private void OnFrame(byte* y, int strideY, byte* u, int strideU, byte* v, int strideV,
            int width, int height, int rotation)
        {
            var chromaWidth = (width + 1) / 2;
            var chromaHeight = (height + 1) / 2;
            var size = width * height + chromaWidth * chromaHeight * 2;

            lock (_frameLock)
            {
                if (_frame == null || _frame.Length < size)
                {
                    _frame = new byte[size];
                }

                fixed (byte* destination = _frame)
                {
                    // Row by row: the native strides are not the widths, and a straight memcpy
                    // of stride * height would copy padding into the next row's pixels.
                    for (int row = 0; row < height; row++)
                    {
                        Buffer.MemoryCopy(y + (long)row * strideY, destination + (long)row * width, width, width);
                    }

                    var uDestination = destination + width * height;
                    var vDestination = uDestination + chromaWidth * chromaHeight;

                    for (int row = 0; row < chromaHeight; row++)
                    {
                        Buffer.MemoryCopy(u + (long)row * strideU, uDestination + (long)row * chromaWidth, chromaWidth, chromaWidth);
                        Buffer.MemoryCopy(v + (long)row * strideV, vDestination + (long)row * chromaWidth, chromaWidth, chromaWidth);
                    }
                }

                _frameWidth = width;
                _frameHeight = height;
                _frameRotation = rotation;
                _frameValid = true;
            }

            FrameReceived?.Invoke(this, new FrameReceivedEventArgs(width, height));
        }

        // u-036: this one callback is logged per EPISODE, not per call, and it is the only one of
        // the twenty-two that needs the distinction.
        //
        // Everywhere else the swallowed catch guards something a user does - answering a call,
        // picking a file, a property update - so a throw is an event and one line per event is
        // exactly right. This one is delivered per VIDEO FRAME from a WebRTC thread, so whatever
        // makes it throw once will almost certainly make it throw thirty times a second for as
        // long as the call lasts. Logged per call it would bury the very line that says what broke
        // under thousands of copies of itself, and it would do the burying on the hot path, inside
        // the frame delivery of a live call.
        //
        // So: the first failure of an episode carries the exception, the rest only count, and the
        // first frame that succeeds afterwards reports how many were lost. Two lines per episode
        // however long it runs, and the stack is in the first one - where it is still useful.
        //
        // The latch is cleared on success rather than by a timer, the same shape as the
        // CalendarPopup guard in u-091, because "it started working again" is the only signal that
        // actually ends an episode. Cost on the healthy path is one relaxed read of a static int
        // per frame, which is nothing next to the frame conversion it sits in front of; the
        // interlocked write only happens when something is already wrong.
        private static int _frameFailing;
        private static int _frameFailures;

        [UnmanagedCallersOnly]
        private static void OnFrameStatic(IntPtr user, byte* y, int strideY, byte* u, int strideU,
            byte* v, int strideV, int width, int height, int rotation)
        {
            // Never let an exception cross back into C++: the frame is delivered from a WebRTC
            // thread and unwinding through it would take the whole process down.
            try
            {
                if (GCHandle.FromIntPtr(user).Target is VoipVideoOutputSink sink)
                {
                    sink.OnFrame(y, strideY, u, strideU, v, strideV, width, height, rotation);
                }

                // Read before writing: on the healthy path this is a plain load that is almost
                // always 0, and the interlocked exchange never runs.
                if (Volatile.Read(ref _frameFailing) != 0
                    && Interlocked.Exchange(ref _frameFailing, 0) != 0)
                {
                    var lost = Interlocked.Exchange(ref _frameFailures, 0);
                    Logger.Warning(nameof(OnFrame) + " recovered after " + lost + " dropped frame(s)");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _frameFailures);

                // Only the frame that opens an episode carries the exception; the others are
                // counted and reported by the recovery line above.
                if (Interlocked.Exchange(ref _frameFailing, 1) == 0)
                {
                    Logger.Error(nameof(OnFrame), ex);
                }
            }
        }
    }

    /// <summary>
    /// Common part of camera and screen capture: both are a <c>tgcalls::VideoCaptureInterface</c>
    /// on the native side, so both are one handle here.
    /// </summary>
    public partial class VoipCaptureBase : IDisposable
    {
        private protected IntPtr _handle;

        public void SetState(VoipVideoState state)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_capture_set_state(_handle, (int)state);
            }
        }

        public void SetOutput(VoipVideoOutputSink sink)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_capture_set_output(_handle, sink?.Handle ?? IntPtr.Zero);
            }
        }

        public void Stop()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_capture_stop(handle);
                UnigramCalls.unigram_voip_capture_release(handle);
            }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        internal IntPtr Handle => _handle;

        /// <summary>
        /// Never raised yet: tgcalls only reports a fatal capture error through
        /// <c>setOnFatalError</c>, which the flat ABI does not carry. Kept because the shared
        /// call page subscribes to it.
        /// </summary>
        public event TypedEventHandler<VoipCaptureBase, object> FatalErrorOccurred;

        private protected void RaiseFatalError()
        {
            FatalErrorOccurred?.Invoke(this, null);
        }
    }

    public sealed partial class VoipVideoCapture : VoipCaptureBase
    {
        public VoipVideoCapture(string id)
        {
            if (UnigramCalls.IsAvailable)
            {
                _handle = UnigramCalls.unigram_voip_capture_create_camera(id ?? string.Empty);
            }
        }

        /// <summary>Cameras as (id, name); the id is what <see cref="VoipVideoCapture"/> takes.</summary>
        public static (string Id, string Name)[] GetDevices()
        {
            return UnigramCalls.ReadTable((buffer, length) =>
            {
                var count = UnigramCalls.unigram_voip_video_devices(buffer, length, out var needed);
                return (count, needed);
            });
        }

        public void SwitchToDevice(string deviceId)
        {
            if (Handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_capture_switch_device(Handle, deviceId ?? string.Empty);
            }
        }

        public void SetPreferredAspectRatio(float aspectRatio)
        {
            if (Handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_capture_set_preferred_aspect_ratio(Handle, aspectRatio);
            }
        }
    }

    /// <summary>
    /// Screen and window capture.
    ///
    /// The Windows constructor takes a <see cref="GraphicsCaptureItem"/>, which the WinRT picker
    /// produces. There is no such picker here: the sources come from
    /// <see cref="GetSources"/> (tgcalls' DesktopCaptureSourceManager over the X11 / PipeWire
    /// capturer inside tg_owt) and are addressed by id. The WinRT overload is kept so the shared
    /// XAML still compiles, and it produces an inert capture -- the id-taking overload is the
    /// one to call on Linux.
    /// </summary>
    public sealed partial class VoipScreenCapture : VoipCaptureBase
    {
        public VoipScreenCapture(GraphicsCaptureItem item)
        {
            // Nothing to do: there is no GraphicsCaptureItem on Linux. Left inert rather than
            // throwing, so that a shared code path that still calls this degrades to "no screen
            // sharing" instead of taking the call down.
        }

        public VoipScreenCapture(string sourceId)
        {
            if (UnigramCalls.IsAvailable)
            {
                _handle = UnigramCalls.unigram_voip_capture_create_screen(sourceId ?? string.Empty);
            }
        }

        /// <summary>Screens (<paramref name="windows"/> false) or windows (true), as (id, title).</summary>
        public static (string Id, string Name)[] GetSources(bool windows)
        {
            return UnigramCalls.ReadTable((buffer, length) =>
            {
                var count = UnigramCalls.unigram_voip_screen_sources(windows ? 1 : 0, buffer, length, out var needed);
                return (count, needed);
            });
        }

        /// <summary>
        /// Never raised: tgcalls' <c>setOnPause</c> is not carried by the flat ABI. Kept because
        /// the shared screen sharing UI subscribes to it.
        /// </summary>
        public event TypedEventHandler<VoipScreenCapture, bool> Paused;

        /// <summary>
        /// True when the desktop capturer actually found something to capture. It is a real
        /// probe, not a constant: an X11 session answers, and a Wayland session without the
        /// portal does not.
        /// </summary>
        public static bool IsSupported()
        {
            return UnigramCalls.IsAvailable && GetSources(false).Length > 0;
        }

        private void RaisePaused(bool paused)
        {
            Paused?.Invoke(this, paused);
        }
    }
}
