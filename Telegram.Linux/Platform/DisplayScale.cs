//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Telegram.Common
{
    /// <summary>
    /// The scale the X11 host will report for the window, read before there is a window to ask.
    /// It comes from Xft.dpi in the X resource database, which is what desktops set when the user
    /// picks a display scale and what Uno itself reads to compute RasterizationScale; sizes handed
    /// to the host are in physical pixels, so the launch size has to be multiplied by it.
    /// </summary>
    public static class DisplayScale
    {
        private static double _value;

        public static double Current => _value != 0 ? _value : _value = Read();

        /// <summary>
        /// Drop the cached value so the next read starts over. The one caller is
        /// <see cref="InterfaceScale.Bootstrap"/>: it has to read this scale (the desktop's) before
        /// it writes <c>UNO_DISPLAY_SCALE_OVERRIDE</c>, and everyone who reads it afterwards wants
        /// the overridden one - the same number Uno will report - so the cache from before the
        /// override has to go.
        /// </summary>
        public static void Invalidate()
        {
            _value = 0;
        }

        private static double Read()
        {
            // An explicit override wins, as it does for Uno itself.
            if (double.TryParse(Environment.GetEnvironmentVariable("UNO_DISPLAY_SCALE_OVERRIDE"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double over) && over > 0)
            {
                return over;
            }

            try
            {
                var display = XOpenDisplay(IntPtr.Zero);
                if (display != IntPtr.Zero)
                {
                    try
                    {
                        var resources = Marshal.PtrToStringUTF8(XResourceManagerString(display));
                        var dpi = ReadXftDpi(resources);
                        if (dpi > 0)
                        {
                            return dpi / 96d;
                        }
                    }
                    finally
                    {
                        XCloseDisplay(display);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
            }

            if (double.TryParse(Environment.GetEnvironmentVariable("GDK_SCALE"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double gdk) && gdk > 0)
            {
                return gdk;
            }

            return 1;
        }

        private static double ReadXftDpi(string resources)
        {
            if (string.IsNullOrEmpty(resources))
            {
                return 0;
            }

            foreach (var line in resources.Split('\n'))
            {
                if (!line.StartsWith("Xft.dpi:", StringComparison.Ordinal))
                {
                    continue;
                }

                if (double.TryParse(line.AsSpan(8).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dpi))
                {
                    return dpi;
                }
            }

            return 0;
        }

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XResourceManagerString(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(IntPtr display);
    }
}
