//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Windows.Media.SystemMediaTransportControls and the six types around it do not exist in Uno
// (checked: zero occurrences in Uno.UI.dll), and the shared Services/PlaybackService.cs -- the 962
// lines of playlist logic the Linux head wants to reuse rather than duplicate -- talks to them in
// two places: the SystemMediaTransportControls region and the PlaybackState setter.
//
// So they are declared here, in the original namespace, keeping exactly the members that shared
// file touches. That is rule 1 of PORTING.md applied to a WinRT API instead of a control, and it
// is what lets upstream's own code feed the desktop's media controls without knowing it: phase 5
// step 3 puts MPRIS2 behind IMediaTransportBackend (Platform/DBus/MprisPlayer.cs), and not one
// line of Telegram/Services/PlaybackService.cs changes when it does.
//
// Until then the controls record what they are told and raise nothing, which is exactly the
// behaviour Windows has when no media session is active.

using System;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Windows.Media
{
    public enum MediaPlaybackStatus
    {
        Closed = 0,
        Changing = 1,
        Stopped = 2,
        Playing = 3,
        Paused = 4
    }

    public enum MediaPlaybackType
    {
        Unknown = 0,
        Music = 1,
        Video = 2,
        Image = 3
    }

    public enum MediaPlaybackAutoRepeatMode
    {
        None = 0,
        Track = 1,
        List = 2
    }

    public enum SystemMediaTransportControlsButton
    {
        Play = 0,
        Pause = 1,
        Stop = 2,
        Record = 3,
        FastForward = 4,
        Rewind = 5,
        Next = 6,
        Previous = 7,
        ChannelUp = 8,
        ChannelDown = 9
    }

    public sealed partial class SystemMediaTransportControlsButtonPressedEventArgs
    {
        public SystemMediaTransportControlsButtonPressedEventArgs(SystemMediaTransportControlsButton button)
        {
            Button = button;
        }

        public SystemMediaTransportControlsButton Button { get; }
    }

    public sealed partial class AutoRepeatModeChangeRequestedEventArgs
    {
        public AutoRepeatModeChangeRequestedEventArgs(MediaPlaybackAutoRepeatMode mode)
        {
            RequestedAutoRepeatMode = mode;
        }

        public MediaPlaybackAutoRepeatMode RequestedAutoRepeatMode { get; }
    }

    public sealed partial class MusicDisplayProperties
    {
        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public string AlbumArtist { get; set; } = string.Empty;

        internal void Clear()
        {
            Title = string.Empty;
            Artist = string.Empty;
            AlbumArtist = string.Empty;
        }
    }

    public sealed partial class SystemMediaTransportControlsDisplayUpdater
    {
        private readonly SystemMediaTransportControls _owner;

        internal SystemMediaTransportControlsDisplayUpdater(SystemMediaTransportControls owner)
        {
            _owner = owner;
        }

        public MediaPlaybackType Type { get; set; }

        public MusicDisplayProperties MusicProperties { get; } = new();

        public RandomAccessStreamReference Thumbnail { get; set; }

        /// <summary>
        /// Also drops the thumbnail on Windows, which is why the shared code re-applies the album
        /// cover after every ClearAll; the same has to be true here or the two would diverge the
        /// day MPRIS starts reading Thumbnail.
        /// </summary>
        public void ClearAll()
        {
            Type = MediaPlaybackType.Unknown;
            Thumbnail = null;
            MusicProperties.Clear();

            _owner.Backend?.Update(_owner);
        }

        public void Update()
        {
            _owner.Backend?.Update(_owner);
        }
    }

    /// <summary>
    /// What phase 5 step 3 implements over D-Bus. It is deliberately shaped as "the desktop tells
    /// us a button was pressed / we tell the desktop what is playing", so the MPRIS object never
    /// has to know about chats, messages or TDLib -- the same blindness rule the rest of
    /// Telegram.Linux/Platform/DBus/ follows.
    /// </summary>
    public interface IMediaTransportBackend
    {
        void Update(SystemMediaTransportControls controls);
    }

    public sealed partial class SystemMediaTransportControls
    {
        // ONE per process, not one per thread, and that is the whole difference between MPRIS
        // working and MPRIS being an object nobody feeds. The WinRT original is per view and a
        // [ThreadStatic] mirrors that, but here the two halves meet on different threads: the
        // shared PlaybackService asks for the controls from the UI thread (EnsureTransport), while
        // the MPRIS bridge asks for them from wherever DesktopIntegration.InitializeAsync happens
        // to run. With one instance per thread each of them would get its OWN controls, the bridge
        // would set Backend on an object the service never touches, and the failure would be
        // silent: metadata updated, nothing on the bus, no error anywhere.
        private static SystemMediaTransportControls _current;
        private static readonly object _currentLock = new();

        private SystemMediaTransportControls()
        {
            DisplayUpdater = new SystemMediaTransportControlsDisplayUpdater(this);
        }

        /// <summary>
        /// The transport of this process. The shared code calls this inside a try/catch and
        /// treats a null transport as "no media controls on this system", so the Linux head can
        /// keep answering an object even before MPRIS exists.
        /// </summary>
        public static SystemMediaTransportControls GetForCurrentView()
        {
            lock (_currentLock)
            {
                return _current ??= new SystemMediaTransportControls();
            }
        }

        /// <summary>
        /// Set once by the MPRIS layer at start-up. Null means nothing is listening, which is the
        /// state phase 5 steps 1 and 2 ship in.
        /// </summary>
        public IMediaTransportBackend Backend { get; set; }

        public SystemMediaTransportControlsDisplayUpdater DisplayUpdater { get; }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                Backend?.Update(this);
            }
        }

        public bool IsPlayEnabled { get; set; }

        public bool IsPauseEnabled { get; set; }

        public bool IsStopEnabled { get; set; }

        public bool IsPreviousEnabled { get; set; }

        public bool IsNextEnabled { get; set; }

        public bool ShuffleEnabled { get; set; }

        public double PlaybackRate { get; set; } = 1;

        public MediaPlaybackAutoRepeatMode AutoRepeatMode { get; set; }

        private MediaPlaybackStatus _playbackStatus = MediaPlaybackStatus.Closed;
        public MediaPlaybackStatus PlaybackStatus
        {
            get => _playbackStatus;
            set
            {
                _playbackStatus = value;
                Backend?.Update(this);
            }
        }

        public event TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs> ButtonPressed;

        public event TypedEventHandler<SystemMediaTransportControls, AutoRepeatModeChangeRequestedEventArgs> AutoRepeatModeChangeRequested;

        /// <summary>Raised by the backend when the desktop presses a media key.</summary>
        public void RaiseButtonPressed(SystemMediaTransportControlsButton button)
        {
            ButtonPressed?.Invoke(this, new SystemMediaTransportControlsButtonPressedEventArgs(button));
        }

        /// <inheritdoc cref="RaiseButtonPressed"/>
        public void RaiseAutoRepeatModeChangeRequested(MediaPlaybackAutoRepeatMode mode)
        {
            AutoRepeatModeChangeRequested?.Invoke(this, new AutoRepeatModeChangeRequestedEventArgs(mode));
        }
    }
}
