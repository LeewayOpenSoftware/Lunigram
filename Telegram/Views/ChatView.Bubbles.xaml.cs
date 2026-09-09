//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Telegram.Collections;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Controls.Chats;
using Telegram.Controls.Media;
using Telegram.Controls.Messages;
using Telegram.Controls.Messages.Service;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Chats;
#if !LINUX
using Telegram.ViewModels.Gallery;
#endif
using Windows.Foundation;
using Microsoft.UI.Composition;
using Windows.UI.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Views
{
    public partial class ChatView
    {
        private readonly DispatcherTimer _debouncer;

        private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Messages.ScrollingHost.ScrollableHeight > 0)
            {
                return;
            }

            _viewChanged = true;
            UpdateArrowVisibility();
        }

        private bool _viewChanged;

        private void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            _viewChanged = true;
            UpdateArrowVisibility();
        }

        private void ItemsPanelRoot_LayoutUpdated(object sender, object e)
        {
            if (_messagesShift.HasRanges())
            {
                var panel = Messages.ItemsPanelRoot as ItemsStackPanel;
                if (panel != null)
                {
#if LINUX
                    var reverse = Messages.CurrentScrollingMode == ItemsUpdatingScrollMode.KeepLastItemInView;
#else
                    var reverse = panel.ItemsUpdatingScrollMode == ItemsUpdatingScrollMode.KeepLastItemInView;
#endif
                    var ranges = _messagesShift.GetRanges(reverse);
                    var diff = 0f;

                    foreach (var range in ranges)
                    {
                        diff -= range.Height;
                        AnimateSizeChanged(panel, range, diff);
                    }
                }
            }

            _messagesShift.Clear();

            if (_viewChanged)
            {
                _viewChanged = false;
                ViewVisibleMessages(false);
            }
        }

        private void UpdateArrowVisibility()
        {
            if (ViewModel.Type is not DialogType.History and not DialogType.Thread || ViewModel.IsSavedMessagesTab)
            {
                Arrows.IsVisible = false;
                return;
            }

            if (Messages.ScrollingHost == null || Messages.ScrollingHost.ScrollableHeight == 0)
            {
                Arrows.IsVisible = false;
                return;
            }

            if (Messages.ScrollingHost == null || Messages.ScrollingHost.ScrollableHeight - Messages.ScrollingHost.VerticalOffset < 40)
            {
                Arrows.IsVisible = ViewModel.IsNewestSliceLoaded == false;
                return;
            }

            Arrows.IsVisible = true;
        }

        private void UnloadVisibleMessages()
        {
            _prev.Clear();
        }

        public void ViewVisibleMessages()
        {
            _debouncer.Stop();
            _debouncer.Start();
        }

        private readonly MutableVector<long> _viewVisibleMessages = new();
        private readonly Dictionary<long, IPlayerView> _viewVisibleMessagesNext = new();
        private readonly HashSet<long> _viewVisibleMessagesPrev = new();

        private long _viewMessagesFirstVisibleId = 0;
        private long _viewMessagesLastVisibleId = 0;

        public void ViewVisibleMessages(bool intermediate)
        {
            var chat = ViewModel.Chat;
            if (chat == null || IsDisconnected)
            {
                return;
            }

            var panel = Messages.ItemsPanelRoot as ItemsStackPanel;
            if (panel == null /*|| panel.FirstVisibleIndex < 0 || panel.LastVisibleIndex >= _messages.Count*/)
            {
                return;
            }

            var firstVisibleId = 0L;
            var lastVisibleId = 0L;

            var lastVisibleIsLastMessage = panel.LastVisibleIndex == Messages.Items.Count - 1;

            var minItem = 2;
            var minDate = true;
            var minDateIndex = panel.FirstVisibleIndex;
            var minDateValue = DateTime.MaxValue;
            var minDateScheduled = false;

            var minMessageTopic = ViewModel.IsForum || ViewModel.IsDirectMessagesGroup;
            var minMessageTopicIndex = panel.FirstVisibleIndex;
            var minMessageTopicValue = default(MessageTopic);

            if (_dateHeaderTranslation == null)
            {
                const string viewportTopExp = $"(reference.Offset.Y + scroll.Translation.Y) - (tracker.Offset.Y - props.Padding)";
#if LINUX
                // Uno's expression parser knows no `this`: it throws ArgumentException
                // ("Unrecognized identifier 'this'") the moment the animation starts, and the
                // throw comes out of ViewVisibleMessages - so the read marks, the media autoplay
                // and the sticky headers all died with it, on every layout pass. The target is
                // passed as an ordinary reference parameter instead.
                const string heightExp = "(target.Size.Y + 8)";
#else
                const string heightExp = "(this.Target.Size.Y + 8)";
#endif
                const string offsetExp = $"({viewportTopExp}) + {heightExp}";
                const string translationExp = $"-{heightExp} * 2 + {offsetExp}";

                //var reference = ElementComposition.GetElementVisual(container);
                _dateHeaderTranslation = _dateHeader.Compositor.CreateExpressionAnimation(translationExp);
                //_dateHeaderExpression.SetReferenceParameter("reference", reference);
                _dateHeaderTranslation.SetReferenceParameter("scroll", Messages.ScrollingPropertySet);
                _dateHeaderTranslation.SetReferenceParameter("tracker", _dateHeaderRelative);
                _dateHeaderTranslation.SetReferenceParameter("props", _messagesPaddingSet);
#if LINUX
                _dateHeaderTranslation.SetReferenceParameter("target", _dateHeader);
#endif
            }

            if (_forumTopicHeaderTranslation == null)
            {
                const string viewportTopExp = $"(reference.Offset.Y + scroll.Translation.Y) - (tracker.Offset.Y - props.Padding)";
#if LINUX
                const string heightExp = "(target.Size.Y + 8)";
#else
                const string heightExp = "(this.Target.Size.Y + 8)";
#endif
                const string offsetExp = $"({viewportTopExp}) + {heightExp}";
                const string translationExp = $"-{heightExp} * 2 + ({offsetExp} - {heightExp})";
                const string scaleExp = $"({offsetExp} - {heightExp}) / ({heightExp} * 2)";

                //var reference = ElementComposition.GetElementVisual(container);
                _forumTopicHeaderTranslation = _forumTopicHeader.Compositor.CreateExpressionAnimation(translationExp);
                //_forumTopicHeaderTranslation.SetReferenceParameter("reference", reference);
                _forumTopicHeaderTranslation.SetReferenceParameter("scroll", Messages.ScrollingPropertySet);
                _forumTopicHeaderTranslation.SetReferenceParameter("tracker", _dateHeaderRelative);
                _forumTopicHeaderTranslation.SetReferenceParameter("props", _messagesPaddingSet);
#if LINUX
                _forumTopicHeaderTranslation.SetReferenceParameter("target", _forumTopicHeader);
#endif

                _forumTopicHeaderScale = _forumTopicHeader.Compositor.CreateExpressionAnimation($"Vector3({scaleExp}, {scaleExp}, 0)");
                //_forumTopicHeaderScale.SetReferenceParameter("reference", reference);
                _forumTopicHeaderScale.SetReferenceParameter("scroll", Messages.ScrollingPropertySet);
                _forumTopicHeaderScale.SetReferenceParameter("tracker", _dateHeaderRelative);
                _forumTopicHeaderScale.SetReferenceParameter("props", _messagesPaddingSet);
#if LINUX
                _forumTopicHeaderScale.SetReferenceParameter("target", _forumTopicHeader);
#endif
            }

            if (_stickySummaryExpression == null)
            {
                _stickySummaryExpression = _forumTopicHeader.Compositor.CreateExpressionAnimation();
                //animation.SetReferenceParameter("reference", reference);
                //animation.SetReferenceParameter("child", ElementComposition.GetElementVisual(container.ContentRoot()));
                _stickySummaryExpression.SetReferenceParameter("scroll", Messages.ScrollingPropertySet);
                _stickySummaryExpression.SetReferenceParameter("messages", _messagesVisual);
                _stickySummaryExpression.SetReferenceParameter("props", _messagesPaddingSet);
            }

            if (_stickyPhotoExpression == null)
            {
                _stickyPhotoExpression = _forumTopicHeader.Compositor.CreateExpressionAnimation();
                //animation.SetReferenceParameter("reference", reference);
                //animation.SetReferenceParameter("child", ElementComposition.GetElementVisual(container.ContentRoot()));
                _stickyPhotoExpression.SetReferenceParameter("scroll", Messages.ScrollingPropertySet);
                _stickyPhotoExpression.SetReferenceParameter("messages", _messagesVisual);
                _stickyPhotoExpression.SetReferenceParameter("props", _messagesPaddingSet);
            }

            var top = 0d;
            var bottom = 0d;

            var summaryAbove = false;

            var stickyAbove = false;
            var stickyBelow = false;

            var playAnimations = !intermediate && ViewModel.Window.IsActive;

            for (int i = panel.FirstVisibleIndex; i <= Math.Min(panel.LastVisibleIndex + 1, Messages.Items.Count - 1); i++)
            {
                // TODO: this would be preferable, but it can't be done because
                // date service messages aren't mapped in the array
                //var message = _messages[i];
                //_messageIdToSelector.TryGetValue(message.Id, out SelectorItem container);

                //if (container == null)
                //{
                //    continue;
                //}

                var container = Messages.ContainerFromIndex(i) as SelectorItem;
                if (container == null)
                {
                    continue;
                }

                var message = Messages.ItemFromContainer(container) as MessageViewModel;
                if (message == null)
                {
                    continue;
                }

                if (firstVisibleId == 0)
                {
                    firstVisibleId = message.Id;
                }
                if (message.Id != 0)
                {
                    lastVisibleId = message.Id;
                }

                if (i == panel.FirstVisibleIndex)
                {
                    // We calculate the item position relative to the current viewport manually
                    // instead of relying on TransformToVisual. This should save us some milliseconds.
                    top = (container.ActualOffset.Y - Messages.ScrollingHost.VerticalOffset) - (_dateHeaderRelative.Offset.Y - _messagesHeaderRootPadding);
                    bottom = (container.ActualOffset.Y - Messages.ScrollingHost.VerticalOffset) - (_messagesVisual.Size.Y - _messagesHeaderRootPadding);
                }

                if (minItem > 0 && i >= panel.FirstVisibleIndex)
                {
                    if (minItem == 2 && top + container.ActualHeight >= 0)
                    {
                        minItem = ViewModel.IsForum || ViewModel.IsDirectMessagesGroup ? 1 : 0;

                        if (message.Content is MessageHeaderUnread)
                        {
                            minDateValue = DateTime.MaxValue;
                        }
                        else if (message.SchedulingState is MessageSchedulingStateSendAtDate sendAtDate)
                        {
                            minDateValue = Formatter.ToLocalTime(sendAtDate.SendDate).Date;
                            minDateScheduled = true;
                        }
                        else if (message.SchedulingState is MessageSchedulingStateSendWhenVideoProcessed sendWhenVideoProcessed)
                        {
                            minDateValue = Formatter.ToLocalTime(sendWhenVideoProcessed.SendDate).Date;
                            minDateScheduled = true;
                        }
                        else if (message.SchedulingState is MessageSchedulingStateSendWhenOnline)
                        {
                            minDateValue = DateTime.MinValue;
                            minDateScheduled = true;
                        }
                        else if (message.Date > 0)
                        {
                            minDateValue = Formatter.ToLocalTime(message.Date).Date;
                        }
                    }

                    if (minItem == 1 && top + container.ActualHeight + DateHeader.ActualSize.Y + 8 >= 0)
                    {
                        minItem = 0;

                        if (message.Content is not MessageHeaderUnread)
                        {
                            minMessageTopicValue = message.TopicId;
                        }
                    }
                }

                void SetContentOpacity(double value)
                {
                    if (container.ContentRoot() is MessageService service)
                    {
                        service.ContentOpacity = value;
                    }
                }

                if (message.Content is MessageHeaderDate && minDate && top + container.ActualSize.Y >= 0)
                {
                    var height = container.ActualSize.Y;
                    var offset = (float)top + height;

                    minDate = false;

                    if (/*offset >= 0 &&*/ offset < height)
                    {
                        SetContentOpacity(0);
                        minDateIndex = int.MaxValue; // Force show
                    }
                    else
                    {
                        SetContentOpacity(1);
                        minDateIndex = i;
                    }

                    if (offset >= height && offset < height * 2)
                    {
                        if (_dateHeaderTracked != container)
                        {
                            var reference = ElementComposition.GetElementVisual(container);
                            _dateHeaderTranslation.SetReferenceParameter("reference", reference);

                            _dateHeader.StartAnimation("Translation.Y", _dateHeaderTranslation);
                            _dateHeaderTracked = container;
                        }
                    }
                    else
                    {
                        _dateHeader.Properties.InsertVector3("Translation", Vector3.Zero);
                        _dateHeaderTracked = null;
                    }
                }
                else if (message.Content is MessageHeaderMessageTopic && minMessageTopic && top + container.ActualSize.Y >= 0)
                {
                    var height = container.ActualSize.Y;
                    var offset = (float)top + height;

                    if (offset > height)
                    {
                        minMessageTopic = false;

                        offset -= height;
                        //height *= 2;

                        if (/*offset >= 0 &&*/ offset < height)
                        {
                            SetContentOpacity(0);
                            minMessageTopicIndex = int.MaxValue; // Force show

                            minMessageTopicValue = message.TopicId;
                        }
                        else
                        {
                            SetContentOpacity(1);
                            minMessageTopicIndex = i;
                        }

                        if (offset >= height && offset < height * 2)
                        {
                            if (_forumTopicHeaderTracked != container)
                            {
                                var reference = ElementComposition.GetElementVisual(container);
                                _forumTopicHeaderTranslation.SetReferenceParameter("reference", reference);
                                _forumTopicHeaderScale.SetReferenceParameter("reference", reference);

                                _forumTopicHeader.StartAnimation("Translation.Y", _forumTopicHeaderTranslation);
#if !LINUX
                                _forumTopicHeader.StartAnimation("Scale", _forumTopicHeaderScale);
#else
                                // Measured on screen: this expression evaluates to Scale between
                                // -21 and -58 in Uno, so the sticky topic header is mirrored and
                                // blown up off the window and the topic name simply never appears
                                // (the row underneath has already faded its own copy to 0 to hand
                                // over). Its numerator is the viewport-relative top of the
                                // separator, which needs `scroll.Translation.Y` and the container
                                // offset to be in the same space; they are not here, because the
                                // property set is not the scroll viewer's manipulation set but one
                                // this port refreshes by hand on ViewChanged (see
                                // ChatHistoryView.OnApplyTemplate). The same numerator drives
                                // Translation.Y just above, but Translation is a property-set
                                // property and lands nowhere visible, while Scale is a real visual
                                // property and destroys the header. Left at 1: the header still
                                // fades in and out with the opacity animation, like the date
                                // header next to it, it just does not shrink while handing over.
                                _forumTopicHeader.Scale = Vector3.One;
#endif
                                _forumTopicHeaderTracked = container;
                            }
                        }
                        else
                        {
                            _forumTopicHeader.Scale = Vector3.One;
                            _forumTopicHeader.Properties.InsertVector3("Translation", Vector3.Zero);
                            _forumTopicHeaderTracked = null;
                        }
                    }
                    else
                    {
                        SetContentOpacity(0);
                    }
                }
                else
                {
                    SetContentOpacity(1);
                }

                bool AnimateStickySummary(Visual visual, GlyphButton stickySummary, ref SelectorItem tracked, ref MessageViewModel item, bool below)
                {
                    if (message.CanSummarizeText && container.ContentRoot() is MessageSelector selector && selector.ContentRoot() is MessageBubble bubble)
                    {
                        const string topExp = "(reference.Offset.Y + scroll.Translation.Y) - props.TopPadding";
                        const string bottomExp = $"(reference.Offset.Y + child.Size.Y + scroll.Translation.Y) - props.TopPadding";

                        const string trueTrueExp = $"{bottomExp} >= 73 ? Max(props.TopPadding, {topExp}) : {bottomExp} - (30 + 4 + 30)";
                        const string trueFalseExp = $"{bottomExp} - 26"; //$"{bottomExp} > 0 ? props.TopPadding : {bottomExp} + 8";
                        const string falseTrueExp = $"Max(0, {topExp})";

                        // 38 = min bubble height + top margin
                        string exp;
                        if (below)
                        {
                            exp = trueFalseExp;
                        }
                        else
                        {
                            exp = trueTrueExp;
                        }

                        if (exp != null)
                        {
                            if (tracked != container)
                            {
                                var reference = ElementComposition.GetElementVisual(container);
                                var bubbly = ElementComposition.GetElementVisual(selector.ContentRoot());

#if LINUX
                                // Visual.Translation only exists on a visual that had
                                // SetIsTranslationEnabled called on it; reading it from an
                                // expression on any other one throws in Uno ("Unable to get
                                // property 'Translation'") - and the throw comes out of
                                // ViewVisibleMessages, taking the rest of it with it. The bubble
                                // is never translated horizontally, so the term is zero anyway.
                                _stickySummaryExpression.Expression = $"Vector3(bubble.Size.X - 30 + child.Offset.X, {exp}, 0)";
#else
                                _stickySummaryExpression.Expression = $"Vector3(bubble.Size.X - 30 + child.Offset.X + child.Translation.X, {exp}, 0)";
#endif
                                _stickySummaryExpression.SetReferenceParameter("reference", reference);
                                _stickySummaryExpression.SetReferenceParameter("child", selector.ContentVisual);
                                _stickySummaryExpression.SetReferenceParameter("bubble", bubbly);

                                visual.StartAnimation("Translation", _stickySummaryExpression);
                                tracked = container;
                            }
                        }
                        else
                        {
                            visual.Properties.InsertVector3("Translation", Vector3.Zero);
                            tracked = null;
                        }

                        if (below && exp == null)
                        {
                            return false;
                        }

                        item = message;
                        bubble.ShowHideSummary(false);

                        stickySummary.Glyph = message.SummarizedText == null ? Icons.ArrowMinimizeSparkles16 : Icons.ArrowMaximizeSparkles16;

                        //stickyPhoto.Source = ProfilePictureSource.Message(message);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }

                bool AnimateStickyPhoto(Visual visual, ProfilePicture stickyPhoto, ref SelectorItem tracked, ref MessageViewModel item, bool below)
                {
                    if (message.HasSenderPhoto && container.ContentRoot() is MessageSelector selector && selector.ContentRoot() is MessageBubble bubble)
                    {
                        const string topExp = "(reference.Offset.Y + scroll.Translation.Y) - (messages.Size.Y - props.Padding)";
                        const string bottomExp = $"(reference.Offset.Y + child.Size.Y + scroll.Translation.Y) - (messages.Size.Y - props.Padding)";

                        const string trueTrueExp = $"{topExp} < -38 ? Min(0, {bottomExp}) : {topExp} + 38";
                        const string trueFalseExp = $"{topExp} < -38 ? 0 : {topExp} + 38";
                        const string falseTrueExp = $"Min(0, {bottomExp})";

                        // 38 = min bubble height + top margin
                        string exp;
                        if (below)
                        {
                            exp = message.IsFirst ? trueFalseExp : null;
                        }
                        else
                        {
                            exp = (message.IsFirst, message.IsLast) switch
                            {
                                (true, true) => trueTrueExp,
                                (true, false) => trueFalseExp,
                                (false, true) => falseTrueExp,
                                (false, false) => null
                            };
                        }

                        if (exp != null)
                        {
                            if (tracked != container)
                            {
                                var reference = ElementComposition.GetElementVisual(container);

#if LINUX
                                // See the note above: child.Translation throws in Uno.
                                _stickyPhotoExpression.Expression = $"Vector3(child.Offset.X, {exp}, 0)";
#else
                                _stickyPhotoExpression.Expression = $"Vector3(child.Offset.X + child.Translation.X, {exp}, 0)";
#endif
                                _stickyPhotoExpression.SetReferenceParameter("reference", reference);
                                _stickyPhotoExpression.SetReferenceParameter("child", selector.ContentVisual);

                                visual.StartAnimation("Translation", _stickyPhotoExpression);
                                tracked = container;
                            }
                        }
                        else
                        {
                            visual.Properties.InsertVector3("Translation", Vector3.Zero);
                            tracked = null;
                        }

                        if (below && exp == null)
                        {
                            return false;
                        }

                        item = message;
                        bubble.ShowHidePhoto(false);

                        stickyPhoto.Source = ProfilePictureSource.Message(message);
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }

                var childHeight = container.ContentRoot().ActualSize.Y;

                if (top <= 0 && top + childHeight > 0 && !summaryAbove)
                {
                    summaryAbove = AnimateStickySummary(_stickySummaryAboveVisual, StickySummaryAboveButton, ref _stickySummaryAboveTracked, ref _stickySummaryAboveMessage, false);
                }
                else if (container.ContentRoot() is MessageSelector selectorSummary && selectorSummary.ContentRoot() is MessageBubble bubble)
                {
                    bubble.ShowHideSummary(true);
                }

                if (bottom + childHeight >= 0 && bottom + childHeight <= childHeight)
                {
                    stickyAbove = AnimateStickyPhoto(_stickyPhotoAboveVisual, StickyPhotoAbove, ref _stickyPhotoAboveTracked, ref _stickyPhotoAboveMessage, false);
                }
                else if (bottom + childHeight >= childHeight)
                {
                    stickyBelow = AnimateStickyPhoto(_stickyPhotoBelowVisual, StickyPhotoBelow, ref _stickyPhotoBelowTracked, ref _stickyPhotoBelowMessage, true);

                    // We found the message after the last visible message, we can break the loop
                    break;
                }
                // ContentTemplateRoot, which is what this was, is always null in Uno (PORTING.md
                // section 6): the test never matched, so the bubble that AnimateStickyPhoto had
                // hidden on the way past was never shown again and the sender avatar disappeared
                // from every group message. ContentRoot() is the helper that walks down to the
                // ContentPresenter.
                else if (container.ContentRoot() is MessageSelector selectorPhoto && selectorPhoto.ContentRoot() is MessageBubble bubble)
                {
                    bubble.ShowHidePhoto(true);
                }

                top += container.ActualHeight;
                bottom += container.ActualHeight;

                // Read and play messages logic:
                if (message.Id == 0 || message.IsSynthetic)
                {
                    continue;
                }

                if (message.ContainsUnreadPollVotes)
                {
                    ViewModel.PollVotes.SetLastViewedMessage(message.Id);
                }

                if (message.ContainsUnreadMention)
                {
                    ViewModel.Mentions.SetLastViewedMessage(message.Id);
                }

                if (message.UnreadReactions?.Count > 0)
                {
                    ViewModel.Reactions.SetLastViewedMessage(message.Id);

                    var root = container.ContentRoot() as FrameworkElement;
                    if (root is MessageSelector selector && selector.Content is MessageBubble bubble)
                    {
                        bubble.UpdateMessageReactions(message, true);
                    }
                    else if (root is MessageService service)
                    {
                        service.UpdateMessageReactions(message, true);
                    }
                }

                // This is a workaround for a bug in messages.readDiscussion that causes sent messages
                // to be marked as read and consequently blocks following updateReadChannelDiscussionOutbox
                if ((i < panel.LastVisibleIndex || lastVisibleIsLastMessage) && (ViewModel.ForumTopic == null || !message.IsOutgoing || (message.IsOutgoing && message.UnreadReactions?.Count > 0)))
                {
                    if (message.Content is MessageAlbum album)
                    {
                        _viewVisibleMessages.AddRange(album.Messages.Keys);
                    }
                    else
                    {
                        _viewVisibleMessages.Add(message.Id);
                    }
                }

                if (playAnimations && message.Content is not MessageAlbum)
                {
                    var result = Play(container, message);
                    if (result.Item2 != null)
                    {
                        _viewVisibleMessagesNext[result.Item1] = result.Item2;
                    }
                    else if (result.Item1 != 0)
                    {
                        _viewVisibleMessagesPrev.Add(result.Item1);
                    }
                }

                while (ViewModel.RepliesStack.TryPeek(out long reply) && reply == message.Id)
                {
                    ViewModel.RepliesStack.Pop();
                }
            }

            if (minDate)
            {
                _dateHeader.Properties.InsertVector3("Translation", Vector3.Zero);
                _dateHeaderTracked = null;
            }

            if (minMessageTopic)
            {
                _forumTopicHeader.Scale = Vector3.One;
                _forumTopicHeader.Properties.InsertVector3("Translation", Vector3.Zero);
                _forumTopicHeaderTracked = null;
            }

            if (!summaryAbove)
            {
                StickySummaryAbove.Visibility = Visibility.Collapsed;
                _stickySummaryAboveVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                _stickySummaryAboveTracked = null;
                _stickySummaryAboveMessage = null;
            }
            else
            {
                StickySummaryAbove.Visibility = Visibility.Visible;
            }

            if (!stickyAbove)
            {
                StickyPhotoRootAbove.Visibility = Visibility.Collapsed;
                _stickyPhotoAboveVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                _stickyPhotoAboveTracked = null;
                _stickyPhotoAboveMessage = null;
            }
            else
            {
                StickyPhotoRootAbove.Visibility = Visibility.Visible;
            }

            if (!stickyBelow)
            {
                StickyPhotoRootBelow.Visibility = Visibility.Collapsed;
                _stickyPhotoBelowVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                _stickyPhotoBelowTracked = null;
                _stickyPhotoBelowMessage = null;
            }
            else
            {
                StickyPhotoRootBelow.Visibility = Visibility.Visible;
            }

            // TODO: do not hide if above corresponding message

            _dateHeaderTimer.Stop();
            _dateHeaderTimer.Start();
            ShowHideDateHeader(minDateValue != DateTime.MaxValue && minDateIndex > 0, minDateValue != DateTime.MaxValue && minDateIndex is > 0 and < int.MaxValue);
            ShowHideForumTopicHeader(minMessageTopicValue != null && minMessageTopicIndex > 0, minMessageTopicValue != null && minMessageTopicIndex is > 0 and < int.MaxValue);

            if (minMessageTopicValue != null)
            {
                UpdateForumTopicHeader(minMessageTopicValue);
            }

            if (minDateValue != DateTime.MaxValue)
            {
                UpdateDateHeader(minDateValue, minDateScheduled);
            }

            // Read and play messages logic:
            if (_viewVisibleMessages.Count > 0 && ViewModel.NavigationService.Window.IsActive && !FromPreview)
            {
                MessageSource source = ViewModel.Type switch
                {
                    DialogType.EventLog => new MessageSourceChatEventLog(),
                    DialogType.Thread => ViewModel.ForumTopic != null
                        ? new MessageSourceForumTopicHistory()
                        : ViewModel.DirectMessagesChatTopic != null
                        ? new MessageSourceDirectMessagesChatTopicHistory()
                        : new MessageSourceMessageThreadHistory(),
                    _ => new MessageSourceChatHistory()
                };

                // This is needed because we don't keep all topics messages in memory as TDLib would do
                ViewModel.ClientService.ViewMessages(chat.Id, ViewModel.TopicId, _viewVisibleMessages, source, false);
            }

            if (playAnimations && (_prev.Count > 0 || _viewVisibleMessagesNext.Count > 0))
            {
                List<long> keysToRemove = null;
                foreach (var kvp in _prev)
                {
                    var key = kvp.Key;
                    if (_viewVisibleMessagesNext.ContainsKey(key) || _viewVisibleMessagesPrev.Contains(key))
                        continue;

                    if (kvp.Value.Target is IPlayerView presenter && presenter.LoopCount == 0)
                        presenter.ViewportChanged(false);

                    keysToRemove ??= new();
                    keysToRemove.Add(key);
                }

                if (keysToRemove != null)
                {
                    foreach (var key in keysToRemove)
                        _prev.Remove(key);
                }

                foreach (var item in _viewVisibleMessagesNext)
                {
                    //if (_prev.TryGetValue(item.Key, out var wr))
                    //    wr.Target = item.Value;
                    //else
                    _prev[item.Key] = new WeakReference(item.Value);
                    item.Value.ViewportChanged(true);
                }
            }

            _viewVisibleMessages.Clear();
            _viewVisibleMessagesNext.Clear();
            _viewVisibleMessagesPrev.Clear();

            // Pinned banner
            if (firstVisibleId == 0 || lastVisibleId == 0)
            {
                return;
            }

            if (ViewModel.Thread != null)
            {
                var message = ViewModel.Thread.Messages.LastOrDefault();
                if (message == null || (firstVisibleId <= message.Id && lastVisibleId >= message.Id) || Messages.ScrollingHost.ScrollableHeight == 0)
                {
                    PinnedMessage.UpdateMessage(ViewModel.Chat, null, false, 0, 1, false);
                }
                else
                {
                    PinnedMessage.UpdateMessage(ViewModel.Chat, ViewModel.CreateMessage(message), false, 0, 1, false);
                }
            }
            else if (ViewModel.PinnedMessages.Count > 0)
            {
                // TODO: Find a way to reuse this for topics too
                if (firstVisibleId == _viewMessagesFirstVisibleId || lastVisibleId == _viewMessagesLastVisibleId)
                {
                    return;
                }

                _viewMessagesFirstVisibleId = firstVisibleId;
                _viewMessagesLastVisibleId = lastVisibleId;

                var currentPinned = ViewModel.PinnedMessages.GetVisible(lastVisibleId, Messages.HasBeenScrolled);
                if (currentPinned != null)
                {
                    PinnedMessage.UpdateMessage(ViewModel.Chat, currentPinned, false, currentPinned.Index, ViewModel.PinnedMessages.TotalCount, intermediate);
                }
                else
                {
                    PinnedMessage.UpdateMessage(ViewModel.Chat, null, false, 0, 1, false);
                }
            }
        }

        private bool _dateHeaderCollapsed = true;

        private void ShowHideDateHeader(bool show, bool animate)
        {
            if (_dateHeaderCollapsed != show)
            {
                return;
            }

            _dateHeaderCollapsed = !show;
            DateHeaderPanel.Visibility = show || animate ? Visibility.Visible : Visibility.Collapsed;

            if (!animate)
            {
                _dateHeaderPanel.Opacity = show ? 1 : 0;
                DateHeaderPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            var batch = _dateHeaderPanel.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                DateHeaderPanel.Visibility = _dateHeaderCollapsed
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };

            var opacity = _dateHeaderPanel.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, show ? 0 : 1);
            opacity.InsertKeyFrame(1, show ? 1 : 0);

            _dateHeaderPanel.StartAnimation("Opacity", opacity);

            batch.End();
        }

        private DateTime _dateHeaderDate;
        private bool _dateHeaderScheduled;

        private void UpdateDateHeader(DateTime date, bool scheduled)
        {
            if (_dateHeaderDate == date && _dateHeaderScheduled == scheduled)
            {
                return;
            }

            _dateHeaderDate = date;
            _dateHeaderScheduled = scheduled;

            if (scheduled)
            {
                if (date != DateTime.MinValue)
                {
                    DateHeader.Tag = null;
                    DateHeaderLabel.Text = string.Format(Strings.MessageScheduledOn, Formatter.DayGrouping(date));
                }
                else
                {
                    DateHeader.Tag = null;
                    DateHeaderLabel.Text = Strings.MessageScheduledUntilOnline;
                }
            }
            else
            {
                DateHeader.Tag = date;
                DateHeaderLabel.Text = Formatter.DayGrouping(date);
            }
        }

        private bool _forumTopicHeaderCollapsed = true;

        private void ShowHideForumTopicHeader(bool show, bool animate)
        {
            if (_forumTopicHeaderCollapsed != show)
            {
                return;
            }

            _forumTopicHeaderCollapsed = !show;
            ForumTopicHeaderPanel.Visibility = show || animate ? Visibility.Visible : Visibility.Collapsed;

            if (!animate)
            {
                _forumTopicHeaderPanel.Opacity = show ? 1 : 0;
                ForumTopicHeaderPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            var batch = _dateHeaderPanel.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                ForumTopicHeaderPanel.Visibility = _forumTopicHeaderCollapsed
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };

            var opacity = _forumTopicHeaderPanel.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, show ? 0 : 1);
            opacity.InsertKeyFrame(1, show ? 1 : 0);

            _forumTopicHeaderPanel.StartAnimation("Opacity", opacity);

            batch.End();
        }

        private MessageTopic _forumTopicHeaderTopic;

        private void UpdateForumTopicHeader(MessageTopic messageTopic)
        {
            if (_forumTopicHeaderTopic.AreTheSame(messageTopic))
            {
                return;
            }

            _forumTopicHeaderTopic = messageTopic;

            ForumTopicHeader.Tag = messageTopic;

            if (ViewModel.ClientService.TryGetForumTopic(ViewModel.ChatId, messageTopic, out ForumTopic forumTopic))
            {
                if (string.IsNullOrEmpty(forumTopic.Info.Name))
                {
                    _forumTopicHeaderTopic = null;
                }

                ForumTopicHeaderLabel.Text = forumTopic.Info.Name;
                ForumTopicHeaderPhoto.Source = null;

                if (forumTopic.Info.IsGeneral || forumTopic.Info.Icon.CustomEmojiId != 0)
                {
                    ForumTopicHeaderTypeIcon.SetStatus(ViewModel.ClientService, forumTopic.Info.Icon);
                    ForumTopicHeaderIconRoot.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ForumTopicHeaderTypeIcon.ClearStatus();
                    ForumTopicHeaderIconRoot.Visibility = Visibility.Visible;

                    var brush = ForumTopicCell.GetIconGradient(forumTopic.Info.Icon);

                    ForumTopicHeaderIconPath.Fill = brush;
                    ForumTopicHeaderIconPath.Stroke = new SolidColorBrush(brush.GradientStops[1].Color);

                    ForumTopicHeaderIconText.Text = InitialNameStringConverter.Convert(forumTopic.Info.Name);
                }
            }
            else if (ViewModel.ClientService.TryGetDirectMessagesChatTopic(ViewModel.ChatId, messageTopic, out DirectMessagesChatTopic directMessagesChatTopic))
            {
                ForumTopicHeaderLabel.Text = ViewModel.ClientService.GetTitle(directMessagesChatTopic.SenderId);
                ForumTopicHeaderPhoto.Source = ProfilePictureSource.MessageSender(ViewModel.ClientService, directMessagesChatTopic.SenderId);

                ForumTopicHeaderTypeIcon.ClearStatus();
                ForumTopicHeaderIconRoot.Visibility = Visibility.Collapsed;
            }
        }

        private readonly Dictionary<long, WeakReference> _prev = new();

        public void PlayMessage(MessageViewModel message, FrameworkElement target)
        {
            var text = message.Content as MessageText;

            if (PowerSavingPolicy.AutoPlayAnimations && (message.Content is MessageAnimation || (text?.LinkPreview != null && text.LinkPreview.Type is LinkPreviewTypeAnimation) || (message.Content is MessageGame game && game.Game.Animation != null)))
            {
                if (_prev.TryGetValue(message.AnimationHash(), out WeakReference reference) && reference.Target is IPlayerView item)
                {
#if !LINUX
                    GalleryViewModelBase viewModel;
                    if (message.Content is MessageAnimation)
                    {
                        viewModel = new ChatGalleryViewModel(ViewModel.ClientService, ViewModel.StorageService, ViewModel.Aggregator, message.ChatId, ViewModel.TopicId, message, null);
                    }
                    else
                    {
                        viewModel = new StandaloneGalleryViewModel(ViewModel.ClientService, ViewModel.StorageService, ViewModel.Aggregator, new GalleryMessage(ViewModel.ClientService, message, null));
                    }

                    ViewModel.NavigationService.ShowGallery(viewModel, target);
#endif
                }
                else
                {
                    ViewVisibleMessages();
                }
            }
            else
            {
                if (_prev.ContainsKey(message.AnimationHash()))
                {
                    Play(new (SelectorItem, MessageViewModel)[0]);
                }
                else
                {
                    if (_messageIdToSelector.TryGetValue(message.Id, out ChatHistoryViewItem container))
                    {
                        Play(new (SelectorItem, MessageViewModel)[] { (container, message) });
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (int, IPlayerView) Play(SelectorItem container, MessageViewModel message)
        {
            if (message.Content is MessageDice dice)
            {
                if (message.GeneratedContentUnread)
                {
                    message.GeneratedContentUnread = dice.IsInitialState();
                }
                else
                {
                    // We don't want to start already played dices
                    // but we don't even want to stop them if they're already playing.
                    return (message.AnimationHash(), null);
                }
            }
            else if (message.Content is MessageStakeDice stakeDice)
            {
                if (message.GeneratedContentUnread)
                {
                    message.GeneratedContentUnread = stakeDice.IsInitialState();
                }
                else
                {
                    // We don't want to start already played dices
                    // but we don't even want to stop them if they're already playing.
                    return (message.AnimationHash(), null);
                }
            }

            if (message.IsAnimatedContentDownloadCompleted())
            {
                var root = container.ContentRoot() as FrameworkElement;
                if (root is not MessageSelector selector || selector.Content is not MessageBubble bubble)
                {
                    return (0, null);
                }

                var player = bubble.GetPlaybackElement();
                if (player != null)
                {
                    return (message.AnimationHash(), player);
                }
            }

            if (message.Effect != null && message.GeneratedContentUnread && message.SendingState == null)
            {
                var root = container.ContentRoot() as FrameworkElement;
                if (root is not MessageSelector selector || selector.Content is not MessageBubble bubble)
                {
                    return (0, null);
                }

                message.GeneratedContentUnread = !bubble.PlayMessageEffect(message);
            }

            return (0, null);
        }

        // TODO: remove
        public void Play(IEnumerable<(SelectorItem Container, MessageViewModel Message)> items)
        {
            Dictionary<long, IPlayerView> next = null;
            HashSet<long> prev = null;

            foreach (var pair in items)
            {
                var message = pair.Message;
                var container = pair.Container;

                if (message.Content is MessageDice dice)
                {
                    if (message.GeneratedContentUnread)
                    {
                        message.GeneratedContentUnread = dice.IsInitialState();
                    }
                    else
                    {
                        // We don't want to start already played dices
                        // but we don't even want to stop them if they're already playing.
                        prev ??= new HashSet<long>();
                        prev.Add(message.AnimationHash());
                        continue;
                    }
                }
                else if (message.Content is MessageStakeDice stakeDice)
                {
                    if (message.GeneratedContentUnread)
                    {
                        message.GeneratedContentUnread = stakeDice.IsInitialState();
                    }
                    else
                    {
                        // We don't want to start already played dices
                        // but we don't even want to stop them if they're already playing.
                        prev ??= new HashSet<long>();
                        prev.Add(message.AnimationHash());
                        continue;
                    }
                }

                if (message.IsAnimatedContentDownloadCompleted())
                {
                    var root = container.ContentRoot() as FrameworkElement;
                    if (root is not MessageSelector selector || selector.Content is not MessageBubble bubble)
                    {
                        continue;
                    }

                    var player = bubble.GetPlaybackElement();
                    if (player != null)
                    {
                        next ??= new Dictionary<long, IPlayerView>();
                        next[message.AnimationHash()] = player;
                    }
                }

                if (message.Effect != null && message.GeneratedContentUnread && message.SendingState == null)
                {
                    var root = container.ContentRoot() as FrameworkElement;
                    if (root is not MessageSelector selector || selector.Content is not MessageBubble bubble)
                    {
                        continue;
                    }

                    message.GeneratedContentUnread = !bubble.PlayMessageEffect(message);
                }
            }

            var skip = next != null && prev != null
                ? next.Keys.Union(prev)
                : next != null ? next.Keys
                : prev;

            var source = skip != null
                ? _prev.Keys.Except(skip).ToList()
                : _prev.Keys.ToList();

            foreach (var item in source)
            {
                var presenter = _prev[item].Target as IPlayerView;
                if (presenter != null && presenter.LoopCount == 0)
                {
                    presenter.ViewportChanged(false);
                }

                _prev.Remove(item);
            }

            if (next != null)
            {
                foreach (var item in next)
                {
                    _prev[item.Key] = new WeakReference(item.Value);
                    item.Value.ViewportChanged(true);
                }
            }
        }









        private readonly MessageContentRecyclePool _contentRecyclePool = new();

        private readonly Dictionary<long, ChatHistoryViewItem> _albumIdToSelector = new();
        private readonly Dictionary<long, ChatHistoryViewItem> _messageIdToSelector = new();
        private readonly MultiValueDictionary<long, long> _messageIdToMessageIds = new();

        private readonly MultiValueDictionary<int, ChatHistoryViewItem> _messageTopicToSelectors = new();

        private readonly Dictionary<ChatHistoryViewItemType, ChoosingItemStrategy> _typeToStrategy = new();

        record ChoosingItemStrategy
        {
            public ChoosingItemStrategy(DataTemplate itemTemplate)
            {
                Queue = new();
                ItemTemplate = itemTemplate;
            }

            public DataTemplate ItemTemplate { get; }

            public HashSet<SelectorItem> Queue { get; }

            public int TotalCount { get; set; }
        }

#if INSTRUMENTATION
        // The message tree's legitimate holders: realized containers + the recycle pools.
        //
        // Roots and descent are exposed separately, rather than as one Analyze call, because orphans are
        // "registered but unreachable from the roots" — analysing one area at a time would report every
        // OTHER area's live objects as leaked. There is one call, over the union of every area's roots;
        // MainPage.DebugAnalyzeOrphans assembles it.
        public IEnumerable<object> DebugRoots()
        {
            if (Messages?.ItemsPanelRoot is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is ChatHistoryViewItem)
                    {
                        yield return child;
                    }
                }
            }

            foreach (var strategy in _typeToStrategy.Values)
            {
                foreach (var container in strategy.Queue)
                {
                    yield return container;
                }
            }

            // Nulled when the view unloads, so a dead view stops rooting the panel and the whole
            // sticker subtree is reported under it.
            if (StickersPanel != null)
            {
                yield return StickersPanel;
            }
        }

        // Hardcoded per-type descent (each instrumented type exposes its instrumented children).
        // Returns nothing for types this area does not own, so it composes with the other areas'.
        internal static IEnumerable<object> DebugChildrenOf(object node)
        {
            return node switch
            {
                // A ChatView that is still alive owns its containers through the realized panel
                // and the recycle queues, so descending into it reports them under it instead of
                // as unparented orphans -- which is what a leaked view looked like before.
                ChatView x => x.DebugRoots(),
                ChatHistoryViewItem x => x.DebugChildren(),
                MessageSelector x => x.DebugChildren(),
                MessageBubble x => x.DebugChildren(),
                MessageTextBlock x => x.DebugChildren(),
                ReactionsPanel x => x.DebugChildren(),
                Telegram.Controls.Messages.Content.InstantContent x => x.DebugChildren(),
                Telegram.Controls.Messages.Content.ChecklistContent x => x.DebugChildren(),
                Telegram.Controls.Messages.Content.PollContent x => x.DebugChildren(),
                Telegram.Controls.Messages.Content.WebPageContent x => x.DebugChildren(),
                Telegram.Controls.Messages.Content.GameContent x => x.DebugChildren(),
                Telegram.Controls.Messages.Content.AlbumContent x => x.DebugChildren(),
                _ => System.Array.Empty<object>()
            };
        }
#endif

        public string GetVirtualizationInfo()
        {
            if (Messages.ItemsPanelRoot is ItemsStackPanel panel)
            {
                var queued = _typeToStrategy.Values.Sum(x => x.Queue.Count);
                var total = _typeToStrategy.Values.Sum(x => x.TotalCount);
#if LINUX
                var cached = panel.LastVisibleIndex + panel.FirstVisibleIndex + 1;
                return string.Format(", [{0}-{1}] {2}/{3}{4}", panel.FirstVisibleIndex, panel.LastVisibleIndex, queued, total, total - queued - cached > 0 ? $", {total - queued - cached} missing" : "");
#else
                var cached = panel.LastCacheIndex + panel.FirstCacheIndex + 1;
                return string.Format(", [{0}-{1}] {2}/{3}{4}", panel.FirstCacheIndex, panel.LastCacheIndex, queued, total, total - queued - cached > 0 ? $", {total - queued - cached} missing" : "");
#endif
            }

            return string.Empty;
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            var typeName = SelectTemplateCore(args.Item);
            var relevantHashSet = _typeToStrategy[typeName];

            // args.ItemContainer is used to indicate whether the ListView is proposing an
            // ItemContainer (ListViewItem) to use. If args.Itemcontainer != null, then there was a
            // recycled ItemContainer available to be reused.
            if (args.ItemContainer is ChatHistoryViewItem selector)
            {
                if (selector.TypeName.Equals(typeName))
                {
                    // Suggestion matches what we want, so remove it from the recycle queue
                    relevantHashSet.Queue.Remove(args.ItemContainer);
                }
                else
                {
                    // TODO: threshold could be made dynamic...
                    // By example if we are in a channel and typeName is UserMessageTemplate, we can just override
                    // Same thing should probably apply to all service messages.

                    // Code inside this branch is the one recommended by Microsoft, that bugs in some scenarios.
                    if (relevantHashSet.Queue.Count > 0)
                    {
                        // The ItemContainer's datatemplate does not match the needed
                        // datatemplate.
                        // Don't remove it from the recycle queue, since XAML will resuggest it later
                        args.ItemContainer = null;
                    }
                    else
                    {
                        var recycledHashSet = _typeToStrategy[selector.TypeName];

                        // Suggested container doesn't match what we want, but ICG2 is stuck in a loop.
                        relevantHashSet.TotalCount++;

                        selector.TypeName = typeName;
                        selector.ContentTemplate = relevantHashSet.ItemTemplate;

                        // Remove the container from the old queue and update the counter.
                        recycledHashSet.Queue.Remove(args.ItemContainer);
                        recycledHashSet.TotalCount--;
                    }
                }
            }

            // If there was no suggested container or XAML's suggestion was a miss, pick one up from the recycle queue
            // or create a new one
            if (args.ItemContainer == null)
            {
                // See if we can fetch from the correct list.
                if (relevantHashSet.Queue.Count > 0)
                {
                    // A HashSet because the block above removes a specific container, which a
                    // Queue cannot do. Taking an arbitrary one out is then a matter of stopping
                    // at the first: First() cannot fast path a set and boxes its struct
                    // enumerator, once for every container realized.
                    foreach (var container in relevantHashSet.Queue)
                    {
                        args.ItemContainer = container;
                        break;
                    }

                    relevantHashSet.Queue.Remove(args.ItemContainer);
                }
                else
                {
                    relevantHashSet.TotalCount++;

                    // There aren't any (recycled) ItemContainers available. So a new one
                    // needs to be created.
                    selector = new ChatHistoryViewItem(Messages, typeName);
                    selector.ContentTemplate = relevantHashSet.ItemTemplate;
                    selector.Style = sender.ItemContainerStyle;
                    selector.IsHitTestVisible = !FromPreview;
                    selector.AddHandler(ContextRequestedEvent, _contextRequestedHandler ??= new TypedEventHandler<UIElement, ContextRequestedEventArgs>(Message_ContextRequested), true);

                    args.ItemContainer = selector;
                }
            }

            // Indicate to XAML that we picked a container for it
            args.IsContainerPrepared = true;
        }

#if LINUX
        private readonly Dictionary<ChatHistoryViewItem, ContainerContentChangingEventArgs> _containerArgs = new();

        /// <summary>
        /// Binds a container that had nothing to bind to when it was prepared. Uno makes a fresh
        /// ContainerContentChangingEventArgs for every container, so the one kept here still
        /// describes this container and this message.
        /// </summary>
        private void OnContainerLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ChatHistoryViewItem container && _containerArgs.TryGetValue(container, out var args))
            {
                BindWhenExpanded(container, args, 0);
            }
        }

        /// <summary>
        /// Bind the row as soon as its bubble exists, however many layout passes that takes.
        ///
        /// <para><c>Loaded</c> is usually late enough — but not always. Uno expands the item
        /// template from <c>ContentPresenter.EnterImpl</c>, and when a whole screenful is realized
        /// at once (opening a chat at a message, which is what a reply, a search result or the
        /// first unread message do) the container's <c>Loaded</c> can beat it: <c>ContentRoot()</c>
        /// answers Uno's <c>ImplicitTextBlock</c> placeholder and there is nothing to bind to.
        /// Giving up there left the row laid out as an empty 20 px bubble — measured on a jump into
        /// the middle of a history, 26 rows of the screenful blank at once — and the bubble that
        /// arrives a pass later never gets its message. So it is retried, bounded, and only from
        /// the container's own layout passes.</para>
        /// </summary>
        private void BindWhenExpanded(ChatHistoryViewItem container, ContainerContentChangingEventArgs args, int attempt)
        {
            const int Attempts = 8;

            var root = container.ContentRoot();
            if (root is MessageSelector or MessageService)
            {
                OnContainerContentChanging(Messages, args);
                return;
            }

            if (attempt >= Attempts)
            {
                // Still nothing after that many passes. Worth a line, because the symptom on screen
                // is an empty row and there is no exception anywhere near it.
                var message = args.Item as MessageViewModel;
                Logger.Warning($"No message content under the container of {message?.Id} ({message?.Content?.GetType().Name ?? "?"}) "
                    + $"after {Attempts} layout passes, root is {root?.GetType().Name ?? "null"}");
                return;
            }

            void expanded(object sender, object e)
            {
                container.LayoutUpdated -= expanded;

                // The container may have been recycled onto another message while we waited; the
                // dictionary is what says which message this container is for now.
                if (_containerArgs.TryGetValue(container, out var current) && current == args)
                {
                    BindWhenExpanded(container, args, attempt + 1);
                }
            }

            container.LayoutUpdated += expanded;
        }
#endif

#if LINUX
        /// <summary>
        /// Uno raises <c>ContainerContentChanging</c> - and the container's <c>Loaded</c>, which is
        /// where the Linux path actually binds - from <c>ItemsControl.PrepareContainerForIndex</c>,
        /// that is from INSIDE <c>VirtualizingPanelLayout.UpdateLayout</c>. That method does
        /// <c>OwnerPanel.ShouldInterceptInvalidate = true</c> on entry and back to false on exit,
        /// with no try/finally, and <c>UIElement.InvalidateMeasure</c> returns immediately while the
        /// flag is set. So one exception escaping the binding of one row leaves the
        /// <c>ItemsStackPanel</c> deaf to every InvalidateMeasure for the rest of the process: the
        /// history measures 0 px high, realizes no container at all, and never recovers - not even
        /// when a different chat is opened, because MasterDetailView reuses the same ChatView and
        /// the same panel. The symptom is an empty history with no exception anywhere near it.
        /// One bad row must not cost the whole history, so it is caught and named here.
        /// </summary>
        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            try
            {
                OnContainerContentChangingImpl(sender, args);
            }
            catch (Exception ex)
            {
                var message = args.Item as MessageViewModel;
                Logger.Error($"Binding the container of message {message?.Id} ({message?.Content?.GetType().Name ?? "?"}) threw, the row stays empty: {ex}");
            }
        }

        private void OnContainerContentChangingImpl(ListViewBase sender, ContainerContentChangingEventArgs args)
