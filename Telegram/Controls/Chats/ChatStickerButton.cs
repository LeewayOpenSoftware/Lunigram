//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;
using Telegram.Assets.Icons;
using Telegram.Common;
using Telegram.Controls.Drawers;
using Telegram.Controls.Media;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.Views;
using Telegram.Views.Popups;
using Microsoft.UI.Input;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Chats
{
    public partial class ChatStickerButton : AnimatedGlyphToggleButton
    {
        private Border Icon;

        // This should be held in memory, or animation will stop
        private CompositionPropertySet _props;

        private IAnimatedVisual _previous;
        private IAnimatedVisualSource2 _source;

        public ChatStickerButton()
        {
            DefaultStyleKey = typeof(ChatStickerButton);
            Source = AppSettings.Stickers.SelectedTab;

            Click += Stickers_Click;

            RegisterPropertyChangedCallback(ForegroundProperty, OnForegroundChanged);

            _animateOnToggle = false;
        }

        #region 

        private DispatcherTimer _stickersTimer;
        private Visual _stickersPanel;
        private Visual _stickersShadow;
        private StickersPanelMode _stickersMode = StickersPanelMode.Collapsed;

        private StickerPanel _controlledPanel;
        public StickerPanel ControlledPanel
        {
            get => _controlledPanel;
            set => SetControlledPanel(value);
        }

        public event EventHandler Redirect;

        public event EventHandler Opening;
        public event EventHandler Closing;

        private void SetControlledPanel(StickerPanel value)
        {
            if (_controlledPanel != null)
            {
                return;
            }

            _controlledPanel = value;

            _stickersPanel = ElementComposition.GetElementVisual(ControlledPanel.Presenter);
#if LINUX
            // ElementCompositionPreview.GetElementChildVisual is NotImplemented on Uno (measured in
            // the deployed Uno.UI.dll's NotImplemented table; SetElementChildVisual is fine, only
            // the getter is missing), so the shadow visual is handed over by the panel instead of
            // looked up. It is normally NULL here, because VisualUtilities.DropShadow returns null
            // on this head by design -- Uno's Compositor.CreateDropShadow throws. Every use of
            // _stickersShadow below is guarded accordingly.
            _stickersShadow = ControlledPanel.ShadowVisual;
#else
            _stickersShadow = ElementCompositionPreview.GetElementChildVisual(ControlledPanel.Shadow);
#endif

            _stickersTimer = new DispatcherTimer();
            _stickersTimer.Interval = TimeSpan.FromMilliseconds(300);
            _stickersTimer.Tick += (s, args) =>
            {
                _stickersTimer.Stop();

                try
                {
                    var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot);

                    foreach (var popup in popups)
                    {
                        if (popup.Child is FlyoutPresenter { Content: EmojiSkinFlyout } or MenuFlyoutPresenter or ZoomableMediaPopup)
                        {
                            return;
                        }
                    }

                    Collapse_Click(null, null);
                    Redirect?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // All the remote procedure calls must be wrapped in a try-catch block
                }
            };

            PointerEntered += Stickers_PointerEntered;
            PointerExited += Stickers_PointerExited;

            ControlledPanel.PointerEntered += Stickers_PointerEntered;
            ControlledPanel.PointerExited += Stickers_PointerExited;

            ControlledPanel.AllowFocusOnInteraction = true;

#if LINUX
            // A pinned drawer holds a handler on the visual root. Leaving the tree while pinned --
            // switching chats with the drawer open does exactly that -- would leave it there.
            Unloaded += (s, args) => Unpin();
#endif
        }

        private void Stickers_PointerExited(object sender, PointerRoutedEventArgs e)
        {
#if LINUX
            // A pinned drawer was opened by a click, so the pointer leaving is not an instruction
            // to close it -- that is the whole difference between summoning a thing and hovering
            // over one. The timer is stopped rather than left running: it is the same timer the
            // hover path uses, and one already ticking would close the drawer 300ms later.
            if (_pinned)
            {
                if (_stickersTimer.IsEnabled)
                {
                    _stickersTimer.Stop();
                }

                return;
            }
#endif

            if (IsPointerOverEnabled(e.Pointer))
            {
                _stickersTimer.Start();
            }
            else if (_stickersTimer.IsEnabled)
            {
                _stickersTimer.Stop();
            }
        }

        private bool IsPointerOverEnabled(Pointer pointer)
        {
            return pointer?.PointerDeviceType == PointerDeviceType.Mouse && AppSettings.Stickers.IsPointerOverEnabled;
        }

        private bool IsPointerOverDisabled(Pointer pointer)
        {
            return pointer != null && (pointer.PointerDeviceType != PointerDeviceType.Mouse || !AppSettings.Stickers.IsPointerOverEnabled);
        }

        private void Stickers_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_stickersTimer.IsEnabled)
            {
                _stickersTimer.Stop();
            }

            if (ControlledPanel.Visibility == Visibility.Visible || IsPointerOverDisabled(e?.Pointer))
            {
                return;
            }

            _stickersMode = StickersPanelMode.Overlay;
            IsChecked = false;
            AppSettings.IsSidebarOpen = false;

            //Focus(FocusState.Programmatic);
            Redirect?.Invoke(this, EventArgs.Empty);
            Opening?.Invoke(this, EventArgs.Empty);

            ControlledPanel.Visibility = Visibility.Visible;
            ControlledPanel.Activate();

            if (!PowerSavingPolicy.AreSmoothTransitionsEnabled)
            {
                _stickersPanel.Opacity = 1;
                _stickersPanel.Clip = _stickersPanel.Compositor.CreateInsetClip(0, 0, 0, 0);

                if (_stickersShadow != null)
                {
                    _stickersShadow.Opacity = 1;
                    _stickersShadow.Clip = _stickersPanel.Compositor.CreateInsetClip(-48, -48, -48, -4);
                }

                return;
            }

            _stickersPanel.Opacity = 0;
            _stickersPanel.Clip = _stickersPanel.Compositor.CreateInsetClip(48, 48, 0, 0);

            if (_stickersShadow != null)
            {
                if (_stickersShadow != null)
                {
                    _stickersShadow.Opacity = 0;
                    _stickersShadow.Clip = _stickersPanel.Compositor.CreateInsetClip(48, 48, -48, -4);
                }
            }

            // One CompositionAnimation instance per StartAnimation call, made by a local factory.
            // PORTING.md 6: Uno's Compositor.RegisterAnimation is `_animations.Add(animation,
            // visual)` on a Dictionary KEYED ON THE ANIMATION, so the second StartAnimation with
            // the same object is an ArgumentException -- and this method started ONE `clip` on
            // LeftInset and TopInset of the same visual, and one `opacity` on the panel and the
            // shadow. On Windows sharing is legal and Unigram does it by habit; here it throws out
            // of a pointer handler, which is how this trap once killed the dispatcher outright.
            // This is the seventh site; the other six are listed in that section.
            ScalarKeyFrameAnimation Opacity()
            {
                var animation = _stickersPanel.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, 0);
                animation.InsertKeyFrame(1, 1);
                return animation;
            }

            ScalarKeyFrameAnimation Clip()
            {
                var animation = _stickersPanel.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, 48);
                animation.InsertKeyFrame(1, 0);
                return animation;
            }

            ScalarKeyFrameAnimation ClipShadow()
            {
                var animation = _stickersPanel.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, 48);
                animation.InsertKeyFrame(1, -48);
                return animation;
            }

            var opacity = Opacity();

            _stickersPanel.StartAnimation("Opacity", opacity);
            _stickersPanel.Clip.StartAnimation("LeftInset", Clip());
            _stickersPanel.Clip.StartAnimation("TopInset", Clip());

            // Guarded for null (see SetControlledPanel). The guard also happens to keep this head
            // clear of the PORTING.md 6 trap right underneath it: `opacity` is ONE
            // CompositionAnimation instance and it is started on _stickersPanel AND on
            // _stickersShadow, and Uno's Compositor.RegisterAnimation is a Dictionary keyed on the
            // animation, so the second StartAnimation with the same object throws.
            if (_stickersShadow != null)
            {
                _stickersShadow.StartAnimation("Opacity", Opacity());
                _stickersShadow.Clip.StartAnimation("LeftInset", ClipShadow());
                _stickersShadow.Clip.StartAnimation("TopInset", ClipShadow());
            }

