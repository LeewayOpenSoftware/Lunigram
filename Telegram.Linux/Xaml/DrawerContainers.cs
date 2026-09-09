//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Telegram.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Telegram.Controls.Drawers
{
    /// <summary>
    /// The two things the drawers lose on Uno, in one place.
    ///
    /// 1. <c>ListViewBase.ChoosingItemContainer</c> is NotImplemented: its add accessor only calls
    ///    TryRaiseNotImplemented, so the handler is never even stored and subscribing compiles
    ///    without a word (PORTING.md 6). Every drawer used it to hand each container its
    ///    ContextRequested hook and to register it with the zoomable-preview handler. Style and
    ///    ContentTemplate came from there too, but those are also declared in the XAML, so the
    ///    default container path already applies them -- the two side effects are the real loss.
    ///    They are re-attached from ContainerContentChanging, which Uno DOES raise. This is the
    ///    same split SearchChatsView already uses: template through the framework, hooks through
    ///    ContainerContentChanging.
    ///
    /// 2. <c>ContentControl.ContentTemplateRoot</c> is always null once the container has a
    ///    template, and every ListViewItem has one. Extensions.ContentRoot() reaches the real root
    ///    through the presenter, but it only answers once the container has entered the visual
    ///    tree, and ContainerContentChanging runs BEFORE that. So a miss is retried on Loaded and,
    ///    only if that also misses, once on SizeChanged -- the third and last pass PORTING.md 6
    ///    measured for an ItemTemplateSelector.
    /// </summary>
    internal static class DrawerContainers
    {
        // Containers are recycled, so a container that has already been hooked must not be hooked
        // again. A conditional table keyed on the container keeps this out of the drawers and dies
        // with them.
        private static readonly ConditionalWeakTable<DependencyObject, object> _hooked = new();

        /// <summary>
        /// Does what OnChoosingItemContainer did, at the first ContainerContentChanging pass for a
        /// container. Idempotent: a recycled container keeps the subscription it already has.
        /// </summary>
        public static void Prepare(SelectorItem container, TypedEventHandler<UIElement, ContextRequestedEventArgs> contextRequested, Action<SelectorItem> zoomerPrepared)
        {
            if (container == null || _hooked.TryGetValue(container, out _))
            {
                return;
            }

            _hooked.Add(container, null);

            if (contextRequested != null)
            {
                container.ContextRequested += contextRequested;
            }

            zoomerPrepared?.Invoke(container);
        }

        /// <summary>
        /// The first item whose container overlaps the scroller's visible rectangle.
        ///
        /// Replaces ItemsWrapGrid/ItemsStackPanel.FirstVisibleIndex, which is NotImplemented on Uno
        /// and THROWS -- and it is read from a ViewChanged handler, so left alone it takes the
        /// scroll gesture down with it rather than merely degrading. Same substitute as everywhere
        /// else in this port: the panel's Children ARE the materialized containers, so walk those
        /// and let geometry decide, instead of asking for an index that does not exist.
        /// </summary>
        public static object FirstVisibleItem(ListViewBase list)
        {
            var panel = list?.ItemsPanelRoot;
            var viewport = list?.GetScrollViewer();

            if (panel == null || viewport == null)
            {
                return null;
            }

            object best = null;
            var bestTop = double.MaxValue;

            var children = panel.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] is not SelectorItem container)
                {
                    continue;
                }

                double top;

                try
                {
                    var bounds = container
                        .TransformToVisual(viewport)
                        .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

                    if (bounds.Bottom <= 0 || bounds.Top >= viewport.ActualHeight)
                    {
                        continue;
                    }

                    top = bounds.Top;
                }
                catch
                {
                    continue;
                }

                if (top < bestTop)
                {
                    bestTop = top;
                    best = list.ItemFromContainer(container);
                }
            }

            return best;
        }

        /// <summary>
        /// Hands <paramref name="bind"/> the item template root, now or as soon as it exists.
        /// <paramref name="item"/> is captured up front and re-checked before a late call, because
        /// the container may have been recycled onto a different item by then.
        /// </summary>
        public static void WithContentRoot<TContent>(ListViewBase owner, SelectorItem container, object item, Action<TContent> bind) where TContent : class
        {
            if (container == null || bind == null)
            {
                return;
            }

            if (container.ContentRoot() is TContent root)
            {
                bind(root);
                return;
            }

            void Retry(bool last)
            {
                // The container is pooled: if it has been handed to another item since, this pass
                // is stale and binding it would paint the wrong cell.
                if (owner != null && !Equals(owner.ItemFromContainer(container), item))
                {
                    return;
                }

                if (container.ContentRoot() is TContent later)
                {
                    bind(later);
                }
                else if (last)
                {
                    // The one place a cell really does stay blank, so it gets said out loud
                    // instead of failing the way ContentTemplateRoot used to: silently.
                    Logger.Warning($"drawer cell has no content root after Loaded and SizeChanged: {item?.GetType().Name}");
                }
            }

            void OnLoaded(object sender, RoutedEventArgs args)
            {
                container.Loaded -= OnLoaded;
                Retry(false);

                if (container.ContentRoot() is TContent)
                {
                    return;
                }

                void OnSizeChanged(object s, SizeChangedEventArgs e)
                {
                    container.SizeChanged -= OnSizeChanged;
                    Retry(true);
                }

                container.SizeChanged += OnSizeChanged;
            }

            container.Loaded += OnLoaded;
        }
    }
}
