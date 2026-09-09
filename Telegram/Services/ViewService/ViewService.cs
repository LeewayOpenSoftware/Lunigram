//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Telegram.Navigation;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;

namespace Telegram.Services
{
    public interface IViewService
    {
        ///<summary>
        /// Creates and opens new secondary view        
        /// </summary>
        /// <param name="page">Type of page to automatically navigate</param>
        /// <param name="parameter">Parameter that will be passed to NavigationService with the page</param>
        /// <param name="title">Title that will be displayed for new view. If <code>null</code> - current view's title will be used</param>
        /// <param name="size">Anchor size for newly created view</param>        
        /// <returns>The <see cref="WindowContext"/> of the newly created view, or <c>null</c> if it could not be created.</returns>
        Task<WindowContext> OpenAsync(ISession session, Type page, object parameter = null, string title = null, Size size = default, string id = "0");

        Task<WindowContext> OpenAsync(ViewServiceOptions options);
    }

    public enum ViewServiceMode
    {
        Default,
        CompactOverlay,
        FullScreen,
    }

    public partial class ViewServiceOptions
    {
        public ViewServiceMode ViewMode { get; set; } = ViewServiceMode.Default;

        public string Title { get; set; }

        public double Width { get; set; }
        public double Height { get; set; }

        public Func<WindowContext, UIElement> Content { get; set; }

        public string PersistedId { get; set; }
    }

#if !LINUX
    public sealed partial class ViewService : IViewService
    {
        private static readonly TaskCompletionSource<bool> _mainWindowCreated = new();

        internal static void OnWindowCreated()
        {
            var view = CoreApplication.GetCurrentView();
            if (!view.IsMain && !view.IsHosted)
            {
                var control = ViewLifetimeControl.GetForCurrentView();
                //This one time it should be made manually, as after Consolidate event fires the inner reference number should become zero
                control.StartViewInUse();

#if NET9_0_OR_GREATER
                var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
#else
                var context = SynchronizationContext.Current;
#endif
                //This is necessary to not make control.StartViewInUse()/control.StopViewInUse() manually on each and every async call. Facade will do it for you
                SynchronizationContext.SetSynchronizationContext(new SecondaryViewSynchronizationContextDecorator(control, context));
            }
        }

        internal static void OnWindowLoaded()
        {
            _mainWindowCreated.TrySetResult(true);
        }

        public static Task WaitForMainWindowAsync()
        {
            return _mainWindowCreated.Task;
        }

    }
#endif
}
