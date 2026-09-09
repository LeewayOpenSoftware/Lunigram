//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.Views.Authorization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

namespace Telegram.Views.Host
{
    public interface IToastHost
    {
        void ToastOpened(TeachingTip toast);
        void ToastClosed(TeachingTip toast);
    }

    public interface IPopupHost
    {
        void PopupOpened();
        void PopupClosed();
    }

    /// <summary>
    /// The Linux RootPage: the shell around the per-session frame, with the accounts pane.
    /// The Windows one (Telegram/Views/Host/RootPage.xaml.cs) also hosts the theme editor, the
    /// animated theme toggle (Win2D) and builds its pane containers in ChoosingItemContainer,
    /// which Uno never raises: here the pane items are their own containers. Same public surface,
    /// MainPage and the services talk to both the same way.
    /// </summary>
    public sealed partial class RootPage : Page, IPopupHost
    {
        private readonly ILifetimeService _lifetime;
        private readonly WindowContext _context;

        private NavigationService _navigationService;

        private RootDestination _navigationViewSelected;

        private long _menuSessions;

        public RootPage(WindowContext context, NavigationService service)
        {
            InitializeComponent();

            _lifetime = LifetimeService.Current;
            _context = context;

            _navigationViewSelected = RootDestination.Chats;

            service.Frame.Navigating += OnNavigating;
            service.Frame.Navigated += OnNavigated;
            service.FrameFacade.ShortcutInvoked += OnShortcutInvoked;

            _navigationService = service;
            InitializeNavigation(service.Frame);

            Navigation.Content = _navigationService.Frame;
        }

        public INavigationService NavigationService
        {
            get
            {
                if (_navigationService?.Frame.Content is MainPage mainPage)
                {
                    return mainPage.NavigationService;
                }

                return null;
            }
        }

        public void PopupOpened()
        {
            if (_navigationService?.Frame.Content is IRootContentPage content)
            {
                content.PopupOpened();
            }
        }

        public void PopupClosed()
        {
            if (_navigationService?.Frame.Content is IRootContentPage content)
            {
                content.PopupClosed();
            }
        }

        public void UpdateComponent()
        {
            _navigationViewSelected = RootDestination.Chats;
            InitializeSessions(AppSettings.IsAccountsSelectorExpanded);
        }

        public void Create()
        {
            var premium = 0;
            var count = 0;

            foreach (var session in _lifetime.Items)
            {
                if (session.Settings.UseTestDC)
                {
                    continue;
                }

                if (session.ClientService.Options.IsPremium)
                {
                    premium++;
                }

                count++;
            }

            var limit = 3;

            if (count >= limit + premium)
            {
                _navigationService.ShowLimitReached(new PremiumLimitTypeConnectedAccounts());
                return;
            }

            Switch(_lifetime.Create());
        }

        public void Switch(ISession session)
        {
            _lifetime.ActiveItem = session;

            if (_lifetime.ActiveItem != session)
            {
                InitializeSessions(AppSettings.IsAccountsSelectorExpanded);
                return;
            }

            if (_navigationService != null)
            {
                Destroy(_navigationService);
            }

            Navigation.IsPaneOpen = false;

            var service = _context.NavigationServices.GetByFrameId($"{session.Id}") as NavigationService;
            if (service == null)
            {
                service = BootStrapper.Current.NavigationServiceFactory(session, _context, BootStrapper.BackButton.Attach, new Frame { CacheSize = 0 }, $"{session.Id}", true) as NavigationService;
                service.Frame.Navigating += OnNavigating;
                service.Frame.Navigated += OnNavigated;
                service.FrameFacade.ShortcutInvoked += OnShortcutInvoked;

                switch (session.ClientService.AuthorizationState)
                {
                    case AuthorizationStateReady:
                        service.Navigate(typeof(MainPage));
                        break;
                    case AuthorizationStateWaitPhoneNumber:
                    case AuthorizationStateWaitOtherDeviceConfirmation:
                        service.Navigate(typeof(AuthorizationPage));
                        service.AddToBackStack(typeof(BlankPage));
                        break;
                    case AuthorizationStateWaitCode:
                        service.Navigate(typeof(AuthorizationCodePage), navigationStackEnabled: false);
                        break;
                    case AuthorizationStateWaitEmailAddress:
                        service.Navigate(typeof(AuthorizationEmailAddressPage), navigationStackEnabled: false);
                        break;
                    case AuthorizationStateWaitEmailCode:
                        service.Navigate(typeof(AuthorizationEmailCodePage), navigationStackEnabled: false);
                        break;
                    case AuthorizationStateWaitRegistration:
                        service.Navigate(typeof(AuthorizationRegistrationPage), navigationStackEnabled: false);
                        break;
                    case AuthorizationStateWaitPassword:
                        service.Navigate(typeof(AuthorizationPasswordPage), navigationStackEnabled: false);
                        break;
                }

                var counters = session.ClientService.GetUnreadCount(new ChatListMain());
                if (counters != null)
                {
                    session.Aggregator.Publish(counters.UnreadChatCount);
                    session.Aggregator.Publish(counters.UnreadMessageCount);
                }

                session.Aggregator.Publish(new UpdateConnectionState(session.ClientService.ConnectionState));
            }

            _navigationService = service;
            Navigation.Content = service.Frame;
        }

