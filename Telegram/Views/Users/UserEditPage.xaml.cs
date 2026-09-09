//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Converters;
using Telegram.Td.Api;
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Drawers;
using Telegram.ViewModels.Users;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Telegram.Views.Users
{
    public sealed partial class UserEditPage : HostedPage, IUserDelegate
    {
        public UserEditViewModel ViewModel => DataContext as UserEditViewModel;

        public UserEditPage()
        {
            InitializeComponent();

            AutomationProperties.SetName(EmojiButton, Strings.Emoji);
        }

#if LINUX
        // WHY THIS EXISTS, and it is not defensive coding: on Uno, FindName is a VISUAL-TREE WALK
        // (IFrameworkElementHelper.FindName, decompiled from the shipped Uno.UI 6.6.184 -- it calls
        // FindLastChild down the children and only then ConvertFromStubToElement). WinUI resolves an
        // x:Load element through the generated code instead, so there it works whenever it is
        // called. Here it can only find a stub that is ALREADY REACHABLE, and it returns null
        // without an error when it is not.
        //
        // UpdateUser runs from UserEditViewModel.OnNavigatedToAsync, i.e. during navigation, before
        // HeaderedControl has applied its template and realised its items. PhotoPanel and NotePanel
        // are direct children of SettingsPanel and were found; LastName is nested inside LayoutRoot's
        // ItemsPresenter and was NOT -- so a contact's SURNAME was invisible and uneditable, and the
        // 96px ProfilePicture beside it stayed blank because its Source was assigned to a control
        // whose template had not been applied either.
        //
        // Measured, not deduced: the visual-tree dump of the running page showed the top Grid with
        // ProfilePicture #Photo and TextBox #FirstName and no LastName at all, on a page that had
        // been on screen for a minute, and that ProfilePicture's Border with no ImageBrush while
        // every working avatar in the same dump carries `bg=ImageBrush`. A build cannot see any of
        // this: FindName returning null is not an error, which is why it had to be driven.
        //
        // Waiting for Loaded is NOT enough and that was measured too -- the first attempt did
        // exactly that and changed nothing on screen. Uno applies a control template during MEASURE,
        // and realises an ItemsPresenter's items later still, so the first pass that can satisfy
        // this walk is a completed LAYOUT pass. Hence LayoutUpdated, and hence the retry: the first
        // pass to complete is not necessarily the one that realised this subtree.
        private const int ReplayPassesLinux = 12;

        private Action _pendingUpdateLinux;
        private int _replayPassesLinux;
        private bool _replayedLinux;

        private void OnLayoutUpdatedLinux(object sender, object e)
        {
            // Materialised? Then run the real update and stop watching. LastName is the canary: it
            // is the deepest of the three x:Load elements this page resolves by name.
            if (FindName(nameof(LastName)) != null || ++_replayPassesLinux >= ReplayPassesLinux)
            {
                LayoutUpdated -= OnLayoutUpdatedLinux;

                var pending = _pendingUpdateLinux;
                _pendingUpdateLinux = null;

                Logger.Info($"UserEditPage: replaying UpdateUser after {_replayPassesLinux} layout pass(es), LastName={(LastName == null ? "null" : "materialised")}");

                pending?.Invoke();
            }
        }
#endif

        #region Delegate

        public void UpdateUser(Chat chat, User user, UserFullInfo fullInfo, bool secret, bool accessToken)
        {
#if LINUX
            if (!_replayedLinux)
            {
                // Deferred unconditionally, not `if (!IsLoaded)`: IsLoaded is already true by the
                // time navigation calls this, and that is precisely why the Loaded-only version of
                // this guard did nothing.
                _replayedLinux = true;
                _pendingUpdateLinux = () => UpdateUser(chat, user, fullInfo, secret, accessToken);
                LayoutUpdated += OnLayoutUpdatedLinux;
                return;
            }
#endif
            Photo.Source = ProfilePictureSource.User(ViewModel.ClientService, user);

            if (user.Type is UserTypeBot userTypeBot && userTypeBot.CanBeEdited)
            {
                FindName(nameof(BotPhoto));

                FindName(nameof(About));

                FindName(nameof(UsernamePanel));
                FindName(nameof(BotPanel));

#if LINUX
                // Two of UsernamePanel's three entries lead to screens that are not in the Linux
                // subset (UserAffiliatePage and ChatStarsPage, see UserEditViewModel), so they are
                // collapsed rather than drawn: a button that opens nothing is worse than no button.
                // Username itself stays -- SettingsUsernamePopup is in. This whole branch is only
                // reached for a bot you own, which is not why the page was ported; the contact
                // branch below is.
                AffiliateProgram.Visibility = Visibility.Collapsed;
                Stars.Visibility = Visibility.Collapsed;
#endif

                if (fullInfo?.BotInfo?.VerificationParameters != null)
                {
                    FindName(nameof(BotPanel2));
                }
                else
                {
                    BotPanel2?.Visibility = Visibility.Collapsed;
                }

                LayoutRoot.Footer = string.Empty;

                Username.Content = Strings.BotPublicLink;
                Username.Badge = MeUrlPrefixConverter.Convert(ViewModel.ClientService, user.ActiveUsername(), true);

                FirstName.PlaceholderText = Strings.BotName;
                FirstName.VerticalAlignment = VerticalAlignment.Center;
                FirstName.Margin = new Thickness();

                Grid.SetRowSpan(FirstName, 2);
            }
            else
            {
                FindName(nameof(PhotoPanel));
                FindName(nameof(LastName));

                if (NotePanel == null)
                {
                    FindName(nameof(NotePanel));

                    EmojiPanel.DataContext = EmojiDrawerViewModel.Create(ViewModel.Session);
                    NoteField.AllowedEntities = FormattedTextEntity.Bold | FormattedTextEntity.Italic | FormattedTextEntity.Underline | FormattedTextEntity.Strikethrough | FormattedTextEntity.Spoiler | FormattedTextEntity.CustomEmoji;
                    NoteField.CustomEmoji = CustomEmoji;
                    NoteField.MaxLength = (int)ViewModel.ClientService.Options.UserNoteTextLengthMax;
                }

                SuggestPhoto.Content = string.Format(Strings.SuggestPhotoFor, user.FirstName);
                PersonalPhoto.Content = string.Format(Strings.SetPhotoFor, user.FirstName);
            }

            if (fullInfo == null)
            {
                return;
            }

            NoteField?.SetText(fullInfo.Note);

            if (ResetPhoto != null)
            {
                if (fullInfo.PersonalPhoto != null)
                {
                    ResetPhotoPhoto.Source = ProfilePictureSource.ChatPhoto(ViewModel.ClientService, user, fullInfo.Photo, false);
                    ResetPhotoPhoto.Visibility = Visibility.Visible;
                    ResetPhoto.Visibility = Visibility.Visible;
                }
                else
                {
                    ResetPhotoPhoto.Visibility = Visibility.Collapsed;
                    ResetPhoto.Visibility = Visibility.Collapsed;
                }

                SuggestPhoto.Visibility = fullInfo.OutgoingPaidMessageStarCount > 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                SuggestBirthday.Visibility = fullInfo.OutgoingPaidMessageStarCount > 0 || fullInfo.Birthdate != null
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (fullInfo.NeedPhoneNumberPrivacyException)
            {
                FindName(nameof(SharePhonePanel));

                SharePhoneCheck.Content = string.Format(Strings.SharePhoneNumberWith, user.FirstName);
            }

            if (fullInfo.BotInfo?.AffiliateProgram != null)
            {
                AffiliateProgram.Badge = fullInfo.BotInfo.AffiliateProgram.Parameters.CommissionPercent();
            }
            else
            {
                AffiliateProgram?.Badge = Strings.AffiliateProgramBotOff;
            }
        }

        public void UpdateUserStatus(Chat chat, User user) { }

        #endregion

        public string ConvertStarCount(StarAmount amount)
        {
            if (amount != null)
            {
                return amount.ToValue();
            }

            return null;
        }

        private void NoteField_TextChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Note = NoteField.GetFormattedText();
        }

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            // We don't want to unfocus the text are when the context menu gets opened
            EmojiPanel.ViewModel.Update();
            EmojiFlyout.ShowAt(sender as FrameworkElement, new FlyoutShowOptions
            {
                ShowMode = FlyoutShowMode.Transient,
                Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
            });
        }

        private void Emoji_ItemClick(object sender, Controls.Drawers.EmojiDrawerItemClickEventArgs e)
        {
            if (e.ClickedItem is EmojiData emoji)
            {
                NoteField.InsertText(emoji.Value);
            }
            else if (e.ClickedItem is StickerViewModel sticker)
            {
                NoteField.InsertEmoji(sticker);
            }

            NoteField.Focus(FocusState.Programmatic);
        }
    }
}
