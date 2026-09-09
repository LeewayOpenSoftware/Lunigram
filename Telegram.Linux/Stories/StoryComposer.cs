//
// Copyright Fela Ameghino & Contributors 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// POSTING A STORY.
//
// This is NOT a port. Unigram for Windows cannot post a story: `postStory` has zero callers in the
// whole tree, `setStoryPrivacySettings` has zero callers, and Resources.cs carries not one string
// matching AddStory / PostStory / CreateStory / StoryPrivacy / StoryPeriod. So there is no upstream
// behaviour to check this against, and every string here is one this file owns.
//
// The picker goes through Windows.Storage.Pickers.FileOpenPicker, which on this head is Uno's
// LinuxFilePickerExtension -> xdg-desktop-portal (org.freedesktop.portal.FileChooser). The same
// path Platform/ProfilePhotoService.cs already uses.
//
// TDLib rules honoured here, from postStory's own doc comment in the generated schema:
//   * ChatId must be the Saved Messages chat id when posting as yourself -> Options.MyId.
//   * ActivePeriod must be one of 6*3600, 12*3600, 86400, 2*86400 (the last is premium only).
//     "hasta 24 horas" is exactly 86400, and that is what this posts.
//   * IsPostedToChatPage is documented as "keep the story accessible after expiration" -- pinning
//     on the profile and surviving the 24 h are the same switch. Default false: an ephemeral story.
//
// PRIVACY, and this is the part that matters on a real account with real contacts:
// the default selection is SelectedUsers with an EMPTY user list, which really is nobody.
// UserPrivacySettingRule.cpp:342-345 maps storyPrivacySettingsSelectedUsers straight onto
// userPrivacySettingRuleAllowUsers([]) with no empty-list guard -- unlike the Contacts branch at
// :330, which DROPS an empty restrict list and falls through to allow-contacts. So "selected, none
// selected" is the safe option and "contacts" is not.
//
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Stories
{
    public enum StoryAudience
    {
        SelectedNobody,
        CloseFriends,
        Contacts,
        Everyone
    }

    public sealed partial class StoryComposerPopup : ContentPopup
    {
        private readonly IClientService _clientService;

        private readonly TextBlock _file;
        private readonly TextBox _caption;
        private readonly ComboBox _audience;
        private readonly CheckBox _keep;
        private readonly TextBlock _status;

        private string _path;
        private bool _isVideo;

        public StoryComposerPopup(IClientService clientService)
        {
            _clientService = clientService;

            Title = "New story";
            SecondaryButtonText = Strings.Cancel;

            var panel = new StackPanel
            {
                Width = 360
            };

            var pick = new Button
            {
                Name = "StoryPickButton",
                Content = "Choose photo or video…",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8)
            };

            pick.Click += OnPickClick;
            panel.Children.Add(pick);

            _file = new TextBlock
            {
                Text = "No file chosen",
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 12)
            };

            panel.Children.Add(_file);

            _caption = new TextBox
            {
                PlaceholderText = "Caption",
                Margin = new Thickness(0, 0, 0, 12)
            };

            panel.Children.Add(_caption);

            panel.Children.Add(new TextBlock
            {
                Text = "Who can see it",
                Margin = new Thickness(0, 0, 0, 4)
            });

            _audience = new ComboBox
            {
                Name = "StoryAudienceCombo",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 12)
            };

            // Order is deliberate: the closed option is first and is the one selected by default,
            // so a stray Return cannot post to everybody.
            _audience.Items.Add("Nobody (selected contacts, none selected)");
            _audience.Items.Add("Close friends");
            _audience.Items.Add("Contacts");
            _audience.Items.Add("Everyone");
            _audience.SelectedIndex = 0;

            panel.Children.Add(_audience);

            _keep = new CheckBox
            {
                Content = "Keep on my profile after it expires",
                Margin = new Thickness(0, 0, 0, 8)
            };

            panel.Children.Add(_keep);

            _status = new TextBlock
            {
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap
            };

            panel.Children.Add(_status);

            // Post is a plain Button inside the content and NOT the dialog's primary button:
            // holding a ContentDialog open across an await needs a deferral, and PORTING.md
            // records that ContentPopup's own completion path had to be given a second door on
            // this head. A button we own has no such contract.
            _post = new Button
            {
                Name = "StoryPostButton",
                Content = "Post",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 12, 0, 0),
                IsEnabled = false
            };

            _post.Click += OnPostClick;
            panel.Children.Add(_post);

            Content = panel;

            // Diagnostic, same family as the UNIGRAM_* switches in PORTING.md: the picker is an
            // out-of-process xdg-desktop-portal dialog that a test harness cannot drive, so this
            // pre-selects a file by path. The picker button above stays the real, only user-facing
            // way in - this just spares a human hand during a scripted run.
            var preset = Environment.GetEnvironmentVariable("UNIGRAM_STORY_FILE");
            if (!string.IsNullOrEmpty(preset) && System.IO.File.Exists(preset))
            {
                Select(preset);
                Logger.Info(string.Format("stories: composer preset from UNIGRAM_STORY_FILE {0}", preset));
            }
        }

        private readonly Button _post;

        private void Select(string path)
        {
            _path = path;
            _isVideo = path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

            _file.Text = System.IO.Path.GetFileName(path);
            _file.Opacity = 1;

            _post.IsEnabled = true;
        }

        private static readonly string[] _types = new[]
        {
            ".jpg", ".jpeg", ".png", ".webp", ".mp4"
        };

        private async void OnPickClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

                foreach (var type in _types)
                {
                    picker.FileTypeFilter.Add(type);
                }

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                Select(file.Path);

                Logger.Info(string.Format("stories: composer picked {0} (video={1})", _path, _isVideo));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                _status.Text = ex.Message;
            }
        }

        /// <summary>
        /// The story TDLib answered with, or null if it refused.
        /// </summary>
        public Story Result { get; private set; }

        private async void OnPostClick(object sender, RoutedEventArgs e)
        {
            _post.IsEnabled = false;

            // PostAsync does file I/O plus a TDLib round trip; either can throw. This used to
            // assign straight into Result with nothing around it, so a throw skipped both the
            // Hide-on-success branch AND the re-enable-on-failure branch -- the button stayed
            // disabled for the rest of the dialog's life with no way to retry and no signal why.
            // Async void: nothing above this method catches that throw, so the recovery has to
            // live here. Same catch shape as OnPickClick above.
            Story result = null;
            try
            {
                result = await PostAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                _status.Text = ex.Message;
            }
            finally
            {
                if (result == null)
                {
                    _post.IsEnabled = true;
                }
            }

            Result = result;
            if (result != null)
            {
                Hide(ContentDialogResult.Primary);
            }
        }

        private async Task<Story> PostAsync()
        {
            if (string.IsNullOrEmpty(_path))
            {
                return null;
            }

            InputStoryContent content = _isVideo
                ? new InputStoryContentVideo(new InputFileLocal(_path), Array.Empty<int>(), 0, 0, false)
                : new InputStoryContentPhoto(new InputFileLocal(_path), Array.Empty<int>());

            var privacy = BuildPrivacy(Audience);

            var caption = string.IsNullOrWhiteSpace(_caption.Text)
                ? null
                : new FormattedText(_caption.Text, Array.Empty<TextEntity>());

            // Saved Messages chat id == our own user id, which is what postStory documents for
            // "on behalf of the current user".
            var chatId = _clientService.Options.MyId;

            _status.Text = "Posting…";

            Logger.Info(string.Format("stories: postStory chat={0} content={1} privacy={2} activePeriod=86400 postedToChatPage={3}",
                chatId, content.GetType().Name, privacy.GetType().Name, _keep.IsChecked == true));

            var response = await _clientService.SendAsync(new PostStory(
                chatId,
                content,
                null,
                caption,
                privacy,
                Array.Empty<int>(),
                86400,
                null,
                _keep.IsChecked == true,
                false));

            if (response is Story story)
            {
                Logger.Info(string.Format("stories: postStory -> Story id={0} posterChatId={1} isBeingPosted={2} canBeDeleted={3}",
                    story.Id, story.PosterChatId, story.IsBeingPosted, story.CanBeDeleted));

                return story;
            }

            if (response is Error error)
            {
                Logger.Error(string.Format("stories: postStory -> Error {0} {1}", error.Code, error.Message));
                _status.Text = string.Format("{0}: {1}", error.Code, error.Message);
            }
            else
            {
                _status.Text = "No answer";
            }

            return null;
        }

        public StoryAudience Audience => _audience.SelectedIndex switch
        {
            1 => StoryAudience.CloseFriends,
            2 => StoryAudience.Contacts,
            3 => StoryAudience.Everyone,
            _ => StoryAudience.SelectedNobody
        };

        public static StoryPrivacySettings BuildPrivacy(StoryAudience audience)
        {
            return audience switch
            {
                StoryAudience.Everyone => new StoryPrivacySettingsEveryone(Array.Empty<long>()),
                StoryAudience.Contacts => new StoryPrivacySettingsContacts(Array.Empty<long>()),
                StoryAudience.CloseFriends => new StoryPrivacySettingsCloseFriends(),
                // The default, and the only one that reaches nobody at all.
                _ => new StoryPrivacySettingsSelectedUsers(Array.Empty<long>())
            };
        }

        /// <summary>
        /// Opens the composer and, if a story was posted, reloads the active story list so the new
        /// story shows up in the strip without waiting for a push.
        /// </summary>
        public static async Task<Story> ComposeAsync(IClientService clientService, XamlRoot xamlRoot)
        {
            if (clientService == null || xamlRoot == null)
            {
                return null;
            }

            var popup = new StoryComposerPopup(clientService);
            await popup.ShowQueuedAsync(xamlRoot);

            return popup.Result;
        }
    }
}
