//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of Controls/Views/ForumView.xaml.cs. Same gap as Xaml/ChatView.Linux.cs, and the
// same fix — see that file for the general shape.
//
// What is worth knowing HERE is that Uno's generator is not consistent about it: for an x:Name'd
// resource it decides by TYPE whether to emit a backing property.
//   - <Style x:Name="VerticalListViewItemStyle"> DOES get one (an ElementNameSubject pair), so it
//     must NOT be declared here: it would be a duplicate member.
//   - <DataTemplate x:Name="..."> and <ItemsPanelTemplate x:Name="..."> do NOT get one — and yet
//     the generator still emits `<Name>.UpdateResourceBindings()` for every one of them in
//     __UpdateNamedResources, which it hooks to Loading. So the six below break the build from
//     INSIDE the generated file, whether or not the code-behind ever names them.
// Hence: declare the templates, leave the style alone.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Views
{
    public sealed partial class ForumView
    {
        private T NamedResource<T>(string key) where T : class
        {
            return Resources.TryGetValue(key, out object value) ? value as T : null;
        }

        private DataTemplate ListTemplate => NamedResource<DataTemplate>(nameof(ListTemplate));

        private DataTemplate VerticalTemplate => NamedResource<DataTemplate>(nameof(VerticalTemplate));

        private DataTemplate HorizontalTemplate => NamedResource<DataTemplate>(nameof(HorizontalTemplate));

        private ItemsPanelTemplate ListPanelTemplate => NamedResource<ItemsPanelTemplate>(nameof(ListPanelTemplate));

        private ItemsPanelTemplate VerticalPanelTemplate => NamedResource<ItemsPanelTemplate>(nameof(VerticalPanelTemplate));

        private ItemsPanelTemplate HorizontalPanelTemplate => NamedResource<ItemsPanelTemplate>(nameof(HorizontalPanelTemplate));
    }
}
