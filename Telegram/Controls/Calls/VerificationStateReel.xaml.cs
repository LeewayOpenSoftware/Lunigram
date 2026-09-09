//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Navigation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Telegram.Controls.Calls
{
    public sealed partial class VerificationStateReel : UserControl
    {
        private static readonly Random _random = new();

        private static readonly string[] _availableEmoji = new[]
        {
            "😉", "😍", "😛", "😭", "😱", "😡", "😎", "😴", "😵", "😈", "😬", "😇", "😏", "👮", "👷", "💂", "👶", "👨", "👩", "👴", "👵", "😻", "😽", "🙀", "👺", "🙈", "🙉", "🙊", "💀", "👽", "💩", "🔥", "💥", "💤", "👂", "👀", "👃", "👅", "👄", "👍", "👎", "👌", "👊", "✌️", "✋️", "👐", "👆", "👇", "👉", "👈", "🙏", "👏", "💪", "🚶", "🏃", "💃", "👫", "👪", "👬", "👭", "💅", "🎩", "👑", "👒", "👟", "👞", "👠", "👕", "👗", "👖", "👙", "👜", "👓", "🎀", "💄", "💛", "💙", "💜", "💚", "💍", "💎", "🐶", "🐺", "🐱", "🐭", "🐹", "🐰", "🐸", "🐯", "🐨", "🐻", "🐷", "🐮", "🐗", "🐴", "🐑", "🐘", "🐼", "🐧", "🐥", "🐔", "🐍", "🐢", "🐛", "🐝", "🐜", "🐞", "🐌", "🐙", "🐚", "🐟", "🐬", "🐋", "🐐", "🐊", "🐫", "🍀", "🌹", "🌻", "🍁", "🌾", "🍄", "🌵", "🌴", "🌳", "🌞", "🌚", "🌙", "🌎", "🌋", "⚡️", "☔️", "❄️", "⛄️", "🌀", "🌈", "🌊", "🎓", "🎆", "🎃", "👻", "🎅", "🎄", "🎁", "🎈", "🔮", "🎥", "📷", "💿", "💻", "☎️", "📡", "📺", "📻", "🔉", "🔔", "⏳", "⏰", "⌚️", "🔒", "🔑", "🔎", "💡", "🔦", "🔌", "🔋", "🚿", "🚽", "🔧", "🔨", "🚪", "🚬", "💣", "🔫", "🔪", "💊", "💉", "💰", "💵", "💳", "✉️", "📫", "📦", "📅", "📁", "✂️", "📌", "📎", "✒️", "✏️", "📐", "📚", "🔬", "🔭", "🎨", "🎬", "🎤", "🎧", "🎵", "🎹", "🎻", "🎺", "🎸", "👾", "🎮", "🃏", "🎲", "🎯", "🏈", "🏀", "⚽️", "⚾️", "🎾", "🎱", "🏉", "🎳", "🏁", "🏇", "🏆", "🏊", "🏄", "☕️", "🍼", "🍺", "🍷", "🍴", "🍕", "🍔", "🍟", "🍗", "🍱", "🍚", "🍜", "🍡", "🍳", "🍞", "🍩", "🍦", "🎂", "🍰", "🍪", "🍫", "🍭", "🍯", "🍎", "🍏", "🍊", "🍋", "🍒", "🍇", "🍉", "🍓", "🍑", "🍌", "🍐", "🍍", "🍆", "🍅", "🌽", "🏡", "🏥", "🏦", "⛪️", "🏰", "⛺️", "🏭", "🗻", "🗽", "🎠", "🎡", "⛲️", "🎢", "🚢", "🚤", "⚓️", "🚀", "✈️", "🚁", "🚂", "🚋", "🚎", "🚌", "🚙", "🚗", "🚕", "🚛", "🚨", "🚔", "🚒", "🚑", "🚲", "🚠", "🚜", "🚦", "⚠️", "🚧", "⛽️", "🎰", "🗿", "🎪", "🎭", "🇯🇵", "🇰🇷", "🇩🇪", "🇨🇳", "🇺🇸", "🇫🇷", "🇪🇸", "🇮🇹", "🇷🇺", "🇬🇧", "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣", "0️⃣", "🔟", "❗️", "❓", "♥️", "♦️", "💯", "🔗", "🔱", "🔴", "🔵", "🔶", "🔷"
        };

        private readonly TextBlock[] _blocks;

        private int _index;
        private int _column;

        private int _generation = -1;
        private string _emoji = string.Empty;

        private int _remanining;
        private bool _playing;

        private string[] _subset;

        public VerificationStateReel()
        {
            InitializeComponent();

            _subset = new string[8];
            _blocks = new TextBlock[4];

            RandomizeSubset();

            for (int i = 0; i < 4; i++)
            {
                _blocks[i] = ((Border)RootGrid.Children[i]).Child as TextBlock;
                ElementCompositionPreview.SetIsTranslationEnabled(RootGrid.Children[i], true);
            }
        }

        private void RandomizeSubset()
        {
            for (int i = 0; i < _subset.Length; i++)
            {
                _subset[i] = _availableEmoji[_random.Next(0, _availableEmoji.Length)];
            }
        }

        public void UpdateState(int generation, string emoji, int column)
        {
            //_subset = new[] { "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣" };

            if (_generation > generation || (_generation == generation && emoji.Length == 0))
            {
                return;
            }
            else if (_generation < generation && emoji.Length == 0)
            {
                RandomizeSubset();
            }

            _generation = generation;
            _emoji = emoji;

            _column = column;

            if (_playing)
            {
                if (emoji.Length == 0)
                {
                    _remanining = int.MaxValue;
                }

                return;
            }

            _playing = true;
            _remanining = int.MaxValue;

            AnimateToState(column);
        }

        public void AnimateToState(int column)
        {
            var compositor = BootStrapper.Current.Compositor;
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                if (_remanining > 0 || _emoji.Length == 0)
                {
                    AnimateToState(-1);
                }
                else
                {
                    _playing = false;
                }
            };

            for (int i = 0; i < 4; i++)
            {
                var local = (_index + i) % 4;
                if (local == 0 && (_column <= 0 || _emoji.Length == 0))
                {
                    if (_emoji.Length > 0 && _remanining > 2)
                    {
                        _remanining = 2;
                        _blocks[i].Text = _emoji;
                    }
                    else
                    {
                        _blocks[i].Text = _subset[_index % _subset.Length];
                    }
                }

                var target = local == 1 && _remanining == 1;
                var targetOrAfter = target || (local == 2 && _remanining == 1);

                //Grid.SetRow(block, local);

                var linear = compositor.CreateLinearEasingFunction();
                //var linear = compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1));
                var delay = TimeSpan.FromMilliseconds(column > 0 ? column * 33 : 0);

                var translation = compositor.CreateScalarKeyFrameAnimation();
                translation.InsertKeyFrame(0, local * 24);
                translation.InsertKeyFrame(1, local * 24 + 24, targetOrAfter ? null : linear);
                //translation.InsertKeyFrame(0, 0);
                //translation.InsertKeyFrame(1, 20, linear);
                translation.DelayTime = delay;
                translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                translation.Duration = TimeSpan.FromSeconds(_remanining < 2 ? 0.2 : 0.066);

                var opacity = compositor.CreateScalarKeyFrameAnimation();
                opacity.InsertKeyFrame(1, target ? 1 : _remanining < 2 && _emoji?.Length > 0 ? 0 : 0.5f);
                opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                opacity.DelayTime = delay;
                opacity.Duration = TimeSpan.FromSeconds(0.05);

                var visual = ElementComposition.GetElementVisual(RootGrid.Children[i]);
                visual.StartAnimation("Offset.Y", translation);
                visual.StartAnimation("Opacity", opacity);
            }

            batch.End();

            _index++;
            _column--;
            _remanining--;
        }
    }
}
