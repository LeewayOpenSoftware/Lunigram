//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Composition;
using Telegram.Controls;
using Telegram.Controls.Cells;
#if !LINUX
using Telegram.Controls.Gallery;
#endif
using Telegram.Controls.Media;
using Telegram.Controls.Messages;
using Telegram.Controls.Views;
using Telegram.Native;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
#if !LINUX
using Telegram.Services.Calls;
#endif
using Telegram.Services.Updates;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Delegates;
using Telegram.Views.Create;
// u-068: was #if !LINUX because Status_Click (EmojiDrawerMode.EmojiStatus) was the only Linux
// reader, and it was itself guarded out. Needed unconditionally now that it isn't.
using Telegram.ViewModels.Drawers;
using Telegram.Views.Host;
using Telegram.Views.Popups;
#if !LINUX
using Telegram.Views.Settings;
#endif
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Windows.UI.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views
{
    public sealed partial class MainPage : CorePage, IRootContentPage, INavigatingPage, IChatListDelegate
    {
        private MainViewModel _viewModel;
        public MainViewModel ViewModel => _viewModel ??= DataContext as MainViewModel;

#if LINUX
        // This head has its own RootPage/IRootContentPage (Telegram.Linux/Hubs); upstream's
        // RootPage -> RootWindow rename is about its own host page, not ours.
        public RootPage Root { get; set; }
#else
        public RootWindow Root { get; set; }
#endif

        private readonly IClientService _clientService;

        private readonly DispatcherTimer _memoryUsageTimer;
        private double _memoryUsage;

        // Leak watch. A step this big in the SETTLED heap is worth interrupting for; smaller ones
        // are indistinguishable from a big chat being opened.
        private const long MemoryStepThreshold = 30 * 1024 * 1024;
        private long _memoryFloor;
        private int _memoryGen2 = -1;
        private bool _memorySettling;

        private bool _unloaded;

        public MainPage()
        {
            InitializeComponent();
            DataContext = LifetimeService.Current.ActiveItem.Resolve<MainViewModel>();

            _clientService = ViewModel.ClientService;

            ViewModel.Chats.Delegate = this;
            LifetimeService.Current.Playback.SourceChanged += OnPlaybackSourceChanged;

            InitializeLock();
            InitializeAccessibleNames();

            UpdateChatFolders();

            VisualUtilities.DropShadow(UpdateShadow);

            RootGrid.CreateInsetClip(0, -40, 0, 0);

            ChatsList.RegisterPropertyChangedCallback(ListViewBase.SelectionModeProperty, List_SelectionModeChanged);

#if LINUX
            // The only thing that ever hooked a chat row's context menu was
            // ChatsList_ChoosingItemContainer, and Uno never raises ChoosingItemContainer. The list
            // re-raises it from GetContainerForItemOverride, which does run here.
            ChatsList.ItemContextRequested += Chat_ContextRequested;

            // One line, once per process: whether AT-SPI is even reachable on this machine, so
            // nobody mistakes RaiseNotificationEvent (used by ToastPopup/BaseChartView) for a real
            // screen-reader announcement here. See AccessibilityProbe's own doc comment and
            // unigram-linux/review/ATSPI-SCOPE.md.
            AccessibilityProbe.RunOnce();
#endif

            //var show = !((TLViewModelBase)ViewModel).Settings.CollapseArchivedChats;
            //ArchivedChatsCompactPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            ArchivedChatsPanel.Visibility = ((ViewModelBase)ViewModel).Settings.HideArchivedChats
                ? Visibility.Collapsed
                : Visibility.Visible;

            ElementCompositionPreview.SetIsTranslationEnabled(ManagePanel, true);
            ElementCompositionPreview.SetIsTranslationEnabled(DialogsPanel, true);

            if (AppSettings.Diagnostics.ShowMemoryUsage)
            {
                _memoryUsageTimer = new DispatcherTimer();
                _memoryUsageTimer.Interval = TimeSpan.FromSeconds(1);
                _memoryUsageTimer.Tick += MemoryUsageTimer_Tick;
                _memoryUsageTimer.Start();

                MemoryUsageTimer_Tick(null, null);
            }

            if (Constants.DEBUG)
            {
                FocusManager.GettingFocus += OnGettingFocus;
            }
        }

        private void OnGettingFocus(object sender, GettingFocusEventArgs args)
        {
            Logger.Info(string.Format("New: {0}, Old: {1}, {2}, {3} ~> {4}",
                args.NewFocusedElement?.GetType().Name ?? "null",
                args.OldFocusedElement?.GetType().Name ?? "null",
                args.Direction, args.InputDevice, args.FocusState));
        }

        private readonly int[] _lastCollectionCount = new int[3]
        {
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2)
        };

        private string PollGC()
        {
            var occurred = GarbageCollectionMonitor.Debug();
            var first = true;

            for (int i = 0; i <= 2; i++)
            {
                int count = GC.CollectionCount(i);
                if (count != _lastCollectionCount[i])
                {
                    _lastCollectionCount[i] = count;

                    if (first)
                        occurred += " (";

                    first = false;
                    occurred += i.ToString();
                }
            }

            if (!first)
                occurred += ")";

            return occurred;
        }

        private void MemoryUsageTimer_Tick(object sender, object e)
        {
            var memoryUsage = Math.Round(Windows.System.MemoryManager.AppMemoryUsage / 1024.0 / 1024.0);
            var occurred = PollGC();

            //double unmanaged = currentProcess.NativeHeap / 1024.0 / 1024.0;
            double managed = GC.GetTotalMemory(false) / 1024.0 / 1024.0; // currentProcess.ManagedHeap / 1024.0 / 1024.0;

            if (MasterDetail?.NavigationService?.Frame?.Content is ChatPage page)
            {
                MemoryLabel.Text = $"- {memoryUsage:F0} MB, {managed:F0} MB" + occurred + page.View.GetVirtualizationInfo();
            }
            else if (memoryUsage != _memoryUsage)
            {
                MemoryLabel.Text = $"- {memoryUsage:F0} MB, {managed:F0} MB" + occurred;
            }

            _memoryUsage = memoryUsage;

            DetectMemoryStep();
        }

        /// <summary>
        /// Notices the managed heap stepping up and staying there, which is the shape of a leak
        /// rather than of garbage. GetTotalMemory(false) once a second is mostly uncollected
        /// garbage and a step in it means nothing, so this samples only after a gen2 collection -
        /// a settled number, for free, without forcing anything.
        /// </summary>
        private void DetectMemoryStep()
        {
            var gen2 = GC.CollectionCount(2);
            if (gen2 != _memoryGen2)
            {
                // Background GC bumps the count when the collection STARTS, so the heap is only
                // settled a tick later.
                _memoryGen2 = gen2;
                _memorySettling = true;
                return;
            }

            if (!_memorySettling)
            {
                return;
            }

            _memorySettling = false;

            var settled = GC.GetTotalMemory(false);

            if (_memoryFloor == 0 || settled < _memoryFloor)
            {
                _memoryFloor = settled;
                return;
            }

            if (settled - _memoryFloor < MemoryStepThreshold)
            {
                return;
            }

            // The page is the only part of this worth reading: the timestamp says WHEN, and this
            // says where the app was when it happened.
            var message = string.Format("Managed heap {0:F0} MB -> {1:F0} MB over {2} gen2 collections, on {3}",
                _memoryFloor / 1024d / 1024d,
                settled / 1024d / 1024d,
                gen2,
                MasterDetail?.NavigationService?.Frame?.Content?.GetType().Name ?? "no page");

            // Re-arm at the new level, so the NEXT step of the same size reports too instead of
            // every tick from here on.
            _memoryFloor = settled;

            // Logger.Error and not TrackError: nothing crashed, and a crash report would say
            // otherwise. This exists to put a timestamp in the log that the tail can be read
            // around - a timer tick's stack would say nothing.
            Logger.Error(message);

            ToastPopup.Show(XamlRoot, message, ToastPopupIcon.Error);
        }

        public INavigationService NavigationService => MasterDetail.NavigationService;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            Initialize();
            NavigationService.Window.SetTitleBar(TitleBarHandle);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            NavigationService.Window.SetTitleBar(null);
        }

        public void Dispose()
        {
            try
            {
                Bindings.StopTracking();

                var viewModel = _viewModel;
                if (viewModel != null)
                {
#if !LINUX
                    viewModel.Settings.Delegate = null;
#endif
                    viewModel.Chats.Delegate = null;
                    viewModel.Topics.Delegate = null;

                    viewModel.Aggregator.Unsubscribe(this);
                    viewModel.Dispose();
                }

                LifetimeService.Current.Playback.SourceChanged -= OnPlaybackSourceChanged;

                MasterDetail.NavigationService.FrameFacade.Navigating -= OnNavigating;
                MasterDetail.NavigationService.FrameFacade.Navigated -= OnNavigated;
                MasterDetail.Dispose();
                SettingsView?.Dispose();

                if (_memoryUsageTimer != null)
                {
                    _memoryUsageTimer.Tick -= MemoryUsageTimer_Tick;
                    _memoryUsageTimer.Stop();
                }

                if (Constants.DEBUG)
                {
                    FocusManager.GettingFocus -= OnGettingFocus;
                }
            }
            catch { }
        }

        protected override void OnLayoutMetricsChanged(SystemOverlayMetrics metrics)
        {
            TitleBarrr.ColumnDefinitions[0].Width = new GridLength(metrics.LeftInset > 0 ? 138 : 0, GridUnitType.Pixel);
            TitleBarrr.ColumnDefinitions[4].Width = new GridLength(metrics.RightInset > 0 ? 138 : 0, GridUnitType.Pixel);

            Grid.SetColumn(TitleBarLogo, metrics.LeftInset > 0 ? 3 : 1);
            TitleText.FlowDirection = metrics.LeftInset > 0
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;

            Photo.HorizontalAlignment = metrics.LeftInset > 0
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;

            UpdateTitleBarMargins();
#if LINUX
            UpdateTitleBarStoriesLayout();
#endif

            Stories.SystemOverlayLeftInset = metrics.LeftInset > 0 ? 138 : 0;
            Stories.SystemOverlayRightInset = metrics.RightInset > 0 ? 138 : 0;
        }

        private void UpdateTitleBarMargins()
        {
            var pad = MasterDetail.MasterVisibility != Visibility.Visible || !_tabsLeftCollapsed;
            var left = pad ? 14 : 48;
            var right = pad ? -50 : 10;

            if (TitleText.FlowDirection == FlowDirection.LeftToRight)
            {
                TitleBarrr.Margin = new Thickness(left, TitleBarrr.Margin.Top, right, TitleBarrr.Margin.Bottom);
            }
            else
            {
                TitleBarrr.Margin = new Thickness(right, TitleBarrr.Margin.Top, left, TitleBarrr.Margin.Bottom);
            }
        }

        private void InitializeLock()
        {
            Lock.Visibility = ViewModel.Passcode.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        // {CustomResource} on an attached property (AutomationProperties.Name / ToolTipService.
        // ToolTip) does not resolve under Uno Skia -- the accessible name is lost in silence, no
        // build error, no runtime exception (PORTING.md Section 6; same family of trap as
        // TextBlockHelper.Markdown, just without the CS1061 this one happens to compile past).
        // ContactsSortedByHeader.xaml.cs already carries the fix for one site; this is the same
        // fix for the nine still left in MainPage.xaml, all found by grepping this file for
        // "AutomationProperties.*CustomResource" (review/ATSPI-SCOPE.md's a11y-hygiene sweep).
        //
        // Unconditional, not "#if LINUX": Windows never needed this trap to begin with, so the
        // same code path costs it nothing, and the one prior instance of this exact fix (u-056,
        // ContactsSortedByHeader) that WAS gated turned into a silent Windows regression nobody
        // caught since this floor doesn't rebuild Windows -- see PORTING.md's own "para las dos
        // plataformas" wording on the underlying trap.
        private void InitializeAccessibleNames()
        {
            AutomationProperties.SetName(Photo, Strings.AccDescrOpenMenu);
            ToolTipService.SetToolTip(Photo, Strings.AccDescrOpenMenu);

            AutomationProperties.SetName(ComposeButton, Strings.NewConversationShortcut);
            ToolTipService.SetToolTip(ComposeButton, Strings.NewConversationShortcut);

            AutomationProperties.SetName(Proxy, Strings.ProxySettings);
            ToolTipService.SetToolTip(Proxy, Strings.ProxySettings);

            AutomationProperties.SetName(Lock, Strings.AccDescrPasscodeLock);
            ToolTipService.SetToolTip(Lock, Strings.AccDescrPasscodeLock);

            AutomationProperties.SetName(ArchivedChatsButton, Strings.ArchivedChats);

            AutomationProperties.SetName(ManageArchive, Strings.Archive);
            ToolTipService.SetToolTip(ManageArchive, Strings.Archive);

            AutomationProperties.SetName(ManageDelete, Strings.Delete);
            ToolTipService.SetToolTip(ManageDelete, Strings.Delete);

            // ChatFoldersSide (ChatTabsLeft) and ChatFolders (ChatTabs) are both x:Load="False" --
            // the same door as everything else in this file that reaches into an x:Load subtree.
            MaterializeCard(nameof(ChatFoldersSide), () => AutomationProperties.SetName(ChatFoldersSide, Strings.Filters));
            MaterializeCard(nameof(ChatFolders), () => AutomationProperties.SetName(ChatFolders, Strings.Filters));
        }

        #region Handle

        public void UpdateChatLastMessage(Chat chat)
        {
            Handle(chat, (chatView, chat) =>
            {
                chatView.UpdateChatReadInbox(chat);
                chatView.UpdateChatLastMessage(chat);
            });
        }

        public void Handle(UpdateChatActiveStories update)
        {
            if (update.ActiveStories.List is StoryListArchive)
            {
                this.BeginOnUIThread(() => ArchivedChats.UpdateStoryList(ViewModel.ClientService, new StoryListArchive()));
            }
            else
            {
                Handle(update.ActiveStories.ChatId, (chatView, chat) => chatView.UpdateChatActiveStories(update.ActiveStories));
            }
        }

        public void Handle(UpdateFileDownloads update)
        {
            this.BeginOnUIThread(() => UpdateFileDownloads(update));
        }

        private void UpdateFileDownloads(UpdateFileDownloads update)
        {
            // REABIERTO 2026-09-05 (#7). El bloqueo de 2026-08-27 era real -- DownloadsPopup,
            // DownloadsViewModel, FileDownloadCell y ScrollViewerScrim de verdad no entran en el
            // subconjunto -- pero se llevo por delante la CUENTA junto con el popup, y la cuenta no
            // depende de ninguno de esos cuatro: DownloadsFooter (icono, texto y tamano) es
            // autonomo. Se usa la misma puerta x:Load de esta pagina (MaterializeCard) en vez del
            // FindName crudo de antes, porque el pie es x:Load="False" igual que las cinco
            // tarjetas de la sesion 3 -- sin la puerta, la primera actualizacion que llega antes de
            // que el subarbol se realice perderia la cuenta en silencio otra vez, solo que por un
            // motivo distinto al de agosto.
            if (update.TotalSize > 0)
            {
                MaterializeCard(nameof(Downloads), () =>
                {
                    Downloads.UpdateFileDownloads(update);
                });
            }
            else
            {
                Downloads?.UpdateFileDownloads(update);
            }
        }

        public void Handle(UpdateChatIsMarkedAsUnread update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatReadInbox(chat));
        }

        public void Handle(UpdateChatReadInbox update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatReadInbox(chat));
        }

        public void Handle(UpdateChatUnreadTopicCount update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatReadInbox(chat));
        }

        public void Handle(UpdateChatReadOutbox update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatReadOutbox(chat));
        }

        public void Handle(UpdateChatUnreadMentionCount update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatUnreadMentionCount(chat));
        }

        public void Handle(UpdateChatUnreadReactionCount update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatUnreadMentionCount(chat));
        }

        public void Handle(UpdateChatUnreadPollVoteCount update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatUnreadMentionCount(chat));
        }

        public void Handle(UpdateChatAddedToList update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatChatLists(chat));
        }

        public void Handle(UpdateChatRemovedFromList update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatChatLists(chat));
        }

        public void Handle(UpdateChatTitle update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatTitle(chat));

            // TODO: threading is not great here
            if (update.ChatId == _viewModel.Topics.Chat?.Id)
            {
                this.BeginOnUIThread(() => TopicListPresenter?.UpdateChatTitle(_viewModel.Topics.Chat));
            }
        }

        public void Handle(UpdateChatPhoto update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatPhoto(chat));
        }

        public void Handle(UpdateChatEmojiStatus update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatEmojiStatus(chat));

            // TODO: threading is not great here
            if (update.ChatId == _viewModel.Topics.Chat?.Id)
            {
                this.BeginOnUIThread(() => TopicListPresenter?.UpdateChatEmojiStatus(_viewModel.Topics.Chat));
            }
        }

        public void Handle(UpdateChatVideoChat update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatVideoChat(chat));
        }

        public void Handle(UpdateChatViewAsTopics update)
        {
            // TODO: threading is not great here
            // TODO: ignore if chatId is saved messages
            if (update.ChatId == _viewModel.Topics.Chat?.Id && !update.ViewAsTopics)
            {
                this.BeginOnUIThread(() => HideTopicList());
            }
            else if (update.ChatId == _viewModel.Chats.SelectedItem && update.ViewAsTopics && update.ChatId != _viewModel.ClientService.Options.MyId)
            {
                this.BeginOnUIThread(() => ShowTopicList(_viewModel.ClientService.GetChat(update.ChatId)));
            }
        }

        public void Handle(UpdateUser update)
        {
            if (update.User.Id == _clientService.Options.MyId)
            {
                this.BeginOnUIThread(() => UpdateUser(update.User));
            }
        }

        public void Handle(UpdateUserStatus update)
        {
            if (update.UserId != _clientService.Options.MyId && update.UserId != 777000 && _clientService.TryGetChatFromUser(update.UserId, out long chatId))
            {
                Handle(chatId, (chatView, chat) => chatView.UpdateUserStatus(chat, update.Status));
            }
        }

        public void Handle(UpdateChatMessageAutoDeleteTime update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatMessageAutoDeleteTime(chat, true));
        }

        public void Handle(UpdateChatAction update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatActions(chat, ViewModel.ClientService.GetChatActions(chat.Id)));
        }

        public void Handle(UpdateMessageMentionRead update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatUnreadMentionCount(chat));
        }

        public void Handle(UpdateMessageUnreadReactions update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatUnreadMentionCount(chat));
        }

        public async void Handle(UpdateSecretChat update)
        {
            var response = await _clientService.SendAsync(new CreateSecretChat(update.SecretChat.Id));
            if (response is Chat result)
            {
                Handle(result.Id, (chatView, chat) => chatView.UpdateChatLastMessage(chat));
            }
        }

        public void Handle(UpdateChatNotificationSettings update)
        {
            Handle(update.ChatId, (chatView, chat) => chatView.UpdateChatNotificationSettings(chat));
        }

        public void Handle(UpdateUnreadChatCount update)
        {
            if (update.ChatList is ChatListArchive)
            {
                this.BeginOnUIThread(() => ArchivedChats.UpdateChatList(ViewModel.ClientService, update.ChatList));
            }
        }

        private void Handle(long chatId, long messageId, Action<Chat> update, Action<ChatCell, Chat> action)
        {
            var chat = _clientService.GetChat(chatId);
            if (chat.LastMessage == null || chat.LastMessage.Id != messageId)
            {
                return;
            }

            update(chat);

            this.BeginOnUIThread(() =>
            {
                if (ChatsList.TryGetCell(chat, out ChatCell chatView))
                {
                    action(chatView, chat);
                }
            });
        }

        private void Handle(long chatId, Action<ChatCell, Chat> action)
        {
            this.BeginOnUIThread(() =>
            {
                if (ChatsList.TryGetChatAndCell(chatId, out Chat chat, out ChatCell chatView))
                {
                    action(chatView, chat);
                }
            });
        }

        private void Handle(Chat chat, Action<ChatCell, Chat> action)
        {
            this.BeginOnUIThread(() =>
            {
                if (ChatsList.TryGetCell(chat, out ChatCell chatView))
                {
                    action(chatView, chat);
                }
            });
        }

        public void Handle(UpdatePasscodeLock update)
        {
            this.BeginOnUIThread(() =>
            {
                Lock.Visibility = update.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        // La puerta de los elementos `x:Load` de esta pagina. Un solo metodo y no un `#if` en cada
        // sitio de llamada, porque lo que cambia entre las dos cabezas es UNA cosa -- si FindName
        // basta por si solo -- y repetir esa decision cinco veces es como se pierde la proxima.
        //
        // En Windows es literalmente lo que habia. En Linux pasa por XLoadGate
        // (Telegram.Linux/Xaml/XLoadGate.cs), donde FindName es un RECORRIDO DEL ARBOL VISUAL y
        // devuelve null en silencio si el trozo de arbol que contiene el stub no se ha realizado
        // todavia. Tres de las cinco tarjetas desreferencian ese null a la linea siguiente, asi que
        // ahi el fallo no es solo que la tarjeta no salga: se lleva el manejador entero por delante
        // -- y una de ellas es el aviso de SESION NO CONFIRMADA, que es una alerta de SEGURIDAD.
        //
        // HASTA DONDE LLEGA LO MEDIDO, y conviene que quede escrito: el mecanismo esta probado en
        // pantalla, pero en OTRA pagina (UserEditPage, parcela 3: se quedo sin apellido y sin foto
        // hasta que se puso esta misma puerta). Las cinco de aqui salen del barrido ESTATICO de
        // Phyllis, y de las cinco la unica que se puede provocar en esta maquina -- la banda de
        // reproduccion -- resulto SALIR YA sin el arreglo, o sea que para ese camino la prediccion
        // era falsa y su subarbol si es alcanzable. Las otras cuatro piden una sesion sin confirmar,
        // una cuenta congelada, una sugerencia de TDLib o una llamada real, y ninguna se puede
        // montar aqui. Asi que esto es una puerta que NO cuesta nada cuando FindName ya funciona
        // (corre en el acto) y que ademas deja una linea en el log cuando se rinde: el defecto que
        // persigue no era que la tarjeta faltara, era que faltaba EN SILENCIO.
        private void MaterializeCard(string name, Action then = null)
        {
#if LINUX
            XLoadGate.Materialize(this, name, then);
#else
            FindName(name);
            then?.Invoke();
#endif
        }

        public void Handle(UpdateConfetti update)
        {
            this.BeginOnUIThread(() =>
            {
                FindName(nameof(Confetti));
                Confetti.Start();
            });
        }

        public void Handle(UpdateUnconfirmedSession update)
        {
            this.BeginOnUIThread(() =>
            {
                if (update.Session == null)
                {
                    UnloadObject(UnconfirmedCard);

                    SetBirthdateCard?.Visibility = Visibility.Visible;
                }
                else
                {
                    MaterializeCard(nameof(UnconfirmedCard), () => UnconfirmedCard.Update(update.Session));

                    SetBirthdateCard?.Visibility = Visibility.Collapsed;
                }
            });
        }

        public void Handle(UpdateFreezeState update)
        {
            this.BeginOnUIThread(() =>
            {
                if (update.IsFrozen)
                {
                    MaterializeCard(nameof(FrozenCard));

                    SetBirthdateCard?.Visibility = Visibility.Collapsed;

                    UnconfirmedCard?.Visibility = Visibility.Collapsed;
                }
                else
                {
                    UnloadObject(FrozenCard);

                    SetBirthdateCard?.Visibility = Visibility.Visible;

                    UnconfirmedCard?.Visibility = Visibility.Visible;
                }
            });
        }

        public void Handle(UpdateConnectionState update)
        {
            this.BeginOnUIThread(() =>
            {
                SetProxyVisibility(_clientService.Options.ExpectBlocking, _clientService.Options.EnabledProxyId, update.State);

                switch (update.State)
                {
                    case ConnectionStateWaitingForNetwork waitingForNetwork:
                        ShowState(Strings.WaitingForNetwork);
                        break;
                    case ConnectionStateConnecting connecting:
                        ShowState(_clientService.Options.EnabledProxyId == 0 ? Strings.Connecting : Strings.ConnectingToProxy);
                        break;
                    case ConnectionStateConnectingToProxy connectingToProxy:
                        ShowState(Strings.ConnectingToProxy);
                        break;
                    case ConnectionStateUpdating updating:
                        ShowState(Strings.Updating);
                        break;
                    case ConnectionStateReady ready:
                        HideState();
                        return;
                }
            });
        }

        public void Handle(UpdateSuggestedActions update)
        {
            this.BeginOnUIThread(() =>
            {
                if (_clientService.HasSuggestedAction(new SuggestedActionSetBirthdate()))
                {
                    MaterializeCard(nameof(SetBirthdateCard));
                }
                else
                {
                    UnloadObject(SetBirthdateCard);
                }
            });
        }

        public void Handle(UpdateOption update)
        {
            if (update.Name == OptionsService.R.ExpectBlocking || update.Name == OptionsService.R.EnabledProxyId)
            {
                this.BeginOnUIThread(() => SetProxyVisibility(_clientService.Options.ExpectBlocking, _clientService.Options.EnabledProxyId, _clientService.ConnectionState));
            }
        }

        private void SetProxyVisibility(bool expectBlocking, long proxyId, ConnectionState connectionState)
        {
            if (expectBlocking || proxyId != 0)
            {
                Proxy.Visibility = Visibility.Visible;
            }
            else
            {
                switch (connectionState)
                {
                    case ConnectionStateWaitingForNetwork:
                    case ConnectionStateConnecting:
                    case ConnectionStateConnectingToProxy:
                        Proxy.Visibility = Visibility.Visible;
                        break;
                    default:
                        Proxy.Visibility = Visibility.Collapsed;
                        break;
                }
            }

            Proxy.Glyph = connectionState is ConnectionStateReady && proxyId != 0 ? Icons.ShieldCheckmark : Icons.ShieldError;
        }

        private void ShowState(string text)
        {
            State.IsIndeterminate = true;
            StateLabel.Text = text;

            var peer = FrameworkElementAutomationPeer.FromElement(TitleText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

            try
            {
                NavigationService.Window.Title = text;
            }
            catch { }
        }

        private void HideState()
        {
            State.IsIndeterminate = false;
            StateLabel.Text = Constants.RELEASE
                ? Strings.AppDisplayName
                : Strings.AppName;

            try
            {
                NavigationService.Window.Title = string.Empty;
            }
            catch { }
        }

        public void Handle(UpdateActiveCall update)
        {
            void UpdatePlaybackHidden(bool hidden)
            {
                Playback?.IsHidden = hidden;
            }

            this.BeginOnUIThread(() =>
            {
                var call = ViewModel.VoipService.ActiveCall;
                if (call != null)
                {
                    UpdatePlaybackHidden(true);

                    // El orden de upstream se queda TAL CUAL, y eso es una decision medida, no una
                    // omision: la primera version de este arreglo daba la vuelta a las sentencias
                    // -- ensenar el banner y materializar despues -- porque el banner de
                    // MasterDetailView vive en un ContentControl que arranca Collapsed y un
                    // subarbol colapsado no se mide. Conducir la app retiro esa hipotesis: el banner
                    // de reproduccion SALE en el binario PREVIO al arreglo, o sea que ese subarbol
                    // si es alcanzable. Y aunque no lo fuera, la puerta ya lo cubre sin tocar el
                    // orden: el reintento corre en la siguiente pasada de layout, es decir despues
                    // de que ShowHideBanner de aqui abajo se haya ejecutado.
                    MaterializeCard(nameof(CallBanner), () => CallBanner.Update(call));

                    _activeCallVisible = true;
                    MasterDetail.ShowHideBanner(_activeCallVisible || _playbackVisible);
                }
                else
                {
                    UpdatePlaybackHidden(false);

                    CallBanner?.Update(null);

                    _activeCallVisible = false;
                    MasterDetail.ShowHideBanner(_activeCallVisible || _playbackVisible);
                }
            });
        }

        private void OnBannerCollapsed(object sender, EventArgs e)
        {
            if (CallBanner != null && !_activeCallVisible)
            {
                CallBanner.Update(null);
                UnloadObject(CallBanner);
            }
        }

        public void Handle(UpdateChatFoldersLayout update)
        {
            this.BeginOnUIThread(UpdateChatFoldersLayout);
        }

        #endregion

        private bool _tabsTopCollapsed = true;
        private bool _tabsLeftCollapsed = true;

#if LINUX
        private bool _chatListTopPaddingHooked;
        private bool _storiesMoved;

        /// <summary>
        /// Moves the stories strip from the overlay slot it is declared in into the caption row,
        /// to the right of the "Telegram" caption, which is where Unigram draws it on Windows.
        ///
        /// The overlay slot is the collapsed slot: upstream the band only leaves the title bar
        /// through the composition choreography in StoriesStrip.SetControlledList, and every
        /// single one of the Uno APIs that choreography needs is missing - the scroll viewer
        /// manipulation property set throws, ViewChanging and the DirectManipulation events never
        /// fire, and an animation whose target is a CompositionPropertySet is never registered
        /// because Uno only animates a Visual. Left where it is declared, the band would be
        /// painted straight across the "Telegram" caption and the search box.
        ///
        /// So the strip is parented into <c>TitleBarrr</c> instead, in the same star column the
        /// caption lives in, and told to be compact: 36px rings and no names, because a name does
        /// not fit in a 40px row and upstream does not draw one up there either. It costs the chat
        /// list nothing - the header goes back to 92 + 40 and
        /// <see cref="UpdateChatListTopPadding"/> follows it down, which is what closes the gap
        /// between the search box and the folder pills at the same time.
        ///
        /// The one thing it must not cost is the window drag: <c>TitleBarHandle</c> is the element
        /// handed to SetTitleBar and it is painted over TitleBarrr (Canvas.ZIndex="1"), so the
        /// strip would be under it and unclickable. <see cref="UpdateTitleBarStoriesLayout"/>
        /// moves the handle's left edge past the strip, the same trick and the same arithmetic
        /// upstream uses for the folded band (StoriesStrip.UpdateIndexes, Windows branch).
        /// </summary>
        private void MoveStoriesIntoTitleBar()
        {
            if (_storiesMoved || Stories == null || TitleBarrr == null)
            {
                return;
            }

            _storiesMoved = true;

            if (Stories.Parent is Panel panel)
            {
                panel.Children.Remove(Stories);
            }
            else if (Stories.Parent is Border border)
            {
                border.Child = null;
            }
            else if (Stories.Parent != null)
            {
                Logger.Warning($"stories: unexpected parent {Stories.Parent.GetType().Name}, leaving the strip where it is");
                return;
            }

            // The -32 the overlay carries is the amount it has to ride up into the title bar. It
            // is in the title bar now.
            Stories.Margin = new Thickness();
            Stories.HorizontalAlignment = HorizontalAlignment.Left;
            Stories.VerticalAlignment = VerticalAlignment.Center;

            // Column 2 is the star column TitleText sits in; RowSpan 2 came from the overlay's
            // two-row host and means nothing in a grid with one row.
            Grid.SetColumn(Stories, 2);
            Grid.SetRow(Stories, 0);
            Grid.SetRowSpan(Stories, 1);

            TitleBarrr.Children.Add(Stories);

            // Both edges of the gap the strip has to start after: the caption grows and shrinks
            // with the connection state ("Telegram", "Conectando…", "Actualizando…"), and the
            // strip itself grows and shrinks with the number of stories.
            TitleText.SizeChanged += Stories_LayoutChanged;
            Stories.SizeChanged += Stories_LayoutChanged;
            Stories.RegisterPropertyChangedCallback(VisibilityProperty, (s, dp) => UpdateTitleBarStoriesLayout());

            // StoriesStripHost stays collapsed and empty: the header is 92 + 40 again.
            UpdateChatListTopPadding();
            UpdateTitleBarStoriesLayout();

            Logger.Info("stories: strip moved into the caption row");
        }

        private void Stories_LayoutChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTitleBarStoriesLayout();
        }

        /// <summary>
        /// Puts the strip after the caption and the window drag handle after the strip.
        /// </summary>
        /// <remarks>
        /// The 40 is the logo column: the logo is 32 wide with a 4px margin either side, so the
        /// star column TitleText and the strip share starts at TitleBarrr.Margin.Left + 40. That
        /// is the same number StoriesStrip.UpdateIndexes writes on Windows.
        /// </remarks>
        private void UpdateTitleBarStoriesLayout()
        {
            if (Stories == null || TitleText == null || TitleBarHandle == null || TitleBarrr == null)
            {
                return;
            }

            var caption = TitleText.ActualWidth + TitleText.Margin.Right + 8;
            if (Math.Abs(Stories.Margin.Left - caption) > 0.5)
            {
                Stories.Margin = new Thickness(caption, 0, 0, 0);
            }

            // With the strip collapsed this is just "after the logo", which is what MainPage.xaml
            // declares in the first place.
            var handle = TitleBarrr.Margin.Left + 40;
            if (Stories.Visibility == Visibility.Visible)
            {
                handle += caption + Stories.ActualWidth + 8;
            }

            if (Math.Abs(TitleBarHandle.Margin.Left - handle) > 0.5)
            {
                TitleBarHandle.Margin = new Thickness(handle, TitleBarHandle.Margin.Top, TitleBarHandle.Margin.Right, TitleBarHandle.Margin.Bottom);
            }
        }

        /// <summary>
        /// Keeps the chat list clear of the header above it, whatever that header ends up
        /// measuring. See the note in ShowHideTopTabsCompleted for why the constant is wrong here.
        /// </summary>
        private void UpdateChatListTopPadding()
        {
            if (ChatListHeader == null || ChatsList == null)
            {
                return;
            }

            if (!_chatListTopPaddingHooked)
            {
                _chatListTopPaddingHooked = true;
                ChatListHeader.SizeChanged += (s, args) => UpdateChatListTopPadding();

                // The header is a StackPanel whose two children are a 92px stand-in for the title
                // and the search box drawn over it, and the folder strip. Its Spacing puts sixty
                // pixels between them that nothing fills here, so the strip floats well below the
                // search box with a band of empty page in between. Measured: children at 0..92 and
                // 152..192. The strip belongs directly under the search box.
                ChatListHeader.Spacing = 0;
            }

            UpdateChatListHeaderSpacer();
            LogChatListHeaderMap();

            var height = ChatListHeader.ActualHeight;
            if (height > 0 && Math.Abs(ChatsList.Margin.Top - height) > 0.5)
            {
                ChatsList.Margin = new Thickness(0, height, 0, 0);
            }
        }

        private string _headerMap;

        /// <summary>
        /// Writes the vertical map of the chat-list header in WINDOW coordinates.
        /// </summary>
        /// <remarks>
        /// The first probe measured the search box against ChatListHeader and got 73, so the
        /// stand-in was set to 81 and the strip should have landed 8px under the box. The user
        /// re-tested and the band is still large, which means those two numbers are not in the
        /// space I assumed: everything here is therefore measured against the window content, one
        /// root both subtrees actually share, instead of across them.
        ///
        /// It prints every element between the title bar and the first chat row, so whichever gap
        /// is the big one is read off the line rather than inferred - and it prints
        /// ChatListHeader.Spacing, which is the other open suspect (something re-applying the 60
        /// after startup would show up as a second line with Spacing=60).
        ///
        /// Logged only when the map CHANGES, which is what makes a late mutation visible: a
        /// one-shot probe would have shown the settled startup state and missed it.
        /// </remarks>
        private void LogChatListHeaderMap()
        {
            var root = XamlRoot?.Content as UIElement;
            if (root == null || ChatListHeader == null)
            {
                return;
            }

            try
            {
                string Span(string name, FrameworkElement element)
                {
                    if (element == null)
                    {
                        return $"{name}=null";
                    }

                    var top = element.TransformToVisual(root).TransformPoint(default).Y;
                    var collapsed = element.Visibility == Visibility.Collapsed ? "!" : string.Empty;

                    return $"{name}{collapsed} {top:F0}..{top + element.ActualHeight:F0}";
                }

                var map = string.Join("  ",
                    Span("titlebar", TitleBarrr),
                    Span("header", Header),
                    Span("search", SearchField),
                    Span("chatsroot", ChatsRoot),
                    Span("dialogs", DialogsPanel),
                    Span("listheader", ChatListHeader),
                    Span("spacer", ChatListHeaderSpacer),
                    Span("tabs", ChatTabs),
                    Span("list", ChatsList),
                    $"spacing={ChatListHeader.Spacing:F0}");

                if (map == _headerMap)
                {
                    return;
                }

                _headerMap = map;

                // The one number the user is complaining about, spelled out rather than left to be
                // subtracted off the map: the empty run between the bottom of the search box and
                // the top of the folder strip.
                var band = ChatTabs != null && SearchField != null
                    ? ChatTabs.TransformToVisual(root).TransformPoint(default).Y
                        - (SearchField.TransformToVisual(root).TransformPoint(default).Y + SearchField.ActualHeight)
                    : double.NaN;

                Logger.Info($"chat list header map (window coords): {map}  ||  BAND search-bottom to tabs-top = {band:F0}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "chat list header map: could not measure");
            }
        }

        /// <summary>
        /// Sizes the stand-in at the top of the header to where the search box actually ends.
        /// </summary>
        /// <remarks>
        /// The folder strip starts immediately after that stand-in, so its height is the distance
        /// between the search box and the tabs. Upstream hard-codes 92, which is where the title
        /// bar and the search box end on Windows; this port does not draw them at the same place -
        /// the page has its own 40px title bar and the header carries Margin="0,-8,0,-8" over a
        /// ChatsRoot pulled up by -72 - so the constant left a band of empty page between the two,
        /// which is what a user sees and reports.
        ///
        /// So measure it, the same reasoning as <see cref="ChatListTopPadding"/>: the only number
        /// that is right here is the one taken off the screen. 8 is the gap upstream leaves below
        /// the box, kept so the strip does not touch it.
        ///
        /// The measurement cannot feed back: the search box is drawn OVER this panel, in a grid of
        /// its own, so nothing about its position depends on how tall this stand-in is. The
        /// epsilon is what stops SizeChanged from re-entering on a rounding difference.
        /// </remarks>
        private void UpdateChatListHeaderSpacer()
        {
            if (ChatListHeaderSpacer == null || SearchField == null || SearchField.ActualHeight <= 0)
            {
                return;
            }

            double bottom;

            try
            {
                bottom = SearchField.TransformToVisual(ChatListHeader)
                    .TransformPoint(new Point(0, SearchField.ActualHeight)).Y + 8;
            }
            catch (Exception ex)
            {
                // Not fatal: without the measurement the stand-in keeps upstream's 92, which is
                // the band this closes and not a crash.
                Logger.Error(ex, "chat list header: could not measure where the search box ends");
                return;
            }

            if (bottom > 0 && Math.Abs(ChatListHeaderSpacer.Height - bottom) > 0.5)
            {
                // Rare (startup, window resize, the caption growing) and it is the only line that
                // says the measurement happened at all, which is what tells the next person
                // whether a band on screen is this or something else.
                Logger.Info($"chat list header: search box ends at {bottom - 8:F0}, folder strip moved from {ChatListHeaderSpacer.Height:F0} to {bottom:F0}");

                ChatListHeaderSpacer.Height = bottom;
            }
        }

#endif

        /// <summary>
        /// What the chat list has to keep clear at the top.
        ///
        /// On Windows that is <see cref="Telegram.Controls.Stories.StoriesStrip.TopPadding"/>, a
        /// sum of constants. On Linux the header is taller than those constants and now also holds
        /// the stories band, so the only number that is right is the measured one - the same one
        /// <see cref="UpdateChatListTopPadding"/> writes. Without this, every path that resets the
        /// margin by hand (showing and hiding the archive) would undo that measurement and put the
        /// first chat row back underneath the folder strip.
        /// </summary>
        private double ChatListTopPadding()
        {
#if LINUX
            var height = ChatListHeader?.ActualHeight ?? 0;
            if (height > 0)
            {
                return height;
            }
#endif
            return Stories.TopPadding;
        }

        private void ShowHideTopTabs(bool show)
        {
            if (_tabsTopCollapsed != show)
            {
                return;
            }

            _tabsTopCollapsed = !show;
#if LINUX
            Logger.Info($"top tabs: show={show}, realizing ChatTabs");
#endif
            FindName(nameof(ChatTabs));
#if LINUX
            Logger.Info($"top tabs: ChatTabs is {(ChatTabs == null ? "null" : ChatTabs.GetType().Name)}");
#endif

            if (TopicListPresenter != null)
            {
                var padding = ChatTabs != null
                    ? _tabsTopCollapsed ? -74 : -78
                    : -12;

                TopicListPresenter.Margin = new Thickness(68, padding, 0, 0);
            }

            Stories.TabsTopCollapsed = !show;
            Stories.ChatTabs = ChatTabs;
            Stories.ControlledList = ChatsList;

            void ShowHideTopTabsCompleted()
            {
                DialogsPanel.Margin = new Thickness();
                ChatsList.Margin = new Thickness(0, ChatListTopPadding(), 0, 0);
#if LINUX
                // Stories.TopPadding is a sum of constants that adds up to the header Windows
                // draws. The one this port draws is taller - measured on screen: header 192, tab
                // strip at 152..192, list starting at 112 - because the panel holding the header
                // charges its Spacing for a child WinUI does not charge it for. Eighty pixels of
                // list end up underneath the folder strip, which is what puts a pill on top of the
                // first chat. The header knows its own height; ask it, and follow it when it
                // changes rather than reading it once while it is still zero.
                UpdateChatListTopPadding();
#endif
                ChatTabs.Visibility = _tabsTopCollapsed
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            var element = VisualTreeHelper.GetChild(ChatsList, 0) as UIElement;
            if (element == null)
            {
                ShowHideTopTabsCompleted();
                return;
            }

#if LINUX
            Logger.Info("top tabs: animating");
#endif
            var topPadding = Stories.GetTopPadding(false);

            ChatTabs.Visibility = Visibility.Visible;
            ChatsList.Margin = new Thickness(0, topPadding, 0, 0);
            DialogsPanel.Margin = new Thickness(0, 0, 0, -40);

            var visual = ElementComposition.GetElementVisual(DialogsPanel);
            var header = ElementComposition.GetElementVisual(ChatTabsView);
            header.Clip = visual.Compositor.CreateInsetClip();

            var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            void BatchCompleted()
            {
                header.Offset = new Vector3();
                visual.Offset = new Vector3();
#if LINUX
                // The clip is the transition, not the layout: the strip slides out from under the
                // header behind an inset that opens from 36 down to 0. Uno never advances the
                // animation on a CompositionClip's TopInset - it holds the first key frame - so the
                // strip is left forty pixels tall behind a thirty-six pixel inset, four visible
                // pixels of a folder row, which reads on screen as the folder pills having vanished.
                // Leaving the archive is the first time the strip is asked to come back after the
                // window has laid out, so that is where it is noticed; at launch the animation is
                // skipped entirely (ChatsList has no child yet) and no clip is ever created, which
                // is why the pills are there until the first time they are hidden.
                // Once the animation is over the strip is either whole or Collapsed, so it does not
                // need a clip at all, and the offsets above are reset for the same reason.
                header.Clip = null;
#endif

                ShowHideTopTabsCompleted();
            }

#if !LINUX
            batch.Completed += (s, args) => BatchCompleted();
#endif

            var offset1 = visual.Compositor.CreateScalarKeyFrameAnimation();
            offset1.InsertKeyFrame(show ? 0 : 1, -36);
            offset1.InsertKeyFrame(show ? 1 : 0, 0);
            //offset.Duration = Constants.FastAnimation;

            var offset2 = visual.Compositor.CreateScalarKeyFrameAnimation();
            offset2.InsertKeyFrame(show ? 0 : 1, 36);
            offset2.InsertKeyFrame(show ? 1 : 0, 0);
            //offset.Duration = Constants.FastAnimation;

            header.Clip.StartAnimation("TopInset", offset2);
            visual.StartAnimation("Offset.Y", offset1);

#if LINUX
            // CompositionScopedBatch.Completed never fires on Uno, and everything that puts the
            // chat-folder tabs back together lives in that handler: without it ChatTabs stays
            // visible whether it was appearing or disappearing, ChatsList keeps the padding the
            // animation borrowed and DialogsPanel keeps its -40 bottom margin. Same pattern as
            // CompositionRenderingClock - see Telegram.Linux/Xaml/CompositionScopedBatchEx.cs.
            // The animations above set no Duration, which on Uno means they finish on their first
            // evaluation, so the wait is a single frame.
            batch.EndWithCompleted(offset1.Duration, BatchCompleted);
            Logger.Info("top tabs: batch queued");
#else
            batch.End();
#endif
        }

        private void ShowHideLeftTabs(bool show)
        {
            if (_tabsLeftCollapsed != show)
            {
                return;
            }

            _tabsLeftCollapsed = !show;
            FindName(nameof(ChatTabsLeft));

            Root?.SetSidebarEnabled(show);

            Stories.TabsLeftCollapsed = !show;

            UpdateTitleBarMargins();

            Photo.Width = show ? 72 : 48;
            Photo.Visibility = show || MasterDetail.MasterVisibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (ChatTabsLeft == null)
            {
                FindName(nameof(ChatTabsLeft));
            }

            void ShowHideLeftTabsCompleted()
            {
                ChatsList.Margin = new Thickness(0, ChatListTopPadding(), 0, 0);
                ChatTabsLeft.Visibility = _tabsLeftCollapsed
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            ShowHideLeftTabsCompleted();
            return;

            var element = VisualTreeHelper.GetChild(ChatsList, 0) as UIElement;
            if (element == null)
            {
                ShowHideLeftTabsCompleted();
                return;
            }

            ChatTabsLeft.Visibility = Visibility.Visible;
            ChatsList.Margin = new Thickness(0, ChatListTopPadding(), 0, -40);

            var parent = ElementComposition.GetElementVisual(ChatsList);

            var visual = ElementComposition.GetElementVisual(element);
            var header = ElementComposition.GetElementVisual(ChatTabsView);

            parent.Clip = null;

            var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            void BatchCompleted()
            {
#if LINUX
                // Stop before writing: an ended KeyFrameAnimation still owns its property in Uno
                // and RenderRootVisual re-evaluates it every frame (PORTING.md 6).
                header.StopAnimation("Offset");
                visual.StopAnimation("Offset");
#endif
                header.Offset = new Vector3();
                visual.Offset = new Vector3();

                ShowHideLeftTabsCompleted();
            }

#if !LINUX
            batch.Completed += (s, args) => BatchCompleted();
#endif

            // One instance per visual: Uno's registry is keyed by the animation object.
            Vector3KeyFrameAnimation Offset()
            {
                var instance = visual.Compositor.CreateVector3KeyFrameAnimation();
                instance.InsertKeyFrame(show ? 0 : 1, new Vector3(0, -40, 0));
                instance.InsertKeyFrame(show ? 1 : 0, new Vector3());
                //instance.Duration = Constants.FastAnimation;
                return instance;
            }

            var opacity1 = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity1.InsertKeyFrame(show ? 0 : 1, 0);
            opacity1.InsertKeyFrame(show ? 1 : 0, 1);
            opacity1.Duration /= 2;

            var opacity2 = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity2.InsertKeyFrame(show ? 0 : 1, 1);
            opacity2.InsertKeyFrame(show ? 1 : 0, 0);
            opacity2.Duration /= 2;

            header.StartAnimation("Offset", Offset());
            visual.StartAnimation("Offset", Offset());

#if LINUX
            batch.EndWithCompleted(Constants.FastAnimation, BatchCompleted);
#else
            batch.End();
#endif
        }

        public void OnBackRequesting(BackRequestedRoutedEventArgs args)
        {
            if (Root?.IsPaneOpen is true)
            {
                Root.IsPaneOpen = false;
                args.Handled = true;
            }
            else if (!_searchCollapsed)
            {
                DialogsSearchPanel.OnBackRequested(args);

                if (args.Handled)
                {
                    return;
                }

                Search_LostFocus(null, null);
                args.Handled = true;
            }
            else if (ViewModel.Chats.SelectionMode == ListViewSelectionMode.Multiple)
            {
                Manage_Click(null, null);
                args.Handled = true;
            }
        }

        public void OnBackRequested(BackRequestedRoutedEventArgs args)
        {
            OnBackRequesting(args);

            if (args.Handled)
            {
                return;
            }

            if (!_topicListCollapsed)
            {
                HideTopicList();
                args.Handled = true;
            }
            else if (_prevIndex != INDEX_CHATS)
            {
                SetPivotSelectedIndex(INDEX_CHATS);
                ViewModel.RaisePropertyChanged(nameof(ViewModel.SelectedFolder));
                args.Handled = true;
            }
            else
            {
                var scrollViewer = ChatsList.GetScrollViewer();
                if (scrollViewer != null && scrollViewer.VerticalOffset > 50)
                {
                    Logger.Info("ChangeView");

                    scrollViewer.TryChangeView(null, 0, null);
                    args.Handled = true;
                }
                else if (ViewModel.Chats.Items.ChatList is ChatListArchive
                    || ViewModel.Folders.Count > 0 && !ViewModel.Chats.Items.ChatList.AreTheSame(ViewModel.Folders[0].ChatList))
                {
                    UpdateFolder(ViewModel.Folders.Count > 0 ? ViewModel.Folders[0] : ChatFolderViewModel.Main);
                    args.Handled = true;
                }
            }
        }

        private int _prevIndex;

        private void LoadAtIndex(int index)
        {
            if (index == _prevIndex)
            {
                return;
            }

            if (index == 0)
            {
                SettingsRoot?.Visibility = Visibility.Collapsed;

                Show(ChatsRoot, _prevIndex > index, 0);
            }
            else if (index == 1)
            {
                ChatsRoot.Visibility = Visibility.Collapsed;

                if (SettingsRoot != null)
                {
                    Show(SettingsRoot, _prevIndex > index, 1);
                }
            }

            _prevIndex = index;
            Pivot_SelectionChanged(null, null);
        }

        private void Show(UIElement element, bool leftToRight, int index)
        {
            if (_prevIndex == -1)
            {
                return;
            }

            element.Visibility = Visibility.Visible;

            if (!PowerSavingPolicy.AreSmoothTransitionsEnabled)
            {
                return;
            }

            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            var visualIn = ElementComposition.GetElementVisual(element);
            var offsetIn = visualIn.Compositor.CreateScalarKeyFrameAnimation();
            offsetIn.InsertKeyFrame(0, leftToRight ? -48 : 48);
            offsetIn.InsertKeyFrame(1, 0);
            offsetIn.Duration = Constants.SoftAnimation;

            var opacityIn = visualIn.Compositor.CreateScalarKeyFrameAnimation();
            opacityIn.InsertKeyFrame(0, 0);
            opacityIn.InsertKeyFrame(1, 1);
            opacityIn.Duration = Constants.SoftAnimation;

            visualIn.StartAnimation("Translation.X", offsetIn);
            visualIn.StartAnimation("Opacity", opacityIn);
        }

        private void SettingsRoot_Loaded(object sender, RoutedEventArgs e)
        {
            SettingsRoot.Loaded -= SettingsRoot_Loaded;
            Show(SettingsRoot, _prevIndex > 1, 1);
        }

        private void UpdateUser(User user)
        {
            TitleBarLogo.IsEnabled = _clientService.IsPremium;

            if (user.EmojiStatus != null)
            {
                LogoBasic.Visibility = Visibility.Collapsed;
                LogoEmoji.Visibility = Visibility.Visible;
                LogoEmoji.Source = new CustomEmojiFileSource(_clientService, user.EmojiStatus.Type);

                if (user.EmojiStatus.Type is EmojiStatusTypeUpgradedGift upgradedGift)
                {
                    LogoEmojiParticles.Source = new ParticlesImageSource(upgradedGift.BackdropColors);
                }
                else
                {
                    LogoEmojiParticles.Source = null;
                }
            }
            else
            {
                LogoBasic.Visibility = Visibility.Visible;
                LogoEmoji.Visibility = Visibility.Collapsed;
                LogoEmoji.Source = null;
                LogoEmojiParticles.Source = null;
            }
        }

        // u-091: this method has already been bitten once by exactly the failure the wrapper now
        // contains - the CoreWindow NullReferenceException documented a few lines below aborted the
        // rest of OnLoaded and took UpdateChatFolders, the connection/unread/session updates and
        // the folder tabs down with it, silently. That specific trap is guarded; the METHOD was
        // still async void with no try, so the next one costs the same again.
        //
        // Wrapping it does not make a failed load succeed - the point is that it stops being
        // invisible. Uncaught, the throw is swallowed by NativeDispatcher.RunAction into one
        // anonymous "NativeDispatcher unhandled exception" console line (measured: see
        // review/probes/AsyncVoidLanding.cs); caught here it is logged as itself, with the stack
        // that says which of the twenty-odd things OnLoaded does was the one that died.
        //
        // Split rather than indented, like ShowMessageMenuLinux: the body stays verbatim and
        // becomes async Task so its exceptions travel on the task, and XAML keeps binding
        // Loaded="OnLoaded" against the small wrapper.
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await OnLoadedCore(sender, e);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private async Task OnLoadedCore(object sender, RoutedEventArgs e)
        {
#if LINUX
            MoveStoriesIntoTitleBar();
#endif
            if (_clientService.TryGetUser(_clientService.Options.MyId, out User user))
            {
                UpdateUser(user);
            }

            Subscribe();

            var context = WindowContext.ForXamlRoot(XamlRoot);
#if LINUX
            // u-charrecv-b: Uno's WinUI Window has no CoreWindow, so WindowContext.CoreWindow reads
            // null and the NullReferenceException used to abort the rest of OnLoaded, taking
            // UpdateChatFolders and the initial connection/unread/session updates down with it.
            // The guard below kept the page alive but left the wire dead: typing a letter with the
            // chat list focused no longer jumped into search.
            //
            // Bridged the way ChatView.xaml.cs does it (u-19, Stanley): tunnel on the window's
            // content, read the character Uno decoded, and focus the box WITHOUT inserting -- the
            // box types the keystroke itself, because PreviewKeyDown runs before delivery.
            if (context?.Content is UIElement windowContent)
            {
                windowContent.PreviewKeyDown += OnWindowPreviewKeyDown;
            }
#else
            if (context != null)
            {
                context.CharacterReceived += OnCharacterReceived;
            }
#endif

            OnStateChanged(null, null);

            ShowHideBanner(LifetimeService.Current.Playback);

            var update = new UpdateConnectionState(ViewModel.ClientService.ConnectionState);
            if (update.State != null)
            {
                Handle(update);
                ViewModel.Aggregator.Publish(update);
            }

            Handle(new UpdateUnconfirmedSession(ViewModel.ClientService.UnconfirmedSession, 1));
            Handle(new UpdateActiveCall());
            Handle(ViewModel.ClientService.FreezeState);
            UpdateChatFolders();

            if (_clientService.HasSuggestedAction(new SuggestedActionSetBirthdate()))
            {
                MaterializeCard(nameof(SetBirthdateCard));
            }

            if (_unloaded)
            {
                _unloaded = false;
                ChatsList.ItemsSource = null;

                Bindings.StopTracking();
                Bindings.Update();
            }

            WatchDog.TrackEvent("MainPage");

            if (AppSettings.Diagnostics.IsLastErrorDiskFull)
            {
                AppSettings.Diagnostics.IsLastErrorDiskFull = false;

                // TODO: Missing translation
                var confirm = await ViewModel.ShowPopupAsync("Unigram has previously failed to launch because the device storage was full.\r\n\r\nMake sure there's enough storage space available and press **OK** to continue.", "Disk storage is full", Strings.OK, Strings.StorageUsage);
                if (confirm == ContentDialogResult.Secondary)
                {
#if !LINUX
                    MasterDetail.NavigationService.Navigate(typeof(SettingsStoragePage));
#endif
                }
            }
        }

        private void Subscribe()
        {
            ViewModel.Aggregator.Subscribe<UpdateFileDownloads>(this, Handle)
                .Subscribe<UpdateChatActiveStories>(Handle)
                .Subscribe<UpdateChatIsMarkedAsUnread>(Handle)
                .Subscribe<UpdateChatReadInbox>(Handle)
                .Subscribe<UpdateChatReadOutbox>(Handle)
                .Subscribe<UpdateChatUnreadMentionCount>(Handle)
                .Subscribe<UpdateChatUnreadReactionCount>(Handle)
                .Subscribe<UpdateChatUnreadPollVoteCount>(Handle)
                .Subscribe<UpdateChatAddedToList>(Handle)
                .Subscribe<UpdateChatRemovedFromList>(Handle)
                .Subscribe<UpdateChatTitle>(Handle)
                .Subscribe<UpdateChatPhoto>(Handle)
                .Subscribe<UpdateChatEmojiStatus>(Handle)
                .Subscribe<UpdateChatVideoChat>(Handle)
                .Subscribe<UpdateChatViewAsTopics>(Handle)
                .Subscribe<UpdateUserStatus>(Handle)
                .Subscribe<UpdateUser>(Handle)
                .Subscribe<UpdateChatMessageAutoDeleteTime>(Handle)
                .Subscribe<UpdateChatAction>(Handle)
                .Subscribe<UpdateMessageMentionRead>(Handle)
                .Subscribe<UpdateMessageUnreadReactions>(Handle)
                .Subscribe<UpdateUnreadChatCount>(Handle)
                .Subscribe<UpdateChatUnreadTopicCount>(Handle)
                .Subscribe<UpdateSecretChat>(Handle)
                .Subscribe<UpdateChatNotificationSettings>(Handle)
                .Subscribe<UpdatePasscodeLock>(Handle)
                .Subscribe<UpdateUnconfirmedSession>(Handle)
                .Subscribe<UpdateFreezeState>(Handle)
                .Subscribe<UpdateConnectionState>(Handle)
                .Subscribe<UpdateOption>(Handle)
                .Subscribe<UpdateSuggestedActions>(Handle)
                .Subscribe<UpdateActiveCall>(Handle)
                .Subscribe<UpdateChatFoldersLayout>(Handle)
                .Subscribe<UpdateConfetti>(Handle);
        }

        private void OnPlaybackSourceChanged(IPlaybackService sender, object e)
        {
            this.BeginOnUIThread(() => ShowHideBanner(sender));
        }

        private bool _playbackVisible;
        private bool _activeCallVisible;

        private void ShowHideBanner(IPlaybackService sender)
        {
            // Orden de upstream intacto, por lo mismo que en Handle(UpdateActiveCall): medido en
            // pantalla, esta banda YA salia antes del arreglo.
            if (sender.CurrentItem != null && Playback == null)
            {
                MaterializeCard(nameof(Playback), () => Playback.Update(ViewModel.ClientService, ViewModel.NavigationService));
            }

            _playbackVisible = sender.CurrentItem != null;
            MasterDetail.ShowHideBanner(_playbackVisible || _activeCallVisible);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var context = WindowContext.ForXamlRoot(this);
#if LINUX
            if (context?.Content is UIElement windowContent)
            {
                windowContent.PreviewKeyDown -= OnWindowPreviewKeyDown;
            }
#else
            if (context != null)
            {
                context.CharacterReceived -= OnCharacterReceived;
            }
#endif

            Bindings.StopTracking();

            _unloaded = true;

            LeakTest(false);
        }

        private void OnCharacterReceived(WindowContext sender, CharacterReceivedRoutedEventArgs args)
        {
            var character = args.Character.ToString();

            if (TryOpenSearchForCharacter(character))
            {
                args.Handled = true;
            }
        }

        // The body below is the receiver as it was, split off only so that the Linux emitter can
        // reach it: there is no CharacterReceivedEventArgs to hand it (Uno's has no public
        // constructor), and a second copy of this decision would be a second copy to keep in step.
        // Deciding `Handled` is left to each caller, which is the only thing that differs.
        // insert: false opens search and focuses the box but leaves the character to whoever
        // delivers it, which is what the Linux emitter needs; see OnWindowPreviewKeyDown.
        private bool TryOpenSearchForCharacter(string character, bool insert = true)
        {
            if (MasterDetail.NavigationService?.Frame.Content is not BlankPage)
            {
                return false;
            }

            if (character.Length == 0 || char.IsControl(character[0]) || char.IsWhiteSpace(character[0]))
            {
                return false;
            }

            var focused = FocusManagerEx.TryGetFocusedElement(XamlRoot);
            if (focused is null or (not TextBox and not RichEditBox))
            {
                var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot);
                if (popups.Count > 0)
                {
                    return false;
                }

                Search_Click(null, null);
                SearchField.Focus(FocusState.Keyboard);

                if (insert)
                {
                    SearchField.Text = character;
                    SearchField.SelectionStart = character.Length;
                }

                return true;
            }

            return false;
        }

#if LINUX
        // Sync on purpose. An async void handler here would be swallowed by NativeDispatcher and
        // read as a dead wire rather than as an exception.
        private void OnWindowPreviewKeyDown(object sender, KeyRoutedEventArgs args)
        {
            CharacterInputBridge.Claim(args, nameof(MainPage), TryOpenSearchForCharacter, "opening search");
        }
#endif

        private static Task CollectAsync()
        {
#if NET9_0_OR_GREATER
            // Off the view thread, or this deadlocks. A finalizing RCW releases through
            // IContextCallback back into the apartment that created it, and an ASTA will not
            // dispatch an incoming call while it is blocked in a wait - so waiting for the
            // finalizers here would block the one thread the finalizer needs.
            return Task.Run(static () =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            });
#else
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Already completed, so the caller's await continues inline and the order below is
            // exactly what it was before the NET9 path existed.
            return Task.CompletedTask;
#endif
        }

        private async void CollectAndAnalyze()
        {
            await CollectAsync();

#if INSTRUMENTATION
            Logger.Info(DebugAnalyzeOrphans());
            Profiler.Report();
#endif

#if !NET9_0_OR_GREATER
            GarbageCollectionMonitor.DisconnectUnusedReferenceSources();
#endif
        }

        public void ProcessKeyboardAccelerators(ShortcutInvokedEventArgs args)
        {
            foreach (var command in args.Shortcut.Commands)
            {
                if (AppSettings.Diagnostics.ShowMemoryUsage && command == ShortcutCommand.Quit)
                {
                    if (!MasterDetail.NavigationService.CanGoBack)
                    {
                        MasterDetail.NavigationService.Navigate(typeof(BlankPage), Guid.NewGuid());
                        MasterDetail.NavigationService.Frame.BackStack.Clear();
                        MasterDetail.NavigationService.Frame.ForwardStack.Clear();
                        MasterDetail.NavigationService.ClearCache(true);
                    }

                    //GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    //GCSettings.LatencyMode = GCSettings.LatencyMode == GCLatencyMode.Interactive
                    //    ? GCLatencyMode.SustainedLowLatency
                    //    : GCLatencyMode.Interactive;

                    CollectAndAnalyze();
                    return;
                }

                ProcessChatCommands(command, args);
                ProcessFolderCommands(command, args);
                ProcessAppCommands(command, args);
            }
        }

#if INSTRUMENTATION
        // ONE Analyze over the union of every instrumented area's roots.
        //
        // It has to be one call. Orphans are "registered but not reachable from the roots", so a per-area
        // call would mark every other area's live objects as leaked — analysing the chat alone reports the
        // open gallery as a leak, and analysing the gallery alone reports the whole message list as one.
        //
        // Adding an area means adding its roots here and chaining its descent below; both return nothing
        // for types they do not own, so the order does not matter and overlaps are harmless (the walk is
        // over a set).
        private string DebugAnalyzeOrphans()
        {
            var roots = new List<object>();

            // The view itself, not its containers: descent reaches those through it now. Any
            // OTHER ChatView still alive is therefore unreachable from the roots and gets
            // reported -- which is the point, since its containers used to show up as orphans
            // while the view that held them stayed invisible to the analysis.
            var chat = this.GetChild<ChatView>();
            if (chat != null)
            {
                roots.Add(chat);
            }

            roots.AddRange(Telegram.Controls.Gallery.GalleryWindow.DebugRoots());
            roots.AddRange(WebAppWindow.DebugRoots());

            return Telegram.Common.Instrumentation.Analyze(roots, DebugChildrenOf)
                + AnimatedImageLoader.DebugReport();
        }

        private static IEnumerable<object> DebugChildrenOf(object node)
        {
            foreach (var child in ChatView.DebugChildrenOf(node))
            {
                yield return child;
            }

            foreach (var child in Telegram.Controls.Gallery.GalleryWindow.DebugChildrenOf(node))
            {
                yield return child;
            }

            foreach (var child in WebAppWindow.DebugChildrenOf(node))
            {
                yield return child;
            }

            foreach (var child in Telegram.Controls.StickerPanel.DebugChildrenOf(node))
            {
                yield return child;
            }
        }
#endif

        private async void ProcessAppCommands(ShortcutCommand command, ShortcutInvokedEventArgs args)
        {
            if (command is ShortcutCommand.SetStatus)
            {
                Status_Click(null, null);
                args.Handled = true;
            }
            else if (command is ShortcutCommand.Search)
            {
                if (MasterDetail.NavigationService.Frame.Content is ISearchablePage child)
                {
                    child.Search();
                }
                else
                {
                    SearchField.Focus(FocusState.Keyboard);
                    Search_Click(null, null);
                }

                args.Handled = true;
            }
            else if (command is ShortcutCommand.SearchChats)
            {
                SearchField.Focus(FocusState.Keyboard);
                Search_Click(null, null);

                args.Handled = true;
            }
            else if (command is ShortcutCommand.Quit)
            {
#if !LINUX
                await BridgeApplicationContext.ExitAsync();
#endif
                await BootStrapper.ConsolidateAsync();
            }
            else if (command is ShortcutCommand.Close)
            {
                await ViewModel.Window.ConsolidateAsync();
            }
            else if (command is ShortcutCommand.Lock)
            {
                Lock_Click(null, null);
                args.Handled = true;
            }
            else if (command is ShortcutCommand.Downloads)
            {
#if !LINUX
                Downloads_Click(null, null);
                args.Handled = true;
#endif
                // COLAPSADO 2026-08-27 (PARIDAD M26). En Linux el atajo NO se consume: el
                // args.Handled = true estaba fuera de toda guarda y Downloads_Click con el cuerpo
                // apagado, o sea que la tecla se tragaba y no abria nada. Mismo patron que el
                // arreglo de ShortcutCommand.MediaStop de mas abajo, al reves.
            }
            else if (command is ShortcutCommand.MediaStop)
            {
                // La guarda #if !LINUX se puso cuando IPlaybackService era inerte, y dejaba el
                // args.Handled = true FUERA: la tecla se consumia y el audio seguia sonando. Desde
                // la fase 5 el PlaybackService compartido esta vivo (LifetimeService.Playback no
                // tiene ninguna guarda), asi que el atajo vuelve a parar la reproduccion.
                LifetimeService.Current.Playback.Clear();
                args.Handled = true;
            }
#if !LINUX
            else if (command is ShortcutCommand.CallAccept && ViewModel.VoipService.ActiveCall is VoipCall acceptCall)
            {
                acceptCall.Accept(false);
                args.Handled = true;
            }
            else if (command is ShortcutCommand.CallReject && ViewModel.VoipService.ActiveCall is VoipCall rejectCall)
            {
                rejectCall.Discard();
                args.Handled = true;
            }
#endif
        }

        private void ProcessFolderCommands(ShortcutCommand command, ShortcutInvokedEventArgs args)
        {
            var folders = ViewModel.Folders;
            if (folders.Empty())
            {
                return;
            }

            if (command == ShortcutCommand.FolderPrevious)
            {
                args.Handled = true;
                ScrollFolder(-1, true);
            }
            else if (command == ShortcutCommand.FolderNext)
            {
                args.Handled = false;
                ScrollFolder(+1, true);
            }
            else if (command == ShortcutCommand.ShowAllChats)
            {
                args.Handled = true;
                ScrollFolder(int.MinValue, true);
            }
            else if (command == ShortcutCommand.ShowFolderLast)
            {
                args.Handled = true;
                ScrollFolder(int.MaxValue, true);
            }
            else if (command == ShortcutCommand.ShowArchive)
            {
                args.Handled = true;
                ArchivedChats_Click(null, null);
            }
            else if (command is >= ShortcutCommand.ShowFolder1 and <= ShortcutCommand.ShowFolder6)
            {
                var index = command - ShortcutCommand.ShowAllChats;
                if (folders.Count > index)
                {
                    UpdateFolder(folders[index], false);
                }
            }
        }

        private async void ProcessChatCommands(ShortcutCommand command, ShortcutInvokedEventArgs args)
        {
            if (command == ShortcutCommand.ChatRecentPrevious)
            {
                args.Handled = ShowChatSwitch(false);
            }
            else if (command == ShortcutCommand.ChatRecentNext)
            {
                args.Handled = ShowChatSwitch(true);
            }
            if (command == ShortcutCommand.ChatPrevious)
            {
                args.Handled = true;
                Scroll(-1, true);
            }
            else if (command == ShortcutCommand.ChatNext)
            {
                args.Handled = true;
                Scroll(+1, true);
            }
            else if (command == ShortcutCommand.ChatFirst)
            {
                args.Handled = true;
                Scroll(int.MinValue, true);
            }
            else if (command == ShortcutCommand.ChatLast)
            {
                args.Handled = true;
                Scroll(int.MaxValue, true);
            }
            else if (command == ShortcutCommand.ChatSelf)
            {
                args.Handled = true;

                if (ViewModel.ClientService.TryGetChat(ViewModel.ClientService.Options.MyId, out Chat chat))
                {
                    MasterDetail.NavigationService.NavigateToChat(chat, force: false);
                    MasterDetail.NavigationService.GoBackAt(0, false);
                }
            }
            else if (command is >= ShortcutCommand.ChatPinned1 and <= ShortcutCommand.ChatPinned5)
            {
                var folders = ViewModel.Folders;
                if (folders.Count > 0)
                {
                    return;
                }

                var index = command - ShortcutCommand.ChatPinned1;

                var response = await ViewModel.ClientService.GetChatListAsync(new ChatListMain(), 0, (int)ViewModel.ClientService.Options.PinnedChatCountMax * 2 + 1);
                if (response is Telegram.Td.Api.Chats chats && index >= 0 && index < chats.ChatIds.Count)
                {
                    for (int i = 0; i < chats.ChatIds.Count; i++)
                    {
                        var chat = ViewModel.ClientService.GetChat(chats.ChatIds[i]);
                        if (chat == null)
                        {
                            return;
                        }

                        //if (chat.Source != null)
                        //{
                        //    index++;
                        //}
                        //else if (i == index)
                        //{
                        //    if (chat.IsPinned)
                        //    {
                        //        MasterDetail.NavigationService.NavigateToChat(chats.ChatIds[index]);
                        //        MasterDetail.NavigationService.GoBackAt(0, false);
                        //    }

                        //    return;
                        //}
                    }
                }
            }
        }

        private bool ShowChatSwitch(bool start)
        {
#if LINUX
            // RecentChatsView is intentionally outside the Linux subset: the shortcut needs the
            // MRU navigation, not a new full-window overlay. Keep its ordering semantics exactly:
            // current chat first, Ctrl+Tab selects the next item, Ctrl+Shift+Tab the last one.
            var chats = ViewModel.ClientService.GetRecentlyOpenedChats();
            var current = MasterDetail.NavigationService.GetChatFromBackStack(true);
            var hasCurrent = ViewModel.ClientService.TryGetChat(current.ChatId, out Chat currentChat);

            if (hasCurrent)
            {
                chats.Remove(currentChat);
                chats.Insert(0, currentChat);
            }

            if (chats.Count < 2)
            {
                return false;
            }

            var index = start
                ? hasCurrent ? 1 : 0
                : chats.Count - 1;

            MasterDetail.NavigationService.NavigateToChat(chats[index], force: false, clearBackStack: true);
            return true;
#else
            foreach (var open in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            {
                if (open.Child is RecentChatsView)
                {
                    return false;
                }
            }

            if (ViewModel.ClientService.RecentlyOpenedChatsCount > 1)
            {
                var popup = new Popup
                {
                    XamlRoot = XamlRoot
                };

                popup.Child = new RecentChatsView(ViewModel.ClientService, MasterDetail.NavigationService, popup, start)
                {
                    Width = ActualWidth,
                    Height = ActualHeight
                };

                popup.IsOpen = true;
                return true;
            }

            return false;
#endif
        }

        public void Scroll(int offset, bool navigate)
        {
            if (!_topicListCollapsed)
            {
                TopicListPresenter.Scroll(offset, navigate);
                return;
            }

            int index;
            if (offset == int.MaxValue)
            {
                index = ViewModel.Chats.Items.Count - 1;
            }
            else if (offset == int.MinValue)
            {
                index = 0;
            }
            else
            {
                index = ChatsList.SelectedIndex + offset;
            }

            if (index >= 0 && index < ViewModel.Chats.Items.Count)
            {
                if (navigate)
                {
                    Navigate(ViewModel.Chats.Items[index], false);
                }
            }
            else if (index < 0 && offset == -1 && !navigate)
            {
                Search_Click(null, null);
            }
        }

        public void ScrollFolder(int offset, bool navigate)
        {
            var already = ViewModel.SelectedFolder;
            if (already == null)
            {
                return;
            }

            var index = ViewModel.Folders.IndexOf(already);
            if (offset == int.MaxValue)
            {
                index = ViewModel.Folders.Count - 1;
            }
            else if (offset == int.MinValue)
            {
                index = 0;
            }
            else
            {
                index += offset;
            }

            if (index >= 0 && index < ViewModel.Folders.Count)
            {
                UpdateFolder(ViewModel.Folders[index], true);
            }
        }

        public void Initialize()
        {
            Frame.BackStack.Clear();

            if (MasterDetail.NavigationService == null)
            {
                MasterDetail.Initialize("Main", Frame, ViewModel);
                MasterDetail.NavigationService.FrameFacade.Navigating += OnNavigating;
                MasterDetail.NavigationService.FrameFacade.Navigated += OnNavigated;
            }

            ViewModel.NavigationService = MasterDetail.NavigationService;

            ArchivedChats.UpdateChatList(ViewModel.ClientService, new ChatListArchive());
            ArchivedChats.UpdateStoryList(ViewModel.ClientService, new StoryListArchive());
        }

        public void Activate(string parameter)
        {
            Initialize();

            if (parameter == null)
            {
                return;
            }

            if (parameter.StartsWith("tg:toast"))
            {
                parameter = parameter.Substring("tg:toast?".Length).TrimStart('?');
            }
            else if (parameter.StartsWith("tg://toast"))
            {
                parameter = parameter.Substring("tg://toast?".Length).TrimStart('?');
            }

#if !LINUX
            var data = Toast.SplitArguments(parameter);
            if (data.TryGetValue("web_app", out string webApp))
            {
                parameter = Toast.FromBase64(webApp);
            }
#else
            // Common/Toast.cs (ToastNotification) is out of the subset: same key=value&… split.
            var data = new Dictionary<string, string>();
            foreach (var item in parameter.Split('&'))
            {
                var pair = item.Split('=');
                if (pair.Length == 2)
                {
                    data[pair[0]] = pair[1];
                }
            }
#endif

            if (Uri.TryCreate(parameter, UriKind.Absolute, out Uri scheme))
            {
                Activate(scheme);
            }
            else
            {
                data.TryGetValue("chat_id", out string chat_id);
                data.TryGetValue("forum_topic_id", out string forum_topic_id);
                data.TryGetValue("saved_messages_topic_id", out string saved_messages_topic_id);
                data.TryGetValue("feedback_chat_topic_id", out string feedback_chat_topic_id);
                data.TryGetValue("thread_id", out string thread_id);

                long.TryParse(chat_id, out long chatId);

                MessageTopic messageTopic = null;
                if (int.TryParse(forum_topic_id, out int forumTopicId))
                {
                    messageTopic = new MessageTopicForum(forumTopicId);
                }
                else if (long.TryParse(saved_messages_topic_id, out long savedMessagesTopicId))
                {
                    messageTopic = new MessageTopicSavedMessages(savedMessagesTopicId);
                }
                else if (long.TryParse(feedback_chat_topic_id, out long directMessagesChatTopicId))
                {
                    messageTopic = new MessageTopicDirectMessages(directMessagesChatTopicId);
                }
                else if (long.TryParse(thread_id, out long threadId))
                {
                    messageTopic = new MessageTopicThread(threadId);
                }

                if (_clientService.TryGetChat(chatId, out Chat chat))
                {
                    if (chat.ViewAsTopics)
                    {
                        MasterDetail.NavigationService.NavigateToChat(chat.Id, topic: messageTopic, force: false);
                    }
                    else
                    {
                        MasterDetail.NavigationService.NavigateToChat(chat.Id, force: false);
                    }
                }
            }

            if (XamlRoot == null)
            {
                return;
            }

            var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot);
            if (popups != null)
            {
#if !LINUX
                foreach (var popup in popups)
                {
                    if (popup.Child is GalleryWindow gallery)
                    {
                        gallery.OnBackRequested(new BackRequestedRoutedEventArgs());
                        break;
                    }
                }
#endif
            }
        }

        public async void Activate(Uri scheme)
        {
            if (MessageHelper.IsTelegramUrl(scheme))
            {
                MessageHelper.OpenTelegramUrl(ViewModel.ClientService, MasterDetail.NavigationService, scheme);
            }
            else if (scheme.Scheme.Equals("ms-contact-profile") || scheme.Scheme.Equals("ms-ipmessaging"))
            {
                var query = scheme.Query.ParseQueryString();
                if (query.TryGetValue("ContactRemoteIds", out string remote) && int.TryParse(remote.Substring(1), out int from_id))
                {
                    var response = await ViewModel.ClientService.SendAsync(new CreatePrivateChat(from_id, false));
                    if (response is Chat chat)
                    {
                        MasterDetail.NavigationService.NavigateToChat(chat, force: false);
                    }
                }
            }
        }

        private void OnNavigating(object sender, NavigatingEventArgs e)
        {
            var allowed = e.SourcePageType == typeof(ChatPage) ||
                // In the subset since scheduled messages got their own page back: without it the
                // list of scheduled messages would draw over the Material background instead of
                // the chat wallpaper, which is the one place this list is consulted.
                e.SourcePageType == typeof(ChatScheduledPage) ||
#if !LINUX
                e.SourcePageType == typeof(ChatPinnedPage) ||
                e.SourcePageType == typeof(ChatEventLogPage) ||
                e.SourcePageType == typeof(ChatBusinessRepliesPage) ||
                e.SourcePageType == typeof(ChatWelcomeMessagesPage) ||
#endif
                e.SourcePageType == typeof(BlankPage);

            var animate = MasterDetail.CurrentState != MasterDetailState.Minimal ||
                (e.SourcePageType != typeof(BlankPage) && e.Content is not BlankPage);

            var type = allowed ? BackgroundKind.Background : BackgroundKind.Material;

            if (MasterDetail.CurrentState == MasterDetailState.Minimal && e.SourcePageType == typeof(BlankPage))
            {
                type = BackgroundKind.None;
            }

            if (MasterDetail.CurrentState != MasterDetailState.Unknown)
            {
                MasterDetail.ShowHideBackground(type, animate);
            }
        }

#if LINUX
        /// <summary>
        /// Puts the chat list's own header back when the detail pane returns to the blank page.
        /// </summary>
        /// <remarks>
        /// The search box and the folder strip are set up by <see cref="ConvertFolder"/> and
        /// <see cref="OnStateChanged"/>, and both of those run when the FOLDER or the pane state
        /// changes. Opening the archive is a folder change and correctly hides the folder strip
        /// (an archive has no folders); coming back out of a chat is neither, so nothing puts it
        /// back, and the chat list is left with the header the archive gave it. Repro: archived
        /// chats, open a chat, Back - and the top bar is gone.
        ///
        /// Re-asserting it here is cheap and cannot cause a flicker: ShowHideTopTabs and
        /// ShowHideArchive both return immediately when the state already matches, which is every
        /// other navigation.
        /// </remarks>
        private void RestoreChatListHeader()
        {
            if (ViewModel?.SelectedFolder == null || ChatsList == null)
            {
                return;
            }

            var before = _tabsTopCollapsed;

            ConvertFolder(ViewModel.SelectedFolder, updateBackStack: false);

            if (before != _tabsTopCollapsed)
            {
                Logger.Info($"chat list header: folder strip collapsed {before} -> {_tabsTopCollapsed}");
            }

            // ConvertFolder decides whether the strip SHOULD be up, and ShowHideTopTabs carries
            // that out - but only through Visibility, and only when its own latch says the state
            // is changing. Opacity is written by somebody else entirely: ShowHideTopicList sets
            // Header.Opacity and ChatTabs.Opacity to 0 while the topic list is up. Nothing in the
            // ShowHideTopTabs path reads or restores that, so once the strip is left at zero
            // opacity it is invisible for good and every re-assert through ConvertFolder agrees
            // that it is already "shown". That is why the pills went and stayed gone.
            //
            // So assert the two properties the latched state machines do not own. Guarded on
            // _topicListCollapsed, because while the topic list really IS up the zero is correct.
            if (!_topicListCollapsed)
            {
                return;
            }

            if (Header != null && (Header.Opacity < 1 || !Header.IsHitTestVisible))
            {
                Logger.Info($"chat list header: search box left invisible (opacity {Header.Opacity:F2}, hit-testable {Header.IsHitTestVisible}) with no topic list up, restoring");

                Header.Opacity = 1;
                Header.IsHitTestVisible = true;
            }

            if (ChatTabs != null && !_tabsTopCollapsed && ChatTabs.Opacity < 1)
            {
                Logger.Info($"chat list header: folder strip left at opacity {ChatTabs.Opacity:F2} with no topic list up, restoring");

                ChatTabs.Opacity = 1;
            }
        }

        /// <summary>
        /// Closes menus left open on the popup root when the detail frame navigates.
        /// </summary>
        /// <remarks>
        /// Same boundary as the gallery wedge fixed in OverlayWindow.Hide: Uno's popup root is a
        /// SIBLING of Window.Content, so a MenuFlyout opened over a page is not a child of that
        /// page and navigating away does not take it down. It stays open with its anchor gone and
        /// swallows clicks and keys over the whole window - the app looks wedged with nothing on
        /// screen to dismiss, because on this head these menus frequently never draw.
        ///
        /// The reported repro is a deep walk inside a group - profile, its tabs, Members, Media -
        /// and then Back, which is exactly a path that opens context menus and then navigates.
        ///
        /// ONLY flyout presenters are closed, deliberately. A ContentPopup completes its queue
        /// from its own Closed event (ContentPopup.ShowQueuedAsync), and forcing its popup shut
        /// from outside would leave _currentDialogShowRequest set for the rest of the session -
        /// trading a wedge for an app with no dialogs at all. Menus have no such contract.
        /// </remarks>
        private void CloseOrphanedMenus()
        {
            var xamlRoot = XamlRoot;
            if (xamlRoot == null)
            {
                return;
            }

            try
            {
                var open = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);

                // A snapshot: closing a popup takes it out of the live collection.
                var snapshot = new Popup[open.Count];

                for (int i = 0; i < open.Count; i++)
                {
                    snapshot[i] = open[i];
                }

                var closed = 0;

                foreach (var popup in snapshot)
                {
                    if (popup == null || !popup.IsOpen || !IsMenu(popup.Child))
                    {
                        continue;
                    }

                    popup.IsOpen = false;
                    closed++;
                }

                if (closed > 0)
                {
                    // Only when there was something orphaned, so this line means "the wedge was
                    // about to happen and this cleared it", not "a navigation happened".
                    Logger.Info($"nav: {closed} menu(s) left open over the window by the previous page; closed them so they do not eat the input");
                }

                // What is deliberately NOT closed, named rather than closed. The shared-media tab
                // repro (circle Pinned/Media/Links/Music/Posts, then Back) involves no context
                // menu at all, so if that wedge is a leftover on the popup root it is one of
                // these - and this is the line that identifies it without touching a queue whose
                // Closed event somebody is awaiting.
                var survivors = string.Empty;

                foreach (var popup in snapshot)
                {
                    if (popup is { IsOpen: true } && !IsMenu(popup.Child))
                    {
                        survivors += (survivors.Length > 0 ? ", " : string.Empty)
                            + (popup.Child?.GetType().Name ?? "null");
                    }
                }

                if (survivors.Length > 0)
                {
                    Logger.Info($"nav: still open over the window after this navigation, left alone on purpose: {survivors}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "nav: could not close the menus left over the window");
            }
        }

        private static bool IsMenu(UIElement child)
        {
            if (child is MenuFlyoutPresenter or FlyoutPresenter)
            {
                return true;
            }

            // Uno wraps the presenter often enough that the child is a panel with the presenter
            // inside it; the same shape Extensions.Presenter() already has to allow for. Walked by
            // hand with a depth cap rather than through Descendants<T>, so this file does not take
            // a dependency on LinqToVisualTree for one call: a menu presenter is one or two levels
            // under the popup child, never deep.
            return HasMenuPresenter(child, 3);
        }

        private static bool HasMenuPresenter(DependencyObject root, int depth)
        {
            if (root == null || depth < 0)
            {
                return false;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is MenuFlyoutPresenter or FlyoutPresenter || HasMenuPresenter(child, depth - 1))
                {
                    return true;
                }
            }

            return false;
        }

#endif
        private void OnNavigated(object sender, NavigatedEventArgs e)
        {
            if (MasterDetail.CurrentState == MasterDetailState.Minimal)
            {
                MasterDetail.AllowCompact = true;
            }
            else
            {
                MasterDetail.AllowCompact = e.SourcePageType != typeof(BlankPage) && _prevIndex == INDEX_CHATS;
            }

            _shouldGoBackWithDetail = true;

#if LINUX
            // Both of these run on EVERY detail-frame navigation, not just the ones that land on
            // the blank page. The first version keyed off BlankPage and so never fired on the path
            // the user actually walks - into a group profile and back out - which is a return to
            // the chat list without being a return to the blank page. Both are idempotent and
            // early-out cheaply when there is nothing to repair.
            RestoreChatListHeader();
            CloseOrphanedMenus();
#endif

            UpdatePaneToggleButtonVisibility();
            UpdateListViewsSelectedItem(MasterDetail.NavigationService.GetChatFromBackStack());

            SettingsView?.UpdateSelection(false);
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (MasterDetail.CurrentState == MasterDetailState.Minimal)
            {
                if (ViewModel.Chats.SelectionMode != ListViewSelectionMode.Multiple)
                {
                    ChatsList.SelectedItem = null;
                    ChatsList.SelectionMode = ListViewSelectionMode.None;
                }

                Header.Visibility = Visibility.Visible;
                TitleText.Visibility = Visibility.Visible;
            }
            else
            {
                if (ViewModel.Chats.SelectionMode != ListViewSelectionMode.Multiple)
                {
                    ChatsList.SelectionMode = ListViewSelectionMode.Single;
                    ChatsList.SelectedItem = ViewModel.Chats.Items.FirstOrDefault(x => x.Id == ViewModel.Chats.SelectedItem);
                }

                Header.Visibility = MasterDetail.CurrentState == MasterDetailState.Expanded ? Visibility.Visible : Visibility.Collapsed;
                TitleText.Visibility = MasterDetail.CurrentState == MasterDetailState.Expanded ? Visibility.Visible : Visibility.Collapsed;
            }

#if LINUX
            // Settings shares Header/RootGrid with Chats but has nothing left visible inside it
            // once UpdateHeader() collapses SearchField/ChatsOptions for it (see the comment
            // there) -- a resize-driven state change above must not re-show that now-blank band.
            if (_prevIndex == INDEX_SETTINGS)
            {
                Header.Visibility = Visibility.Collapsed;
            }
#endif

            UpdatePaneToggleButtonVisibility();

            ChatsList.UpdateViewState(MasterDetail.CurrentState);

            var frame = MasterDetail.NavigationService.Frame;
            var allowed = frame.CurrentSourcePageType == typeof(ChatPage) ||
                frame.CurrentSourcePageType == typeof(ChatScheduledPage) ||
#if !LINUX
                frame.CurrentSourcePageType == typeof(ChatPinnedPage) ||
                frame.CurrentSourcePageType == typeof(ChatEventLogPage) ||
                frame.CurrentSourcePageType == typeof(ChatBusinessRepliesPage) ||
                frame.CurrentSourcePageType == typeof(ChatWelcomeMessagesPage) ||
#endif
                frame.CurrentSourcePageType == typeof(BlankPage);

            var type = allowed ? BackgroundKind.Background : BackgroundKind.Material;

            if (MasterDetail.CurrentState == MasterDetailState.Minimal && frame.CurrentSourcePageType == typeof(BlankPage))
            {
                type = BackgroundKind.None;
            }

            if (MasterDetail.CurrentState != MasterDetailState.Unknown)
            {
                MasterDetail.ShowHideBackground(type, false);
            }
        }

        private void OnMasterVisibilityChanged(object sender, EventArgs e)
        {
            UpdateTitleBarMargins();

            Stories.IsVisible = MasterDetail.MasterVisibility == Visibility.Visible;
            Photo.Visibility = MasterDetail.MasterVisibility == Visibility.Visible || !_tabsLeftCollapsed
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdatePaneToggleButtonVisibility()
        {
            var visible = ViewModel.Chats.Items.ChatList is ChatListArchive
                || !_searchCollapsed
                || _prevIndex != INDEX_CHATS;

            if (MasterDetail.CurrentState == MasterDetailState.Minimal)
            {
                visible &= MasterDetail.NavigationService.CurrentPageType == typeof(BlankPage);
            }

            Photo.IsChecked = visible;
            //Photo.Glyph = visible
            //    ? Photo.HorizontalAlignment == HorizontalAlignment.Right
            //    ? Icons.ArrowRight
            //    : Icons.ArrowLeft
            //    : Icons.Hamburger;
        }

        private Visibility UpdateScrollingHostHeaderVisibility(MasterDetailState state, bool primaryFolderSelected)
        {
            return state == MasterDetailState.Compact
                ? Visibility.Collapsed
                : primaryFolderSelected
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateListViewsSelectedItem(ChatMessageTopic openChat, bool fromSelection = false)
        {
            if (openChat.ChatId == 0 && ViewModel.Topics.Chat != null)
            {
                openChat = new ChatMessageTopic(ViewModel.Topics.Chat.Id, null);
            }

            ViewModel.Chats.SelectedItem = openChat.ChatId;

            if (ViewModel.Topics.ChatId == openChat.ChatId)
            {
                ViewModel.Topics.SelectedItem = openChat.MessageTopic;
                ViewModel.Topics.Delegate?.SetSelectedItem(ViewModel.Topics.Items.GetItem(openChat.MessageTopic));
            }
            else
            {
                ViewModel.Topics.SelectedItem = null;
                ViewModel.Topics.Delegate?.SetSelectedItem(null);
            }

            if (ViewModel.Chats.SelectionMode != ListViewSelectionMode.Multiple)
            {
                try
                {
                    if (ViewModel.ClientService.TryGetChat(openChat.ChatId, out Chat chat) && ViewModel.Chats.Items.Contains(chat))
                    {
                        if (fromSelection)
                        {
                            // If we come from selection we need to delay this as ItemClick comes before SelectionChanged,
                            // hence, if we unselect here, the ListView internal code will re-select the item right away.
                            VisualUtilities.QueueCallbackForCompositionRendered(this, () => ChatsList.SelectedItem = chat);
                        }
                        else if (ChatsList.SelectedItem != chat)
                        {
                            ChatsList.SelectedItem = chat;
                        }
                    }
                    else if (fromSelection)
                    {
                        // If we come from selection we need to delay this as ItemClick comes before SelectionChanged,
                        // hence, if we unselect here, the ListView internal code will re-select the item right away.
                        VisualUtilities.QueueCallbackForCompositionRendered(this, () => ChatsList.ClearValue(Selector.SelectedItemProperty));
                    }
                    else
                    {
                        ChatsList.ClearValue(Selector.SelectedItemProperty);
                    }
                }
                catch
                {
                    // In some cases setting SelectedItem can trigger
                }
            }
        }

        public bool EvaluatePaneToggleButtonVisibility()
        {
            if (MasterDetail.CurrentState == MasterDetailState.Minimal)
            {
                return MasterDetail.NavigationService.CurrentPageType == typeof(BlankPage);
            }

            return true;
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e == null)
            {
                SearchField.Text = string.Empty;
                Search_LostFocus(null, null);
                return;
            }

            if (ViewModel.Chats.SelectionMode == ListViewSelectionMode.Multiple && e.ClickedItem is Chat chat)
            {
                if (ViewModel.Chats.SelectedItems.Contains(chat))
                {
                    ViewModel.Chats.SelectedItems.Remove(chat);
                }
                else
                {
                    ViewModel.Chats.SelectedItems.Add(chat);
                }

                if (ViewModel.Chats.SelectedItems.Empty())
                {
                    ViewModel.Chats.SelectionMode = MasterDetail.CurrentState == MasterDetailState.Minimal
                        ? ListViewSelectionMode.None
                        : ListViewSelectionMode.Single;
                }
            }
            else
            {
                Navigate(e.ClickedItem, true);
            }
        }

        private void ListView_ItemClick(object sender, ForumViewItemClickEventArgs e)
        {
            Navigate(e.ClickedItem, e.FromSelection);
        }

        public async void Navigate(object item, bool selectionChanged)
        {
            var profile = false;

#if !LINUX
            if (item is TLCallGroup callGroup)
            {
                item = callGroup.Message;
            }
#endif

            if (item is Message message)
            {
                ViewModel.Chats.SelectedItem = message.ChatId;

                var modifiers = WindowContext.KeyModifiers();
                var createNewWindow = modifiers == Windows.System.VirtualKeyModifiers.Control;

                var messageChat = ViewModel.ClientService.GetChat(message.ChatId);
                var hasTabs = ViewModel.ClientService.HasTabs(messageChat);

                MasterDetail.NavigationService.NavigateToChat(
                    messageChat,
                    message: message.Id,
                    topic: hasTabs ? message.TopicId : null,
                    force: false,
                    createNewWindow: createNewWindow);
            }
            else
            {
                SearchField.Text = string.Empty;
                Search_LostFocus(null, null);
            }

#if !LINUX
            if (item is TLCallGroup group)
            {
                item = group.Message;
            }
            else
#endif
            if (item is SearchResult result)
            {
                if (result.Chat != null)
                {
                    item = result.Chat;
                    ViewModel.ClientService.Send(new AddRecentlyFoundChat(result.Chat.Id));
                }
                else
                {
                    item = result.User;
                }

                profile = result.Type == SearchResultType.WebApps;
            }

            //if (item is TLMessageCommonBase message)
            //{
            //    if (message.Parent != null)
            //    {
            //        MasterDetail.NavigationService.NavigateToDialog(message.Parent, message.Id);
            //    }
            //}
            //else
            //{
            //    SearchField.Text = string.Empty;
            //}

            if (item is User user)
            {
                var response = await ViewModel.ClientService.SendAsync(new CreatePrivateChat(user.Id, false));
                if (response is Chat)
                {
                    item = response as Chat;
                }
            }

            if (item is Chat chat)
            {
                ViewModel.Chats.SelectedItem = chat.Id;

                if (chat.ViewAsTopics && chat.Type is ChatTypeSupergroup && !ViewModel.ClientService.HasTabs(chat))
                {
                    if (ViewModel.Chats.SelectedItem != ViewModel.Topics.Chat?.Id)
                    {
                        ShowTopicList(chat);
                    }
                    else
                    {
                        HideTopicList(true);
                    }
                }
                else
                {
                    if (profile)
                    {
                        MasterDetail.NavigationService.Navigate(typeof(ProfilePage), chat.Id);
                    }
                    else
                    {
                        var modifiers = WindowContext.KeyModifiers();
                        if (modifiers == VirtualKeyModifiers.Menu && selectionChanged)
                        {
                            Handle(chat, (cell, chat) => cell.ShowPreview(null));
                        }
                        else
                        {
                            var createNewWindow = selectionChanged && modifiers == VirtualKeyModifiers.Control;

                            // TODO: new display mode
                            var messageThreadId = chat.LastMessage != null && ViewModel.ClientService.IsForum(chat) ? chat.LastMessage.ForumTopicId() : 0;
                            messageThreadId = 0;

                            MasterDetail.NavigationService.NavigateToChat(chat, force: false, createNewWindow: createNewWindow, clearBackStack: true);
                        }
                    }

                    HideTopicList();
                }
            }
            else if (item is ForumTopic topic)
            {
                var modifiers = WindowContext.KeyModifiers();
                if (modifiers == VirtualKeyModifiers.Menu && selectionChanged)
                {
                    TopicListPresenter.HandleForumTopic(topic, (cell, chat) => cell.ShowPreview(null));
                }
                else
                {
                    var createNewWindow = selectionChanged && modifiers == VirtualKeyModifiers.Control;

                    ViewModel.Chats.SelectedItem = topic.Info.ChatId;
                    ViewModel.Topics.SelectedItem = topic.ToId();
                    MasterDetail.NavigationService.NavigateToChat(ViewModel.Topics.Chat, topic: topic.ToId(), force: false, createNewWindow: createNewWindow, clearBackStack: true);
                }
            }
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel.Chats.SelectedItems.Count > 0)
            {
                var muted = ViewModel.Chats.SelectedItems.Any(x => ViewModel.ClientService.Notifications.IsMuted(x));
                ManageMute.Glyph = muted ? Icons.Alert : Icons.AlertOff;
                Automation.SetToolTip(ManageMute, muted ? Strings.UnmuteNotifications : Strings.MuteNotifications);

                var unread = ViewModel.Chats.SelectedItems.Any(x => x.IsUnread());
                // Este era el unico sitio del port que fabricaba el FontIcon a mano, sin FontFamily
                // ni FontSize: el glifo es de la zona de uso privado de la fuente de Telegram, asi
                // que con la fuente por defecto no existe. MenuFlyoutHelper.CreateIcon SI compila en
                // Linux (solo las lineas 11-13 y 82-111 de MenuFlyoutHelper.cs son #if !LINUX) y es
                // lo que usa el resto de este mismo menu.
                ManageMark.Icon = MenuFlyoutHelper.CreateIcon(unread ? Icons.MarkAsRead : Icons.MarkAsUnread);
                ManageMark.Text = unread ? Strings.MarkAsRead : Strings.MarkAsUnread;

                ManageClear.IsEnabled = ViewModel.Chats.SelectedItems.All(x => DialogClear_Loaded(x));
            }
        }

        private void Pivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MasterDetail.AllowCompact = MasterDetail.NavigationService.CurrentPageType != typeof(BlankPage) && _prevIndex == INDEX_CHATS;

            switch (_prevIndex)
            {
                case INDEX_CHATS:
                    Root?.SetSelectedIndex(RootDestination.Chats);
                    break;
                case INDEX_SETTINGS:
                    _shouldGoBackWithDetail = false;

                    Root?.SetSelectedIndex(RootDestination.Settings);
                    SearchField.ControlledList = null;

                    if (SettingsView == null)
                    {
                        FindName(nameof(SettingsRoot));
                        SettingsView.DataContext = ViewModel.Settings;
                        ViewModel.Settings.Delegate = SettingsView;

                        _ = ViewModel.Settings.NavigatedToAsync(null, NavigationMode.Refresh, null);
                    }
                    break;
            }

            SearchField.Text = string.Empty;

            UpdateHeader();
            UpdatePaneToggleButtonVisibility();

            SearchReset();

            if (_prevIndex != INDEX_CHATS)
            {
                Stories.Collapse();
                ViewModel.IsSettingsSelected = true;
            }
            else
            {
                ViewModel.IsSettingsSelected = false;
            }

            _shouldGoBackWithDetail = false;

            for (int i = 0; i < ViewModel.Children.Count; i++)
            {
                if (ViewModel.Children[i] is IChildViewModel child)
                {
                    if (i == _prevIndex)
                    {
                        child.Activate();
                    }
                    else
                    {
                        child.Deactivate();
                    }
                }
            }
        }

        private void UpdateHeader()
        {
            if (_prevIndex == INDEX_CHATS)
            {
                ChatsOptions.Visibility = Visibility.Visible;
                SearchField.Padding = new Thickness(10, 5, 40, 6);
            }
            else
            {
                ChatsOptions.Visibility = Visibility.Collapsed;
                SearchField.Padding = new Thickness(10, 5, 6, 6);
            }

            SearchField.PlaceholderText = _prevIndex == INDEX_SETTINGS
                ? Strings.SearchInSettings
                : Strings.Search;

#if LINUX
            // La rama de Search_TextChanged que busca en los ajustes esta bajo #if !LINUX (el
            // servicio contesta Array.Empty y la lista de resultados lleva el prefijo win:), pero
            // este metodo colapsaba ChatsOptions y NO el campo de busqueda: al entrar en Ajustes
            // quedaba una caja de texto que ademas cambiaba su texto de ayuda a «Buscar en los
            // ajustes», o sea la app anunciaba la funcion en el mismo hueco donde no la hace.
            // Escribir ahi no producia absolutamente nada. La cabecera tiene alto fijo
            // (NavigationViewTopPaneHeight), asi que colapsarlo no mueve nada de sitio.
            // La busqueda de la pestana de chats SI funciona (MainViewModel.SearchChats esta vivo),
            // y por eso se colapsa por pestana y no del todo.
            SearchField.Visibility = _prevIndex == INDEX_SETTINGS
                ? Visibility.Collapsed
                : Visibility.Visible;

            // u-aire-arriba: collapsing what's INSIDE Header above does not reclaim its row --
            // Header itself carries the fixed Height (NavigationViewTopPaneHeight) the comment
            // above already flagged, so it stayed on screen as an empty band over the Settings
            // profile header. Collapse Header itself for Settings; mirror OnStateChanged's own
            // Minimal/Expanded rule for the restore-to-Chats path so this does not fight a
            // Compact-width resize.
            Header.Visibility = _prevIndex == INDEX_SETTINGS
                ? Visibility.Collapsed
                : (MasterDetail.CurrentState == MasterDetailState.Minimal || MasterDetail.CurrentState == MasterDetailState.Expanded
                    ? Visibility.Visible
                    : Visibility.Collapsed);
#endif
        }

        #region Search

        private bool _searchCollapsed = true;

        private void ShowHideSearch(bool show)
        {
            if (_searchCollapsed != show)
            {
                return;
            }

            _searchCollapsed = !show;

            FindName(nameof(DialogsSearchPanel));
            DialogsPanel.Visibility = Visibility.Visible;
            DialogsSearchPanel.Visibility = Visibility.Visible;

            if (show)
            {
                DialogsSearchPanel.Activate();
                SearchField.ControlledList = DialogsSearchPanel.Root;
                Stories.Collapse();
            }

            var chats = ElementComposition.GetElementVisual(DialogsPanel);
            var panel = ElementComposition.GetElementVisual(DialogsSearchPanel);

            chats.CenterPoint = panel.CenterPoint = new Vector3(DialogsPanel.ActualSize / 2, 0);

            var batch = panel.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            void completed()
            {
                DialogsPanel.Visibility = _searchCollapsed ? Visibility.Visible : Visibility.Collapsed;
                DialogsSearchPanel.Visibility = _searchCollapsed ? Visibility.Collapsed : Visibility.Visible;

                if (_searchCollapsed)
                {
                    DialogsSearchPanel.Deactivate();
                }
            }

#if !LINUX
            batch.Completed += (s, args) => completed();
#endif

            var scale1 = panel.Compositor.CreateVector3KeyFrameAnimation();
            scale1.InsertKeyFrame(show ? 0 : 1, new Vector3(1.05f, 1.05f, 1));
            scale1.InsertKeyFrame(show ? 1 : 0, new Vector3(1));
            scale1.Duration = TimeSpan.FromMilliseconds(200);

            var scale2 = panel.Compositor.CreateVector3KeyFrameAnimation();
            scale2.InsertKeyFrame(show ? 0 : 1, new Vector3(1));
            scale2.InsertKeyFrame(show ? 1 : 0, new Vector3(0.95f, 0.95f, 1));
            scale2.Duration = TimeSpan.FromMilliseconds(200);

            var opacity1 = panel.Compositor.CreateScalarKeyFrameAnimation();
            opacity1.InsertKeyFrame(show ? 0 : 1, 0);
            opacity1.InsertKeyFrame(show ? 1 : 0, 1);
            opacity1.Duration = TimeSpan.FromMilliseconds(200);

            var opacity2 = panel.Compositor.CreateScalarKeyFrameAnimation();
            opacity2.InsertKeyFrame(show ? 0 : 1, 1);
            opacity2.InsertKeyFrame(show ? 1 : 0, 0);
            opacity2.Duration = TimeSpan.FromMilliseconds(200);

            panel.StartAnimation("Scale", scale1);
            panel.StartAnimation("Opacity", opacity1);

            chats.StartAnimation("Scale", scale2);
            chats.StartAnimation("Opacity", opacity2);

            if (!_topicListCollapsed)
            {
                var header = ElementComposition.GetElementVisual(Header);
#if LINUX
                // NOT opacity1: Compositor.RegisterAnimation keys its dictionary BY THE ANIMATION
                // INSTANCE in Uno, so starting the same one on a second visual is
                // "ArgumentException: An item with the same key has already been added"
                // (PORTING.md 6). Legal in WinUI, which is why upstream shares it.
                var opacity3 = panel.Compositor.CreateScalarKeyFrameAnimation();
                opacity3.InsertKeyFrame(show ? 0 : 1, 0);
                opacity3.InsertKeyFrame(show ? 1 : 0, 1);
                opacity3.Duration = TimeSpan.FromMilliseconds(200);

                header.StartAnimation("Opacity", opacity3);
#else
                header.StartAnimation("Opacity", opacity1);
#endif
            }

#if LINUX
            // THE reason the search results rendered and took no clicks, measured 2026-08-26.
            // Completed never fires in Uno (PORTING.md 6), and it is the ONLY place that sets
            // Visibility: the chat list is faded out by a composition Opacity animation, but
            // Opacity does not enter Uno's hit testing (UIElement.CoerceHitTestVisibility looks at
            // IsLoaded, IsHitTestVisible, Visibility, IsEnabledOverride and IsViewHit, and nothing
            // else), so DialogsPanel stayed Visible, invisible, and ON TOP of the search results,
            // swallowing every press. Nothing was wrong with the rows or with the context menu
            // hook: the pointer never reached them - measured with a PointerPressed probe on the
            // result container that never fired once.
            //
            // The mirror image is just as bad: leaving search leaves DialogsSearchPanel Visible on
            // top of the chat list, and Deactivate() never runs.
            batch.EndWithCompleted(TimeSpan.FromMilliseconds(200), completed);
#else
            batch.End();
#endif
        }

        public void Search()
        {
            SearchField.Focus(FocusState.Keyboard);
            Search_Click(null, null);
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            if (SearchField.FocusState == FocusState.Keyboard && sender == SearchField)
            {
                return;
            }

            Search_TextChanged(null, null);
        }

        private void Search_LostFocus(object sender, RoutedEventArgs e)
        {
            MasterDetail.AllowCompact = MasterDetail.NavigationService?.CurrentPageType != typeof(BlankPage)
                && _prevIndex == INDEX_CHATS;

            SearchReset();

            UpdatePaneToggleButtonVisibility();
        }

        private const int INDEX_CHATS = 0;
        private const int INDEX_SETTINGS = 1;

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchField.FocusState == FocusState.Unfocused && string.IsNullOrWhiteSpace(SearchField.Text))
            {
                return;
            }

            _shouldGoBackWithDetail = false;

            MasterDetail.AllowCompact = false;

            if (_prevIndex == INDEX_CHATS)
            {
                ShowHideSearch(true);
                ViewModel.SearchChats.Query = SearchField.Text;
            }
#if !LINUX
            // Settings search STAYS out, and not for a mechanical reason: on Linux
            // ISettingsSearchService is Telegram.Linux/Hubs/SettingsSearchService.cs, whose Search
            // returns Array.Empty because no settings page is indexed, and the ListView bound to
            // ViewModel.Settings.Results (MainPage.xaml:1008) carries the win: prefix, so it is not
            // even compiled here. Turning this branch on would be a search box that answers nothing.
            else if (_prevIndex == INDEX_SETTINGS)
            {
                if (string.IsNullOrWhiteSpace(SearchField.Text))
                {
                    SearchReset();
                }
                else
                {
                    SettingsView?.Visibility = Visibility.Collapsed;

                    ViewModel.Settings.Search(SearchField.Text);
                }
            }
#endif

            UpdatePaneToggleButtonVisibility();
        }

        private void SearchReset()
        {
            //DialogsPanel.Visibility = Visibility.Visible;
            ShowHideSearch(false);

            if (_prevIndex == INDEX_CHATS && SearchField.FocusState != FocusState.Unfocused)
            {
                Photo.Focus(FocusState.Programmatic);
            }

            SearchField.Text = string.Empty;

#if !LINUX
            SettingsView?.Visibility = Visibility.Visible;

            ViewModel.Settings.Results.Clear();
#endif
        }

        #endregion

        private void Lock_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Passcode.Lock(false);
        }

        private void Settings_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
