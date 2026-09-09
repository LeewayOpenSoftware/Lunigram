//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// The bottom bar of the story viewer: what you can DO with the story you are looking at.
//
// Two shapes, same slot, exactly as upstream splits them:
//
//   * your own story  -> StoryInteractionBar: the heads of the last few viewers, "N views",
//     the reaction tally, and delete. Tapping it opens the "seen by" list.
//   * somebody else's -> StoryReplyBar: a reply field and a heart.
//
// Both are dropped into Telegram.Linux/Stories/StoriesWindow.cs's `InteractionSlot`, the named,
// bottom-aligned Border that batch left empty on purpose for this one ("the interactions batch
// owns those, and this window leaves a named, bottom-aligned slot for them to drop a control
// into" - StoriesWindow.cs:30-32).
//
// WHY THIS IS CODE AND NOT XAML, and why the class names are upstream's: the upstream pair
// (Controls/Stories/StoryInteractionBar.xaml + .xaml.cs, and the composer that lives inline in
// StoriesWindow.xaml) are not in the Linux subset, so the names are free. Taking them keeps any
// shared code that later learns about the bar compiling unchanged, which is the same trick
// StoriesWindow.cs itself plays.
//
// THE ONE PIECE THAT IS NOT UPSTREAM'S, and the reason it had to be rebuilt rather than ported:
// upstream replies to a story through StoriesWindow.xaml's `TextField`, a
// controls:FormattedTextBox - i.e. RichEditBox + ITextDocument, which PORTING.md 6 lists as not
// implemented by Uno Skia (`RichEditBox.Document`). So the field here is a plain TextBox, single
// line, which is the same substitution the composer batch made for the chat box. The four
// composer traps of PORTING.md 6 do NOT apply to it and it is worth writing down why, so nobody
// "fixes" it back into them:
//   (1)+(2)+(3) are all about AcceptsReturn being toggled to keep a multi-line box from eating
//       Enter. This box is single-line and never toggles it, so there is nothing to trip.
//   (4) TDLib drops lone '\r' (MessageEntity.cpp:4217), so the conversion has to happen before
//       ParseMarkdown. On this path it does: ComposeViewModel.SendMessageAsync(string, ...) does
//       `text = text.Replace('\v','\n').Replace('\r','\n')` as its second statement and only
//       calls GetFormattedText(text) -> ParseMarkdown after that. Passing a raw string (not a
//       FormattedText) is therefore the safe overload, and it is the one used below.
//
// WHAT ACTUALLY REACHES TDLIB FROM HERE - three calls, no popups on the way:
//   * the heart     -> SetStoryReaction(posterChatId, storyId, ReactionTypeEmoji("<heart>"), false)
//   * the reply     -> ActiveStoriesViewModel.SendMessageAsync(text), whose GetReply() returns
//                      InputMessageReplyToStory(ChatId, SelectedItem.Id) (ActiveStoriesViewModel
//                      .cs:190) and whose PickMessageSendOptionsAsync is overridden to answer
//                      synchronously (:203) - so it does NOT go through the ContentPopup await
//                      that PORTING.md 6 records as a dead end.
//   * delete        -> DeleteStory(posterChatId, storyId), behind a confirmation.
//
// NOT DONE HERE, and deliberately:
//   * the reaction PICKER (upstream's StoryReactPopup, 632 lines, entered through
//     ReactionsMenuFlyout). The heart is TDLib's own default and needs no picker; the full
//     grid does, and it drags in the emoji drawer that FALTA.md 1.4 prices separately.
//   * stealth mode, forwarding, reporting - none of them is react/reply/seen-by.

