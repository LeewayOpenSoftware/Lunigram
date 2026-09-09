//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;

namespace Telegram.Native.Calls
{
    public delegate void SignalingDataEmittedDelegate(byte[] data);

    public delegate void EmitJsonPayloadDelegate(int ssrc, string payload);

    public delegate byte[] EncryptGroupCallDataDelegate(VoipDataChannel dataChannel, byte[] data, int unencryptedPrefixSize);

    public delegate byte[] DecryptGroupCallDataDelegate(long userId, byte[] data);

    public delegate void BroadcastPartRequestedDeferral(long time, long response, IList<byte> data);

    public delegate void BroadcastTimeRequestedDeferral(long time);

    public delegate void MediaChannelDescriptionsRequestedDeferral(IList<VoipMediaChannelDescription> participants);

    public sealed partial class RemoteMediaStateUpdatedEventArgs
    {
        internal RemoteMediaStateUpdatedEventArgs(VoipAudioState audio, VoipVideoState video)
        {
            Audio = audio;
            Video = video;
        }

        public VoipAudioState Audio { get; }

        public VoipVideoState Video { get; }
    }

    public sealed partial class GroupNetworkStateChangedEventArgs
    {
        internal GroupNetworkStateChangedEventArgs(bool isConnected, bool isTransitioning)
        {
            IsConnected = isConnected;
            IsTransitioningFromBroadcastToRtc = isTransitioning;
        }

        public bool IsConnected { get; }

        public bool IsTransitioningFromBroadcastToRtc { get; }
    }

    public sealed partial class BroadcastTimeRequestedEventArgs
    {
        internal BroadcastTimeRequestedEventArgs(BroadcastTimeRequestedDeferral deferral)
        {
            Deferral = deferral;
        }

        public BroadcastTimeRequestedDeferral Deferral { get; }
    }

    public sealed partial class AudioBroadcastPartRequestedEventArgs
    {
        internal AudioBroadcastPartRequestedEventArgs(int scale, long time, BroadcastPartRequestedDeferral deferral)
        {
            Scale = scale;
            Time = time;
            Deferral = deferral;
        }

        public int Scale { get; }

        public long Time { get; }

        public BroadcastPartRequestedDeferral Deferral { get; }
    }

    public sealed partial class VideoBroadcastPartRequestedEventArgs
    {
        internal VideoBroadcastPartRequestedEventArgs(int scale, long time, int channelId,
            VoipVideoChannelQuality videoQuality, BroadcastPartRequestedDeferral deferral)
        {
            Scale = scale;
            Time = time;
            ChannelId = channelId;
            VideoQuality = videoQuality;
            Deferral = deferral;
        }

        public int Scale { get; }

        public long Time { get; }

        public int ChannelId { get; }

        public VoipVideoChannelQuality VideoQuality { get; }

        public BroadcastPartRequestedDeferral Deferral { get; }
    }

    public sealed partial class MediaChannelDescriptionsRequestedEventArgs
    {
        internal MediaChannelDescriptionsRequestedEventArgs(IList<uint> audioSourceIds,
            MediaChannelDescriptionsRequestedDeferral deferral)
        {
            AudioSourceIds = audioSourceIds ?? new List<uint>();
            Deferral = deferral;
        }

        public IList<uint> AudioSourceIds { get; } = new List<uint>();

        public MediaChannelDescriptionsRequestedDeferral Deferral { get; }
    }

    public sealed partial class FrameReceivedEventArgs
    {
        internal FrameReceivedEventArgs(int pixelWidth, int pixelHeight)
        {
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
        }

        public int PixelWidth { get; }

        public int PixelHeight { get; }
    }

    public sealed partial class VoipVideoSourceGroup
    {
        public VoipVideoSourceGroup(string semantics, IReadOnlyList<int> sourceIds)
        {
            Semantics = semantics;
            SourceIds = sourceIds;
        }

        public string Semantics { get; set; }

        public IReadOnlyList<int> SourceIds { get; set; }
    }

    public sealed partial class VoipVideoChannelInfo
    {
        public VoipVideoChannelInfo(int audioSource, long participantId, string endpointId, IList<VoipVideoSourceGroup> sourceGroups, VoipVideoChannelQuality minQuality, VoipVideoChannelQuality maxQuality)
        {
            AudioSource = audioSource;
            ParticipantId = participantId;
            EndpointId = endpointId;
            SourceGroups = sourceGroups;
            MinQuality = minQuality;
            MaxQuality = maxQuality;
        }

        public int AudioSource { get; }

        public long ParticipantId { get; }

        public string EndpointId { get; }

        public IList<VoipVideoSourceGroup> SourceGroups { get; }

        public VoipVideoChannelQuality MinQuality { get; }

        public VoipVideoChannelQuality MaxQuality { get; }
    }
}
