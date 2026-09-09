//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

namespace Telegram.Native.Calls
{
    public enum VoipDataSaving
    {
        Never,
        Mobile,
        Always
    }

    public enum VoipReadyState
    {
        WaitInit = 0,
        WaitInitAck = 1,
        Established = 2,
        Failed = 3,
        Reconnecting = 4
    }

    public enum VoipAudioState
    {
        Muted = 0,
        Active = 1
    }

    public enum VoipVideoState
    {
        Inactive = 0,
        Paused = 1,
        Active = 2
    }

    public enum VoipGroupConnectionMode
    {
        None = 0,
        Rtc = 1,
        Broadcast = 2
    }

    public enum VoipVideoContentType
    {
        None = 0,
        Screencast = 1,
        Generic = 2
    }

    public enum VoipVideoChannelQuality
    {
        Thumbnail = 0,
        Medium = 1,
        Full = 2
    }

    public enum VoipDataChannel
    {
        Main = 0,
        ScreenSharing = 1
    }

    public struct VoipGroupParticipant
    {
        public int AudioSource;
        public float Level;
        public bool IsSpeaking;
        public bool IsMuted;
    }

    public struct VoipMediaChannelDescription
    {
        public int AudioSource;
        public long UserId;
    }
}
