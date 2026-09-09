//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Navigation;
using Windows.UI.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
#if LINUX
using Microsoft.UI.Xaml.Input;
#endif

namespace Telegram.Services.Keyboard
{
    public partial class InputListener
    {
        private readonly WindowContext _window;

#if LINUX
        // Uno's WinUI Window has no CoreWindow (it is null) and no AcceleratorKeyActivated:
        // the routed events on the root element (WindowControl, which survives content swaps)
        // are the closest equivalent. Attached from WindowContext.SetContent.
        private UIElement _root;
        private readonly KeyEventHandler _keyDown;
        private readonly PointerEventHandler _pointerPressed;
#endif

        public InputListener(WindowContext window)
        {
            _window = window;

#if LINUX
            _keyDown = OnKeyDown;
            _pointerPressed = OnPointerPressed;
#else
            _window.Dispatcher.AcceleratorKeyActivated += OnAcceleratorKeyActivated;
            _window.CoreWindow.PointerPressed += OnPointerPressed;
#endif
        }

#if LINUX
        /// <summary>
        /// En Windows los atajos entran por <c>CoreDispatcher.AcceleratorKeyActivated</c>, que es la
        /// fase de PREPROCESO de la ventana: se dispara para toda tecla antes que el control que
        /// tiene el foco. Aqui se enganchaban por <c>KeyDownEvent</c>, que BURBUJEA desde el
        /// elemento enfocado hacia arriba, o sea que llegaba el ultimo y solo si nadie habia puesto
        /// <c>Handled</c>. Consecuencia medida sobre la tabla por defecto: con el cursor dentro de
        /// la caja de mensaje, <c>alt+down</c>/<c>alt+up</c> (chat siguiente/anterior) y
        /// <c>Alt+Izquierda</c> (atras) los consumia el <c>TextBox</c> y el atajo no ocurria — algo
        /// que en Windows no puede pasar.
        ///
        /// Uno 6.6.184 SI levanta <c>PreviewKeyDownEvent</c>: <c>Uno.UI.Xaml.Core.InputManager.OnKey</c>
        /// hace <c>uIElement.RaiseTunnelingEvent(UIElement.PreviewKeyDownEvent, e)</c> ANTES del
        /// <c>RaiseEvent(KeyDownEvent, e)</c>, con el MISMO objeto de argumentos, y
        /// <c>UIElement.RaiseTunnelingEvent</c> recorre <c>GetAllParents().Reverse()</c>, o sea de la
        /// raiz hacia abajo. <c>WindowControl</c> es ancestro de todo lo que hay en la ventana, asi
        /// que un handler suyo de tunel corre primero — que es exactamente el orden de Windows.
        ///
        /// Se dejan LOS DOS enganches a proposito: el tunel no incluye al propio elemento enfocado,
        /// y cuando no hay nada enfocado Uno levanta los dos eventos sobre <c>RootElement</c>, que
        /// esta POR ENCIMA de <c>WindowControl</c> y por tanto no pasa por aqui en ninguna de las dos
        /// fases. El de burbuja es la red que cubre ese caso. Como Uno reutiliza la misma instancia
        /// de <c>KeyRoutedEventArgs</c> para las dos fases, <see cref="_lastKeyArgs"/> impide que una
        /// misma pulsacion se procese dos veces.
        /// </summary>
        public void Attach(UIElement root)
        {
            if (_root != null)
            {
                _root.RemoveHandler(UIElement.PreviewKeyDownEvent, _keyDown);
                _root.RemoveHandler(UIElement.KeyDownEvent, _keyDown);
                _root.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressed);
            }

            _root = root;
            _root?.AddHandler(UIElement.PreviewKeyDownEvent, _keyDown, false);
            _root?.AddHandler(UIElement.KeyDownEvent, _keyDown, false);
            _root?.AddHandler(UIElement.PointerPressedEvent, _pointerPressed, false);

            _lastKeyArgs = null;
        }

        private object _lastKeyArgs;
#endif

