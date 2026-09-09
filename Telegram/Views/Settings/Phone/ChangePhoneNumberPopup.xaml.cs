//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;

namespace Telegram.Views.Settings
{
    public sealed partial class ChangePhoneNumberPopup : ContentPopup
    {
        public ChangePhoneNumberPopup()
        {
            InitializeComponent();

            PrimaryButtonText = Strings.OK;

            // {CustomResource} on an attached property (common:TextBlockHelper.Markdown) does not
            // resolve under Uno Skia (PORTING.md §6); the value is static, so setting it once here
            // is equivalent to what the markup extension would have produced on Windows.
            TextBlockHelper.SetMarkdown(PhoneNumberHelpText, Strings.PhoneNumberHelp);
        }
    }
}
