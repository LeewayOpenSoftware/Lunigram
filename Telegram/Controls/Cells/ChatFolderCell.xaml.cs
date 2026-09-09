//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Numerics;
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;

namespace Telegram.Controls.Cells
{
    public sealed partial class ChatFolderCell : UserControl
    {
        public ChatFolderViewModel ViewModel => DataContext as ChatFolderViewModel;

        public ChatFolderCell()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            ElementCompositionPreview.SetIsTranslationEnabled(UnselectedIcon, true);
            ElementCompositionPreview.SetIsTranslationEnabled(SelectedIcon, true);

            var iconUnselected = ElementComposition.GetElementVisual(UnselectedIcon);
            var iconSelected = ElementComposition.GetElementVisual(SelectedIcon);

            iconUnselected.Opacity = 1;
            iconSelected.Opacity = 0;
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            Bindings.Update();
        }

        private void OnCurrentStateChanged(object sender, VisualStateChangedEventArgs e)
        {
            var prev = e.OldState?.Name;
            var next = e.NewState?.Name;

            var compositor = BootStrapper.Current.Compositor;

            var iconUnselected = ElementComposition.GetElementVisual(UnselectedIcon);
            var iconSelected = ElementComposition.GetElementVisual(SelectedIcon);

            var title = ElementComposition.GetElementVisual(Title);

            if (next == "PointerOver")
            {
                title.StopAnimation("Opacity");
                title.Opacity = 1;

                iconUnselected.StopAnimation("Opacity");
                iconUnselected.Opacity = 1;

                iconSelected.StopAnimation("Translation");
                iconSelected.StopAnimation("Opacity");
                iconSelected.Opacity = 0;
            }
            else if (next == "Pressed")
            {
                // One animation instance per target, never one shared by two. Uno registers a
                // running animation in a dictionary KEYED BY THE ANIMATION OBJECT
                // (Compositor.RegisterAnimation does Dictionary.Add, not the indexer), so starting
                // the same instance on a second visual throws ArgumentException "An item with the
                // same key has already been added" - and that throw comes out of
                // OnCurrentStateChanged, which runs inside SelectorItem.OnPointerPressed: the
                // whole PointerPressed (and then PointerReleased) is abandoned for every press on
                // a folder tab. WinUI allows the sharing; this costs nothing there.
                Vector3KeyFrameAnimation Offset()
                {
                    var offset = compositor.CreateVector3KeyFrameAnimation();
                    offset.InsertKeyFrame(0, new Vector3());
                    offset.InsertKeyFrame(1, new Vector3(0, -3, 0));
                    return offset;
                }

                iconUnselected.StartAnimation("Translation", Offset());
                iconSelected.StartAnimation("Translation", Offset());

                title.StopAnimation("Opacity");
                title.Opacity = 0.8f;

                iconUnselected.StopAnimation("Opacity");
                iconUnselected.Opacity = 0.6f;
            }
            else if (next == "Selected" && prev == null)
            {
                title.StopAnimation("Opacity");
                title.Opacity = 0;

                iconUnselected.Opacity = 0;
                iconUnselected.Properties.InsertVector3("Translation", new Vector3(0, 7, 0));

                iconSelected.Opacity = 1;
                iconSelected.Properties.InsertVector3("Translation", new Vector3(0, 7, 0));
            }
            else if (next.Contains("Selected") && prev != null && !prev.Contains("Selected"))
            {
                // See the note in "Pressed": one instance per target.
                CompositionAnimation Spring()
                {
#if LINUX
                    // Compositor.CreateSpringVector3Animation is [NotImplemented] in Uno and
                    // THROWS: the whole PointerReleased on a folder tab was being abandoned right
                    // here, every single time a folder was selected. Key frames with a back-out
                    // ease instead, which is the same overshoot-and-settle the spring draws
                    // (DampingRatio 0.5 from -3 to 7).
                    var settle = compositor.CreateVector3KeyFrameAnimation();
                    settle.InsertKeyFrame(0, new Vector3(0, -3, 0));
                    settle.InsertKeyFrame(0.6f, new Vector3(0, 9, 0));
                    settle.InsertKeyFrame(1, new Vector3(0, 7, 0));
                    settle.Duration = Constants.FastAnimation;

                    return settle;
#else
                    var spring = compositor.CreateSpringVector3Animation();
                    spring.InitialValue = new Vector3(0, -3, 0);
                    spring.FinalValue = new Vector3(0, 7, 0);
                    spring.DampingRatio = 0.5f;

                    return spring;
#endif
                }

                ScalarKeyFrameAnimation Fade(float from, float to)
                {
                    var fade = compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0, from);
                    fade.InsertKeyFrame(1, to);
                    return fade;
                }

                iconUnselected.StartAnimation("Opacity", Fade(1, 0));
                iconUnselected.StartAnimation("Translation", Spring());

                iconSelected.StartAnimation("Opacity", Fade(0, 1));
                iconSelected.StartAnimation("Translation", Spring());

                var titleFadeOut = Fade(1, 0);
                titleFadeOut.Duration = Constants.FastAnimation;
                title.StartAnimation("Opacity", titleFadeOut);
            }
            else if (next == "Normal" && prev == "Selected")
            {
                // See the note in "Pressed": one instance per target.
                Vector3KeyFrameAnimation Offset()
                {
                    var offset = compositor.CreateVector3KeyFrameAnimation();
                    offset.InsertKeyFrame(0, new Vector3(0, 7, 0));
                    offset.InsertKeyFrame(1, new Vector3());
                    return offset;
                }

                ScalarKeyFrameAnimation Fade(float from, float to)
                {
                    var fade = compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0, from);
                    fade.InsertKeyFrame(1, to);
                    return fade;
                }

