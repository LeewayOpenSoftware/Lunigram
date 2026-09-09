//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Native.Calls;
using Windows.Graphics.Capture;
using Microsoft.UI.Xaml;

namespace Telegram.Services
{
    /// <summary>
    /// The Linux CaptureSessionService: what the call page asks when the user presses
    /// "share screen".
    ///
    /// The Windows one is 207 lines of <c>Windows.Graphics.Capture</c> plus
    /// <c>AppCapabilityAccess</c>. None of that exists here, and the replacement is not a
    /// translation of it -- on Linux the sources come from tgcalls' DesktopCaptureSourceManager
    /// and are addressed by id, not by <see cref="GraphicsCaptureItem"/>.
    ///
    /// WHAT THIS DOES TODAY: it lists the sources, so a picker can be built on top of
    /// <see cref="FindAll"/>, and <see cref="ChooseAsync"/> returns null -- there is no general
    /// picker yet, and picking an arbitrary one by itself would start sharing the user's screen
    /// without asking. The call page treats null as "cancelled" and simply does not share.
    ///
    /// AND THERE IS A MEASURED CATCH worth reading before building that general picker: under
    /// Wayland (with Xwayland behind it), the X11 capturer inside tg_owt enumerates one screen
    /// through the Xwayland root but delivers ZERO frames from it (measured with
    /// loopback-smoke: the local preview sink and the remote sink both stayed at 0). The id that
    /// can actually produce an image under Wayland is <see cref="PipeWireSourceId"/>, which goes
    /// through the portal and opens the system's own "share your screen" dialog. The native
    /// enumeration offers it first, and only, when the session looks like Wayland (see
    /// <c>unigram_calls.cpp:unigram_voip_screen_sources</c>) -- nothing here selects it on its
    /// own in general, precisely because it is that dialog that must ask. The one exception is
    /// <see cref="FindPipeWireSourceId"/>: since it is the ONE source worth auto-picking before a
    /// real picker exists (there is nothing else on Wayland to choose between), the call page
    /// uses it directly instead of going through <see cref="ChooseAsync"/>.
    /// </summary>
    public partial class CaptureSessionService
    {
        /// <summary>
        /// The fixed id tgcalls' desktop capturer uses for the xdg-desktop-portal ScreenCast
        /// source (<c>DesktopCaptureSourceHelper.cpp</c>). Not a real enumerated screen or
        /// window -- picking it is what asks the portal to open its own share dialog.
        /// </summary>
        public const string PipeWireSourceId = "desktop_capturer_pipewire";

        /// <summary>
        /// The PipeWire portal source, if the native side is offering one (Wayland only -- see
        /// the class remarks), else null. Until there is a real picker, this is the entire
        /// "choose a source" logic on Linux: it is the only source that can produce an image
        /// under Wayland, and under X11 the native side never offers it at all.
        /// </summary>
        public static string FindPipeWireSourceId()
        {
            foreach (var item in FindAllDisplayIds())
            {
                if (item is DesktopCaptureSessionItem { SourceId: PipeWireSourceId } desktop)
                {
                    return desktop.SourceId;
                }
            }

            return null;
        }

        public static Task<CaptureSessionOptions> ChooseAsync(XamlRoot xamlRoot, bool canShareAudio)
        {
            Logger.Info("Screen sharing: sources can be listed, but there is no picker on Linux yet");
            return Task.FromResult<CaptureSessionOptions>(null);
        }

        /// <summary>Screens first, then windows, as the shared UI expects.</summary>
        public static IList<CaptureSessionItem> FindAll()
        {
            var result = new List<CaptureSessionItem>();
            result.AddRange(FindAllDisplayIds());
            result.AddRange(FindAllTopLevelWindowIds());
            return result;
        }

        public static IList<CaptureSessionItem> FindAllTopLevelWindowIds()
        {
            return Read(true);
        }

        public static IList<CaptureSessionItem> FindAllDisplayIds()
        {
            return Read(false);
        }

        private static IList<CaptureSessionItem> Read(bool windows)
        {
            var result = new List<CaptureSessionItem>();

            try
            {
                foreach (var (id, name) in VoipScreenCapture.GetSources(windows))
                {
                    result.Add(new DesktopCaptureSessionItem(name, id));
                }
            }
            catch
            {
                // Enumerating touches X11 / the portal. An empty list is the right answer when
                // that fails; taking the call page down is not.
            }

            return result;
        }
    }

    /// <summary>
    /// A source as tgcalls addresses it: by id string. The Windows records carry a
    /// <see cref="GraphicsCaptureItem"/> instead, which is why this one is separate rather than
    /// a Linux body for the same record.
    /// </summary>
    public record CaptureSessionOptions(GraphicsCaptureItem CaptureItem, long ProcessId);

    public abstract record CaptureSessionItem(string DisplayName)
    {
        public abstract CaptureSessionOptions ToOptions(bool includeAudio);
    }

    public record DesktopCaptureSessionItem(string DisplayName, string SourceId) : CaptureSessionItem(DisplayName)
    {
        /// <summary>
        /// Always null: there is no GraphicsCaptureItem on Linux. Whoever builds the picker
        /// should call <c>new VoipScreenCapture(SourceId)</c> with <see cref="SourceId"/> and
        /// hand that to the call, instead of going through CaptureSessionOptions.
        /// </summary>
        public override CaptureSessionOptions ToOptions(bool includeAudio)
        {
            return null;
        }
    }
}