        private void Destroy(NavigationService master)
        {
            if (master.Frame.Content is IRootContentPage content)
            {
                content.Root = null;
                content.Dispose();
            }

            var detail = WindowContext.Current.NavigationServices.GetByFrameId($"Main{master.FrameFacade.FrameId}");
            if (detail != null)
            {
                detail.Suspend();
                detail.ClearCache();
            }

            master.Frame.Navigating -= OnNavigating;
            master.Frame.Navigated -= OnNavigated;
            master.FrameFacade.ShortcutInvoked -= OnShortcutInvoked;
            master.Suspend();

            WindowContext.Current.NavigationServices.Remove(master);
            WindowContext.Current.NavigationServices.Remove(detail);
        }

        private void OnNavigating(object sender, NavigatingCancelEventArgs e)
        {
            if (_navigationService?.Frame?.Content is IRootContentPage content)
            {
                content.Root = null;
            }
        }

        private void OnNavigated(object sender, NavigationEventArgs e)
        {
            if (e.Content is IRootContentPage content)
            {
                content.Root = this;
                InitializeNavigation(sender as Frame);
            }
        }

        private void InitializeNavigation(Frame frame)
        {
            if (frame?.Content is MainPage page && page.ViewModel != null)
            {
                InitializeUser(page.ViewModel.ClientService);
                InitializeSessions(page.ViewModel.ClientService, AppSettings.IsAccountsSelectorExpanded);
            }
        }

        private async void InitializeUser(IClientService clientService)
        {
            var user = clientService.GetUser(clientService.Options.MyId);
            user ??= await clientService.SendAsync(new GetMe()) as User;

            if (user == null)
            {
                return;
            }

            Photo.Source = ProfilePictureSource.User(clientService, user);
            NameLabel.Text = user.FullName();

            if (AppSettings.Diagnostics.HidePhoneNumber)
            {
                PhoneLabel.Text = "+42 --- --- ----";
            }
            else
            {
                PhoneLabel.Text = PhoneNumber.Format(user.PhoneNumber);
            }

            ExpandedRotation.Angle = AppSettings.IsAccountsSelectorExpanded ? 180 : 0;
        }

        private void InitializeSessions(bool show)
        {
            if (_navigationService != null)
            {
                InitializeSessions(_navigationService.Session.ClientService, show);
            }
        }

