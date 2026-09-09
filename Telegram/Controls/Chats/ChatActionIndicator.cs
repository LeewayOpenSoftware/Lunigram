//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Controls;
using System;
#if LINUX
using System.Collections.Generic;
#endif
using System.Numerics;
#if !LINUX
using Telegram.Assets.Icons;
#endif
using Telegram.Navigation;
using Telegram.Td.Api;
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls.Chats
{
    public partial class ChatActionIndicator : FrameworkElement
    {
        // This should be held in memory, or animation will stop
        private CompositionPropertySet _props;

        private IAnimatedVisual _previous;
        private AnimationType _action;

        private enum AnimationType
        {
            None,
            Typing,
            Uploading,
            Playing,
            VideoRecording,
            VoiceRecording,
            Watching
        }

        public void UpdateAction(ChatAction action)
        {
            var type = GetAnimationType(action);
            if (type == _action)
            {
                return;
            }

#if LINUX
            _action = type;
            UpdateVisual(type);
            return;
#else
            var color = Fill?.Color ?? Colors.Black;
            var visual = GetVisual(type, BootStrapper.Current.Compositor, color, out _props);

            _action = type;
            _previous = visual;

            if (visual?.RootVisual != null)
            {
                visual.RootVisual.Scale = new Vector3(0.1f, 0.1f, 1.0f);
            }

            ElementCompositionPreview.SetElementChildVisual(this, visual?.RootVisual);
#endif
        }

#if LINUX
        #region A drawn glyph per action, and animations that actually get to tick

        /// <summary>
        /// The animations of the visual currently hanging off this element, so they can be started
        /// once it is in the tree and stopped when it leaves it.
        /// </summary>
        private readonly List<(Visual Visual, string Property, CompositionAnimation Animation)> _animated = new();

        private bool _hooked;
        private bool _started;

        /// <summary>
        /// Builds the glyph for <paramref name="type"/>, hangs it off this element and starts it.
        /// </summary>
        /// <remarks>
        /// <para>The upstream indicator is one of six LottieGen visuals from
        /// <c>Telegram.Assets.Icons</c>, which is not part of the Linux build, so
        /// <see cref="GetVisual(AnimationType, Compositor, Color)"/> returns <c>null</c> here and
        /// the control was a 20x20 hole. The first pass at this drew the three typing dots for all
        /// six actions, which reads wrong the moment two people in a group are doing different
        /// things: the label says "recording audio" next to the icon for typing. So there is one
        /// silhouette per action now, drawn from the primitives Uno's Skia compositor does render.
        /// </para>
        /// <para><b>What the drawing is allowed to use</b>, all read off Uno 6.6.184's IL rather
        /// than assumed. <c>CompositionSpriteShape</c> paints both <c>FillBrush</c> and
        /// <c>StrokeBrush</c> (the stroke goes through <c>SKPaint.IsStroke</c>), so an outline is a
        /// legal shape and the eye and the camera can be hollow. A <c>CompositionShape</c> carries
        /// a static <c>Offset</c>/<c>Scale</c>/<c>RotationAngle</c> that ends up in the geometry's
        /// transform, but it has <b>no</b> <c>SetAnimatableProperty</c> override, and
        /// <c>Compositor.RegisterAnimation</c> bails out unless the target
        /// <c>is Visual</c> - so a shape can be placed but never animated. Everything that moves
        /// here is therefore a <see cref="ShapeVisual"/>, and it moves through <c>Opacity</c> or
        /// <c>Offset</c>, two of the ten names <c>Visual.SetAnimatableProperty</c> knows.
        /// <c>CompositionGeometry</c> only takes <c>TrimStart</c>/<c>TrimEnd</c>/<c>TrimOffset</c>,
        /// which is the measured half of the PORTING.md section 6 note about <c>Size</c>; and the
        /// note about <c>RotationAngleInDegrees</c> is now explained - the animatable name is
        /// <c>RotationAngle</c>, in radians, and nothing here needs either.</para>
        /// <para><b>Why the old dots never moved.</b>
        /// <c>Compositor.RegisterAnimation</c> starts with
        /// <c>if (!animation.IsTrackedByCompositor || visual is not Visual) return;</c> and then
        /// <c>if (visual.CompositionTarget != null)</c> - and <c>CompositionTarget</c> walks up
        /// <c>Parent</c>. The old code started each dot's animation <b>before</b> adding it to the
        /// root and before the root was handed to
        /// <c>ElementCompositionPreview.SetElementChildVisual</c>, so every one of them was
        /// unparented at that instant, never entered <c>_runningAnimations</c>, and never got a
        /// frame: <c>ScalarKeyFrameAnimation.Start</c> wrote the value of keyframe 0 and that was
        /// the whole animation. Hence the order below - build, attach, <i>then</i> start - and
        /// hence <see cref="TryStartAnimations"/> waiting for <c>Loaded</c>, because a cell in the
        /// chat list gets its action before it is in the tree.</para>
        /// <para><b>And why the None case is not a null.</b>
        /// <c>SetElementChildVisual(element, null)</c> throws in Uno: it does
        /// <c>new ContainerVisual(...).Children.InsertAtTop(visual)</c>, and
        /// <c>VisualCollection.InsertAtTop</c> ends with <c>newChild.Parent = _owner</c> - a
        /// <c>NullReferenceException</c> on the way out of every "X stopped typing" update, before
        /// the old child was removed. An empty container removes it and throws nothing.</para>
        /// <para>One animation instance per visual and per property throughout, because Uno's
        /// running-animation table is a <c>Dictionary</c> keyed on the animation object. No
        /// <c>DelayTime</c> either: it is <c>[NotImplemented]</c> on <c>KeyFrameAnimation</c>, so
        /// phase differences are spelled out in keyframes.</para>
        /// </remarks>
        private void UpdateVisual(AnimationType type)
        {
            EnsureHooked();
            StopAnimations();
            _animated.Clear();

            var compositor = BootStrapper.Current.Compositor;
            var root = compositor.CreateContainerVisual();
            root.Size = new Vector2(20, 20);

            if (type != AnimationType.None)
            {
                var brush = compositor.CreateColorBrush(Fill?.Color ?? Colors.Black);

                switch (type)
                {
                    case AnimationType.Typing:
                        BuildTyping(compositor, brush, root);
                        break;
                    case AnimationType.Uploading:
                        BuildUploading(compositor, brush, root);
                        break;
                    case AnimationType.Playing:
                        BuildPlaying(compositor, brush, root);
                        break;
                    case AnimationType.VideoRecording:
                        BuildVideoRecording(compositor, brush, root);
                        break;
                    case AnimationType.VoiceRecording:
                        BuildVoiceRecording(compositor, brush, root);
                        break;
                    case AnimationType.Watching:
                        BuildWatching(compositor, brush, root);
                        break;
                }
            }

            ElementCompositionPreview.SetElementChildVisual(this, root);
            TryStartAnimations();
        }

        private void EnsureHooked()
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TryStartAnimations();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // Not tidiness: an animation that stays registered keeps
            // Compositor.RenderRootVisual asking its target for another frame, for ever, and a
            // chat list recycles these cells all day.
            StopAnimations();
        }

        private void TryStartAnimations()
        {
            if (_started || !IsLoaded)
            {
                return;
            }

            _started = true;

            foreach (var (visual, property, animation) in _animated)
            {
                visual.StartAnimation(property, animation);
            }
        }

        private void StopAnimations()
        {
            if (!_started)
            {
                return;
            }

            _started = false;

            foreach (var (visual, property, _) in _animated)
            {
                visual.StopAnimation(property);
            }
        }

        #endregion

        #region The six glyphs

        // Everything below draws inside the 20x20 the three XAML hosts give this element, with the
        // glyph sitting around y = 11 so it lines up with the label next to it, the way the three
        // dots did.

        /// <summary>Three dots that light up in turn. The one action that already read right.</summary>
        private void BuildTyping(Compositor compositor, CompositionBrush brush, ContainerVisual root)
        {
            for (var i = 0; i < 3; i++)
            {
                var dot = Layer(compositor, root, Rounded(compositor, brush, 2 + i * 6, 11, 4, 4, 2));

                var animation = compositor.CreateScalarKeyFrameAnimation();
                animation.Duration = TimeSpan.FromMilliseconds(1050);
                animation.IterationBehavior = AnimationIterationBehavior.Forever;

                for (var frame = 0; frame <= 3; frame++)
                {
                    // Which dot is lit at this third of the cycle.
                    animation.InsertKeyFrame(frame / 3f, (frame % 3) == i ? 1f : 0.3f);
                }

                Animate(dot, "Opacity", animation);
            }
        }

        /// <summary>A packet climbing out of a tray: sending a photo, a file, a video.</summary>
        private void BuildUploading(Compositor compositor, CompositionBrush brush, ContainerVisual root)
        {
            // The tray stays put; only the packet moves, so the eye reads the direction.
            Layer(compositor, root, Rounded(compositor, brush, 4, 16, 12, 2, 1));

            var packet = Layer(compositor, root, Rounded(compositor, brush, 7, 4, 6, 6, 2));

            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.Duration = TimeSpan.FromMilliseconds(1100);
            offset.IterationBehavior = AnimationIterationBehavior.Forever;
            offset.InsertKeyFrame(0, new Vector3(0, 7, 0));
            offset.InsertKeyFrame(1, new Vector3(0, 0, 0));

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Duration = TimeSpan.FromMilliseconds(1100);
            opacity.IterationBehavior = AnimationIterationBehavior.Forever;
            opacity.InsertKeyFrame(0, 0);
            opacity.InsertKeyFrame(0.15f, 1);
            opacity.InsertKeyFrame(0.7f, 1);
            opacity.InsertKeyFrame(1, 0);

            Animate(packet, "Offset", offset);
            Animate(packet, "Opacity", opacity);
        }

        /// <summary>A gamepad with two buttons blinking in turn.</summary>
        private void BuildPlaying(Compositor compositor, CompositionBrush brush, ContainerVisual root)
        {
            // Hollow, so the two buttons inside it are visible at all: a filled body and a filled
            // dot are the same colour and would be one blob.
            Layer(compositor, root, Outline(Rounded(compositor, null, 3, 7, 14, 8, 4), brush, 1.5f));

            var left = Layer(compositor, root, Rounded(compositor, brush, 6, 9.5f, 3, 3, 1.5f));
            var right = Layer(compositor, root, Rounded(compositor, brush, 11, 9.5f, 3, 3, 1.5f));

            Animate(left, "Opacity", Blink(compositor, 800, 1f, 0.2f));
            Animate(right, "Opacity", Blink(compositor, 800, 0.2f, 1f));
        }

        /// <summary>A camcorder - body, prism, and a recording dot that pulses inside it.</summary>
        private void BuildVideoRecording(Compositor compositor, CompositionBrush brush, ContainerVisual root)
        {
            Layer(compositor, root,
                Outline(Rounded(compositor, null, 3.75f, 7.75f, 9.5f, 7.5f, 2), brush, 1.5f),
                Rounded(compositor, brush, 14.5f, 9.25f, 3.5f, 4.5f, 1));

            var record = Layer(compositor, root, Rounded(compositor, brush, 6.75f, 9.75f, 3.5f, 3.5f, 1.75f));

            Animate(record, "Opacity", Blink(compositor, 1200, 1f, 0.15f));
        }

        /// <summary>A microphone with sound coming off both sides. The one that had to stop
        /// looking like typing, because a group full of voice notes is where this is read.</summary>
        private void BuildVoiceRecording(Compositor compositor, CompositionBrush brush, ContainerVisual root)
        {
            Layer(compositor, root,
                Rounded(compositor, brush, 7.75f, 3.5f, 4.5f, 9, 2.25f),   // capsule
                Rounded(compositor, brush, 9.25f, 13.5f, 1.5f, 2.5f, 0.75f), // stem
                Rounded(compositor, brush, 6, 16, 8, 1.8f, 0.9f));           // base

            // Two rings of sound, the outer one a beat behind the inner one.
            var inner = Layer(compositor, root,
                Rounded(compositor, brush, 4.5f, 9.5f, 2, 2, 1),
                Rounded(compositor, brush, 13.5f, 9.5f, 2, 2, 1));

            var outer = Layer(compositor, root,
                Rounded(compositor, brush, 1.5f, 9.5f, 2, 2, 1),
                Rounded(compositor, brush, 16.5f, 9.5f, 2, 2, 1));

            var innerAnimation = compositor.CreateScalarKeyFrameAnimation();
            innerAnimation.Duration = TimeSpan.FromMilliseconds(900);
            innerAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
            innerAnimation.InsertKeyFrame(0, 0.15f);
            innerAnimation.InsertKeyFrame(0.35f, 1);
            innerAnimation.InsertKeyFrame(0.7f, 0.15f);
            innerAnimation.InsertKeyFrame(1, 0.15f);

            var outerAnimation = compositor.CreateScalarKeyFrameAnimation();
            outerAnimation.Duration = TimeSpan.FromMilliseconds(900);
            outerAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
            outerAnimation.InsertKeyFrame(0, 0.15f);
            outerAnimation.InsertKeyFrame(0.35f, 0.15f);
            outerAnimation.InsertKeyFrame(0.7f, 1);
            outerAnimation.InsertKeyFrame(1, 0.15f);

            Animate(inner, "Opacity", innerAnimation);
            Animate(outer, "Opacity", outerAnimation);
        }

        /// <summary>An eye whose pupil looks left and right: choosing a sticker, watching a GIF.</summary>
        private void BuildWatching(Compositor compositor, CompositionBrush brush, ContainerVisual root)
        {
            Layer(compositor, root, Outline(Ellipse(compositor, null, 10, 11, 7, 4.5f), brush, 1.5f));

            var pupil = Layer(compositor, root, Ellipse(compositor, brush, 10, 11, 2.2f, 2.2f));

            var animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(2000);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.InsertKeyFrame(0, new Vector3(-3, 0, 0));
            animation.InsertKeyFrame(0.5f, new Vector3(3, 0, 0));
            animation.InsertKeyFrame(1, new Vector3(-3, 0, 0));

            Animate(pupil, "Offset", animation);
        }

        #endregion

        #region Primitives

        private static CompositionSpriteShape Rounded(Compositor compositor, CompositionBrush fill, float x, float y, float width, float height, float radius)
        {
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Offset = new Vector2(x, y);
            geometry.Size = new Vector2(width, height);
            geometry.CornerRadius = new Vector2(radius);

            // Assigned last: CompositionSpriteShape rebuilds its Skia path from
            // OnPropertyChangedCore("Geometry"), and a geometry mutated afterwards does not push a
            // new one through.
            var shape = compositor.CreateSpriteShape();
            shape.Geometry = geometry;
            shape.FillBrush = fill;

            return shape;
        }

        private static CompositionSpriteShape Ellipse(Compositor compositor, CompositionBrush fill, float centerX, float centerY, float radiusX, float radiusY)
        {
            var geometry = compositor.CreateEllipseGeometry();
            geometry.Center = new Vector2(centerX, centerY);
            geometry.Radius = new Vector2(radiusX, radiusY);

            var shape = compositor.CreateSpriteShape();
            shape.Geometry = geometry;
            shape.FillBrush = fill;

            return shape;
        }

        private static CompositionSpriteShape Outline(CompositionSpriteShape shape, CompositionBrush stroke, float thickness)
        {
            shape.StrokeBrush = stroke;
            shape.StrokeThickness = thickness;

            return shape;
        }

        /// <summary>
        /// One <see cref="ShapeVisual"/> holding shapes that move together. The shapes carry
        /// absolute coordinates inside the 20x20 box and the visual sits at the origin, so an
        /// Offset animation on it moves the whole group and nothing else has to be recomputed.
        /// </summary>
        private static ShapeVisual Layer(Compositor compositor, ContainerVisual root, params CompositionSpriteShape[] shapes)
        {
            var visual = compositor.CreateShapeVisual();

            // ShapeVisual.Paint returns without drawing anything when either side of Size is zero.
            visual.Size = new Vector2(20, 20);

            foreach (var shape in shapes)
            {
                visual.Shapes.Add(shape);
            }

            root.Children.InsertAtTop(visual);
            return visual;
        }

        private static ScalarKeyFrameAnimation Blink(Compositor compositor, double milliseconds, float from, float to)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.InsertKeyFrame(0, from);
            animation.InsertKeyFrame(0.5f, to);
            animation.InsertKeyFrame(1, from);

            return animation;
        }

        private void Animate(Visual visual, string property, CompositionAnimation animation)
        {
            _animated.Add((visual, property, animation));
        }

        #endregion
