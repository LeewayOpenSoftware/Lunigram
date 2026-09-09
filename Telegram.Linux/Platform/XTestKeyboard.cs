//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: synthesises real key presses through the X11 XTEST extension,
    /// so a test can drive a focused control the same way a user's keyboard does - through Uno's
    /// X11 event loop and the whole TextBox input path - instead of poking <c>Text</c> from code,
    /// which skips exactly the part under test.
    ///
    /// There is no xdotool on this machine; this is the same call it would have made
    /// (<c>XTestFakeKeyEvent</c>), minus the process. The events are injected over a *second*
    /// connection to the display, so they travel server -> compositor -> app like any other key.
    /// Under XWayland the compositor re-injects XTEST events as if they came from a real device,
    /// which is why this works on a Wayland session at all (unlike screen grabs, see Screenshot).
    ///
    /// Only for diagnostics (UNIGRAM_TYPE_TEST); nothing in the app calls it.
    /// </summary>
    public static class XTestKeyboard
    {
        private const string LibX11 = "libX11.so.6";
        private const string LibXtst = "libXtst.so.6";

        private const ulong XK_Shift_L = 0xffe1;

        [DllImport(LibX11)]
        private static extern IntPtr XOpenDisplay(string display);

        [DllImport(LibX11)]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport(LibX11)]
        private static extern int XFlush(IntPtr display);

        [DllImport(LibX11)]
        private static extern int XSync(IntPtr display, bool discard);

        [DllImport(LibX11)]
        private static extern ulong XStringToKeysym(string name);

        [DllImport(LibX11)]
        private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

        [DllImport(LibX11)]
        private static extern ulong XkbKeycodeToKeysym(IntPtr display, byte keycode, int group, int level);

        [DllImport(LibX11)]
        private static extern int XDisplayKeycodes(IntPtr display, out int min, out int max);

        [DllImport(LibX11)]
        private static extern IntPtr XGetKeyboardMapping(IntPtr display, byte first, int count, out int keysymsPerKeycode);

        [DllImport(LibX11)]
        private static extern int XChangeKeyboardMapping(IntPtr display, int first, int keysymsPerKeycode, ulong[] keysyms, int count);

        [DllImport(LibX11)]
        private static extern int XFree(IntPtr data);

        [DllImport(LibX11)]
        private static extern int XGetInputFocus(IntPtr display, out ulong focus, out int revertTo);

        [DllImport(LibX11)]
        private static extern int XSetInputFocus(IntPtr display, ulong focus, int revertTo, ulong time);

        [DllImport(LibX11)]
        private static extern int XFetchName(IntPtr display, ulong window, out IntPtr name);

        [DllImport(LibX11)]
        private static extern int XQueryTree(IntPtr display, ulong window, out ulong root, out ulong parent, out IntPtr children, out uint count);

        [DllImport(LibX11)]
        private static extern ulong XDefaultRootWindow(IntPtr display);

        [DllImport(LibX11)]
        private static extern ulong XInternAtom(IntPtr display, string name, bool onlyIfExists);

        [DllImport(LibX11)]
        private static extern int XGetWindowProperty(IntPtr display, ulong window, ulong property, long offset, long length, bool delete, ulong requestedType,
            out ulong actualType, out int actualFormat, out ulong items, out ulong bytesAfter, out IntPtr property2);

        [DllImport(LibX11)]
        private static extern int XSendEvent(IntPtr display, ulong window, bool propagate, long eventMask, byte[] send);

        [DllImport(LibXtst)]
        private static extern bool XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int majorVersion, out int minorVersion);

        [DllImport(LibXtst)]
        private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool isPress, ulong delay);

        private static IntPtr _display;
        private static bool _probed;
        private static string _error;

        /// <summary>
        /// A keycode nothing in the layout uses, borrowed to carry a keysym the layout has no key
        /// for (a '+' on a layout where it is not reachable, say). Restored to unused on release.
        /// </summary>
        private static byte _spare;
        private static int _keysymsPerKeycode;
        private static ulong _borrowed;
        private static ulong _own;

        /// <summary>
        /// Why it is unavailable, or which XTEST it found when it is.
        /// </summary>
        public static string Diagnostics => _error;

        /// <summary>
        /// Deliver the keys straight to the focused window with XSendEvent instead of driving the
        /// keyboard with XTEST. See <see cref="Send"/> for what that trades away, and why this
        /// session needs it.
        /// </summary>
        public static bool UseSendEvent { get; set; }

        /// <summary>
        /// The title of this process's own window. Uno sets no <c>_NET_WM_PID</c>, so this is the
        /// only way to recognise it among the desktop's windows - and recognising it is what keeps
        /// <see cref="UseSendEvent"/> from typing into somebody else's window when the focus is
        /// not where it was expected.
        /// </summary>
        public static string WindowTitle { get; set; }

        /// <summary>
        /// The WM_CLASS of this process's own window, which is how it is actually recognised here:
        /// Uno leaves WM_NAME empty on this session even after AppWindow.Title is assigned, so
        /// <see cref="WindowTitle"/> matches nothing and this is what is left.
        /// </summary>
        public static string WindowClass { get; set; } = "Unigram";

        public static bool IsAvailable
        {
            get
            {
                Probe();
                return _display != IntPtr.Zero;
            }
        }

        private static void Probe()
        {
            if (_probed)
            {
                return;
            }

            _probed = true;

            try
            {
                var name = Environment.GetEnvironmentVariable("DISPLAY");
                if (string.IsNullOrEmpty(name))
                {
                    _error = "DISPLAY is not set";
                    return;
                }

                var display = XOpenDisplay(name);
                if (display == IntPtr.Zero)
                {
                    _error = $"XOpenDisplay({name}) failed";
                    return;
                }

                if (!XTestQueryExtension(display, out _, out _, out int major, out int minor))
                {
                    XCloseDisplay(display);
                    _error = "the X server has no XTEST extension";
                    return;
                }

                _display = display;
                _error = $"XTEST {major}.{minor} on {name}";

                FindSpareKeycode();
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
        }

        private static void FindSpareKeycode()
        {
            XDisplayKeycodes(_display, out int min, out int max);

            var count = max - min + 1;
            var mapping = XGetKeyboardMapping(_display, (byte)min, count, out _keysymsPerKeycode);

            if (mapping == IntPtr.Zero || _keysymsPerKeycode <= 0)
            {
                return;
            }

            try
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    var used = false;

                    for (int j = 0; j < _keysymsPerKeycode; j++)
                    {
                        if (Marshal.ReadIntPtr(mapping, (i * _keysymsPerKeycode + j) * IntPtr.Size) != IntPtr.Zero)
                        {
                            used = true;
                            break;
                        }
                    }

                    if (!used)
                    {
                        _spare = (byte)(min + i);
                        return;
                    }
                }
            }
            finally
            {
                XFree(mapping);
            }
        }

        #region Pointer

        // Core X pointer events, delivered to the window rather than driven through the server.
        //
        // XTEST cannot aim a pointer on this session at all: mutter keeps a guard window over the
        // whole X screen, so a faked motion is reported over the root window and never reaches the
        // Uno surface (xdotool getmouselocation answers WINDOW=0x408 wherever the pointer is put).
        // XSendEvent has no such problem, because it names the destination window: the event is
        // put straight on this process's own queue.
        //
        // It is still the real input path on the app's side. Uno's X11 host dispatches
        // MotionNotify / ButtonPress / ButtonRelease from its event loop into
        // X11PointerInputSource, which raises PointerMoved / PointerPressed / PointerWheelChanged
        // exactly as it does for a physical mouse; nothing there looks at send_event. The one
        // thing that must be right is XButtonEvent.window: the source marks the args Handled when
        // it is not the top-level window, and a Handled pointer event goes nowhere.
        //
        // Buttons are the X numbering: 1 left, 2 middle, 3 right, 4/5 wheel up/down, 6/7 wheel
        // left/right. Coordinates are physical pixels relative to the window's top left corner,
        // which is what the X server would put in the event; Uno divides them by the
        // rasterization scale to get layout coordinates.
        private const int MotionNotify = 6;
        private const int ButtonPress = 4;
        private const int ButtonRelease = 5;

        // The event mask of XSendEvent is not "what kind of event this is", it is "which clients
        // should get it": with a mask, the server delivers to the clients that selected one of
        // those event types, and Uno selects the core pointer events only when it cannot use XI2
        // (X11XamlRootHost calls XSelectInput with 0x20807C & ~0x74, which is exactly ButtonPress,
        // EnterWindow, LeaveWindow and PointerMotion taken out). Sent with ButtonPressMask, the
        // click was addressed to nobody and the server dropped it in silence. An empty mask means
        // "the client that created the destination window", which is the one we are aiming at.
        private const long OwnerMask = 0;

        /// <summary>
        /// Moves the pointer to a point of the window, so whatever is under it takes the hover
        /// state and the click that follows is hit-tested where it should be.
        /// </summary>
        public static bool TryMove(int x, int y)
        {
            return SendPointer(MotionNotify, OwnerMask, x, y, 0, 0);
        }

        /// <summary>
        /// A full press and release of <paramref name="button"/> at that point of the window.
        /// </summary>
        public static bool TryClick(int x, int y, int button = 1)
        {
            // The state mask of the release carries the button that is still down, the way the
            // server reports it; Uno keeps its own bookkeeping, but a faithful event costs nothing.
            return TryMove(x, y)
                && SendPointer(ButtonPress, OwnerMask, x, y, button, 0)
                && SendPointer(ButtonRelease, OwnerMask, x, y, button, (uint)(1 << (7 + button)));
        }

        /// <summary>
        /// Press the button and leave it down. Pairs with <see cref="TryRelease"/>.
        ///
        /// <para>Exists for one measurement that has no other way in: the story viewer pauses while
        /// the finger is down and resumes when it lifts, and a full press+release in one call is a
        /// tap, which is the opposite gesture (it advances the story).</para>
        /// </summary>
        public static bool TryPress(int x, int y, int button = 1)
        {
            return TryMove(x, y)
                && SendPointer(ButtonPress, OwnerMask, x, y, button, 0);
        }

        /// <summary>
        /// Release a button held down by <see cref="TryPress"/>.
        /// </summary>
        public static bool TryRelease(int x, int y, int button = 1)
        {
            return SendPointer(ButtonRelease, OwnerMask, x, y, button, (uint)(1 << (7 + button)));
        }

        /// <summary>
        /// Turns the wheel <paramref name="notches"/> steps at that point of the window: positive
        /// scrolls down (button 5), negative scrolls up (button 4). Each notch is a press and a
        /// release of the wheel button, which is how X reports a wheel, and each one reaches Uno
        /// as a PointerWheelChanged of ±120.
        /// </summary>
        public static bool TryWheel(int x, int y, int notches)
        {
            var button = notches < 0 ? 4 : 5;

            if (!TryMove(x, y))
            {
                return false;
            }

            for (int i = 0; i < Math.Abs(notches); i++)
            {
                if (!SendPointer(ButtonPress, OwnerMask, x, y, button, 0)
                    || !SendPointer(ButtonRelease, OwnerMask, x, y, button, 0))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SendPointer(int type, long mask, int x, int y, int button, uint state)
        {
            Probe();

            if (_display == IntPtr.Zero)
            {
                return false;
            }

            var window = FindOwnWindow();
            if (window == 0)
            {
                _error = "no window of this process found under the root window";
                return false;
            }

            // XButtonEvent and XMotionEvent share the layout of XKeyEvent up to the last field
            // this needs, so the offsets are the ones Send() already uses; button sits where the
            // keycode does.
            var message = new byte[192];

            BitConverter.TryWriteBytes(message.AsSpan(0), type);
            BitConverter.TryWriteBytes(message.AsSpan(24), (long)_display);
            BitConverter.TryWriteBytes(message.AsSpan(32), window);
            BitConverter.TryWriteBytes(message.AsSpan(40), XDefaultRootWindow(_display));
            BitConverter.TryWriteBytes(message.AsSpan(56), (ulong)Environment.TickCount64);
            BitConverter.TryWriteBytes(message.AsSpan(64), x);
            BitConverter.TryWriteBytes(message.AsSpan(68), y);
            BitConverter.TryWriteBytes(message.AsSpan(72), x);              // x_root
            BitConverter.TryWriteBytes(message.AsSpan(76), y);              // y_root
            BitConverter.TryWriteBytes(message.AsSpan(80), state);
            BitConverter.TryWriteBytes(message.AsSpan(84), (uint)button);
            BitConverter.TryWriteBytes(message.AsSpan(88), 1);              // same_screen

            XSendEvent(_display, window, false, mask, message);
            XFlush(_display);
            XSync(_display, false);

            return true;
        }

        #endregion

        /// <summary>
        /// Presses and releases the key that produces <paramref name="character"/>, holding Shift
        /// when the layout puts it on the shifted level.
        /// </summary>
        public static bool TryType(char character)
        {
            // Latin-1 characters are their own keysym, which covers everything a phone number or a
            // verification code is made of.
            return TryKeysym(character);
        }

        /// <summary>
        /// Presses and releases a named key: "BackSpace", "Left", "Right", "Home", "End", "Delete".
        ///
        /// <para><paramref name="forceShift"/> holds Shift around the key even when the layout does
        /// not ask for it. It exists for exactly one measurement that has no other way in from
        /// outside: the composer sends on Enter and makes a line on Shift+Enter, and those two are
        /// the SAME keysym - the only thing that separates them is the modifier. Without this,
        /// "Shift+Enter makes a newline" cannot be tested at all.</para>
        /// </summary>
        public static bool TryKey(string keysymName, bool forceShift = false)
        {
            Probe();

            if (_display == IntPtr.Zero)
            {
                return false;
            }

            var keysym = XStringToKeysym(keysymName);
            return keysym != 0 && TryKeysym(keysym, forceShift);
        }

        private static bool TryKeysym(ulong keysym, bool forceShift = false)
        {
            Probe();

            if (_display == IntPtr.Zero)
            {
                return false;
            }

            var keycode = XKeysymToKeycode(_display, keysym);
            var shift = forceShift;

            if (keycode == 0)
            {
                if (_spare == 0)
                {
                    _error = $"no key produces keysym 0x{keysym:x} and no spare keycode to lend it one";
                    return false;
                }

                // The loan is NOT undone right after the key: the app has not read the event yet,
                // and it would translate the keycode through a mapping that no longer says
                // anything. It is undone by ReleaseSpare, or when a different keysym needs it.
                if (_borrowed != keysym)
                {
                    Borrow(keysym);
                    _borrowed = keysym;
                }

                keycode = _spare;
            }
            else
            {
                // Level 0 is the unshifted symbol; if the wanted one is not there but sits on level
                // 1, the key needs Shift held down. Groups beyond the first are not worth chasing:
                // nothing this types lives on a third level.
                if (XkbKeycodeToKeysym(_display, keycode, 0, 0) != keysym)
                {
                    if (XkbKeycodeToKeysym(_display, keycode, 0, 1) == keysym)
                    {
                        shift = true;
                    }
                }
            }

            if (UseSendEvent)
            {
                var window = FindOwnWindow();

                if (window == 0)
                {
                    _error = "this process's window could not be found to send the key to";
                    return false;
                }

                Send(window, keycode, 2 /* KeyPress */, shift);
                Send(window, keycode, 3 /* KeyRelease */, shift);
            }
            else
            {
                var shiftCode = XKeysymToKeycode(_display, XK_Shift_L);

                if (shift && shiftCode != 0)
                {
                    XTestFakeKeyEvent(_display, shiftCode, true, 0);
                }

                XTestFakeKeyEvent(_display, keycode, true, 0);
                XTestFakeKeyEvent(_display, keycode, false, 0);

                if (shift && shiftCode != 0)
                {
                    XTestFakeKeyEvent(_display, shiftCode, false, 0);
                }
            }

            XFlush(_display);
            XSync(_display, false);

            return true;
        }

        /// <summary>
        /// Delivers one key event straight to a window, instead of driving the keyboard and hoping
        /// the desktop routes it there. XTEST is the faithful path - the event walks in through
        /// the compositor exactly as a real key would - but on a Wayland session that routing is
        /// the compositor's business and it does not always hand the keys to the X client that
        /// holds the focus. This path cannot be routed away from its target, at the cost of the
        /// event carrying <c>send_event = True</c>; Uno's X11 host does not look at that flag.
        /// </summary>
        private static void Send(ulong window, byte keycode, int type, bool shift)
        {
            const long KeyPressMask = 1 << 0;
            const long KeyReleaseMask = 1 << 1;
            const uint ShiftMask = 1 << 0;

            var message = new byte[192];

            BitConverter.TryWriteBytes(message.AsSpan(0), type);
            BitConverter.TryWriteBytes(message.AsSpan(24), (long)_display);
            BitConverter.TryWriteBytes(message.AsSpan(32), window);
            BitConverter.TryWriteBytes(message.AsSpan(40), XDefaultRootWindow(_display));
            BitConverter.TryWriteBytes(message.AsSpan(56), (ulong)Environment.TickCount64);
            BitConverter.TryWriteBytes(message.AsSpan(80), shift ? ShiftMask : 0u);
            BitConverter.TryWriteBytes(message.AsSpan(84), (uint)keycode);
            BitConverter.TryWriteBytes(message.AsSpan(88), 1);              // same_screen

            XSendEvent(_display, window, false, type == 2 ? KeyPressMask : KeyReleaseMask, message);
        }

        private static void Borrow(ulong keysym)
        {
            var keysyms = new ulong[_keysymsPerKeycode];

            for (int i = 0; i < keysyms.Length; i++)
            {
                keysyms[i] = keysym;
            }

            XChangeKeyboardMapping(_display, _spare, _keysymsPerKeycode, keysyms, 1);
            XSync(_display, false);

            // The mapping change reaches the app as a MappingNotify it has to digest before the
            // key that follows means anything; without a beat here the first borrowed key is lost.
            System.Threading.Thread.Sleep(60);
        }

        /// <summary>
        /// Which window the X server will deliver the injected keys to, by name. XTEST does not
        /// aim: it drives the keyboard, and the keyboard goes wherever the focus is - so when the
        /// keys do not arrive, this is the first thing to look at.
        /// </summary>
        public static string DescribeFocus()
        {
            Probe();

            if (_display == IntPtr.Zero)
            {
                return "no display";
            }

            XGetInputFocus(_display, out ulong window, out int revert);

            var name = NameOf(window);
            var walked = window;

            // The focused window is often a WM frame or an input-only child with no name of its
            // own; the title lives further up.
            for (int i = 0; i < 4 && name == null && walked != 0; i++)
            {
                if (XQueryTree(_display, walked, out _, out ulong parent, out IntPtr children, out _) == 0)
                {
                    break;
                }

                if (children != IntPtr.Zero)
                {
                    XFree(children);
                }

                walked = parent;
                name = NameOf(walked);
            }

            return $"0x{window:x} \"{name ?? "?"}\" (revert {revert})";
        }

        /// <summary>
        /// What the window manager calls the active window, and which windows this process owns.
        /// Under a Wayland compositor the X focus above and the compositor's idea of the active
        /// window can disagree, and it is the compositor's that decides where XTEST keys land.
        /// </summary>
        public static string DescribeActive()
        {
            Probe();

            if (_display == IntPtr.Zero)
            {
                return "no display";
            }

            var root = XDefaultRootWindow(_display);
            var active = XInternAtom(_display, "_NET_ACTIVE_WINDOW", true);
            var text = "the window manager publishes no _NET_ACTIVE_WINDOW";

            if (active != 0
                && XGetWindowProperty(_display, root, active, 0, 1, false, 0, out _, out int format, out ulong items, out _, out IntPtr value) == 0
                && value != IntPtr.Zero)
            {
                var window = format == 32 && items >= 1 ? (ulong)(uint)Marshal.ReadInt32(value) : 0;
                XFree(value);

                text = $"active 0x{window:x} \"{NameOf(window) ?? "?"}\"";
            }

            var pidAtom = XInternAtom(_display, "_NET_WM_PID", true);
            var mine = new List<string>();

            if (pidAtom != 0)
            {
                Collect(root, pidAtom, (ulong)Environment.ProcessId, 0, mine);
            }

            var own = FindOwnWindow();

            return $"{text}; by _NET_WM_PID this process owns {(mine.Count == 0 ? "no window" : string.Join(", ", mine))}"
                + $"; keys will be sent to {(own == 0 ? "NOTHING - own window not identified" : $"0x{own:x} \"{NameOf(own) ?? "?"}\"")}";
        }

        private static void Collect(ulong window, ulong pidAtom, ulong pid, int depth, List<string> into)
        {
            if (depth > 4)
            {
                return;
            }

            if (depth > 0 && PidOf(window, pidAtom) == pid)
            {
                into.Add($"0x{window:x} \"{NameOf(window) ?? "?"}\"");
            }

            if (XQueryTree(_display, window, out _, out _, out IntPtr children, out uint count) == 0)
            {
                return;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    Collect((ulong)Marshal.ReadInt64(children, i * 8), pidAtom, pid, depth + 1, into);
                }
            }
            finally
            {
                if (children != IntPtr.Zero)
                {
                    XFree(children);
                }
            }
        }

        private static string NameOf(ulong window)
        {
            if (window == 0 || XFetchName(_display, window, out IntPtr text) == 0 || text == IntPtr.Zero)
            {
                return null;
            }

            var name = Marshal.PtrToStringAnsi(text);
            XFree(text);

            return name;
        }

        /// <summary>
        /// Asks the window manager to focus this process's own window, so the injected keys land
        /// in it rather than in whatever the user (or the terminal that launched the app) left
        /// focused. Returns false when no window of this process could be found.
        /// </summary>
        public static bool TryActivateOwnWindow()
        {
            Probe();

            if (_display == IntPtr.Zero)
            {
                return false;
            }

            var root = XDefaultRootWindow(_display);
            var window = FindOwnWindow();

            if (window == 0)
            {
                _error = "no window of this process found under the root window";
                return false;
            }

            // Two ways of asking, because neither is honoured everywhere: the EWMH message is what
            // a pager would send (and what a Wayland compositor's XWayland side listens to), and
            // XSetInputFocus is the blunt X11 one for a bare WM.
            var active = XInternAtom(_display, "_NET_ACTIVE_WINDOW", false);
            var message = new byte[192];

            BitConverter.TryWriteBytes(message.AsSpan(0), 33);              // ClientMessage
            BitConverter.TryWriteBytes(message.AsSpan(24), (long)_display);
            BitConverter.TryWriteBytes(message.AsSpan(32), window);
            BitConverter.TryWriteBytes(message.AsSpan(40), active);
            BitConverter.TryWriteBytes(message.AsSpan(48), 32);             // format
            BitConverter.TryWriteBytes(message.AsSpan(56), 2L);             // source indication: pager

            const long SubstructureNotifyMask = 1 << 19;
            const long SubstructureRedirectMask = 1 << 20;

            XSendEvent(_display, root, false, SubstructureNotifyMask | SubstructureRedirectMask, message);
            XSetInputFocus(_display, window, 2 /* RevertToParent */, 0 /* CurrentTime */);
            XSync(_display, false);

            return true;
        }

        /// <summary>
        /// This process's own top-level window: by <c>_NET_WM_PID</c> when there is one, otherwise
        /// by <see cref="WindowTitle"/>, because Uno's X11 host publishes no PID.
        /// </summary>
        private static ulong FindOwnWindow()
        {
            if (_own != 0)
            {
                return _own;
            }

            var root = XDefaultRootWindow(_display);
            var pidAtom = XInternAtom(_display, "_NET_WM_PID", true);

            if (pidAtom != 0)
            {
                _own = FindByPid(root, pidAtom, (ulong)Environment.ProcessId, 0);
            }

            if (_own == 0 && !string.IsNullOrEmpty(WindowTitle))
            {
                // The window that holds the X input focus, when it is one of ours. Checked before
                // the tree walk because it is the window the server is already routing keys to,
                // and the walk can land on a frame the client never selected key events on.
                XGetInputFocus(_display, out ulong focus, out _);

                // Only when the focused window is OUR client window, which WM_CLASS is the one
                // thing that tells. Mutter reparents an X11 client into a frame drawn by a
                // SEPARATE process (WM_CLASS "mutter-x11-frames") and hands the input focus to
                // that frame, and TitleOf walks up to an ancestor when a window has no WM_NAME of
                // its own, so the title test alone accepted the frame: every XSendEvent then went
                // to somebody else's window, TryClick still answered "sent: True", and nothing
                // ever arrived -- not even the calibration motion ("no PointerMoved arrived").
                if (focus != 0 && IsOurs(focus) && string.Equals(TitleOf(focus), WindowTitle, StringComparison.Ordinal))
                {
                    _own = focus;
                }
            }

            if (_own == 0 && !string.IsNullOrEmpty(WindowClass))
            {
                // Last and most reliable: WM_CLASS. Uno leaves WM_NAME empty on this session even
                // though AppWindow.Title was assigned - `xprop` on the window reads
                // `WM_NAME(STRING) =` with nothing after it, while WM_CLASS carries the app name
                // that Resizetizer put in the manifest - so the name lookup above finds nothing and
                // only this one answers.
                _own = FindByClass(root, WindowClass, 0);
            }

            if (_own == 0 && !string.IsNullOrEmpty(WindowTitle))
            {
                // Only if WM_CLASS answered nothing at all: WM_NAME is carried by the frame too,
                // so this can still land on one, and it is here rather than above for that reason.
                _own = FindByName(root, WindowTitle, 0);
            }

            return _own;
        }

        /// <summary>
        /// Whether that window is this app's own client window, by WM_CLASS. The frame mutter
        /// wraps it in carries our WM_NAME but not our WM_CLASS, which is what makes this the
        /// test that separates the two.
        /// </summary>
        private static bool IsOurs(ulong window)
        {
            if (string.IsNullOrEmpty(WindowClass))
            {
                return true;
            }

            return ClassOf(window) is string value
                && (string.Equals(value, WindowClass, StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith(WindowClass + "\0", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Which window the injected events are being delivered to, for the log.
        /// </summary>
        public static string DescribeOwnWindow()
        {
            Probe();

            if (_display == IntPtr.Zero)
            {
                return _error;
            }

            var window = FindOwnWindow();

            return window == 0
                ? $"no window of this process found (title \"{WindowTitle}\", class \"{WindowClass}\")"
                : $"0x{window:x}, WM_NAME \"{NameOf(window)}\", WM_CLASS \"{ClassOf(window)}\"";
        }

        /// <summary>
        /// The first window whose WM_CLASS matches, at any depth: the window manager reparents the
        /// client window into a frame of its own, so ours is never a direct child of the root.
        /// </summary>
        private static ulong FindByClass(ulong window, string name, int depth)
        {
            if (depth > 4)
            {
                return 0;
            }

            if (depth > 0 && ClassOf(window) is string value
                && (string.Equals(value, name, StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith(name + "\0", StringComparison.OrdinalIgnoreCase)))
            {
                return window;
            }

            if (XQueryTree(_display, window, out _, out _, out IntPtr children, out uint count) == 0)
            {
                return 0;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var found = FindByClass((ulong)Marshal.ReadInt64(children, i * 8), name, depth + 1);

                    if (found != 0)
                    {
                        return found;
                    }
                }
            }
            finally
            {
                if (children != IntPtr.Zero)
                {
                    XFree(children);
                }
            }

            return 0;
        }

        private static string ClassOf(ulong window)
        {
            const ulong XA_STRING = 31;

            var atom = XInternAtom(_display, "WM_CLASS", true);

            if (atom == 0 || window == 0)
            {
                return null;
            }

            if (XGetWindowProperty(_display, window, atom, 0, 64, false, XA_STRING,
                out _, out int format, out ulong items, out _, out IntPtr value) != 0 || value == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // WM_CLASS is two null separated strings, instance then class; the first one is
                // enough to tell our window from everybody else's.
                return format == 8 && items > 0 ? Marshal.PtrToStringAnsi(value) : null;
            }
            finally
            {
                XFree(value);
            }
        }

        /// <summary>
        /// The window's own WM_NAME, or the nearest ancestor's: the focused window is often a frame
        /// or an input-only child that carries no title of its own.
        /// </summary>
        private static string TitleOf(ulong window)
        {
            var walked = window;

            for (int i = 0; i < 4 && walked != 0; i++)
            {
                var name = NameOf(walked);

                if (name != null)
                {
                    return name;
                }

                if (XQueryTree(_display, walked, out _, out ulong parent, out IntPtr children, out _) == 0)
                {
                    return null;
                }

                if (children != IntPtr.Zero)
                {
                    XFree(children);
                }

                walked = parent;
            }

            return null;
        }

        private static ulong FindByName(ulong window, string title, int depth)
        {
            if (depth > 4)
            {
                return 0;
            }

            if (depth > 0 && string.Equals(NameOf(window), title, StringComparison.Ordinal))
            {
                return window;
            }

            if (XQueryTree(_display, window, out _, out _, out IntPtr children, out uint count) == 0)
            {
                return 0;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var found = FindByName((ulong)Marshal.ReadInt64(children, i * 8), title, depth + 1);

                    if (found != 0)
                    {
                        return found;
                    }
                }
            }
            finally
            {
                if (children != IntPtr.Zero)
                {
                    XFree(children);
                }
            }

            return 0;
        }

        private static ulong FindByPid(ulong window, ulong pidAtom, ulong pid, int depth)
        {
            if (depth > 4)
            {
                return 0;
            }

            if (depth > 0 && PidOf(window, pidAtom) == pid)
            {
                return window;
            }

            if (XQueryTree(_display, window, out _, out _, out IntPtr children, out uint count) == 0)
            {
                return 0;
            }

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var child = (ulong)Marshal.ReadInt64(children, i * 8);
                    var found = FindByPid(child, pidAtom, pid, depth + 1);

                    if (found != 0)
                    {
                        return found;
                    }
                }
            }
            finally
            {
                if (children != IntPtr.Zero)
                {
                    XFree(children);
                }
            }

            return 0;
        }

        private static ulong PidOf(ulong window, ulong pidAtom)
        {
            const ulong AnyPropertyType = 0;

            if (XGetWindowProperty(_display, window, pidAtom, 0, 1, false, AnyPropertyType,
                    out _, out int format, out ulong items, out _, out IntPtr value) != 0)
            {
                return 0;
            }

            if (value == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                return format == 32 && items >= 1 ? (ulong)(uint)Marshal.ReadInt32(value) : 0;
            }
            finally
            {
                XFree(value);
            }
        }

        /// <summary>
        /// Hands the borrowed keycode back to the layout. Call it when the typing is over: leaving
        /// a keycode mapped to something the user's layout never had would outlive the test.
        /// </summary>
        public static void ReleaseSpare()
        {
            if (_display == IntPtr.Zero || _borrowed == 0)
            {
                return;
            }

            var keysyms = new ulong[_keysymsPerKeycode];

            XChangeKeyboardMapping(_display, _spare, _keysymsPerKeycode, keysyms, 1);
            XSync(_display, false);

            _borrowed = 0;
        }
    }
}
