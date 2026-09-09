//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Native.Controls
{
    // Managed version of Telegram.Native/Controls/AnimatedImageBase.cpp
    public partial class AnimatedImageBase : Control
    {
        private bool _loaded;
        private bool _unloaded;

        private XamlRoot _xamlRoot;
        private double _rasterizationScale;

        private bool _viewportRegistered;
        private bool _visible;

        public AnimatedImageBase()
        {
            Loaded += OnLoadedChanged;
            Unloaded += OnUnloadedChanged;
            SizeChanged += HandleSizeChanged;
        }

        public bool IsConnected => _loaded;

        public bool IsDisconnected => _unloaded;

        protected virtual void OnLoaded()
        {
            var xamlRoot = XamlRoot;
            if (xamlRoot != null && _xamlRoot == null)
            {
                _xamlRoot = xamlRoot;
                _rasterizationScale = ResolveRasterizationScale(xamlRoot);
                xamlRoot.Changed += HandleXamlRootChanged;
            }
        }

        protected virtual void OnUnloaded()
        {
            if (_xamlRoot != null)
            {
                _xamlRoot.Changed -= HandleXamlRootChanged;
                _xamlRoot = null;
            }
        }

        // 12.10.2 changed this hook from SizeChangedEventArgs to the two sizes; the shared
        // AnimatedImage overrides that shape, so the base follows it.
        protected virtual void OnSizeChanged(Size oldSize, Size newSize)
        {

        }

        protected virtual void OnRasterizationScaleChanged(double rasterizationScale)
        {

        }

        protected virtual void OnViewportChanged(bool visible)
        {

        }

        /// <summary>
        /// The scale animated frames have to be rasterized at.
        ///
        /// <see cref="XamlRoot.RasterizationScale"/> reports 1 in Uno's X11 host even on a 2x
        /// display: the scale is applied to layout (a 2200x1440 window lays out as 1100x720) but
        /// the XamlRoot still answers 1 when the template is applied, and no
        /// <see cref="XamlRoot.Changed"/> arrives afterwards to correct it. Everything decoded with
        /// <c>DecodeFrameType="Logical"</c> -- every sticker, emoji, GIF and video sticker -- would
        /// then be rasterized at logical size and blown up by the compositor: a 222x222 frame
        /// stretched over 444 physical pixels, visibly soft (and a frame cache keyed to the wrong
        /// size, since the file is named after it).
        ///
        /// <see cref="Telegram.Common.DisplayScale"/> reads Xft.dpi, which is where that scale
        /// comes from in the first place, so it stands in whenever the XamlRoot has nothing better
        /// to say. A XamlRoot that does report a scale above 1 wins, so this disappears by itself
        /// the day Uno fills it in.
        /// </summary>
        protected static double ResolveRasterizationScale(XamlRoot xamlRoot)
        {
            var scale = xamlRoot?.RasterizationScale ?? 0;
            if (scale > 1)
            {
                return scale;
            }

            var display = Telegram.Common.DisplayScale.Current;
            if (display > 1)
            {
                return display;
            }

            return scale > 0 ? scale : 1;
        }

        public void RegisterViewportChanged()
        {
            if (!_viewportRegistered)
            {
                _viewportRegistered = true;
                EffectiveViewportChanged += HandleEffectiveViewportChanged;
            }
        }

        public void UnregisterViewportChanged()
        {
            if (_viewportRegistered)
            {
                _viewportRegistered = false;
                EffectiveViewportChanged -= HandleEffectiveViewportChanged;
            }
        }

        private void OnLoadedChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded)
            {
                _loaded = true;
                _unloaded = false;
                OnLoaded();
            }
        }

        private void OnUnloadedChanged(object sender, RoutedEventArgs e)
        {
            if (_loaded)
            {
                _loaded = false;
                _unloaded = true;
                OnUnloaded();
            }
        }

        private void HandleSizeChanged(object sender, SizeChangedEventArgs e)
        {
            OnSizeChanged(e.PreviousSize, e.NewSize);
        }

        private void HandleXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            var rasterizationScale = ResolveRasterizationScale(sender);
            if (rasterizationScale != _rasterizationScale)
            {
                _rasterizationScale = rasterizationScale;
                OnRasterizationScaleChanged(rasterizationScale);
            }
        }

        private void HandleEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            // Only EffectiveViewport is implemented in Uno: BringIntoViewDistanceX/Y and
            // MaxViewport all throw NotImplementedException. That is not a local failure - the
            // event is raised from EventManager.RaiseEffectiveViewportChangedEvents, which blanks
            // each queue entry *before* invoking it and only clears the queue after the loop, so a
            // handler that throws leaves a null element in the queue and every UpdateLayout from
            // then on dies with a NullReferenceException. One throwing viewport handler stops the
            // layout of the whole app.
            //
            // EffectiveViewport is the viewport expressed in this element's own coordinates, so
            // the distance the element would have to travel to come into view - which is what
            // BringIntoViewDistance means - is how far the viewport lies past either edge.
            var viewport = args.EffectiveViewport;

            var distanceX = Math.Max(Math.Max(viewport.X - sender.ActualWidth, -viewport.Right), 0);
            var distanceY = Math.Max(Math.Max(viewport.Y - sender.ActualHeight, -viewport.Bottom), 0);

            var visible = distanceX < sender.ActualWidth && distanceY < sender.ActualHeight;
            if (visible != _visible)
            {
                _visible = visible;
                OnViewportChanged(visible);
            }
        }
    }
}
