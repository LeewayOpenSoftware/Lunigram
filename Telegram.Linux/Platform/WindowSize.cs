//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Services;
using Telegram.Services.Settings;
using Windows.Foundation;

namespace Telegram.Common
{
    /// <summary>
    /// The size of the main window, in logical units, remembered across runs.
    /// </summary>
    /// <remarks>
    /// On Windows the shell does this for the app: <c>ApplicationView.PreferredLaunchViewSize</c>
    /// follows the window as the user drags it, and RootPage only writes to it when the theme
    /// editor widens the window. Uno stores the property but never updates it - the X11 host reads
    /// it once, in X11XamlRootHost.Initialize, to size the window it is about to create - so the
    /// size is tracked here instead, from Window.SizeChanged.
    ///
    /// It is kept in logical units on purpose. The host takes the launch size in physical pixels,
    /// so <see cref="Restore"/> is multiplied by <see cref="DisplayScale.Current"/> at launch; a
    /// value stored in pixels would resize the window by a whole display scale the first time the
    /// user changed the desktop's DPI between two runs.
    ///
    /// <para><b>And that is why the interface scale is stored with it.</b> A logical unit is only
    /// worth the same number of pixels while the scale stays put, and Unigram's own 100-250%
    /// setting moves it (<see cref="InterfaceScale"/>) - so the same stored 1100x720 asks for a
    /// physically different window on either side of a scale change. Measured: touch mode raises
    /// the scale to 150%, the window is saved at the logical size it has there, and turning touch
    /// mode off reopened the window at <b>two thirds</b> of its physical size (100/150), which is
    /// exactly the ratio between the two scales. The percentage in force is therefore saved
    /// alongside, and <see cref="Restore"/> converts: <c>stored x saved% / current%</c> is the
    /// logical size that lands on the same physical window. A desktop DPI change is left alone -
    /// there the logical size is still the right thing to keep, which is what the paragraph above
    /// is about.</para>
    /// </remarks>
    public static class WindowSize
    {
        /// <summary>
        /// The size the window opens at when nothing has been remembered yet.
        /// </summary>
        public static readonly Size Default = new(1100, 720);

        /// <summary>
        /// The smallest size the window may be dragged to.
        /// </summary>
        public static readonly Size Minimum = new(320, 500);

        // Windows an X server hands out while they are being mapped or unmapped are 1x1, and a
        // window that is being restored can report a stale size for a frame: neither is a size
        // the user chose, and writing one over the remembered size would lose it.
        private const double LargestSaneSide = 32767;

        private static SettingsServiceBase _container;
        private static SettingsServiceBase Container => _container ??= new SettingsServiceBase("Window");

        private static double _width;
        private static double _height;

        /// <summary>
        /// The interface-scale percentage the stored size was measured at. 0 means "not recorded"
        /// - a settings file written before this was tracked - and is treated as "the current one",
        /// which is what the code did before.
        /// </summary>
        private static int _percent;

        /// <summary>
        /// The size to open the window at, in logical units.
        /// </summary>
        public static Size Restore()
        {
            try
            {
                var width = Container.GetValueOrDefault("Width", Default.Width);
                var height = Container.GetValueOrDefault("Height", Default.Height);
                var percent = Container.GetValueOrDefault("Scaling", 0);

                if (IsSane(width, height))
                {
                    // Remembered as they are stored, not as they are returned: the next
                    // Save then sees a change and rewrites both the size and the percentage.
                    _width = width;
                    _height = height;
                    _percent = percent;

                    var factor = ScaleFactorSince(percent);

                    return new Size(
                        Math.Max(width * factor, Minimum.Width),
                        Math.Max(height * factor, Minimum.Height));
                }
            }
            catch (Exception ex)
            {
                // A window that opens at the default size beats one that does not open.
                Logger.Error(ex.ToString());
            }

            return Default;
        }

        /// <summary>
        /// Remembers the size the window now has, in logical units. Safe to call for every
        /// resize event: the value is only written when it actually changed, and the settings
        /// store coalesces writes to disk.
        /// </summary>
        public static void Save(Size size)
        {
            var percent = InterfaceScale.AppliedPercent;

            if (!IsSane(size.Width, size.Height) || (size.Width == _width && size.Height == _height && percent == _percent))
            {
                return;
            }

            _width = size.Width;
            _height = size.Height;
            _percent = percent;

            try
            {
                Container.AddOrUpdateValue("Width", size.Width);
                Container.AddOrUpdateValue("Height", size.Height);
                Container.AddOrUpdateValue("Scaling", percent);
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
            }
        }

        /// <summary>
        /// What one stored logical unit is worth now, in logical units: the ratio between the
        /// interface scale the size was saved at and the one this process is running at. 1 when
        /// nothing changed, when nothing was recorded, or when either end is out of range.
        /// </summary>
        private static double ScaleFactorSince(int saved)
        {
            var current = InterfaceScale.AppliedPercent;

            if (saved < 100 || current < 100 || saved == current)
            {
                return 1;
            }

            return saved / (double)current;
        }

        private static bool IsSane(double width, double height)
        {
            return width >= Minimum.Width && width <= LargestSaneSide
                && height >= Minimum.Height && height <= LargestSaneSide;
        }
    }
}
