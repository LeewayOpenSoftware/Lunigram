//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Common;
using Telegram.Controls.Drawers;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Drawers;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Telegram.Controls
{
    public sealed partial class StickerPanel : UserControl
    {
        public new FrameworkElement Shadow => ShadowElement;

        internal Microsoft.UI.Composition.Visual ShadowVisual { get; private set; }
        public FrameworkElement Presenter => BackgroundElement;

        public event EventHandler SettingsClick;

        public Action<object> EmojiClick { get; set; }
        public event TypedEventHandler<UIElement, ItemContextRequestedEventArgs<StickerViewModel>> EmojiContextRequested;

        public event EventHandler<StickerDrawerItemClickEventArgs> StickerClick;
        public event EventHandler<ItemContextRequestedEventArgs<Sticker>> StickerContextRequested;
        public event EventHandler ChoosingSticker;

        public event EventHandler<ItemClickEventArgs> AnimationClick;
        public event EventHandler<ItemContextRequestedEventArgs<Animation>> AnimationContextRequested;

        public DialogViewModel ViewModel => DataContext as DialogViewModel;

        public ISession Session
        {
            get
            {
                if (DataContext is ViewModelBase viewModel)
                {
                    return viewModel.Session;
                }

                // TODO: verify
                return null;
            }
        }

        private int _prevIndex = -1;

        public StickerPanel()
        {
            InitializeComponent();

            Instrumentation.Register(this);

            var header = VisualUtilities.DropShadow(HeaderSeparator);
            var shadow = VisualUtilities.DropShadow(ShadowElement);

            if (header != null)
            {
                header.Clip = header.Compositor.CreateInsetClip(0, -40, 0, 40);
            }

            // Kept rather than discarded: ChatStickerButton reaches the panel's shadow visual with
            // ElementCompositionPreview.GetElementChildVisual, which is NotImplemented on Uno, so on
            // this head it has to be handed the visual instead of looking it up. Null here is the
            // normal Linux case (no drop shadow at all), and the button copes with that.
            ShadowVisual = shadow;
        }

        private void Emojis_ItemClick(object sender, EmojiDrawerItemClickEventArgs e)
        {
            if (e.ClickedItem is EmojiData emoji)
            {
                EmojiClick?.Invoke(emoji.Value);
            }
            else if (e.ClickedItem is StickerViewModel sticker)
            {
                EmojiClick?.Invoke((Sticker)sticker);
            }
        }

        private IEnumerable<IDrawer> GetDrawers()
        {
            yield return EmojisRoot;
            yield return AnimationsRoot;
            yield return StickersRoot;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadAtIndex(ViewModel?.Chat, Navigation.SelectedIndex, /* unsure here */ false);
        }

        private void LoadAtIndex(Chat chat, int index, bool unload)
        {
            if (index == 0)
            {
                if (unload)
                {
                    UnloadAtIndex(1);
                    UnloadAtIndex(2);
                }
                else
                {
                    Tab1.Visibility = Visibility.Collapsed;
                    Tab2.Visibility = Visibility.Collapsed;

                    AnimationsRoot?.UnloadVisibleItems();
                    StickersRoot?.UnloadVisibleItems();
                }

                Tab0.Visibility = Visibility.Visible;

#if LINUX
                // Uno's XAML generator never emits the UnloadObject() that WinUI's compiler does
                // (measured: zero occurrences in the generated StickerPanel partial, while x:Load
                // itself works and produces ElementStubs). The Linux UnloadObject, in
                // Telegram.Linux/Xaml/StickerPanelLinux.cs, therefore cannot put the stub back and
                // the drawer stays materialized. UnloadAtIndex still nulls the DataContext and drops
                // the handlers, so "already materialized" stops implying "already wired": without
                // this the second visit to a tab skips the init branch and returns a live control
                // with no view model and no click handlers -- a drawn, dead panel.
                if (EmojisRoot == null || EmojisRoot.DataContext == null)
#else
                if (EmojisRoot == null)
#endif
                {
                    if (EmojisRoot == null)
                    {
                        FindName(nameof(EmojisRoot));
#if LINUX
                        AdoptMaterialized(EmojisPanel, EmojisRoot);
#endif
                    }

                    EmojisRoot.DataContext = EmojiDrawerViewModel.Create(Session);
                    EmojisRoot.ItemContextRequested += EmojiContextRequested;
                }
                else
                {
                    if (EmojisRoot.IsLoaded)
                    {
                        Show(Tab0, _prevIndex, _prevIndex = index);
                    }

                    EmojisRoot.LoadVisibleItems();
                }

                EmojisRoot.Activate(chat);
                AppSettings.Stickers.SelectedTab = StickersTab.Emoji;
            }
            else if (index == 1)
            {
                if (unload)
                {
                    UnloadAtIndex(0);
                    UnloadAtIndex(2);
                }
                else
                {
                    Tab0.Visibility = Visibility.Collapsed;
                    Tab2.Visibility = Visibility.Collapsed;

                    EmojisRoot?.UnloadVisibleItems();
                    StickersRoot?.UnloadVisibleItems();
                }

                Tab1.Visibility = Visibility.Visible;

#if LINUX
                // Uno's XAML generator never emits the UnloadObject() that WinUI's compiler does
                // (measured: zero occurrences in the generated StickerPanel partial, while x:Load
                // itself works and produces ElementStubs). The Linux UnloadObject, in
                // Telegram.Linux/Xaml/StickerPanelLinux.cs, therefore cannot put the stub back and
                // the drawer stays materialized. UnloadAtIndex still nulls the DataContext and drops
                // the handlers, so "already materialized" stops implying "already wired": without
                // this the second visit to a tab skips the init branch and returns a live control
                // with no view model and no click handlers -- a drawn, dead panel.
                if (AnimationsRoot == null || AnimationsRoot.DataContext == null)
#else
                if (AnimationsRoot == null)
#endif
                {
                    if (AnimationsRoot == null)
                    {
                        FindName(nameof(AnimationsRoot));
#if LINUX
                        AdoptMaterialized(AnimationsPanel, AnimationsRoot);
#endif
                    }

                    AnimationsRoot.DataContext = AnimationDrawerViewModel.Create(Session);
                    AnimationsRoot.ItemClick += AnimationClick;
                    AnimationsRoot.ItemContextRequested += AnimationContextRequested;
                }
                else
                {
                    if (AnimationsRoot.IsLoaded)
                    {
                        Show(Tab1, _prevIndex, _prevIndex = index);
                    }

                    AnimationsRoot.LoadVisibleItems();
                }

                AnimationsRoot.Activate(chat);
                AppSettings.Stickers.SelectedTab = StickersTab.Animations;
            }
            else if (index == 2)
            {
                if (unload)
                {
                    UnloadAtIndex(0);
                    UnloadAtIndex(1);
                }
                else
                {
                    Tab0.Visibility = Visibility.Collapsed;
                    Tab1.Visibility = Visibility.Collapsed;

                    EmojisRoot?.UnloadVisibleItems();
                    AnimationsRoot?.UnloadVisibleItems();
                }

                Tab2.Visibility = Visibility.Visible;

#if LINUX
                // Uno's XAML generator never emits the UnloadObject() that WinUI's compiler does
                // (measured: zero occurrences in the generated StickerPanel partial, while x:Load
                // itself works and produces ElementStubs). The Linux UnloadObject, in
                // Telegram.Linux/Xaml/StickerPanelLinux.cs, therefore cannot put the stub back and
                // the drawer stays materialized. UnloadAtIndex still nulls the DataContext and drops
                // the handlers, so "already materialized" stops implying "already wired": without
                // this the second visit to a tab skips the init branch and returns a live control
                // with no view model and no click handlers -- a drawn, dead panel.
                if (StickersRoot == null || StickersRoot.DataContext == null)
#else
                if (StickersRoot == null)
#endif
                {
#if LINUX
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var materialized = 0L;
#endif
                    if (StickersRoot == null)
                    {
                        FindName(nameof(StickersRoot));
#if LINUX
                        AdoptMaterialized(StickersPanel, StickersRoot);
                        materialized = stopwatch.ElapsedMilliseconds;
#endif
                    }

#if LINUX
                    // Kept for the life of the panel instead of resolved again.
                    //
                    // The drawer view models are registered as INSTANCES, not singletons, so
                    // Session.Resolve<T>() constructs a new one on every call (the generated
                    // resolver in Telegram.Generators/SessionResolverGenerator.cs returns
                    // `new ...` for _instances and a cached field only for _singletons/_lazy).
                    // Upstream that costs nothing, because the branch is guarded on
                    // `StickersRoot == null` and so runs exactly once. Here it is guarded on
                    // DataContext == null as well - it has to be, see the note above - and
                    // UnloadAtIndex nulls the DataContext, so a fresh view model was being built
                    // and refetching every sticker on EVERY open of the tab.
                    //
                    // Reuse is safe for the reason the re-wire exists: what unload drops is the
                    // DataContext and the three handlers, both of which are restored right here.
                    // UnloadAtIndex already reaches for the OLD view model after nulling it, to
                    // reset the search - so the object was expected to outlive the unload.
                    _stickersViewModelWasNew = _stickersViewModel == null;
                    StickersRoot.DataContext = _stickersViewModel ??= StickerDrawerViewModel.Create(Session);
#else
                    StickersRoot.DataContext = StickerDrawerViewModel.Create(Session);
#endif
                    StickersRoot.ItemClick += StickerClick;
                    StickersRoot.ItemContextRequested += StickerContextRequested;
                    StickersRoot.ChoosingItem += ChoosingSticker;
#if LINUX
                    Logger.Info($"sticker panel: stickers tab ready in {stopwatch.ElapsedMilliseconds}ms " +
                        $"(materialize {materialized}ms, view model {(_stickersViewModelWasNew ? "created" : "reused")})");
#endif
                }
                else
                {
                    if (StickersRoot.IsLoaded)
                    {
                        Show(Tab2, _prevIndex, _prevIndex = index);
                    }

                    StickersRoot.LoadVisibleItems();
                }

                StickersRoot.Activate(chat);
                AppSettings.Stickers.SelectedTab = StickersTab.Stickers;
            }

            Navigation.SelectionChanged -= OnSelectionChanged;
            Navigation.SelectedIndex = index;
            Navigation.SelectionChanged += OnSelectionChanged;
        }

#if LINUX
        /// <summary>
        /// Makes a Border that has just had its <c>x:Load</c> child materialized actually measure
        /// it.
        /// </summary>
        /// <remarks>
        /// Measured on screen, not deduced: with the sticker panel open, the visual tree dump
        /// reads
        ///
        ///   Border #EmojisPanel [1547,253 320x694]
        ///     EmojiDrawer #EmojisRoot [1547,253 0x0]
        ///       Grid [1547,253 0x0]  Border #SearchHost [1547,253 0x0]  GridView #List [0x0]
        ///
        /// - the host Border has its full size and the drawer under it is zero, all the way down
        /// including the search box, which is an Auto row that would measure ~32px if it had ever
        /// been measured at all. Both drawers do it, and switching tabs (which makes the tab Grid
        /// Visible and so re-measures it) does not fix it, so it is not a missed invalidation
        /// higher up: the Border simply is not measuring this child.
        ///
        /// That is the <c>x:Load</c>/ElementStub family again (PORTING.md 6, u-078), in the one
        /// place the existing notes did not cover: a stub under a <b>Border</b> rather than a
        /// Panel. FindName puts the real control in the visual tree, but the Border's own Child is
        /// still what it was, so its measure pass never reaches the drawer and every drawer in
        /// this panel renders as an empty rectangle.
        ///
        /// Re-assigning Child is what re-seats it and invalidates the Border in one go. Deferred
        /// loading is kept - this runs once per tab, on the materializing branch only.
        /// </remarks>
        private static void AdoptMaterialized(Border host, UIElement materialized)
        {
            if (host == null || materialized == null)
            {
                return;
            }

            if (!ReferenceEquals(host.Child, materialized))
            {
                Logger.Info($"sticker panel: {materialized.GetType().Name} materialized but its host Border was still holding {(host.Child == null ? "null" : host.Child.GetType().Name)}; re-seating it");
            }

            host.Child = null;
            host.Child = materialized;
            host.InvalidateMeasure();
        }

#endif
        private void Show(UIElement element, int prevIndex, int index)
        {
            Settings.Visibility = index != 1
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!PowerSavingPolicy.AreSmoothTransitionsEnabled || prevIndex == index || prevIndex == -1)
            {
                return;
            }

            var leftToRight = prevIndex > index;

            var visualIn = ElementComposition.GetElementVisual(element);
            var offsetIn = visualIn.Compositor.CreateVector3KeyFrameAnimation();
            offsetIn.InsertKeyFrame(0, new Vector3(leftToRight ? -48 : 48, 0, 0));
            offsetIn.InsertKeyFrame(1, new Vector3());
            offsetIn.Duration = Constants.SoftAnimation;

            var opacityIn = visualIn.Compositor.CreateScalarKeyFrameAnimation();
            opacityIn.InsertKeyFrame(0, 0);
            opacityIn.InsertKeyFrame(1, 1);
            opacityIn.Duration = Constants.SoftAnimation;

            visualIn.StartAnimation("Offset", offsetIn);
            visualIn.StartAnimation("Opacity", opacityIn);
        }

#if LINUX
        private StickerDrawerViewModel _stickersViewModel;
        private bool _stickersViewModelWasNew;
#endif

        private void UnloadAtIndex(int index)
        {
            if (index == 0 && EmojisRoot != null)
            {
                EmojisRoot.Deactivate();
                EmojisRoot.DataContext = null;
                EmojisRoot.ItemContextRequested -= EmojiContextRequested;
                UnloadObject(EmojisRoot);

                Tab0.Visibility = Visibility.Collapsed;
            }
            else if (index == 1 && AnimationsRoot != null)
            {
                var viewModel = AnimationsRoot.DataContext as AnimationDrawerViewModel;

                AnimationsRoot.Deactivate();
                AnimationsRoot.DataContext = null;
                AnimationsRoot.ItemClick -= AnimationClick;
                AnimationsRoot.ItemContextRequested -= AnimationContextRequested;
                UnloadObject(AnimationsRoot);

                Tab1.Visibility = Visibility.Collapsed;

                viewModel?.Search(string.Empty);
            }
            else if (index == 2 && StickersRoot != null)
            {
                var viewModel = StickersRoot.DataContext as StickerDrawerViewModel;

                StickersRoot.Deactivate();
                StickersRoot.DataContext = null;
                StickersRoot.ItemClick -= StickerClick;
                StickersRoot.ItemContextRequested -= StickerContextRequested;
                StickersRoot.ChoosingItem -= ChoosingSticker;
                UnloadObject(StickersRoot);

                Tab2.Visibility = Visibility.Collapsed;

                viewModel?.Search(string.Empty, false);
            }
        }

        private bool _emojisRights;
        private bool _stickersRights;
        private bool _animationsRights;

        public void UpdateChatPermissions(IClientService clientService, Chat chat)
        {
            var emojisRights = DialogViewModel.VerifyRights(clientService, chat, x => x.CanSendBasicMessages, Strings.GlobalSendMessageRestricted, Strings.SendMessageRestrictedForever, Strings.SendMessageRestricted, out string emojisLabel);
            var stickersRights = DialogViewModel.VerifyRights(clientService, chat, x => x.CanSendOtherMessages, Strings.GlobalAttachStickersRestricted, Strings.AttachStickersRestrictedForever, Strings.AttachStickersRestricted, out string stickersLabel);
            var animationsRights = DialogViewModel.VerifyRights(clientService, chat, x => x.CanSendOtherMessages, Strings.GlobalAttachGifRestricted, Strings.AttachGifRestrictedForever, Strings.AttachGifRestricted, out string animationsLabel);

            if (_emojisRights != emojisRights || emojisRights)
            {
                _emojisRights = emojisRights;
                EmojisPanel.Visibility = emojisRights ? Visibility.Collapsed : Visibility.Visible;
                EmojisPermission.Visibility = emojisRights ? Visibility.Visible : Visibility.Collapsed;
                EmojisPermission.Text = emojisLabel ?? string.Empty;
            }

            if (_stickersRights != stickersRights || stickersRights)
            {
                _stickersRights = stickersRights;
                StickersPanel.Visibility = stickersRights ? Visibility.Collapsed : Visibility.Visible;
                StickersPermission.Visibility = stickersRights ? Visibility.Visible : Visibility.Collapsed;
                StickersPermission.Text = stickersLabel ?? string.Empty;
            }

            if (_animationsRights != animationsRights || animationsRights)
            {
                _animationsRights = animationsRights;
                AnimationsPanel.Visibility = animationsRights ? Visibility.Collapsed : Visibility.Visible;
                AnimationsPermission.Visibility = animationsRights ? Visibility.Visible : Visibility.Collapsed;
                AnimationsPermission.Text = animationsLabel ?? string.Empty;
            }
        }

        public void Activate()
        {
            switch (AppSettings.Stickers.SelectedTab)
            {
                case StickersTab.Emoji:
                    LoadAtIndex(ViewModel?.Chat, 0, /* unsure here */ false);
                    break;
                case StickersTab.Animations:
                    LoadAtIndex(ViewModel?.Chat, 1, /* unsure here */ false);
                    break;
                case StickersTab.Stickers:
                    LoadAtIndex(ViewModel?.Chat, 2, /* unsure here */ false);
                    break;
            }
        }

        public void Deactivate()
        {
            for (int i = 0; i < 3; i++)
            {
                UnloadAtIndex(i);
            }

            _prevIndex = -1;
        }

        public void UnloadVisibleItems()
        {
            foreach (var drawer in GetDrawers())
            {
                drawer?.UnloadVisibleItems();
            }
        }

        public void LoadVisibleItems()
        {
            foreach (var drawer in GetDrawers())
            {
                drawer?.LoadVisibleItems();
            }
        }

        private void EmojisRoot_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                element.Loaded -= EmojisRoot_Loaded;
                Show(Tab0, _prevIndex, _prevIndex = 0);
            }
        }

        private void AnimationsRoot_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                element.Loaded -= AnimationsRoot_Loaded;
                Show(Tab1, _prevIndex, _prevIndex = 1);
            }
        }

        private void StickersRoot_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                element.Loaded -= StickersRoot_Loaded;
                Show(Tab2, _prevIndex, _prevIndex = 2);
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            SettingsClick?.Invoke(this, EventArgs.Empty);
        }

#if INSTRUMENTATION
        // The three drawers are x:Load deferred: UnloadObject nulls the field the XAML generator
        // owns, so a drawer that is still alive after UnloadAtIndex is by construction unreachable
        // from here and gets reported. With every tab unloaded the correct number of live drawers
        // is ZERO.
        //
        // The panel is reached through ChatView.DebugRoots rather than a static of its own, so a
        // panel hosted anywhere else (StoriesWindow, and the bare StickerDrawer on
        // BusinessIntroPage) is not rooted and will be reported as an orphan while it is open.
        internal IEnumerable<object> DebugChildren()
        {
            yield return EmojisRoot;
            yield return AnimationsRoot;
            yield return StickersRoot;
        }

        // Returns nothing for types this area does not own, so it composes with the other areas'.
        internal static IEnumerable<object> DebugChildrenOf(object node)
        {
            return node switch
            {
                StickerPanel x => x.DebugChildren(),
                _ => Array.Empty<object>()
            };
        }
#endif
    }

    public interface IDrawer
    {
        void Activate(Chat chat, EmojiSearchType type = EmojiSearchType.Default);
        void Deactivate();

        void LoadVisibleItems();
        void UnloadVisibleItems();

        StickersTab Tab { get; }
    }
}
