//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Telegram.Navigation.Services;
using Telegram.Td.Api;
using Windows.Storage;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Telegram.Services
{
    /// <summary>
    /// Linux replacement for <c>Telegram/Services/ProfilePhotoService.cs</c>, which is not in the
    /// subset: it goes through <c>PickSingleMediaAsync</c> (StoragePhoto/StorageVideo, the media
    /// entities), <c>EditMediaPopup</c> + <c>ImageCropper</c> and, for animated avatars, the
    /// VideoGeneration transcoder - none of which are ported yet.
    ///
    /// Same shape as <c>Platform/StorageService.cs</c>: the interface is declared here too, so the
    /// shared file stays out of the csproj entirely.
    ///
    /// What is kept: the whole <c>Complete</c> half (it is pure TDLib and portable) and the file
    /// picking, which on X11 goes through Uno's <c>LinuxFilePickerExtension</c> and therefore
    /// through xdg-desktop-portal. What is degraded: no cropping and no animated avatars. The
    /// picked file is handed to TDLib as an <c>InputChatPhotoStatic(InputFileLocal)</c> and the
    /// server does the square crop, which is what it does for any client that does not crop.
    /// </summary>
    public interface IProfilePhotoService
    {
        Task<InputChatPhoto> PreviewSetPhotoAsync(INavigationService navigation);
        Task<bool> SetPhotoAsync(INavigationService navigation, long? chatId, bool isPublic = false, bool isPersonal = false);
        Task<InputChatPhoto> PreviewCreatePhotoAsync(INavigationService navigation);
        Task<bool> CreatePhotoAsync(INavigationService navigation, long? chatId, bool isPublic = false, bool isPersonal = false);
    }

    public partial class ProfilePhotoService : IProfilePhotoService
    {
        private readonly IClientService _clientService;

        public ProfilePhotoService(IClientService clientService)
        {
            _clientService = clientService;
        }

        // Deliberately narrower than Constants.MediaTypes: a video avatar would need the
        // transcoder, so only still images are offered.
        private static readonly string[] _imageTypes = new[]
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".webp"
        };

        public async Task<InputChatPhoto> PreviewSetPhotoAsync(INavigationService navigation)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

                foreach (var type in _imageTypes)
                {
                    picker.FileTypeFilter.Add(type);
                }

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    // Cancelled. Not an error, and no toast: the user closed the dialog.
                    return null;
                }

                Logger.Info($"Profile photo picked: {file.Path}");
                return new InputChatPhotoStatic(new InputFileLocal(file.Path));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);

                if (navigation != null)
                {
                    await navigation.ShowPopupAsync(Strings.OpenImageUnsupported, Strings.AppName, Strings.OK);
                }
            }

            return null;
        }

        public async Task<bool> SetPhotoAsync(INavigationService navigation, long? chatId, bool isPublic, bool isPersonal)
        {
            var inputPhoto = await PreviewSetPhotoAsync(navigation);
            if (inputPhoto != null)
            {
                return await Complete(navigation, chatId, isPublic, isPersonal, inputPhoto);
            }

            return false;
        }

        public Task<InputChatPhoto> PreviewCreatePhotoAsync(INavigationService navigation)
        {
            // CreateChatPhotoPopup (draw an avatar out of a sticker and a gradient) is outside the
            // subset: it needs the sticker drawers and CreateChatPhotoViewModel.
            Logger.Info("CreateChatPhotoPopup is not available on Linux");
            return Task.FromResult<InputChatPhoto>(null);
        }

        public Task<bool> CreatePhotoAsync(INavigationService navigation, long? chatId, bool isPublic, bool isPersonal)
        {
            Logger.Info("CreateChatPhotoPopup is not available on Linux");
            return Task.FromResult(false);
        }

        /// <summary>
        /// Verbatim from the shared service: which of the five TDLib setters a photo goes to
        /// depends on whether the target is a chat, a bot, a contact or ourselves.
        /// </summary>
        private async Task<bool> Complete(INavigationService navigation, long? chatId, bool isPublic, bool isPersonal, InputChatPhoto inputPhoto)
        {
            if (chatId.HasValue && _clientService.TryGetUser(chatId.Value, out User user))
            {
                if (user.Type is UserTypeBot userTypeBot && userTypeBot.CanBeEdited)
                {
                    _clientService.Send(new SetBotProfilePhoto(user.Id, inputPhoto));
                    return true;
                }
                else if (isPersonal)
                {
                    var confirm = await navigation.ShowPopupAsync(string.Format(Strings.SetUserPhotoAlertMessage, user.FirstName, user.FirstName), Strings.AppName, Strings.SetPhoto, Strings.Cancel);
                    if (confirm == ContentDialogResult.Primary)
                    {
                        _clientService.Send(new SetUserPersonalProfilePhoto(user.Id, inputPhoto));
                        return true;
                    }
                }
                else
                {
                    var confirm = await navigation.ShowPopupAsync(string.Format(Strings.SuggestPhotoAlertMessage, user.FirstName), Strings.AppName, Strings.SuggestPhotoShort, Strings.Cancel);
                    if (confirm == ContentDialogResult.Primary)
                    {
                        _clientService.Send(new SuggestUserProfilePhoto(user.Id, inputPhoto));
                        return true;
                    }
                }
            }
            else if (chatId.HasValue)
            {
                _clientService.Send(new SetChatPhoto(chatId.Value, inputPhoto));
                return true;
            }
            else
            {
                _clientService.Send(new SetProfilePhoto(inputPhoto, isPublic));
                return true;
            }

            return false;
        }

        /// <summary>
        /// The avatar the two Create popups show while the chat does not exist yet.
        ///
        /// Upstream never fills <c>NewGroupViewModel.Preview</c>/<c>NewChannelViewModel.Preview</c>
        /// (<c>ChoosePhoto</c> only stores the InputChatPhoto), so on Windows picking a photo
        /// changes nothing on screen. Here it has to, or the button reads as dead.
        ///
        /// The stream overload and not <c>new BitmapImage(new Uri(path))</c>: SetSourceAsync is
        /// the path this port already uses (see Native/Image/PixelBitmap.cs), and a bare Unix path
        /// is not a valid absolute Uri anyway.
        /// </summary>
        public static BitmapImage CreatePreview(InputChatPhoto photo)
        {
            if (photo is InputChatPhotoStatic staticPhoto && staticPhoto.Photo is InputFileLocal local && !string.IsNullOrEmpty(local.Path))
            {
                var bitmap = new BitmapImage();
                _ = LoadPreviewAsync(bitmap, local.Path);

                return bitmap;
            }

            return null;
        }

        private static async Task LoadPreviewAsync(BitmapImage bitmap, string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();

                await bitmap.SetSourceAsync(stream);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }
}
