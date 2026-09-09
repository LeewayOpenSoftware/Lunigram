//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
#if !LINUX
using Telegram.Controls.Stories;
#endif
using Telegram.Converters;
using Telegram.Native;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels;
#if !LINUX
using Telegram.ViewModels.Settings;
using Telegram.ViewModels.Stories;
using Telegram.ViewModels.Supergroups;
#endif
using Telegram.Views;
#if !LINUX
using Telegram.Views.Business;
using Telegram.Views.Chats.Popups;
using Telegram.Views.Create;
using Telegram.Views.Folders;
#endif
// AddFolderPopup (ChatFolderInvite links) and StickersPopup/BackgroundPopup (StickerSet and
// Background links) are in the subset, so these two namespaces resolve on Linux as well.
using Telegram.Views.Folders.Popups;
using Telegram.Views.Host;
using Telegram.Views.Popups;
#if !LINUX
using Telegram.Views.Premium.Popups;
using Telegram.Views.Settings;
using Telegram.Views.Stars.Popups;
#endif
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
#if !LINUX
using Windows.ApplicationModel.Resources.Core;
#endif
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Common
{
    public partial class OpenUrlSource
    {

    }

    public partial class OpenUrlSourceChat : OpenUrlSource
    {
        public long ChatId { get; }

        public MessageSender SenderId { get; }

        public OpenUrlSourceChat(long chatId, MessageSender senderId)
        {
            ChatId = chatId;
            SenderId = senderId;
        }
    }

    public partial class OpenUrlSourceJoinChatRequest : OpenUrlSource
    {
        public long QueryId { get; }

        public long ChatId { get; }

        public OpenUrlSourceJoinChatRequest(long queryId, long chatId)
        {
            QueryId = queryId;
            ChatId = chatId;
        }
    }

    public partial class TonSite
    {
        public static bool TryCreate(IClientService clientService, Uri uri, out string magic)
        {
            magic = null;

            if (string.Equals(uri.Scheme, "tonsite", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".ton", StringComparison.OrdinalIgnoreCase))
            {
                var domain = clientService.Config.GetNamedString("ton_proxy_address", "magic.org");

                magic = uri.Host
                    .Replace("-", "-h")
                    .Replace(".", "-d");
                magic = $"https://{magic}.{domain}" + uri.PathAndQuery + uri.Fragment;
                return true;
            }

            return false;
        }

        public static bool TryUnmask(IClientService clientService, string url, out Uri magic)
        {
            if (MessageHelper.TryCreateUri(url, out Uri navigation))
            {
                magic = Unmask(clientService, navigation);
                return true;
            }

            magic = null;
            return false;
        }

        public static Uri Unmask(IClientService clientService, Uri navigation)
        {
            var domain = clientService.Config.GetNamedString("ton_proxy_address", "magic.org");

            var host = navigation.Host;
            if (host.EndsWith("." + domain))
            {
                host = host.Replace("." + domain, string.Empty)
                    .Replace("-d", ".")
                    .Replace("-h", "-");
            }

            return new Uri("tonsite://" + host + navigation.PathAndQuery + navigation.Fragment);
        }
    }

    public partial class MessageHelper
    {
        public static async void CopyLink(IClientService clientService, XamlRoot xamlRoot, InternalLinkType type)
        {
            var response = await clientService.SendAsync(new GetInternalLink(type, true));
            if (response is HttpUrl httpUrl)
            {
                CopyLink(xamlRoot, httpUrl.Url);
            }
        }

        public static void CopyLink(XamlRoot xamlRoot, string link, bool publiz = true)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(link);
            ClipboardEx.TrySetContent(dataPackage);

            ToastPopup.Show(xamlRoot, publiz ? Strings.LinkCopied : Strings.LinkCopiedPrivate, ToastPopupIcon.LinkCopied);
        }

        public static void CopyText(XamlRoot xamlRoot, string text)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(text);
            ClipboardEx.TrySetContent(dataPackage);

            ToastPopup.Show(xamlRoot, Strings.TextCopied, ToastPopupIcon.Copied);
        }

        public static async void CopyText(XamlRoot xamlRoot, FormattedText text)
        {
            // Callers pass a selection or an optional field: GetSelectedText returns null with
            // nothing selected, and UserFullInfo.Note is null when there is no note. Copying
            // nothing is a no-op, and this method is async void, so a throw here kills the app.
            if (string.IsNullOrEmpty(text?.Text))
            {
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(text.Text);

            var entities = text.Entities.Where(x => x.IsEditable()).ToList();
            if (entities.Count > 0)
            {
                using (var stream = new InMemoryRandomAccessStream())
                {
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteInt32(entities.Count);

                        foreach (var entity in entities)
                        {
                            writer.WriteInt32(entity.Offset);
                            writer.WriteInt32(entity.Length);

                            switch (entity.Type)
                            {
                                case TextEntityTypeBold:
                                    writer.WriteByte(1);
                                    break;
                                case TextEntityTypeItalic:
                                    writer.WriteByte(2);
                                    break;
                                case TextEntityTypeStrikethrough:
                                    writer.WriteByte(3);
                                    break;
                                case TextEntityTypeUnderline:
                                    writer.WriteByte(4);
                                    break;
                                case TextEntityTypeSpoiler:
                                    writer.WriteByte(5);
                                    break;
                                case TextEntityTypeBlockQuote:
                                case TextEntityTypeExpandableBlockQuote:
                                    writer.WriteByte(6);
                                    break;
                                case TextEntityTypeCustomEmoji customEmoji:
                                    writer.WriteByte(7);
                                    writer.WriteInt64(customEmoji.CustomEmojiId);
                                    break;
                                case TextEntityTypeCode:
                                    writer.WriteByte(8);
                                    break;
                                case TextEntityTypePre:
                                    writer.WriteByte(9);
                                    break;
                                case TextEntityTypePreCode preCode:
                                    writer.WriteByte(10);
                                    writer.WriteUInt32(writer.MeasureString(preCode.Language));
                                    writer.WriteString(preCode.Language);
                                    break;
                                case TextEntityTypeTextUrl textUrl:
                                    writer.WriteByte(11);
                                    writer.WriteUInt32(writer.MeasureString(textUrl.Url));
                                    writer.WriteString(textUrl.Url);
                                    break;
                                case TextEntityTypeMentionName mentionName:
                                    writer.WriteByte(12);
                                    writer.WriteInt64(mentionName.UserId);
                                    break;
                                case TextEntityTypeDateTime date:
                                    writer.WriteByte(13);
                                    writer.WriteInt32(date.UnixTime);
                                    if (date.FormattingType is DateTimeFormattingTypeAbsolute absolute)
                                    {
                                        writer.WriteByte(1);
                                        writer.WriteByte(absolute.DatePrecision switch
                                        {
                                            DateTimePartPrecisionLong => 1,
                                            DateTimePartPrecisionShort => 2,
                                            _ => 0
                                        });
                                        writer.WriteByte(absolute.TimePrecision switch
                                        {
                                            DateTimePartPrecisionLong => 1,
                                            DateTimePartPrecisionShort => 2,
                                            _ => 0
                                        });
                                        writer.WriteBoolean(absolute.ShowDayOfWeek);
                                    }
                                    else if (date.FormattingType is DateTimeFormattingTypeRelative)
                                    {
                                        writer.WriteByte(2);
                                    }
                                    else
                                    {
                                        writer.WriteByte(0);
                                    }
                                    break;
                            }
                        }

                        await writer.FlushAsync();
                        await writer.StoreAsync();
                    }

                    stream.Seek(0);
                    dataPackage.SetData("application/x-tl-field-tags", stream.CloneStream());
                }
            }

            ClipboardEx.TrySetContent(dataPackage);

            if (xamlRoot != null)
            {
                ToastPopup.Show(xamlRoot, Strings.TextCopied, ToastPopupIcon.Copied);
            }
        }

        public static async void DragStarting(MessageViewModel message, DragStartingEventArgs args)
        {
            var file = message?.GetFile();
            if (file != null && file.Local.IsDownloadingCompleted && message.CanBeSaved)
            {
                var deferral = args.GetDeferral();

                try
                {
                    var item = await StorageFile.GetFileFromPathAsync(file.Local.Path);

                    args.Data.RequestedOperation = DataPackageOperation.Copy;
                    args.Data.SetStorageItems(new[] { item });

                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                        {
                            writer.WriteInt64(message.ChatId);
                            writer.WriteInt64(message.Id);

                            await writer.FlushAsync();
                            await writer.StoreAsync();
                        }

                        stream.Seek(0);
                        args.Data.SetData("application/x-tl-message", stream.CloneStream());
                    }

                    args.DragUI.SetContentFromDataPackage();
                }
                catch
                {
                    // All the remote procedure calls must be wrapped in a try-catch block
                }
                finally
                {
                    deferral.Complete();
                }
            }
            else
            {
                args.Cancel = true;
            }
        }

        public static async Task<FormattedText> PasteTextAsync(DataPackageView package)
        {
            if (package.AvailableFormats.Contains(StandardDataFormats.Text))
            {
                string text = await package.GetTextAsync();
                MutableVector<TextEntity> entities = null;

                if (package.AvailableFormats.Contains("application/x-tl-field-tags"))
                {
                    var data = await package.GetDataAsync("application/x-tl-field-tags") as IRandomAccessStream;
                    var reader = new DataReader(data.GetInputStreamAt(0));

                    await reader.LoadAsync((uint)data.Size);

                    var count = reader.ReadInt32();
                    entities = new MutableVector<TextEntity>(count);

                    for (int i = 0; i < count; i++)
                    {
                        var entity = new TextEntity
                        {
                            Offset = reader.ReadInt32(),
                            Length = reader.ReadInt32()
                        };

                        var type = reader.ReadByte();

                        switch (type)
                        {
                            case 1:
                                entity.Type = new TextEntityTypeBold();
                                break;
                            case 2:
                                entity.Type = new TextEntityTypeItalic();
                                break;
                            case 3:
                                entity.Type = new TextEntityTypeStrikethrough();
                                break;
                            case 4:
                                entity.Type = new TextEntityTypeUnderline();
                                break;
                            case 5:
                                entity.Type = new TextEntityTypeSpoiler();
                                break;
                            case 6:
                                entity.Type = new TextEntityTypeBlockQuote();
                                break;
                            case 7:
                                entity.Type = new TextEntityTypeCustomEmoji(reader.ReadInt64());
                                break;
                            case 8:
                                entity.Type = new TextEntityTypeCode();
                                break;
                            case 9:
                                entity.Type = new TextEntityTypePre();
                                break;
                            case 10:
                                entity.Type = new TextEntityTypePreCode(reader.ReadString(reader.ReadUInt32()));
                                break;
                            case 11:
                                entity.Type = new TextEntityTypeTextUrl(reader.ReadString(reader.ReadUInt32()));
                                break;
                            case 12:
                                entity.Type = new TextEntityTypeMentionName(reader.ReadInt64());
                                break;
                            case 13:
                                {
                                    // The precisions are written DATE first, while the type takes
                                    // TIME first — read them into locals so they don't get swapped.
                                    var unixTime = reader.ReadInt32();
                                    var formattingType = reader.ReadByte();

                                    if (formattingType == 1)
                                    {
                                        var date = ReadDateTimePartPrecision(reader);
                                        var time = ReadDateTimePartPrecision(reader);

                                        entity.Type = new TextEntityTypeDateTime(unixTime, new DateTimeFormattingTypeAbsolute(time, date, reader.ReadBoolean()));
                                    }
                                    else
                                    {
                                        entity.Type = new TextEntityTypeDateTime(unixTime, formattingType == 2 ? new DateTimeFormattingTypeRelative() : null);
                                    }
                                }
                                break;
                        }

                        entities.Add(entity);
                    }
                }

                return new FormattedText(text, entities ?? Array.Empty<TextEntity>());
            }

            return null;
        }

        private static DateTimePartPrecision ReadDateTimePartPrecision(DataReader reader)
        {
            return reader.ReadByte() switch
            {
                1 => new DateTimePartPrecisionLong(),
                2 => new DateTimePartPrecisionShort(),
                _ => new DateTimePartPrecisionNone()
            };
        }

        public static bool AreTheSame(string bae, string url, out string fragment)
        {
            if (TryCreateUri(bae, out Uri current) && TryCreateUri(url, out Uri result))
            {
                fragment = result.Fragment.Length > 0 ? result.Fragment?.Substring(1) : null;
                return fragment != null && Uri.Compare(current, result, UriComponents.Host | UriComponents.PathAndQuery, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
            }

            fragment = null;
            return false;
        }

        public static bool TryCreateUri(string url, out Uri uri)
        {
            if (url == null)
            {
                uri = null;
                return false;
            }

            if (!url.StartsWith("http://")
                && !url.StartsWith("https://")
                && !url.StartsWith("tg:")
                && !url.StartsWith("tonsite:")
                && !url.StartsWith("ftp:")
                && !url.StartsWith("mailto:"))
            {
                url = "https://" + url;
            }

            return Uri.TryCreate(url, UriKind.Absolute, out uri);
        }

        public static bool IsTelegramUrl(Uri uri)
        {
            var host = uri.Host;

            var splitHostName = uri.Host.Split('.');
            if (splitHostName.Length >= 2)
            {
                host = splitHostName[^2] + "." +
                       splitHostName[^1];
            }

            if (Constants.TelegramHosts.Contains(host))
            {
                return string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            }

            return IsTelegramScheme(uri);
        }

        public static bool IsTelegramScheme(Uri uri)
        {
            return string.Equals(uri.Scheme, "tg", StringComparison.OrdinalIgnoreCase);
        }

        public static async void OpenTelegramUrl(IClientService clientService, INavigationService navigation, Uri uri, OpenUrlSource source = null)
        {
            var url = uri.ToString();
            if (url.Contains("telegra.ph"))
            {
#if LINUX
                // Instant View is outside the Linux subset; the article opens in the browser.
                OpenUrl(null, null, url);
#else
                navigation.NavigateToInstant(url);
#endif
                return;
            }

            var response = await clientService.SendAsync(new GetInternalLinkType(url));
            if (response is InternalLinkType internalLink)
            {
                OpenTelegramUrl(clientService, navigation, internalLink, source);
            }
            else
            {
                OpenLoginUrl(clientService, navigation, url, await clientService.SendAsync(new GetExternalLinkInfo(url)));
            }
        }

#if LINUX
        // WHICH LINK CLASSES THIS BUILD ANSWERS, AND WHY THE REST NOW SAY SO OUT LOUD.
        //
        // Windows resolves 49 InternalLinkType classes in the switch below. This branch used to
        // resolve 21 AND HAD NO `default`, so a link whose class was not listed left the method
        // without opening anything and without a line in the log: the click was swallowed whole.
        // A link the app cannot open has to say so; silence is the one answer that is never right.
        //
        // So: everything that could be wired to something the subset already compiles is wired
        // (Instant View out to the browser, story links, the settings sections whose page is in,
        // one's own profile, language packs), the classes Windows itself answers with a no-op are
        // written down as no-ops so they do not masquerade as Linux gaps, and every remaining class
        // reaches `default`, which logs the class name and tells the user.
        //
        // Three classes left `default` when the popups behind them came in: StickerSet
        // (StickersPopup), ChatFolderInvite (AddFolderPopup) and Background -- the last of which
        // needed no new file at all, BackgroundPopup having been in the subset since the themes
        // phase; only the `case` was missing. NewPrivateChat followed with ContactsPopup. The
        // count answered here is 33 of Windows' 49.
        //
        // TWO CLASSES STAY IN `default` ON PURPOSE, and they are not waiting for a file.
        // PremiumFeaturesPage would call navigation.ShowPromo and Invoice would call
        // navigation.NavigateToInvoice; on Linux BOTH are log-only no-ops in TLNavigationService
        // (:118 "Premium promo is not available on Linux", :139 "Payments are not available on
        // Linux"). Giving them a `case` would take a link that currently says out loud it cannot
        // be opened and turn it into one that silently does nothing -- a regression, not parity.
        // They belong to whatever tanda brings the promo and payment screens, and they should be
        // wired in the same commit as their destination, never before it.
        //
        // What still reaches `default`, and what each one is waiting for:
        //   MessageDraft (the ?text= shares),
        //   BotAddToChannel                            ChooseChatsPopup
        //   WebApp, MainWebApp, AttachmentMenuBot      the mini app window (no WebView2 counterpart)
        //   Oauth                                      OAuthPopup
        //   RequestManagedBot                          NewBotPopup
        //   TextCompositionStyle                       TextStylePreviewPopup
        //   PremiumGiftCode, UpgradedGift              the promo and the gift popups
        //   GroupCall, LiveStory, CallsPage            group calls (VoipCoordinator's Group region
        //                                              is behind #if LINUX)
        //   ChatBoost                                  ChatBoostFeaturesPopup
        //   NewPrivateChat                             ContactsPopup
        private static async void OpenLoginUrl(IClientService clientService, INavigationService navigation, string url, Object info)
        {
            if (info is LoginUrlInfoOpen infoOpen)
            {
                OpenUrl(null, navigation, infoOpen.Url, !infoOpen.SkipConfirmation);
            }
            else if (info is LoginUrlInfoRequestConfirmation requestConfirmation)
            {
                // "Log in with Telegram". This used to fall into a bare else that opened the raw
                // url: the browser got the ANONYMOUS page, the user was never told which site was
                // asking, under which account, or whether the bot would be allowed to message
                // them, and nothing said the consent step had been skipped. Silently downgrading
                // an authenticated hand-off to an anonymous one is exactly the kind of half-step
                // that must not happen without the user seeing it, so the popup is in the subset
                // now (Views/Popups/LoginUrlInfoPopup, a ContentPopup with a TextBlock and two
                // CheckBoxes -- it needs nothing that was not already compiled).
                //
                // Same flow as Windows, deliberately including the detail that CheckBox1 is not a
                // gate there either: pressing Open is the consent, and the write-access checkbox
                // is what travels to GetExternalLink.
                var popup = new Views.Popups.LoginUrlInfoPopup(clientService, requestConfirmation);

                var confirm = await navigation.ShowPopupAsync(popup);
                if (confirm != ContentDialogResult.Primary)
                {
                    return;
                }

                var response = await clientService.SendAsync(new GetExternalLink(url, popup.AllowWriteAccess));
                if (response is HttpUrl httpUrl)
                {
                    OpenUrl(null, null, httpUrl.Url);
                }
                else if (response is Error)
                {
                    OpenUrl(null, null, url);
                }
            }
            else
            {
                // Neither an "open it" nor a confirmation: there is no login step to consent to,
                // so the link opens as it is. When the reason is an error, that answer is a guess
                // -- getExternalLinkInfo is the only thing that knows whether a confirmation was
                // due -- so it is written down instead of being dropped in silence.
                if (info is Error externalLinkError)
                {
                    Logger.Error("getExternalLinkInfo error " + externalLinkError);
                }

                OpenUrl(null, null, url);
            }
        }

        public static void OpenTelegramUrl(IClientService clientService, INavigationService navigation, InternalLinkType internalLink, OpenUrlSource source = null)
        {
            switch (internalLink)
            {
                case InternalLinkTypeAuthenticationCode authenticationCode:
                    if (clientService.AuthorizationState is AuthorizationStateWaitCode)
                    {
                        clientService.Send(new CheckAuthenticationCode(authenticationCode.Code));
                    }
                    break;
                case InternalLinkTypeBotStart botStart:
                    NavigateToBotStart(clientService, navigation, botStart.BotUsername, botStart.StartParameter, botStart.Autostart, false);
                    break;
                case InternalLinkTypeBotStartInGroup botStartInGroup:
                    NavigateToBotStart(clientService, navigation, botStartInGroup.BotUsername, botStartInGroup.StartParameter, false, true);
                    break;
                case InternalLinkTypeBusinessChat businessChat:
                    NavigateToBusinessChat(clientService, navigation, businessChat.LinkName);
                    break;
                case InternalLinkTypeChatInvite chatInvite:
                    NavigateToInviteLink(clientService, navigation, chatInvite.InviteLink);
                    break;
                case InternalLinkTypeGame game:
                    NavigateToUsername(clientService, navigation, game.BotUsername, null, game.GameShortName);
                    break;
                case InternalLinkTypeInstantView instantView:
                    // The reader page is outside the subset (PageBlock rendering), but an Instant
                    // View link is a link to an ordinary web page, so it goes to the browser --
                    // the very same answer OpenTelegramUrl(Uri) already gives a telegra.ph link
                    // a hundred lines above. FallbackUrl is what TDLib hands over when the article
                    // itself cannot be reached, so it is the second choice, not the first.
                    OpenUrl(null, null, string.IsNullOrEmpty(instantView.Url) ? instantView.FallbackUrl : instantView.Url);
                    break;
                case InternalLinkTypeLanguagePack languagePack:
                    NavigateToLanguage(clientService, navigation, languagePack.LanguagePackId);
                    break;
                case InternalLinkTypeMessage message:
                    NavigateToMessage(clientService, navigation, message.Url);
                    break;
                case InternalLinkTypeMyProfilePage myProfile:
                    NavigateToMyProfile(clientService, navigation, myProfile.Section);
                    break;
                case InternalLinkTypePassportDataRequest:
                    // Windows answers this one with a bare `break` as well: Telegram Passport has
                    // no client side in Unigram on any platform. Listed on purpose so it does not
                    // fall into `default` and get reported to the user as a Linux gap.
                    Logger.Warning("InternalLinkTypePassportDataRequest: no handler on any Unigram build");
                    break;
                case InternalLinkTypePhoneNumberConfirmation phoneNumberConfirmation:
                    // NavigateToConfirmPhone is commented out end to end upstream, so this is a
                    // no-op there too. Kept in the switch, with the log line, for the same reason
                    // as the case above.
                    Logger.Warning("InternalLinkTypePhoneNumberConfirmation: NavigateToConfirmPhone is a stub on every Unigram build");
                    NavigateToConfirmPhone(clientService, phoneNumberConfirmation.PhoneNumber, phoneNumberConfirmation.Hash);
                    break;
                case InternalLinkTypeProxy proxy:
                    NavigateToProxy(clientService, navigation, proxy.Proxy);
                    break;
                case InternalLinkTypePublicChat publicChat:
                    NavigateToUsername(clientService, navigation, publicChat.ChatUsername, draftText: publicChat.DraftText, openProfile: publicChat.OpenProfile);
                    break;
                case InternalLinkTypeQrCodeAuthentication:
                    // Same as Windows: nothing to do. Signing another device in by QR needs the
                    // camera page, which is not part of Unigram on any platform.
                    Logger.Warning("InternalLinkTypeQrCodeAuthentication: no handler on any Unigram build");
                    break;
                case InternalLinkTypeSavedMessages:
                    navigation.NavigateToChat(clientService.Options.MyId, force: false);
                    break;
                case InternalLinkTypeSettings settings:
                    NavigateToSettings(clientService, navigation, settings.Section);
                    break;
                case InternalLinkTypeStory story:
                    NavigateToStory(clientService, navigation, story.StoryPosterUsername, story.StoryId);
                    break;
                case InternalLinkTypeTheme theme:
                    NavigateToTheme(clientService, navigation, theme.ThemeName);
                    break;
                case InternalLinkTypeUnknownDeepLink unknownDeepLink:
                    NavigateToUnknownDeepLink(clientService, navigation, unknownDeepLink.Link);
                    break;
                case InternalLinkTypeUserPhoneNumber phoneNumber:
                    NavigateToPhoneNumber(clientService, navigation, phoneNumber.PhoneNumber, phoneNumber.DraftText, phoneNumber.OpenProfile);
                    break;
                case InternalLinkTypeUserToken userToken:
                    NavigateToUserToken(clientService, navigation, userToken.Token);
                    break;
                case InternalLinkTypeVideoChat videoChat:
                    NavigateToUsername(clientService, navigation, videoChat.ChatUsername, videoChat.InviteHash, null);
                    break;
                case InternalLinkTypeChatAffiliateProgram chatAffiliateProgram:
                    NavigateToUsername(clientService, navigation, chatAffiliateProgram.Username, referrer: chatAffiliateProgram.Referrer);
                    break;
                case InternalLinkTypeDirectMessagesChat directMessagesChat:
                    NavigateToDirectMessagesChat(clientService, navigation, directMessagesChat.ChannelUsername);
                    break;
                case InternalLinkTypeStoryAlbum storyAlbum:
                    NavigateToUsername(clientService, navigation, storyAlbum.StoryAlbumOwnerUsername);
                    break;
                case InternalLinkTypeGiftCollection giftCollection:
                    NavigateToUsername(clientService, navigation, giftCollection.GiftOwnerUsername);
                    break;
                case InternalLinkTypeStickerSet stickerSet:
                    NavigateToStickerSet(navigation, stickerSet.StickerSetName);
                    break;
                case InternalLinkTypeBackground background:
                    NavigateToBackground(clientService, navigation, background.BackgroundName);
                    break;
                case InternalLinkTypeChatFolderInvite chatFolderInvite:
                    NavigateToChatFolderInviteLink(clientService, navigation, chatFolderInvite.InviteLink);
                    break;
                // tg://... links that open the three Create popups.
                case InternalLinkTypeNewChannelChat:
                    navigation.ShowPopup(new Views.Create.NewChannelPopup());
                    break;
                case InternalLinkTypeNewGroupChat:
                    navigation.ShowPopup(new Views.Create.NewGroupPopup());
                    break;
                case InternalLinkTypeNewPrivateChat:
                    navigation.ShowPopup(new ContactsPopup());
                    break;
                default:
                    // The branch this switch did not have. Everything listed in the block comment
                    // at the top of this region lands here.
                    OnUnsupportedLink(navigation, internalLink);
                    break;
            }
        }

        /// <summary>
        /// The answer to a link class this build cannot open yet: a line in the log with the class
        /// name, and a toast so the user knows the click was seen and refused rather than lost.
        /// </summary>
        /// <param name="what">
        /// The <see cref="InternalLinkType"/> that had no case, or the <see cref="SettingsSection"/>
        /// whose page is outside the subset. Only its type name is used.
        /// </param>
        private static void OnUnsupportedLink(INavigationService navigation, object what)
        {
            Logger.Warning("no handler for " + (what?.GetType().Name ?? "null") + "; the link was not opened");

            // LinuxStrings, not Strings/*.resw: this text is one this port adds, and PORTING.md
            // rule 4 keeps the generated resource files untouched.
            if (navigation != null && LinuxStrings.TryGetString("LinkNotSupported", out string message))
            {
                navigation.ShowToast(message, ToastPopupIcon.Info);
            }
        }

        /// <summary>
        /// Upstream: the same method, verbatim. It needs nothing this subset lacks --
        /// <see cref="Controls.Stories.StoriesWindow"/> is the Linux rewrite in
        /// Telegram.Linux/Stories/, same namespace, same class name and the same two entry points,
        /// and the three view models it builds on are compiled.
        /// </summary>
        private static async void NavigateToStory(IClientService clientService, INavigationService navigation, string username, int storyId)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(username));
            if (response is Chat chat)
            {
                var response2 = await clientService.SendAsync(new GetStory(chat.Id, storyId, false));
                if (response2 is Story story)
                {
                    var settings = clientService.Session.Resolve<ISettingsService>();
                    var aggregator = clientService.Session.Resolve<IEventAggregator>();

                    var activeStories = new ViewModels.Stories.ActiveStoriesViewModel(clientService, settings, aggregator, story);
                    var viewModel = ViewModels.Stories.StoryListViewModel.Create(navigation, activeStories);

                    var window = new Controls.Stories.StoriesWindow(navigation.XamlRoot);
                    window.Update(viewModel, activeStories, Controls.Stories.StoryOpenOrigin.Card, Rect.Empty, null);
                    _ = window.ShowAsync();
                }
                else
                {
                    navigation.ShowToast(Strings.StoryNotFound, ToastPopupIcon.ExpiredStory);
                }
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        /// <summary>
        /// Upstream lists five subsections (posts, posts/all-stories, posts/add-album, gifts,
        /// archived-posts) and answers all five with a bare break, so only the plain link opens
        /// anything. Here the page opens either way -- a link that asked for the gifts tab of one's
        /// own profile is better served by the profile than by nothing -- and the subsection it
        /// could not honour goes to the log.
        /// </summary>
        private static void NavigateToMyProfile(IClientService clientService, INavigationService navigation, string section)
        {
            if (!string.IsNullOrEmpty(section))
            {
                Logger.Warning("InternalLinkTypeMyProfilePage: subsection '" + section + "' has no destination; opening the profile itself");
            }

            navigation.Navigate(typeof(ProfilePage), clientService.Options.MyId);
        }

        /// <summary>
        /// Upstream's version is around 420 lines because it resolves every subsection to a page of
        /// its own, and most of those pages are outside this subset. Two things it does NOT do are
        /// worth writing down, because they bound what this one can do:
        ///
        /// <para>the settings ROOT is not a navigable page on either build -- SettingsPage is
        /// declared inside MainPage.xaml as &lt;local:SettingsPage x:Name="SettingsView"/&gt; and
        /// shown by switching the pivot, which is why upstream never navigates to it -- so a
        /// section with no page of its own cannot fall back to "open Settings"; and it answers
        /// several sections with a plain break, so a link that opens nothing is not by itself a
        /// Linux gap.</para>
        ///
        /// <para>What is left: map the section to its own page when that page is compiled, ignore
        /// the subsection (logged, so the gap is traceable), and hand the rest to the same answer
        /// as the switch's default.</para>
        /// </summary>
        private static void NavigateToSettings(IClientService clientService, INavigationService navigation, SettingsSection section)
        {
            Type target = section switch
            {
                SettingsSectionAppearance => typeof(Views.Settings.SettingsAppearancePage),
                SettingsSectionDataAndStorage => typeof(Views.Settings.SettingsDataAndStoragePage),
                SettingsSectionDevices => typeof(Views.Settings.SettingsSessionsPage),
                SettingsSectionLanguage => typeof(Views.Settings.SettingsLanguagePage),
                // Windows has no case for this one at all: settingsSectionNotifications runs off
                // the end of its switch and does nothing. SettingsNotificationsPage IS compiled
                // here and a link naming the section has exactly one sensible destination, so it
                // opens. Deliberate, and the only place this method goes past upstream.
                SettingsSectionNotifications => typeof(Views.Settings.SettingsNotificationsPage),
                SettingsSectionPrivacyAndSecurity => typeof(Views.Settings.SettingsPrivacyAndSecurityPage),
                _ => null
            };

            if (target == null)
            {
                OnUnsupportedLink(navigation, section);
                return;
            }

            Logger.Info("InternalLinkTypeSettings: " + section.GetType().Name + " -> " + target.Name + " (subsections are not resolved on this build)");
            navigation.Navigate(target);
        }

        /// <summary>
        /// Upstream: the same method, with the two <c>ResourceContext</c> lines guarded --
        /// Windows.ApplicationModel.Resources.Core does not exist in Uno, and the reset only clears
        /// the .resw fallback catalogue, while every string the interface shows after a language
        /// change comes from TDLib's language pack through LocaleService, which SetLanguageAsync has
        /// already re-pointed by that line. Same guard, same reasoning, as the other call site of
        /// this pair inside the subset (SettingsLanguageViewModel.Change).
        /// </summary>
        public static async void NavigateToLanguage(IClientService clientService, INavigationService navigation, string languagePackId)
        {
            var response = await clientService.SendAsync(new GetLanguagePackInfo(languagePackId));
            if (response is LanguagePackInfo info)
            {
                if (info.Id == AppSettings.LanguagePackId)
                {
                    var confirm = await navigation.ShowPopupAsync(string.Format(Strings.LanguageSame, info.Name), Strings.Language, Strings.OK, Strings.Settings);
                    if (confirm != ContentDialogResult.Secondary)
                    {
                        return;
                    }

                    navigation.Navigate(typeof(Views.Settings.SettingsLanguagePage));
                }
                else if (info.TotalStringCount == 0)
                {
                    navigation.ShowPopup(string.Format(Strings.LanguageUnknownCustomAlert, info.Name), Strings.LanguageUnknownTitle, Strings.OK);
                }
                else
                {
                    var message = info.IsOfficial
                        ? Strings.LanguageAlert
                        : Strings.LanguageCustomAlert;

                    var start = message.IndexOf('[');
                    var end = message.IndexOf(']');
                    if (start != -1 && end != -1)
                    {
                        message = message.Insert(end + 1, $"({info.TranslationUrl})");
                    }

                    var confirm = await navigation.ShowPopupAsync(string.Format(message, info.Name, (int)Math.Ceiling(info.TranslatedStringCount / (float)info.TotalStringCount * 100)), Strings.LanguageTitle, Strings.Change, Strings.Cancel);
                    if (confirm != ContentDialogResult.Primary)
                    {
                        return;
                    }

                    var set = await LocaleService.Current.SetLanguageAsync(info, true);
                    if (set is Ok)
                    {
                        WindowContext.ForEach(window =>
                        {
                            if (window.Content is FrameworkElement frameworkElement)
                            {
                                frameworkElement.FlowDirection = LocaleService.Current.FlowDirection;
                            }

                            if (window.Content is RootPage root)
                            {
                                root.UpdateComponent();
                            }
                        });
                    }
                }
            }
        }
#else
        private static async void OpenLoginUrl(IClientService clientService, INavigationService navigation, string url, Object info)
        {
            if (info is LoginUrlInfoOpen infoOpen)
            {
                OpenUrl(null, navigation, infoOpen.Url, !infoOpen.SkipConfirmation);
            }
            else if (info is LoginUrlInfoRequestConfirmation requestConfirmation)
            {
                var popup = new LoginUrlInfoPopup(clientService, requestConfirmation);

                var confirm = await navigation.ShowPopupAsync(popup);
                if (confirm != ContentDialogResult.Primary)
                {
                    return;
                }

                var response = await clientService.SendAsync(new GetExternalLink(url, popup.AllowWriteAccess));
                if (response is HttpUrl httpUrl)
                {
                    OpenUrl(null, null, httpUrl.Url);
                }
                else if (response is Error)
                {
                    OpenUrl(null, null, url);
                }
            }
        }

        public static void OpenTelegramUrl(IClientService clientService, INavigationService navigation, InternalLinkType internalLink, OpenUrlSource source = null)
        {
            switch (internalLink)
            {
                case InternalLinkTypeAuthenticationCode authenticationCode:
                    if (clientService.AuthorizationState is AuthorizationStateWaitCode)
                    {
                        clientService.Send(new CheckAuthenticationCode(authenticationCode.Code));
                    }
                    break;
                case InternalLinkTypeAttachmentMenuBot attachmentMenuBot:
                    NavigateToAttachmentMenuBot(clientService, navigation, attachmentMenuBot, source);
                    break;
                case InternalLinkTypeBackground background:
                    NavigateToBackground(clientService, navigation, background.BackgroundName);
                    break;
                case InternalLinkTypeBotStart botStart:
                    NavigateToBotStart(clientService, navigation, botStart.BotUsername, botStart.StartParameter, botStart.Autostart, false);
                    break;
                case InternalLinkTypeBotStartInGroup botStartInGroup:
                    // Not yet supported: AdministratorRights
                    NavigateToBotStart(clientService, navigation, botStartInGroup.BotUsername, botStartInGroup.StartParameter, false, true);
                    break;
                case InternalLinkTypeBusinessChat businessChat:
                    NavigateToBusinessChat(clientService, navigation, businessChat.LinkName);
                    break;
                case InternalLinkTypeChatBoost chatBoost:
                    NavigateToChatBoost(clientService, navigation, chatBoost.Url);
                    break;
                case InternalLinkTypeChatInvite chatInvite:
                    NavigateToInviteLink(clientService, navigation, chatInvite.InviteLink);
                    break;
                case InternalLinkTypeChatFolderInvite chatFolderInvite:
                    NavigateToChatFolderInviteLink(clientService, navigation, chatFolderInvite.InviteLink);
                    break;
                case InternalLinkTypeGame game:
                    NavigateToUsername(clientService, navigation, game.BotUsername, null, game.GameShortName);
                    break;
                case InternalLinkTypeInstantView instantView:
                    navigation.NavigateToInstant(instantView.Url, instantView.FallbackUrl);
                    break;
                case InternalLinkTypeInvoice invoice:
                    NavigateToInvoice(navigation, invoice.InvoiceName);
                    break;
                case InternalLinkTypeLanguagePack languagePack:
                    NavigateToLanguage(clientService, navigation, languagePack.LanguagePackId);
                    break;
                case InternalLinkTypeMessage message:
                    NavigateToMessage(clientService, navigation, message.Url);
                    break;
                case InternalLinkTypeMessageDraft messageDraft:
                    NavigateToShare(navigation, messageDraft.Text, messageDraft.ContainsLink);
                    break;
                case InternalLinkTypePassportDataRequest:
                    break;
                case InternalLinkTypePremiumFeaturesPage premiumFeatures:
                    navigation.ShowPromo(new PremiumSourceLink(premiumFeatures.Referrer));
                    break;
                case InternalLinkTypePremiumGiftCode premiumGiftCode:
                    NavigateToPremiumGiftCode(clientService, navigation, premiumGiftCode.Code, source);
                    break;
                case InternalLinkTypePhoneNumberConfirmation phoneNumberConfirmation:
                    NavigateToConfirmPhone(clientService, phoneNumberConfirmation.PhoneNumber, phoneNumberConfirmation.Hash);
                    break;
                case InternalLinkTypeProxy proxy:
                    NavigateToProxy(clientService, navigation, proxy.Proxy);
                    break;
                case InternalLinkTypePublicChat publicChat:
                    NavigateToUsername(clientService, navigation, publicChat.ChatUsername, draftText: publicChat.DraftText, openProfile: publicChat.OpenProfile);
                    break;
                case InternalLinkTypeQrCodeAuthentication:
                    break;
                case InternalLinkTypeCallsPage calls:
                    NavigateToCalls(clientService, navigation, calls.Section);
                    break;
                case InternalLinkTypeMyProfilePage myProfile:
                    NavigateToMyProfile(clientService, navigation, myProfile.Section);
                    break;
                case InternalLinkTypeNewChannelChat:
                    navigation.ShowPopup(new NewChannelPopup());
                    break;
                case InternalLinkTypeNewGroupChat:
                    navigation.ShowPopup(new NewGroupPopup());
                    break;
                case InternalLinkTypeNewPrivateChat:
                    navigation.ShowPopup(new ContactsPopup());
                    break;
                case InternalLinkTypeSavedMessages:
                    navigation.NavigateToChat(clientService.Options.MyId, force: false);
                    break;
                case InternalLinkTypeSettings settings:
                    NavigateToSettings(clientService, navigation, settings.Section);
                    break;
                case InternalLinkTypeStickerSet stickerSet:
                    NavigateToStickerSet(navigation, stickerSet.StickerSetName);
                    break;
                case InternalLinkTypeStory story:
                    NavigateToStory(clientService, navigation, story.StoryPosterUsername, story.StoryId);
                    break;
                case InternalLinkTypeLiveStory liveStory:
                    NavigateToLiveStory(clientService, navigation, liveStory.StoryPosterUsername);
                    break;
                case InternalLinkTypeTheme theme:
                    NavigateToTheme(clientService, navigation, theme.ThemeName);
                    break;
                case InternalLinkTypeUnknownDeepLink unknownDeepLink:
                    NavigateToUnknownDeepLink(clientService, navigation, unknownDeepLink.Link);
                    break;
                case InternalLinkTypeUserPhoneNumber phoneNumber:
                    NavigateToPhoneNumber(clientService, navigation, phoneNumber.PhoneNumber, phoneNumber.DraftText, phoneNumber.OpenProfile);
                    break;
                case InternalLinkTypeUserToken userToken:
                    NavigateToUserToken(clientService, navigation, userToken.Token);
                    break;
                case InternalLinkTypeVideoChat videoChat:
                    NavigateToUsername(clientService, navigation, videoChat.ChatUsername, videoChat.InviteHash, null);
                    break;
                case InternalLinkTypeWebApp webApp:
                    NavigateToWebApp(clientService, navigation, webApp.BotUsername, webApp.StartParameter, webApp.WebAppShortName, webApp.Mode, source);
                    break;
                case InternalLinkTypeMainWebApp mainWebApp:
                    NavigateToMainWebApp(clientService, navigation, mainWebApp.BotUsername, mainWebApp.StartParameter, mainWebApp.Mode, source);
                    break;
                case InternalLinkTypeChatAffiliateProgram chatAffiliateProgram:
                    NavigateToUsername(clientService, navigation, chatAffiliateProgram.Username, referrer: chatAffiliateProgram.Referrer);
                    break;
                case InternalLinkTypeUpgradedGift upgradedGift:
                    NavigateToUpgradedGift(clientService, navigation, upgradedGift.Name);
                    break;
                case InternalLinkTypeGroupCall groupCall:
                    NavigateToGroupCall(clientService, navigation, new InputGroupCallLink(groupCall.InviteLink));
                    break;
                case InternalLinkTypeBotAddToChannel botAddToChannel:
                    NavigateToBotAddToChannel(clientService, navigation, botAddToChannel.BotUsername, botAddToChannel.AdministratorRights);
                    break;
                case InternalLinkTypeDirectMessagesChat directMessagesChat:
                    NavigateToDirectMessagesChat(clientService, navigation, directMessagesChat.ChannelUsername);
                    break;
                case InternalLinkTypeStoryAlbum storyAlbum:
                    NavigateToUsername(clientService, navigation, storyAlbum.StoryAlbumOwnerUsername);
                    break;
                case InternalLinkTypeGiftCollection giftCollection:
                    NavigateToUsername(clientService, navigation, giftCollection.GiftOwnerUsername);
                    break;
                case InternalLinkTypeOauth oauth:
                    NavigateToOauth(clientService, navigation, oauth.Url);
                    break;
                case InternalLinkTypeRequestManagedBot requestManagedBot:
                    NavigateToRequestManagedBot(clientService, navigation, requestManagedBot.ManagerBotUsername, requestManagedBot.SuggestedBotName, requestManagedBot.SuggestedBotUsername);
                    break;
                case InternalLinkTypeTextCompositionStyle textCompositionStyle:
                    NavigateToTextCompositionStyle(clientService, navigation, textCompositionStyle.StyleName);
                    break;
            }
        }

        private static async void NavigateToTextCompositionStyle(IClientService clientService, INavigationService navigation, string styleName)
        {
            var response = await clientService.SendAsync(new SearchTextCompositionStyle(styleName));
            if (response is TextCompositionStyle style)
            {
                navigation.ShowPopup(new TextStylePreviewPopup(clientService, navigation, style, style.EnglishExample));
            }
            else
            {
                // TODO
            }
        }

        private static async void NavigateToRequestManagedBot(IClientService clientService, INavigationService navigation, string managerBotUsername, string suggestedBotName, string suggestedBotUsername)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(managerBotUsername));
            if (response is Chat chat && clientService.TryGetUser(chat, out User user))
            {
                if (user.Type is UserTypeBot { CanManageBots: true })
                {
                    navigation.ShowPopup(new NewBotPopup(), new NewBotArgs(user.Id, true, suggestedBotName, suggestedBotUsername));
                }
                else
                {
                    navigation.ShowToast(string.Format(Strings.CreateManagedBotUnsupported, chat.Title), ToastPopupIcon.Info);
                }
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        private static async void NavigateToOauth(IClientService clientService, INavigationService navigation, string url)
        {
            // TODO: origin
            var response = await clientService.SendAsync(new GetOauthLinkInfo(url, string.Empty));
            if (response is OauthLinkInfo info)
            {
                var popup = new OAuthPopup(clientService, navigation, info);

                var confirm = await navigation.ShowPopupAsync(popup);
                if (confirm == ContentDialogResult.Primary)
                {
                    // TODO: match code
                    var accept = await clientService.SendAsync(new AcceptOauthRequest(url, string.Empty, popup.AllowWriteAccess, popup.AllowPhoneNumberAccess));
                    if (accept is HttpUrl httpUrl)
                    {
                        OpenUrl(null, null, httpUrl.Url);
                    }
                    else if (response is Error)
                    {
                        OpenUrl(null, null, url);
                    }
                }
                else
                {
                    clientService.Send(new DeclineOauthRequest(url));
                }
            }
        }

        private static void NavigateToCalls(IClientService clientService, INavigationService navigation, string section)
        {
            switch (section)
            {
                case "start-call":
                    CallsViewModel.NewCall(clientService, navigation);
                    break;
                case "all": break;
                case "missed": break;
                case "edit": break;
                case "show-tab": break;
                default:
                    navigation.ShowPopup(new CallsPopup());
                    break;
            }
        }

        private static void NavigateToMyProfile(IClientService clientService, INavigationService navigation, string section)
        {
            switch (section)
            {
                case "posts": break;
                case "posts/all-stories": break;
                case "posts/add-album": break;
                case "gifts": break;
                case "archived-posts": break;
                default:
                    navigation.Navigate(typeof(ProfilePage), clientService.Options.MyId);
                    break;
            }
        }

        private static void NavigateToSettings(IClientService clientService, INavigationService navigation, SettingsSection section)
        {
            switch (section)
            {
                case SettingsSectionAppearance appearance:
                    switch (appearance.Subsection)
                    {
                        case "themes": goto default;
                        case "themes/edit":
                        case "themes/create":
                            navigation.Navigate(typeof(SettingsThemesPage));
                            break;
                        case "wallpapers": goto default;
                        case "wallpapers/edit":
                        case "wallpapers/set":
                        case "wallpapers/choose-photo":
                            navigation.Navigate(typeof(SettingsBackgroundsPage));
                            break;
                        case "your-color/profile": goto default;
                        case "your-color/profile/add-icons":
                        case "your-color/profile/use-gift":
                        case "your-color/profile/reset":
                        case "your-color/name":
                        case "your-color/name/add-icons":
                        case "your-color/name/use-gift":
                            navigation.Navigate(typeof(SettingsProfileColorPage));
                            break;
                        case "stickers-and-emoji": goto default;
                        case "stickers-and-emoji/edit":
                        case "stickers-and-emoji/trending":
                        case "stickers-and-emoji/archived":
                        case "stickers-and-emoji/emoji":
                        case "stickers-and-emoji/suggest-by-emoji":
                        case "stickers-and-emoji/large-emoji":
                        case "stickers-and-emoji/dynamic-order":
                            navigation.Navigate(typeof(SettingsStickersPage));
                            break;
                        case "stickers-and-emoji/archived/edit":
                            navigation.Navigate(typeof(SettingsStickersPage), StickersType.Archived);
                            break;
                        case "stickers-and-emoji/emoji/edit":
                        case "stickers-and-emoji/emoji/archived":
                        case "stickers-and-emoji/emoji/suggest":
                        case "stickers-and-emoji/emoji/show-more":
                            navigation.Navigate(typeof(SettingsStickersPage), StickersType.Emoji);
                            break;
                        case "stickers-and-emoji/emoji/archived/edit":
                            navigation.Navigate(typeof(SettingsStickersPage), StickersType.EmojiArchived);
                            break;
                        case "stickers-and-emoji/emoji/quick-reaction":
                        case "stickers-and-emoji/emoji/quick-reaction/choose":
                        case "night-mode":
                        case "auto-night-mode":
                        case "text-size":
                        case "text-size/use-system":
                        case "message-corners":
                        case "animations":
                        case "app-icon":
                        case "tap-for-next-media":
                        default:
                            navigation.Navigate(typeof(SettingsAppearancePage));
                            break;
                    }
                    break;
                case SettingsSectionAskQuestion:
                    break;
                case SettingsSectionBusiness business:
                    switch (business.Subsection)
                    {
                        case "do-not-hide-ads":
                        default:
                            navigation.Navigate(typeof(BusinessPage));
                            break;
                    }
                    break;
                case SettingsSectionChatFolders chatFolders:
                    switch (chatFolders.Subsection)
                    {
                        case "edit":
                        case "create":
                        case "add-recommended":
                        case "show-tags":
                        case "tab-view":
                        default:
                            navigation.Navigate(typeof(FoldersPage));
                            break;
                    }
                    break;
                case SettingsSectionDataAndStorage dataAndStorage:
                    switch (dataAndStorage.Subsection)
                    {
                        case "storage":
                        case "storage/edit":
                        case "storage/auto-remove":
                        case "storage/clear-cache":
                        case "storage/max-cache":
                            navigation.Navigate(typeof(SettingsStoragePage));
                            break;
                        case "usage":
                        case "usage/mobile":
                        case "usage/wifi":
                        case "usage/roaming":
                        case "usage/reset":
                            navigation.Navigate(typeof(SettingsNetworkPage));
                            break;
                        //case "usage/mobile/auto-download": break;
                        //case "usage/mobile/auto-download/enable": break;
                        //case "usage/mobile/auto-download/usage": break;
                        //case "usage/mobile/auto-download/photos": break;
                        //case "usage/mobile/auto-download/stories": break;
                        //case "usage/mobile/auto-download/videos": break;
                        //case "usage/mobile/auto-download/files": break;
                        //case "usage/wifi/auto-download": break;
                        //case "usage/wifi/auto-download/enable": break;
                        //case "usage/wifi/auto-download/usage": break;
                        //case "usage/wifi/auto-download/photos": break;
                        //case "usage/wifi/auto-download/stories": break;
                        //case "usage/wifi/auto-download/videos": break;
                        //case "usage/wifi/auto-download/files": break;
                        //case "usage/roaming/auto-download": break;
                        //case "usage/roaming/auto-download/enable": break;
                        //case "usage/roaming/auto-download/usage": break;
                        //case "usage/roaming/auto-download/photos": break;
                        //case "usage/roaming/auto-download/stories": break;
                        //case "usage/roaming/auto-download/videos": break;
                        //case "usage/roaming/auto-download/files": break;
                        case "auto-download/data":
                        case "auto-download/data/enable":
                        case "auto-download/data/usage":
                        case "auto-download/data/photos":
                        case "auto-download/data/stories":
                        case "auto-download/data/videos":
                        case "auto-download/data/files":
                        case "auto-download/wifi":
                        case "auto-download/wifi/enable":
                        case "auto-download/wifi/usage":
                        case "auto-download/wifi/photos":
                        case "auto-download/wifi/stories":
                        case "auto-download/wifi/videos":
                        case "auto-download/wifi/files":
                        case "auto-download/roaming":
                        case "auto-download/roaming/enable":
                        case "auto-download/roaming/usage":
                        case "auto-download/roaming/photos":
                        case "auto-download/roaming/stories":
                        case "auto-download/roaming/videos":
                        case "auto-download/roaming/files":
                        case "auto-download/reset":
                        case "save-to-photos/chats":
                        case "save-to-photos/chats/max-video-size":
                        case "save-to-photos/chats/add-exception":
                        case "save-to-photos/chats/delete-all":
                        case "save-to-photos/groups":
                        case "save-to-photos/groups/max-video-size":
                        case "save-to-photos/groups/add-exception":
                        case "save-to-photos/groups/delete-all":
                        case "save-to-photos/channels":
                        case "save-to-photos/channels/max-video-size":
                        case "save-to-photos/channels/add-exception":
                        case "save-to-photos/channels/delete-all":
                        case "less-data-calls":
                        case "open-links":
                        case "share-sheet":
                        case "share-sheet/suggested-chats":
                        case "share-sheet/suggest-by":
                        case "share-sheet/reset":
                        case "saved-edited-photos":
                        case "pause-music":
                        case "raise-to-listen":
                        case "raise-to-speak":
                        case "show-18-content":
                        default:
                            navigation.Navigate(typeof(SettingsDataAndStoragePage));
                            break;
                        case "proxy":
                        case "proxy/edit":
                        case "proxy/use-proxy":
                        case "proxy/add-proxy":
                        case "proxy/share-list":
                        case "proxy/use-for-calls":
                            navigation.Navigate(typeof(SettingsProxyPage));
                            break;
                    }
                    break;
                case SettingsSectionDevices devices:
                    switch (devices.Subsection)
                    {
                        case "edit":
                        case "link-desktop":
                        case "terminate-sessions":
                        case "auto-terminate":
                        default:
                            navigation.Navigate(typeof(SettingsSessionsPage));
                            break;
                    }
                    break;
                case SettingsSectionEditProfile editProfile:
                    switch (editProfile.Subsection)
                    {
                        case "set-photo":
                        case "first-name":
                        case "last-name":
                        case "emoji-status":
                        case "bio":
                        case "birthday":
                        case "change-number":
                        case "username":
                        case "your-color":
                        case "channel":
                        case "add-account":
                        case "log-out":
                        case "profile-photo/use-emoji":
                        default:
                            navigation.Navigate(typeof(SettingsProfilePage));
                            break;
                        case "profile-color/profile":
                        case "profile-color/profile/add-icons":
                        case "profile-color/profile/use-gift":
                        case "profile-color/name":
                        case "profile-color/name/add-icons":
                        case "profile-color/name/use-gift":
                            navigation.Navigate(typeof(SettingsProfileColorPage));
                            break;
                    }
                    break;
                case SettingsSectionFaq:
                    break;
                case SettingsSectionFeatures:
                    break;
                case SettingsSectionInAppBrowser inAppBrowser:
                    switch (inAppBrowser.Subsection)
                    {
                        case "enable-browser": break;
                        case "clear-cookies": break;
                        case "clear-cache": break;
                        case "history": break;
                        case "clear-history": break;
                        case "never-open": break;
                        case "clear-list": break;
                        case "search": break;
                        default: break;
                    }
                    break;
                case SettingsSectionLanguage language:
                    switch (language.Subsection)
                    {
                        case "show-button":
                        case "translate-chats":
                        case "do-not-translate":
                        default:
                            navigation.Navigate(typeof(SettingsLanguagePage));
                            break;
                    }
                    break;
                case SettingsSectionMyStars myStars:
                    switch (myStars.Subsection)
                    {
                        case "top-up": break;
                        case "stats": break;
                        case "gift": break;
                        case "earn": break;
                        default: break;
                    }
                    break;
                case SettingsSectionMyGrams myGrams:
                    break;
                case SettingsSectionPowerSaving powerSaving:
                    switch (powerSaving.Subsection)
                    {
                        case "videos":
                        case "gifs":
                        case "stickers":
                        case "emoji":
                        case "effects":
                        case "preload":
                        case "background":
                        case "call-animations":
                        case "particles":
                        case "transitions":
                        default:
                            navigation.Navigate(typeof(SettingsPowerSavingPage));
                            break;
                    }
                    break;
                case SettingsSectionPremium premium:
                    break;
                case SettingsSectionPrivacyAndSecurity privacyAndSecurity:
                    switch (privacyAndSecurity.Subsection)
                    {
                        case "blocked": goto default;
                        case "blocked/edit":
                        case "blocked/block-user":
                        case "blocked/block-user/chats":
                        case "blocked/block-user/contacts":
                            navigation.Navigate(typeof(SettingsBlockedChatsPage));
                            break;
                        case "active-websites": goto default;
                        case "active-websites/edit":
                        case "active-websites/disconnect-all":
                            navigation.Navigate(typeof(SettingsWebSessionsPage));
                            break;
                        case "passcode": goto default;
                        case "passcode/disable":
                        case "passcode/change":
                        case "passcode/auto-lock":
                        case "passcode/face-id":
                        case "passcode/fingerprint":
                            break;
                        case "2sv": goto default;
                        case "2sv/change":
                        case "2sv/disable":
                        case "2sv/change-email":
                            break;
                        case "passkey": goto default;
                        case "passkey/create":
                            break;
                        case "auto-delete": goto default;
                        case "auto-delete/set-custom":
                            break;
                        case "login-email": goto default;
                        case "phone-number": goto default;
                        case "phone-number/never":
                        case "phone-number/always":
                            break;
                        case "last-seen": goto default;
                        case "last-seen/never":
                        case "last-seen/always":
                        case "last-seen/hide-read-time":
                            break;
                        case "profile-photos": goto default;
                        case "profile-photos/never":
                        case "profile-photos/always":
                        case "profile-photos/set-public":
                        case "profile-photos/update-public":
                        case "profile-photos/remove-public":
                            break;
                        case "bio": goto default;
                        case "bio/never":
                        case "bio/always":
                            break;
                        case "gifts": goto default;
                        case "gifts/show-icon":
                        case "gifts/never":
                        case "gifts/always":
                        case "gifts/accepted-types":
                            break;
                        case "birthday": goto default;
                        case "birthday/add":
                        case "birthday/never":
                        case "birthday/always":
                            break;
                        case "saved-music": goto default;
                        case "saved-music/never":
                        case "saved-music/always":
                            break;
                        case "forwards": goto default;
                        case "forwards/never":
                        case "forwards/always":
                            break;
                        case "calls": goto default;
                        case "calls/never":
                        case "calls/always":
                        case "calls/p2p":
                            break;
                        case "calls/p2p/never":
                        case "calls/p2p/always":
                            break;
                        case "calls/ios-integration": break;
                        case "voice": goto default;
                        case "voice/never":
                        case "voice/always":
                            break;
                        case "messages": goto default;
                        case "messages/set-price":
                        case "messages/exceptions":
                            break;
                        case "invites": goto default;
                        case "invites/never":
                        case "invites/always":
                            break;
                        case "self-destruct":
                        case "data-settings":
                        case "data-settings/sync-contacts":
                        case "data-settings/delete-synced":
                        case "data-settings/suggest-contacts":
                        case "data-settings/delete-cloud-drafts":
                        case "data-settings/clear-payment-info":
                        case "data-settings/link-previews":
                        case "data-settings/bot-settings":
                        case "data-settings/map-provider":
                        case "archive-and-mute":
                        default:
                            navigation.Navigate(typeof(SettingsPrivacyAndSecurityPage));
                            break;
                    }
                    break;
                case SettingsSectionPrivacyPolicy:
                    break;
                case SettingsSectionQrCode qrCode:
                    switch (qrCode.Subsection)
                    {
                        case "share": break;
                        case "scan": break;
                        default: break;
                    }
                    break;
                case SettingsSectionSearch:
                    break;
                case SettingsSectionSendGift sendGift:
                    switch (sendGift.Subsection)
                    {
                        case "self": break;
                        default: break;
                    }
                    break;

            }
        }
#endif

        private static async void NavigateToDirectMessagesChat(IClientService clientService, INavigationService navigation, string channelUsername)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(channelUsername));
            if (response is Chat chat && clientService.TryGetSupergroup(chat, out Supergroup supergroup))
            {
                if (supergroup.IsChannel && supergroup.HasDirectMessagesGroup)
                {
                    var fullInfo = clientService.GetSupergroupFull(supergroup.Id);
                    fullInfo ??= await clientService.SendAsync(new GetSupergroupFullInfo(supergroup.Id)) as SupergroupFullInfo;

                    if (fullInfo != null && fullInfo.DirectMessagesChatId != 0)
                    {
                        navigation.NavigateToChat(fullInfo.DirectMessagesChatId);
                    }
                }
            }
        }

