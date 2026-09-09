//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Telegram.Common;
using Telegram.Controls.Media;
#if LINUX
// DrawerContainers: the ChoosingItemContainer substitute. It lives in the drawers'
// namespace because u-003 needed it first; it is not drawer-specific.
using Telegram.Controls.Drawers;
#endif
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsThemesPage : HostedPage
    {
        public SettingsThemesViewModel ViewModel => DataContext as SettingsThemesViewModel;

        public SettingsThemesPage()
        {
            InitializeComponent();
            Title = Strings.ColorThemes;
        }

        private async void Switch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio && radio.Tag is ThemeInfoBase info)
            {
                await ViewModel.SetThemeAsync(info);
            }
        }

        #region Binding

        private SolidColorBrush ConvertAccent(IList<ThemeAccentInfo> accents, int index)
        {
            if (accents != null && accents.Count > index)
            {
                return new SolidColorBrush(accents[index].SelectionColor);
            }

            return null;
        }

        #endregion

        #region Context menu

        private void Theme_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var element = sender as FrameworkElement;
            var theme = element.Tag as ThemeInfoBase;

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(ViewModel.CreateTheme, theme, Strings.CreateNewThemeMenu, Icons.Color);

            if (theme is ThemeCustomInfo custom)
            {
                flyout.CreateFlyoutSeparator();
                // CC-2: ViewModel.ShareTheme's ChooseChatsPopup is ported now.
                flyout.CreateFlyoutItem(ViewModel.ShareTheme, custom, Strings.ShareFile, Icons.Share);
                flyout.CreateFlyoutItem(ViewModel.EditTheme, custom, Strings.Edit, Icons.Edit);
                flyout.CreateFlyoutItem(ViewModel.DeleteTheme, custom, Strings.Delete, Icons.Delete, destructive: true);
            }

            flyout.ShowAt(sender, args);
        }

        #endregion

        #region Recycle

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new ListViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = sender.ItemTemplate;
                args.ItemContainer.ContextRequested += Theme_ContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {

#if LINUX
            // OnChoosingItemContainer never runs: ListViewBase.ChoosingItemContainer is
            // NotImplemented on Uno and its add accessor only calls TryRaiseNotImplemented, so the
            // handler is not even stored (PORTING.md 6). Style and ContentTemplate survive because
            // this list declares both in its XAML and the default container path applies them; the
            // ContextRequested hook does not, so it is re-attached here, from the event Uno DOES
            // raise. Without it the right-click menu on a theme is simply gone.
            DrawerContainers.Prepare(args.ItemContainer, Theme_ContextRequested, null);
#endif
            if (args.InRecycleQueue)
            {
                return;
            }

            var theme = args.Item as ThemeInfoBase;
            args.ItemContainer.Tag = theme;

            var root = args.ItemContainer.ContentRoot();
            if (root != null)
            {
                BindRadio(root as RadioButton ?? (root as StackPanel)?.Children[0] as RadioButton, theme);
            }
#if LINUX
            else
            {
                // ContentRoot() answers null on a brand new container: ContainerContentChanging
                // fires from PrepareContainerForIndex, before the container enters the visual tree,
                // which is also when Uno expands the item template (PORTING.md 6). The built-in
                // themes row called ContentRoot() the same way but unguarded, so on that first pass
                // `radio` stayed null and the very next line (radio.Click -=) threw inside the
                // container-generation loop -- loud enough to leave every swatch in the row unbound,
                // which is why it rendered empty instead of merely delayed. WithContentRoot is the
                // drawers' fix for exactly this race (retry on Loaded, then SizeChanged); nothing
                // here is drawer-specific.
                DrawerContainers.WithContentRoot<FrameworkElement>(sender, args.ItemContainer, theme, later =>
                {
                    BindRadio(later as RadioButton ?? (later as StackPanel)?.Children[0] as RadioButton, theme);
                });
            }
#endif
        }

        private void BindRadio(RadioButton radio, ThemeInfoBase theme)
        {
            if (radio == null || theme == null)
            {
                return;
            }

            radio.Click -= Switch_Click;
            radio.Click += Switch_Click;

            if (theme is ThemeCustomInfo custom)
            {
                radio.RequestedTheme = custom.Parent == TelegramTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
                radio.IsChecked = AppSettings.Appearance[AppSettings.Appearance.RequestedTheme].Type == TelegramThemeType.Custom && string.Equals(AppSettings.Appearance[AppSettings.Appearance.RequestedTheme].Custom, custom.Path, StringComparison.OrdinalIgnoreCase);
            }
            else if (theme is ThemeAccentInfo accent)
            {
                radio.RequestedTheme = accent.Parent == TelegramTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
                radio.IsChecked = AppSettings.Appearance[AppSettings.Appearance.RequestedTheme].Type == accent.Type && AppSettings.Appearance.Accents[accent.Type] == accent.AccentColor;
            }
            else
            {
                radio.RequestedTheme = theme.Parent == TelegramTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
                radio.IsChecked = AppSettings.Appearance[AppSettings.Appearance.RequestedTheme].Type == TelegramThemeType.Classic && AppSettings.Appearance.RequestedTheme == theme.Parent;
            }
        }

        #endregion

        private void Theme_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is RadioButton radio && args.NewValue is ThemeInfoBase theme)
            {
                if (theme is ThemeCustomInfo custom)
                {
                    radio.IsChecked = AppSettings.Appearance[AppSettings.Appearance.RequestedTheme].Type == TelegramThemeType.Custom && string.Equals(AppSettings.Appearance[AppSettings.Appearance.RequestedTheme].Custom, custom.Path, StringComparison.OrdinalIgnoreCase);
                }
                else if (theme is ThemeAccentInfo accent)
                {
                    radio.IsChecked = AppSettings.Appearance[AppSettings.Appearance.RequestedTheme].Type == accent.Type && AppSettings.Appearance.Accents[accent.Type] == accent.AccentColor;
                }
                else
                {
                    radio.IsChecked = AppSettings.Appearance[AppSettings.Appearance.RequestedTheme].Type == TelegramThemeType.Classic && AppSettings.Appearance.RequestedTheme == theme.Parent;
                }
            }
        }
    }
}
