//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Linq;
using LinqToVisualTree;
using Telegram.Common;
using Telegram.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Telegram.Views
{
    /// <summary>
    /// Scrolls the ScrollViewer under the pointer when the wheel turns, because Uno Skia does not.
    ///
    /// <para><b>The gap.</b> The wheel is decoded and delivered perfectly on this platform -- one
    /// <c>PointerWheelChanged</c> per notch, correct sign, correct ±120 magnitude, measured with
    /// <see cref="PointerTrace"/> (u-wheel, 2026-09-05). Nothing consumes it: the event reaches the
    /// window root with <c>Handled == false</c>, which is what "the wheel scrolls nothing anywhere"
    /// actually is. Uno's <c>ScrollContentPresenter.MouseWheelUp/Down/Left/Right</c> are
    /// <c>ApiInformation.TryRaiseNotImplemented</c> stubs in 6.6.184, so no ScrollViewer ever acts
    /// on a wheel notch. This supplies the missing half.</para>
    ///
    /// <para><b>Why a handler at the root and not on ScrollViewer.</b> There is no seam on the
    /// control: <c>ScrollContentPresenter</c>'s wheel entry points are the stubs above and
    /// <c>ScrollViewer.ArePointerWheelEventsIgnored</c> is internal. Overriding
    /// <c>OnPointerWheelChanged</c> would not work either -- Uno's routed-event generator inherits
    /// the base type's flags and does not turn an event on because a subclass overrides its handler
    /// (PORTING.md 6, and the note on StoriesWindow.cs:594 that found it the hard way). One handler
    /// at the top of each tree, walking down from the pointer, is the shape that works, and it
    /// generalises the two per-control handlers that already exist for exactly this reason
    /// (StoriesWindow, CarouselViewer).</para>
    ///
    /// <para><b>Not double-handling, and why not by Handled.</b> The obvious guard -- register
    /// with <c>handledEventsToo: false</c> and let whoever claimed the notch keep it -- drops
    /// notches, because Uno hands some wheel args out <i>already</i> handled:
    /// <c>Handled = (data.EventWindow != TopX11Window.Window)</c> is set when the args are built,
    /// before any element sees them. Measured: 2 of 5 identical synthetic notches arrived
    /// pre-handled, and those are exactly the ones that scroll nothing today. So this listens with
    /// <c>handledEventsToo: true</c> and decides for itself: the walk below stops at a
    /// CarouselViewer, which is the one control inside this window that owns the wheel for its own
    /// purposes. StoriesWindow needs no such check -- it is a separate window with its own root,
    /// so its notches never reach this handler at all.</para>
    ///
    /// <para><b>Popups get their own.</b> A popup is hosted in the XamlRoot's popup layer, a
    /// different branch from the window content, so the root handler is not on its route --
    /// <c>ContentPopup</c> attaches one for itself.</para>
    /// </summary>
    public static class WheelScroll
    {
        /// <summary>
        /// Pixels per notch. WinUI calls 120 units one notch and scrolls three lines; three lines
        /// of a chat list row is about this, and it is what the trackpad on this machine produces
        /// per detent. Deliberately not the raw delta: 120 px a notch overshoots a message list.
        /// </summary>
        private const double PixelsPerNotch = 60;

        public static void Attach(WindowContext window)
        {
            if (window?.Content is UIElement content)
            {
                Attach(content);
            }
        }

        public static void Attach(UIElement root)
        {
            // handledEventsToo: true -- see the class note; Handled cannot be trusted as "somebody
            // acted on this" here, because Uno pre-sets it on the args it builds.
            root?.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);
        }

        private static void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(null);
            var delta = point.Properties.MouseWheelDelta;

            if (delta == 0)
            {
                return;
            }

            // Shift+wheel is the usual way to scroll sideways with a wheel that has one axis, and
            // a tilt wheel says so itself. KeyModifiers off the args and not WindowContext, whose
            // Windows half reads CoreWindow -- null here (PORTING.md 6, CarouselViewer.Linux.cs).
            var horizontal = point.Properties.IsHorizontalMouseWheel
                || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);

            var scroll = FindScrollable(e.OriginalSource as DependencyObject, horizontal);

            if (scroll == null)
            {
                return;
            }

            var offset = delta / 120d * PixelsPerNotch;

            if (horizontal)
            {
                // A notch "down" (negative) scrolls right, the way it does on Windows.
                ChangeView(scroll, scroll.HorizontalOffset - offset, null);
            }
            else
            {
                ChangeView(scroll, null, scroll.VerticalOffset - offset);
            }

            e.Handled = true;

            // First one only: one line per detent would be the noisiest thing in the log, and all
            // that is worth knowing is that the wire is alive and what it picked.
            if (_scrolled++ == 0)
            {
                Logger.Info($"WheelScroll: scrolling {scroll.GetType().Name} by {-offset:F0}px per notch ({(horizontal ? "horizontal" : "vertical")}); the wheel is live");
            }
        }

        private static int _scrolled;

        /// <summary>How many notches this has turned into a scroll. Read by the pointer harness.</summary>
        public static int Scrolled => _scrolled;

        private static void ChangeView(ScrollViewer scroll, double? horizontal, double? vertical)
        {
            // disableAnimation: an animated ChangeView per notch queues one animation per detent
            // and the list crawls behind the finger; the wheel is expected to be immediate.
            scroll.ChangeView(horizontal, vertical, null, true);
        }

        /// <summary>
        /// The nearest ScrollViewer at or above <paramref name="source"/> that can actually move in
        /// the requested direction. Walking past the ones that cannot is what makes a list inside a
        /// scrolling page behave: the inner one takes the wheel until it reaches its end, and then
        /// -- because it no longer has anywhere to go -- the page does.
        /// </summary>
        private static ScrollViewer FindScrollable(DependencyObject source, bool horizontal)
        {
            if (source == null)
            {
                return null;
            }

            foreach (var ancestor in source.AncestorsAndSelf())
            {
                // A CarouselViewer between the pointer and any scroller owns the wheel: it turns a
                // notch into a page change (CarouselViewer.Linux.cs). Stop rather than scroll the
                // page behind it.
                if (ancestor is Telegram.Controls.CarouselViewer)
                {
                    return null;
                }

                if (ancestor is not ScrollViewer scroll)
                {
                    continue;
                }

                if (horizontal)
                {
                    if (scroll.HorizontalScrollMode != ScrollMode.Disabled && scroll.ScrollableWidth > 0)
                    {
                        return scroll;
                    }
                }
                else if (scroll.VerticalScrollMode != ScrollMode.Disabled && scroll.ScrollableHeight > 0)
                {
                    return scroll;
                }
            }

            return null;
        }
    }
}
