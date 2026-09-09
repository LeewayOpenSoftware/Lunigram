//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Telegram.Common;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Colors = Microsoft.UI.Colors;
#if LINUX
// Size.ToVector2 is an instance method of the projected struct in WinUI but an extension of
// Windows.Foundation.SizeExtensions here, so the namespace has to be imported. The Point that the
// import brings in is already spoken for by the global alias in CsWinRT.cs, which is why there is
// no alias of its own here: a second one in this file is CS1537, not a fix.
using Windows.Foundation;
#endif

namespace Telegram.Controls
{
    public partial class PatternBackground : ContentControl
    {
        public PatternBackground()
        {
            DefaultStyleKey = typeof(PatternBackground);
        }

        private AnimatedImageSource _pattern;
        private Color _centerColor;
        private Color _edgeColor;
        private Color _symbolColor;

        #region InitializeContent

        private Grid HeaderRoot;
        private Border HeaderGlow;
        private ProfilePatternCover Pattern;
        private ContentPresenter ContentPresenter;

        private bool _templateApplied;

        protected override void OnApplyTemplate()
        {
            HeaderRoot = GetTemplateChild(nameof(HeaderRoot)) as Grid;
            HeaderGlow = GetTemplateChild(nameof(HeaderGlow)) as Border;
            Pattern = GetTemplateChild(nameof(Pattern)) as ProfilePatternCover;
            ContentPresenter = GetTemplateChild(nameof(ContentPresenter)) as ContentPresenter;
#if LINUX
            // Without the default style there are no template children, and an exception out of
            // OnApplyTemplate runs inside the measure pass of whatever message list is showing.
            if (HeaderRoot == null || Pattern == null || ContentPresenter == null)
            {
                base.OnApplyTemplate();
                return;
            }
#endif
            ContentPresenter.SizeChanged += OnSizeChanged;

            _templateApplied = true;

            if (_pattern != null)
            {
                Update(_pattern, _centerColor, _edgeColor, _symbolColor);
            }

            base.OnApplyTemplate();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Pattern.Center = new RectangleF(new Vector2(0, 36), e.NewSize.ToVector2());
        }

        #endregion

        public void Update(IClientService clientService, UpgradedGift gift)
        {
            var source = DelayedFileSource.FromSticker(clientService, gift.Symbol.Sticker);
            var centerColor = gift.Backdrop.Colors.CenterColor.ToColor();
            var edgeColor = gift.Backdrop.Colors.EdgeColor.ToColor();
            var symbolColor = gift.Backdrop.Colors.SymbolColor.ToColor();

            Update(source, centerColor, edgeColor, symbolColor);
        }

        public void Update(IClientService clientService, UpgradedGiftBackdrop backdrop, UpgradedGiftSymbol symbol)
        {
            var source = DelayedFileSource.FromSticker(clientService, symbol.Sticker);
            var centerColor = backdrop.Colors.CenterColor.ToColor();
            var edgeColor = backdrop.Colors.EdgeColor.ToColor();
            var symbolColor = backdrop.Colors.SymbolColor.ToColor();

            Update(source, centerColor, edgeColor, symbolColor);
        }

        public void Update(AnimatedImageSource pattern, Color centerColor, Color edgeColor, Color symbolColor)
        {
            _pattern = pattern;
            _centerColor = centerColor;
            _edgeColor = edgeColor;
            _symbolColor = symbolColor;

            if (!_templateApplied)
            {
                return;
            }

            // TODO: support for ProfileColors here.
            // Currently only used for gifts, would be nice to use for profile too
            var radial = new RadialGradientBrush();
            radial.Center = new Point(0.5f, 0.5f);
            radial.RadiusX = 0.5;
            radial.RadiusY = 0.5;
            radial.GradientStops.Add(new GradientStop { Color = centerColor });
            radial.GradientStops.Add(new GradientStop { Color = edgeColor, Offset = 1 });

            HeaderRoot.Background = radial;
            HeaderRoot.RequestedTheme = ElementTheme.Dark;

#if LINUX
            // The cover's template feeds Foreground into AnimatedImage.ReplacementColor, and that
            // path builds a second CompositionVisualSurface over a LayoutRoot it sets to Opacity 0 -
            // which Uno captures as empty, so a tinted symbol is a blank symbol here. Left untinted:
            // the pattern keeps the sticker's own colours behind the backdrop instead of losing it.
            Pattern.Foreground = null;
#else
            Pattern.Foreground = new SolidColorBrush(symbolColor);
#endif
            Pattern.Source = pattern;
        }

        public void Clear()
        {
            if (!_templateApplied)
            {
                return;
            }

            HeaderRoot.Background = null;
            Pattern.Source = null;
        }

        #region Footer

        public object Footer
        {
            get { return (object)GetValue(FooterProperty); }
            set { SetValue(FooterProperty, value); }
        }

        public static readonly DependencyProperty FooterProperty =
            DependencyProperty.Register("Footer", typeof(object), typeof(PatternBackground), new PropertyMetadata(null));

        #endregion
    }
}
