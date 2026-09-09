//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Telegram.Services;
using Telegram.ViewModels.Settings;
#if !LINUX
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public sealed partial class StartupSwitch : UserControl
    {
        public SettingsAdvancedViewModel ViewModel => DataContext as SettingsAdvancedViewModel;

#if LINUX
        public StartupSwitch()
        {
            InitializeComponent();

            // Nothing to detect here. On Windows both halves of this control depend on a packaged
            // identity — FullTrustProcessLauncher for the tray icon (which lives in Telegram.Stub)
            // and the StartupTask contract for autostart — so the control hides itself when it is
            // not packaged. On Linux the tray icon is a StatusNotifierItem inside this very process
            // and autostart is a file in ~/.config/autostart: both always exist. See
            // Telegram.Linux/Platform/DBus/AutoStart.cs.
            //
            // The two x:Load="False" checkboxes are materialized from Loaded and not from here,
            // because in Uno FindName on an ElementStub does nothing while the control is still
            // being built: measured on 6.6.184, the constructor call left the tray checkbox out of
            // the visual tree altogether and the page came up with autostart as its only row.
            // LayoutUpdated is the backstop this port already uses for the same reason elsewhere
            // (ItemsPanelRoot, GalleryWindow): whichever arrives first does the work once.
            Loaded += OnControlLoaded;
            LayoutUpdated += OnControlLayoutUpdated;

            Visibility = Visibility.Visible;
        }

        private bool _materialized;

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Materialize();
        }

        private void OnControlLayoutUpdated(object sender, object e)
        {
            Materialize();
        }

        private CheckBox _tray;
        private CheckBox _minimized;

        /// <summary>
        /// Puts the two checkboxes that the XAML declares with <c>x:Load="False"</c> on screen.
        ///
        /// <para>They are BUILT here rather than materialized, because in Uno the stub of an
        /// <c>x:Load="False"</c> element that is an ITEM of an ItemsControl (which
        /// <c>HeaderedControl</c> is) never gets replaced: <c>FindName</c> does nothing from the
        /// constructor and does nothing from <c>Loaded</c> either — measured on 6.6.184, the page
        /// came up with autostart as its only row both times, and the visual tree showed the two
        /// stubs as empty ContentControls. Everything else about them (their strings, their style,
        /// what they do) is the same as the XAML's, and the Windows path is untouched.</para>
        /// </summary>
        private void Materialize()
        {
            if (_materialized)
            {
                return;
            }

            _materialized = true;

            Loaded -= OnControlLoaded;
            LayoutUpdated -= OnControlLayoutUpdated;

            var style = Resources.TryGetValue("SettingsCheckBoxStyle", out object local) ? local as Style
                : Application.Current.Resources.TryGetValue("SettingsCheckBoxStyle", out object global) ? global as Style
                : null;

            _tray = new CheckBox
            {
                Content = Strings.ShowTrayIcon,
                Style = style,
                IsChecked = AppSettings.IsTrayVisible
            };

            _minimized = new CheckBox
            {
                Content = Strings.AutoStartMinized,
                Style = style
            };

            // Before and after the autostart checkbox the XAML already draws, in the order the
            // XAML has them.
            Headered.Items.Insert(0, _tray);
            Headered.Items.Add(_minimized);

            _tray.Checked += Tray_Toggled;
            _tray.Unchecked += Tray_Toggled;

            OnLoaded();
        }

        private void Tray_Toggled(object sender, RoutedEventArgs e)
        {
            var viewModel = ViewModel;
            if (viewModel != null)
            {
                // The view model is what the XAML binds to on Windows, and it is where the icon is
                // actually turned on and off.
                viewModel.IsTrayVisible = _tray.IsChecked is true;
            }
            else
            {
                AppSettings.IsTrayVisible = _tray.IsChecked is true;
                _ = DesktopIntegration.SetTrayVisibleAsync(_tray.IsChecked is true);
            }
        }