#if LINUX
            // u-084: THIS is why the emoji/sticker/GIF drawer opened invisible.
            //
            // Not the shared-animation trap right above -- that one was already fixed in u-003 and
            // the fix holds. It is its sibling, the one measured on the composer pill: an animation
            // started on a visual that does not have a CompositionTarget yet is dropped on the
            // floor. Uno's Compositor.RegisterAnimation reads
            //
            //     var target = v.CompositionTarget;
            //     if (target != null) { _runningAnimations.Add(animation, target); ... }
            //
            // and there is NO else, so the animation never enters _runningAnimations and
            // RenderRootVisual never evaluates it. The only write that reaches the property is the
            // one StartAnimation performs itself, with the keyframe at progress 0 -- and for a
            // fade-in that value is 0. ControlledPanel was Collapsed until fifteen lines above
            // this, so its visual is exactly the case the rule describes: the panel laid out
            // correctly at its full 322x736 and painted not one pixel, for ever, with a clean log.
            // The dump said it plainly: #BackgroundElement [1586,212 322x736] c-opacity=0,00.
            //
            // ControlledPanel.Presenter IS #BackgroundElement (StickerPanel.Presenter =>
            // BackgroundElement), so _stickersPanel is that visual and no other.
            //
            // Remedy is the documented one -- the completion handler writes the final state by
            // hand, with its StopAnimation in front because on Uno a finished KeyFrameAnimation
            // keeps driving its property. There is no batch on this path, so it is queued on the
            // frame clock exactly as AnimatedGlyphButton and ChatBottomButton do. The values below
            // are not invented: they are the ones the !AreSmoothTransitionsEnabled branch at the
            // top of this method already writes for the same end state.
            CompositionScopedBatchEx.QueueCompleted(Constants.FastAnimation, () =>
            {
                _stickersPanel.StopAnimation("Opacity");
                _stickersPanel.Clip.StopAnimation("LeftInset");
                _stickersPanel.Clip.StopAnimation("TopInset");

                _stickersPanel.Opacity = 1;
                _stickersPanel.Clip = _stickersPanel.Compositor.CreateInsetClip(0, 0, 0, 0);

                if (_stickersShadow != null)
                {
                    _stickersShadow.StopAnimation("Opacity");
                    _stickersShadow.Clip.StopAnimation("LeftInset");
                    _stickersShadow.Clip.StopAnimation("TopInset");

                    _stickersShadow.Opacity = 1;
                    _stickersShadow.Clip = _stickersPanel.Compositor.CreateInsetClip(-48, -48, -48, -4);
                }
            });
