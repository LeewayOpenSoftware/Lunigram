//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Windows.Foundation;

namespace Telegram.Native
{
    public sealed partial class HttpProxyWatcher : IDisposable
    {
        private static HttpProxyWatcher _current;

        public static HttpProxyWatcher Current => _current ??= new HttpProxyWatcher();

        public HttpProxyWatcher()
        {
        }

        public string Server => null;

        public bool IsEnabled => false;

        public event TypedEventHandler<HttpProxyWatcher, bool> Changed;

        public void Dispose()
        {
        }
    }
}
