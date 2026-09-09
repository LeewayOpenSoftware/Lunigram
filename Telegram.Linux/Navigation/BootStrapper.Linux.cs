//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Navigation.Services;
using Telegram.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Microsoft.UI.Composition;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Resources;
using LaunchActivatedEventArgs = Windows.ApplicationModel.Activation.LaunchActivatedEventArgs;

namespace Telegram.Navigation
{
    /// <summary>
    /// The Linux/Uno-Skia half of <see cref="BootStrapper"/>, beside BootStrapper.Uwp.cs and
    /// BootStrapper.Win32.cs. Upstream 12.10 moved the per-host launch path out of BootStrapper.cs
    /// into one partial per host; this is that partial for the Uno X11 head.
    ///
    /// It supplies the host-flavour members .Uwp.cs supplies (OnLaunched, OnWindowCreated,
    /// OnWindowClosed, OnClosed, CreateWindowWrapper) built on WinUI's Window instead of a
    /// CoreWindow, plus the shortcut/back plumbing this head routes itself.
    /// </summary>
    public partial class BootStrapper
    {
        // WinUI has no OnWindowCreated: the window is created in OnLaunched and handed here.
        private void OnWindowCreated(Window window)
        {
            Logger.Info();

            IsMainWindowCreated = true;

            CustomXamlResourceLoader.Current = new XamlResourceLoader();
            CreateWindowWrapper(window);
            ViewService.OnWindowCreated();

            window.Activated += OnActivated;
            window.Closed += OnClosed;
        }

        // Named apart from the shared BootStrapper.OnActivated(IActivatedEventArgs): this one is
        // the WinUI Window.Activated handler, and it carries the platform args.
        private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
        {
            if (sender is Window window)
            {
                // One window on this head, so Current is the one that was activated.
                OnWindowActivated(WindowContext.Current,
                    e.WindowActivationState != CoreWindowActivationState.Deactivated);
            }
        }

        private void OnClosed(object sender, WindowEventArgs e)
        {

            if (sender is Window window)
            {
                window.Activated -= OnActivated;
                window.Closed -= OnClosed;

                OnWindowClosed(window);
            }
        }

        protected virtual void OnWindowClosed(Window window)
        {

        }

        private WindowContext CreateWindowWrapper(Window window)
        {
            // WindowContext's constructor assigns the thread-static Current, so building a second
            // one for a view that already has one replaces it, and whatever was attached to the
            // first - the frame, most importantly - is silently orphaned. Activation can run
            // before OnWindowCreated, so the context may already exist by the time we get here.
            var context = WindowContext.Current;
            if (context != null)
            {
                if ((Window)context == window)
                {
                    Logger.Info("Window context already exists, reusing it");
                    return context;
                }

                Logger.Warning("Window context belongs to a different window, replacing it");
            }

            Logger.Info("Creating the window context");
            return new WindowContext(window);
        }

        public INavigationService NavigationService => WindowContext.Current.NavigationServices.FirstOrDefault();

        protected sealed override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var e = args.UWPLaunchActivatedEventArgs;

            Logger.Info(e.Kind);
            WatchDog.Launch(e.PreviousExecutionState);

            // The interface scale, before anything reads a scale at all. Uno's X11 display
            // information takes UNO_DISPLAY_SCALE_OVERRIDE from the environment once, in the
            // constructor it runs when the first window asks for a DisplayInformation - so this is
            // the last moment at which the 100-250% setting can still be applied, and the reason a
            // change of it only shows on the next start. See Telegram.Linux/Platform/InterfaceScale.cs.
            //
            // It comes first so the launch size below is scaled with the number the window will
            // actually use. The remembered size is in logical units, so a larger scale reopens the
            // window physically larger; the window manager clamps it to the screen.
            var appearance = AppSettings.Appearance;
            Common.InterfaceScale.Bootstrap(appearance.UseDefaultScaling ? 0 : appearance.Scaling);

            // The X11 host takes the launch size in physical pixels, so it is scaled here: asking for
            // it in logical ones lands a half-size window on a HiDPI screen, and resizing after the
            // first layout leaves the window painting its old, smaller surface.
            //
            // The size itself is the one the window was left at, in logical units - Uno never
            // updates PreferredLaunchViewSize on its own, so WindowContext.OnSizeChanged keeps it
            // (see Telegram.Linux/Platform/WindowSize.cs). PreferredLaunchViewSize is only the way
            // to hand it to X11XamlRootHost, which reads it as it creates the window.
            var scale = Common.DisplayScale.Current;
            var size = Common.WindowSize.Restore();

            ApplicationView.PreferredLaunchWindowingMode = ApplicationViewWindowingMode.PreferredLaunchViewSize;
            ApplicationView.PreferredLaunchViewSize = new Windows.Foundation.Size(size.Width * scale, size.Height * scale);