#if !LINUX
            if (args.InRecycleQueue)
            {
                return;
            }

            static string GetPath(SettingsSearchEntry item)
            {
                if (item.Parent != null)
                {
                    return GetPath(item.Parent) + " > " + item.Text;
                }

                return item.Text;
            }

            var entry = args.Item as SettingsSearchEntry;
            var button = args.ItemContainer.ContentRoot() as SettingsButton;
            button.Command = ViewModel.Settings.NavigateCommand;
            button.CommandParameter = entry;

            button.Content = entry.Text;
            button.IconSource = entry.Icon;

            var icon = button.GetChild<AnimatedIcon>();
            icon?.InvalidateMeasure();

            if (entry.Parent == null)
            {
                button.Badge = null;
                button.BadgeVisibility = Visibility.Collapsed;
            }
            else
            {
                button.Badge = GetPath(entry.Parent);
                button.BadgeVisibility = Visibility.Visible;
            }
#endif

            args.Handled = true;
        }

        private void SetPivotSelectedIndex(int index, bool updateBackStack = false)
        {
            if (_prevIndex != index || updateBackStack)
            {
                LoadAtIndex(index);

                if (MasterDetail.CurrentState == MasterDetailState.Minimal &&
                    MasterDetail.NavigationService.CurrentPageType != typeof(BlankPage))
                {
                    MasterDetail.NavigationService.GoBackAt(0);
                }
            }
        }

        public void NavigationView_ItemClick(RootDestination destination)
        {
            if (destination == RootDestination.Chats)
            {
                SetPivotSelectedIndex(INDEX_CHATS);
            }
            else if (destination == RootDestination.Contacts)
            {
                ViewModel.NavigationService.ShowPopup(new ContactsPopup());
            }
#if !LINUX
            else if (destination == RootDestination.Calls)
            {
                ViewModel.NavigationService.ShowPopup(new CallsPopup());
            }
#endif
            else if (destination == RootDestination.Settings)
            {
                SetPivotSelectedIndex(INDEX_SETTINGS);
            }
            else if (destination == RootDestination.ArchivedChats)
            {
                ArchivedChats_Click(null, null);
            }
            else if (destination == RootDestination.Status)
            {
                Status_Click(null, null);
            }
            else if (destination == RootDestination.NewGroup)
            {
                ViewModel.NavigationService.ShowPopup(new NewGroupPopup());
            }
            else if (destination == RootDestination.NewChannel)
            {
                ViewModel.NavigationService.ShowPopup(new NewChannelPopup());
            }
            else if (destination == RootDestination.MyProfile)
            {
                ViewModel.NavigateToMyProfile(false);
            }
            else if (destination == RootDestination.SavedMessages)
            {
                ViewModel.NavigateToMyProfile(true);
            }
            else if (destination == RootDestination.Tips && Uri.TryCreate(Strings.TelegramFeaturesUrl, UriKind.Absolute, out Uri tipsUri))
            {
                MessageHelper.OpenTelegramUrl(ViewModel.ClientService, MasterDetail.NavigationService, tipsUri);
            }
            else if (destination == RootDestination.News)
            {
                MessageHelper.NavigateToUsername(ViewModel.ClientService, MasterDetail.NavigationService, "unigram");
            }
#if LINUX
            else
            {
                // The Linux RootPage builds its menu from the same RootDestination enum as the
                // Windows one, so a destination whose branch is still under #if !LINUX reaches
                // here and the click does nothing at all. Silence is what made "create a group"
                // look like a dead button rather than a missing screen: say so.
                Logger.Warning($"No handler for RootDestination.{destination} in the Linux subset");
            }
#endif
        }

        private void Arrow_Click(object sender, RoutedEventArgs e)
        {
            var scrollViewer = ChatsList.GetScrollViewer();
            scrollViewer?.TryChangeView(null, 0, null);
        }

        private void Proxy_Click(object sender, RoutedEventArgs e)
        {
            // Unguarded in the u-039 consolidation, once u-031 put SettingsProxyPage in the subset.
            // This was not cosmetic: the button IS drawn (UpdateConnectionState sets
            // Proxy.Visibility = Visible at :648 and :657 whenever a proxy is configured or the
            // connection is going through one), so until now it was a dead button in the one
            // situation where a user most needs it -- a network where Telegram is blocked.
            // Fully qualified on purpose: `using Telegram.Views.Settings;` is still inside a
            // `#if !LINUX` at the top of this file, because most of that namespace is out of the
            // subset. Qualifying the one type that IS in beats unguarding the whole namespace and
            // risking an ambiguity somewhere else in these 4.500 lines.
            MasterDetail.NavigationService.Navigate(typeof(Telegram.Views.Settings.SettingsProxyPage));
        }

        public void UpdateChatListArchive()
        {
            this.BeginOnUIThread(() => ArchivedChats.UpdateChatList(ViewModel.ClientService, new ChatListArchive()));
        }

        public void UpdateChatFoldersLayout()
        {
            void handler(object sender, object e)
            {
                ChatsList.LayoutUpdated -= handler;
                ChatsList.ItemContainerTransitions.Clear();
            }

            ChatsList.ItemContainerTransitions.Clear();
            ChatsList.ItemContainerTransitions.Add(new RepositionThemeTransition());

            ChatsList.LayoutUpdated += handler;
            ChatsList.UpdateVisibleChats();

            ConvertFolder(ViewModel.SelectedFolder);
        }

        public void UpdateChatFolders()
        {
            ConvertFolder(ViewModel.SelectedFolder);
        }

        private ChatFolderViewModel ConvertFolder(ChatFolderViewModel folder, bool updateBackStack = true)
        {
            ShowHideArchive(folder?.ChatList is ChatListMain or null && ViewModel.Chats.Items.ChatList is not ChatListArchive, false);
            ShowHideLeftTabs(AppSettings.UseLeftTabsForChats && ViewModel.Folders.Count > 0);
            ShowHideTopTabs(!AppSettings.UseLeftTabsForChats && ViewModel.Folders.Count > 0 && folder.ChatList is not ChatListArchive);

            UpdatePaneToggleButtonVisibility();

            if (MasterDetail.CurrentState != MasterDetailState.Minimal && updateBackStack)
            {
                SetPivotSelectedIndex(INDEX_CHATS);
            }

            ChatsList.CanGoNext = ViewModel.Folders.Count > 0 && ViewModel.Folders[^1] != folder;
            ChatsList.CanGoPrev = ViewModel.Folders.Count > 0 && ViewModel.Folders[0] != folder;

            return folder;
        }

        private void ChatFolders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListViewBase listView && listView.SelectedItem is ChatFolderViewModel folder)
            {
                UpdateFolder(folder, updateBackStack: false);

                SetPivotSelectedIndex(INDEX_CHATS, true);
                HideTopicList();
            }
        }

        public void ArchivedChats_Click(object sender, RoutedEventArgs e)
        {
            UpdateFolder(ChatFolderViewModel.Archive);
        }

        private void ChatFolder_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            var element = sender as FrameworkElement;
            var folder = ChatFolders?.ItemFromContainer(sender) as ChatFolderViewModel;

            folder ??= ChatFoldersSide?.ItemFromContainer(sender) as ChatFolderViewModel;

            if (folder == null || folder.IsNavigationItem)
            {
                return;
            }

            var flyout = new MenuFlyout();

            if (folder.ChatFolderId == Constants.ChatListMain)
            {
                flyout.CreateFlyoutItem(ViewModel.EditFolder, folder, Strings.FilterEditAll, Icons.Edit);
                flyout.CreateFlyoutItem(ViewModel.MarkFolderAsRead, folder, Strings.MarkAllAsRead, Icons.MarkAsRead);
            }
            else
            {
                flyout.CreateFlyoutItem(ViewModel.EditFolder, folder, Strings.FilterEdit, Icons.Edit);
                flyout.CreateFlyoutItem(ViewModel.MarkFolderAsRead, folder, Strings.MarkAllAsRead, Icons.MarkAsRead);
                flyout.CreateFlyoutItem(ViewModel.AddToFolder, folder, Strings.FilterAddChats, Icons.Add);
                flyout.CreateFlyoutSeparator();
                flyout.CreateFlyoutItem(ViewModel.DeleteFolder, folder, Strings.Remove, Icons.Delete, destructive: true);
            }

            flyout.ShowAt(element, FlyoutPlacementMode.BottomEdgeAlignedLeft);
        }

        private void ArchivedChats_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            var flyout = new MenuFlyout();

            //if (((TLViewModelBase)ViewModel).Settings.CollapseArchivedChats)
            //{
            //    flyout.CreateFlyoutItem(new RelayCommand(ToggleArchive), Strings.AccDescrExpandPanel, Icons.Expand);
            //}
            //else
            //{
            //    flyout.CreateFlyoutItem(new RelayCommand(ToggleArchive), Strings.AccDescrCollapsePanel, Icons.Collapse);
            //}

            // CERRADO 2026-08-27 (PARIDAD M18). El MenuFlyout se creaba y se descartaba sin
            // mostrarse: el boton derecho sobre «Archivados» no sacaba nada. Las dos entradas
            // tienen destino vivo -- ToggleArchive es el metodo de esta misma clase (justo debajo,
            // sin guarda) y MainViewModel.MarkFolderAsRead tampoco la tiene --, asi que solo
            // faltaba mostrarlo.
            // Ojo con lo que dice PARIDAD M18: ese menu NO sale hoy desde el panel lateral. El
            // RootPage.OnContextRequested que lo monta sin #if es el de Windows
            // (Telegram/Views/Host/RootPage.xaml.cs), y ese fichero NO esta en el csproj: la cabeza
            // Linux compila su propio Telegram.Linux/Hubs/RootPage.xaml(.cs), que no lleva menu
            // contextual. O sea que la incoherencia era peor de lo apuntado: el archivo no tenia
            // menu por ningun lado. Con esto lo tiene por el sitio donde Windows lo pone.
            flyout.CreateFlyoutItem(ToggleArchive, Strings.ArchiveMoveToMainMenu, Icons.SubtractCircle);
            flyout.CreateFlyoutItem(ViewModel.MarkFolderAsRead, ChatFolderViewModel.Archive, Strings.MarkAllAsRead, Icons.MarkAsRead);

            flyout.ShowAt(sender, args);
        }

        public async void ToggleArchive()
        {
            ViewModel.ToggleArchive();

            ArchivedChatsPanel.Visibility = Visibility.Visible;
            //ArchivedChatsCompactPanel.Visibility = Visibility.Visible;

            await ArchivedChatsPanel.UpdateLayoutAsync();

            void ToggleActiveCompleted()
            {
                ArchivedChatsPanel.Visibility = ((ViewModelBase)ViewModel).Settings.HideArchivedChats
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                //ArchivedChatsCompactPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                ChatsList.Margin = new Thickness(0, ChatListTopPadding(), 0, 0);

                Root.UpdateSessions();

                if (((ViewModelBase)ViewModel).Settings.HideArchivedChats)
                {
                    ToastPopup.Show(Photo, Strings.ArchiveMoveToMainMenuInfo, TeachingTipPlacementMode.BottomRight);
                }
            }

            var show = !((ViewModelBase)ViewModel).Settings.HideArchivedChats;

            var element = VisualTreeHelper.GetChild(ChatsList, 0) as UIElement;
            if (element == null)
            {
                ToggleActiveCompleted();
            }

            var presenter = ElementComposition.GetElementVisual(ArchivedChatsPresenter);
            var parent = ElementComposition.GetElementVisual(ChatsList);

            var chats = ElementComposition.GetElementVisual(element);
            var panel = ElementComposition.GetElementVisual(ArchivedChatsPanel);
            //var compact = ElementComposition.GetElementVisual(ArchivedChatsCompactPanel);

            presenter.Clip = chats.Compositor.CreateInsetClip();
            parent.Clip = chats.Compositor.CreateInsetClip();

            var batch = chats.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                chats.Offset = new Vector3();
                panel.Offset = new Vector3();
                //compact.Offset = new Vector3();

                ToggleActiveCompleted();
            };

            var panelY = ArchivedChatsPanel.ActualSize.Y;
            var compactY = 0; //(float)ArchivedChatsCompactPanel.ActualHeight;

            ChatsList.Margin = new Thickness(0, ChatListTopPadding(), 0, -(panelY - compactY));

            float y0, y1;

            if (show)
            {
                y0 = -(panelY - compactY);
                y1 = 0;
            }
            else
            {
                y0 = 0;
                y1 = -(panelY - compactY);
            }

            var offset0 = chats.Compositor.CreateVector3KeyFrameAnimation();
            offset0.InsertKeyFrame(0, new Vector3(0, y0, 0));
            offset0.InsertKeyFrame(1, new Vector3(0, y1, 0));
            chats.StartAnimation("Offset", offset0);

            //var offset1 = chats.Compositor.CreateVector3KeyFrameAnimation();
            //offset1.InsertKeyFrame(0, new Vector3(0, show ? 0 : compactY, 0));
            //offset1.InsertKeyFrame(1, new Vector3(0, show ? compactY : 0, 0));
            //compact.StartAnimation("Offset", offset1);

            var offset2 = chats.Compositor.CreateVector3KeyFrameAnimation();
            offset2.InsertKeyFrame(0, new Vector3(0, show ? -compactY : 0, 0));
            offset2.InsertKeyFrame(1, new Vector3(0, show ? 0 : -compactY, 0));
            panel.StartAnimation("Offset", offset2);

            batch.End();
        }

        private bool _archiveCollapsed;

        private async void ShowHideArchive(bool show, bool animate)
        {
            if (_archiveCollapsed != show)
            {
                return;
            }

            _archiveCollapsed = !show;
            ArchivedChatsPresenter.Visibility = Visibility.Visible;

            void ShowHideArchiveCompleted()
            {
                ChatsList.Margin = new Thickness(0, ChatListTopPadding(), 0, 0);
                ArchivedChatsPresenter.Visibility = _archiveCollapsed
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            var element = VisualTreeHelper.GetChild(ChatsList, 0) as UIElement;
            if (element == null || !animate || ((ViewModelBase)ViewModel).Settings.HideArchivedChats)
            {
                ShowHideArchiveCompleted();
                return;
            }

            if (ArchivedChatsPanel.ActualWidth == 0)
            {
                await ArchivedChatsPanel.UpdateLayoutAsync();
            }

            var parent = ElementComposition.GetElementVisual(ChatsList);
            var chats = ElementComposition.GetElementVisual(element);

            parent.Clip = chats.Compositor.CreateInsetClip();
            chats.StopAnimation("Offset");

            var batch = chats.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                chats.Offset = new Vector3();
                ShowHideArchiveCompleted();
            };

            var y = ArchivedChatsPresenter.ActualSize.Y;

            ChatsList.Margin = new Thickness(0, ChatListTopPadding(), 0, -y);

            var offset0 = chats.Compositor.CreateVector3KeyFrameAnimation();
            offset0.InsertKeyFrame(0, new Vector3(0, show ? -y : 0, 0));
            offset0.InsertKeyFrame(1, new Vector3(0, show ? 0 : -y, 0));
            chats.StartAnimation("Offset", offset0);

            batch.End();
        }

        private bool _shouldGoBackWithDetail = true;

        public void BackRequested()
        {
            if (_shouldGoBackWithDetail && MasterDetail.NavigationService.CanGoBack)
            {
                ViewModel.Window.RaiseBackRequested();
            }
            else
            {
                _shouldGoBackWithDetail = true;
                OnBackRequested(new BackRequestedRoutedEventArgs());
            }
        }

        private void UpdateFolder(ChatFolderViewModel folder, bool update = true, bool updateBackStack = true)
        {
            CarouselDirection direction = CarouselDirection.None;
            if (folder.ChatList is ChatListArchive)
            {
                direction = CarouselDirection.Next;
            }
            else if (ViewModel.Chats.Items.ChatList is ChatListArchive)
            {
                direction = CarouselDirection.Previous;
            }
            else if (_prevIndex == INDEX_CHATS)
            {
                var nextIndex = ViewModel.Folders.IndexOf(folder);
                var prevIndex = ViewModel.Folders.IndexOf(ViewModel.SelectedFolder);

                if (nextIndex == prevIndex)
                {
                    return;
                }

                direction = nextIndex <= prevIndex
                    ? CarouselDirection.Previous
                    : CarouselDirection.Next;
            }

            ChatsList.ChangeView(direction, () =>
            {
                ViewModel.SelectedFolder = folder;

                if (update)
                {
                    ConvertFolder(folder, updateBackStack);

                    Logger.Info("ChangeView");

                    var scrollingHost = ChatsList.GetScrollViewer();
                    scrollingHost?.TryChangeView(null, 0, null, true);
                }
            });

            if (folder.ChatList is ChatListArchive)
            {
                _shouldGoBackWithDetail = false;
            }

            Search_LostFocus(null, null);
        }

        #region Selection

        private void List_SelectionModeChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (ViewModel.Chats.SelectionMode == ListViewSelectionMode.Multiple)
            {
                ShowHideManagePanel(true);
            }
            else
            {
                ShowHideManagePanel(false);
            }

            UpdatePaneToggleButtonVisibility();
        }

        private bool _manageCollapsed = true;

        private void ShowHideManagePanel(bool show)
        {
            if (_manageCollapsed != show)
            {
                return;
            }

            if (show)
            {
                HideTopicList();
            }

            _manageCollapsed = !show;
            ManagePanel.Visibility = Visibility.Visible;

            var manage = ElementComposition.GetElementVisual(ManagePanel);
            //manage.Offset = new Vector3(show ? -20 : 12, 8, 0);
            manage.Opacity = show ? 0 : 1;

            var batch = manage.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                //manage.Offset = new Vector3(show ? 12 : -20, 8, 0);
                manage.Opacity = show ? 1 : 0;

                if (show)
                {
                    ManagePanel.Visibility = Visibility.Visible;
                }
                else
                {
                    ManagePanel.Visibility = Visibility.Collapsed;
                    ViewModel.Chats.SelectedItems.Clear();
                }
            };

            var offset1 = manage.Compositor.CreateVector3KeyFrameAnimation();
            offset1.InsertKeyFrame(show ? 0 : 1, new Vector3(0, 48, 0));
            offset1.InsertKeyFrame(show ? 1 : 0, new Vector3(0, 0, 0));

            var opacity1 = manage.Compositor.CreateScalarKeyFrameAnimation();
            opacity1.InsertKeyFrame(show ? 0 : 1, 0);
            opacity1.InsertKeyFrame(show ? 1 : 0, 1);

            manage.StartAnimation("Translation", offset1);
            manage.StartAnimation("Opacity", opacity1);

            batch.End();

            if (show)
            {
                ManagePanel.Visibility = Visibility.Visible;
            }
            else
            {
                MainHeader.Visibility = Visibility.Visible;
            }
        }

        private void Manage_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Chats.SelectionMode != ListViewSelectionMode.Multiple)
            {
                ViewModel.Chats.SelectionMode = ListViewSelectionMode.Multiple;
            }
            else
            {
                ViewModel.Chats.SelectionMode = MasterDetail.CurrentState == MasterDetailState.Minimal
                    ? ListViewSelectionMode.None
                    : ListViewSelectionMode.Single;
            }
        }

        public void SetSelectionMode(bool enabled)
        {
            if (enabled)
            {
                ViewModel.Chats.SelectionMode = ListViewSelectionMode.Multiple;
            }
            else
            {
                ViewModel.Chats.SelectionMode = MasterDetail.CurrentState == MasterDetailState.Minimal
                    ? ListViewSelectionMode.None
                    : ListViewSelectionMode.Single;
            }
        }

        public async void SetSelectedItem(Chat chat)
        {
            await System.Threading.Tasks.Task.Delay(100);

            if (ViewModel.Chats.SelectionMode != ListViewSelectionMode.Multiple)
            {
                try
                {
                    ChatsList.SelectedItem = chat;

                    // TODO: would be great, but doesn't seem to work well enough :(
                    //VisualUtilities.QueueCallbackForCompositionRendered(() => ChatsList.SelectedItem = chat);
                }
                catch
                {
                    // All the remote procedure calls must be wrapped in a try-catch block
                }
            }
        }

        public void SetSelectedItems(IList<Chat> chats)
        {
            if (ViewModel.Chats.SelectionMode == ListViewSelectionMode.Multiple)
            {
                try
                {
                    foreach (var item in chats)
                    {
                        if (!ChatsList.SelectedItems.Contains(item))
                        {
                            ChatsList.SelectedItems.Add(item);
                        }
                    }

                    foreach (Chat item in ChatsList.SelectedItems)
                    {
                        if (!chats.Contains(item))
                        {
                            ChatsList.SelectedItems.Remove(item);
                        }
                    }
                }
                catch
                {
                    // SelectedItems likes to throw
                }
            }
        }

        #endregion

        private void Confetti_Completed(object sender, EventArgs e)
        {
            this.BeginOnUIThread(() =>
            {
                UnloadObject(Confetti);
            });
        }

        public static string GetFolderIcon(ChatListFolderFlags folder)
        {
            if (folder == ChatListFolderFlags.ExcludeMuted)
            {
                return Icons.AlertFilled;
            }
            else if (folder == ChatListFolderFlags.ExcludeRead)
            {
                return Icons.ChatUnreadFilled; //FontFamily = App.Current.Resources["TelegramThemeFontFamily"] as FontFamily };
            }
            else if (folder == ChatListFolderFlags.ExcludeArchived)
            {
                return Icons.ArchiveFilled;
            }
            else if (folder == ChatListFolderFlags.IncludeContacts)
            {
                return Icons.PersonFilled;
            }
            else if (folder == ChatListFolderFlags.IncludeNonContacts)
            {
                return Icons.PersonQuestionMarkFilled;
            }
            else if (folder == ChatListFolderFlags.IncludeGroups)
            {
                return Icons.PeopleFilled;
            }
            else if (folder == ChatListFolderFlags.IncludeChannels)
            {
                return Icons.MegaphoneFilled;
            }
            else if (folder == ChatListFolderFlags.IncludeBots)
            {
                return Icons.BotFilled;
            }
            else if (folder == ChatListFolderFlags.ExistingChats)
            {
                return Icons.ChatMultipleFilled;
            }
            else if (folder == ChatListFolderFlags.NewChats)
            {
                return Icons.ChatUnreadFilled;
            }

            return null;
        }

        private void ChatFolders_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            var list = sender as TopNavView;
            if (list == null)
            {
                return;
            }

            if (e.Items.Count > 1)
            {
                list.CanReorderItems = false;
                e.Cancel = true;
            }
            else
            {
                var items = ViewModel?.Folders;
                if (items == null || items.Count < 2)
                {
                    list.CanReorderItems = false;
                    e.Cancel = true;
                }
                else
                {
                    list.CanReorderItems = true;
                }
            }
        }

        private void ChatFolders_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            sender.CanReorderItems = false;

            if (args.DropResult == DataPackageOperation.Move && args.Items.Count == 1 && args.Items[0] is ChatFolderViewModel folder)
            {
                var items = ViewModel?.Folders;
                var index = items.IndexOf(folder);

                var compare = items[index > 0 ? index - 1 : index + 1];
                if (compare.ChatList is ChatListMain && index > 0 && index < items.Count - 1 && !_clientService.IsPremium)
                {
                    compare = items[index + 1];
                }

                if ((compare.ChatList is ChatListMain || folder.ChatList is ChatListMain) && !_clientService.IsPremium)
                {
                    ViewModel.Handle(new UpdateChatFolders(ViewModel.ClientService.ChatFolders, 0, false));

                    ToastPopup.ShowPromo(ViewModel.NavigationService, string.Format(Strings.LimitReachedReorderFolder, Strings.FilterAllChats), Strings.PremiumMore, new PremiumSourceLimitExceeded(new PremiumLimitTypeChatFolderCount()));
                }
                else
                {
                    var folders = items.Where(x => x.ChatList is ChatListFolder).Select(x => x.ChatFolderId).ToArray();
                    var main = _clientService.IsPremium ? items.IndexOf(items.FirstOrDefault(x => x.ChatList is ChatListMain)) : 0;

                    ViewModel.ClientService.Send(new ReorderChatFolders(folders, main));
                }
            }
        }

        private void ArchivedChats_ActualThemeChanged(FrameworkElement sender, object args)
        {
            ArchivedChats.UpdateChatList(ViewModel.ClientService, new ChatListArchive());
            ArchivedChats.UpdateStoryList(ViewModel.ClientService, new StoryListArchive());
        }

        private async void Downloads_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.NavigationService.ShowPopupAsync(new DownloadsPopup());
        }

        private void Photo_Click(object sender, RoutedEventArgs e)
        {
            if (MasterDetail.CurrentState == MasterDetailState.Minimal && MasterDetail.NavigationService.CanGoBack)
            {
                Root.IsPaneOpen = true;
            }
            else if (!_searchCollapsed)
            {
                Search_LostFocus(null, null);
            }
            else if (_prevIndex != INDEX_CHATS)
            {
                SetPivotSelectedIndex(INDEX_CHATS);
                ViewModel.RaisePropertyChanged(nameof(ViewModel.SelectedFolder));
            }
            else if (ViewModel.Chats.Items.ChatList is ChatListArchive)
            {
                UpdateFolder(ViewModel.Folders.Count > 0 ? ViewModel.Folders[0] : ChatFolderViewModel.Main);
            }
            else
            {
                Root.IsPaneOpen = true;
            }
        }

        private void ChatsList_GettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            {
                if (Photo == sender)
                {
                    Photo.UseSystemFocusVisuals = args.Direction != FocusNavigationDirection.None;
                }

                return;
            }

            try
            {
                // ListViewBase ignores GettingFocus events with Direction equals to None
                // What we do here is to simulate the default behavior, so that closing the active chat
                // will move the focus to the last selected item in the chat list if possible.
                if (args.Direction == FocusNavigationDirection.None && args.OldFocusedElement is not ChatListListViewItem)
                {
                    if (!_topicListCollapsed && ViewModel?.Topics.LastSelectedItem is MessageTopicForum forum && TopicListPresenter.TryGetContainer(forum.ForumTopicId, out SelectorItem container))
                    {
                        Logger.Info("Set topic item as focused element");

                        if (args.TrySetNewFocusedElement(container))
                        {
                            args.Handled = true;
                        }
                    }
                    else if (ChatsList.TryGetContainer(ViewModel?.Chats.LastSelectedItem ?? 0, out container))
                    {
                        Logger.Info("Set chat item as focused element");

                        if (args.TrySetNewFocusedElement(container))
                        {
                            args.Handled = true;
                        }
                    }
                    else if (sender != ChatsList)
                    {
                        Logger.Info("Set chat list as focused element");

                        if (args.TrySetNewFocusedElement(ChatsList))
                        {
                            args.Handled = true;
                        }
                    }

                    if (args.NewFocusedElement is ChatListListViewItem item)
                    {
                        // Let's disable the awkward focus rect that would appear on activation.
                        // ChatListListViewItem.OnLostFocus takes care of reenabling it.
                        item.UseSystemFocusVisuals = false;
                    }
                }
            }
            catch
            {
                // All the remote procedure calls must be wrapped in a try-catch block
            }
        }

        private void ChatFolders_ChoosingGroupHeaderContainer(ListViewBase sender, ChoosingGroupHeaderContainerEventArgs args)
        {
            args.GroupHeaderContainer = new ListViewHeaderItem
            {
                Visibility = args.GroupIndex == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible
            };
        }

        private void ChatsList_ChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
