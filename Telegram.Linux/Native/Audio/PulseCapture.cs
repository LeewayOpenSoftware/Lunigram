//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Telegram.Native.Audio
{
    /// <summary>
    /// One block of captured audio, always <see cref="PulseCapture.FrameSize"/> mono samples in
    /// [-1, 1]. A span rather than an array because both consumers -- the waveform accumulator and
    /// the Opus encoder -- read it and nobody keeps it: the same buffer is refilled 50 times a
    /// second.
    /// </summary>
    public delegate void PulseCaptureCallback(ReadOnlySpan<float> samples);

    /// <summary>
    /// Microphone capture for the Linux head, on the PulseAudio <c>pa_simple</c> API. This is what
    /// replaces <c>MediaCapture</c> + <c>MediaFrameReader</c> of the Windows recorder: Uno/Skia has
    /// no audio capture, and Unigram's Windows path is WinRT from top to bottom.
    ///
    /// <para>Why pa_simple and not PipeWire's own API: this machine (and every desktop that runs
    /// PipeWire) exposes the PulseAudio protocol through <c>pipewire-pulse</c>, so one binding
    /// covers both stacks, and the whole surface is four blocking calls -- there is no event loop
    /// to drive, no thread of theirs to keep alive, and nothing to marshal back. The blocking read
    /// is what we want anyway: the recording IS the read loop.</para>
    ///
    /// <para>The stream is asked for exactly what <see cref="Telegram.Native.Opus.OpusOutput"/>
    /// eats -- mono, 48 kHz, 32-bit float -- and the server resamples and downmixes whatever the
    /// device really is. That is the one big simplification over the Windows engine, which had to
    /// renegotiate the frame source's format and, failing that, record a WAV and transcode it when
    /// sending: here there is no format to negotiate and no transcode, so every recording is
    /// Ogg/Opus by the time the user lets go of the button.</para>
    ///
    /// <para>Threading: <see cref="Start"/> spawns one thread that owns the handle for its whole
    /// life; <see cref="SamplesAvailable"/> is raised on it. <see cref="Stop"/> asks it to finish
    /// and joins -- it never frees the handle underneath a blocked read, which is what a
    /// <c>pa_simple_free</c> from the UI thread would be.</para>
    /// </summary>
    public sealed partial class PulseCapture : IDisposable
    {
        /// <summary>Not negotiable: it is what the Opus header of a voice note says.</summary>
        public const int SampleRate = 48000;

        public const int ChannelCount = 1;

        /// <summary>20 ms at 48 kHz, one Opus frame, and the read size of the loop.</summary>
        public const int FrameSize = 960;

        private const int FrameBytes = FrameSize * sizeof(float);

        // pa_stream_direction_t
        private const int PA_STREAM_RECORD = 2;

        // pa_sample_format_t: U8, ALAW, ULAW, S16LE, S16BE, FLOAT32LE...
        private const int PA_SAMPLE_FLOAT32LE = 5;

        // (uint32_t) -1 is "let the server pick", which is what every field but fragsize wants.
        private const uint Unspecified = uint.MaxValue;

        private readonly string _application;
        private readonly string _stream;
        private readonly string _device;

        private IntPtr _handle;
        private Thread _thread;

        // Read by the capture thread on every iteration and written by whoever stops it.
        private volatile bool _running;

        /// <summary>
        /// Raised on the capture thread, once per 20 ms of audio. Set it before
        /// <see cref="Start"/>.
        /// </summary>
        public PulseCaptureCallback SamplesAvailable;

        /// <summary>
        /// The device gave up mid-recording (unplugged, or the server went away). Raised on the
        /// capture thread, once, right before it ends. The equivalent of MediaCapture.Failed, and
        /// it exists for the same reason: without it the UI sits in a recording state that can
        /// never end.
        /// </summary>
        public event EventHandler<string> Failed;

        public PulseCapture(string application, string stream, string device = null)
        {
            _application = application;
            _stream = stream;
            _device = device;
        }

        /// <summary>
        /// True when libpulse-simple could be loaded at all. False on a machine with no PulseAudio
        /// and no PipeWire, where the answer has to be "no microphone" rather than a crash.
        /// </summary>
        public static bool IsAvailable { get; } = Probe();

        private static bool Probe()
        {
            foreach (var name in new[] { "libpulse-simple.so.0", "libpulse-simple.so" })
            {
                // The handle is deliberately not freed: the runtime is going to keep the library
                // loaded for the P/Invokes anyway, and unloading it here would only make the first
                // real call load it again.
                if (NativeLibrary.TryLoad(name, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Opens a recording stream and closes it again, to answer whether there is a microphone
        /// to record from. This is the closest thing Linux has to the capability check Windows
        /// does before recording: there is no permission to ask for, but the server does say
        /// whether a source exists and whether we are allowed to read it.
        /// </summary>
        public static bool CanCapture(out string error)
        {
            error = null;

            if (!IsAvailable)
            {
                error = "libpulse-simple.so.0 is not available";
                return false;
            }

            var handle = Open(null, "Unigram", "microphone check", out error);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            pa_simple_free(handle);
            return true;
        }

        /// <summary>
        /// Opens the device and starts reading. Returns false, with a message, when there is
        /// nothing to record from -- the caller reports it as a failed recording, exactly as the
        /// Windows engine does when InitializeAsync throws.
        /// </summary>
        public bool Start(out string error)
        {
            error = null;

            if (_thread != null)
            {
                error = "already recording";
                return false;
            }

            if (!IsAvailable)
            {
                error = "libpulse-simple.so.0 is not available";
                return false;
            }

            var handle = Open(_device, _application, _stream, out error);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            _handle = handle;
            _running = true;

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "PulseCapture",

                // Above normal: a late read is a gap in the recording, and this thread does
                // nothing but block on the device.
                Priority = ThreadPriority.AboveNormal
            };

            _thread.Start();
            return true;
        }

        /// <summary>
        /// Stops reading and closes the device. Blocks until the capture thread is out of its
        /// read, which is at most one fragment (20 ms), so it is safe to call from the UI thread.
        /// Idempotent.
        /// </summary>
        public void Stop()
        {
            var thread = _thread;

            _running = false;
            _thread = null;

            if (thread != null && thread != Thread.CurrentThread)
            {
                // Generous, and only reached if the server stopped answering: the handle is freed
                // by the thread itself, so giving up here leaks a stream rather than freeing one
                // that is still being read.
                thread.Join(TimeSpan.FromSeconds(2));
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void Run()
        {
            var buffer = new float[FrameSize];
            string failure = null;

            try
            {
                while (_running)
                {
                    int result;
                    int error;

                    unsafe
                    {
                        fixed (float* pinned = buffer)
                        {
                            result = pa_simple_read(_handle, pinned, (nuint)FrameBytes, out error);
                        }
                    }

                    if (result < 0)
                    {
                        failure = Describe(error);
                        break;
                    }

                    // Straight to the consumer on this thread: marshalling to the UI thread would
                    // cost an allocation per 20 ms and the consumers (waveform, encoder) are both
                    // written to be called from here.
                    SamplesAvailable?.Invoke(buffer);
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            finally
            {
                var handle = _handle;
                _handle = IntPtr.Zero;

                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        pa_simple_free(handle);
                    }
                    catch
                    {
                        // Nothing to do about it, and throwing out of a thread would take the
                        // process down.
                    }
                }
            }

            if (failure != null && _running)
            {
                // Only when nobody asked us to stop: a read that fails because the stream is being
                // torn down is not a device failure.
                _running = false;

                try
                {
                    Failed?.Invoke(this, failure);
                }
                catch
                {
                }
            }
        }

        private static IntPtr Open(string device, string application, string stream, out string error)
        {
            var spec = new SampleSpec
            {
                Format = PA_SAMPLE_FLOAT32LE,
                Rate = SampleRate,
                Channels = ChannelCount
            };

            var attr = new BufferAttr
            {
                MaxLength = Unspecified,
                TLength = Unspecified,
                PreBuf = Unspecified,
                MinReq = Unspecified,

                // The only field that matters for recording: how much audio the server hands over
                // at a time. One Opus frame, so a read returns in 20 ms and stopping is prompt.
                FragSize = FrameBytes
            };

            try
            {
                var handle = pa_simple_new(null, application, PA_STREAM_RECORD, device, stream,
                    in spec, IntPtr.Zero, in attr, out var code);

                if (handle == IntPtr.Zero)
                {
                    error = Describe(code);
                    return IntPtr.Zero;
                }

                error = null;
                return handle;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                error = ex.Message;
                return IntPtr.Zero;
            }
        }

        private static string Describe(int error)
        {
            try
            {
                // IntPtr and not string on purpose: the generated UTF-8 marshaller frees what a
                // string return gives back, and this pointer belongs to libpulse -- it is a
                // constant of theirs, so freeing it would corrupt their heap.
                var message = Marshal.PtrToStringUTF8(pa_strerror(error));
                if (!string.IsNullOrEmpty(message))
                {
                    return message + " (" + error + ")";
                }
            }
            catch
            {
                // pa_strerror lives in libpulse, not in libpulse-simple: if the loader could not
                // follow the dependency the number on its own still says which error it was.
            }

            return "PulseAudio error " + error;
        }

        /// <summary>
        /// <c>pa_sample_spec</c>. The format is a C enum, so a 32-bit int, and the trailing byte
        /// pads the struct to 12 bytes on both sides of the boundary.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SampleSpec
        {
            public int Format;
            public uint Rate;
            public byte Channels;
        }

        /// <summary>
        /// <c>pa_buffer_attr</c>: five uint32 in this order.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct BufferAttr
        {
            public uint MaxLength;
            public uint TLength;
            public uint PreBuf;
            public uint MinReq;
            public uint FragSize;
        }

        [LibraryImport(NativeLibraryResolver.PulseSimple, EntryPoint = "pa_simple_new", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr pa_simple_new(string server, string name, int direction, string device, string streamName,
            in SampleSpec spec, IntPtr map, in BufferAttr attr, out int error);

        [LibraryImport(NativeLibraryResolver.PulseSimple, EntryPoint = "pa_simple_read")]
        private static unsafe partial int pa_simple_read(IntPtr handle, void* data, nuint bytes, out int error);

        [LibraryImport(NativeLibraryResolver.PulseSimple, EntryPoint = "pa_simple_free")]
        private static partial void pa_simple_free(IntPtr handle);

        [LibraryImport(NativeLibraryResolver.Pulse, EntryPoint = "pa_strerror")]
        private static partial IntPtr pa_strerror(int error);
    }
}
