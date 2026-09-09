//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Navigation.Services;
using Telegram.Td.Api;
using Windows.Devices.Geolocation;

namespace Telegram.Services
{
    // Copied from Telegram/Services/LocationService.cs, whose implementation needs
    // ExtendedExecution for live locations; keep in sync with it.
    public readonly struct GetVenuesResult
    {
        public string NextOffset { get; }

        public List<Venue> Venues { get; }

        public GetVenuesResult(string offset, List<Venue> venues)
        {
            NextOffset = offset;
            Venues = venues;
        }
    }

    public interface ILocationService
    {
        Task<Geolocator> StartTrackingAsync();
        void StopTracking();

        Task<Location> GetPositionAsync(INavigationService navigation);

        Task<GetVenuesResult> GetVenuesAsync(long chatId, double latitude, double longitude, string query = null, string offset = null);
    }

    /// <summary>
    /// The Linux LocationService: no geolocation (Uno has no provider on X11), so tracking and
    /// the current position always come back empty. Venue search is a TDLib request and works.
    /// </summary>
    public partial class LocationService : ILocationService
    {
        private readonly IClientService _clientService;

        public LocationService(IClientService clientService)
        {
            _clientService = clientService;
        }

        public Task<Geolocator> StartTrackingAsync()
        {
            return Task.FromResult<Geolocator>(null);
        }

        public void StopTracking()
        {
        }

        public Task<Location> GetPositionAsync(INavigationService navigation)
        {
            return Task.FromResult<Location>(null);
        }

        public async Task<GetVenuesResult> GetVenuesAsync(long chatId, double latitude, double longitude, string query = null, string offset = null)
        {
            var results = new List<Venue>();

            var option = _clientService.Options.VenueSearchBotUsername;
            if (string.IsNullOrEmpty(option))
            {
                return new GetVenuesResult(null, results);
            }

            var chat = await _clientService.SendAsync(new SearchPublicChat(option)) as Chat;
            if (chat == null)
            {
                return new GetVenuesResult(null, results);
            }

            var user = _clientService.GetUser(chat);
            if (user == null)
            {
                return new GetVenuesResult(null, results);
            }

            var response = await _clientService.SendAsync(new GetInlineQueryResults(user.Id, chatId, new Location(latitude, longitude, 0), query ?? string.Empty, offset ?? string.Empty));
            if (response is InlineQueryResults inlineResults)
            {
                foreach (var item in inlineResults.Results)
                {
                    if (item is InlineQueryResultVenue venue)
                    {
                        results.Add(venue.Venue);
                    }
                }

                return new GetVenuesResult(inlineResults.NextOffset, results);
            }

            return new GetVenuesResult(null, results);
        }
    }
}
