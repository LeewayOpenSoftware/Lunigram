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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Chats;
using Telegram.Views;
using Windows.Foundation;

namespace Telegram.Controls
{
    // Declared in Controls/MoreButton.cs with a LottieGen icon (Assets/Icons/More.cs), which
    // PORTING.md §6 records as one of Uno's lies: LottieGen output implements IAnimatedVisualSource,
    // and Uno's version of that interface folds in nine members WinUI keeps elsewhere.
    //
    // The stub used to keep upstream's `DefaultStyleKey = typeof(MoreButton)`, and that was a
    // SECOND defect hiding behind the first: the only `Style TargetType="local:MoreButton"` in
    // Themes/Generic.xaml is inside a `win:` block, so on Skia this control resolved NO template at
    // all and measured zero -- the same failure mode that made StickerPanel's button invisible
    // (FALTA §1.4). It surfaced when u-013 drew the translate bar, whose menu button is a MoreButton.
    //
    // Pointing DefaultStyleKey at SettingsButton picks up a template that IS applied on Linux (that
    // style is not win:-only) and the static ellipsis stands in for the animated icon.
    public partial class MoreButton : SettingsButton
    {
        public MoreButton()
        {
            DefaultStyleKey = typeof(SettingsButton);

            // U+E712, "More" in Segoe Fluent Icons / Segoe MDL2 Assets.
            Glyph = "\uE712";
        }
    }
}
