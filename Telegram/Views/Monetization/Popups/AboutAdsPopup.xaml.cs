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
using Telegram.ViewModels;
using Telegram.Views.Popups;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Telegram.Views.Monetization.Popups
{
    public sealed partial class AboutAdsPopup : ContentPopup
    {
        private readonly string _value;
        private readonly string _url;

        private readonly DialogViewModel _viewModel;
        private readonly SponsoredMessage _message;

        public AboutAdsPopup(DialogViewModel viewModel, SponsoredMessage message)
        {
            InitializeComponent();

            _viewModel = viewModel;
            _message = message;

            //Icon.Source = new LocalFileSource($"ms-appx:///Assets/Animations/CollectibleUsername.tgs");

            TextBlockHelper.SetMarkdown(LongInfo, string.Format(Strings.RevenueSharingAdsInfo4Subtitle2, string.Empty));//string.Format("[{0}]({1})", Strings.RevenueSharingAdsInfo4SubtitleLearnMore.Replace("**", string.Empty), Strings.PromoteUrl)));

            // PORTING.md §6, measured by u-021: Uno's XAML generator loses the ATTACHED nature of
            // `owner:Type.Property` when the value is a custom markup extension -- it emits an
            // instance property set on the target element, so `common:TextBlockHelper.Markdown=
            // "{CustomResource X}"` comes out as `textBlock.Markdown = ...` and fails with CS1061.
            // A literal or a {TemplateBinding} in the same attribute resolves correctly, which is
            // how the probe pinned it down. These four (and CocoonAboutPopup's one) are set from
            // here instead -- which is what upstream already does one line above for LongInfo, so
            // this is that same idiom applied four more times, not a new one.
            TextBlockHelper.SetMarkdown(Info1, Strings.RevenueSharingAdsInfo1Subtitle);
            TextBlockHelper.SetMarkdown(Info2, Strings.RevenueSharingAdsInfo2Subtitle);
            TextBlockHelper.SetMarkdown(Info3, Strings.RevenueSharingAdsInfo3SubtitleBot);
        }

        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {

        }

        private void Learn_Click(object sender, RoutedEventArgs e)
        {
            Hide(ContentDialogResult.Primary);
            MessageHelper.OpenUrl(null, null, _url);
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            Hide(ContentDialogResult.Secondary);
            MessageHelper.CopyText(XamlRoot, _value);
        }

        private void More_ContextRequested(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            flyout.CreateFlyoutItem(SponsorInfo, Strings.SponsoredMessageSponsorReportable, Icons.Megaphone);
            flyout.CreateFlyoutSeparator();

            if (_message.CanBeReported)
            {
                flyout.CreateFlyoutItem(ReportAd, Strings.ReportAd, Icons.HandRight);
            }

            flyout.CreateFlyoutItem(HideAd, Strings.HideAd, Icons.DismissCircle);

            flyout.ShowAt(sender as UIElement, FlyoutPlacementMode.BottomEdgeAlignedRight);
        }

        private void SponsorInfo()
        {

        }

        private void ReportAd()
        {
            Hide();
            _viewModel.ShowPopup(new ReportAdsPopup(_viewModel, _viewModel.Chat.Id, _message.MessageId, null));
        }

        private void HideAd()
        {
            Hide();
            _viewModel.HideSponsoredMessage();
        }
    }
}
