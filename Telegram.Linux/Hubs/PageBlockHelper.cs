//
// Telegram.Linux — the part of Telegram/Common/PageBlockHelper.cs the phase-1 subset can use.
//
// WHY THE UPSTREAM FILE IS OUT (PORTING.md rule 8). Common/PageBlockHelper.cs is 2.333 lines
// written against a NEWER TDLib schema than the tdjson this port vendors: it names
// RichTextButton, TextEntityTypeButton, InlineButton, PageBlockButtonRow, PageBlockDocument,
// PageBlockExpandableBlockQuote, PageBlockUnsupported, InputPageBlockButtonRow,
// PageBlockTable.IsCompact and a five-argument InputPageBlockTable — none of which exist in
// Libraries/tdjson/td_api.tl, so the generator never emits them. Compiling it produces 55 errors
// that have nothing to do with Uno or with Linux, and none of them can be fixed here: they need
// the vendored tdjson rebuilt from a newer schema. That is a separate job.
//
// WHAT THIS FILE IS. Everything the compiled subset actually calls, over the schema we do have:
//
//   - GetRichText / GetPlainText: the text projection of a rich message. Lifted VERBATIM out of
//     Td/Api/TdExtensions.cs, where it lived as a private nested class so ChatCell and the
//     automation peers could show something for a MessageRichMessage. It is public now because
//     the profile's shared-links tab needs it too, and one copy is better than two.
//   - GetLinks: every link reachable from the block tree, in document order. Needed by
//     SharedLinkCell, which is the whole content of the profile's "Links" tab for an instant-view
//     article. Same shape as upstream minus the RichTextButton arm (no such type here); since
//     upstream treats RichTextButton as a NO-link leaf anyway, dropping it changes nothing.
//   - FindFirstMedia: the first block of a requested kind, for the thumbnail SharedMediaCell and
//     SharedAudioCell draw. Same walk as upstream minus the PageBlockDocument arm — again a type
//     the schema lacks, and one that carries no preview, so no medium is lost.
//
// WHAT IS NOT HERE, and is not missing by accident: Flatten, ToInputRichMessage, ToPageBlocks,
// TryGetFormattedText and the whole block-diffing half. Every caller of those is already behind
// `#if !LINUX` (ComposeViewModel, DialogViewModel, MessageDelegate, PlaybackService), because
// composing and editing an instant-view article is not phase 1.
//
using System.Collections.Generic;
using Telegram.Td.Api;

namespace Telegram.Common
{
    /// <summary>Where a link was found, so a caller can tell a URL from a mention.</summary>
    public enum PageBlockLinkKind
    {
        Url,
        Email,
        Phone,
        Mention,
        ReferenceLink,
        AnchorLink,
        RelatedArticle,
        PhotoUrl,
    }

    public class PageBlockLink
    {
        public string Url { get; }
        public string Text { get; }
        public PageBlockLinkKind Kind { get; }

        public PageBlockLink(string url, string text, PageBlockLinkKind kind)
        {
            Url = url ?? string.Empty;
            Text = text ?? string.Empty;
            Kind = kind;
        }

        public override string ToString() => $"[{Kind}] {Url}" + (Text.Length > 0 && Text != Url ? $" ({Text})" : "");
    }

    /// <summary>Which media a caller is looking for. Same values and same groupings as upstream.</summary>
    public enum PageBlockMediaKind : uint
    {
        None = 0,
        Photo = 1u,
        Video = 2u,
        Animation = 4u,
        Audio = 8u,
        VoiceNote = 0x10u,
        Map = 0x20u,
        Embedded = 0x40u,
        EmbeddedPost = 0x80u,

        // Document = 0x100u upstream. pageBlockDocument is not in this port's schema, and it is
        // the one kind with no preview to show, so nothing visual is lost by its absence.

        Media = Photo | Video,
        Visual = Photo | Video | Animation,
        Audible = Audio | VoiceNote,
        Any = Photo | Video | Animation | Audio | VoiceNote | Map | Embedded | EmbeddedPost,
    }

    public static class PageBlockHelper
    {
        // =================================================================================
        // Text
        // =================================================================================

        public static RichTexts GetRichText(IReadOnlyList<PageBlock> blocks)
        {
            var pieces = new List<RichText>();
            Collect(blocks, pieces);

            var joined = new List<RichText>();
            foreach (var piece in pieces)
            {
                if (piece.IsNullOrEmpty())
                {
                    continue;
                }

                if (joined.Count > 0)
                {
                    joined.Add(new RichTextPlain("\n"));
                }

                joined.Add(piece);
            }

            return new RichTexts(joined.ToVector());
        }

