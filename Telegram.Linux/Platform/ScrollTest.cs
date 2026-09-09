//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: with UNIGRAM_SCROLL_TEST=&lt;seconds&gt;:&lt;pixels&gt; the
    /// tallest scrollable list in the window is scrolled by that many pixels at that instant.
    /// Several instants are comma separated and the pixel amount may be negative:
    ///
    ///     UNIGRAM_SCROLL_TEST=18:600,24:-600
    ///
    /// It exists because the mouse wheel cannot be injected from outside under XWayland: mutter
    /// keeps its guard window over the whole X screen, so xdotool's XTEST pointer events are
    /// reported over the root window and never reach the Uno surface (`xdotool getmouselocation`
    /// answers WINDOW=0x408 wherever the pointer is put). Driving the ScrollViewer directly still
    /// exercises what the port needs proven - virtualization, container recycling and the
    /// rebinding of a recycled cell - it just does not exercise the wheel plumbing itself.
    ///
    /// The target is picked by size rather than by name so the same variable works for the chat
    /// list and for the message history.
    /// </summary>
    public static class ScrollTest
    {
        private readonly record struct Step(double Seconds, double Pixels);

        public static void Schedule(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_SCROLL_TEST");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var steps = new List<Step>();

            foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':');
                if (parts.Length != 2
                    || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels))
                {
                    Logger.Error("UNIGRAM_SCROLL_TEST must be <seconds>:<pixels>, comma separated for more than one instant");
                    return;
                }

                steps.Add(new Step(seconds, pixels));
            }

            steps.Sort((x, y) => x.Seconds.CompareTo(y.Seconds));

            var started = Stopwatch.StartNew();
            var timer = new DispatcherTimer();
            var index = 0;

            void ScheduleNext()
            {
                if (index >= steps.Count)
                {
                    return;
                }

                var remaining = (steps[index].Seconds - started.Elapsed.TotalSeconds) * 1000;
                timer.Interval = TimeSpan.FromMilliseconds(Math.Max(remaining, 1));
                timer.Start();
            }

            timer.Tick += (s, args) =>
            {
                timer.Stop();

                var step = steps[index++];

                try
                {
                    var scrollViewer = FindTallestScrollable(window.Content);
                    if (scrollViewer == null)
                    {
                        Logger.Warning("scroll test: no scrollable ScrollViewer in the window");
                    }
                    else
                    {
                        var target = Math.Clamp(scrollViewer.VerticalOffset + step.Pixels, 0, scrollViewer.ScrollableHeight);
                        var changed = scrollViewer.ChangeView(null, target, null, true);

                        Logger.Info($"scroll test: {step.Pixels:F0}px at {started.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)}s, "
                            + $"offset {scrollViewer.VerticalOffset:F0} -> {target:F0} of {scrollViewer.ScrollableHeight:F0} (accepted: {changed})");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }

                ScheduleNext();
            };

            ScheduleNext();
        }

        private static ScrollViewer FindTallestScrollable(UIElement root)
        {
            ScrollViewer best = null;

            void Walk(DependencyObject element, int depth)
            {
                if (depth > 40)
                {
                    return;
                }

                if (element is ScrollViewer scrollViewer
                    && scrollViewer.ScrollableHeight > 0
                    && (best == null || scrollViewer.ActualHeight > best.ActualHeight))
                {
                    best = scrollViewer;
                }

                var count = VisualTreeHelper.GetChildrenCount(element);

                for (int i = 0; i < count; i++)
                {
                    Walk(VisualTreeHelper.GetChild(element, i), depth + 1);
                }
            }

            if (root != null)
            {
                Walk(root, 0);
            }

            return best;
        }
    }
}
