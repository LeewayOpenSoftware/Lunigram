//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Threading.Tasks;
using Telegram.Collections;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels.Delegates;
using Telegram.Views.Popups;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;

namespace Telegram.ViewModels.Gallery
{
    public abstract class GalleryViewModelBase : ViewModelBase, IDelegable<IGalleryDelegate>
    {
        private readonly IStorageService _storageService;

        protected int _additionalPhotos;
        protected bool _hasProtectedContent;

        public IGalleryDelegate Delegate { get; set; }

        public GalleryViewModelBase(IClientService clientService, IStorageService storageService, IEventAggregator aggregator)
            : base(clientService, clientService.Session.Resolve<ISettingsService>(), aggregator)
        {
            _storageService = storageService;
            //Aggregator.Subscribe(this);
        }

        //public void Handle(UpdateFile update)
        //{
        //    BeginOnUIThread(() => Delegate?.UpdateFile(update.File));
        //}

        //protected override void BeginOnUIThread(Action action)
        //{
        //    // This is somehow needed because this viewmodel requires a Dispatcher
        //    // in some situations where base one might be null.
        //    Execute.BeginOnUIThread(action);
        //}

        public bool HasProtectedContent => _hasProtectedContent;

        public virtual int Position
        {
            get
            {
                return SelectedIndex + 1;
            }
        }

        public int SelectedIndex
        {
            get
            {
                if (Items == null || SelectedItem == null)
                {
                    return 0;
                }

                return Items.IndexOf(SelectedItem);
            }
        }

        protected int _totalItems;
        public int TotalItems
        {
            get => _totalItems;
            set
            {
                Set(ref _totalItems, value);
                RaisePropertyChanged(nameof(Position));
            }
        }

        protected GalleryMedia _selectedItem;
        public GalleryMedia SelectedItem
        {
            get => _selectedItem;
            set
            {
                Set(ref _selectedItem, value);
                OnSelectedItemChanged(value);
                //RaisePropertyChanged(() => SelectedIndex);
            }
        }

        protected GalleryMedia _firstItem;
        public GalleryMedia FirstItem
        {
            get => _firstItem;
            set => Set(ref _firstItem, value);
        }

        protected object _poster;
        public object Poster
        {
            get => _poster;
            set => Set(ref _poster, value);
        }

        public RangeObservableCollection<GalleryMedia> Items { get; protected set; }

        public virtual RangeObservableCollection<GalleryMedia> Group { get; }

        public void LoadMore()
        {
            if (Items.Count > 1)
            {
                var index = SelectedIndex;
                if (index == Items.Count - 1)
                {
                    LoadNext();
                }
                if (index == 0)
                {
                    LoadPrevious();
                }
            }
        }

        protected virtual void LoadPrevious() { }
        protected virtual void LoadNext() { }

        protected virtual void OnSelectedItemChanged(GalleryMedia item)
        {
            RaisePropertyChanged(nameof(Position));

            if (item == null || Window == null)
            {
                return;
            }

            if (item.HasProtectedContent && !_hasProtectedContent)
            {
                _hasProtectedContent = true;
                Window.DisableScreenCapture(GetHashCode());
            }
            else if (_hasProtectedContent && !item.HasProtectedContent)
            {
                _hasProtectedContent = false;
                Window.EnableScreenCapture(GetHashCode());
            }
        }

        public override INavigationService NavigationService
        {
            get => base.NavigationService;
            set
            {
                base.NavigationService = value;
                OnSelectedItemChanged(SelectedItem);
            }
        }

        public virtual bool CanDelete
        {
            get
            {
                return false;
            }
        }

        public virtual bool CanOpenWith
        {
            get
            {
                if (SelectedItem is GalleryMessage message && message.HasProtectedContent)
                {
                    return false;
                }

                return true;
            }
        }

