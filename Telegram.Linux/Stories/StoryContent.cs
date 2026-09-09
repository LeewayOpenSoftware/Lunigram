//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// One story card: the 9:16 rectangle the viewer shows, with its progress bars, its header, its
// photo or video, and the gestures that move between stories.
//
// Same class name and namespace as Controls/Stories/StoryContent.xaml.cs, which this head does NOT
// compile. Four things in that file have no equivalent here and three of them belong to other
// batches, so the card is rebuilt rather than patched:
//
//   * the video is a <SwapChainPanel x:Name="Video"> fed by an AsyncMediaPlayer with
//     CreateSwapChain = true. SwapChainPanel is on the not-implemented list of PORTING.md 6, and
//     Telegram.Linux's AsyncMediaPlayerSwapChain.Attach() is an empty method: mpv runs vid=no in
//     this port, it is the sound and the clock and never the pixels. The pixels come from
//     LinuxVideoPlayer, the gallery's player, through StoryGalleryVideo next door.
//   * the reply box is a FormattedTextBox, i.e. RichEditBox.Document, which Uno Skia does not
//     implement; the composer is another batch's area besides.
//   * the sticker/emoji panel is StickerPanel, likewise out of the subset and another batch's.
//   * the live-broadcast half is VoipGroupCall, i.e. the calls subsystem, which is not in this head
//     at all (only VoipCallBase is). A live story therefore falls back to its poster and caption,
//     which is the honest answer, rather than a black card.
//
// The tree is built in C# instead of XAML on purpose: no .xaml means no Uno XAML compiler pass to
// get right blind, and it is the same move Controls/StartupSwitch.xaml.cs already had to make in
// this port for its own reasons.
//
// The three animation traps of PORTING.md 6 that this screen walks into are dealt with by not
// starting a single composition animation anywhere in it:
//   - one CompositionAnimation instance is never shared between visuals -> there are none;
//   - a KeyFrameAnimation with no Duration is TimeSpan.Zero and jumps to its last keyframe -> there
//     are none;
//   - an animation started on a visual with no CompositionTarget, or on anything that is not a
//     Visual at all, is dropped in silence and leaves the property at keyframe 0 -> there are none.
// Everything that moves (the progress bar, the chrome fading out while the finger is down) is a
// value written per frame from Telegram.Common.CompositionRenderingClock onto a RenderTransform or
// an Opacity, which is the shape the rest of this port uses and which needs no compositor
// registration.