#endif

        #region Fill

        public SolidColorBrush Fill
        {
            get => (SolidColorBrush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register("Fill", typeof(SolidColorBrush), typeof(ChatActionIndicator), new PropertyMetadata(null));

        #endregion

        private AnimationType GetAnimationType(ChatAction action)
        {
            switch (action)
            {
                case ChatActionTyping:
                    return AnimationType.Typing;
                case ChatActionUploadingDocument:
                case ChatActionUploadingPhoto:
                case ChatActionUploadingVideo:
                case ChatActionUploadingVideoNote:
                case ChatActionUploadingVoiceNote:
                    return AnimationType.Uploading;
                case ChatActionStartPlayingGame:
                    return AnimationType.Playing;
                case ChatActionRecordingVideo:
                case ChatActionRecordingVideoNote:
                    return AnimationType.VideoRecording;
                case ChatActionRecordingVoiceNote:
                    return AnimationType.VoiceRecording;
                case ChatActionChoosingSticker:
                case ChatActionWatchingAnimations:
                    return AnimationType.Watching;
                default:
                    return AnimationType.None;
            }
        }

        private IAnimatedVisual GetVisual(AnimationType action, Compositor compositor, Color color, out CompositionPropertySet properties)
        {
            try
            {
                var source = GetVisual(action, compositor, color);
                if (source == null)
                {
                    properties = null;
                    return null;
                }

                // Line that needs to be try-catched
                // TODO: Should we generate try-catch inside TryCreateAnimatedVisual?
                var visual = source.TryCreateAnimatedVisual(compositor, out _);
                if (visual == null)
                {
                    properties = null;
                    return null;
                }

                var linearEasing = compositor.CreateLinearEasingFunction();
                var animation = compositor.CreateScalarKeyFrameAnimation();
                animation.Duration = visual.Duration;
                animation.InsertKeyFrame(1, 1, linearEasing);
                animation.IterationBehavior = AnimationIterationBehavior.Forever;

                properties = compositor.CreatePropertySet();
                properties.InsertScalar("Progress", 0);

#if LINUX
                // Uno's expression lexer has no '_' in its alphabet (PORTING.md §6): a property set
                // named `_` throws ArgumentException ("Unexpected character '_'") the moment the
                // animation starts. One letter of difference with WinUI.
                var progressAnimation = compositor.CreateExpressionAnimation("props.Progress");
                progressAnimation.SetReferenceParameter("props", properties);
#else
                var progressAnimation = compositor.CreateExpressionAnimation("_.Progress");
                progressAnimation.SetReferenceParameter("_", properties);
#endif
                visual.RootVisual.Properties.InsertScalar("Progress", 0.0F);
                visual.RootVisual.Properties.StartAnimation("Progress", progressAnimation);

                properties.StartAnimation("Progress", animation);

                return visual;
            }
            catch
            {
                properties = null;
                return null;
            }
        }

        private IAnimatedVisualSource2 GetVisual(AnimationType action, Compositor compositor, Color color)
        {
#if LINUX
            // LottieGen icons (Assets/Icons) are not part of the Linux build yet.
            return null;
#else
            switch (action)
            {
                case AnimationType.Typing:
                    return new ActionTyping(color);
                case AnimationType.Uploading:
                    return new ActionFile(color);
                case AnimationType.Playing:
                    return new ActionGame(color);
                case AnimationType.VideoRecording:
                    return new ActionVideo(color);
                case AnimationType.VoiceRecording:
                    return new ActionVoice(color);
                case AnimationType.Watching:
                    return new ActionSticker(color);
            }

            return null;
#endif
        }
    }
}