#else
        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
#endif
        {
            args.Handled = true;

            if (args.Item is not MessageViewModel message || args.ItemContainer is not ChatHistoryViewItem container)
            {
                return;
            }

#if LINUX
            // Uno raises this once per prepared container (InRecycleQueue is always false);
            // recycling goes through ChatHistoryView.ClearContainerForItemOverride instead.
            //
            // It is also raised from ItemsControl.PrepareContainerForIndex, that is *before* the
            // container enters the visual tree - and Uno expands the item template from
            // ContentPresenter.EnterImpl, so a container that has just been created has no bubble
            // under it yet and ContentRoot() answers null. Everything below asks for the bubble
            // with a type test, so nothing threw: the containers were laid out empty and the
            // history came up blank. A container that is being reused already has its bubble and
            // binds here and now; a new one binds again when it is loaded. Same shape as
            // ChatListListView.OnContainerContentChanging.
            _containerArgs[container] = args;

            container.Loaded -= OnContainerLoaded;
            container.Loaded += OnContainerLoaded;

            UpdateCache(message, container, false);
#else
            UpdateCache(message, container, args.InRecycleQueue);

            if (args.InRecycleQueue)
            {
                // XAML has indicated that the item is no longer being shown, so add it to the recycle queue
                _typeToStrategy[container.TypeName].Queue.Add(args.ItemContainer);

                if (args.ItemContainer.ContentRoot() is MessageSelector selector)
                {
                    selector.Recycle();

                    if (_sizeChangedHandler != null)
                    {
                        selector.SizeChanged -= _sizeChangedHandler;
                    }
                }
                else if (args.ItemContainer.ContentRoot() is MessageService service)
                {
                    service.Recycle();
                }

                if (message.Content is MessageHeaderUnread)
                {
                    args.ItemContainer.EffectiveViewportChanged -= HeaderUnread_EffectiveViewportChanged;
                }

                if (_oldestItem == container)
                {
                    _oldestItem = null;
                    _oldestItemAsHeader = null;
                    container.UpdatePadding(0, -1);

                    UpdateOldestItemAsHeader(false);
                }

                if (_newestItem == container)
                {
                    _newestItem = null;
                    _newestItemAsFooter = null;
                    container.UpdatePadding(-1, 0);

                    UpdateNewestItemAsFooter(false);
                }
            }
            else
#endif
            {
                if (message.Content is MessageHeaderUnread)
                {
                    args.ItemContainer.EffectiveViewportChanged += HeaderUnread_EffectiveViewportChanged;
                }

                var content = args.ItemContainer.ContentRoot() as FrameworkElement;
                if (content == null)
                {
                    return;
                }

                if (content is MessageService service)
                {
                    if (message.Content is MessageHeaderUnread)
                    {
                        AutomationProperties.SetName(service, Strings.UnreadMessages);
#if LINUX
                        RegisterEvents(sender, args);
#else
                        args.RegisterUpdateCallback(2, RegisterEvents);
#endif
                    }

                    service.UpdateMessage(args.Item as MessageViewModel);
                }
                else if (content is MessageSelector checkbox)
                {
                    // TODO: are there chances that at this point TextArea is not up to date yet?
                    checkbox.IsTrackerEnabled = !FromPreview;
                    checkbox.PrepareForItemOverride(message,
                        _viewModel.Type is DialogType.History or DialogType.Thread or DialogType.ScheduledMessages
                        && _replyEnabled is true);

                    if (checkbox.Content is MessageBubble bubble)
                    {
                        if (bubble.NeedShadow && ApiInfo.CanCreateThemeShadow && AppSettings.Diagnostics.BubbleElevationDebug)
                        {
                            bubble.UpdateShadow(_shadow);
                        }

                        bubble.UpdateContentRecyclePool(AppSettings.Diagnostics.BubbleContentRecyclingDebug ? _contentRecyclePool : null);

                        bubble.UpdateQuery(ViewModel.Search?.Query, false);
                        bubble.UpdateMessage(args.Item as MessageViewModel);

#if LINUX
                        RegisterEvents(sender, args);
#else
                        args.RegisterUpdateCallback(2, RegisterEvents);
#endif
                    }

                    checkbox.UpdateMessage(message, Messages, ViewModel.IsSelectionEnabled);
                    checkbox.HorizontalAlignment = message.Date == 0 && message.Id == 0
                        ? HorizontalAlignment.Center
                        : HorizontalAlignment.Stretch;
                }

                if (ViewModel.IsSavedMessagesTab)
                {
                    return;
                }

                void UpdateNewestOldest(bool? needed, bool? loaded, ref ChatHistoryViewItem item, ref ChatHistoryViewItem headerFooter, Index index)
                {
                    if (args.ItemIndex == (index.IsFromEnd ? ViewModel.Items.Count - index.Value : index.Value) && loaded is true)
                    {
                        item = container;
                        headerFooter?.UpdatePadding(index.IsFromEnd ? -1 : 0, index.IsFromEnd ? 0 : -1);

                        if (needed is true)
                        {
                            headerFooter = container;
                            headerFooter.UpdatePadding(index.IsFromEnd ? -1 : _messagesScrollBarPadding, index.IsFromEnd ? _messagesHeaderRootPadding : -1);
                        }
                        else
                        {
                            headerFooter = null;
                            container.UpdatePadding(index.IsFromEnd ? -1 : 0, index.IsFromEnd ? 0 : -1);
                        }
                    }
                    else
                    {
                        container.UpdatePadding(index.IsFromEnd ? -1 : 0, index.IsFromEnd ? 0 : -1);
                    }
                }

                UpdateNewestOldest(_oldestItemAsHeaderNeeded, ViewModel.IsOldestSliceLoaded, ref _oldestItem, ref _oldestItemAsHeader, 0);
                UpdateNewestOldest(_newestItemAsFooterNeeded, ViewModel.IsNewestSliceLoaded, ref _newestItem, ref _newestItemAsFooter, ^1);
            }
        }

        private void RegisterEvents(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            args.Handled = true;

            if (args.Item is MessageViewModel { Content: MessageHeaderUnread })
            {
                _headerUnreadNotReady = false;

                if (_headerUnreadRetry)
                {
                    UpdateMessagesHeaderPadding();
                }

                return;
            }

            if (args.ItemContainer.ContentRoot() is MessageSelector selector && selector.Content is MessageBubble bubble)
            {
                selector.SizeChanged += _sizeChangedHandler ??= new SizeChangedEventHandler(Item_SizeChanged);
                bubble.RegisterEvents();
            }
        }

        private TypedEventHandler<UIElement, ContextRequestedEventArgs> _contextRequestedHandler;
        private SizeChangedEventHandler _sizeChangedHandler;

        private void OnPreparingContainerForItem(object sender, ChatHistoryViewItem selector)
        {
#if LINUX
            selector.IsHitTestVisible = !FromPreview;
#endif
            selector.AddHandler(ContextRequestedEvent, _contextRequestedHandler ??= new TypedEventHandler<UIElement, ContextRequestedEventArgs>(Message_ContextRequested), true);
        }

