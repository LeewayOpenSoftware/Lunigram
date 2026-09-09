//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Telegram.Common;
using Telegram.Native;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls
{
    public sealed partial class ProfileGiftsCover : UserControl
    {
        private readonly uint _seed;

        private List<OrbitGenerator.Position> _positions;
        private long _gifts;
        private float _frameWidth;
        private float _frameHeight;

        public ProfileViewModel ViewModel => DataContext as ProfileViewModel;

        public ProfileGiftsCover()
        {
            InitializeComponent();

            _seed = (uint)DateTime.Now.ToUnixTimeSeconds();
        }

        public UIElement TitleRoot { get; set; }

        private float _transitionFraction;
        public float TransitionFraction
        {
            get => _transitionFraction;
            set => Update(_transitionFraction = value, TitleRoot);
        }

        private void Update(float avatarTransitionFraction, UIElement titleRoot)
        {
            var newSize = new Vector2(ActualSize.X + 36, ActualSize.Y);
            var seed = _seed;

            var gifts = GetPinnedGifts(out long hash);

            var avatarSize = new Vector2(120, 120);
            var centerFrame = new RectangleF((-72 + newSize.X - avatarSize.X) / 2f, (-36 + 204 - avatarSize.Y) / 2f, avatarSize.X, avatarSize.Y);

            if (_gifts != hash || _positions == null || _frameWidth != newSize.X || _frameHeight != newSize.Y)
            {
                GeneratePositions(avatarTransitionFraction, titleRoot);
            }

            var i = 0;

            foreach (var child in RootGrid.Children)
            {
                if (_positions == null || _positions.Count <= i)
                {
                    child.Opacity = 0;
                    continue;
                }

                var iconPosition = _positions[i++];
                var itemDistanceFraction = Math.Max(0.0f, Math.Min(0.5f, (iconPosition.Distance - avatarSize.X / 2.0f) / 144.0f));
                var itemScaleFraction = OrbitGenerator.PatternScaleValueAt(fraction: Math.Min(1.0f, avatarTransitionFraction * 1.33f), t: itemDistanceFraction, reverse: false);

                var toAngle = MathF.PI * 0.18f;
                var centerPosition = new OrbitGenerator.Position(distance: 0.0f, angle: iconPosition.Angle + toAngle, scale: iconPosition.Scale);
                var effectivePosition = OrbitGenerator.InterpolatePosition(from: iconPosition, to: centerPosition, t: itemScaleFraction);
                var effectiveAngle = toAngle * itemScaleFraction;

                var absolutePosition = effectivePosition.GetAbsolutePosition(centerFrame.Center);

                var visual = ElementComposition.GetElementVisual(child);
                visual.Offset = new Vector3(absolutePosition, 0);
                visual.Scale = new Vector3(iconPosition.Scale * (1.0f - itemScaleFraction));
                visual.RotationAngle = effectiveAngle;
            }
        }

        private void GeneratePositions(float avatarTransitionFraction, UIElement titleRoot)
        {
            var newSize = new Vector2(ActualSize.X + 36, ActualSize.Y);
            var seed = _seed;

            var gifts = GetPinnedGifts(out long hash);

            var avatarSize = new Vector2(120, 120);
            var centerFrame = new RectangleF((-72 + newSize.X - avatarSize.X) / 2f, (-36 + 204 - avatarSize.Y) / 2f, avatarSize.X, avatarSize.Y);

            RectangleF[] excludeRects;
            if (titleRoot != null)
            {
                var titleTransform = titleRoot.TransformToVector2(this);

                excludeRects = new RectangleF[]
                {
                    new(titleTransform.X - 4, titleTransform.Y, titleRoot.ActualSize.X + 8, titleRoot.ActualSize.Y),
                };
            }
            else
            {
                excludeRects = Array.Empty<RectangleF>();
            }

            var positionGenerator = new OrbitGenerator(
                containerSize: newSize,
                centerFrame: centerFrame,
                exclusionZones: excludeRects,
                minimumDistance: 42.0f,
                edgePadding: 5.0f,
                seed: seed
            );

            _positions = positionGenerator.GeneratePositions(count: 12, itemSize: new Vector2(28));
            _gifts = hash;
            _frameWidth = newSize.X;
            _frameHeight = newSize.Y;

            RootGrid.Children.Clear();

            var iconPositions = _positions;
            if (iconPositions == null)
            {
                return;
            }

            for (int i = 0; i < Math.Max(iconPositions.Count, gifts.Count); i++)
            {
                if (i >= gifts.Count || i >= iconPositions.Count || gifts[i].Gift is not SentGiftUpgraded upgraded)
                {
                    continue;
                }

                OrbitGenerator.Position iconPosition = iconPositions[i];
                var itemDistanceFraction = Math.Max(0.0f, Math.Min(0.5f, (iconPosition.Distance - avatarSize.X / 2.0f) / 144.0f));
                var itemScaleFraction = OrbitGenerator.PatternScaleValueAt(fraction: Math.Min(1.0f, avatarTransitionFraction * 1.33f), t: itemDistanceFraction, reverse: false);

                var toAngle = MathF.PI * 0.18f;
                var centerPosition = new OrbitGenerator.Position(distance: 0.0f, angle: iconPosition.Angle + toAngle, scale: iconPosition.Scale);
                var effectivePosition = OrbitGenerator.InterpolatePosition(from: iconPosition, to: centerPosition, t: itemScaleFraction);
                var effectiveAngle = toAngle * itemScaleFraction;

                var absolutePosition = effectivePosition.GetAbsolutePosition(centerFrame.Center);

                var centerColor = upgraded.Gift.Backdrop.Colors.CenterColor.ToColor().WithBrightness(0.3f);

                var gradient = new RadialGradientBrush();
                gradient.Center = new Point(0.5, 0.5);
                gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(166, centerColor.R, centerColor.G, centerColor.B) });
                gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(166, centerColor.R, centerColor.G, centerColor.B), Offset = 0.3 });
                gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, centerColor.R, centerColor.G, centerColor.B), Offset = 1 });

                var particles = new AnimatedImage
                {
                    Source = new ParticlesImageSource(Colors.White, ParticlesType.Status),
                    IsViewportAware = false,
                    Stretch = Stretch.UniformToFill,
                    DecodeFrameType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
                    FrameSize = new Size(36, 36),
                    Width = 36,
                    Height = 36,
                    Margin = new Thickness(-4)
                };

                var icon = new CustomEmojiIcon
                {
                    Source = DelayedFileSource.FromSticker(ViewModel.ClientService, upgraded.Gift.Model.Sticker),
                    Width = 28,
                    Height = 28,
                    FrameSize = new Size(28, 28),
                    IsViewportAware = false
                };

                icon.Ready += OnReady;

                var root = new Grid
                {
                    Opacity = 0,
                    Width = 28,
                    Height = 28,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };

                root.Children.Add(new Border
                {
                    Background = gradient,
                    Width = 32,
                    Height = 32,
                    Margin = new Thickness(-2)
                });

                root.Children.Add(particles);
                root.Children.Add(icon);

                RootGrid.Children.Add(root);

                var visual = ElementComposition.GetElementVisual(root);
                visual.Offset = new Vector3(absolutePosition, 0);
                visual.Scale = new Vector3(iconPosition.Scale * (1.0f - itemScaleFraction));
                visual.RotationAngle = effectiveAngle;
            }
        }

        private void OnReady(object sender, EventArgs e)
        {
            var icon = sender as CustomEmojiIcon;

            var root = icon.Parent as Grid;
            if (root == null)
            {
                return;
            }

            var visual = ElementComposition.GetElementVisual(root);

            var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0, Vector3.Zero);
            scale.InsertKeyFrame(1, visual.Scale);

            root.Opacity = 1;

            visual.CenterPoint = new Vector3(14);
            visual.StartAnimation("Scale", scale);
        }

        private IList<ReceivedGift> GetPinnedGifts(out long hash)
        {
            hash = 0;

            var itemsView = ViewModel?.GiftsTab?.Items;
            if (itemsView == null)
            {
                return Array.Empty<ReceivedGift>();
            }

            var items = new List<ReceivedGift>();

            foreach (var gift in itemsView)
            {
                if (gift.IsPinned && gift.Gift is SentGiftUpgraded upgraded)
                {
                    items.Add(gift);
                    hash = ((hash * 20261) + 0x80000000L + upgraded.Gift.Id) % 0x80000000L;
                }
            }

            return items;
        }
    }
}
