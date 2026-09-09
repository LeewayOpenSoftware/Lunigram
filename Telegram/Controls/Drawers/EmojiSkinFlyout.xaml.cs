//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Linq;
using Telegram.Common;
using Telegram.Td.Api;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Drawers
{
    public sealed partial class EmojiSkinFlyout : Grid
    {
        private readonly EmojiDrawer _drawer;
        private readonly Flyout _flyout;
        private readonly EmojiSkinData _emoji;
        private readonly (string, string) _outlines;

        private EmojiSkinTone _tone1;
        private EmojiSkinTone _tone2;

        public EmojiSkinFlyout(EmojiDrawer drawer, Flyout flyout, EmojiSkinData emoji)
        {
            InitializeComponent();

            _drawer = drawer;
            _flyout = flyout;
            _emoji = emoji;

            var clean = Emoji.RemoveModifiers(emoji.Emoji);

            if (Emoji.EmojiGroupInternal._doubleSkinEmojis.Contains(emoji.Emoji))
            {
                _outlines = emoji.Emoji switch
                {
                    "\U0001F91D" => ("\uE001", "\uE002"),
                    "\U0001F46F\u200D\u2640\uFE0F" => ("\uE013", "\uE015"),
                    "\U0001F46F" => ("\uE016", "\uE017"),
                    "\U0001F46F\u200D\u2642\uFE0F" => ("\uE018", "\uE014"),
                    "\U0001F46B" => ("\uE003", "\uE004"),
                    "\U0001F46D" => ("\uE003", "\uE005"),
                    "\U0001F46C" => ("\uE006", "\uE004"),
                    "\U0001F469\u200D\u2764\uFE0F\u200D\U0001F468" => ("\uE007", "\uE008"),
                    "\U0001F469\u200D\u2764\uFE0F\u200D\U0001F469" => ("\uE007", "\uE009"),
                    "\U0001F9D1\u200D\u2764\uFE0F\u200D\U0001F9D1" => ("\uE00A", "\uE00B"),
                    "\U0001F468\u200D\u2764\uFE0F\u200D\U0001F468" => ("\uE00C", "\uE008"),
                    "\U0001F469\u200D\u2764\uFE0F\u200D\U0001F48B\u200D\U0001F468" => ("\uE00D", "\uE00E"),
                    "\U0001F469\u200D\u2764\uFE0F\u200D\U0001F48B\u200D\U0001F469" => ("\uE00D", "\uE00F"),
                    "\U0001F9D1\u200D\u2764\uFE0F\u200D\U0001F48B\u200D\U0001F9D1" => ("\uE010", "\uE011"),
                    "\U0001F468\u200D\u2764\uFE0F\u200D\U0001F48B\u200D\U0001F468" => ("\uE012", "\uE00E"),
                    "\U0001F93C\u200D\u2640\uFE0F" => ("\uE019", "\uE01B"),
                    "\U0001F93C" => ("\uE01C", "\uE01D"),
                    "\U0001F93C\u200D\u2642\uFE0F" => ("\uE01E", "\uE01A"),
                    _ => LogUnmappedDoubleSkinEmoji(emoji.Emoji)
                };

                var tone1 = new[]
                {
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz12, EmojiSkinTone.Fitz6),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz3, EmojiSkinTone.Fitz6),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz4, EmojiSkinTone.Fitz6),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz5, EmojiSkinTone.Fitz6),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz6, EmojiSkinTone.Fitz6),
                };

                var tone2 = new[]
                {
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz6, EmojiSkinTone.Fitz12),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz6, EmojiSkinTone.Fitz3),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz6, EmojiSkinTone.Fitz4),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz6, EmojiSkinTone.Fitz5),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz6, EmojiSkinTone.Fitz6),
                };

                DoubleRoot.Visibility = Visibility.Visible;
                Default.Text = emoji.Emoji;

                ScrollingHost1.SelectionChanged += OnSelectionChanged;
                ScrollingHost2.SelectionChanged += OnSelectionChanged;

                ScrollingHost1.ItemsSource = tone1;
                ScrollingHost2.ItemsSource = tone2;

                ScrollingHost1.SelectedItem = tone1.FirstOrDefault(x => x.Tone1 == emoji.Tone1);
                ScrollingHost2.SelectedItem = tone2.FirstOrDefault(x => x.Tone2 == emoji.Tone2);

                if (ScrollingHost1.SelectedItem == null && ScrollingHost2.SelectedItem == null)
                {
                    OnSelectionChanged(null, null);
                }

                MaxWidth = 208;
            }
            else
            {
                ScrollingHost1.ItemClick += OnItemClick;
                ScrollingHost1.ItemsSource = new[]
                {
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Default),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz12),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz3),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz4),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz5),
                    new EmojiSkinData(emoji.Emoji, EmojiSkinTone.Fitz6),
                };

                MaxWidth = 248;
            }

            MinWidth = 40;
            MinHeight = 40;
        }

        private static (string, string) LogUnmappedDoubleSkinEmoji(string emoji)
        {
            // _doubleSkinEmojis and this switch can drift apart; degrade honestly (no outlines)
            // instead of throwing a SwitchExpressionException out of a UI event handler.
            Logger.Error(emoji);
            return default;
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TextGridViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;
            }

            if (args.Item is EmojiData or Sticker)
            {
                args.ItemContainer.Margin = new Thickness(4, 4, 0, 4);
                args.ItemContainer.CornerRadius = new CornerRadius(4, 4, 4, 4);
            }
            else
            {
                args.ItemContainer.Margin = new Thickness();
                args.ItemContainer.CornerRadius = new CornerRadius();
            }

            args.ItemContainer.MinHeight = 0;

            args.ItemContainer.ContentTemplate = sender.ItemTemplate;
            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.Item is not EmojiSkinData emoji)
            {
                return;
            }

            AutomationProperties.SetName(args.ItemContainer, emoji.Value);

