//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Views;
using Windows.ApplicationModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Controls
{
    public partial class VersionLabel : Button
    {
        private int _advanced;

        public VersionLabel()
        {
            DefaultStyleKey = typeof(VersionLabel);

            Content = "Unigram " + GetVersion();

            Click += OnClick;
            ContextRequested += OnContextRequested;
        }

        public event RoutedEventHandler Navigate;

        private void OnClick(object sender, RoutedEventArgs e)
        {
            _advanced++;

            if (_advanced >= 10)
            {
                _advanced = 0;

                if (Navigate != null)
                {
                    Navigate.Invoke(this, e);
                }
#if !LINUX
                else
                {
                    var frame = this.GetParent<Frame>();
                    frame?.Navigate(typeof(DiagnosticsPage));
                }
#endif
            }
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
#if !LINUX
            var flyout = new MenuFlyout();
            var element = sender as FrameworkElement;

            flyout.CreateFlyoutItem(CopyVersion, Strings.Copy, Icons.Copy);

            flyout.ShowAt(element, args);
#endif
        }

        private void CopyVersion()
        {
            MessageHelper.CopyText(XamlRoot, GetVersion());
        }

        public static string GetVersion()
        {
#if LINUX
            // There is no package identity here, and Uno does not fail on Package.Current: it
            // answers 1.0 for the version, Unknown for the architecture and a SignatureKind that
            // falls through to " Direct", so the foot of Settings read "Unigram 1.0 Unknown Direct".
            // The same three facts, taken from where they actually live on this platform.
            var assembly = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(VersionLabel).Assembly;
            var informational = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?
                .InformationalVersion;

            // The SDK appends "+<commit sha>" to the informational version.
            var plus = informational?.IndexOf('+') ?? -1;
            if (plus > 0)
            {
                informational = informational[..plus];
            }

            var parsed = System.Version.TryParse(informational, out var assemblyVersion)
                ? assemblyVersion
                : assembly.GetName().Version ?? new System.Version(0, 0);

            var build = parsed.Build > 0
                ? string.Format("{0}.{1}.{2}", parsed.Major, parsed.Minor, parsed.Build)
                : string.Format("{0}.{1}", parsed.Major, parsed.Minor);

            var buildNumber = parsed.Revision > 0
                ? string.Format(" ({0})", parsed.Revision)
                : string.Empty;

            return string.Format("{0}{1} {2}", build, buildNumber,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
#else
            Package package = Package.Current;
            PackageId packageId = package.Id;
            PackageVersion version = packageId.Version;

            var type = Package.Current.SignatureKind switch
            {
                PackageSignatureKind.Store => "",
                PackageSignatureKind.Enterprise => " Direct",
                _ => " Direct"
            };

            var revision = version.Revision > 0
                ? string.Format(" ({0})", version.Revision)
                : string.Empty;

            if (version.Build > 0)
            {
                return string.Format("{0}.{1}.{2}{3} {4}{5}", version.Major, version.Minor, version.Build, revision, packageId.Architecture, type);
            }

            return string.Format("{0}.{1}{2} {3}{4}", version.Major, version.Minor, revision, packageId.Architecture, type);
#endif
        }
    }
}
