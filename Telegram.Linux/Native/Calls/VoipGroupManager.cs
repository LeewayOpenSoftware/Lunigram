//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.Foundation;

namespace Telegram.Native.Calls
{
    /// <summary>
    /// Group video chat and live stream, over <c>tgcalls::GroupInstanceCustomImpl</c>.
    ///
    /// The asynchronous requests (current broadcast time, audio/video broadcast parts, media
    /// channel descriptions) reach C# as a token, not as a continuation: a <c>std::function</c>
    /// cannot cross a flat C ABI. The token is wrapped back into the same <c>Deferral</c>
    /// delegates the Windows component exposes, so the shared call page does not notice the
    /// difference -- it still gets an event args object with a <c>Deferral</c> to invoke when
    /// TDLib answers.
    ///
    /// Noise suppression is what pulls rnnoise in, and rnnoise is in this repo
    /// (Telegram.Native.Calls/rnnoise, the same copy the Windows .vcxproj compiles), so group
    /// chats are built, not left out.
    ///
    /// Every event is raised from a WebRTC thread. Subscribers must dispatch.
    /// </summary>
    public sealed unsafe partial class VoipGroupManager : IDisposable
    {
        private IntPtr _handle;
        private GCHandle _self;
        private bool _isMuted;
        private bool _isNoiseSuppressionEnabled;
        private EncryptGroupCallDataDelegate _encrypt;
        private DecryptGroupCallDataDelegate _decrypt;
        private readonly List<VoipVideoOutputSink> _sinks = new();
        private EmitJsonPayloadDelegate _joinPayload;

        public VoipGroupManager(VoipGroupDescriptor descriptor)
        {
            _isNoiseSuppressionEnabled = descriptor?.IsNoiseSuppressionEnabled ?? false;

            if (!UnigramCalls.IsAvailable || descriptor == null)
            {
                return;
            }

            _self = GCHandle.Alloc(this, GCHandleType.Normal);

            var input = Marshal.StringToCoTaskMemUTF8(descriptor.AudioInputId ?? string.Empty);
            var output = Marshal.StringToCoTaskMemUTF8(descriptor.AudioOutputId ?? string.Empty);

            try
            {
                var config = new UnigramCalls.GroupConfig
                {
                    AudioInputId = input,
                    AudioOutputId = output,
                    LogPath = IntPtr.Zero,
                    VideoContentType = (int)descriptor.VideoContentType,
                    IsConference = descriptor.IsConference ? 1 : 0,
                    NoiseSuppression = _isNoiseSuppressionEnabled ? 1 : 0,
                    VideoCapture = descriptor.VideoCapture?.Handle ?? IntPtr.Zero
                };

                var events = new UnigramCalls.GroupEvents
                {
                    NetworkStateUpdated = &OnNetworkStateUpdated,
                    AudioLevelsUpdated = &OnAudioLevelsUpdated,
                    BroadcastTimeRequested = &OnBroadcastTimeRequested,
                    AudioBroadcastPartRequested = &OnAudioBroadcastPartRequested,
                    VideoBroadcastPartRequested = &OnVideoBroadcastPartRequested,
                    MediaChannelDescriptionsRequested = &OnMediaChannelDescriptionsRequested,
                    EncryptDecrypt = &OnEncryptDecrypt
                };

                _handle = UnigramCalls.unigram_voip_group_create(config, events, GCHandle.ToIntPtr(_self));
            }
            finally
            {
                Marshal.FreeCoTaskMem(input);
                Marshal.FreeCoTaskMem(output);
            }

            if (_handle == IntPtr.Zero)
            {
                _self.Free();
            }
        }

