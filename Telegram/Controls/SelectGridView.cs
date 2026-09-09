//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
#if LINUX
using Telegram.Common;
#endif

namespace Telegram.Controls
{
    public partial class SelectGridView : GridView
    {
        public SelectGridView()
        {
            ContainerContentChanging += OnContainerContentChanging;
            RegisterPropertyChangedCallback(SelectionModeProperty, OnSelectionModeChanged);
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
#if LINUX
            // ContentTemplateRoot is never assigned by Uno for anything that carries a control
            // template, and a GridViewItem always does. See PORTING.md 6 (ContentTemplateRootEx).
            var content = args.ItemContainer.ContentRoot();
#else
            var content = args.ItemContainer.ContentTemplateRoot;
#endif
            content?.IsHitTestVisible = SelectionMode == ListViewSelectionMode.None;
        }

        private void OnSelectionModeChanged(DependencyObject sender, DependencyProperty dp)
        {
#if LINUX
            // Not `as ItemsWrapGrid`: on Linux the media grids swap that panel for a
            // VariableSizedWrapGrid, because Uno's ItemsWrapGrid is a bare Panel with no
            // MeasureOverride and no layout at all (measured on the deployed Uno.UI.dll, see
            // PORTING.md 6). Asking for the base Panel serves either one.
            var panel = ItemsPanelRoot;
#else
            var panel = ItemsPanelRoot as ItemsWrapGrid;
#endif
            if (panel == null)
            {
                return;
            }

            foreach (SelectorItem container in panel.Children)
            {
#if LINUX
                var content = container.ContentRoot();
#else
                var content = container.ContentTemplateRoot;
#endif
                content?.IsHitTestVisible = SelectionMode != ListViewSelectionMode.Multiple;
            }
        }
    }
}
