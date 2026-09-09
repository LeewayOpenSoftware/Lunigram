//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The full-screen story viewer.
//
// Same class name, same namespace and the same two public entry points as
// Controls/Stories/StoriesWindow.xaml.cs, because six shared files construct it by name and this
// head compiles all six of them:
//
//     var window = new StoriesWindow();
//     window.Update(viewModel, activeStories, StoryOpenOrigin.X, origin, closing);
//     _ = window.ShowAsync(xamlRoot);
//
// (Controls/ActiveStoriesSegments.cs:141, Controls/Messages/MessageBubble.xaml.cs:3243,
// Controls/Messages/Service/MessagePhotoContent.xaml.cs:138, Common/MessageHelper.cs:1500 and
// :1540, ViewModels/DialogViewModel.Delegate.cs:455, ViewModels/Chats/ChatStoriesViewModel.cs:447,
// ViewModels/Stories/StoryListViewModel.cs:250.) Every one of those call sites is inside an
// `#if !LINUX` today; opening them is what makes this screen reachable, and it is listed as an open
// issue rather than done here because most of those blocks are hundreds of lines belonging to other
// batches.
//
// What this window does NOT carry, and why -- all four are somebody else's area or somebody else's
// missing subsystem, and none of them is the viewer:
//   * the reply composer (FormattedTextBox = RichEditBox.Document, not implemented on Uno Skia),
//     the sticker/emoji panel and the voice-note button -- the composer batch owns those;
//   * the "seen by" bar and the reaction row (StoryInteractionBar, StoryChannelInteractionBar,
//     StoryReactPopup) -- the interactions batch owns those, and this window leaves a named,
//     bottom-aligned slot (InteractionSlot) for them to drop a control into;
//   * the live-broadcast half (StoryLiveInteractionBar, VoipGroupCall), which needs the calls
//     subsystem this head does not have;
//   * stealth mode, which is a premium feature hanging off the composer's placeholder text.
//
// The three animation traps of PORTING.md 6 that a screen like this walks straight into are avoided
// by not starting a single composition animation: everything that moves -- the pass between
// authors, the fade in and out of the whole window -- is a value written once per frame from
// Telegram.Common.CompositionRenderingClock onto a CompositeTransform or an Opacity. That is the
// same construction Xaml/CarouselViewer.Linux.cs uses for the gallery pass, which is measured and
// working, and it is the reason a story card can be dragged here at all: InteractionTracker throws
// on Uno (TryUpdatePositionWithAnimation, NaturalRestingPosition, ConfigurePositionXInertiaModifiers
// -- see PORTING.md 6), so anything that would have used it is rebuilt on the manipulation events,
// which Uno does implement.

