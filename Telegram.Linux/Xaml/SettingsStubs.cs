//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Controls that the settings pages of the Linux subset instantiate but that are out of it.
// Same rule as the rest of Telegram.Linux/Xaml: keep the members the shared XAML and code-behind
// touch, render nothing, never throw.

using Telegram.Services;
using Telegram.Td.Api;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls
{
    /// <summary>
    /// The two-swatch badge next to "Your name colour" in <c>SettingsAppearancePage</c>. The real
    /// one (<c>Controls/ProfileColorBadge.xaml</c>) draws its corner wedges with Win2D
    /// (<c>Microsoft.Graphics.Canvas.Geometry</c>) and its row navigates to
    /// <c>SettingsProfileColorPage</c>, which is not in the subset either - so the row is collapsed
    /// on Linux and this only has to exist for the XAML to resolve the type.
    /// </summary>
    public partial class ProfileColorBadge : Control
    {
        public void SetUser(IClientService clientService, User user)
        {
        }

        public void SetChat(IClientService clientService, Chat chat)
        {
        }
    }
}
