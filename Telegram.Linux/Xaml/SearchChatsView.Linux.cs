//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of Controls/Views/SearchChatsView.xaml.cs. Same gap as Xaml/ForumView.Linux.cs and
// Xaml/ChatView.Linux.cs -- see ForumView.Linux.cs for the general shape and for WHY the split is
// by type.
//
// Concretely here: SearchChatsView.xaml declares three item templates in <UserControl.Resources>,
// and two of the three carry x:Name rather than x:Key:
//     <DataTemplate x:Name="ProfileTemplate">   (the search result row)
//     <DataTemplate x:Name="MessageTemplate">   (the message result row)
// Uno does NOT emit a backing field for an x:Name'd DataTemplate, but it DOES emit
// `ProfileTemplate.UpdateResourceBindings();` for it inside __UpdateNamedResources, which it hooks
// to Loading. So without the two properties below the build breaks from INSIDE
// obj/.../XamlCodeGenerator/ with CS0103, even though the code-behind never names them.
//
// HeaderTemplate and HeaderListViewItemStyle use x:Key, so they are not named resources and must
// NOT be declared here.

using Microsoft.UI.Xaml;

namespace Telegram.Controls.Views
{
    public sealed partial class SearchChatsView
    {
        private T NamedResource<T>(string key) where T : class
        {
            return Resources.TryGetValue(key, out object value) ? value as T : null;
        }

        private DataTemplate ProfileTemplate => NamedResource<DataTemplate>(nameof(ProfileTemplate));

        private DataTemplate MessageTemplate => NamedResource<DataTemplate>(nameof(MessageTemplate));
    }
}
