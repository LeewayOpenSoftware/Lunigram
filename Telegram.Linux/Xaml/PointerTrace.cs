//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Views
{
    /// <summary>
    /// Logs every pointer press, release and wheel notch that reaches the window root. Off unless
    /// <c>UNIGRAM_POINTER_TRACE</c> is set; it changes no behaviour and handles nothing.
    ///
    /// <para><b>Why it exists.</b> "The wheel scrolls nothing" had a documented cause that turned
    /// out to be wrong, and it was wrong in a way no amount of reading could settle -- only a trace
    /// of what actually arrives could. It is kept because the same question will be asked again
    /// about the scrollbar and about touch.</para>
    ///
    /// <para><b>What it measured (u-wheel-pointer, 2026-09-05).</b> Five synthetic notches down
    /// then five up, over the chat list:</para>
    /// <code>
    /// pointer trace: WHEEL delta=-120 horizontal=False handled=True  device=Mouse
    /// pointer trace: WHEEL delta=-120 horizontal=False handled=False device=Mouse   (x4)
    /// pointer trace: WHEEL delta=+120 horizontal=False handled=False device=Mouse   (x5)
    /// pointer trace: PRESSED kind=LeftButtonPressed left=True ... handled=True
    /// </code>
    /// <para>So the wheel IS decoded and delivered, with the right sign, the right magnitude and
    /// one event per notch; and it arrives at the window root <b>unhandled</b>, which means no
    /// ScrollViewer between the pointer and the root consumed it. A click, by contrast, arrives
    /// already handled and navigates. The pointer pipeline is healthy; the scrolling layer is
    /// what ignores the wheel. Read <c>handled</c> at the root as "somebody below acted on this".</para>
    ///
    /// <para>This is NOT the cause PORTING.md records. That entry blames
    /// <c>X11PointerInputSource</c> leaking wheel buttons into <c>_pressedButtons</c> because
    /// <c>ProcessButtonReleasedEvent</c> returns before clearing them. The leak is real and it is
    /// <b>inert</b>: the only readers of that mask are <c>IsLeftButtonPressed</c> (bit 1),
    /// <c>IsMiddleButtonPressed</c> (bit 2) and <c>IsRightButtonPressed</c> (bit 3), while the
    /// wheel buttons set bits 4-7, which nothing reads back. Fixing it would change nothing, and
    /// the trace above shows the wheel arriving correctly with the leak in place.</para>
    /// </summary>
    public static class PointerTrace
    {
        private static bool _attached;

        public static void Attach(WindowContext window)
        {
            if (_attached
                || Environment.GetEnvironmentVariable("UNIGRAM_POINTER_TRACE") == null
                || window?.Content is not UIElement content)
            {
                return;
            }

            _attached = true;

            // handledEventsToo, or the interesting cases -- the ones something already claimed --
            // are exactly the ones that would not be logged. StoriesWindow.cs:248 uses the same
            // shape.
            content.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
            content.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
            content.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);

            Logger.Info("pointer trace: attached to the window root");
        }

        private static void OnWheel(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(null).Properties;
            Logger.Info($"pointer trace: WHEEL delta={p.MouseWheelDelta} horizontal={p.IsHorizontalMouseWheel} handled={e.Handled} device={e.Pointer.PointerDeviceType}");
        }

        private static void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(null).Properties;
            Logger.Info($"pointer trace: PRESSED kind={p.PointerUpdateKind} left={p.IsLeftButtonPressed} mid={p.IsMiddleButtonPressed} right={p.IsRightButtonPressed} wheel={p.MouseWheelDelta} handled={e.Handled} device={e.Pointer.PointerDeviceType}");
        }

        private static void OnReleased(object sender, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(null).Properties;
            Logger.Info($"pointer trace: RELEASED kind={p.PointerUpdateKind} handled={e.Handled}");
        }
    }
}
