//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Services.Updates;
using Telegram.ViewModels;
using Telegram.ViewModels.Authorization;
using Telegram.ViewModels.Create;
#if !LINUX
using Telegram.ViewModels.Business;
using Telegram.ViewModels.Chats;
#endif
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Settings;
// Both configurations: the Linux subset carries SupergroupMembersViewModel and
// SupergroupAdministratorsViewModel (the members and administrators screens of a group or
// channel), so the namespace is not empty here.
using Telegram.ViewModels.Supergroups;
// Both configurations since 2026-08-26: the thirteen per-category privacy view models are in
// the Linux subset.
using Telegram.ViewModels.Settings.Privacy;
// AddFolderViewModel put this namespace in the subset (u-052); the folder EDITOR is still out.
using Telegram.ViewModels.Folders;
// Both configurations since parcel 3: UserEditViewModel (add to contacts) is in the Linux subset.
using Telegram.ViewModels.Users;
#if !LINUX
using Telegram.ViewModels.Payments;
using Telegram.ViewModels.Premium;
using Telegram.ViewModels.Stars;
#endif
using Telegram.Views;
using Telegram.Views.Authorization;
using Telegram.Views.Create;
#if !LINUX
using Telegram.Views.Business;
using Telegram.Views.Chats;
#endif
using Telegram.Views.Folders;
// AddFolderPopup only; Views/Folders itself (the editor pages) stays out.
using Telegram.Views.Folders.Popups;
using Telegram.Views.Host;
#if !LINUX
using Telegram.Views.Payments;
#endif
using Telegram.Views.Popups;
using Telegram.Views.Settings;
// Both configurations: see the ViewModels.Supergroups using above.
using Telegram.Views.Supergroups;
// Both configurations since 2026-08-26: the thirteen per-category privacy pages are in the
// Linux subset.
using Telegram.Views.Settings.Privacy;
// Both configurations since u-045: SettingsUsernamePopup (Change username) is in the Linux
// subset too.
using Telegram.Views.Settings.Popups;
// Both configurations since parcel 3: see the ViewModels.Users using above.
using Telegram.Views.Users;
#if !LINUX
using Telegram.Views.Premium.Popups;
using Telegram.Views.Stars;
using Telegram.Views.Stars.Popups;
using Telegram.Views.Stories.Popups;
using Telegram.Views.Supergroups.Popups;
#endif
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
#if !LINUX
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.UI.Notifications;
using Windows.UI.ViewManagement;
#endif
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram
{
    sealed partial class App : BootStrapper
    {
#if !LINUX
        private static ExtendedExecutionSession _extendedSession;
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="App"/> class.
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            AppSettings.Initialize();
#if !NET9_0_OR_GREATER
            GarbageCollectionMonitor.Initialize(GC.Collect, AppSettings.Diagnostics.DisableXamlGcCollect, AppSettings.Diagnostics.DisableMemoryPressure);
#endif
            WatchDog.Initialize();
            LifetimeService.Initialize();

            SetApplicationTheme(NightModeService.Current.GetCalculatedApplicationTheme());
            InitializeComponent();

#if LINUX
            // Uno's generated InitializeComponent registers the dependencies' default styles but
            // not the ones of this assembly (Themes/Generic.xaml): without this call every custom
            // control with a DefaultStyleKey gets no template. Generic.xaml also merges dictionaries
            // outside the phase 1 subset, which Uno would fail to locate.
            MissingDictionaries.Register();
            GlobalStaticResources.RegisterDefaultStyles();
#endif
        }

        protected override void OnWindowActivated(WindowContext window, bool active)
        {
            NightModeService.Current.UpdateTimer();

            var navigation = window.GetNavigationService();
            if (navigation != null)
            {
                var aggregator = navigation.Session.Resolve<IEventAggregator>();
                aggregator?.Publish(new UpdateWindowActivated(active));

                var clientService = navigation.Session.Resolve<IClientService>();
                clientService?.Options.Online = active;
            }
        }

#if !LINUX
        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);

            if (args.TaskInstance.TriggerDetails is AppServiceTriggerDetails appService && string.Equals(appService.CallerPackageFamilyName, Package.Current.Id.FamilyName))
            {
                BridgeApplicationContext.Connect(appService.AppServiceConnection, args.TaskInstance);
            }
            else
            {
                var deferral = args.TaskInstance.GetDeferral();

                if (args.TaskInstance.TriggerDetails is ToastNotificationActionTriggerDetail triggerDetail)
                {
                    var data = Toast.GetData(triggerDetail);
                    if (data == null)
                    {
                        deferral.Complete();
                        return;
                    }

                    var session = LifetimeService.Current.ActiveItem.Id;
                    if (data.TryGetValue("session", out string value) && int.TryParse(value, out int result))
                    {
                        session = result;
                    }

                    if (LifetimeService.Current.TryResolve(session, out INotificationsService service))
                    {
                        await service.ProcessAsync(data);
                    }
                }

                deferral.Complete();
            }
        }
