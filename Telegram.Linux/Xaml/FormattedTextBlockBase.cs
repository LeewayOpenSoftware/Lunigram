//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Native.Controls
{
    // Managed version of Telegram.Native/Controls/FormattedTextBlockBase.cpp
    public partial class FormattedTextBlockBase : Control
    {
        // UISettings.DoubleClickTime is not implemented on Uno Skia
        private const long DoubleClickTime = 500;

        // PointerDeviceType.Mouse in both Windows.Devices.Input and Microsoft.UI.Input
        private const int MousePointerDeviceType = 2;

        private bool _loaded;
        private bool _unloaded;

        private RichTextBlock _textBlock;

        private FrameworkElement _layoutUpdatedTarget;
        private FrameworkElement _effectiveViewportTarget;

        private long _expandSelectionDeadline;

        public FormattedTextBlockBase()
        {
            Loaded += OnLoadedChanged;
            Unloaded += OnUnloadedChanged;
        }

        public bool IsConnected => _loaded;

        public bool IsDisconnected => _unloaded;

        protected virtual void OnLoaded()
        {

        }

        protected virtual void OnUnloaded()
        {

        }

        protected virtual void OnLayoutUpdated()
        {

        }

        protected virtual void OnViewportChanged(double left, double top, double right, double bottom)
        {

        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_textBlock != null)
            {
                _textBlock.LostFocus -= HandleLostFocus;
                _textBlock.SizeChanged -= HandleSizeChanged;
                _textBlock.ContextMenuOpening -= HandleContextMenuOpening;
                _textBlock.RemoveHandler(DoubleTappedEvent, new DoubleTappedEventHandler(HandleDoubleTapped));
                _textBlock.RemoveHandler(TappedEvent, new TappedEventHandler(HandleTapped));
            }

            if (GetTemplateChild("TextBlock") is RichTextBlock textBlock)
            {
                _textBlock = textBlock;
                _textBlock.LostFocus += HandleLostFocus;
                _textBlock.SizeChanged += HandleSizeChanged;
                _textBlock.ContextMenuOpening += HandleContextMenuOpening;
                _textBlock.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(HandleDoubleTapped), true);
                _textBlock.AddHandler(TappedEvent, new TappedEventHandler(HandleTapped), true);
            }
            else
            {
                _textBlock = null;
            }
        }

        public void RegisterLayoutChanged()
        {
            if (_layoutUpdatedTarget == null)
            {
                _layoutUpdatedTarget = _textBlock ?? (FrameworkElement)this;
                _layoutUpdatedTarget.LayoutUpdated += HandleLayoutUpdated;
            }
        }

        public void RegisterViewportChanged()
        {
            if (_effectiveViewportTarget == null)
            {
                _effectiveViewportTarget = _textBlock ?? (FrameworkElement)this;
                _effectiveViewportTarget.EffectiveViewportChanged += HandleEffectiveViewportChanged;
            }
        }

        public void UnregisterViewportChanged()
        {
            if (_effectiveViewportTarget != null)
            {
                _effectiveViewportTarget.EffectiveViewportChanged -= HandleEffectiveViewportChanged;
                _effectiveViewportTarget = null;
            }
        }

        private void UnregisterLayoutChanged()
        {
            if (_layoutUpdatedTarget != null)
            {
                _layoutUpdatedTarget.LayoutUpdated -= HandleLayoutUpdated;
                _layoutUpdatedTarget = null;
            }
        }

        private void OnLoadedChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded)
            {
                _loaded = true;
                _unloaded = false;
                OnLoaded();
            }
        }

        private void OnUnloadedChanged(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                _loaded = false;
                _unloaded = true;
                OnUnloaded();
            }
        }

        private void HandleLostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                _textBlock.Select(_textBlock.ContentStart, _textBlock.ContentStart);
            }
            catch
            {
                // Selection APIs are not implemented on every Uno backend
            }
        }

        private void HandleSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UnregisterLayoutChanged();
            OnLayoutUpdated();
        }

        private void HandleContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
        }

        private void HandleDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((int)e.PointerDeviceType == MousePointerDeviceType)
            {
                _expandSelectionDeadline = Environment.TickCount64 + DoubleClickTime;
            }
        }

        private void HandleTapped(object sender, TappedRoutedEventArgs e)
        {
            // If a double tap is followed by a single tap, then it's a triple tap (duh)
            if ((int)e.PointerDeviceType == MousePointerDeviceType && Environment.TickCount64 < _expandSelectionDeadline)
            {
                _expandSelectionDeadline = Environment.TickCount64 + DoubleClickTime;
                DispatcherQueue.TryEnqueue(ExpandSelection);
            }
        }

        private static DependencyObject FindParent(DependencyObject obj)
        {
            if (obj == null)
            {
                return null;
            }

            if (obj is RichTextBlock || obj is Paragraph)
            {
                return obj;
            }
            else if (obj is TextElement element)
            {
                return FindParent(element.ElementStart?.Parent);
            }

            return null;
        }

        private void ExpandSelection()
        {
            try
            {
                var textBlock = _textBlock;
                if (textBlock == null || textBlock.SelectionStart == null || textBlock.SelectionEnd == null)
                {
                    return;
                }

                var startBlock = FindParent(textBlock.SelectionStart.Parent);
                var endBlock = FindParent(textBlock.SelectionEnd.Parent);

                if (startBlock == endBlock && startBlock != null)
                {
                    if (startBlock is TextElement element)
                    {
                        textBlock.Select(element.ContentStart, element.ContentEnd);
                    }
                    else if (startBlock is RichTextBlock block)
                    {
                        textBlock.Select(block.ContentStart, block.ContentEnd);
                    }
                }
            }
            catch
            {
                // Selection APIs are not implemented on every Uno backend
            }
        }

        private void HandleLayoutUpdated(object sender, object e)
        {
            UnregisterLayoutChanged();
            OnLayoutUpdated();
        }

        private void HandleEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            var viewport = args.EffectiveViewport;
            OnViewportChanged(viewport.X, viewport.Y, viewport.X + viewport.Width, viewport.Y + viewport.Height);
        }
    }
}
