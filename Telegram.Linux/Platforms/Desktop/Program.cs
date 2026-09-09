using System;
using Microsoft.Extensions.Logging;
using Uno.UI.Hosting;

namespace Telegram
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Every P/Invoke of the Linux head goes through one resolver (the runtime only accepts
            // one per assembly): "tdjson.dll" -> libtdjson.so, "unigram-native" ->
            // libunigram-native.so. See Native/NativeLibraryResolver.cs to add a library.
            Native.NativeLibraryResolver.Register(typeof(Program).Assembly);

            InitializeUnoLogging();

            // Before anything can end the process: names the door the process leaves by (an
            // exit() from anywhere, managed shutdown, a signal) and saves the settings on the two
            // that would not. See Telegram.Linux/Platform/ExitWatch.cs.
            Common.ExitWatch.Install();

            // One Unigram per session, and the way the desktop reaches the one that is running.
            // Before Uno builds anything, for two reasons: a second launch (a tg:// link, a second
            // click on the dock) has to cost a bus round trip and not a XAML host, and the D-Bus
            // name has to be owned before the bus delivers the Open() that started this very
            // process. False means the work went to the instance that was already up.
            // See Telegram.Linux/Platform/DBus/SingleInstance.cs.
            if (!Services.SingleInstance.Startup(args))
            {
                return;
            }

            // Before anything can measure text: Uno's FontDetailsCache caches the fallback service
            // in a static readonly field the first time it resolves a font.
            Common.LinuxFontFallback.Register();

            // Started here and answered while Uno boots, so the theme is already known when the
            // first window is created: the X11 host only learns the desktop colour scheme from
            // xdg-desktop-portal seconds later, and until then it reports Light.
            Common.DesktopColorScheme.BeginRead();

            var host = UnoPlatformHostBuilder.Create()
                .App(() => new App())
                .UseX11()
                .Build();

            host.Run();

            // The last word on the way out, and it has to be the last: TDLib's receive loop is a
            // background thread parked inside td_receive() with a 300 s timeout, and nothing wakes
            // it up when the app quits. Measured with UNIGRAM_SCREENSHOT=<s>:<png>:exit, which
            // takes the same Application.Current.Exit() the tray's Quit does: the process ended
            // 139 - SIGSEGV, not the SIGABRT the native audit fixed - and the kernel named it,
            //
            //     TdReceive[2547031]: segfault at bc ip ... in libtdjson.so[3c6c23,...]
            //
            // which is that thread still walking TDLib globals that exit()'s static destructors
            // have already torn down. Subscribing HERE, after host.Run() has returned, makes this
            // the last ProcessExit handler of the process: returning from Main runs the earlier
            // ones first - the settings flush in LocalSettings, unigram_native_shutdown - and then
            // this one ends the process before a single C++ destructor gets to run. _exit, never
            // exit: exit() is exactly what runs them.
            AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
            {
                try
                {
                    Console.Out.Flush();
                    Console.Error.Flush();
                }
                catch
                {
                    // A closed stdout must not become the failure of the exit path.
                }

                ExitImmediately(0);
            };
        }

        /// <summary>
        /// <c>_exit(2)</c>: ends the process without running atexit handlers or C++ static
        /// destructors, which is the whole point - see the note at the end of <see cref="Main"/>.
        /// </summary>
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "_exit")]
        private static extern void ExitImmediately(int code);

        /// <summary>
        /// Uno reports what it swallows (page construction failures, dispatcher exceptions, XAML
        /// resource lookups...) through its own logger, which is silent unless a factory is set.
        /// UNIGRAM_UNO_LOG=Debug|Information|Warning (default) |Error|None picks the level.
        /// </summary>
        private static void InitializeUnoLogging()
        {
            var level = Microsoft.Extensions.Logging.LogLevel.Warning;
            if (Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(Environment.GetEnvironmentVariable("UNIGRAM_UNO_LOG"), true, out var parsed))
            {
                level = parsed;
            }

            // UNIGRAM_UNO_LOG_ONLY=<substring>[,<substring>] narrows that level down to the
            // categories whose name contains one of those substrings; everything else stays at
            // Warning. Uno's own Debug logging is the only way to see inside a measure pass of
            // VirtualizingPanelLayout (seed index, seed start, ExtendedViewportStart/End,
            // GetItemsStart/End, every AddView, the size EstimatePanelSize returns) without
            // patching Uno, and unfiltered it buries the log under every other control in the
            // window. UNIGRAM_UNO_LOG=Debug UNIGRAM_UNO_LOG_ONLY=VirtualizingPanelLayout is the
            // combination that debugged the empty history - and it is VirtualizingPanelLayout,
            // not ItemsStackPanelLayout: Uno's this.Log() names the type that DECLARES the method,
            // not the one that was instantiated, so filtering on the concrete panel prints nothing
            // at all and costs a run.
            var only = Environment.GetEnvironmentVariable("UNIGRAM_UNO_LOG_ONLY");
            var onlyParts = string.IsNullOrEmpty(only)
                ? null
                : only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            {
                Microsoft.Extensions.Logging.ConsoleLoggerExtensions.AddConsole(builder);

                if (onlyParts is { Length: > 0 })
                {
                    builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                    Microsoft.Extensions.Logging.FilterLoggingBuilderExtensions.AddFilter(builder, (category, lvl) =>
                    {
                        if (category != null)
                        {
                            foreach (var part in onlyParts)
                            {
                                if (category.Contains(part, StringComparison.OrdinalIgnoreCase))
                                {
                                    return lvl >= level;
                                }
                            }
                        }

                        return lvl >= Microsoft.Extensions.Logging.LogLevel.Warning;
                    });
                }
                else
                {
                    builder.SetMinimumLevel(level);
                }
            });

            global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;
            global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
        }
    }
}
