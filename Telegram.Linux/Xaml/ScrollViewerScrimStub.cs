//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls
{
    /// <summary>
    /// The gradient that fades the top and bottom edge of a list inside a dialog. Purely
    /// decorative, and inert here.
    ///
    /// <para><b>Why it is a stub and not the real control.</b> Two independent reasons, either of
    /// which alone would be enough:</para>
    ///
    /// <para>1. Its only <c>&lt;Style TargetType&gt;</c> is <c>win:</c>-prefixed
    /// (<c>Themes/Generic.xaml:4136</c>), so on Skia the control gets no template at all - and
    /// unlike MoreButton, which merely measured 0x0, this one would <b>throw</b>:
    /// <c>OnApplyTemplate</c> writes <c>TopScrim.Height</c> on the line after
    /// <c>GetTemplateChild("TopScrim")</c>, with no null check
    /// (<c>Controls/ScrollViewerScrim.cs:48-52</c>). That is a NullReferenceException out of a
    /// template pass, i.e. it takes the popup down, not the gradient.</para>
    ///
    /// <para>2. Even with a template it could not do its job:
    /// <c>ElementCompositionPreview.GetScrollViewerManipulationPropertySet</c> is on the list of
    /// APIs Uno Skia does not implement (PORTING.md 6), and that property set is the only input of
    /// both expression animations that drive the two scrims.</para>
    ///
    /// <para><b>What is lost.</b> The gradient, and nothing else: the list still scrolls, the
    /// dialog still clips. A Control with no DefaultStyleKey measures 0x0 in Uno, and a 0x0
    /// element is not a hit-test target either, so this cannot become the invisible panel that
    /// eats clicks (PORTING.md, MainPage.ShowHideSearch).</para>
    ///
    /// <para><b>Why here and not one #if per call site.</b> The element appears in more than
    /// twenty popups across <c>Views/</c> - the two folder popups, the payment ones, the star
    /// ones, DoNotTranslatePopup - always as a sibling laid over the list, always decorative. One
    /// inert type keeps every one of those XAML files byte-identical to upstream.</para>
    ///
    /// <para><b>The way back</b> is a real style in <c>Hubs/LinuxOverrides.xaml</c> plus a scroll
    /// source that exists here (<c>ScrollViewer.ViewChanged</c>, which does fire, rather than the
    /// manipulation property set, which does not); at that point delete this file and add
    /// <c>Controls/ScrollViewerScrim.cs</c> to the subset instead.</para>
    /// </summary>
    public partial class ScrollViewerScrim : Control
    {
        public ScrollViewerScrim()
        {
            // No DefaultStyleKey on purpose: nothing to draw, and nothing to look up.
            IsHitTestVisible = false;
        }

        /// <summary>
        /// Same shape as upstream - a plain CLR property, which is all the callers need: every
        /// one of them assigns it once, from XAML, as <c>ScrollingHost="{x:Bind ScrollingHost}"</c>.
        /// </summary>
        public FrameworkElement ScrollingHost { get; set; }

        public double TopInset { get; set; } = 32;

        public double BottomInset { get; set; } = 32;
    }
}
