//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Telegram.Common;
using Telegram.Streams;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Telegram.Controls
{
    public partial class ProfilePatternCover : Control
    {
        private readonly static OrbitGenerator.Position[] _positions = new[]
        {
            new OrbitGenerator.Position(100, -1.57079637f, 0.9593789f),
            new OrbitGenerator.Position(100, -0.5235988f, 0.7199175f),
            new OrbitGenerator.Position(100, 0.5235988f, 0.9537933f),
            new OrbitGenerator.Position(100, 1.57079637f, 0.7504041f), // Hidden by title
            new OrbitGenerator.Position(100, 2.61799383f, 0.968893051f),
            new OrbitGenerator.Position(100, 3.66519117f, 0.875341058f),
            //new ProfileGiftsCover.PositionGenerator.Position(140, -1.04719758f, 0.797602057f), // Out of bounds top
            new OrbitGenerator.Position(140, 0f, 0.7811355f),
            new OrbitGenerator.Position(140, 1.04719758f, 0.788561344f), // Hidden by subtitle
            new OrbitGenerator.Position(140, 2.09439516f, 0.9652828f), // Hidden by subtitle
            new OrbitGenerator.Position(140, 3.1415925f, 0.7321501f),
            //new ProfileGiftsCover.PositionGenerator.Position(140, 4.18879f, 0.7033648f), // Out of bounds top
            //new ProfileGiftsCover.PositionGenerator.Position(180, -1.57079637f, 0.720738232f), // Out of bounds top
            new OrbitGenerator.Position(180, -0.5235988f, 0.7289108f),
            new OrbitGenerator.Position(180, 0.5235988f, 0.7759581f),
            //new ProfileGiftsCover.PositionGenerator.Position(180, 1.57079637f, 0.718606f), // Out of bounds bottom
            new OrbitGenerator.Position(180, 2.61799383f, 0.867521644f),
            new OrbitGenerator.Position(180, 3.66519117f, 0.716817141f),
            //new ProfileGiftsCover.PositionGenerator.Position(220, -1.04719758f, 0.870925665f), // Out of bounds top
            new OrbitGenerator.Position(220, 0f, 0.9330126f),
            //new ProfileGiftsCover.PositionGenerator.Position(220, 1.04719758f, 0.8217091f), // Out of bounds bottom
            //new ProfileGiftsCover.PositionGenerator.Position(220, 2.09439516f, 0.7188775f), // Out of bounds bottom
            new OrbitGenerator.Position(220, 3.1415925f, 0.8571975f),
            //new ProfileGiftsCover.PositionGenerator.Position(220, 4.18879f, 0.9217857f), // Out of bounds top
        };

#if LINUX
        // The side of surface.SourceSize below, which on Uno is also the size every sprite has to
        // keep because the surface brush paints 1:1 (see Update).
        private const float SurfaceSide = 37f;
#endif

        public ProfilePatternCover()
        {
            DefaultStyleKey = typeof(ProfilePatternCover);
        }

        protected override void OnApplyTemplate()
        {
            // TODO: Names
            var pattern = GetTemplateChild("Animated") as UIElement;
            var layoutRoot = GetTemplateChild("LayoutRoot") as Border;

#if LINUX
            // An exception that escapes OnApplyTemplate takes the whole measure pass with it,
            // and a control whose default style never got registered has no template children.
            if (pattern == null || layoutRoot == null)
            {
                return;
            }
#endif

            if (pattern is AnimatedImage animated)
            {
                animated.Ready += OnReady;
            }

#if LINUX
            // Uno's CompositionVisualSurface paints its source with Visual.RenderRootVisual, which
            // returns before drawing anything when the source's Opacity is 0 - so the template's
            // hidden pattern would be captured as an empty surface and none of the orbiting symbols
            // would show. The source stays opaque here and is pushed out of this control's clip in
            // ArrangeOverride instead: RenderRootVisual cancels the source's own Visual.Offset, so
            // moving it on screen leaves the capture exactly where it was.
            pattern.Opacity = 1;
#endif

            var visual = ElementComposition.GetElementVisual(pattern);
            var compositor = visual.Compositor;

            // Create a VisualSurface positioned at the same location as this control and feed that
            // through the color effect.
            var surfaceBrush = compositor.CreateSurfaceBrush();
            var surface = compositor.CreateVisualSurface();

            // Select the source visual and the offset/size of this control in that element's space.
            surface.SourceVisual = visual;
            surface.SourceOffset = new Vector2(0, 0);
            surface.SourceSize = new Vector2(37, 37);
            surfaceBrush.HorizontalAlignmentRatio = 0.5f;
            surfaceBrush.VerticalAlignmentRatio = 0.5f;
            surfaceBrush.Surface = surface;
            surfaceBrush.Stretch = CompositionStretch.Fill;
            surfaceBrush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            surfaceBrush.SnapToPixels = true;

            var container = compositor.CreateContainerVisual();
            container.RelativeSizeAdjustment = Vector2.One;

            for (int i = 0; i < _positions.Length; i++)
            {
                var redirect = compositor.CreateSpriteVisual();
                redirect.Brush = surfaceBrush;

                container.Children.InsertAtTop(redirect);
            }

            ElementCompositionPreview.SetElementChildVisual(layoutRoot, container);
        }

        private void OnReady(object sender, EventArgs e)
        {
            var layoutRoot = GetTemplateChild("LayoutRoot") as Border;
#if LINUX
            if (layoutRoot == null)
            {
                return;
            }
#endif
            var container = ElementCompositionPreview.GetElementChildVisual(layoutRoot) as ContainerVisual;
#if LINUX
            if (container == null)
            {
                return;
            }
#endif

            var batch = container.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            // One instance per visual: Uno's Compositor indexes its animation registry by the
            // animation object, so starting the same one twice throws.
            foreach (var redirect in container.Children)
            {
                redirect.StartAnimation("Scale", CreateScale());
            }

            batch.End();

            Vector3KeyFrameAnimation CreateScale()
            {
                var scale = container.Compositor.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(0, Vector3.Zero);
                scale.InsertKeyFrame(1, Vector3.One);
#if LINUX
                // Uno leaves Duration at TimeSpan.Zero and evaluates progress as 1 straight
                // away, so without this the symbols pop in at full size instead of growing.
                scale.Duration = TimeSpan.FromSeconds(1);
#endif
                return scale;
            }
        }

        private RectangleF _center = new(0, 48, 160, 160);
        public RectangleF Center
        {
            get => _center;
            set => SetCenter(value);
        }

        private void SetCenter(RectangleF center)
        {
            _center = center;
            InvalidateArrange();
        }

        private float _avatarTransitionFraction;
        public float TransitionFraction
        {
            get => _avatarTransitionFraction;
            set => Update(_avatarTransitionFraction = value, ActualSize);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Update(_avatarTransitionFraction, finalSize.ToVector2());
#if LINUX
            HideCaptureSource(finalSize);
#endif
            return base.ArrangeOverride(finalSize);
        }

#if LINUX
        // Second half of the opaque-source workaround in OnApplyTemplate. Visual.Offset is free to
        // use here for two reasons: Uno keeps the layout position in a separate ArrangeOffset, and
        // RenderRootVisual subtracts the total offset before capturing - so parking the source above
        // the control moves it on screen only. The clip is what keeps that parked copy off the card,
        // and both depend on the height, hence every arrange.
        private Size _clippedTo;

        private void HideCaptureSource(Size finalSize)
        {
            if (GetTemplateChild("Animated") is not UIElement pattern)
            {
                return;
            }

            var visual = ElementComposition.GetElementVisual(pattern);
            visual.Offset = new Vector3(0, -(float)(finalSize.Height + 128), 0);

            // Assigned only when it changes: writing Clip from inside ArrangeOverride is what would
            // otherwise ask for another arrange pass on every pass.
            if (_clippedTo != finalSize)
            {
                _clippedTo = finalSize;
                Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
                {
                    Rect = new Rect(0, 0, finalSize.Width, finalSize.Height)
                };
            }
        }
#endif

        private void Update(float avatarTransitionFraction, Vector2 finalSize)
        {
            var layoutRoot = GetTemplateChild("LayoutRoot") as Border;
#if LINUX
            // Reached from ArrangeOverride: throwing here would leave the message list
            // deaf to InvalidateMeasure for the rest of the process.
            if (layoutRoot == null)
            {
                return;
            }
#endif
            var container = ElementCompositionPreview.GetElementChildVisual(layoutRoot) as ContainerVisual;
#if LINUX
            if (container == null)
            {
                return;
            }
#endif

            var y = _center.Width * 0.2f * 1.5f;

            var avatarSize = new Vector2(_center.Width, _center.Width);
            var newSize = new Vector2(finalSize.X, y + _center.Width);
            var centerFrame = new RectangleF((newSize - avatarSize) / 2, avatarSize);
            //var avatarSize = new Vector2(140, 140);
            //var newSize = new Vector2(1000 + 36, 320);
            //var centerFrame = new RectangleF((-72 + newSize.X - avatarSize.X) / 2f, (-36 + 204 - avatarSize.Y) / 2f, avatarSize.X, avatarSize.Y);

            //var test = Generate2(0);
            //var builder = new StringBuilder();

            //foreach (var point in test)
            //{
            //    builder.AppendFormat("new ProfileGiftsCover.PositionGenerator.Position({0:R}, {1:R}f, {2:R}f),\r\n", point.Distance, point.Angle, point.Scale);
            //}

            //var yolo = builder.ToString();

            var i = 0;

            foreach (var redirect in container.Children)
            {
                if (_positions.Length <= i)
                {
                    redirect.Opacity = 0;
                    continue;
                }

                var iconPosition = _positions[i++];
                var iconOpacity = iconPosition.Distance / 260f;

                iconPosition = new OrbitGenerator.Position(distance: iconPosition.Distance / 100f * (_center.Width * 0.6f), iconPosition.Angle, iconPosition.Scale);

                // TODO: find a way to calculate 2.3f dynamically
                var itemDistanceFraction = 0.6f - Math.Max(0.0f, Math.Min(0.5f, (iconPosition.Distance - avatarSize.X / 2.3f) / 74));
                var itemScaleFraction = OrbitGenerator.PatternScaleValueAt(fraction: Math.Min(1.0f, avatarTransitionFraction * 1.33f), t: itemDistanceFraction, reverse: false);

                var toAngle = MathF.PI * 0.18f;
                var centerPosition = new OrbitGenerator.Position(distance: 0.0f, angle: iconPosition.Angle + toAngle, scale: iconPosition.Scale);
                var effectivePosition = OrbitGenerator.InterpolatePosition(from: iconPosition, to: centerPosition, t: itemScaleFraction);
                var effectiveAngle = toAngle * itemScaleFraction;

                var absolutePosition = effectivePosition.GetAbsolutePosition(centerFrame.Center);

                var size = _center.Width * 0.2f;

#if LINUX
                // Uno paints a CompositionVisualSurface 1:1 into the sprite's bounds and ignores the
                // brush's Stretch and alignment ratios, so a sprite smaller than the surface CROPS
                // the symbol instead of shrinking it. The sprite keeps the surface's own size and
                // the shrink moves to TransformMatrix, which composes with the Scale the reveal
                // animation drives (Visual.GetTransform multiplies the two about CenterPoint).
                var side = SurfaceSide;
                var factor = MathF.Floor(size * (iconPosition.Scale * (1.0f - itemScaleFraction))) / side;

                redirect.Size = new Vector2(side);
                redirect.CenterPoint = new Vector3(side / 2, side / 2, 0);
                redirect.TransformMatrix = Matrix4x4.CreateScale(factor, factor, 1f, redirect.CenterPoint);
                redirect.Offset = new Vector3(absolutePosition - new Vector2(side / 2), 0);
                redirect.RotationAngle = 0; // effectiveAngle;
                redirect.Opacity = (1.0f - itemScaleFraction) * (1.0f - iconOpacity);
#else
                //redirect.Size = new Vector2(36, 36);
                redirect.Size = new Vector2(MathF.Floor(size * (iconPosition.Scale * (1.0f - itemScaleFraction))));
                redirect.Offset = new Vector3(absolutePosition - new Vector2(size / 2), 0);
                //redirect.Scale = new Vector3(iconPosition.Scale * (1.0f - itemScaleFraction));
                redirect.RotationAngle = 0; // effectiveAngle;
                redirect.CenterPoint = new Vector3(redirect.Size / 2, 0);
                redirect.Opacity = (1.0f - itemScaleFraction) * (1.0f - iconOpacity);
#endif
            }
        }

        #region Source

        public AnimatedImageSource Source
        {
            get { return (AnimatedImageSource)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(AnimatedImageSource), typeof(ProfilePatternCover), new PropertyMetadata(null));

        #endregion
    }
}
