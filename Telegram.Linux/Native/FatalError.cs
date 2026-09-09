//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;

namespace Telegram.Native
{
    public struct FatalErrorFrame
    {
        public long NativeIP;
        public long NativeImageBase;
    }

    public sealed partial class FatalError
    {
        public FatalError(string type, string message, string stackTrace, IList<FatalErrorFrame> frames)
        {
            Type = type;
            Message = message;
            StackTrace = stackTrace;
            Frames = frames ?? new List<FatalErrorFrame>();
        }

        public string Type { get; set; }

        public string Message { get; set; }

        public string StackTrace { get; set; }

        public IList<FatalErrorFrame> Frames { get; }

        public FatalError InnerException { get; set; }
    }
}
