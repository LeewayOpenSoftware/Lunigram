//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Telegram.Collections;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Stories;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Telegram.Views.Chats;
using Telegram.Views.Popups;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.ViewModels.Chats
{
    public partial class ChatStoriesViewModel : ViewModelBase, IIncrementalCollectionOwner
    {
        private ConcurrentDictionary<int, StoryViewModel> _cache = new();

        private ChatStoriesType _type = ChatStoriesType.Pinned;
        private int _fromStoryId;

        private Chat _chat;
        public Chat Chat => _chat;

        public ChatStoriesViewModel(IClientService clientService, ISettingsService settingsService, IEventAggregator aggregator)
            : base(clientService, settingsService, aggregator)
        {
            Items = new IncrementalCollection<StoryViewModel>(this);
            SelectedItems = new RangeObservableCollection<StoryViewModel>();
            SelectedItems.CollectionChanged += OnCollectionChanged;

            ItemsView = new IncrementalCollectionView<StoryViewModel, IncrementalCollection<StoryViewModel>>(Items);

            Albums = new ObservableCollection<StoryAlbumViewModel>();
            Albums.Add(new StoryAlbumViewModel(this, new StoryAlbum(0, Strings.StoriesAlbumNameAllStories, null, null)));
            Albums.CollectionChanged += Albums_CollectionChanged;

            SelectedAlbum = Albums[0];
        }

        public StoryViewModel GetOrCreate(Story story)
        {
            if (_cache.TryGetValue(story.Id, out StoryViewModel viewModel))
            {
                return viewModel;
            }

            viewModel = new StoryViewModel(ClientService, story);
            _cache[story.Id] = viewModel;

            return viewModel;
        }

        private void OnCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RaisePropertyChanged(nameof(CanSelectedToggleIsPinned));
            RaisePropertyChanged(nameof(CanSelectedBeDeleted));
        }

        public string Title => _type switch
        {
            ChatStoriesType.Archive => Strings.ProfileStoriesArchive,
            ChatStoriesType.Pinned or _ => Strings.ProfileMyStories
        };

        public bool IsPostedToChatPage => _type == ChatStoriesType.Pinned;

        public IncrementalCollection<StoryViewModel> Items { get; }
        public RangeObservableCollection<StoryViewModel> SelectedItems { get; }

        public bool HasAlbums => _type == ChatStoriesType.Pinned && (Albums.Count > 1 || CanEditStories);

        private bool _albumsLoaded;

        // Set when a load arrived before navigation supplied the chat, so it can be re-driven.
        private bool _loadDeferredNoChat;

        public ObservableCollection<StoryAlbumViewModel> Albums { get; private set; }

        private StoryAlbumViewModel _selectedAlbum;
        public StoryAlbumViewModel SelectedAlbum
        {
            get => _selectedAlbum;
            set
            {
                if (Set(ref _selectedAlbum, value ?? Albums.FirstOrDefault()))
                {
#if LINUX
                    // u-profile-posts-tab-empty: IncrementalCollectionView.SetSource (the sync
                    // overload) never calls LoadMoreItemsAsync itself -- on WinUI, the GridView's
                    // own ISupportIncrementalLoading auto-invoke is what kicks the FIRST load once
                    // the panel measures an empty-but-loadable source. Measured: that auto-invoke
                    // never fires for this tab's GridView on Uno Skia, so Items stayed empty
                    // forever (both on initial navigation AND on switching album). SetSourceAsync
                    // already exists for exactly this (see ProfileGiftsTabViewModel.Reload) --
                    // await it explicitly instead of depending on the panel.
                    _ = ItemsView.SetSourceAsync(_selectedAlbum.Items);
#else
                    ItemsView.SetSource(_selectedAlbum.Items);
#endif
                }
            }
        }

        public IncrementalCollectionView<StoryViewModel, IncrementalCollection<StoryViewModel>> ItemsView { get; }

        public bool CanSelectedToggleIsPinned => SelectedItems.All(x => x.CanToggleIsPostedToChatPage);
        public bool CanSelectedBeDeleted => SelectedItems.All(x => x.CanBeDeleted);

        public bool CanEditStories => Chat != null && (Chat.IsUser(ClientService.Options.MyId) || Chat.CanEditStories(ClientService));

        protected override Task OnNavigatedToAsync(object parameter, NavigationMode mode, NavigationState state)
        {
            if (parameter is ChatStoriesArgs args)
            {
                _chat = ClientService.GetChat(args.ChatId);
                _type = args.Type;
            }
            else if (parameter is ChatMessageTopic chatMessageTopic)
            {
                _chat = ClientService.GetChat(chatMessageTopic.ChatId);
            }
            else if (parameter is long chatId)
            {
                _chat = ClientService.GetChat(chatId);
                //_type = ChatStoriesType.Pinned;
            }
            else
            {
                _chat = ClientService.GetChat(ClientService.Options.MyId);
                //_type = ChatStoriesType.Pinned;
            }

            if (_chat != null && !_albumsLoaded && _type == ChatStoriesType.Pinned)
            {
                _albumsLoaded = true;
                InitializeAlbums();
            }

            // A load that ran before the chat existed returned empty. Nothing else re-drives it --
            // the list is already at rest with no items -- so re-arm it now that Chat is real.
            // Guarded on the flag so the normal path costs no extra request.
            if (_chat != null && _loadDeferredNoChat)
            {
                _loadDeferredNoChat = false;
                Items.Restart();
            }

            return Task.CompletedTask;
        }

        private async void InitializeAlbums()
        {
            var response = await ClientService.SendAsync(new GetChatStoryAlbums(Chat.Id));
            if (response is StoryAlbums albums)
            {
                foreach (var album in albums.Albums)
                {
                    Albums.Add(new StoryAlbumViewModel(this, album));
                }
            }
        }

        private void Albums_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RaisePropertyChanged(nameof(HasAlbums));
        }

        public void SetType(ChatStoriesType type)
        {
            _type = type;
        }

        public void OpenArchive()
        {
#if LINUX
            Logger.Warning("ChatStoriesViewModel.OpenArchive: ChatStoriesPage is not in the Linux subset.");
#else
            NavigationService.Navigate(typeof(ChatStoriesPage), new ChatStoriesArgs(Chat.Id, ChatStoriesType.Archive));
#endif
        }

        public void ShareAlbum(StoryAlbumViewModel album)
        {
            if (ClientService.HasActiveUsername(Chat, out string username))
            {
                ShowPopup(new ChooseChatsPopup(), new ChooseChatsConfigurationPostLink(new InternalLinkTypeStoryAlbum(username, album.Id)));
            }
        }

        public async void RenameAlbum(StoryAlbumViewModel album)
        {
            var popup = new InputPopup(InputPopupType.Text)
            {
                Title = Strings.StoriesAlbumMenuEditName,
                Header = Strings.StoriesAlbumRenameHint,
                PlaceholderText = Strings.StoriesAlbumTitleInputHint,
                PrimaryButtonText = Strings.Rename,
                PrimaryButtonStyle = BootStrapper.Current.Resources["AccentButtonStyle"] as Style,
                SecondaryButtonText = Strings.Cancel,
                Text = album.Name,
                MinLength = 1,
                MaxLength = 12
            };

            var result = await popup.ShowQueuedAsync(XamlRoot);

            var confirm = new InputPopupResult(result, popup.Text, popup.Value);
            if (confirm.Result == ContentDialogResult.Primary)
            {
                album.Name = confirm.Text;
                ClientService.Send(new SetStoryAlbumName(Chat.Id, album.Id, confirm.Text));
            }
        }

        public async void DeleteAlbum(StoryAlbumViewModel album)
        {
            var confirm = await ShowPopupAsync(string.Format(Strings.StoriesAlbumMenuDeleteAlbumAsk, album.Name), Strings.StoriesAlbumMenuDeleteAlbum, Strings.Delete, Strings.Cancel, destructive: true);
            if (confirm == ContentDialogResult.Primary)
            {
                ClientService.Send(new DeleteStoryAlbum(Chat.Id, album.Id));

                Albums.Remove(album);
                SelectedAlbum = Albums[0];
            }
        }

        public async void CreateAlbum(StoryViewModel story)
        {
            var popup = new InputPopup(InputPopupType.Text)
            {
                Title = Strings.StoriesAlbumCreateNew,
                Header = Strings.StoriesAlbumAddHint,
                PlaceholderText = Strings.StoriesAlbumTitleInputHint,
                PrimaryButtonText = Strings.Create,
                PrimaryButtonStyle = BootStrapper.Current.Resources["AccentButtonStyle"] as Style,
                SecondaryButtonText = Strings.Cancel,
                MinLength = 1,
                MaxLength = 12
            };

            var result = await popup.ShowQueuedAsync(XamlRoot);

            var confirm = new InputPopupResult(result, popup.Text, popup.Value);
            if (confirm.Result == ContentDialogResult.Primary)
            {
                var storyIds = new MutableVector<int>();
                if (story != null)
                {
                    storyIds.Add(story.Id);
                }

                var response = await ClientService.SendAsync(new CreateStoryAlbum(Chat.Id, popup.Text, storyIds));
                if (response is StoryAlbum album)
                {
                    var viewModel = new StoryAlbumViewModel(this, album);

                    story?.AlbumIds.Add(album.Id);

                    Albums.Add(viewModel);
                    SelectedAlbum = viewModel;
                }
            }
        }

        public async void AddStoriesToAlbum(StoryAlbumViewModel album)
        {
            var popup = new ChooseStoriesPopup(this);

            var confirm = await ShowPopupAsync(popup);
            if (confirm == ContentDialogResult.Primary)
            {
                var storyIds = new MutableVector<int>();

                foreach (var story in popup.SelectedItems)
                {
                    if (story.AlbumIds.Contains(album.Id) || album.Items.Contains(story))
                    {
                        continue;
                    }

                    if (album.HasLoadedItems)
                    {
                        album.Items.Insert(0, story);
                    }

                    story.AlbumIds.Add(album.Id);
                    storyIds.Add(story.Id);
                }

                ClientService.Send(new AddStoryAlbumStories(Chat.Id, album.Id, storyIds));
                ShowToast(Locale.Declension(Strings.R.StoryAddedToAlbumTitle, storyIds.Count, album.Name), ToastPopupIcon.Info);
            }
        }

        public void AddStoryToAlbum((StoryViewModel story, StoryAlbumViewModel album) param)
        {
            if (param.story.AlbumIds.Contains(param.album.Id))
            {
                ClientService.Send(new RemoveStoryAlbumStories(Chat.Id, param.album.Id, new[] { param.story.Id }));

                param.story.AlbumIds.Remove(param.album.Id);

                if (param.album.HasLoadedItems)
                {
                    param.album.Items.Remove(param.story);
                }

                ShowToast(string.Format(Strings.StoryRemovedFromAlbumX, param.album.Name), ToastPopupIcon.Info);
            }
            else
            {
                ClientService.Send(new AddStoryAlbumStories(Chat.Id, param.album.Id, new[] { param.story.Id }));

                param.story.AlbumIds.Add(param.album.Id);

                if (param.album.HasLoadedItems)
                {
                    param.album.Items.Insert(0, param.story);
                }

                ShowToast(string.Format(Strings.StoryAddedToAlbumX, param.album.Name), ToastPopupIcon.Info);
            }
        }

        public void ArchiveStory(StoryViewModel story)
        {
            ClientService.Send(new ToggleStoryIsPostedToChatPage(story.PosterChatId, story.Id, !IsPostedToChatPage));

            if (IsPostedToChatPage)
            {
                Items.Remove(story);

                foreach (var album in Albums)
                {
                    album.Items.Remove(story);
                }
            }

            ShowToast(IsPostedToChatPage ? Strings.StoryRemovedFromProfile : Strings.StorySavedToProfile);
        }

        public void ArchiveSelectedStories()
        {
            var selection = SelectedItems.ToArray();

            foreach (var story in selection)
            {
                ClientService.Send(new ToggleStoryIsPostedToChatPage(story.PosterChatId, story.Id, !IsPostedToChatPage));

                if (IsPostedToChatPage)
                {
                    Items.Remove(story);

                    foreach (var album in Albums)
                    {
                        album.Items.Remove(story);
                    }
                }
            }

            ShowToast(Locale.Declension(IsPostedToChatPage ? Strings.R.StoriesRemovedFromProfile : Strings.R.StoriesSavedToProfile, selection.Length));
            UnselectStories();
        }

        public void PinStory(StoryViewModel story)
        {
            if (_pinnedStoryIds.Contains(story.Id))
            {
                _pinnedStoryIds.Remove(story.Id);
                ShowToast(Locale.Declension(Strings.R.StoriesUnpinned, 1), ToastPopupIcon.Unpin);

                Items.Remove(story);

                var index = Items.BinarySearch(story.Date, (date, item) => _pinnedStoryIds.Contains(item.Id) ? 1 : item.Date.CompareTo(date));
                if (index < 0 && (~index < Items.Count || !Items.HasMoreItems))
                {
                    Items.Insert(~index, story);
                }
            }
            else if (_pinnedStoryIds.Count < ClientService.Options.PinnedStoryCountMax)
            {
                _pinnedStoryIds.Insert(0, story.Id);
                ShowToast(Locale.Declension(Strings.R.StoriesPinned, 1), ToastPopupIcon.Pin);

                Items.Remove(story);
                Items.Insert(0, story);
            }
            else
            {
                ShowToast(Locale.Declension(Strings.R.StoriesPinLimit, ClientService.Options.PinnedStoryCountMax), ToastPopupIcon.Info);
                return;
            }

            ClientService.Send(new SetChatPinnedStories(Chat.Id, _pinnedStoryIds));
        }

        public async void DeleteStory(StoryViewModel story)
        {
            var message = Strings.DeleteStorySubtitle;
            var title = Strings.DeleteStoryTitle;

            var confirm = await ShowPopupAsync(message, title, Strings.Delete, Strings.Cancel, destructive: true);
            if (confirm == ContentDialogResult.Primary)
            {
                ClientService.Send(new DeleteStory(story.PosterChatId, story.Id));
                Items.Remove(story);

                foreach (var album in Albums)
                {
                    album.Items.Remove(story);
                }
            }
        }

        public async void DeleteSelectedStories()
        {
            var message = Locale.Declension(Strings.R.DeleteStoriesSubtitle, SelectedItems.Count);
            var title = SelectedItems.Count == 1 ? Strings.DeleteStoryTitle : Strings.DeleteStoriesTitle;

            var confirm = await ShowPopupAsync(message, title, Strings.Delete, Strings.Cancel, destructive: true);
            if (confirm == ContentDialogResult.Primary)
            {
                var selection = SelectedItems.ToArray();

                foreach (var story in selection)
                {
                    ClientService.Send(new DeleteStory(story.PosterChatId, story.Id));
                    Items.Remove(story);

                    foreach (var album in Albums)
                    {
                        album.Items.Remove(story);
                    }
                }

                UnselectStories();
            }
        }

        public void SelectStory(StoryViewModel story)
        {
            SelectedItems.Add(story);
        }

        public void UnselectStories()
        {
            SelectedItems.Clear();
        }

        public void OpenStory(StoryViewModel story, Rect origin, Func<ActiveStoriesViewModel, Rect> closing)
        {
            var activeStories = new ActiveStoriesViewModel(ClientService, Settings, Aggregator, story, ItemsView);
            var viewModel = StoryListViewModel.Create(NavigationService, activeStories);

            var window = new StoriesWindow(XamlRoot);
            window.Update(viewModel, activeStories, StoryOpenOrigin.Card, origin, closing);
            _ = window.ShowAsync();
        }

        public async Task<IncrementalLoadResult> LoadMoreItemsAsync(uint count)
        {
            Logger.Info();

            // Items is wired to this view model in the CONSTRUCTOR, but _chat is only assigned in
            // OnNavigatedToAsync. Opening another user's profile binds ItemsView and triggers a load
            // before that navigation happens -- measured at ~477ms before it -- so Chat is still null
            // here and `Chat.Id` threw a NullReferenceException. IncrementalCollection swallows it
            // into a zero-item load, which is why the Posts tab rendered blank with no error on screen.
            // Report "nothing yet, but there is more" so the collection stays armed, and remember to
            // restart it once navigation supplies the chat.
            if (Chat == null)
            {
                _loadDeferredNoChat = true;
                return new IncrementalLoadResult(0, true);
            }

            var totalCount = 0u;
            var canBeEdited = false;
            var botPreview = false;

            Function function;
            if (ClientService.TryGetUser(Chat.Id, out User user) && user.Type is UserTypeBot userTypeBot)
            {
                canBeEdited = userTypeBot.CanBeEdited;
                botPreview = true;

                function = new GetBotMediaPreviews(user.Id);
            }
            else
            {
                function = _type switch
                {
                    ChatStoriesType.Archive => new GetChatArchivedStories(Chat.Id, _fromStoryId, 50),
                    ChatStoriesType.Pinned or _ => new GetChatPostedToChatPageStories(Chat.Id, _fromStoryId, 50),
                };
            }

            var response = await ClientService.SendAsync(function);
            if (response is BotMediaPreviews previews)
            {
                var items = previews.Previews
                    .Select((x, i) => new Story
                    {
                        Id = i + 1,
                        PosterChatId = Chat.Id,
                        Date = x.Date,
                        Content = x.Content,
                        CanBeDeleted = canBeEdited
                    })
                    .ToVector();
                response = new Td.Api.Stories(0, items, Array.Empty<int>());
            }

            if (response is Td.Api.Stories stories)
            {
                _pinnedStoryIds = stories.PinnedStoryIds.ToMutableVector();

                foreach (var story in stories.StoriesValue)
                {
                    _fromStoryId = story.Id;

                    if (botPreview)
                    {
                        Items.Add(new StoryViewModel(ClientService, story, botPreview));
                    }
                    else
                    {
                        Items.Add(GetOrCreate(story));
                    }

                    totalCount++;
                }

                Items.TotalCount = stories.TotalCount;
            }

            IsEmpty = Items.Count == 0;

            return new IncrementalLoadResult(totalCount, totalCount > 0 && function is not GetBotMediaPreviews);
        }

        private bool _isEmpty = false;
        public bool IsEmpty
        {
            get => Albums[0].IsEmpty;
            set
            {
                Albums[0].IsEmpty = value;
                RaisePropertyChanged(nameof(ShowHint));
            }
        }

        public bool ShowHint => !IsEmpty && _type == ChatStoriesType.Archive;

        private MutableVector<int> _pinnedStoryIds = Array.Empty<int>();

        public bool IsPinned(StoryViewModel story)
        {
            return _pinnedStoryIds != null && _pinnedStoryIds.Contains(story.Id);
        }

        public void SetPinnedItems()
        {
            var storyIds = new MutableVector<int>();

            foreach (var item in Items)
            {
                if (IsPinned(item))
                {
                    storyIds.Add(item.Id);
                }
                else
                {
                    break;
                }
            }

            if (storyIds.Count != _pinnedStoryIds.Count)
            {
                return;
            }

            ClientService.Send(new SetChatPinnedStories(Chat.Id, storyIds));
        }

        public void SetPinnedItem(StoryViewModel story)
        {
            var index = _pinnedStoryIds.IndexOf(story.Id);
            if (index >= 0 && index < Items.Count)
            {
                Items.Remove(story);
                Items.Insert(index, story);
            }
        }
    }

    public partial class StoryAlbumViewModel : ServiceBase, IIncrementalCollectionOwner
    {
        private readonly ChatStoriesViewModel _viewModel;

        private int _fromStoryId;

        public StoryAlbumViewModel(ChatStoriesViewModel viewModel, StoryAlbum album)
            : base(viewModel.ClientService, viewModel.Settings, viewModel.Aggregator)
        {
            _viewModel = viewModel;

            Name = album.Name;
            Id = album.Id;

            if (album.Id == 0)
            {
                Items = _viewModel.Items;
            }
            else
            {
                Items = new IncrementalCollection<StoryViewModel>(this);
                Items.CollectionChanged += OnCollectionChanged;
            }
        }

        private void OnCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            IsEmpty = Items.Empty();
        }

        private string _name;
        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        public int Id { get; }

        public IncrementalCollection<StoryViewModel> Items { get; }

        public async Task<IncrementalLoadResult> LoadMoreItemsAsync(uint count)
        {
            Logger.Info();

            var totalCount = 0u;

            var response = await ClientService.SendAsync(new GetStoryAlbumStories(_viewModel.Chat.Id, Id, _fromStoryId, 50));
            if (response is Td.Api.Stories stories)
            {
                foreach (var story in stories.StoriesValue)
                {
                    _fromStoryId = story.Id;

                    Items.Add(_viewModel.GetOrCreate(story));
                    totalCount++;
                }

                //Items.TotalCount = stories.TotalCount;
            }

            IsEmpty = Items.Count == 0;
            HasLoadedItems = true;

            return new IncrementalLoadResult(totalCount, totalCount > 0);
        }

        public bool HasLoadedItems { get; private set; }

        private bool _isEmpty;
        public bool IsEmpty
        {
            get => _isEmpty;
            set => Set(ref _isEmpty, value);
        }

        public void ReorderStories()
        {
            if (Id != 0)
            {
                ClientService.Send(new ReorderStoryAlbumStories(_viewModel.Chat.Id, Id, Items.Select(x => x.Id).ToVector()));
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }
}