        // The Windows build patches the list in place; with a dozen items rebuilding it is simpler
        // and the containers (which are the items) come out fresh.
        private void InitializeSessions(IClientService clientService, bool show)
        {
            var items = _lifetime.GetItemsForMenu(show, out long sessionsHash);

            var hasArchived = _navigationService.Session.Settings.HideArchivedChats;
            var hasPremium = clientService.IsPremium;

            var list = new List<object>();

            if (show)
            {
                foreach (var session in items)
                {
                    list.Add(CreateSessionItem(session));
                }

#if !DEBUG
                if (items.Count < 4)
#endif
                {
                    list.Add(CreateDestinationItem(RootDestination.AddAccount, clientService));
                }

                list.Add(new Controls.NavigationViewItemSeparator());
            }

            if (hasArchived)
            {
                list.Add(CreateDestinationItem(RootDestination.ArchivedChats, clientService));
            }

            list.Add(CreateDestinationItem(RootDestination.SavedMessages, clientService));
            list.Add(CreateDestinationItem(RootDestination.MyProfile, clientService));

            if (hasPremium)
            {
                list.Add(CreateDestinationItem(RootDestination.Status, clientService));
            }

            list.Add(new Controls.NavigationViewItemSeparator());
            list.Add(CreateDestinationItem(RootDestination.Contacts, clientService));
            list.Add(CreateDestinationItem(RootDestination.NewGroup, clientService));
            list.Add(CreateDestinationItem(RootDestination.NewChannel, clientService));
            list.Add(CreateDestinationItem(RootDestination.Chats, clientService));
            // u-063: Contactos entra de vuelta - ContactsPopup lleva en el subconjunto desde u-056.
            // «Llamadas» sigue sin anadirse: la rama que la atiende (CallsPopup) sigue bajo
            // #if !LINUX en MainPage.NavigationView_ItemClick, y CallsPopup no esta en
            // Telegram.Linux.csproj - anadirla dibujaria un icono que al pulsarlo solo deja una
            // linea de log. Cuando entre, se anade aqui igual que Contactos.
            list.Add(CreateDestinationItem(RootDestination.Settings, clientService));
            list.Add(new Controls.NavigationViewItemSeparator());
            list.Add(CreateDestinationItem(RootDestination.Tips, clientService));
            list.Add(CreateDestinationItem(RootDestination.News, clientService));

            NavigationViewList.ItemsSource = list;

            _menuSessions = sessionsHash;
            ExpandedRotation.Angle = show ? 180 : 0;
        }

        #region Pane items

        private Controls.NavigationViewItem CreateDestinationItem(RootDestination destination, IClientService clientService)
        {
            var content = new Controls.NavigationViewItem
            {
                Tag = destination,
                IsChecked = destination == _navigationViewSelected,
                BadgeVisibility = Visibility.Collapsed
            };

            switch (destination)
            {
                case RootDestination.AddAccount:
                    content.Text = Strings.AddAccount;
                    content.Glyph = Icons.PersonAdd;
                    break;

                case RootDestination.NewGroup:
                    content.Text = Strings.NewGroup;
                    content.Glyph = Icons.People;
                    break;
                case RootDestination.NewChannel:
                    content.Text = Strings.NewChannel;
                    content.Glyph = Icons.Megaphone;
                    break;
                case RootDestination.Chats:
                    content.Text = Strings.FilterChats;
                    content.Glyph = Icons.ChatMultiple;
                    break;
                case RootDestination.Contacts:
                    content.Text = Strings.Contacts;
                    content.Glyph = Icons.Person;
                    break;
                case RootDestination.Calls:
                    content.Text = Strings.Calls;
                    content.Glyph = Icons.Call;
                    break;
                case RootDestination.Settings:
                    content.Text = Strings.Settings;
                    content.Glyph = Icons.Settings;
                    break;

                case RootDestination.ArchivedChats:
                    content.Text = Strings.ArchivedChats;
                    content.Glyph = Icons.Archive;
                    break;
                case RootDestination.SavedMessages:
                    content.Text = Strings.SavedMessages;
                    content.Glyph = Icons.Bookmark;
                    break;

                case RootDestination.Status:
                    if (clientService.TryGetUser(clientService.Options.MyId, out User user))
                    {
                        content.Text = user.EmojiStatus == null ? Strings.SetEmojiStatus : Strings.ChangeEmojiStatus;
                        content.Glyph = user.EmojiStatus == null ? Icons.EmojiAdd : Icons.EmojiEdit;
                    }
                    break;
                case RootDestination.MyProfile:
                    content.Text = Strings.MyProfile;
                    content.Glyph = Icons.PersonCircle;
                    break;

                case RootDestination.Tips:
                    content.Text = Strings.TelegramFeatures;
                    content.Glyph = Icons.QuestionCircle;
                    break;
                case RootDestination.News:
                    content.Text = Strings.News;
                    content.Glyph = Icons.Megaphone;
                    break;
            }

            return content;
        }

