//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls
{
    public partial class AnimatedIconToggleButton : AnimatedGlyphToggleButton
    {
        private AnimatedIcon Icon;

        public AnimatedIconToggleButton()
        {
            DefaultStyleKey = typeof(AnimatedIconToggleButton);
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Icon?.Source is IAnimatedVisualSource2 source && Foreground is SolidColorBrush foreground)
            {
                source.SetColorProperty("Foreground", foreground.Color);
            }
        }

        protected override bool IsRuntimeCompatible()
        {
#if LINUX
            // u012 / new PORTING.md §6 entry: measured by reflecting Uno.Foundation.dll and
            // calling it directly -- Windows.Foundation.Metadata.ApiInformation
            // .IsApiContractPresent("Windows.Foundation.UniversalApiContract", 11) answers TRUE
            // on Uno, always, regardless of what's actually implemented. Taking that at face
            // value skips AnimatedGlyphToggleButton's OnApplyTemplate entirely (the
            // ContentPresenter1/2 glyph swap it sets up), leaving Source's inert
            // TryCreateAnimatedVisual as the only path -- MuteUnmute, VoiceVideo2 and
            // VoiceRecognition all render nothing. Forcing false here routes through the glyph
            // swap that's already written and correct.
            return false;
#else
            return Windows.Foundation.Metadata.ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 11);
#endif
        }

        protected override void OnApplyTemplate()
        {
            Icon = GetTemplateChild(nameof(Icon)) as AnimatedIcon;
            base.OnApplyTemplate();
        }

        #region Source

        public IAnimatedVisualSource2 Source
        {
            get { return (IAnimatedVisualSource2)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(IAnimatedVisualSource2), typeof(AnimatedIconToggleButton), new PropertyMetadata(null));

        #endregion
    }
}