#if LINUX
        private void OnContainerRecycled(ChatHistoryViewItem container, object item)
        {
            _containerArgs.Remove(container);

            if (item is not MessageViewModel message)
            {
                return;
            }

            UpdateCache(message, container, true);

            if (container.ContentRoot() is MessageSelector selector)
            {
                selector.Recycle();

                if (_sizeChangedHandler != null)
                {
                    selector.SizeChanged -= _sizeChangedHandler;
                }
            }
            else if (container.ContentRoot() is MessageService service)
            {
                service.Recycle();
            }

            if (message.Content is MessageHeaderUnread)
            {
                container.EffectiveViewportChanged -= HeaderUnread_EffectiveViewportChanged;
            }

            if (_oldestItem == container)
            {
                _oldestItem = null;
                _oldestItemAsHeader = null;
                container.UpdatePadding(0, -1);

                UpdateOldestItemAsHeader(false);
            }

            if (_newestItem == container)
            {
                _newestItem = null;
                _newestItemAsFooter = null;
                container.UpdatePadding(-1, 0);

                UpdateNewestItemAsFooter(false);
            }
        }
#endif

        private void Item_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var next = e.NewSize.ToVector2();
            var prev = e.PreviousSize.ToVector2();

            var diff = next.Y - prev.Y;

            var panel = Messages.ItemsPanelRoot as ItemsStackPanel;
            if (panel == null || prev.Y == next.Y || Math.Abs(diff) == 6)
            {
                return;
            }

            var selector = sender as MessageSelector;

            var message = selector?.Message;
            if (message == null || message.AnimationState != MessageAnimationState.Added)
            {
                if (message != null && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
                {
                    message.AnimationState = MessageAnimationState.Added;
                }
                else
                {
                    return;
                }
            }

            var index = _messages.IndexOf(message);
            if (index < panel.LastVisibleIndex && e.PreviousSize.Width < 1 && e.PreviousSize.Height < 1)
            {
                return;
            }

            var container = ContainerFromItem(message.Id);
            if (container == null)
            {
                return;
            }

            AnimateSizeChanged(panel, container, index, prev, next);
        }

