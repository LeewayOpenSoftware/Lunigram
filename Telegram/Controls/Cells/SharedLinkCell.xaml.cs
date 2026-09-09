//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Telegram.Common;
using Telegram.Navigation.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Cells
{
    public sealed partial class SharedLinkCell : Grid
    {
        private MessageWithOwner _message;
        private INavigationService _navigationService;

        public SharedLinkCell()
        {
            InitializeComponent();
        }

        public void UpdateMessage(INavigationService navigationService, MessageWithOwner message)
        {
            _navigationService = navigationService;
            _message = message;

            var links = new List<string>();

            string title = null;
            string description = null;
            string description2 = null;
            string webPageLink = null;
            bool webPageCached = false;
            Thumbnail webPageThumbnail = null;
            Minithumbnail webPageMinithumbnail = null;

            if (message.Content is MessageText { LinkPreview: LinkPreview linkPreview })
            {

                title = linkPreview.Title;
                if (string.IsNullOrEmpty(title))
                {
                    title = linkPreview.SiteName;
                }

                description = string.IsNullOrEmpty(linkPreview.Description?.Text) ? null : linkPreview.Description?.Text;
                webPageLink = linkPreview.Url;
                webPageCached = linkPreview.InstantViewVersion != 0;
                webPageThumbnail = linkPreview.GetThumbnail();
                webPageMinithumbnail = linkPreview.GetMinithumbnail();
            }
            else if (message.Content is MessageRichMessage richMessage)
            {
                foreach (var link in PageBlockHelper.GetLinks(richMessage.Message.Blocks))
                {
                    links.Add(link.Url);
                }

                description = PageBlockHelper.GetPlainText(richMessage.Message.Blocks);
            }

            var caption = message.GetCaption();
            if (caption?.Entities.Count > 0)
            {
                for (int a = 0; a < caption.Entities.Count; a++)
                {
                    var entity = caption.Entities[a];
                    if (entity.Length <= 0 || entity.Offset < 0 || entity.Offset >= caption.Text.Length)
                    {
                        continue;
                    }
                    else if (entity.Offset + entity.Length > caption.Text.Length)
                    {
                        entity.Length = caption.Text.Length - entity.Offset;
                    }

                    if (a == 0 && webPageLink != null && !(entity.Offset == 0 && entity.Length == caption.Text.Length))
                    {
                        if (caption.Entities.Count == 1)
                        {
                            if (description == null)
                            {
                                description2 = caption.Text;
                            }
                        }
                        else
                        {
                            description2 = caption.Text;
                        }
                    }

                    try
                    {
                        string link = null;
                        if (entity.Type is TextEntityTypeTextUrl or TextEntityTypeUrl)
                        {
                            if (entity.Type is TextEntityTypeUrl)
                            {
                                link = caption.Text.Substring(entity.Offset, entity.Length);
                            }
                            else if (entity.Type is TextEntityTypeTextUrl textUrl)
                            {
                                link = textUrl.Url;
                            }
                            if (title == null || title.Length == 0)
                            {
                                title = link;
                                var url = link;
                                if (url.StartsWith("http") == false)
                                {
                                    url = "http://" + url;
                                }

                                var uri = new Uri(url);
                                title = uri.Host;
                                title ??= link;
                                int index;
                                if (title != null && (index = title.LastIndexOf('.')) >= 0)
                                {
                                    title = title.Substring(0, index);
                                    if ((index = title.LastIndexOf('.')) >= 0)
                                    {
                                        title = title.Substring(index + 1);
                                    }
                                    title = title.Substring(0, 1).ToUpper() + title.Substring(1);
                                }
                                if (entity.Offset != 0 || entity.Length != caption.Text.Length)
                                {
                                    description = caption.Text;
                                }
                            }
                        }
                        else if (entity.Type is TextEntityTypeEmailAddress)
                        {
                            if (title == null || title.Length == 0)
                            {
                                link = "mailto:" + caption.Text.Substring(entity.Offset, entity.Length);
                                title = caption.Text.Substring(entity.Offset, entity.Length);
                                if (entity.Offset != 0 || entity.Length != caption.Text.Length)
                                {
                                    description = caption.Text;
                                }
                            }
                        }
                        if (link != null)
                        {
                            if (link.ToLower().IndexOf("http") != 0 && link.ToLower().IndexOf("mailto") != 0)
                            {
                                links.Add("http://" + link);
                            }
                            else
                            {
                                links.Add(link);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        //FileLog.e(e);
                    }
                }
            }

            if (webPageLink != null && links.Count == 0)
            {
                links.Add(webPageLink);
            }

            //if (hasThumb)
            //{
            //    Thumbnail.Visibility = Visibility.Visible;
            //}
            //else
            //{
            //    Thumbnail.Visibility = Visibility.Collapsed;
            //}

            if (title != null)
            {
                TitleLabel.Text = title.Replace('\n', ' ');
                TitleLabel.Visibility = Visibility.Visible;
            }
            else
            {
                TitleLabel.Visibility = Visibility.Collapsed;
            }

            if (description != null)
            {
                DescriptionLabel.Text = description;
                DescriptionLabel.Visibility = Visibility.Visible;
            }
            else
            {
                DescriptionLabel.Visibility = Visibility.Collapsed;
            }

            if (description2 != null)
            {
                Description2Label.Text = description2;
                Description2Label.Visibility = Visibility.Visible;

                if (description != null)
                {
                    Description2Label.Margin = new Thickness(0, 8, 0, 0);
                }
                else
                {
                    Description2Label.Margin = new Thickness(0);
                }
            }
            else
            {
                Description2Label.Visibility = Visibility.Collapsed;
            }

            LinksPanel.Children.Clear();
            LinksPanel.RowDefinitions.Clear();

            Photo.Source = null;

            if (webPageThumbnail != null)
            {
                Photo.Source = new ProfilePictureSourcePhoto(message.ClientService, message.Id, webPageThumbnail.File, webPageMinithumbnail);
            }

            for (int i = 0; i < links.Count; i++)
            {
                var link = links[i];
                if (MessageHelper.TryCreateUri(link, out Uri uri))
                {
                    Photo.Source ??= ProfilePictureSourceText.GetNameForChat(uri.Host, uri.GetHashCode());

#if LINUX
                    // RichTextBlock is [NotImplemented] for __SKIA__ in Uno.UI itself: it measures
                    // as an empty box and draws not one letter, without throwing and without a
                    // warning, so this whole tab would have been a column of blank rows. A
                    // TextBlock takes the same Hyperlink -- one level less of nesting, because a
                    // TextBlock has Inlines and no Blocks. Same substitution as ChatFolderCell's
                    // title, PORTING.md 6.
                    var textBlock = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = false };
#else
                    var textBlock = new RichTextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = false };
                    var paragraph = new Paragraph();
#endif
                    var hyperlink = new Hyperlink { UnderlineStyle = UnderlineStyle.None };

                    if (link == webPageLink && webPageCached)
                    {
                        hyperlink.Inlines.Add(new Run { Text = "\uE611", FontSize = 12, FontFamily = Navigation.BootStrapper.Current.Resources["TelegramThemeFontFamily"] as FontFamily });
                        hyperlink.Inlines.Add(new Run { Text = " \u200D" });

                        hyperlink.Click += (s, args) => InstantView_Click(s, link);
                    }
                    else
                    {
                        hyperlink.Click += (s, args) => Hyperlink_Click(s, uri);
                    }

                    hyperlink.Inlines.Add(new Run { Text = link });
#if LINUX
                    textBlock.Inlines.Add(hyperlink);
                    textBlock.Inlines.Add(new Run { Text = " " });
#else
                    paragraph.Inlines.Add(hyperlink);
                    paragraph.Inlines.Add(new Run { Text = " " });
                    textBlock.Blocks.Add(paragraph);
#endif
#if LINUX
                    // MessageHelper keeps only Hyperlink_ContextRequested(sender, link, args) on
                    // Linux -- the overload Paragraph_ContextRequested calls walks a
                    // RichTextBlock's selection, and RichTextBlock is [NotImplemented] for
                    // __SKIA__ (this cell is a TextBlock here, see UpdateMessage). The link of
                    // this row is in hand, so capture it and use the overload that exists.
                    var capturedLink = link;
                    textBlock.ContextRequested += (s, a) => MessageHelper.Hyperlink_ContextRequested(s, capturedLink, a);
#else
                    textBlock.ContextRequested += Paragraph_ContextRequested;
#endif

                    MessageHelper.SetHyperlinkInfo(hyperlink, new TextEntityClickEventArgs(null, link));

                    Extensions.SetToolTip(hyperlink, link);
                    SetRow(textBlock, i);

                    LinksPanel.RowDefinitions.Add(1, GridUnitType.Auto);
                    LinksPanel.Children.Add(textBlock);
                }
            }
        }

#if !LINUX
        private void Paragraph_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            MessageHelper.Hyperlink_ContextRequested(null, sender, args, null);
        }
#endif

        private void InstantView_Click(Hyperlink sender, string link)
        {
            _navigationService.NavigateToInstant(link);
        }

        private void Hyperlink_Click(Hyperlink sender, Uri uri)
        {
            MessageHelper.OpenUrl(_message.ClientService, _navigationService, uri.ToString());
        }

        private void Thumbnail_Click(object sender, RoutedEventArgs e)
        {
            if (_message?.Content is MessageText text && text.LinkPreview != null)
            {
                MessageHelper.OpenUrl(_message.ClientService, _navigationService, text.LinkPreview.Url);
            }
        }
    }
}
