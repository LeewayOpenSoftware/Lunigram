//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using LinqToVisualTree;
using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Drawers;
#if !LINUX
using Telegram.ViewModels.Stories;
using Telegram.Views.Stars.Popups;
#endif
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls.Messages
{
    public sealed partial class ReactionsMenuFlyout : UserControl
    {
        private readonly AvailableReactions _reactions;
        private EmojiDrawerViewModel _viewModel;

        private IClientService _clientService;

        private readonly MessageViewModel _message;
        private readonly IReactionsDelegate _bubble;

#if !LINUX
        private readonly StoryViewModel _story;
#endif
        private readonly FrameworkElement _reserved;

        private readonly MenuFlyout _flyout;

        private MenuFlyoutPresenter _presenter;
        private Popup _popup;

        public ReactionsMenuFlyout()
        {
            InitializeComponent();
            InitializeAccessibleNames();
        }

        public static ReactionsMenuFlyout ShowAt(AvailableReactions reactions, MessageViewModel message, IReactionsDelegate bubble, MenuFlyout flyout)
        {
            return new ReactionsMenuFlyout(reactions, message, bubble, flyout);
        }

        private ReactionsMenuFlyout(AvailableReactions reactions, MessageViewModel message, IReactionsDelegate bubble, MenuFlyout flyout)
        {
            _reactions = reactions;
            _message = message;
            _bubble = bubble;
            _flyout = flyout;

            _viewModel = EmojiDrawerViewModel.Create(message.ClientService.Session, EmojiDrawerMode.Reactions);

            InitializeComponent();
            InitializeAccessibleNames();
            Initialize(reactions, message.ClientService, flyout);
        }

#if !LINUX
        // Reacting to a Story -- out of the Linux subset entirely (FALTA 2.10).
        public static ReactionsMenuFlyout ShowAt(AvailableReactions reactions, StoryViewModel story, FrameworkElement reserved, MenuFlyout flyout)
        {
            return new ReactionsMenuFlyout(reactions, story, reserved, flyout);
        }

        private ReactionsMenuFlyout(AvailableReactions reactions, StoryViewModel story, FrameworkElement reserved, MenuFlyout flyout)
        {
            _reactions = reactions;
            _story = story;
            _reserved = reserved;
            _flyout = flyout;

            _viewModel = EmojiDrawerViewModel.Create(story.ClientService.Session, EmojiDrawerMode.Reactions);

            InitializeComponent();
            InitializeAccessibleNames();
            Initialize(reactions, story.ClientService, flyout);
        }
#endif

        private void InitializeAccessibleNames()
        {
            AutomationProperties.SetName(Expand, Strings.AccDescrMoreReactions);
        }

        private async void Initialize(AvailableReactions available, IClientService clientService, MenuFlyout flyout)
        {
            // Observed here and not at the call site: this is an async void, so what it throws does
            // NOT travel back to whoever called it -- the state machine catches it and posts it to
            // the dispatcher, where it is lost. That is how the bar managed to die inside
            // CreateDropShadow without leaving a single line anywhere, which cost a QA round.
            try
            {
                await InitializeCore(available, clientService, flyout);
            }
            catch (Exception ex)
            {
                Logger.Error("reactions bar failed to initialize", ex);
            }
        }

        // u-095: the bar was drawing at window origin (0,0) instead of over the message. Root
        // cause: `presenter.TransformToVisual(null)` -- `presenter` is a MenuFlyoutPresenter
        // living inside the ALREADY-OPEN context menu's own Popup, one level of popup nesting
        // below window content, and on this Uno, TransformToVisual cannot walk across that popup
        // boundary. On real WinUI, passing null means "transform to the root of the visual tree"
        // and that walk succeeds; here it silently returns Matrix3x2.Identity instead of throwing
        // or returning null -- the same class of failure PORTING.md's popup-root section
        // documents for GetParent<T>() (returns null across the boundary), just with a
        // plausible-looking wrong answer instead of an obviously missing one, which is why it
        // never got a log line.
        //
        // Fix: find presenter's own hosting Popup (VisualTreeHelper.GetOpenPopupsForXamlRoot) and
        // read ITS HorizontalOffset/VerticalOffset -- Uno tracks those as plain numbers set when
        // the popup was placed, not a tree walk, so they are correct regardless of the boundary.
        // Same technique as Screenshot.cs/PointerTest.cs's popup-offset fallback, just used as the
        // primary path here since we already know we start inside a popup. In the common case
        // `presenter` IS that popup's Child (Extensions.Presenter() relies on the same fact); if a
        // future flyout nests it one level deeper, the local TransformToVisual from the popup's
        // Child down to presenter never crosses a popup boundary either, so it is safe to add.
        private Point ResolvePresenterOrigin(FrameworkElement presenter)
        {
#if LINUX
            var xamlRoot = presenter.XamlRoot;
            if (xamlRoot != null)
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                {
                    if (ReferenceEquals(popup.Child, presenter))
                    {
                        return new Point(popup.HorizontalOffset, popup.VerticalOffset);
                    }

                    if (popup.Child is DependencyObject root && root.Descendants<FrameworkElement>().Any(x => ReferenceEquals(x, presenter)))
                    {
                        try
                        {
                            var local = presenter.TransformToVisual(popup.Child).TransformPoint(new Point());
                            return new Point(popup.HorizontalOffset + local.X, popup.VerticalOffset + local.Y);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("ReactionsMenuFlyout: presenter found but not transformable within its popup", ex);
                            return new Point(popup.HorizontalOffset, popup.VerticalOffset);
                        }
                    }
                }
            }
#endif

            var transform = presenter.TransformToVisual(null);
            return transform.TransformPoint(new Point());
        }

#if LINUX
        // u-095b: with the context menu opening UPWARD -- a right-click low on screen, where the
        // menu does not fit below the click -- the reactions bar was drawn ON TOP of the menu.
        //
        // `position` is the anchor the menu was opened at, not the menu's top edge: measured on
        // 10b0a17, a right-click at 584,585 put the bar popup at y=501 = 585 - 84, straight off
        // the click. When the menu fits below the click it grows down from it and the two are the
        // same point, which is why sitting the bar 4 px above `position` is right in that geometry
        // and only in that geometry. Low on screen the menu goes up instead, spanning
        // [position.Y - menuHeight, position.Y], and 4 px above the ANCHOR is inside it.
        //
        // So return the edge the bar should sit above: the menu's actual top. The overflow test is
        // the one EmojiMenuFlyout/MessageEffectMenuFlyout already use to flip themselves
        // ("position.Y + height past XamlRoot.Size.Height"), so the bar decides the direction the
        // same way the menus around it do. If the menu then leaves no room for the bar above the
        // window, the bar goes under the menu's bottom edge instead: half a bar outside the window
        // is not an improvement on the overlap this is fixing.
        //
        // Not measured -> return the anchor, i.e. exactly the placement this head already had.
        private double ResolveBarAnchorY(FrameworkElement presenter, Point position, double barHeight = 40)
        {
            var menuHeight = presenter.ActualSize.Y;
            var windowHeight = presenter.XamlRoot?.Size.Height ?? 0;

            if (menuHeight <= 0 || windowHeight <= 0)
            {
                Logger.Error($"ReactionsMenuFlyout: the open menu has no measured size ({menuHeight}) or no window ({windowHeight}); the bar cannot be kept off it");
                return position.Y;
            }

            var upward = position.Y + menuHeight > windowHeight;

            var menuTop = upward ? position.Y - menuHeight : position.Y;
            var menuBottom = upward ? position.Y : position.Y + menuHeight;

            // The pill's bottom edge sits 4 above whatever this returns, so barHeight + 4 is the
            // whole room it needs above the menu. Two callers, two pill heights: the reactions bar
            // is 40 tall and the effects picker, which carries a header, is 60.
            var room = barHeight + 4;
            var fits = menuTop >= room;
            var anchorY = fits ? menuTop : menuBottom + room + 4;

            // Every candidate in ONE line of ONE run: the chosen number on its own cannot say why
            // a bar is where it is, and reading it without the ones it was chosen over is what
            // made u-095 take three builds.
            Logger.Info($"reactions bar: anchor={position.X}x{position.Y} barHeight={barHeight} menu={presenter.ActualSize.X}x{menuHeight} window={presenter.XamlRoot.Size.Width}x{windowHeight} upward={upward} menu=[{menuTop},{menuBottom}] roomAbove={fits} anchorY={anchorY}");

            return anchorY;
        }

        // u-095d: near the right edge of the window the bar was cut in half -- a right-click at
        // x=1250 of a 1328 wide window left only 3 of its 7 reactions visible. Clamp it into the
        // viewport so all reactions remain reachable.
        private double ClampBarLeft(FrameworkElement presenter, double left, double barWidth)
        {
            var windowWidth = presenter.XamlRoot?.Size.Width ?? 0;
            if (windowWidth <= 0)
            {
                return left;
            }

            // LayoutRoot has 16px left padding, so pill starts at left + 16 and ends at left + 16 + barWidth.
            var min = -16d;
            var max = windowWidth - barWidth - 16;

            if (max <= min)
            {
                return min;
            }

            var clamped = Math.Clamp(left, min, max);
            if (clamped != left)
            {
                Logger.Info($"reactions bar: clamped horizontal offset from {left} to {clamped} (barWidth={barWidth}, windowWidth={windowWidth})");
            }

            return clamped;
        }
#endif

        private async Task InitializeCore(AvailableReactions available, IClientService clientService, MenuFlyout flyout)
        {
            var presenter = flyout.Presenter();
            if (presenter == null)
            {
                // Reached on Uno whenever the presenter cannot be found: the bar is placed and
                // sized entirely from it (position, width, theme and XamlRoot), so there is
                // nothing sensible to draw without it. It used to return here in silence, which
                // is why the menu opened and the reactions bar did not.
                Logger.Error("ReactionsMenuFlyout: the open menu has no MenuFlyoutPresenter, the reactions bar cannot be placed");
                return;
            }

            flyout.Closed += Flyout_Closed;

            presenter.PreviewKeyDown += Presenter_PreviewKeyDown;
            Presenter.PreviewKeyDown += OnPreviewKeyDown;

            static void SetAutomation(UIElement element, int index, int count)
            {
                if (ApiInfo.IsWindows11 && false)
                {
                    AutomationProperties.SetAutomationControlType(element, AutomationControlType.ListItem);
                }
                else
                {
                    AutomationProperties.SetPositionInSet(element, index + 1);
                    AutomationProperties.SetSizeOfSet(element, count);
                }
            }

            _presenter = presenter;
            _popup = new Popup();

            var position = ResolvePresenterOrigin(presenter);

            var sum = available.TopReactions.Count
                + available.RecentReactions.Count
                + available.PopularReactions.Count;

            var select = available.AllowCustomEmoji || sum > 7;
            var count = select ? 7 : sum;

            var itemSize = 28;
            var itemPadding = 4;

            var itemTotal = itemSize + itemPadding;

            var actualWidth = presenter.ActualSize.X + 18 + 12 + 18;
            var width = Math.Max(36 + 4, 8 + (count * itemTotal));

            var padding = actualWidth - width;
            var index = 0;

            Presenter.Padding = new Thickness(4, 0, 0, 0);

            Shadow.Width = width;
            Pill.Width = width;
            Presenter.Width = width;

            var height = 40;
            var haheight = 20;

            Pill.VerticalAlignment = VerticalAlignment.Top;
            Pill.Height = Shadow.Height = height + 20;
            Pill.Margin = Shadow.Margin = new Thickness(0, 0, 0, -20);

            if (select)
            {
                SetAutomation(Expand, count - 1, count);
                Expand.Visibility = Visibility.Visible;
            }
            else
            {
                Expand.Visibility = Visibility.Collapsed;
            }

            var figure = new PathFigure();
            if (count > 1)
            {
                figure.StartPoint = new Point(haheight, 0);
                figure.Segments.Add(new LineSegment { Point = new Point(width - haheight, 0) });
                figure.Segments.Add(new ArcSegment { Point = new Point(width - haheight, height), Size = new Size(haheight, haheight), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });

                figure.Segments.Add(new ArcSegment { Point = new Point(width - haheight - 14, height), Size = new Size(7, 7), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });

                figure.Segments.Add(new LineSegment { Point = new Point(haheight, height) });
                figure.Segments.Add(new ArcSegment { Point = new Point(haheight, 0), Size = new Size(haheight, haheight), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });
            }
            else
            {
                figure.StartPoint = new Point(20, 40);
                figure.Segments.Add(new BezierSegment { Point1 = new Point(31.0457, 40), Point2 = new Point(40, 31.0457), Point3 = new Point(40, 20) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(40, 8.9543), Point2 = new Point(31.0457, 0), Point3 = new Point(20, 0) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(8.9543, 0), Point2 = new Point(0, 8.9543), Point3 = new Point(0, 20) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(0, 26.3285), Point2 = new Point(2.93929, 31.9704), Point3 = new Point(7.52717, 35.6352) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(6.57139, 36.832), Point2 = new Point(6, 38.3493), Point3 = new Point(6, 40) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(6, 43.866), Point2 = new Point(9.13401, 47), Point3 = new Point(13, 47) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(16.866, 47), Point2 = new Point(20, 43.866), Point3 = new Point(20, 40) });
            }

            var path = new PathGeometry();
            path.Figures.Add(figure);

            var data = new GeometryGroup();
            data.FillRule = FillRule.Nonzero;
            data.Children.Add(path);
            data.Children.Add(new EllipseGeometry { Center = new Point(width - haheight - 8 + 5, height + 20 - 7), RadiusX = 3.5f, RadiusY = 3.5f });

            Pill.Data = data;

            LayoutRoot.Padding = new Thickness(16, 40, 16, 32);

            var rect1 = CanvasGeometry.CreateRectangle(null, Math.Min(width - actualWidth, 0), 0, Math.Max(width + 16 + 16 + Math.Max(0, padding), actualWidth), 860);
            var elli1 = CanvasGeometry.CreateRoundedRectangle(null, width - actualWidth + 18 + 16, height + height + 5, presenter.ActualSize.X, 860, 8, 8);
            var group1 = CanvasGeometry.CreateGroup(null, new[] { elli1, rect1 }, CanvasFilledRegionDetermination.Alternate);

            var rootVisual = ElementComposition.GetElementVisual(LayoutRoot);
            var compositor = rootVisual.Compositor;
            rootVisual.Clip = rootVisual.Compositor.CreateGeometricClip(rootVisual.Compositor.CreatePathGeometry(new CompositionPath(group1)));

            // Through the guarded helper, which returns null on this head on purpose: Uno's
            // Compositor.CreateDropShadow throws instead of degrading. Called raw, this line is
            // where the reactions bar died -- after the Popup had been built but before it was
            // opened, so the context menu appeared and the bar never did. The shadow is
            // decoration; the bar is not. Same null contract as the other callers in the subset.
            var pillReceiver = VisualUtilities.DropShadow(Pill, 16, 0.14f, Shadow);
            if (pillReceiver != null)
            {
                pillReceiver.RelativeSizeAdjustment = Vector2.Zero;
                pillReceiver.Size = new Vector2(width, height + 20);
                pillReceiver.Offset = new Vector3(0, 8, 0);
            }

            var x = position.X - 18 + padding;
#if LINUX
            // u-095b: above the menu's top EDGE, which is not the anchor when the menu opened
            // upward. See ResolveBarAnchorY.
            var y = ResolveBarAnchorY(presenter, position, height) - (40 + 4);
#else
            var y = position.Y - (40 + 4);
#endif

            _popup.Child = this;
            _popup.Margin = new Thickness(x - 16, y - height, 0, 0);
#if LINUX
            // u-095: the bar drew at the window ORIGIN, over the search box, instead of over the
            // message. Uno places a Popup by HorizontalOffset/VerticalOffset; the Margin above is
            // what WinUI honours, and on this head it moves the popup exactly nowhere.
            //
            // This sits on top of 8b3470a, which fixed the ANCHOR on the theory that
            // presenter.TransformToVisual(null) could not cross the context menu's popup boundary.
            // Instrumenting that call on e8ab7fe disproved it -- it logged 652x652 for a
            // right-click at 652,652, the same value the popup-origin helper returns -- and a
            // screenshot of the shipped tip (624822e, with 8b3470a in it) still shows the bar at
            // the window origin. The anchor was never the bug; the placement call was.
            // u-095d: clamp horizontal offset so reactions bar is not clipped near right edge.
            _popup.HorizontalOffset = ClampBarLeft(presenter, x - 16, width);
            _popup.VerticalOffset = y - height;
#endif
            _popup.RequestedTheme = presenter.ActualTheme;
            _popup.ShouldConstrainToRootBounds = false;
            _popup.AllowFocusOnInteraction = false;
            _popup.XamlRoot = presenter.XamlRoot;
            this.ApplyChatTheme(presenter.XamlRoot);
            _popup.IsOpen = true;

            var visualPill = ElementComposition.GetElementVisual(Pill);
            visualPill.CenterPoint = new Vector3(height / 2, height / 2, 0);
            visualPill.CenterPoint = new Vector3(width - height / 2, height / 2, 0);

            var visualExpand = ElementComposition.GetElementVisual(Expand);
            visualExpand.CenterPoint = new Vector3(32 / 2f, 24 / 2f, 0);

            var clip = compositor.CreateRoundedRectangleGeometry();
            clip.CornerRadius = new Vector2(height / 2);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            // One instance per target, and not the single shared one this used to be: Uno does
            // not share a CompositionAnimation between visuals, so starting the same object on
            // the second and third target leaves them unanimated. The pattern to grep for is
            // literal -- StartAnimation( twice with the same identifier -- and it was here three
            // times over, all on "Scale".
            CompositionAnimation ScalePill()
            {
#if LINUX
                // Compositor.CreateSpringVector3Animation is [NotImplemented] in Uno and THROWS.
                // That is what left the bar empty: InitializeCore died on this line, after the
                // pill was already on screen but before a single ReactionButton was created, so
                // the popup opened with nothing in it. Key frames draw the same
                // overshoot-and-settle the spring does; for a DampingRatio of 0.7 the peak sits
                // about 5% past the final value (it is 16% at the 0.5 used elsewhere).
                var settle = compositor.CreateVector3KeyFrameAnimation();
                settle.InsertKeyFrame(0, Vector3.Zero);
                settle.InsertKeyFrame(0.6f, new Vector3(1.05f, 1.05f, 1));
                settle.InsertKeyFrame(1, Vector3.One);
                settle.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                settle.Duration = Constants.FastAnimation;

                return settle;
#else
                var spring = compositor.CreateSpringVector3Animation();
                spring.InitialValue = Vector3.Zero;
                spring.FinalValue = Vector3.One;
                spring.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                spring.DampingRatio = 0.7f;

                return spring;
#endif
            }

            var translation = compositor.CreateScalarKeyFrameAnimation();
            translation.InsertKeyFrame(0, 0);
            translation.InsertKeyFrame(1, 16);

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(1, 0.14f);

            visualPill.StartAnimation("Scale", ScalePill());
            visualExpand.StartAnimation("Scale", ScalePill());

            translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            translation.DelayTime = TimeSpan.FromMilliseconds(150 + 100);
            opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            opacity.DelayTime = TimeSpan.FromMilliseconds(150 + 100);

            // No shadow on this head, so nothing to animate: the helper above returned null and
            // the bar simply comes up flat.
            if (pillReceiver?.Shadow is DropShadow pillShadow)
            {
                pillShadow.StartAnimation("BlurRadius", translation);
                pillShadow.StartAnimation("Opacity", opacity);
            }

            var resize = compositor.CreateVector2KeyFrameAnimation();
            resize.InsertKeyFrame(0, new Vector2(height, height));
            resize.InsertKeyFrame(1, new Vector2(width, height));
            resize.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            resize.DelayTime = TimeSpan.FromMilliseconds(100);
            resize.Duration = Constants.FastAnimation;

            var move = compositor.CreateVector2KeyFrameAnimation();
            move.InsertKeyFrame(0, new Vector2(width - height, 40));
            move.InsertKeyFrame(1, new Vector2(0, 40));
            move.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            move.DelayTime = TimeSpan.FromMilliseconds(100);
            move.Duration = Constants.FastAnimation;

            var viewVisual = ElementComposition.GetElementVisual(Presenter);
            viewVisual.CenterPoint = new Vector3(height / 2, height / 2, 0);
            viewVisual.CenterPoint = new Vector3(width - height / 2, height / 2, 0);
            viewVisual.StartAnimation("Scale", ScalePill());

            batch.End();

            var viewModel = _viewModel;
            var items = await viewModel.UpdateReactions(available);

            foreach (var item in items)
            {
                static AnimatedImage Create(double size, bool auto, bool cache)
                {
                    var animated = new AnimatedImage();
                    animated.AutoPlay = auto;
                    animated.LimitFps = !auto;
                    animated.LoopCount = auto ? 1 : 0;
                    animated.FrameSize = new Size(size, size);
                    animated.DecodeFrameType = DecodePixelType.Logical;
                    animated.IsCachingEnabled = cache;
                    animated.Width = size;
                    animated.Height = size;

                    return animated;
                }

                if (item.Item1.Type is ReactionTypePaid)
                {
                    PaidReaction.Visibility = Visibility.Visible;
                }

                var visible = Create(28, true, item.Item2.Id != 0);
                var preload = Create(32, false, item.Item2.Id != 0);

                visible.Source = new DelayedFileSource(clientService, item.Item2);
                preload.Source = new DelayedFileSource(clientService, item.Item2.StickerValue);
                preload.LoopCompleted += (s, args) => args.Cancel = true;
                preload.Opacity = 0;
                preload.Play();

                var button = new HyperlinkButton();
                button.Width = 28;
                button.Height = 28;
                button.Background = new SolidColorBrush(Colors.Transparent);
                button.CornerRadius = new CornerRadius(14);
                button.Margin = new Thickness(2, 0, 2, 0);
                button.Content = visible;
                button.Style = BootStrapper.Current.Resources["EmptyHyperlinkButtonStyle"] as Style;
                button.Tag = item.Item1;
                button.Click += Reaction_Click;

                if (item.Item1.Type is ReactionTypeEmoji emoji)
                {
                    AutomationProperties.SetName(button, emoji.Emoji);
                }

                SetAutomation(button, index, count);

                Grid.SetColumn(preload, index);
                Grid.SetColumn(button, index);

                Canvas.SetZIndex(button, 1);

                Presenter.ColumnDefinitions.Add(1, GridUnitType.Auto);
                Presenter.Children.Insert(index, button);
                Preloader.Children.Add(preload);
                index++;
            }

            Presenter.ColumnDefinitions.Add(1, GridUnitType.Auto);
        }

        public static ReactionsMenuFlyout ShowAt(IClientService clientService, Vector<long> effectIds, FrameworkElement reserved, MenuFlyout flyout)
        {
            return new ReactionsMenuFlyout(clientService, effectIds, reserved, flyout);
        }

        private ReactionsMenuFlyout(IClientService clientService, Vector<long> effectIds, FrameworkElement reserved, MenuFlyout flyout)
        {
            //_reactions = reactions;
            //_story = story;
            _reserved = reserved;
            _flyout = flyout;

            _viewModel = EmojiDrawerViewModel.Create(clientService.Session, EmojiDrawerMode.Reactions);
            _clientService = clientService;

            InitializeComponent();
            Initialize(effectIds, clientService, flyout);
        }

        private async void Initialize(Vector<long> effectIds, IClientService clientService, MenuFlyout flyout)
        {
            var presenter = flyout.Presenter();
            flyout.Closed += Flyout_Closed;

            presenter.PreviewKeyDown += Presenter_PreviewKeyDown;
            Presenter.PreviewKeyDown += OnPreviewKeyDown;

            static void SetAutomation(UIElement element, int index, int count)
            {
                if (ApiInfo.IsWindows11 && false)
                {
                    AutomationProperties.SetAutomationControlType(element, AutomationControlType.ListItem);
                }
                else
                {
                    AutomationProperties.SetPositionInSet(element, index + 1);
                    AutomationProperties.SetSizeOfSet(element, count);
                }
            }

            _presenter = presenter;
            _popup = new Popup();

            var position = ResolvePresenterOrigin(presenter);

            var sum = effectIds.Count;

            var select = true || sum > 7;
            var count = select ? 7 : sum;

            var itemSize = 28;
            var itemPadding = 4;

            var itemTotal = itemSize + itemPadding;

            var actualWidth = presenter.ActualSize.X + 18 + 12 + 18;
            var width = Math.Max(36 + 4, 8 + (count * itemTotal));

            var padding = actualWidth - width;
            var index = 0;

            Presenter.Padding = new Thickness(4, 0, 0, 0);

            Shadow.Width = width;
            Pill.Width = width;
            Presenter.Width = width;

            var height = 60;
            var haheight = 20;

            Pill.VerticalAlignment = VerticalAlignment.Top;
            Pill.Height = Shadow.Height = height + 20;
            Pill.Margin = Shadow.Margin = new Thickness(0, 0, 0, -20);

            Header.Text = Strings.AddEffectMessageHint;
            Header.Visibility = Visibility.Visible;

            if (select)
            {
                SetAutomation(Expand, count - 1, count);
                Expand.Visibility = Visibility.Visible;
            }
            else
            {
                Expand.Visibility = Visibility.Collapsed;
            }

            var figure = new PathFigure();
            if (count > 1)
            {
                figure.StartPoint = new Point(haheight, 0);
                figure.Segments.Add(new LineSegment { Point = new Point(width - haheight, 0) });
                figure.Segments.Add(new ArcSegment { Point = new Point(width, haheight), Size = new Size(haheight, haheight), RotationAngle = 90, SweepDirection = SweepDirection.Clockwise });
                figure.Segments.Add(new LineSegment { Point = new Point(width, height - haheight) });
                figure.Segments.Add(new ArcSegment { Point = new Point(width - haheight, height), Size = new Size(haheight, haheight), RotationAngle = 90, SweepDirection = SweepDirection.Clockwise });

                figure.Segments.Add(new ArcSegment { Point = new Point(width - haheight - 14, height), Size = new Size(7, 7), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });

                figure.Segments.Add(new LineSegment { Point = new Point(haheight, height) });
                figure.Segments.Add(new ArcSegment { Point = new Point(0, height - haheight), Size = new Size(haheight, haheight), RotationAngle = 90, SweepDirection = SweepDirection.Clockwise });
                figure.Segments.Add(new LineSegment { Point = new Point(0, haheight) });
                figure.Segments.Add(new ArcSegment { Point = new Point(haheight, 0), Size = new Size(haheight, haheight), RotationAngle = 90, SweepDirection = SweepDirection.Clockwise });

                //figure.Segments.Add(new ArcSegment { Point = new Point(width - haheight, height), Size = new Size(haheight, haheight), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });

                //figure.Segments.Add(new ArcSegment { Point = new Point(width - haheight - 14, height), Size = new Size(7, 7), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });

                //figure.Segments.Add(new LineSegment { Point = new Point(haheight, height) });
                //figure.Segments.Add(new ArcSegment { Point = new Point(haheight, 0), Size = new Size(haheight, haheight), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });
            }
            else
            {
                figure.StartPoint = new Point(20, 40);
                figure.Segments.Add(new BezierSegment { Point1 = new Point(31.0457, 40), Point2 = new Point(40, 31.0457), Point3 = new Point(40, 20) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(40, 8.9543), Point2 = new Point(31.0457, 0), Point3 = new Point(20, 0) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(8.9543, 0), Point2 = new Point(0, 8.9543), Point3 = new Point(0, 20) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(0, 26.3285), Point2 = new Point(2.93929, 31.9704), Point3 = new Point(7.52717, 35.6352) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(6.57139, 36.832), Point2 = new Point(6, 38.3493), Point3 = new Point(6, 40) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(6, 43.866), Point2 = new Point(9.13401, 47), Point3 = new Point(13, 47) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(16.866, 47), Point2 = new Point(20, 43.866), Point3 = new Point(20, 40) });
            }

            var path = new PathGeometry();
            path.Figures.Add(figure);

            var data = new GeometryGroup();
            data.FillRule = FillRule.Nonzero;
            data.Children.Add(path);
            data.Children.Add(new EllipseGeometry { Center = new Point(width - haheight - 8 + 5, height + 20 - 7), RadiusX = 3.5f, RadiusY = 3.5f });

            Pill.Data = data;

            LayoutRoot.Padding = new Thickness(16, 40, 16, 32);

            //var device = ElementComposition.GetSharedDevice();
            var rect1 = CanvasGeometry.CreateRectangle(null, Math.Min(width - actualWidth, 0), 0, Math.Max(width + 16 + 16 + Math.Max(0, padding), actualWidth), 860);
            var elli1 = CanvasGeometry.CreateRoundedRectangle(null, width - actualWidth + 18 + 16, height + height + 5, presenter.ActualSize.X, 860, 8, 8);
            var group1 = CanvasGeometry.CreateGroup(null, new[] { elli1, rect1 }, CanvasFilledRegionDetermination.Alternate);

            var rootVisual = ElementComposition.GetElementVisual(LayoutRoot);
            var compositor = rootVisual.Compositor;
            rootVisual.Clip = rootVisual.Compositor.CreateGeometricClip(rootVisual.Compositor.CreatePathGeometry(new CompositionPath(group1)));

            var headerVisual = ElementComposition.GetElementVisual(Header);
            headerVisual.CenterPoint = new Vector3((float)width / 2, 10, 0);

            // Through the guarded helper, which returns null on this head on purpose: Uno's
            // Compositor.CreateDropShadow throws instead of degrading. Called raw, this line is
            // where the reactions bar died -- after the Popup had been built but before it was
            // opened, so the context menu appeared and the bar never did. The shadow is
            // decoration; the bar is not. Same null contract as the other callers in the subset.
            var pillReceiver = VisualUtilities.DropShadow(Pill, 16, 0.14f, Shadow);
            if (pillReceiver != null)
            {
                pillReceiver.RelativeSizeAdjustment = Vector2.Zero;
                pillReceiver.Size = new Vector2(width, height + 20);
                pillReceiver.Offset = new Vector3(0, 8, 0);
            }

            var x = position.X - 18 + padding;
#if LINUX
            // u-095c: the same two faults the reactions bar had, in the effects picker. Uno places
            // a Popup by HorizontalOffset/VerticalOffset and the Margin below moves it nowhere, so
            // this drew at the window origin; and the anchor a menu is opened at is not the menu's
            // top edge once the menu has to open upward. Same helpers, and the pill here is 60
            // tall rather than 40, which is why they take the height.
            //
            // NOT REACHABLE on this head: the only caller is ChatView.Send_ContextRequested, whose
            // whole body is behind #if !LINUX, so nothing opens this picker yet. Fixed anyway
            // because it is the same defect in the same file and it would otherwise be waiting for
            // whoever un-fences that menu; unverified for exactly the same reason.
            var y = ResolveBarAnchorY(presenter, position, height) - (40 + 4);
#else
            var y = position.Y - (40 + 4);
#endif

            _popup.Child = this;
            _popup.Margin = new Thickness(x - 16, y - height, 0, 0);
#if LINUX
            _popup.HorizontalOffset = ClampBarLeft(presenter, x - 16, width);
            _popup.VerticalOffset = y - height;
#endif
            _popup.RequestedTheme = presenter.ActualTheme;
            _popup.ShouldConstrainToRootBounds = false;
            _popup.AllowFocusOnInteraction = false;
            _popup.XamlRoot = presenter.XamlRoot;
            this.ApplyChatTheme(presenter.XamlRoot);
            _popup.IsOpen = true;

            var visualPill = ElementComposition.GetElementVisual(Pill);
            visualPill.CenterPoint = new Vector3(height / 2, height / 2, 0);
            visualPill.CenterPoint = new Vector3(width - height / 2, height / 2, 0);

            var visualExpand = ElementComposition.GetElementVisual(Expand);
            visualExpand.CenterPoint = new Vector3(32 / 2f, 24 / 2f, 0);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            // One instance per target, and not the single shared one this used to be: Uno does
            // not share a CompositionAnimation between visuals, so starting the same object on
            // the second and third target leaves them unanimated. The pattern to grep for is
            // literal -- StartAnimation( twice with the same identifier -- and it was here three
            // times over, all on "Scale".
            CompositionAnimation ScalePill()
            {
#if LINUX
                // Compositor.CreateSpringVector3Animation is [NotImplemented] in Uno and THROWS.
                // That is what left the bar empty: InitializeCore died on this line, after the
                // pill was already on screen but before a single ReactionButton was created, so
                // the popup opened with nothing in it. Key frames draw the same
                // overshoot-and-settle the spring does; for a DampingRatio of 0.7 the peak sits
                // about 5% past the final value (it is 16% at the 0.5 used elsewhere).
                var settle = compositor.CreateVector3KeyFrameAnimation();
                settle.InsertKeyFrame(0, Vector3.Zero);
                settle.InsertKeyFrame(0.6f, new Vector3(1.05f, 1.05f, 1));
                settle.InsertKeyFrame(1, Vector3.One);
                settle.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                settle.Duration = Constants.FastAnimation;

                return settle;
#else
                var spring = compositor.CreateSpringVector3Animation();
                spring.InitialValue = Vector3.Zero;
                spring.FinalValue = Vector3.One;
                spring.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                spring.DampingRatio = 0.7f;

                return spring;
#endif
            }

            var translation = compositor.CreateScalarKeyFrameAnimation();
            translation.InsertKeyFrame(0, 0);
            translation.InsertKeyFrame(1, 16);

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(1, 0.14f);

            visualPill.StartAnimation("Scale", ScalePill());
            visualExpand.StartAnimation("Scale", ScalePill());
            headerVisual.StartAnimation("Scale", ScalePill());

            translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            translation.DelayTime = TimeSpan.FromMilliseconds(150 + 100);
            opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            opacity.DelayTime = TimeSpan.FromMilliseconds(150 + 100);

            // No shadow on this head, so nothing to animate: the helper above returned null and
            // the bar simply comes up flat.
            if (pillReceiver?.Shadow is DropShadow pillShadow)
            {
                pillShadow.StartAnimation("BlurRadius", translation);
                pillShadow.StartAnimation("Opacity", opacity);
            }

            var resize = compositor.CreateVector2KeyFrameAnimation();
            resize.InsertKeyFrame(0, new Vector2(height, height));
            resize.InsertKeyFrame(1, new Vector2(width, height));
            resize.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            resize.DelayTime = TimeSpan.FromMilliseconds(100);
            resize.Duration = Constants.FastAnimation;

            var move = compositor.CreateVector2KeyFrameAnimation();
            move.InsertKeyFrame(0, new Vector2(width - height, 40));
            move.InsertKeyFrame(1, new Vector2(0, 40));
            move.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            move.DelayTime = TimeSpan.FromMilliseconds(100);
            move.Duration = Constants.FastAnimation;

            var viewVisual = ElementComposition.GetElementVisual(Presenter);
            viewVisual.CenterPoint = new Vector3(height / 2, height / 2, 0);
            viewVisual.CenterPoint = new Vector3(width - height / 2, height / 2, 0);
            viewVisual.StartAnimation("Scale", ScalePill());

            batch.End();

            var viewModel = _viewModel;
            var items = await clientService.GetMessageEffectsAsync(effectIds.Take(select ? 6 : sum));

            foreach (var item in items)
            {
                static AnimatedImage Create(double size, bool auto)
                {
                    var animated = new AnimatedImage();
                    animated.AutoPlay = auto;
                    animated.LimitFps = !auto;
                    animated.LoopCount = auto ? 1 : 0;
                    animated.FrameSize = new Size(size, size);
                    animated.DecodeFrameType = DecodePixelType.Logical;
                    animated.Width = size;
                    animated.Height = size;

                    return animated;
                }

                var visible = Create(28, true);
                var preload = Create(32, false);

                if (item.Type is MessageEffectTypeEmojiReaction emojiReaction)
                {
                    visible.Source = new DelayedFileSource(clientService, emojiReaction.SelectAnimation);
                    preload.Source = new DelayedFileSource(clientService, emojiReaction.SelectAnimation.StickerValue);
                }

                preload.LoopCompleted += (s, args) => args.Cancel = true;
                preload.Opacity = 0;
                preload.Play();

                var button = new HyperlinkButton();
                button.Width = 28;
                button.Height = 28;
                button.Background = new SolidColorBrush(Colors.Transparent);
                button.CornerRadius = new CornerRadius(14);
                button.Margin = new Thickness(2, 0, 2, 0);
                button.Content = visible;
                button.Style = BootStrapper.Current.Resources["EmptyHyperlinkButtonStyle"] as Style;
                button.Tag = item;
                button.Click += Reaction_Click;

                AutomationProperties.SetName(button, item.Emoji);

                SetAutomation(button, index, count);

                Grid.SetColumn(preload, index);
                Grid.SetColumn(button, index);

                Presenter.ColumnDefinitions.Add(1, GridUnitType.Auto);
                Presenter.Children.Insert(index, button);
                Preloader.Children.Add(preload);
                index++;
            }

            Presenter.ColumnDefinitions.Add(1, GridUnitType.Auto);
        }

        public async void Initialize(IClientService clientService)
        {
            var empty = Array.Empty<AvailableReaction>();
            var reactions = clientService.ActiveReactions
                .Select(x => new AvailableReaction(new ReactionTypeEmoji(x), false))
                .ToVector();

            var viewModel = EmojiDrawerViewModel.Create(clientService.Session, EmojiDrawerMode.Reactions);
            _ = viewModel.UpdateReactions(new AvailableReactions(reactions, empty, empty, true, false, null));

            _viewModel = viewModel;
            _clientService = clientService;
            _popup = new Popup();

            Presenter.PreviewKeyDown += OnPreviewKeyDown;

            static void SetAutomation(UIElement element, int index, int count)
            {
                if (ApiInfo.IsWindows11 && false)
                {
                    AutomationProperties.SetAutomationControlType(element, AutomationControlType.ListItem);
                }
                else
                {
                    AutomationProperties.SetPositionInSet(element, index + 1);
                    AutomationProperties.SetSizeOfSet(element, count);
                }
            }

            var sum = reactions.Count;

            var select = true || sum > 7;
            var count = select ? 7 : sum;

            var itemSize = 28;
            var itemPadding = 4;

            var itemTotal = itemSize + itemPadding;

            var presenter = this;

            var actualWidth = presenter.ActualSize.X + 18 + 12 + 18;
            var width = Math.Max(36 + 4, 8 + (count * itemTotal));

            var padding = actualWidth - width;
            var index = 0;

            Presenter.Padding = new Thickness(4, 0, 0, 0);

            Shadow.Width = width;
            Pill.Width = width;
            Presenter.Width = width;

            var height = 40;
            var haheight = 20;

            Pill.VerticalAlignment = VerticalAlignment.Top;
            Pill.Height = Shadow.Height = height + 20;
            Pill.Margin = Shadow.Margin = new Thickness(0, 0, 0, -20);

            if (select)
            {
                SetAutomation(Expand, count - 1, count);
                Expand.Visibility = Visibility.Visible;
            }
            else
            {
                Expand.Visibility = Visibility.Collapsed;
            }

            var figure = new PathFigure();
            if (count > 1)
            {
                figure.StartPoint = new Point(haheight, 0);
                figure.Segments.Add(new LineSegment { Point = new Point(width - haheight, 0) });
                figure.Segments.Add(new ArcSegment { Point = new Point(width - haheight, height), Size = new Size(haheight, haheight), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });

                //figure.Segments.Add(new ArcSegment { Point = new Point(width - haheight - 14, height), Size = new Size(7, 7), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });

                figure.Segments.Add(new LineSegment { Point = new Point(haheight, height) });
                figure.Segments.Add(new ArcSegment { Point = new Point(haheight, 0), Size = new Size(haheight, haheight), RotationAngle = 180, SweepDirection = SweepDirection.Clockwise });
            }
            else
            {
                figure.StartPoint = new Point(20, 40);
                figure.Segments.Add(new BezierSegment { Point1 = new Point(31.0457, 40), Point2 = new Point(40, 31.0457), Point3 = new Point(40, 20) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(40, 8.9543), Point2 = new Point(31.0457, 0), Point3 = new Point(20, 0) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(8.9543, 0), Point2 = new Point(0, 8.9543), Point3 = new Point(0, 20) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(0, 26.3285), Point2 = new Point(2.93929, 31.9704), Point3 = new Point(7.52717, 35.6352) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(6.57139, 36.832), Point2 = new Point(6, 38.3493), Point3 = new Point(6, 40) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(6, 43.866), Point2 = new Point(9.13401, 47), Point3 = new Point(13, 47) });
                figure.Segments.Add(new BezierSegment { Point1 = new Point(16.866, 47), Point2 = new Point(20, 43.866), Point3 = new Point(20, 40) });
            }

            var path = new PathGeometry();
            path.Figures.Add(figure);

            var data = new GeometryGroup();
            data.FillRule = FillRule.Nonzero;
            data.Children.Add(path);
            //data.Children.Add(new EllipseGeometry { Center = new Point(width - haheight - 8 + 5, height + 20 - 7), RadiusX = 3.5f, RadiusY = 3.5f });

            Pill.Data = data;

            LayoutRoot.Padding = new Thickness(16, 40, 16, 32);

            var rect1 = CanvasGeometry.CreateRectangle(null, Math.Min(width - actualWidth, 0), 0, Math.Max(width + 16 + 16 + Math.Max(0, padding), actualWidth), 860);
            var elli1 = CanvasGeometry.CreateRoundedRectangle(null, width - actualWidth + 18 + 16, height + height + 5, presenter.ActualSize.X, 860, 8, 8);
            var group1 = CanvasGeometry.CreateGroup(null, new[] { elli1, rect1 }, CanvasFilledRegionDetermination.Alternate);

            var rootVisual = ElementComposition.GetElementVisual(LayoutRoot);
            var compositor = rootVisual.Compositor;
            rootVisual.Clip = null;//rootVisual.Compositor.CreateGeometricClip(rootVisual.Compositor.CreatePathGeometry(new CompositionPath(group1)));

            // Through the guarded helper, which returns null on this head on purpose: Uno's
            // Compositor.CreateDropShadow throws instead of degrading. Called raw, this line is
            // where the reactions bar died -- after the Popup had been built but before it was
            // opened, so the context menu appeared and the bar never did. The shadow is
            // decoration; the bar is not. Same null contract as the other callers in the subset.
            var pillReceiver = VisualUtilities.DropShadow(Pill, 16, 0.14f, Shadow);
            if (pillReceiver != null)
            {
                pillReceiver.RelativeSizeAdjustment = Vector2.Zero;
                pillReceiver.Size = new Vector2(width, height + 20);
                pillReceiver.Offset = new Vector3(0, 8, 0);
            }

            var visualPill = ElementComposition.GetElementVisual(Pill);
            visualPill.CenterPoint = new Vector3(height / 2, height / 2, 0);
            visualPill.CenterPoint = new Vector3(width - height / 2, height / 2, 0);

            var visualExpand = ElementComposition.GetElementVisual(Expand);
            visualExpand.CenterPoint = new Vector3(32 / 2f, 24 / 2f, 0);

            var clip = compositor.CreateRoundedRectangleGeometry();
            clip.CornerRadius = new Vector2(height / 2);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            // One instance per target, and not the single shared one this used to be: Uno does
            // not share a CompositionAnimation between visuals, so starting the same object on
            // the second and third target leaves them unanimated. The pattern to grep for is
            // literal -- StartAnimation( twice with the same identifier -- and it was here three
            // times over, all on "Scale".
            CompositionAnimation ScalePill()
            {
#if LINUX
                // Compositor.CreateSpringVector3Animation is [NotImplemented] in Uno and THROWS.
                // That is what left the bar empty: InitializeCore died on this line, after the
                // pill was already on screen but before a single ReactionButton was created, so
                // the popup opened with nothing in it. Key frames draw the same
                // overshoot-and-settle the spring does; for a DampingRatio of 0.7 the peak sits
                // about 5% past the final value (it is 16% at the 0.5 used elsewhere).
                var settle = compositor.CreateVector3KeyFrameAnimation();
                settle.InsertKeyFrame(0, Vector3.Zero);
                settle.InsertKeyFrame(0.6f, new Vector3(1.05f, 1.05f, 1));
                settle.InsertKeyFrame(1, Vector3.One);
                settle.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                settle.Duration = Constants.FastAnimation;

                return settle;
#else
                var spring = compositor.CreateSpringVector3Animation();
                spring.InitialValue = Vector3.Zero;
                spring.FinalValue = Vector3.One;
                spring.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                spring.DampingRatio = 0.7f;

                return spring;
#endif
            }

            var translation = compositor.CreateScalarKeyFrameAnimation();
            translation.InsertKeyFrame(0, 0);
            translation.InsertKeyFrame(1, 16);

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(1, 0.14f);

            visualPill.StartAnimation("Scale", ScalePill());
            visualExpand.StartAnimation("Scale", ScalePill());

            translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            translation.DelayTime = TimeSpan.FromMilliseconds(150 + 100);
            opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            opacity.DelayTime = TimeSpan.FromMilliseconds(150 + 100);

            // No shadow on this head, so nothing to animate: the helper above returned null and
            // the bar simply comes up flat.
            if (pillReceiver?.Shadow is DropShadow pillShadow)
            {
                pillShadow.StartAnimation("BlurRadius", translation);
                pillShadow.StartAnimation("Opacity", opacity);
            }

            var resize = compositor.CreateVector2KeyFrameAnimation();
            resize.InsertKeyFrame(0, new Vector2(height, height));
            resize.InsertKeyFrame(1, new Vector2(width, height));
            resize.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            resize.DelayTime = TimeSpan.FromMilliseconds(100);
            resize.Duration = Constants.FastAnimation;

            var move = compositor.CreateVector2KeyFrameAnimation();
            move.InsertKeyFrame(0, new Vector2(width - height, 40));
            move.InsertKeyFrame(1, new Vector2(0, 40));
            move.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            move.DelayTime = TimeSpan.FromMilliseconds(100);
            move.Duration = Constants.FastAnimation;

            var viewVisual = ElementComposition.GetElementVisual(Presenter);
            viewVisual.CenterPoint = new Vector3(height / 2, height / 2, 0);
            viewVisual.CenterPoint = new Vector3(width - height / 2, height / 2, 0);
            viewVisual.StartAnimation("Scale", ScalePill());

            batch.End();

            Presenter.ColumnDefinitions.Clear();

            for (int i = 0; i < Presenter.Children.Count - 2; i++)
            {
                Presenter.Children.RemoveAt(i);
                i--;
            }

            foreach (var reaction in reactions.Take(count - 1))
            {
                static AnimatedImage Create(double size, bool auto)
                {
                    var animated = new AnimatedImage();
                    animated.AutoPlay = auto;
                    animated.LimitFps = !auto;
                    animated.LoopCount = auto ? 1 : 0;
                    animated.FrameSize = new Size(size, size);
                    animated.DecodeFrameType = DecodePixelType.Logical;
                    animated.Width = size;
                    animated.Height = size;

                    return animated;
                }

                var visible = Create(28, true);
                var preload = Create(32, false);

                if (reaction.Type is not ReactionTypeEmoji emoji)
                {
                    continue;
                }

                var response = await clientService.SendAsync(new GetEmojiReaction(emoji.Emoji));
                if (response is not EmojiReaction item)
                {
                    continue;
                }

                visible.Source = new DelayedFileSource(clientService, item.SelectAnimation);
                preload.Source = new DelayedFileSource(clientService, item.SelectAnimation.StickerValue);

                preload.LoopCompleted += (s, args) => args.Cancel = true;
                preload.Opacity = 0;
                preload.Play();

                var button = new HyperlinkButton();
                button.Width = 28;
                button.Height = 28;
                button.Background = new SolidColorBrush(Colors.Transparent);
                button.CornerRadius = new CornerRadius(14);
                button.Margin = new Thickness(2, 0, 2, 0);
                button.Content = visible;
                button.Style = BootStrapper.Current.Resources["EmptyHyperlinkButtonStyle"] as Style;
                button.Tag = reaction;
                button.Click += Reaction_Click;

                AutomationProperties.SetName(button, item.Emoji);

                SetAutomation(button, index, count);

                Grid.SetColumn(preload, index);
                Grid.SetColumn(button, index);

                Presenter.ColumnDefinitions.Add(1, GridUnitType.Auto);
                Presenter.Children.Insert(index, button);
                Preloader.Children.Add(preload);
                index++;
            }

            Presenter.ColumnDefinitions.Add(1, GridUnitType.Auto);
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is VirtualKey.Tab && _presenter != null)
            {
                e.Handled = true;
                _presenter.Focus(FocusState.Keyboard);
            }
            else if (e.Key is VirtualKey.Left or VirtualKey.Right)
            {
                e.Handled = true;

                var down = e.Key is VirtualKey.Right;
                var delta = down ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous;

                var focused = FocusManager.FindNextFocusableElement(delta);
                if (focused is Control control)
                {
                    control.Focus(FocusState.Keyboard);
                    return;
                }
            }
            else if (e.Key is VirtualKey.Up or VirtualKey.Down && _flyout != null)
            {
                e.Handled = true;

                var down = e.Key is VirtualKey.Down;
                var delta = down ? _flyout.Items[0] : FindLast();

                delta.Focus(FocusState.Keyboard);
            }
        }

        private void Presenter_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key is VirtualKey.Tab)
            {
                e.Handled = true;
                Focus(FocusState.Keyboard);
            }
            else if ((e.Key is VirtualKey.Up && e.OriginalSource == _flyout.Items[0]) || (e.Key is VirtualKey.Down && e.OriginalSource == FindLast()))
            {
                var control = FocusManager.FindFirstFocusableElement(Presenter) as Control;
                if (control != null && control.Focus(FocusState.Keyboard))
                {
                    e.Handled = true;
                }
            }
        }

        private MenuFlyoutItem FindLast()
        {
            for (int i = _flyout.Items.Count - 1; i >= 0; i--)
            {
                if (_flyout.Items[i] is MenuFlyoutItem item)
                {
                    return item;
                }
            }

            return null;
        }

        private void Flyout_Closed(object sender, object e)
        {
            _popup.IsOpen = false;

            if (_bubble is FrameworkElement element && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            {
                var selector = element.GetParent<SelectorItem>();
                selector?.Focus(FocusState.Keyboard);
            }
        }

        private void Reaction_Click(object sender, RoutedEventArgs e)
        {
            _flyout?.Hide();

            if (sender is HyperlinkButton button)
            {
                if (button.Tag is AvailableReaction reaction)
                {
#if !LINUX
                    if (_story != null)
                    {
                        StoryToggleReaction(reaction);
                    }
                    else if (_message != null)
#else
                    if (_message != null)
#endif
                    {
                        MessageToggleReaction(reaction);
                    }
                    else
                    {
                        ItemClick?.Invoke(this, reaction);
                    }
                }
                else if (button.Tag is MessageEffect effect)
                {
                    Selected?.Invoke(this, effect);
                }
            }
        }

        public event EventHandler<MessageEffect> Selected;

        public event EventHandler<AvailableReaction> ItemClick;

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
                if (_reactions != null && _reactions.AreTags)
                {
                    WindowContext.GetNavigationService(this).ShowPromo(new PremiumFeatureSavedMessagesTags());
                }
                else
                {
                    ToastPopup.ShowFeaturePromo(WindowContext.GetNavigationService(this), new PremiumFeatureUniqueReactions());
                }

                return;
            }

            if (reaction.Type is not ReactionTypePaid && message.InteractionInfo?.Reactions != null && message.InteractionInfo.Reactions.IsChosen(reaction.Type))
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
                    // u-064: paid (Stars) reactions need the Stars purchase flow, out of the
                    // subset. Same reduction as EmojiMenuFlyout's MessageToggleReaction.
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

        private void Expand_Click(object sender, RoutedEventArgs e)
        {
            if (_flyout != null)
            {
                _flyout.Closed -= Flyout_Closed;
            }

#if !LINUX
            if (_story != null)
            {
                var flyout = EmojiMenuFlyout.ShowAt(this, _story, _reserved, _reactions, _viewModel);
                flyout.Loaded += (s, args) =>
                {
                    _flyout?.Hide();
                };
                flyout.Opened += (s, args) =>
                {
                    _popup.IsOpen = false;
                };
            }
            else if (_message != null)
#else
            if (_message != null)
#endif
            {
                var flyout = EmojiMenuFlyout.ShowAt(this, _message, _bubble, _reactions, _viewModel);
                flyout.Loaded += (s, args) =>
                {
                    _flyout?.Hide();
                };
                flyout.Opened += (s, args) =>
                {
                    _popup.IsOpen = false;
                };
            }
            else if (ItemClick != null)
            {
                var empty = Array.Empty<AvailableReaction>();
                var reactions = _viewModel.ClientService.ActiveReactions
                    .Select(x => new AvailableReaction(new ReactionTypeEmoji(x), false))
                    .ToList();

                var flyout = EmojiMenuFlyout.ShowAt(_clientService, EmojiDrawerMode.Reactions, this, EmojiFlyoutAlignment.Top, _viewModel);
                flyout.ItemClick += ItemClick;
                flyout.Loaded += (s, args) =>
                {
                    _flyout?.Hide();
                };
                flyout.Opened += (s, args) =>
                {
                    _popup.IsOpen = false;
                };
            }
#if !LINUX
            else
            {
                var flyout = MessageEffectMenuFlyout.ShowAt(_clientService, this, EmojiFlyoutAlignment.Center);
                flyout.Selected += Selected;
                flyout.Loaded += (s, args) =>
                {
                    _flyout?.Hide();
                };
                flyout.Opened += (s, args) =>
                {
                    _popup.IsOpen = false;
                };
            }
#endif
        }
    }
}
