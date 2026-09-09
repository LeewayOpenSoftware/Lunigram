//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Media;

namespace Telegram.Native.Opus
{
    /// <summary>
    /// Linux replacement for Telegram.Native.Opus.OpusOutput, binding the opus module of
    /// <c>libunigram-native.so</c> (libopus + libogg). Writes the mono 48 kHz Ogg/Opus file that
    /// Telegram expects in <c>inputMessageVoiceNote</c>.
    ///
    /// <para>Since phase 5 this is the encoder of the recorder:
    /// Telegram.Linux/Platform/ChatRecordEngine.cs feeds it the blocks that
    /// <see cref="Telegram.Native.Audio.PulseCapture"/> reads from the microphone, through
    /// <see cref="Write(ReadOnlySpan{float})"/>, so a recording is Ogg/Opus by the time the button
    /// comes up and is uploaded with no generation at all. Measured end to end in
    /// unigram-linux/spikes/VoiceRecordSpike: a 440 Hz tone recorded through this binding comes
    /// back out of the .ogg as 440 Hz.</para>
    ///
    /// <para>The two Windows callers are still there and still Windows-only:
    /// GenerationService.TranscodeOpusAsync, which turns a recorded WAV into a voice note through
    /// <see cref="Transcode"/>, and Common/Recording/VoiceSink, which pushes AudioFrames -- neither
    /// is in the subset, and neither is needed here.</para>
    ///
    /// <para>Differences from the Windows component, all in the engine and all deliberate: leftover
    /// samples are buffered instead of dropped, so <see cref="Write(ReadOnlySpan{float})"/> accepts
    /// any chunk size; and closing writes a real end-of-stream page, so a recording whose length is
    /// an exact multiple of 20 ms still reports its duration. See
    /// unigram-linux/native/engines/misc/README.md.</para>
    /// </summary>
    public sealed partial class OpusOutput : IDisposable
    {
        /// <summary>UNIGRAM_OPUS_SAMPLE_RATE. Baked into the Opus header; not negotiable.</summary>
        public const int SampleRate = 48000;

        /// <summary>UNIGRAM_OPUS_FRAME_SIZE: 20 ms at 48 kHz.</summary>
        public const int FrameSize = 960;

        private readonly OpusOutputHandle _handle;

        // A capture thread writes while the recording is stopped from another; the native object is
        // explicitly single-threaded, so the lock is here.
        private readonly object _lock = new();

        private bool _reported;

        public OpusOutput(string fileName)
        {
            if (!UnigramNative.IsAvailable || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            try
            {
                var handle = unigram_opus_output_create(fileName);

                if (handle == null || handle.IsInvalid)
                {
                    UnigramNative.Report(Logger.LogLevel.Error,
                        $"opus: cannot create {fileName}: {UnigramNative.LastError()}");
                    handle?.Dispose();
                    return;
                }

                _handle = handle;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"opus: create({fileName})", ex);
            }
        }