#if LINUX
            // OnChoosingItemContainer is dead on Uno (PORTING.md 6). Unlike the other drawers this
            // one used it for more than hooks: the container's Margin, CornerRadius and MinHeight
            // depend on the ITEM, so they are re-applied here, where the item is known. It hooked
            // neither a context menu nor the zoomer, so neither is invented.
            args.ItemContainer.Margin = args.Item is EmojiData or Sticker
                ? new Thickness(4, 4, 0, 4)
                : new Thickness();
            args.ItemContainer.CornerRadius = args.Item is EmojiData or Sticker
                ? new CornerRadius(4, 4, 4, 4)
                : new CornerRadius();
            args.ItemContainer.MinHeight = 0;
#endif

            if (args.ItemContainer.ContentRoot() is Grid content)
            {
                var textBlock = content.Children[0] as TextBlock;
                var outline = content.Children[1] as TextBlock;

                textBlock.Text = emoji.Value;
                outline.Text = sender == ScrollingHost1
                    ? _outlines.Item2 ?? string.Empty
                    : _outlines.Item1 ?? string.Empty;
            }

            args.Handled = true;
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is EmojiSkinData emoji)
            {
                _emoji.SetValue(emoji.Tone1);
                _drawer.InsertEmoji(_emoji);
                _flyout.Hide();
            }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var tone1 = ScrollingHost1.SelectedItem as EmojiSkinData;
            var tone2 = ScrollingHost2.SelectedItem as EmojiSkinData;

            if (tone1 != null && tone2 != null)
            {
                Outline1.Text = new EmojiSkinData(tone1.Emoji, tone1.Tone1, tone2.Tone2).Value;
                Outline2.Text = string.Empty;

                _tone1 = tone1.Tone1;
                _tone2 = tone2.Tone2;
                return;
            }

            if (tone1 != null)
            {
                Outline1.Text = tone1.Value;
                Outline2.Text = _outlines.Item2;
            }
            else if (tone2 != null)
            {
                Outline1.Text = tone2.Value;
                Outline2.Text = _outlines.Item1;
            }
            else
            {
                Outline1.Text = _outlines.Item1;
                Outline2.Text = _outlines.Item2;
            }

            _tone1 = EmojiSkinTone.Default;
            _tone2 = EmojiSkinTone.Default;
        }

        private void Default_Click(object sender, RoutedEventArgs e)
        {
            _emoji.SetValue(EmojiSkinTone.Default, EmojiSkinTone.Default);
            _drawer.InsertEmoji(_emoji);
            _flyout.Hide();
        }

        private void Outline_Click(object sender, RoutedEventArgs e)
        {
            if (_tone1 != EmojiSkinTone.Default && _tone2 != EmojiSkinTone.Default)
            {
                _emoji.SetValue(_tone1, _tone2);
                _drawer.InsertEmoji(_emoji);
                _flyout.Hide();
            }
        }
    }
}
