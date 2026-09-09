//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Microsoft.UI.Xaml;

namespace Telegram
{
    public partial class Properties : Dictionary<string, object>
    {

    }

    /// <summary>
    /// The Linux stand-in for Common/WatchDog.cs, which is the AppCenter-style crash reporter:
    /// minidumps, stowed exceptions and an upload to the Unigram backend, none of which apply
    /// here. What remains is the part that is useful on a developer's machine - every unhandled
    /// exception is written with the Logger tail to crash.log in the app data folder, handled
    /// errors go to errors.log, and the next launch can tell the previous one crashed.
    /// </summary>
    public partial class WatchDog
    {
        private static readonly string _folder;
        private static readonly string _crashLog;
        private static readonly string _errorsLog;

        private static readonly object _lock = new();

        private static readonly string _userId;
        private static readonly long _launchTime;

        private static bool _lastSessionTerminatedUnexpectedly;

        static WatchDog()
        {
            _userId = AppSettings.AnonymousUserId;
            _launchTime = MonotonicUnixTime.Now;

            _folder = ApplicationData.Current.LocalFolder.Path;
            _crashLog = Path.Combine(_folder, "crash.log");
            _errorsLog = Path.Combine(_folder, "errors.log");
        }

        public static bool HasCrashedInLastSession { get; private set; }

        public static long LaunchTime => _launchTime;

        public static string UserId => _userId;

        public static void Initialize()
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            BootStrapper.Current.UnhandledException += OnUnhandledException;

            Read();
        }

        private static void OnUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            // The process is going down: write synchronously, before the runtime prints the
            // exception and exits.
            WriteCrash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            // Survivable: an async void or a forgotten task. Recorded as an error so it is not
            // lost, and observed so the finalizer thread does not take the process with it.
            TrackError(e.Exception);
            e.SetObserved();
        }

        private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            // Marked handled and recorded, as on Windows: one broken handler is not worth the
            // session. What cannot be survived reaches AppDomain.UnhandledException instead.
            Logger.Error(args.Exception.ToString());
            TrackError(args.Exception);

            args.Handled = true;
        }

        public static void TrackError(Exception ex)
        {
            Append(_errorsLog, ex.ToString());
        }

        public static void TrackError(string message)
        {
            Append(_errorsLog, message + "\n" + Environment.StackTrace);
        }

        public static void TrackEvent(string name, Properties properties = null)
        {
            // Analytics are not collected on Linux.
        }

        public static Architecture OSArchitecture()
        {
            return RuntimeInformation.OSArchitecture;
        }

        public static void MemoryStatus()
        {
            Logger.Debug(string.Format("Usage: {0}, working set: {1}", FileSizeConverter.Convert(GC.GetTotalMemory(false)), FileSizeConverter.Convert(Environment.WorkingSet)));
        }

        public static string BuildReport(int hresult)
        {
            var version = LinuxSystemInfo.ApplicationVersion;
            var language = LocaleService.Current.Id;

            var next = MonotonicUnixTime.Now - _launchTime;
            var diff = TimeSpan.FromSeconds(next).ToDuration();

            var count = AppSettings.Diagnostics.UpdateCount;

            var info =
                $"Current version: {version}\n" +
                $"Current language: {language}\n" +
                $"Current duration: {diff}\n" +
                $"Memory usage: {FileSizeConverter.Convert(GC.GetTotalMemory(false))}\n" +
                $"Working set: {FileSizeConverter.Convert(Environment.WorkingSet)}\n" +
                $"System: {LinuxSystemInfo.SystemVersion} ({LinuxSystemInfo.KernelVersion}, {RuntimeInformation.OSArchitecture})\n" +
                $"Update count: {count}\n" +
                $"HRESULT: 0x{hresult:X4}\n\n";

            var dump = Logger.Dump();
            return info + dump;
        }

        private static void WriteCrash(Exception ex)
        {
            try
            {
                var report = $"[{DateTime.UtcNow:O}] {_userId}\n{ex}\n\n{BuildReport(ex.HResult)}\n";

                lock (_lock)
                {
                    Directory.CreateDirectory(_folder);
                    File.WriteAllText(_crashLog, report);
                }

                Console.Error.WriteLine(report);
            }
            catch
            {
                // Nothing useful to do here, and throwing would hide the original exception.
            }
        }

        private static void Append(string path, string text)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(_folder);
                    File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {text}\n\n");
                }
            }
            catch
            {
                // If this fails for any reason we don't want the app to crash
            }
        }

        private static void Read()
        {
            try
            {
                if (File.Exists(_crashLog))
                {
                    _lastSessionTerminatedUnexpectedly = true;

                    // Kept beside the live one: the crash is what the user came to look at.
                    File.Copy(_crashLog, _crashLog + ".1", true);
                    File.Delete(_crashLog);
                }
            }
            catch
            {
                // If this fails for any reason we don't want the app to crash
            }
        }

        public static void Launch(ApplicationExecutionState previousExecutionState)
        {
            // NotRunning: An app could be in this state because it hasn't been launched
            // since the last time the user rebooted or logged in. It can also be in this
            // state if it was running but then crashed, or because the user closed it earlier.

            HasCrashedInLastSession =
                _lastSessionTerminatedUnexpectedly
                && previousExecutionState == ApplicationExecutionState.NotRunning;
        }

        public static void Suspend()
        {
            // crash.log is only written by an unhandled exception, so a clean exit has nothing
            // to clear. The Windows build deletes its crash.id marker here.
        }
    }

    public partial class VLCException : Exception
    {
        public VLCException(string message, string stackTrace)
            : base(message + "\n" + stackTrace)
        {
        }
    }

    public partial class VoipException : Exception
    {
        public VoipException(string message, string stackTrace)
            : base(message + "\n" + stackTrace)
        {
        }
    }

    public partial class NativeException : Exception
    {
        public NativeException(string message, string stackTrace)
            : base(message + "\n" + stackTrace)
        {
        }
    }
}
