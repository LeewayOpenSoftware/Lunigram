//
// Copyright Fela Ameghino & Contributors 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// "Who has seen my story", written as Linux-head code.
//
// Upstream's Views/Stories/Popups/StoryInteractionsPopup.xaml(.cs) is not in the compiled subset,
// and it would need a rewrite anyway: its list binds through ChoosingItemContainer (which Uno
// never raises) and reads ContentTemplateRoot (always null for a templated container) -- both
// PORTING.md rule 6. Rather than port the two traps and then undo them, this popup builds its rows
// by hand into a StackPanel inside a ScrollViewer. A story's viewer list is tens of rows at most,
// so there is nothing here that virtualization would buy.
//
// The data is getStoryInteractions, which TDLib only answers for stories posted by the current
// user, so this is only ever opened from StoryInteractionBar (your own story).
//
// AND IT IS NOT A POPUP, which cost one run to find out. The first version was a ContentPopup and
// it opened correctly - Opened fired, getStoryInteractions came back with total_count - and then
// did not paint a single pixel: the story viewer's own OverlayWindow stayed on top of it. A second
// popup root opened over the first one loses. So this is a plain control that StoriesWindow puts
// into its OWN visual tree with a high ZIndex, which is also what lets it suspend the story
// underneath while the list is up.
//
using System;
using System.Collections.Generic;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Stories
{
    public sealed partial class StoryViewersOverlay : UserControl
    {
        private readonly StoryViewModel _story;
        private readonly StackPanel _rows;
        private readonly TextBlock _empty;
        private readonly TextBlock _title;

        public event EventHandler CloseRequested;

        public StoryViewersOverlay(StoryViewModel story)
        {
            _story = story;

            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;

            _title = new TextBlock
            {
                Text = Locale.Declension(Strings.R.Views, 0),
                FontSize = 18,
                Margin = new Thickness(12, 10, 12, 10),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            };

            _rows = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            _empty = new TextBlock
            {
                Text = Strings.Loading,
                Margin = new Thickness(12, 12, 12, 12),
                Opacity = 0.6,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            };

            var list = new StackPanel();
            list.Children.Add(_empty);
            list.Children.Add(_rows);

            var scroller = new ScrollViewer
            {
                Content = list,
                MaxHeight = 380,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var close = new GlyphButton
            {
                Name = "StoryViewersClose",
                Glyph = Icons.Dismiss,
                Width = 36,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0)
            };
            close.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);

            var header = new Grid();
            header.Children.Add(_title);
            header.Children.Add(close);

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(scroller);

            var card = new Border
            {
                Name = "StoryViewersCard",
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1C)),
                CornerRadius = new CornerRadius(12),
                Width = 360,
                Child = body,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Tapping outside the card closes it, the way a light-dismiss popup would.
            var scrim = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x00, 0x00, 0x00))
            };
            scrim.Tapped += (s, e) =>
            {
                e.Handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
            };

            var root = new Grid();
            root.Children.Add(scrim);
            root.Children.Add(card);

            Content = root;
        }

        public async System.Threading.Tasks.Task LoadAsync()
        {
            var story = _story;
            if (story?.ClientService == null)
            {
                return;
            }

            // getStoryInteractions is answered only for our own stories; anything else comes back
            // as an Error and the list simply stays empty.
            var response = await story.ClientService.SendAsync(new GetStoryInteractions(story.Id, string.Empty, false, false, true, string.Empty, 50));

            if (response is not StoryInteractions interactions)
            {
                Logger.Info(string.Format("stories: getStoryInteractions story={0} -> {1}", story.Id, response?.GetType().Name));

                _empty.Text = Strings.NobodyViews;
                return;
            }

            Logger.Info(string.Format("stories: getStoryInteractions story={0} -> total={1} reactions={2} rows={3}",
                story.Id, interactions.TotalCount, interactions.TotalReactionCount, interactions.Interactions?.Count ?? 0));

            _rows.Children.Clear();

            var count = 0;

            foreach (var interaction in interactions.Interactions ?? Array.Empty<StoryInteraction>())
            {
                _rows.Children.Add(CreateRow(story, interaction));
                count++;
            }

            _empty.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
            _empty.Text = count > 0 ? string.Empty : Strings.NobodyViews;

            _title.Text = Locale.Declension(Strings.R.Views, interactions.TotalCount);
        }

        private FrameworkElement CreateRow(StoryViewModel story, StoryInteraction interaction)
        {
            var grid = new Grid
            {
                Height = 48,
                Margin = new Thickness(12, 0, 12, 0)
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var photo = new ProfilePicture
            {
                Width = 36,
                Height = 36,
                Size = 36,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Source = ProfilePictureSource.MessageSender(story.ClientService, interaction.ActorId)
            };

            Grid.SetColumn(photo, 0);
            grid.Children.Add(photo);

            var name = new TextBlock
            {
                Text = story.ClientService.GetTitle(interaction.ActorId),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var side = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.6,
                Margin = new Thickness(8, 0, 0, 0)
            };

            if (interaction.Type is StoryInteractionTypeView view && view.ChosenReactionType is ReactionTypeEmoji emoji)
            {
                side.Text = emoji.Emoji;
                side.Opacity = 1;
            }
            else
            {
                side.Text = Formatter.DateExtended(interaction.InteractionDate);
            }

            Grid.SetColumn(side, 2);
            grid.Children.Add(side);

            return grid;
        }
    }

    /// <summary>
    /// A yes/no asked INSIDE the story viewer.
    ///
    /// Same reason as StoryViewersOverlay: a MessagePopup opened over the viewer's OverlayWindow
    /// opens and does not paint, and a confirmation nobody can see is a delete that never happens.
    /// </summary>
    public sealed partial class StoryConfirmOverlay : UserControl
    {
        public event EventHandler<bool> Answered;

        public StoryConfirmOverlay(string title, string message, string ok, string cancel)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;

            var white = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

            var body = new StackPanel
            {
                Margin = new Thickness(16, 16, 16, 12)
            };

            body.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                Foreground = white,
                Margin = new Thickness(0, 0, 0, 8)
            });

            body.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = white,
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 16)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelButton = new Button
            {
                Name = "StoryConfirmCancel",
                Content = cancel,
                Margin = new Thickness(0, 0, 8, 0)
            };

            cancelButton.Click += (s, e) => Answered?.Invoke(this, false);

            var okButton = new Button
            {
                Name = "StoryConfirmOk",
                Content = ok
            };

            okButton.Click += (s, e) => Answered?.Invoke(this, true);

            buttons.Children.Add(cancelButton);
            buttons.Children.Add(okButton);
            body.Children.Add(buttons);

            var card = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1C)),
                CornerRadius = new CornerRadius(12),
                Width = 340,
                Child = body,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var scrim = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xAA, 0x00, 0x00, 0x00))
            };

            scrim.Tapped += (s, e) =>
            {
                e.Handled = true;
                Answered?.Invoke(this, false);
            };

            var root = new Grid();
            root.Children.Add(scrim);
            root.Children.Add(card);

            Content = root;
        }
    }
}
