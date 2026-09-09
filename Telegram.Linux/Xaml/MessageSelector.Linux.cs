//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Telegram.Common;
using Telegram.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Telegram.Controls.Messages
{
    /// <summary>
    /// Linux half of <see cref="MessageSelector"/>'s swipe-to-reply/share: the pan on a message
    /// bubble that reveals a reply or forward action, rebuilt on top of the gesture recognizer
    /// because <c>ConfigureInteractionTracker</c> (the Windows half, in <c>MessageSelector.xaml.cs</c>)
    /// uses exactly the composition surface PORTING.md Section 6 already measured as broken on Uno:
    /// <c>InteractionTracker.ConfigurePositionXInertiaModifiers</c> and <c>NaturalRestingPosition</c>
    /// both throw <see cref="NotImplementedException"/>, <c>VisualInteractionSource.DeltaPosition</c>/
    /// <c>PositionVelocity</c> throw, and <c>IsPositionXRailsEnabled</c> is a no-op setter. That
    /// section already names this exact file's Windows counterpart as pending the same rewrite it
    /// gave <see cref="CarouselViewer"/> (<c>Telegram.Linux/Xaml/CarouselViewer.Linux.cs</c>): replace
    /// the tracker with <c>GestureRecognizer.Manipulation</c>, which Uno genuinely implements.
    ///
    /// <para>The reveal icon is a plain XAML <see cref="Border"/>+<see cref="Image"/> pair built in
    /// code and driven by a <see cref="CompositeTransform"/>, not the Windows half's
    /// <c>CompositionSpriteVisual</c>/<c>LoadedImageSurface</c> pair -- measured (CS0246 on a first
    /// attempt) that <c>LoadedImageSurface</c> is not referenced at all for this target framework;
    /// the Windows half only ever compiles it under its own <c>#if !LINUX</c>, so nothing had
    /// verified it would resolve here. <see cref="CarouselViewer"/>'s own Linux half already settled
    /// this exact question the same way: it drives its three elements through
    /// <see cref="TranslateTransform"/>, not composition offsets, for precisely this reason.</para>
    ///
    /// <para><b>Scope, and what is deliberately NOT here</b>: only swipe-to-REPLY and swipe-to-SHARE
    /// (forward) are implemented. Swipe-to-GO-BACK (<see cref="SettingsService.SwipeToGoBack"/>) is
    /// left unimplemented on purpose -- it hands the gesture to
    /// <c>MasterDetailView.AttachBackGesture(InteractionTracker)</c>, whose own edge-swipe chip
    /// (<c>MasterDetailView.cs</c>, <c>_backTracker</c>) is built on the SAME broken
    /// <c>InteractionTracker</c> machinery, including a <c>ConfigurePositionXInertiaModifiers</c>
    /// call of its own. Porting that chip is a real, separate task (the chip's own reveal/ripple/
    /// burst animation, not just a threshold check) -- not something to fake here by skipping the
    /// visual and just firing <c>CommitBackGesture()</c>, which is itself entangled with the
    /// chip's fields (<c>_backIndicator</c> et al.) and would half-run into null-conditional no-ops.
    /// The honest shape of this gap: when a message's only enabled direction is back (share off,
    /// reply off), <see cref="MessageSelector.PrepareForItemOverride"/> already computes
    /// <c>_share = false</c> for it, and this file's clamp math (below) makes that direction commit
    /// to 0 either way -- the row simply does not swipe, rather than swiping into a dead gesture.
    ///
    /// <para>Windows' own commit rule is a plain position threshold at the drag's clamp (<c>tracker.
    /// Position.X &gt;= 72</c> / <c>&lt;= -72</c> in <c>OnInertiaStateEntered</c>), not a
    /// velocity-projected flick like <see cref="CarouselViewer"/>'s pass gesture -- so unlike that
    /// file, this one does not need <c>ManipulationCompleted</c>'s velocity at all, only where the
    /// drag ended up.</para>
    /// </summary>
    public sealed partial class MessageSelector
    {
        /// <summary>Same 72 device-independent pixels Windows clamps <c>tracker.Position.X</c> to.</summary>
        private const float CommitPositionLinux = 72;

        private const double SettleDurationMillisecondsLinux = 200;

        private bool _manipulatingLinux;
        private float _offsetLinux;

        private bool _settlingLinux;
        private float _settleFromLinux;
        private ulong _settleStartLinux;

        private Border _indicatorLinux;
        private CompositeTransform _indicatorTransformLinux;

        private void InitializeLinux()
        {
            if (RootGrid == null || !IsTrackerEnabled || IsAlbumChild)
            {
                return;
            }

            if (!(AppSettings.SwipeToReply || AppSettings.SwipeToShare))
            {
                return;
            }

            // TranslateX only: a vertical drag is the list's own scroll and must not be captured
            // here, unlike CarouselViewer, which owns every gesture over the gallery and has no
            // scroller competing for it.
            ManipulationMode = ManipulationModes.TranslateX;

            ManipulationStarted += OnManipulationStartedLinux;
            ManipulationDelta += OnManipulationDeltaLinux;
            ManipulationCompleted += OnManipulationCompletedLinux;
        }

        private void UninitializeLinux()
        {
            StopSettleLinux();

            ManipulationStarted -= OnManipulationStartedLinux;
            ManipulationDelta -= OnManipulationDeltaLinux;
            ManipulationCompleted -= OnManipulationCompletedLinux;

            _manipulatingLinux = false;

            // The container goes back to the pool from here, and the pool hands it to the next
            // message AS IT IS: stopping the settle clock leaves whatever translation the drag had
            // reached still written on the visual, up to the +-72 commit distance, and the
            // indicator still faded in. The next message drawn in this container then sits that
            // far off at rest, with a reply icon showing, having never been touched.
            //
            // Guarded rather than unconditional so a container that never moved does not build an
            // indicator on its way out: EnsureIndicatorLinux runs inside ApplyOffsetLinux.
            if (_offsetLinux != 0)
            {
                ApplyOffsetLinux(0);
            }
        }

        private void OnManipulationStartedLinux(object sender, ManipulationStartedRoutedEventArgs e)
        {
            if (Message == null || (!_reply && !_share))
            {
                return;
            }

            StopSettleLinux();
            _manipulatingLinux = true;

            GestureProbe.Log($"swipe started, reply {_reply}, share {_share}");
        }

        private void OnManipulationDeltaLinux(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            if (!_manipulatingLinux)
            {
                return;
            }

            var x = (float)e.Cumulative.Translation.X;
            var max = x > 0
                ? (_reply ? CommitPositionLinux : 0)
                : (_share ? -CommitPositionLinux : 0);

            x = max >= 0
                ? Math.Clamp(x, 0, max)
                : Math.Clamp(x, max, 0);

            ApplyOffsetLinux(x);
        }

        private void OnManipulationCompletedLinux(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (!_manipulatingLinux)
            {
                return;
            }

            _manipulatingLinux = false;

            if (Message != null)
            {
                if (_offsetLinux >= CommitPositionLinux && _reply)
                {
                    GestureProbe.Log("swipe committed: reply");
                    _owner.ViewModel.ReplyToMessage(Message);
                }
                else if (_offsetLinux <= -CommitPositionLinux && _share)
                {
                    GestureProbe.Log("swipe committed: share");
                    _owner.ViewModel.ForwardMessage(Message);
                }
            }

            SettleLinux();
        }

        private void ApplyOffsetLinux(float x)
        {
            _offsetLinux = x;
            _visual.Properties.InsertVector3("Translation", new Vector3(x, 0, 0));

            var abs = Math.Abs(x);
            var percent = abs / CommitPositionLinux;

            EnsureIndicatorLinux();

            if (_indicatorLinux != null)
            {
                var width = (float)ActualWidth;
                var height = (float)ActualHeight;

                // Same geometry as the Windows half's OnValuesChanged: the icon slides in from the
                // edge the finger is dragging away from, growing and fading in with the drag.
                _indicatorTransformLinux.TranslateX = x > 0 ? width - percent * 60 : -30 + percent * 55;
                _indicatorTransformLinux.TranslateY = (height - 30) / 2;
                _indicatorTransformLinux.ScaleX = x > 0 ? 0.8f + percent * 0.2f : -(0.8f + percent * 0.2f);
                _indicatorTransformLinux.ScaleY = 0.8f + percent * 0.2f;
                _indicatorLinux.Opacity = percent;
            }
        }

        /// <summary>
        /// Built once, lazily -- a circle behind the same <c>Reply.png</c> the Windows half uses,
        /// just loaded as a plain <see cref="BitmapImage"/> instead of a composition surface (see
        /// the type doc comment). Parented directly on <c>RootGrid</c> rather than a composition
        /// child visual, with hit-testing off so it never steals the manipulation from the bubble
        /// underneath it.
        /// </summary>
        private void EnsureIndicatorLinux()
        {
            if (_indicatorLinux != null)
            {
                return;
            }

            var background = Navigation.BootStrapper.Current.Resources.TryGetValue("MessageServiceBackgroundBrush", out var value)
                ? value as Brush
                : null;

            var image = new Image
            {
                Width = 18,
                Height = 18,
                Source = new BitmapImage(new Uri("ms-appx:///Assets/Images/Reply.png"))
            };

            _indicatorTransformLinux = new CompositeTransform
            {
                CenterX = 15,
                CenterY = 15
            };

            _indicatorLinux = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = background,
                Child = image,
                Opacity = 0,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = _indicatorTransformLinux
            };

            RootGrid.Children.Add(_indicatorLinux);
        }

        private void SettleLinux()
        {
            StopSettleLinux();

            _settleFromLinux = _offsetLinux;

            if (_settleFromLinux == 0)
            {
                return;
            }

            _settleStartLinux = Logger.TickCount;
            _settlingLinux = true;

            CompositionRenderingClock.Rendering += OnSettleTickLinux;
        }

        private void OnSettleTickLinux(object sender, object e)
        {
            var elapsed = Logger.TickCount - _settleStartLinux;
            var progress = Math.Clamp(elapsed / SettleDurationMillisecondsLinux, 0, 1);

            // Ease out cubic, the shape a WinUI implicit spring-back would have had.
            var eased = 1 - Math.Pow(1 - progress, 3);

            ApplyOffsetLinux((float)(_settleFromLinux * (1 - eased)));

            if (progress >= 1)
            {
                StopSettleLinux();
            }
        }

        private void StopSettleLinux()
        {
            if (_settlingLinux)
            {
                _settlingLinux = false;
                CompositionRenderingClock.Rendering -= OnSettleTickLinux;
            }
        }
    }
}