#if LINUX
            // Never called: Uno Skia does not raise ChoosingItemContainer (the XAML subscription
            // itself is only a TryRaiseNotImplemented). The two things this handler did on Windows
            // are covered on Linux by the managed path instead: ChatListListView
            // .GetContainerForItemOverride already returns the ChatListListViewItem AND attaches
            // ContextRequested (raised back here as ItemContextRequested), and ItemTemplate is
            // applied by the generator. Kept only so the XAML markup binds on both platforms.
#else
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new ChatListListViewItem(ChatsList);
                args.ItemContainer.ContentTemplate = ChatsList.ItemTemplate;
                args.ItemContainer.ContextRequested += Chat_ContextRequested;
            }

            args.IsContainerPrepared = true;
#endif
        }

        public void PopupOpened()
        {
            NavigationService.Window.SetTitleBar(null);

            if (NavigationService.Frame.Content is IActivablePage page)
            {
                page.PopupOpened();
            }
        }

        public void PopupClosed()
        {
            NavigationService.Window.SetTitleBar(TitleBarHandle);

            if (NavigationService.Frame.Content is IActivablePage page)
            {
                page.PopupClosed();
            }
        }

        #region Context menu

        private void DialogsSearchPanel_ItemContextRequested(UIElement sender, ItemContextRequestedEventArgs args)
        {
            if (args.Item is SearchResult result && result.Chat != null)
            {
                var element = sender as FrameworkElement;
                var chat = result.Chat;

                Chat_ContextRequested(chat, element, args.EventArgs, false);
            }
        }

        private void Chat_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var element = sender as FrameworkElement;
            var chat = ChatsList.ItemFromContainer(element) as Chat;

            Chat_ContextRequested(chat, sender, args, true);
        }

        private async void Chat_ContextRequested(Chat chat, UIElement sender, ContextRequestedEventArgs args, bool allowSelection)
        {
            var viewModel = ViewModel.Chats;
            if (viewModel == null)
            {
                return;
            }

            var flyout = new MenuFlyout();
            var element = sender as FrameworkElement;

            var position = chat?.GetPosition(viewModel.Items.ChatList);
            if (position == null)
            {
                return;
            }

            var muted = ViewModel.ClientService.Notifications.IsMuted(chat);
            var archived = chat.Positions.Any(x => x.List is ChatListArchive);

            if (DialogArchive_Loaded(chat))
            {
                // Suggest to unarchive only when archive is open
                if (viewModel.Items.ChatList is ChatListArchive && archived)
                {
                    flyout.CreateFlyoutItem(DialogArchive_Loaded, viewModel.ArchiveChat, chat, Strings.Unarchive, Icons.Unarchive);
                }
                else if (viewModel.Items.ChatList is not ChatListArchive && !archived)
                {
                    flyout.CreateFlyoutItem(DialogArchive_Loaded, viewModel.ArchiveChat, chat, Strings.Archive, Icons.Archive);
                }
            }

            flyout.CreateFlyoutItem(DialogPin_Loaded, viewModel.PinChat, chat, position.IsPinned ? Strings.UnpinFromTop : Strings.PinToTop, position.IsPinned ? Icons.PinOff : Icons.Pin);

            var chatLists = await ViewModel.ClientService.SendAsync(new GetChatListsToAddChat(chat.Id)) as ChatLists;
            if (chatLists != null && chatLists.ChatListsValue.Count > 0)
            {
                var folders = ViewModel.ClientService.ChatFolders.ToDictionary(x => x.Id);

                var item = new MenuFlyoutSubItem();
                item.Text = Strings.FilterAddTo;
                item.Icon = MenuFlyoutHelper.CreateIcon(Icons.FolderAdd);

                foreach (var chatList in chatLists.ChatListsValue.OfType<ChatListFolder>())
                {
                    // Skip current folder from "Add to folder" list to avoid confusion
                    if (chatList.AreTheSame(viewModel.Items.ChatList))
                    {
                        continue;
                    }

                    if (folders.TryGetValue(chatList.ChatFolderId, out ChatFolderInfo folder))
                    {
                        var icon = Icons.ParseFolder(folder.Icon);
                        var glyph = Icons.FolderToGlyph(icon);

                        // TODO: Custom emojis
                        item.CreateFlyoutItem(viewModel.AddToFolder, (folder.Id, chat), folder.Name.Text.Text, glyph.Item1);
                    }
                }

                if (folders.Count < 10 && item.Items.Count > 0)
                {
                    item.CreateFlyoutSeparator();
                    item.CreateFlyoutItem(viewModel.CreateFolder, chat, Strings.CreateNewFilter, Icons.Add);
                }

                if (item.Items.Count > 0)
                {
                    flyout.Items.Add(item);
                }
            }

            if (viewModel.Items.ChatList is ChatListFolder chatListFolder)
            {
                var response = await ViewModel.ClientService.SendAsync(new GetChatFolder(chatListFolder.ChatFolderId)) as ChatFolder;
                if (response != null)
                {
                    if (response.Any(chat.Id))
                    {
                        flyout.CreateFlyoutItem(viewModel.RemoveFromFolder, (chatListFolder.ChatFolderId, chat), Strings.FilterRemoveFrom, Icons.FolderMove);
                    }
                }
            }

            if (DialogNotify_Loaded(chat))
            {
                var silent = ViewModel.ClientService.Notifications.IsSilent(chat);

                var mute = new MenuFlyoutSubItem();
                mute.Text = Strings.Mute;
                mute.Icon = MenuFlyoutHelper.CreateIcon(muted ? Icons.Alert : Icons.AlertOff);

                if (muted is false)
                {
                    mute.CreateFlyoutItem(ViewModel.Chats.SetChatSound, Tuple.Create<Chat, bool>(chat, !silent),
                        silent ? Strings.SoundOn : Strings.SoundOff,
                        silent ? Icons.MusicNote2 : Icons.MusicNoteOff2);
                }

                mute.CreateFlyoutItem(ViewModel.Chats.MuteChatFor, Tuple.Create<Chat, int?>(chat, 60 * 60), Strings.MuteFor1h, Icons.ClockAlarmHour);
                mute.CreateFlyoutItem(ViewModel.Chats.MuteChatFor, Tuple.Create<Chat, int?>(chat, null), Strings.MuteForPopup, Icons.AlertSnooze);

                var toggle = mute.CreateFlyoutItem(
                    ViewModel.Chats.NotifyChat,
                    chat,
                    muted ? Strings.UnmuteNotifications : Strings.MuteNotifications,
                    muted ? Icons.Speaker3 : Icons.SpeakerOff);

                if (muted is false)
                {
                    toggle.Foreground = BootStrapper.Current.Resources["DangerButtonBackground"] as Brush;
                }

                flyout.Items.Add(mute);

            }

            flyout.CreateFlyoutItem(DialogMark_Loaded, viewModel.MarkChatAsRead, chat, chat.IsUnread() ? Strings.MarkAsRead : Strings.MarkAsUnread, chat.IsUnread() ? Icons.MarkAsRead : Icons.MarkAsUnread);
            flyout.CreateFlyoutItem(DialogClear_Loaded, viewModel.ClearChat, chat, Strings.ClearHistory, Icons.Broom);
            flyout.CreateFlyoutItem(DialogDelete_Loaded, viewModel.DeleteChat, chat, DialogDelete_Text(chat), Icons.Delete, destructive: true);

            if (viewModel.SelectionMode != ListViewSelectionMode.Multiple)
            {
                if (ApiInfo.HasMultipleViews)
                {
                    flyout.CreateFlyoutSeparator();
                    flyout.CreateFlyoutItem(viewModel.OpenChat, chat, Strings.OpenInNewWindow, Icons.WindowNew);
                }

                if (allowSelection)
                {
                    flyout.CreateFlyoutSeparator();
                    flyout.CreateFlyoutItem(viewModel.SelectChat, chat, Strings.Select, Icons.CheckmarkCircle);
                }
            }

            flyout.ShowAt(sender, args);
        }

        private bool DialogMark_Loaded(Chat chat)
        {
            return true;
        }

        private bool DialogPin_Loaded(Chat chat)
        {
            //if (!chat.IsPinned)
            //{
            //    var count = ViewModel.Dialogs.LegacyItems.Where(x => x.IsPinned).Count();
            //    var max = ViewModel.ClientService.Config.PinnedDialogsCountMax;

            //    return count < max ? Visibility.Visible : Visibility.Collapsed;
            //}

            var position = chat.GetPosition(ViewModel.Chats.Items.ChatList);
            if (position?.Source != null)
            {
                return false;
            }

            return true;
        }

        private bool DialogArchive_Loaded(Chat chat)
        {
            var position = chat.GetPosition(ViewModel.Chats.Items.ChatList);
            if (ViewModel.ClientService.IsSavedMessages(chat) || position?.Source != null || chat.Id == 777000)
            {
                return false;
            }

            return true;
        }

        private bool DialogNotify_Loaded(Chat chat)
        {
            var position = chat.GetPosition(ViewModel.Chats.Items.ChatList);
            if (ViewModel.ClientService.IsSavedMessages(chat) || position?.Source is ChatSourcePublicServiceAnnouncement)
            {
                return false;
            }

            return true;
        }

        public bool DialogClear_Loaded(Chat chat)
        {
            var position = chat.GetPosition(ViewModel.Chats.Items.ChatList);
            if (position?.Source != null)
            {
                return false;
            }

            if (chat.Type is ChatTypeSupergroup super)
            {
                var supergroup = ViewModel.ClientService.GetSupergroup(super.SupergroupId);
                if (supergroup != null)
                {
                    return !supergroup.HasActiveUsername() && !super.IsChannel;
                }
            }

            return true;
        }

        private bool DialogDelete_Loaded(Chat chat)
        {
            var position = chat.GetPosition(ViewModel.Chats.Items.ChatList);
            if (position?.Source is ChatSourceMtprotoProxy)
            {
                return false;
            }

            //if (dialog.With is TLChannel channel)
            //{
            //    return Visibility.Visible;
            //}
            //else if (dialog.Peer is TLPeerUser userPeer)
            //{
            //    return Visibility.Visible;
            //}
            //else if (dialog.Peer is TLPeerChat chatPeer)
            //{
            //    return dialog.With is TLChatForbidden || dialog.With is TLChatEmpty ? Visibility.Visible : Visibility.Collapsed;
            //}

            //return Visibility.Collapsed;

            return true;
        }

        private string DialogDelete_Text(Chat chat)
        {
            var position = chat.GetPosition(ViewModel.Chats.Items.ChatList);
            if (position?.Source is ChatSourcePublicServiceAnnouncement)
            {
                return Strings.PsaHide;
            }
            else if (chat.Type is ChatTypeSupergroup super)
            {
                return super.IsChannel ? Strings.LeaveChannelMenu : Strings.LeaveMegaMenu;
            }
            else if (chat.Type is ChatTypeBasicGroup)
            {
                return Strings.DeleteAndExit;
            }

            return Strings.Delete;
        }

        #endregion

        // u-068: was `#if !LINUX` because EmojiMenuFlyout was outside the Linux subset. u-064
        // brought it in (Reacting for the first time), guarding out the Story/paid-reaction
        // branches this call site never reaches anyway (EmojiDrawerMode.EmojiStatus takes neither).
        private void Status_Click(object sender, RoutedEventArgs e)
        {
            // u-084 removed the u-083 temporary guard that made this click a no-op on Linux. The
            // two reasons it existed are both gone: the drawer no longer opens at opacity 0 (the
            // animation was being dropped for want of a CompositionTarget -- see
            // ChatStickerButton), and the per-frame exception storm was the flyout animating Size,
            // Offset and CornerRadius on a composition geometry, which EmojiMenuFlyout now writes
            // instead of animating on this head.
            if (ViewModel.IsPremium)
            {
                EmojiMenuFlyout.ShowAt(ViewModel.ClientService, EmojiDrawerMode.EmojiStatus, LogoEmoji, EmojiFlyoutAlignment.TopLeft);
            }
        }

        public void ShowTopicList(Chat chat)
        {
            ViewModel.Topics.SetChat(chat);
            ShowHideTopicList(true);
            TopicListPresenter?.UpdateChat(chat);

            var currentChat = MasterDetail.NavigationService.GetChatFromBackStack();
            if (currentChat.ChatId == chat.Id)
            {
                UpdateListViewsSelectedItem(currentChat);
            }
            else
            {
                UpdateListViewsSelectedItem(new ChatMessageTopic(chat.Id, null));
            }
        }

        public void HideTopicList(bool fromSelection = false)
        {
            var chatId = ViewModel.Topics.Chat?.Id;

            ShowHideTopicList(false);

            // MasterDetail.NavigationService is still null while the frame is navigating INTO this
            // page, and ChatFolders_SelectionChanged fires exactly then: the SelectedItem binding
            // of the folder strip applies during ShowHideTopTabs. Measured on 2026-08-27 (log of
            // the first run of that day, twice per start): the NullReferenceException came out of
            // GetChatFromBackStack, and because it was thrown inside a binding setter Uno swallowed
            // it as "Failed to apply binding to property SelectedItem" -- the folder tab's
            // selection binding was dropped and nobody saw an error. There is nothing to update at
            // that moment anyway: no chat is open yet.
            if (ViewModel.Chats.SelectedItem == chatId && MasterDetail.NavigationService != null)
            {
                UpdateListViewsSelectedItem(MasterDetail.NavigationService.GetChatFromBackStack(), fromSelection);
            }
        }

        private bool _topicListCollapsed = true;

        private void ShowHideTopicList(bool show)
        {
            if (_topicListCollapsed != show)
            {
                return;
            }

            FindName(nameof(TopicListPresenter));
            TopicListPresenter.DataContext = ViewModel.Topics;

#if LINUX
            // ForumView.xaml fills its list with a OneTime x:Bind (ItemsSource="{x:Bind
            // ViewModel.Items}"), and a OneTime x:Bind is evaluated exactly once, from the
            // control's Loading. That is the only thing that ever gives the list its items here,
            // because - unlike DialogsSearchPanel, the other x:Load presenter of this page -
            // TopicListPresenter has no DataContext in MainPage.xaml: it gets one from the line
            // above, after FindName has already built the control. ChatView drives the same
            // control through ForumView.ViewModel, the setter that assigns DataContext, ItemsSource
            // and SelectedItem in one go; doing the same here makes the topic list independent of
            // when the binding is evaluated, and costs nothing if it is: same collection instance,
            // reused for the lifetime of the view model (SetChat reloads it, never replaces it).
            if (show)
            {
                TopicListPresenter.ViewModel = ViewModel.Topics;
            }
#endif

            ViewModel.Topics.Delegate = TopicListPresenter;

            _topicListCollapsed = !show;
            TopicListPresenter.Visibility = Visibility.Visible;

            MasterDetail.CornerRadius = new CornerRadius(show ? 0 : 8, 0, 0, 0);
            Canvas.SetZIndex(ChatsRoot, show ? 1 : 0);

            if (show)
            {
                Stories.Collapse();
                TopicListPresenter.Focus(FocusState.Programmatic);
            }
            else
            {
                ViewModel.Topics.SetChat(null);
            }

            var margin = _tabsTopCollapsed ? 38 : 74;

            var padding = ChatTabs != null
                ? _tabsTopCollapsed ? -74 : -78
                : -14;

#if LINUX
            // The three constants above are upstream's answer to "how far up must the topic list
            // be pulled so that its top edge lands at window y=40", one per state of
            // ChatListHeader: 92 tall with ChatTabs never materialized, 152 with it materialized
            // and collapsed, 192 with it visible. ChatsPanel row 0 is the header and row 1 holds
            // TopicListPresenter with Margin.Top = padding, over a DialogsPanel pulled up by
            // -margin, so window y = header height + padding - margin, and all three converge on
            // 40 = the top of MasterFrame.
            //
            // In Uno the header does not measure 92 in the first state: x:Load="False" leaves an
            // ElementStub in Panel.Children (PORTING.md 6), StackPanel.MeasureOverride counts it
            // as visible for the Spacing="60", and 92 + 60 = 152. With padding = -14 the panel
            // landed at y=100 instead of 40, and those 60px of bare page background (#171717,
            // 395px wide) above the topic list ARE the "black space".
            //
            // So derive the padding from the height the header actually has, which reproduces
            // -14/-74/-78 exactly in upstream's three states and also gets the fourth right
            // (materialized and collapsed: Uno does not count a Collapsed child for the spacing,
            // so the header is back to 92 and upstream's -74 would overshoot by 60 the other way).
            // 40 is the height of this page's own title bar, `<Grid Height="40">` in MainPage.xaml.
            var headerHeight = ChatListHeader.ActualHeight;
            if (headerHeight > 0)
            {
                padding = (int)Math.Round(40 + margin - headerHeight);
            }
#endif

            DialogsPanel.Margin = new Thickness(0, -margin, 0, 0);
            TopicListPresenter.Margin = new Thickness(68, padding, 0, 0);

#if LINUX
            // Moving DialogsPanel moves ChatListHeader with it, and the search box - which the
            // stand-in at the top of that header is sized against - does NOT move: it lives in
            // RootGrid's first row, outside this panel. So the distance the stand-in represents
            // changes by exactly this margin, and the measurement has to be retaken or it is
            // stale by 74px. Measured in the field: the same probe read 73 undisplaced and 147
            // with this -74 applied.
            UpdateChatListTopPadding();
#endif

            void ShowHideTopicListCompleted()
            {
                if (_topicListCollapsed)
                {
                    TopicListPresenter.Visibility = Visibility.Collapsed;
                }
            }

#if LINUX
            // No slide here: what the Windows path below animates is a redirect visual of the
            // chat list's own ScrollViewer, taken down again from CompositionScopedBatch.Completed
            // - and that handler never fires in Uno (PORTING.md 6), so the scaffolding would stay
            // up for good: the redirect frozen over the list, DialogsPanel keeping the negative
            // margin the animation borrowed and the chat rows keeping the inset clip. It also
            // reaches into the list's template by child index (GetChild(ChatsList, 0), then
            // GetChild(element, 1)), which is a WinUI shape, not Uno's.
            //
            // The end state is what matters and it is all here: the topic list is on top
            // (Canvas.ZIndex 1 in MainPage.xaml, opaque background, 68px left margin) and the
            // chat list underneath goes compact, which is the avatar rail those 68px show.
            // Same trade as ChatListListView.ChangeView, which switches folder without the slide.
            Header.Opacity = show ? 0 : 1;

            // Opacity is not part of Uno's hit testing (UIElement.CoerceHitTestVisibility looks at
            // IsLoaded, IsHitTestVisible, Visibility, IsEnabledOverride and IsViewHit, and at
            // nothing else), so the search field and the "new chat" button stayed clickable while
            // invisible. Upstream lives with it because the topic list covers them; here they are
            // only covered from y=40 down and only once the panel is in the right place.
            Header.IsHitTestVisible = !show;

            if (ChatTabs != null)
            {
                ChatTabs.Opacity = show ? 0 : 1;
            }

            ChatsList.UpdateViewState(show ? MasterDetailState.Compact : MasterDetail.CurrentState);
            ShowHideTopicListCompleted();

            // The two clips ARE reproduced: they are not decoration, they are what makes the
            // topic list appear IN PLACE OF the chat list instead of on top of it.
            //
            // `chats.Clip` is the important one. Compact mode does NOT move the chat rows - the
            // code in ChatCell.UpdateViewState that used to shift them left is commented out
            // upstream, so all it does now is swap the unread badge. The whole "the chat list
            // shrinks to its avatar rail" effect is this clip and nothing else. Without it the
            // full-width rows stay where they were and show through the topic list, whose own
            // BackgroundRoot is SettingsItemBackground = #0DFFFFFF, i.e. 5% white: opaque enough
            // over the page background it is designed to sit on, transparent over a chat list.
            // Measured on screen before writing this: the list is 463 wide, so the 395 of inset
            // leaves exactly the 68px rail that TopicListPresenter's left margin uncovers.
            //
            // GetChild(ChatsList, 0) is safe here: the local:ChatListListView template in
            // Themes/Generic.xaml carries no win: prefix, and the tree dump confirms its root is
            // the Grid this expects (Grid > [Border Ghost, ScrollViewer]).
            var element = VisualTreeHelper.GetChild(ChatsList, 0) as UIElement;
            if (element != null)
            {
                var chats = ElementComposition.GetElementVisual(element);

                if (_topicListCollapsed)
                {
                    chats.Clip = null;

                    // What batch.Completed writes back on Windows: the panel gives back the
                    // margin the (absent) animation would have borrowed.
                    DialogsPanel.Margin = new Thickness(0);

                    // And the stand-in has to be told, or it keeps the height it was given while
                    // the panel was displaced - which is the whole band.
                    UpdateChatListTopPadding();
                }
                else
                {
                    var compositor = chats.Compositor;

                    chats.Clip = compositor.CreateInsetClip(0, 0, ChatsList.ActualSize.X - 68, 0);

                    // There used to be a second clip here, on DialogsPanel, copied from the
                    // Windows path where it travels with an animation of Translation.Y. Without
                    // that animation it cannot clip anything, and it is worth writing down why so
                    // that nobody chases a layout hole through it again: its TopInset is
                    // ChatsList.Margin.Top, measured in DialogsPanel's own coordinates, and in
                    // those same coordinates the top edge of ChatsList IS ChatsList.Margin.Top -
                    // the list sits in row 0 of ChatsPanel, which starts at y=0, and nothing else
                    // moves it. The two numbers are equal by construction in both states (76/76
                    // with side tabs, 112/112 with top tabs), and the only thing above that line
                    // inside DialogsPanel is ChatListHeader's `<Border Height="92"/>`, which has
                    // no Background and paints nothing.
                }
            }
            else if (_topicListCollapsed)
            {
                DialogsPanel.Margin = new Thickness(0);
                UpdateChatListTopPadding();
            }

            return;
#else
            var element = VisualTreeHelper.GetChild(ChatsList, 0) as UIElement;
            if (element == null)
            {
                ShowHideTopicListCompleted();
                return;
            }

            var scrollingHost = VisualTreeHelper.GetChild(element, 1) as UIElement;

            var chats = ElementComposition.GetElementVisual(element);
            var panel = ElementComposition.GetElementVisual(TopicListPresenter);

            var dialogs = ElementComposition.GetElementVisual(DialogsPanel);
            var header = ElementComposition.GetElementVisual(Header);

            var compositor = chats.Compositor;

            var inset = 68;
            var width = ChatsList.ActualSize.X - inset;

            var sourceOffset = new Vector2(inset, 0);
            var sourceSize = new Vector2(width, ChatsList.ActualSize.Y);

            var redirect = compositor.CreateRedirectVisual(scrollingHost, sourceOffset, sourceSize);
            redirect.Offset = new Vector3(sourceOffset, 0);
            redirect.Clip = compositor.CreateInsetClip();

            ElementCompositionPreview.SetElementChildVisual(ChatsList, redirect);
            ElementCompositionPreview.SetIsTranslationEnabled(TopicListPresenter, true);

            chats.Clip = compositor.CreateInsetClip(0, 0, width, 0);
            dialogs.Clip = null;

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                redirect.Size = Vector2.Zero;
                ElementCompositionPreview.SetElementChildVisual(ChatsList, null);

                if (_topicListCollapsed)
                {
                    chats.Clip = null;
                    TopicListPresenter.Visibility = Visibility.Collapsed;

                    dialogs.Properties.InsertVector3("Translation", Vector3.Zero);
                    DialogsPanel.Margin = new Thickness(0);
                }
                else
                {
                    dialogs.Clip = compositor.CreateInsetClip(0, (float)ChatsList.Margin.Top, 0, 0);
                }
            };

            var offset0 = compositor.CreateScalarKeyFrameAnimation();
            offset0.InsertKeyFrame(0, show ? width : 0);
            offset0.InsertKeyFrame(1, show ? 0 : width);
            offset0.Duration = Constants.FastAnimation;

            var offset1 = compositor.CreateScalarKeyFrameAnimation();
            offset1.InsertKeyFrame(0, show ? inset : -width + inset);
            offset1.InsertKeyFrame(1, show ? -width + inset : inset);
            offset1.Duration = Constants.FastAnimation;

            var clip0 = compositor.CreateScalarKeyFrameAnimation();
            clip0.InsertKeyFrame(0, show ? 0 : width);
            clip0.InsertKeyFrame(1, show ? width : 0);
            clip0.Duration = Constants.FastAnimation;

            panel.StartAnimation("Translation.X", offset0);
            redirect.StartAnimation("Offset.X", offset1);
            redirect.Clip.StartAnimation("LeftInset", clip0);

            ChatsList.UpdateViewState(show ? MasterDetailState.Compact : MasterDetail.CurrentState);

            var offset2 = compositor.CreateScalarKeyFrameAnimation();
            offset2.InsertKeyFrame(0, show ? margin : 0);
            offset2.InsertKeyFrame(1, show ? 0 : margin);
            offset2.Duration = Constants.FastAnimation;

            var clip1 = compositor.CreateScalarKeyFrameAnimation();
            clip1.InsertKeyFrame(0, show ? 0 : 40);
            clip1.InsertKeyFrame(1, show ? 40 : 0);
            clip1.Duration = Constants.FastAnimation;

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, show ? 1 : 0);
            opacity.InsertKeyFrame(1, show ? 0 : 1);
            opacity.Duration = Constants.FastAnimation;

            dialogs.StartAnimation("Translation.Y", offset2);
            header.StartAnimation("Opacity", opacity);

            if (ChatTabs != null)
            {
                var tabs = ElementComposition.GetElementVisual(ChatTabs);
                tabs.StartAnimation("Opacity", opacity);
            }

            batch.End();
