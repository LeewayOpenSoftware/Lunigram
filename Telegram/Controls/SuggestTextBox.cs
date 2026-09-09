//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Controls
{
    public partial class SuggestTextBox : TextBox
    {
        public SuggestTextBox()
        {
            DefaultStyleKey = typeof(SuggestTextBox);
            TextChanged += OnTextChanged;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (ControlledList != null)
            {
#if LINUX
                // Same meaning as the two lines below: from here to the next refill of the list,
                // the first real result is the one to highlight. See OnItemsVectorChanged.
                _pendingSelection = true;
#else
                ControlledList.ChoosingItemContainer -= OnChoosingItemContainer;
                ControlledList.ChoosingItemContainer += OnChoosingItemContainer;
#endif
            }
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Down && ControlledList != null)
            {
                var nextIndex = Math.Max(ControlledList.SelectedIndex + 1, StartingIndex);
                if (nextIndex < ControlledList.Items.Count)
                {
                    ControlledList.SelectedIndex = nextIndex;
                    ControlledList.ScrollIntoView(ControlledList.SelectedItem);
                }

                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Up && ControlledList != null)
            {
                if (ControlledList.SelectedIndex > StartingIndex)
                {
                    ControlledList.SelectedIndex--;
                    ControlledList.ScrollIntoView(ControlledList.SelectedItem);
                }
                else
                {
                    ControlledList.SelectedIndex = -1;
                    ControlledList.ScrollToTop();
                }

                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Enter && ControlledList != null)
            {
                var index = Math.Max(ControlledList.SelectedIndex, StartingIndex);
                if (index < ControlledList.Items.Count)
                {
                    var container = ControlledList.ContainerFromIndex(index) as ListViewItem;
                    if (container != null)
                    {
                        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(container);
                        var provider = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                        provider?.Invoke();
                    }
                }

                e.Handled = true;
            }
            else
            {
                base.OnKeyDown(e);
            }
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (sender.Items.Count > StartingIndex)
            {
                sender.SelectedIndex = StartingIndex;
                sender.ChoosingItemContainer -= OnChoosingItemContainer;
            }
        }

#if LINUX
        // ListViewBase.ChoosingItemContainer is [NotImplemented("__SKIA__")] in Uno: its add and
        // remove accessors share one body and that body only calls TryRaiseNotImplemented, so the
        // handler above is never even stored, let alone called. The visible consequence was that
        // SelectedIndex stayed at -1 while typing and nothing was highlighted until the first
        // press of Down; Enter kept working only because it asks for Math.Max(SelectedIndex,
        // StartingIndex), which turns -1 into StartingIndex.
        //
        // ItemCollection.VectorChanged IS implemented in the deployed Uno.UI 6.6.184: assigning
        // ItemsSource raises a Reset (ItemCollection.SetItemsSource) and, when the source is an
        // INotifyCollectionChanged or an IObservableVector, every later change of the source is
        // forwarded item by item. That is exactly the moment this needs -- the list has just been
        // refilled with the results for the text that was typed -- and it is a narrower hook than
        // ChoosingItemContainer, which on Windows also fires on plain scrolling. The flag is what
        // keeps the selection from being re-imposed on the user after every incremental page:
        // one selection per text change, which is what the -= on Windows achieves.
        private bool _pendingSelection;

        private void OnItemsVectorChanged(IObservableVector<object> sender, IVectorChangedEventArgs args)
        {
            if (!_pendingSelection || sender.Count <= StartingIndex)
            {
                return;
            }

            var list = ControlledList;
            if (list == null)
            {
                return;
            }

            _pendingSelection = false;
            list.SelectedIndex = StartingIndex;
        }
#endif

        #region ControlledList

        public ListViewBase ControlledList
        {
            get { return (ListViewBase)GetValue(ControlledListProperty); }
            set { SetValue(ControlledListProperty, value); }
        }

        public static readonly DependencyProperty ControlledListProperty =
            DependencyProperty.Register("ControlledList", typeof(ListViewBase), typeof(SuggestTextBox), new PropertyMetadata(null, OnControlledListChanged));

        private static void OnControlledListChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SuggestTextBox)d).OnControlledListChanged((ListViewBase)e.NewValue, (ListViewBase)e.OldValue);
        }

        private void OnControlledListChanged(ListViewBase newValue, ListViewBase oldValue)
        {
#if LINUX
            // AutomationProperties.GetControlledPeers returns NULL in Uno when the attached
            // property has never been set (WinUI hands back an empty list), so the two calls below
            // are a NullReferenceException raised from inside a DependencyProperty callback -
            // measured on 2026-08-26, the moment MainPage started handing the real
            // SearchChatsView.Root to the search box. It is swallowed by Uno's property system, so
            // what is lost is silent: the rest of this setter, and with it the ChoosingItemContainer
            // subscription, never runs.
            //
            // The peer list is worthless without an accessibility client, so the guard is the
            // whole fix rather than a workaround. The ChoosingItemContainer subscription is gone
            // for good here: Uno never raises it (PORTING.md 6) and Items.VectorChanged takes its
            // place -- see OnItemsVectorChanged.
            var peers = AutomationProperties.GetControlledPeers(this);

            if (oldValue != null)
            {
                oldValue.Items.VectorChanged -= OnItemsVectorChanged;
                peers?.Remove(oldValue);
            }

            if (newValue != null)
            {
                newValue.Items.VectorChanged += OnItemsVectorChanged;
                peers?.Add(newValue);
            }

            // A list that is being handed over mid-search has nothing pending on it.
            _pendingSelection = false;
#else
            if (oldValue != null)
            {
                oldValue.ChoosingItemContainer -= OnChoosingItemContainer;
                AutomationProperties.GetControlledPeers(this).Remove(oldValue);
            }

            if (newValue != null)
            {
                newValue.ChoosingItemContainer += OnChoosingItemContainer;
                AutomationProperties.GetControlledPeers(this).Add(newValue);
            }
#endif
        }

        #endregion

        #region StartingIndex

        public int StartingIndex
        {
            get { return (int)GetValue(StartingIndexProperty); }
            set { SetValue(StartingIndexProperty, value); }
        }

        public static readonly DependencyProperty StartingIndexProperty =
            DependencyProperty.Register("StartingIndex", typeof(int), typeof(SuggestTextBox), new PropertyMetadata(0));

        #endregion
    }
}
