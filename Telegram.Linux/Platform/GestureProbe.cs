//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Globalization;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: with UNIGRAM_GESTURE_PROBE=1 the two rebuilt gallery gestures
    /// write what they are doing to the log, so a real finger can be measured instead of described.
    ///
    /// <see cref="Telegram.Controls.CarouselViewer"/> (the pass between images) logs the progress of
    /// every manipulation delta and, when the finger lifts, the three numbers its decision is made
    /// of - the cumulative translation, the release velocity and the projected progress - next to
    /// the decision itself. <see cref="Telegram.Controls.ZoomViewer"/> (the pinch) logs the factor
    /// and the translation of every step, plus the content point that was under the fingers before
    /// and after it: if the anchoring is right those two are the same point, and their distance is
    /// the error in layout pixels.
    ///
    /// Everything is behind <see cref="Enabled"/> so that no formatting happens on the gesture path
    /// when the probe is off, which is always outside a diagnosis run.
    /// </summary>
    public static class GestureProbe
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("UNIGRAM_GESTURE_PROBE") is string value
                && value.Length > 0 && value != "0";

        public static void Log(string message)
        {
            if (Enabled)
            {
                Logger.Info("gesture: " + message);
            }
        }

        public static string F(double value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }
    }
}
