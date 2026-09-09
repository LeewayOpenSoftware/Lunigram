//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices;

namespace Telegram.Common
{
    /// <summary>
    /// The two things "close to tray" needs from the window and Uno does not do: take it off the
    /// screen and put it back.
    ///
    /// <para>Measured on Uno 6.6.184 with the X11 host: <c>AppWindow.Hide()</c> returns without
    /// throwing and the window stays <c>IsViewable</c>, focused and on screen — so the close was
    /// intercepted (the process survived, "Close to tray" in the log) and nothing happened, which
    /// to the user is a close button that does nothing at all. <c>AppWindow.Show()</c> is the same
    /// on the way back.</para>
    ///
    /// <para>So the port does it itself, in the ICCCM way a tray application is supposed to:
    /// <c>XWithdrawWindow</c> (unmap plus the synthetic UnmapNotify the window manager needs to
    /// stop managing it) and <c>XMapRaised</c> to bring it back, followed by the EWMH activation
    /// message so the compositor gives it the focus instead of just drawing it behind everything.
    /// The window is found the same way <see cref="XTestKeyboard"/> finds it — by
    /// <c>WM_CLASS</c>, because Uno publishes no <c>_NET_WM_PID</c> — but this is deliberately not
    /// built on that class: this one ships, that one is a diagnostic.</para>
    /// </summary>
    internal static class X11Window
    {
        private const string LibX11 = "libX11.so.6";

        /// <summary>The <c>WM_CLASS</c> of this app's window, set by the csproj's application id.</summary>
        public const string WindowClass = "Unigram";

        [DllImport(LibX11)]
        private static extern IntPtr XOpenDisplay(string display);

        [DllImport(LibX11)]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport(LibX11)]
        private static extern int XFlush(IntPtr display);

        [DllImport(LibX11)]
        private static extern int XSync(IntPtr display, bool discard);

        [DllImport(LibX11)]
        private static extern ulong XDefaultRootWindow(IntPtr display);

        [DllImport(LibX11)]
        private static extern int XDefaultScreen(IntPtr display);

        [DllImport(LibX11)]
        private static extern int XQueryTree(IntPtr display, ulong window, out ulong root, out ulong parent, out IntPtr children, out uint count);

        [DllImport(LibX11)]
        private static extern int XFree(IntPtr data);

        [DllImport(LibX11)]
        private static extern int XGetClassHint(IntPtr display, ulong window, out XClassHint hint);

        [DllImport(LibX11)]
        private static extern int XGetWindowAttributes(IntPtr display, ulong window, out XWindowAttributes attributes);

        [DllImport(LibX11)]
        private static extern int XWithdrawWindow(IntPtr display, ulong window, int screen);

        [DllImport(LibX11)]
        private static extern int XMapRaised(IntPtr display, ulong window);

        [DllImport(LibX11)]
        private static extern ulong XInternAtom(IntPtr display, string name, bool onlyIfExists);

        [DllImport(LibX11)]
        private static extern int XSendEvent(IntPtr display, ulong window, bool propagate, long eventMask, byte[] send);

        [DllImport(LibX11)]
        private static extern int XGetWindowProperty(IntPtr display, ulong window, ulong property, long offset, long length, bool delete,
            ulong requestedType, out ulong actualType, out int actualFormat, out ulong items, out ulong bytesAfter, out IntPtr data);

        // Returns XErrorHandler -- the PREVIOUS handler, a function pointer, eight bytes. Declared
        // int it came back cut in half, which is harmless only while nobody looks at it; the usual
        // use of this function is to keep that pointer and put it back, and half a pointer is a
        // jump into nowhere on the first X error after the restore.
        [DllImport(LibX11)]
        private static extern IntPtr XSetErrorHandler(IntPtr handler);

        [StructLayout(LayoutKind.Sequential)]
        private struct XClassHint
        {
            public IntPtr Name;
            public IntPtr Class;
        }

