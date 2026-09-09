//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation;

namespace Telegram.Native.Calls
{
    /// <summary>
    /// One-to-one call. The Linux counterpart of the <c>VoipManager</c> WinRT runtime class,
    /// over the flat C ABI of libunigram-calls.so.
    ///
    /// Every event is raised from a WebRTC thread (network, worker or media), never from the UI
    /// thread. That was already true on Windows -- the WinRT version marshalled through the
    /// event's own dispatcher -- so subscribers that were written against it already dispatch;
    /// this class does not add a dispatcher of its own because it has no window to dispatch to.
    ///
    /// Without the native library every member is an inert no-op and <see cref="IsAvailable"/>
    /// is false, which is exactly the behaviour the phase 1 stub had.
    /// </summary>
    public sealed unsafe partial class VoipManager : IDisposable
    {
        private static VoipCallProtocol _protocol;

        /// <summary>
        /// Protocol versions announced to the server. Taken from the native tgcalls registry
        /// when it is there (so the list cannot drift from what
        /// <c>tgcalls::Meta::Create</c> can actually build) and from the hard-coded Windows list
        /// when it is not.
        /// </summary>
        public static VoipCallProtocol Protocol
        {
            get
            {
                if (_protocol != null)
                {
                    return _protocol;
                }

                if (UnigramCalls.IsAvailable)
                {
                    var versions = UnigramCalls.Versions;
                    if (!string.IsNullOrEmpty(versions))
                    {
                        return _protocol = new VoipCallProtocol(true, true, 65, UnigramCalls.MaxLayer,
                            new List<string>(versions.Split(',', StringSplitOptions.RemoveEmptyEntries)));
                    }
                }

                return _protocol = new VoipCallProtocol(true, true, 65, 92,
                    new List<string> { "2.4.4", "2.7.7", "5.0.0", "6.0.0", "7.0.0", "8.0.0", "9.0.0", "10.0.0", "11.0.0" });
            }
        }

        public static bool IsAvailable => UnigramCalls.IsAvailable;

        /// <summary>
        /// Audio devices as (id, name). The id is the <c>#index</c> form, which is the only one
        /// tgcalls can resolve on Linux: tg_owt's PulseAudio backend leaves the guid empty, so
        /// <c>SetAudioInputDeviceById</c>'s guid branch never matches. Feed these ids straight
        /// back to <see cref="SetAudioInputDevice"/> / <see cref="SetAudioOutputDevice"/>.
        /// </summary>
        public static (string Id, string Name)[] GetAudioDevices(bool input)
        {
            return UnigramCalls.ReadTable((buffer, length) =>
            {
                var count = UnigramCalls.unigram_voip_audio_devices(input ? 1 : 0, buffer, length, out var needed);
                return (count, needed);
            });
        }

        private IntPtr _handle;
        private GCHandle _self;
        private bool _isMuted;
        private SignalingDataEmittedDelegate _signalingHandler;
        private VoipVideoOutputSink _incomingVideoOutput;

        public VoipManager()
        {
            if (!UnigramCalls.IsAvailable)
            {
                return;
            }

            _self = GCHandle.Alloc(this, GCHandleType.Normal);

            var events = new UnigramCalls.Events
            {
                StateUpdated = &OnStateUpdated,
                SignalBarsUpdated = &OnSignalBarsUpdated,
                AudioLevelUpdated = &OnAudioLevelUpdated,
                RemoteBatteryLowUpdated = &OnRemoteBatteryLowUpdated,
                RemoteMediaStateUpdated = &OnRemoteMediaStateUpdated,
                RemotePreferredAspectRatioUpdated = &OnRemotePreferredAspectRatioUpdated,
                SignalingDataEmitted = &OnSignalingDataEmitted
            };

            _handle = UnigramCalls.unigram_voip_manager_create(events, GCHandle.ToIntPtr(_self));

            if (_handle == IntPtr.Zero)
            {
                _self.Free();
            }
        }

