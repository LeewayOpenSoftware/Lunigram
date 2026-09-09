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
    /// Unigram's 100-250% interface scale (<c>AppearanceSettings.Scaling</c>), on Uno.
    ///
    /// <para><b>What it replaced.</b> On Windows the setting went through
    /// <c>NativeUtils.OverrideScaleForCurrentView</c>, which pushed a resolution scale onto the
    /// current view: from that point on the whole stack - layout, hit testing, image decode,
    /// <c>RasterizationScale</c>, popups - agreed on the same number. On Linux that entry point was
    /// an empty stub, so the setting was stored and never applied.</para>
    ///
    /// <para><b>The three ways to do it on Uno, and why this one.</b>
    /// <list type="number">
    /// <item><description><b>The host's own scale</b> - what this class does. Uno's X11 display
    /// information reads <c>UNO_DISPLAY_SCALE_OVERRIDE</c> from the environment and, when it is
    /// set, uses it instead of <c>Xft.dpi</c> for <c>RawPixelsPerViewPixel</c>, <c>LogicalDpi</c>
    /// and <c>ResolutionScale</c> - on the XRandR path as well as on the fallback one (checked in
    /// <c>X11DisplayInformationExtension.UpdateDetails</c> and
    /// <c>GetDisplayInformationXRandR1_3</c> of Uno 6.6.184; the override is honoured in both, which
    /// is not obvious from the code because the XRandR branch returns early). This is the same
    /// single number the Windows call moved, so nothing downstream can disagree with it.
    /// Its price: the value is read once, into a <c>readonly</c> field, when
    /// <c>DisplayInformation</c> is first constructed - i.e. with the first window. It cannot be
    /// moved afterwards, so <b>a change of scale takes effect on the next start</b>.</description></item>
    /// <item><description><b>A <c>ScaleTransform</c> on the root content</b>, sized to
    /// <c>window / factor</c> so that measure and arrange still run in a coherent space. This one
    /// does apply live, and layout does survive it - but two things do not. Everything that
    /// rasterises by hand asks <c>XamlRoot.RasterizationScale</c> /
    /// <c>DisplayInformation.RawPixelsPerViewPixel</c> how many device pixels a DIP is worth
    /// (stickers, emoji, animated avatars, blurred thumbnails - see the note on
    /// <c>RasterizationScale</c> in <c>PORTING.md</c> §6, which is this exact bug already once):
    /// those numbers would keep reporting the display's scale, so every hand-rasterised surface
    /// would come out at the wrong resolution and be stretched by the transform. And popups,
    /// flyouts, menus, tooltips and content dialogs are parented to the <c>XamlRoot</c>'s own
    /// overlay layer, <i>outside</i> the scaled subtree, so the page would grow and every menu
    /// opened over it would stay small. In a client made of flyouts that is not a
    /// trade.</description></item>
    /// <item><description><b>Writing <c>Xft.dpi</c> into the X resource database</b> and letting
    /// Uno pick it up through its DPI-changed path. It applies live and it is coherent - for
    /// <i>every</i> X client in the session, which is not ours to change.</description></item>
    /// </list></para>
    ///
    /// <para><b>The number.</b> <c>UNO_DISPLAY_SCALE_OVERRIDE</c> <i>replaces</i> the display scale,
    /// it does not multiply it, while Unigram's setting means "this much on top of what the desktop
    /// already does". So the value written is <c>system scale x percent / 100</c>: on a Surface
    /// Pro 4 with <c>Xft.dpi: 192</c> (scale 2), 150% is written as <c>3</c>. The system scale comes
    /// from <see cref="DisplayScale"/>, which reads the same <c>Xft.dpi</c> Uno reads and honours
    /// the same override - so it has to be read <b>before</b> the override is set, and that is why
    /// <see cref="Bootstrap"/> reads it first.</para>
    /// </summary>
    public static class InterfaceScale
    {
        /// <summary>
        /// The environment variable Uno's X11 display information reads. Also honoured by
        /// <see cref="DisplayScale"/>, so both agree once it is set.
        /// </summary>
        public const string OverrideVariable = "UNO_DISPLAY_SCALE_OVERRIDE";

        /// <summary>
        /// The scale the desktop asked for, before Unigram had a say: 2 on a 192 dpi panel.
        /// Read once, and read before <see cref="Bootstrap"/> writes the override, so it keeps
        /// meaning "the system's", not "ours".
        /// </summary>
        private static double _systemScale;

        /// <summary>
        /// The percentage in force for this process, 100 = "whatever the desktop says". This is
        /// what <c>NativeUtils.GetScaleForCurrentView</c> answers, and it does not move when the
        /// setting is changed - the setting is for the next start.
        /// </summary>
        private static int _appliedPercent = 100;

        public static double SystemScale => _systemScale != 0 ? _systemScale : _systemScale = DisplayScale.Current;

        public static int AppliedPercent => _appliedPercent;

        /// <summary>
        /// True once the stored scale has been applied to this process, which is the only state in
        /// which changing it needs a restart to be seen.
        /// </summary>
        public static bool IsApplied { get; private set; }

        /// <summary>
        /// Called from <c>Program.Main</c>, before Uno builds anything: this is the last moment at
        /// which the environment variable can still be read by the display information Uno creates
        /// with the first window.
        ///
        /// <paramref name="percent"/> is <c>AppearanceSettings.Scaling</c>, or 0 / out of range for
        /// "leave the desktop alone", in which case nothing is written and Uno keeps reading
        /// <c>Xft.dpi</c> exactly as before this class existed.
        /// </summary>
        public static void Bootstrap(int percent)
        {
            // Read first: DisplayScale honours the same variable, and once it is set it would
            // answer with our own number instead of the desktop's.
            var system = SystemScale;

            if (percent is < 100 or > 250 || percent == 100)
            {
                _appliedPercent = 100;
                return;
            }

            // An override already in the environment is the user's (or a test's), and it wins:
            // it is how someone reproduces a scale without touching the settings file.
            var existing = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrEmpty(existing))
            {
                return;
            }

            var factor = system * percent / 100d;

            Environment.SetEnvironmentVariable(OverrideVariable, factor.ToString("0.####", CultureInfo.InvariantCulture));

            // DisplayScale cached the desktop's number a moment ago; from here on everyone who asks
            // it - AnimatedImageBase.ResolveRasterizationScale above all, which decodes stickers and
            // emoji at that scale - has to get the one Uno is about to report, or every hand-drawn
            // surface comes out at the display's scale and gets stretched to ours.
            DisplayScale.Invalidate();

            _appliedPercent = percent;
            IsApplied = true;
        }
    }
}