        public void Release()
        {
#if LINUX
            Attach(null);
#else
            _window.Dispatcher.AcceleratorKeyActivated -= OnAcceleratorKeyActivated;
            _window.CoreWindow.PointerPressed -= OnPointerPressed;
#endif
        }

#if LINUX
        private void OnKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Handled)
            {
                return;
            }

            // La misma pulsacion llega aqui dos veces: por el tunel (fase de preproceso, primero) y
            // por la burbuja (despues). Ver el comentario de Attach: los dos enganches son a
            // proposito, pero solo el primero que llegue debe procesarse.
            if (ReferenceEquals(_lastKeyArgs, args))
            {
                return;
            }

            _lastKeyArgs = args;

            if (args.Key is VirtualKey.GoBack
                         or VirtualKey.NavigationLeft
                         or VirtualKey.GamepadLeftShoulder
                         or VirtualKey.Escape)
            {
                args.Handled = BootStrapper.Current.RaiseBackRequested(null, args.Key);
            }
            else if (args.Key is VirtualKey.GoForward
                              or VirtualKey.NavigationRight
                              or VirtualKey.GamepadRightShoulder)
            {
                args.Handled = BootStrapper.Current.RaiseForwardRequested();
            }
            else if (args.Key is VirtualKey.Back
                              or VirtualKey.Left)
            {
                var modifiers = WindowContext.KeyModifiers();
                if (modifiers == VirtualKeyModifiers.Menu)
                {
                    args.Handled = BootStrapper.Current.RaiseBackRequested(null, args.Key);
                }
            }
            else if (args.Key is VirtualKey.Right)
            {
                var modifiers = WindowContext.KeyModifiers();
                if (modifiers == VirtualKeyModifiers.Menu)
                {
                    args.Handled = BootStrapper.Current.RaiseForwardRequested();
                }
            }
            else
            {
                // Shortcuts are a per-session lazy service on Linux (LifetimeService has no Shortcuts).
                var shortcuts = LifetimeService.Current.ActiveItem?.Resolve<IShortcutsService>();
                if (shortcuts == null)
                {
                    return;
                }

                var invoked = shortcuts.Process(args.Key, out VirtualKeyModifiers modifiers);
                if (invoked != null)
                {
                    args.Handled = BootStrapper.Current.RaiseShortcutInvoked(invoked, modifiers);
                }
            }
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(null).Properties;

            // Ignore button chords with the left, right, and middle buttons
            if (properties.IsLeftButtonPressed || properties.IsRightButtonPressed ||
                properties.IsMiddleButtonPressed)
            {
                return;
            }

            // If back or forward are pressed (but not both) navigate appropriately
            bool backPressed = properties.IsXButton1Pressed;
            bool forwardPressed = properties.IsXButton2Pressed;
            if (backPressed ^ forwardPressed)
            {
                e.Handled = true;
                if (backPressed)
                {
                    BootStrapper.Current.RaiseBackRequested();
                }

                if (forwardPressed)
                {
                    BootStrapper.Current.RaiseForwardRequested();
                }
            }
        }
#else
        private void OnAcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args.EventType is not CoreAcceleratorKeyEventType.KeyDown and not CoreAcceleratorKeyEventType.SystemKeyDown || args.Handled)
            {
                return;
            }

            if (args.VirtualKey is VirtualKey.GoBack
                                or VirtualKey.NavigationLeft
                                or VirtualKey.GamepadLeftShoulder
                                or VirtualKey.Escape)
            {
                args.Handled = _window.RaiseBackRequested(args.VirtualKey);
            }
            else if (args.VirtualKey is VirtualKey.GoForward
                                     or VirtualKey.NavigationRight
                                     or VirtualKey.GamepadRightShoulder)
            {
                args.Handled = _window.RaiseForwardRequested();
            }
            else if (args.VirtualKey is VirtualKey.Back
                                     or VirtualKey.Left)
            {
                var modifiers = WindowContext.KeyModifiers();
                if (modifiers == VirtualKeyModifiers.Menu)
                {
                    args.Handled = _window.RaiseBackRequested(args.VirtualKey);
                }
            }
            else if (args.VirtualKey is VirtualKey.Right)
            {
                var modifiers = WindowContext.KeyModifiers();
                if (modifiers == VirtualKeyModifiers.Menu)
                {
                    args.Handled = _window.RaiseForwardRequested();
                }
            }
            else
            {
                var invoked = LifetimeService.Current.Shortcuts.Process(args, out VirtualKeyModifiers modifiers);
                if (invoked != null)
                {
                    args.Handled = _window.RaiseShortcutInvoked(invoked, modifiers);
                }
            }
        }
#endif

        /// <summary>
        /// Invoked on every mouse click, touch screen tap, or equivalent interaction when this
        /// page is active and occupies the entire window.  Used to detect browser-style next and
        /// previous mouse button clicks to navigate between pages.
        /// </summary>
        /// <param name="sender">Instance that triggered the event.</param>
        /// <param name="e">Event data describing the conditions that led to the event.</param>
#if !LINUX
        private void OnPointerPressed(CoreWindow sender, PointerEventArgs e)
        {
            var properties = e.CurrentPoint.Properties;

            // Ignore button chords with the left, right, and middle buttons
            if (properties.IsLeftButtonPressed || properties.IsRightButtonPressed ||
                properties.IsMiddleButtonPressed)
            {
                return;
            }

            // If back or forward are pressed (but not both) navigate appropriately
            bool backPressed = properties.IsXButton1Pressed;
            bool forwardPressed = properties.IsXButton2Pressed;
            if (backPressed ^ forwardPressed)
            {
                e.Handled = true;
                if (backPressed)
                {
                    _window.RaiseBackRequested();
                }

                if (forwardPressed)
                {
                    _window.RaiseForwardRequested();
                }
            }
        }
#endif

        public static bool IsPointerGoBackGesture(PointerPoint point)
        {
            var properties = point.Properties;

            // Ignore button chords with the left, right, and middle buttons
            if (properties.IsLeftButtonPressed || properties.IsRightButtonPressed ||
                properties.IsMiddleButtonPressed)
            {
                return false;
            }

            // If back or forward are pressed (but not both) navigate appropriately
            bool backPressed = properties.IsXButton1Pressed;
            return backPressed;
        }
    }
}
