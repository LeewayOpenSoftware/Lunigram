//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Windows.UI.Core;

namespace Telegram.Native
{
    public delegate void CollectCallback();

    public static class GarbageCollectionMonitor
    {
        public static void Initialize(CollectCallback collectCallback, bool disableGcCollect, bool disablePressure)
        {
        }

        public static void StartMonitoring(CoreWindow window)
        {
        }

        public static void StopMonitoring(CoreWindow window)
        {
        }

        public static void DisconnectUnusedReferenceSources()
        {
        }

        public static string Debug()
        {
            return string.Empty;
        }
    }
}