using System;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Windows.Foundation;
using Windows.UI.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Stories
{
    public sealed partial class StoriesWindow : OverlayWindow
    {
        /// <summary>
        /// How many cards live in the ring. Same seven as upstream: the open one plus three either
        /// side, which is what fills the width of a 16:9 window at the 0,4 side scale.
        /// </summary>
        private const int CardCount = 7;

        /// <summary>Index of the middle card, i.e. the one being watched.</summary>
        private const int CenterSlot = 3;

        /// <summary>
        /// Fraction of a card's travel a drag has to reach for the pass to commit instead of
        /// reverting. Same 0,25 the gallery uses (CarouselViewer.CommitThreshold), which is itself
        /// the constant of the Windows snap condition.
        /// </summary>
        private const double CommitThreshold = 0.25;

        /// <summary>Milliseconds of coasting a flick is projected over. Same as the gallery.</summary>
        private const double FlickProjectionMilliseconds = 120;

        private const double SettleDurationMilliseconds = 250;
        private const double FadeDurationMilliseconds = 150;

        #region Tree

        private readonly Border _layer;
        private readonly Border _titleBar;
        private readonly GlyphButton _backButton;
        private readonly Grid _layoutRoot;
        private Grid _root;
        private readonly Grid _viewport;
        private readonly GlyphButton _prevButton;
        private readonly GlyphButton _nextButton;
        private readonly Border _interactionSlot;

        private readonly StoryContent[] _cards = new StoryContent[CardCount];
        private readonly CompositeTransform[] _transforms = new CompositeTransform[CardCount];

        #endregion

        private StoryListViewModel _viewModel;
        public StoryListViewModel ViewModel => _viewModel ??= DataContext as StoryListViewModel;

        private StoryOpenOrigin _origin;
        private Rect _originRect;
        private Func<ActiveStoriesViewModel, Rect> _closing;

        private int _index;
        private int _total;

        // The interactions batch's bar, dropped into InteractionSlot on load.
        private StoryInteractionsHost _interactions;

        // Card geometry, recomputed on every resize.
        private double _cardWidth;
        private double _cardHeight;

        // 12.10 gave OverlayWindow the XamlRoot it opens in; upstream's own StoriesWindow
        // takes one too, so the shared call sites need no fence.
        public StoriesWindow(XamlRoot xamlRoot)
            : base(xamlRoot)
        {
            IsLightDismissEnabled = false;
            IsTabStop = true;
            RequestedTheme = ElementTheme.Dark;
            Background = null;
            OverlayBrush = null;

            _layer = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xDD, 0x22, 0x22, 0x22))
            };
            _layer.Tapped += Layer_Tapped;

            // The area the window manager's own title bar would occupy. Unigram draws its own, and
            // OverlayWindow.MaskTitleAndStatusBar hides it while a popup is up; this is the strip
            // that stays draggable.
            _titleBar = new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Top,
                Height = 40
            };

            _backButton = new GlyphButton
            {
                Glyph = Icons.Dismiss,
                Width = 48,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            _backButton.Click += BackButton_Click;
            AutomationProperties.SetName(_backButton, Strings.AccDescrGoBack);

            _layoutRoot = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _layoutRoot.SizeChanged += OnLayoutRootSizeChanged;

            for (int i = 0; i < CardCount; i++)
            {
                var transform = new CompositeTransform();
                var card = new StoryContent
                {
                    RenderTransform = transform,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Visibility = Visibility.Collapsed
                };

                card.Completed += OnCompleted;
                card.PreviousRequested += OnPreviousRequested;
                card.NextRequested += OnNextRequested;
                card.MoreClick += OnMoreClick;
                card.Click += OnCardClick;

                _cards[i] = card;
                _transforms[i] = transform;

                _layoutRoot.Children.Add(card);
            }

            // GlyphButton and not a Button with a FontIcon: the glyph constants of Icons come from
            // Unigram's own icon font, and GlyphButton's style in Themes/Generic.xaml (which is not
            // win:-only, so it is compiled here) is what puts that font on them. A bare FontIcon
            // would fall back to the symbol font and draw a box.
            _prevButton = new GlyphButton
            {
                Glyph = Icons.ArrowLeft,
                Width = 40,
                Height = 40,
                IsTabStop = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            _prevButton.Click += Prev_Click;

            _nextButton = new GlyphButton
            {
                Glyph = Icons.ArrowRight,
                Width = 40,
                Height = 40,
                IsTabStop = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            _nextButton.Click += Next_Click;

            _viewport = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = true
            };

            _viewport.Children.Add(_prevButton);
            _viewport.Children.Add(_nextButton);

            Canvas.SetZIndex(_viewport, 1);

            _interactionSlot = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 12)
            };

            Canvas.SetZIndex(_interactionSlot, 2);

            _root = new Grid();
            var root = _root;
            root.Children.Add(_layer);
            root.Children.Add(_titleBar);
            root.Children.Add(_layoutRoot);
            root.Children.Add(_viewport);
            root.Children.Add(_interactionSlot);
            root.Children.Add(_backButton);

            Content = root;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            PreviewKeyDown += OnPreviewKeyDown;
            AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);

            InitializeManipulation();
        }

        /// <summary>
        /// Where the interactions batch drops its bar ("seen by N", the reaction row, the reply
        /// composer when it exists). Left empty by this batch on purpose.
        /// </summary>
        public Border InteractionSlot => _interactionSlot;

        #region Entry point

        /// <summary>
        /// Same signature as upstream, so the six shared call sites need no change.
        /// </summary>
        public void Update(StoryListViewModel viewModel, ActiveStoriesViewModel activeStories, StoryOpenOrigin origin, Rect point, Func<ActiveStoriesViewModel, Rect> closing)
        {
            _origin = origin;
            _originRect = point;
            _closing = closing;

            Update(viewModel, activeStories);
        }

        private void Update(StoryListViewModel viewModel, ActiveStoriesViewModel activeStories)
        {
            if (viewModel == null)
            {
                return;
            }

            if (_viewModel != viewModel)
            {
                _viewModel = viewModel;
                DataContext = viewModel;

                _index = Math.Max(0, viewModel.Items.IndexOf(activeStories));
            }

            _total = viewModel.Items.Count;

            for (int i = 0; i < CardCount; i++)
            {
                var real = _index + i - CenterSlot;
                var card = _cards[i];

                if (real >= 0 && real < _total)
                {
                    card.Visibility = Visibility.Visible;
                    card.Update(viewModel.Items[real], i == CenterSlot, i);
                }
                else
                {
                    card.Visibility = Visibility.Collapsed;
                }
            }

            UpdateButtons();

            // Same tail as upstream: reaching the last author asks the incremental collection for
            // one more page, so the pass does not stop at the edge of what was loaded.
            if (_index >= _total - 1 && viewModel.Items is ISupportIncrementalLoading incremental && incremental.HasMoreItems)
            {
                _ = incremental.LoadMoreItemsAsync(20);
            }
        }

        private void UpdateButtons()
        {
            var story = ActiveCard?.ViewModel?.SelectedItem;
            var stories = ActiveCard?.ViewModel;

            var canGoBack = _index > 0 || (stories != null && story != null && stories.Items.IndexOf(story) > 0);
            var canGoForward = _index < _total - 1 || (stories != null && story != null && stories.Items.IndexOf(story) < stories.Items.Count - 1);

            _prevButton.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;
            _nextButton.Visibility = canGoForward ? Visibility.Visible : Visibility.Collapsed;

            // SEAM with the interactions batch, and it is the one line their host asked for:
            // StoriesWindow keeps the author in a private _index and raises no event for the pass
            // between authors, so the bar is re-pointed from here. UpdateButtons already runs on
            // every Update() and on every Move(), which is exactly when the author can change.
            _interactions?.Update(stories);
        }

        private StoryContent ActiveCard => _cards[CenterSlot];

        #endregion

        #region Layout

        private void OnLayoutRootSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Same fit as upstream: a 9:16 card, uniformly fitted into the window and capped at 720
            // on each side, so on a very large screen the story does not become a wall.
            var height = Math.Min(e.NewSize.Height, 720);
            var width = Math.Min(e.NewSize.Width, 720);

            if (height <= 0 || width <= 0)
            {
                return;
            }

            var ratio = Math.Min(height / 16, width / 9);

            _cardWidth = 9 * ratio;
            _cardHeight = 16 * ratio;

            for (int i = 0; i < CardCount; i++)
            {
                _cards[i].Width = _cardWidth;
                _cards[i].Height = _cardHeight;
            }

            _viewport.Width = _cardWidth + 96; // 48 px either side, so the arrows sit outside the card
            _viewport.Height = _cardHeight;

            _interactionSlot.Width = _cardWidth;
            _interactionSlot.Margin = new Thickness(0, 0, 0, Math.Max(12, (e.NewSize.Height - _cardHeight) / 2 + 12));

            ApplyOffset(_offset);
        }

        /// <summary>
        /// Where the card <paramref name="distance"/> places away from the middle sits, in layout
        /// pixels, and how big it is. Both are continuous in <paramref name="distance"/> so that a
        /// half-finished drag has a well defined position -- upstream only ever needed the integer
        /// cases because its pass was an InteractionTracker snap, which does not exist here.
        ///
        /// The integer values are upstream's, written out: side cards are at scale 0,40, the first
        /// one 0,78 card-widths off centre ((x/2 - small/2) + (small + margin) with small = 0,40x
        /// and margin = 0,08x), and every further one 0,48 card-widths beyond that.
        /// </summary>
        private void PlaceCard(int slot, double distance)
        {
            var transform = _transforms[slot];
            var magnitude = Math.Abs(distance);
            var sign = distance < 0 ? -1 : 1;

            double offset;

            if (magnitude <= 1)
            {
                offset = 0.78 * _cardWidth * distance;
            }
            else
            {
                offset = sign * _cardWidth * (0.30 + 0.48 * magnitude);
            }

            var scale = 1 - 0.6 * Math.Min(magnitude, 1);

            transform.TranslateX = offset;
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }

        // How far the ring has been dragged, in cards. 0 = the middle card is centred.
        private double _offset;

        private void ApplyOffset(double offset)
        {
            _offset = offset;

            if (_cardWidth <= 0)
            {
                return;
            }

            for (int i = 0; i < CardCount; i++)
            {
                PlaceCard(i, i - CenterSlot + offset);
            }
        }

        #endregion

        #region Moving between stories and authors

        /// <summary>
        /// Straight port of upstream's Move: inside the current author first, and only when there is
        /// nothing left in that direction does it change author. <paramref name="force"/> skips
        /// straight to the author change, which is what PageUp/PageDown and a tap on a side card do.
        /// </summary>
        private bool Move(Direction direction, int increment = 1, bool force = false)
        {
            if (_viewModel == null || _total == 0)
            {
                return false;
            }

            if (!force)
            {
                var author = _viewModel.Items[_index];
                var item = author.Items.IndexOf(author.SelectedItem);

                // Upstream's two conditions, unchanged. Note that item == -1 (a SelectedItem that is
                // not in the list) is deliberately safe here: backward is refused, and forward asks
                // for Items[0], which is the first story.
                if ((item > 0 && direction == Direction.Backward)
                    || (item < author.Items.Count - 1 && direction == Direction.Forward))
                {
                    author.SelectedItem = author.Items[direction == Direction.Forward ? item + 1 : item - 1];

                    Update(_viewModel, author);
                    return true;
                }
            }

            if (_index < _total - increment && direction == Direction.Forward)
            {
                _index += increment;
            }
            else if (_index >= increment && direction == Direction.Backward)
            {
                _index -= increment;
            }
            else
            {
                return false;
            }

            // The ring is rebuilt rather than rotated: every card is told which author it shows and
            // the ones that fall outside the list collapse. Upstream rotates an index array instead,
            // to keep the loaded media of the cards that stay -- worth doing only once there is a
            // measurement saying the reload costs something, and the media of a card that moves one
            // place is a different story anyway.
            Update(_viewModel, _viewModel.Items[_index]);

            // The ring has been renumbered, so the very same pixels are now described by an offset
            // one card further from zero on the other side: what was the card at distance +1 is now
            // the middle card, and to leave it where the eye last saw it the offset has to grow by
            // the same increment. Starting from the CURRENT offset and not from zero is what makes a
            // drag that is committed at 60% of the way continue from 60% instead of snapping back
            // and starting again -- the same "moving resting value" the gallery pass writes out in
            // CarouselViewer.CommitLinux.
            var from = _offset + (direction == Direction.Forward ? increment : -increment);

            ApplyOffset(from);
            Settle(0);

            return true;
        }

        private void OnCompleted(object sender, EventArgs e)
        {
            // The story ran out. Next story of this author, then next author, and when there is no
            // next author the viewer closes -- which is what Telegram does.
            if (Move(Direction.Forward))
            {
                return;
            }

            TryHide(ContentDialogResult.None);
        }

        private void OnPreviousRequested(object sender, EventArgs e)
        {
            Move(Direction.Backward);
        }

        private void OnNextRequested(object sender, EventArgs e)
        {
            if (!Move(Direction.Forward))
            {
                TryHide(ContentDialogResult.None);
            }
        }

        private void OnCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is not StoryContent card)
            {
                return;
            }

            var distance = _layoutRoot.Children.IndexOf(card) - CenterSlot;
            if (distance == 0)
            {
                return;
            }

            Move(distance > 0 ? Direction.Forward : Direction.Backward, Math.Abs(distance), true);
        }

        private void OnMoreClick(object sender, StoryEventArgs e)
        {
            // The context menu is the interactions batch's: report, share, save, delete, mute the
            // poster. Nothing is drawn here rather than drawing a menu whose items do nothing.
            Logger.Info("story: the overflow menu is not wired in this head yet");
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            Move(Direction.Backward);
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (!Move(Direction.Forward))
            {
                TryHide(ContentDialogResult.None);
            }
        }

        #endregion

        #region Keyboard and wheel

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key is VirtualKey.Left or VirtualKey.PageUp)
            {
                Move(Direction.Backward, force: args.Key is VirtualKey.PageUp);
                args.Handled = true;
            }
            else if (args.Key is VirtualKey.Right or VirtualKey.PageDown)
            {
                if (!Move(Direction.Forward, force: args.Key is VirtualKey.PageDown))
                {
                    TryHide(ContentDialogResult.None);
                }

                args.Handled = true;
            }
            else if (args.Key is VirtualKey.Space)
            {
                ActiveCard?.Toggle();
                args.Handled = true;
            }
            else if (args.Key is VirtualKey.Escape)
            {
                if (_viewers != null)
                {
                    HideViewers();
                }
                else
                {
                    TryHide(ContentDialogResult.None);
                }

                args.Handled = true;
            }
        }

        // AddHandler and not `protected override void OnPointerWheelChanged`: Uno's routed-event
        // generator does not turn an event on because a subclass overrides its handler, it inherits
        // the flags of the base type (PORTING.md 6, measured on ChatTextBox, where an OnKeyUp
        // override was dead code). ContentControl does not implement OnPointerWheelChanged, so the
        // override would never have been called. Registering the handler by hand is the shape that
        // works, and handledEventsToo:true because the cards below may already have handled it.
        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);

            if (point.Properties.MouseWheelDelta > 0)
            {
                Move(Direction.Backward);
            }
            else
            {
                Move(Direction.Forward);
            }

            e.Handled = true;
        }

        #endregion

        #region Drag between authors

        private bool _panning;

        private void InitializeManipulation()
        {
            // One manipulating element per branch, and this is it. Nesting two is impossible on Uno:
            // UIElement.PrepareManagedManipulationEventBubbling calls GestureRecognizer
            // .CompleteGesture() on every ancestor a manipulation event bubbles through, so an
            // ancestor that also manipulates gets one ManipulationStarting per finger and zero
            // deltas (measured in unigram-linux/spikes/GestureSpike, run-03.log). The cards
            // themselves only use pointer events, which is why hold-to-pause and this drag can
            // coexist: when the drag takes the pointer, the card sees PointerCaptureLost and treats
            // it as "not a tap", which is exactly right.
            //
            // Rails on Y so a mostly vertical drag is not read as a pass. Inertia is deliberately
            // off: with TranslateInertia set, ManipulationCompleted does not arrive until Uno's own
            // inertia processor finishes, and that processor is driven by CompositionTarget
            // .Rendering, which is not a frame clock on Uno (PORTING.md 6). The flick is projected
            // by hand from the release velocity instead.
            _layoutRoot.ManipulationMode = ManipulationModes.TranslateX
                | ManipulationModes.TranslateY
                | ManipulationModes.TranslateRailsY;

            // Subscribing is what turns the gesture settings on in Uno
            // (UIElement.AddManipulationHandler -> UpdateManipulations); the mode alone is not enough.
            _layoutRoot.ManipulationStarted += OnManipulationStarted;
            _layoutRoot.ManipulationDelta += OnManipulationDelta;
            _layoutRoot.ManipulationCompleted += OnManipulationCompleted;
        }

        private void OnManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            StopSettle();

            _panning = true;
            ApplyOffset(0);
        }

        private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (!_panning || _cardWidth <= 0)
            {
                return;
            }

            // Cumulative, not the running sum of the deltas: PointerMoved arrives twice per move on
            // X11 (PORTING.md 6) and a hand-accumulated delta would count double. Cumulative is
            // computed by the recognizer from the pointer positions, so it is immune.
            var progress = e.Cumulative.Translation.X / (_cardWidth * 0.78);

            ApplyOffset(Clamp(progress));
        }

        private void OnManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (!_panning)
            {
                return;
            }

            _panning = false;

            if (_cardWidth <= 0)
            {
                Settle(0);
                return;
            }

            var travel = _cardWidth * 0.78;
            var projected = e.Cumulative.Translation.X + e.Velocities.Linear.X * FlickProjectionMilliseconds;
            var progress = projected / travel;

            GestureProbe.Log($"stories released: drag {GestureProbe.F(e.Cumulative.Translation.X)} px, "
                + $"velocity {GestureProbe.F(e.Velocities.Linear.X)} px/ms, "
                + $"projected {GestureProbe.F(progress)} against {GestureProbe.F(CommitThreshold)}");

            // Dragging to the RIGHT (positive X) brings the previous author in, i.e. moves backward.
            if (progress > CommitThreshold && _index > 0)
            {
                Move(Direction.Backward, 1, true);
            }
            else if (progress < -CommitThreshold && _index < _total - 1)
            {
                Move(Direction.Forward, 1, true);
            }
            else
            {
                Settle(0);
            }
        }

        private double Clamp(double progress)
        {
            var min = _index < _total - 1 ? -1d : 0d;
            var max = _index > 0 ? 1d : 0d;

            return Math.Clamp(progress, min, max);
        }

        #endregion

        #region Settle

        private bool _settling;
        private double _settleFrom;
        private double _settleTo;
        private ulong _settleStart;
        private double _settleDuration;

        private void Settle(double to)
        {
            StopSettle();

            _settleFrom = _offset;
            _settleTo = to;

            if (Math.Abs(_settleFrom - _settleTo) < 0.001 || !PowerSavingPolicy.AreSmoothTransitionsEnabled)
            {
                ApplyOffset(to);
                return;
            }

            _settleDuration = Math.Max(60, SettleDurationMilliseconds * Math.Abs(_settleFrom - _settleTo));
            _settleStart = Logger.TickCount;
            _settling = true;

            CompositionRenderingClock.Rendering += OnSettleTick;
        }

        private void OnSettleTick(object sender, object e)
        {
            var elapsed = Logger.TickCount - _settleStart;
            var progress = Math.Clamp(elapsed / _settleDuration, 0, 1);

            // Ease out cubic, the shape a WinUI implicit animation would have had.
            var eased = 1 - Math.Pow(1 - progress, 3);

            ApplyOffset(_settleFrom + (_settleTo - _settleFrom) * eased);

            if (progress >= 1)
            {
                StopSettle();
            }
        }

        private void StopSettle()
        {
            if (_settling)
            {
                _settling = false;
                CompositionRenderingClock.Rendering -= OnSettleTick;
            }
        }

        #endregion

        #region Opening and closing

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Focus(FocusState.Programmatic);

            if (_viewModel?.NavigationService?.Window is WindowContext window)
            {
                window.Activated += OnActivated;
            }

            // The interaction bar goes in on load and not in the constructor: Attach walks
            // InteractionSlot, which is a live element, and the host wires itself to the author's
            // PropertyChanged the moment it is given one.
            if (_interactions == null)
            {
                _interactions = StoryInteractionsHost.Attach(this);

                if (_interactions != null)
                {
                    _interactions.ViewersRequested += OnViewersRequested;
                    _interactions.DeleteRequested += OnDeleteRequested;
                    _interactions.Deleted += OnStoryDeleted;
                }
            }

            _interactions?.Update(ActiveCard?.ViewModel);

            ApplyOffset(0);
            Fade(0, 1, null);
        }

        private StoryViewersOverlay _viewers;

        /// <summary>
        /// The "seen by" list, shown INSIDE this window rather than as a popup over it.
        ///
        /// Measured on 2026-08-26: a ContentPopup opened while this OverlayWindow is up does open
        /// - Opened fires and getStoryInteractions comes back with its answer - but it does not
        /// paint: the story viewer's own overlay stays on top of it and the list is invisible.
        /// Two popup roots, and the second one loses. So the list goes into this window's own
        /// visual tree, above everything else, and the story is suspended while it is up.
        /// </summary>
        private void OnViewersRequested(object sender, StoryViewModel story)
        {
            if (story == null || _root == null)
            {
                return;
            }

            HideViewers();

            _viewers = new StoryViewersOverlay(story);
            _viewers.CloseRequested += (s, e) => HideViewers();

            Canvas.SetZIndex(_viewers, 20);
            _root.Children.Add(_viewers);

            ActiveCard?.Suspend(StoryPauseSource.Popup);

            _ = _viewers.LoadAsync();
        }

        private StoryConfirmOverlay _confirm;

        /// <summary>
        /// Delete, asked and done inside this window. See StoryInteractionBar.OnDeleteClick for why
        /// the confirmation cannot be a MessagePopup here.
        /// </summary>
        private void OnDeleteRequested(object sender, StoryViewModel story)
        {
            if (story == null || _root == null || _confirm != null)
            {
                return;
            }

            ActiveCard?.Suspend(StoryPauseSource.Popup);

            _confirm = new StoryConfirmOverlay(Strings.DeleteStoryTitle, Strings.DeleteStorySubtitle, Strings.Delete, Strings.Cancel);
            _confirm.Answered += (s, yes) =>
            {
                _root?.Children.Remove(_confirm);
                _confirm = null;

                ActiveCard?.Resume(StoryPauseSource.Popup);

                if (!yes)
                {
                    Logger.Info(string.Format("stories: delete of story {0} cancelled", story.Id));
                    return;
                }

                Logger.Info(string.Format("stories: deleteStory posterChatId={0} storyId={1}", story.PosterChatId, story.Id));

                story.ClientService.Send(new DeleteStory(story.PosterChatId, story.Id), result =>
                {
                    Logger.Info(string.Format("stories: deleteStory {0} -> {1}", story.Id, result?.GetType().Name));
                });

                _interactions?.RaiseDeleted(story);
            };

            Canvas.SetZIndex(_confirm, 30);
            _root.Children.Add(_confirm);
        }

        private void HideViewers()
        {
            if (_viewers != null)
            {
                _root?.Children.Remove(_viewers);
                _viewers = null;

                ActiveCard?.Resume(StoryPauseSource.Popup);
            }
        }

        private void OnStoryDeleted(object sender, StoryViewModel story)
        {
            // The story is gone from the server; TDLib's updateStoryDeleted will empty the author
            // out from underneath us. Step forward, and if there is nowhere to step, close.
            Logger.Info(string.Format("stories: deleted story {0}, stepping on", story?.Id));

            var stories = ActiveCard?.ViewModel;
            if (stories != null && story != null)
            {
                stories.Items.Remove(story);

                if (stories.Items.Count > 0)
                {
                    stories.SelectedItem = stories.Items[Math.Min(stories.Items.Count - 1, 0)];
                    ActiveCard?.Update(stories, true, CenterSlot);
                    UpdateButtons();
                    return;
                }
            }

            OnBackRequestedOverride(this, new BackRequestedRoutedEventArgs());
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopSettle();
            StopFade();

            if (_viewModel?.NavigationService?.Window is WindowContext window)
            {
                window.Activated -= OnActivated;
            }
        }

        private void OnActivated(object sender, Navigation.WindowActivatedEventArgs e)
        {
            // The window lost focus: a story keeps playing behind an alt-tab otherwise.
            //
            // CoreWindowActivationState and NOT Windows.UI.Core.WindowActivationState, which is what
            // upstream's StoriesWindow compares against: on Uno the WindowActivationState property of
            // WindowActivatedEventArgs is a CoreWindowActivationState. WindowContext.OnActivated
            // already carries the same #if LINUX for the same reason (Navigation/WindowContext.cs:735).
            if (!e.IsActive)
            {
                ActiveCard?.Suspend(StoryPauseSource.Window);
            }
            else
            {
                ActiveCard?.Resume(StoryPauseSource.Window);
            }
        }

        private void Layer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            TryHide(ContentDialogResult.None);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            TryHide(ContentDialogResult.None);
        }

        private bool _done;

        protected override void OnBackRequestedOverride(object sender, BackRequestedRoutedEventArgs e)
        {
            if (_done)
            {
                e.Handled = true;
                return;
            }

            _done = true;
            e.Handled = true;

            IsHitTestVisible = false;
            ActiveCard?.Suspend(StoryPauseSource.Window);

            // Upstream closes inside a CompositionScopedBatch.Completed handler. That handler NEVER
            // runs on Uno (PORTING.md 6, and the gallery had exactly this bug: "fades the gallery out
            // and never closes it, leaving the chat covered"), so the close is driven by the frame
            // clock and Hide() is called from the last frame -- and from the fallback path when
            // animations are off, so there is no arrangement of settings in which the window fades
            // and stays.
            Fade(1, 0, Hide);
        }

        #endregion

        #region Fade

        private bool _fading;
        private double _fadeFrom;
        private double _fadeTo;
        private ulong _fadeStart;
        private Action _fadeCompleted;

        private void Fade(double from, double to, Action completed)
        {
            StopFade();

            _fadeFrom = from;
            _fadeTo = to;
            _fadeCompleted = completed;

            if (!PowerSavingPolicy.AreSmoothTransitionsEnabled)
            {
                Opacity = to;
                completed?.Invoke();
                return;
            }

            Opacity = from;

            _fadeStart = Logger.TickCount;
            _fading = true;

            CompositionRenderingClock.Rendering += OnFadeTick;
        }

        private void OnFadeTick(object sender, object e)
        {
            var elapsed = Logger.TickCount - _fadeStart;
            var progress = Math.Clamp(elapsed / FadeDurationMilliseconds, 0, 1);

            Opacity = _fadeFrom + (_fadeTo - _fadeFrom) * progress;

            if (progress >= 1)
            {
                var completed = _fadeCompleted;

                StopFade();

                completed?.Invoke();
            }
        }

        private void StopFade()
        {
            _fadeCompleted = null;

            if (_fading)
            {
                _fading = false;
                CompositionRenderingClock.Rendering -= OnFadeTick;
            }
        }

        #endregion
    }
}
