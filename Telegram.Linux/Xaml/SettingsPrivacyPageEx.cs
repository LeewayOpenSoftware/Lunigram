//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Microsoft.UI.Xaml;

namespace Telegram.Views.Settings
{
    /// <summary>
    /// Hides the "Add exceptions" block of a privacy page.
    /// </summary>
    /// <remarks>
    /// <para>Every one of the thirteen privacy pages ends in a block headed
    /// <c>{CustomResource AddExceptions}</c> holding exactly two rows, "Always allow" and "Never
    /// allow". Both go through <c>SettingsPrivacyViewModelBase.Always/Never</c>, and the only
    /// thing those methods do is open <c>ChooseChatsPopup</c> - 3.443 lines that are not in the
    /// Linux subset. Left alone the rows would draw, respond to the click and open nothing, which
    /// is the one outcome this port has decided is worse than the row not being there.</para>
    ///
    /// <para>The count is NOT lost with the rows. Both per-page badges (AllowedBadge,
    /// RestrictedBadge) and the "(-3, +12)" suffix on the root page's badge are computed by
    /// <c>UpdatePrivacyImpl</c> straight from the rules TDLib returns, so the user can still see
    /// how many people they have excepted - they just cannot edit the list from here.</para>
    ///
    /// <para><b>Why a resolver and not the element.</b> On
    /// SettingsPrivacyAllowPrivateVoiceAndVideoNoteMessagesPage the block carries
    /// <c>x:Load="{x:Bind ViewModel.IsPremium}"</c>, so the generated field is still null while the
    /// page's constructor runs and only gets assigned if and when the binding turns true. Reading
    /// it once would silently miss exactly the case where the block does appear. The Func is
    /// re-evaluated on each layout pass until the element shows up or the budget runs out.</para>
    /// </remarks>
    internal static class SettingsPrivacyPageEx
    {
        public static void HideExceptions(FrameworkElement page, Func<FrameworkElement> resolve)
        {
            if (page == null || resolve == null)
            {
                return;
            }

            if (TryHide(resolve))
            {
                return;
            }

            var attempts = 0;

            void OnLayoutUpdated(object sender, object e)
            {
                attempts++;

                // LayoutUpdated is a window-wide per-pass event, so the budget is in passes.
                // Twelve is generous: the x:Load binding flips as soon as the view model has
                // loaded, which is the first pass after OnNavigatedToAsync returns.
                if (attempts > 12 || TryHide(resolve))
                {
                    page.LayoutUpdated -= OnLayoutUpdated;
                }
            }

            page.LayoutUpdated += OnLayoutUpdated;
        }

        private static bool TryHide(Func<FrameworkElement> resolve)
        {
            var element = resolve();
            if (element == null)
            {
                return false;
            }

            element.Visibility = Visibility.Collapsed;
            return true;
        }
    }
}