#endif
        }

        private void ChatList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var element = VisualTreeHelper.GetChild(ChatsList, 0) as UIElement;
            if (element == null)
            {
                return;
            }

            var chats = ElementComposition.GetElementVisual(element);
            if (chats.Clip is InsetClip inset && inset.RightInset != 0 && TopicListPresenter != null)
            {
                inset.RightInset = TopicListPresenter.ActualSize.X;
            }
        }

        private void Banner_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            MasterDetail.BackgroundMargin = new Thickness(0, -e.NewSize.Height, 0, 0);
        }

        private void Stories_Expanding(object sender, EventArgs e)
        {
            if (_prevIndex != INDEX_CHATS)
            {
                SetPivotSelectedIndex(INDEX_CHATS);
                ViewModel.RaisePropertyChanged(nameof(ViewModel.SelectedFolder));
            }
            else if (!_searchCollapsed)
            {
                Search_LostFocus(null, null);
            }

            HideTopicList();
        }

        private void ComposeButton_Click(object sender, RoutedEventArgs e)
        {
#if LINUX
            // u-056: FrozenPopup (the explanation shown to a frozen account) is not in the Linux
            // subset - rare state, and TDLib itself still refuses whatever a frozen account tries
            // regardless of which popup opens first, so this always opens ContactsPopup rather
            // than inventing a substitute.
            ViewModel.NavigationService.ShowPopup(new ContactsPopup
            {
                Title = Strings.NewMessageTitle
            });
#else
            if (ViewModel.ClientService.FreezeState.IsFrozen)
            {
                ViewModel.NavigationService.ShowPopup(new FrozenPopup(ViewModel.ClientService.FreezeState));
            }
            else
            {
                ViewModel.NavigationService.ShowPopup(new ContactsPopup
                {
                    Title = Strings.NewMessageTitle
                });
            }
#endif
        }

        // CABLEADO (u-stories, 2026-09-05). Estuvo sin cablear mientras
        // Telegram.Linux/Xaml/ActiveStoriesSegments.cs fue un cascaron: HasActiveStories era
        // `=> false`, Open() tenia el cuerpo vacio y no se pintaba ningun anillo, asi que quitar
        // la guarda solo habria mandado el clic a un metodo vacio -- y ni siquiera compilaba,
        // porque el Open del cascaron declaraba Func<object, Rect> y esta lambda lee story.Chat.
        // Ese cascaron ya tiene cuerpo y esa firma, de modo que este es el mismo cuerpo que WinUI
        // y no hay dos caminos que mantener.
        private void ChatCell_StoryClick(object sender, Chat chat)
        {
            if (sender is ActiveStoriesSegments segments)
            {
                segments.Open(ViewModel.NavigationService, ViewModel.ClientService, chat, 48, story =>
                {
                    var container = ChatsList.ContainerFromItem(story.Chat) as SelectorItem;
                    if (container != null)
                    {
                        var transform = container.TransformToVisual(null);
                        var point = transform.TransformPoint(new Point());

                        return new Rect(point.X + 4 + 8, point.Y + 4 + 8, 40, 40);
                    }

                    return Rect.Empty;
                });
            }
        }

        public Task UpdateLayoutAsync()
        {
            if (ChatsList.IsConnected)
            {
                if (ChatsList.ItemsPanelRoot != null)
                {
                    return ChatsList.ItemsPanelRoot.UpdateLayoutAsync();
                }

                return ChatsList.UpdateLayoutAsync();
            }

            return Task.CompletedTask;
        }

        #region Chat List

        private void Chats_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            try
            {
                if (e.Items[0] is Chat chat)
                {
                    var position = chat.GetPosition(ViewModel.Chats.Items.ChatList);
                    if (position == null || !position.IsPinned || e.Items.Count > 1 || ChatsList.SelectionMode == ListViewSelectionMode.Multiple)
                    {
                        ChatsList.CanReorderItems = false;
                        e.Cancel = true;
                    }
                    else
                    {
                        ChatsList.CanReorderItems = true;
                    }
                }
            }
            catch
            {
                ChatsList.CanReorderItems = false;
                e.Cancel = true;
            }
        }

        private void Chats_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            ChatsList.CanReorderItems = false;

            var chatList = ViewModel.Chats.Items.ChatList;
            if (chatList == null)
            {
                return;
            }

            if (args.DropResult == DataPackageOperation.Move && args.Items.Count == 1 && args.Items[0] is Chat chat)
            {
                var items = ViewModel.Chats.Items;
                if (items.Count == 1)
                {
                    return;
                }

                var index = items.IndexOf(chat);
                var compare = items[index > 0 ? index - 1 : index + 1];

                var position = compare.GetPosition(items.ChatList);
                if (position == null)
                {
                    return;
                }

                if (position.Source != null && index > 0)
                {
                    position = items[index + 1].GetPosition(items.ChatList);
                }

                if (position.IsPinned)
                {
                    var pinned = items.Where(x =>
                    {
                        var position = x.GetPosition(items.ChatList);
                        if (position == null)
                        {
                            return false;
                        }

                        return position.IsPinned;
                    }).Select(x => x.Id).ToArray();

                    ViewModel.ClientService.Send(new SetPinnedChats(chatList, pinned));
                }
                else
                {
                    var real = chat.GetPosition(items.ChatList);
                    if (real != null)
                    {
                        items.Handle(chat.Id, real.Order);
                    }
                }
            }
        }

        private void ChatsList_Swiped(object sender, ChatListSwipedEventArgs e)
        {
            ScrollFolder(e.Direction == CarouselDirection.Next ? 1 : -1, true);
        }

        #endregion

        private bool _testLeak;

        [Conditional("DEBUG")]
        public void LeakTest(bool enable)
        {
            if (!_testLeak)
            {
                if (enable)
                {
                    _testLeak = true;
                }

                return;
            }

            return;

            _viewModel = null;
            LayoutRoot.Children.Clear();

            LayoutRoot = null;
            State = null;
            TitleBarrr = null;
            ChatTabsLeft = null;
            Photo = null;
            MasterDetail = null;
            Confetti = null;
            Playback = null;
            CallBanner = null;
            Header = null;
            Stories = null;
            SettingsRoot = null;
            SettingsView = null;
            DialogsSearchPanel = null;
            DialogsPanel = null;
            ChatsPanel = null;
            Downloads = null;
            ManagePanel = null;
            ButtonManage = null;
            ManageCount = null;
            ManageMute = null;
            ManageMark = null;
            ManageClear = null;
            UpdateShadow = null;
            UpdateCloud = null;
            ChatListHeader = null;
            ChatsList = null;
            TopicListPresenter = null;
            EmptyState = null;
            ArchivedChatsPresenter = null;
            ArchivedChatsPanel = null;
            ArchivedChats = null;
            SetBirthdateCard = null;
            UnconfirmedCard = null;
            ChatTabs = null;
            ChatTabsView = null;
            ChatFolders = null;
            MainHeader = null;
            SearchField = null;
            ChatsOptions = null;
            Proxy = null;
            Lock = null;
            ChatFoldersSide = null;
            TitleBarHandle = null;
            TitleBarLogo = null;
            TitleText = null;
            StateLabel = null;
            MemoryLabel = null;
            LogoBasic = null;
            LogoEmoji = null;
        }

        private void ChatFolders_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem == ViewModel.SelectedFolder)
            {
                var scrollViewer = ChatsList.GetScrollViewer();
                scrollViewer?.TryChangeView(null, 0, null);
            }
        }
    }
}