        /// <summary>
        /// True while the file is open and the encoder alive. The callers check this before feeding
        /// anything and report FILE_GENERATE_LOCATION_INVALID when it is false, so a missing native
        /// library surfaces as a failed generation instead of a silent empty file.
        /// </summary>
        public bool IsValid
        {
            get
            {
                if (_handle == null)
                {
                    return false;
                }

                try
                {
                    return unigram_opus_output_is_valid(_handle) != 0;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Duration of everything accepted so far, from the sample count rather than the clock.
        /// </summary>
        public TimeSpan Duration
        {
            get
            {
                if (_handle == null)
                {
                    return TimeSpan.Zero;
                }

                try
                {
                    return TimeSpan.FromMilliseconds(unigram_opus_output_duration_ms(_handle));
                }
                catch (ObjectDisposedException)
                {
                    return TimeSpan.Zero;
                }
            }
        }

        /// <summary>
        /// Reads a PCM 16-bit mono 48 kHz WAV and encodes it whole. Anything else is refused (the
        /// engine parses the RIFF chunks instead of assuming a 44-byte header, so a recorder that
        /// writes a LIST chunk no longer produces noise).
        /// </summary>
        public void Transcode(string fileName)
        {
            if (_handle == null || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            lock (_lock)
            {
                try
                {
                    if (unigram_opus_output_transcode_wav(_handle, fileName) == 0)
                    {
                        UnigramNative.Report(Logger.LogLevel.Error,
                            $"opus: cannot transcode {fileName}: {UnigramNative.LastError()}");
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    UnigramNative.Report(Logger.LogLevel.Error, $"opus: transcode({fileName})", ex);
                }
            }
        }

        /// <summary>
        /// Encodes float samples in [-1, 1], mono, 48 kHz. Any length: what does not fill a frame
        /// stays in the library's buffer and goes out with the next call or on close.
        /// <para>This is the entry point the Linux capture path will use (phase 5): there is no
        /// AudioFrame to unpack, just the span PipeWire hands over.</para>
        /// </summary>
        public unsafe void Write(ReadOnlySpan<float> samples)
        {
            if (_handle == null || samples.Length == 0)
            {
                return;
            }

            lock (_lock)
            {
                try
                {
                    fixed (float* pinned = samples)
                    {
                        if (unigram_opus_output_write_float(_handle, pinned, (nuint)samples.Length) == 0 && !_reported)
                        {
                            _reported = true;
                            UnigramNative.Report(Logger.LogLevel.Error,
                                $"opus: write failed: {UnigramNative.LastError()}");
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    UnigramNative.Report(Logger.LogLevel.Error, "opus: write", ex);
                }
            }
        }

        /// <summary>
        /// Same for 16-bit samples, which is what a WAV and most capture backends hand over.
        /// </summary>
        public unsafe void Write(ReadOnlySpan<short> samples)
        {
            if (_handle == null || samples.Length == 0)
            {
                return;
            }

            lock (_lock)
            {
                try
                {
                    fixed (short* pinned = samples)
                    {
                        if (unigram_opus_output_write_pcm16(_handle, pinned, (nuint)samples.Length) == 0 && !_reported)
                        {
                            _reported = true;
                            UnigramNative.Report(Logger.LogLevel.Error,
                                $"opus: write failed: {UnigramNative.LastError()}");
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    UnigramNative.Report(Logger.LogLevel.Error, "opus: write", ex);
                }
            }
        }

        /// <summary>
        /// Kept for VoiceSink, which is written against Windows.Media capture. Uno has no audio
        /// capture on Skia yet, so this only unwraps the frame and forwards it to
        /// <see cref="Write(ReadOnlySpan{float})"/>; when the PipeWire source lands it should call
        /// that overload directly and this can go.
        /// </summary>
        public unsafe void WriteFrame(AudioFrame frame)
        {
            if (_handle == null || frame == null)
            {
                return;
            }

            try
            {
                using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
                using var reference = buffer.CreateReference();

                // The byte-access interface is the only way to the samples, and it is COM: on Uno
                // it may simply not be implemented, which is an InvalidCastException, not a crash.
                var access = (IMemoryBufferByteAccess)reference;
                access.GetBuffer(out byte* data, out uint capacity);

                var count = (int)Math.Min(capacity, buffer.Length) / sizeof(float);
                if (count > 0)
                {
                    Write(new ReadOnlySpan<float>(data, count));
                }
            }
            catch (Exception ex)
            {
                if (!_reported)
                {
                    _reported = true;
                    UnigramNative.Report(Logger.LogLevel.Error, "opus: WriteFrame (no audio capture on Uno/Skia yet)", ex);
                }
            }
        }

        /// <summary>
        /// Flushes the tail, marks end of stream and closes the file. Idempotent, and the handle
        /// does it anyway if this is never called.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                _handle?.Dispose();
            }
        }

        /// <summary>
        /// <c>IMemoryBufferByteAccess</c>, declared here rather than taken from Telegram/Common:
        /// the shared declaration and its <c>Buffer()</c> extension are inside a <c>#if !LINUX</c>
        /// block, because everything else around them is WinRT interop.
        /// </summary>
        [ComImport]
        [Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMemoryBufferByteAccess
        {
            unsafe void GetBuffer(out byte* value, out uint capacity);
        }

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_opus_output_create", StringMarshalling = StringMarshalling.Utf8)]
        private static partial OpusOutputHandle unigram_opus_output_create(string path);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_opus_output_is_valid")]
        private static partial int unigram_opus_output_is_valid(OpusOutputHandle handle);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_opus_output_write_pcm16")]
        private static unsafe partial int unigram_opus_output_write_pcm16(OpusOutputHandle handle, short* samples, nuint sampleCount);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_opus_output_write_float")]
        private static unsafe partial int unigram_opus_output_write_float(OpusOutputHandle handle, float* samples, nuint sampleCount);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_opus_output_transcode_wav", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int unigram_opus_output_transcode_wav(OpusOutputHandle handle, string wavPath);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_opus_output_duration_ms")]
        private static partial long unigram_opus_output_duration_ms(OpusOutputHandle handle);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_opus_output_destroy")]
        internal static partial void unigram_opus_output_destroy(IntPtr handle);
    }

    /// <summary>
    /// Closes the stream properly (tail, end-of-stream page, file) exactly once, even if nobody
    /// disposes the encoder: <c>_destroy</c> calls <c>_close</c>, which calls <c>_finish</c>.
    /// </summary>
    internal sealed class OpusOutputHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public OpusOutputHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            OpusOutput.unigram_opus_output_destroy(handle);
            return true;
        }
    }
}