        public void Stop()
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_group_stop(_handle);
            }
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_group_release(handle);
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }

            _sinks.Clear();
        }

        public void SetConnectionMode(VoipGroupConnectionMode connectionMode, bool keepBroadcastIfWasEnabled, bool isUnifiedBroadcast)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_group_set_connection_mode(_handle, (int)connectionMode,
                    keepBroadcastIfWasEnabled ? 1 : 0, isUnifiedBroadcast ? 1 : 0);
            }
        }

        /// <summary>
        /// Asks tgcalls for the join payload. The completion fires once, on a WebRTC thread.
        /// Only one request can be outstanding: a second call replaces the first handler, which
        /// matches how the shared code uses it (one join per manager).
        /// </summary>
        public void EmitJoinPayload(EmitJsonPayloadDelegate completion)
        {
            if (_handle == IntPtr.Zero || completion == null)
            {
                return;
            }

            _joinPayload = completion;
            UnigramCalls.unigram_voip_group_emit_join_payload(_handle, &OnJoinPayload, GCHandle.ToIntPtr(_self));
        }

        public void SetJoinResponsePayload(string payload)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_group_set_join_response_payload(_handle, payload ?? string.Empty);
            }
        }

        public void RemoveSsrcs(IList<int> ssrcs)
        {
            if (_handle == IntPtr.Zero || ssrcs == null || ssrcs.Count == 0)
            {
                return;
            }

            var values = new uint[ssrcs.Count];
            for (int i = 0; i < ssrcs.Count; i++)
            {
                values[i] = unchecked((uint)ssrcs[i]);
            }

            UnigramCalls.unigram_voip_group_remove_ssrcs(_handle, values, values.Length);
        }

        public void AddIncomingVideoOutput(string endpointId, VoipVideoOutputSink sink)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            if (sink != null)
            {
                // Held on this side too: tgcalls only keeps a weak reference, and the native
                // side holds one as well, but the managed object has to outlive both.
                _sinks.Add(sink);
            }

            UnigramCalls.unigram_voip_group_add_incoming_video_output(_handle, endpointId ?? string.Empty,
                sink?.Handle ?? IntPtr.Zero);
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                if (_handle != IntPtr.Zero)
                {
                    UnigramCalls.unigram_voip_group_set_muted(_handle, value ? 1 : 0);
                }
            }
        }

        public bool IsNoiseSuppressionEnabled
        {
            get => _isNoiseSuppressionEnabled;
            set
            {
                _isNoiseSuppressionEnabled = value;
                if (_handle != IntPtr.Zero)
                {
                    UnigramCalls.unigram_voip_group_set_noise_suppression(_handle, value ? 1 : 0);
                }
            }
        }

        public void SetAudioOutputDevice(string id)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_group_set_audio_output_device(_handle, id ?? string.Empty);
            }
        }

        public void SetAudioInputDevice(string id)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_group_set_audio_input_device(_handle, id ?? string.Empty);
            }
        }

        public void SetVideoCapture(VoipCaptureBase videoCapture)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_group_set_video_capture(_handle, videoCapture?.Handle ?? IntPtr.Zero);
            }
        }

        public void SetVolume(int ssrc, double volume)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_group_set_volume(_handle, unchecked((uint)ssrc), volume);
            }
        }

        public void SetRequestedVideoChannels(IList<VoipVideoChannelInfo> descriptions)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            if (descriptions == null || descriptions.Count == 0)
            {
                UnigramCalls.unigram_voip_group_set_requested_video_channels(_handle, Array.Empty<UnigramCalls.GroupVideoChannel>(), 0);
                return;
            }

            var strings = new List<IntPtr>();
            var channels = new UnigramCalls.GroupVideoChannel[descriptions.Count];

            try
            {
                for (int i = 0; i < descriptions.Count; i++)
                {
                    var description = descriptions[i];

                    // The ssrc groups are flattened to "semantics ssrc ssrc;semantics ssrc;":
                    // a nested array would need a second level of marshalling for something the
                    // native side immediately turns back into strings.
                    var packed = new StringBuilder();
                    if (description.SourceGroups != null)
                    {
                        foreach (var group in description.SourceGroups)
                        {
                            packed.Append(group.Semantics);
                            if (group.SourceIds != null)
                            {
                                foreach (var id in group.SourceIds)
                                {
                                    packed.Append(' ').Append(unchecked((uint)id));
                                }
                            }
                            packed.Append(';');
                        }
                    }

                    var endpoint = Marshal.StringToCoTaskMemUTF8(description.EndpointId ?? string.Empty);
                    var groups = Marshal.StringToCoTaskMemUTF8(packed.ToString());
                    strings.Add(endpoint);
                    strings.Add(groups);

                    channels[i] = new UnigramCalls.GroupVideoChannel
                    {
                        AudioSource = unchecked((uint)description.AudioSource),
                        ParticipantId = description.ParticipantId,
                        EndpointId = endpoint,
                        SourceGroups = groups,
                        MinQuality = (int)description.MinQuality,
                        MaxQuality = (int)description.MaxQuality
                    };
                }

                UnigramCalls.unigram_voip_group_set_requested_video_channels(_handle, channels, channels.Length);
            }
            finally
            {
                foreach (var pointer in strings)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }

        /// <summary>
        /// End-to-end encryption hooks for conference calls. Both are called synchronously from
        /// a WebRTC thread and must return promptly: the native side is holding the frame.
        /// </summary>
        public void SetEncryptDecrypt(EncryptGroupCallDataDelegate encryptData, DecryptGroupCallDataDelegate decryptData)
        {
            _encrypt = encryptData;
            _decrypt = decryptData;
        }

        public event TypedEventHandler<VoipGroupManager, GroupNetworkStateChangedEventArgs> NetworkStateUpdated;
        public event TypedEventHandler<VoipGroupManager, IList<VoipGroupParticipant>> AudioLevelsUpdated;
        public event TypedEventHandler<VoipGroupManager, BroadcastTimeRequestedEventArgs> BroadcastTimeRequested;
        public event TypedEventHandler<VoipGroupManager, AudioBroadcastPartRequestedEventArgs> AudioBroadcastPartRequested;
        public event TypedEventHandler<VoipGroupManager, VideoBroadcastPartRequestedEventArgs> VideoBroadcastPartRequested;
        public event TypedEventHandler<VoipGroupManager, MediaChannelDescriptionsRequestedEventArgs> MediaChannelDescriptionsRequested;

        // ---------------------------------------------------------------- callbacks

        private static VoipGroupManager Resolve(IntPtr user)
        {
            return GCHandle.FromIntPtr(user).Target as VoipGroupManager;
        }

        [UnmanagedCallersOnly]
        private static void OnJoinPayload(IntPtr user, uint ssrc, IntPtr json)
        {
            try
            {
                var group = Resolve(user);
                var handler = group?._joinPayload;
                handler?.Invoke(unchecked((int)ssrc), Marshal.PtrToStringUTF8(json) ?? string.Empty);
            }
            catch (Exception ex) { Logger.Error(nameof(OnJoinPayload), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnNetworkStateUpdated(IntPtr user, int isConnected, int isTransitioning)
        {
            try
            {
                var group = Resolve(user);
                group?.NetworkStateUpdated?.Invoke(group,
                    new GroupNetworkStateChangedEventArgs(isConnected != 0, isTransitioning != 0));
            }
            catch (Exception ex) { Logger.Error(nameof(OnNetworkStateUpdated), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnAudioLevelsUpdated(IntPtr user, UnigramCalls.GroupLevel* levels, int count)
        {
            try
            {
                var group = Resolve(user);
                if (group?.AudioLevelsUpdated == null)
                {
                    return;
                }

                var result = new List<VoipGroupParticipant>(count);
                for (int i = 0; i < count; i++)
                {
                    result.Add(new VoipGroupParticipant
                    {
                        AudioSource = unchecked((int)levels[i].AudioSource),
                        Level = levels[i].Level,
                        IsSpeaking = levels[i].IsSpeaking != 0,
                        IsMuted = levels[i].IsMuted != 0
                    });
                }

                group.AudioLevelsUpdated.Invoke(group, result);
            }
            catch (Exception ex) { Logger.Error(nameof(OnAudioLevelsUpdated), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnBroadcastTimeRequested(IntPtr user, long token)
        {
            try
            {
                var group = Resolve(user);
                if (group?.BroadcastTimeRequested == null)
                {
                    return;
                }

                var handle = group._handle;
                group.BroadcastTimeRequested.Invoke(group, new BroadcastTimeRequestedEventArgs(
                    time => UnigramCalls.unigram_voip_group_broadcast_time_respond(handle, token, time)));
            }
            catch (Exception ex) { Logger.Error(nameof(OnBroadcastTimeRequested), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnAudioBroadcastPartRequested(IntPtr user, int scale, long time, long token)
        {
            try
            {
                var group = Resolve(user);
                if (group?.AudioBroadcastPartRequested == null)
                {
                    return;
                }

                group.AudioBroadcastPartRequested.Invoke(group,
                    new AudioBroadcastPartRequestedEventArgs(scale, time, group.MakeDeferral(token)));
            }
            catch (Exception ex) { Logger.Error(nameof(OnAudioBroadcastPartRequested), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnVideoBroadcastPartRequested(IntPtr user, int scale, long time, int channel, int quality, long token)
        {
            try
            {
                var group = Resolve(user);
                if (group?.VideoBroadcastPartRequested == null)
                {
                    return;
                }

                group.VideoBroadcastPartRequested.Invoke(group, new VideoBroadcastPartRequestedEventArgs(
                    scale, time, channel, (VoipVideoChannelQuality)quality, group.MakeDeferral(token)));
            }
            catch (Exception ex) { Logger.Error(nameof(OnVideoBroadcastPartRequested), ex); }
        }

        /// <summary>
        /// Wraps a native token into the deferral shape the Windows component exposes. The
        /// <c>response</c> parameter is the response timestamp the caller got from TDLib; a null
        /// or empty payload is reported as "not ready", which is what makes tgcalls retry rather
        /// than resync.
        /// </summary>
        private BroadcastPartRequestedDeferral MakeDeferral(long token)
        {
            var handle = _handle;
            return (time, response, data) =>
            {
                byte[] buffer;
                if (data == null || data.Count == 0)
                {
                    buffer = Array.Empty<byte>();
                }
                else if (data is byte[] array)
                {
                    buffer = array;
                }
                else
                {
                    buffer = new byte[data.Count];
                    data.CopyTo(buffer, 0);
                }

                // 0 = Success, 1 = NotReady (tgcalls::BroadcastPart::Status).
                var status = buffer.Length > 0 ? 0 : 1;
                UnigramCalls.unigram_voip_group_broadcast_part_respond(handle, token, time, status, buffer, buffer.Length);
            };
        }

        [UnmanagedCallersOnly]
        private static void OnMediaChannelDescriptionsRequested(IntPtr user, uint* ssrcs, int count, long token)
        {
            try
            {
                var group = Resolve(user);
                if (group?.MediaChannelDescriptionsRequested == null)
                {
                    return;
                }

                var sources = new List<uint>(count);
                for (int i = 0; i < count; i++)
                {
                    sources.Add(ssrcs[i]);
                }

                var handle = group._handle;
                group.MediaChannelDescriptionsRequested.Invoke(group, new MediaChannelDescriptionsRequestedEventArgs(
                    sources,
                    participants =>
                    {
                        var audioSources = new uint[participants?.Count ?? 0];
                        var userIds = new long[audioSources.Length];
                        for (int i = 0; i < audioSources.Length; i++)
                        {
                            audioSources[i] = unchecked((uint)participants[i].AudioSource);
                            userIds[i] = participants[i].UserId;
                        }

                        UnigramCalls.unigram_voip_group_media_channels_respond(handle, token, audioSources, userIds, audioSources.Length);
                    }));
            }
            catch (Exception ex) { Logger.Error(nameof(OnMediaChannelDescriptionsRequested), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnEncryptDecrypt(IntPtr user, byte* data, int length, long userId, int encrypt, int channelId)
        {
            try
            {
                var group = Resolve(user);
                if (group == null)
                {
                    return;
                }

                var input = new byte[length];
                if (length > 0)
                {
                    Marshal.Copy((IntPtr)data, input, 0, length);
                }

                var result = encrypt != 0
                    ? group._encrypt?.Invoke((VoipDataChannel)channelId, input, 0)
                    : group._decrypt?.Invoke(userId, input);

                // Must be written back from inside this callback: the native side only keeps
                // somewhere to put it for the duration of the call.
                UnigramCalls.unigram_voip_group_encrypt_result(group._handle, result ?? Array.Empty<byte>(), result?.Length ?? 0);
            }
            catch (Exception ex) { Logger.Error(nameof(OnEncryptDecrypt), ex); }
        }
    }
}
