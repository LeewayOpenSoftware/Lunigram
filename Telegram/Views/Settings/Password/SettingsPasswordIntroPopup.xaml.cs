//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Controls;

namespace Telegram.Views.Settings
{
    public sealed partial class SettingsPasswordIntroPopup : ContentPopup
    {
        public SettingsPasswordIntroPopup()
        {
            InitializeComponent();

            PrimaryButtonText = Strings.TwoStepVerificationSetPassword;
            SecondaryButtonText = Strings.Cancel;

#if LINUX
            // The info line: Uno cannot compile {CustomResource} on an attached property (see the
            // comment in this popup's XAML), so on Skia the TextBlock arrives empty and is filled
            // here through the same helper the Windows markup uses.
            Common.TextBlockHelper.SetMarkdown(Info, Info.Inlines, Strings.SetAdditionalPasswordInfo);
#endif
        }
    }
}
