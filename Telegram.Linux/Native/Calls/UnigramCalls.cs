//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Telegram.Native.Calls
{
    /// <summary>
    /// P/Invoke layer over <c>libunigram-calls.so</c>, the flat C ABI that replaces the
    /// Telegram.Native.Calls C++/WinRT component on Linux. The contract lives in
    /// <c>unigram-linux/native/calls/include/unigram_calls.h</c>.
    ///
    /// This is a SECOND native library, not part of libunigram-native.so, and on purpose: it
    /// carries 32 MB of WebRTC that must not be mapped on every start, and it pulls X11,
    /// PipeWire, GLib and OpenSSL, which the sticker/video library does not. The reasoning is
    /// written out at the top of unigram_calls.h.
    ///
    /// Optional at runtime, the same way libmpv and libpulse-simple are: a machine without the
    /// library (or with a stale one) gets "calls unavailable" instead of a crash, and every
    /// public type in this namespace degrades to the inert behaviour the phase 1 stubs had.
    /// </summary>
    internal static unsafe partial class UnigramCalls
    {
        internal const string Library = NativeLibraryResolver.UnigramCalls;

        /// <summary>
        /// UNIGRAM_VOIP_ABI this binding was written against. Anything else means a stale .so
        /// next to the binary; refusing here turns what would be a much later segfault into a
        /// log line and a disabled feature.
        /// </summary>
        internal const int Abi = 1;

        /// <summary>
        /// webrtc::AudioDeviceModule::AudioLayer value that means "the dummy, silent device".
        /// If <see cref="ActiveAudioLayer"/> returns this, tg_owt was built without audio
        /// backends and a call would connect and be mute -- worth a log line, because nothing
        /// else about the call would look wrong.
        /// </summary>
        internal const int DummyAudioLayer = 10;

        internal const int PulseAudioLayer = 4;

        internal static bool IsAvailable { get; } = Probe();

        private static bool Probe()
        {
            int abi;

            try
            {
                abi = unigram_voip_initialize();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // Expected until the library has been built and rsync'd from the Mint box.
                // UNIGRAM_CALLS_PATH overrides the lookup.
                UnigramNative.Report(Logger.LogLevel.Error,
                    $"libunigram-calls.so is not usable ({ex.GetType().Name}: {ex.Message}): calls disabled");
                return false;
            }

            if (abi != Abi)
            {
                UnigramNative.Report(Logger.LogLevel.Error,
                    $"libunigram-calls.so reports ABI {abi}, this build speaks {Abi}: calls disabled");
                return false;
            }

            var layer = unigram_voip_active_audio_layer();
            if (layer == DummyAudioLayer)
            {
                // Not fatal: signalling, networking and video would still work. But it is the one
                // failure nothing else makes visible -- the call connects and is simply silent --
                // so it gets said out loud, once, with the flag that causes it.
                UnigramNative.Report(Logger.LogLevel.Error,
                    "libunigram-calls.so was linked against a tg_owt built with " +
                    "TG_OWT_BUILD_AUDIO_BACKENDS=OFF: calls would be SILENT");
            }
            else
            {
                UnigramNative.Report(Logger.LogLevel.Info,
                    $"libunigram-calls.so ready, audio layer {layer} " +
                    $"({(layer == PulseAudioLayer ? "PulseAudio" : "other")}), tgcalls versions {Versions}");
            }

            return true;
        }

        internal static string Versions
        {
            get
            {
                if (!IsAvailable)
                {
                    return string.Empty;
                }

                var pointer = unigram_voip_versions();
                return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
            }
        }

        internal static int MaxLayer => IsAvailable ? unigram_voip_max_layer() : 0;

        internal static int ActiveAudioLayer => IsAvailable ? unigram_voip_active_audio_layer() : -1;

        /// <summary>
        /// Reads one of the "id\tname\n" tables the ABI produces (audio devices, cameras,
        /// screens, windows) and hands back the parsed pairs. Grows the buffer once if the
        /// first pass did not fit, which is what the <c>needed</c> out parameter is for.
        /// </summary>
        internal static (string Id, string Name)[] ReadTable(Func<byte[], int, (int Count, int Needed)> read)
        {
            if (!IsAvailable)
            {
                return Array.Empty<(string, string)>();
            }

            var buffer = new byte[4096];
            var (count, needed) = read(buffer, buffer.Length);

            if (count <= 0)
            {
                return Array.Empty<(string, string)>();
            }

            if (needed > buffer.Length)
            {
                buffer = new byte[needed];
                (count, _) = read(buffer, buffer.Length);
                if (count <= 0)
                {
                    return Array.Empty<(string, string)>();
                }
            }

            var end = Array.IndexOf(buffer, (byte)0);
            var payload = Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);

            var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var result = new (string, string)[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var tab = lines[i].IndexOf('\t');
                result[i] = tab < 0
                    ? (lines[i], lines[i])
                    : (lines[i].Substring(0, tab), lines[i].Substring(tab + 1));
            }

            return result;
        }

        // ---------------------------------------------------------------- process

        [LibraryImport(Library)]
        internal static partial int unigram_voip_initialize();

        [LibraryImport(Library)]
        internal static partial IntPtr unigram_voip_versions();

        [LibraryImport(Library)]
        internal static partial int unigram_voip_max_layer();

        [LibraryImport(Library)]
        internal static partial int unigram_voip_active_audio_layer();

        [LibraryImport(Library)]
        internal static partial int unigram_voip_audio_devices(int input, byte[] buffer, int bufferLen, out int needed);

        [LibraryImport(Library)]
        internal static partial int unigram_voip_video_devices(byte[] buffer, int bufferLen, out int needed);

        [LibraryImport(Library)]
        internal static partial int unigram_voip_screen_sources(int windows, byte[] buffer, int bufferLen, out int needed);

        // ---------------------------------------------------------------- sink

        [LibraryImport(Library)]
        internal static partial IntPtr unigram_voip_sink_create(delegate* unmanaged<IntPtr, byte*, int, byte*, int, byte*, int, int, int, int, void> onFrame, IntPtr user);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_sink_release(IntPtr sink);

        [LibraryImport(Library)]
        internal static partial int unigram_voip_sink_width(IntPtr sink);

        [LibraryImport(Library)]
        internal static partial int unigram_voip_sink_height(IntPtr sink);

        // ---------------------------------------------------------------- capture

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr unigram_voip_capture_create_camera(string deviceId);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr unigram_voip_capture_create_screen(string sourceId);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_capture_set_state(IntPtr capture, int state);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_capture_set_output(IntPtr capture, IntPtr sink);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_capture_set_preferred_aspect_ratio(IntPtr capture, float ratio);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void unigram_voip_capture_switch_device(IntPtr capture, string deviceId);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_capture_stop(IntPtr capture);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_capture_release(IntPtr capture);

        // ---------------------------------------------------------------- 1:1 call

        /// <summary>
        /// Mirrors <c>unigram_voip_server</c>. Field order and types are the ABI: do not
        /// reorder, and keep every int an int (the C side uses int, not bool).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Server
        {
            public long Id;
            public IntPtr Ip;
            public IntPtr Ipv6;
            public int Port;
            public int IsReflector;
            public IntPtr PeerTag;
            public int IsTcp;
            public IntPtr Username;
            public IntPtr Password;
            public int SupportsTurn;
            public int SupportsStun;
        }

        /// <summary>Mirrors <c>unigram_voip_config</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Config
        {
            public IntPtr Version;
            public IntPtr CustomParameters;
            public IntPtr LogPath;
            public double InitializationTimeout;
            public double ReceiveTimeout;
            public IntPtr PersistentState;
            public int PersistentStateLength;
            public IntPtr EncryptionKey;
            public int EncryptionKeyLength;
            public int IsOutgoing;
            public int EnableP2p;
            public IntPtr AudioInputId;
            public IntPtr AudioOutputId;
            public IntPtr Servers;
            public int ServerCount;
            public IntPtr VideoCapture;
        }

        /// <summary>
        /// Mirrors <c>unigram_voip_events</c>: seven function pointers, in order. They are
        /// <c>[UnmanagedCallersOnly]</c> statics rather than delegates so that nothing has to
        /// stay rooted for the lifetime of the call, and so that this survives AOT.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Events
        {
            public delegate* unmanaged<IntPtr, int, void> StateUpdated;
            public delegate* unmanaged<IntPtr, int, void> SignalBarsUpdated;
            public delegate* unmanaged<IntPtr, float, void> AudioLevelUpdated;
            public delegate* unmanaged<IntPtr, int, void> RemoteBatteryLowUpdated;
            public delegate* unmanaged<IntPtr, int, int, void> RemoteMediaStateUpdated;
            public delegate* unmanaged<IntPtr, float, void> RemotePreferredAspectRatioUpdated;
            public delegate* unmanaged<IntPtr, byte*, int, void> SignalingDataEmitted;
        }

        [LibraryImport(Library)]
        internal static partial IntPtr unigram_voip_manager_create(in Events events, IntPtr user);

        [LibraryImport(Library)]
        internal static partial int unigram_voip_manager_start(IntPtr manager, in Config config);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_stop(IntPtr manager);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_release(IntPtr manager);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_set_muted(IntPtr manager, int muted);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_set_audio_output_gain_control(IntPtr manager, int enabled);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_set_echo_cancellation_strength(IntPtr manager, int strength);

        [LibraryImport(Library)]
        internal static partial int unigram_voip_manager_supports_video(IntPtr manager);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_set_incoming_video_output(IntPtr manager, IntPtr sink);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void unigram_voip_manager_set_audio_input_device(IntPtr manager, string id);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void unigram_voip_manager_set_audio_output_device(IntPtr manager, string id);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_set_audio_output_ducking(IntPtr manager, int enabled);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_set_is_low_battery(IntPtr manager, int isLow);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_receive_signaling_data(IntPtr manager, byte[] data, int length);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_set_video_capture(IntPtr manager, IntPtr capture);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_manager_set_requested_video_aspect(IntPtr manager, float aspect);

        [LibraryImport(Library)]
        internal static partial long unigram_voip_manager_preferred_relay_id(IntPtr manager);

        [LibraryImport(Library)]
        internal static partial int unigram_voip_manager_debug_info(IntPtr manager, byte[] buffer, int bufferLen);

        // ---------------------------------------------------------------- group call

        [StructLayout(LayoutKind.Sequential)]
        internal struct GroupConfig
        {
            public IntPtr AudioInputId;
            public IntPtr AudioOutputId;
            public IntPtr LogPath;
            public int VideoContentType;
            public int IsConference;
            public int NoiseSuppression;
            public IntPtr VideoCapture;
        }

        /// <summary>Mirrors <c>unigram_voip_group_level</c>: the shape of one audio level update.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct GroupLevel
        {
            public uint AudioSource;
            public float Level;
            public int IsSpeaking;
            public int IsMuted;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GroupVideoChannel
        {
            public uint AudioSource;
            public long ParticipantId;
            public IntPtr EndpointId;
            public IntPtr SourceGroups;
            public int MinQuality;
            public int MaxQuality;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GroupEvents
        {
            public delegate* unmanaged<IntPtr, int, int, void> NetworkStateUpdated;
            public delegate* unmanaged<IntPtr, GroupLevel*, int, void> AudioLevelsUpdated;
            public delegate* unmanaged<IntPtr, long, void> BroadcastTimeRequested;
            public delegate* unmanaged<IntPtr, int, long, long, void> AudioBroadcastPartRequested;
            public delegate* unmanaged<IntPtr, int, long, int, int, long, void> VideoBroadcastPartRequested;
            public delegate* unmanaged<IntPtr, uint*, int, long, void> MediaChannelDescriptionsRequested;
            public delegate* unmanaged<IntPtr, byte*, int, long, int, int, void> EncryptDecrypt;
        }

        [LibraryImport(Library)]
        internal static partial IntPtr unigram_voip_group_create(in GroupConfig config, in GroupEvents events, IntPtr user);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_stop(IntPtr group);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_release(IntPtr group);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_set_connection_mode(IntPtr group, int mode, int keepBroadcast, int unified);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_emit_join_payload(IntPtr group, delegate* unmanaged<IntPtr, uint, IntPtr, void> callback, IntPtr user);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void unigram_voip_group_set_join_response_payload(IntPtr group, string payload);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_remove_ssrcs(IntPtr group, uint[] ssrcs, int count);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void unigram_voip_group_add_incoming_video_output(IntPtr group, string endpointId, IntPtr sink);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_set_muted(IntPtr group, int muted);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_set_noise_suppression(IntPtr group, int enabled);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void unigram_voip_group_set_audio_input_device(IntPtr group, string id);

        [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void unigram_voip_group_set_audio_output_device(IntPtr group, string id);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_set_video_capture(IntPtr group, IntPtr capture);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_set_volume(IntPtr group, uint ssrc, double volume);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_set_requested_video_channels(IntPtr group, GroupVideoChannel[] channels, int count);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_broadcast_time_respond(IntPtr group, long token, long time);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_broadcast_part_respond(IntPtr group, long token, long timestamp, int status, byte[] data, int length);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_media_channels_respond(IntPtr group, long token, uint[] audioSources, long[] userIds, int count);

        [LibraryImport(Library)]
        internal static partial void unigram_voip_group_encrypt_result(IntPtr group, byte[] data, int length);
    }
}
