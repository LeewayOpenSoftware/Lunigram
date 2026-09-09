//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Telegram.Navigation;
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls
{
    public partial class ButtonEx : Button
    {
        public ButtonEx()
        {
            DefaultStyleKey = typeof(ButtonEx);
        }

        #region Skeleton

        public void ShowSkeleton()
        {
            if (ActualSize.X == 0 || ActualSize.Y == 0)
            {
                return;
            }

            var compositor = BootStrapper.Current.Compositor;
            var rectangle = compositor.CreateRoundedRectangleGeometry();
            rectangle.Size = new Vector2(ActualSize.X - 2, ActualSize.Y - 2);
            rectangle.Offset = new Vector2(1, 1);
            rectangle.CornerRadius = new Vector2(4);

            var strokeColor = Background is SolidColorBrush brush ? brush.Color : Colors.White;

            var stroke = compositor.CreateLinearGradientBrush();
            stroke.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(0x00, strokeColor.R, strokeColor.G, strokeColor.B)));
            stroke.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(0xaa, strokeColor.R, strokeColor.G, strokeColor.B)));
            stroke.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(0x00, strokeColor.R, strokeColor.G, strokeColor.B)));

            var fill = compositor.CreateLinearGradientBrush();
            fill.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(0x00, 0xff, 0xff, 0xff)));
            fill.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(0xaa, 0xff, 0xff, 0xff)));
            fill.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(0x00, 0xff, 0xff, 0xff)));

            var shape = compositor.CreateSpriteShape();
            shape.Geometry = rectangle;
            shape.FillBrush = fill;
            shape.StrokeBrush = stroke;
            shape.StrokeThickness = 1;

            var visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(ActualSize.X, ActualSize.Y);
            visual.Shapes.Add(shape);

            // One instance per target. Uno's Compositor keeps its running animations in a
            // dictionary KEYED BY THE ANIMATION INSTANCE (RegisterAnimation is
            // _animations.Add(animation, visual)), so starting the same object on a second target
            // while the first is still running throws ArgumentException -- and an exception on
            // this path takes the dispatcher with it, which is how RootPage froze the app with 32
            // threads at 0%. It is not an ExpressionAnimation problem: keyframes are keyed the
            // same way. WinUI shares instances happily, which is why the code looked fine.
            //
            // The sweep is endless, so both starts are concurrent by construction.
            ScalarKeyFrameAnimation CreateEndless()
            {
                var animation = compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, -ActualSize.X);
                animation.InsertKeyFrame(1, +ActualSize.X);
                animation.IterationBehavior = AnimationIterationBehavior.Forever;
                animation.Duration = TimeSpan.FromMilliseconds(2000);

                return animation;
            }

            stroke.StartAnimation("Offset.X", CreateEndless());
            fill.StartAnimation("Offset.X", CreateEndless());

            ElementCompositionPreview.SetElementChildVisual(this, visual);
        }

        public void HideSkeleton()
        {
            ElementCompositionPreview.SetElementChildVisual(this, BootStrapper.Current.Compositor.CreateSpriteVisual());
        }

        #endregion
    }
}
