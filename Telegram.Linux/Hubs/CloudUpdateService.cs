//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Threading.Tasks;

namespace Telegram.Services
{
    /// <summary>
    /// The Linux CloudUpdateService. Updates come from the distro package or the build, not from
    /// the update channel, so there is never a pending one.
    /// </summary>
    public partial class CloudUpdateService : ICloudUpdateService
    {
        public CloudUpdate NextUpdate => null;

        public Task UpdateAsync(bool force)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Windows hands the pending update to the installer; here there is nothing to launch.
        /// </summary>
        public static Task<bool> LaunchAsync(bool checkAvailability)
        {
            return Task.FromResult(false);
        }
    }
}
