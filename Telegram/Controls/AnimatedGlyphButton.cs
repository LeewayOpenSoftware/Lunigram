//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Telegram.Common;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Telegram.Controls
{
    public partial class AnimatedGlyphButton : Button
    {
        private TextBlock ContentPresenter1;
        private TextBlock ContentPresenter2;

        private Visual _visual1;
        private Visual _visual2;

        private TextBlock _label;
        private Visual _visual;

        public AnimatedGlyphButton()
        {
            DefaultStyleKey = typeof(AnimatedGlyphButton);
        }

        protected virtual bool IsRuntimeCompatible()
        {
            return false;
        }

        protected override void OnApplyTemplate()
        {
            if (IsRuntimeCompatible())
            {
                return;
            }

            ContentPresenter1 = GetTemplateChild(nameof(ContentPresenter1)) as TextBlock;
            ContentPresenter2 = GetTemplateChild(nameof(ContentPresenter2)) as TextBlock;

            _label = ContentPresenter1;

            _visual1 = _visual = ElementComposition.GetElementVisual(ContentPresenter1);
            _visual2 = ElementComposition.GetElementVisual(ContentPresenter2);

            ContentPresenter2.Text = string.Empty;

            _visual2.Opacity = 0;
            _visual2.Scale = new Vector3();
            _visual2.CenterPoint = new Vector3(10);

            ContentPresenter1.Text = Glyph ?? string.Empty;

            _visual1.Opacity = 1;
            _visual1.Scale = new Vector3(1);
            _visual1.CenterPoint = new Vector3(10);

            base.OnApplyTemplate();
        }

        #region Glyph
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register("Glyph", typeof(string), typeof(AnimatedGlyphButton), new PropertyMetadata(null, OnGlyphChanged));

        private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((AnimatedGlyphButton)d).OnGlyphChanged((string)e.NewValue, (string)e.OldValue);
        }
        #endregion

        private void OnGlyphChanged(string newValue, string oldValue)
        {
            if (string.IsNullOrEmpty(oldValue) || string.IsNullOrEmpty(newValue))
            {
                _label?.Text = newValue;

                return;
            }

            if (string.Equals(newValue, oldValue, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_visual == null || _label == null)
            {
                return;
            }

            var visualShow = _visual == _visual1 ? _visual2 : _visual1;
            var visualHide = _visual == _visual1 ? _visual1 : _visual2;

            var labelShow = _visual == _visual1 ? ContentPresenter2 : ContentPresenter1;
            var labelHide = _visual == _visual1 ? ContentPresenter1 : ContentPresenter2;

            var hide1 = _visual.Compositor.CreateVector3KeyFrameAnimation();
            hide1.InsertKeyFrame(0, new Vector3(1));
            hide1.InsertKeyFrame(1, new Vector3(0));

            var hide2 = _visual.Compositor.CreateScalarKeyFrameAnimation();
            hide2.InsertKeyFrame(0, 1);
            hide2.InsertKeyFrame(1, 0);

            visualHide.StartAnimation("Scale", hide1);
            visualHide.StartAnimation("Opacity", hide2);

            labelShow.Text = newValue;

            var show1 = _visual.Compositor.CreateVector3KeyFrameAnimation();
            show1.InsertKeyFrame(1, new Vector3(1));
            show1.InsertKeyFrame(0, new Vector3(0));

            var show2 = _visual.Compositor.CreateScalarKeyFrameAnimation();
            show2.InsertKeyFrame(1, 1);
            show2.InsertKeyFrame(0, 0);

            visualShow.StartAnimation("Scale", show1);
            visualShow.StartAnimation("Opacity", show2);

#if LINUX
            // Write the end state once the animations have had their time. On Uno an animation
            // whose visual has no CompositionTarget yet is never registered with the compositor
            // (Compositor.RegisterAnimation returns early on a null target), so the only value that
            // is ever written is the one CompositionObject.StartAnimation applies on the spot -
            // the keyframe at progress 0, which for show1/show2 is Scale 0 and Opacity 0. The
            // incoming glyph would then be laid out and invisible for good. Measured in the same
            // shape on ChatView.CheckButtonsVisibility; see PORTING.md 6.
            CompositionScopedBatchEx.QueueCompleted(Constants.FastAnimation, () =>
            {
                visualHide.StopAnimation("Scale");
                visualHide.StopAnimation("Opacity");
                visualShow.StopAnimation("Scale");
                visualShow.StopAnimation("Opacity");

                visualHide.Scale = Vector3.Zero;
                visualHide.Opacity = 0;
                visualShow.Scale = Vector3.One;
                visualShow.Opacity = 1;
            });
#endif

            _visual = visualShow;
            _label = labelShow;
        }
    }
}
