//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of Controls/FrameworkElementEx.cs. The WinUI XAML compiler emits an
// `UnloadObject(DependencyObject)` method into every page/user control that uses x:Load
// (MainPage, ChatView, RootPage, StickerPanel…); Uno's generator does not, so the bases the
// code-behinds derive from supply it on top of XamlMarkupHelper, which Uno does implement.

using Telegram.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace Telegram.Controls
{
    public partial class UserControlEx
    {
        protected void UnloadObject(DependencyObject element)
        {
            if (element != null)
            {
                XamlMarkupHelper.UnloadObject(element);
            }
        }
    }

    public partial class PageEx
    {
        protected void UnloadObject(DependencyObject element)
        {
            if (element != null)
            {
                XamlMarkupHelper.UnloadObject(element);
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> once the page actually has a DataContext.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is needed at all.</b> Measured 2026-08-26, opening
        /// "Privacidad y seguridad" for the first time:
        /// <c>System.NullReferenceException at SettingsPrivacyAndSecurityPage.OnNavigatedTo</c>,
        /// on the line <c>ViewModel.PropertyChanged += …</c>.</para>
        ///
        /// <para>Uno raises <c>Page.OnNavigatedTo</c> from <c>Frame.ChangeContent</c>, which runs
        /// <b>inside</b> <c>Frame.Navigate</c>. Unigram assigns the DataContext from
        /// <c>NavigationService.NavigateToAsync</c>, which hangs off the <c>Navigated</c> event
        /// that the frame raises <b>after</b> that. So on the first navigation to a page type,
        /// <c>OnNavigatedTo</c> sees <c>DataContext == null</c> and every
        /// <c>ViewModel.Something</c> in it is a NullReferenceException. The log order is
        /// unambiguous: <c>Navigate</c> at .112, the throw out of <c>Frame.ChangeContent</c>, and
        /// only then <c>FacadeNavigatedEventHandler</c> + <c>NavigateToAsync</c> at .173.</para>
        ///
        /// <para>The damage is not the exception itself - the navigation completes and the x:Binds
        /// resolve once the DataContext lands - but everything <c>OnNavigatedTo</c> was going to do
        /// is skipped: subscriptions, <c>FindName</c> of x:Load'd blocks, the first read of a value
        /// that has no binding. On this page that was the sensitive-content block and the login
        /// e-mail pattern.</para>
        ///
        /// <para>Callers use it as an early-out at the top of their override, so the method runs
        /// again unchanged when the context arrives:
        /// <c>if (ViewModel == null) { WhenViewModelReady(() =&gt; OnNavigatedTo(e)); return; }</c>
        /// </para>
        /// </remarks>
        /// <param name="isReady">
        /// Whether the page has the DataContext it is waiting for. It has to be the CALLER's own
        /// type test and not just a null check: a Page inherits its DataContext down the visual
        /// tree, so a settings page arrives at OnNavigatedTo already holding the SettingsViewModel
        /// of the frame that hosts it - non-null, and not the view model it wants. A plain
        /// `DataContext != null` here was an infinite recursion that crashed the process with a
        /// stack overflow (measured, and the reason this parameter exists).
        /// </param>
        protected void WhenViewModelReady(System.Func<bool> isReady, System.Action action)
        {
            if (action == null || isReady == null)
            {
                return;
            }

            if (isReady())
            {
                action();
                return;
            }

            var attempts = 0;

            void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
            {
                if (isReady())
                {
                    DataContextChanged -= OnDataContextChanged;
                    action();
                }
                else if (++attempts > 8)
                {
                    // Give up loudly rather than sit on the event for the life of the page: if the
                    // right view model never arrives, that is a missing entry in
                    // App.ViewModelForPage or in Session.Registrations, and this line names the
                    // page that needs it.
                    DataContextChanged -= OnDataContextChanged;
                    Logger.Warning(string.Format("{0}: the expected view model never arrived; DataContext is {1}", GetType().Name, DataContext?.GetType().Name ?? "null"));
                }
            }

            DataContextChanged -= OnDataContextChanged;
            DataContextChanged += OnDataContextChanged;
        }
    }
}
