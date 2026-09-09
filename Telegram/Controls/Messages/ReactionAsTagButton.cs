//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views.Popups;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Controls.Messages
{
    public partial class ReactionAsTagButton : ReactionButton
    {
        private MessageTag _tag;

        public ReactionAsTagButton()
        {
#if LINUX
            // The style of this type in Generic.xaml is a win: element, and Uno drops win: elements
            // on every non-Windows platform: the generated Generic for this head has the
            // ReactionButton style and no ReactionAsTagButton style at all (checked in
            // obj/gen/.../Generic_*.cs). Left pointing at its own type this control would come up
            // with NO template, and ReactionButton.OnApplyTemplate would then dereference a null
            // Icon on the first saved message.
            //
            // So it borrows ReactionButton's template, which carries every part this hierarchy
            // asks for (LayoutRoot, Icon, Overlay, Count, and RecentChoosers behind an x:Load) and
            // the same Checked/CheckedPointerOver/CheckedPressed states. What is lost is the tag
            // SILHOUETTE -- the notched ReactionAsTagPath of the win: template, which would drag
            // SavedMessagesTagButton.cs in for a shape. What is gained is everything else the tag
            // is: the label instead of a count, and the tag menu instead of the reaction menu.
            // Same class of degradation, and the same reason, as SettingsRadioButton and
            // SettingsComboBox falling back to the Fluent templates.
            DefaultStyleKey = typeof(ReactionButton);
#else
            DefaultStyleKey = typeof(ReactionAsTagButton);
#endif
        }

        protected override void OnLoaded()
        {
            if (_tag != null)
            {
                _tag.PropertyChanged += OnPropertyChanged;
            }
        }

        protected override void OnUnloaded()
        {
            if (_tag != null)
            {
                _tag.PropertyChanged -= OnPropertyChanged;
            }
        }

        protected override void UpdateInteraction(MessageViewModel message, MessageReaction interaction, bool recycled, bool chosen)
        {
            IsChecked = interaction.IsChosen;

            if (_tag != null)
            {
                _tag.PropertyChanged -= OnPropertyChanged;
            }

            _tag = message.ClientService.GetSavedMessagesTag(interaction.Type);

            if (_tag != null && IsConnected)
            {
                _tag.PropertyChanged += OnPropertyChanged;
            }

            if (string.IsNullOrEmpty(_tag?.Label))
            {
                Count.Visibility = Visibility.Collapsed;
            }
            else
            {
                Count.Visibility = Visibility.Visible;
                Count.Text = _tag.Label;
            }
        }

        private void OnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender == _tag && e.PropertyName == nameof(MessageTag.Label))
            {
                this.BeginOnUIThread(UpdateLabel);
            }
        }

        private void UpdateLabel()
        {
            if (string.IsNullOrEmpty(_tag?.Label))
            {
                Count.Visibility = Visibility.Collapsed;
            }
            else
            {
                Count.Visibility = Visibility.Visible;
                Count.Text = _tag.Label;
            }
        }

        public override void OnContextRequested(ContextRequestedEventArgs args)
        {
            var chosen = _reaction;
            if (chosen != null)
            {
                OnClick(_message, chosen);
            }
        }

        protected override void OnClick(MessageViewModel message, MessageReaction chosen)
        {
            if (!message.ClientService.IsPremium)
            {
                if (message.ClientService.IsPremiumAvailable)
                {
                    message.Delegate.NavigationService.ShowPromo(new PremiumSourceFeature(new PremiumFeatureSavedMessagesTags()));
                }

                return;
            }

            var flyout = new MenuFlyout();

            var tag = _message.ClientService.GetSavedMessagesTag(chosen.Type);

            var edit = string.IsNullOrEmpty(tag?.Label)
                ? Strings.SavedTagLabelTag
                : Strings.SavedTagRenameTag;

#if !LINUX
            var selected = _message.Delegate.SavedMessagesTag;

            if (selected == null || !chosen.Type.AreTheSame(selected))
            {
                flyout.CreateFlyoutItem(FilterByTag, Strings.SavedTagFilterByTag, Icons.TagFilter);
            }
#else
            // "Filter by tag" is deliberately NOT offered here, and FilterByTag below is dead code
            // under LINUX for that reason. The filter itself works -- SavedMessagesTag goes through
            // MessageDelegate to Search.SavedMessagesTag and DialogViewModel reloads the slice with
            // SearchSavedMessages -- but the only way OUT of that state is the chat search bar, and
            // ChatSearchBar is a phase-1 stub whose Update() is empty and whose element is
            // Collapsed, so ViewModel.Search stays non-null and Escape never clears it. A filter
            // that engages and cannot be lifted is worse than a menu entry that is not there.
            // It comes back with the search bar. Rename and Remove are live and are the two
            // everyday tag operations.
#endif

            flyout.CreateFlyoutItem(RenameTag, edit, Icons.TagEdit);
            flyout.CreateFlyoutSeparator();
            flyout.CreateFlyoutItem(RemoveTag, Strings.SavedTagRemoveTag, Icons.TagOff, destructive: true);

            FlyoutPlacementMode placement;
            if (Parent is FrameworkElement parent)
            {
                var transform = TransformToVisual(parent);
                var point = transform.TransformPoint(new Windows.Foundation.Point());

                placement = point.X < (parent.ActualWidth - (point.X + ActualWidth))
                        ? FlyoutPlacementMode.BottomEdgeAlignedLeft
                        : FlyoutPlacementMode.BottomEdgeAlignedRight;
            }
            else
            {
                placement = FlyoutPlacementMode.BottomEdgeAlignedLeft;
            }

            flyout.ShowAt(this, placement);
        }

        private void FilterByTag()
        {
            var chosen = _reaction;
            if (chosen == null)
            {
                return;
            }

            _message.Delegate.SavedMessagesTag = chosen.Type;
        }

        private async void RenameTag()
        {
            var chosen = _reaction;
            if (chosen == null || !_message.ClientService.TryGetSavedMessagesTag(chosen.Type, out MessageTag tag))
            {
                return;
            }

            var popup = new InputPopup();
            popup.Title = string.IsNullOrEmpty(tag.Label) ? Strings.SavedTagLabelTag : Strings.SavedTagRenameTag;
            popup.Header = Strings.SavedTagLabelTagText;
            popup.PlaceholderText = Strings.SavedTagLabelPlaceholder;
            popup.Text = tag.Label;
            popup.MinLength = 0;
            popup.MaxLength = 12;
            popup.IsPrimaryButtonEnabled = true;
            popup.IsSecondaryButtonEnabled = true;
            popup.PrimaryButtonText = Strings.Save;
            popup.SecondaryButtonText = Strings.Cancel;

            var confirm = await popup.ShowQueuedAsync(XamlRoot);
            if (confirm == ContentDialogResult.Primary)
            {
                _message.ClientService.Send(new SetSavedMessagesTagLabel(chosen.Type, popup.Text));
            }
        }

        private void RemoveTag()
        {
            var chosen = _reaction;
            if (chosen == null)
            {
                return;
            }

            _message.ClientService.Send(new RemoveMessageReaction(_message.ChatId, _message.Id, chosen.Type));
        }
    }
}