        public static string GetPlainText(IReadOnlyList<PageBlock> blocks)
        {
            return GetRichText(blocks).ToPlainText();
        }

        private static void Collect(IReadOnlyList<PageBlock> blocks, List<RichText> pieces)
        {
            if (blocks == null)
            {
                return;
            }

            foreach (var block in blocks)
            {
                switch (block)
                {
                    case PageBlockTitle title: pieces.Add(title.Title); break;
                    case PageBlockSubtitle subtitle: pieces.Add(subtitle.Subtitle); break;
                    case PageBlockKicker kicker: pieces.Add(kicker.Kicker); break;
                    case PageBlockAuthorDate authorDate: pieces.Add(authorDate.Author); break;
                    case PageBlockHeader header: pieces.Add(header.Header); break;
                    case PageBlockSubheader subheader: pieces.Add(subheader.Subheader); break;
                    case PageBlockSectionHeading sectionHeading: pieces.Add(sectionHeading.Text); break;
                    case PageBlockThinking thinking: pieces.Add(thinking.Text); break;
                    case PageBlockFooter footer: pieces.Add(footer.Footer); break;
                    case PageBlockParagraph paragraph: pieces.Add(paragraph.Text); break;
                    case PageBlockPreformatted preformatted: pieces.Add(preformatted.Text); break;
                    case PageBlockPullQuote pullQuote:
                        pieces.Add(pullQuote.Text);
                        pieces.Add(pullQuote.Credit);
                        break;
                    case PageBlockBlockQuote blockQuote:
                        Collect(blockQuote.Blocks, pieces);
                        pieces.Add(blockQuote.Credit);
                        break;
                    case PageBlockList list:
                        if (list.Items != null)
                        {
                            foreach (var item in list.Items)
                            {
                                Collect(item.Blocks, pieces);
                            }
                        }
                        break;
                    case PageBlockDetails details:
                        pieces.Add(details.Header);
                        Collect(details.Blocks, pieces);
                        break;
                    case PageBlockTable table:
                        pieces.Add(table.Caption);
                        if (table.Cells != null)
                        {
                            foreach (var row in table.Cells)
                            {
                                foreach (var cell in row)
                                {
                                    pieces.Add(cell.Text);
                                }
                            }
                        }
                        break;
                    case PageBlockEmbeddedPost embeddedPost:
                        Collect(embeddedPost.Blocks, pieces);
                        pieces.Add(embeddedPost.Caption?.Text);
                        break;
                    case PageBlockCollage collage:
                        Collect(collage.Blocks, pieces);
                        pieces.Add(collage.Caption?.Text);
                        break;
                    case PageBlockSlideshow slideshow:
                        Collect(slideshow.Blocks, pieces);
                        pieces.Add(slideshow.Caption?.Text);
                        break;
                    case PageBlockCover cover:
                        Collect(new[] { cover.Cover }, pieces);
                        break;
                    case PageBlockPhoto photo: pieces.Add(photo.Caption?.Text); break;
                    case PageBlockVideo video: pieces.Add(video.Caption?.Text); break;
                    case PageBlockAnimation animation: pieces.Add(animation.Caption?.Text); break;
                    case PageBlockAudio audio: pieces.Add(audio.Caption?.Text); break;
                    case PageBlockVoiceNote voiceNote: pieces.Add(voiceNote.Caption?.Text); break;
                    case PageBlockMap map: pieces.Add(map.Caption?.Text); break;
                    case PageBlockEmbedded embedded: pieces.Add(embedded.Caption?.Text); break;
                }
            }
        }

        // =================================================================================
        // Links
        // =================================================================================

        /// <summary>Every link reachable from the block list, in document order.</summary>
        public static IList<PageBlockLink> GetLinks(IReadOnlyList<PageBlock> blocks)
        {
            var result = new List<PageBlockLink>();
            CollectLinksFromBlocks(blocks, result);
            return result;
        }

        private static void CollectLinksFromBlocks(IReadOnlyList<PageBlock> blocks, List<PageBlockLink> result)
        {
            if (blocks == null)
            {
                return;
            }

            foreach (var block in blocks)
            {
                CollectLinksFromBlock(block, result);
            }
        }

