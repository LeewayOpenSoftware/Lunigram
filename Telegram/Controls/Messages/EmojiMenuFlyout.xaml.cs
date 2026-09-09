//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Telegram.Common;
using Telegram.Controls.Drawers;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Drawers;
#if !LINUX
using Telegram.ViewModels.Stories;
#endif
using Telegram.Views.Popups;
#if !LINUX
using Telegram.Views.Stars.Popups;
#endif
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls.Messages
{
    public partial class EmojiSelectedEventArgs : EventArgs
    {
        public ReactionType Type { get; }

        public EmojiSelectedEventArgs(ReactionType type)
        {
            Type = type;
        }
    }

    public enum EmojiFlyoutAlignment
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        Center,
        Top
    }

    public sealed partial class EmojiMenuFlyout : UserControl
    {
        private readonly IClientService _clientService;
        private readonly EmojiDrawerMode _mode;

        private readonly MessageViewModel _message;
        private readonly IReactionsDelegate _bubble;

#if !LINUX
        private readonly StoryViewModel _story;
        private readonly FrameworkElement _reserved;
#endif

        private readonly bool _allowCustomEmoji = true;
        private readonly bool _areTags;

        private readonly Popup _popup;

        private EmojiDrawer _drawer;

        public event EventHandler<EmojiSelectedEventArgs> EmojiSelected;
        public event EventHandler<AvailableReaction> ItemClick;

        public event EventHandler Opened;

        public static EmojiMenuFlyout ShowAt(FrameworkElement element, MessageViewModel message, IReactionsDelegate bubble, AvailableReactions reactions, EmojiDrawerViewModel viewModel)
        {
            return new EmojiMenuFlyout(element, message, bubble, reactions, viewModel);
        }

        private EmojiMenuFlyout(FrameworkElement element, MessageViewModel message, IReactionsDelegate bubble, AvailableReactions reactions, EmojiDrawerViewModel viewModel)
        {
            InitializeComponent();

            _clientService = message.ClientService;
            _mode = EmojiDrawerMode.Reactions;
            _message = message;
            _bubble = bubble;
            _allowCustomEmoji = reactions.AllowCustomEmoji;
            _areTags = reactions.AreTags;

            _popup = new Popup();

            Initialize(message.ClientService, element, EmojiFlyoutAlignment.Center, viewModel);
        }

        private void OnClosed(object sender, object e)
        {
            _popup.Closed -= OnClosed;

            // The view model is resolved per flyout but stays subscribed to the aggregator,
            // so it can queue an Update long after the popup is gone. Deactivate detaches the
            // drawer's collection bindings, which is what keeps that update from raising into
            // native peers that are no longer alive.
            _drawer.ItemClick -= OnStatusClick;
            _drawer.ItemContextRequested -= OnStatusContextRequested;
            _drawer.Deactivate();
            _drawer.DataContext = null;

            if (_bubble is FrameworkElement element && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            {
                var selector = element.GetParent<SelectorItem>();
                selector?.Focus(FocusState.Keyboard);
            }
        }

        public static EmojiMenuFlyout ShowAt(IClientService clientService, EmojiDrawerMode mode, FrameworkElement element, EmojiFlyoutAlignment alignment, EmojiDrawerViewModel viewModel = null)
        {
            return new EmojiMenuFlyout(clientService, mode, element, alignment, viewModel);
        }

        private EmojiMenuFlyout(IClientService clientService, EmojiDrawerMode mode, FrameworkElement element, EmojiFlyoutAlignment alignment, EmojiDrawerViewModel viewModel = null)
        {
            InitializeComponent();

            _clientService = clientService;
            _mode = mode;

            _popup = new Popup();

            Initialize(clientService, element, alignment, viewModel);
        }

#if !LINUX
        // Reacting to a Story. Stories are not in the Linux subset at all (FALTA 2.10,
        // ActiveStoriesSegments is an inert stub) -- there is no _story to construct this with.
        public static EmojiMenuFlyout ShowAt(FrameworkElement element, StoryViewModel story, FrameworkElement reserved, AvailableReactions reactions, EmojiDrawerViewModel viewModel)
        {
            return new EmojiMenuFlyout(element, story, reserved, reactions, viewModel);
        }

        private EmojiMenuFlyout(FrameworkElement element, StoryViewModel story, FrameworkElement reserved, AvailableReactions reactions, EmojiDrawerViewModel viewModel)
        {
            InitializeComponent();

            _clientService = story.ClientService;
            _mode = EmojiDrawerMode.Reactions;
            _story = story;
            _reserved = reserved;

            _popup = new Popup();

            Initialize(story.ClientService, element, EmojiFlyoutAlignment.Center, viewModel);
        }
#endif

        private void Initialize(IClientService clientService, FrameworkElement element, EmojiFlyoutAlignment alignment, EmojiDrawerViewModel viewModel = null)
        {
            var transform = element.TransformToVisual(null);
            var position = transform.TransformPoint(new Point());

            var count = 8;

            var itemSize = 32;
            var itemPadding = 4;

            var itemTotal = itemSize + itemPadding;

            var actualWidth = 8 + 4 + (count * itemTotal);

            var cols = 8;
            var rows = 8;

            var width = 8 + 4 + (cols * itemTotal);
            var viewport = 8 + 4 + (cols * itemTotal);
            var height = rows * itemTotal;

            var padding = actualWidth - width;

            // We try to constraint the popup within the window bounds
            if (alignment is EmojiFlyoutAlignment.TopLeft or EmojiFlyoutAlignment.TopRight)
            {
                var diff = (position.Y + height) - element.XamlRoot.Size.Height;
                if (diff >= 24 && position.Y - height > 0)
                {
                    if (alignment == EmojiFlyoutAlignment.TopLeft)
                    {
                        //alignment = EmojiFlyoutAlignment.BottomLeft;
                    }
                    else if (alignment == EmojiFlyoutAlignment.TopRight)
                    {
                        alignment = EmojiFlyoutAlignment.BottomRight;
                    }
                }
            }

            var yy = 20f;
            var byy = alignment == EmojiFlyoutAlignment.BottomRight ? 20 : 0;

            var radius = 8;

            viewModel ??= EmojiDrawerViewModel.Create(clientService.Session, _mode);

            var view = new EmojiDrawer(_mode);
            _drawer = view;

            // Subscribed here rather than in the constructors, so that all three of them tear
            // the drawer down and not just the message one.
            _popup.Closed += OnClosed;

            view.DataContext = viewModel;
            view.VerticalAlignment = VerticalAlignment.Top;
            view.Width = width;
            view.Height = height;
            view.ItemClick += OnStatusClick;

            if (!_allowCustomEmoji)
            {
                view.HideNavigation();
            }

            if (_mode == EmojiDrawerMode.EmojiStatus)
            {
                view.ItemContextRequested += OnStatusContextRequested;
            }

            Presenter.Children.Add(view);

            //if (_mode is EmojiDrawerMode.EmojiStatus or EmojiDrawerMode.ChatEmojiStatus)
            {
                view.Activate(null, EmojiSearchType.EmojiStatus);
            }
            //else
            //{
            //    viewModel.Update();
            //}

            Shadow.Width = width;
            Pill.Width = width;
            Presenter.Width = width;

            Shadow.Height = height;
            Pill.Height = height + yy + byy;
            Presenter.Padding = new Thickness(0, yy, 0, 0);
            Presenter.Height = height + yy;

            var figure = new PathFigure();
            figure.StartPoint = new Point(radius, yy);

            if (alignment == EmojiFlyoutAlignment.TopLeft)
            {
                figure.Segments.Add(new LineSegment { Point = new Point(18 + 20, yy + 0) });
                figure.Segments.Add(new ArcSegment { Point = new Point(18 + 20 + 14, yy + 0), Size = new Size(7, 7), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });
            }
            else if (alignment == EmojiFlyoutAlignment.TopRight)
            {
                figure.Segments.Add(new LineSegment { Point = new Point(width - 18 - 20 - 14, yy + 0) });
                figure.Segments.Add(new ArcSegment { Point = new Point(width - 18 - 20, yy + 0), Size = new Size(7, 7), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });
            }

            figure.Segments.Add(new LineSegment { Point = new Point(width - radius, yy + 0) });
            figure.Segments.Add(new ArcSegment { Point = new Point(width, yy + radius), Size = new Size(radius, radius), RotationAngle = 90, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = new Point(width, yy + height - radius) });
            figure.Segments.Add(new ArcSegment { Point = new Point(width - radius, yy + height), Size = new Size(radius, radius), RotationAngle = 90, SweepDirection = SweepDirection.Clockwise });

            if (alignment == EmojiFlyoutAlignment.BottomRight)
            {
                figure.Segments.Add(new LineSegment { Point = new Point(width - 18 - 20, yy + height) });
                figure.Segments.Add(new ArcSegment { Point = new Point(width - 18 - 20 - 14, yy + height), Size = new Size(7, 7), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });
            }

            figure.Segments.Add(new LineSegment { Point = new Point(radius, yy + height) });
            figure.Segments.Add(new ArcSegment { Point = new Point(0, yy + height - radius), Size = new Size(radius, radius), RotationAngle = 90, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = new Point(0, yy + radius) });
            figure.Segments.Add(new ArcSegment { Point = new Point(radius, yy + 0), Size = new Size(radius, radius), RotationAngle = 90, SweepDirection = SweepDirection.Clockwise });

            var path = new PathGeometry();
            path.Figures.Add(figure);

            var data = new GeometryGroup();
            data.FillRule = FillRule.Nonzero;
            data.Children.Add(path);

            if (alignment == EmojiFlyoutAlignment.TopLeft)
            {
                data.Children.Add(new EllipseGeometry { Center = new Point(20 + 18, 7), RadiusX = 3.5, RadiusY = 3.5 });
            }
            else if (alignment == EmojiFlyoutAlignment.TopRight)
            {
                data.Children.Add(new EllipseGeometry { Center = new Point(width - 20 - 18, 7), RadiusX = 3.5, RadiusY = 3.5 });
            }
            else if (alignment == EmojiFlyoutAlignment.BottomRight)
            {
                data.Children.Add(new EllipseGeometry { Center = new Point(width - 20 - 18, yy + height + 14), RadiusX = 3.5, RadiusY = 3.5 });
            }

            Pill.Data = data;
            Pill.Margin = new Thickness(0, -yy, 0, 0);
            Presenter.Margin = new Thickness(0, -yy - byy, 0, 0);

            LayoutRoot.Padding = new Thickness(16, 36, 16, 16);

            var rootVisual = ElementComposition.GetElementVisual(LayoutRoot);
            var compositor = rootVisual.Compositor;

            var x = position.X - 6;
            var y = position.Y + element.ActualHeight + 8;

            var widthDiff = (width - element.ActualSize.X) / 2;

            if (alignment == EmojiFlyoutAlignment.TopRight)
            {
                x = position.X - width + element.ActualWidth + 6;
            }
            else if (alignment == EmojiFlyoutAlignment.Center)
            {
                y = _allowCustomEmoji ? position.Y - 44 : position.Y + 36;
                x = position.X - widthDiff;
            }
            else if (alignment == EmojiFlyoutAlignment.Top)
            {
                y = position.Y - (height - element.ActualHeight) - 32;
                x = position.X - widthDiff;
            }
            else if (alignment == EmojiFlyoutAlignment.BottomRight)
            {
                y = position.Y - height - 8;
                x = position.X - width + element.ActualWidth + 6;
            }

            _popup.Child = this;
            _popup.Margin = new Thickness(x - 16, y - 36, 0, 0);
            _popup.ShouldConstrainToRootBounds = false;
            _popup.RequestedTheme = element.ActualTheme;
            _popup.IsLightDismissEnabled = true;
            _popup.XamlRoot = element.XamlRoot;
            this.ApplyChatTheme(element.XamlRoot);
            _popup.IsOpen = true;
            //_popup.Opacity = 0.5;
            //_popup.Scale = new Vector3(28f / 32f);
            //_popup.CenterPoint = new Vector3(204, 148, 0);

            //return;

            var visualPill = ElementComposition.GetElementVisual(Pill);
            visualPill.CenterPoint = new Vector3(alignment == EmojiFlyoutAlignment.TopLeft ? 36 / 2 : width - 36 / 2, yy + 36 / 2, 0);

            var visualExpand = ElementComposition.GetElementVisual(Expand);
            visualExpand.CenterPoint = new Vector3(32 / 2f, 24 / 2f, 0);

            var clip = compositor.CreateRoundedRectangleGeometry();
            clip.CornerRadius = new Vector2(8);
            clip.Offset = new Vector2(36, yy);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += Batch_Completed;

            var drawer = ElementComposition.GetElementVisual(Presenter);
            var presenter = ElementComposition.GetElementVisual(this);
            ElementCompositionPreview.SetIsTranslationEnabled(this, true);

            var test = ElementComposition.GetElementVisual(LayoutRoot);
            if (_allowCustomEmoji)
            {
                test.CenterPoint = new Vector3(300, 138, 0); // 148
            }
            else
            {
                test.CenterPoint = new Vector3(300, 50, 0); // 148
            }

            drawer.Clip = compositor.CreateGeometricClip(clip);
            visualPill.Clip = compositor.CreateGeometricClip(clip);

            var ratio = 32f / 28f;

            var corner = compositor.CreateVector2KeyFrameAnimation();
            corner.InsertKeyFrame(0, new Vector2(20 * ratio));
            corner.InsertKeyFrame(1, new Vector2(8));
            corner.Duration = Constants.SoftAnimation + TimeSpan.FromSeconds(0);
#if LINUX
            // u-084: a composition geometry animates only TrimStart/TrimEnd/TrimOffset here.
            // CompositionRoundedRectangleGeometry has no SetAnimatableProperty of its own, and the
            // base CompositionGeometry compares the three Trim* before falling through to
            // CompositionObject's property set, which answers "Unable to set property" -- ONCE PER
            // COMPOSED FRAME, for as long as the flyout is open. Together with Size and Offset
            // below, that is the render storm u-083 had to suppress this whole flyout to avoid.
            // The Linux branch writes the end value and does not animate, exactly as the three
            // sites in ChatView and ChatCell already do.
            clip.CornerRadius = new Vector2(8);
#else
            clip.StartAnimation("CornerRadius", corner);
#endif

            var scalePill = compositor.CreateVector3KeyFrameAnimation();
            scalePill.InsertKeyFrame(0, new Vector3(28f / 32f));
            scalePill.InsertKeyFrame(1, new Vector3(1));
            scalePill.Duration = Constants.SoftAnimation + TimeSpan.FromSeconds(0);
            //visualPill.StartAnimation("Scale", scalePill);
            test.StartAnimation("Scale", scalePill);


            var resize = compositor.CreateVector2KeyFrameAnimation();
            if (alignment == EmojiFlyoutAlignment.Center || alignment == EmojiFlyoutAlignment.Top)
            {
                resize.InsertKeyFrame(0, new Vector2(260, 40 * ratio));
                resize.InsertKeyFrame(1, new Vector2(width, height));
            }
            else
            {
                resize.InsertKeyFrame(0, new Vector2());
                resize.InsertKeyFrame(1, new Vector2(width, height + yy + byy));
            }
            resize.Duration = Constants.SoftAnimation + TimeSpan.FromSeconds(0);

#if LINUX
            // Same rule as CornerRadius above: Size is not animatable on a composition geometry.
            // The end value is the second keyframe of `resize`, spelled out here because a
            // KeyFrameAnimation does not let its frames be read back.
            clip.Size = alignment == EmojiFlyoutAlignment.Center || alignment == EmojiFlyoutAlignment.Top
                ? new Vector2(width, height)
                : new Vector2(width, height + yy + byy);
#else
            clip.StartAnimation("Size", resize);
#endif

            var move = compositor.CreateVector2KeyFrameAnimation();
            var movePresenter = compositor.CreateScalarKeyFrameAnimation();

            if (alignment == EmojiFlyoutAlignment.Center || alignment == EmojiFlyoutAlignment.Top)
            {
                move.InsertKeyFrame(0, new Vector2(0, yy + (_allowCustomEmoji ? 82 : 0)));
                move.InsertKeyFrame(1, new Vector2(0, yy));

                if (alignment == EmojiFlyoutAlignment.Top)
                {
                    movePresenter.InsertKeyFrame(0, height - (_allowCustomEmoji ? 124 : 42));
                    movePresenter.InsertKeyFrame(1, 0);
                }
            }
            else if (alignment == EmojiFlyoutAlignment.TopRight)
            {
                move.InsertKeyFrame(0, new Vector2(width - 36, yy));
                move.InsertKeyFrame(1, new Vector2());
            }
            else if (alignment == EmojiFlyoutAlignment.TopLeft)
            {
                move.InsertKeyFrame(0, new Vector2(36, yy));
                move.InsertKeyFrame(1, new Vector2());
            }
            else if (alignment == EmojiFlyoutAlignment.BottomRight)
            {
                move.InsertKeyFrame(0, new Vector2(width - 36, height + yy));
                move.InsertKeyFrame(1, new Vector2());
            }
            move.Duration = Constants.SoftAnimation + TimeSpan.FromSeconds(0);
            movePresenter.Duration = Constants.SoftAnimation + TimeSpan.FromSeconds(0);

#if LINUX
            // Offset, same rule again. Its end value is Vector2.Zero for every alignment except
            // the Center/Top pair, which lands at (0, yy).
            clip.Offset = alignment == EmojiFlyoutAlignment.Center || alignment == EmojiFlyoutAlignment.Top
                ? new Vector2(0, yy)
                : new Vector2();
#else
            clip.StartAnimation("Offset", move);
#endif
            presenter.StartAnimation("Translation.Y", movePresenter);

#if LINUX
            // u-084: Batch_Completed is what gives the flyout its shadow and raises Opened, and
            // CompositionScopedBatch.Completed never fires on Uno -- so the flyout came up with no
            // shadow and nobody was ever told it had opened.
            //
            // The two visual animations left running above (`test` Scale and `presenter`
            // Translation.Y) are started on visuals belonging to a Popup whose IsOpen was set a few
            // lines earlier, so they are also candidates for the no-CompositionTarget drop that
            // left the composer drawer at opacity 0. Writing their end state here costs nothing if
            // they did run, and is the difference between a painted flyout and a stuck one if they
            // did not. StopAnimation first, because a finished KeyFrameAnimation keeps driving its
            // property on this head.
            batch.EndWithCompleted(Constants.SoftAnimation, () =>
            {
                test.StopAnimation("Scale");
                test.Scale = new Vector3(1);

                presenter.StopAnimation("Translation.Y");
                presenter.Properties.InsertVector3("Translation", Vector3.Zero);

                Batch_Completed(null, null);
            });
#else
            batch.End();
#endif




            // u-064: upstream's own "3D experiment" -- unreachable on every platform (the `return;`
            // right above is unconditional, no dead code deleted here that ever ran). Dropped
            // instead of guarded: it's the only user of `Perspective`
            // (Grid.Transform3D/PerspectiveTransform3D in EmojiMenuFlyout.xaml), a type that does
            // not exist in Uno's WinUI3 surface at all (not NotImplemented -- absent), and the XAML
            // element is removed to match. Nothing observable changes on any platform: this code
            // never executed.
        }

        private void Batch_Completed(object sender, CompositionBatchCompletedEventArgs args)
        {
            Shadow.Shadow = new ThemeShadow();
            Shadow.Translation = new Vector3(0, 0, 32);

            Opened?.Invoke(this, EventArgs.Empty);
        }

        public static float ToRadians(float angleIn10thofaDegree)
        {
            // Angle in 10th of a degree
            return ((angleIn10thofaDegree * MathF.PI) / 180f);
        }

        static float Apothem(float n, float a)
        {
            return a / (2 * MathF.Tan(180 / n * MathF.PI / 180));
        }

        private void OnStatusContextRequested(UIElement sender, ItemContextRequestedEventArgs<StickerViewModel> args)
        {
            var element = sender as FrameworkElement;
            var sticker = args.Item;

            if (sticker == null)
            {
                return;
            }

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(SetStatus, (sticker, 60 * 60), Strings.SetEmojiStatusUntil1Hour);
            flyout.CreateFlyoutItem(SetStatus, (sticker, 60 * 60 * 2), Strings.SetEmojiStatusUntil2Hours);
            flyout.CreateFlyoutItem(SetStatus, (sticker, 60 * 60 * 8), Strings.SetEmojiStatusUntil8Hours);
            flyout.CreateFlyoutItem(SetStatus, (sticker, 60 * 60 * 48), Strings.SetEmojiStatusUntil2Days);
            flyout.CreateFlyoutItem(ChooseStatus, sticker, Strings.SetEmojiStatusUntilOther);

            args.ShowAt(flyout, element);
        }

        private void OnStatusClick(object sender, EmojiDrawerItemClickEventArgs e)
        {
            if (e.ClickedItem is StickerViewModel sticker)
            {
                _popup.IsOpen = false;

#if !LINUX
                if (_story != null)
                {
                    StoryToggleReaction(sticker.Reaction);
                }
                else if (_message != null)
#else
                if (_message != null)
#endif
                {
                    MessageToggleReaction(sticker.Reaction);
                }
                else if (ItemClick != null)
                {
                    ItemClick(this, sticker.Reaction);
                }
                else if (_mode == EmojiDrawerMode.EmojiStatus)
                {
                    _clientService.Send(new SetEmojiStatus(new EmojiStatus(sticker.EmojiStatusType, 0)));
                }
                else if (_mode == EmojiDrawerMode.Reactions)
                {
                    _clientService.Send(new SetDefaultReactionType(sticker.Reaction.Type));
                }

                EmojiSelected?.Invoke(this, new EmojiSelectedEventArgs(sticker.ToReactionType()));
            }
        }

        private void SetStatus((StickerViewModel Sticker, int Duration) item)
        {
            if (_mode == EmojiDrawerMode.EmojiStatus && item.Sticker.FullType is StickerFullTypeCustomEmoji customEmoji)
            {
                _clientService.Send(new SetEmojiStatus(new EmojiStatus(item.Sticker.EmojiStatusType, DateTime.Now.ToUnixTimeSeconds() + item.Duration)));
            }
        }

        private async void ChooseStatus(StickerViewModel sticker)
        {
            var popup = new ChooseStatusDurationPopup();

            var confirm = await popup.ShowQueuedAsync(XamlRoot);
            if (confirm == ContentDialogResult.Primary && _mode == EmojiDrawerMode.EmojiStatus)
            {
                _clientService.Send(new SetEmojiStatus(new EmojiStatus(sticker.EmojiStatusType, DateTime.Now.ToUnixTimeSeconds() + popup.Value)));
            }
        }

#if !LINUX
        private async void StoryToggleReaction(AvailableReaction reaction)
        {
            if (reaction.NeedsPremium && !_story.ClientService.IsPremium)
            {
                ToastPopup.ShowFeaturePromo(WindowContext.GetNavigationService(this), new PremiumFeatureUniqueReactions());
                return;
            }

            if (_story.ChosenReactionType != null && _story.ChosenReactionType.AreTheSame(reaction.Type))
            {
                _story.ClientService.Send(new SetStoryReaction(_story.PosterChatId, _story.Id, null, true));
            }
            else
            {
                await _story.ClientService.SendAsync(new SetStoryReaction(_story.PosterChatId, _story.Id, reaction.Type, true));

                if (_reserved != null && _reserved.IsLoaded)
                {
                    // TODO: UI feedback
                }
            }
        }
#endif

        private async void MessageToggleReaction(AvailableReaction reaction)
        {
            var message = _message;
            if (message.Content is MessageAlbum album)
            {
                message = album.Messages[0];
            }

            if (reaction.NeedsPremium && !message.ClientService.IsPremium)
            {
                if (_areTags)
                {
                    WindowContext.GetNavigationService(this).ShowPromo(new PremiumFeatureSavedMessagesTags());
                }
                else
                {
                    ToastPopup.ShowFeaturePromo(WindowContext.GetNavigationService(this), new PremiumFeatureUniqueReactions());
                }

                return;
            }

            if (message.InteractionInfo != null && message.InteractionInfo.Reactions.IsChosen(reaction.Type))
            {
                message.ClientService.Send(new RemoveMessageReaction(message.ChatId, message.Id, reaction.Type));
            }
            else
            {
                Object added;
#if !LINUX
                if (reaction.Type is ReactionTypePaid)
                {
                    var popup = new ReactPopup(message.ClientService, message);

                    var confirm = await popup.ShowQueuedAsync(XamlRoot);
                    if (confirm != ContentDialogResult.Primary)
                    {
                        return;
                    }

                    message.ClientService.Send(new SetPaidMessageReactionType(message.ChatId, message.Id, popup.Type));
                    added = await PaidReactionService.AddPendingAsync(XamlRoot, message, popup.StarCount, popup.Type);
                }
                else
#endif
                {
                    // u-064: paid (Stars) reactions need Telegram.Views.Stars.Popups -- the whole
                    // Stars purchase flow, out of the subset (FALTA, Premium/Stars/Business behind
                    // WebView2). A free reaction is what this sends whether or not the tapped tile
                    // was a paid-reaction tile; EmojiDrawer itself decides what tiles it offers.
                    added = await message.ClientService.SendAsync(new AddMessageReaction(message.ChatId, message.Id, reaction.Type, false, true));
                }

                if (added is Ok && _bubble != null && _bubble.IsLoaded)
                {
                    var unread = new UnreadReaction(reaction.Type, null, false);
                    var previous = _message.UnreadReactions;

                    _message.UnreadReactions = previous.With(unread);
                    _bubble.UpdateMessageReactions(_message, true);
                    _message.UnreadReactions = previous;
                }
            }
        }
    }
}
