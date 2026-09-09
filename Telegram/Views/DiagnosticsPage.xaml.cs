//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Converters;
using Telegram.Native;
using Telegram.Services;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Telegram.Views
{
    public sealed partial class DiagnosticsPage : HostedPage
    {
        public DiagnosticsViewModel ViewModel => DataContext as DiagnosticsViewModel;

        public DiagnosticsPage()
        {
            InitializeComponent();
            Title = "Diagnostics";

#if LINUX
            // u-094b: camera diagnostics has no Linux camera stack to report on (VideoInfo is a
            // no-op here, see DiagnosticsViewModel.cs) -- hide the dead-end row rather than draw a
            // button that does nothing.
            //
            // FindName, not the generated field: a win:-excluded element earlier in the file was
            // observed to drop the compile-time field for a later x:Name ("VideoInfoButton does
            // not exist in the current context", CS0103) even though the element and its x:Name
            // registration are still there at runtime -- FindName walks the namescope instead of
            // relying on that generated field, so it is unaffected regardless of cause.
            if (FindName("VideoInfoButton") is FrameworkElement videoInfoButton)
            {
                videoInfoButton.Visibility = Visibility.Collapsed;
            }
#endif
        }

        #region Binding

        private string ConvertVerbosity(VerbosityLevel level)
        {
            return Enum.GetName(typeof(VerbosityLevel), level);
        }

        private string ConvertSize(ulong size)
        {
            return FileSizeConverter.Convert((long)size);
        }

        #endregion

        private void Exception_Click(object sender, RoutedEventArgs e)
        {
            ElementCompositionPreview.GetElementVisual(null);
        }

        private void Crash_Click(object sender, RoutedEventArgs e)
        {
            NativeUtils.Crash();
        }

        private void Logger_Click(object sender, RoutedEventArgs e)
        {
            Client.Execute(new AddLogMessage(0, "This should produce a stack trace"));
        }

        private void Anonymous_Click(object sender, RoutedEventArgs e)
        {
            MessageHelper.CopyText(XamlRoot, AppSettings.AnonymousUserId);
        }
    }
}