#endif
        }

        private void Collapse_Click(object sender, RoutedEventArgs e)
        {
#if LINUX
            // Ahead of the guard below on purpose: every way the drawer closes comes through here,
            // and a pin left set on a closed drawer would swallow the next hover.
            Unpin();
#endif

            if (ControlledPanel.Visibility == Visibility.Collapsed || _stickersMode == StickersPanelMode.Collapsed)
            {
                return;
            }

            _stickersMode = StickersPanelMode.Collapsed;
            AppSettings.IsSidebarOpen = false;

            Closing?.Invoke(this, EventArgs.Empty);

            if (!PowerSavingPolicy.AreSmoothTransitionsEnabled)
            {
                _stickersPanel.Opacity = 0;
                _stickersPanel.Clip = _stickersPanel.Compositor.CreateInsetClip(48, 48, 0, 0);

                _stickersShadow.Opacity = 0;
                _stickersShadow.Clip = _stickersPanel.Compositor.CreateInsetClip(48, 48, -48, -4);

                ControlledPanel.Visibility = Visibility.Collapsed;
                ControlledPanel.Deactivate();

                return;
            }

            var batch = BootStrapper.Current.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                ControlledPanel.Visibility = Visibility.Collapsed;
                ControlledPanel.Deactivate();
            };

#if LINUX
            // u-084: this handler is the ONLY place the panel is collapsed and deactivated, and
            // CompositionScopedBatch.Completed never fires on Uno -- the third member of the family
            // CompositionRenderingClock/CompositionRenderedClock already cover. Left as it is on
            // Windows, opening the drawer once would have made it permanent: the fix in the open
            // path above is what makes it paint, and this one is what lets it close again.
            void Settled()
            {
                // Same reason as the open path: the animations may never have been registered at
                // all, and a finished KeyFrameAnimation keeps driving its property either way, so
                // the end state is written by hand behind a StopAnimation rather than trusted to
                // the animation. The values are the ones the !AreSmoothTransitionsEnabled branch
                // above writes for this same transition.
                _stickersPanel.StopAnimation("Opacity");
                _stickersPanel.Clip.StopAnimation("LeftInset");
                _stickersPanel.Clip.StopAnimation("TopInset");

                _stickersPanel.Opacity = 0;
                _stickersPanel.Clip = _stickersPanel.Compositor.CreateInsetClip(48, 48, 0, 0);

                if (_stickersShadow != null)
                {
                    _stickersShadow.StopAnimation("Opacity");
                    _stickersShadow.Clip.StopAnimation("LeftInset");
                    _stickersShadow.Clip.StopAnimation("TopInset");

                    _stickersShadow.Opacity = 0;
                    _stickersShadow.Clip = _stickersPanel.Compositor.CreateInsetClip(48, 48, -48, -4);
                }

                ControlledPanel.Visibility = Visibility.Collapsed;
                ControlledPanel.Deactivate();
            }
