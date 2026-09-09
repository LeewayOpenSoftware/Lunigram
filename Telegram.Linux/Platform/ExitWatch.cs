//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Telegram.Common
{
    /// <summary>
    /// Names the way the process ends, and saves the settings on the ways that would not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A silent death leaves nothing behind: the log stops mid line, the journal has nothing, and
    /// the only evidence is a number in the shell. This puts a breadcrumb on each of the three
    /// doors out of a Linux process, so that number always comes with a line that says who opened
    /// which one:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b><c>on_exit(3)</c></b> - fires for ANY <c>exit(status)</c>, including
    /// one made from native code that never reaches managed shutdown, and unlike <c>atexit</c> it
    /// receives the status. This is the one that separates "somebody called exit(N)" from
    /// "the runtime went out another way": if the breadcrumb is there, the call came through
    /// libc's exit and its stack trace names the caller; if the process ends with a status and no
    /// breadcrumb, it did not.</description></item>
    /// <item><description><b><see cref="AppDomain.ProcessExit"/></b> - managed shutdown. Measured
    /// on .NET 10: this does NOT run for SIGTERM, so its absence does not mean a crash.</description></item>
    /// <item><description><b>SIGTERM / SIGINT / SIGHUP / SIGQUIT</b> - a kill from outside. The
    /// runtime ends the process with <c>exit(128 + signo)</c> from its own signal thread and does
    /// not raise <see cref="AppDomain.ProcessExit"/>, so without this the settings written in the
    /// last half second would be the ones the save timer had not reached yet.</description></item>
    /// </list>
    /// <para>
    /// Everything here is written to survive being called at a bad moment: the first line goes out
    /// with a raw <c>write(2)</c> to stderr, which needs no lock and no allocation, and only then
    /// does it try the log file and the settings flush, each inside its own try.
    /// </para>
    /// </remarks>
    public static class ExitWatch
    {
        private static bool _installed;
        private static object _signals;

        /// <summary>
        /// Call once, as early in <c>Main</c> as possible, before anything can end the process.
        /// </summary>
        public static void Install()
        {
            if (_installed)
            {
                return;
            }

            _installed = true;

            try
            {
                unsafe
                {
                    on_exit(&OnNativeExit, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("ExitWatch: on_exit is not available: " + ex.Message);
            }

            try
            {
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => Report("ProcessExit", Environment.ExitCode.ToString());
            }
            catch (Exception ex)
            {
                Logger.Warning("ExitWatch: cannot hook ProcessExit: " + ex.Message);
            }

            try
            {
                // Held in a field: a PosixSignalRegistration unregisters itself when collected.
                _signals = new object[]
                {
                    System.Runtime.InteropServices.PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal),
                    System.Runtime.InteropServices.PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal),
                    System.Runtime.InteropServices.PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal),
                    System.Runtime.InteropServices.PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal),
                };
            }
            catch (Exception ex)
            {
                Logger.Warning("ExitWatch: cannot register the signal handlers: " + ex.Message);
            }
        }

        private static void OnSignal(PosixSignalContext context)
        {
            // Cancel is left alone: the runtime still ends the process with 128 + signo, which is
            // what a caller expects to read. All this does is leave a line and save the settings,
            // because the managed shutdown that would have saved them does not run for a signal.
            // The number is not printed on purpose: PosixSignal's members are negative sentinels
            // (SIGTERM is -1), so the name is the only honest thing to write here.
            Report("signal " + context.Signal, "128+N");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnNativeExit(int status, IntPtr state)
        {
            Report("exit()", status.ToString());
        }

        private static void Report(string door, string status)
        {
            // Raw first, and raw is a write(2) on fd 2: no lock to deadlock on, no allocation to
            // fail, nothing that a half torn down process can swallow.
            WriteRaw("[exit-watch] " + door + " status=" + status.ToString() + " thread=" + ThreadName() + "\n");

            try
            {
                Services.LocalSettingsContainer.FlushNow();
            }
            catch
            {
                // Nothing left to do about it here.
            }

            try
            {
                LogFile.Write("[exit-watch] " + door + " status=" + status + " thread=" + ThreadName()
                    + "\n" + Environment.StackTrace);
            }
            catch
            {
                // The raw line above is the record that matters.
            }
        }

        /// <summary>
        /// The kernel's name for the calling thread, which is the one thing that says whether the
        /// exit came from the UI thread, from TDLib's receive loop or from a decoder thread.
        /// </summary>
        private static string ThreadName()
        {
            try
            {
                var comm = System.IO.File.ReadAllText("/proc/thread-self/comm").Trim();
                return comm.Length > 0 ? comm : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static void WriteRaw(string line)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(line);

                unsafe
                {
                    fixed (byte* p = bytes)
                    {
                        _ = write(2, p, (IntPtr)bytes.Length);
                    }
                }
            }
            catch
            {
                // stderr is closed: there is nowhere else to say so.
            }
        }

        /// <summary>
        /// <c>on_exit(3)</c>, a GNU extension: like <c>atexit</c> but the handler receives the
        /// status the process is exiting with, which is the number this whole file exists for.
        /// </summary>
        [DllImport("libc", EntryPoint = "on_exit", SetLastError = true)]
        private static unsafe extern int on_exit(delegate* unmanaged[Cdecl]<int, IntPtr, void> function, IntPtr arg);

        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        private static unsafe extern IntPtr write(int fd, byte* buffer, IntPtr count);
    }
}