        /// <summary>
        /// Builds the native descriptor and starts the call. Keeps a call that is already
        /// running rather than replacing it -- the native side enforces that too, for the same
        /// reason the Windows version documents: replacing the instance would destroy the
        /// previous one without tgcalls' own teardown.
        /// </summary>
        public void Start(VoipDescriptor descriptor)
        {
            if (_handle == IntPtr.Zero || descriptor == null)
            {
                return;
            }

            // Every string and array crosses as a pinned/native copy that lives exactly as long
            // as the call below: the ABI copies before returning.
            var strings = new List<IntPtr>();
            IntPtr Utf8(string value)
            {
                var pointer = Marshal.StringToCoTaskMemUTF8(value ?? string.Empty);
                strings.Add(pointer);
                return pointer;
            }

            var servers = descriptor.Servers ?? Array.Empty<VoipCallServer>();
            var native = new UnigramCalls.Server[servers.Count];

            for (int i = 0; i < servers.Count; i++)
            {
                var server = servers[i];
                native[i] = new UnigramCalls.Server
                {
                    Id = server.Id,
                    Ip = Utf8(server.IpAddress),
                    Ipv6 = Utf8(server.Ipv6Address),
                    Port = server.Port
                };

                if (server.Type is VoipCallServerTypeTelegramReflector reflector)
                {
                    native[i].IsReflector = 1;
                    native[i].PeerTag = Utf8(reflector.PeerTag);
                    native[i].IsTcp = reflector.IsTcp ? 1 : 0;
                    native[i].Username = Utf8(string.Empty);
                    native[i].Password = Utf8(string.Empty);
                }
                else if (server.Type is VoipCallServerTypeWebrtc webrtc)
                {
                    native[i].PeerTag = Utf8(string.Empty);
                    native[i].Username = Utf8(webrtc.Username);
                    native[i].Password = Utf8(webrtc.Password);
                    native[i].SupportsTurn = webrtc.SupportsTurn ? 1 : 0;
                    native[i].SupportsStun = webrtc.SupportsStun ? 1 : 0;
                }
                else
                {
                    native[i].PeerTag = Utf8(string.Empty);
                    native[i].Username = Utf8(string.Empty);
                    native[i].Password = Utf8(string.Empty);
                }
            }

            var key = ToArray(descriptor.EncryptionKey);
            var persistent = ToArray(descriptor.PersistentState);

            try
            {
                fixed (UnigramCalls.Server* serverPointer = native)
                fixed (byte* keyPointer = key)
                fixed (byte* persistentPointer = persistent)
                {
                    var config = new UnigramCalls.Config
                    {
                        Version = Utf8(descriptor.Version),
                        CustomParameters = Utf8(descriptor.CustomParameters),
                        LogPath = Utf8(string.Empty),
                        InitializationTimeout = descriptor.InitializationTimeout,
                        ReceiveTimeout = descriptor.ReceiveTimeout,
                        PersistentState = (IntPtr)persistentPointer,
                        PersistentStateLength = persistent.Length,
                        EncryptionKey = (IntPtr)keyPointer,
                        EncryptionKeyLength = key.Length,
                        IsOutgoing = descriptor.IsOutgoing ? 1 : 0,
                        EnableP2p = descriptor.EnableP2p ? 1 : 0,
                        AudioInputId = Utf8(descriptor.AudioInputId),
                        AudioOutputId = Utf8(descriptor.AudioOutputId),
                        Servers = (IntPtr)serverPointer,
                        ServerCount = native.Length,
                        VideoCapture = descriptor.VideoCapture?.Handle ?? IntPtr.Zero
                    };

                    UnigramCalls.unigram_voip_manager_start(_handle, config);
                }

                // The mute state can be set before Start; push it now that there is an instance.
                if (_isMuted)
                {
                    UnigramCalls.unigram_voip_manager_set_muted(_handle, 1);
                }
            }
            finally
            {
                foreach (var pointer in strings)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }

        private static byte[] ToArray(IList<byte> value)
        {
            if (value == null || value.Count == 0)
            {
                return Array.Empty<byte>();
            }

            if (value is byte[] array)
            {
                return array;
            }

            var result = new byte[value.Count];
            value.CopyTo(result, 0);
            return result;
        }

        public void Stop()
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_stop(_handle);
            }
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_release(handle);
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                if (_handle != IntPtr.Zero)
                {
                    UnigramCalls.unigram_voip_manager_set_muted(_handle, value ? 1 : 0);
                }
            }
        }

        public void SetAudioOutputGainControlEnabled(bool enabled)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_set_audio_output_gain_control(_handle, enabled ? 1 : 0);
            }
        }

        public void SetEchoCancellationStrength(int strength)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_set_echo_cancellation_strength(_handle, strength);
            }
        }

        public bool SupportsVideo => _handle != IntPtr.Zero
            && UnigramCalls.unigram_voip_manager_supports_video(_handle) != 0;

        public void SetIncomingVideoOutput(VoipVideoOutputSink sink)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            // Held so the sink is not collected while tgcalls only has a weak reference to it.
            // A null sink still has to reach the native side: it clears the sink a newly
            // negotiated video channel would otherwise be handed on creation.
            _incomingVideoOutput = sink;
            UnigramCalls.unigram_voip_manager_set_incoming_video_output(_handle, sink?.Handle ?? IntPtr.Zero);
        }

        public void SetAudioInputDevice(string id)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_set_audio_input_device(_handle, id ?? string.Empty);
            }
        }

        public void SetAudioOutputDevice(string id)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_set_audio_output_device(_handle, id ?? string.Empty);
            }
        }

        public void SetAudioOutputDuckingEnabled(bool enabled)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_set_audio_output_ducking(_handle, enabled ? 1 : 0);
            }
        }

        public void SetIsLowBatteryLevel(bool isLowBatteryLevel)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_set_is_low_battery(_handle, isLowBatteryLevel ? 1 : 0);
            }
        }

        public string GetDebugInfo()
        {
            if (_handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            var needed = UnigramCalls.unigram_voip_manager_debug_info(_handle, null, 0);
            if (needed <= 1)
            {
                return string.Empty;
            }

            var buffer = new byte[needed];
            UnigramCalls.unigram_voip_manager_debug_info(_handle, buffer, buffer.Length);

            var end = Array.IndexOf(buffer, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }

        public long GetPreferredRelayId()
        {
            return _handle == IntPtr.Zero ? 0 : UnigramCalls.unigram_voip_manager_preferred_relay_id(_handle);
        }

        public void ReceiveSignalingData(byte[] data)
        {
            if (_handle != IntPtr.Zero && data is { Length: > 0 })
            {
                UnigramCalls.unigram_voip_manager_receive_signaling_data(_handle, data, data.Length);
            }
        }

        public void SetVideoCapture(VoipCaptureBase videoCapture)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_set_video_capture(_handle, videoCapture?.Handle ?? IntPtr.Zero);
            }
        }

        public void SetRequestedVideoAspect(float aspect)
        {
            if (_handle != IntPtr.Zero)
            {
                UnigramCalls.unigram_voip_manager_set_requested_video_aspect(_handle, aspect);
            }
        }

        public event TypedEventHandler<VoipManager, VoipReadyState> StateUpdated;
        public event TypedEventHandler<VoipManager, int> SignalBarsUpdated;
        public event TypedEventHandler<VoipManager, float> AudioLevelUpdated;
        public event TypedEventHandler<VoipManager, bool> RemoteBatteryLevelIsLowUpdated;
        public event TypedEventHandler<VoipManager, RemoteMediaStateUpdatedEventArgs> RemoteMediaStateUpdated;
        public event TypedEventHandler<VoipManager, float> RemotePrefferedAspectRatioUpdated;

        /// <summary>
        /// Signalling goes through a delegate rather than an event because the Windows component
        /// does the same: there is exactly one consumer (the call service, which forwards to
        /// TDLib) and it must not be possible for a second subscriber to also send it.
        /// </summary>
        public void SetSignalingDataEmitted(SignalingDataEmittedDelegate handler)
        {
            _signalingHandler = handler;
        }

        // ---------------------------------------------------------------- callbacks
        // Static and [UnmanagedCallersOnly] so nothing has to stay rooted for the lifetime of
        // the call and so this keeps working under AOT. Each one resolves the GCHandle that was
        // handed to the native side at creation. None of them may let an exception escape: they
        // run on WebRTC threads, and unwinding into C++ takes the process down.

        private static VoipManager Resolve(IntPtr user)
        {
            return GCHandle.FromIntPtr(user).Target as VoipManager;
        }

        [UnmanagedCallersOnly]
        private static void OnStateUpdated(IntPtr user, int state)
        {
            try
            {
                var manager = Resolve(user);
                manager?.StateUpdated?.Invoke(manager, (VoipReadyState)state);
            }
            catch (Exception ex) { Logger.Error(nameof(OnStateUpdated), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnSignalBarsUpdated(IntPtr user, int bars)
        {
            try
            {
                var manager = Resolve(user);
                manager?.SignalBarsUpdated?.Invoke(manager, bars);
            }
            catch (Exception ex) { Logger.Error(nameof(OnSignalBarsUpdated), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnAudioLevelUpdated(IntPtr user, float level)
        {
            try
            {
                var manager = Resolve(user);
                manager?.AudioLevelUpdated?.Invoke(manager, level);
            }
            catch (Exception ex) { Logger.Error(nameof(OnAudioLevelUpdated), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnRemoteBatteryLowUpdated(IntPtr user, int isLow)
        {
            try
            {
                var manager = Resolve(user);
                manager?.RemoteBatteryLevelIsLowUpdated?.Invoke(manager, isLow != 0);
            }
            catch (Exception ex) { Logger.Error(nameof(OnRemoteBatteryLowUpdated), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnRemoteMediaStateUpdated(IntPtr user, int audio, int video)
        {
            try
            {
                var manager = Resolve(user);
                manager?.RemoteMediaStateUpdated?.Invoke(manager,
                    new RemoteMediaStateUpdatedEventArgs((VoipAudioState)audio, (VoipVideoState)video));
            }
            catch (Exception ex) { Logger.Error(nameof(OnRemoteMediaStateUpdated), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnRemotePreferredAspectRatioUpdated(IntPtr user, float ratio)
        {
            try
            {
                var manager = Resolve(user);
                manager?.RemotePrefferedAspectRatioUpdated?.Invoke(manager, ratio);
            }
            catch (Exception ex) { Logger.Error(nameof(OnRemotePreferredAspectRatioUpdated), ex); }
        }

        [UnmanagedCallersOnly]
        private static void OnSignalingDataEmitted(IntPtr user, byte* data, int length)
        {
            try
            {
                var manager = Resolve(user);
                var handler = manager?._signalingHandler;
                if (handler == null)
                {
                    return;
                }

                var copy = new byte[length];
                if (length > 0)
                {
                    Marshal.Copy((IntPtr)data, copy, 0, length);
                }

                handler(copy);
            }
            catch (Exception ex) { Logger.Error(nameof(OnSignalingDataEmitted), ex); }
        }
    }
}