#endif

            // Same rule as the open path above: one instance per StartAnimation call.
            ScalarKeyFrameAnimation Opacity()
            {
                var animation = BootStrapper.Current.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, 1);
                animation.InsertKeyFrame(1, 0);
                return animation;
            }

            ScalarKeyFrameAnimation Clip()
            {
                var animation = BootStrapper.Current.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, 0);
                animation.InsertKeyFrame(1, 48);
                return animation;
            }

            _stickersPanel.StartAnimation("Opacity", Opacity());
            _stickersPanel.Clip.StartAnimation("LeftInset", Clip());
            _stickersPanel.Clip.StartAnimation("TopInset", Clip());

            if (_stickersShadow != null)
            {
                _stickersShadow.StartAnimation("Opacity", Opacity());
                _stickersShadow.Clip.StartAnimation("LeftInset", Clip());
                _stickersShadow.Clip.StartAnimation("TopInset", Clip());
            }

#if LINUX
            batch.EndWithCompleted(Constants.FastAnimation, Settled);
#else
            batch.End();
#endif

            IsChecked = false;
            Source = AppSettings.Stickers.SelectedTab;
        }

        private void Stickers_Click(object sender, RoutedEventArgs e)
        {
#if LINUX
            if (ControlledPanel.Visibility == Visibility.Collapsed || _stickersMode == StickersPanelMode.Collapsed)
            {
                // Passing a null pointer is what makes this ignore the hover setting: the guard it
                // lands on is IsPointerOverDisabled, which is false for a null pointer. Upstream
                // already relies on that for the keyboard shortcuts.
                Stickers_PointerEntered(sender, null);

                // Only pin what actually opened. Visibility and _stickersMode are two flags for one
                // state and the method above returns early on the first of them.
                if (ControlledPanel.Visibility == Visibility.Visible)
                {
                    Pin();
                }
            }
            else if (!_pinned)
            {
                // The drawer is on screen because the pointer is over the button, and upstream
                // reads this click as "close it". That is the behaviour being replaced: with
                // hover-to-open on -- and it is on by default -- a click could never open
                // anything, because the hover had already opened it a moment earlier. The click
                // now does the one thing hover cannot, which is make it STAY.
                Pin();
            }
            else
            {
                Collapse_Click(null, null);
            }
#else
            if (ControlledPanel.Visibility == Visibility.Collapsed || _stickersMode == StickersPanelMode.Collapsed)
            {
                Stickers_PointerEntered(sender, null);
            }
            else
            {
                Collapse_Click(null, null);
            }
#endif
        }

