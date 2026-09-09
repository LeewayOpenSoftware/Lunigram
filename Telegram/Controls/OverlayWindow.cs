//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Views.Host;
using Microsoft.UI.Input;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core.Preview;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls
{
    public abstract class OverlayWindow : ContentControl, INavigablePage
    {
        private Popup _popupHost;

        private bool _closing;

        private TaskCompletionSource<ContentDialogResult> _callback;
        private ContentDialogResult _result;

        protected Border Container;
        protected Border BackgroundElement;

        public event EventHandler Closing;

        private static readonly ConditionalWeakTable<XamlRoot, OverlayWindow> _instances = new();

        public WindowContext Window { get; }

        public OverlayWindow(XamlRoot xamlRoot)
        {
            DefaultStyleKey = typeof(OverlayWindow);

            Window = WindowContext.ForXamlRoot(xamlRoot);
            XamlRoot = xamlRoot;

            Loading += OnLoading;
            Loaded += OnLoaded;
            //FullSizeDesired = true;

            //Opened += OnOpened;
            //Closed += OnClosed;
        }

        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            var pointer = e.GetCurrentPoint(this);
            if (pointer.Properties.IsLeftButtonPressed && IsLightDismissEnabled && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            {
                OnBackRequested(new BackRequestedRoutedEventArgs());
            }

            try
            {
                base.OnPointerPressed(e);
            }
            catch
            {
                // All the remote procedure calls must be wrapped in a try-catch block
            }
        }

        private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            UpdateViewBase();
        }

        protected virtual void MaskTitleAndStatusBar(WindowContext window)
        {
            if (window.Content is IPopupHost host)
            {
                host.PopupOpened();
            }

#if !LINUX
            // ApplicationView no está implementado en Uno (PORTING.md 6): GetForCurrentView() lanza.
            // Lo que se pierde es pintar de blanco los botones de la barra de título mientras la
            // galería está encima; la barra propia de Unigram ya se oculta por IPopupHost.
            var titlebar = ApplicationView.GetForCurrentView().TitleBar;
            //titlebar.BackgroundColor = Colors.Black;
            titlebar.ForegroundColor = Colors.White;
            //titlebar.ButtonBackgroundColor = Colors.Black;
            titlebar.ButtonForegroundColor = Colors.White;
#endif
        }

        protected void UnmaskTitleAndStatusBar(WindowContext window)
        {
            if (window.Content is IPopupHost host)
            {
                host.PopupClosed();
            }

            window.UpdateTitleBar();
        }

        public async Task<ContentDialogResult> ShowAsync()
        {
            _instances.GetOrAdd(XamlRoot, this);
            Margin = new Thickness();

            if (Constants.DEBUG && DataContext is INavigable navigable && navigable.NavigationService == null)
            {
                throw new InvalidOperationException();
            }

            var previous = _callback;

            _result = ContentDialogResult.None;
            _callback = new TaskCompletionSource<ContentDialogResult>();

            if (_popupHost == null)
            {
                _popupHost = new Popup();
                _popupHost.Child = this;
                _popupHost.IsLightDismissEnabled = false;
                _popupHost.Loaded += PopupHost_Loaded;
                _popupHost.Opened += PopupHost_Opened;
                _popupHost.Closed += PopupHost_Closed;

                Unloaded += PopupHost_Unloaded;
            }

            // Cool down
            if (previous != null)
            {
                await previous.Task;
            }
            //if (Environment.TickCount - _lastHide < 500)
            //{
            //    await Task.Delay(200);
            //}

#if LINUX
            // Ni ApplicationView ni SystemNavigationManagerPreview existen en Uno, y las dos líneas
            // están en el camino de abrir la galería: sin este #if, tocar una foto lanza y no pasa
            // nada. VisibleBounds (la zona no tapada por barras del sistema) es la ventana entera
            // aquí, y el CloseRequested del sistema lo cubre el propio cierre de la ventana.
            Margin = new Thickness();
            UpdateViewBase();
#else
            _applicationView = ApplicationView.GetForCurrentView();
            OnVisibleBoundsChanged(_applicationView, null);

            SystemNavigationManagerPreview.GetForCurrentView().CloseRequested += OnCloseRequested;
#endif

            Padding = new Thickness(0, 40, 0, 0);

            Logger.Info();

            _closing = false;
            _popupHost.XamlRoot = XamlRoot;
            _popupHost.IsOpen = true;

            return await _callback.Task;
        }

        private void OnCloseRequested(object sender, SystemNavigationCloseRequestedPreviewEventArgs e)
        {
            var args = new BackRequestedRoutedEventArgs();
            OnBackRequested(args);

            e.Handled = args.Handled;
        }

        private void PopupHost_Unloaded(object sender, RoutedEventArgs e)
        {
            _callback.TrySetResult(_result);
        }

        private void PopupHost_Loading(FrameworkElement sender, object args)
        {
            // Upstream 12.10 dropped ApplicationView for XamlRoot.Changed; UpdateViewBase is what
            // both paths ultimately do.
            UpdateViewBase();
        }

        private void PopupHost_Loaded(object sender, RoutedEventArgs e)
        {
            Focus(FocusState.Programmatic);
        }

        public static bool PopupOpened(XamlRoot xamlRoot)
        {
            if (xamlRoot != null && _instances.TryGetValue(xamlRoot, out OverlayWindow window))
            {
                window.PopupOpened();
                return true;
            }

            return false;
        }

        public void PopupOpened()
        {
            UnmaskTitleAndStatusBar(Window);
        }

        public static bool PopupClosed(XamlRoot xamlRoot)
        {
            if (xamlRoot != null && _instances.TryGetValue(xamlRoot, out OverlayWindow window))
            {
                window.PopupClosed();
                return true;
            }

            return false;
        }

        public void PopupClosed()
        {
            MaskTitleAndStatusBar(Window);
        }

        private void PopupHost_Opened(object sender, object e)
        {
            MaskTitleAndStatusBar(Window);

            _popupHost.XamlRoot.Changed += OnXamlRootChanged;
            UpdateViewBase();

#if LINUX
            // No ApplicationView here, so nothing was watching the window at all: the overlay took
            // the size of the XamlRoot once, when it opened, and kept it. What that costs is
            // visible the moment the gallery puts the window full screen -- measured in the app,
            // window 1368x912 with the gallery still 1368x776, so the chat showed through under
            // the black. WindowContext already forwards Window.SizeChanged; this is that event.
            _sizeChangedHost = WindowContext.ForXamlRoot(this) ?? WindowContext.Current;

            if (_sizeChangedHost != null)
            {
                _sizeChangedHost.SizeChanged += OnSizeChanged;
            }
#endif
        }

        private void PopupHost_Closed(object sender, object e)
        {
            UnmaskTitleAndStatusBar(Window);

            //_callback.TrySetResult(_result);

            _popupHost.XamlRoot.Changed -= OnXamlRootChanged;

#if LINUX
            if (_sizeChangedHost != null)
            {
                _sizeChangedHost.SizeChanged -= OnSizeChanged;
                _sizeChangedHost = null;
            }
#endif

#if !LINUX
            SystemNavigationManagerPreview.GetForCurrentView().CloseRequested -= OnCloseRequested;
#endif
        }

        public void OnBackRequested(BackRequestedRoutedEventArgs e)
        {
            if (_closing)
            {
                e.Handled = true;
                return;
            }

            //BootStrapper.BackRequested -= OnBackRequested;
            _closing = true;
            OnBackRequestedOverride(this, e);
        }

        protected void Cancel()
        {
            _closing = false;
        }

        protected virtual void OnBackRequestedOverride(object sender, BackRequestedRoutedEventArgs e)
        {
            e.Handled = true;
            Hide(ContentDialogResult.None);
        }

        protected void Prepare()
        {
            //Margin = new Thickness(WindowContext.Current.Bounds.Width, WindowContext.Current.Bounds.Height, 0, 0);
            Closing?.Invoke(this, EventArgs.Empty);
        }

        public static void TryHide(XamlRoot xamlRoot, ContentDialogResult result)
        {
            if (xamlRoot != null && _instances.TryGetValue(xamlRoot, out OverlayWindow window))
            {
                window.TryHide(result);
            }
        }

        public void TryHide(ContentDialogResult result)
        {
            var e = new BackRequestedRoutedEventArgs();
            OnBackRequestedOverride(this, e);

            if (e.Handled)
            {
                return;
            }

            Hide(result);
        }

        public void Hide()
        {
            Hide(ContentDialogResult.None);
        }

        public void Hide(ContentDialogResult result)
        {
            if (_popupHost == null || !_popupHost.IsOpen)
            {
                return;
            }

            Logger.Info();

#if LINUX
            CloseOverlayPopups();
#endif

            _result = result;
            _popupHost.IsOpen = false;

            if (_instances.TryGetValue(XamlRoot, out OverlayWindow window))
            {
                if (window == this)
                {
                    _instances.Remove(XamlRoot);
                }
            }
        }

#if LINUX
        /// <summary>
        /// Closes whatever was opened ON TOP of this overlay, before the overlay itself goes away.
        /// </summary>
        /// <remarks>
        /// Uno's popup root is a SIBLING of Window.Content, not a child of it (PORTING.md, "un
        /// objetivo de clic vive en el popup root"). A MenuFlyout opened from inside the gallery
        /// therefore hangs off that root and NOT off this overlay's own popup, so setting
        /// _popupHost.IsOpen = false does not close it. It survives its host, with the element it
        /// was anchored to already gone, and sits over the main window swallowing every click and
        /// key press: the app looks wedged, and there is nothing on screen to dismiss because the
        /// menu itself frequently never drew.
        ///
        /// The repro this closes: open a chat image, right-click it, close the viewer, try to
        /// navigate - the window takes no input afterwards.
        ///
        /// Everything except this overlay's own host is closed, deliberately: an OverlayWindow is
        /// modal and covers the window, so anything else open at this moment was opened from
        /// inside it and has no business outliving it.
        /// </remarks>
        private void CloseOverlayPopups()
        {
            var xamlRoot = XamlRoot;
            if (xamlRoot == null)
            {
                return;
            }

            var open = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);

            // A snapshot: closing a popup takes it straight out of the live collection.
            var snapshot = new Popup[open.Count];

            for (int i = 0; i < open.Count; i++)
            {
                snapshot[i] = open[i];
            }

            var closed = 0;

            foreach (var popup in snapshot)
            {
                if (popup == null || popup == _popupHost || popup.Child == this || !popup.IsOpen)
                {
                    continue;
                }

                try
                {
                    popup.IsOpen = false;
                    closed++;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "overlay: a popup left over the window would not close");
                }
            }

            if (closed > 0)
            {
                // Only when there WAS something left over, so this line means "the wedge was about
                // to happen and this is what cleared it" rather than "an overlay closed".
                Logger.Info($"overlay: {GetType().Name} closed with {closed} popup(s) still open over the window; closed them so they do not eat the input");
            }
        }

