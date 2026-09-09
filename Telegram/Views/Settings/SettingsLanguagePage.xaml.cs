//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#if LINUX
using Microsoft.UI.Xaml.Controls.Primitives;
#endif
using Microsoft.UI.Xaml.Input;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsLanguagePage : HostedPage
    {
        public SettingsLanguageViewModel ViewModel => DataContext as SettingsLanguageViewModel;

        public SettingsLanguagePage()
        {
            InitializeComponent();
            Title = Strings.Language;
        }

        private void RadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: LanguagePackInfo language })
            {
                ViewModel.Change(language);
            }
        }

        #region Context menu

        private void Language_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var info = ScrollingHost.ItemFromContainer(sender) as LanguagePackInfo;
            if (info.IsInstalled is false)
            {
                return;
            }

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(ViewModel.Delete, info, Strings.Delete, Icons.Delete, destructive: true);
            flyout.ShowAt(sender, args);
        }

        #endregion

        #region Recycle

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += Language_ContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

#if LINUX
            if (args.Item is not LanguagePackInfo language)
            {
                return;
            }

            // Uno never raises ChoosingItemContainer (PORTING.md 6), so OnChoosingItemContainer
            // above is dead here and the container arrives from
            // TableListView.GetContainerForItemOverride instead. Two of the three things that
            // handler did are done anyway - the style and the item template come from the
            // ListView itself - but the third is not: nothing subscribes ContextRequested, so the
            // "delete this language pack" menu would never open. Same hole TopNavView had, and on
            // Windows it is invisible because ChoosingItemContainer covers it.
            // The -= before the += is not defensive noise: containers are recycled, and this runs
            // once per (container, item) pair.
            args.ItemContainer.ContextRequested -= Language_ContextRequested;
            args.ItemContainer.ContextRequested += Language_ContextRequested;

            // ContentTemplateRoot is always null in Uno for anything that has a control template,
            // which every ListViewItem has (PORTING.md 6). ContentRoot() walks down to the
            // presenter that actually expanded the item template - but it can only answer once the
            // container is in the visual tree, and ContainerContentChanging runs before that, from
            // PrepareContainerForIndex. Hence the retry on Loaded: without it every row would draw
            // the two empty TextBlocks the DataTemplate declares, which is the same silent miss
            // that emptied the chat list.
            if (TryBindLanguage(args.ItemContainer, language) is false)
            {
                // Note the parameter is not called "sender": a local function may not shadow a
                // parameter of the enclosing method (CS0136), and this one already has one.
                void OnContainerLoaded(object element, RoutedEventArgs e)
                {
                    if (element is SelectorItem container)
                    {
                        container.Loaded -= OnContainerLoaded;

                        // The container is recycled, so by the time Loaded arrives it may already
                        // be showing a different language: ask the list, not the closure.
                        if (ScrollingHost.ItemFromContainer(container) is LanguagePackInfo current)
                        {
                            TryBindLanguage(container, current);
                        }
                    }
                }

                args.ItemContainer.Loaded += OnContainerLoaded;
            }

            args.Handled = true;
#else
            if (args.ItemContainer.ContentTemplateRoot is RadioButton content && args.Item is LanguagePackInfo language)
            {
                content.Checked -= RadioButton_Checked;

                // Justified because Checked
                content.Tag = language;
                content.IsChecked = language == ViewModel.SelectedItem;

                content.Checked += RadioButton_Checked;

                var grid = content.Content as Grid;
                if (grid != null)
                {
                    var nativeName = grid.Children[0] as TextBlock;
                    var name = grid.Children[1] as TextBlock;

                    nativeName.Text = language.NativeName;
                    name.Text = language.Name;
                }

                args.Handled = true;
            }
#endif
        }

#if LINUX
        private bool TryBindLanguage(SelectorItem container, LanguagePackInfo language)
        {
            if (container.ContentRoot() is not RadioButton content)
            {
                return false;
            }

            content.Checked -= RadioButton_Checked;

            // Justified because Checked
            content.Tag = language;
            content.IsChecked = language == ViewModel.SelectedItem;

            content.Checked += RadioButton_Checked;

            if (content.Content is Grid grid && grid.Children.Count > 1)
            {
                var nativeName = grid.Children[0] as TextBlock;
                var name = grid.Children[1] as TextBlock;

                nativeName.Text = language.NativeName;
                name.Text = language.Name;
            }

            return true;
        }
#endif

        #endregion

        #region Binding

        private Visibility ConvertDoNotTranslate(bool messages, bool chats)
        {
            return messages || chats
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        #endregion
    }
}
