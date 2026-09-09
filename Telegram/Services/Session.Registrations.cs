//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;

namespace Telegram.Services
{
    // The registrations SessionResolverGenerator turns into the fields, the constructor and the
    // Resolve<T> switch. Adding a view model here is all that is needed to make it resolvable.
    //
    // Singletons and Lazy are read pairwise, interface then implementation. Order matters only in
    // that the generator sorts singletons by dependency and promotes any lazy one they need.
    [GenerateResolver(
        Self = new Type[]
        {
            typeof(ISession),
        },
        // The one global Resolve hands out; the other four are constructor-only.
        Exposed = new Type[]
        {
#if !LINUX
            typeof(IShortcutsService),
#endif
        },
        Globals = new Type[]
        {
            typeof(Telegram.Services.ILifetimeService),
            typeof(Telegram.Services.ILocaleService),
            typeof(Telegram.Services.IPasscodeService),
#if !LINUX
            typeof(Telegram.Services.IShortcutsService),
#endif
            typeof(Telegram.Services.IProxyService),
            typeof(Telegram.Services.IDownloadFolderService),
        },
        Singletons = new Type[]
        {
            typeof(Telegram.Services.IDeviceInfoService), typeof(Telegram.Services.DeviceInfoService),
            typeof(Telegram.Services.ISettingsService), typeof(Telegram.Services.SettingsService),
            typeof(Telegram.Services.IEventAggregator), typeof(Telegram.Services.EventAggregator),
            typeof(Telegram.Services.IClientService), typeof(Telegram.Services.ClientService),
            typeof(Telegram.Services.IContactsService), typeof(Telegram.Services.ContactsService),
            typeof(Telegram.Services.IVoipService), typeof(Telegram.Services.VoipService),
            typeof(Telegram.Services.INetworkService), typeof(Telegram.Services.NetworkService),
#if !LINUX
            typeof(Telegram.Services.IGenerationService), typeof(Telegram.Services.GenerationService),
#endif
            typeof(Telegram.Services.INotificationsService), typeof(Telegram.Services.NotificationsService),
        },
        Lazy = new Type[]
        {
            typeof(Telegram.Services.ISettingsSearchService), typeof(Telegram.Services.SettingsSearchService),
            typeof(Telegram.Services.ICloudUpdateService), typeof(Telegram.Services.CloudUpdateService),
            typeof(Telegram.Services.ILocationService), typeof(Telegram.Services.LocationService),
            typeof(Telegram.Services.IThemeService), typeof(Telegram.Services.ThemeService),
            typeof(Telegram.Services.IViewService), typeof(Telegram.Services.ViewService),
            typeof(Telegram.Services.IStorageService), typeof(Telegram.Services.StorageService),
            typeof(Telegram.Services.ITranslateService), typeof(Telegram.Services.TranslateService),
#if !LINUX
            typeof(Telegram.Services.IProfilePhotoService), typeof(Telegram.Services.ProfilePhotoService),
            typeof(Telegram.Services.ITextRecognitionService), typeof(Telegram.Services.TextRecognitionService),
#else
            // Owned by LifetimeService on Windows; here each session builds its own on first use.
            typeof(Telegram.Services.IShortcutsService), typeof(Telegram.Services.ShortcutsService),
            // Telegram.Linux/Platform/ProfilePhotoService.cs (the shared one needs the media
            // entities and the cropper). NewGroupViewModel and NewChannelViewModel take it in
            // their constructor, so without this entry both resolve to null and the popups blow
            // up before they are shown.
            typeof(Telegram.Services.IProfilePhotoService), typeof(Telegram.Services.ProfilePhotoService),
#endif
        },
        Instances = new Type[]
        {
            typeof(Telegram.ViewModels.Authorization.AuthorizationViewModel),
            typeof(Telegram.ViewModels.Authorization.AuthorizationRegistrationViewModel),
            typeof(Telegram.ViewModels.Authorization.AuthorizationCodeViewModel),
            typeof(Telegram.ViewModels.Authorization.AuthorizationPasswordViewModel),
            typeof(Telegram.ViewModels.Authorization.AuthorizationRecoveryViewModel),
            typeof(Telegram.ViewModels.Authorization.AuthorizationEmailAddressViewModel),
            typeof(Telegram.ViewModels.Authorization.AuthorizationEmailCodeViewModel),
            typeof(Telegram.ViewModels.MainViewModel),
#if !LINUX
            typeof(Telegram.ViewModels.CallsViewModel),
            typeof(Telegram.ViewModels.SendLocationViewModel),
#endif
            // CC-1: the share/forward chat picker. u-053's pattern applies (both halves needed):
            // this registration, and App.ViewModelForPage's ChooseChatsPopup switch entry below.
            typeof(Telegram.ViewModels.ChooseChatsViewModel),
            typeof(Telegram.ViewModels.DialogViewModel),
            // Scheduled messages: ChatScheduledPage.Activate resolves this one by name, and
            // Session.Resolve returns null for anything not listed here, which would be a null
            // DataContext and an empty screen instead of a failure.
            typeof(Telegram.ViewModels.DialogScheduledViewModel),
#if !LINUX
            typeof(Telegram.ViewModels.DialogBusinessRepliesViewModel),
            typeof(Telegram.ViewModels.DialogWelcomeMessagesViewModel),
            typeof(Telegram.ViewModels.DialogPinnedViewModel),
            typeof(Telegram.ViewModels.DialogEventLogViewModel),
            typeof(Telegram.ViewModels.CreateChatPhotoViewModel),
#endif
            // The four drawer view models below are reached through their own static Create,
            // which does session.Resolve<T>() and then dereferences the result without a null
            // check. Resolve returns null for anything not listed here, so leaving them out of
            // the Linux branch is a NullReferenceException on first use, not a missing drawer:
            // EmojiDrawerViewModel from the reactions bar (ReactionsMenuFlyout), the emoji status
            // button (EmojiMenuFlyout) and the composer panel (StickerPanel), and Sticker and
            // Animation from the sticker and GIF tabs of that same panel. They are kept together
            // as one family on purpose. EffectDrawerViewModel has no caller in the subset today
            // -- MessageEffectMenuFlyout is not in Telegram.Linux.csproj -- and is registered
            // anyway so that whoever brings that flyout in does not walk into the same trap.
            typeof(Telegram.ViewModels.Drawers.AnimationDrawerViewModel),
            typeof(Telegram.ViewModels.Drawers.StickerDrawerViewModel),
            typeof(Telegram.ViewModels.Drawers.EmojiDrawerViewModel),
            typeof(Telegram.ViewModels.Drawers.EffectDrawerViewModel),
            // The profile screen. ProfileViewModel is what App.ViewModelForPage hands to
            // ProfilePage, and the eight below are resolved one by one from the
            // ProfileTabsViewModel constructor - Session.Resolve returns null for anything not
            // listed here, so leaving any of them out is a NullReferenceException in the
            // constructor, not a missing tab.
            typeof(Telegram.ViewModels.ProfileViewModel),
            typeof(Telegram.ViewModels.Profile.ProfileStoriesTabViewModel),
            typeof(Telegram.ViewModels.Profile.ProfileMembersTabViewModel),
            typeof(Telegram.ViewModels.Profile.ProfileGroupsTabViewModel),
            typeof(Telegram.ViewModels.Profile.ProfileChannelsTabViewModel),
            typeof(Telegram.ViewModels.Profile.ProfileBotsTabViewModel),
            typeof(Telegram.ViewModels.Profile.ProfileGiftsTabViewModel),
            typeof(Telegram.ViewModels.Profile.ProfileSavedChatsTabViewModel),
            typeof(Telegram.ViewModels.Profile.ProfileTopicsTabViewModel),
#if LINUX
            // The two management screens a group or channel profile can reach. Both are also
            // resolved by App.ViewModelForPage, so leaving either out is a page with no
            // DataContext, not a missing feature. Under #if LINUX because the Windows block
            // below already lists them and this array is registered entry by entry.
            typeof(Telegram.ViewModels.Supergroups.SupergroupMembersViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupAdministratorsViewModel),
            // Create a group / create a channel. Both are also resolved by App.ViewModelForPage,
            // and both are under #if LINUX here because the Windows block below already lists
            // them and this array is registered entry by entry.
            typeof(Telegram.ViewModels.Create.NewGroupViewModel),
            typeof(Telegram.ViewModels.Create.NewChannelViewModel),
            // The sticker-pack and folder-invite popups (u-052). Session.Resolve returns null for
            // anything not listed, and neither popup survives that: StickersPopup assigns
            // Resolve<StickersViewModel>() to DataContext in its own constructor and
            // ShowAsyncInternal dereferences popup.ViewModel on the very next line, so a missing
            // registration is a NullReferenceException before anything is drawn -- not a blank
            // popup. Both are under #if LINUX because the Windows block below already lists them
            // and this array is registered entry by entry.
            typeof(Telegram.ViewModels.StickersViewModel),
            typeof(Telegram.ViewModels.Folders.AddFolderViewModel),
            typeof(Telegram.ViewModels.Folders.FoldersViewModel),
            typeof(Telegram.ViewModels.Folders.FolderViewModel),
            typeof(Telegram.ViewModels.Folders.ShareFolderViewModel),
            typeof(Telegram.ViewModels.Folders.RemoveFolderViewModel),
            // Starting a conversation from scratch, reached BOTH from the tg:// new-private-chat
            // link (u-052/u-057) and from the compose pencil on the chat list (u-056). Both popups
            // take their DataContext from ShowPopupAsync, so each needs its App.ViewModelForPage
            // case as well as the registration here -- and ContactsPopup opens NewContactPopup
            // itself, which is why the second one is not optional. Registered once, not per entry
            // point: this array is registered entry by entry and a repeat would be a duplicate key.
            typeof(Telegram.ViewModels.ContactsViewModel),
            typeof(Telegram.ViewModels.Create.NewContactViewModel),
            // Found by the consolidation #4 check, not by any parcel: u-045 and u-053 each added
            // App.ViewModelForPage arms for these five while the view models themselves stayed
            // registered under #if !LINUX only. Session.Resolve returns null for anything not in
            // this array, so log out, own profile, change username, the passcode page and the two
            // proxy entries would every one of them have opened with a null DataContext -- which
            // is the exact defect u-053 existed to fix, half-done: it moved the four theme /
            // background / power-saving ones and left these behind. Nothing in a per-parcel build
            // can catch this; only the switch-versus-registrations cross-check does.
            typeof(Telegram.ViewModels.LogOutViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsProfileViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsUsernameViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsPasscodeViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsProxyViewModel),
            // BackgroundPopup's view model, needed by both the tg:// background link and the
            // Settings backgrounds picker. ChatNotificationsViewModel and ChooseSoundViewModel
            // were already on this path; only their switch arms were missing.
            typeof(Telegram.ViewModels.BackgroundViewModel),
            // Parcel 3: UserEditPage is the "add to contacts" screen, and it is a page, so it needs
            // BOTH this registration and its App.ViewModelForPage arm -- Session.Resolve returns
            // null for anything absent here and the page would open with a null DataContext, which
            // is the exact half-done shape the comment above this one describes.
            typeof(Telegram.ViewModels.Users.UserEditViewModel),
            // u-094b: DiagnosticsPage brought into the subset. App.xaml.cs has the matching half --
            // the ViewModelForPage arm moved into its own #if LINUX block.
            typeof(Telegram.ViewModels.DiagnosticsViewModel),
#endif
#if !LINUX
            typeof(Telegram.ViewModels.Users.UserEditViewModel),
            typeof(Telegram.ViewModels.Users.UserAffiliateViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupEditViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupEditTypeViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupEditStickerSetViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupEditAdministratorViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupEditRestrictedViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupEditLinkedChatViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupChooseMemberViewModel),
            typeof(Telegram.ViewModels.Chats.ChatInviteLinksViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupAdministratorsViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupBannedViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupPermissionsViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupTopicsViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupDirectMessagesViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupMembersViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupReactionsViewModel),
            typeof(Telegram.ViewModels.Chats.ChatStatisticsViewModel),
            typeof(Telegram.ViewModels.Chats.ChatBoostsViewModel),
            typeof(Telegram.ViewModels.Chats.ChatRevenueViewModel),
            typeof(Telegram.ViewModels.Chats.ChatStarsViewModel),
            typeof(Telegram.ViewModels.Chats.ChatAffiliateViewModel),
            typeof(Telegram.ViewModels.Chats.MessageStatisticsViewModel),
            typeof(Telegram.ViewModels.Create.NewChannelViewModel),
            typeof(Telegram.ViewModels.Create.NewGroupViewModel),
            typeof(Telegram.ViewModels.Create.NewBotViewModel),
            typeof(Telegram.ViewModels.InstantViewModel),
            typeof(Telegram.ViewModels.LogOutViewModel),
            typeof(Telegram.ViewModels.DiagnosticsViewModel),
            typeof(Telegram.ViewModels.Chats.ChatStoriesViewModel),
            typeof(Telegram.ViewModels.SettingsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsAdvancedViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsStorageViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsNetworkViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsUsernameViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsSessionsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsWebSessionsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsBlockedChatsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsNotificationsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsNotificationsExceptionsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsDataAndStorageViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsProxyViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsPrivacyAndSecurityViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowCallsViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowP2PCallsViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowChatInvitesViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowForwardedViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyPhoneViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowPhoneViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAutosaveGiftsViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowFindingByPhoneNumberViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowPhotoViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowProfileAudioViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowStatusViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowBioViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowBirthdateViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowUnpaidMessagesViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyNewChatViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsAutoDeleteViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsProfileViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsProfileColorViewModel),
            typeof(Telegram.ViewModels.Supergroups.SupergroupProfileColorViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsPasswordViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsPasskeysViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsPasscodeViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsStickersViewModel),
#endif
            // In the Linux subset: SettingsAppearancePage is where the touch-mode checkbox and the
            // interface scale live, SettingsAdvancedPage is where the tray icon and autostart
            // switches of phase 4 do, and SettingsLanguagePage lists what
            // GetLocalizationTargetInfo returns.
            typeof(Telegram.ViewModels.Settings.SettingsAdvancedViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsAppearanceViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsLanguageViewModel),
#if LINUX
            // Privacy and security, 2026-08-26. Every one of these pages is x:Bind from top to
            // bottom, so a view model that is not registered here is a page that opens fully drawn
            // and completely empty - which is the failure mode this whole batch exists to avoid.
            // Listed under #if LINUX because the Windows block below already names them and this
            // array is registered entry by entry.
            typeof(Telegram.ViewModels.Settings.SettingsPrivacyAndSecurityViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowCallsViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowP2PCallsViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowChatInvitesViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowForwardedViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyPhoneViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowPhoneViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAutosaveGiftsViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowFindingByPhoneNumberViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowPhotoViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowProfileAudioViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowStatusViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowBioViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyShowBirthdateViewModel),
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyNewChatViewModel),
            // Declared INSIDE SettingsPrivacyNewChatViewModel.cs, which is why it is easy to miss:
            // SettingsPrivacyNewChatViewModel's constructor does
            // Session.Resolve<SettingsPrivacyAllowUnpaidMessagesViewModel>() and then subscribes to
            // it on the very next line. An unregistered type resolves to NULL, so leaving it out is
            // a NullReferenceException in that constructor - which propagates up through
            // SettingsPrivacyAndSecurityViewModel's constructor and App.ViewModelForPage, so the
            // whole privacy root opens with a null DataContext and EVERY badge blank. Measured
            // 2026-08-26: the page drew all its rows and not one real value.
            typeof(Telegram.ViewModels.Settings.Privacy.SettingsPrivacyAllowUnpaidMessagesViewModel),
            // Active sessions, web sessions, blocked users, two-step verification and the global
            // auto-delete timer.
            typeof(Telegram.ViewModels.Settings.SettingsSessionsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsWebSessionsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsBlockedChatsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsPasswordViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsAutoDeleteViewModel),
            // SettingsPage's own view model, which App.ViewModelForPage now resolves here too.
            // It was only in the Windows list, which held because MainPage assigns
            // SettingsView.DataContext by hand; with the settings rows navigating for real,
            // SettingsPage.UpdateSelection reads ViewModel.NavigationService and needs it.
            typeof(Telegram.ViewModels.SettingsViewModel),
            // Notifications, data and storage.
            typeof(Telegram.ViewModels.Settings.SettingsNotificationsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsNotificationsExceptionsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsDataAndStorageViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsStorageViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsNetworkViewModel),
            typeof(Telegram.ViewModels.ChooseSoundViewModel),
            typeof(Telegram.ViewModels.ChatNotificationsViewModel),
            // u-053: these four were registered under #if !LINUX below even though their pages
            // (SettingsThemesPage/SettingsThemePage/SettingsBackgroundsPage/
            // SettingsPowerSavingPage, u-011/u-020) are in the Linux subset - Resolve<T>() would
            // have thrown rather than returned the view model. SettingsNightModeViewModel and
            // SettingsShortcutsViewModel stay below: neither page is in the Linux subset (auto
            // night-mode schedule is collapsed in SettingsAppearancePage; Shortcuts is
            // unreachable on both platforms, see FALTA.md §2.4).
            typeof(Telegram.ViewModels.Settings.SettingsThemesViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsThemeViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsBackgroundsViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsPowerSavingViewModel),

            // u-downloadspopup: same shape as the four above. DownloadsPopup is in the Linux
            // subset now, so leaving its view model under #if !LINUX would make Resolve<T>()
            // throw at the moment the downloads counter is clicked -- the failure the whole
            // parcel exists to remove.
            typeof(Telegram.ViewModels.DownloadsViewModel),
#endif
#if !LINUX
            typeof(Telegram.ViewModels.Settings.SettingsNightModeViewModel),
            typeof(Telegram.ViewModels.Settings.SettingsShortcutsViewModel),
            typeof(Telegram.ViewModels.BackgroundViewModel),
            typeof(Telegram.ViewModels.StickersViewModel),
            typeof(Telegram.ViewModels.Payments.PaymentAddressViewModel),
            typeof(Telegram.ViewModels.Payments.PaymentCredentialsViewModel),
            typeof(Telegram.ViewModels.Payments.PaymentFormViewModel),
            typeof(Telegram.ViewModels.StoryInteractionsViewModel),
            typeof(Telegram.ViewModels.Folders.FoldersViewModel),
            typeof(Telegram.ViewModels.Folders.FolderViewModel),
            typeof(Telegram.ViewModels.Folders.ShareFolderViewModel),
            typeof(Telegram.ViewModels.Folders.AddFolderViewModel),
            typeof(Telegram.ViewModels.Folders.RemoveFolderViewModel),
            typeof(Telegram.ViewModels.ChooseSoundViewModel),
            typeof(Telegram.ViewModels.ChatNotificationsViewModel),
            typeof(Telegram.ViewModels.Premium.PromoViewModel),
            typeof(Telegram.ViewModels.Stars.StarsViewModel),
            typeof(Telegram.ViewModels.Stars.BuyViewModel),
            typeof(Telegram.ViewModels.Stars.PayViewModel),
            typeof(Telegram.ViewModels.Business.BusinessViewModel),
            typeof(Telegram.ViewModels.Business.BusinessLocationViewModel),
            typeof(Telegram.ViewModels.Business.BusinessHoursViewModel),
            typeof(Telegram.ViewModels.Business.BusinessRepliesViewModel),
            typeof(Telegram.ViewModels.Business.BusinessGreetViewModel),
            typeof(Telegram.ViewModels.Business.BusinessAwayViewModel),
            typeof(Telegram.ViewModels.Business.BusinessBotsViewModel),
            typeof(Telegram.ViewModels.Business.BusinessIntroViewModel),
            typeof(Telegram.ViewModels.Business.BusinessChatLinksViewModel),
            typeof(Telegram.ViewModels.RevenueViewModel),
#endif
        })]
    public partial class SessionImpl
    {
    }
}