#if LINUX
        #region Pinned open

        /// <remarks>
        /// Linux only, and an addition rather than a port: upstream has no pinned state at all.
        /// The drawer there is a hover surface -- it appears under the pointer and leaves with it --
        /// and the click that was supposed to be the deliberate way in could not work, because with
        /// hover-to-open on (the default) the pointer had already opened the drawer before the
        /// click arrived, so Stickers_Click only ever found it open and closed it again.
        ///
        /// Pinned means: opened on purpose, stays until dismissed on purpose. Hover is untouched --
        /// an unpinned drawer still opens on pointer-in and closes 300ms after pointer-out, and the
        /// Settings > Stickers toggle still governs that path alone.
        /// </remarks>
        private bool _pinned;

        private UIElement _dismissRoot;
        private PointerEventHandler _dismissHandler;

        private void Pin()
        {
            if (_pinned)
            {
                return;
            }

            _pinned = true;

            // Click-away. The drawer is an ordinary element in the chat's own tree, not a Popup, so
            // there is no light-dismiss to ask for -- the press has to be watched for. The root is
            // walked up from the panel rather than taken from XamlRoot.Content, which is reported
            // to throw on this head (see the comment in VoipPage), and handledEventsToo is on
            // because whatever was clicked will have handled its own press long before it gets
            // here. A press inside a flyout the drawer itself opened -- the skin-tone picker, a
            // context menu -- lives in the popup tree and never reaches this root, which is the
            // behaviour wanted: those must not count as clicking away.
            // Assigned to the field first and then handed over, deliberately: AddHandler and
            // RemoveHandler match on the delegate INSTANCE, so a handler built inline could never
            // be taken off again and every pin would leave another one on the root for good.
            _dismissHandler ??= new PointerEventHandler(Stickers_DismissPressed);

            _dismissRoot = VisualRootOf(ControlledPanel);
            _dismissRoot?.AddHandler(UIElement.PointerPressedEvent, _dismissHandler, true);
        }

        private void Unpin()
        {
            _pinned = false;

            if (_dismissRoot != null && _dismissHandler != null)
            {
                _dismissRoot.RemoveHandler(UIElement.PointerPressedEvent, _dismissHandler);
            }

            _dismissRoot = null;
        }

        private void Stickers_DismissPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_pinned || e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            // Inside the drawer is not clicking away: choosing a sticker has to keep sending it,
            // and the press that precedes that choice must not close the drawer out from under it.
            // Inside the button is not clicking away either -- the Click that follows toggles it,
            // and closing here as well would open and shut it on one press.
            if (IsDescendantOf(source, ControlledPanel) || IsDescendantOf(source, this))
            {
                return;
            }

            // Not handled: the click belongs to whatever was clicked. Dismissing is a side effect.
            Collapse_Click(null, null);
        }

        /// <summary>
        /// Closes the drawer if a click pinned it open. Returns whether it did, so that Escape can
        /// stop at the drawer instead of also clearing the reply behind it.
        /// </summary>
        public bool CollapsePinned()
        {
            if (_pinned && ControlledPanel is { Visibility: Visibility.Visible })
            {
                Collapse_Click(null, null);
                return true;
            }

            return false;
        }

        private static UIElement VisualRootOf(DependencyObject element)
        {
            UIElement root = null;

            while (element != null)
            {
                if (element is UIElement candidate)
                {
                    root = candidate;
                }

                element = VisualTreeHelper.GetParent(element);
            }

            return root;
        }

        private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, ancestor))
                {
                    return true;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        #endregion
#endif

        public StickersPanelMode Mode => _stickersMode;

        public void Show(StickersTab tab)
        {
            if (ControlledPanel.Visibility == Visibility.Collapsed || _stickersMode == StickersPanelMode.Collapsed)
            {
                AppSettings.Stickers.SelectedTab = tab;
                Stickers_PointerEntered(null, null);
            }
            else if (AppSettings.Stickers.SelectedTab != tab)
            {
                AppSettings.Stickers.SelectedTab = tab;
                ControlledPanel.Activate();
            }
            else
            {
                Collapse();
            }
        }

        public void Collapse()
        {
            if (_stickersMode == StickersPanelMode.Overlay)
            {
                Collapse_Click(null, null);
            }
        }

        #endregion

        private void OnForegroundChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (_source != null && Foreground is SolidColorBrush foreground)
            {
                _source.SetColorProperty("Color_000000", foreground.Color);
            }
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            Icon = GetTemplateChild(nameof(Icon)) as Border;

            OnSourceChanged(Source, StickersTab.None);
            OnGlyphChanged(Source switch
            {
                StickersTab.Emoji => Icons.Emoji24,
                StickersTab.Stickers => Icons.Sticker24,
                StickersTab.Animations => Icons.Gif24,
                _ => Icons.Sticker24
            });
        }

        protected override bool IsRuntimeCompatible()
        {
#if LINUX
            // ApiInformation.IsApiContractPresent LIES on Uno: it answers true for any contract
            // asked of it (measured by u-012 against Uno.Foundation.dll), so this check said "the
            // real AnimatedIcon is available" on a head where it is not. Same treatment as
            // ChatRecordButton just below and as u-012 gave AnimatedIconToggleButton.
            //
            // It cost this button twice over, and both halves had to go:
            //   - AnimatedGlyphToggleButton.OnApplyTemplate RETURNS EARLY when this is true, so
            //     ContentPresenter1/ContentPresenter2 were never resolved and there was no glyph
            //     to show in the first place;
            //   - OnSourceChanged then took the LottieGen branch, whose six morph sources are
            //     INERT on this head (Telegram.Linux/Xaml/IconStubs.cs, added by u-016 itself), so
            //     GetVisual returns null and SetElementChildVisual(Icon, null) draws nothing.
            // False takes the glyph branch instead, which is Icons.Emoji24 / Sticker24 / Gif24 --
            // font glyphs from Assets/Fonts/Telegram.ttf, which do render here.
            //
            // Note this is the SECOND fault on this control: u-016 gave it back its template (its
            // only style was win:-only, so it measured 0). A template with no icon in it would
            // have been a fix that still looked broken.
            return false;
#else
            return Windows.Foundation.Metadata.ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 11);