                iconUnselected.StartAnimation("Opacity", Fade(0, 0.6f));
                iconUnselected.StartAnimation("Translation", Offset());

                iconSelected.StartAnimation("Opacity", Fade(1, 0));
                iconSelected.StartAnimation("Translation", Offset());

                var titleFadeIn = Fade(0, 0.8f);
                titleFadeIn.Duration = Constants.FastAnimation;
                title.StartAnimation("Opacity", titleFadeIn);
            }
            else if (next == "Normal")
            {
                title.StopAnimation("Opacity");
                title.Opacity = 0.8f;

                iconUnselected.StopAnimation("Opacity");
                iconUnselected.Opacity = 0.6f;
            }
        }

        #region Title

        public static ChatFolderName GetTitle(DependencyObject obj)
        {
            return (ChatFolderName)obj.GetValue(TitleProperty);
        }

        public static void SetTitle(DependencyObject obj, ChatFolderName value)
        {
            obj.SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.RegisterAttached("Title", typeof(ChatFolderName), typeof(RichTextBlock), new PropertyMetadata(null, OnTitleChanged));

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
#if LINUX
            // The folder bars carry a TextBlock here, not a RichTextBlock: Uno marks RichTextBlock
            // [NotImplemented] for __SKIA__, so the names never appeared. Same inlines, same
            // CustomEmojiIcon.Add (its LINUX branch already writes plain Runs into an
            // InlineCollection), one level less of nesting because a TextBlock has no Blocks.
            var textBlock = d as TextBlock;
            var inlines = textBlock?.Inlines;
            var formattedText = e.NewValue as ChatFolderName;

            var clientService = textBlock?.DataContext switch
            {
                ChatFolderViewModel chatFolder => chatFolder.ClientService,
                ViewModelBase viewModel => viewModel.ClientService,
                _ => null
            };

            if (clientService == null || formattedText == null || inlines == null)
            {
                return;
            }

            var size = textBlock.FontSize < 14
                ? 14
                : 20;

            CustomEmojiIcon.Add(null, inlines, clientService, formattedText, size: size);
#else
            var textBlock = d as RichTextBlock;
            var paragraph = textBlock?.Blocks[0] as Paragraph;
            var formattedText = e.NewValue as ChatFolderName;

            var clientService = textBlock?.DataContext switch
            {
                ChatFolderViewModel chatFolder => chatFolder.ClientService,
                ViewModelBase viewModel => viewModel.ClientService,
                _ => null
            };

            if (clientService == null || formattedText == null)
            {
                return;
            }

            var size = textBlock.FontSize < 14
                ? 14
                : 20;

            CustomEmojiIcon.Add(textBlock, paragraph.Inlines, clientService, formattedText, size: size);
#endif
        }

        #endregion
    }
}