        private static void CollectLinksFromBlock(PageBlock block, List<PageBlockLink> result)
        {
            switch (block)
            {
                case null:
                    return;

                case PageBlockTitle t: CollectLinks(t.Title, result); return;
                case PageBlockSubtitle st: CollectLinks(st.Subtitle, result); return;
                case PageBlockKicker k: CollectLinks(k.Kicker, result); return;
                case PageBlockAuthorDate ad: CollectLinks(ad.Author, result); return;
                case PageBlockHeader h: CollectLinks(h.Header, result); return;
                case PageBlockSubheader sh: CollectLinks(sh.Subheader, result); return;
                case PageBlockSectionHeading sec: CollectLinks(sec.Text, result); return;
                case PageBlockThinking th: CollectLinks(th.Text, result); return;
                case PageBlockFooter f: CollectLinks(f.Footer, result); return;
                case PageBlockParagraph p: CollectLinks(p.Text, result); return;
                case PageBlockPreformatted pre: CollectLinks(pre.Text, result); return;

                case PageBlockPullQuote pq:
                    CollectLinks(pq.Text, result);
                    CollectLinks(pq.Credit, result);
                    return;
                case PageBlockBlockQuote bq:
                    CollectLinksFromBlocks(bq.Blocks, result);
                    CollectLinks(bq.Credit, result);
                    return;

                case PageBlockList list:
                    if (list.Items != null)
                    {
                        foreach (var item in list.Items)
                        {
                            CollectLinksFromBlocks(item.Blocks, result);
                        }
                    }
                    return;
                case PageBlockDetails details:
                    CollectLinks(details.Header, result);
                    CollectLinksFromBlocks(details.Blocks, result);
                    return;
                case PageBlockTable table:
                    CollectLinks(table.Caption, result);
                    if (table.Cells != null)
                    {
                        foreach (var row in table.Cells)
                        {
                            foreach (var cell in row)
                            {
                                CollectLinks(cell.Text, result);
                            }
                        }
                    }
                    return;
                case PageBlockCover cover:
                    CollectLinksFromBlock(cover.Cover, result);
                    return;
                case PageBlockEmbeddedPost ep:
                    CollectLinksFromBlocks(ep.Blocks, result);
                    CollectLinks(ep.Caption?.Text, result);
                    return;
                case PageBlockCollage collage:
                    CollectLinksFromBlocks(collage.Blocks, result);
                    CollectLinks(collage.Caption?.Text, result);
                    return;
                case PageBlockSlideshow slideshow:
                    CollectLinksFromBlocks(slideshow.Blocks, result);
                    CollectLinks(slideshow.Caption?.Text, result);
                    return;

                case PageBlockRelatedArticles related:
                    CollectLinks(related.Header, result);
                    if (related.Articles != null)
                    {
                        foreach (var article in related.Articles)
                        {
                            if (!string.IsNullOrEmpty(article?.Url))
                            {
                                result.Add(new PageBlockLink(article.Url, article.Title, PageBlockLinkKind.RelatedArticle));
                            }
                        }
                    }
                    return;

                case PageBlockPhoto photo:
                    if (!string.IsNullOrEmpty(photo.Url))
                    {
                        result.Add(new PageBlockLink(photo.Url, string.Empty, PageBlockLinkKind.PhotoUrl));
                    }
                    CollectLinks(photo.Caption?.Text, result);
                    return;

                case PageBlockVideo video: CollectLinks(video.Caption?.Text, result); return;
                case PageBlockAnimation animation: CollectLinks(animation.Caption?.Text, result); return;
                case PageBlockAudio audio: CollectLinks(audio.Caption?.Text, result); return;
                case PageBlockVoiceNote voiceNote: CollectLinks(voiceNote.Caption?.Text, result); return;
                case PageBlockMap map: CollectLinks(map.Caption?.Text, result); return;
                case PageBlockEmbedded embedded: CollectLinks(embedded.Caption?.Text, result); return;

                default:
                    return;
            }
        }

