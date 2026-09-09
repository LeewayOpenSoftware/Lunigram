//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Text;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: with UNIGRAM_DUMP_TREE=&lt;seconds&gt; the window's visual tree is
    /// written to the log that many seconds after the first content loads (type, name, bounds in
    /// logical pixels, visibility, opacity and solid backgrounds), so blank or black areas can be
    /// traced to the element that paints them without a designer attached.
    /// </summary>
    public static class VisualTreeDump
    {
        public static void Schedule(Window window)
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("UNIGRAM_DUMP_TREE"), out int seconds) || seconds < 0)
            {
                return;
            }

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };

            timer.Tick += (s, args) =>
            {
                timer.Stop();
                Dump(window.Content);
            };

            timer.Start();
        }

        public static void Dump(UIElement root)
        {
            if (root == null)
            {
                Logger.Info("(no content)");
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine();
            Dump(root, root, 0, builder);

            // Popups are a SIBLING of Window.Content in Uno's tree, not a child: the gallery, the
            // flyouts and every ContentPopup are invisible to a dump of the content alone. Each open
            // one is appended with its own coordinates.
            try
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
                {
                    if (popup.Child is not UIElement child)
                    {
                        continue;
                    }

                    builder.AppendLine();
                    builder.AppendLine($"--- open popup at {popup.HorizontalOffset:F0},{popup.VerticalOffset:F0} ---");
                    Dump(child, child, 0, builder);
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine($"(popups could not be walked: {ex.Message})");
            }

            Logger.Info(builder.ToString());
        }

        private static void Dump(UIElement root, DependencyObject element, int depth, StringBuilder builder)
        {
            builder.Append(' ', depth * 2);
            builder.Append(element.GetType().Name);

            if (element is FrameworkElement fe)
            {
                if (!string.IsNullOrEmpty(fe.Name))
                {
                    builder.Append(" #").Append(fe.Name);
                }

                try
                {
                    var point = fe.TransformToVisual(root).TransformPoint(new Point());
                    builder.Append($" [{point.X:F0},{point.Y:F0} {fe.ActualWidth:F0}x{fe.ActualHeight:F0}]");
                }
                catch
                {
                    builder.Append($" [? {fe.ActualWidth:F0}x{fe.ActualHeight:F0}]");
                }

                if (fe.Visibility == Visibility.Collapsed)
                {
                    builder.Append(" collapsed");
                }

                if (fe.Opacity < 1)
                {
                    builder.Append($" opacity={fe.Opacity:F2}");
                }

                // The XAML Opacity above is NOT what the composition animations of the port drive:
                // ShowHideDateHeader and friends animate Visual.Opacity, which is a different
                // property and stays invisible to a dump that only reads the FrameworkElement. An
                // element whose box is right and whose pixels are missing is almost always one of
                // these, so they are printed with a "c" prefix whenever they are not the default.
                try
                {
                    var visual = ElementComposition.GetElementVisual(fe);
                    if (visual != null)
                    {
                        if (visual.Opacity < 1)
                        {
                            builder.Append($" c-opacity={visual.Opacity:F2}");
                        }

                        if (visual.Offset.X != 0 || visual.Offset.Y != 0)
                        {
                            builder.Append($" c-offset={visual.Offset.X:F0},{visual.Offset.Y:F0}");
                        }

                        if (visual.Scale.X != 1 || visual.Scale.Y != 1)
                        {
                            builder.Append($" c-scale={visual.Scale.X:F2},{visual.Scale.Y:F2}");
                        }

                        if (visual.Clip != null)
                        {
                            builder.Append(" c-clip=").Append(visual.Clip.GetType().Name);

                            if (visual.Clip is InsetClip inset)
                            {
                                builder.Append($"({inset.LeftInset:F0},{inset.TopInset:F0},{inset.RightInset:F0},{inset.BottomInset:F0}) c-size={visual.Size.X:F0}x{visual.Size.Y:F0}");
                            }
                        }
                    }
                }
                catch
                {
                    // A visual is not guaranteed for every element; the dump must never throw.
                }

                var background = element switch
                {
                    Panel panel => panel.Background,
                    Border border => border.Background,
                    Control control => control.Background,
                    _ => null
                };

                if (background is SolidColorBrush solid && solid.Color.A > 0)
                {
                    builder.Append($" bg=#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}");
                }
                else if (background != null)
                {
                    builder.Append(" bg=").Append(background.GetType().Name);
                }

                if (element is TextBlock text)
                {
                    builder.Append(" \"").Append(text.Text?.Length > 40 ? text.Text[..40] + "…" : text.Text).Append('"');
                }
                else if (element is TextBox box)
                {
                    // What the composer is holding right now, with the line breaks made visible.
                    // Enter sends and Shift+Enter makes a line, and those two are the same keysym:
                    // a screenshot cannot tell "two lines" from "one line that wrapped", and the
                    // escaped \n can. Same 40-character cap as above.
                    var value = box.Text?.Replace("\r", "\\r").Replace("\n", "\\n") ?? string.Empty;
                    builder.Append(" text=\"").Append(value.Length > 40 ? value[..40] + "…" : value)
                        .Append("\" caret=").Append(box.SelectionStart);
                }
            }

            builder.AppendLine();

            var count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
            {
                Dump(root, VisualTreeHelper.GetChild(element, i), depth + 1, builder);
            }
        }
    }
}
