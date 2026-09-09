//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Telegram.Native;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Telegram.Controls
{
    // The element kinds the run builder creates. Paragraph is a Span here: Uno's RichTextBlock
    // is a stub on Skia, so the text lives in a TextBlock, one Span per paragraph separated by
    // LineBreaks (TextBlockBlocks below).
    public enum TextElementType
    {
        Paragraph,
        Span,
        Run
    }

    public enum TextElementProperty
    {
        Block_TextAlignment,
        Paragraph_Inlines,
        RichTextBlock_Blocks,
        Run_FlowDirection,
        Run_Text,
        Hyperlink_UnderlineStyle,
        Span_Inlines,
        TextElement_FontFamily,
        TextElement_FontSize,
        TextElement_FontStyle,
        TextElement_FontWeight,
        TextElement_Foreground,
        TextElement_TextDecorations
    }

    // Stands in for Windows.UI.Xaml.Core.Direct.XamlDirect and NativeUtils.AddRunToCollection:
    // the call shape FormattedTextBlock's builder already uses, over the managed
    // Run/Span/Hyperlink objects. The objects are the elements themselves, nothing is wrapped.
    public sealed class TextElementDirect
    {
        private static readonly TextElementDirect _default = new();

        private TextElementDirect()
        {
        }

        public static TextElementDirect GetDefault()
        {
            return _default;
        }

        public object CreateInstance(TextElementType type)
        {
            return type switch
            {
                TextElementType.Paragraph => new Span(),
                TextElementType.Span => new Span(),
                TextElementType.Run => new Run(),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }

        public object GetXamlDirectObject(object value)
        {
            return value;
        }

        public object GetObject(object value)
        {
            return value;
        }

        public object GetXamlDirectObjectProperty(object target, TextElementProperty property)
        {
            return property switch
            {
                TextElementProperty.Paragraph_Inlines => ((Span)target).Inlines,
                TextElementProperty.Span_Inlines => ((Span)target).Inlines,
                TextElementProperty.RichTextBlock_Blocks => new TextBlockBlocks((TextBlock)target),
                _ => throw new ArgumentOutOfRangeException(nameof(property))
            };
        }

        public void AddToCollection(object collection, object value)
        {
            switch (collection)
            {
                case InlineCollection inlines:
                    inlines.Add((Inline)value);
                    break;
                case TextBlockBlocks blocks:
                    blocks.Add((Inline)value);
                    break;
            }
        }

        public void ClearCollection(object collection)
        {
            switch (collection)
            {
                case InlineCollection inlines:
                    inlines.Clear();
                    break;
                case TextBlockBlocks blocks:
                    blocks.Clear();
                    break;
            }
        }

        public void SetStringProperty(object target, TextElementProperty property, string value)
        {
            if (property == TextElementProperty.Run_Text)
            {
                ((Run)target).Text = value ?? string.Empty;
            }
        }

        public void SetDoubleProperty(object target, TextElementProperty property, double value)
        {
            if (property == TextElementProperty.TextElement_FontSize)
            {
                ((TextElement)target).FontSize = value;
            }
        }

        public void SetEnumProperty(object target, TextElementProperty property, uint value)
        {
            switch (property)
            {
                case TextElementProperty.Hyperlink_UnderlineStyle:
                    ((Hyperlink)target).UnderlineStyle = (UnderlineStyle)value;
                    break;
                case TextElementProperty.Run_FlowDirection:
                    ((Run)target).FlowDirection = (FlowDirection)value;
                    break;
                case TextElementProperty.TextElement_FontStyle:
                    ((TextElement)target).FontStyle = (FontStyle)value;
                    break;
                case TextElementProperty.TextElement_TextDecorations:
                    ((TextElement)target).TextDecorations = (TextDecorations)value;
                    break;
                case TextElementProperty.Block_TextAlignment:
                    // A Span has no alignment; the TextBlock's own TextAlignment (template
                    // bound to the control's) already covers the per-paragraph case.
                    break;
            }
        }

        // A null value reads as "not set", like the XamlDirect code that passes null to drop
        // a Foreground: the element inherits again. TextElement.Foreground only takes a
        // SolidColorBrush on Uno, which every brush that reaches here is.
        public void SetObjectProperty(object target, TextElementProperty property, object value)
        {
            if (value == null)
            {
                ClearProperty(target, property);
                return;
            }

            switch (property)
            {
                case TextElementProperty.TextElement_FontWeight:
                    ((TextElement)target).FontWeight = (FontWeight)value;
                    break;
                case TextElementProperty.TextElement_FontFamily:
                    ((TextElement)target).FontFamily = (FontFamily)value;
                    break;
                case TextElementProperty.TextElement_Foreground:
                    ((TextElement)target).Foreground = (Brush)value;
                    break;
            }
        }

        public void ClearProperty(object target, TextElementProperty property)
        {
            var dp = property switch
            {
                TextElementProperty.TextElement_FontFamily => TextElement.FontFamilyProperty,
                TextElementProperty.TextElement_FontSize => TextElement.FontSizeProperty,
                TextElementProperty.TextElement_FontStyle => TextElement.FontStyleProperty,
                TextElementProperty.TextElement_FontWeight => TextElement.FontWeightProperty,
                TextElementProperty.TextElement_Foreground => TextElement.ForegroundProperty,
                TextElementProperty.TextElement_TextDecorations => TextElement.TextDecorationsProperty,
                _ => null
            };

            if (dp != null)
            {
                ((DependencyObject)target).ClearValue(dp);
            }
        }

        public static object AddRunToCollection(TextElementDirect direct, object inlines, string text, FlowDirection direction, TextStyle style, FontFamily fontFamily, double fontSize, bool transparent)
        {
            return AddRunToCollection(direct, inlines, text, 0, text?.Length ?? 0, direction, style, fontFamily, fontSize, transparent);
        }

        // Mirrors Telegram.Native's AddRunToCollection (and FormattedTextBlock.ApplyRunProperties
        // for the pooled case). `transparent` marks the zero-width ZWNJ/LTR/RTL helpers, which
        // have nothing to paint either way, so the foreground is simply left inherited.
        public static object AddRunToCollection(TextElementDirect direct, object inlines, string text, int offset, int length, FlowDirection direction, TextStyle style, FontFamily fontFamily, double fontSize, bool transparent)
        {
            var run = new Run
            {
                Text = text == null ? string.Empty : offset == 0 && length == text.Length ? text : text.Substring(offset, length),
                FlowDirection = direction
            };

            if ((style & TextStyle.Bold) != TextStyle.None)
            {
                run.FontWeight = FontWeights.SemiBold;
            }

            if ((style & TextStyle.Italic) != TextStyle.None)
            {
                run.FontStyle = FontStyle.Italic;
            }

            var decorations = TextDecorations.None;
            if ((style & TextStyle.Underline) != TextStyle.None)
            {
                decorations |= TextDecorations.Underline;
            }
            if ((style & TextStyle.Strikethrough) != TextStyle.None)
            {
                decorations |= TextDecorations.Strikethrough;
            }

            if (decorations != TextDecorations.None)
            {
                run.TextDecorations = decorations;
            }

            if (fontFamily != null)
            {
                run.FontFamily = fontFamily;
            }

            if (fontSize > 0)
            {
                run.FontSize = fontSize;
            }

            direct.AddToCollection(inlines, run);
            return run;
        }

        // A TextBlock's Inlines seen as the RichTextBlock's Blocks: every Add is a paragraph,
        // kept apart from the previous one by a LineBreak.
        private sealed class TextBlockBlocks
        {
            private readonly TextBlock _textBlock;

            public TextBlockBlocks(TextBlock textBlock)
            {
                _textBlock = textBlock;
            }

            public void Add(Inline paragraph)
            {
                var inlines = _textBlock.Inlines;
                if (inlines.Count > 0)
                {
                    inlines.Add(new LineBreak());
                }

                inlines.Add(paragraph);
            }

            public void Clear()
            {
                _textBlock.Inlines.Clear();
            }
        }
    }
}
