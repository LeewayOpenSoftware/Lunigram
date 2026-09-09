//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Telegram.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls
{
    /// <summary>
    /// Linux half of <see cref="CarouselViewer"/>: the pass between gallery images, rebuilt on top
    /// of the manipulation events because the machinery the Windows half uses does not exist on
    /// Uno Skia.
    ///
    /// What was measured on the assemblies that ship with this port (Uno 6.6.184, IL of
    /// <c>Uno.UI.Composition.dll</c>, not documentation) — every one of these is what the Windows
    /// half calls, so none of it is theoretical:
    ///  - <c>InteractionTracker.TryUpdatePositionWithAnimation</c> **throws**
    ///    <see cref="NotImplementedException"/>. That is the animated <c>ChangeView</c>, i.e. the
    ///    arrow buttons and the keyboard.
    ///  - <c>InteractionTracker.ConfigurePositionXInertiaModifiers</c> is a **no-op** (it only calls
    ///    <c>ApiInformation.TryRaiseNotImplemented</c>), so the snap points that decide "complete or
    ///    revert" never exist.
    ///  - <c>InteractionTracker.NaturalRestingPosition</c> throws, and it is the only input of those
    ///    snap conditions.
    ///  - <c>VisualInteractionSource.IsPositionXRailsEnabled</c> is a no-op setter and
    ///    <c>DeltaPosition</c>/<c>PositionVelocity</c>/<c>Scale</c> all throw.
    ///  - the expressions of the Windows half are written against <c>this.Target</c>, which Uno's
    ///    expression parser rejects with <c>ArgumentException: Unrecognized identifier 'this'</c>
    ///    (already noted in PORTING.md §6).
    /// So the tracker path is not "partially working": it is a page-killing exception on the first
    /// swipe and a dead snap otherwise.
    ///
    /// What Uno *does* implement, and what this file is built on, is the gesture recognizer:
    /// <c>GestureRecognizer.Manipulation</c> is a real implementation (rails, thresholds, velocities,
    /// inertia), and <c>ManipulationModes.Scale</c> maps to <c>GestureSettings.ManipulationScale</c>
    /// through <c>ManipulationModesExtensions.ToGestureSettings</c>. Numbers that matter and that
    /// are baked into Uno, not into this file:
    ///  - a touch manipulation only *starts* after 15 px of travel (<c>Manipulation.StartTouch</c>),
    ///    and every following delta needs 2 px (<c>DeltaTouch</c>);
    ///  - velocities are **pixels per millisecond** (<c>ComputeVelocities</c> divides by
    ///    <c>elapsedMicroseconds / 1000</c>).
    ///
    /// Inertia is deliberately left **off** here (no <c>TranslateInertia</c> in the mode): with it,
    /// <c>ManipulationCompleted</c> only arrives once Uno's own inertia processor has finished, and
    /// that processor is driven by <c>CompositionTarget.Rendering</c>
    /// (<c>Manipulation.CompositionInertiaProcessorTimer</c>) — the very clock PORTING.md §6 says is
    /// not a frame clock in this app. The flick is instead projected by hand from the velocity that
    /// <c>ManipulationCompleted</c> reports, and the settle is animated on
    /// <see cref="CompositionRenderingClock"/>, which is this port's working frame clock.
    ///
    /// The geometry is a straight port of the Windows expression animations, so the two platforms
    /// place the three elements at exactly the same offsets; see <see cref="GetOffsetLinux"/>.
    /// </summary>
    public partial class CarouselViewer
    {
        /// <summary>
        /// Fraction of the viewport a drag has to reach for the pass to complete instead of
        /// reverting. This is the same 0.25 the Windows snap conditions use
        /// ("<c>NaturalRestingPosition.X &gt; RestingValue - (RestingValue - MaxPosition.X) * 0.25</c>").
        /// </summary>
        public const double CommitThreshold = 0.25;

        /// <summary>
        /// How far ahead a flick is projected, in milliseconds of coasting. On Windows this is the
        /// difference between <c>Position</c> and <c>NaturalRestingPosition</c>, which Uno does not
        /// compute; 120 ms of the release velocity is what makes a fast, short flick (say 60 px at
        /// 2 px/ms) commit while a slow drag of the same 60 px reverts.
        /// </summary>
        public const double FlickProjectionMilliseconds = 120;

        /// <summary>Duration of the settle animation, complete or revert.</summary>
        public const double SettleDurationMilliseconds = 250;

        private bool _isManipulationEnabled = true;

        // Number of contacts currently down on the control. A second finger means a pinch, which
        // belongs to the ZoomViewer above: the pan is abandoned instead of following the midpoint
        // of the two fingers, which is what the recognizer reports for a two finger gesture.
        private int _contacts;
        private bool _abandoned;

        private bool _panning;

        // Progress in [-1, 1], the same quantity the Windows half keeps in the tracker's property
        // set: > 0 means the current image is on its way out to the left (Next), < 0 to the right.
        private float _progress;

        private bool _settling;
        private float _settleFrom;
        private float _settleTo;
        private CarouselDirection _settleCommit;
        private ulong _settleStart;
        private double _settleDuration;

        private void InitializeLinux()
        {
            // This one control owns **every** touch gesture of the gallery, the pinch included, and
            // that is not a design preference: nesting two manipulating elements is impossible on
            // Uno. UIElement.PrepareManagedManipulationEventBubbling calls
            // GestureRecognizer.CompleteGesture() on every ancestor a manipulation event bubbles
            // through, so the moment this control starts manipulating, the ZoomViewer above it is
            // completed and never sees another delta. Measured in unigram-linux/spikes/GestureSpike
            // (run-03.log): with the pinch handled one level up, the outer recognizer raised
            // ManipulationStarting once per finger and **zero** ManipulationDelta.
            // So: Scale lives here, and ZoomViewer listens to the events this recognizer emits as
            // they bubble past it.
            //
            // Rails on Y (not on X): a mostly vertical drag has its X zeroed, so it cannot be read
            // as a pass; a horizontal one keeps both axes, which is what panning a zoomed picture
            // needs.
            ManipulationMode = ManipulationModes.TranslateX
                | ManipulationModes.TranslateY
                | ManipulationModes.TranslateRailsY
                | ManipulationModes.Scale;

            // Subscribing is what turns the gesture settings on in Uno
            // (UIElement.AddManipulationHandler -> UpdateManipulations), the mode alone is not enough.
            ManipulationStarted += OnManipulationStartedLinux;
            ManipulationDelta += OnManipulationDeltaLinux;
            ManipulationCompleted += OnManipulationCompletedLinux;

            AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressedLinux), true);
            AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerLostLinux), true);
            AddHandler(PointerCanceledEvent, new PointerEventHandler(OnPointerLostLinux), true);
            AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPointerLostLinux), true);
        }

        private void OnLoadedLinux()
        {
            ApplyProgressLinux(_progress);
        }

        private void OnUnloadedLinux()
        {
            StopSettleLinux();
        }

        private void OnPointerWheelChangedLinux(PointerRoutedEventArgs e)
        {
            if (!_isManipulationEnabled || IsScrolling)
            {
                return;
            }

            var point = e.GetCurrentPoint(this);

            // WindowContext.KeyModifiers() of the Windows half goes through CoreWindow, which is
            // null on Uno (PORTING.md §6); the args carry the same information.
            if (point.Properties.IsHorizontalMouseWheel || e.KeyModifiers != Windows.System.VirtualKeyModifiers.None)
            {
                return;
            }

            var direction = point.Properties.MouseWheelDelta > 0
                ? CarouselDirection.Previous
                : CarouselDirection.Next;

            if (direction == CarouselDirection.Next ? !_canGoNext : !_canGoPrev)
            {
                return;
            }

            ViewChanging?.Invoke(this, new CarouselViewChangingEventArgs(direction));
            ChangeView(direction);

            e.Handled = true;
        }

        #region Contacts

        private void OnPointerPressedLinux(object sender, PointerRoutedEventArgs e)
        {
            _contacts++;

            if (_contacts > 1 && _panning)
            {
                // A pinch started while a pan was in flight: give the gesture to the zoom and put
                // the images back where they were.
                AbandonLinux();
            }
        }

        private void OnPointerLostLinux(object sender, PointerRoutedEventArgs e)
        {
            _contacts = Math.Max(0, _contacts - 1);

            if (_contacts == 0)
            {
                _abandoned = false;
            }
        }

        private void AbandonLinux()
        {
            _abandoned = true;
            _panning = false;

            SettleLinux(0, CarouselDirection.None);
        }

        #endregion

        #region Manipulation

        private void OnManipulationStartedLinux(object sender, ManipulationStartedRoutedEventArgs e)
        {
            if (!_isManipulationEnabled || _contacts > 1 || _abandoned || IsScrolling)
            {
                return;
            }

            StopSettleLinux();

            _panning = true;
            _progress = 0;

            ConfigureElements();
            ApplyProgressLinux(0);

            GestureProbe.Log($"carousel started, viewport {GestureProbe.F(ActualSize.X)} wide, "
                + $"prev {_canGoPrev}, next {_canGoNext}");
        }

        private void OnManipulationDeltaLinux(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (e.Delta.Scale != 1 && _panning)
            {
                // A pinch, not a pass: hand the gesture to the zoom and put the images back.
                AbandonLinux();
            }

            if (!_panning || _contacts > 1 || _abandoned)
            {
                return;
            }

            var width = ActualSize.X;
            if (width <= 0)
            {
                return;
            }

            // Cumulative, not the running sum of the deltas: PORTING.md §6 notes that PointerMoved
            // arrives twice per move on X11, and a hand-accumulated delta would count double. The
            // recognizer's Cumulative is computed from the pointer positions, so it is immune.
            ApplyProgressLinux(ClampProgressLinux((float)(-e.Cumulative.Translation.X / width)));

            GestureProbe.Log($"carousel delta cumulative {GestureProbe.F(e.Cumulative.Translation.X)} px "
                + $"-> progress {GestureProbe.F(_progress)}, current image at x "
                + $"{GestureProbe.F(GetTranslationLinux(1))}");

            // Deliberately not Handled: ZoomViewer is listening to this very event as it bubbles.
        }

        private void OnManipulationCompletedLinux(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (!_panning)
            {
                return;
            }

            _panning = false;

            var width = ActualSize.X;
            if (width <= 0)
            {
                SettleLinux(0, CarouselDirection.None);
                return;
            }

            var direction = DecideLinux(e.Cumulative.Translation.X, e.Velocities.Linear.X, width, _canGoPrev, _canGoNext);

            if (GestureProbe.Enabled)
            {
                var cumulative = e.Cumulative.Translation.X;
                var velocity = e.Velocities.Linear.X;
                var projected = cumulative + velocity * FlickProjectionMilliseconds;

                GestureProbe.Log($"carousel released: drag {GestureProbe.F(cumulative)} px, "
                    + $"velocity {GestureProbe.F(velocity)} px/ms, "
                    + $"projected progress {GestureProbe.F(-projected / width)} "
                    + $"against threshold {GestureProbe.F(CommitThreshold)} => {direction}");
            }

            CommitLinux(direction);
        }

        /// <summary>
        /// Complete or revert. Pure function of the numbers the recognizer reports, so the spike can
        /// check the threshold without a window: <paramref name="cumulativeX"/> in layout pixels
        /// (negative = the finger went left), <paramref name="velocityX"/> in pixels per
        /// millisecond, <paramref name="width"/> the viewport.
        /// </summary>
        public static CarouselDirection DecideLinux(double cumulativeX, double velocityX, double width, bool canGoPrev, bool canGoNext)
        {
            if (width <= 0)
            {
                return CarouselDirection.None;
            }

            var projected = cumulativeX + velocityX * FlickProjectionMilliseconds;
            var progress = -projected / width;

            if (canGoNext && progress > CommitThreshold)
            {
                return CarouselDirection.Next;
            }
            else if (canGoPrev && progress < -CommitThreshold)
            {
                return CarouselDirection.Previous;
            }

            return CarouselDirection.None;
        }

        private void CommitLinux(CarouselDirection direction)
        {
            if (direction == CarouselDirection.None)
            {
                SettleLinux(0, CarouselDirection.None);
                return;
            }

            // Same order as the Windows half: the host is told first, and it is the host that
            // rotates the three elements (GalleryWindow.TryChangeView -> PrepareNext ->
            // PrepareElements). IsScrolling must still be false here or the host refuses.
            var before = _progress;

            ViewChanging?.Invoke(this, new CarouselViewChangingEventArgs(direction));

            // After the rotation the very same pixels are described by a progress one unit closer to
            // zero from the other side: the element that was the current one at p is now the
            // previous one at p - 1, and the element that was next is now the current one. This is
            // the moving "RestingValue" of the Windows half, written out.
            _progress = before - (int)direction;

            SettleLinux(0, direction);
        }

        #endregion

        #region Settle

        private void SettleLinux(float to, CarouselDirection commit)
        {
            StopSettleLinux();

            _settleFrom = _progress;
            _settleTo = to;
            _settleCommit = commit;

            if (_settleFrom == _settleTo || !PowerSavingPolicy.AreSmoothTransitionsEnabled)
            {
                ApplyProgressLinux(to);
                CompleteSettleLinux();
                return;
            }

            // Proportional to the distance left, so a pass that is already 90% done does not take
            // as long as one that starts from zero.
            _settleDuration = Math.Max(60, SettleDurationMilliseconds * Math.Abs(_settleFrom - _settleTo));
            _settleStart = Logger.TickCount;
            _settling = true;
            _scrolling = Logger.TickCount;

            CompositionRenderingClock.Rendering += OnSettleTickLinux;
        }

        private void OnSettleTickLinux(object sender, object e)
        {
            var elapsed = Logger.TickCount - _settleStart;
            var progress = Math.Clamp(elapsed / _settleDuration, 0, 1);

            // Ease out cubic, the shape a WinUI implicit animation would have used.
            var eased = 1 - Math.Pow(1 - progress, 3);

            ApplyProgressLinux((float)(_settleFrom + (_settleTo - _settleFrom) * eased));

            // Keep IsScrolling true for the whole settle: the host uses it to refuse a second pass
            // while one is running.
            _scrolling = Logger.TickCount;

            if (progress >= 1)
            {
                StopSettleLinux();
                CompleteSettleLinux();
            }
        }

        private void StopSettleLinux()
        {
            if (_settling)
            {
                _settling = false;
                CompositionRenderingClock.Rendering -= OnSettleTickLinux;
            }
        }

        private void CompleteSettleLinux()
        {
            if (_settleCommit != CarouselDirection.None)
            {
                var direction = _settleCommit;
                _settleCommit = CarouselDirection.None;

                ViewChanged?.Invoke(this, new CarouselViewChangedEventArgs(direction));
            }
        }

        /// <summary>
        /// The Linux <c>ChangeView</c>: the host has already rotated the elements (it calls
        /// <c>TryChangeView</c> first), so the new current element is sitting off screen and the
        /// pass is played by walking the progress from -direction back to zero.
        /// </summary>
        private void ChangeViewLinux(CarouselDirection direction, bool disableAnimation)
        {
            _scrolling = Logger.TickCount;

            StopSettleLinux();
            ConfigureElements();

            _panning = false;
            _progress = -(int)direction;

            ApplyProgressLinux(_progress);

            if (disableAnimation)
            {
                ApplyProgressLinux(0);
                _settleCommit = direction;
                CompleteSettleLinux();
            }
            else
            {
                SettleLinux(0, direction);
            }
        }

        #endregion

        #region Geometry

        private float ClampProgressLinux(float progress)
        {
            var min = _canGoPrev ? -1f : 0f;
            var max = _canGoNext ? 1f : 0f;

            return Math.Clamp(progress, min, max);
        }

        private static Vector2 GetSizeLinux(FrameworkElement element)
        {
            if (element is AspectView { RotationAngle: RotationAngle.Angle90 or RotationAngle.Angle270 })
            {
                return new Vector2(element.ActualSize.Y, element.ActualSize.X);
            }

            return element.ActualSize;
        }

        /// <summary>
        /// Where the element of <paramref name="slot"/> (0 = previous, 1 = current, 2 = next) sits
        /// for a given progress. Transcribed from the three expression animations of the Windows
        /// half so both platforms agree pixel for pixel, ±2 px seam included.
        /// </summary>
        public static float GetOffsetLinux(int slot, float halfWidth, float elementWidth, float progress)
        {
            // GetElementPosition of the Windows half, verbatim: column 0 is Floor(-halfWidth - w/2)
            // and column 2 is Floor(halfWidth + w/2). They are not each other's negation once the
            // half pixel shows up (Floor(-x) == -Ceil(x)), so both are kept.
            var right = MathF.Floor(halfWidth + elementWidth / 2);
            var left = MathF.Floor(-halfWidth - elementWidth / 2);

            switch (slot)
            {
                // "Floor(value + (Progress < 0 ? Progress * (maxValue - value) * -1 : 0)) - 2"
                // with value = left and maxValue = 0, which reduces to left * (1 + Progress).
                case 0:
                    return MathF.Floor(progress < 0 ? left * (1 + progress) : left) - 2;

                // "Progress > 0 ? Progress * maxValue * -1 : Progress * minValue"
                // with maxValue = right and minValue = left.
                case 1:
                    return progress > 0 ? -progress * right : progress * left;

                // "Ceil(value + (Progress > 0 ? Progress * (value - maxValue) * -1 : 0)) + 2"
                // with value = right and maxValue = 0, which reduces to right * (1 - Progress).
                default:
                    return MathF.Ceiling(progress > 0 ? right * (1 - progress) : right) + 2;
            }
        }

        private void ApplyProgressLinux(float progress)
        {
            _progress = progress;

            var halfWidth = ActualSize.X / 2;

            for (int i = 0; i < _elements.Length; i++)
            {
                var element = _elements[i];
                if (element == null)
                {
                    continue;
                }

                var size = GetSizeLinux(element);
                var offset = GetOffsetLinux(i, halfWidth, size.X, progress);

                // RenderTransform and not the composition Translation the Windows half animates:
                // there is no expression animation to drive here, the value is written once per
                // frame from managed code anyway, and a TranslateTransform is something a spike can
                // read back and assert on.
                if (element.RenderTransform is not TranslateTransform translate)
                {
                    translate = new TranslateTransform();
                    element.RenderTransform = translate;
                }

                translate.X = offset;
            }
        }

        /// <summary>Current progress, for diagnostics and for the gesture spike.</summary>
        public float ProgressLinux => _progress;

        /// <summary>Where the element of that slot currently sits, for the gesture spike.</summary>
        public double GetTranslationLinux(int slot)
        {
            return _elements[slot]?.RenderTransform is TranslateTransform translate
                ? translate.X
                : 0;
        }

        #endregion
    }
}
