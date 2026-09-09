//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Threading.Tasks;

namespace Telegram.Services
{
    // Copied from Telegram/Services/ContactsService.cs, whose implementation fills the Windows
    // jump list; keep in sync with it.
    public interface IContactsService
    {
        Task JumpListAsync();
    }

    /// <summary>
    /// The Linux ContactsService. There is no jump list on Linux, so only the option resets the
    /// Windows build sends before rebuilding it are kept.
    /// </summary>
    public partial class ContactsService : IContactsService
    {
        private readonly IClientService _clientService;
        private readonly ISettingsService _settingsService;
        private readonly IEventAggregator _aggregator;

        public ContactsService(IClientService clientService, ISettingsService settingsService, IEventAggregator aggregator)
        {
            _clientService = clientService;
            _settingsService = settingsService;
            _aggregator = aggregator;
        }

        public Task JumpListAsync()
        {
            _clientService.Send(new Td.Api.SetOption("x_user_data_account", new Td.Api.OptionValueEmpty()));
            _clientService.Send(new Td.Api.SetOption("x_contact_list", new Td.Api.OptionValueEmpty()));
            _clientService.Send(new Td.Api.SetOption("x_annotation_list", new Td.Api.OptionValueEmpty()));

            return Task.CompletedTask;
        }
    }
}
