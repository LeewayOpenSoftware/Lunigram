//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Chats;
using Telegram.Native;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Services.Keyboard;
using Telegram.Td.Api;
using Telegram.Views;
using Telegram.Views.Authorization;
using Telegram.Views.Calls;
using Telegram.Views.Host;
using Telegram.Views.Popups;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Navigation
{
    /// <summary>
    /// The Linux/Uno-Skia half of <see cref="WindowContext"/>, the third host flavour beside
    /// WindowContext.Uwp.cs and WindowContext.Win32.cs. Upstream 12.10 split the per-host members
    /// out of WindowContext.cs into one partial per host; this file is that partial for the Uno
    /// X11 head, and it replaces the ~30 "#if LINUX" fences the same code used to live behind.
    ///
    /// Only ONE of the three partials is ever compiled: Telegram.Linux.csproj lists the shared
    /// Telegram/ sources explicitly and simply never names WindowContext.Uwp.cs, while everything
    /// under Telegram.Linux/ is picked up by SDK globbing.
    ///
    /// The members here mirror what WindowContext.Uwp.cs supplies (a Window/CoreWindow/
    /// ApplicationView story) built instead on Uno's WinUI Window + AppWindow, plus the handful
    /// this head needs that no Windows host does (Show/Hide, ApplyContentMaterial, CloseOpenChats).
    /// </summary>
    public partial class WindowContext
    {
        private readonly Window _window;

        private bool _consolidated;

        public static implicit operator Window(WindowContext window) => window._window;

        public CoreWindow CoreWindow => _window.CoreWindow;

        private string _persistedId;

        public Microsoft.UI.Composition.Compositor Compositor => _window.Compositor;

        public WindowContext(Window window)
        {
            _window = window;
            _current = this;

            Dispatcher = new DispatcherContext(window.DispatcherQueue);
            Id = (int)window.AppWindow.Id.Value;

            // Without a title the frame shows an empty caption and the window lists as unnamed.
            window.AppWindow.Title = "Unigram";
            Bounds = window.Bounds;

            var scaling = AppSettings.Appearance.Scaling;
            if (scaling is >= 100 and <= 250 && !AppSettings.Appearance.UseDefaultScaling)
            {
                NativeUtils.OverrideScaleForCurrentView(scaling);
            }

            // One window: the first context is the main view.
            if (Main == null)
            {
                Main = this;
                IsInMainView = true;
            }

            lock (_allLock)
            {
                All.Add(this);
            }

            _inputListener = new InputListener(this);

            window.Activated += OnActivated;
            window.VisibilityChanged += OnVisibilityChanged;
            window.SizeChanged += OnSizeChanged;
            window.Closed += OnClosed;
            // "Close to tray". On Windows the process outlives its window because Telegram.Stub
            // holds it up; here there is no second process, so the window is hidden instead of
            // closed and the tray icon is what brings it back. DesktopIntegration answers false
            // when there is no tray icon to bring it back FROM, and then the close proceeds.
            if (window.AppWindow != null)
            {
                window.AppWindow.Closing += OnAppWindowClosing;
            }

            #region Legacy code

            SetPreferredMinSize(WindowSize.Minimum);
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;

            UpdateTitleBar();

            #endregion

            if (LifetimeService.Current.Passcode.IsLockscreenRequired)
            {
                Lock(true);
            }

            ApplicationView.GetForCurrentView().VisibleBoundsChanged += OnVisibleBoundsChanged;
            ApplicationView.GetForCurrentView().Consolidated += OnConsolidated;
        }

        /// <summary>
        /// Applies the smallest size the window may be dragged to.
        /// </summary>
        /// <remarks>
        /// Uno's Skia ApplicationView.SetPreferredMinSize is implemented, unlike the reference
        /// assembly's: it forwards to <see cref="OverlappedPresenter.PreferredMinimumWidth"/> and
        /// <see cref="OverlappedPresenter.PreferredMinimumHeight"/>, which the X11 host writes into
        /// the window's WM_NORMAL_HINTS as PMinSize. Those are physical pixels though, and the
        /// minimum here is in logical units, so it is scaled the same way BootStrapper scales the
        /// launch size - otherwise the window can be dragged down to half the intended minimum on
        /// a HiDPI screen. The presenter is set directly because the window has one by now (it is
        /// created together with the native window, before OnWindowCreated hands the window over),
        /// and going through ApplicationView would silently do nothing if it did not.
        /// </remarks>
        private void SetPreferredMinSize(Size minSize)
        {
            var scale = DisplayScale.Current;

            var width = (int)Math.Ceiling(minSize.Width * scale);
            var height = (int)Math.Ceiling(minSize.Height * scale);

            try
            {
                if (_window.AppWindow?.Presenter is OverlappedPresenter presenter)
                {
                    presenter.PreferredMinimumWidth = width;
                    presenter.PreferredMinimumHeight = height;
                }
                else
                {
                    ApplicationView.GetForCurrentView().SetPreferredMinSize(new Size(width, height));
                }
            }
            catch (Exception ex)
            {
                // A window with no minimum size is still a usable window.
                Logger.Error(ex.ToString());
            }
        }

        private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            // BEFORE hiding, not after showing: the window is about to leave the screen with
            // whatever was on it, and it comes back exactly as it left. Clearing on the way out
            // means the conversation is never painted for the person who reopens it; clearing on
            // the way in would show it first and take it away.
            CloseOpenChats();

            if (Telegram.Services.DesktopIntegration.TryHideWindow(this))
            {
                Logger.Info("Close to tray");
                args.Cancel = true;
            }
        }

        /// <summary>
        /// Leaves the detail pane empty when the window goes to the tray.
        /// </summary>
        /// <remarks>
        /// Closing the window on this port does not end the process, it hides it, so the window
        /// that comes back from the tray is the same one that went in - still showing the last
        /// conversation. To whoever reopens it that is indistinguishable from starting the app,
        /// and it puts somebody's chat on screen without them asking for it. A cold start is
        /// already clean (the detail frame opens on BlankPage), so this is the one path that
        /// leaks.
        ///
        /// The back stacks go too: leaving them would put the chat one Back press away, which is
        /// the same exposure with an extra step.
        /// </remarks>
        private void CloseOpenChats()
        {
            foreach (var service in NavigationServices)
            {
                var type = service?.CurrentPageType;
                if (type != typeof(Views.ChatPage) && type != typeof(Views.ChatScheduledPage))
                {
                    continue;
                }

                try
                {
                    service.Navigate(typeof(Views.BlankPage), Guid.NewGuid());
                    service.Frame.BackStack.Clear();
                    service.Frame.ForwardStack.Clear();

                    Logger.Info($"Close to tray: cleared the open {type.Name} so it is not on screen when the window comes back");
                }
                catch (Exception ex)
                {
                    // Never block the close over this: a window that will not hide is worse than
                    // one that hides with a chat still in it, and the failure is visible here.
                    Logger.Error(ex, "Close to tray: could not clear the open chat");
                }
            }
        }

        /// <summary>
        /// Hides the window without closing it, for "close to tray". False means the platform did
        /// not give us a window to hide, and the caller has to let the close happen.
        /// </summary>
        public bool Hide()
        {
            var appWindow = _window.AppWindow;
            if (appWindow == null)
            {
                return false;
            }

            appWindow.Hide();

            // And then the window is still there: measured on Uno 6.6.184 / X11, AppWindow.Hide()
            // returns without throwing and leaves the window mapped, focused and on screen, which
            // is a close button that does nothing. X11Window does the ICCCM withdraw itself; if it
            // cannot find the window it answers false and the close is allowed to go through,
            // because a window that neither closes nor hides is worse than one that closes.
            return X11Window.Hide();
        }

        /// <summary>Undoes <see cref="Hide"/>. Harmless on a window that is already visible.</summary>
        public void Show()
        {
            _window.AppWindow?.Show();
            X11Window.Show();
        }

        private void OnVisibleBoundsChanged(ApplicationView sender, object args)
        {
            Logger.Debug(sender.VisibleBounds);
        }

        public async Task ConsolidateAsync()
        {
            if (_consolidated)
            {
                return;
            }

            // ApplicationView.TryConsolidateAsync is not implemented by Uno: close the window.
            if (IsInMainView)
            {
                // There is only one native window, so a call/live-stream/etc. opened via
                // ViewService.OpenAsync is swapped into RootPage rather than given its own
                // WindowContext (see ViewService.Linux.OpenAsync + RootPage.PresentContent).
                // Closing it therefore means dismissing that presented content, not closing
                // the main window itself — do that instead of silently no-op'ing (which used
                // to leave e.g. VoipWindow's "call ended" screen on screen forever).
                if (Content is RootPage root && root.HasPresentedContent)
                {
                    root.PresentContent(null);
                }

                return;
            }

            _consolidated = true;

            OnClosed(null, null);
            _window.Close();
        }

        private void OnConsolidated(ApplicationView sender, ApplicationViewConsolidatedEventArgs args)
        {
            if (IsInMainView)
            {
                return;
            }

            _consolidated = true;
            _inputListener.Release();
            sender.VisibleBoundsChanged -= OnVisibleBoundsChanged;
            sender.Consolidated -= OnConsolidated;

            // TODO: since we can't call Close directly,
            // Closed event will be never fired.
            OnClosed(null, null);
            ClearTitleBar(sender);

            // Unroot the tree here rather than leaving it to the framework: until the content is
            // dropped every element in it is still reachable, so the collect in OnShutdownStarting
            // would have nothing to hand back and the releases would fall past the XAML core.
            _window.Content = null;

        }

        private void OnClosed(object sender, WindowEventArgs e)
        {
            lock (_allLock)
            {
                if (_xamlRoot != null)
                {
                    _mapping.Remove(_xamlRoot);
                }

                All.Remove(this);
            }

            NavigationServices.ForEach(x => x.Suspend());
            NavigationServices.Clear();

            _content = null;

            _window.Activated -= OnActivated;
            _window.VisibilityChanged -= OnVisibilityChanged;
            _window.SizeChanged -= OnSizeChanged;
            _window.Closed -= OnClosed;
            if (_window.AppWindow != null)
            {
                _window.AppWindow.Closing -= OnAppWindowClosing;
            }
        }

        /// <summary>
        /// Swaps the window's content, handing focus over rather than letting it fall away with the
        /// outgoing tree.
        /// </summary>
        /// <remarks>
        /// Focus has to leave the outgoing tree before it is detached, or the focus manager goes on
        /// pointing at an element that is no longer in the tree, and XAML can never resolve a peer
        /// for it again: every later GetFocusedElement fails with E_FAIL, including its own, from
        /// ContentDialog.ChangeVisualState and ListViewBase.FocusItem where no try-catch of ours can
        /// reach. WindowControl is the one element that survives the swap, and it only takes focus
        /// as a tab stop.
        ///
        /// Both moves are awaited because <see cref="Control.Focus"/> answers before the pipeline
        /// has run and reports success even when a LosingFocus handler cancels the move underneath
        /// it - which is what the passcode field does. The tab stop is given up only once the
        /// incoming tree has taken focus, since clearing it drops whatever the root is holding.
        /// </remarks>
        private async Task SetContentAsync(UIElement content)
        {
            if (_content == null)
            {
                SetContent(content);
                return;
            }

            _content.IsTabStop = true;

            if (!await TryFocusAsync(_content))
            {
                Logger.Warning("Focus did not leave the outgoing content");
            }

            _content.Content = content;

            if (content is Control control)
            {
                await TryFocusAsync(control);
            }

            _content.IsTabStop = false;

            ApplyContentMaterial(content);
        }

        private static async Task<bool> TryFocusAsync(DependencyObject element)
        {
            try
            {
                var result = await FocusManager.TryFocusAsync(element, FocusState.Programmatic);
                return result.Succeeded;
            }
            catch
            {
                // Never worth stranding a caller mid-swap over: the content still has to change.
                return false;
            }
        }

        private void ApplyContentMaterial(UIElement content)
        {
            if (!_contentMaterial && content is RootPage)
            {
                _contentMaterial = true;
                BackdropMaterial.SetApplyToRootOrPageBackground(_content, true);
            }
        }

        private void OnLoading(FrameworkElement sender, object args)
        {
            sender.Loading -= OnLoading;

            lock (_allLock)
            {
                _xamlRoot = sender.XamlRoot;
                _mapping.AddOrUpdate(sender.XamlRoot, this);
            }
        }

        // Uno's WinUI Window has no CoreWindow: the mode is tracked from Window.Activated.
        public CoreWindowActivationMode ActivationMode { get; private set; } = CoreWindowActivationMode.ActivatedInForeground;

        // Upstream 12.10 wraps the platform args in its own Telegram.Navigation event-arg types,
        // so the handler takes the platform one (fully qualified: the enclosing namespace now
        // declares a WindowActivatedEventArgs of its own, which beats any using-alias) and raises
        // the wrapper, exactly as WindowContext.Uwp.cs does.
        private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
        {
            Activated?.Invoke(this, new WindowActivatedEventArgs(e.WindowActivationState != CoreWindowActivationState.Deactivated));

            lock (_activeLock)
            {
                // Uno's WindowActivatedEventArgs.WindowActivationState is a CoreWindowActivationState.
                ActivationMode = e.WindowActivationState == CoreWindowActivationState.Deactivated
                    ? CoreWindowActivationMode.Deactivated
                    : CoreWindowActivationMode.ActivatedInForeground;

                if (e.WindowActivationState != CoreWindowActivationState.Deactivated)
                {
                    Active = this;
                }
                else if (Active == this)
                {
                    Active = null;
                }
            }
        }

        private void OnVisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            VisibilityChanged?.Invoke(this, new WindowVisibilityEventArgs(e.Visible));
        }

        private void OnSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs e)
        {
            Bounds = _window.Bounds;
            // Windows updates ApplicationView.PreferredLaunchViewSize as the user drags the window;
            // Uno never does, so the size is remembered here. Bounds is already in logical units -
            // the X11 host divides the configured size by the XamlRoot's rasterization scale before
            // it publishes it - which is what WindowSize stores.
            if (IsInMainView)
            {
                WindowSize.Save(new Size(Bounds.Width, Bounds.Height));
            }
            SizeChanged?.Invoke(this, new WindowSizeChangedEventArgs(e.Size));
        }

        private void OnResizeStarted(CoreWindow sender, object args)
        {
            Logger.Debug(sender.Bounds);
            Bounds = sender.Bounds;

            if (AppSettings.Diagnostics.WindowResizeDebug)
            {
                return;
            }

            if (_window.Content is FrameworkElement element)
            {
                element.Width = sender.Bounds.Width;
                element.Height = sender.Bounds.Height;
                element.HorizontalAlignment = HorizontalAlignment.Left;
                element.VerticalAlignment = VerticalAlignment.Top;
            }
        }

        private void OnResizeCompleted(CoreWindow sender, object args)
        {
            Logger.Debug(sender.Bounds);
            Bounds = sender.Bounds;

            if (AppSettings.Diagnostics.WindowResizeDebug)
            {
                return;
            }

            if (_window.Content is FrameworkElement element)
            {
                element.Width = double.NaN;
                element.Height = double.NaN;
                element.HorizontalAlignment = HorizontalAlignment.Stretch;
                element.VerticalAlignment = VerticalAlignment.Stretch;
            }
        }

        public Rect Bounds { get; private set; }

        public void SetTitleBar(UIElement element)
        {
            _window.SetTitleBar(element);
        }

        public bool IsFullScreenMode => ApplicationView.GetForCurrentView().IsFullScreenMode;

        public void ExitFullScreenMode()
        {
            ApplicationView.GetForCurrentView().ExitFullScreenMode();
        }

        // u-groupcall-compile-clean: returns bool, matching the WinUI shape. Uno's
        // ApplicationView.TryEnterFullScreenMode() already returns bool (verified in
        // Uno.dll); this wrapper was swallowing it, so callers written as
        // `else if (Window.TryEnterFullScreenMode())` -- LiveStreamWindow:268 -- got
        // CS0029 "cannot convert void to bool". Existing statement-position callers
        // (GalleryWindow:524) are unaffected.
        public bool TryEnterFullScreenMode()
        {
            return ApplicationView.GetForCurrentView().TryEnterFullScreenMode();
        }

        public void SetTitleBar(UIElement titleBar, bool collapsed = false)
        {
            _window.SetTitleBar(titleBar);

            if (collapsed)
            {
                // Uno draws only the client area: there is no system title bar to hide.
            }
        }


        /// <summary>
        /// Update the Title and Status Bars colors.
        /// </summary>
        public void UpdateTitleBar()
        {
            // Nothing here would reach the X11 frame: every ApplicationViewTitleBar colour is a
            // NotImplemented stub on Uno Skia (it logs a warning and returns), and the title bar
            // itself belongs to the window manager. So only the one flag with a meaning is set.
            //
            // It is set to false because extending has to go through Window.ExtendsContentIntoTitleBar,
            // which the X11 host answers by dropping the window's _MOTIF_WM_HINTS decorations, and
            // the caption Unigram draws in their place is Windows-only (Telegram.Native.Controls):
            // there would be no drag or resize region left, and no frame to grab either. Until that
            // caption exists here, the desktop draws the frame.
            CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBar = false;
        }

        private void ClearTitleBar(ApplicationView view)
        {
        }

        public static bool IsKeyDown(VirtualKey key)
        {
            //return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            return (KeyState.Current.GetAsyncKeyState(key) & CoreVirtualKeyStates.Down) != 0;
        }

        public static bool IsKeyDownAsync(VirtualKey key)
        {
            //return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            return (KeyState.Current.GetAsyncKeyState(key) & CoreVirtualKeyStates.Down) != 0;
        }

        public static VirtualKeyModifiers KeyModifiers()
        {
            //return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

            var modifiers = VirtualKeyModifiers.None;
            var coreWindow = KeyState.Current;

            if ((coreWindow.GetAsyncKeyState(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0)
            {
                modifiers |= VirtualKeyModifiers.Control;
            }

            if ((coreWindow.GetAsyncKeyState(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0)
            {
                modifiers |= VirtualKeyModifiers.Menu;
            }

            if ((coreWindow.GetAsyncKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0)
            {
                modifiers |= VirtualKeyModifiers.Shift;
            }

            return modifiers;
        }

        public static bool KeyModifiers(VirtualKeyModifiers compare)
        {
            //return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

            var modifiers = VirtualKeyModifiers.None;
            var coreWindow = KeyState.Current;

            if ((coreWindow.GetAsyncKeyState(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0)
            {
                modifiers |= VirtualKeyModifiers.Control;
            }

            if ((coreWindow.GetAsyncKeyState(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0)
            {
                modifiers |= VirtualKeyModifiers.Menu;
            }

            if ((coreWindow.GetAsyncKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0)
            {
                modifiers |= VirtualKeyModifiers.Shift;
            }

            return modifiers == compare;
        }
        // ---- the rest of the host-flavour contract WindowContext.Uwp.cs declares ------------

        [ThreadStatic]
        private static WindowContext _current;

        public static WindowContext Current
        {
            get
            {
                if (_current == null)
                {
                    Logger.Info(Environment.StackTrace);
                }

                return _current;
            }
        }

        public bool IsActive => ActivationMode != CoreWindowActivationMode.Deactivated;

        /// <summary>
        /// The window is the foreground one. Distinct from <see cref="IsActive"/>: a window can be
        /// activated without being in the foreground.
        /// </summary>
        public bool IsForeground => ActivationMode == CoreWindowActivationMode.ActivatedInForeground;

        public void Close()
        {
            _ = ConsolidateAsync();
        }

        /// <summary>
        /// Uno draws only the client area, so nothing of the window is obscured by system chrome
        /// and the visible bounds are the bounds.
        /// </summary>
        public Rect VisibleBounds => Bounds;

        /// <summary>
        /// Uno draws the caption itself on this host - there are no system buttons to hide behind.
        /// </summary>
        public static bool HasSystemCaptionButtons => false;

        public bool TryResizeView(Size size)
        {
            var window = _window?.AppWindow;
            if (window == null)
            {
                return false;
            }

            window.Resize(new Windows.Graphics.SizeInt32 { Width = (int)size.Width, Height = (int)size.Height });
            return true;
        }

        /// <summary>
        /// One window, so there is never another view to switch to.
        /// </summary>
        public IAsyncAction SwitchToAsync()
        {
            return Task.CompletedTask.AsAsyncAction();
        }

        /// <summary>
        /// WinRT interop that hands a picker the owning HWND. Uno drives its own pickers and needs
        /// no association, so this is deliberately nothing.
        /// </summary>
        internal static void InitializeWithWindow(object target, XamlRoot xamlRoot)
        {
        }

        /// <summary>
        /// DEGRADED: Uno's Window has no CoreWindow, so this host cannot report a screen-relative
        /// pointer position. The one caller (Extensions.TransformToPointerPosition) subtracts
        /// Bounds from it, so returning the window origin places things at the top-left corner
        /// rather than under the pointer. Tracked as a follow-up, not a build blocker.
        /// </summary>
        public Point PointerPosition => default;

        private void OnShutdownCompleted(DispatcherQueue sender, object args)
        {
            _consolidated = true;
        }

        // Uno has no per-window key state; InputKeyboardSource tracks it for the UI thread.
        private sealed class KeyState
        {
            public static readonly KeyState Current = new();

            public CoreVirtualKeyStates GetAsyncKeyState(VirtualKey key)
            {
                return Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
            }
        }

        /// <summary>
        /// One window, so a persisted-id activation is just an activation.
        /// </summary>
        public static void Activate(string persistedId)
        {
            Current?.Activate();
        }

        /// <summary>
        /// The window's caption. Uno routes it through the AppWindow.
        /// </summary>
        public string Title
        {
            get => _window?.AppWindow?.Title ?? string.Empty;
            set
            {
                if (_window?.AppWindow is AppWindow appWindow)
                {
                    appWindow.Title = value;
                }
            }
        }

        public void Activate()
        {
            _window.Activate();
        }

        public INavigationService GetNavigationService()
        {
            return GetNavigationService(_window);
        }

        public static INavigationService GetNavigationService(Window window)
        {
            var content = window.Content;
            if (content is WindowPresenter contentControl)
            {
                content = contentControl.Content as UIElement;
            }

            if (content is RootPage rootPage && rootPage.NavigationService != null)
            {
                return rootPage.NavigationService;
            }
            else if (content is Page { DataContext: ViewModelBase viewModel })
            {
                return viewModel.NavigationService;
            }

            return null;
        }

        // ---- the per-host seams 12.10.2 declares as `partial void` -------------------------
        //
        // These are the silent half of the split: an unimplemented `partial void` is legal C# and
        // the compiler DELETES it together with every call site, so a missing one costs no error,
        // no warning, and no hint -- SetHostContent going unimplemented is what left this head
        // painting a black window with a fully built visual tree behind it.

        /// <summary>
        /// Hand the root element to the window. Upstream 12.10.2 replaced a direct
        /// `_window.Content = _content` with this seam; UWP assigns Window.Content and Win32
        /// assigns IslandWindow.Content. Uno's WinUI Window takes it directly.
        /// </summary>
        partial void SetHostContent(UIElement content)
        {
            _window.Content = content;
        }

        /// <summary>
        /// Uno draws only the client area on this host: there is no system title bar, and the
        /// caption buttons are the app's own (HasSystemCaptionButtons is false here), so there is
        /// nothing to hand to the shell.
        /// </summary>
        partial void SetHostCaptionButtons(CaptionButtons buttons)
        {
        }

        /// <summary>
        /// Windows hides a window from capture through ApplicationView.IsScreenCaptureEnabled (and
        /// Win32 through SetWindowDisplayAffinity). X11 offers no equivalent: any client can read
        /// the root window. Deliberately a no-op, and deliberately NOT silent about it -- callers
        /// that believe a passcode screen is unrecordable would be wrong on this platform.
        /// </summary>
        partial void SetScreenCaptureEnabled(bool enabled)
        {
        }

        /// <summary>
        /// The backdrop is applied through BackdropMaterial on the root element rather than by the
        /// host. ApplyContentMaterial is idempotent (it guards on _contentMaterial), so it is safe
        /// whether this seam or SetContentAsync gets there first.
        /// </summary>
        partial void SetBackdropMaterial(WindowPresenter content)
        {
            ApplyContentMaterial(content);
        }

    }
}