using System;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Converters;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Telegram.Controls.Stories
{
    public sealed partial class StoryContent : UserControlEx
    {
        #region Tree

        private readonly Grid _root;

        private readonly ImageView _photo;
        private readonly Border _videoPanel;

        private readonly Grid _activeRoot;
        private Rectangle _shade;
        private Grid _info;
        private readonly StoryProgress _progress;
        private readonly ProfilePicture _avatar;
        private readonly TextBlock _title;
        private readonly TextBlock _subtitle;
        private readonly GlyphToggleButton _mute;
        private readonly GlyphButton _more;
        private readonly FormattedTextBlock _caption;
        private readonly Border _captionRoot;

        private readonly Grid _inactiveRoot;
        private readonly ProfilePicture _avatarMini;
        private readonly TextBlock _titleMini;

        #endregion

        private LinuxVideoPlayer _player;
        private StoryGalleryVideo _video;

        private ActiveStoriesViewModel _viewModel;
        public ActiveStoriesViewModel ViewModel => _viewModel;

        private long _chatId;
        private int _storyId;

        // What openStory was last sent for, so closeStory is sent for the same pair and never for a
        // story that was never opened. Upstream keeps the same two fields.
        private long _openedChatId;
        private int _openedStoryId;

        private bool _open;

        // The slot this card occupies in the window's ring of seven (3 = the middle one). Kept
        // because the window passes it and because it is what tells a card two places to the left
        // from the one immediately to the left, which is what a tap on a side card has to know.
        private int _index;

        private StoryType _type;
        private StoryPauseSource _state;

        private bool _unloaded;

        /// <summary>The current story ran to its end.</summary>
        public event EventHandler Completed;

        /// <summary>A tap on the left of an open card.</summary>
        public event EventHandler PreviousRequested;

        /// <summary>A tap on the right of an open card, and what the end of a story turns into.</summary>
        public event EventHandler NextRequested;

        /// <summary>The "..." button. Same event name and args as upstream.</summary>
        public event EventHandler<StoryEventArgs> MoreClick;

        /// <summary>A tap on a card that is NOT the open one: the window jumps to that author.</summary>
        public event RoutedEventHandler Click;

        public StoryContent()
        {
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;

            _photo = new ImageView
            {
                Stretch = Stretch.UniformToFill,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            _videoPanel = new Border();

            _progress = new StoryProgress
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 6, 6, 0)
            };
            _progress.Completed += OnProgressCompleted;

            _avatar = new ProfilePicture
            {
                Size = 32,
                Width = 32,
                Height = 32,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            _title = new TextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };

            _subtitle = new TextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.7 },
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };

            _mute = new GlyphToggleButton
            {
                Glyph = Icons.Speaker2,
                CheckedGlyph = Icons.SpeakerMuteFilled,
                Width = 32,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            _mute.Click += Mute_Click;

            _more = new GlyphButton
            {
                Glyph = Icons.MoreHorizontal,
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _more.Click += More_Click;

            _caption = new FormattedTextBlock
            {
                Margin = new Thickness(12, 6, 12, 12),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            };

            _captionRoot = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed,
                Child = _caption
            };

            _avatarMini = new ProfilePicture
            {
                Size = 48,
                Width = 48,
                Height = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _titleMini = new TextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Margin = new Thickness(4, 8, 4, 0)
            };

            _activeRoot = BuildActiveRoot();
            _inactiveRoot = BuildInactiveRoot();

            var media = new Grid();
            media.Children.Add(_photo);
            media.Children.Add(_videoPanel);

            _root = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
                CornerRadius = new CornerRadius(8)
            };

            _root.Children.Add(media);
            _root.Children.Add(_activeRoot);
            _root.Children.Add(_inactiveRoot);

            Content = _root;

            Disconnected += OnDisconnected;
        }

        private Grid BuildActiveRoot()
        {
            var root = new Grid
            {
                // Hit-testable, otherwise neither the tap zones nor the hold-to-pause see a pointer.
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };

            // The dark wash under the header, so a white name stays readable over a bright photo.
            var shade = new Rectangle
            {
                Height = 72,
                Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 1),
                    EndPoint = new Point(0, 0),
                    GradientStops =
                    {
                        new GradientStop { Color = Microsoft.UI.Colors.Transparent, Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(0xAA, 0, 0, 0), Offset = 1 }
                    }
                }
            };

            var info = new Grid
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 18, 0, 0),
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition(),
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

            var names = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            names.Children.Add(_title);
            names.Children.Add(_subtitle);

            Grid.SetColumn(_avatar, 0);
            Grid.SetColumn(names, 1);
            Grid.SetColumn(_mute, 2);
            Grid.SetColumn(_more, 3);

            info.Children.Add(_avatar);
            info.Children.Add(names);
            info.Children.Add(_mute);
            info.Children.Add(_more);

            _shade = shade;
            _info = info;

            root.Children.Add(shade);
            root.Children.Add(_progress);
            root.Children.Add(info);
            root.Children.Add(_captionRoot);

            root.PointerPressed += OnPointerPressed;
            root.PointerReleased += OnPointerReleased;
            root.PointerCanceled += OnPointerCanceled;
            root.PointerCaptureLost += OnPointerCanceled;

            return root;
        }

        private Grid BuildInactiveRoot()
        {
            var root = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Visibility = Visibility.Collapsed
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(_avatarMini);
            stack.Children.Add(_titleMini);

            root.Children.Add(stack);
            root.Tapped += OnInactiveTapped;

            return root;
        }

        private void OnInactiveTapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            Click?.Invoke(this, new RoutedEventArgs());
        }

        private void OnDisconnected(object sender, RoutedEventArgs e)
        {
            _unloaded = true;

            _progress.Stop();
            CloseStory();
            DisposePlayer();

            UpdateManager.Unsubscribe(this, ref _photoToken);
        }

        #region Update

        /// <summary>
        /// Same signature as upstream: which author this card shows, whether it is the one in the
        /// middle, and the slot it occupies (3 = middle). The window calls this for all of its cards
        /// on every move.
        /// </summary>
        public void Update(ActiveStoriesViewModel activeStories, bool open, int index)
        {
            if (activeStories == null)
            {
                return;
            }

            _unloaded = false;
            _viewModel = activeStories;
            _index = index;

            var chat = activeStories.Chat;
            if (chat != null && chat.Id != _chatId)
            {
                _chatId = chat.Id;
                _storyId = 0;
                _state = StoryPauseSource.None;

                if (activeStories.ClientService.TryGetUser(chat, out User user))
                {
                    var name = activeStories.IsMyStory ? Strings.SelfStoryTitle : user.FirstName;

                    _title.Text = name;
                    _titleMini.Text = name;

                    _avatar.Source = ProfilePictureSource.User(activeStories.ClientService, user);
                    _avatarMini.Source = ProfilePictureSource.User(activeStories.ClientService, user);
                }
                else
                {
                    _title.Text = chat.Title;
                    _titleMini.Text = chat.Title;

                    _avatar.Source = ProfilePictureSource.Chat(activeStories.ClientService, chat);
                    _avatarMini.Source = ProfilePictureSource.Chat(activeStories.ClientService, chat);
                }
            }

            _activeRoot.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            _inactiveRoot.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            Canvas.SetZIndex(_activeRoot, open ? 1 : 0);

            var story = activeStories.SelectedItem;
            if (story == null)
            {
                return;
            }

            var changed = story.Id != _storyId;

            if (changed)
            {
                _state = StoryPauseSource.None;

                // Same warm-up upstream does, and in the same order: the ten stories after this one
                // get a Prepare (thumbnail + metadata), everything else a Load. Reverse, because
                // TDLib's download queue is LIFO and the first request sent is the last served.
                var position = activeStories.Items.IndexOf(story);
                var limit = Math.Min(position + 10, activeStories.Items.Count);

                for (int i = activeStories.Items.Count - 1; i >= 0; i--)
                {
                    if (i > position && i < limit)
                    {
                        activeStories.Items[i].Prepare();
                    }
                    else
                    {
                        activeStories.Items[i].Load();
                    }
                }
            }

            if (open && (changed || !_open))
            {
                Activate(story);
            }
            else if (_open && !open)
            {
                Deactivate();
            }
            else if (changed)
            {
                // A card that is not the open one still shows the right poster; nothing to play.
                UpdateHeader(story);
            }

            _open = open;
            _storyId = story.Id;
        }

        private void UpdateHeader(StoryViewModel story)
        {
            _subtitle.Text = story.Date != 0
                ? Locale.FormatRelativeShort(story.Date)
                : string.Format("{0}/{1}", 1 + _viewModel.Items.IndexOf(story), _viewModel.Items.Count);

            if (string.IsNullOrEmpty(story.Caption?.Text))
            {
                _captionRoot.Visibility = Visibility.Collapsed;
                _caption.Clear();
            }
            else
            {
                _captionRoot.Visibility = Visibility.Visible;
                _caption.SetText(story.ClientService, story.Caption);
            }
        }

        /// <summary>
        /// Make this story the one that is playing: paint the header, put the media on screen, tell
        /// TDLib the story was opened, and start the bar.
        /// </summary>
        private void Activate(StoryViewModel story)
        {
            if (_unloaded)
            {
                return;
            }

            CloseStory();
            UpdateHeader(story);

            // Clamped, not raw: IndexOf answers -1 for a SelectedItem that is not in the list, and a
            // progress row whose "current" bar does not exist never finishes -- which would leave
            // the viewer parked on that story with no way forward but a tap.
            var index = Math.Max(0, _viewModel.Items.IndexOf(story));
            var count = Math.Max(1, _viewModel.Items.Count);

            if (story.Content is StoryContentVideo videoContent)
            {
                var video = videoContent.Video;

                _type = StoryType.Video;
                _video = new StoryGalleryVideo(story.ClientService, video, story.Caption);

                UpdateManager.Unsubscribe(this, ref _photoToken);

                _mute.Visibility = Visibility.Visible;
                _mute.IsEnabled = !video.IsAnimation;
                _mute.IsChecked = video.IsAnimation || AppSettings.VolumeMuted;

                // The poster frame stays underneath until the first decoded frame lands on top of
                // it, so the card is never black while the file streams in.
                _photo.SetSource(story.ClientService, video.Thumbnail?.File, video.Minithumbnail);

                _progress.Update(index, count, _video.PreciseDuration);

                PlayVideo(story);
            }
            else if (story.Content is StoryContentPhoto photoContent)
            {
                _type = StoryType.Photo;
                _video = null;

                _mute.Visibility = Visibility.Collapsed;

                DisposePlayer();

                var big = photoContent.Photo.GetBig();

                // ImageView owns the download and the minithumbnail placeholder; that is the same
                // path the chat cells and the gallery already use in this head.
                _photo.SetSource(story.ClientService, big?.Photo, photoContent.Photo.Minithumbnail);

                _progress.Update(index, count, StoryProgress.PhotoDurationSeconds);

                // The five seconds start when the photo is actually there, not when the story is
                // selected -- upstream starts its timer from Texture_ImageOpened for the same
                // reason. Without this, a story whose file is still downloading spends part of its
                // five seconds as a blurred minithumbnail and then jumps to the next one.
                // ImageView has no "opened" event to hook, so the file itself is watched; a second
                // subscriber on the same file is fine, UpdateManager routes by token.
                UpdateManager.Unsubscribe(this, ref _photoToken);

                if (big == null || big.Photo.Local.IsDownloadingCompleted)
                {
                    OpenStory(story);
                    _progress.Begin();
                }
                else
                {
                    UpdateManager.Subscribe(this, story.ClientService, big.Photo, ref _photoToken, OnPhotoReady, true);
                }
            }
            else
            {
                // A live broadcast, or a content type this head does not render. The poster and the
                // caption are shown and the bar is left empty rather than running against a length
                // nobody knows: the user can still move on with a tap or an arrow key.
                _type = StoryType.Photo;
                _video = null;

                _mute.Visibility = Visibility.Collapsed;

                DisposePlayer();
                UpdateManager.Unsubscribe(this, ref _photoToken);

                _photo.Clear();
                _progress.Update(-1, Math.Max(1, count), 0);

                OpenStory(story);
            }
        }

        private void Deactivate()
        {
            _progress.Stop();
            CloseStory();

            // The player is torn down and not merely paused. A LinuxVideoPlayer that is alive owns a
            // decode thread and a RemoteFileSource, and RemoteFileSource moves TDLib's single
            // download window per file every time it asks for bytes (see the streaming section of
            // PORTING.md): six parked side cards each holding one of those would be six readers
            // fighting the one that is actually on screen. Re-opening costs a seek into a file TDLib
            // has already cached.
            DisposePlayer();
        }

        #endregion

        private long _photoToken;

        /// <summary>
        /// The photo finished downloading (completionOnly, so this is the only call). Runs on the
        /// UpdateManager's dispatch, which is the UI thread for a file update.
        /// </summary>
        // 12.10.2 dropped the subscriber argument from UpdateHandler<T>.
        private void OnPhotoReady(File file)
        {
            if (_unloaded || _type != StoryType.Photo)
            {
                return;
            }

            var story = _viewModel?.SelectedItem;
            if (story == null)
            {
                return;
            }

            OpenStory(story);

            if (_state == StoryPauseSource.None)
            {
                _progress.Begin();
            }
        }

        #region Video

        private void PlayVideo(StoryViewModel story)
        {
            if (_player == null)
            {
                _player = new LinuxVideoPlayer();
                _player.PositionChanged += OnPositionChanged;
                _player.DurationChanged += OnDurationChanged;
                _player.FirstFrameReady += OnFirstFrameReady;
                _player.IsPlayingChanged += OnIsPlayingChanged;
                _player.Failed += OnFailed;

                _videoPanel.Child = _player;
            }

            _player.IsLoopingEnabled = _video.IsLoopingEnabled;
            _player.Mute = _video.IsAnimation || AppSettings.VolumeMuted;

            _player.Play(_video, 0);
        }

        private void DisposePlayer()
        {
            if (_player != null)
            {
                _player.PositionChanged -= OnPositionChanged;
                _player.DurationChanged -= OnDurationChanged;
                _player.FirstFrameReady -= OnFirstFrameReady;
                _player.IsPlayingChanged -= OnIsPlayingChanged;
                _player.Failed -= OnFailed;

                _player.Clear();

                _videoPanel.Child = null;
                _player = null;
            }
        }

        private void OnFirstFrameReady(VideoPlayerBase sender, EventArgs args)
        {
            // The first decoded frame is on screen: that is the moment the story is really being
            // watched, so that is when the bar starts and when TDLib is told.
            if (_type != StoryType.Video || _unloaded)
            {
                return;
            }

            var story = _viewModel?.SelectedItem;
            if (story != null)
            {
                OpenStory(story);
            }

            if (_state == StoryPauseSource.None)
            {
                _progress.Begin();
            }
        }

        private void OnPositionChanged(VideoPlayerBase sender, VideoPlayerPositionChangedEventArgs args)
        {
            if (_type == StoryType.Video)
            {
                _progress.Synchronize(args.Position);
            }
        }

        /// <summary>
        /// The length the demuxer read, which is the one to trust when TDLib announced nothing.
        /// A storyVideo whose duration comes through as 0 would otherwise leave the bar with no
        /// length, and a bar with no length never ends -- i.e. the viewer would sit on that story
        /// with no way out but a tap. Only the zero case re-arms the bar, so a video already
        /// running is never reset by a late correction of a second or two.
        /// </summary>
        private void OnDurationChanged(VideoPlayerBase sender, VideoPlayerDurationChangedEventArgs args)
        {
            if (_type != StoryType.Video || _unloaded || _video == null || args.Duration <= 0)
            {
                return;
            }

            if (_video.PreciseDuration > 0)
            {
                return;
            }

            var story = _viewModel?.SelectedItem;
            if (story == null)
            {
                return;
            }

            _progress.Update(_viewModel.Items.IndexOf(story), _viewModel.Items.Count, args.Duration);

            if (_state == StoryPauseSource.None)
            {
                _progress.Begin();
            }
        }

        private void OnIsPlayingChanged(VideoPlayerBase sender, VideoPlayerIsPlayingChangedEventArgs args)
        {
            // The player has no "ended" event: Restart() sets its private _ended and reports
            // IsPlaying = false, which is the same thing a pause reports. The two are told apart by
            // where the position is. This is only a backstop -- the bar reaching its end is what
            // normally moves the viewer on -- for a video whose real length is shorter than the
            // duration TDLib announced.
            if (args.IsPlaying || _type != StoryType.Video || _unloaded || _video == null)
            {
                return;
            }

            // Whichever length is known: TDLib's if it announced one, the demuxer's otherwise.
            var duration = _video.PreciseDuration > 0 ? _video.PreciseDuration : sender.Duration;

            if (_state == StoryPauseSource.None && duration > 0 && sender.Position >= duration - 0.25)
            {
                OnProgressCompleted(this, EventArgs.Empty);
            }
        }

        private void OnFailed(VideoPlayerBase sender, EventArgs args)
        {
            Logger.Error("story: the video could not be played");

            // Do not strand the viewer on a card that will never move: treat a dead file as a story
            // that finished.
            if (!_unloaded)
            {
                OnProgressCompleted(this, EventArgs.Empty);
            }
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            var muted = _mute.IsChecked is true;

            AppSettings.VolumeMuted = muted;

            if (_player != null)
            {
                _player.Mute = muted;
            }
        }

        #endregion

        #region TDLib open / close

        private void OpenStory(StoryViewModel story)
        {
            if (story == null || (story.PosterChatId == _openedChatId && story.Id == _openedStoryId))
            {
                return;
            }

            CloseStory();

            _openedChatId = story.PosterChatId;
            _openedStoryId = story.Id;

            // This is what marks the story as viewed for its poster, and what moves it out of the
            // unread half of the strip.
            story.ClientService.Send(new OpenStory(_openedChatId, _openedStoryId));
        }

        private void CloseStory()
        {
            if (_openedStoryId == 0 || _viewModel == null)
            {
                _openedChatId = 0;
                _openedStoryId = 0;
                return;
            }

            _viewModel.ClientService.Send(new CloseStory(_openedChatId, _openedStoryId));

            _openedChatId = 0;
            _openedStoryId = 0;
        }

        #endregion

        #region Pausing

        /// <summary>
        /// Upstream contract: the first source to arrive pauses, the last one to leave resumes, so
        /// a flyout opened while the finger is down does not resume on release.
        /// </summary>
        public void Suspend(StoryPauseSource source)
        {
            var none = _state == StoryPauseSource.None;

            _state |= source;

            if (none)
            {
                if (_type == StoryType.Video)
                {
                    _player?.Pause();
                }

                _progress.Suspend();
            }
        }

        public void Resume(StoryPauseSource source)
        {
            _state &= ~source;

            if (_state == StoryPauseSource.None)
            {
                if (_type == StoryType.Video)
                {
                    _player?.Play();
                }

                _progress.Resume();
            }
        }

        public void Toggle()
        {
            if (_state != StoryPauseSource.None)
            {
                Resume(StoryPauseSource.Interaction);
            }
            else
            {
                Suspend(StoryPauseSource.Interaction);
            }
        }

        public bool IsPaused => _state != StoryPauseSource.None;

        /// <summary>
        /// The slot the window last gave this card. 3 is the middle, i.e. the open one.
        /// </summary>
        public int Slot => _index;

        #endregion

        #region Gestures

        // Hold to pause and tap to move are the same gesture up to its length, so both are decided
        // on release. Two numbers, and both are Uno's, not invented here: a touch manipulation only
        // starts after 15 px of travel (Manipulation.StartTouch), so anything under that is still a
        // press; and PointerMoved arrives twice per move on X11 (PORTING.md 6), which is why the
        // distance is measured between the press and the release positions rather than accumulated.
        private const double TapSlopPixels = 12;
        private const ulong HoldMilliseconds = 220;

        private bool _pressed;
        private ulong _pressedAt;
        private Point _pressedPoint;

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_open)
            {
                return;
            }

            // A press that started on the mute toggle or on "..." is that button's, not the card's.
            // ButtonBase marks PointerPressed handled, which on its own would already keep it away
            // from a `+=` subscription like this one, but the tap zones are the whole point of this
            // screen: relying on "probably handled" is how a button ends up also skipping the story.
            if (IsInteractive(e.OriginalSource))
            {
                return;
            }

            var point = e.GetCurrentPoint(_activeRoot);
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
                && !point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            _pressed = true;
            _pressedAt = Logger.TickCount;
            _pressedPoint = point.Position;

            Suspend(StoryPauseSource.Interaction);
            ShowHideChrome(false);
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed)
            {
                return;
            }

            _pressed = false;

            var point = e.GetCurrentPoint(_activeRoot).Position;

            var elapsed = Logger.TickCount - _pressedAt;
            var moved = Math.Abs(point.X - _pressedPoint.X) > TapSlopPixels
                || Math.Abs(point.Y - _pressedPoint.Y) > TapSlopPixels;

            ShowHideChrome(true);
            Resume(StoryPauseSource.Interaction);

            if (elapsed > HoldMilliseconds || moved)
            {
                // It was a hold, or a drag: the pause was the whole point.
                return;
            }

            // Telegram's split: the left third goes back, the rest goes forward.
            var width = _activeRoot.ActualWidth;
            if (width <= 0)
            {
                return;
            }

            e.Handled = true;

            if (point.X < width / 3)
            {
                PreviousRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                NextRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed)
            {
                return;
            }

            _pressed = false;

            ShowHideChrome(true);
            Resume(StoryPauseSource.Interaction);
        }

        /// <summary>
        /// Whether the pointer landed on a button rather than on the card. Walks up from the
        /// original source with a hard bound, because a loop over the visual tree inside a pointer
        /// handler is exactly the shape that costs a whole screen when it goes wrong.
        /// </summary>
        private static bool IsInteractive(object source)
        {
            var element = source as DependencyObject;

            for (int i = 0; i < 16 && element != null; i++)
            {
                if (element is ButtonBase)
                {
                    return true;
                }

                element = VisualTreeHelper.GetParent(element);
            }

            return false;
        }

        // Holding hides the bars, the header and the caption so the picture can be looked at. A
        // plain Opacity write, not an animation: see the header of this file.
        private void ShowHideChrome(bool show)
        {
            // Named, not walked by index or by type: PORTING.md 6 has a whole entry on why looking
            // for siblings positionally in this port is how a panel ends up dereferencing null.
            var opacity = show ? 1d : 0d;

            _progress.Opacity = opacity;
            _captionRoot.Opacity = opacity;
            _shade.Opacity = show ? 0.5 : 0;
            _info.Opacity = opacity;
        }

        #endregion

        private void OnProgressCompleted(object sender, EventArgs e)
        {
            if (_unloaded || !_open)
            {
                return;
            }

            Completed?.Invoke(this, EventArgs.Empty);
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            MoreClick?.Invoke(_more, new StoryEventArgs(_viewModel));
        }
    }
}
