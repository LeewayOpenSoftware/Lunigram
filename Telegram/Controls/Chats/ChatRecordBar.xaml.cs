//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Common.Recording;
using Telegram.Composition;
using Telegram.Controls.Media;
using Telegram.Native.Controls;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Storage;
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls.Chats
{
    public sealed partial class ChatRecordBar : GridEx
    {
        private readonly DispatcherTimer _elapsedTimer;
        private readonly Visual _ellipseVisual;
        private readonly Visual _elapsedVisual;
        private readonly Visual _slideVisual;
        private readonly Visual _recordVisual;
        private readonly Visual _rootVisual;

        private readonly CompositionBlobVisual _blobVisual;

#if LINUX
        // The pulse of the recording circle. See StartBlob.
        private readonly CompositionVSync _blobVSync = new(30);
        private Visual _blobCircleVisual;
        private float _blobLevel;
        private float _blobPresentationLevel;
        private bool _blobPulsing;
#endif

#if !LINUX
        private Popup _videoPopup;
        private CaptureElement _videoElement;
#endif

        public ChatRecordBar()
        {
            InitializeComponent();

            if (ApiInfo.CanCreateThemeShadow)
            {
                var shadow = new ThemeShadow();
                var translation = new Vector3(0, 0, Constants.BubbleElevation);

                ViewOnceCaster.Shadow = shadow;
                ViewOnceCaster.Translation = translation;

                PauseCaster.Shadow = shadow;
                PauseCaster.Translation = translation;
            }

            ElementCompositionPreview.SetIsTranslationEnabled(Ellipse, true);

            _ellipseVisual = ElementComposition.GetElementVisual(Ellipse);
            _elapsedVisual = ElementComposition.GetElementVisual(ElapsedPanel);
            _slideVisual = ElementComposition.GetElementVisual(SlidePanel);
            _recordVisual = ElementComposition.GetElementVisual(this);
            _rootVisual = ElementComposition.GetElementVisual(this);

            _ellipseVisual.CenterPoint = new Vector3(80);
            _ellipseVisual.Scale = new Vector3(0);

            _elapsedTimer = new DispatcherTimer();
            _elapsedTimer.Interval = TimeSpan.FromMilliseconds(100);
            _elapsedTimer.Tick += OnElapsedTimerTick;

#if LINUX
            try
            {
                // The blob is the only piece of this control that builds composition geometry, and
                // it runs in the constructor -- which runs when a chat opens. If Uno ever refuses
                // one of those calls, the price has to be a circle that does not wobble, not a chat
                // that does not open.
                _blobVisual = new CompositionBlobVisual(Blob, 160, 160, 4);
            }
            catch (Exception ex)
            {
                Logger.Error("CompositionBlobVisual", ex);
            }
#else
            _blobVisual = new CompositionBlobVisual(Blob, 160, 160, 4);
#endif

#if LINUX
            // The blob does not animate here (see StartBlob), so the Border it would have drawn
            // over becomes the circle: same 80 px the glyph sits on, same corner radius the
            // resting blob has.
            Blob.Width = 80;
            Blob.Height = 80;
            Blob.CornerRadius = new CornerRadius(40);
            Blob.HorizontalAlignment = HorizontalAlignment.Center;
            Blob.VerticalAlignment = VerticalAlignment.Center;
#endif

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        protected override void OnDisconnectVisualChildren()
        {
            _blobVisual?.StopAnimating();
#if LINUX
            StopBlobPulse();
#endif
        }

        private void OnElapsedTimerTick(object sender, object e)
        {
            ElapsedLabel.Text = ControlledButton.Elapsed.ToString("m\\:ss\\.ff");
        }

        private void ElapsedPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var point = _elapsedVisual.Offset;
            point.X = (float)-e.NewSize.Width;

            _elapsedVisual.Offset = point;
            _elapsedVisual.Size = e.NewSize.ToVector2();
        }

        private void SlidePanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var point = _slideVisual.Offset;
            point.X = (float)e.NewSize.Width + 36;

            _slideVisual.Opacity = 0;
            _slideVisual.Offset = point;
            _slideVisual.Size = e.NewSize.ToVector2();
        }

        private void ButtonCancelRecording_Click(object sender, RoutedEventArgs e)
        {
            ControlledButton.Cancel();
        }

        private void OnQuantumProcessed(object sender, float amplitude)
        {
            _blobVisual?.UpdateLevel(amplitude * 40);

#if LINUX
            // Raised on the capture thread (ChatRecordSession says so, and means it), so this is a
            // single float store and nothing else. The 4 is the maxLevel handed to the
            // CompositionBlobVisual constructor above, and normalising here rather than in
            // OnBlobRendering keeps the two definitions of "full" in the same file.
            _blobLevel = MathF.Min(1, MathF.Max(amplitude * 40 / 4f, 0));
#endif
        }

        private void ChatRecordLocked_Click(object sender, RoutedEventArgs e)
        {
            ControlledButton.Complete();
        }

        private ChatRecordButton _controlledButton;
        public ChatRecordButton ControlledButton
        {
            get => _controlledButton;
            set => SetControlledButton(value);
        }

        private void SetControlledButton(ChatRecordButton value)
        {
            if (_controlledButton == value)
            {
                return;
            }

            Detach();
            _controlledButton = value;

            if (IsLoaded)
            {
                Attach();
            }
        }

        private bool _attached;

        private void Attach()
        {
            if (_controlledButton == null || _attached)
            {
                return;
            }

            _attached = true;

            _controlledButton.RecordingStarting += OnRecordingStarting;
#if !LINUX
            _controlledButton.RecordingStarted += OnRecordingStarted;
#endif
            _controlledButton.RecordingStopped += OnRecordingStopped;
            _controlledButton.RecordingLocked += OnRecordingLocked;
            _controlledButton.QuantumProcessed += OnQuantumProcessed;
            _controlledButton.ManipulationDelta += OnManipulationDelta;
        }

        private void Detach()
        {
            if (_controlledButton == null || !_attached)
            {
                return;
            }

            _attached = false;

            _controlledButton.RecordingStarting -= OnRecordingStarting;
#if !LINUX
            _controlledButton.RecordingStarted -= OnRecordingStarted;
#endif
            _controlledButton.RecordingStopped -= OnRecordingStopped;
            _controlledButton.RecordingLocked -= OnRecordingLocked;
            _controlledButton.QuantumProcessed -= OnQuantumProcessed;
            _controlledButton.ManipulationDelta -= OnManipulationDelta;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Attach();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Detach();
        }

#if !LINUX
        private async void OnRecordingStarted(object sender, MediaCapture mediaCapture)
        {
            try
            {
                if (mediaCapture == null || mediaCapture.MediaCaptureSettings.StreamingCaptureMode == StreamingCaptureMode.Audio)
                {
                    return;
                }

                _videoElement = new CaptureElement
                {
                    Source = mediaCapture,
                    Stretch = Stretch.UniformToFill,
                    Width = 272,
                    Height = 272,
                    RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform
                    {
                        ScaleX = -1
                    }
                };

                var videoRoot = new Grid
                {
                    Width = 272,
                    Height = 272,
                    Background = await LoadLastFrameAsync(),
                    CornerRadius = new CornerRadius(272 / 2),
                    Translation = new Vector3(0, 0, 128),
                    Shadow = new ThemeShadow()
                };

                videoRoot.Children.Add(_videoElement);
                videoRoot.Children.Add(new SelfDestructTimer
                {
                    Width = 272,
                    Height = 272,
                    Center = 272 / 2,
                    Radius = 272 / 2 - 3,
                    Background = new SolidColorBrush(Colors.Transparent),
                    Maximum = ChatRecordSession.MaximumVideoDuration.TotalSeconds,
                    Value = DateTime.Now + ChatRecordSession.MaximumVideoDuration
                });

                _videoPopup = new Popup
                {
                    XamlRoot = XamlRoot,
                    IsHitTestVisible = false,
                    Child = new Border
                    {
                        Width = XamlRoot.Size.Width,
                        Height = XamlRoot.Size.Height,
                        Background = new SolidColorBrush(ActualTheme == ElementTheme.Light
                                ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
                                : Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
                        Child = videoRoot
                    }
                };

                _videoPopup.IsOpen = true;
                _ = mediaCapture.StartPreviewAsync();
            }
            catch
            {
                // As everything happens asynchronously, the media capture could be already disposed
            }
        }
#endif

        private void OnRecordingStarting(object sender, EventArgs e)
        {
            // TODO: video message
            Visibility = Visibility.Visible;

            var fill = ActualTheme == ElementTheme.Light
                    ? Theme.AccentLight.Default
                    : Theme.AccentDark.Default;

            if (_blobVisual != null)
            {
                _blobVisual.FillColor = fill;
            }

#if LINUX
            Blob.Background = new SolidColorBrush(fill);
#endif

            StartBlob();

#if !LINUX
            if (_videoPopup != null)
            {
                _videoPopup.IsOpen = false;
                _videoPopup = null;

                _videoElement.Source = null;
                _videoElement = null;
            }
#endif

            ChatRecordPopup.IsHitTestVisible = false;
            ChatRecordPopup.IsOpen = true;
            ChatRecordGlyph.Text = ControlledButton.Mode == ChatRecordMode.Video
                ? Icons.VideoNoteFilled24
                : Icons.MicOnFilled24;
            ChatRecordGlyph.FontSize = 24;

            var slideWidth = SlidePanel.ActualSize.X;
            var elapsedWidth = ElapsedPanel.ActualSize.X;

            _slideVisual.Opacity = 1;

            var compositor = BootStrapper.Current.Compositor;

            var batch = BootStrapper.Current.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            void BatchCompleted()
            {
                _elapsedTimer.Start();
                AttachExpression();
            }

#if !LINUX
            batch.Completed += (s, args) => BatchCompleted();
#endif

            var slideAnimation = compositor.CreateScalarKeyFrameAnimation();
            slideAnimation.InsertKeyFrame(0, slideWidth + 36);
            slideAnimation.InsertKeyFrame(1, 0);
            slideAnimation.Duration = TimeSpan.FromMilliseconds(300);

            var elapsedAnimation = compositor.CreateScalarKeyFrameAnimation();
            elapsedAnimation.InsertKeyFrame(0, -elapsedWidth);
            elapsedAnimation.InsertKeyFrame(1, 0);
            elapsedAnimation.Duration = TimeSpan.FromMilliseconds(300);

            var visibleAnimation = compositor.CreateScalarKeyFrameAnimation();
            visibleAnimation.InsertKeyFrame(0, 0);
            visibleAnimation.InsertKeyFrame(1, 1);

            var ellipseAnimation = compositor.CreateVector3KeyFrameAnimation();
            ellipseAnimation.InsertKeyFrame(0, new Vector3(56f / 96f));
            ellipseAnimation.InsertKeyFrame(1, new Vector3(1));
            ellipseAnimation.Duration = TimeSpan.FromMilliseconds(200);

            _slideVisual.StartAnimation("Offset.X", slideAnimation);
            _elapsedVisual.StartAnimation("Offset.X", elapsedAnimation);
            _recordVisual.StartAnimation("Opacity", visibleAnimation);
            _ellipseVisual.StartAnimation("Scale", ellipseAnimation);

#if LINUX
            // CompositionScopedBatch.Completed never fires in Uno (PORTING.md 6), and this handler
            // is the one that starts the elapsed clock and attaches the two ExpressionAnimations of
            // the slider and the ellipse. Without it the counter sat at "0:00,0" for the whole
            // recording. 300 ms is what slideAnimation and elapsedAnimation last.
            batch.EndWithCompleted(TimeSpan.FromMilliseconds(300), BatchCompleted);
#else
            batch.End();
#endif

            StartTyping?.Invoke(this, ControlledButton.IsChecked.Value
                ? new ChatActionRecordingVideoNote()
                : new ChatActionRecordingVoiceNote());
        }

        public event EventHandler<ChatAction> StartTyping;
        public event EventHandler CancelTyping;

#if !LINUX
        private async Task<Brush> LoadLastFrameAsync()
        {
            try
            {
                var file = await ApplicationData.Current.TemporaryFolder.TryGetItemAsync("LastVideoFrame.png");
                if (file != null)
                {
                    var source = new SoftwareBitmapSource();

                    try
                    {
                        var bitmap = await Task.Run(() => Direct2D.Shared.DrawBlurred(file.Path, 3));
                        await source.SetBitmapAsync(bitmap);
                    }
                    catch { }

                    return new ImageBrush
                    {
                        ImageSource = source
                    };
                }
            }
            catch
            {
                // Catching as this would break the UI otherwise
            }

            return new SolidColorBrush(Colors.Black);
        }

        private async Task SaveLastFrameAsync()
        {
            try
            {
                var element = _videoElement;
                if (element == null || !element.IsConnected())
                {
                    return;
                }

                var target = new RenderTargetBitmap();
                await target.RenderAsync(_videoElement, 80, 80);
                var pixels = await target.GetPixelsAsync();

                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync("LastVideoFrame.png", CreationCollisionOption.ReplaceExisting);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

                var width = (uint)target.PixelWidth;
                var height = (uint)target.PixelHeight;

                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels.ToArray());
                encoder.BitmapTransform.Flip = BitmapFlip.Horizontal;

                await encoder.FlushAsync();
            }
            catch
            {
                // Catching as this would break the UI otherwise
            }
        }
#endif

        private async void OnRecordingStopped(object sender, EventArgs e)
        {
            //if (btnVoiceMessage.IsLocked)
            //{
            //    Poggers.Visibility = Visibility.Visible;
            //    Poggers.UpdateWaveform(btnVoiceMessage.GetWaveform());
            //    return;
            //}

            _viewOnceToast?.IsOpen = false;
            _viewOnceToast = null;

            _blobVisual?.StopAnimating();
#if LINUX
            StopBlobPulse();
#endif

#if !LINUX
            await SaveLastFrameAsync();

            if (_videoPopup != null)
            {
                _videoPopup.IsOpen = false;
                _videoPopup = null;

                _videoElement.Source = null;
                _videoElement = null;
            }
#endif

            AttachExpression();

            var slidePosition = ActualSize.X - 48 - 36;
            var difference = slidePosition - ElapsedPanel.ActualSize.X;

            var batch = BootStrapper.Current.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            void BatchCompleted()
            {
                _elapsedTimer.Stop();

                DetachExpression();

                ChatRecordPopup.IsOpen = false;

                Visibility = Visibility.Collapsed;
                ButtonCancelRecording.Visibility = Visibility.Collapsed;
                ViewOnceRoot.Visibility = Visibility.Collapsed;
                ViewOnceButton.IsChecked = false;
                PauseRoot.Visibility = Visibility.Collapsed;
                PauseButton.IsChecked = false;
                ElapsedLabel.Text = "0:00,0";

                WaveformLabel.Text = string.Empty;
                Waveform.Visibility = Visibility.Collapsed;

                ElementCompositionPreview.SetElementChildVisual(WaveformBackground, null);
                ChatRecordGlyph.Foreground = new SolidColorBrush(Colors.White);

#if LINUX
                // Stop before writing. An ended KeyFrameAnimation still owns its property in Uno
                // and RenderRootVisual re-evaluates it on every composed frame, so these three
                // assignments would never take (PORTING.md 6).
                _slideVisual.StopAnimation("Offset.X");
                _elapsedVisual.StopAnimation("Offset.X");
                _recordVisual.StopAnimation("Opacity");
#endif

                var point = _slideVisual.Offset;
                point.X = _slideVisual.Size.X + 36;

                _slideVisual.Opacity = 0;
                _slideVisual.Offset = point;

                point = _elapsedVisual.Offset;
                point.X = -_elapsedVisual.Size.X;

                _elapsedVisual.Offset = point;

                _ellipseVisual.Properties.TryGetVector3("Translation", out point);
                point.Y = 0;

                _ellipseVisual.Properties.InsertVector3("Translation", point);
            }

#if !LINUX
            batch.Completed += (s, args) => BatchCompleted();
#endif

            var slideAnimation = BootStrapper.Current.Compositor.CreateScalarKeyFrameAnimation();
            slideAnimation.InsertKeyFrame(0, _slideVisual.Offset.X);
            slideAnimation.InsertKeyFrame(1, -slidePosition);
            slideAnimation.Duration = TimeSpan.FromMilliseconds(200);

            var visibleAnimation = BootStrapper.Current.Compositor.CreateScalarKeyFrameAnimation();
            visibleAnimation.InsertKeyFrame(0, 1);
            visibleAnimation.InsertKeyFrame(1, 0);

            _slideVisual.StartAnimation("Offset.X", slideAnimation);
            _recordVisual.StartAnimation("Opacity", visibleAnimation);

            if (ViewOnceRoot.Visibility == Visibility.Visible)
            {
                var viewOnce = ElementComposition.GetElementVisual(ViewOnceRoot);
                viewOnce.CenterPoint = new Vector3(18);

                var scale = BootStrapper.Current.Compositor.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(0, Vector3.One);
                scale.InsertKeyFrame(1, Vector3.Zero);

                viewOnce.StartAnimation("Scale", scale);
            }

            if (PauseRoot.Visibility == Visibility.Visible)
            {
                var pause = ElementComposition.GetElementVisual(PauseRoot);
                pause.CenterPoint = new Vector3(18);

                var scale = BootStrapper.Current.Compositor.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(0, Vector3.One);
                scale.InsertKeyFrame(1, Vector3.Zero);

                pause.StartAnimation("Scale", scale);
            }

#if LINUX
            // Same as above: without a replacement for Completed the bar never took itself down -
            // it stayed on top of the composer with its popup open, the elapsed timer running and
            // the two ExpressionAnimations still attached. 300 ms covers slideAnimation (200) and
            // the two scale animations, which carry no Duration and therefore take one frame.
            batch.EndWithCompleted(TimeSpan.FromMilliseconds(300), BatchCompleted);
#else
            batch.End();
#endif

            ShowHideDelete(false);

            CancelTyping?.Invoke(this, EventArgs.Empty);
        }

        private void OnRecordingLocked(object sender, EventArgs e)
        {
            ChatRecordGlyph.Text = Icons.SendFilled;
            ChatRecordPopup.IsHitTestVisible = true;

            DetachExpression();

            var ellipseAnimation = BootStrapper.Current.Compositor.CreateScalarKeyFrameAnimation();
            ellipseAnimation.InsertKeyFrame(0, -57);
            ellipseAnimation.InsertKeyFrame(1, 0);

            _ellipseVisual.StartAnimation("Translation.Y", ellipseAnimation);

            ButtonCancelRecording.Visibility = Visibility.Visible;
            ControlledButton.Focus(FocusState.Programmatic);

            var point = _slideVisual.Offset;
            point.X = _slideVisual.Size.X + 36;

            _slideVisual.Opacity = 0;
            _slideVisual.Offset = point;

            ViewOnceRoot.Visibility = Visibility.Visible;
            PauseRoot.Visibility = Visibility.Visible;

            var batch = BootStrapper.Current.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            void BatchCompleted()
            {
                if (AppSettings.ToolTip.Increment("NotesViewOnce"))
                {
                    _viewOnceToast = ToastPopup.Show(ViewOnceRoot, ControlledButton.Mode == ChatRecordMode.Voice ? Strings.VoiceSetOnceHint : Strings.VideoSetOnceHint, TeachingTipPlacementMode.Right, dismissAfter: TimeSpan.FromSeconds(3));
                }
            }

#if !LINUX
            batch.Completed += (s, args) => BatchCompleted();
#endif

            var viewOnce = ElementComposition.GetElementVisual(ViewOnceRoot);
            viewOnce.CenterPoint = new Vector3(18);

            var pause = ElementComposition.GetElementVisual(PauseRoot);
            pause.CenterPoint = new Vector3(18);

            // One instance per visual: the same Vector3KeyFrameAnimation used to be started on
            // pause AND on viewOnce, which is
            // "ArgumentException: An item with the same key has already been added" from
            // Compositor.RegisterAnimation, and it took the "view once" and "pause" buttons with
            // it every time the recording was locked (PORTING.md 6). Twelfth site of this shape.
            Vector3KeyFrameAnimation Scale()
            {
                var instance = BootStrapper.Current.Compositor.CreateVector3KeyFrameAnimation();
                instance.InsertKeyFrame(0, Vector3.Zero);
                instance.InsertKeyFrame(1, Vector3.One);
                return instance;
            }

            pause.StartAnimation("Scale", Scale());
            viewOnce.StartAnimation("Scale", Scale());

#if LINUX
            batch.EndWithCompleted(Constants.FastAnimation, BatchCompleted);
#else
            batch.End();
#endif
        }

        private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            Vector3 point;
            if (ControlledButton.IsLocked || !ControlledButton.IsRecording)
            {
                point = _slideVisual.Offset;
                point.X = 0;

                _slideVisual.Offset = point;

                _ellipseVisual.Properties.TryGetVector3("Translation", out point);
                point.Y = 0;

                _ellipseVisual.Properties.InsertVector3("Translation", point);

                return;
            }

            var cumulative = e.Cumulative.Translation.ToVector2();
            point = _slideVisual.Offset;
            point.X = Math.Min(0, cumulative.X);

            _slideVisual.Offset = point;

            if (point.X < -80)
            {
                e.Complete();
                ControlledButton.Cancel();
                return;
            }

            _ellipseVisual.Properties.TryGetVector3("Translation", out point);
            point.Y = Math.Min(0, cumulative.Y);

            _ellipseVisual.Properties.InsertVector3("Translation", point);

            if (point.Y < -120)
            {
                e.Complete();
                ControlledButton.Lock();
            }
        }

        private void AttachExpression()
        {
            var elapsedExpression = BootStrapper.Current.Compositor.CreateExpressionAnimation("min(0, slide.Offset.X + ((root.Size.X - 48 - 36 - slide.Size.X) - elapsed.Size.X))");
            elapsedExpression.SetReferenceParameter("slide", _slideVisual);
            elapsedExpression.SetReferenceParameter("elapsed", _elapsedVisual);
            elapsedExpression.SetReferenceParameter("root", _rootVisual);

            var ellipseExpression = BootStrapper.Current.Compositor.CreateExpressionAnimation("Vector3(max(0, min(1, 1 + slide.Offset.X / (root.Size.X - 48 - 36))), max(0, min(1, 1 + slide.Offset.X / (root.Size.X - 48 - 36))), 1)");
            ellipseExpression.SetReferenceParameter("slide", _slideVisual);
            ellipseExpression.SetReferenceParameter("elapsed", _elapsedVisual);
            ellipseExpression.SetReferenceParameter("root", _rootVisual);

            _elapsedVisual.StopAnimation("Offset.X");
            _elapsedVisual.StartAnimation("Offset.X", elapsedExpression);

            _ellipseVisual.StopAnimation("Scale");
            _ellipseVisual.StartAnimation("Scale", ellipseExpression);
        }

        private void DetachExpression()
        {
            _elapsedVisual.StopAnimation("Offset.X");
            _ellipseVisual.StopAnimation("Scale");
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _rootVisual.Size = e.NewSize.ToVector2();
        }

        private ToastPopup _viewOnceToast;

        private void ViewOnce_Click(object sender, RoutedEventArgs e)
        {
            ControlledButton.IsViewOnce = ViewOnceButton.IsChecked is true;

            if (ControlledButton.IsViewOnce)
            {
                _viewOnceToast = ToastPopup.Show(ViewOnceRoot, ControlledButton.Mode == ChatRecordMode.Voice ? Strings.VoiceSetOnceHintEnabled : Strings.VideoSetOnceHintEnabled, TeachingTipPlacementMode.Right, dismissAfter: TimeSpan.FromSeconds(3));
            }
            else
            {
                _viewOnceToast?.IsOpen = false;
                _viewOnceToast = null;
            }
        }

        public void Pause()
        {
            Pause_Click(null, null);
        }

        private async void Pause_Click(object sender, RoutedEventArgs e)
        {
            _elapsedTimer.Stop();

            var result = await ControlledButton.TogglePauseAsync();
            if (result != null)
            {
                _blobVisual?.StopAnimating();
#if LINUX
                StopBlobPulse();
#endif

                WaveformLabel.Text = result.Duration.ToString("m\\:ss");
                Waveform.Visibility = Visibility.Visible;
                Waveform.UpdateWaveform(result.Waveform, -1);

                var compositor = BootStrapper.Current.Compositor;
                var ellipse = compositor.CreateRoundedRectangleGeometry();
                ellipse.CornerRadius = new Vector2(WaveformBackground.ActualSize.Y / 2);
                ellipse.Size = new Vector2(WaveformBackground.ActualSize.Y, WaveformBackground.ActualSize.Y);

                var shape = compositor.CreateSpriteShape(ellipse);
                shape.FillBrush = compositor.CreateColorBrush(ActualTheme == ElementTheme.Light
                    ? Theme.AccentLight.Default
                    : Theme.AccentDark.Default);

                var visual = compositor.CreateShapeVisual();
                visual.Size = WaveformBackground.ActualSize;
                visual.Shapes.Add(shape);

                var width = compositor.CreateScalarKeyFrameAnimation();
                width.InsertKeyFrame(0, WaveformBackground.ActualSize.Y);
                width.InsertKeyFrame(1, WaveformBackground.ActualSize.X - 48);

                var offset = compositor.CreateScalarKeyFrameAnimation();
                offset.InsertKeyFrame(0, WaveformBackground.ActualSize.X - 44);
                offset.InsertKeyFrame(1, 0);

                ellipse.StartAnimation("Size.X", width);
                ellipse.StartAnimation("Offset.X", offset);

                var viewOnce = ElementComposition.GetElementVisual(ViewOnceRoot);
                var pause = ElementComposition.GetElementVisual(PauseRoot);
                ElementCompositionPreview.SetIsTranslationEnabled(ViewOnceRoot, true);
                ElementCompositionPreview.SetIsTranslationEnabled(PauseRoot, true);

                var translate = compositor.CreateScalarKeyFrameAnimation();
                translate.InsertKeyFrame(0, 0);
                translate.InsertKeyFrame(1, 20);

                viewOnce.StartAnimation("Translation.Y", translate);
                pause.StartAnimation("Translation.Y", translate);

                ElementCompositionPreview.SetElementChildVisual(WaveformBackground, visual);
                ChatRecordGlyph.Foreground = new SolidColorBrush(ActualTheme == ElementTheme.Light
                    ? Theme.AccentLight.Default
                    : Theme.AccentDark.Default);
                ChatRecordGlyph.Text = Icons.SendFilled32;
                ChatRecordGlyph.FontSize = 32;

                ShowHideDelete(true);
            }
            else
            {
                StartBlob();
                _elapsedTimer.Start();

                WaveformLabel.Text = string.Empty;
                Waveform.Visibility = Visibility.Collapsed;

                var compositor = BootStrapper.Current.Compositor;
                var ellipse = compositor.CreateRoundedRectangleGeometry();
                ellipse.CornerRadius = new Vector2(WaveformBackground.ActualSize.Y / 2);
                ellipse.Size = new Vector2(WaveformBackground.ActualSize.Y, WaveformBackground.ActualSize.Y);

                var shape = compositor.CreateSpriteShape(ellipse);
                shape.FillBrush = compositor.CreateColorBrush(ActualTheme == ElementTheme.Light
                    ? Theme.AccentLight.Default
                    : Theme.AccentDark.Default);

                var visual = compositor.CreateShapeVisual();
                visual.Size = WaveformBackground.ActualSize;
                visual.Shapes.Add(shape);

                var width = compositor.CreateScalarKeyFrameAnimation();
                width.InsertKeyFrame(1, WaveformBackground.ActualSize.Y);
                width.InsertKeyFrame(0, WaveformBackground.ActualSize.X - 48);

                var offset = compositor.CreateScalarKeyFrameAnimation();
                offset.InsertKeyFrame(1, WaveformBackground.ActualSize.X - 44);
                offset.InsertKeyFrame(0, 0);

                ellipse.StartAnimation("Size.X", width);
                ellipse.StartAnimation("Offset.X", offset);

                var viewOnce = ElementComposition.GetElementVisual(ViewOnceRoot);
                var pause = ElementComposition.GetElementVisual(PauseRoot);
                ElementCompositionPreview.SetIsTranslationEnabled(ViewOnceRoot, true);
                ElementCompositionPreview.SetIsTranslationEnabled(PauseRoot, true);

                var translate = compositor.CreateScalarKeyFrameAnimation();
                translate.InsertKeyFrame(1, 0);
                translate.InsertKeyFrame(0, 20);

                viewOnce.StartAnimation("Translation.Y", translate);
                pause.StartAnimation("Translation.Y", translate);

                ElementCompositionPreview.SetElementChildVisual(WaveformBackground, visual);
                ChatRecordGlyph.Foreground = new SolidColorBrush(Colors.White);
                ChatRecordGlyph.Text = Icons.SendFilled;
                ChatRecordGlyph.FontSize = 24;

                ShowHideDelete(false);
            }
        }

        /// <summary>
        /// Starts the blob that pulses with the microphone level, when the platform can draw it.
        /// </summary>
        private void StartBlob()
        {
#if LINUX
            // The wobble stays off: it is a CompositionPathGeometry driven by a
            // PathKeyFrameAnimation, and Compositor.CreatePathKeyFrameAnimation is the one call
            // Uno answers with NotImplementedException (ApiInfo.CanAnimatePaths, measured).
            //
            // What used to be off with it was ALL the feedback: the circle just sat there, and
            // that circle is the only thing on screen that says the microphone is hearing
            // anything. So the level drives it another way. Three measured facts pick the shape:
            //
            //  - a CompositionShape cannot be animated at all (no SetAnimatableProperty override,
            //    and Compositor.RegisterAnimation returns early unless the target `is Visual`),
            //    which is also why CompositionBlobShape.Level - the upstream path for exactly this
            //    - does nothing here: it animates _shape.Scale;
            //  - a Visual CAN be animated, and Scale is one of the ten names it accepts;
            //  - and a KeyFrameAnimation is the wrong tool anyway, because the level is not known
            //    in advance. So it is written by hand, once a frame, and the animation is stopped
            //    before the first write - PORTING.md 6: a KeyFrameAnimation that has finished
            //    still owns its property in Uno and quietly eats what you assign.
            //
            // The clock is CompositionVSync, which already routes to
            // Telegram.Common.CompositionRenderingClock under #if LINUX, because
            // CompositionTarget.Rendering here follows whatever is invalidating the window.
            _blobVisual?.Clear();
            _blobVisual?.Collapse();

            // Upstream gates on AreMaterialsEnabled && CanAnimatePaths. Only the first half of
            // that carries over: CanAnimatePaths is a capability gate for the path animation this
            // no longer uses, while AreMaterialsEnabled is the user's own "no decorative
            // animation" preference and still means what it says.
            if (PowerSavingPolicy.AreMaterialsEnabled)
            {
                StartBlobPulse();
            }
#else
            if (PowerSavingPolicy.AreMaterialsEnabled && ApiInfo.CanAnimatePaths)
            {
                _blobVisual.StartAnimating();
            }
            else
            {
                _blobVisual.Clear();
            }
#endif
        }