#if LINUX
        /// <summary>
        /// One slide animation, for ONE visual. Never reuse the instance: see the note at its two
        /// call sites in AnimateSizeChanged.
        /// </summary>
        private static ScalarKeyFrameAnimation Slide(float from)
        {
            var animation = BootStrapper.Current.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, from);
            animation.InsertKeyFrame(1, 0);

            return animation;
        }
#endif

        private void AnimateSizeChanged(ItemsStackPanel panel, SelectorItem selector, int index, Vector2 prev, Vector2 next)
        {
            var diff = next.Y - prev.Y;

            if (index >= panel.FirstVisibleIndex && index <= panel.LastVisibleIndex)
            {
#if LINUX
                var direction = Messages.CurrentScrollingMode == ItemsUpdatingScrollMode.KeepItemsInView ? -1 : 1;
#else
                var direction = panel.ItemsUpdatingScrollMode == ItemsUpdatingScrollMode.KeepItemsInView ? -1 : 1;
#endif
                var edge = (index == panel.LastVisibleIndex && direction == 1) || (index == panel.FirstVisibleIndex && direction == -1);

                if (edge && !Messages.ScrollingHost.ViewportContains(selector))
                {
                    direction *= -1;
                }

#if LINUX
                var first = direction == 1 ? panel.FirstVisibleIndex : index + 1;
                var last = direction == 1 ? index : panel.LastVisibleIndex;
#else
                var first = direction == 1 ? panel.FirstCacheIndex : index + 1;
                var last = direction == 1 ? index : panel.LastCacheIndex;
#endif

                var batch = BootStrapper.Current.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
#if !LINUX
                var anim = BootStrapper.Current.Compositor.CreateScalarKeyFrameAnimation();
                anim.InsertKeyFrame(0, diff * direction);
                anim.InsertKeyFrame(1, 0);
                //anim.Duration = TimeSpan.FromSeconds(5);
#endif

                for (int i = first; i <= last; i++)
                {
                    var container = Messages.ContainerFromIndex(i) as SelectorItem;
                    if (container == null)
                    {
                        continue;
                    }

                    var child = VisualTreeHelper.GetChild(container, 0) as UIElement;
                    if (child != null)
                    {
                        var visual = ElementComposition.GetElementVisual(child);
#if LINUX
                        // A fresh instance per container. Uno's Compositor.RegisterAnimation keys
                        // its dictionary on the ANIMATION OBJECT and does Dictionary.Add, so the
                        // second container in this loop threw ArgumentException - out of
                        // ItemsPanelRoot_LayoutUpdated, on the dispatcher, unhandled. Measured in a
                        // two-and-a-half-minute session with some scrolling: 48 of them, one per
                        // batch of inserted messages, each one abandoning the rest of that
                        // LayoutUpdated. See PORTING.md 6.
                        visual.StartAnimation("Offset.Y", Slide(diff * direction));
#else
                        visual.StartAnimation("Offset.Y", anim);
#endif
                    }
                }

                batch.End();
            }
        }

        private void AnimateSizeChanged(ItemsStackPanel panel, IndexShiftTracker.Range range, float diff)
        {
#if LINUX
            var direction = Messages.CurrentScrollingMode == ItemsUpdatingScrollMode.KeepItemsInView ? -1 : 1;
#else
            var direction = panel.ItemsUpdatingScrollMode == ItemsUpdatingScrollMode.KeepItemsInView ? -1 : 1;
#endif

            if (range.Anchor)
            {
                direction *= -1;
            }

#if LINUX
            var first = direction == 1 ? panel.FirstVisibleIndex : range.Index;
            var last = direction == 1 ? range.Index - 1 : panel.LastVisibleIndex;
#else
            var first = direction == 1 ? panel.FirstCacheIndex : range.Index;
            var last = direction == 1 ? range.Index - 1 : panel.LastCacheIndex;
#endif

            var firstVisible = first >= panel.FirstVisibleIndex && first <= panel.LastVisibleIndex;
            var lastVisible = last >= panel.FirstVisibleIndex && last <= panel.LastVisibleIndex;

            if (firstVisible || lastVisible)
            {
                var batch = Window.Current.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
#if !LINUX
                var anim = Window.Current.Compositor.CreateScalarKeyFrameAnimation();
                anim.InsertKeyFrame(0, diff * direction);
                anim.InsertKeyFrame(1, 0);
                //anim.Duration = TimeSpan.FromSeconds(5);
#endif

                for (int i = first; i <= last; i++)
                {
                    var container = Messages.ContainerFromIndex(i) as SelectorItem;
                    if (container == null)
                    {
                        continue;
                    }

                    var child = VisualTreeHelper.GetChild(container, 0) as UIElement;
                    if (child != null)
                    {
                        var visual = ElementComposition.GetElementVisual(child);
#if LINUX
                        // A fresh instance per container. Uno's Compositor.RegisterAnimation keys
                        // its dictionary on the ANIMATION OBJECT and does Dictionary.Add, so the
                        // second container in this loop threw ArgumentException - out of
                        // ItemsPanelRoot_LayoutUpdated, on the dispatcher, unhandled. Measured in a
                        // two-and-a-half-minute session with some scrolling: 48 of them, one per
                        // batch of inserted messages, each one abandoning the rest of that
                        // LayoutUpdated. See PORTING.md 6.
                        visual.StartAnimation("Offset.Y", Slide(diff * direction));
#else
                        visual.StartAnimation("Offset.Y", anim);
#endif
                    }
                }

                batch.End();
            }
        }

        private ChatHistoryViewItemType SelectTemplateCore(object item)
        {
            var message = item as MessageViewModel;
            if (message == null)
            {
                return ChatHistoryViewItemType.Incoming;
            }

            if (message.Content is MessageUnsupported)
            {
                return ChatHistoryViewItemType.Unsupported;
            }
            else if (message.IsService)
            {
                return message.Content switch
                {
                    MessageGiveawayPrizeStars => ChatHistoryViewItemType.ServiceGiftCode,
                    MessageGiftedPremium or MessageGiftedStars or MessageGift or MessagePremiumGiftCode => ChatHistoryViewItemType.ServiceGift,
                    MessageUpgradedGift => ChatHistoryViewItemType.ServiceUpgradedGift,
                    MessageUpgradedGiftPurchaseOffer => ChatHistoryViewItemType.ServiceUpgradedGiftPurchaseOffer,
                    MessageChatHasProtectedContentDisableRequested => ChatHistoryViewItemType.ServiceChatHasProtectedContentDisableRequested,
                    MessageChatChangePhoto or MessageSuggestProfilePhoto or MessageAsyncStory => ChatHistoryViewItemType.ServicePhoto,
                    MessageSuggestBirthdate => ChatHistoryViewItemType.ServiceBirthdate,
                    MessageChatSetBackground { OldBackgroundMessageId: 0 } or MessageChatEvent { Action: ChatEventBackgroundChanged { NewBackground: not null } } => ChatHistoryViewItemType.ServiceBackground,
                    MessageHeaderUnread => ChatHistoryViewItemType.ServiceUnread,
                    MessageHeaderMessageTopic => ChatHistoryViewItemType.ServiceForumTopic,
                    MessageHeaderAccountInfo => ChatHistoryViewItemType.ServiceAccountInfo,
                    MessageHeaderNewThread => ChatHistoryViewItemType.ServiceNewThread,
                    _ => ChatHistoryViewItemType.Service
                };
            }

            if (message.IsChannelPost || (message.IsSaved && message.ForwardInfo?.Source is { IsOutgoing: false }))
            {
                return ChatHistoryViewItemType.Incoming;
            }
            else if (message.IsOutgoing || message.ForwardInfo?.Source is { IsOutgoing: true })
            {
                return ChatHistoryViewItemType.Outgoing;
            }

            return ChatHistoryViewItemType.Incoming;
        }

        public bool IsItemVisible(long id)
        {
            if (Messages.ItemsPanelRoot is ItemsStackPanel panel && _messageIdToSelector.TryGetValue(id, out var container))
            {
                var index = Messages.IndexFromContainer(container);
                return index >= panel.FirstVisibleIndex && index <= panel.LastVisibleIndex;
            }

            return false;
        }

        public bool HasContainerForItem(long id)
        {
            return _messageIdToSelector.ContainsKey(id);
        }

        public SelectorItem ContainerFromItem(long id)
        {
            if (_messageIdToSelector.TryGetValue(id, out var container))
            {
                return container;
            }

            return null;
        }

        public void UpdateContainerWithMessageId(long id, Action<SelectorItem> action)
        {
            if (_messageIdToSelector.TryGetValue(id, out var container))
            {
                action(container);
            }
        }

        public void UpdateBubbleWithMessageId(long id, Action<MessageBubble> action)
        {
            if (_messageIdToSelector.TryGetValue(id, out var container))
            {
                if (container.ContentRoot() is MessageSelector selector && selector.Content is MessageBubble bubble)
                {
                    action(bubble);
                }
            }
        }

        public void UpdateBubbleWithMediaAlbumId(long id, Action<MessageBubble> action)
        {
            if (_albumIdToSelector.TryGetValue(id, out var container))
            {
                if (container.ContentRoot() is MessageSelector selector && selector.Content is MessageBubble bubble)
                {
                    action(bubble);
                }
            }
        }

        public void UpdateBubbleWithReplyToMessageId(long id, Action<MessageBubble, MessageViewModel> action)
        {
            if (_messageIdToMessageIds.TryGetValue(id, out var ids))
            {
                foreach (var messageId in ids)
                {
                    if (_viewModel.Items.TryGetValue(messageId, out MessageViewModel message))
                    {
                        if (message.ReplyToItem is MessageViewModel && _messageIdToSelector.TryGetValue(messageId, out var container))
                        {
                            if (container.ContentRoot() is MessageSelector selector && selector.Content is MessageBubble bubble)
                            {
                                action(bubble, message);
                            }
                        }
                    }
                }
            }
        }

        public void UpdateServiceWithForumTopic(int forumTopicId)
        {
            if (_messageTopicToSelectors.TryGetValue(forumTopicId, out var containers))
            {
                foreach (var container in containers)
                {
                    if (container.ContentRoot() is MessageHeaderMessageTopicContent header)
                    {
                        header.UpdateMessageTopic();
                    }
                }
            }

            if (_forumTopicHeaderTopic.IsForum(forumTopicId))
            {
                _forumTopicHeaderTopic = null;
                UpdateForumTopicHeader(new MessageTopicForum(forumTopicId));
            }
        }

        public void ForEach(Action<MessageBubble, MessageViewModel> action)
        {
            foreach (var item in _messageIdToSelector)
            {
                if (_viewModel.Items.TryGetValue(item.Key, out MessageViewModel message))
                {
                    if (item.Value.ContentRoot() is MessageSelector selector && selector.Content is MessageBubble bubble)
                    {
                        action(bubble, message);
                    }
                }
            }
        }

        public void ForEach(Action<MessageBubble> action)
        {
            foreach (var item in _messageIdToSelector)
            {
                if (item.Value.ContentRoot() is MessageSelector selector && selector.Content is MessageBubble bubble)
                {
                    action(bubble);
                }
            }
        }

        public void UpdateMessageSendSucceeded(long oldMessageId, MessageViewModel message)
        {
            if (_messageIdToSelector.TryGetValue(oldMessageId, out ChatHistoryViewItem container))
            {
                _messageIdToSelector[message.Id] = container;
                _messageIdToSelector.Remove(oldMessageId);
            }

            if (message.ReplyTo is MessageReplyToMessage replyToMessage && _messageIdToMessageIds.TryGetValue(replyToMessage.MessageId, out var ids))
            {
                ids.Add(message.Id);
                ids.Remove(oldMessageId);
            }
        }

        public void UpdateMessageSummary(MessageViewModel message)
        {
            if (_stickySummaryAboveMessage == message)
            {
                StickySummaryAboveButton.Glyph = message.SummarizedText == null
                    ? Icons.ArrowMinimizeSparkles16
                    : Icons.ArrowMaximizeSparkles16;
            }
        }

        public void UpdateMessageSelection(MessageViewModel message)
        {
            // Album sub-messages aren't list items (not in _messageIdToSelector); they're reached
            // through their album container via MediaAlbumId. The hosting MessageSelector then
            // refreshes the album-level state and the specific child (see MessageSelector.UpdateSelection).
            if (_messageIdToSelector.TryGetValue(message.Id, out var container)
                || (message.MediaAlbumId != 0 && _albumIdToSelector.TryGetValue(message.MediaAlbumId, out container)))
            {
                (container.ContentRoot() as MessageSelector)?.UpdateSelection(message.Id);
            }
        }

        private void UpdateCache(MessageViewModel message, ChatHistoryViewItem container, bool recycle)
        {
            if (recycle)
            {
                if (message.MediaAlbumId != 0)
                    _albumIdToSelector.Remove(message.MediaAlbumId);

                if (message.Id != 0)
                    _messageIdToSelector.Remove(message.Id);

                if (message.ReplyTo is MessageReplyToMessage replyToMessage)
                    _messageIdToMessageIds.Remove(replyToMessage.MessageId, message.Id);

                if (message.Content is MessageHeaderMessageTopic && message.TopicId is MessageTopicForum messageTopicForum)
                    _messageTopicToSelectors.Remove(messageTopicForum.ForumTopicId, container);
            }
            else
            {
                if (message.MediaAlbumId != 0)
                    _albumIdToSelector[message.MediaAlbumId] = container;

                if (message.Id != 0)
                    _messageIdToSelector[message.Id] = container;

                // The two multi-value dictionaries below are the only entries that are not
                // idempotent, and on Linux this method runs TWICE for the same container: once when
                // it is prepared (before it is in the tree, so there is no bubble to bind yet) and
                // once from its Loaded. Recycling removes one occurrence, so without the removal
                // here every reply and every topic separator leaves a duplicate behind that keeps a
                // recycled container alive for the rest of the session.
                if (message.ReplyTo is MessageReplyToMessage replyToMessage)
                {
#if LINUX
                    _messageIdToMessageIds.Remove(replyToMessage.MessageId, message.Id);
#endif
                    _messageIdToMessageIds.Add(replyToMessage.MessageId, message.Id);
                }

                if (message.Content is MessageHeaderMessageTopic && message.TopicId is MessageTopicForum messageTopicForum)
                {
#if LINUX
                    _messageTopicToSelectors.Remove(messageTopicForum.ForumTopicId, container);
#endif
                    _messageTopicToSelectors.Add(messageTopicForum.ForumTopicId, container);
                }
            }
        }
    }
}
