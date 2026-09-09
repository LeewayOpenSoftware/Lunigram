//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Telegram.Navigation;
using Telegram.Views.Host;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Services
{
    /// <summary>
    /// The Linux ViewService. The Windows one opens secondary windows through
    /// CoreApplication.CreateNewView; here there is a single window, so everything that asks for
    /// a new view is presented inside the RootPage (the same facade the Windows build uses on
    /// Xbox) and the lifetime control it gets back is a facade too.
    /// </summary>
    public sealed partial class ViewService : IViewService
    {
        private static readonly TaskCompletionSource<bool> _mainWindowCreated = new();

        internal static void OnWindowCreated()
        {
        }

        internal static void OnWindowLoaded()
        {
            _mainWindowCreated.TrySetResult(true);
        }

        public static Task WaitForMainWindowAsync()
        {
            return _mainWindowCreated.Task;
        }

        public Task<WindowContext> OpenAsync(ViewServiceOptions options)
        {
            var window = WindowContext.Main ?? WindowContext.Active ?? WindowContext.Current;
            if (window == null)
            {
                Logger.Error("ViewService.OpenAsync: no WindowContext available (Main, Active and Current are all null)");
                return Task.FromResult<WindowContext>(null);
            }

            return window.Dispatcher.DispatchAsync(() =>
            {
                if (window.Content is RootPage root)
                {
                    root.PresentContent(options.Content(window));
                }
                else
                {
                    Logger.Warning($"ViewService.OpenAsync: window.Content is not RootPage ({window.Content?.GetType().FullName ?? "null"})");
                }

                return window;
            });
        }

        public Task<WindowContext> OpenAsync(ISession session, Type page, object parameter = null, string title = null, Size size = default, string id = "0")
        {
            Logger.Info($"Page: {page}, Parameter: {parameter}, Title: {title}, Size: {size}");

            var window = WindowContext.Main ?? WindowContext.Active ?? WindowContext.Current;
            if (window == null)
            {
                Logger.Error("ViewService.OpenAsync: no WindowContext available (Main, Active and Current are all null)");
                return Task.FromResult<WindowContext>(null);
            }

            return window.Dispatcher.DispatchAsync(() =>
            {
                if (window.Content is RootPage root)
                {
                    var nav = BootStrapper.Current.NavigationServiceFactory(session, window, BootStrapper.BackButton.Ignore, new Frame(), id, false);
                    nav.Navigate(page, parameter);

                    root.PresentContent(nav.Frame);
                }
                else
                {
                    Logger.Warning($"ViewService.OpenAsync: window.Content is not RootPage ({window.Content?.GetType().FullName ?? "null"})");
                }

                return window;
            });
        }
    }

    /// <summary>
    /// Stands in for the Windows ViewLifetimeControl (one per secondary view, reference counted).
    /// There is one window, so consolidating means dismissing whatever the RootPage is presenting.
    /// </summary>
    public sealed partial class ViewLifetimeControl
    {
        public DispatcherContext Dispatcher { get; }

        public int Id { get; }

        public Window Window { get; }

        private ViewLifetimeControl()
        {
            var context = WindowContext.Main ?? WindowContext.Active ?? WindowContext.Current;

            Dispatcher = context?.Dispatcher as DispatcherContext;
            Window = context;
            Id = context?.Id ?? 0;
        }

        public static ViewLifetimeControl Facade()
        {
            return new ViewLifetimeControl();
        }

        public Task ConsolidateAsync()
        {
            if (Dispatcher == null || Dispatcher.HasThreadAccess)
            {
                return ConsolidateAsyncImpl();
            }

            return Dispatcher.DispatchAsync(ConsolidateAsyncImpl);
        }

        private Task ConsolidateAsyncImpl()
        {
            var context = WindowContext.Main ?? WindowContext.Active ?? WindowContext.Current;
            if (context?.Content is RootPage root)
            {
                root.PresentContent(null);
            }

            return Task.CompletedTask;
        }

        public int StartViewInUse()
        {
            return 1;
        }

        public int StopViewInUse()
        {
            return 0;
        }

        public event ViewReleasedHandler Released;
    }
}
