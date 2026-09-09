//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Telegram.Native.Calls;

namespace Telegram.Common
{
    /// <summary>
    /// The Linux MediaDeviceList.
    ///
    /// The Windows one (<c>Telegram/Common/MediaDeviceList.cs</c>) is built on WinRT:
    /// <c>DeviceInformation.CreateWatcher</c> plus <c>MediaDevice.DefaultAudio*DeviceChanged</c>.
    /// Neither exists here, and neither does an equivalent in Uno, so this asks the same source
    /// the call itself will use: the enumeration inside <c>libunigram-calls.so</c>, which is
    /// tg_owt's AudioDeviceModule. That matters more than it looks -- a list built from any
    /// other source (PipeWire's own graph, for instance) could offer an id that
    /// <c>tgcalls::SetAudioInputDeviceById</c> cannot resolve.
    ///
    /// TWO THINGS ARE DELIBERATELY DIFFERENT FROM WINDOWS, and both are consequences of that:
    ///
    /// 1. The ids are <c>#index</c>, not GUIDs. Measured in phase 2 with <c>adm-smoke</c>: the
    ///    PulseAudio backend of tg_owt leaves the guid empty for every device, so the
    ///    by-guid branch of tgcalls can never match on Linux. The by-index form is the one that
    ///    resolves.
    /// 2. There is NO watcher, so <see cref="Changed"/> never fires. Plugging in a headset
    ///    mid-call will not move the call onto it by itself. The list is re-read on every
    ///    <see cref="GetValues"/> instead, so a device that appears while the app runs does show
    ///    up the next time the picker is opened. Making this live needs either a PipeWire
    ///    registry listener or polling, and neither belongs in this batch.
    /// </summary>
    public partial class MediaDeviceList
    {
        private readonly MediaDeviceClass _class;
        private readonly object _lock = new();

        private MediaDeviceId _tracked;
        private bool _stopped;

        public MediaDeviceList(MediaDeviceClass deviceClass)
        {
            _class = deviceClass;
            _tracked = new MediaDeviceId(string.Empty, true);
        }

        public void Stop()
        {
            _stopped = true;
        }

        /// <summary>
        /// Never raised on Linux: there is no device watcher behind this list. Declared because
        /// <see cref="MediaDeviceTracker"/> and the shared call UI subscribe to it.
        /// </summary>
        public event EventHandler<MediaDeviceChangedEventArgs> Changed;

        public bool? HasValues
        {
            get
            {
                if (!UnigramCalls.IsAvailable)
                {
                    return false;
                }

                // Never null: null means "still enumerating" on Windows, and here the answer is
                // always immediate, so a caller waiting for null to resolve would wait forever.
                return ReadDevices().Count > 0;
            }
        }

        public IList<MediaDevice2> GetValues()
        {
            var devices = ReadDevices();

            if (_class != MediaDeviceClass.VideoInput)
            {
                // Same shape the Windows list gives back: the empty id is "system default",
                // and tgcalls reads an empty id as exactly that.
                devices.Insert(0, new MediaDevice2(string.Empty, Strings.Default, _class));
            }

            return devices;
        }

        public void Track(string deviceId)
        {
            lock (_lock)
            {
                _tracked = string.IsNullOrEmpty(deviceId)
                    ? new MediaDeviceId(string.Empty, true)
                    : new MediaDeviceId(deviceId, false);
            }
        }

        private List<MediaDevice2> ReadDevices()
        {
            var result = new List<MediaDevice2>();

            if (_stopped || !UnigramCalls.IsAvailable)
            {
                return result;
            }

            try
            {
                var table = _class switch
                {
                    MediaDeviceClass.VideoInput => VoipVideoCapture.GetDevices(),
                    MediaDeviceClass.AudioInput => VoipManager.GetAudioDevices(true),
                    _ => VoipManager.GetAudioDevices(false)
                };

                foreach (var (id, name) in table)
                {
                    // The ADM's first row is its own "default" pseudo device, and GetValues
                    // already puts a Default entry in front of the list. Keeping both would
                    // show the same device twice, which is what the Windows list avoids by
                    // never enumerating a default in the first place.
                    if (name != null && name.StartsWith("default:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    result.Add(new MediaDevice2(id, name, _class));
                }
            }
            catch
            {
                // Enumeration opens an AudioDeviceModule underneath. A sound server that went
                // away must degrade to an empty list, not take the call page down.
            }

            return result;
        }

        private void RaiseChanged(string deviceId)
        {
            Changed?.Invoke(this, new MediaDeviceChangedEventArgs(_class, deviceId));
        }
    }
}
