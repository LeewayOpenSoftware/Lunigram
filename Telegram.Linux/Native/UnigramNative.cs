//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices;

namespace Telegram.Native
{
    /// <summary>
    /// Module-agnostic entry points of <c>libunigram-native.so</c>, the flat C ABI that replaces
    /// Telegram.Native / RLottie.winmd on Linux. The contract lives in
    /// <c>unigram-linux/native/unigram-native/include/unigram_native.h</c>; every module binding
    /// (lottie today, video / image / opus later) goes through <see cref="IsAvailable"/> first.
    ///
    /// The library is optional at runtime: until it has been built (on the Mint box, see
    /// unigram-linux/native) every binding degrades the way the phase 1 stubs did, which for
    /// animated stickers means "no animation" rather than a crash.
    /// </summary>
    internal static partial class UnigramNative
    {
        /// <summary>
        /// Name used by every <c>[LibraryImport]</c> of the port; resolved to the actual .so by
        /// <see cref="NativeLibraryResolver"/>.
        /// </summary>
        internal const string Library = NativeLibraryResolver.UnigramNative;

        /// <summary>
        /// UNIGRAM_NATIVE_ABI this binding was written against. A library that answers anything
        /// else is refused: the header promises it never changes, and a mismatch means a stale
        /// .so next to the binary, which would otherwise segfault much later.
        /// </summary>
        internal const int Abi = 1;

        /// <summary>
        /// True once the library has been loaded and its ABI checked. Probing happens once, on
        /// first use, and never throws.
        /// </summary>
        internal static bool IsAvailable { get; } = Probe();

        private static bool Probe()
        {
            int abi;

            try
            {
                abi = unigram_native_abi();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // Expected while the native side is being built: animated stickers and emoji stay
                // still, everything else works. UNIGRAM_NATIVE_PATH overrides the lookup.
                Report(Logger.LogLevel.Error, $"libunigram-native.so is not usable ({ex.GetType().Name}: {ex.Message}): native media disabled");
                return false;
            }

            if (abi != Abi)
            {
                Report(Logger.LogLevel.Error, $"libunigram-native.so reports ABI {abi}, this build speaks {Abi}: native media disabled");
                return false;
            }

            Report(Logger.LogLevel.Info, Version);

            // The library starts a background thread of its own the first time a video is
            // cached, and the header asks for Shutdown() before the process ends. Nobody was
            // calling it: not Program.Main, which just returns from host.Run(), and not
            // DesktopIntegration.Quit, which reaches Environment.Exit(0) or
            // Application.Current.Exit() -- and WindowContext.OnShutdownStarting, the one place
            // that drains finalizers on the way out, is compiled out on Linux. ProcessExit is
            // the hook that covers all of those at once, and hanging it here means it is armed
            // exactly when there is a library to shut down, instead of every exit path having
            // to remember. It does not cover a SIGKILL or a FailFast, which is why the native
            // side no longer depends on being asked nicely either.
            try
            {
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => Shutdown();
            }
            catch (Exception ex)
            {
                Report(Logger.LogLevel.Error, "cannot hook ProcessExit for unigram_native_shutdown", ex);
            }

            return true;
        }

        /// <summary>
        /// Logging that cannot become the failure. Logger.Log reaches SettingsService and TDLib, so
        /// it throws in the two places this binding logs from: the static probe (an exception there
        /// turns every later use into a TypeInitializationException) and the render worker, which
        /// keeps running while the app is shutting down.
        /// </summary>
        internal static void Report(Logger.LogLevel level, string message, Exception exception = null)
        {
            try
            {
                if (exception != null)
                {
                    Logger.Error(message, exception);
                }
                else if (level == Logger.LogLevel.Error)
                {
                    Logger.Error(message);
                }
                else if (level == Logger.LogLevel.Warning)
                {
                    Logger.Warning(message);
                }
                else if (level == Logger.LogLevel.Debug)
                {
                    Logger.Debug(message);
                }
                else
                {
                    Logger.Info(message);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Build string of the loaded library, e.g. "unigram-native 0.1.0 (rlottie 0.2, ...)".
        /// Empty when the library is missing. Owned by the library: never freed here, which is why
        /// the import returns a raw pointer instead of a marshalled string (the UTF-8 string
        /// marshaller frees what it converts).
        /// </summary>
        internal static string Version
        {
            get
            {
                try
                {
                    return Marshal.PtrToStringUTF8(unigram_native_version()) ?? string.Empty;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Reason for the last failure on the calling thread, for diagnostics only. Valid until
        /// the next failing call on this thread, so it is copied out immediately.
        /// </summary>
        internal static string LastError()
        {
            if (!IsAvailable)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUTF8(unigram_native_last_error()) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Stops and joins the library's background threads (the frame cache compressor). Hooked
        /// to <see cref="AppDomain.ProcessExit"/> by <c>Probe</c>, so no exit path has to call it
        /// by hand; safe to call more than once, and a no-op when the library was never loaded.
        /// It returns quickly even mid-cache: the abort is polled inside the compression pass,
        /// not only between passes.
        /// </summary>
        internal static void Shutdown()
        {
            if (!IsAvailable)
            {
                return;
            }

            try
            {
                unigram_native_shutdown();
            }
            catch (Exception ex)
            {
                Logger.Error("unigram_native_shutdown", ex);
            }
        }

        [LibraryImport(Library, EntryPoint = "unigram_native_abi")]
        private static partial int unigram_native_abi();

        [LibraryImport(Library, EntryPoint = "unigram_native_version")]
        private static partial IntPtr unigram_native_version();

        [LibraryImport(Library, EntryPoint = "unigram_native_last_error")]
        private static partial IntPtr unigram_native_last_error();

        [LibraryImport(Library, EntryPoint = "unigram_native_shutdown")]
        private static partial void unigram_native_shutdown();
    }

    /// <summary>
    /// <c>unigram_color_replacement</c>: every colour of the source resource exactly equal to
    /// <see cref="From"/> is painted as <see cref="To"/>, both packed 0x00RRGGBB. Flattened form of
    /// the <c>IReadOnlyDictionary&lt;int, int&gt;</c> the shared code carries in
    /// AnimatedImageSource.ColorReplacements.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UnigramColorReplacement
    {
        public uint From;
        public uint To;
    }
}