#if LINUX
        /// <summary>
        /// Starts writing the microphone level onto the recording circle, once a composed frame.
        /// </summary>
        /// <remarks>
        /// The circle is the <c>Blob</c> border itself: the constructor gives it the 80 px and the
        /// 40 px corner radius the resting blob has, and <c>OnRecordingStarting</c> paints it with
        /// the accent colour. Scaling its own visual therefore costs no new geometry and cannot
        /// land anywhere except on top of the glyph, which is what makes this safe to reason about
        /// without a screen. Scale, not Size: <c>Size</c> would re-run layout for every frame of
        /// the recording, and on a composition Visual it is the same money as Scale anyway.
        /// </remarks>
        private void StartBlobPulse()
        {
            if (_blobPulsing)
            {
                return;
            }

            _blobPulsing = true;
            _blobPresentationLevel = 0;

            _blobCircleVisual ??= ElementComposition.GetElementVisual(Blob);
            _blobCircleVisual.CenterPoint = new Vector3(40, 40, 0);
            _blobCircleVisual.StopAnimation("Scale");
            _blobCircleVisual.Scale = Vector3.One;

            _blobVSync.Rendering += OnBlobRendering;
        }

        private void StopBlobPulse()
        {
            if (!_blobPulsing)
            {
                return;
            }

            _blobPulsing = false;
            _blobVSync.Rendering -= OnBlobRendering;

            if (_blobCircleVisual != null)
            {
                _blobCircleVisual.StopAnimation("Scale");
                _blobCircleVisual.Scale = Vector3.One;
            }
        }

        private void OnBlobRendering(object sender, EventArgs e)
        {
            // The same one-pole filter CompositionBlobVisual.OnRendering uses, and for the same
            // reason: the capture thread delivers roughly 40 levels a second and they rattle, so
            // the circle follows the voice instead of buzzing with it.
            _blobPresentationLevel = _blobPresentationLevel * 0.9f + _blobLevel * 0.1f;

            // 1.0 at silence up to 1.24 at the top. That is the 0.45 -> 0.55 span the small blob
            // travels upstream, carried over as a scale of the circle rather than of a shape.
            var scale = 1f + 0.24f * _blobPresentationLevel;
            _blobCircleVisual.Scale = new Vector3(scale, scale, 1);
        }