        // Only the first fields are read, but the struct has to be the right size: Xlib fills it
        // whole and a short one would be written past its end.
        [StructLayout(LayoutKind.Sequential)]
        private struct XWindowAttributes
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public int BorderWidth;
            public int Depth;
            public IntPtr Visual;
            public ulong Root;
            public int Class;
            public int BitGravity;
            public int WinGravity;
            public int BackingStore;
            public ulong BackingPlanes;
            public ulong BackingPixel;
            public int SaveUnder;
            public ulong Colormap;
            public int MapInstalled;
            public int MapState;
            public long AllEventMasks;
            public long YourEventMask;
            public long DoNotPropagateMask;
            public int OverrideRedirect;
            public IntPtr Screen;
        }

        private const int IsUnmapped = 0;

        private static readonly object _lock = new();
        private static IntPtr _display;
        private static ulong _window;
        private static bool _failed;

        /// <summary>Whether the window could be found, and therefore whether hiding it can work.</summary>
        public static bool IsAvailable
        {
            get
            {
                lock (_lock)
                {
                    return Resolve() != 0;
                }
            }
        }

        /// <summary>Whether the window is on screen right now, as the X server sees it.</summary>
        public static bool IsMapped
        {
            get
            {
                lock (_lock)
                {
                    var window = Resolve();
                    if (window == 0)
                    {
                        return true;
                    }

                    return IsMappedCore(window);
                }
            }
        }

        private static bool IsMappedCore(ulong window)
        {
            if (XGetWindowAttributes(_display, window, out var attributes) == 0)
            {
                // The window went away under us: forget it and answer the safe thing, which is
                // "there is nothing hidden waiting to be brought back".
                _window = 0;
                return true;
            }

            return attributes.MapState != IsUnmapped;
        }

        /// <summary>Whether the window manager says this window is the focused one.</summary>
        private static bool IsActive(ulong window)
        {
            var active = XInternAtom(_display, "_NET_ACTIVE_WINDOW", true);
            if (active == 0)
            {
                return false;
            }

            const ulong AnyPropertyType = 0;

            if (XGetWindowProperty(_display, XDefaultRootWindow(_display), active, 0, 1, false, AnyPropertyType,
                out _, out int format, out ulong items, out _, out IntPtr data) != 0)
            {
                return false;
            }

            try
            {
                if (data == IntPtr.Zero || items < 1 || format != 32)
                {
                    return false;
                }

                // A 32-bit X property is a long in the client's memory, whatever the wire says.
                return (ulong)Marshal.ReadIntPtr(data) == window;
            }
            finally
            {
                if (data != IntPtr.Zero)
                {
                    XFree(data);
                }
            }
        }

        /// <summary>Takes the window off the screen without closing it. False means it is still there.</summary>
        public static bool Hide()
        {
            lock (_lock)
            {
                var window = Resolve();
                if (window == 0)
                {
                    return false;
                }

                XWithdrawWindow(_display, window, XDefaultScreen(_display));
                XSync(_display, false);

                return true;
            }
        }

        /// <summary>Puts it back and asks the compositor for the focus.</summary>
        public static bool Show()
        {
            lock (_lock)
            {
                var window = Resolve();
                if (window == 0)
                {
                    return false;
                }

                if (IsMappedCore(window) && IsActive(window))
                {
                    // Already on screen and already the focused window: there is nothing to raise,
                    // and asking anyway costs a line of noise in the log — Uno's X11 host selects
                    // SubstructureNotify on the root, so it sees the activation message meant for
                    // the window manager and reports it as "unexpected ClientMessage event on
                    // foreign window".
                    return true;
                }

                XMapRaised(_display, window);

                // The map alone leaves a window that is drawn but not focused (and, on a
                // compositor that honours focus stealing prevention, one that is not even raised).
                // This is the message a pager sends, which is what the tray icon is.
                var root = XDefaultRootWindow(_display);
                var active = XInternAtom(_display, "_NET_ACTIVE_WINDOW", false);

                var message = new byte[192];
                BitConverter.TryWriteBytes(message.AsSpan(0), 33);              // ClientMessage
                BitConverter.TryWriteBytes(message.AsSpan(24), (long)_display);
                BitConverter.TryWriteBytes(message.AsSpan(32), window);
                BitConverter.TryWriteBytes(message.AsSpan(40), active);
                BitConverter.TryWriteBytes(message.AsSpan(48), 32);             // format
                BitConverter.TryWriteBytes(message.AsSpan(56), 2L);             // source: pager

                const long SubstructureNotifyMask = 1 << 19;
                const long SubstructureRedirectMask = 1 << 20;

                XSendEvent(_display, root, false, SubstructureNotifyMask | SubstructureRedirectMask, message);
                XSync(_display, false);

                return true;
            }
        }

        private static ulong Resolve()
        {
            if (_failed)
            {
                return 0;
            }

            try
            {
                if (_display == IntPtr.Zero)
                {
                    // A connection of our own, like DisplayScale's: the one Uno uses is not
                    // reachable from managed code, and X11 allows as many as one likes.
                    _display = XOpenDisplay(null);

                    if (_display == IntPtr.Zero)
                    {
                        _failed = true;
                        Logger.Warning("X11Window: cannot open the display, close to tray is off");
                        return 0;
                    }

                    // Xlib's default error handler calls exit(): a BadWindow on a window that
                    // closed while we were looking at it would take Unigram down, and so would one
                    // from Uno's own X11 host (seen once: XQueryTree on a window that had just
                    // been destroyed printed "X Error of failed request: BadWindow" and killed the
                    // process). The handler is process wide, so this one does not swallow anything
                    // — it writes the code to the log and lets the app live.
                    XSetErrorHandler(_ignoreErrors);
                }

                if (_window == 0)
                {
                    _window = FindByClass(XDefaultRootWindow(_display), WindowClass, 0);

                    if (_window == 0)
                    {
                        Logger.Warning($"X11Window: no window of class {WindowClass} found");
                    }
                }

                return _window;
            }
            catch (Exception ex)
            {
                _failed = true;
                Logger.Error("X11Window: libX11 is not usable", ex);
                return 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static int IgnoreErrors(IntPtr display, IntPtr error)
        {
            try
            {
                // XErrorEvent on LP64: int type (0, padded to 8), Display* (8), XID resourceid
                // (16), unsigned long serial (24), then the three code bytes. Read by offset
                // because this is a log line, not a decision.
                var code = Marshal.ReadByte(error, 32);
                var request = Marshal.ReadByte(error, 33);

                Logger.Warning($"X11 error {code} on request {request} (ignored: the default handler exits the process)");
            }
            catch
            {
                // Nothing may be thrown back into Xlib.
            }

            return 0;
        }

        private static readonly unsafe IntPtr _ignoreErrors = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&IgnoreErrors;

        private static ulong FindByClass(ulong window, string name, int depth)
        {
            if (depth > 4)
            {
                return 0;
            }

            if (depth > 0 && Matches(window, name))
            {
                return window;
            }

            if (XQueryTree(_display, window, out _, out _, out var children, out uint count) == 0 || children == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var child = (ulong)Marshal.ReadIntPtr(children, i * IntPtr.Size);
                    var found = FindByClass(child, name, depth + 1);

                    if (found != 0)
                    {
                        return found;
                    }
                }
            }
            finally
            {
                XFree(children);
            }

            return 0;
        }

        private static bool Matches(ulong window, string name)
        {
            if (XGetClassHint(_display, window, out var hint) == 0)
            {
                return false;
            }

            try
            {
                var value = Marshal.PtrToStringAnsi(hint.Class);

                if (!string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // The class is on the top level window and on nothing else, but a menu or a
                // tooltip of the same app would carry it too: only a window of real size is the
                // one the user closes.
                if (XGetWindowAttributes(_display, window, out var attributes) == 0)
                {
                    return false;
                }

                return attributes.Width > 100 && attributes.Height > 100;
            }
            finally
            {
                if (hint.Name != IntPtr.Zero)
                {
                    XFree(hint.Name);
                }

                if (hint.Class != IntPtr.Zero)
                {
                    XFree(hint.Class);
                }
            }
        }
    }
}