#endif

        public override void OnInitialize(IActivatedEventArgs args)
        {
            //Locator.Configure();
            //UnigramContainer.Current.ResolveType<IGenerationService>();

            if (LifetimeService.Current.Passcode.IsEnabled)
            {
                LifetimeService.Current.Passcode.Lock(true);
                InactivityHelper.Initialize(LifetimeService.Current.Passcode.AutolockTimeout);
            }
        }

        public override async void OnStart(WindowContext window, StartKind startKind, IActivatedEventArgs args)
        {
#if DEBUG
            DebugSettings.EnableFrameRateCounter = false;
#endif

#if !LINUX
            if (startKind == StartKind.Activate)
            {
                var sessionId = Toast.GetSession(args);
                if (sessionId != null)
                {
                    if (LifetimeService.Current.ActiveItem.Id != sessionId && LifetimeService.Current.TryResolve(sessionId.Value, out ISession session))
                    {
                        LifetimeService.Current.ActiveItem = session;

                        if (window.Content is RootWindow root)
                        {
                            root.Switch(LifetimeService.Current.ActiveItem);
                        }
                    }
                }
            }
#endif

            var activeSession = LifetimeService.Current.ActiveItem;
            var navigation = window.NavigationServices.GetByFrameId($"{activeSession.Id}");

            var update = activeSession.Resolve<ICloudUpdateService>();
            var service = activeSession.Resolve<IClientService>();

            var state = await service.GetAuthorizationStateAsync();

#if LINUX
            // u-wheel-pointer: off unless UNIGRAM_POINTER_TRACE is set. See PointerTrace for what
            // it measured and why the documented wheel root cause is not the one.
            PointerTrace.Attach(WindowContext.Current);

            // u-wheel-scroll-dead: Uno Skia stubs out the wheel half of ScrollContentPresenter, so
            // nothing scrolls on a notch. See WheelScroll.
            WheelScroll.Attach(WindowContext.Current);

            WindowContext.Current.Activate(args, navigation, state);

            _ = Task.Run(() => OnStartSync(startKind, update));
#else
            if (args is not ShareTargetActivatedEventArgs share)
            {
                window.Activate(args, navigation, state);

                _ = Task.Run(() => OnStartSync(startKind, update));

                if (startKind != StartKind.Launch && window.IsInMainView)
                {
                    var view = ApplicationView.GetForCurrentView();
                    await ApplicationViewSwitcher.TryShowAsStandaloneAsync(view.Id);
                    //view.TryResizeView(WindowContext.Current.Bounds.ToSize());
                }
            }
            else if (window.Content is ShareWindow sharePage)
            {
                sharePage.Activate(share, navigation, state);
            }
#endif
        }

        public override UIElement CreateRootElement(IActivatedEventArgs args, WindowContext window)
        {
#if !LINUX
            var sessionId = Toast.GetSession(args);
            if (sessionId != null)
            {
                if (LifetimeService.Current.ActiveItem.Id != sessionId && LifetimeService.Current.TryResolve(sessionId.Value, out ISession session))
                {
                    LifetimeService.Current.ActiveItem = session;
                }
            }
#endif

            var activeSession = LifetimeService.Current.ActiveItem;
            var navigationService = NavigationServiceFactory(activeSession, window, BackButton.Ignore, $"{activeSession.Id}", true) as NavigationService;

#if !LINUX
            if (args is ShareTargetActivatedEventArgs)
            {
                return new ShareWindow(window, activeSession)
                {
                    FlowDirection = LocaleService.Current.FlowDirection
                };
            }
#endif

            return new RootWindow(window, navigationService)
            {
                FlowDirection = LocaleService.Current.FlowDirection
            };
        }

        public override UIElement CreateRootElement(INavigationService navigationService)
        {
#if LINUX
            // Single window: secondary views are presented by RootPage, the frame is enough.
            return navigationService.Frame;
#else
            return new StandaloneWindow(navigationService)
            {
                FlowDirection = LocaleService.Current.FlowDirection
            };
#endif
        }

        protected override INavigationService CreateNavigationService(ISession session, WindowContext window, Frame frame, string id, bool root)
        {
            if (root)
            {
                return new TLRootNavigationService(session, window, frame, id);
            }

            return new TLNavigationService(session, window, frame, id);
        }