        // Same layout as the SessionItemTemplate of the Windows RootPage.xaml, built in code so
        // that the container, its content and the unread badge are wired without x:Bind.
        private ListViewItem CreateSessionItem(ISession session)
        {
            var photo = new ProfilePicture
            {
                Size = 28,
                Margin = new Thickness(10, 0, 10, 0)
            };

            var indicator = new Rectangle
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 0, 0, 0),
                Width = 3,
                Height = 16,
                RadiusX = 2,
                RadiusY = 2,
                Visibility = session.IsActive ? Visibility.Visible : Visibility.Collapsed
            };

            ElementTheme? appliedTheme = null;

            void ApplyIndicatorTheme()
            {
                var theme = indicator.ActualTheme;
                if (appliedTheme == theme)
                {
                    return;
                }

                appliedTheme = theme;
                indicator.Fill = LookupBrush("NavigationViewSelectionIndicatorForeground", theme);
            }

            // Application.Current.Resources.TryGetValue alone is fixed to Uno's startup theme.
            // Use both hooks: Loaded fixes containers rebuilt after leaving Appearance, while
            // ActualThemeChanged covers a switch made with this account row still in the tree.
            ApplyIndicatorTheme();
            indicator.Loaded += (sender, args) => ApplyIndicatorTheme();
            indicator.ActualThemeChanged += (sender, args) => ApplyIndicatorTheme();