#endif
        protected override void OnApplyTemplate()
        {
            Container = GetTemplateChild(nameof(Container)) as Border;
            BackgroundElement = GetTemplateChild(nameof(BackgroundElement)) as Border;

            UpdateViewBase();

#if LINUX
            // Uno hands out no template child here: the gallery is an OverlayWindow whose own XAML
            // is its content, and the default style that carries Container/BackgroundElement never
            // gets applied, so both come back null. Dereferencing them threw a NullReferenceException
            // out of OnApplyTemplate on every gallery open -- caught by Uno's InvokeLoadedWithTry
            // and only visible in the log, but it also skipped everything below it. What is lost
            // while they are null is light dismiss, and the gallery turns that off anyway
            // (IsLightDismissEnabled="False").
            if (Container == null || BackgroundElement == null)
            {
                Logger.Warning($"OverlayWindow has no template: Container {Container != null}, BackgroundElement {BackgroundElement != null}; light dismiss is off");
                return;
            }
#endif

            Container.Tapped += Outside_Tapped;
            BackgroundElement.Tapped += Inside_Tapped;
        }

        private void Inside_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e.OriginalSource == BackgroundElement && IsLightDismissEnabled)
            {
                Hide();
            }

            e.Handled = true;
        }

        private void Outside_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsLightDismissEnabled)
            {
                Hide();
            }
        }

        public bool IsLightDismissEnabled { get; set; }

