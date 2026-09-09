//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The Linux head of Controls/ActiveStoriesSegments.cs. It used to be an inert stand-in --
// HasActiveStories => false, an empty Open(), and drawing methods that drew nothing -- so a chat
// with active stories looked exactly like one without, and clicking the avatar did nothing. The
// ring geometry and the window it opens both exist on this head now, so the shell has a body.
//
// What is ported verbatim and why: the segment maths (GetSegments) is upstream's, run through
// Telegram.Linux/Graphics' Win2D shim, whose CanvasPathBuilder already carries the
// AddArc(center, radiusX, radiusY, startAngle, sweepAngle) overload this needs. Drawing the arcs
// by hand in Skia would be a second implementation of a circle divided into n parts, and the two
// would drift.
//
// What is NOT ported, deliberately: upstream's ShowIndeterminate spins ~30 dashes with
// TrimStart/RotationAngle animations and shares ONE animation object between two targets. PORTING
// §6 has all three of those as silent no-ops or worse on this Uno -- a CompositionAnimation is not
// shared between visuals, RotationAngle on a ShapeVisual is swallowed, and DelayTime is a second
// thing to believe. The spinner would be invisible or half-drawn and would look like this file
// failing. It draws a static ring in the same colours instead: it is on screen for the moment
// between the click and the window opening.

