//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views.Profile
{
    public sealed partial class ProfileBotsTabPage : ProfileTabPage
    {
        public new ProfileViewModel ViewModel => DataContext as ProfileViewModel;

        public ProfileBotsTabPage()
        {
            InitializeComponent();
        }

        private void ListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is User user)
            {
                ViewModel.OpenSimilarBot(user);
            }
        }

        protected override void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TableListViewItem();
                args.ItemContainer.Style = ScrollingHost.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = ScrollingHost.ItemTemplate;
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
            // See ProfileTabContainer: ChoosingItemContainer never fires, ContentTemplateRoot is
            // null, and there is exactly one phase.
            ProfileTabContainer.Bind<ProfileCell>(sender, args, null,
                (content, item) => content.UpdateSimilarBotInflated(ViewModel.ClientService, item as User));
#else
            else if (args.ItemContainer.ContentTemplateRoot is ProfileCell content)
            {
                content.UpdateSimilarBot(ViewModel.ClientService, args, OnContainerContentChanging);
            }
#endif
        }

        private FormattedText ConvertMoreSimilar(int totalCount)
        {
            var text = string.Format(Strings.MoreSimilarText, "**100**");
            return Extensions.ReplacePremiumLink(text, new PremiumFeatureIncreasedLimits());
        }
    }
}
