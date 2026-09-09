//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of Views/ChatView.xaml.cs. ChatView.xaml declares its message templates as
// x:Name'd resources (`<DataTemplate x:Name="OutgoingMessageTemplate">` …). The WinUI XAML
// compiler turns those into fields; Uno's generator only registers them in `Resources[...]`
// (and still emits `<Name>.UpdateResourceBindings()` calls against the field names), so this
// partial exposes them as accessors over the resource dictionary.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views
{
    public sealed partial class ChatView
    {
        private T NamedResource<T>(string key) where T : class
        {
            return Resources.TryGetValue(key, out object value) ? value as T : null;
        }

        private ControlTemplate SavedMessagesTabTemplate => NamedResource<ControlTemplate>(nameof(SavedMessagesTabTemplate));

        private DataTemplate ServiceMessagePhotoTemplate => NamedResource<DataTemplate>(nameof(ServiceMessagePhotoTemplate));

        private DataTemplate ServiceMessageBirthdateTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageBirthdateTemplate));

        private DataTemplate ServiceMessageBackgroundTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageBackgroundTemplate));

        private DataTemplate ServiceMessageGiftCodeTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageGiftCodeTemplate));

        private DataTemplate ServiceMessageGiftTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageGiftTemplate));

        private DataTemplate ServiceMessageUpgradedGiftPurchaseOfferTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageUpgradedGiftPurchaseOfferTemplate));

        private DataTemplate ServiceMessageChatHasProtectedContentDisableRequestedTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageChatHasProtectedContentDisableRequestedTemplate));

        private DataTemplate ServiceMessageUpgradedGiftTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageUpgradedGiftTemplate));

        private DataTemplate ServiceMessageTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageTemplate));

        private DataTemplate ServiceMessageAccountInfoTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageAccountInfoTemplate));

        private DataTemplate UnsupportedTemplate => NamedResource<DataTemplate>(nameof(UnsupportedTemplate));

        private DataTemplate ServiceMessageNewThreadTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageNewThreadTemplate));

        private DataTemplate ServiceMessageUnreadTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageUnreadTemplate));

        private DataTemplate ServiceMessageForumTopicTemplate => NamedResource<DataTemplate>(nameof(ServiceMessageForumTopicTemplate));

        private DataTemplate OutgoingMessageTemplate => NamedResource<DataTemplate>(nameof(OutgoingMessageTemplate));

        private DataTemplate IncomingMessageTemplate => NamedResource<DataTemplate>(nameof(IncomingMessageTemplate));
    }
}