#endif

        private bool _deleteCollapsed = true;

        private void ShowHideDelete(bool show)
        {
            if (_deleteCollapsed != show)
            {
                return;
            }

            _deleteCollapsed = !show;
            DeleteButton.Visibility = Visibility.Visible;

            var visual1 = ElementComposition.GetElementVisual(DeleteButton);
            var visual2 = ElementComposition.GetElementVisual(RecordGlyph);

            visual1.CenterPoint = new Vector3(24);
            visual2.CenterPoint = new Vector3(6);

            var batch = visual1.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            void BatchCompleted()
            {
                DeleteButton.Visibility = _deleteCollapsed
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

#if !LINUX
            batch.Completed += (s, args) => BatchCompleted();
#endif

            var scale1 = visual1.Compositor.CreateVector3KeyFrameAnimation();
            scale1.InsertKeyFrame(show ? 0 : 1, Vector3.Zero);
            scale1.InsertKeyFrame(show ? 1 : 0, Vector3.One);

            var scale2 = visual1.Compositor.CreateVector3KeyFrameAnimation();
            scale2.InsertKeyFrame(show ? 0 : 1, Vector3.One);
            scale2.InsertKeyFrame(show ? 1 : 0, Vector3.Zero);

            visual1.StartAnimation("Scale", scale1);
            visual2.StartAnimation("Scale", scale2);

#if LINUX
            batch.EndWithCompleted(Constants.FastAnimation, BatchCompleted);
#else
            batch.End();
#endif
        }
    }
}