#endif
        }

        #region Source

        public StickersTab Source
        {
            get { return (StickersTab)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(StickersTab), typeof(ChatStickerButton), new PropertyMetadata(StickersTab.None, OnSourceChanged));

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ChatStickerButton)d).OnSourceChanged((StickersTab)e.NewValue, (StickersTab)e.OldValue);
        }

        #endregion

        private void OnSourceChanged(StickersTab newValue, StickersTab oldValue)
        {
            if (newValue == oldValue || Icon == null)
            {
                return;
            }

            if (IsRuntimeCompatible())
            {
                var animate = oldValue != StickersTab.None;
                var visual = GetVisual(newValue, oldValue, animate, BootStrapper.Current.Compositor, out var source, out _props);

                _source = source;
                _previous = visual;

                if (Foreground is SolidColorBrush brush)
                {
                    source.SetColorProperty("Color_000000", brush.Color);
                }

                ElementCompositionPreview.SetElementChildVisual(Icon, visual?.RootVisual);
            }
            else
            {
                OnGlyphChanged(newValue switch
                {
                    StickersTab.Emoji => Icons.Emoji24,
                    StickersTab.Stickers => Icons.Sticker24,
                    StickersTab.Animations => Icons.Gif24,
                    _ => Icons.Sticker24
                });
            }
        }

        private IAnimatedVisual GetVisual(StickersTab newValue, StickersTab oldValue, bool animate, Compositor compositor, out IAnimatedVisualSource2 source, out CompositionPropertySet properties)
        {
            source = GetVisual(newValue, oldValue);

            if (source == null)
            {
                properties = null;
                return null;
            }

            var visual = source.TryCreateAnimatedVisual(compositor, out _);
            if (visual == null)
            {
                properties = null;
                return null;
            }

            properties = compositor.CreatePropertySet();
            properties.InsertScalar("Progress", animate ? 0.0F : 1.0F);

            var progressAnimation = compositor.CreateExpressionAnimation("_.Progress");
            progressAnimation.SetReferenceParameter("_", properties);
            visual.RootVisual.Properties.InsertScalar("Progress", animate ? 0.0F : 1.0F);
            visual.RootVisual.Properties.StartAnimation("Progress", progressAnimation);

            visual.RootVisual.Scale = new Vector3(0.1f, 0.1f, 0);

            if (animate)
            {
                var linearEasing = compositor.CreateLinearEasingFunction();
                var animation = compositor.CreateScalarKeyFrameAnimation();
                animation.Duration = visual.Duration;
                animation.InsertKeyFrame(1, 1, linearEasing);

                properties.StartAnimation("Progress", animation);
            }

            return visual;
        }

        private IAnimatedVisualSource2 GetVisual(StickersTab newValue, StickersTab oldValue)
        {
            return newValue switch
            {
                StickersTab.Emoji => oldValue switch
                {
                    StickersTab.Stickers => new StickerToEmoji(),
                    StickersTab.Animations => new GifToEmoji(),
                    _ => new GifToEmoji()
                },
                StickersTab.Stickers => oldValue switch
                {
                    StickersTab.Emoji => new EmojiToSticker(),
                    StickersTab.Animations => new GifToSticker(),
                    _ => new GifToSticker()
                },
                StickersTab.Animations => oldValue switch
                {
                    StickersTab.Emoji => new EmojiToGif(),
                    StickersTab.Stickers => new StickerToGif(),
                    _ => new StickerToGif()
                },
                _ => new EmojiToSticker()
            };
        }

        protected override void OnToggle()
        {
            //base.OnToggle();
        }
    }
}
