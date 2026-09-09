//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Telegram.Native
{
    /// <summary>
    /// Single <see cref="NativeLibrary.SetDllImportResolver"/> callback for the whole assembly
    /// (the runtime only accepts one), shared by every P/Invoke of the Linux head.
    ///
    /// Each entry maps the name written in the <c>[LibraryImport]</c> attribute to
    /// (environment variable, candidate file names). Candidates are tried in this order:
    ///   1. the path in the environment variable, if set (development override),
    ///   2. next to the binary, and under <c>runtimes/linux-x64/native/</c>,
    ///   3. whatever the loader finds on the system path (ld.so cache, LD_LIBRARY_PATH).
    ///
    /// Add a library here rather than calling SetDllImportResolver again.
    /// </summary>
    internal static class NativeLibraryResolver
    {
        /// <summary>
        /// Name to use in <c>[LibraryImport]</c> for libunigram-native.so, the C ABI over the
        /// vendored native engines (rlottie for now; video, webp... as they land).
        /// </summary>
        public const string UnigramNative = "unigram-native";

        /// <summary>
        /// Name to use in <c>[LibraryImport]</c> for libunigram-calls.so, the C ABI over tgcalls
        /// + tg_owt that replaces the Telegram.Native.Calls C++/WinRT component. It is a SECOND
        /// library of ours rather than another module of libunigram-native.so on purpose (32 MB
        /// of WebRTC that must not be mapped at start, and a completely different dependency
        /// chain); the reasoning is written out at the top of
        /// <c>unigram-linux/native/calls/include/unigram_calls.h</c>. Optional: without it,
        /// <c>Telegram.Native.Calls.UnigramCalls.IsAvailable</c> is false and calls degrade the
        /// way the phase 1 stubs did.
        /// </summary>
        public const string UnigramCalls = "unigram-calls";

        /// <summary>
        /// Name to use in <c>[LibraryImport]</c> for libmpv, the audio engine of phase 5. It is a
        /// SYSTEM library (nothing in this repo builds it) and it is optional: soname 2 means mpv
        /// &gt;= 0.35, and a distribution that only ships <c>libmpv.so.1</c> gets no audio rather
        /// than a crash -- see <c>Telegram.Native.Media.MpvClient.IsAvailable</c>.
        /// </summary>
        public const string Mpv = "mpv";

        /// <summary>
        /// Name to use in <c>[LibraryImport]</c> for libpulse-simple, the microphone capture of
        /// phase 5 (<c>Telegram.Native.Audio.PulseCapture</c>). SYSTEM library, and optional the
        /// same way libmpv is: a machine without PulseAudio or PipeWire gets "no microphone"
        /// instead of a crash. On this desktop it is <c>pipewire-pulse</c> answering.
        /// </summary>
        public const string PulseSimple = "pulse-simple";

        /// <summary>
        /// libpulse itself, and only for <c>pa_strerror</c>: libpulse-simple links it, but the
        /// symbol is not re-exported, so asking for it by name is more honest than relying on the
        /// loader walking the dependency.
        /// </summary>
        public const string Pulse = "pulse";

        private static readonly Dictionary<string, (string Variable, string[] Files)> _libraries = new()
        {
            // Td/Client.cs imports "tdjson.dll" verbatim, from the Windows sources.
            ["tdjson.dll"] = ("TDJSON_PATH", new[] { "libtdjson.so", "libtdjson.so.1.8.67" }),
            [UnigramNative] = ("UNIGRAM_NATIVE_PATH", new[] { "libunigram-native.so" }),
            [UnigramCalls] = ("UNIGRAM_CALLS_PATH", new[] { "libunigram-calls.so" }),
            // Only soname 2 is listed on purpose: libmpv.so.1 is a different, incompatible ABI and
            // loading it would fail much later and much less clearly than not loading it at all.
            [Mpv] = ("MPV_PATH", new[] { "libmpv.so.2" }),
            // Both sonames are listed unlike libmpv's, because here they are the same ABI: the
            // .so.0 is what a runtime install ships and the bare .so only exists with the -dev
            // package.
            [PulseSimple] = ("PULSE_SIMPLE_PATH", new[] { "libpulse-simple.so.0", "libpulse-simple.so" }),
            [Pulse] = ("PULSE_PATH", new[] { "libpulse.so.0", "libpulse.so" }),
        };

        private static int _registered;

        /// <summary>
        /// Installs the resolver. Idempotent: the runtime throws if a resolver is set twice for
        /// the same assembly, so callers other than Program.Main can call this safely too.
        /// </summary>
        public static void Register(Assembly assembly)
        {
            if (System.Threading.Interlocked.Exchange(ref _registered, 1) == 0)
            {
                NativeLibrary.SetDllImportResolver(assembly, Resolve);
            }
        }

        public static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!_libraries.TryGetValue(libraryName, out var library))
            {
                // Not ours: let the default probing logic run.
                return IntPtr.Zero;
            }

            var overridden = Environment.GetEnvironmentVariable(library.Variable);
            if (!string.IsNullOrEmpty(overridden) && TryLoad(overridden, out var handle))
            {
                return handle;
            }

            foreach (var file in library.Files)
            {
                if (TryLoad(Path.Combine(AppContext.BaseDirectory, file), out handle)
                    || TryLoad(Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", file), out handle))
                {
                    return handle;
                }
            }

            foreach (var file in library.Files)
            {
                if (NativeLibrary.TryLoad(file, assembly, searchPath, out handle))
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        }

        private static bool TryLoad(string path, out IntPtr handle)
        {
            if (File.Exists(path))
            {
                return NativeLibrary.TryLoad(path, out handle);
            }

            handle = IntPtr.Zero;
            return false;
        }
    }
}
