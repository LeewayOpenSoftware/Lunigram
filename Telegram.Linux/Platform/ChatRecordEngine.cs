//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.IO;
using System.Threading.Tasks;
using Telegram.Native.Audio;
using Telegram.Native.Opus;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.Media.Capture;
using Windows.Storage;

namespace Telegram.Common.Recording
{
    /// <summary>
    /// Linux replacement for the Windows <c>ChatRecordEngine</c> (which is under
    /// <c>#if !LINUX</c> in Telegram/Common/Recording): captures a voice message and encodes it as
    /// it arrives. Same public surface, because <see cref="ChatRecordSession"/> -- the state
    /// machine, the clock and the too-short rule -- is the shared file, unchanged.
    ///
    /// <para>What the Windows engine does with MediaCapture + MediaFrameReader + VoiceSink, this
    /// does with two pieces that already existed: <see cref="PulseCapture"/> asks the server for
    /// mono 48 kHz float (so there is no format to negotiate and no channel folding) and
    /// <see cref="OpusOutput"/> -- the same encoder Unigram uses on Windows, through
    /// libunigram-native -- writes the .ogg. The WAV-then-transcode fallback of the Windows path
    /// has no reason to exist here: the format is always the one the encoder wants, so a recording
    /// is always Ogg/Opus by the time the button comes up, and it is sent with no generation at
    /// all.</para>
    ///
    /// <para>Threading is upstream's: every transition is serialized on a
    /// <see cref="ConcurrentQueueWorker"/> of one, so a stop that lands while the start is still
    /// opening the device waits for it instead of racing it. Samples arrive on the capture thread
    /// and are folded into the waveform and handed to the encoder there.</para>
    ///
    /// <para><b>Video messages are not supported yet</b> (phase 6: they need a V4L2 capture path
    /// and an mp4 encoder). Asking for one fails the recording instead of pretending, and
    /// <c>ChatRecordButton</c> has <c>_hasRecordVideo</c> false under <c>#if LINUX</c>, so the
    /// mode can never be switched to it from the UI.</para>
    /// </summary>
    public partial class ChatRecordEngine
    {
        public event EventHandler RecordingFailed;
        public event EventHandler RecordingStarting;
        public event EventHandler RecordingStarted;
        public event EventHandler RecordingStopped;
        public event EventHandler RecordingTooShort;

        public Action<float> QuantumProcessed;

        // Anything shorter than this is a tap that lasted a little longer, not a message. Same
        // number as the Windows engine.
        private const int MinimumDurationMs = 700;

        private readonly ConcurrentQueueWorker _recordQueue;
        private readonly DispatcherQueue _dispatcherQueue;

        private readonly AudioWaveform _waveform = new();

        private PulseCapture _capture;
        private OpusOutput _output;

        private string _file;
        private ChatRecordMode _mode;
        private Chat _chat;

        // Written from the UI thread and read on the capture thread on every block: while it is
        // set the samples are dropped, which keeps the device warm and resuming instant, exactly
        // as the streaming path of the Windows engine does.
        private volatile bool _paused;

        public ChatRecordEngine()
        {
            _recordQueue = new ConcurrentQueueWorker(1);
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }

        public bool IsViewOnce { get; set; }

        /// <summary>
        /// Always null: it exists because <see cref="ChatRecordSession"/> hands it to whoever wants
        /// to show a camera preview, and there is no camera here. The session already treats null
        /// as "nothing to preview", so the video popup of ChatRecordBar never opens.
        /// </summary>
        public MediaCapture MediaSource => null;

