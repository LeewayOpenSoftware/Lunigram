//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

#if !LINUX
using Telegram.Native;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.System.Profile;
#endif
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Common
{
#if LINUX
    // Constants: there is one target (Uno Skia on X11) and no runtime API probing to do. The
    // values pick the code paths that exist on Linux - the Windows 11 branches are the modern
    // ones, shadows and rectangle clips are Uno composition features - and disable what has no
    // counterpart: Store, Xbox, VoIP, the native media stack and the Downloads folder link
    // (FutureAccessList), so downloads stay in the TDLib cache.
    public static class ApiInfo
    {
        public static bool IsStoreRelease => false;

        // Not the sideloaded package the Windows build means by this, and the difference is not
        // academic: it is what picks TDLib's verbosity, and level 4 wrote 100 MB of synchronous
        // log in a quarter of an hour - on top of a round trip into TDLib for every line the app
        // itself logs. False gives the same level a Windows developer build uses.
        public static bool IsPackagedRelease => false;

        public static bool IsDesktop => true;

        public static bool IsXbox => false;

        public static bool IsMediaSupported => false;

        public static bool HasDownloadFolder => false;

        public static bool HasCacheOnly => !HasDownloadFolder;

        public static bool HasMultipleViews => false;

        public static bool HasKnownFolders => false;

        public static bool IsVoipSupported => false;

        public static bool CanCreateRectangleClip => true;

        public static bool CanCreateThemeShadow => false;

        public static bool CanSetSelectedBorderBrush => false;

        // MEDIDO, y costo una pantalla entera: Uno no implementa
        // Compositor.CreatePathKeyFrameAnimation() -- lanza NotImplementedException nada mas
        // llamarla. La sonda UNIGRAM_CALL_TEST se llevo por delante el constructor de VoipPage
        // por aqui (CompositionBlobVisual.cs:400). No es lo mismo que CompositionPath, que este
        // port SI resuelve por reflexion en Telegram.Linux/Graphics/CompositionPath.cs: eso
        // dibuja un camino, esto lo ANIMA entre fotogramas clave, y Uno no tiene con que.
        // Los cinco llamantes de esta propiedad ya tienen su rama alternativa.
        public static bool CanAnimatePaths => false;

        public static bool IsWindows11 => true;

        public static bool IsBuildOrGreater(ulong compare)
        {
            return true;
        }

        public static NavigationCacheMode NavigationCacheMode => NavigationCacheMode.Enabled;
    }
#else
    public static class ApiInfo
    {
        private static bool? _isStoreRelease;
        public static bool IsStoreRelease => _isStoreRelease ??= (Package.Current.SignatureKind == PackageSignatureKind.Store);

        public static bool IsPackagedRelease => !IsStoreRelease;

        private static bool? _isDesktop;
        public static bool IsDesktop => _isDesktop ??= string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Desktop");

        private static bool? _isXbox;
        public static bool IsXbox => _isXbox ??= string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Xbox");

        private static bool? _isMediaSupported;
        public static bool IsMediaSupported => _isMediaSupported ??= NativeUtils.IsMediaSupported();

        private static bool? _hasDownloadFolder;
        public static bool HasDownloadFolder => _hasDownloadFolder ??= IsDesktop;

        public static bool HasCacheOnly => !HasDownloadFolder;

        public static bool HasMultipleViews => !IsXbox;

        private static bool? _hasKnownFolders;
        public static bool HasKnownFolders => _hasKnownFolders ??= ApiInformation.IsEnumNamedValuePresent("Windows.Storage.KnownFolderId", "DownloadsFolder");

        private static bool? _isVoipSupported;
        public static bool IsVoipSupported => _isVoipSupported ??= ApiInformation.IsApiContractPresent("Windows.ApplicationModel.Calls.CallsVoipContract", 1);

        private static bool? _canCreateRectangleClip;
        public static bool CanCreateRectangleClip => _canCreateRectangleClip ??= ApiInformation.IsMethodPresent("Microsoft.UI.Composition.Compositor", "CreateRectangleClip");

        // We only enable shadows on Windows 11 for three reasons:
        // First: they look terrible on Windows 10
        // Second: they are way more optimized on Windows 11 (they use a nine-grid instead of dynamically casted shadows)
        // Third: there seems to be no way to create a custom shadow that only casts below messages without overlaps
        private static bool? _canCreateThemeShadow;
        public static bool CanCreateThemeShadow => IsWindows11 && (_canCreateThemeShadow ??= ApiInformation.IsPropertyPresent("Microsoft.UI.Xaml.UIElement", "Shadow"));

        // ListViewItemPresenter's Selected*BorderBrush properties live on IListViewItemPresenter4,
        // added in UniversalApiContract 13.0 (22621), so setting them throws on anything older.
        private static bool? _canSetSelectedBorderBrush;
        public static bool CanSetSelectedBorderBrush => _canSetSelectedBorderBrush ??= ApiInformation.IsPropertyPresent("Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter", "SelectedBorderBrush");

        private static bool? _canAnimatePaths;
        public static bool CanAnimatePaths => _canAnimatePaths ??= IsBuildOrGreater(19043);

        private static bool? _isWindows11;
        public static bool IsWindows11 => _isWindows11 ??= IsBuildOrGreater(22000);

        private static ulong? _build;
        public static bool IsBuildOrGreater(ulong compare)
        {
            if (_build == null)
            {
                string deviceFamilyVersion = AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
                ulong version = ulong.Parse(deviceFamilyVersion);
                ulong build = (version & 0x00000000FFFF0000L) >> 16;

                _build = build;
            }

            return _build >= compare;
        }

        public static NavigationCacheMode NavigationCacheMode => IsXbox
                ? NavigationCacheMode.Disabled
                : Constants.DEBUG
                ? NavigationCacheMode.Enabled
                : NavigationCacheMode.Enabled;
    }
#endif
}
