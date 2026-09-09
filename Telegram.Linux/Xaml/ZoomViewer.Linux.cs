//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.ComponentModel;
using Telegram.Common;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls
{
    /// <summary>
    /// Linux half of <see cref="ZoomViewer"/>: pinch to zoom, rebuilt because the
    /// <see cref="ScrollViewer"/> zoom the Windows half delegates to **does nothing at all** on Uno
    /// Skia. Measured on the shipped <c>Uno.UI.dll</c> (IL, 6.6.184):
    ///  - <c>ScrollViewer.ChangeViewNative</c> calls <c>presenter.Set(horizontalOffset,
    ///    verticalOffset, <b>null</b>, ...)</c>: the zoom factor argument of
    ///    <c>ChangeView(h, v, zoomFactor)</c> is dropped on the floor before it reaches the presenter.
    ///  - <c>ScrollContentPresenter.Set</c> then calls <c>Update(view, h, v, <b>1.0</b>, options)</c>,
    ///    i.e. the scale is hard coded to 1.
    ///  - <c>ScrollViewer.ZoomToFactor</c> is a no-op (<c>ApiInformation.TryRaiseNotImplemented</c>),
    ///    <c>OnZoomModeChanged</c> has an empty body, and
    ///    <c>ScrollContentPresenter.OnMinZoomFactorChanged</c>/<c>OnMaxZoomFactorChanged</c> are empty.
    ///  - <c>ScrollViewer.OnPresenterZoomed</c>, the only writer of <c>ZoomFactor</c>, has no caller
    ///    in the whole assembly, so <c>ZoomFactor</c> answers its default 1 forever.
    /// So <c>ZoomMode="Enabled"</c> compiles, does not throw, reports 1, and never scales anything:
    /// one more of the APIs of PORTING.md §6 that lie. And it lies twice over, because
    /// <c>GalleryWindow.ScrollingHost_ViewChanged</c> reads that constant 1 to decide whether the
    /// pass between images is allowed.
    ///
    /// The replacement keeps the public shape of the control (<see cref="ZoomFactor"/>,
    /// <see cref="CanZoomIn"/>, <see cref="CanZoomOut"/>, <c>Zoom</c>, <c>ViewChanged</c>,
    /// <c>PanStarting</c>) so the gallery code above it is untouched, and puts the transform on the
    /// content itself: a <see cref="CompositeTransform"/> whose scale/translate pair is driven by
    /// <c>ManipulationDelta.Scale</c>, which Uno's gesture recognizer does compute
    /// (<c>Manipulation.GetDelta</c>: <c>scale = to.Distance / from.Distance</c> when
    /// <c>GestureSettings.ManipulationScale</c> is on).
    ///
    /// **This control does not own the gesture.** It cannot: Uno's
    /// <c>UIElement.PrepareManagedManipulationEventBubbling</c> calls
    /// <c>GestureRecognizer.CompleteGesture()</c> on every ancestor that a manipulation event bubbles
    /// through, so an outer manipulating element is killed the instant an inner one starts. Measured
    /// in <c>unigram-linux/spikes/GestureSpike</c> (run-03.log): with <c>ManipulationMode</c> set
    /// here and on the carousel inside, this control's recognizer raised <c>ManipulationStarting</c>
    /// once per finger and **zero** <c>ManipulationDelta</c>, while the carousel got every one of
    /// them. So <see cref="CarouselViewer"/> owns the single recognizer (Scale included) and this
    /// control subscribes to the events as they bubble past, with <c>handledEventsToo</c>.
    /// Consequence for whoever changes this later: adding <c>ManipulationMode</c> here would not
    /// "also" work, it would silently do nothing.
    ///
    /// Anchoring: a pinch must keep the content point that was under the fingers under them. With
    /// <c>p -&gt; s*p + t</c> (the order a CompositeTransform applies: scale first, translate last),
    /// that point is <c>p = (W' - t) / s</c> where <c>W'</c> is where the fingers were, so putting it
    /// under the fingers' new position <c>W</c> at the new scale <c>s'</c> means
    /// <c>t' = W - s' * (W' - t) / s</c>. One formula for both the zoom anchor (W == W') and the pan
    /// (s' == s, which reduces to <c>t' = t + (W - W')</c>); see <see cref="ApplyPinchLinux"/>.
    ///
    /// <c>W</c> is not read from <c>ManipulationDelta.Position</c> directly: that is expressed in the
    /// coordinates of <c>e.Container</c>, i.e. of the carousel, which is the very element this
    /// transform scales. Running it through <c>TransformToVisual(this)</c> puts it back into this
    /// control's coordinates, which is where the transform lives - and, incidentally, makes it the
    /// real position of the fingers in the window whatever the current zoom is.
    /// </summary>
    public partial class ZoomViewer
    {
        private double _zoomFactor = 1;
        private double _translateX;
        private double _translateY;

        private CompositeTransform _transform;

        private bool _pinching;
        private bool _hasCenter;
        private Point _center;

        private void InitializeLinux()
        {
            // The Windows template stretched the content inside its ScrollViewer; without a template
            // the ContentControl defaults would leave the carousel at its natural size in a corner.
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;

            // No ManipulationMode here on purpose - see the class remarks. handledEventsToo, because
            // the events come from a descendant and may already be marked as handled.
            AddHandler(ManipulationStartedEvent, new ManipulationStartedEventHandler(OnManipulationStartedLinux), true);
            AddHandler(ManipulationDeltaEvent, new ManipulationDeltaEventHandler(OnManipulationDeltaLinux), true);
            AddHandler(ManipulationCompletedEvent, new ManipulationCompletedEventHandler(OnManipulationCompletedLinux), true);

            // The mouse path of the Windows half moved the ScrollViewer offsets; here it moves the
            // same translation the pinch does, and only while there is something to pan.
            AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressedLinux), true);
            AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMovedLinux), true);
            AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleasedLinux), true);

            SizeChanged += OnSizeChangedLinux;
        }

        private void OnSizeChangedLinux(object sender, SizeChangedEventArgs e)
        {
            // A ContentControl does not clip, and the ScrollViewer that used to do it is gone.
            Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };

            ClampLinux();
            ApplyTransformLinux(false);
        }

        private CompositeTransform GetTransformLinux()
        {
            if (Content is not FrameworkElement content)
            {
                return null;
            }

            if (content.RenderTransform is not CompositeTransform transform || _transform == null)
            {
                transform = _transform = new CompositeTransform();

                content.RenderTransformOrigin = new Point();
                content.RenderTransform = transform;
            }

            return transform;
        }

        #region Zoom

        private void ZoomLinux(float factor, bool disableAnimation)
        {
            // The buttons zoom around the centre of the viewport, which is what the offset
            // arithmetic of the Windows half works out to when there is nothing scrolled yet.
            var center = new Point(ActualWidth / 2, ActualHeight / 2);

            ApplyPinchLinux(factor / _zoomFactor, center, center);
        }

        /// <summary>
        /// The pinch itself. <paramref name="scale"/> is the relative factor of this step
        /// (<c>ManipulationDelta.Scale</c>), <paramref name="center"/> where the fingers are now and
        /// <paramref name="previousCenter"/> where they were, both in this control's coordinates.
        /// Returns true when anything moved.
        /// </summary>
        private bool ApplyPinchLinux(double scale, Point center, Point previousCenter)
        {
            var previous = _zoomFactor;

            var factor = Math.Clamp(previous * scale, Math.Max(0.1, MinZoomFactor), Math.Max(MinZoomFactor, MaxZoomFactor));
            var applied = factor / previous;

            var beforeX = _translateX;
            var beforeY = _translateY;

            // t' = W - s' * (W' - t) / s : the content point that was under the fingers ends up under
            // where the fingers are now, at the new scale. Zoom anchor and pan in one line.
            _translateX = center.X - applied * (previousCenter.X - _translateX);
            _translateY = center.Y - applied * (previousCenter.Y - _translateY);
            _zoomFactor = factor;

            ClampLinux();

            var changed = applied != 1 || _translateX != beforeX || _translateY != beforeY;
            if (changed)
            {
                ApplyTransformLinux(true);
            }

            if (GestureProbe.Enabled)
            {
                // The content point that was under the fingers, before and after this step. The
                // whole point of the anchoring is that the two are the same point: their distance
                // is the error, in layout pixels of the content.
                var anchorBeforeX = (previousCenter.X - beforeX) / previous;
                var anchorBeforeY = (previousCenter.Y - beforeY) / previous;
                var anchorAfterX = (center.X - _translateX) / _zoomFactor;
                var anchorAfterY = (center.Y - _translateY) / _zoomFactor;

                var driftX = anchorAfterX - anchorBeforeX;
                var driftY = anchorAfterY - anchorBeforeY;

                GestureProbe.Log($"zoom step {GestureProbe.F(scale)}: factor {GestureProbe.F(previous)} "
                    + $"-> {GestureProbe.F(_zoomFactor)}, fingers at ({GestureProbe.F(center.X)}, "
                    + $"{GestureProbe.F(center.Y)}), translate ({GestureProbe.F(beforeX)}, "
                    + $"{GestureProbe.F(beforeY)}) -> ({GestureProbe.F(_translateX)}, "
                    + $"{GestureProbe.F(_translateY)}), anchored content point "
                    + $"({GestureProbe.F(anchorBeforeX)}, {GestureProbe.F(anchorBeforeY)}) -> "
                    + $"({GestureProbe.F(anchorAfterX)}, {GestureProbe.F(anchorAfterY)}), drift "
                    + $"({GestureProbe.F(driftX)}, {GestureProbe.F(driftY)})");
            }

            return changed;
        }

        /// <summary>
        /// Keeps the scaled content covering the viewport: no empty band at the edges, and centred
        /// on the axes where it is smaller than the viewport (which is every axis at factor 1).
        /// </summary>
        private void ClampLinux()
        {
            var width = ActualWidth;
            var height = ActualHeight;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            var scaledWidth = width * _zoomFactor;
            var scaledHeight = height * _zoomFactor;

            _translateX = scaledWidth <= width
                ? (width - scaledWidth) / 2
                : Math.Clamp(_translateX, width - scaledWidth, 0);

            _translateY = scaledHeight <= height
                ? (height - scaledHeight) / 2
                : Math.Clamp(_translateY, height - scaledHeight, 0);
        }

        private void ApplyTransformLinux(bool notify)
        {
            var transform = GetTransformLinux();
            if (transform == null)
            {
                return;
            }

            transform.ScaleX = _zoomFactor;
            transform.ScaleY = _zoomFactor;
            transform.TranslateX = _translateX;
            transform.TranslateY = _translateY;

            if (notify)
            {
                ViewChanged?.Invoke(this, new ScrollViewerViewChangedEventArgs());
            }
        }

        #endregion

        #region Manipulation

        private void OnManipulationStartedLinux(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _pinching = false;
            _hasCenter = false;
        }

        private void OnManipulationDeltaLinux(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            var scale = e.Delta.Scale;
            var center = ToLocalLinux(e);

            var previous = _hasCenter ? _center : center;

            _center = center;
            _hasCenter = true;

            // A one finger drag reports Scale == 1. At factor 1 there is nothing to pan and the
            // gesture belongs to the CarouselViewer underneath, so it is left alone; once zoomed in
            // the same drag pans, and the carousel has been told to stop listening by
            // GalleryWindow.ScrollingHost_ViewChanged.
            if (scale == 1 && _zoomFactor <= 1)
            {
                return;
            }

            if (scale != 1)
            {
                _pinching = true;
            }

            ApplyPinchLinux(scale, center, previous);
        }

        private void OnManipulationCompletedLinux(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (_pinching || _zoomFactor > 1)
            {
                // One last non intermediate notification so the gallery re-reads CanZoomIn/Out and
                // re-enables the pass between images when the pinch ended back at 1.
                ApplyTransformLinux(true);
            }

            _pinching = false;
            _hasCenter = false;
        }

        /// <summary>
        /// The position the manipulation reports, in this control's coordinates. It arrives in the
        /// coordinates of the element that owns the recognizer (the carousel, which this transform
        /// scales), so it has to come back through the transform.
        /// </summary>
        private Point ToLocalLinux(ManipulationDeltaRoutedEventArgs e)
        {
            if (e.Container != null && e.Container != this)
            {
                try
                {
                    return e.Container.TransformToVisual(this).TransformPoint(e.Position);
                }
                catch
                {
                    // Not in the tree any more; the raw position is the best guess left.
                }
            }

            return e.Position;
        }

        #endregion

        #region Mouse

        private void OnPointerPressedLinux(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || _zoomFactor <= 1)
            {
                return;
            }

            var args = new CancelEventArgs();
            PanStarting?.Invoke(this, args);

            if (args.Cancel)
            {
                return;
            }

            CapturePointer(e.Pointer);

            _pointerPressed = true;
            _pointerPosition = e.GetCurrentPoint(this).Position;
        }

        private void OnPointerMovedLinux(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerPressed || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                return;
            }

            var point = e.GetCurrentPoint(this).Position;

            // PORTING.md §6: PointerMoved arrives twice for every move on X11. Dropping the second
            // one is not needed here because the delta is taken against the last position seen, not
            // accumulated: the duplicate has the same position and contributes zero.
            var deltaX = point.X - _pointerPosition.X;
            var deltaY = point.Y - _pointerPosition.Y;

            _pointerPosition = point;

            if (deltaX != 0 || deltaY != 0)
            {
                ApplyPinchLinux(1, point, new Point(point.X - deltaX, point.Y - deltaY));
            }
        }

        private void OnPointerReleasedLinux(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                return;
            }

            if (PointerCaptures?.Count > 0)
            {
                ReleasePointerCapture(e.Pointer);
            }

            _pointerPressed = false;
            _pointerPosition = default;
        }

        #endregion

        #region Diagnostics

        /// <summary>Current translation of the content, for diagnostics and for the gesture spike.</summary>
        public Point TranslationLinux => new(_translateX, _translateY);

        #endregion
    }
}
