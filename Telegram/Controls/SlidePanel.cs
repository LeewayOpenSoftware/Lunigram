//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Numerics;
using Telegram.Common;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Telegram.Controls
{
    public partial class SlidePanel : Panel
    {
        public class SlideState
        {
            private readonly UIElement _element;
            private readonly float _expectedHeight;

            private bool _collapsed;
            private int _pending;

            public SlideState(UIElement element, bool visible, float expectedHeight)
            {
                _element = element;
                _expectedHeight = expectedHeight;

                _collapsed = !visible;

                element.Visibility = visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            }

            public static implicit operator bool(SlideState d) => d._collapsed;

            public bool IsVisible
            {
                get => !_collapsed;
                set => ShowHide(_element, value);
            }

            public void Show()
            {
                _collapsed = false;
                _element.Visibility = Visibility.Visible;
            }

            public void Collapse()
            {
                _collapsed = true;
                _element.Visibility = Visibility.Collapsed;
            }

            public async void ShowHide(UIElement element, bool show)
            {
                if (_collapsed != show)
                {
                    return;
                }

                _collapsed = !show;
                _pending++;

                //SlidePanel.SetIsVisible(element, show);

                element.Visibility = Visibility.Visible;

                var pending = _pending;
                var height = _expectedHeight > 0 ? _expectedHeight : element.ActualSize.Y;

                if (height == 0 && element is FrameworkElement framework)
                {
                    await framework.UpdateLayoutAsync();
                }

                var visual = ElementCompositionPreview.GetElementVisual(element);
                visual.Clip ??= visual.Compositor.CreateInsetClip();

                var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                batch.Completed += (s, args) =>
                {
                    if (_collapsed && _pending == pending)
                    {
                        //visual.Clip = null;
                        //visual.Properties.InsertVector3("Translation", Vector3.Zero);

                        element.Visibility = Visibility.Collapsed;
                    }
                };

                //_chatView.UpdateMessagesHeaderPadding();

                var clip = visual.Compositor.CreateScalarKeyFrameAnimation();
                clip.InsertKeyFrame(show ? 0 : 1, height);
                clip.InsertKeyFrame(show ? 1 : 0, 0);
                clip.Duration = Constants.FastAnimation;

                var offset = visual.Compositor.CreateScalarKeyFrameAnimation();
                offset.InsertKeyFrame(show ? 0 : 1, -height);
                offset.InsertKeyFrame(show ? 1 : 0, 0);
                offset.Duration = Constants.FastAnimation;

                visual.Clip.StartAnimation("TopInset", clip);
                visual.StartAnimation("Translation.Y", offset);

                batch.End();
            }
        }

        // Children are stacked by composition rather than by ArrangeOverride (which puts every
        // one of them at the origin), so a header sliding in can push the ones below it down
        // without a layout pass per frame. Each visible child's Offset.Y follows the previous
        // visible one through an expression animation.
        //
        // All of it is one-time-per-child setup, and measure runs on every header show/hide:
        // establishing it per pass meant an animation allocation and a StartAnimation per child
        // each time, and handed the element a new transform in the middle of the pass.
        // Kept in index-parallel arrays instead of a dictionary, so the steady state is a
        // reference comparison per child and nothing is retained once a child goes away.
        private UIElement[] _children = Array.Empty<UIElement>();

        // The hand-off visual is created once per element and outlives unload/reload, so it's
        // safe to hold and saves an interop call per child per measure.
        private Visual[] _visuals = Array.Empty<Visual>();

        // Whose Offset.Y each child currently follows. null means no animation is running,
        // which is the correct state for the topmost visible child.
        private Visual[] _anchors = Array.Empty<Visual>();

        // The expression every following child runs. It reads the PREVIOUS child's Translation,
        // which is the property SlideState.ShowHide animates, so a header sliding in pushes the
        // ones below it down without a layout pass per frame.
        private const string AnchorExpression =
            "reference.Offset.Y + (reference.Size.Y > 0 ? reference.Translation.Y : 0) + reference.Size.Y";

#if LINUX
        // ONE INSTANCE PER CHILD, and it is not a style preference -- sharing one was the crash.
        //
        // Upstream reuses a single ExpressionAnimation and repoints its "reference" before each
        // StartAnimation, on the documented WinUI behaviour that StartAnimation SNAPSHOTS the
        // expression and its parameters. Uno does not snapshot, and this is decompiled from the
        // shipped Uno.UI.Composition 6.6.184, not assumed:
        //
        //   * CompositionAnimation.ReferenceParameters is a live Dictionary and
        //     SetReferenceParameter just writes into it; ExpressionAnimation.Evaluate() reads it at
        //     evaluation time. Repointing it therefore rewrites the expression of every child that
        //     is ALREADY running this instance -- they all end up following the last child instead
        //     of their own predecessor.
        //   * CompositionObject.StartAnimation does `animation.AnimationFrame += ReEvaluateAnimation`.
        //     One shared instance accumulates one handler per child, so a single frame writes the
        //     same value into every one of them.
        //   * Put together, a child can end up referencing ITSELF: Visual.Offset -> SetProperty ->
        //     PropagateChanged -> RaiseAnimationFrame -> ReEvaluateAnimation -> Offset again,
        //     unbounded. That is the `Stack overflow.` inside ExpressionAnimation.Evaluate /
        //     Visual.set_Offset seen on this floor.
        //   * And CompositionAnimation.Stop() disposes the parsed expression, so the
        //     `visual.StopAnimation("Offset.Y")` below -- correct for the topmost child -- would
        //     leave every other child evaluating a null expression.
        //
        // One instance per child index costs a handful of objects on a panel with nine children and
        // makes each of the four bullets structurally impossible.
        private ExpressionAnimation[] _expressions = Array.Empty<ExpressionAnimation>();
#else
        private ExpressionAnimation _anchorAnimation;
#endif

        protected override Size MeasureOverride(Size availableSize)
        {
            var count = Children.Count;

            if (_children.Length != count)
            {
                Array.Resize(ref _children, count);
                Array.Resize(ref _visuals, count);
                Array.Resize(ref _anchors, count);
#if LINUX
                Array.Resize(ref _expressions, count);
#endif
            }

            var width = 0d;
            var height = 0d;

            Visual previous = null;

            for (int i = 0; i < count; i++)
            {
                var child = Children[i];
                child.Measure(availableSize);

                width = Math.Max(width, child.DesiredSize.Width);
                height += child.DesiredSize.Height;

                if (_children[i] != child)
                {
                    _children[i] = child;
                    _visuals[i] = ElementComposition.GetElementVisual(child);
                    _anchors[i] = null;
#if LINUX
                    _expressions[i] = null;
#endif

                    // Read by the expression below, on the previous child rather than this one,
                    // so every child needs it whether or not it's visible right now.
                    ElementCompositionPreview.SetIsTranslationEnabled(child, true);
                }

#if LINUX
                // AND THIS IS WHAT MADE THE APP ABORT. On Uno, Translation is NOT a property of
                // Visual: Visual.GetAnimatableProperty has no case for it and falls through to the
                // visual's property BAG, and SetIsTranslationEnabled above only flips an internal
                // bool (`element.Visual.IsTranslationEnabled = value`) -- it does not create the
                // entry. So until something WRITES Translation on a visual, reading it goes to
                // SubPropertyHelpers.TryGetFromProperties, which ends in
                // `throw new Exception("Unable to get property 'Translation'.")`.
                //
                // The `reference.Size.Y > 0 ?` guard in the expression does short-circuit (Uno's
                // AnimationTernaryExpressionSyntax evaluates one branch), but its condition is TRUE
                // for any laid-out visible child, so it never protects the read.
                //
                // The window is exact: SlideState.ShowHide makes the header VISIBLE first, then
                // awaits a layout pass, and only then starts the Translation.Y animation that would
                // have created the entry. That layout pass is this method -- the child below reads
                // a Translation that does not exist yet, and it throws.
                //
                // And the throw is fatal rather than logged, because it happens on the
                // re-evaluation path: CompositionObject.StartAnimation wraps its FIRST
                // SetAnimatableProperty in a try/catch, but ReEvaluateAnimation calls
                // SetAnimatableProperty(..., animation.Evaluate()) with no guard at all, so the
                // exception escapes into whatever raised the frame -- unhandled, SIGABRT, exit 134.
                //
                // Seeding zero is the whole fix, and zero is the CORRECT value at that instant: the
                // header has not slid anywhere yet. Inserted only when absent, so a Translation an
                // animation is already driving is never clobbered.
                var properties = _visuals[i].Properties;
                if (properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded)
                {
                    properties.InsertVector3("Translation", Vector3.Zero);
                }
#endif

                if (child.Visibility != Visibility.Visible)
                {
                    continue;
                }

                var visual = _visuals[i];

                if (_anchors[i] != previous)
                {
                    if (previous == null)
                    {
                        visual.StopAnimation("Offset.Y");
                        visual.Offset = new Vector3(visual.Offset.X, 0, visual.Offset.Z);
                    }
                    else
                    {
#if LINUX
                        var animation = _expressions[i] ??= previous.Compositor.CreateExpressionAnimation(AnchorExpression);
                        animation.SetReferenceParameter("reference", previous);

                        visual.StartAnimation("Offset.Y", animation);
#else
                        // One instance reused: StartAnimation snapshots the expression and its
                        // parameters, so the reference can be repointed for the next child.
                        _anchorAnimation ??= previous.Compositor.CreateExpressionAnimation(AnchorExpression);
                        _anchorAnimation.SetReferenceParameter("reference", previous);

                        visual.StartAnimation("Offset.Y", _anchorAnimation);
#endif
                    }

                    _anchors[i] = previous;
                }

                previous = visual;
            }

            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                child.Arrange(new Rect(0, 0, finalSize.Width, child.DesiredSize.Height));
            }

            return finalSize;
        }

        #region IsVisible

        // TODO: would be great to somehow use attached properties, to make this more "integrated" (as in, plug and play)
        // but currently this panel is only used to control chat header, where each component handles its current state anyway
        // plus, there are a few unusual behaviors (specifically ChatPinnedMessage collapsing on Unload) and adding all the code there
        // is quite an overkill, without considering attached properties overhead.
        // This said, it's also unclear how to conciliate various factors when using attached properties:
        // We need a backing field to store the actual state, plus a real attached property for controlling the visibility.
        // It's not clear whether or not this is a good pattern and how to properly keep them in sync.
        // Additionally, it's somehow confusing how to set the initial property value in regards of UIElement.Visibility.
        // And more, initial value set shouldn't be animated, so supposedly this should be controlled somewhere else.
        //public static bool GetIsVisible(DependencyObject obj)
        //{
        //    var state = (SlideState)obj.GetValue(SlideStateProperty);
        //    if (state == null)
        //    {
        //        obj.SetValue(SlideStateProperty, state = new SlideState(obj as UIElement, true));
        //    }

        //    return state.IsVisible;
        //}

        //public static void SetIsVisible(DependencyObject obj, bool value)
        //{
        //    var state = (SlideState)obj.GetValue(SlideStateProperty);
        //    if (state == null)
        //    {
        //        obj.SetValue(SlideStateProperty, state = new SlideState(obj as UIElement, value));
        //    }
        //    else
        //    {
        //        state.IsVisible = value;
        //    }

        //    obj.SetValue(IsVisibleProperty, value);
        //}

        //public static readonly DependencyProperty IsVisibleProperty =
        //    DependencyProperty.RegisterAttached("IsVisible", typeof(bool), typeof(UIElement), new PropertyMetadata(true, OnIsVisibleChanged));

        //public static readonly DependencyProperty SlideStateProperty =
        //    DependencyProperty.RegisterAttached("SlideState", typeof(SlideState), typeof(UIElement), new PropertyMetadata(null));

        //private static void OnIsVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        //{
        //    var child = d as UIElement;
        //    var parent = VisualTreeHelper.GetParent(child);

        //    //if (parent is SlidePanel panel && panel._states.TryGetValue(child, out SlideState state))
        //    //{
        //    //    state.ShowHide(child, (bool)e.NewValue);
        //    //}
        //}

        #endregion
    }
}