#if LINUX
        private async void OnStartSync(StartKind startKind, ICloudUpdateService updateService = null)
        {
            // What BridgeApplicationContext.LaunchAsync does on Windows, minus the process: the
            // tray icon, the launcher counter and the notification server are all D-Bus objects
            // inside this one. See Telegram.Linux/Platform/DBus.
            await DesktopIntegration.InitializeAsync();

            if (updateService != null)
            {
                await updateService.UpdateAsync(false);
            }
        }
#else
        private async void OnStartSync(StartKind startKind, ICloudUpdateService updateService = null)
        {
            await RequestExtendedExecutionSessionAsync();
            await Toast.RegisterBackgroundTasks();

            try
            {
                TileUpdateManager.CreateTileUpdaterForApplication("App").Clear();
            }
            catch { }

            try
            {
                ToastNotificationManager.History.Clear("App");
            }
            catch { }

            if (Constants.RELEASE && startKind == StartKind.Launch)
            {
                if (await CloudUpdateService.LaunchAsync(true))
                {
                    return;
                }
            }

            if (AppSettings.IsTrayVisible)
            {
                await SystemTray.ShowAsync();
            }
            else if (Constants.RELEASE && startKind == StartKind.Launch)
            {
                await BridgeApplicationContext.AddLoopbackExemptionAsync();
            }

            Windows.ApplicationModel.Core.CoreApplication.EnablePrelaunch(true);

            if (updateService != null)
            {
                await updateService.UpdateAsync(false);
            }
        }

        private async Task RequestExtendedExecutionSessionAsync()
        {
            if (_extendedSession == null && ApiInfo.IsDesktop)
            {
                var session = new ExtendedExecutionSession();
                session.Reason = ExtendedExecutionReason.Unspecified;
                session.Revoked += ExtendedExecutionSession_Revoked;

                var result = await session.RequestExtensionAsync();
                if (result == ExtendedExecutionResult.Allowed)
                {
                    _extendedSession = session;

                    Logger.Info("ExtendedExecutionResult.Allowed");
                }
                else
                {
                    session.Revoked -= ExtendedExecutionSession_Revoked;
                    session.Dispose();

                    Logger.Warning("ExtendedExecutionResult.Denied");
                }
            }
        }

        private void ExtendedExecutionSession_Revoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            Logger.Warning(args.Reason);
            _extendedSession?.Dispose();
            _extendedSession = null;
        }
