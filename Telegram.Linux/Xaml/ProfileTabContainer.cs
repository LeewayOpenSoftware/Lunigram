//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// What ChoosingItemContainer + ContainerContentChanging do together on Windows, done from the one
// callback Uno actually raises. Used by the group and channel tabs of the profile
// (ProfileMembersTabPage, ProfileGroupsTabPage, ProfileChannelsTabPage, ProfileBotsTabPage,
// ProfileSavedChatsTabPage) and by the two Supergroup pages that reach the same cells
// (SupergroupMembersPage, SupergroupAdministratorsPage).
//
// Three measured Uno facts meet in these lists, and each of them is silent on its own:
//
//   * ListViewBase.ChoosingItemContainer NEVER fires. add_ChoosingItemContainer and
//     remove_ChoosingItemContainer share one method body in the deployed Uno.UI.dll 6.6.184 and
//     that body is a call to ApiInformation.TryRaiseNotImplemented. Nothing throws. Two of the
//     three things upstream does in that handler survive anyway - TableListView already returns a
//     TableListViewItem from GetContainerForItemOverride, and the item template reaches the
//     presenter through the TemplateBinding that Uno's own ListViewItem template carries - but the
//     ContextRequested subscription does not, and that subscription IS the whole context menu:
//     promote, restrict, remove and edit-tag on a member; pin, unpin and delete on a saved chat.
//
//   * ContentControl.ContentTemplateRoot is always null for anything with a control template, so
//     the `args.ItemContainer.ContentTemplateRoot is ProfileCell content` test upstream writes
//     never matches and the row is left with whatever its template declares. That is the same
//     silent miss that emptied the chat list (PORTING.md 6). ContentRoot() finds it - but only
//     once the container is in the visual tree, and ContainerContentChanging is raised from
//     ItemsControl.PrepareContainerForIndex, i.e. before. Giving up on the first miss is what left
//     26 rows of chat history drawn as empty bubbles after a jump, so this retries on the
//     container's own layout passes and re-checks each time that it has not been recycled onto a
//     different item in the meantime.
//
//   * ContainerContentChanging is raised ONCE, with Phase == 0, and RegisterUpdateCallback never
//     runs. Callers therefore have to bind everything in one go; see ProfileCell.Linux.cs for the
//     inflated cell writers this hands the cell to.


using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

// Telegram.Common and not Telegram.Views.Profile: the same three Uno facts apply to the two
// Supergroup pages (members and administrators), which live in another namespace and already
// import this one.
namespace Telegram.Common
{
    internal static class ProfileTabContainer
    {
        /// <summary>
        /// Hooks <paramref name="contextRequested"/> up to the container (once per container, not
        /// once per item, because containers are recycled) and hands the expanded cell to
        /// <paramref name="bind"/>, waiting for the item template to expand if it has not yet.
        /// </summary>
        /// <param name="host">The list, used to tell whether a container has been recycled.</param>
        /// <param name="args">The one ContainerContentChanging Uno raises for this container.</param>
        /// <param name="contextRequested">
        /// The page's own context-menu handler, or null for the tabs that have no menu.
        /// </param>
        /// <param name="bind">
        /// Called with the expanded cell and the item it belongs to. Called at most once.
        /// </param>
        public static void Bind<TContent>(ListViewBase host, ContainerContentChangingEventArgs args, TypedEventHandler<UIElement, ContextRequestedEventArgs> contextRequested, Action<TContent, object> bind)
            where TContent : class
        {
            if (args == null || args.InRecycleQueue)
            {
                return;
            }

            var container = args.ItemContainer;
            if (container == null || bind == null)
            {
                return;
            }

            var item = args.Item;

            AttachContextRequested(container, contextRequested);

            // Uno raises this once and does not come back, so mark it handled for the same reason
            // upstream does: nothing else is going to fill this row.
            args.Handled = true;

            if (container.ContentRoot() is TContent content)
            {
                Invoke(bind, content, item);
                return;
            }

            var attempts = 0;

            void OnLayoutUpdated(object sender, object e)
            {
                attempts++;

                // LayoutUpdated is a per-pass event for the whole window, not for this container,
                // so the cap is on passes seen and not on time. Eight is what ChatView.
                // BindWhenExpanded settled on for the same wait.
                if (attempts > 8 || !ReferenceEquals(host?.ItemFromContainer(container), item))
                {
                    container.LayoutUpdated -= OnLayoutUpdated;
                    return;
                }

                if (container.ContentRoot() is TContent expanded)
                {
                    container.LayoutUpdated -= OnLayoutUpdated;
                    Invoke(bind, expanded, item);
                }
            }

            container.LayoutUpdated += OnLayoutUpdated;
        }

        private static void Invoke<TContent>(Action<TContent, object> bind, TContent content, object item)
            where TContent : class
        {
            // A row that throws while binding must cost a row, not the list: an exception raised
            // from inside a container callback leaves VirtualizingPanelLayout with
            // ShouldInterceptInvalidate stuck true and the panel deaf to InvalidateMeasure for the
            // rest of the process (PORTING.md 6). That is a blank tab that does not come back.
            try
            {
                bind(content, item);
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
        }

        /// <summary>
        /// Subscribes the handler once per container. The flag lives on the container itself so it
        /// travels with it through the recycle queue.
        /// </summary>
        public static void AttachContextRequested(SelectorItem container, TypedEventHandler<UIElement, ContextRequestedEventArgs> handler)
        {
            if (container == null || handler == null)
            {
                return;
            }

            if ((bool)container.GetValue(ContextRequestedAttachedProperty))
            {
                return;
            }

            container.SetValue(ContextRequestedAttachedProperty, true);
            container.ContextRequested += handler;
        }

        private static readonly DependencyProperty ContextRequestedAttachedProperty =
            DependencyProperty.RegisterAttached("ProfileTabContextRequestedAttached", typeof(bool), typeof(ProfileTabContainer), new PropertyMetadata(false));
    }
}

