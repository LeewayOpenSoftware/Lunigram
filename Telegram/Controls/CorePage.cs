//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

#if !LINUX
using Windows.ApplicationModel.Core;
#endif
using Microsoft.UI.Xaml;

namespace Telegram.Controls
{
    public partial class SystemOverlayMetrics
    {
#if LINUX
        // No custom title bar on Linux: the system overlay is empty.
        public SystemOverlayMetrics()
        {
        }
#else
        public SystemOverlayMetrics(CoreApplicationViewTitleBar sender)
        {
            Height = sender.Height;
            LeftInset = sender.SystemOverlayLeftInset;
            RightInset = sender.SystemOverlayRightInset;
            IsVisible = sender.IsVisible;
        }
#endif

        public double Height { get; }

        public double LeftInset { get; }

        public double RightInset { get; }

        public bool IsVisible { get; }
    }

    public partial class CorePage : PageEx
    {
        private bool _registered;

        public CorePage()
        {
            Connected += OnConnected;
            Disconnected += OnDisconnected;
        }

        private void OnConnected(object sender, RoutedEventArgs e)
        {
            if (!_registered)
            {
                _registered = true;

#if LINUX
                OnLayoutMetricsChanged(new SystemOverlayMetrics());
#else
                var application = CoreApplication.GetCurrentView().TitleBar;
                application.IsVisibleChanged += OnLayoutMetricsChanged;
                application.LayoutMetricsChanged += OnLayoutMetricsChanged;

                OnLayoutMetricsChanged(application, null);
#endif
            }
        }

        private void OnDisconnected(object sender, RoutedEventArgs e)
        {
            if (_registered)
            {
                _registered = false;

#if !LINUX
                var application = CoreApplication.GetCurrentView().TitleBar;
                application.IsVisibleChanged -= OnLayoutMetricsChanged;
                application.LayoutMetricsChanged -= OnLayoutMetricsChanged;
#endif
            }
        }

#if !LINUX
        private void OnLayoutMetricsChanged(CoreApplicationViewTitleBar sender, object args)
        {
            try
            {
                OnLayoutMetricsChanged(new SystemOverlayMetrics(sender));
            }
            catch
            {
                // Most likely InvalidComObjectException
            }
        }
#endif

        protected virtual void OnLayoutMetricsChanged(SystemOverlayMetrics metrics)
        {

        }
    }
}
