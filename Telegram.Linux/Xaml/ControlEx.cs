//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Telegram.Native.Controls
{
    // Managed versions of Telegram.Native/Controls/FrameworkElementEx.h. The C++ side had to
    // inspect the parent because WinUI can raise Loaded/Unloaded out of order; Uno raises them
    // synchronously and in order, so the events themselves are the state.

    public partial class ControlEx : Control
    {
        private bool _loaded;
        private bool _unloaded;

        public ControlEx()
        {
            Loaded += OnLoadedChanged;
            Unloaded += OnUnloadedChanged;
        }

        public bool IsConnected => _loaded;

        public bool IsDisconnected => _unloaded;

        protected virtual void OnLoaded()
        {

        }

        protected virtual void OnUnloaded()
        {

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
    }

    public partial class GridEx : Grid
    {
        private bool _loaded;
        private bool _unloaded;

        /// <summary>
        /// In the C++/WinRT original this is the hook of
        /// <c>DisconnectVisualChildrenRecursive</c>, which the XAML framework calls when it tears
        /// the subtree down. Uno has no such hook, so it is raised from the unload instead: the one
        /// override in the tree (ChatRecordBar) uses it to stop an animation that would otherwise
        /// keep a composition callback alive after the control left the tree, and leaving the tree
        /// is exactly when that matters.
        /// </summary>
        protected virtual void OnDisconnectVisualChildren()
        {

        }

        public GridEx()
        {
            Loaded += OnLoadedChanged;
            Unloaded += OnUnloadedChanged;
        }

        public bool IsConnected => _loaded;

        public bool IsDisconnected => _unloaded;

        protected virtual void OnLoaded()
        {

        }

        protected virtual void OnUnloaded()
        {

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
                OnDisconnectVisualChildren();
            }
        }
    }

    public partial class HyperlinkButtonEx : HyperlinkButton
    {
        private bool _loaded;
        private bool _unloaded;

        public HyperlinkButtonEx()
        {
            Loaded += OnLoadedChanged;
            Unloaded += OnUnloadedChanged;
        }

        public bool IsConnected => _loaded;

        public bool IsDisconnected => _unloaded;

        protected virtual void OnLoaded()
        {

        }

        protected virtual void OnUnloaded()
        {

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
    }

    public partial class ToggleButtonEx : ToggleButton
    {
        private bool _loaded;
        private bool _unloaded;

        public ToggleButtonEx()
        {
            Loaded += OnLoadedChanged;
            Unloaded += OnUnloadedChanged;
        }

        public bool IsConnected => _loaded;

        public bool IsDisconnected => _unloaded;

        protected virtual void OnLoaded()
        {

        }

        protected virtual void OnUnloaded()
        {

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
    }
}
