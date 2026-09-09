//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.CompilerServices;
using Telegram.Navigation;
using Telegram.Services;
using Windows.Foundation;
using Windows.UI.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Controls
{
    public partial class MasterDetailPanel : Panel
    {
        private const double columnCompactWidthLeft = 72;
        private const double columnMinimalWidthLeft = 260;
        private const double columnMaximalWidthLeft = 540;
        private const double columnMinimalWidthMain = 380;
        private const double kDefaultDialogsWidthRatio = 5d / 14d;

        private double gripWidthRatio = AppSettings.DialogsWidthRatio;
        private double dialogsWidthRatio = AppSettings.DialogsWidthRatio;

        private MasterDetailState _currentState = MasterDetailState.Unknown;
        public MasterDetailState CurrentState
        {
            get => _currentState;
            set
            {
                if (_currentState != value)
                {
                    _currentState = value;
                    ViewStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private bool _allowCompact = true;
        public bool AllowCompact
        {
            get => _allowCompact;
            set
            {
                if (ActualWidth >= columnMinimalWidthLeft + columnMinimalWidthMain && dialogsWidthRatio == 0 && _allowCompact != value)
                {
                    _allowCompact = value;
                    InvalidateMeasure();
                }
                else
                {
                    _allowCompact = value;
                }
            }
        }

        public bool HasMaster { get; set; } = true;

        /// <summary>
        /// Touch mode's single column: pin the panel to <see cref="MasterDetailState.Minimal"/>
        /// whatever the window measures. Minimal is the layout Unigram was born with on Windows
        /// Phone - the master fills the window and the detail sits on top of it, so a chat opens
        /// full screen with a back button - and until now the only thing that chose it was a window
        /// narrower than <c>columnMinimalWidthLeft + columnMinimalWidthMain</c>. Nothing else about
        /// the layout changes: the same branch runs, so the grip is arranged empty and the master
        /// keeps its banner offset exactly as it does in a narrow window.
        /// </summary>
        private bool _forceMinimal;
        public bool ForceMinimal
        {
            get => _forceMinimal;
            set
            {
                if (_forceMinimal != value)
                {
                    _forceMinimal = value;
                    InvalidateMeasure();
                }
            }
        }

        /// <summary>
        /// Whether the panel has to lay out as a single column: the window is too narrow for two,
        /// there is no master to show, or touch mode asked for one.
        /// </summary>
        private bool IsSingleColumn(double width)
        {
            return _forceMinimal || width < columnMinimalWidthLeft + columnMinimalWidthMain || !HasMaster;
        }

        public double ActualMasterWidth => ((FrameworkElement)Children[2]).ActualWidth;

        public double ActualDetailWidth => ((FrameworkElement)Children[1]).ActualWidth;

        private bool _registerEvents = true;

        private UIElement _banner;

        private double GetBannerDesiredHeight(FrameworkElement detail)
        {
            _banner ??= detail.FindName("BannerPresenter") as UIElement;

            if (_banner != null && _banner.DesiredSize.Height > 0)
            {
                return _banner.DesiredSize.Height + 8;
            }

            return 0;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var detail = Children[0] as FrameworkElement;
            var master = Children[1];
            var grip = Children[2] as FrameworkElement;

            if (_registerEvents)
            {
                _registerEvents = false;

                grip.PointerEntered += Grip_PointerEntered;
                grip.PointerExited += Grip_PointerExited;
                grip.PointerPressed += Grip_PointerPressed;
                grip.PointerMoved += Grip_PointerMoved;
                grip.PointerReleased += Grip_PointerReleased;
                grip.PointerCanceled += Grip_PointerReleased;
                grip.PointerCaptureLost += Grip_PointerReleased;
                grip.Unloaded += Grip_Unloaded;
            }

            // Single column mode
            if (IsSingleColumn(availableSize.Width))
            {
                detail.Measure(CreateSize(availableSize.Width, Math.Max(0, availableSize.Height)));

                var desiredHeight = GetBannerDesiredHeight(detail);
                master.Measure(CreateSize(availableSize.Width, Math.Max(0, availableSize.Height - desiredHeight)));

                grip.Measure(CreateSize(0, 0));
            }
            else
            {
                var result = 0d;
                if (dialogsWidthRatio == 0 && _allowCompact)
                {
                    result = columnCompactWidthLeft;
                }
                else
                {
                    result = dialogsWidthRatio > 0 ? CountDialogsWidthFromRatio(availableSize.Width, dialogsWidthRatio) : columnMinimalWidthLeft;
                }

                master.Measure(CreateSize(result, availableSize.Height));
                detail.Measure(CreateSize(availableSize.Width - result, availableSize.Height));

                grip.Measure(CreateSize(8, availableSize.Height));
            }

            return availableSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var detail = Children[0] as FrameworkElement;
            var master = Children[1];
            var grip = Children[2] as FrameworkElement;

            // Single column mode
            if (IsSingleColumn(finalSize.Width))
            {
                CurrentState = MasterDetailState.Minimal;

                detail.Arrange(CreateRect(0, 0, finalSize.Width, finalSize.Height));

                var desiredHeight = GetBannerDesiredHeight(detail);
                master.Arrange(CreateRect(0, desiredHeight, finalSize.Width, finalSize.Height - desiredHeight));

                grip.Arrange(CreateRect(0, 0, 0, 0));
            }
            else
            {
                double result;
                if (dialogsWidthRatio == 0 && _allowCompact)
                {
                    result = columnCompactWidthLeft;
                    CurrentState = MasterDetailState.Compact;
                }
                else
                {
                    result = dialogsWidthRatio > 0 ? CountDialogsWidthFromRatio(finalSize.Width, dialogsWidthRatio) : columnMinimalWidthLeft;
                    CurrentState = MasterDetailState.Expanded;
                }

                master.Arrange(CreateRect(0, 0, result, finalSize.Height));
                detail.Arrange(CreateRect(result, 0, finalSize.Width - result, finalSize.Height));

                grip.Arrange(CreateRect(result, 0, 8, finalSize.Height));
            }

            return finalSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Size CreateSize(double width, double height)
        {
            return new Size(Math.Max(0, width), Math.Max(0, height));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Rect CreateRect(double x, double y, double width, double height)
        {
            return new Rect(x, y, Math.Max(0, width), Math.Max(0, height));
        }

        public static double CountDialogsWidthFromRatio(double width, double ratio)
        {
            var result = Math.Round(width * ratio);
            result = Math.Max(result, columnMinimalWidthLeft);
            result = Math.Min(result, columnMaximalWidthLeft);

            return result;
        }

#if LINUX
        // No CoreWindow.PointerCursor on Uno: the cursor is set through UIElement.ProtectedCursor.
        private static readonly Microsoft.UI.Input.InputCursor _defaultCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        private static readonly Microsoft.UI.Input.InputCursor _resizeCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);

        private void SetPointerCursor(Microsoft.UI.Input.InputCursor cursor)
        {
            ProtectedCursor = cursor;
        }
#else
        private static readonly CoreCursor _defaultCursor = new(CoreCursorType.Arrow, 1);
        private static readonly CoreCursor _resizeCursor = new(CoreCursorType.SizeWestEast, 1);

        private void SetPointerCursor(CoreCursor cursor)
        {
            Window.Current.CoreWindow.PointerCursor = cursor;
        }
#endif

        private bool _pointerPressed;
        private double _pointerDelta;

        private void Grip_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            WindowContext.SetPointerCursor(PointerCursorType.SizeWestEast);
        }

        private void Grip_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerPressed)
            {
                WindowContext.SetPointerCursor(PointerCursorType.Arrow);
            }
        }

        private void Grip_Unloaded(object sender, RoutedEventArgs e)
        {
            WindowContext.SetPointerCursor(PointerCursorType.Arrow);
        }

        private void Grip_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var master = Children[1] as FrameworkElement;
            var grip = Children[2] as UserControl;

            _pointerPressed = true;
            _pointerDelta = e.GetCurrentPoint(this).Position.X - master.ActualWidth;

            VisualStateManager.GoToState(grip, "Pressed", false);

            grip.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_pointerPressed)
            {
                var master = Children[1] as FrameworkElement;
                var grip = Children[2] as UserControl;

                var point = e.GetCurrentPoint(this);

                var newWidth = point.Position.X - _pointerDelta;
                var newRatio = (newWidth < columnMinimalWidthLeft / 2)
                    ? 0
                    : newWidth / ActualWidth;

                if (newRatio == 0 && _allowCompact)
                {
                    newWidth = columnCompactWidthLeft;
                }
                else
                {
                    newWidth = CountDialogsWidthFromRatio(ActualWidth, newRatio);
                }

                grip.Arrange(new Rect(newWidth, 0, 8, ActualHeight));
                gripWidthRatio = newRatio;
            }
        }

        private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var master = Children[1] as FrameworkElement;
            var grip = Children[2] as UserControl;

            _pointerPressed = false;
            VisualStateManager.GoToState(grip, "Normal", false);

            dialogsWidthRatio = gripWidthRatio;
            AppSettings.DialogsWidthRatio = gripWidthRatio;

            InvalidateMeasure();

            grip.ReleasePointerCapture(e.Pointer);
            e.Handled = true;

            var point = e.GetCurrentPoint(grip);
            if (point.Position.X is < 0 or > 8)
            {
                WindowContext.SetPointerCursor(PointerCursorType.Arrow);
            }
        }

        public event EventHandler ViewStateChanged;
    }
}
