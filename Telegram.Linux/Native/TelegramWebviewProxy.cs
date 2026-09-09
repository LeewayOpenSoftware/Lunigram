//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

namespace Telegram.Native
{
    public delegate void TelegramWebviewProxyDelegate(string eventName, string eventData);

    public sealed partial class TelegramWebviewProxy
    {
        private readonly TelegramWebviewProxyDelegate _delegate;

        public TelegramWebviewProxy(TelegramWebviewProxyDelegate delegato)
        {
            _delegate = delegato;
        }

        public void PostEvent(string eventName, string eventData)
        {
            _delegate?.Invoke(eventName, eventData);
        }
    }
}