            OnWindowCreated(new Window());
            CallInternalLaunchAsync(e);
        }

        private void BackHandler(object sender, BackRequestedEventArgs args)
        {
            Logger.Info();

            //var handled = false;
            //if (ApiInformation.IsApiContractPresent(nameof(Windows.Phone.PhoneContract), 1, 0))
            //{
            //    if (NavigationService?.CanGoBack == true)
            //    {
            //        handled = true;
            //    }
            //}
            //else
            //{
            //    handled = (NavigationService?.CanGoBack == false);
            //}
            var handled = NavigationService?.CanGoBack == false;

            RaiseBackRequested(null, VirtualKey.GoBack, ref handled);
            args.Handled = handled;
        }

        public bool RaiseShortcutInvoked(InvokedShortcut shortcut, VirtualKeyModifiers modifiers)
        {
            var args = new ShortcutInvokedEventArgs(shortcut, modifiers);

            foreach (var frame in WindowContext.Current.NavigationServices.Select(x => x.FrameFacade).Reverse())
            {
                frame.RaiseShortcutInvoked(args);

                if (args.Handled)
                {
                    return true;
                }
            }

            return false;
        }

        public void RaiseBackRequested()
        {
            var handled = false;
            RaiseBackRequested(null, VirtualKey.GoBack, ref handled);
        }

        public bool RaiseBackRequested(XamlRoot xamlRoot, VirtualKey key)
        {
            var handled = false;
            RaiseBackRequested(xamlRoot, key, ref handled);
            return handled;
        }

        /// <summary>
        /// Default Hardware/Shell Back handler overrides standard Back behavior 
        /// that navigates to previous app in the app stack to instead cause a backward page navigation.
        /// Views or Viewodels can override this behavior by handling the BackRequested 
        /// event and setting the Handled property of the BackRequestedEventArgs to true.
        /// </summary>
        private void RaiseBackRequested(XamlRoot xamlRoot, VirtualKey key, ref bool handled)
        {
            Logger.Info();

            var args = new BackRequestedRoutedEventArgs(key);
            var popups = xamlRoot == null
                ? VisualTreeHelper.GetOpenPopups(WindowContext.Current)
                : VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);

            foreach (var popup in popups)
            {
                if (popup.Child is INavigablePage page)
                {
                    page.OnBackRequested(args);

                    if (handled = args.Handled)
                    {
                        return;
                    }
                }
                else if (popup.Child is ContentDialog dialog)
                {
                    dialog.Hide();
                    return;
                }
                else if (popup.Child is ToolTip toolTip)
                {
                    toolTip.IsOpen = false;
                }
                else if (popup.Child is TeachingTip teachingTip)
                {
                    if (teachingTip.IsLightDismissEnabled)
                    {
                        teachingTip.IsOpen = false;
                    }
                }
                else if (key == VirtualKey.Escape)
                {
                    // TODO: what is this for? I have no clue anymore
                    if (popup.Child is not Grid)
                    {
                        //handled = args.Handled = true;
                        return;
                    }
                }
            }

            foreach (var frame in WindowContext.Current.NavigationServices.Select(x => x.FrameFacade).Reverse())
            {
                frame.RaiseBackRequested(args);

                if (handled = args.Handled)
                {
                    return;
                }
            }

            if (NavigationService?.CanGoBack ?? false)
            {
                NavigationService?.GoBack();
                handled = true;
            }
        }

        public bool RaiseForwardRequested()
        {
            Logger.Info();

            var args = new HandledEventArgs();

            foreach (var frame in WindowContext.Current.NavigationServices.Select(x => x.FrameFacade))
            {
                frame.RaiseForwardRequested(args);
                if (args.Handled)
                {
                    return true;
                }
            }

            if (NavigationService?.CanGoForward ?? false)
            {
                NavigationService?.GoForward();
                return true;
            }

            return false;
        }
        // ---- host-flavour hooks declared partial in BootStrapper.cs -------------------------

        /// <summary>
        /// Prelaunch is a UWP app-model concept (the shell starting the app ahead of the user).
        /// Nothing on this host does that, so an activation is always a real one.
        /// </summary>
        protected partial bool IsPrelaunch(LaunchActivatedEventArgs e)
        {
            return false;
        }

        protected partial void SetApplicationTheme(ApplicationTheme theme)
        {
            RequestedTheme = theme;
        }

        /// <summary>
        /// One window, and OnLaunched has already built its context by the time any activation is
        /// dispatched, so Current is the answer. If it is not there yet the frame cannot be built
        /// and the caller is expected to handle null.
        /// </summary>
        private partial WindowContext ResolveWindowContext(IActivatedEventArgs e)
        {
            var context = WindowContext.Current;
            if (context == null)
            {
                Logger.Error($"Activated with no window context, cannot initialize the frame. Kind: {e.Kind}");
            }

            return context;
        }

    }
}