        public async void OpenStickers()
        {
#if LINUX
            // StickersPopup is out of the subset (the whole sticker set viewer). The button that
            // calls this is x:Load'ed on HasStickers, and it is win: in the XAML, so nothing reaches
            // here; the method stays for the shared signature.
#else
            if (_selectedItem != null && _selectedItem.HasStickers)
            {
                if (_selectedItem is GalleryChatPhoto chatPhoto)
                {
                    if (chatPhoto.Sticker?.Type is ChatPhotoStickerTypeRegularOrMask regularOrMask)
                    {
                        await StickersPopup.ShowAsync(NavigationService, regularOrMask.StickerSetId);
                    }
                    else if (chatPhoto.Sticker?.Type is ChatPhotoStickerTypeCustomEmoji customEmoji)
                    {
                        var response = await ClientService.SendAsync(new GetCustomEmojiStickers(new[] { customEmoji.CustomEmojiId }));
                        if (response is Stickers stickers && stickers.StickersValue.Count == 1)
                        {
                            await StickersPopup.ShowAsync(NavigationService, stickers.StickersValue[0].SetId);
                        }
                    }
                }
                else
                {
                    var file = _selectedItem.File;
                    if (file == null)
                    {
                        return;
                    }

                    await StickersPopup.ShowAsync(NavigationService, new InputFileId(file.Id));
                }
            }
#endif
        }

        public virtual void View()
        {
            FirstItem = null;

            var message = _selectedItem as GalleryMessage;
            if (message == null || !message.CanBeViewed)
            {
                return;
            }

            NavigationService.NavigateToChat(message.ChatId, message: message.Id);
        }

        // CC-3b (2026-09-04): esta entrada YA estaba viva sin guarda en el menu contextual
        // (GalleryWindow.xaml.cs:1462, `flyout.CreateFlyoutItem(() => item.CanBeShared,
        // viewModel.Forward, ...)`, sin `#if`), asi que el boton se dibujaba, se podia pulsar y
        // no hacia nada -- justo lo que CLAUDE.md llama peor que no tener el boton. El comentario
        // que decia que ChooseChatsPopup estaba «fuera del subconjunto» quedo obsoleto con CC-1.
        // Cuerpo unico para las dos plataformas (el `#if !LINUX` interior ya era redundante bajo
        // el `#else` exterior, asi que se retira con el mismo cambio).
        public virtual async void Forward()
        {
            if (_selectedItem is GalleryMessage message)
            {
                var response = await ClientService.SendAsync(new GetMessageProperties(message.ChatId, message.Id));
                if (response is MessageProperties properties && properties.CanBeForwarded)
                {
                    ShowPopup(new ChooseChatsPopup(), new ChooseChatsConfigurationShareMessages(new MessageToShare(message.Message, properties)), ElementTheme.Dark);
                }
            }
            else
            {
                var input = _selectedItem?.ToInput();
                if (input != null)
                {
                    ShowPopup(new ChooseChatsPopup(), new ChooseChatsConfigurationPostMessage(input), ElementTheme.Dark);
                }
            }
        }

        public virtual void Delete()
        {
        }

        public async void Copy()
        {
            var item = _selectedItem;
            if (item == null || !item.CanBeCopied)
            {
                return;
            }

            var file = item.File;
            if (file == null)
            {
                return;
            }

            var cached = await ClientService.GetFileAsync(file);
            if (cached != null)
            {
                var dataPackage = new DataPackage();
                dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromFile(cached));
                ClipboardEx.TrySetContent(dataPackage);

                ToastPopup.Show(XamlRoot, Strings.ImageCopied, ToastPopupIcon.Copied);
            }
        }

        public virtual async void Save()
        {
            var item = _selectedItem;
            if (item == null || !item.CanBeSaved)
            {
                return;
            }

            var file = item.File;
            if (file != null)
            {
                await _storageService.SaveFileAsAsync(XamlRoot, file);
            }
        }

        public virtual async void OpenWith()
        {
            var item = _selectedItem;
            if (item == null || !CanOpenWith)
            {
                return;
            }

            var file = item.File;
            if (file != null)
            {
                await _storageService.OpenFileWithAsync(file);
            }
        }

        public void OpenMessage(GalleryMedia galleryItem)
        {
            var message = galleryItem as GalleryMessage;
            if (message == null)
            {
                return;
            }

            ClientService.Send(new OpenMessageContent(message.ChatId, message.Id));
        }

        public virtual void PlaybackStarted(GalleryMedia item)
        {

        }

        public virtual void PlaybackStopped()
        {

        }

        public virtual void AdvertisementDisplayed()
        {

        }

        public virtual void HideAdvertisement()
        {

        }
    }
}