#endif

        public override void OnResuming(object s, object e, AppExecutionState previousExecutionState)
        {
            Logger.Info("OnResuming");

            // #1225: Will this work? No one knows.
            foreach (var network in LifetimeService.Current.ResolveAll<INetworkService>())
            {
                network.Reconnect();
            }

            //foreach (var client in TLContainer.Current.ResolveAll<IClientService>())
            //{
            //    client.TryInitialize();
            //}

            // #2034: Will this work? No one knows.
            NightModeService.Current.Update(null);

            OnStartSync(StartKind.Activate);
        }

        public override Task OnSuspendingAsync(object s, SuspendingEventArgs e)
        {
            Logger.Info("OnSuspendingAsync");

            LifetimeService.Current.Passcode.CloseTime = DateTime.UtcNow;

            //return Task.WhenAll(LifetimeService.Current.ResolveAll<IVoipService>().Select(x => x.DiscardAsync()));
            //await Task.WhenAll(LifetimeService.Current.ResolveAll<IClientService>().Select(x => x.CloseAsync()));
            return Task.CompletedTask;
        }

        public override ViewModelBase ViewModelForPage(UIElement page, ISession session)
        {
            var sessionId = session.Id;
#if LINUX
            // Only the pages in the Linux subset; the Windows switch below names every page.
            return page switch
            {
                AuthorizationRecoveryPage => session.Resolve<AuthorizationRecoveryViewModel>(),
                AuthorizationRegistrationPage => session.Resolve<AuthorizationRegistrationViewModel>(),
                AuthorizationPasswordPage => session.Resolve<AuthorizationPasswordViewModel>(),
                AuthorizationCodePage => session.Resolve<AuthorizationCodeViewModel>(),
                AuthorizationEmailAddressPage => session.Resolve<AuthorizationEmailAddressViewModel>(),
                AuthorizationEmailCodePage => session.Resolve<AuthorizationEmailCodeViewModel>(),
                AuthorizationPage signIn => session.Resolve<AuthorizationViewModel, ISignInDelegate>(signIn),
                //
                ProfilePage profile => session.Resolve<ProfileViewModel, IProfileDelegate>(profile),
                //
                // Create a group / create a channel. ShowPopupAsync only sets DataContext and
                // calls OnNavigatedTo when this returns non-null, so without these two entries
                // the popups open with no view model and every x:Bind resolves to nothing - and
                // the OK button, bound to ViewModel.CanCreate, never enables.
                NewGroupPopup => session.Resolve<NewGroupViewModel>(),
                NewChannelPopup => session.Resolve<NewChannelViewModel>(),
                //
                // Accepting a shared-folder invite link. Same reasoning as the two above, and the
                // consequence is worse here: OnNavigatedTo is where AddFolderPopup reads
                // ViewModel.Subtitle and subscribes to it, so with no entry the popup opens blank
                // and the Add button hands an empty selection back through its
                // TaskCompletionSource. StickersPopup does NOT belong in this switch: it is shown
                // with ShowQueuedAsync, not ShowPopupAsync, and resolves its own view model in the
                // constructor -- it needs the DI registration, not a case here.
                AddFolderPopup => session.Resolve<AddFolderViewModel>(),
                FoldersPage => session.Resolve<FoldersViewModel>(),
                FolderPage => session.Resolve<FolderViewModel>(),
                ShareFolderPopup => session.Resolve<ShareFolderViewModel>(),
                RemoveFolderPopup => session.Resolve<RemoveFolderViewModel>(),
                //
                // Starting a conversation with someone you have no chat with yet. TWO entry
                // points reach this same popup and u-052/u-057 and u-056 arrived with one case
                // each: the tg:// new-private-chat link (MessageHelper.OpenTelegramUrl) and the
                // compose pencil on the chat list (MainPage). One case serves both -- the pair is
                // deliberately NOT duplicated, a second identical arm is unreachable. NewContactPopup
                // is reached from the button inside ContactsPopup (ContactsPopup.xaml.cs:159), so it
                // needs its own case or that button opens a popup with nothing behind it.
                ContactsPopup => session.Resolve<ContactsViewModel>(),
                DownloadsPopup => session.Resolve<DownloadsViewModel>(),
                NewContactPopup => session.Resolve<NewContactViewModel>(),
                //
                // The two management screens a group or channel profile can reach: the full
                // member list and the administrator list. Without an entry here the page opens
                // with a null DataContext and every x:Bind resolves to nothing.
                SupergroupMembersPage supergroupMembers => session.Resolve<SupergroupMembersViewModel, ISupergroupMembersDelegate>(supergroupMembers),
                SupergroupAdministratorsPage supergroupAdministrators => session.Resolve<SupergroupAdministratorsViewModel, ISupergroupMembersDelegate>(supergroupAdministrators),
                //
                // "Add to contacts" (parcel 3). Delegable: the page IS the IUserDelegate its view
                // model pushes the user into, so it has to be resolved with the two-argument
                // overload -- with the one-argument one the DataContext would be right and
                // UpdateUser would never be called, which on this page means the photo, the note
                // panel and the share-phone checkbox never appear.
                UserEditPage userEdit => session.Resolve<UserEditViewModel, IUserDelegate>(userEdit),
                //
                SettingsAppearancePage => session.Resolve<SettingsAppearanceViewModel>(),
                // Every visible part of SettingsLanguagePage is an x:Bind on ViewModel - the list
                // itself, the three translation toggles and the search box - so without this line
                // the page opens empty and silent instead of failing.
                SettingsLanguagePage => session.Resolve<SettingsLanguageViewModel>(),
                //
                // Privacy and security, 2026-08-26. Same rule as everything above: these pages are
                // x:Bind from top to bottom, so a page missing from this switch opens with a null
                // DataContext and draws every row empty rather than failing.
                SettingsPrivacyAndSecurityPage => session.Resolve<SettingsPrivacyAndSecurityViewModel>(),
                SettingsPrivacyAllowCallsPage => session.Resolve<SettingsPrivacyAllowCallsViewModel>(),
                SettingsPrivacyAllowChatInvitesPage => session.Resolve<SettingsPrivacyAllowChatInvitesViewModel>(),
                SettingsPrivacyAllowP2PCallsPage => session.Resolve<SettingsPrivacyAllowP2PCallsViewModel>(),
                SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesPage => session.Resolve<SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesViewModel>(),
                SettingsPrivacyShowForwardedPage => session.Resolve<SettingsPrivacyShowForwardedViewModel>(),
                SettingsPrivacyPhonePage => session.Resolve<SettingsPrivacyPhoneViewModel>(),
                // The delegate form, copied from the Windows branch: this page hands the cell its
                // own IUserDelegate so the public-photo preview can draw.
                SettingsPrivacyShowPhotoPage privacyShowPhotoPage => session.Resolve<SettingsPrivacyShowPhotoViewModel, IUserDelegate>(privacyShowPhotoPage),
                SettingsPrivacyShowProfileAudioPage => session.Resolve<SettingsPrivacyShowProfileAudioViewModel>(),
                SettingsPrivacyShowStatusPage => session.Resolve<SettingsPrivacyShowStatusViewModel>(),
                SettingsPrivacyShowBioPage => session.Resolve<SettingsPrivacyShowBioViewModel>(),
                SettingsPrivacyShowBirthdatePage => session.Resolve<SettingsPrivacyShowBirthdateViewModel>(),
                SettingsPrivacyNewChatPage => session.Resolve<SettingsPrivacyNewChatViewModel>(),
                SettingsPrivacyAutosaveGiftsPage => session.Resolve<SettingsPrivacyAutosaveGiftsViewModel>(),
                //
                SettingsSessionsPage => session.Resolve<SettingsSessionsViewModel>(),
                SettingsWebSessionsPage => session.Resolve<SettingsWebSessionsViewModel>(),
                SettingsBlockedChatsPage => session.Resolve<SettingsBlockedChatsViewModel>(),
                SettingsPasswordPage => session.Resolve<SettingsPasswordViewModel>(),
                SettingsAutoDeletePage => session.Resolve<SettingsAutoDeleteViewModel>(),
                //
                // SettingsPage itself and SettingsAdvancedPage were both missing from this branch.
                // Harmless by accident until now - MainPage.xaml.cs assigns SettingsView.DataContext
                // by hand and Advanced's only live row wires itself up under #if LINUX - but
                // SettingsPage.UpdateSelection reads ViewModel.NavigationService, and with the
                // privacy and language rows now navigating, that path is live.
                SettingsPage settings => session.Resolve<SettingsViewModel, ISettingsDelegate>(settings),
                SettingsAdvancedPage => session.Resolve<SettingsAdvancedViewModel>(),
                //
                // Notifications, data and storage, 2026-08-26.
                SettingsNotificationsPage => session.Resolve<SettingsNotificationsViewModel>(),
                SettingsNotificationsExceptionsPage => session.Resolve<SettingsNotificationsExceptionsViewModel>(),
                SettingsDataAndStoragePage => session.Resolve<SettingsDataAndStorageViewModel>(),
                SettingsStoragePage => session.Resolve<SettingsStorageViewModel>(),
                SettingsNetworkPage => session.Resolve<SettingsNetworkViewModel>(),
                //
                // Profile, username and log out, u-045. Same rule as every entry above: a page or
                // popup missing here opens with DataContext null and every x:Bind resolves to
                // nothing rather than failing loudly.
                SettingsProfilePage settingsProfilePage => session.Resolve<SettingsProfileViewModel, IUserDelegate>(settingsProfilePage),
                LogOutPopup => session.Resolve<LogOutViewModel>(),
                SettingsUsernamePopup => session.Resolve<SettingsUsernameViewModel>(),
                // u-053: six pages that already shipped (u-011 themes/backgrounds, u-020
                // power saving/passcode, u-031 proxy) but were missing from this switch, so
                // every one of them opened with DataContext null - the "no visto en pantalla"
                // caveat on all three parcels was this, not an oversight in the pages
                // themselves. Session.Registrations.cs already had all six view models
                // registered somewhere; SettingsThemesViewModel/SettingsThemeViewModel/
                // SettingsBackgroundsViewModel/SettingsPowerSavingViewModel were registered
                // under the WRONG #if (Windows-only) - see the Session.Registrations.cs edit
                // in this same commit.
                SettingsPowerSavingPage => session.Resolve<SettingsPowerSavingViewModel>(),
                SettingsPasscodePage => session.Resolve<SettingsPasscodeViewModel>(),
                SettingsProxyPage => session.Resolve<SettingsProxyViewModel>(),
                SettingsProxyPopup => session.Resolve<SettingsProxyViewModel>(),
                SettingsThemesPage => session.Resolve<SettingsThemesViewModel>(),
                SettingsThemePage => session.Resolve<SettingsThemeViewModel>(),
                SettingsBackgroundsPage => session.Resolve<SettingsBackgroundsViewModel>(),
                // Found by the consolidation #4 mirror check, and older than this wave: three
                // popups opened with ShowPopup from inside the subset that had no arm at all. The
                // switch-versus-registrations check cannot see this direction -- it walks the arms
                // that exist -- so they sat here through every green build.
                //   BackgroundPopup       MessageHelper:2047 (the tg:// background link, u-052)
                //                         and SettingsBackgroundsViewModel:148/176 -- i.e. BOTH
                //                         the link AND picking a chat background from Settings.
                //   ChatNotificationsPopup  SettingsNotificationsExceptionsPage:37
                //   ChooseSoundPopup        SettingsNotificationsExceptionsViewModel:124
                // u-wallpaper-apply-missing: this arm resolved the view model but NOT the
                // delegate, so ViewModel.Delegate stayed null and IBackgroundDelegate.
                // UpdateBackground never ran on Linux. That method is where the popup fills in
                // PrimaryButton.Content ("Apply for all chats"), hides SecondaryButton, sets
                // Service1.Text and shows the blur toggle -- so the apply button existed,
                // laid out and focusable, with NOTHING WRITTEN ON IT. Two independent proofs
                // in the delivery7 log: the delegate's own paint line never appears (only the
                // x:Bind converter's), and OnGettingFocus walks THREE MessageService controls
                // inside the popup -- primary, secondary and cancel -- of which the user could
                // only ever see the one whose Content is set in XAML, Cancel.
                // The Windows arm below has always used the two-argument form; this is the
                // only place in either switch where the two disagree (checked both ways).
                BackgroundPopup backgroundPopup => session.Resolve<BackgroundViewModel, IBackgroundDelegate>(backgroundPopup),
                ChatNotificationsPopup => session.Resolve<ChatNotificationsViewModel>(),
                ChooseSoundPopup => session.Resolve<ChooseSoundViewModel>(),
                // CC-1: the share/forward chat picker. Session.Registrations.cs has the matching
                // half -- ChooseChatsViewModel moved out of its #if !LINUX block.
                ChooseChatsPopup => session.Resolve<ChooseChatsViewModel>(),
                // u-094b: DiagnosticsPage brought into the subset. Session.Registrations.cs has the
                // matching half -- DiagnosticsViewModel moved into its #if LINUX block.
                DiagnosticsPage => session.Resolve<DiagnosticsViewModel>(),
                _ => null
            };
#else
            return page switch
            {
                DiagnosticsPage => session.Resolve<DiagnosticsViewModel>(),
                LogOutPopup => session.Resolve<LogOutViewModel>(),
                ProfilePage profile => session.Resolve<ProfileViewModel, IProfileDelegate>(profile),
                InstantPage => session.Resolve<InstantViewModel>(),
                //
                SettingsPage settings => session.Resolve<SettingsViewModel, ISettingsDelegate>(settings),
                NewContactPopup => session.Resolve<NewContactViewModel>(),
                NewChannelPopup => session.Resolve<NewChannelViewModel>(),
                NewGroupPopup => session.Resolve<NewGroupViewModel>(),
                NewBotPopup => session.Resolve<NewBotViewModel>(),
                UserEditPage userEdit => session.Resolve<UserEditViewModel, IUserDelegate>(userEdit),
                UserAffiliatePage => session.Resolve<UserAffiliateViewModel>(),
                //
                SupergroupChooseMemberPopup => session.Resolve<SupergroupChooseMemberViewModel>(),
                SupergroupAdministratorsPage supergroupAdministrators => session.Resolve<SupergroupAdministratorsViewModel, ISupergroupMembersDelegate>(supergroupAdministrators),
                SupergroupBannedPage supergroupBanned => session.Resolve<SupergroupBannedViewModel, ISupergroupMembersDelegate>(supergroupBanned),
                SupergroupEditAdministratorPopup supergroupEditAdministrator => session.Resolve<SupergroupEditAdministratorViewModel, IMemberPopupDelegate>(supergroupEditAdministrator),
                SupergroupEditLinkedChatPage supergroupEditLinkedChat => session.Resolve<SupergroupEditLinkedChatViewModel, ISupergroupDelegate>(supergroupEditLinkedChat),
                SupergroupEditRestrictedPopup supergroupEditRestricted => session.Resolve<SupergroupEditRestrictedViewModel, IMemberPopupDelegate>(supergroupEditRestricted),
                SupergroupEditStickerSetPopup => session.Resolve<SupergroupEditStickerSetViewModel>(),
                SupergroupEditTypePage supergroupEditType => session.Resolve<SupergroupEditTypeViewModel, ISupergroupEditDelegate>(supergroupEditType),
                SupergroupEditPage supergroupEdit => session.Resolve<SupergroupEditViewModel, ISupergroupEditDelegate>(supergroupEdit),
                SupergroupMembersPage supergroupMembers => session.Resolve<SupergroupMembersViewModel, ISupergroupMembersDelegate>(supergroupMembers),
                SupergroupPermissionsPage supergroupPermissions => session.Resolve<SupergroupPermissionsViewModel, ISupergroupMembersDelegate>(supergroupPermissions),
                SupergroupTopicsPage => session.Resolve<SupergroupTopicsViewModel>(),
                SupergroupDirectMessagesPage => session.Resolve<SupergroupDirectMessagesViewModel>(),
                SupergroupReactionsPopup => session.Resolve<SupergroupReactionsViewModel>(),
                SupergroupProfileColorPage => session.Resolve<SupergroupProfileColorViewModel>(),
                ChatBoostsPage => session.Resolve<ChatBoostsViewModel>(),
                ChatAffiliatePage => session.Resolve<ChatAffiliateViewModel>(),
                //
                AuthorizationRecoveryPage => session.Resolve<AuthorizationRecoveryViewModel>(),
                AuthorizationRegistrationPage => session.Resolve<AuthorizationRegistrationViewModel>(),
                AuthorizationPasswordPage => session.Resolve<AuthorizationPasswordViewModel>(),
                AuthorizationCodePage => session.Resolve<AuthorizationCodeViewModel>(),
                AuthorizationEmailAddressPage => session.Resolve<AuthorizationEmailAddressViewModel>(),
                AuthorizationEmailCodePage => session.Resolve<AuthorizationEmailCodeViewModel>(),
                AuthorizationPage signIn => session.Resolve<AuthorizationViewModel, ISignInDelegate>(signIn),
                //
                FoldersPage => session.Resolve<FoldersViewModel>(),
                FolderPage => session.Resolve<FolderViewModel>(),
                ShareFolderPopup => session.Resolve<ShareFolderViewModel>(),
                AddFolderPopup => session.Resolve<AddFolderViewModel>(),
                RemoveFolderPopup => session.Resolve<RemoveFolderViewModel>(),
                //
                SettingsBlockedChatsPage => session.Resolve<SettingsBlockedChatsViewModel>(),
                SettingsStickersPage => session.Resolve<SettingsStickersViewModel>(),
                //
                SettingsThemePage => session.Resolve<SettingsThemeViewModel>(),
                //
                SettingsAdvancedPage => session.Resolve<SettingsAdvancedViewModel>(),
                SettingsAppearancePage => session.Resolve<SettingsAppearanceViewModel>(),
                SettingsAutoDeletePage => session.Resolve<SettingsAutoDeleteViewModel>(),
                SettingsBackgroundsPage => session.Resolve<SettingsBackgroundsViewModel>(),
                SettingsDataAndStoragePage => session.Resolve<SettingsDataAndStorageViewModel>(),
                SettingsLanguagePage => session.Resolve<SettingsLanguageViewModel>(),
                SettingsNetworkPage => session.Resolve<SettingsNetworkViewModel>(),
                SettingsNightModePage => session.Resolve<SettingsNightModeViewModel>(),
                SettingsNotificationsExceptionsPage => session.Resolve<SettingsNotificationsExceptionsViewModel>(),
                SettingsPasscodePage => session.Resolve<SettingsPasscodeViewModel>(),
                SettingsPasswordPage => session.Resolve<SettingsPasswordViewModel>(),
                SettingsPasskeysPage => session.Resolve<SettingsPasskeysViewModel>(),
                SettingsPrivacyAndSecurityPage => session.Resolve<SettingsPrivacyAndSecurityViewModel>(),
                SettingsProxyPage => session.Resolve<SettingsProxyViewModel>(),
                SettingsProxyPopup => session.Resolve<SettingsProxyViewModel>(),
                SettingsShortcutsPage => session.Resolve<SettingsShortcutsViewModel>(),
                SettingsThemesPage => session.Resolve<SettingsThemesViewModel>(),
                SettingsWebSessionsPage => session.Resolve<SettingsWebSessionsViewModel>(),
                SettingsNotificationsPage => session.Resolve<SettingsNotificationsViewModel>(),
                SettingsSessionsPage => session.Resolve<SettingsSessionsViewModel>(),
                SettingsStoragePage => session.Resolve<SettingsStorageViewModel>(),
                SettingsProfilePage settingsProfilePage => session.Resolve<SettingsProfileViewModel, IUserDelegate>(settingsProfilePage),
                SettingsProfileColorPage => session.Resolve<SettingsProfileColorViewModel>(),
                SettingsPowerSavingPage => session.Resolve<SettingsPowerSavingViewModel>(),
                SettingsPrivacyAllowCallsPage => session.Resolve<SettingsPrivacyAllowCallsViewModel>(),
                SettingsPrivacyAllowChatInvitesPage => session.Resolve<SettingsPrivacyAllowChatInvitesViewModel>(),
                SettingsPrivacyAllowP2PCallsPage => session.Resolve<SettingsPrivacyAllowP2PCallsViewModel>(),
                SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesPage => session.Resolve<SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesViewModel>(),
                SettingsPrivacyShowForwardedPage => session.Resolve<SettingsPrivacyShowForwardedViewModel>(),
                SettingsPrivacyPhonePage => session.Resolve<SettingsPrivacyPhoneViewModel>(),
                SettingsPrivacyShowPhotoPage privacyShowPhotoPage => session.Resolve<SettingsPrivacyShowPhotoViewModel, IUserDelegate>(privacyShowPhotoPage),
                SettingsPrivacyShowProfileAudioPage privacyShowProfileAudioPage => session.Resolve<SettingsPrivacyShowProfileAudioViewModel>(),
                SettingsPrivacyShowStatusPage => session.Resolve<SettingsPrivacyShowStatusViewModel>(),
                SettingsPrivacyShowBioPage => session.Resolve<SettingsPrivacyShowBioViewModel>(),
                SettingsPrivacyShowBirthdatePage => session.Resolve<SettingsPrivacyShowBirthdateViewModel>(),
                SettingsPrivacyNewChatPage => session.Resolve<SettingsPrivacyNewChatViewModel>(),
                SettingsPrivacyAutosaveGiftsPage => session.Resolve<SettingsPrivacyAutosaveGiftsViewModel>(),

                BusinessPage => session.Resolve<BusinessViewModel>(),
                BusinessLocationPage => session.Resolve<BusinessLocationViewModel>(),
                BusinessHoursPage => session.Resolve<BusinessHoursViewModel>(),
                BusinessRepliesPage businessRepliesPage => session.Resolve<BusinessRepliesViewModel, IBusinessRepliesDelegate>(businessRepliesPage),
                BusinessGreetPage => session.Resolve<BusinessGreetViewModel>(),
                BusinessAwayPage => session.Resolve<BusinessAwayViewModel>(),
                BusinessBotsPage => session.Resolve<BusinessBotsViewModel>(),
                BusinessIntroPage => session.Resolve<BusinessIntroViewModel>(),
                BusinessChatLinksPage businessChatLinksPage => session.Resolve<BusinessChatLinksViewModel, IBusinessChatLinksDelegate>(businessChatLinksPage),

                RevenuePage => session.Resolve<RevenueViewModel>(),

                PaymentFormPage => session.Resolve<PaymentFormViewModel>(),
                MessageStatisticsPage => session.Resolve<MessageStatisticsViewModel>(),
                ChatInviteLinksPage => session.Resolve<ChatInviteLinksViewModel>(),
                ChatStatisticsPage => session.Resolve<ChatStatisticsViewModel>(),
                ChatRevenuePage => session.Resolve<ChatRevenueViewModel>(),
                ChatStarsPage => session.Resolve<ChatStarsViewModel>(),
                ChatStoriesPage => session.Resolve<ChatStoriesViewModel>(),

                // Popups
                ContactsPopup => session.Resolve<ContactsViewModel>(),
                CallsPopup => session.Resolve<CallsViewModel>(),
                DownloadsPopup => session.Resolve<DownloadsViewModel>(),
                SettingsUsernamePopup => session.Resolve<SettingsUsernameViewModel>(),
                ChooseChatsPopup => session.Resolve<ChooseChatsViewModel>(),
                ChooseSoundPopup => session.Resolve<ChooseSoundViewModel>(),
                ChatNotificationsPopup => session.Resolve<ChatNotificationsViewModel>(),
                CreateChatPhotoPopup => session.Resolve<CreateChatPhotoViewModel>(),
                PromoPopup => session.Resolve<PromoViewModel>(),
                StarsPage => session.Resolve<StarsViewModel>(),
                BuyPopup => session.Resolve<BuyViewModel>(),
                PayPopup => session.Resolve<PayViewModel>(),
                StoryInteractionsPopup => session.Resolve<StoryInteractionsViewModel>(),
                BackgroundsPopup => session.Resolve<SettingsBackgroundsViewModel>(),
                BackgroundPopup backgroundPopup => session.Resolve<BackgroundViewModel, IBackgroundDelegate>(backgroundPopup),
                _ => null
            };
#endif
        }
    }
}