using System;
using System.ComponentModel;
using System.Linq;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Stories
{
    /// <summary>
    /// Everything the bottom slot of the viewer can hold, and the wiring that keeps it pointed at
    /// the story currently on screen.
    /// </summary>
    public sealed partial class StoryInteractionsHost : UserControl
    {
        private readonly StoryInteractionBar _mine = new();
        private readonly StoryReplyBar _theirs = new();

        private ActiveStoriesViewModel _author;

        public StoryInteractionsHost()
        {
            Background = null;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Bottom;

            _mine.Visibility = Visibility.Collapsed;
            _theirs.Visibility = Visibility.Collapsed;

            var panel = new Grid();
            panel.Children.Add(_mine);
            panel.Children.Add(_theirs);

            // UserControl and not Border: nothing else in this tree derives from Border, and
            // WinUI's Border was sealed in UWP - whether Uno's is could not be checked without
            // building, so this uses the base every other control here already uses.
            Content = panel;

            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// Raised when the user asks to see who watched their own story. The viewer batch owns the
        /// window, so opening a popup over it is left to whoever attached this host.
        /// </summary>
        public event EventHandler<StoryViewModel> ViewersRequested;

        /// <summary>
        /// Raised after a story of yours has been deleted, so the window can close or step on.
        /// </summary>
        public event EventHandler<StoryViewModel> Deleted;

        /// <summary>
        /// Raised when the delete button is pressed, BEFORE anything is sent. Whoever attached this
        /// host owns the confirmation and the deleteStory call - see the note on OnDeleteClick.
        /// </summary>
        public event EventHandler<StoryViewModel> DeleteRequested;

        /// <summary>
        /// Puts a host into a viewer's empty interaction slot and returns it.
        ///
        /// SEAM, and it is one line: this follows the SELECTED STORY on its own, because
        /// ActiveStoriesViewModel is a BindableBase and raises PropertyChanged for SelectedItem
        /// (ActiveStoriesViewModel.cs:113-118). It does NOT follow the pass between AUTHORS,
        /// because StoriesWindow tracks that in a private `_index` and exposes no event for it.
        /// Until the viewer batch calls Update() from its UpdateButtons(), the bar keeps showing
        /// the author it was last given. See open issues.
        /// </summary>
        public static StoryInteractionsHost Attach(StoriesWindow window)
        {
            if (window?.InteractionSlot is not Border slot)
            {
                return null;
            }

            if (slot.Child is StoryInteractionsHost existing)
            {
                return existing;
            }

            var host = new StoryInteractionsHost();
            slot.Child = host;

            return host;
        }

        /// <summary>
        /// Points the bar at an author. Safe to call with the same author repeatedly.
        /// </summary>
        public void Update(ActiveStoriesViewModel author)
        {
            if (_author == author)
            {
                UpdateStory();
                return;
            }

            if (_author != null)
            {
                _author.PropertyChanged -= OnAuthorPropertyChanged;
            }

            _author = author;

            if (_author != null)
            {
                _author.PropertyChanged += OnAuthorPropertyChanged;
            }

            UpdateStory();
        }

        private void OnAuthorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ActiveStoriesViewModel.SelectedItem) or null or "")
            {
                UpdateStory();
            }
        }

        private void UpdateStory()
        {
            var author = _author;
            var story = author?.SelectedItem;

            if (story == null)
            {
                _mine.Visibility = Visibility.Collapsed;
                _theirs.Visibility = Visibility.Collapsed;
                return;
            }

            // IsMyStory is decided once per author from the chat type, not per story
            // (ActiveStoriesViewModel.cs:46), which is what upstream keys the two bars off too.
            if (author.IsMyStory)
            {
                _theirs.Visibility = Visibility.Collapsed;
                _mine.Visibility = Visibility.Visible;
                _mine.Update(story);
            }
            else
            {
                _mine.Visibility = Visibility.Collapsed;
                _theirs.Visibility = Visibility.Visible;
                _theirs.Update(author, story);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_author != null)
            {
                _author.PropertyChanged -= OnAuthorPropertyChanged;
                _author = null;
            }
        }

        internal void RaiseViewersRequested(StoryViewModel story)
        {
            ViewersRequested?.Invoke(this, story);
        }

        internal void RaiseDeleted(StoryViewModel story)
        {
            Deleted?.Invoke(this, story);
        }

        internal void RaiseDeleteRequested(StoryViewModel story)
        {
            DeleteRequested?.Invoke(this, story);
        }
    }

    /// <summary>
    /// Your own story: who has seen it, how many reacted, and delete.
    ///
    /// Same class name and namespace as Controls/Stories/StoryInteractionBar.xaml.cs, and the same
    /// two public events (ViewersClick, DeleteClick), so shared code that later binds to it needs
    /// no change. The tree is built here instead of in XAML because there is no .xaml half in the
    /// subset to compile against.
    /// </summary>
    public sealed partial class StoryInteractionBar : UserControl
    {
        private readonly RecentUserHeads _viewers;
        private readonly TextBlock _viewersCount;
        private readonly TextBlock _reactionIcon;
        private readonly TextBlock _reactionCount;
        private readonly Button _viewersButton;
        private readonly GlyphButton _deleteButton;

        private StoryViewModel _viewModel;
        public StoryViewModel ViewModel => _viewModel;

        public StoryInteractionBar()
        {
            _viewers = new RecentUserHeads
            {
                ItemSize = 28,
                Margin = new Thickness(4, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            _viewers.RecentUserHeadChanged += OnRecentUserHeadChanged;

            _viewersCount = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(8, 1, 12, 3)
            };

            // Icons.HeartFilled16 and not Icons.Heart: upstream's XAML writes the glyph raw as
            // &#xE985;, and E985 is HeartFilled16 while E987 is the outline Heart the react
            // button below uses. Naming them keeps the two from being swapped.
            _reactionIcon = new TextBlock
            {
                Text = Icons.HeartFilled16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x00, 0x00)),
                FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Telegram.ttf#Telegram"),
                FontSize = 20,
                Visibility = Visibility.Collapsed
            };

            _reactionCount = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(4, 1, 12, 3),
                Visibility = Visibility.Collapsed
            };

            var summary = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0, 0, 0))
            };
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_viewersCount, 1);
            Grid.SetColumn(_reactionIcon, 2);
            Grid.SetColumn(_reactionCount, 3);

            summary.Children.Add(_viewers);
            summary.Children.Add(_viewersCount);
            summary.Children.Add(_reactionIcon);
            summary.Children.Add(_reactionCount);

            _viewersButton = new Button
            {
                // Named so a test harness can aim at it: the delete button is 300px away in the
                // same bar and a click resolved by type or by point can land on the wrong one.
                Name = "StoryViewersButton",
                Content = summary,
                Background = null,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(20),
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(4),
                Padding = new Thickness(0)
            };

            _deleteButton = new GlyphButton
            {
                Name = "StoryDeleteButton",
                Glyph = Icons.Delete,
                CornerRadius = new CornerRadius(20)
            };

            Grid.SetColumn(_deleteButton, 2);

            var root = new Grid
            {
                // Upstream asks for PageSubHeaderBackgroundBrush2, which is an AcrylicBrush
                // (App.xaml:38). Acrylic on Uno Skia is unmeasured in this port - PORTING.md has
                // no entry for it either way - so this uses a flat translucent fill instead. If
                // acrylic turns out to work, this is one line.
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
                CornerRadius = new CornerRadius(24),
                Height = 48
            };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            root.Children.Add(_viewersButton);
            root.Children.Add(_deleteButton);

            _deleteButton.Click += OnDeleteClick;
            _viewersButton.Click += OnViewersClick;

            // UserControl, like upstream's StoryInteractionBar: it owns a Content and needs no
            // ControlTemplate, so there is no generic.xaml entry to add and no
            // ContentTemplateRoot to go looking for (PORTING.md 6).
            Content = root;
        }

        public event RoutedEventHandler ViewersClick;
        public event RoutedEventHandler DeleteClick;

        public void Update(StoryViewModel story)
        {
            _viewModel = story;

            // Bot previews have no viewers and no reactions; upstream hides the whole bar for
            // them (StoryInteractionBar.xaml.cs:41-49) and so does this.
            if (story.ClientService.TryGetUser(story.PosterChatId, out User user) && user.Type is UserTypeBot)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            Visibility = Visibility.Visible;

            var info = story.InteractionInfo;
            if (info != null)
            {
                _viewers.Items.ReplaceDiff(info.RecentViewerUserIds.Select(x => new MessageSenderUser(x)));
                _viewers.Visibility = info.RecentViewerUserIds.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                _viewersCount.Text = info.ViewCount > 0
                    ? Locale.Declension(Strings.R.Views, info.ViewCount)
                    : Strings.NobodyViews;

                _reactionCount.Text = info.ReactionCount.ToString("N0");
                _reactionCount.Visibility =
                    _reactionIcon.Visibility = info.ReactionCount > 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            else
            {
                _viewers.Items.Clear();
                _viewers.Visibility = Visibility.Collapsed;
                _viewersCount.Text = Strings.NobodyViews;
                _reactionCount.Visibility = _reactionIcon.Visibility = Visibility.Collapsed;
            }

            // CanGetInteractions is TDLib's own answer to "is the seen-by list still available",
            // and it goes false once the viewer list expires (StoryManager.cpp:2374,
            // get_story_viewers_expire_date). Upstream leaves the button live regardless; here it
            // is disabled, because a button that opens an empty list is the thing this port keeps
            // being asked not to ship.
            _viewersButton.IsEnabled = story.CanGetInteractions;
            _deleteButton.Visibility = story.CanBeDeleted ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnRecentUserHeadChanged(ProfilePicture sender, MessageSender messageSender)
        {
            var story = _viewModel;
            if (story != null)
            {
                sender.Source = ProfilePictureSource.MessageSender(story.ClientService, messageSender);
            }
        }

        private void OnViewersClick(object sender, RoutedEventArgs e)
        {
            ViewersClick?.Invoke(this, e);

            if (this.GetParent<StoryInteractionsHost>() is StoryInteractionsHost host && _viewModel != null)
            {
                host.RaiseViewersRequested(_viewModel);
            }
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            DeleteClick?.Invoke(this, e);

            var story = _viewModel;
            if (story == null)
            {
                return;
            }

            // The confirmation is NOT a MessagePopup any more. Measured on 2026-08-26: a
            // ContentPopup opened while the story viewer's OverlayWindow is up does open - its
            // Opened fires and its content loads - and then paints nothing, because the viewer's
            // popup root stays on top. A confirmation nobody can see is a delete that never
            // happens. StoriesWindow puts an overlay into its OWN tree instead, and does the
            // deleting from there.
            Logger.Info(string.Format("stories: delete requested for story {0} of chat {1}", story.Id, story.PosterChatId));

            if (this.GetParent<StoryInteractionsHost>() is StoryInteractionsHost host)
            {
                host.RaiseDeleteRequested(story);
            }
        }
    }

    /// <summary>
    /// Somebody else's story: reply, and a heart.
    ///
    /// Upstream has no control by this name - the reply field lives inline in StoriesWindow.xaml
    /// as a FormattedTextBox. That type is RichEditBox-based and PORTING.md 6 lists
    /// RichEditBox.Document as not implemented on Uno Skia, so the field here is a plain TextBox
    /// and the class is new. The send path underneath it is upstream's, untouched.
    /// </summary>
    public sealed partial class StoryReplyBar : UserControl
    {
        private readonly TextBox _field;
        private readonly GlyphButton _sendButton;
        private readonly GlyphButton _reactButton;

        private ActiveStoriesViewModel _author;
        private StoryViewModel _story;

        public StoryReplyBar()
        {
            _field = new TextBox
            {
                // Single line, and it stays single line. See the note at the top of this file for
                // why none of the four AcceptsReturn traps of PORTING.md 6 reach this box.
                AcceptsReturn = false,
                PlaceholderText = Strings.ReplyPrivately,
                Background = null,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            _field.TextChanged += OnTextChanged;
            _field.KeyDown += OnFieldKeyDown;

            _sendButton = new GlyphButton
            {
                Glyph = Icons.Send,
                CornerRadius = new CornerRadius(20),
                Visibility = Visibility.Collapsed
            };
            _sendButton.Click += OnSendClick;

            _reactButton = new GlyphButton
            {
                Glyph = Icons.Heart,
                CornerRadius = new CornerRadius(20)
            };

            _reactButton.Click += OnReactClick;

            var root = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
                CornerRadius = new CornerRadius(24),
                MinHeight = 48
            };
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_field, 0);
            Grid.SetColumn(_sendButton, 1);
            Grid.SetColumn(_reactButton, 2);

            root.Children.Add(_field);
            root.Children.Add(_sendButton);
            root.Children.Add(_reactButton);

            Content = root;
        }

        public void Update(ActiveStoriesViewModel author, StoryViewModel story)
        {
            _author = author;
            _story = story;

            // CanBeReplied is TDLib's, and it is false for channels you cannot write to and for
            // stories whose author blocks replies (StoryViewModel.cs:142).
            _field.Visibility = story.CanBeReplied ? Visibility.Visible : Visibility.Collapsed;
            _sendButton.Visibility = story.CanBeReplied && _field.Text.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateReaction();
        }

        private void UpdateReaction()
        {
            // ChosenReactionType is what TDLib reports back after setStoryReaction, so the button
            // reflects the server, not the click.
            var chosen = _story?.ChosenReactionType is ReactionTypeEmoji;

            _reactButton.Glyph = chosen ? Icons.HeartFilled16 : Icons.Heart;
            _reactButton.Foreground = chosen
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x00, 0x00))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            _sendButton.Visibility = _story is { CanBeReplied: true } && _field.Text.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Enter sends. There is no Shift+Enter newline here because the box is single line;
            // WindowContext.KeyModifiers() is the measured way to read Shift on X11 if that ever
            // changes (PORTING.md 6, and HANDOFF's composer entry).
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Send();
            }
        }

        private void OnSendClick(object sender, RoutedEventArgs e)
        {
            Send();
        }

        private async void Send()
        {
            var author = _author;
            var text = _field.Text;

            if (author == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // Clear first: SendMessageAsync awaits, and a second Enter in the meantime would
            // otherwise send the same text twice.
            _field.Text = string.Empty;

            try
            {
                // The string overload, not the FormattedText one - it is the one that normalises
                // '\r' before ParseMarkdown (ComposeViewModel.cs:1133-1145). GetReply() supplies
                // InputMessageReplyToStory on its own.
                await author.SendMessageAsync(text);
            }
            catch (Exception ex)
            {
                // Same shape as ChatTextBoxSend.Send(): the field is already cleared above on
                // purpose (anti-double-Enter), so a throw here used to lose the reply outright.
                // Async void handler -- nothing above catches this -- so restoring happens here.
                Logger.Error("story reply failed, restoring composed text", ex);
                _field.Text = text;
            }
        }

        private void OnReactClick(object sender, RoutedEventArgs e)
        {
            var story = _story;
            if (story == null)
            {
                return;
            }

            var chosen = story.ChosenReactionType is ReactionTypeEmoji;

            // Same heart and the same update_recent_reactions=false as upstream's channel bar
            // (StoryChannelInteractionBar.xaml.cs:163-167): tapping again clears it.
            // Escaped, not a literal heart: upstream writes it "\u2764\uFE0F" (StoryChannelInteractionBar
            // selector is invisible in an editor, so a copied literal silently loses it and TDLib
            // gets a different reaction than the one the icon shows.
            story.ClientService.Send(new SetStoryReaction(story.PosterChatId, story.Id,
                chosen ? null : new ReactionTypeEmoji("\u2764\uFE0F"), false));
        }
    }
}
