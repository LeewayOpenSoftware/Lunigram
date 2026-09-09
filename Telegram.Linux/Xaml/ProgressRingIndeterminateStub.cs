//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls
{
    /// <summary>
    /// The "calculating..." spinner of the storage usage screen, on Uno's own ProgressRing instead
    /// of upstream's LottieGen composition ring.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not port the real one.</b> It walks straight into two separate PORTING.md 6
    /// entries, and one of them throws:</para>
    ///
    /// <para>1. Its <c>BindProperty</c> / <c>BindProperty2</c> start the SAME
    /// <c>ExpressionAnimation</c> instance (<c>_reusableExpressionAnimation</c>) on eight different
    /// objects. <c>Compositor.RegisterAnimation</c> indexes its dictionary BY THE ANIMATION OBJECT,
    /// so the second start is
    /// <c>ArgumentException: An item with the same key has already been added</c> - the shared
    /// animation trap, which this port has already hit eleven times.</para>
    ///
    /// <para>2. It animates <c>RotationAngleInDegrees</c>, which Uno does not animate at all (it
    /// animates <c>RotationAngle</c>, in radians). A spinner that cannot rotate is not a
    /// spinner.</para>
    ///
    /// <para>Between rewriting a LottieGen visual and using the ProgressRing Uno already ships, for
    /// an indicator that shows for the second or two <c>getStorageStatistics</c> takes, the
    /// ProgressRing wins.</para>
    ///
    /// <para><b>Why a stub type and not <c>win:</c> in the XAML.</b> The conditional prefix only
    /// works for elements of the default XAML namespace, and this one is
    /// <c>controls:ProgressRingIndeterminate</c> (PORTING.md rule 1). A stub keeps
    /// SettingsStoragePage.xaml byte-identical to upstream.</para>
    ///
    /// <para>Fill, Stroke and StrokeThickness are kept as real properties because the XAML sets all
    /// three; Stroke is mapped onto Foreground, which is what Uno's ring actually paints with, and
    /// the other two are accepted and ignored (upstream's ring draws no track and its thickness is
    /// baked into the Lottie visual anyway).</para>
    /// </remarks>
    public partial class ProgressRingIndeterminate : ProgressRing
    {
        public ProgressRingIndeterminate()
        {
            // Upstream's ring has no "off" state: it exists only while something is being
            // calculated, and the page adds and removes it. Same contract here.
            IsActive = true;
        }

        #region Stroke

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register("Stroke", typeof(Brush), typeof(ProgressRingIndeterminate), new PropertyMetadata(null, OnStrokeChanged));

        private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // The moving arc. Uno's ProgressRing paints it with Foreground.
            if (d is ProgressRingIndeterminate sender && e.NewValue is Brush brush)
            {
                sender.Foreground = brush;
            }
        }

        #endregion

        #region Fill

        /// <summary>
        /// The static track behind the arc. Accepted and ignored: Uno's ProgressRing draws no
        /// track, and painting Background instead would put a filled disc behind the ring rather
        /// than a ring behind it.
        /// </summary>
        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register("Fill", typeof(Brush), typeof(ProgressRingIndeterminate), new PropertyMetadata(null));

        #endregion

        #region StrokeThickness

        /// <summary>
        /// Accepted and ignored: the thickness of Uno's ring is part of its template.
        /// </summary>
        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register("StrokeThickness", typeof(double), typeof(ProgressRingIndeterminate), new PropertyMetadata(0d));

        #endregion
    }
}
