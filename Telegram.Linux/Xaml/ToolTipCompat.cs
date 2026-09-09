//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram
{
    /// <summary>
    /// Uno's XAML generator, given an attached property whose value is a <c>{CustomResource}</c>
    /// (<c>ToolTipService.ToolTip="{CustomResource AccDescrPageDown}"</c>, used on every button of
    /// MainPage, ChatView, ChatHistoryArrows…), emits <c>element.ToolTip = …</c> instead of
    /// <c>ToolTipService.SetToolTip(element, …)</c>, and nothing in WinUI has a ToolTip instance
    /// property. This extension property is what that generated assignment binds to; it routes
    /// the value to the attached property the XAML meant. Lives in the <c>Telegram</c> namespace
    /// so every generated page (all under Telegram.*) finds it through its enclosing namespaces.
    /// </summary>
    public static class ToolTipCompat
    {
        extension(DependencyObject element)
        {
            public object ToolTip
            {
                get => ToolTipService.GetToolTip(element);
                set => ToolTipService.SetToolTip(element, value);
            }
        }
    }
}
