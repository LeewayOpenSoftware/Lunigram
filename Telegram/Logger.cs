//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Telegram.Native;
using Telegram.Services;
using Telegram.Td;
using Telegram.Td.Api;

namespace Telegram
{
    public sealed partial class Logger
    {
        public enum LogLevel
        {
            Assert,
            Error,
            Warning,
            Info,
            Debug,
        }

        public static void Assert(object message = null, [CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0)
        {
            Log(LogLevel.Assert, message, member, filePath, line);
        }

        public static void Debug(object message = null, [CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0)
        {
            Log(LogLevel.Debug, message, member, filePath, line);
        }

        public static void Warning(object message = null, [CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0)
        {
            Log(LogLevel.Warning, message, member, filePath, line);
        }

        public static void Error(object message = null, [CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0)
        {
            Log(LogLevel.Error, message, member, filePath, line);
        }

        public static void Error(object message, Exception exception, [CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0)
        {
            Log(LogLevel.Error, message + "\n" + exception, member, filePath, line);
        }

        public static void Exception(Exception exception, [CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0)
        {
            // The exception, not just Environment.StackTrace: that only says where the catch is,
            // which the caller attribution already gives. A caught exception logged without its
            // own message and stack tells you nothing about what went wrong.
            Log(LogLevel.Error, exception, member, filePath, line);

            if (Constants.RELEASE)
            {
                WatchDog.TrackError(exception);
            }
        }

        public static void Info(object message = null, [CallerMemberName] string member = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int line = 0)
        {
            Log(LogLevel.Info, message, member, filePath, line);
        }

        // Ships with every crash report, so the size trades how much history a report
        // carries against how large every report gets.
        private const int TailCapacity = 200;

        private static readonly string[] _lastCalls = new string[TailCapacity];
        private static int _lastCallsHead;
        private static int _lastCallsCount;
        private static readonly object _lock = new();

#if LINUX
        private static ulong GetTickCount64()
        {
            return (ulong)Environment.TickCount64;
        }
#elif NET9_0_OR_GREATER
        [LibraryImport("kernel32.dll")]
        private static partial ulong GetTickCount64();
#else
        [SuppressUnmanagedCodeSecurity]
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();
#endif

        public static ulong TickCount => GetTickCount64();

#if LINUX
        // Same FILETIME epoch as the Win32 call, so the timestamp arithmetic below stays shared.
        private unsafe static void GetSystemTimeAsFileTime(long* pSystemTimeAsFileTime)
        {
            *pSystemTimeAsFileTime = DateTime.UtcNow.ToFileTimeUtc();
        }
#elif NET9_0_OR_GREATER
        [LibraryImport("kernel32.dll")]
        private unsafe static partial void GetSystemTimeAsFileTime(long* pSystemTimeAsFileTime);
#else
        [SuppressUnmanagedCodeSecurity]
        [DllImport("kernel32.dll")]
        private unsafe static extern void GetSystemTimeAsFileTime(long* pSystemTimeAsFileTime);
#endif

        static Logger()
        {
#if !LINUX
            NativeUtils.SetLogCallback(LogCallback);
#endif
        }

        private static void LogCallback(int level, string message, string member, string filePath, int line)
        {
            Log((LogLevel)level, message, member, filePath, line);
        }

        private static unsafe void Log(LogLevel level, object message, string member, string filePath, int line)
        {
            // We use UtcNow instead of Now because Now is expensive.
            long diff = 116444736000000000;
            long time = 0;

            GetSystemTimeAsFileTime(&time);

            string entry;
            if (message != null)
            {
                entry = string.Format(FormatWithMessage, (time - diff) / 10_000_000d, level, Path.GetFileName(filePath), line, member, message);
            }
            else
            {
                entry = string.Format(FormatWithoutMessage, (time - diff) / 10_000_000d, level, Path.GetFileName(filePath), line, member);
            }

            lock (_lock)
            {
                // Overwrite the oldest slot instead of shifting the window down, which
                // copied every retained entry on each call once the window was full.
                _lastCalls[_lastCallsHead] = entry;
                _lastCallsHead = (_lastCallsHead + 1) % TailCapacity;

                if (_lastCallsCount < TailCapacity)
                {
                    _lastCallsCount++;
                }
            }

#if LINUX
            if (TryGetVerbosityLevel(out int verbosity) && (int)level <= verbosity && (level != LogLevel.Debug || message != null))
#else
            if ((int)level <= AppSettings.VerbosityLevel && (level != LogLevel.Debug || message != null))
#endif
            {
                Client.Execute(new AddLogMessage(2, string.Format("[{0}:{1}][{2}] {3}", Path.GetFileName(filePath), line, member, message)));
            }

            if (level != LogLevel.Debug || message != null)
            {
#if LINUX
                LogFile.Write(entry);
#else
                System.Diagnostics.Debug.WriteLine(entry);
#endif
            }
        }

#if LINUX
        private static bool _verbosityAvailable;

        /// <summary>
        /// The verbosity setting, or false while there is no way to read it.
        ///
        /// <para>On Linux the first things Main does — the single instance check and the desktop
        /// colour scheme probe — run BEFORE Uno builds the application, and until then
        /// Windows.Storage.ApplicationData throws "The Package.Id is not initialized yet". Reading
        /// the level from there does not just fail: <c>ApplicationData.LocalFolder</c> is a
        /// <see cref="System.Lazy{T}"/> in its default mode, which CACHES the exception, so one
        /// early touch leaves the app data folder unreachable FOR THE WHOLE PROCESS and the app
        /// dies later, in its own constructor, on a line that has always worked. Hence the gate is
        /// a plain null check on the application and not a try/catch: by the time there is an
        /// <see cref="Application"/> the package is initialized, and until then nothing may so much
        /// as ask.</para>
        ///
        /// <para>Nothing is lost while it answers false — TDLib is not loaded that early either,
        /// and the entry still reaches stderr and unigram.log through <see cref="LogFile"/>, which
        /// has always known how to wait for the folder.</para>
        /// </summary>
        private static bool TryGetVerbosityLevel(out int verbosity)
        {
            if (!_verbosityAvailable && Microsoft.UI.Xaml.Application.Current == null)
            {
                verbosity = 0;
                return false;
            }

            try
            {
                verbosity = AppSettings.VerbosityLevel;
                _verbosityAvailable = true;
                return true;
            }
            catch
            {
                verbosity = 0;
                return false;
            }
        }
#endif

        //private const string FormatWithMessage = "[{0:yyyy-MM-dd HH\\:mm\\:ss\\:ffff}][{1}][{2}:{3}] {4}";
        //private const string FormatWithoutMessage = "[{0:yyyy-MM-dd HH\\:mm\\:ss\\:ffff}][{1}][{2}:{3}]";

        private const string FormatWithMessage = "[{0:F3}][{2}:{3}][{4}] {5}";
        private const string FormatWithoutMessage = "[{0:F3}][{2}:{3}][{4}]";

        public static unsafe string Dump()
        {
            // We use UtcNow instead of Now because Now is expensive.
            long diff = 116444736000000000;
            long time = 0;

            GetSystemTimeAsFileTime(&time);

            var builder = new StringBuilder();

            lock (_lock)
            {
                // Once the window has wrapped, the slot due to be written next is the oldest.
                var start = _lastCallsCount < TailCapacity ? 0 : _lastCallsHead;

                for (int i = 0; i < _lastCallsCount; i++)
                {
                    builder.Append(_lastCalls[(start + i) % TailCapacity]);
                    builder.Append('\n');
                }
            }

            // Marks when the report was taken. Appended to the output rather than stored as
            // an entry, so that dumping neither evicts a line nor leaves a trail of markers
            // in the next dump.
            builder.AppendFormat("[{0:F3}] Bump", (time - diff) / 10_000_000d);
            return builder.ToString();
        }
    }

    public partial class RuntimeException : Exception
    {
        public RuntimeException(Exception innerException)
            : base(innerException.Message, innerException)
        {

        }
    }
}
