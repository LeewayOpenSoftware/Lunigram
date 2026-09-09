//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Telegram.Common;
using Telegram.Controls.Cells;
using Telegram.Td.Api;
using Telegram.ViewModels.Settings;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsSessionsPage : HostedPage
    {
        public SettingsSessionsViewModel ViewModel => DataContext as SettingsSessionsViewModel;

        public SettingsSessionsPage()
        {
            InitializeComponent();
            Title = Strings.SessionsTitle;

#if LINUX
            // MEASURED 2026-08-26, and this was the worst kind of miss: TDLib answered
            // getActiveSessions with `sessions = vector[11]` and the screen showed exactly ONE
            // device - the current one, which is drawn by a direct x:Bind and never goes near the
            // list. The other ten were simply not there, on the screen whose whole purpose is
            // "which devices are logged into my account".
            //
            // The list is bound to `{x:Bind ItemsSource.View}`, where ItemsSource is a
            // CollectionViewSource with IsSourceGrouped="True" over a collection of
            // KeyedList<KeyedGroup, Session>. Uno ships the grouping types (IsSourceGrouped,
            // ItemsPath and ICollectionViewGroup are all in Uno.UI.dll) but that view comes back
            // empty here: the ItemsStackPanel measured 831x0 with ten sessions in the view model.
            // Nothing throws and nothing is logged - the panel is just empty.
            //
            // So Linux skips the grouped view and feeds the rows straight in, flattened in the
            // order the view model already put them (login attempts first, then the rest). What is
            // lost is the group HEADER ("Otras sesiones" / "Intentos de acceso") and its footer;
            // what is gained is the ten devices. Everything else is untouched: the same
            // SessionCell, the same ItemClick, and so the same two confirmations before any
            // terminateSession.
            _flat = new ObservableCollection<Session>();
#endif
        }

#if LINUX
        private readonly ObservableCollection<Session> _flat;
        private INotifyCollectionChanged _watching;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (ViewModel == null)
            {
                WhenViewModelReady(() => ViewModel != null, () => OnNavigatedTo(e));
                return;
            }

            if (_watching != ViewModel.Items)
            {
                if (_watching != null)
                {
                    _watching.CollectionChanged -= OnGroupsChanged;
                }

                _watching = ViewModel.Items;
                _watching.CollectionChanged += OnGroupsChanged;
            }

            // The assignment lives HERE and not in the constructor: the XAML still carries
            // ItemsSource="{x:Bind ItemsSource.View}", and that binding is evaluated after the
            // constructor returns, so a constructor assignment is silently overwritten by the
            // empty grouped view. Measured: the first attempt at this fix changed nothing on
            // screen for exactly that reason.
            if (ScrollingHost.ItemsSource != _flat)
            {
                ScrollingHost.ItemsSource = _flat;
            }

            Flatten();
        }

        private void OnGroupsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Flatten();
        }

        /// <summary>
        /// Re-lays the grouped view model onto the flat collection the ListView is bound to.
        /// </summary>
        /// <remarks>
        /// Rebuilt wholesale rather than diffed because the view model itself replaces the groups
        /// wholesale (ReplaceWith) whenever getActiveSessions comes back, and the list is a
        /// handful of rows.
        /// </remarks>
        private void Flatten()
        {
            if (ScrollingHost.ItemsSource != _flat)
            {
                ScrollingHost.ItemsSource = _flat;
            }

            _flat.Clear();

            foreach (var group in ViewModel.Items)
            {
                foreach (var session in group)
                {
                    _flat.Add(session);
                }
            }
        }
#endif

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.Terminate(e.ClickedItem as Session);
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

#if LINUX
            // ContentTemplateRoot is always null in Uno for a templated container, so the type
            // test below never matches and every session row would draw the empty SessionCell its
            // template declares - the silent blank-row failure of PORTING.md 6. ProfileTabContainer
            // goes through ContentRoot() and retries until the item template has expanded.
            //
            // No context menu on this list (null): a session is ended by clicking the row, which
            // ItemClick already handles, and that path keeps both its confirmations.
            //
            // UpdateSession is not phased upstream, so there is nothing to inflate here.
            ProfileTabContainer.Bind<SessionCell>(sender, args, null,
                (cell, item) => cell.UpdateSession(item as Session));
#else
            if (args.ItemContainer.ContentTemplateRoot is SessionCell cell)
            {
                cell.UpdateSession(args.Item as Session);
                args.Handled = true;
            }
#endif
        }
    }
}
