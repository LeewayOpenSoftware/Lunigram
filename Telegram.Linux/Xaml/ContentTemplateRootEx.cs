//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Common
{
    /// <summary>
    /// <see cref="ContentControl.ContentTemplateRoot"/> is always <c>null</c> in Uno for anything
    /// that has a control template.
    ///
    /// Uno only fills that property in the "content presenter bypass" case, and its
    /// <c>IsContentPresenterBypassEnabled</c> reads
    /// <c>Template == null ? !HasDefaultTemplate(GetDefaultStyleKey()) : false</c> - so as soon as
    /// a <c>Template</c> is set, which is the case for every ListViewItem, the property is never
    /// assigned by anyone: the item template is expanded by the <see cref="ContentPresenter"/> that
    /// lives inside the container's own template, and it keeps the result in *its* own
    /// <c>ContentTemplateRoot</c> without ever propagating it to the templated parent.
    ///
    /// The consequence is silent, because the shared code always asks with a type test
    /// (<c>container.ContentTemplateRoot is ChatCell content</c>): the test simply never matches,
    /// no exception is thrown, and the cell is left showing whatever its XAML declares - which is
    /// why the chat list came up with empty titles, no avatar and the design-time "11:10" in every
    /// row.
    ///
    /// This walks down to the presenter instead. Two caveats for callers:
    /// <list type="bullet">
    /// <item>It answers only once the container is in the visual tree: Uno materializes the
    /// presenter's content from <c>ContentPresenter.EnterImpl</c>, and
    /// <c>ContainerContentChanging</c> is raised before that (from
    /// <c>ItemsControl.PrepareContainerForIndex</c>), so a caller running that early has to retry
    /// when the container is loaded.</item>
    /// <item>It returns the first presenter content found depth-first, which is the item template
    /// root for a list container. It never descends into that root, so nested content presenters
    /// inside the cell itself cannot shadow it.</item>
    /// </list>
    /// </summary>
    public static class ContentTemplateRootEx
    {
        private const int MaxDepth = 16;

        /// <summary>
        /// The root of the expanded item/content template, whichever of the two places Uno left it.
        /// </summary>
        public static UIElement GetContentTemplateRootEx(this ContentControl container)
        {
            if (container == null)
            {
                return null;
            }

            return container.ContentTemplateRoot ?? FindPresenterContent(container, 0);
        }

        /// <summary>
        /// <see cref="GetContentTemplateRootEx"/> typed, for the
        /// <c>ContentTemplateRoot is TCell cell</c> tests of the shared code.
        /// </summary>
        public static T ContentTemplateRootAs<T>(this ContentControl container) where T : class
        {
            return GetContentTemplateRootEx(container) as T;
        }

        private static UIElement FindPresenterContent(DependencyObject element, int depth)
        {
            if (depth > MaxDepth)
            {
                return null;
            }

            var count = VisualTreeHelper.GetChildrenCount(element);

            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);

                if (child is ContentPresenter presenter)
                {
                    // Found the presenter: whatever it holds is the answer, and there is no reason
                    // to look inside it.
                    if (presenter.ContentTemplateRoot != null)
                    {
                        return presenter.ContentTemplateRoot;
                    }

                    continue;
                }

                var nested = FindPresenterContent(child, depth + 1);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