        public async void Start(ChatRecordMode mode, Chat chat, int videoNoteLength)
        {
            Logger.Debug("Start invoked, mode: " + mode);

            await _recordQueue.Enqueue(async () =>
            {
                Logger.Debug("Enqueued start invoked");

                if (_capture != null)
                {
                    Logger.Debug("_capture != null, abort");

                    RecordingFailed?.Invoke(this, EventArgs.Empty);
                    return;
                }

                RecordingStarting?.Invoke(this, EventArgs.Empty);

                try
                {
                    if (mode == ChatRecordMode.Video)
                    {
                        throw new NotSupportedException("video messages are not implemented on Linux yet");
                    }

                    _mode = mode;
                    _chat = chat;
                    _paused = false;
                    _waveform.Reset();

                    _file = CreateFile();
                    _output = new OpusOutput(_file);

                    if (!_output.IsValid)
                    {
                        throw new InvalidOperationException("Opus encoder couldn't open " + _file);
                    }

                    var capture = new PulseCapture("Unigram", "Voice message");
                    capture.SamplesAvailable = OnSamplesAvailable;
                    capture.Failed += OnCaptureFailed;

                    // Last, so that no sample arrives before there is something to write it to.
                    if (!capture.Start(out var error))
                    {
                        throw new InvalidOperationException("the microphone couldn't be opened: " + error);
                    }

                    _capture = capture;

                    // The device is open and samples are flowing: this is what restarts the
                    // session's clock, so it counts recording and not the time the server took.
                    RecordingStarted?.Invoke(this, EventArgs.Empty);

                    Logger.Debug("Recording started at " + DateTime.Now);
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to start recording, abort: " + ex);

                    _capture?.Dispose();
                    _capture = null;

                    _output?.Dispose();
                    _output = null;

                    Delete(_file);
                    _file = null;

                    RecordingFailed?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        /// <summary>
        /// The microphone went away mid-recording. Tearing down here is what stops the UI sitting
        /// in a recording state that can never end, and it leaves the engine able to start again.
        /// </summary>
        private void OnCaptureFailed(object sender, string message)
        {
            Logger.Error("Capture failed: " + message);

            Cancel();
            RecordingFailed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raised on the capture thread, once per 20 ms.
        /// </summary>
        private void OnSamplesAvailable(ReadOnlySpan<float> samples)
        {
            if (_paused)
            {
                return;
            }

            // Every block is folded in and only the notification is rate-limited: this callback is
            // the recording now, so it can't afford to skip one.
            if (_waveform.Add(samples))
            {
                QuantumProcessed?.Invoke(_waveform.Level);
            }

            // Safe against a stop racing this: the binding locks and the handle refuses a write
            // once it is closed, and Stop() joins the capture thread before disposing anyway.
            _output?.Write(samples);
        }

        private static string CreateFile()
        {
            var folder = ApplicationData.Current.TemporaryFolder.Path;
            Directory.CreateDirectory(folder);

            // Unique rather than fail: the name is only second-accurate, and two recordings within
            // the same second is a tap away.
            var name = string.Format("voice_{0:yyyy}-{0:MM}-{0:dd}_{0:HH}-{0:mm}-{0:ss}.oga", DateTime.Now);
            var path = Path.Combine(folder, name);

            for (int i = 2; System.IO.File.Exists(path); i++)
            {
                path = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + " (" + i + ").oga");
            }

            return path;
        }

        private static void Delete(string path)
        {
            try
            {
                if (path != null)
                {
                    System.IO.File.Delete(path);
                }
            }
            catch
            {
                // A file we can't delete is a temporary file left behind, nothing more.
            }
        }

        public byte[] GetWaveform()
        {
            return _waveform.GetWaveform();
        }

        /// <summary>
        /// Pauses or resumes. Returns what has been recorded so far when it pauses, and null when
        /// it resumes -- which is the contract the bar reads to decide whether to show the
        /// waveform preview.
        /// </summary>
        public async Task<ChatRecordResult> PauseAsync()
        {
            Logger.Debug("Pause invoked");

            var tsc = new TaskCompletionSource<ChatRecordResult>();

            _ = _recordQueue.Enqueue(() =>
            {
                Logger.Debug("Enqueued pause invoked");

                var output = _output;
                if (_capture == null || output == null)
                {
                    Logger.Debug("recorder == null, abort");

                    // Setting it is what releases the caller: it awaits this task.
                    tsc.SetResult(null);
                    return Task.CompletedTask;
                }

                // Nothing to pause on the device: the blocks keep arriving and are dropped, which
                // keeps it warm and makes resuming instant.
                _paused = !_paused;

                tsc.SetResult(_paused
                    ? new ChatRecordResult(output.Duration, GetWaveform())
                    : null);

                return Task.CompletedTask;
            });

            return await tsc.Task;
        }

        /// <summary>
        /// Stops the recording and sends it, unless it was too short to be one.
        /// </summary>
        public void Complete(ComposeViewModel viewModel)
        {
            Stop(viewModel, discard: false);
        }

        /// <summary>
        /// Stops the recording and throws it away.
        /// </summary>
        public void Cancel()
        {
            Stop(null, discard: true);
        }

        private async void Stop(ComposeViewModel viewModel, bool discard)
        {
            Logger.Debug("Stop invoked, discard: " + discard);

            await _recordQueue.Enqueue(() =>
            {
                Logger.Debug("Enqueued stop invoked");

                var capture = _capture;
                var output = _output;
                var file = _file;
                var chat = _chat;

                if (capture == null || output == null || file == null || chat == null)
                {
                    Logger.Debug("recorder or file == null, abort");
                    return Task.CompletedTask;
                }

                _capture = null;
                _output = null;
                _file = null;

                capture.Failed -= OnCaptureFailed;

                RecordingStopped?.Invoke(this, EventArgs.Empty);

                Logger.Debug("stopping capture");

                // Joins the capture thread, so nothing else touches the encoder after this line.
                capture.Stop();
                QuantumProcessed?.Invoke(0);

                // Counted from the samples that were actually encoded, so it matches the file
                // rather than the moment the user pressed the button. Read before disposing: a
                // closed encoder no longer answers.
                var duration = output.Duration;
                var waveform = GetWaveform();

                // Flushes the tail and writes the end-of-stream page, which is what makes the
                // duration readable by every other client.
                output.Dispose();

                Logger.Debug("recorder stopped, duration: " + duration);

                if (discard || duration.TotalMilliseconds < MinimumDurationMs)
                {
                    Delete(file);

                    Logger.Debug("recording canceled or too short, abort");

                    if (!discard)
                    {
                        RecordingTooShort?.Invoke(this, EventArgs.Empty);
                    }
                }
                else
                {
                    Logger.Debug("sending recorded file");
                    Send(viewModel, file, duration, waveform);
                }

                return Task.CompletedTask;
            });
        }

        private void Send(ComposeViewModel viewModel, string file, TimeSpan duration, byte[] waveform)
        {
            var selfDestructType = IsViewOnce
                    ? new MessageSelfDestructTypeImmediately()
                    : null;

            IsViewOnce = false;

            try
            {
                _dispatcherQueue.TryEnqueue(() => _ = viewModel.SendVoiceNoteAsync(file, duration, waveform, null, selfDestructType));
            }
            catch
            {
                // The window went away between the release and here.
            }
        }
    }
}
