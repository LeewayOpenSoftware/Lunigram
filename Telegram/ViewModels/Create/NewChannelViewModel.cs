//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.Views.Supergroups;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Telegram.ViewModels.Create
{
    public partial class NewChannelViewModel : ViewModelBase
    {
        private readonly IProfilePhotoService _profilePhotoService;

        public NewChannelViewModel(IClientService clientService, ISettingsService settingsService, IEventAggregator aggregator, IProfilePhotoService profilePhotoService)
            : base(clientService, settingsService, aggregator)
        {
            _profilePhotoService = profilePhotoService;
        }

        private string _title;
        public string Title
        {
            get => _title;
            set
            {
                if (Set(ref _title, value))
                {
                    RaisePropertyChanged(nameof(CanCreate));
                }
            }
        }

        private string _about;
        public string About
        {
            get => _about;
            set => Set(ref _about, value);
        }

        private InputChatPhoto _inputPhoto;

        private BitmapImage _preview;
        public BitmapImage Preview
        {
            get => _preview;
            set => Set(ref _preview, value);
        }

        public bool CanCreate => !string.IsNullOrWhiteSpace(Title);

        public async void Create()
        {
            var response = await ClientService.SendAsync(new CreateNewSupergroupChat(_title, false, true, _about ?? string.Empty, null, 0, false));
            if (response is Chat chat)
            {
                if (_inputPhoto != null)
                {
                    ClientService.Send(new SetChatPhoto(chat.Id, _inputPhoto));
                }

#if LINUX
                // SupergroupEditTypePage - where upstream sends you next to pick public/private
                // and claim a username - is outside the subset: it needs PrefixTextBox (whose
                // Generic.xaml style is win:-only), UsernameInfoCell, SettingsUsernameViewModel's
                // UsernameInfo, drag-to-reorder and two x:Load inside a ListView header. The
                // channel is already created and private at this point, so open it.
                //
                // Reviewed 2026-08-27 and left as it is. The missing step is a whole screen, and
                // half of it would be worse than none: a "channel type" page that cannot claim a
                // username is a page that lies. What this costs is one step, not the channel --
                // CreateNewSupergroupChat has already returned a working private channel, which is
                // also what a user gets on Windows by dismissing that page. It comes back with
                // SupergroupEditTypePage.
                NavigationService.NavigateToChat(chat.Id);
                NavigationService.GoBackAt(0, false);
#else
                NavigationService.Navigate(typeof(SupergroupEditTypePage), new SupergroupEditTypeArgs(chat.Id, true));
                NavigationService.GoBackAt(0, false);
#endif
            }
            else if (response is Error error)
            {
                if (error.MessageEquals(ErrorType.CHANNELS_TOO_MUCH))
                {
                    NavigationService.ShowLimitReached(new PremiumLimitTypeSupergroupCount());
                }
                else
                {
                    ShowToast(error);
                }
            }
        }

        public async void ChoosePhoto()
        {
            _inputPhoto = await _profilePhotoService.PreviewSetPhotoAsync(NavigationService);
#if LINUX
            // See NewGroupViewModel.ChoosePhoto.
            Preview = ProfilePhotoService.CreatePreview(_inputPhoto);
#endif
        }
    }
}
