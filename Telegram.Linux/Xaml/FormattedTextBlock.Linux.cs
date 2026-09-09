//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Telegram.Controls
{
    // Linux side of FormattedTextBlock. Uno's RichTextBlock is a stub on Skia, so the inner
    // control is a TextBlock (one Span per paragraph, LineBreaks in between, see
    // TextElementDirect) and whatever the Windows code reads from TextPointers or DirectWrite
    // metrics is answered from the inline tree or a scratch measurement instead.
    public partial class FormattedTextBlock
    {
        // Uno's TextBlock rejects InlineUIContainer outright, so custom emoji, math and inline
        // buttons render as the text they stand for.
        private const bool InlineUIContainerSupported = false;

        // MaxLines used to be answered here with a detached TextBlock of its own, because
        // PlaceholderImageHelper had none of the text metrics implemented. It has them now
        // (Telegram.Linux/Native/Text/UnoTextLayout.cs, same detached-measure trick but shared by
        // every metric and with the message font), so the shared MeasureOverride goes through
        // PlaceholderHelper.Foreground.MaxLines on Linux exactly as it does on Windows.

        // Highlighter index of the first unit inside `span` (inline mode: the host has inlines
        // of its own in front of the text). Run characters count 1 each, a LineBreak 1, the
        // same units TextHighlighter.Ranges use.
        private int GetContentOffset(Span span)
        {
            var index = 0;

            if (TextBlock != null)
            {
                WalkTo(TextBlock.Inlines, span, ref index);
            }

            return index;
        }

        private static bool WalkTo(InlineCollection inlines, Span target, ref int index)
        {
            foreach (var inline in inlines)
            {
                if (inline == target)
                {
                    return true;
                }

                switch (inline)
                {
                    case Run run:
                        index += run.Text?.Length ?? 0;
                        break;
                    case Span span:
                        if (WalkTo(span.Inlines, target, ref index))
                        {
                            return true;
                        }
                        break;
                    case LineBreak:
                        index += 1;
                        break;
                }
            }

            return false;
        }

        // ContentLength without a TextPointer: the end of the rendered space SetText built,
        // which the index map records (a null map is the plain single-paragraph fast path).
        private int GetRenderedLength()
        {
            var map = _indexMap;
            if (map != null)
            {
                return map.Count > 0 ? map[^1].Rendered + map[^1].RenderedLength : 0;
            }

            if (_text != null && _first == _last && _first >= 0 && _first < _text.Paragraphs.Count)
            {
                return _text.Paragraphs[_first].Text.Length;
            }

            return 0;
        }

        // The inner TextBlock the template declares or, while the template still says
        // RichTextBlock, a TextBlock put in its place with the same bindings, so the control
        // renders either way.
        private TextBlock GetOrCreateTextBlock()
        {
            var child = GetTemplateChild(nameof(TextBlock));
            if (child is TextBlock textBlock)
            {
                return textBlock;
            }

            if (child is not FrameworkElement placeholder || VisualTreeHelper.GetParent(placeholder) is not Panel parent)
            {
                return null;
            }

            textBlock = new TextBlock();
            Bind(textBlock, TextBlock.ForegroundProperty, nameof(Foreground));
            Bind(textBlock, TextBlock.PaddingProperty, nameof(Padding));
            Bind(textBlock, TextBlock.FontFamilyProperty, nameof(FontFamily));
            Bind(textBlock, TextBlock.FontSizeProperty, nameof(FontSize));
            Bind(textBlock, TextBlock.TextAlignmentProperty, nameof(TextAlignment));
            Bind(textBlock, TextBlock.TextTrimmingProperty, nameof(TextTrimming));
            Bind(textBlock, TextBlock.TextWrappingProperty, nameof(TextWrapping));
            Bind(textBlock, TextBlock.TextDecorationsProperty, nameof(TextDecorations));
            Bind(textBlock, TextBlock.MaxLinesProperty, nameof(MaxLines));

            var index = parent.Children.IndexOf(placeholder);
            parent.Children.RemoveAt(index);
            parent.Children.Insert(index, textBlock);

            return textBlock;
        }

        private void Bind(TextBlock target, DependencyProperty property, string path)
        {
            target.SetBinding(property, new Binding
            {
                Source = this,
                Path = new PropertyPath(path)
            });
        }

        // A XAML-declared Paragraph (inline mode): its inlines move into the TextBlock as one
        // Span, so the trailing Span the host renders into stays the same object.
        private void AdoptParagraph(Paragraph block)
        {
            var inlines = TextBlock.Inlines;
            if (inlines.Count > 0)
            {
                inlines.Add(new LineBreak());
            }

            var children = new List<Inline>(block.Inlines);
            block.Inlines.Clear();

            var span = new Span();
            foreach (var child in children)
            {
                span.Inlines.Add(child);
            }

            inlines.Add(span);
        }
    }
}