#else
        public StartupSwitch()
        {
            InitializeComponent();

            var integrated = false;

            if (ApiInformation.IsTypePresent("Windows.ApplicationModel.FullTrustProcessLauncher"))
            {
                integrated = true;
                FindName(nameof(TraySwitch));
            }

            if (ApiInformation.IsApiContractPresent("Windows.ApplicationModel.StartupTaskContract", 2))
            {
                integrated = true;

#if DESKTOP_BRIDGE
                FindName(nameof(ToggleMinimized));
#endif

                OnLoaded();
            }

            Visibility = integrated
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
#endif

#if LINUX
        private void OnLoaded()
        {
            Toggle.Checked -= OnToggled;
            Toggle.Unchecked -= OnToggled;

            // Null-checked like the Windows path: the two extra checkboxes only exist once
            // Materialize has run, and a settings page must not go down with them.
            if (_minimized != null)
            {
                _minimized.Checked -= Minimized_Toggled;
                _minimized.Unchecked -= Minimized_Toggled;
            }

            // There is no StartupTaskState.DisabledByUser: Windows lets the user veto a startup
            // task from Settings and the app can only report it, while here the file in
            // ~/.config/autostart is the whole truth. So the switch is never disabled and
            // Strings.AutoStartDisabledInfo never applies.
            var enabled = AutoStart.IsEnabled;

            Toggle.IsChecked = enabled;
            Toggle.IsEnabled = true;

            if (_minimized != null)
            {
                _minimized.IsChecked = enabled && AutoStart.IsMinimized;

                // The XAML binds this Visibility to Toggle.IsChecked; a checkbox built in code has
                // no binding, so it is set here — "start minimized" means nothing while the app
                // does not start with the session.
                _minimized.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            }

            Headered.Footer = string.Empty;

            Toggle.Checked += OnToggled;
            Toggle.Unchecked += OnToggled;

            if (_minimized != null)
            {
                _minimized.Checked += Minimized_Toggled;
                _minimized.Unchecked += Minimized_Toggled;
            }
        }

        private void OnToggled(object sender, RoutedEventArgs e)
        {
            if (Toggle.IsChecked is true)
            {
                AutoStart.Enable(AppSettings.IsLaunchMinimized);
            }
            else
            {
                AutoStart.Disable();
            }

            // Reads the file back rather than trusting the click: a read-only home is the one case
            // where the switch has to bounce back to where it was.
            OnLoaded();
        }
#else
        private async void OnLoaded()
        {
            Toggle.Checked -= OnToggled;
            Toggle.Unchecked -= OnToggled;

            if (ToggleMinimized != null)
            {
                ToggleMinimized.Checked -= Minimized_Toggled;
                ToggleMinimized.Unchecked -= Minimized_Toggled;
            }

            var task = await GetTaskAsync();
            if (task == null || task.State == StartupTaskState.DisabledByUser)
            {
                Toggle.IsChecked = false;
                Toggle.IsEnabled = false;

                if (ToggleMinimized != null)
                {
                    ToggleMinimized.IsChecked = false;
                    ToggleMinimized.Visibility = Visibility.Collapsed;
                }

                Headered.Footer = Strings.AutoStartDisabledInfo;
                Visibility = Visibility.Visible;
            }
            else if (task.State == StartupTaskState.Enabled)
            {
                Toggle.IsChecked = true;
                Toggle.IsEnabled = true;

                if (ToggleMinimized != null)
                {
                    ToggleMinimized.IsChecked = AppSettings.IsLaunchMinimized;
                    ToggleMinimized.Visibility = Visibility.Visible;
                }

                Headered.Footer = string.Empty;

                Visibility = Visibility.Visible;
            }
            else if (task.State == StartupTaskState.Disabled)
            {
                Toggle.IsChecked = false;
                Toggle.IsEnabled = true;

                if (ToggleMinimized != null)
                {
                    ToggleMinimized.IsChecked = false;
                    ToggleMinimized.Visibility = Visibility.Collapsed;
                }

                Headered.Footer = string.Empty;

                Visibility = Visibility.Visible;
            }
            else
            {
                Visibility = Visibility.Collapsed;
            }

            Toggle.Checked += OnToggled;
            Toggle.Unchecked += OnToggled;

            if (ToggleMinimized != null)
            {
                ToggleMinimized.Checked += Minimized_Toggled;
                ToggleMinimized.Unchecked += Minimized_Toggled;
            }
        }

        private async void OnToggled(object sender, RoutedEventArgs e)
        {
            var task = await GetTaskAsync();
            if (task == null)
            {
                return;
            }

            if (Toggle.IsChecked is true)
            {
                await task.RequestEnableAsync();
            }
            else
            {
                task.Disable();
            }

            OnLoaded();
        }

        private async Task<StartupTask> GetTaskAsync()
        {
            try
            {
                return await StartupTask.GetAsync("Telegram");
            }
            catch
            {
                return null;
            }
        }

#endif

        private void Minimized_Toggled(object sender, RoutedEventArgs e)
        {
#if LINUX
            AppSettings.IsLaunchMinimized = _minimized.IsChecked is true;
            // The setting is not enough here: "start minimized" travels as --minimized on the
            // command line the session manager runs, so the entry has to be written again.
            AutoStart.Enable(AppSettings.IsLaunchMinimized);
#else
            AppSettings.IsLaunchMinimized = ToggleMinimized.IsChecked is true;
#endif
        }
    }
}