        private static void CollectLinks(RichText rt, List<PageBlockLink> result)
        {
            switch (rt)
            {
                case null:
                    return;

                case RichTexts rs:
                    if (rs.Texts != null)
                    {
                        foreach (var t in rs.Texts)
                        {
                            CollectLinks(t, result);
                        }
                    }
                    return;

                // Style wrappers: a link can be nested under styling.
                case RichTextBold b: CollectLinks(b.Text, result); return;
                case RichTextItalic b: CollectLinks(b.Text, result); return;
                case RichTextUnderline b: CollectLinks(b.Text, result); return;
                case RichTextStrikethrough b: CollectLinks(b.Text, result); return;
                case RichTextFixed b: CollectLinks(b.Text, result); return;
                case RichTextSubscript b: CollectLinks(b.Text, result); return;
                case RichTextSuperscript b: CollectLinks(b.Text, result); return;
                case RichTextMarked b: CollectLinks(b.Text, result); return;

                // Link-producing wrappers: emit the link and stop. A link's children are its
                // label, not nested links — Telegram never generates a nested anchor.
                case RichTextUrl u:
                    result.Add(new PageBlockLink(u.Url, PlainText(u.Text), PageBlockLinkKind.Url));
                    return;
                case RichTextEmailAddress e:
                    {
                        var text = PlainText(e.Text);
                        result.Add(new PageBlockLink(e.EmailAddress ?? text, text, PageBlockLinkKind.Email));
                        return;
                    }
                case RichTextPhoneNumber pn:
                    {
                        var text = PlainText(pn.Text);
                        result.Add(new PageBlockLink(pn.PhoneNumber ?? text, text, PageBlockLinkKind.Phone));
                        return;
                    }
                case RichTextReferenceLink rl:
                    result.Add(new PageBlockLink(rl.Url, PlainText(rl.Text), PageBlockLinkKind.ReferenceLink));
                    return;
                case RichTextAnchorLink al:
                    result.Add(new PageBlockLink(al.Url, PlainText(al.Text), PageBlockLinkKind.AnchorLink));
                    return;

                // Everything else is a leaf that carries no link (plain, custom emoji, icon,
                // anchor, date/time, formula, hashtag, cashtag, bot command, bank card).
                default:
                    return;
            }
        }

        private static string PlainText(RichText rt)
        {
            return rt?.ToPlainText() ?? string.Empty;
        }

        // =================================================================================
        // Media
        // =================================================================================

        /// <summary>The first block of one of the requested kinds, in document order.</summary>
        public static PageBlock FindFirstMedia(IReadOnlyList<PageBlock> blocks, PageBlockMediaKind kind)
        {
            if (blocks == null || kind == PageBlockMediaKind.None)
            {
                return null;
            }

            foreach (var block in blocks)
            {
                var match = FindFirstMediaCore(block, kind);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static PageBlock FindFirstMediaCore(PageBlock block, PageBlockMediaKind kind)
        {
            switch (block)
            {
                case null:
                    return null;

                case PageBlockPhoto when (kind & PageBlockMediaKind.Photo) != 0:
                case PageBlockVideo when (kind & PageBlockMediaKind.Video) != 0:
                case PageBlockAnimation when (kind & PageBlockMediaKind.Animation) != 0:
                case PageBlockAudio when (kind & PageBlockMediaKind.Audio) != 0:
                case PageBlockVoiceNote when (kind & PageBlockMediaKind.VoiceNote) != 0:
                case PageBlockMap when (kind & PageBlockMediaKind.Map) != 0:
                case PageBlockEmbedded when (kind & PageBlockMediaKind.Embedded) != 0:
                case PageBlockEmbeddedPost when (kind & PageBlockMediaKind.EmbeddedPost) != 0:
                    return block;

                // Containers: descend in document order, but only when the block itself did not
                // match. EmbeddedPost is both, and the arm above takes precedence.
                case PageBlockCover cover:
                    return FindFirstMediaCore(cover.Cover, kind);
                case PageBlockList list:
                    if (list.Items != null)
                    {
                        foreach (var item in list.Items)
                        {
                            var m = FindFirstMediaInList(item.Blocks, kind);
                            if (m != null) return m;
                        }
                    }
                    return null;
                case PageBlockDetails details:
                    return FindFirstMediaInList(details.Blocks, kind);
                case PageBlockCollage collage:
                    return FindFirstMediaInList(collage.Blocks, kind);
                case PageBlockSlideshow slideshow:
                    return FindFirstMediaInList(slideshow.Blocks, kind);
                case PageBlockBlockQuote blockquote:
                    return FindFirstMediaInList(blockquote.Blocks, kind);
                case PageBlockEmbeddedPost ep:
                    return FindFirstMediaInList(ep.Blocks, kind);

                default:
                    return null;
            }
        }

        private static PageBlock FindFirstMediaInList(IReadOnlyList<PageBlock> blocks, PageBlockMediaKind kind)
        {
            if (blocks == null)
            {
                return null;
            }

            foreach (var b in blocks)
            {
                var m = FindFirstMediaCore(b, kind);
                if (m != null)
                {
                    return m;
                }
            }

            return null;
        }
    }
}
