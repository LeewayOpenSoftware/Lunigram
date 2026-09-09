//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Services;
using Microsoft.UI.Xaml.Resources;

namespace Telegram.Common
{
    public partial class XamlResourceLoader : CustomXamlResourceLoader
    {
        protected override object GetResource(string resourceId, string objectType, string propertyName, string propertyType)
        {
#if LINUX
            // The few strings this port adds that Telegram's translation platform does not have -
            // see Telegram.Linux/Platform/LinuxStrings.cs for why they live there and not in
            // Strings/*.resw, and for the one caveat (a key here shadows the same key upstream).
            if (LinuxStrings.TryGetString(resourceId, out var linux))
            {
                return linux;
            }
#endif

            return LocaleService.Current.GetString(resourceId);
        }
    }
}
