//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The row of bars along the top of the story viewer: one per story of the current author, the ones
// already seen filled, the current one filling as it plays.
//
// ---------------------------------------------------------------------------------------------
// Why none of this is a composition animation, which is what upstream uses
// ---------------------------------------------------------------------------------------------
//
// Upstream (Controls/Stories/StoryContent.xaml.cs:2115, `StoryProgress : Grid`) builds the moving
// bar out of four composition objects and animates three of them. Every one of those three is a
// measured dead end on Uno 6.6.184, and they are three DIFFERENT entries of PORTING.md 6:
//
//  1. `expression.SetReferenceParameter("_", _progressPropertySet)` -- Uno's expression lexer has
//     no '_' in its alphabet. ExpressionAnimationLexer.EatToken answers
//     `ArgumentException: Unexpected character '_'` the moment the animation is started. On
//     Windows that identifier is legal, which is why the shared code uses it by habit; PORTING.md
//     lists StoryContent by name among the hundred-odd call sites still carrying it.
//
//  2. `ellipse.StartAnimation("Size", expression)` on a CompositionRoundedRectangleGeometry --
//     `Compositor.RegisterAnimation` begins with `if (!animation.IsTrackedByCompositor || visual
//     is not Visual v) return;`, so an animation whose target is a geometry is never registered
//     and never evaluated. What stays written is the single value StartAnimation itself writes,
//     the keyframe at progress 0. The same call additionally costs one "Unable to set property"
//     per composed frame, because neither CompositionRoundedRectangleGeometry nor its base
//     implements SetAnimatableProperty for Size.
//
//  3. `_progressPropertySet.StartAnimation("Progress", compositorAnimation)` -- same rule, same
//     outcome: a CompositionPropertySet is not a Visual. Measured directly in
//     unigram-linux/spikes/TransportSpike: TryGetScalar("Progress") still reads 0,000 seven
//     hundred milliseconds into a four second animation.
//
// So on Uno the upstream bar would throw on the first story, and had it not thrown it would have
// sat at zero forever. `TryGetAnimationController` does return a controller (its IL looks the
// animation up in CompositionObject's own dictionary, which StartAnimation does fill), so
// Suspend()/Resume() would have looked wired up while pausing an animation that never ran --
// exactly the "a button that draws and does nothing" this port keeps rejecting.
//
// What is used instead is the shape the rest of this port settled on for anything that has to
// move: a value written by hand once per frame from Telegram.Common.CompositionRenderingClock (the
// working frame clock -- CompositionTarget.Rendering is not one on Uno), onto a RenderTransform,
// which is plain XAML and needs no compositor registration at all. The same combination drives the
// gallery pass settle in Xaml/CarouselViewer.Linux.cs, which is measured and working.
//
// A RenderTransform and not the Width: assigning Width once per frame would invalidate measure on
// the popup root sixty times a second. ScaleTransform.ScaleX does not invalidate layout.
//
// ---------------------------------------------------------------------------------------------
// Where the time comes from
// ---------------------------------------------------------------------------------------------
//
// A stopwatch, resynchronised against the player. For a photo that is all there is (five seconds,
// the same constant upstream's StoryContentPhotoTimer uses). For a video the stopwatch is the
// interpolator and LinuxVideoPlayer.Position is the truth: the player reports position about four
// times a second (AsyncMediaPlayer.PositionInterval = 250 ms), which on a 200 px bar would be four
// visible steps a second, so each report re-bases the stopwatch instead of being drawn directly.
// That is the same trick PlaybackSlider uses on Windows, minus the composition animation it uses
// to do the interpolating -- which is the property-set animation of point 3 above, and therefore
// also does nothing here.
//
// Pausing is then honest by construction: the stopwatch stops, and so does the bar. There is no
// animation left running underneath to be out of step with it.

using System;
using System.Diagnostics;
using Telegram.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;


namespace Telegram.Controls.Stories
{
    /// <summary>
    /// Same name, same namespace and same base class as the upstream control, so a file that names
    /// it does not care which head it is compiled in.
    /// </summary>
    public partial class StoryProgress : Grid
    {
        /// <summary>
        /// How long a photo story is shown for. Upstream: StoryContentPhotoTimer's interval.
        /// </summary>
        public const double PhotoDurationSeconds = 5;

        private readonly Stopwatch _watch = new();

        // Seconds already elapsed before the stopwatch was last (re)started. Moved by Synchronize
        // when the video player reports a real position, and by Suspend/Resume.
        private double _offset;
        private double _duration;

        private ScaleTransform _fill;

        private bool _running;
        private bool _subscribed;
        private bool _completed;

        // Last value written, so a frame that would not move the bar by a visible amount does not
        // touch the tree at all. The bar is at most a few hundred pixels wide, so a thousandth of
        // its width is well under a pixel.
        private double _painted = -1;

        /// <summary>
        /// Raised once, on the frame the current bar reaches its end. The window turns it into
        /// "next story, and if there is none, next author".
        /// </summary>
        public event EventHandler Completed;