#if !LINUX
        private static async void NavigateToBotAddToChannel(IClientService clientService, INavigationService navigation, string botUsername, ChatAdministratorRights administratorRights)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(botUsername));
            if (response is Chat chat && clientService.TryGetUser(chat, out User botUser))
            {
                navigation.ShowPopup(new ChooseChatsPopup(), new ChooseChatsConfigurationBotAddToChannel(botUser.Id, administratorRights));
            }
        }

        public static async void NavigateToGroupCall(IClientService clientService, INavigationService navigation, InputGroupCall inputGroupCall)
        {
            var response = await clientService.SendAsync(new GetGroupCallParticipants(inputGroupCall, 3));
            if (response is GroupCallParticipants participants)
            {
                var confirm = await navigation.ShowPopupAsync(new JoinGroupCallPopup(clientService, participants));
                if (confirm == ContentDialogResult.Primary)
                {
                    clientService.Session.Resolve<IVoipService>().JoinGroupCall(navigation, inputGroupCall);
                }
            }
            else
            {
                navigation.ShowToast(Strings.LinkIsNoActive, ToastPopupIcon.Error);
            }
        }

        public static async void NavigateToUpgradedGift(IClientService clientService, INavigationService navigation, string name)
        {
            var response = await clientService.SendAsync(new GetUpgradedGift(name));
            if (response is UpgradedGift gift)
            {
                var text = gift.OriginalDetails?.Text ?? string.Empty.AsFormattedText();
                var receivedGift = new ReceivedGift(string.Empty, null, text, 0, true, false, false, false, false, false, 0, new SentGiftUpgraded(gift), Array.Empty<int>(), 0, 0, false, 0, 0, 0, 0, 0, string.Empty, 0);

                navigation.ShowPopup(new ReceivedGiftPopup(clientService, navigation, receivedGift, null, null));
            }
            else
            {
                navigation.ShowToast(Strings.UniqueGiftNotFound, ToastPopupIcon.Error);
            }
        }

        private static async void NavigateToPremiumGiftCode(IClientService clientService, INavigationService navigation, string code, OpenUrlSource source)
        {
            var response = await clientService.SendAsync(new CheckPremiumGiftCode(code));
            if (response is PremiumGiftCodeInfo info)
            {
                if (source is OpenUrlSourceChat sourceChat)
                {
                    navigation.ShowPopup(new PromoPopup(clientService, sourceChat.SenderId ?? new MessageSenderChat(sourceChat.ChatId), info, code));
                }
                else
                {
                    navigation.ShowPopup(new PromoPopup(clientService, null, info, code));
                }
            }
            else
            {
                // TODO: error
            }
        }

        private static async void NavigateToAttachmentMenuBot(IClientService clientService, INavigationService navigation, InternalLinkTypeAttachmentMenuBot attachmentMenuBot, OpenUrlSource source)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(attachmentMenuBot.BotUsername));
            if (response is Chat chat && clientService.TryGetUser(chat, out User botUser))
            {
                if (botUser.Type is not UserTypeBot userTypeBot || !userTypeBot.CanBeAddedToAttachmentMenu)
                {
                    return;
                }

                var response2 = await clientService.SendAsync(new GetAttachmentMenuBot(botUser.Id));
                if (response2 is AttachmentMenuBot menuBot)
                {
                    OpenMiniApp(clientService, navigation, botUser, menuBot, attachmentMenuBot.Url, source, attachmentMenuBot);
                }
            }
        }

        public static async void OpenMiniApp(IClientService clientService, INavigationService navigation, User user, AttachmentMenuBot bot, string url, OpenUrlSource source = null, InternalLinkType sourceLink = null, Action<bool> continuation = null)
        {
            if (bot.ShowDisclaimerInSideMenu || !clientService.IsBotAddedToAttachmentMenu(bot.BotUserId))
            {
                var textBlock = new TextBlock();

                var markdown = ClientEx.ParseMarkdown(Strings.BotWebAppDisclaimerCheck);
                if (markdown != null && markdown.Entities.Count == 1)
                {
                    markdown.Entities[0].Type = new TextEntityTypeTextUrl(Strings.WebAppDisclaimerUrl);
                    TextBlockHelper.SetFormattedText(textBlock, markdown);
                }
                else
                {
                    textBlock.Text = Strings.BotWebAppDisclaimerCheck;
                }

                var popup = new MessagePopup
                {
                    Title = Strings.TermsOfUse,
                    Message = Strings.BotWebAppDisclaimerSubtitle,
                    CheckBoxLabel = textBlock,
                    PrimaryButtonText = Strings.Continue,
                    SecondaryButtonText = Strings.Cancel,
                    IsCheckedRequired = true
                };

                var confirm = await navigation.ShowPopupAsync(popup);
                if (confirm != ContentDialogResult.Primary)
                {
                    continuation?.Invoke(false);
                    return;
                }

                await clientService.SendAsync(new ToggleBotIsAddedToAttachmentMenu(bot.BotUserId, true, true));
            }

            continuation?.Invoke(true);

            var response = await clientService.SendAsync(new GetWebAppUrl(bot.BotUserId, url, new WebAppOpenParameters(navigation.Window.ThemeParameters, Constants.WebAppHostName, new WebAppOpenModeFullSize())));
            if (response is WebAppUrl webAppUrl)
            {
                navigation.NavigateToWebApp(user, webAppUrl, 0, bot, null, source, sourceLink);
            }
        }

        private static async void NavigateToChatBoost(IClientService clientService, INavigationService navigation, string url)
        {
            var response = await clientService.SendAsync(new GetChatBoostLinkInfo(url));
            if (response is ChatBoostLinkInfo linkInfo)
            {
                if (linkInfo.ChatId == 0 || !clientService.TryGetChat(linkInfo.ChatId, out Chat chat))
                {
                    navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
                    return;
                }

                var response1 = await clientService.SendAsync(new GetChatBoostFeatures(chat.Type is ChatTypeSupergroup { IsChannel: true }));
                var response2 = await clientService.SendAsync(new GetAvailableChatBoostSlots());
                var response3 = await clientService.SendAsync(new GetChatBoostStatus(linkInfo.ChatId));

                if (response1 is ChatBoostFeatures features && response2 is ChatBoostSlots slots && response3 is ChatBoostStatus status)
                {
                    navigation.ShowPopup(new ChatBoostFeaturesPopup(clientService, navigation, chat, status, slots, features, ChatBoostFeature.None, 0));
                }
            }
        }

        private static async void NavigateToStory(IClientService clientService, INavigationService navigation, string username, int storyId)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(username));
            if (response is Chat chat)
            {
                var response2 = await clientService.SendAsync(new GetStory(chat.Id, storyId, false));
                if (response2 is Story story)
                {
                    var settings = clientService.Session.Resolve<ISettingsService>();
                    var aggregator = clientService.Session.Resolve<IEventAggregator>();

                    var activeStories = new ActiveStoriesViewModel(clientService, settings, aggregator, story);
                    var viewModel = StoryListViewModel.Create(navigation, activeStories);

                    var window = new StoriesWindow(navigation.XamlRoot);
                    window.Update(viewModel, activeStories, StoryOpenOrigin.Card, Rect.Empty, null);
                    _ = window.ShowAsync();
                }
                else
                {
                    navigation.ShowToast(Strings.StoryNotFound, ToastPopupIcon.ExpiredStory);
                }
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        private static async void NavigateToLiveStory(IClientService clientService, INavigationService navigation, string username)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(username));
            if (response is Chat chat)
            {
                var response2 = await clientService.SendAsync(new GetChatActiveStories(chat.Id));
                if (response2 is ChatActiveStories stories)
                {
                    var liveStory = stories.Stories.FirstOrDefault(x => x.IsLive);
                    if (liveStory != null)
                    {
                        var response3 = await clientService.SendAsync(new GetStory(chat.Id, liveStory.StoryId, false));
                        if (response3 is Story story)
                        {
                            if (story.Content is StoryContentLive live && !clientService.TryGetGroupCall(live.GroupCallId, out _))
                            {
                                await clientService.SendAsync(new GetGroupCall(live.GroupCallId));
                            }

                            var settings = clientService.Session.Resolve<ISettingsService>();
                            var aggregator = clientService.Session.Resolve<IEventAggregator>();

                            var activeStories = new ActiveStoriesViewModel(clientService, settings, aggregator, story);
                            var viewModel = StoryListViewModel.Create(navigation, activeStories);

                            var window = new StoriesWindow(navigation.XamlRoot);
                            window.Update(viewModel, activeStories, StoryOpenOrigin.Card, Rect.Empty, null);
                            _ = window.ShowAsync();

                            return;
                        }
                    }
                }

                navigation.ShowToast(Strings.StoryNotFound, ToastPopupIcon.ExpiredStory);
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        public static async void NavigateToWebApp(IClientService clientService, INavigationService navigation, string botUsername, string startParameter, string webAppShortName, WebAppOpenMode mode, OpenUrlSource source)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(botUsername));
            if (response is Chat chat && clientService.TryGetUser(chat, out User botUser))
            {
                if (botUser.Type is not UserTypeBot)
                {
                    return;
                }

                var responss = await clientService.SendAsync(new SearchWebApp(botUser.Id, webAppShortName));
                if (responss is FoundWebApp foundWebApp)
                {
                    var popup = new MessagePopup
                    {
                        Title = Strings.AppName,
                        Message = Strings.BotWebViewStartPermission,
                        PrimaryButtonText = Strings.Start,
                        SecondaryButtonText = Strings.Cancel,
                    };

                    if (foundWebApp.RequestWriteAccess)
                    {
                        var textBlock = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap
                        };

                        var markdown = ClientEx.ParseMarkdown(string.Format(Strings.OpenUrlOption2, botUser.FirstName));
                        if (markdown != null)
                        {
                            TextBlockHelper.SetFormattedText(textBlock, markdown);
                        }
                        else
                        {
                            textBlock.Text = Strings.OpenUrlOption2;
                        }

                        popup.CheckBoxLabel = textBlock;
                    }

                    var confirm = await navigation.ShowPopupAsync(popup);
                    if (confirm != ContentDialogResult.Primary)
                    {
                        return;
                    }

                    var chatId = source switch
                    {
                        OpenUrlSourceChat sourceMessage => sourceMessage.ChatId,
                        _ => 0
                    };

                    var responsa = await clientService.SendAsync(new GetWebAppLinkUrl(chatId, botUser.Id, webAppShortName, startParameter, foundWebApp.RequestWriteAccess && popup.IsChecked is true, new WebAppOpenParameters(navigation.Window.ThemeParameters, Constants.WebAppHostName, mode)));
                    if (responsa is WebAppUrl webAppUrl)
                    {
                        navigation.NavigateToWebApp(botUser, webAppUrl, openMode: mode, source: source, sourceLink: new InternalLinkTypeWebApp(botUsername, webAppShortName, startParameter, mode));
                    }
                }
                else
                {
                    navigation.NavigateToChat(chat);
                }
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        public static async void NavigateToMainWebApp(IClientService clientService, INavigationService navigation, string botUsername, string startParameter, WebAppOpenMode mode, OpenUrlSource source = null)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(botUsername));
            if (response is Chat chat && clientService.TryGetUser(chat, out User botUser))
            {
                NavigateToMainWebApp(clientService, navigation, botUser, startParameter, mode, source);
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        public static async void NavigateToMainWebApp(IClientService clientService, INavigationService navigation, User botUser, string startParameter, WebAppOpenMode mode, OpenUrlSource source = null)
        {
            if (botUser.Type is not UserTypeBot { HasMainWebApp: true })
            {
                return;
            }

            AttachmentMenuBot menuBot = null;
            if (botUser.Type is UserTypeBot { CanBeAddedToAttachmentMenu: true })
            {
                menuBot = await clientService.SendAsync(new GetAttachmentMenuBot(botUser.Id)) as AttachmentMenuBot;
            }

            if (menuBot?.RequestWriteAccess is true)
            {
                var popup = new MessagePopup
                {
                    Title = Strings.AppName,
                    Message = Strings.BotWebViewStartPermission,
                    PrimaryButtonText = Strings.Start,
                    SecondaryButtonText = Strings.Cancel,
                };

                var textBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap
                };

                var markdown = ClientEx.ParseMarkdown(string.Format(Strings.OpenUrlOption2, botUser.FirstName));
                if (markdown != null)
                {
                    TextBlockHelper.SetFormattedText(textBlock, markdown);
                }
                else
                {
                    textBlock.Text = Strings.OpenUrlOption2;
                }

                popup.CheckBoxLabel = textBlock;

                var confirm = await navigation.ShowPopupAsync(popup);
                if (confirm != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            var chatId = source switch
            {
                OpenUrlSourceChat sourceMessage => sourceMessage.ChatId,
                _ => 0
            };

            var responsa = await clientService.SendAsync(new GetMainWebApp(chatId, botUser.Id, startParameter, new WebAppOpenParameters(navigation.Window.ThemeParameters, Constants.WebAppHostName, mode)));
            if (responsa is MainWebApp webApp)
            {
                navigation.NavigateToWebApp(botUser, webApp.Url, menuBot: menuBot, openMode: webApp.Mode, sourceLink: new InternalLinkTypeMainWebApp(botUser.ActiveUsername(), startParameter, webApp.Mode));
            }
        }

#endif

#if LINUX
        private static async void NavigateToUnknownDeepLink(IClientService clientService, INavigationService navigation, string url)
        {
            var response = await clientService.SendAsync(new GetDeepLinkInfo(url));
            if (response is DeepLinkInfo info)
            {
                // There is no store to send the user to for the update the link may ask for.
                navigation.ShowPopup(info.Text, Strings.AppName, Strings.OK);
            }
        }
#else
        private static async void NavigateToUnknownDeepLink(IClientService clientService, INavigationService navigation, string url)
        {
            var response = await clientService.SendAsync(new GetDeepLinkInfo(url));
            if (response is DeepLinkInfo info)
            {
                var confirm = await navigation.ShowPopupAsync(info.Text, Strings.AppName, Strings.OK, info.NeedUpdateApplication ? Strings.UpdateApp : null);
                if (confirm == ContentDialogResult.Secondary)
                {
                    await Launcher.LaunchUriAsync(new Uri("ms-windows-store://pdp/?PFN=" + Package.Current.Id.FamilyName));
                }
            }
        }
#endif

        // Nothing platform-specific here: it was inside the #else above only because it happened to
        // sit after NavigateToUnknownDeepLink, whose two versions differ over the Store URL.
        // BackgroundPopup has been in the subset since the themes phase, so this compiles as it is.
        private static async void NavigateToBackground(IClientService clientService, INavigationService navigation, string slug)
        {
            var response = await clientService.SendAsync(new SearchBackground(slug));
            if (response is Background background)
            {
                navigation.ShowPopup(new BackgroundPopup(), new BackgroundParameters(background));
            }
        }

        private static async void NavigateToMessage(IClientService clientService, INavigationService navigation, string url)
        {
            var response = await clientService.SendAsync(new GetMessageLinkInfo(url));
            if (response is MessageLinkInfo info && clientService.TryGetChat(info.ChatId, out Chat chat))
            {
                if (info.Message != null)
                {
                    var state = new NavigationState
                    {
                        { "checklist_task_id", info.ChecklistTaskId },
                        { "poll_option_id", info.PollOptionId }
                    };

                    if (info.TopicId is MessageTopicThread topicThread)
                    {
                        var thread = await clientService.SendAsync(new GetMessageThread(info.ChatId, topicThread.MessageThreadId));
                        if (thread is MessageThreadInfo)
                        {
                            navigation.NavigateToChat(chat, info.Message.Id, topic: info.TopicId, state: state);
                        }
                        else
                        {
                            navigation.ShowPopup(Strings.LinkNotFound, Strings.AppName, Strings.OK);
                        }
                    }
                    if (info.TopicId is MessageTopicForum topicForum)
                    {
                        var topic = await clientService.SendAsync(new GetForumTopic(chat.Id, topicForum.ForumTopicId)) as ForumTopic;
                        if (topic != null)
                        {
                            navigation.NavigateToChat(chat, info.Message.Id, topic: info.TopicId, state: state);
                        }
                        else
                        {
                            navigation.ShowPopup(Strings.LinkNotFound, Strings.AppName, Strings.OK);
                        }
                    }
                    else
                    {
                        navigation.NavigateToChat(chat, info.Message.Id, state: state);
                    }
                }
                else
                {
                    navigation.NavigateToChat(chat, topic: info.TopicId);
                }
            }
            else
            {
                navigation.ShowPopup(Strings.LinkNotFound, Strings.AppName, Strings.OK);
            }
        }

        private static void NavigateToTheme(IClientService clientService, INavigationService navigation, string slug)
        {
            navigation.ShowPopup(Strings.ThemeNotSupported, Strings.Theme, Strings.OK);
        }

#if !LINUX
        private static void NavigateToInvoice(INavigationService navigation, string invoiceName)
        {
            navigation.NavigateToInvoice(new InputInvoiceName(invoiceName));
        }

        public static async void NavigateToLanguage(IClientService clientService, INavigationService navigation, string languagePackId)
        {
            var response = await clientService.SendAsync(new GetLanguagePackInfo(languagePackId));
            if (response is LanguagePackInfo info)
            {
                if (info.Id == AppSettings.LanguagePackId)
                {
                    var confirm = await navigation.ShowPopupAsync(string.Format(Strings.LanguageSame, info.Name), Strings.Language, Strings.OK, Strings.Settings);
                    if (confirm != ContentDialogResult.Secondary)
                    {
                        return;
                    }

                    navigation.Navigate(typeof(SettingsLanguagePage));
                }
                else if (info.TotalStringCount == 0)
                {
                    navigation.ShowPopup(string.Format(Strings.LanguageUnknownCustomAlert, info.Name), Strings.LanguageUnknownTitle, Strings.OK);
                }
                else
                {
                    var message = info.IsOfficial
                        ? Strings.LanguageAlert
                        : Strings.LanguageCustomAlert;

                    var start = message.IndexOf('[');
                    var end = message.IndexOf(']');
                    if (start != -1 && end != -1)
                    {
                        message = message.Insert(end + 1, $"({info.TranslationUrl})");
                    }

                    var confirm = await navigation.ShowPopupAsync(string.Format(message, info.Name, (int)Math.Ceiling(info.TranslatedStringCount / (float)info.TotalStringCount * 100)), Strings.LanguageTitle, Strings.Change, Strings.Cancel);
                    if (confirm != ContentDialogResult.Primary)
                    {
                        return;
                    }

                    var set = await LocaleService.Current.SetLanguageAsync(info, true);
                    if (set is Ok)
                    {
                        WindowContext.ForEach(window =>
                        {
                            ResourceContext.GetForCurrentView().Reset();
                            ResourceContext.GetForViewIndependentUse().Reset();

                            if (window.Content is FrameworkElement frameworkElement)
                            {
                                //window.CoreWindow.FlowDirection = _localeService.FlowDirection == FlowDirection.RightToLeft
                                //    ? CoreWindowFlowDirection.RightToLeft
                                //    : CoreWindowFlowDirection.LeftToRight;

                                frameworkElement.FlowDirection = LocaleService.Current.FlowDirection;
                            }

                            if (window.Content is RootWindow root)
                            {
                                root.UpdateComponent();
                            }
                        });
                    }
                }
            }
        }

#endif

        public static async void NavigateToSendCode(IClientService clientService, INavigationService navigation, string phoneCode)
        {
            if (clientService.AuthorizationState is AuthorizationStateWaitCode)
            {
                if (clientService.Options.TryGetValue("x_firstname", out string firstValue))
                {
                }

                if (clientService.Options.TryGetValue("x_lastname", out string lastValue))
                {
                }

                var response = await clientService.SendAsync(new CheckAuthenticationCode(phoneCode));
                if (response is Error error)
                {
                    if (error.MessageEquals(ErrorType.PHONE_NUMBER_INVALID))
                    {
                        navigation.ShowPopup(error.Message, Strings.InvalidPhoneNumber, Strings.OK);
                    }
                    else if (error.MessageEquals(ErrorType.PHONE_CODE_EMPTY) || error.MessageEquals(ErrorType.PHONE_CODE_INVALID))
                    {
                        navigation.ShowPopup(error.Message, Strings.InvalidCode, Strings.OK);
                    }
                    else if (error.MessageEquals(ErrorType.PHONE_CODE_EXPIRED))
                    {
                        navigation.ShowPopup(error.Message, Strings.CodeExpired, Strings.OK);
                    }
                    else if (error.MessageEquals(ErrorType.FIRSTNAME_INVALID))
                    {
                        navigation.ShowPopup(error.Message, Strings.InvalidFirstName, Strings.OK);
                    }
                    else if (error.MessageEquals(ErrorType.LASTNAME_INVALID))
                    {
                        navigation.ShowPopup(error.Message, Strings.InvalidLastName, Strings.OK);
                    }
                    else if (error.Message.StartsWith("FLOOD_WAIT"))
                    {
                        navigation.ShowPopup(Strings.FloodWait, Strings.AppName, Strings.OK);
                    }
                    else if (error.Code != -1000)
                    {
                        navigation.ShowPopup(error.Message, Strings.AppName, Strings.OK);
                    }

                    Logger.Error("account.signIn error " + error);
                }
            }
            else
            {
                if (phoneCode.Length > 3)
                {
                    phoneCode = phoneCode.Substring(0, 3) + "-" + phoneCode.Substring(3);
                }

                navigation.ShowPopup(string.Format(Strings.OtherLoginCode, phoneCode), Strings.AppName, Strings.OK);
            }
        }

#if !LINUX
        public static async void NavigateToShare(INavigationService navigation, FormattedText text, bool hasUrl)
        {
            await navigation.ShowPopupAsync(new ChooseChatsPopup(), new ChooseChatsConfigurationPostText(text));
        }
#endif

#if LINUX
        public static async void NavigateToProxy(IClientService clientService, INavigationService navigation, Proxy proxy)
        {
            if (proxy == null)
            {
                navigation.ShowToast(Strings.ProxyLinkUnsupported, ToastPopupIcon.Error);
                return;
            }

            // AddProxyPopup is outside the subset: it is built on controls:TableView, whose default
            // style in Themes/Generic.xaml is win:-only, so the control has no template on Skia and
            // bringing the popup in means bringing the table in too.
            //
            // What is NOT acceptable is what used to stand in for it: "server:port" and two
            // buttons. Everything that decides whether a proxy can be accepted -- what KIND of
            // proxy it is, and the MTProto secret or the SOCKS credentials the link carries -- was
            // hidden behind a confirmation that then routed every byte the app sends through it.
            // The dialog now shows the same fields as the Windows popup, with the same labels and
            // in the same order, plus the sponsor warning MTProto proxies get there. Only the
            // "Status" row is missing, because pinging is the one thing here that reaches out to
            // the proxy before the user has agreed to anything.
            //
            // Values go inside `backticks`: TDLib's ParseMarkdown treats underscores as emphasis,
            // and an MTProto secret or a password is exactly where a stray "__" would eat
            // characters. A code entity is not styled by TextBlockHelper, so the value shows
            // verbatim.
            var kind = proxy.Type switch
            {
                ProxyTypeMtproto => Strings.UseProxyTelegram,
                ProxyTypeSocks5 => Strings.UseProxySocks5,
                _ => "HTTP Proxy"
            };

            var lines = new List<string>
            {
                "**" + kind + "**",
                string.Empty,
                $"{Strings.UseProxyAddress}: `{proxy.Server}`",
                $"{Strings.UseProxyPort}: `{proxy.Port}`"
            };

            if (proxy.Type is ProxyTypeMtproto mtproto)
            {
                if (!string.IsNullOrEmpty(mtproto.Secret))
                {
                    lines.Add($"{Strings.UseProxySecret}: `{mtproto.Secret}`");
                }

                lines.Add(string.Empty);
                lines.Add(Strings.UseProxyTelegramInfo2);
            }
            else
            {
                var username = proxy.Type switch
                {
                    ProxyTypeSocks5 socks5 => socks5.Username,
                    ProxyTypeHttp http => http.Username,
                    _ => null
                };

                var password = proxy.Type switch
                {
                    ProxyTypeSocks5 socks5 => socks5.Password,
                    ProxyTypeHttp http => http.Password,
                    _ => null
                };

                if (!string.IsNullOrEmpty(username))
                {
                    lines.Add($"{Strings.UseProxyUsername}: `{username}`");
                }

                if (!string.IsNullOrEmpty(password))
                {
                    lines.Add($"{Strings.UseProxyPassword}: `{password}`");
                }
            }

            var confirm = await navigation.ShowPopupAsync(string.Join(Environment.NewLine, lines), Strings.UseProxyTitle, Strings.ConnectingConnectProxy, Strings.Cancel);
            if (confirm == ContentDialogResult.Primary)
            {
                LifetimeService.Current.Proxy.AddProxy(proxy, Constants.RELEASE);
            }
        }
#else
        public static async void NavigateToProxy(IClientService clientService, INavigationService navigation, Proxy proxy)
        {
            if (proxy == null)
            {
                navigation.ShowToast(Strings.ProxyLinkUnsupported, ToastPopupIcon.Error);
                return;
            }

            var confirm = await navigation.ShowPopupAsync(new AddProxyPopup(clientService, navigation, proxy));
            if (confirm == ContentDialogResult.Primary)
            {
                LifetimeService.Current.Proxy.AddProxy(proxy, Constants.RELEASE);
            }
        }
#endif

        public static void NavigateToConfirmPhone(IClientService clientService, string phone, string hash)
        {
            //var response = await clientService.SendConfirmPhoneCodeAsync(hash, false);
            //if (response.IsSucceeded)
            //{
            //    var state = new SignInSentCodePage.NavigationParameters
            //    {
            //        PhoneNumber = phone,
            //        //Result = response.Result,
            //    };

            //    App.Current.NavigationService.Navigate(typeof(SignInSentCodePage), state);

            //    //Telegram.Api.Helpers.Execute.BeginOnUIThread(delegate
            //    //{
            //    //    if (frame != null)
            //    //    {
            //    //        frame.CloseBlockingProgress();
            //    //    }
            //    //    TelegramViewBase.NavigateToConfirmPhone(result);
            //    //});
            //}
            //else
            //{
            //    //if (error.CodeEquals(ErrorCode.BAD_REQUEST) && error.TypeEquals(ErrorType.USERNAME_NOT_OCCUPIED))
            //    //{
            //    //    return;
            //    //}
            //    //Telegram.Api.Helpers.Logs.Log.Write(string.Format("account.sendConfirmPhoneCode error {0}", error));
            //};
        }

        public static async void NavigateToStickerSet(INavigationService navigation, string text)
        {
            await StickersPopup.ShowAsync(navigation, text);
        }

        public static async void NavigateToPhoneNumber(IClientService clientService, INavigationService navigation, string phoneNumber, string draftText = null, bool openProfile = false)
        {
            await NavigateToUserByResponse(clientService, navigation, new SearchUserByPhoneNumber(phoneNumber, false), draftText, openProfile);
        }

        public static async void NavigateToUserToken(IClientService clientService, INavigationService navigation, string userToken)
        {
            await NavigateToUserByResponse(clientService, navigation, new SearchUserByToken(userToken));
        }

        private static async Task NavigateToUserByResponse(IClientService clientService, INavigationService navigation, Function request, string draftText = null, bool openProfile = false)
        {
            var response = await clientService.SendAsync(request);
            if (response is User user)
            {
                var chat = await clientService.SendAsync(new CreatePrivateChat(user.Id, false)) as Chat;
                if (chat != null)
                {
                    if (draftText != null)
                    {
                        navigation.NavigateToChat(chat, state: new NavigationState { { "draft", draftText.AsFormattedText() } });
                    }
                    else if (openProfile)
                    {
                        // ProfilePage IS in the subset (Views/ProfilePage.xaml plus the four fixes
                        // in Telegram.Linux/Xaml/ProfilePage.Linux.cs). The guard that used to send
                        // this to the chat was written when it was not, and it turned a link that
                        // asks IN SO MANY WORDS for the profile -- tg://user?...&profile -- into a
                        // link that opens the conversation instead.
                        navigation.Navigate(typeof(ProfilePage), chat.Id);
                    }
                    else
                    {
                        navigation.NavigateToChat(chat);
                    }
                }
                else
                {
                    navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
                }
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        public static async void NavigateToBotStart(IClientService clientService, INavigationService navigation, string username, string startParameter, bool autoStart, bool group)
        {
            var response = await clientService.SendAsync(new SearchPublicChat(username));
            if (response is Chat chat && clientService.TryGetUser(chat, out User user))
            {
#if LINUX
                // ChooseChatsPopup is outside the subset: the bot's own chat opens instead of the
                // picker for the group to add it to.
                if (autoStart)
#else
                if (group)
                {
                    navigation.ShowPopup(new ChooseChatsPopup(), new ChooseChatsConfigurationStartBot(user, startParameter));
                }
                else if (autoStart)
#endif
                {
                    clientService.Send(new SendBotStartMessage(user.Id, chat.Id, startParameter));
                    navigation.NavigateToChat(chat);
                }
                else
                {
                    navigation.NavigateToChat(chat, accessToken: startParameter);
                }
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        public static async void NavigateToBusinessChat(IClientService clientService, INavigationService navigation, string linkName)
        {
            var response = await clientService.SendAsync(new GetBusinessChatLinkInfo(linkName));
            if (response is BusinessChatLinkInfo info)
            {
                navigation.NavigateToChat(info.ChatId, state: new NavigationState { { "draft", info.Text } });
            }
        }

        public static async void NavigateToUsername(IClientService clientService, INavigationService navigation, string username, string videoChat = null, string game = null, string draftText = null, string referrer = null, bool openProfile = false)
        {
            var response = await clientService.SendAsync(referrer != null ? new SearchChatAffiliateProgram(username, referrer) : new SearchPublicChat(username));
            if (response is Chat chat)
            {
                if (game != null)
                {

                }
                else if (clientService.TryGetUser(chat, out User user))
                {
                    if (draftText != null)
                    {
                        navigation.NavigateToChat(chat, state: new NavigationState { { "draft", draftText.AsFormattedText() } });
                    }
                    // Same as NavigateToUserByResponse above: the profile page is compiled, so a
                    // t.me/<name>?profile link opens the profile on this build too.
                    else if (openProfile)
                    {
                        navigation.Navigate(typeof(ProfilePage), chat.Id);
                    }
                    else
                    {
                        navigation.NavigateToChat(chat);

                        if (chat.LastMessage != null && referrer != null)
                        {
                            clientService.Send(new SendBotStartMessage(user.Id, chat.Id, string.Empty));
                        }
                    }
                }
                else if (videoChat != null)
                {
                    navigation.NavigateToChat(chat, state: new NavigationState { { "videoChat", videoChat } });
                }
#if !LINUX
                else if (clientService.IsForum(chat))
                {
                    navigation.NavigateToForum(chat);
                }
#endif
                else
                {
                    navigation.NavigateToChat(chat);
                }
            }
            else if (referrer != null)
            {
                navigation.ShowPopup(Strings.AffiliateLinkExpiredText, Strings.AffiliateLinkExpiredTitle, Strings.OK);
            }
            else
            {
                navigation.ShowToast(Strings.NoUsernameFound, ToastPopupIcon.Info);
            }
        }

        public static async void NavigateToInviteLink(IClientService clientService, INavigationService navigation, string link)
        {
            var response = await clientService.CheckChatInviteLinkAsync(link);
            if (response is ChatInviteLinkInfo info)
            {
                if (info.ChatId != 0)
                {
                    navigation.NavigateToChat(info.ChatId);
                }
                else
                {
#if LINUX
                    // JoinChatPopup is in the subset now. It used to be a bare
                    // ShowPopupAsync(info.Title, ...): a title and two buttons, with no photo, no
                    // member count, no description and no way to tell a public channel from a
                    // private group, or a plain join from a request that goes to the admins -- the
                    // primary button even said "OK" where it should say "Request to Join". Asking
                    // someone to accept an invitation while hiding what they are joining is the
                    // same class of defect as the proxy one below, so the real popup was brought
                    // in: it needs ProfilePicture, IdentityIcon, CustomEmojiIcon, RecentUserHeads
                    // and Locale, all of which were already compiled.
                    var popup = new Views.Popups.JoinChatPopup(clientService, info);

                    var confirm = await navigation.ShowPopupAsync(popup);
#else
                    var popup = new JoinChatPopup(clientService, info);

                    var confirm = await navigation.ShowPopupAsync(popup);
#endif
                    if (confirm != ContentDialogResult.Primary)
                    {
                        return;
                    }

                    var import = await clientService.SendAsync(new JoinChatByInviteLink(link));
                    if (import is ChatJoinResultSuccess success)
                    {
                        navigation.NavigateToChat(success.ChatId);
                    }
                    else
                    {
                        HandleChatJoinResult(clientService, navigation, info.ChatId, info.Type is InviteLinkChatTypeChannel, import);
                    }
                }
            }
            else if (response is Error error)
            {
                if (error.MessageEquals(ErrorType.FLOOD_WAIT))
                {
                    navigation.ShowPopup(Strings.FloodWait, Strings.AppName, Strings.OK);
                }
                else
                {
                    navigation.ShowPopup(Strings.JoinToGroupErrorNotExist, Strings.AppName, Strings.OK);
                }
            }
        }

        public static async void HandleChatJoinResult(IClientService clientService, INavigationService navigation, long chatId, bool channel, Object result)
        {
#if LINUX
            // Guard bot approval runs inside the bot's mini app. What used to stand here was a
            // toast reading Strings.JoinToGroupErrorNotExist -- "Sorry, this chat doesn't seem to
            // exist." The chat exists, the link is good, and the only thing in the way is a bot
            // that has to approve you: the message was simply false, and it sent the user off to
            // ask whoever shared the link for a new one.
            //
            // The flow is the Windows one, with the substitution this port already makes for every
            // mini app spelled out in the confirmation instead of hidden: TLNavigationService's
            // Linux NavigateToWebApp hands the url to the browser, and a mini app opened outside
            // the client may well not be able to finish the approval. That is said before anything
            // is opened, so the user decides with the facts in front of them.
            if (result is ChatJoinResultGuardBotApprovalRequired approvalRequired && clientService.TryGetUser(approvalRequired.BotUserId, out User botUser))
            {
                var popup = new MessagePopup
                {
                    Title = Strings.AppName,
                    Message = string.Format("**{0}** has to approve you before you can join this chat.", botUser.FullName())
                        + Environment.NewLine + Environment.NewLine
                        + Strings.BotWebViewStartPermission
                        + Environment.NewLine + Environment.NewLine
                        + "This build has no in-app mini apps: the approval page opens in your browser, and it may not be able to complete the approval there.",
                    PrimaryButtonText = Strings.Start,
                    SecondaryButtonText = Strings.Cancel,
                };

                var confirm = await navigation.ShowPopupAsync(popup);
                if (confirm != ContentDialogResult.Primary)
                {
                    return;
                }

                var response = await clientService.SendAsync(new GetGuardBotWebAppUrl(approvalRequired.QueryId, new WebAppOpenParameters(navigation.Window.ThemeParameters, Constants.WebAppHostName, new WebAppOpenModeFullSize())));
                if (response is WebAppUrl webAppUrl)
                {
                    navigation.NavigateToWebApp(botUser, webAppUrl, source: new OpenUrlSourceJoinChatRequest(approvalRequired.QueryId, chatId));
                }
                else if (response is Error webAppError)
                {
                    navigation.ShowToast(webAppError);
                }
            }
            else if (result is ChatJoinResultGuardBotApprovalRequired)
            {
                // The bot is not in the cache, so it cannot be named -- but the reason is still
                // known, and it is not "this chat does not exist".
                navigation.ShowToast("A bot has to approve you before you can join this chat.", ToastPopupIcon.Info);
            }
            else
#else
            if (result is ChatJoinResultGuardBotApprovalRequired approvalRequired && clientService.TryGetUser(approvalRequired.BotUserId, out User botUser))
            {
                var popup = new MessagePopup
                {
                    Title = Strings.AppName,
                    Message = Strings.BotWebViewStartPermission,
                    PrimaryButtonText = Strings.Start,
                    SecondaryButtonText = Strings.Cancel,
                };

                var confirm = await navigation.ShowPopupAsync(popup);
                if (confirm != ContentDialogResult.Primary)
                {
                    return;
                }

                var response = await clientService.SendAsync(new GetGuardBotWebAppUrl(approvalRequired.QueryId, new WebAppOpenParameters(navigation.Window.ThemeParameters, Constants.WebAppHostName, new WebAppOpenModeFullSize())));
                if (response is WebAppUrl webAppUrl)
                {
                    navigation.NavigateToWebApp(botUser, webAppUrl, source: new OpenUrlSourceJoinChatRequest(approvalRequired.QueryId, chatId));
                }
                else if (response is Error error)
                {
                    navigation.ShowToast(error);
                }
            }
            else
#endif
            if (result is ChatJoinResultRequestSent)
            {
                await navigation.ShowPopupAsync(channel ? Strings.RequestToJoinChannelSentDescription : Strings.RequestToJoinGroupSentDescription, Strings.RequestToJoinSent, Strings.OK);
                return;

                var message = Strings.RequestToJoinSent + Environment.NewLine + (channel ? Strings.RequestToJoinChannelSentDescription : Strings.RequestToJoinGroupSentDescription);
                var entity = new TextEntity(0, Strings.RequestToJoinSent.Length, new TextEntityTypeBold());

                var text = new FormattedText(message, new[] { entity });

                ToastPopup.Show(navigation.XamlRoot, text, ToastPopupIcon.JoinRequested);
            }
            else if (result is ChatJoinResultDeclined)
            {
                navigation.ShowToast(string.Format(Strings.GuardBotJoinRequestDeclined, clientService.GetTitle(chatId)), ToastPopupIcon.Ban);
            }
            else if (result is Error error)
            {
#if !LINUX
                if (error.MessageEquals(ErrorType.CHANNELS_TOO_MUCH))
                {
                    navigation.ShowLimitReached(new PremiumLimitTypeSupergroupCount());
                }
                else
#endif
                {
                    navigation.ShowToast(error);
                }
            }
            else if (Constants.DEBUG && channel)
            {
                clientService.Send(new AddLocalMessage(chatId, new MessageSenderChat(chatId), null, true, new InputMessageContact(new Contact("999888777666", "SIMILAR", "CHANNELS", string.Empty, clientService.Options.MyId))));
            }
        }

        public static async void NavigateToChatFolderInviteLink(IClientService clientService, INavigationService navigation, string link)
        {
            var response = await clientService.SendAsync(new CheckChatFolderInviteLink(link));
            if (response is ChatFolderInviteLinkInfo info)
            {
                var popup = new AddFolderPopup();

                var confirm = await navigation.ShowPopupAsync(popup, info);
                if (confirm == ContentDialogResult.Primary)
                {
                    if (info.ChatFolderInfo.Id == 0)
                    {
                        var import = await clientService.SendAsync(new AddChatFolderByInviteLink(link, popup.SelectedItems.ToVector()));
                        if (import is Error error)
                        {
                            if (error.MessageEquals(ErrorType.CHATLISTS_TOO_MUCH))
                            {
                                navigation.ShowLimitReached(new PremiumLimitTypeShareableChatFolderCount());
                            }
                            else if (error.MessageEquals(ErrorType.FILTER_INCLUDE_TOO_MUCH))
                            {
                                navigation.ShowLimitReached(new PremiumLimitTypeChatFolderChosenChatCount());
                            }
                            else if (error.MessageEquals(ErrorType.CHANNELS_TOO_MUCH))
                            {
                                navigation.ShowLimitReached(new PremiumLimitTypeSupergroupCount());
                            }
                            else
                            {
                                navigation.ShowPopup(Strings.FolderLinkExpiredAlert, Strings.AppName, Strings.OK);
                            }
                        }
                    }
                    else if (popup.SelectedItems.Count > 0)
                    {
                        clientService.Send(new ProcessChatFolderNewChats(info.ChatFolderInfo.Id, popup.SelectedItems.ToVector()));
                    }
                }
            }
            else if (response is Error error)
            {
                navigation.ShowPopup(Strings.FolderLinkExpiredAlert, Strings.AppName, Strings.OK);
            }
        }

        public static bool IsValidUsername(string username)
        {
            if (username.Length <= 2)
            {
                return false;
            }
            if (username.Length > 32)
            {
                return false;
            }
            if (username[0] != '@')
            {
                return false;
            }
            for (int i = 1; i < username.Length; i++)
            {
                if (!IsValidUsernameSymbol(username[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsValidCommandSymbol(char symbol)
        {
            return (symbol >= 'a' && symbol <= 'z') || (symbol >= 'A' && symbol <= 'Z') || (symbol >= '0' && symbol <= '9') || symbol == '_';
        }

        public static bool IsValidUsernameSymbol(char symbol)
        {
            return (symbol >= 'a' && symbol <= 'z') || (symbol >= 'A' && symbol <= 'Z') || (symbol >= '0' && symbol <= '9') || symbol == '_';
        }

        public static async void OpenUrl(IClientService clientService, INavigationService navigationService, string url, bool untrust = false, OpenUrlSource source = null)
        {
            if (TryCreateUri(url, out Uri uri))
            {
                var telegramUrl = IsTelegramUrl(uri);
                if (telegramUrl && clientService != null && navigationService != null)
                {
                    OpenTelegramUrl(clientService, navigationService, uri, source);
                }
#if !LINUX
                // TON sites need the in-app browser (WebView2), which has no Linux counterpart yet.
                else if (clientService != null && navigationService != null && TonSite.TryCreate(clientService, uri, out string magic))
                {
                    if (navigationService is TLNavigationService tl)
                    {
                        tl.NavigateToWeb3(magic);
                    }
                }
#endif
                else
                {
                    if (untrust)
                    {
                        var confirm = await navigationService.ShowPopupAsync(string.Format(Strings.OpenUrlAlert, string.Format("[{0}]({0})", url)), Strings.OpenUrlTitle, Strings.Open, Strings.Cancel);
                        if (confirm != ContentDialogResult.Primary)
                        {
                            return;
                        }
                    }

                    try
                    {
#if LINUX
                        // LauncherOptions are not implemented by Uno; xdg-open takes the URI as is.
                        await Launcher.LaunchUriAsync(uri);
#else
                        var options = new LauncherOptions
                        {
                            IgnoreAppUriHandlers = telegramUrl
                        };

                        await Launcher.LaunchUriAsync(uri, options);
#endif
                    }
                    catch { }
                }
            }
        }

        #region Entity

#if !LINUX
        public static void Hyperlink_ContextRequested(ITranslateService service, UIElement sender, ContextRequestedEventArgs args, MessageViewModel message)
        {
            if (args.TryGetPosition(sender, out Point point))
            {
                var flyout = new MenuFlyout();

                if (sender is RichTextBlock text)
                {
                    Hyperlink_ContextRequested(flyout, service, text, point, message);
                }

                if (flyout.Items.Count > 0)
                {
                    // We don't want to unfocus the text are when the context menu gets opened
                    flyout.ShowAt(sender, new FlyoutShowOptions { Position = point, ShowMode = FlyoutShowMode.Transient });
                    args.Handled = true;
                }
                else
                {
                    args.Handled = false;
                }
            }
            else
            {
                args.Handled = false;
            }
        }

        public static void Hyperlink_ContextRequested(ITranslateService service, Hyperlink sender, ContextRequestedEventArgs args, MessageViewModel message)
        {
            var flyout = new MenuFlyout();

            Hyperlink_ContextRequested(flyout, service, sender, message);

            if (flyout.Items.Count > 0)
            {
                // We don't want to unfocus the text are when the context menu gets opened
                flyout.ShowAt(sender.ElementStart.VisualParent as FrameworkElement);
                args.Handled = true;
            }
            else
            {
                args.Handled = false;
            }
        }

        public static void Hyperlink_ContextRequested(ITranslateService service, UIElement sender, string text, ContextRequestedEventArgs args)
        {
            if (args.TryGetPosition(sender, out Point point))
            {
                var flyout = new MenuFlyout();

                Hyperlink_ContextRequested(sender.XamlRoot, flyout, service, text);

                if (flyout.Items.Count > 0)
                {
                    // We don't want to unfocus the text are when the context menu gets opened
                    flyout.ShowAt(sender, new FlyoutShowOptions { Position = point, ShowMode = FlyoutShowMode.Transient });
                    args.Handled = true;
                }
                else
                {
                    args.Handled = false;
                }
            }
            else
            {
                args.Handled = false;
            }
        }

        private static void Hyperlink_ContextRequested(XamlRoot xamlRoot, MenuFlyout flyout, ITranslateService service, string text)
        {
            var length = text.Length;
            if (length > 0)
            {
                flyout.CreateFlyoutItem(() => LinkCopy_Click(xamlRoot, text), Strings.Copy, Icons.Copy);

                if (service != null && service.CanTranslateText(text))
                {
                    var translate = flyout.CreateFlyoutItem(null as Action, Strings.TranslateMessage, Icons.Translate);

                    async void handler(object sender, RoutedEventArgs e)
                    {
                        translate.Click -= handler;

                        var language = LanguageIdentification.IdentifyLanguage(text);
                        var popup = new TranslatePopup(service, text, language, service.Settings.Translate.To, true);
                        await popup.ShowQueuedAsync(translate.XamlRoot);
                    }

                    translate.Click += handler;
                    translate.IsEnabled = true;
                }
            }
        }

        public static void Hyperlink_ContextRequested(MenuFlyout flyout, ITranslateService service, RichTextBlock text, Point point, MessageViewModel message)
        {
            if (point.X < 0 || point.Y < 0)
            {
                point = new Point(Math.Max(point.X, 0), Math.Max(point.Y, 0));
            }

            if (text.SelectedText.Length > 0)
            {
                Hyperlink_ContextRequested(text.XamlRoot, flyout, service, text.SelectedText);
            }
            else
            {
                var hyperlink = text.GetHyperlinkFromPoint(point);
                if (hyperlink != null)
                {
                    Hyperlink_ContextRequested(flyout, service, hyperlink, message);
                }
            }
        }

        public static void Hyperlink_ContextRequested(MenuFlyout flyout, ITranslateService service, Hyperlink hyperlink, MessageViewModel message)
        {
            var info = GetHyperlinkInfo(hyperlink);
            if (info == null)
            {
                return;
            }

            if (info.Type is null or TextEntityTypeUrl or TextEntityTypeTextUrl)
            {
                var action = GetEntityAction(hyperlink);
                if (action != null)
                {
                    flyout.CreateFlyoutItem(action, Strings.Open, Icons.OpenIn);
                }
                else
                {
                    flyout.CreateFlyoutItem(() => LinkOpen_Click(hyperlink.XamlRoot, info.Text), Strings.Open, Icons.OpenIn);
                }

                flyout.CreateFlyoutItem(() => LinkCopy_Click(hyperlink.XamlRoot, info.Text), Strings.CopyLink, Icons.Copy);
            }
            else if (info.Type is TextEntityTypePhoneNumber)
            {
                flyout.CreateFlyoutItem(() => TextCopy_Click(hyperlink.XamlRoot, info.Text), Strings.CopyNumber, Icons.Copy);
                flyout.CreateFlyoutSeparator();

                CreateProfileFlyoutItem(flyout, service.ClientService, hyperlink, new SearchUserByPhoneNumber(info.Text, false));
            }
            else if (info.Type is TextEntityTypeMention)
            {
                flyout.CreateFlyoutItem(() => TextCopy_Click(hyperlink.XamlRoot, info.Text), Strings.CopyUsername, Icons.Copy);
                flyout.CreateFlyoutSeparator();

                CreateProfileFlyoutItem(flyout, service.ClientService, hyperlink, new SearchPublicChat(info.Text));
            }
            else if (info.Type is TextEntityTypeDateTime dateTime)
            {
                flyout.Items.Add(new MenuFlyoutLabel
                {
                    Padding = new Thickness(12, 4, 12, 4),
                    MaxWidth = 178,
                    Text = Formatter.DateAt(dateTime.UnixTime)
                });

                flyout.CreateFlyoutSeparator();
                flyout.CreateFlyoutItem(() => TextCopy_Click(hyperlink.XamlRoot, info.Text), Strings.RelativeDateMenuCopy, Icons.Copy);
                //flyout.CreateFlyoutItem(() => AddToCalendar_Click(hyperlink.XamlRoot, dateTime.UnixTime, message), Strings.RelativeDateMenuAddToACalendar, Icons.Calendar);
                flyout.CreateFlyoutItem(() => SetAReminder_Click(hyperlink.XamlRoot, dateTime.UnixTime, message), Strings.RelativeDateMenuSetAReminder, Icons.Alert);
            }
            else
            {
                var text = info.Type switch
                {
                    TextEntityTypeHashtag or TextEntityTypeCashtag => Strings.CopyHashtag,
                    TextEntityTypeEmailAddress => Strings.CopyMail,
                    _ => Strings.Copy
                };

                flyout.CreateFlyoutItem(() => TextCopy_Click(hyperlink.XamlRoot, info.Text), text, Icons.Copy);
            }
        }

        //        private static async void AddToCalendar_Click(XamlRoot xamlRoot, int date, MessageViewModel message)
        //        {
        //            DateTime eventStart = Formatter.ToLocalTime(date).ToUniversalTime();

        //            string content = $@"BEGIN:VCALENDAR
        //VERSION:2.0
        //PRODID:-//Telegram//EN
        //BEGIN:VEVENT
        //UID:{Guid.NewGuid()}@yourdomain.com
        //DTSTAMP:{DateTime.UtcNow.ToString("yyyyMMddTHHmmss")}Z
        //DTSTART:{eventStart.ToString("yyyyMMddTHHmmss")}
        //SUMMARY:Event Title
        //END:VEVENT
        //END:VCALENDAR";

        //            var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync("event.ics", CreationCollisionOption.ReplaceExisting);

        //            await FileIO.WriteTextAsync(file, content);
        //            await Launcher.LaunchFileAsync(file);
        //        }

        private static async void SetAReminder_Click(XamlRoot xamlRoot, int date, MessageViewModel message)
        {
            var user = message.ClientService.GetUser(message.ClientService.Options.MyId);
            var popup = new ScheduleMessagePopup(message.ClientService, message.Delegate.NavigationService, user, true);

            var confirm = await message.Delegate.NavigationService.ShowPopupAsync(popup);

            if (popup.SchedulingState != null)
            {
                var options = new MessageSendOptions(null, false, false, 0, false, popup.SchedulingState, 0, 0, false);
                message.ClientService.Send(new ForwardMessages(message.ClientService.Options.MyId, null, message.ChatId, [message.Id], options, false, false));
            }
        }

        private static async void CreateProfileFlyoutItem(MenuFlyout flyout, IClientService clientService, Hyperlink hyperlink, Function function)
        {
            var profile = new ProfileCell();
            var button = new Button
            {
                Content = profile,
                Style = BootStrapper.Current.Resources["ListEmptyButtonStyle"] as Style,
                CornerRadius = new CornerRadius(4),
                IsEnabled = false
            };

            var content = new MenuFlyoutContent
            {
                Content = button,
                Height = 48,
                Width = 200,
                Padding = new Thickness(0)
            };

            void handler(object sender, RoutedEventArgs e)
            {
                profile.Loaded -= handler;
                profile.ShowHideSkeleton(true);
            }

            profile.Loaded += handler;

            flyout.Items.Add(content);

            var response = await clientService.SendAsync(function);
            if (response is User user)
            {
                button.IsEnabled = true;
                button.Click += (s, args) =>
                {
                    flyout.Hide();
                    WindowContext.GetNavigationService(hyperlink.XamlRoot).NavigateToUser(user.Id);
                };

                profile.Loaded -= handler;
                profile.ShowHideSkeleton(false);
                profile.UpdateUser(clientService, user, 36, true);
                profile.Subtitle = Strings.ViewProfile;
            }
            if (response is Chat chat)
            {
                button.IsEnabled = true;
                button.Click += (s, args) =>
                {
                    flyout.Hide();
                    WindowContext.GetNavigationService(hyperlink.XamlRoot).Navigate(typeof(ProfilePage), chat.Id);
                };

                profile.Loaded -= handler;
                profile.ShowHideSkeleton(false);
                profile.UpdateChat(clientService, chat, 36);
                profile.Subtitle = Strings.ViewProfile;
            }
            else
            {
                button.Content = new TextBlock
                {
                    Text = function is SearchPublicChat
                        ? Strings.UsernameNotOnTelegram
                        : Strings.NumberNotOnTelegram,
                    TextWrapping = TextWrapping.Wrap,
                    Style = BootStrapper.Current.Resources["InfoCaptionTextBlockStyle"] as Style,
                    Margin = new Thickness(12, 0, 12, 0)
                };
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.VerticalContentAlignment = VerticalAlignment.Center;
            }
        }
#endif

        public static void Hyperlink_ContextRequested(UIElement sender, string link, ContextRequestedEventArgs args)
        {
            if (args.TryGetPosition(sender, out Point point))
            {
                if (point.X < 0 || point.Y < 0)
                {
                    point = new Point(Math.Max(point.X, 0), Math.Max(point.Y, 0));
                }

                var flyout = new MenuFlyout();
                // El comentario que habia aqui («MenuFlyoutHelper (CreateFlyoutItem) is out of the
                // Linux subset») estaba caducado: MenuFlyoutHelper.cs entra en el csproj de Linux y
                // las unicas partes suyas bajo #if !LINUX son las lineas 11-13 y 82-111, ninguna de
                // ellas CreateFlyoutItem -- el menu contextual de la lista de chats lo usa desde la
                // tanda de integracion. Este menu era el unico del port que salia sin columna de
                // iconos.
                flyout.CreateFlyoutItem(() => LinkOpen_Click(sender.XamlRoot, link), Strings.Open, Icons.OpenIn);
                flyout.CreateFlyoutItem(() => LinkCopy_Click(sender.XamlRoot, link), Strings.Copy, Icons.Copy);

                // We don't want to unfocus the text are when the context menu gets opened
                flyout.ShowAt(sender, new FlyoutShowOptions { Position = point, ShowMode = FlyoutShowMode.Transient });

                args.Handled = true;
            }
        }

        private static async void LinkOpen_Click(XamlRoot xamlRoot, string link)
        {
            if (TryCreateUri(link, out Uri uri))
            {
                try
                {
                    await Launcher.LaunchUriAsync(uri);
                }
                catch
                {
                    Logger.Error();
                }
            }
        }

        private static void LinkCopy_Click(XamlRoot xamlRoot, string link)
        {
            CopyLink(xamlRoot, link);
        }

        private static void TextCopy_Click(XamlRoot xamlRoot, string link)
        {
            CopyText(xamlRoot, link);
        }



        public static Action GetEntityAction(DependencyObject obj)
        {
            return (Action)obj.GetValue(EntityActionProperty);
        }

        public static void SetEntityAction(DependencyObject obj, Action value)
        {
            obj.SetValue(EntityActionProperty, value);
        }

        public static readonly DependencyProperty EntityActionProperty =
            DependencyProperty.RegisterAttached("EntityAction", typeof(Action), typeof(MessageHelper), new PropertyMetadata(null));





        public static TextEntityClickEventArgs GetHyperlinkInfo(DependencyObject obj)
        {
            return (TextEntityClickEventArgs)obj.GetValue(HyperlinkInfoProperty);
        }

        public static void SetHyperlinkInfo(DependencyObject obj, TextEntityClickEventArgs value)
        {
            obj.SetValue(HyperlinkInfoProperty, value);
        }

        // TODO: FormattedTextBlock should not need this. The write measured ~3.6us per link,
        // which is a third of what building one costs, and the block can already map a point to a
        // rendered index and on to the entity that covers it (FormattedTextBlock.Selectable.cs) -
        // so the click could resolve its entity by offset and store nothing per link at all.
        //
        // Two things stand in the way. Hyperlink_ContextRequested above reads this from a hit test
        // for a link the caller found on its own, so that path has to ask the block instead. And a
        // ConditionalWeakTable is not a substitute: this lives on the native DependencyObject,
        // while a table keyed on the projection dies with the wrapper - which nothing keeps alive
        // for a Hyperlink no managed code holds, as SharedLinkCell and ProfileHeader build.
        public static readonly DependencyProperty HyperlinkInfoProperty =
            DependencyProperty.RegisterAttached("HyperlinkInfo", typeof(TextEntityClickEventArgs), typeof(MessageHelper), new PropertyMetadata(null));

        #endregion
    }

    public enum MessageCommandType
    {
        Invoke,
        Mention,
        Hashtag
    }
}