using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Numerics;
using Telegram.Common;
using Microsoft.UI.Xaml.Hosting;
using Telegram.Controls.Stories;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Telegram.Controls
{
    public enum ActiveStoriesClickMode
    {
        Disabled,
        Enabled,
        Auto
    }

    public partial class ActiveStoriesSegments : HyperlinkButton
    {
        private static readonly Color _storyUnreadTopColor = Color.FromArgb(0xFF, 0x34, 0xC7, 0x6F);
        private static readonly Color _storyUnreadBottomColor = Color.FromArgb(0xFF, 0x3D, 0xA1, 0xFD);

        private static readonly Color _storyCloseFriendTopColor = Color.FromArgb(0xFF, 0x78, 0xd5, 0x38);
        private static readonly Color _storyCloseFriendBottomColor = Color.FromArgb(0xFF, 0x2a, 0xb5, 0x6d);

        private static readonly Color _storyDefaultColor = Color.FromArgb(77, 128, 128, 128);

        private static readonly Color _storyLiveColor = Color.FromArgb(0xFF, 0xFF, 0x2C, 0x55);

        private bool _hasActiveStories;
        private bool _hasLiveBadge;

        public bool HasActiveStories => _hasActiveStories;

        public bool HasLiveBadge => _hasLiveBadge;

        public bool IsLiveBadgeVisible { get; set; } = true;

        private ContentPresenter Presenter;
        private Border LiveBadge;

        private Visual _visual;

        public ActiveStoriesSegments()
        {
            // Not decoration: without it the control inherits HyperlinkButton's DefaultStyleKey and
            // therefore Uno's Fluent HyperlinkButton template, which has Padding, MinWidth and
            // MinHeight of its own. The 48x48 ProfilePicture inside then overflows the padded
            // content slot, Uno clips the child to that slot, and the circle the Border paints
            // comes out as a rounded rectangle 46x34 layout units - measured on fase1/lista-1.png
            // and fase1/historial-1.png, where the arc of the clipped circle is still visible in
            // the corners. Naming the real style key picks up the Style in Themes/Generic.xaml
            // (which is NOT win: only, so it is compiled here): a bare Grid with a stretched
            // ContentPresenter, no padding, no minimums.
            DefaultStyleKey = typeof(ActiveStoriesSegments);
        }

        protected override void OnApplyTemplate()
        {
            Presenter = GetTemplateChild(nameof(Presenter)) as ContentPresenter;

            if (_visual != null)
            {
                UpdateVisual(_hasLiveBadge, _visual);
            }

            base.OnApplyTemplate();
        }

        private void UpdateVisual(bool live, Visual visual)
        {
            _hasLiveBadge = live;

            // The ring hangs off the template's ContentPresenter. Before OnApplyTemplate there is
            // none, and a visual dropped on the floor here is a ring that never appears for the
            // first cell recycled into view -- so it is parked and hung on the template instead.
            if (Presenter != null)
            {
                _visual = null;
                ElementCompositionPreview.SetElementChildVisual(Presenter, visual);

                if (live && IsLiveBadgeVisible)
                {
                    ShowLiveBadge();
                }
                else if (LiveBadge != null)
                {
                    XamlMarkupHelper.UnloadObject(LiveBadge);
                    LiveBadge = null;
                }
            }
            else
            {
                _visual = visual;
            }
        }

        // LiveBadge is x:Load="False" in the template, so GetTemplateChild returns null until
        // something realizes it. XLoadGate is this port's answer to exactly that: it runs the
        // callback once the element is reachable, or gives up after a bounded number of layout
        // passes rather than staying subscribed to LayoutUpdated forever.
        //
        // A live story still reads as live without the badge -- the ring is drawn in the live
        // colour by the same call -- so failing to realize it degrades the label, not the feature,
        // and says so once instead of silently.
        private void ShowLiveBadge()
        {
            LiveBadge ??= GetTemplateChild(nameof(LiveBadge)) as Border;

            if (LiveBadge != null)
            {
                return;
            }

            XLoadGate.Materialize(this, nameof(LiveBadge), () =>
            {
                LiveBadge ??= GetTemplateChild(nameof(LiveBadge)) as Border;

                if (LiveBadge == null)
                {
                    Logger.Error("ActiveStoriesSegments: LiveBadge never materialized; the live ring is drawn but the badge is not");
                }
            });
        }

        public async void Open(INavigationService navigationService, IClientService clientService, Chat chat, int side, Func<ActiveStoriesViewModel, Rect> origin)
        {
            if (chat == null)
            {
                return;
            }

            try
            {
                var transform = TransformToVisual(null);
                var point = transform.TransformPoint(new Point());

                var pointz = new Rect(point.X + 4, point.Y + 4, side - 8, side - 8);

                if (clientService.TryGetActiveStories(chat.Id, out ChatActiveStories cached))
                {
                    var unreadCount = cached.CountUnread(out bool closeFriends, out bool live);
                    ShowIndeterminate(side, unreadCount, closeFriends);
                }
                else
                {
                    ShowIndeterminate(side, 1, false);
                }

                await clientService.SendAsync(new GetChatActiveStories(chat.Id));

                if (clientService.TryGetActiveStories(chat.Id, out ChatActiveStories chatActiveStories))
                {
                    var settings = clientService.Session.Resolve<ISettingsService>();
                    var aggregator = clientService.Session.Resolve<IEventAggregator>();

                    var activeStories = new ActiveStoriesViewModel(clientService, settings, aggregator, chatActiveStories, chat);
                    await activeStories.Wait;

                    if (activeStories.Items.Count > 0)
                    {
                        var viewModel = new StoryListViewModel(clientService, settings, aggregator, activeStories);
                        viewModel.NavigationService = navigationService;
                        viewModel.UpdateSelectedItem();

                        var window = new StoriesWindow(XamlRoot);
                        window.Update(viewModel, activeStories, StoryOpenOrigin.ProfilePhoto, pointz, origin);
                        _ = window.ShowAsync();
                    }
                }

                SetChat(clientService, chat, side);
            }
            catch (Exception ex)
            {
                // async void on the UI thread: NativeDispatcher swallows what escapes, so an
                // exception here would read as "clicking the avatar does nothing" -- the very
                // symptom this file exists to fix -- rather than as a crash.
                Logger.Error("ActiveStoriesSegments: opening the story viewer failed", ex);
            }
        }

        public void Clear()
        {
            if (_hasActiveStories && Content is UIElement element)
            {
                var visual = ElementComposition.GetElementVisual(element);
                visual.Scale = Vector3.One;

                UpdateVisual(false, visual.Compositor.CreateSpriteVisual());

                _hasActiveStories = false;
                IsEnabled = InteractionMode is ActiveStoriesClickMode.Enabled;
            }
        }

        public void SetUser(IClientService clientService, User user, int side)
        {
            if (user.Id != clientService.Options.MyId && clientService.TryGetChatFromUser(user.Id, out Chat chat))
            {
                UpdateActiveStories(clientService, chat.Id, user.ActiveStoryState, side);
            }
            else
            {
                UpdateActiveStories(clientService, 0, null, side);
            }
        }

        public void SetChat(IClientService clientService, Chat chat, int side)
        {
            if (clientService != null && chat?.Type is ChatTypePrivate typePrivate && typePrivate.UserId != clientService.Options.MyId && clientService.TryGetUser(typePrivate.UserId, out User user))
            {
                UpdateActiveStories(clientService, chat.Id, user.ActiveStoryState, side);
            }
            else if (clientService != null && chat?.Type is ChatTypeSupergroup typeSupergroup && clientService.TryGetSupergroup(typeSupergroup.SupergroupId, out Supergroup supergroup))
            {
                UpdateActiveStories(clientService, chat.Id, supergroup.ActiveStoryState, side);
            }
            else
            {
                UpdateActiveStories(clientService, 0, null, side);
            }
        }

        public void UpdateActiveStories(ChatActiveStories activeStories, int side, bool precise)
        {
            if (activeStories.Stories.Count > 0)
            {
                Shrink(side);

                var unreadCount = activeStories.CountUnread(out bool closeFriends, out bool live);

                if (precise)
                {
                    UpdateSegments(side, closeFriends, live, activeStories.Stories.Count, unreadCount);
                }
                else if (unreadCount > 0)
                {
                    UpdateSegments(side, closeFriends, live, 1, 1, 3.0f, 3.0f);
                }
                else if (live)
                {
                    UpdateSegments(side, false, true, 1, 1, 3.0f, 3.0f);
                }
                else
                {
                    UpdateSegments(side, false, false, 1, 0, 3.0f, 3.0f);
                }
            }
            else
            {
                Clear();
            }
        }

        private void UpdateActiveStories(IClientService clientService, long chatId, ActiveStoryState activeStoryState, int side)
        {
            if (chatId == 0 || activeStoryState == null)
            {
                Clear();
                return;
            }

            Shrink(side);

            if (clientService.TryGetActiveStories(chatId, out ChatActiveStories activeStories))
            {
                var unreadCount = activeStories.CountUnread(out bool closeFriends, out bool live);
                UpdateSegments(side, closeFriends, live, activeStories.Stories.Count, unreadCount);
            }
            else if (activeStoryState is ActiveStoryStateUnread)
            {
                UpdateSegments(side, false, false, 1, 1);
            }
            else if (activeStoryState is ActiveStoryStateLive)
            {
                UpdateSegments(side, false, true, 1, 1);
            }
            else
            {
                UpdateSegments(side, false, false, 1, 0);
            }
        }

        // The avatar is scaled down so the ring has room around it, and the control is only
        // clickable once there is something to open. Upstream repeats this block at four call
        // sites; one copy is enough and it keeps _hasActiveStories and IsEnabled in step.
        private void Shrink(int side, float thickness = 2.0f)
        {
            if (_hasActiveStories)
            {
                return;
            }

            if (Content is UIElement element)
            {
                var visual = ElementComposition.GetElementVisual(element);
                visual.CenterPoint = new Vector3(side / 2);
                visual.Scale = new Vector3((side - thickness * 4) / side);
            }

            _hasActiveStories = true;
            IsEnabled = InteractionMode is ActiveStoriesClickMode.Enabled or ActiveStoriesClickMode.Auto;
        }

        public void UpdateSegments(int side, bool closeFriends, bool unread)
        {
            Shrink(side);
            UpdateSegments(side, closeFriends, false, 1, unread ? 1 : 0);
        }

        public void UpdateSegments(int side, int total, int unread, float unreadThickness = 2.0f, float readThickness = 1.0f)
        {
            if (total > 0)
            {
                Shrink(side, unreadThickness);
                UpdateSegments(side, false, false, total, unread, unreadThickness, readThickness);
            }
            else
            {
                Clear();
            }
        }

        private void UpdateSegments(int side, bool closeFriends, bool live, int total, int unread, float unreadThickness = 2.0f, float readThickness = 1.0f)
        {
            if (live)
            {
                total = 1;
                unread = 1;
            }

            var compositor = BootStrapper.Current.Compositor;
            var read = total - unread;

            var unreadPath = GetSegments(compositor, side, total, 0, unread, unreadThickness, unreadThickness * 2);
            var readPath = GetSegments(compositor, side, total, unread, read, readThickness, unreadThickness * 2);

            var segments = compositor.CreateShapeVisual();

            // PORTING §6: a ShapeVisual whose Size is (0,0) paints nothing and says nothing, and
            // RelativeSizeAdjustment does not fill it in on this head. This is the one line that
            // decides whether any of the drawing above is ever seen.
            segments.Size = new Vector2(side);

            if (unreadPath != null)
            {
                var unreadStroke = compositor.CreateLinearGradientBrush();
                unreadStroke.ColorStops.Add(compositor.CreateColorGradientStop(0, TopColor ?? (live ? _storyLiveColor : closeFriends ? _storyCloseFriendTopColor : _storyUnreadTopColor)));
                unreadStroke.ColorStops.Add(compositor.CreateColorGradientStop(1, BottomColor ?? (live ? _storyLiveColor : closeFriends ? _storyCloseFriendBottomColor : _storyUnreadBottomColor)));
                unreadStroke.EndPoint = new Vector2(0, 1);

                var unreadShape = compositor.CreateSpriteShape();
                unreadShape.Geometry = unreadPath;
                unreadShape.StrokeBrush = unreadStroke;
                unreadShape.StrokeThickness = unreadThickness;
                unreadShape.StrokeStartCap = CompositionStrokeCap.Round;
                unreadShape.StrokeEndCap = CompositionStrokeCap.Round;

                segments.Shapes.Add(unreadShape);
            }

            if (readPath != null)
            {
                var readStroke = compositor.CreateColorBrush(_storyDefaultColor);

                var readShape = compositor.CreateSpriteShape();
                readShape.Geometry = readPath;
                readShape.StrokeBrush = readStroke;
                readShape.StrokeThickness = readThickness;
                readShape.StrokeStartCap = CompositionStrokeCap.Round;
                readShape.StrokeEndCap = CompositionStrokeCap.Round;

                segments.Shapes.Add(readShape);
            }

            UpdateVisual(live, segments);
        }

        // Upstream's maths, unchanged, over Telegram.Linux/Graphics' Win2D shim.
        private CompositionGeometry GetSegments(Compositor compositor, float side, float segments, int index, int length, float thickness = 2.0f, float spacing = 4.0f)
        {
            var center = new Vector2(side * 0.5f);
            var radius = center.X - (thickness * 0.5f);

            if (length == 0)
            {
                return null;
            }
            else if (segments == 1)
            {
                var ellipse = compositor.CreateEllipseGeometry();
                ellipse.Center = center;
                ellipse.Radius = new Vector2(radius);

                return ellipse;
            }

            CanvasGeometry result;
            using (var builder = new CanvasPathBuilder(null))
            {
                var startAngle = MathFEx.ToRadians(360f / segments);

                var angularSpacing = spacing / radius;
                var circleLength = MathF.PI * 2.0f * radius;
                var segmentLength = (circleLength - spacing * segments) / segments;
                var segmentAngle = segmentLength / radius;

                var current = MathFEx.ToRadians(-90) + angularSpacing / 2;
                current -= index * startAngle;

                for (int i = 0; i < length; i++)
                {
                    var x = center.X + (radius * MathF.Cos(current - startAngle));
                    var y = center.Y + (radius * MathF.Sin(current - startAngle));

                    builder.BeginFigure(x, y);
                    builder.AddArc(center, radius, radius, current - startAngle, segmentAngle);
                    builder.EndFigure(CanvasFigureLoop.Open);

                    current -= startAngle;
                }

                result = CanvasGeometry.CreatePath(builder);
            }

            return compositor.CreatePathGeometry(new CompositionPath(result));
        }

        // Static, and see the note at the top of the file for why: upstream's spinner is built out
        // of three things this Uno drops without a word.
        public void ShowIndeterminate(int side, int unreadCount, bool closeFriends)
        {
            Shrink(side);
            UpdateSegments(side, closeFriends, false, 1, unreadCount > 0 ? 1 : 0, 3.0f, 3.0f);
        }

        #region Mode

        public ActiveStoriesClickMode InteractionMode
        {
            get { return (ActiveStoriesClickMode)GetValue(InteractionModeProperty); }
            set { SetValue(InteractionModeProperty, value); }
        }

        public static readonly DependencyProperty InteractionModeProperty =
            DependencyProperty.Register("InteractionMode", typeof(ActiveStoriesClickMode), typeof(ActiveStoriesSegments), new PropertyMetadata(ActiveStoriesClickMode.Auto, OnInteractionModeChanged));

        private static void OnInteractionModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ActiveStoriesSegments)d).OnInteractionModeChanged((ActiveStoriesClickMode)e.NewValue);
        }

        private void OnInteractionModeChanged(ActiveStoriesClickMode interactionMode)
        {
            if (_hasActiveStories)
            {
                IsEnabled = interactionMode is ActiveStoriesClickMode.Enabled or ActiveStoriesClickMode.Auto;
            }
            else
            {
                IsEnabled = interactionMode is ActiveStoriesClickMode.Enabled;
            }
        }

        #endregion

        public Color? TopColor { get; set; }
        public Color? BottomColor { get; set; }
    }
}