#if LINUX
        private WindowContext _sizeChangedHost;

        // Uno brings both Windows.UI.Core.WindowSizeChangedEventArgs (via Windows.UI.Core, used
        // above for the back request) and Microsoft.UI.Xaml.WindowSizeChangedEventArgs into scope.
        private void OnSizeChanged(object sender, Navigation.WindowSizeChangedEventArgs e)
        {
            // Upstream leaves this empty because ApplicationView.VisibleBoundsChanged does the
            // work there; here it is the only thing that keeps the overlay the size of the window.
            UpdateViewBase();
        }
#else
        private void OnSizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            //UpdateViewBase();
        }
#endif

        private void OnLoading(FrameworkElement sender, object args)
        {
            if (Windows.ApplicationModel.DesignMode.DesignModeEnabled)
            {
                return;
            }

            UpdateViewBase();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Windows.ApplicationModel.DesignMode.DesignModeEnabled)
            {
                return;
            }

            UpdateViewBase();
        }

        private void UpdateViewBase()
        {
#if LINUX
            // Sin ApplicationView, el tamaño de la ventana lo da el XamlRoot. Ojo: `this` todavía no
            // tiene XamlRoot cuando ShowAsync llama aquí (el Popup se lo pasa después), por eso se
            // pregunta primero al del Popup. Sin esto, la superposición se queda del tamaño de su
            // contenido en vez de ocupar la ventana.
            var root = _popupHost?.XamlRoot ?? XamlRoot;
            if (root != null)
            {
                Width = root.Size.Width;
                Height = root.Size.Height;
            }
#else
            if (_applicationView != null)
            {
                Width = XamlRoot.Size.Width;
                Height = XamlRoot.Size.Height;
            }
#endif
        }

        #region OverlayBrush

        public Brush OverlayBrush
        {
            get => (Brush)GetValue(OverlayBrushProperty);
            set => SetValue(OverlayBrushProperty, value);
        }

        public static readonly DependencyProperty OverlayBrushProperty =
            DependencyProperty.Register("OverlayBrush", typeof(Brush), typeof(OverlayWindow), new PropertyMetadata(null));

        #endregion
    }
}