            var botVerified = new CustomEmojiIcon
            {
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, -2, 4, -2)
            };

            var title = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            if (Application.Current.Resources.TryGetValue("EmojiThemeFontFamily", out object fontFamily) && fontFamily is FontFamily emoji)
            {
                title.FontFamily = emoji;
            }

            var identity = new IdentityIcon
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 0, 0)
            };

            var info = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition());
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(title, 1);
            Grid.SetColumn(identity, 2);
            info.Children.Add(botVerified);
            info.Children.Add(title);
            info.Children.Add(identity);

            var badge = new BadgeControl
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 12, 0)
            };

            var root = new Grid
            {
                Margin = new Thickness(0, 6, 0, 6)
            };

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 1);
            Grid.SetColumn(badge, 2);
            root.Children.Add(photo);
            root.Children.Add(indicator);
            root.Children.Add(info);
            root.Children.Add(badge);

            var user = session.ClientService.GetUser(session.UserId);
            if (user != null)
            {
                title.Text = user.FullName();
                photo.Source = ProfilePictureSource.User(session.ClientService, user);
                identity.SetStatus(session.ClientService, user, botVerified);
            }
            else
            {
                session.ClientService.Send(new GetUser(session.UserId));
            }

            void UpdateBadge()
            {
                badge.Text = Formatter.ShortNumber(session.UnreadCount);
                badge.IsUnmuted = session.IsUnmuted;
                badge.Visibility = session.ShowCount ? Visibility.Visible : Visibility.Collapsed;
            }

            void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                UpdateBadge();
            }

            UpdateBadge();

            var container = new ListViewItem
            {
                Tag = session,
                Content = root,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0)
            };

            container.Loaded += (s, args) => session.PropertyChanged += OnPropertyChanged;
            container.Unloaded += (s, args) => session.PropertyChanged -= OnPropertyChanged;

            AutomationProperties.SetName(container, user?.FullName() ?? string.Empty);
            return container;
        }

        private static Brush LookupBrush(string key, ElementTheme theme)
        {
            var resources = Application.Current?.Resources;
            if (resources == null)
            {
                return null;
            }

            if (TryLookupThemed(resources, key, theme == ElementTheme.Dark ? "Dark" : "Light", theme == ElementTheme.Dark ? "Default" : null, out Brush themed))
            {
                return themed;
            }

            return resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : null;
        }

        private static bool TryLookupThemed(ResourceDictionary dictionary, string key, string primary, string secondary, out Brush result)
        {
            if (dictionary.ThemeDictionaries != null)
            {
                if (dictionary.ThemeDictionaries.TryGet(primary, out ResourceDictionary first) && first.TryGet(key, out result))
                {
                    return true;
                }

                if (secondary != null && dictionary.ThemeDictionaries.TryGet(secondary, out ResourceDictionary second) && second.TryGet(key, out result))
                {
                    return true;
                }
            }

            var merged = dictionary.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (TryLookupThemed(merged[i], key, primary, secondary, out result))
                {
                    return true;
                }
            }

            result = null;
            return false;
        }

        #endregion

        private void Expand_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.IsAccountsSelectorExpanded = !AppSettings.IsAccountsSelectorExpanded;
            InitializeSessions(AppSettings.IsAccountsSelectorExpanded);
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            // The items are their own containers: what was clicked is what we created.
            if (e.ClickedItem is FrameworkElement { Tag: ISession session })
            {
                if (session.IsActive)
                {
                    return;
                }

                Switch(session);
            }
            else if (e.ClickedItem is FrameworkElement { Tag: RootDestination destination })
            {
                if (destination is RootDestination.AddAccount)
                {
                    Create();
                }
                else if (_navigationService?.Frame?.Content is IRootContentPage content)
                {
                    content.NavigationView_ItemClick(destination);
                }
            }
            else
            {
                return;
            }

            ContinueNavigation();
        }

        private void ContinueNavigation(bool done = true)
        {
            if (done)
            {
                Navigation.IsPaneOpen = false;

                var scroll = NavigationViewList.GetScrollViewer();
                scroll?.TryChangeView(null, 0, null, true);
            }
        }

        #region Exposed

        public void PresentContent(UIElement element)
        {
            if (Transition.Child is IDisposable disposable)
            {
                disposable.Dispose();
            }

            Transition.Child = element;
            Navigation.Visibility = element != null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>
        /// True while <see cref="PresentContent"/> has swapped in content (a call, a live stream,
        /// ...) in place of the main navigation surface. WindowContext.Linux's ConsolidateAsync
        /// reads this to tell "close the presented content" apart from "close the main window".
        /// </summary>
        public bool HasPresentedContent => Transition.Child != null;

        public void UpdateSessions()
        {
            InitializeSessions(AppSettings.IsAccountsSelectorExpanded);
        }

        public void SetSelectedIndex(RootDestination value)
        {
            _navigationViewSelected = value;

            if (NavigationViewList.ItemsSource is IEnumerable<object> items)
            {
                foreach (var item in items)
                {
                    if (item is Controls.NavigationViewItem { Tag: RootDestination destination } selector)
                    {
                        selector.IsChecked = destination == value;
                    }
                }
            }
        }

        #endregion

        // The theme editor (SettingsThemePage) is not in the Linux subset.
        public void ShowEditor(ThemeCustomInfo theme)
        {
            Logger.Info("Theme editor is not available on Linux");
        }

        public void HideEditor()
        {
        }

        public bool IsPaneOpen
        {
            get => Navigation.IsPaneOpen;
            set => Navigation.IsPaneOpen = value;
        }

        private bool _isSidebarEnabled;

        public void SetSidebarEnabled(bool value)
        {
            if (_isSidebarEnabled != value)
            {
                _isSidebarEnabled = value;
            }
        }

        private void Navigation_PaneOpening(SplitView sender, object args)
        {
            InitializeSessions(AppSettings.IsAccountsSelectorExpanded);

            if (_navigationService != null)
            {
                InitializeUser(_navigationService.Session.ClientService);
            }
        }

        private void OnShortcutInvoked(object sender, ShortcutInvokedEventArgs args)
        {
            if (_navigationService?.Frame.Content is MainPage mainPage)
            {
                mainPage.ProcessKeyboardAccelerators(args);
            }
        }
    }

    public interface IRootContentPage : IPopupHost
    {
        RootPage Root { get; set; }

        void NavigationView_ItemClick(RootDestination destination);

        void Dispose();
    }

    public enum RootDestination
    {
        ShowAccounts,
        AddAccount,

        Status,

        MyProfile,
        ArchivedChats,
        SavedMessages,

        NewGroup,
        NewChannel,
        Chats,
        Contacts,
        Calls,
        Settings,

        Tips,
        News,

        Separator
    }
}