        /// <summary>
        /// Rebuilds the row: <paramref name="count"/> bars, the ones before
        /// <paramref name="index"/> full, the one at <paramref name="index"/> empty and ready to
        /// run for <paramref name="duration"/> seconds. Does NOT start it -- see <see cref="Begin"/>.
        /// A negative <paramref name="index"/> means "no current story" (upstream passes -1 for a
        /// live broadcast, which has no length).
        /// </summary>
        public void Update(int index, int count, double duration)
        {
            Unsubscribe();

            _watch.Reset();
            _offset = 0;
            _duration = duration > 0 ? duration : 0;
            _running = false;
            _completed = false;
            _painted = -1;
            _fill = null;

            Children.Clear();
            ColumnDefinitions.Clear();

            for (int i = 0; i < count; i++)
            {
                ColumnDefinitions.Add(new ColumnDefinition());

                var track = new Border
                {
                    Margin = new Thickness(0, 2, 2, 2),
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    // The bar that has not been reached yet is the dim track; a bar already seen is
                    // painted solid by leaving the fill of the segment at full scale, below.
                    Background = new SolidColorBrush(Microsoft.UI.Colors.White)
                    {
                        Opacity = i < index ? 1 : 0.3
                    },
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (i == index)
                {
                    var scale = new ScaleTransform { ScaleX = 0, ScaleY = 1 };

                    var fill = new Border
                    {
                        CornerRadius = new CornerRadius(1),
                        Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                        // The scale has to grow from the left edge, not from the middle.
                        RenderTransformOrigin = new Point(0, 0.5),
                        RenderTransform = scale
                    };

                    track.Child = fill;
                    _fill = scale;
                }

                Grid.SetColumn(track, i);
                Children.Add(track);
            }
        }

        /// <summary>
        /// Starts the current bar. Called when the media is actually on screen -- the photo decoded,
        /// or the video's first frame out -- and not when the story is selected, so that a slow
        /// download does not eat the beginning of the bar.
        /// </summary>
        public void Begin()
        {
            if (_fill == null || _duration <= 0)
            {
                return;
            }

            _completed = false;
            _running = true;

            _watch.Restart();
            Subscribe();
            Paint();
        }

        /// <summary>
        /// Re-bases the clock on a position reported by the video player, in seconds.
        /// </summary>
        public void Synchronize(double position)
        {
            if (_fill == null || _duration <= 0 || position < 0)
            {
                return;
            }

            _offset = Math.Min(position, _duration);

            if (_running)
            {
                _watch.Restart();
            }
            else
            {
                _watch.Reset();
            }

            Paint();
        }

        /// <summary>
        /// Holds the bar where it is. Every reason to pause funnels through the card's
        /// <see cref="StoryPauseSource"/> mask, so this is only reached on the edge into "paused".
        /// </summary>
        public void Suspend()
        {
            if (!_running)
            {
                return;
            }

            _offset += _watch.Elapsed.TotalSeconds;
            _watch.Reset();
            _running = false;

            Unsubscribe();
            Paint();
        }

        public void Resume()
        {
            if (_running || _fill == null || _duration <= 0 || _completed)
            {
                return;
            }

            _running = true;
            _watch.Restart();

            Subscribe();
            Paint();
        }

        /// <summary>
        /// Stops for good, without firing <see cref="Completed"/>. Used when the card is put away.
        /// </summary>
        public void Stop()
        {
            _running = false;
            _watch.Reset();
            _offset = 0;

            Unsubscribe();
        }

        /// <summary>
        /// True while the bar is moving. The card reads it to tell "paused" from "playing" without
        /// keeping a second copy of the state.
        /// </summary>
        public bool IsRunning => _running;

        #region Clock

        private void Subscribe()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                CompositionRenderingClock.Rendering += OnRendering;
            }
        }

        private void Unsubscribe()
        {
            if (_subscribed)
            {
                _subscribed = false;
                CompositionRenderingClock.Rendering -= OnRendering;
            }
        }

        private void OnRendering(object sender, object e)
        {
            Paint();
        }

        private double Elapsed => _offset + (_running ? _watch.Elapsed.TotalSeconds : 0);

        private void Paint()
        {
            var fill = _fill;
            if (fill == null || _duration <= 0)
            {
                return;
            }

            var progress = Math.Clamp(Elapsed / _duration, 0, 1);

            if (Math.Abs(progress - _painted) >= 0.001 || progress >= 1 || progress <= 0)
            {
                _painted = progress;
                fill.ScaleX = progress;
            }

            if (progress >= 1 && !_completed)
            {
                _completed = true;
                _running = false;
                _watch.Reset();

                // Unsubscribe BEFORE raising: the handler is going to move to the next story, which
                // calls Update() and Begin() again, and a subscription left behind here would then
                // be removed by that Update() and leave the new bar without a clock.
                Unsubscribe();

                Completed?.Invoke(this, EventArgs.Empty);
            }
        }

        #endregion
    }
}
