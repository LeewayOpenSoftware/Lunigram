//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Globalization;
using System.Numerics;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls.Cells
{
    public sealed partial class ActiveStoriesCell : UserControl
    {
        /// <summary>
        /// Outer side of the ring, and the size everything else in the cell is derived from.
        /// </summary>
        /// <remarks>
        /// Compact on Linux, where the strip does not get a band of its own: it shares the 40px
        /// caption row with the Telegram logo and the window drag handle
        /// (MainPage.MoveStoriesIntoTitleBar), which is where upstream draws it too. A name does
        /// not fit in 40px and upstream does not draw one up there either, so the cell is the ring
        /// and nothing else. 36 is deliberately a little larger than the 32px logo button it sits
        /// next to - that is the comparison the caption row is judged by.
        /// </remarks>
#if LINUX
        private const int Side = 36;
#else
        private const int Side = 48;
#endif

        public ActiveStoriesCell()
        {
            InitializeComponent();
#if LINUX
            LayoutRoot.Width = Side + 4;
            LayoutRoot.Padding = new Thickness(0, 2, 0, 2);

            // The name is what the 40px row has no room for.
            Title.Visibility = Visibility.Collapsed;

            PhotoCiccio.Width = PhotoCiccio.Height = Side;
            PhotoCiccio.CornerRadius = new CornerRadius(Side / 2d);
            PhotoRoot.Width = PhotoRoot.Height = Side;

            // Same 8px the ring takes out of the avatar upstream (Photo 40 inside a 48 side), so
            // the segments keep drawing around the picture and not over it.
            Photo.Size = Side - 8;

            Segments.Width = Segments.Height = Side;
            SegmentsSmall.Width = SegmentsSmall.Height = Side;
#endif
        }

        private ActiveStoriesViewModel _viewModel;
        public ActiveStoriesViewModel ViewModel => _viewModel;

        private string _automationName;
        public string GetAutomationName()
        {
            return _automationName;
        }

        public void Update(ActiveStoriesViewModel activeStories)
        {
            _viewModel = activeStories;

            var chat = activeStories.Chat;
            if (activeStories.ClientService.TryGetUser(chat, out User user))
            {
                if (activeStories.IsMyStory)
                {
                    _automationName = Strings.MyStory;
                    Title.Text = Strings.MyStory;
                }
                else
                {
                    _automationName = string.Format(Strings.AccDescrStoryBy, user.FirstName);
                    Title.Text = user.FirstName;
                }

                Photo.Source = ProfilePictureSource.User(activeStories.ClientService, user);

            }
            else
            {
                _automationName = string.Format(Strings.AccDescrStoryBy, chat.Title);
                Title.Text = chat.Title;

                Photo.Source = ProfilePictureSource.Chat(activeStories.ClientService, chat);
            }
        }

        private ChatActiveStories _trigger;

        /// <summary>
        /// The story state the rings are drawn from, pushed in by
        /// <c>Trigger="{x:Bind Item, Mode=OneWay}"</c> in StoriesStrip.xaml.
        /// </summary>
        /// <remarks>
        /// The getter is not used by anything: it is there because this is an x:Bind target and a
        /// set-only property is a shape the XAML generators are not guaranteed to accept.
        /// </remarks>
        // Was `internal` on Linux for a while, to dodge the CS0400 that Uno's
        // BindableTypeProviders raises for a type built by ANOTHER source generator. That worked
        // for the compiler and broke the binding: Uno resolves a binding setter by reflection over
        // PUBLIC members, so every cell logged "The property setter for [Trigger] does not exist on
        // ActiveStoriesCell" and the rings never got their data. Measured with the app running on
        // 2026-08-26. The compile-time half is fixed where it belongs, with an empty partial in
        // Telegram.Linux/Xaml/TdApiPartials.cs, and this is public on both heads again.
        public ChatActiveStories Trigger
        {
            get => _trigger;
            set
            {
                _trigger = value;

                Segments.UpdateActiveStories(value, Side, true);
                SegmentsSmall.UpdateActiveStories(value, Side, false);

                LiveBadge.Visibility = Segments.HasLiveBadge
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public void Update(SelectorItem container, int index, int f, int l, CompositionPropertySet tracker, ExpressionAnimation expression)
        {
            if (tracker == null)
            {
                return;
            }

            var visual = ElementComposition.GetElementVisual(container);
            var ciccio = ElementComposition.GetElementVisual(PhotoCiccio);
            var photo = ElementComposition.GetElementVisual(PhotoRoot);
            var title = ElementComposition.GetElementVisual(Title);
            var gradient = ElementComposition.GetElementVisual(SegmentsRoot);
            var cross1 = ElementComposition.GetElementVisual(Segments);
            var cross2 = ElementComposition.GetElementVisual(SegmentsSmall);
            var live = ElementComposition.GetElementVisual(LiveBadge);

            var included = index >= f && index <= l;
            var clamp = Math.Clamp(index, f, l);

            var prevX = 72 * index + 6f - (12 * clamp) /* + 14 */;
            var nextX = 0;

            var diffX = prevX - nextX;
            //var boh = 1 - progress;

            ElementCompositionPreview.SetIsTranslationEnabled(container, true);
            ElementCompositionPreview.SetIsTranslationEnabled(Title, true);

            //visual.Properties.InsertVector3("Translation", new Vector3(-prevX + diffX * boh, 0, 0));
            visual.CenterPoint = new Vector3(12 + 24);
            //visual.Scale = new Vector3(min + max * (1 - progress));

            var compositor = visual.Compositor;

            var visualTranslationX = compositor.CreateExpressionAnimation(string.Format(CultureInfo.InvariantCulture, "-{0} + {1} * _.Progress", prevX, diffX));

            // One instance per START, not per expression. Uno's Compositor keeps its running
            // animations in a dictionary KEYED BY THE ANIMATION INSTANCE (RegisterAnimation is
            // _animations.Add(animation, visual)), so the same object started a second time while
            // the first is still running throws ArgumentException -- and an exception here takes
            // the dispatcher with it, which is how RootPage froze the app with 32 threads at 0%.
            // WinUI shares instances happily, which is why this code read as correct.
            //
            // An ExpressionAnimation shared across targets is worse than the throw on its own: its
            // ReferenceParameters are live (last write wins), one AnimationFrame subscription is
            // shared, and Stop() on any target disposes the parsed expression the others are still
            // evaluating -- the SIGABRT that killed SlidePanel. That last one is not theoretical
            // here: the recycled-cell path below calls ciccio.StopAnimation("Opacity").
            //
            // So the expression STRING is what gets shared; every start gets its own animation.
            ExpressionAnimation Track(string expression)
            {
                var animation = compositor.CreateExpressionAnimation(expression);
                animation.SetReferenceParameter("_", tracker);

                return animation;
            }

            // Two properties of one visual are still two concurrent starts, and the dictionary is
            // keyed by the instance, not by the target.
            const string scaleExpression = "0.5 + 0.5 * _.Progress";

            visualTranslationX.SetReferenceParameter("_", tracker);
            visual.StartAnimation("Translation.X", visualTranslationX);
            visual.StartAnimation("Scale.X", Track(scaleExpression));
            visual.StartAnimation("Scale.Y", Track(scaleExpression));

            if (index >= f && index < l)
            {
                // TODO: replace this with an ellipse in the UI
                var rect1 = CanvasGeometry.CreateRectangle(null, 0, 0, 48, 48);
                var elli1 = CanvasGeometry.CreateEllipse(null, 48 + 72 * 0, 24, 22, 22);
                var elli2 = CanvasGeometry.CreateEllipse(null, 48 + 72 * 1, 24, 22, 22);
                var group1 = CanvasGeometry.CreateGroup(null, new[] { elli1, rect1 }, CanvasFilledRegionDetermination.Alternate);
                var group2 = CanvasGeometry.CreateGroup(null, new[] { elli2, rect1 }, CanvasFilledRegionDetermination.Alternate);

                var geometry1 = compositor.CreatePathGeometry(new CompositionPath(group1));
                var clip1 = compositor.CreateGeometricClip(geometry1);

                var linear = compositor.CreateLinearEasingFunction();
                var pathAnimation = compositor.CreatePathKeyFrameAnimation();
                pathAnimation.InsertKeyFrame(0, new CompositionPath(group1), linear);
                pathAnimation.InsertKeyFrame(1, new CompositionPath(group2), linear);

                geometry1.StartAnimation("Path", pathAnimation);
                var controller = geometry1.TryGetAnimationController("Path");
                controller.Pause();

                // Its own instance: for f < index < l BOTH of these blocks run, so the old shared
                // `clean` was started on two controllers in the same pass.
                controller.StartAnimation("Progress", Track("_.Progress"));

                gradient.Clip = clip1;
            }
            else
            {
                gradient.Clip = null;
            }

            if (index > f && index <= l)
            {
                // TODO: replace this with an ellipse in the UI
                var rect1 = CanvasGeometry.CreateRectangle(null, 0, 0, 48, 48);
                var elli1 = CanvasGeometry.CreateEllipse(null, -0 + -72 * 0, 24, 22, 22);
                var elli2 = CanvasGeometry.CreateEllipse(null, -0 + -72 * 1, 24, 22, 22);
                var group1 = CanvasGeometry.CreateGroup(null, new[] { elli1, rect1 }, CanvasFilledRegionDetermination.Alternate);
                var group2 = CanvasGeometry.CreateGroup(null, new[] { elli2, rect1 }, CanvasFilledRegionDetermination.Alternate);

                var geometry1 = compositor.CreatePathGeometry(new CompositionPath(group1));
                var clip1 = compositor.CreateGeometricClip(geometry1);

                var linear = compositor.CreateLinearEasingFunction();
                var pathAnimation = compositor.CreatePathKeyFrameAnimation();
                pathAnimation.InsertKeyFrame(0, new CompositionPath(group1), linear);
                pathAnimation.InsertKeyFrame(1, new CompositionPath(group2), linear);

                geometry1.StartAnimation("Path", pathAnimation);
                var controller = geometry1.TryGetAnimationController("Path");
                controller.Pause();
                controller.StartAnimation("Progress", Track("_.Progress"));

                photo.Clip = clip1;
            }
            else
            {
                photo.Clip = null;
            }

            photo.CenterPoint = new Vector3(24);
            //photo.Scale = new Vector3(min + max * (1 - progress));
            //title.Scale = new Vector3(min + max * (1 - progress));
            //photo.Opacity = included ? 1 : 1 - progress;
            //title.Opacity = 1 - progress;

            var distance = Math.Max(0, index - l) / 10f;
            var multiplier = 2 + Math.Max(0, 0.5f - distance);
            var normalizer = 1 + 0;

            var fadeOut = string.Format(CultureInfo.InvariantCulture, "Max(0, {0} * _.Progress - {1})", multiplier, normalizer);
            var fadeIn = string.Format(CultureInfo.InvariantCulture, "Max(0, 1 - ({0} * _.Progress - {1}))", multiplier, normalizer);

            // Four targets for the same expression, five with the recycled-cell branch below.
            title.StartAnimation("Opacity", Track(fadeOut));
            live.StartAnimation("Opacity", Track(fadeOut));
            cross1.StartAnimation("Opacity", Track(fadeOut));
            cross2.StartAnimation("Opacity", Track(fadeIn));

            if (included)
            {

                ciccio.StopAnimation("Opacity");
                ciccio.Opacity = 1;
            }
            else
            {
                ciccio.StartAnimation("Opacity", Track(fadeOut));
            }

            return;
            //title.Properties.InsertVector3("Translation", new Vector3(0, -28 * progress, 0));







            var ellisss = compositor.CreateEllipseGeometry();
            ellisss.Radius = new Vector2(23);
            ellisss.Center = new Vector2(24, 24);

            var shape2 = compositor.CreateSpriteShape();
            shape2.Geometry = ellisss;
            shape2.StrokeBrush = compositor.CreateColorBrush(index == 1 ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Blue);
            shape2.StrokeThickness = 2;

            var test = compositor.CreateShapeVisual();
            test.Size = new Vector2(48, 48);
            test.Shapes.Add(shape2);


            var visualScalezzzz = compositor.CreateExpressionAnimation(
                $"2 - 1 * _.Progress");

            visualScalezzzz.SetReferenceParameter("_", tracker);
            shape2.StartAnimation("StrokeThickness", visualScalezzzz);

            //ElementCompositionPreview.SetElementChildVisual(PhotoRoot, test);
        }

        public void Disconnect(SelectorItem container)
        {
            var visual = ElementComposition.GetElementVisual(container);
            var ciccio = ElementComposition.GetElementVisual(PhotoCiccio);
            var photo = ElementComposition.GetElementVisual(PhotoRoot);
            var title = ElementComposition.GetElementVisual(Title);
            var gradient = ElementComposition.GetElementVisual(SegmentsRoot);
            var cross1 = ElementComposition.GetElementVisual(Segments);
            var cross2 = ElementComposition.GetElementVisual(SegmentsSmall);
            var live = ElementComposition.GetElementVisual(LiveBadge);

            ElementCompositionPreview.SetIsTranslationEnabled(container, true);
            ElementCompositionPreview.SetIsTranslationEnabled(Title, true);

            visual.StopAnimation("Translation.X");
            visual.StopAnimation("Scale.X");
            visual.StopAnimation("Scale.Y");

            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Scale = Vector3.One;

            gradient.Clip = null;
            photo.Clip = null;

            photo.CenterPoint = new Vector3(24);

            title.StopAnimation("Opacity");
            live.StopAnimation("Opacity");
            cross1.StopAnimation("Opacity");
            cross2.StopAnimation("Opacity");
            ciccio.StopAnimation("Opacity");

            title.Opacity = 1;
            live.Opacity = 1;
            cross1.Opacity = 1;
            cross2.Opacity = 0;
            ciccio.Opacity = 1;
        }
    }
}
