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

namespace Telegram.Controls
{
    public partial class ZoomViewer : ContentControl
    {
#if !LINUX
        private ScrollViewer ScrollingHost;
#endif

        private bool _pointerPressed;
        private Point _pointerPosition;

        public ZoomViewer()
        {
#if LINUX
            // No DefaultStyleKey here: the <Style TargetType="local:ZoomViewer"> of Generic.xaml is
            // a win: one, so on Skia nothing registers a template for this type and Uno falls into
            // its "content presenter bypass" (Template == null and no default template), which puts
            // the Content in the visual tree directly. That is exactly what is wanted: the template
            // is a ScrollViewer whose zoom does nothing here anyway - measured, see ZoomViewer.Linux.cs.
            InitializeLinux();
#else
            DefaultStyleKey = typeof(ZoomViewer);
#endif
        }

        public event EventHandler<ScrollViewerViewChangedEventArgs> ViewChanged;

        protected override void OnApplyTemplate()
        {
#if LINUX
            base.OnApplyTemplate();
#else
            ScrollingHost = GetTemplateChild(nameof(ScrollingHost)) as ScrollViewer;
            ScrollingHost.ViewChanged += ScrollingHost_ViewChanged;

            ScrollingHost.AddHandler(PointerPressedEvent, new PointerEventHandler(ScrollingHost_PointerPressed), true);
            ScrollingHost.AddHandler(PointerMovedEvent, new PointerEventHandler(ScrollingHost_PointerMoved), true);
            ScrollingHost.AddHandler(PointerReleasedEvent, new PointerEventHandler(ScrollingHost_PointerReleased), true);
#endif
        }

#if LINUX
        public bool CanZoomIn => _zoomFactor < MaxZoomFactor;
        public bool CanZoomOut => _zoomFactor > MinZoomFactor;

        public double ZoomFactor => _zoomFactor;
#else
        public bool CanZoomIn => ScrollingHost?.ZoomFactor < MaxZoomFactor;
        public bool CanZoomOut => ScrollingHost?.ZoomFactor > MinZoomFactor;

        public double ZoomFactor => ScrollingHost?.ZoomFactor ?? 1;
#endif

        public event CancelEventHandler PanStarting;

        #region MaxZoomFactor

        public double MaxZoomFactor
        {
            get { return (double)GetValue(MaxZoomFactorProperty); }
            set { SetValue(MaxZoomFactorProperty, value); }
        }

        public static readonly DependencyProperty MaxZoomFactorProperty =
            DependencyProperty.Register("MaxZoomFactor", typeof(double), typeof(ZoomViewer), new PropertyMetadata(1d));

        #endregion

        #region MinZoomFactor

        public double MinZoomFactor
        {
            get { return (double)GetValue(MinZoomFactorProperty); }
            set { SetValue(MinZoomFactorProperty, value); }
        }

        public static readonly DependencyProperty MinZoomFactorProperty =
            DependencyProperty.Register("MinZoomFactor", typeof(double), typeof(ZoomViewer), new PropertyMetadata(1d));

        #endregion

        public void Zoom(bool zoomIn, bool disableAnimation = false)
        {
#if LINUX
            Zoom((float)(_zoomFactor + (zoomIn ? 0.25f : -0.25f)), disableAnimation);
#else
            Zoom(ScrollingHost.ZoomFactor + (zoomIn ? 0.25f : -0.25f), disableAnimation);
#endif
        }

        public void Zoom(float factor, bool disableAnimation = false)
        {
#if LINUX
            ZoomLinux(factor, disableAnimation);
#else
            if (ScrollingHost == null)
            {
                return;
            }

            if (factor <= ScrollingHost.MaxZoomFactor)
            {
                var horizontal = ScrollingHost.ViewportWidth * factor - ScrollingHost.ActualWidth;
                var vertical = ScrollingHost.ViewportHeight * factor - ScrollingHost.ActualHeight;

                if (ScrollingHost.ScrollableWidth > 0)
                {
                    horizontal *= ScrollingHost.HorizontalOffset / ScrollingHost.ScrollableWidth;
                }
                else
                {
                    horizontal /= 2;
                }

                if (ScrollingHost.ScrollableHeight > 0)
                {
                    vertical *= ScrollingHost.VerticalOffset / ScrollingHost.ScrollableHeight;
                }
                else
                {
                    vertical /= 2;
                }

                ScrollingHost.TryChangeView(horizontal, vertical, factor, disableAnimation);
            }
#endif
        }

#if !LINUX
        private void ScrollingHost_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            ViewChanged?.Invoke(this, e);
        }

        private void ScrollingHost_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                return;
            }

            var args = new CancelEventArgs();
            PanStarting?.Invoke(this, args);

            if (args.Cancel)
            {
                return;
            }

            ScrollingHost.CapturePointer(e.Pointer);

            _pointerPressed = true;
            _pointerPosition = e.GetCurrentPoint(ScrollingHost).Position;

            _pointerPosition.X += ScrollingHost.HorizontalOffset;
            _pointerPosition.Y += ScrollingHost.VerticalOffset;
        }

        private void ScrollingHost_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                return;
            }

            if (_pointerPressed)
            {
                var point = e.GetCurrentPoint(ScrollingHost);

                var diffX = _pointerPosition.X - point.Position.X;
                var diffY = _pointerPosition.Y - point.Position.Y;

                ScrollingHost.TryChangeView(diffX, diffY, null, true);
            }
        }

        private void ScrollingHost_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            {
                return;
            }

            if (ScrollingHost.PointerCaptures?.Count > 0)
            {
                ScrollingHost.ReleasePointerCapture(e.Pointer);
            }

            _pointerPressed = false;
            _pointerPosition = default;
        }
#endif
    }
}
