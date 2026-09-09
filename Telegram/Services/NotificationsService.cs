//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views;
using Windows.Storage;
#if !LINUX
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Services
{
    public interface INotificationsService
    {
        Task ProcessAsync(Dictionary<string, string> data);

        void PlaySound(bool sent);

        #region Chats related

        void SetMuteFor(Chat chat, int muteFor, XamlRoot xamlRoot);
        void SetMuteFor(ForumTopic topic, int muteFor, XamlRoot xamlRoot);

        void SetSound(Chat chat, bool silent, XamlRoot xamlRoot);

        #endregion
    }

    public partial class NotificationsService : INotificationsService
    {
        private readonly IClientService _clientService;
        private readonly ISession _sessionService;
        private readonly ISettingsService _settings;
        private readonly IEventAggregator _aggregator;

        private readonly DebouncedProperty<int> _unreadCount;

        private readonly bool? _suppress;

        public NotificationsService(IClientService clientService, ISettingsService settingsService, ISession sessionService, IEventAggregator aggregator)
        {
            _clientService = clientService;
            _settings = settingsService;
            _sessionService = sessionService;
            _aggregator = aggregator;

            _unreadCount = new DebouncedProperty<int>(200, UpdateUnreadCount, useBackgroundThread: true);

            Subscribe();

#if LINUX
            // Clicking the banner, or its Reply / Mark as Read buttons, comes back here through
            // D-Bus. On Windows the same three arrive as a background activation of the app.
            DesktopIntegration.NotificationActivated += OnNotificationActivated;
#endif

            var unreadCount = _clientService.GetUnreadCount(new ChatListMain());
            Handle(unreadCount.UnreadChatCount);
            Handle(unreadCount.UnreadMessageCount);
        }

#if !LINUX
        static NotificationsService()
        {
            RemoveCollections();
        }

        private static async void RemoveCollections()
        {
            if (AppSettings.HasRemovedCollections)
            {
                return;
            }

            AppSettings.HasRemovedCollections = true;

            try
            {
                await ToastNotificationManager.GetDefault().GetToastCollectionManager().RemoveAllToastCollectionsAsync();
            }
            catch
            {
                // All the remote procedure calls must be wrapped in a try-catch block
            }
        }
#endif

        private void Subscribe()
        {
            _aggregator.Subscribe<UpdateUnreadMessageCount>(this, Handle)
                .Subscribe<UpdateUnreadChatCount>(Handle)
                .Subscribe<UpdateSuggestedActions>(Handle)
                .Subscribe<UpdateServiceNotification>(Handle)
                .Subscribe<UpdateTermsOfService>(Handle)
                .Subscribe<UpdateSpeedLimitNotification>(Handle)
                .Subscribe<UpdateNotification>(Handle)
                .Subscribe<UpdateNotificationGroup>(Handle)
                .Subscribe<UpdateHavePendingNotifications>(Handle)
                .Subscribe<UpdateActiveNotifications>(Handle);
        }

        private void UpdateUnreadCount(int count)
        {
#if LINUX
            // com.canonical.Unity.LauncherEntry instead of BadgeUpdateManager: same number, drawn
            // by the dock on the icon of our .desktop file. Zero hides it, which is Clear().
            _ = LauncherEntry.SetCountAsync(count);
#else
            try
            {
                var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication("App");
                if (count == 0)
                {
                    updater.Clear();
                    return;
                }

                var document = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                var element = document.SelectSingleNode("/badge") as XmlElement;
                element.SetAttribute("value", count.ToString());

                updater.Update(new BadgeNotification(document));
            }
            catch { }
#endif
        }

        private async void Handle(UpdateSpeedLimitNotification update)
        {
            Logger.Info("UpdateSpeedLimitNotification");

            var text = update.IsUpload
                ? string.Format("**{0}**\n{1}", Strings.UploadSpeedLimited, string.Format(Strings.UploadSpeedLimitedMessage, _clientService.Options.PremiumUploadSpeedup))
                : string.Format("**{0}**\n{1}", Strings.DownloadSpeedLimited, string.Format(Strings.DownloadSpeedLimitedMessage, _clientService.Options.PremiumDownloadSpeedup));

            var markdown = ClientEx.ParseMarkdown(text);
            if (markdown.Entities.Count == 2)
            {
                markdown.Entities[1].Type = new TextEntityTypeTextUrl();
            }

            await ViewService.WaitForMainWindowAsync();

            var window = WindowContext.Active ?? WindowContext.Main;
            var dispatcher = window?.Dispatcher;

            dispatcher?.Dispatch(() =>
            {
                var navigationService = window.NavigationServices?.GetByFrameId($"Main{_clientService.SessionId}");
                if (navigationService == null)
                {
                    return;
                }

                var toast = ToastPopup.Show(navigationService.XamlRoot, markdown, ToastPopupIcon.SpeedLimit);
                void handler(object sender, TextUrlClickEventArgs e)
                {
                    toast.Click -= handler;
                    navigationService.ShowPromo(new PremiumSourceFeature(new PremiumFeatureImprovedDownloadSpeed()));
                }

                toast.Click += handler;
            });
        }

        public async void Handle(UpdateTermsOfService update)
        {
            Logger.Info("UpdateTermsOfService");

            if (update.TermsOfService.ShowPopup)
            {
                async void DeleteAccount(XamlRoot xamlRoot)
                {
                    var decline = await MessagePopup.ShowAsync(xamlRoot, Strings.TosUpdateDecline, Strings.TermsOfService, Strings.DeclineDeactivate, Strings.Back);
                    if (decline != ContentDialogResult.Primary)
                    {
                        Handle(update);
                        return;
                    }

                    var delete = await MessagePopup.ShowAsync(xamlRoot, Strings.TosDeclineDeleteAccount, Strings.AppName, Strings.Deactivate, Strings.Cancel);
                    if (delete != ContentDialogResult.Primary)
                    {
                        Handle(update);
                        return;
                    }

                    _clientService.Send(new DeleteAccount("Decline ToS update", string.Empty));
                }

                await ViewService.WaitForMainWindowAsync();

                var window = WindowContext.Active ?? WindowContext.Main;
                var dispatcher = window?.Dispatcher;

                dispatcher?.Dispatch(async () =>
                {
                    var xamlRoot = window.XamlRoot;
                    if (xamlRoot == null)
                    {
                        return;
                    }

                    var confirm = await MessagePopup.ShowAsync(xamlRoot, update.TermsOfService.Text, Strings.PrivacyPolicyAndTerms, Strings.Agree, Strings.Cancel);
                    if (confirm != ContentDialogResult.Primary)
                    {
                        DeleteAccount(xamlRoot);
                        return;
                    }

                    if (update.TermsOfService.MinUserAge > 0)
                    {
                        var age = await MessagePopup.ShowAsync(xamlRoot, string.Format(Strings.TosAgeText, update.TermsOfService.MinUserAge), Strings.TosAgeTitle, Strings.Agree, Strings.Cancel);
                        if (age != ContentDialogResult.Primary)
                        {
                            DeleteAccount(xamlRoot);
                            return;
                        }
                    }

                    _clientService.Send(new AcceptTermsOfService(update.TermsOfServiceId));
                });
            }
        }

        public async void Handle(UpdateSuggestedActions update)
        {
            Logger.Info("UpdateSuggestedActions");

            var enableArchiveAndMuteNewChats = update.AddedActions.FirstOrDefault(x => x is SuggestedActionEnableArchiveAndMuteNewChats);
            if (enableArchiveAndMuteNewChats == null)
            {
                return;
            }

            await ViewService.WaitForMainWindowAsync();

            var window = WindowContext.Active ?? WindowContext.Main;
            var dispatcher = window?.Dispatcher;

            dispatcher?.Dispatch(async () =>
            {
                var xamlRoot = window.XamlRoot;
                if (xamlRoot == null)
                {
                    return;
                }

                if (enableArchiveAndMuteNewChats is SuggestedActionEnableArchiveAndMuteNewChats)
                {
                    var confirm = await MessagePopup.ShowAsync(xamlRoot, Strings.HideNewChatsAlertText, Strings.HideNewChatsAlertTitle, Strings.OK, Strings.Cancel);
                    if (confirm == ContentDialogResult.Primary)
                    {
                        var response = await _clientService.SendAsync(new GetArchiveChatListSettings());
                        if (response is ArchiveChatListSettings settings)
                        {
                            settings.ArchiveAndMuteNewChatsFromUnknownUsers = true;
                            _clientService.Send(new SetArchiveChatListSettings(settings));
                        }
                    }

                    _clientService.Send(new HideSuggestedAction(enableArchiveAndMuteNewChats));
                }
            });
        }

        public async void Handle(UpdateServiceNotification update)
        {
            Logger.Info("UpdateServiceNotification");

            var caption = update.Content.GetCaption();
            if (caption == null)
            {
                return;
            }

            var text = caption.Text;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            await ViewService.WaitForMainWindowAsync();

            var window = WindowContext.Active ?? WindowContext.Main;
            var dispatcher = window?.Dispatcher;

            dispatcher?.Dispatch(async () =>
            {
                var xamlRoot = window.XamlRoot;
                if (xamlRoot == null)
                {
                    return;
                }

                if (update.Type.StartsWith("AUTH_KEY_DROP_"))
                {
                    var confirm = await MessagePopup.ShowAsync(xamlRoot, text, Strings.AppName, Strings.LogOut, Strings.Cancel);
                    if (confirm == ContentDialogResult.Primary)
                    {
                        _clientService.Send(new Destroy());
                    }
                }
                else if (ContentPopup.IsAnyPopupOpen(xamlRoot))
                {
                    await MessagePopup.ShowAsync(xamlRoot, target: null, text, Strings.AppName, Strings.OK);
                }
                else
                {
                    await MessagePopup.ShowAsync(xamlRoot, text, Strings.AppName, Strings.OK);
                }
            });
        }

        public void Handle(UpdateUnreadMessageCount update)
        {
            if (update.ChatList is not ChatListMain || !_settings.Notifications.CountUnreadMessages || !_sessionService.IsActive)
            {
                return;
            }

            if (_settings.Notifications.IncludeMutedChats)
            {
                _unreadCount.Set(update.UnreadCount);
            }
            else
            {
                _unreadCount.Set(update.UnreadUnmutedCount);
            }

            SendUnreadCount(update.UnreadCount, update.UnreadUnmutedCount);
        }

        public void Handle(UpdateUnreadChatCount update)
        {
            if (update.ChatList is not ChatListMain || _settings.Notifications.CountUnreadMessages || !_sessionService.IsActive)
            {
                return;
            }

            if (_settings.Notifications.IncludeMutedChats)
            {
                _unreadCount.Set(update.UnreadCount);
            }
            else
            {
                _unreadCount.Set(update.UnreadUnmutedCount);
            }

            SendUnreadCount(update.UnreadCount, update.UnreadUnmutedCount);
        }

        private int _notifyIconUnreadCount;
        private int _notifyIconUnreadUnmutedCount;

        private void SendUnreadCount(int unreadCount, int unreadUnmutedCount)
        {
            unreadCount = Math.Min(_settings.Notifications.IncludeMutedChats ? unreadCount : 0, 1);
            unreadUnmutedCount = Math.Min(unreadUnmutedCount, 1);

            if (unreadCount != _notifyIconUnreadCount || unreadUnmutedCount != _notifyIconUnreadUnmutedCount)
            {
                _notifyIconUnreadCount = unreadCount;
                _notifyIconUnreadUnmutedCount = unreadUnmutedCount;

#if LINUX
                // Same two booleans the ValueSet carried to Telegram.Stub, and the same three
                // icons; the difference is that the tray icon is in this process now.
                _ = DesktopIntegration.SetTrayStateAsync(unreadCount > 0, unreadUnmutedCount > 0);
#else
                BridgeApplicationContext.SendUnreadCount(unreadCount, unreadUnmutedCount);
#endif
            }
        }

        private ulong _lastPlayedSent;
        private ulong _lastPlayedReceived;

        public void PlaySound(bool sent)
        {
            if (!_settings.Notifications.InAppSounds)
            {
                return;
            }

            var now = Logger.TickCount;
            if (sent)
            {
                if (now - _lastPlayedSent < 500)
                {
                    return;
                }

                _lastPlayedSent = now;
            }
            else
            {
                if (now - _lastPlayedReceived < 500)
                {
                    return;
                }

                _lastPlayedReceived = now;
            }

            Task.Run(() => SoundEffects.Play(sent ? SoundEffect.Sent : SoundEffect.Received));
        }

        public void Handle(UpdateActiveNotifications update)
        {
#if LINUX
            // The Windows path reconciles what TDLib thinks is on screen against the toast
            // history, which outlives the app. A freedesktop server keeps no record we can ask for
            // (and ours is a dictionary in memory), so at start nothing of ours is up and the
            // honest answer is to drop them all -- which is literally what the Windows path does
            // when the history is unreachable, the catch block below.
            foreach (var group in update.Groups)
            {
                _clientService.Send(new RemoveNotificationGroup(group.Id, int.MaxValue));
            }
#else
            try
            {
                var history = ToastNotificationManager.History.GetHistory();
                var hash = new HashSet<string>();

                foreach (var item in history)
                {
                    hash.Add($"{item.Group}_{item.Tag}");
                }

                foreach (var group in update.Groups)
                {
                    foreach (var notification in group.Notifications)
                    {
                        if (hash.Contains($"{_clientService.SessionId}_{group.Id}_{notification.Id}"))
                        {
                            continue;
                        }

                        _clientService.Send(new RemoveNotification(group.Id, notification.Id));
                    }
                }
            }
            catch
            {
                foreach (var group in update.Groups)
                {
                    _clientService.Send(new RemoveNotificationGroup(group.Id, int.MaxValue));
                }
            }
#endif
        }

        public void Handle(UpdateHavePendingNotifications update)
        {
            // We want to ignore both delayed and unreceived notifications,
            // as they're the result of update difference on sync.
            if (_suppress == null && update.HaveDelayedNotifications && update.HaveUnreceivedNotifications)
            {
                //_suppress = true;
            }
            else if (_suppress == true && !update.HaveDelayedNotifications && !update.HaveUnreceivedNotifications)
            {
                //_suppress = false;
            }
        }

        public async void Handle(UpdateNotificationGroup update)
        {
#if LINUX
            // Read somewhere else: TDLib takes the notifications out of the group, and the banner
            // has to go with them. There is one banner per group here (see UpdateToast), so it is
            // closed when the group empties -- CloseNotification is what replaces removing an
            // entry from the toast history.
            if (update.RemovedNotificationIds.Count > 0 && update.TotalCount <= 0)
            {
                _ = DesktopNotifications.Current.CloseAsync(GetNotificationKey(update.NotificationGroupId));
            }
#else
            try
            {
                var history = ToastNotificationManager.History;

                foreach (var removed in update.RemovedNotificationIds)
                {
                    history.Remove($"{removed}", $"{_clientService.SessionId}_{update.NotificationGroupId}");
                }
            }
            catch
            {
                // All the remote procedure calls must be wrapped in a try-catch block
            }
#endif

            if (_suppress == true)
            {
                Logger.Info("_suppress is true");

                // This is an unsynced message, we don't want to show a notification for it as it has been probably pushed already by WNS
                return;
            }

            if (_clientService.ConnectionState is ConnectionStateUpdating)
            {
                Logger.Info("ConnectionState is ConnectionStateUpdating");

                // This is an unsynced message, we don't want to show a notification for it as it has been probably pushed already by WNS
                return;
            }

            if (!_sessionService.IsActive && !AppSettings.IsAllAccountsNotifications)
            {
                Logger.Info("Session is not active");

                return;
            }

            foreach (var notification in update.AddedNotifications)
            {
                await ProcessNotification(update.NotificationGroupId, update.NotificationSoundId, update.ChatId, notification);
                //_clientService.Send(new RemoveNotification(update.NotificationGroupId, notification.Id));
            }
        }

        public void Handle(UpdateNotification update)
        {
            if (_clientService.ConnectionState is ConnectionStateUpdating)
            {
                // This is an unsynced message, we don't want to show a notification for it as it has been probably pushed already by WNS
                return;
            }

            //ProcessNotification(update.NotificationGroupId, 0, update.Notification);
        }

        private async Task ProcessNotification(int group, long soundId, long chatId, Td.Api.Notification notification)
        {
            var time = Formatter.ToLocalTime(notification.Date);
            if (time < DateTime.Now.AddHours(-1))
            {
                _clientService.Send(new RemoveNotification(group, notification.Id));

                Logger.Info("Notification is too old");
                return;
            }

            switch (notification.Type)
            {
                case NotificationTypeNewCall:
                    break;
                case NotificationTypeNewMessage newMessage:
                    await ProcessNewMessage(group, notification.Id, newMessage.Message, time, soundId, notification.IsSilent);
                    break;
                case NotificationTypeNewSecretChat:
                    break;
            }
        }

        private async Task ProcessNewMessage(int groupId, int id, Message message, DateTime date, long soundId, bool silent)
        {
            var chat = _clientService.GetChat(message.ChatId);
            if (chat == null)
            {
                Logger.Info("Chat is null");
                return;
            }

            if (UpdateAsync(chat, message))
            {
                var caption = GetCaption(chat, silent);
                var content = GetContent(chat, message);
                var launch = GetLaunch(chat, message);
                var picture = GetPhoto(chat);
                var dateTime = date.ToUniversalTime().ToString("s") + "Z";
                var canReply = !(chat.Type is ChatTypeSupergroup super && super.IsChannel);

                Td.Api.File soundFile = null;
                if (soundId != -1 && soundId != 0 && !silent)
                {
                    Logger.Info("Custom notification sound");

                    var response = await _clientService.SendAsync(new GetSavedNotificationSound(soundId));
                    if (response is NotificationSound notificationSound)
                    {
                        if (notificationSound.Sound.Local.IsDownloadingCompleted)
                        {
                            soundFile = notificationSound.Sound;
                        }
                        else
                        {
                            // If notification sound is not yet available
                            // download it and show the notification as is.

                            _clientService.DownloadFile(notificationSound.Sound.Id, 32);
                        }
                    }
                }

                var showPreview = _settings.Notifications.GetShowPreview(chat);

                if (chat.Type is ChatTypeSecret || !showPreview || !_settings.Notifications.ShowName || LifetimeService.Current.Passcode.IsLockscreenRequired)
                {
                    picture = string.Empty;
                    caption = Strings.AppName;
                    content = Strings.YouHaveNewMessage;
                    canReply = false;
                }
                else if (!_settings.Notifications.ShowText)
                {
                    content = Strings.YouHaveNewMessage;
                    canReply = false;
                }
                else if (!_settings.Notifications.ShowReply)
                {
                    canReply = false;
                }

                UpdateToast(caption, content, $"{_sessionService.Id}", silent, silent || soundId == 0, soundFile, launch, $"{id}", $"{groupId}", picture, dateTime, canReply);
            }
        }

        private bool UpdateAsync(Chat chat, Message message)
        {
            try
            {
                var active = WindowContext.Active;
                if (active == null)
                {
                    return true;
                }

                var service = active.NavigationServices?.GetByFrameId($"Main{_clientService.SessionId}");
                if (service == null)
                {
                    return true;
                }

                if (chat.ViewAsTopics && service.CurrentPageType == typeof(ChatPage) && service.CurrentPageParam is ChatMessageTopic args)
                {
                    if (args.ChatId == chat.Id && args.MessageTopic.AreTheSame(message.TopicId))
                    {
                        Logger.Info("Topic is open");
                        return false;
                    }
                }
                else if (service.IsChatOpen(chat.Id, true))
                {
                    Logger.Info("Chat is open");
                    return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private void UpdateToast(string caption, string message, string account, bool suppressPopup, bool silent, Td.Api.File soundFile, string launch, string tag, string group, string picture, string date, bool canReply)
        {
            // The account suffix of "Show notifications from -> All accounts". This used to live
            // inside the #else below, i.e. Windows only, while the Linux branch shipped the switch
            // and the mock-up that PREVIEWS the suffix (SettingsNotificationsPage.xaml.cs:31-42,
            // ConvertName, which draws "Title -> Account"). So half the switch worked - inactive
            // accounts did notify, decided at :532 - and the visible half never did: the banner
            // never carried the account name. Hoisted above the #if so both platforms build the
            // same caption; `caption` is a by-value parameter and is used below on both sides.
            if (LifetimeService.Current.Count > 1
                && AppSettings.IsAllAccountsNotifications
                && _clientService.TryGetUser(_clientService.Options.MyId, out User captionUser))
            {
                caption = string.Format("{0} ⭢ {1}", caption, captionUser.FullName());
            }

#if LINUX
            // The whole XML document below is the Windows toast. Here the same six decisions
            // (title, body, avatar, sound, actions, which banner this replaces) are arguments of
            // one D-Bus call. The one deliberately dropped is suppressPopup, because a freedesktop
            // server has no "put it straight into the action center" (replaces_id already keeps
            // one banner per chat instead of a pile).
            //
            // The sound follows the Windows rule exactly: a custom sound silences the banner --
            // <audio silent='true'/> there, the suppress-sound hint here -- and the app plays the
            // file itself, because neither a toast nor a freedesktop server can be handed one.
            ShowNotification(caption, message, silent || soundFile != null, launch, group, picture, canReply);

            if (soundFile != null)
            {
                SoundEffects.Play(soundFile);
            }
#else
            var xml = $"<toast launch='{launch}' displayTimestamp='{date}'>";
            xml += "<visual><binding template='ToastGeneric'>";

            if (!string.IsNullOrEmpty(picture))
            {
                xml += $"<image placement='appLogoOverride' hint-crop='circle' src='{picture}'/>";
            }

            // The account suffix used to be computed here; it is now built above the #if so that
            // Linux gets it too. See the comment there.

            xml += $"<text><![CDATA[{caption}]]></text><text><![CDATA[{message}]]></text>";
            xml += "</binding></visual>";

            if (!string.IsNullOrEmpty(group) && canReply)
            {
                xml += string.Format("<actions><input id='input' type='text' placeHolderContent='{0}' /><action activationType='background' placement='contextMenu' arguments='action=markAsRead&amp;", Strings.Reply);
                xml += launch;
                xml += string.Format("' content='{0}'/><action activationType='background' arguments='action=reply&amp;", Strings.MarkAsRead);
                xml += launch;
                xml += string.Format("' hint-inputId='input' content='{0}'/></actions>", Strings.Send);
            }

            /* Single notification with unread count:
<toast>
  <visual>
    <binding template="ToastGeneric">
      <text>Hello World</text>
      <text>This is a simple toast message</text>

      <group>
          <subgroup>
              <text hint-style="bodySubtle" hint-align="center">text</text>
          </subgroup>
      </group>
    </binding>
  </visual>
</toast>    */
            if (silent || soundFile != null)
            {
                xml += "<audio silent='true'/>";
            }

            xml += "</toast>";

            try
            {
                var notifier = ToastNotificationManager.CreateToastNotifier("App");
                var document = new XmlDocument();
                document.LoadXml(xml);

                var notification = new ToastNotification(document);

                if (!string.IsNullOrEmpty(tag))
                {
                    notification.Tag = tag;
                    notification.RemoteId = tag;
                }

                if (!string.IsNullOrEmpty(group))
                {
                    notification.Group = account + "_" + group;
                    notification.RemoteId += "_";
                    notification.RemoteId += group;
                }

                var ticks = Logger.TickCount;

                notification.SuppressPopup = suppressPopup || ticks - _lastShownToast <= 7000;
                notifier.Show(notification);

                if (ticks - _lastShownToast <= 7000)
                {
                    Logger.Info("Suppress popup");
                }

                if (soundFile != null && notifier.Setting == NotificationSetting.Enabled)
                {
                    SoundEffects.Play(soundFile);
                }

                if (_lastShownToast == 0 || ticks - _lastShownToast > 7000)
                {
                    _lastShownToast = ticks;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
            }
#endif
        }

        private ulong _lastShownToast;

        public string GetLaunch(Chat chat, Message message)
        {
            var launch = string.Format(CultureInfo.InvariantCulture, "chat_id={0}", chat.Id);
            launch = string.Format(CultureInfo.InvariantCulture, "{0}&amp;session={1}", launch, _clientService.SessionId);

            if (chat.Type is not ChatTypePrivate and not ChatTypeSecret)
            {
                launch = string.Format(CultureInfo.InvariantCulture, "{0}&amp;msg_id={1}", launch, message.Id);
            }

            if (message.TopicId is MessageTopicForum messageTopicForum)
            {
                launch = string.Format(CultureInfo.InvariantCulture, "{0}&amp;forum_topic_id={1}", launch, messageTopicForum.ForumTopicId);
            }
            else if (message.TopicId is MessageTopicSavedMessages messageTopicSavedMessages)
            {
                launch = string.Format(CultureInfo.InvariantCulture, "{0}&amp;saved_messages_topic_id={1}", launch, messageTopicSavedMessages.SavedMessagesTopicId);
            }
            else if (message.TopicId is MessageTopicDirectMessages messageTopicDirectMessagesChat)
            {
                launch = string.Format(CultureInfo.InvariantCulture, "{0}&amp;feedback_chat_topic_id={1}", launch, messageTopicDirectMessagesChat.DirectMessagesChatTopicId);
            }
            else if (message.TopicId is MessageTopicThread messageTopicThread)
            {
                launch = string.Format(CultureInfo.InvariantCulture, "{0}&amp;thread_id={1}", launch, messageTopicThread.MessageThreadId);
            }

            return launch;
        }

        public async Task ProcessAsync(Dictionary<string, string> data)
        {
            var state = await _clientService.GetAuthorizationStateAsync();
            if (state is not AuthorizationStateReady)
            {
                return;
            }

            if (data.TryGetValue("action", out string action))
            {
                var chat = default(Chat);
                if (data.TryGetValue("chat_id", out string chat_id) && long.TryParse(chat_id, out long chatId))
                {
                    _clientService.TryGetChat(chatId, out chat);
                    chat ??= await _clientService.SendAsync(new GetChat(chatId)) as Chat;
                }

                if (chat == null)
                {
                    return;
                }

                if (string.Equals(action, "reply", StringComparison.OrdinalIgnoreCase) && data.TryGetValue("input", out string text))
                {
                    var messageText = text.Replace("\r\n", "\n").Replace('\v', '\n').Replace('\r', '\n');
                    var formatted = ClientEx.ParseMarkdown(messageText);

                    // TODO: topic id

                    var replyToMessage = data.TryGetValue("msg_id", out string msg_id) && long.TryParse(msg_id, out long messageId) ? new InputMessageReplyToMessage(messageId, null, 0, string.Empty) : null;
                    var response = await _clientService.SendAsync(new SendMessage(chat.Id, null, replyToMessage, new MessageSendOptions(null, false, true, 0, false, null, 0, 0, false), new InputMessageText(formatted, null, false)));

                    if (chat.Type is ChatTypePrivate && chat.LastMessage != null)
                    {
                        await _clientService.SendAsync(new ViewMessages(chat.Id, new long[] { chat.LastMessage.Id }, new MessageSourceNotification(), true));
                    }
                }
                else if (string.Equals(action, "markasread", StringComparison.OrdinalIgnoreCase) && chat.LastMessage != null)
                {
                    await _clientService.SendAsync(new ViewMessages(chat.Id, new long[] { chat.LastMessage.Id }, new MessageSourceNotification(), true));
                }
            }
        }


















#if LINUX
        #region Linux desktop notifications

        /// <summary>
        /// One banner per notification group, which is one per chat: the key the server's
        /// <c>replaces_id</c> is looked up with. Same identity the Windows toast gets from its
        /// Group, so a second message updates the banner instead of piling another one on.
        /// </summary>
        private string GetNotificationKey(int groupId)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}_{1}", _clientService.SessionId, groupId);
        }

        private string GetNotificationKey(string group)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}_{1}", _clientService.SessionId, group);
        }

        /// <summary>
        /// The avatar, as a token this file's own <see cref="ShowNotification"/> turns into pixels.
        ///
        /// <para>It is a string and not a bitmap because the shared code above treats the result of
        /// <c>GetPhoto</c> as an opaque handle and CLEARS it with <c>string.Empty</c> when the
        /// preview has to be hidden (secret chats, no preview, the lock screen). Keeping that
        /// contract means the four branches that decide what a notification may show do not need a
        /// single <c>#if</c>.</para>
        ///
        /// <para>The source is <c>ProfilePictureSource.Chat</c>, the very same call the chat list makes,
        /// so a chat looks the same in the list and in the banner -- including the coloured
        /// initials when there is no photo yet, which is most of the time on a fresh install.</para>
        /// </summary>
        private string GetAvatar(Chat chat)
        {
            try
            {
                var source = ProfilePictureSource.Chat(_clientService, chat);

                if (source is ProfilePictureSourcePhoto photo)
                {
                    if (photo.Photo != null && photo.Photo.Local.IsDownloadingCompleted)
                    {
                        return "file\n" + photo.Photo.Local.Path;
                    }

                    if (photo.Photo != null)
                    {
                        // Not there yet: ask for it, so the NEXT message of this chat has a face.
                        _clientService.DownloadFile(photo.Photo.Id, 16);
                    }

                    source = photo.Text;
                }

                if (source is ProfilePictureSourceText text)
                {
                    return string.Format(CultureInfo.InvariantCulture, "text\n{0}\n{1}\n{2}\n{3}",
                        text.Initials, ToArgb(text.TopColor), ToArgb(text.BottomColor), text.IsGlyph ? 1 : 0);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Cannot describe the notification avatar", ex);
            }

            return string.Empty;
        }

        private static uint ToArgb(Windows.UI.Color color)
        {
            return (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
        }

        private void ShowNotification(string caption, string message, bool silent, string launch, string group, string picture, bool canReply)
        {
            var request = new NotificationRequest
            {
                Summary = caption,
                Body = message,
                Silent = silent,
                CanReply = canReply,
                Payload = launch,
                Image = CreateAvatar(picture)
            };

            _ = DesktopNotifications.Current.ShowAsync(GetNotificationKey(group), request);
        }

        private static NotificationImage CreateAvatar(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor))
            {
                return null;
            }

            var parts = descriptor.Split('\n');

            if (parts[0] == "file" && parts.Length > 1)
            {
                var image = NotificationAvatar.FromFile(parts[1]);
                if (image != null)
                {
                    return image;
                }
            }
            else if (parts[0] == "text" && parts.Length > 4)
            {
                return NotificationAvatar.FromInitials(parts[1],
                    uint.Parse(parts[2], CultureInfo.InvariantCulture),
                    uint.Parse(parts[3], CultureInfo.InvariantCulture),
                    parts[4] == "1",
                    GlyphFontPath);
            }

            return null;
        }

        /// <summary>
        /// Telegram.ttf, the font the icon glyphs live in: Saved Messages, the replies bot and a
        /// deleted account have a glyph where everyone else has initials.
        /// </summary>
        private static string GlyphFontPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Telegram.ttf");

        private async void OnNotificationActivated(object sender, DesktopNotificationActivatedEventArgs e)
        {
            var data = ParseLaunch(e.Payload);
            if (data == null)
            {
                return;
            }

            // The event is static and every session subscribes to it, so each one has to check
            // that the banner was its own; on Windows the activation arguments are routed to the
            // right session before any of this runs.
            if (!data.TryGetValue("session", out string session)
                || !int.TryParse(session, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sessionId)
                || sessionId != _clientService.SessionId)
            {
                return;
            }

            if (e.Action == DesktopNotificationAction.MarkAsRead)
            {
                data["action"] = "markasread";
                await ProcessAsync(data);
                return;
            }

            if (e.Action == DesktopNotificationAction.Reply && !string.IsNullOrEmpty(e.Text))
            {
                // Only KDE gets here: it is the only server that puts a text field on the banner.
                data["action"] = "reply";
                data["input"] = e.Text;
                await ProcessAsync(data);
                return;
            }

            // Everywhere else -- and that includes GNOME, which is what this port runs on -- the
            // reply button means the same as clicking the banner: open the chat and let the user
            // type there. That is the graceful degradation the spec forces; a button that vanished
            // would be worse than one that takes you where you can answer.
            OpenChat(data);
        }

        /// <summary>
        /// Undoes <see cref="GetLaunch"/>. The launch string is built for XML, so its separators
        /// arrive escaped; both spellings are accepted so that the shared builder stays untouched.
        /// </summary>
        private static Dictionary<string, string> ParseLaunch(string launch)
        {
            if (string.IsNullOrEmpty(launch))
            {
                return null;
            }

            var data = new Dictionary<string, string>();

            foreach (var pair in launch.Replace("&amp;", "&").Split('&'))
            {
                var separator = pair.IndexOf('=');
                if (separator > 0)
                {
                    data[pair.Substring(0, separator)] = pair.Substring(separator + 1);
                }
            }

            return data;
        }

        private void OpenChat(Dictionary<string, string> data)
        {
            if (!data.TryGetValue("chat_id", out string chatIdText) || !long.TryParse(chatIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long chatId))
            {
                return;
            }

            long? messageId = null;
            if (data.TryGetValue("msg_id", out string messageIdText) && long.TryParse(messageIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                messageId = parsed;
            }

            var window = WindowContext.Active ?? WindowContext.Main;
            if (window == null)
            {
                return;
            }

            window.Dispatcher.Dispatch(() =>
            {
                try
                {
                    if (window.NavigationServices?.GetByFrameId($"Main{_clientService.SessionId}") is TLNavigationService service)
                    {
                        service.NavigateToChat(chatId, message: messageId);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Cannot open the chat of a notification", ex);
                }
            });
        }

        #endregion
#endif

        private string GetCaption(Chat chat, bool silent)
        {
            if (chat.Type is ChatTypeSecret)
            {
                return Strings.AppName;
            }

            var title = _clientService.GetTitle(chat);

            if (silent)
            {
                return string.Format("\U0001F515 {0}", title);
            }

            return title;
        }

        private string GetContent(Chat chat, Message message)
        {
            if (chat.Type is ChatTypeSecret)
            {
                return Strings.YouHaveNewMessage;
            }

            var brief = ChatCell.UpdateBriefLabel(message.Content, message.IsOutgoing, null, true, out _);
            var clean = brief.ReplaceSpoilers(false);

            var content = ChatCell.UpdateFromLabel(_clientService, chat, message) + clean.Text;

            if (message.SenderId.IsUser(_clientService.Options.MyId))
            {
                return string.Format("\U0001F4C5 {0}", content);
            }

            return content;
        }

        private string GetPhoto(Chat chat)
        {
#if LINUX
            return GetAvatar(chat);
#else
            try
            {
                var photo = chat.Photo;
                if (photo != null && photo.Small.Local.IsDownloadingCompleted)
                {
                    var relative = Path.GetRelativePath(ApplicationData.Current.LocalFolder.Path, photo.Small.Local.Path);
                    return "ms-appdata:///local/" + relative.Replace('\\', '/');
                }
            }
            catch
            {
                // TODO: race condition
            }

            return string.Empty;
#endif
        }

        public void SetSound(Chat chat, bool silent, XamlRoot xamlRoot)
        {
            if (_settings.Notifications.TryGetScope(chat, out ScopeNotificationSettings scope))
            {
                var settings = chat.NotificationSettings.Clone();
                var value = silent ? 0L : -1;

                var useDefault = !silent;
                if (useDefault)
                {
                    value = scope.SoundId;
                }

                settings.UseDefaultSound = useDefault;
                settings.SoundId = value;

                _clientService.Send(new SetChatNotificationSettings(chat.Id, settings));

                if (silent)
                {
                    ToastPopup.Show(xamlRoot, Strings.SoundOffHint, ToastPopupIcon.SoundOff);
                }
                else
                {
                    ToastPopup.Show(xamlRoot, Strings.SoundOnHint, ToastPopupIcon.SoundOn);
                }
            }
        }

        public void SetMuteFor(Chat chat, int value, XamlRoot xamlRoot)
        {
            if (_settings.Notifications.TryGetScope(chat, out ScopeNotificationSettings scope))
            {
                var settings = chat.NotificationSettings.Clone();

                var useDefault = value == scope.MuteFor || (value >= 366 * 24 * 60 * 60 && scope.MuteFor >= 366 * 24 * 60 * 60);
                if (useDefault)
                {
                    value = scope.MuteFor;
                }

                settings.UseDefaultMuteFor = useDefault;
                settings.MuteFor = value;

                _clientService.Send(new SetChatNotificationSettings(chat.Id, settings));

                if (xamlRoot == null)
                {
                    return;
                }

                if (value == 0)
                {
                    ToastPopup.Show(xamlRoot, Strings.NotificationsUnmutedHint, ToastPopupIcon.Unmute);
                }
                else if (value >= 366 * 24 * 60 * 60)
                {
                    ToastPopup.Show(xamlRoot, Strings.NotificationsMutedHint, ToastPopupIcon.Mute);
                }
                else
                {
                    ToastPopup.Show(xamlRoot, string.Format(Strings.NotificationsMutedForHint, Locale.FormatMuteFor(value)), ToastPopupIcon.MuteFor);
                }
            }
        }

        public void SetMuteFor(ForumTopic topic, int value, XamlRoot xamlRoot)
        {
            if (_clientService.TryGetChat(topic.Info.ChatId, out Chat chat) && _settings.Notifications.TryGetScope(chat, out ScopeNotificationSettings scope))
            {
                var settings = topic.NotificationSettings.Clone();

                var useDefault = value == scope.MuteFor || (value >= 366 * 24 * 60 * 60 && scope.MuteFor >= 366 * 24 * 60 * 60);
                if (useDefault)
                {
                    value = scope.MuteFor;
                }

                settings.UseDefaultMuteFor = useDefault;
                settings.MuteFor = value;

                _clientService.Send(new SetForumTopicNotificationSettings(chat.Id, topic.Info.ForumTopicId, settings));

                if (xamlRoot == null)
                {
                    return;
                }

                if (value == 0)
                {
                    ToastPopup.Show(xamlRoot, Strings.NotificationsUnmutedHint, ToastPopupIcon.Unmute);
                }
                else if (value >= 366 * 24 * 60 * 60)
                {
                    ToastPopup.Show(xamlRoot, Strings.NotificationsMutedHint, ToastPopupIcon.Mute);
                }
                else
                {
                    ToastPopup.Show(xamlRoot, string.Format(Strings.NotificationsMutedForHint, Locale.FormatMuteFor(value)), ToastPopupIcon.MuteFor);
                }
            }
        }
    }
}
