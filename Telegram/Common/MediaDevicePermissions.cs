//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Controls;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Common
{
    public enum MediaDeviceAccess
    {
        Audio,
        Video,
        AudioAndVideo
    }

    /// <summary>
    /// What the devices are wanted for. It only picks the wording of the denial.
    /// </summary>
    public enum MediaDevicePurpose
    {
        Call,
        Record
    }

    public partial class MediaDevicePermissions
    {
        public static bool IsUnsupported(XamlRoot xamlRoot)
        {
#if LINUX
            // ApiInfo.IsMediaSupported is false on Linux because it means "MediaFoundation and
            // MediaCapture are there", and they are not. Recording does not go through either of
            // them here (PulseAudio + the Opus encoder do it), so answering "unsupported" would
            // only put a popup in front of a microphone that works. Calls, which is the other
            // caller, are not in the subset at all.
            return false;
#else
            if (ApiInfo.IsMediaSupported)
            {
                return false;
            }

            // VoIP isn't supported on Windows N because:
            // - MediaCapture is used for capturing video (no alternatives on WinRT)
            // - MediaFoundation is used for encoding/decoding video frames (can fallback for WebRTC's software)
            _ = MessagePopup.ShowAsync(xamlRoot, Strings.VoipPlatformUnsupportedText, Strings.VoipPlatformUnsupportedTitle, Strings.OK);
            return true;
#endif
        }

        public static async Task<bool> CheckAccessAsync(XamlRoot xamlRoot, MediaDeviceAccess requestedAccess, ElementTheme requestedTheme = ElementTheme.Default, MediaDevicePurpose purpose = MediaDevicePurpose.Call)
        {
#if LINUX
            // There is no capability to request: on a desktop the microphone is readable by
            // whoever the audio server lets in, and nothing asks the user first. What can still be
            // answered is the question this method really exists for -- "can I record?" -- and the
            // audio server answers it: PulseCapture.CanCapture opens a recording stream and closes
            // it again.
            //
            // (Under Flatpak the answer would come from the portal instead, and it would come out
            // of this same call: the stream simply fails to open when the permission is not there.)
            //
            // The camera used to be DENIED WITHOUT ASKING: everything that was not Audio fell into
            // a branch that showed the "no camera permission" popup and returned false, on the
            // grounds that there was no camera path. There is one, and it is the same one the call
            // itself uses: tg_owt inside libunigram-calls.so enumerates the V4L2 devices, and
            // VoipVideoCapture.GetDevices reads that list (it answers an empty array, not an
            // exception, when the library is not there). VoipPage is in the subset, so this branch
            // is reached by every attempt to turn the camera on during a call.
            //
            // The wording had to move with it. Strings.PermissionNo* all end in "Please enable it
            // in Settings": that is the Windows remedy for a capability the user revoked, and it is
            // not what happens here -- there is no capability to grant, and what the two probes
            // answer is whether a device replied. Strings.NotFoundMicrophone and
            // Strings.NotFoundCamera say exactly that. It is also why the popup below has no
            // "Settings" button any more and is not poorer for it: with the message no longer
            // pointing at a settings page, there is no page left to open.
            var wantsAudio = requestedAccess is MediaDeviceAccess.Audio or MediaDeviceAccess.AudioAndVideo;
            var wantsVideo = requestedAccess is MediaDeviceAccess.Video or MediaDeviceAccess.AudioAndVideo;

            // Off the UI thread: opening a capture stream is a round trip to the audio server, and
            // enumerating cameras walks /dev/video* inside the native library.
            var (hasAudio, hasVideo) = await Task.Run(() =>
            {
                var audio = true;
                var video = true;

                if (wantsAudio)
                {
                    audio = Native.Audio.PulseCapture.CanCapture(out var error);
                    if (!audio)
                    {
                        Logger.Error("No audio capture device: " + error);
                    }
                }

                if (wantsVideo)
                {
                    video = Native.Calls.VoipVideoCapture.GetDevices().Length > 0;
                    if (!video)
                    {
                        Logger.Error("No video capture device");
                    }
                }

                return (audio, video);
            });

            if (hasAudio && hasVideo)
            {
                return true;
            }

            // Same three cases Windows distinguishes, and for the same reason: the user is told
            // which of the two is missing, not just that something is.
            ShowPopup(xamlRoot, !hasAudio && !hasVideo
                ? MediaDeviceAccess.AudioAndVideo
                : hasAudio
                ? MediaDeviceAccess.Video
                : MediaDeviceAccess.Audio, purpose, requestedTheme);

            return false;
#else
            string[] capabilities;

            if (requestedAccess == MediaDeviceAccess.AudioAndVideo)
            {
                capabilities = new[] { "microphone", "webcam" };
            }
            else if (requestedAccess == MediaDeviceAccess.Audio)
            {
                capabilities = new[] { "microphone" };
            }
            else
            {
                capabilities = new[] { "webcam" };
            }

            IReadOnlyDictionary<string, AppCapabilityAccessStatus> access;
            try
            {
                access = await AppCapability.RequestAccessForCapabilitiesAsync(capabilities);
            }
            catch
            {
                access = capabilities.ToDictionary(x => x, y => AppCapabilityAccessStatus.DeniedBySystem);
            }

            access.TryGetValue("microphone", out AppCapabilityAccessStatus audio);
            access.TryGetValue("webcam", out AppCapabilityAccessStatus video);

            if (requestedAccess == MediaDeviceAccess.AudioAndVideo)
            {
                if (audio != AppCapabilityAccessStatus.Allowed && video != AppCapabilityAccessStatus.Allowed)
                {
                    ShowPopup(xamlRoot, MediaDeviceAccess.AudioAndVideo, purpose, requestedTheme);
                    return false;
                }
                else if (audio != AppCapabilityAccessStatus.Allowed)
                {
                    ShowPopup(xamlRoot, MediaDeviceAccess.Audio, purpose, requestedTheme);
                    return false;
                }
                else if (video != AppCapabilityAccessStatus.Allowed)
                {
                    ShowPopup(xamlRoot, MediaDeviceAccess.Video, purpose, requestedTheme);
                    return false;
                }
            }
            else if (requestedAccess == MediaDeviceAccess.Audio && audio != AppCapabilityAccessStatus.Allowed)
            {
                ShowPopup(xamlRoot, MediaDeviceAccess.Audio, purpose, requestedTheme);
                return false;
            }
            else if (requestedAccess == MediaDeviceAccess.Video && video != AppCapabilityAccessStatus.Allowed)
            {
                ShowPopup(xamlRoot, MediaDeviceAccess.Video, purpose, requestedTheme);
                return false;
            }

            return true;
#endif
        }

        private static async void ShowPopup(XamlRoot xamlRoot, MediaDeviceAccess requestedAccess, MediaDevicePurpose purpose, ElementTheme requestedTheme = ElementTheme.Default)
        {
            var popup = new MessagePopup
            {
                Title = Strings.AppName,
#if LINUX
                // Not the Strings.PermissionNo* family: nothing was denied, no device answered.
                // The purpose (call or recording) does not change that, so it does not change the
                // wording either.
                Message = requestedAccess switch
                {
                    MediaDeviceAccess.Audio => Strings.NotFoundMicrophone,
                    MediaDeviceAccess.Video => Strings.NotFoundCamera,
                    _ => Strings.NotFoundMicrophone + Environment.NewLine + Strings.NotFoundCamera
                },
                // No "Settings" button, and after the change above that is no longer a loss: the
                // Windows one opens ms-settings:appsfeatures-app to un-deny a capability, and the
                // message here no longer claims a capability was denied. Nothing a settings page
                // could do would make a device appear.
                PrimaryButtonText = Strings.OK,
#else
                Message = purpose == MediaDevicePurpose.Record
                    ? requestedAccess switch
                    {
                        MediaDeviceAccess.Audio => Strings.PermissionNoAudio,
                        MediaDeviceAccess.Video => Strings.PermissionNoCamera,
                        _ => Strings.PermissionNoAudioVideo
                    }
                    : requestedAccess switch
                    {
                        MediaDeviceAccess.Audio => Strings.PermissionNoAudioCalls,
                        MediaDeviceAccess.Video => Strings.PermissionNoVideoCalls,
                        _ => Strings.PermissionNoAudioVideoCalls
                    },
                PrimaryButtonText = Strings.Settings,
                SecondaryButtonText = Strings.OK,
#endif
                RequestedTheme = requestedTheme
            };

#if LINUX
            await popup.ShowQueuedAsync(xamlRoot);
#else
            var confirm = await popup.ShowQueuedAsync(xamlRoot);
            if (confirm == ContentDialogResult.Primary)
            {
                await Launcher.LaunchUriAsync(new Uri("ms-settings:appsfeatures-app"));
            }
#endif
        }
    }
}
