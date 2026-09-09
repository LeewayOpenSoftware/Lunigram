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
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: real mouse input, delivered to the app's own window.
    ///
    ///     UNIGRAM_CLICK=&lt;seconds&gt;:&lt;target&gt;[;&lt;seconds&gt;:&lt;target&gt;]
    ///     UNIGRAM_WHEEL=&lt;seconds&gt;:&lt;notches&gt;[:&lt;target&gt;][;…]
    ///
    /// where a target is one of
    /// <list type="bullet">
    /// <item><c>#Name</c> - the centre of the element with that x:Name;</item>
    /// <item><c>Name[3]</c> - the centre of container 3 of the ItemsControl with that x:Name;</item>
    /// <item><c>120,340</c> - a point, in layout pixels;</item>
    /// <item>nothing (wheel only) - the centre of the tallest scrollable list on screen.</item>
    /// </list>
    ///
    /// Positive notches scroll down. This is the wheel that
    /// <see cref="ScrollTest"/> could not do: that one moves the ScrollViewer by hand because
    /// XTEST cannot aim a pointer under XWayland, while this goes in as a button event on the
    /// window and walks the whole PointerWheelChanged path - the handlers of ChatHistoryView
    /// included.
    ///
    /// Layout pixels are turned into the physical ones the X event carries by measuring, not by
    /// assuming: a motion is sent to a known physical point and the point the app reports back
    /// gives the ratio. Uno divides pointer coordinates by XamlRoot.RasterizationScale, which on
    /// this port answers 1 while the window is laid out at the real display scale, so the ratio is
    /// not always the scale the rest of the app believes in.
    /// </summary>
    public static class PointerTest
    {
        private readonly record struct Step(double Seconds, string Target, int Notches, bool Wheel);

        private static double _scale;

        public static void Schedule(Window window)
        {
            var steps = new List<Step>();

            Parse(Environment.GetEnvironmentVariable("UNIGRAM_CLICK"), false, steps);
            Parse(Environment.GetEnvironmentVariable("UNIGRAM_WHEEL"), true, steps);

            if (steps.Count == 0)
            {
                return;
            }

            steps.Sort((x, y) => x.Seconds.CompareTo(y.Seconds));

            _ = RunAsync(window, steps);
        }

        /// <summary>
        /// Clicks on demand, in the same folder <c>UNIGRAM_SHOT_REQUESTS</c> watches: a file named
        /// <c>&lt;name&gt;.click</c> whose contents are one target is clicked and then deleted, so
        /// the request disappearing is what tells the outside that the click has been delivered.
        ///
        /// Same reason the shot requests exist: what has to be pressed only exists after the app
        /// has walked its way to it, and that instant is not known before the app starts. A schedule
        /// decided up front either fires into an empty window or waits far longer than it needs to.
        /// </summary>
        public static void ScheduleRequests(Window window)
        {
            var folder = Environment.GetEnvironmentVariable("UNIGRAM_SHOT_REQUESTS");
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            var busy = false;

            timer.Tick += async (s, args) =>
            {
                if (busy)
                {
                    return;
                }

                string[] requests;

                try
                {
                    requests = System.IO.Directory.GetFiles(folder, "*.click");
                    requests = [.. requests, .. System.IO.Directory.GetFiles(folder, "*.hgoto")];
                    requests = [.. requests, .. System.IO.Directory.GetFiles(folder, "*.type")];
                }
                catch
                {
                    return;
                }

                if (requests.Length == 0)
                {
                    return;
                }

                busy = true;

                try
                {
                    Array.Sort(requests, StringComparer.Ordinal);

                    foreach (var request in requests)
                    {
                        try
                        {
                            var target = System.IO.File.ReadAllText(request).Trim();

                            if (request.EndsWith(".type", StringComparison.Ordinal))
                            {
                                // <name>.type with a line of text: typed key by key into whatever
                                // has the focus, through the same X path UNIGRAM_TYPE_TEST uses.
                                // A named key goes in braces - "{Return}", "{BackSpace}" - because
                                // a search box only searches once Enter is pressed, and there is no
                                // other way to press it from outside.
                                await TypeAsync(window, target);
                            }
                            else if (request.EndsWith(".hgoto", StringComparison.Ordinal))
                            {
                                // <name>.hgoto with a TDLib message id: bring that message of the
                                // open history into view, realized or not.
                                if (long.TryParse(target, out long messageId))
                                {
                                    await HistoryProbe.GotoMessageAsync(messageId);
                                }
                                else
                                {
                                    Logger.Error($"hgoto request: \"{target}\" is not a message id");
                                }
                            }
                            else
                            {
                                await ClickAsync(window, target);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"click request: {request} failed", ex);
                        }

                        try
                        {
                            System.IO.File.Delete(request);
                        }
                        catch
                        {
                        }
                    }
                }
                finally
                {
                    busy = false;
                }
            };

            timer.Start();

            Logger.Info($"click requests: watching {folder} for *.click");
        }

        /// <summary>
        /// A target may be prefixed with <c>right:</c> for button 3. Every context menu in the app
        /// -- a member row, a chat row, a message -- is only reachable that way, and a left click on
        /// the same row does something else entirely (it opens the thing). <see cref="XTestKeyboard.TryClick"/>
        /// already took the button; nothing but the spelling was missing.
        /// </summary>
        /// <summary>
        /// Types <paramref name="text"/> into whatever has the keyboard focus, one real X key at a
        /// time. <c>{Name}</c> is a named key ("{Return}", "{BackSpace}", "{Escape}").
        /// </summary>
        // Which of the two delivery paths this session's compositor actually lets through, decided
        // once by measurement. TypeTest already did this for UNIGRAM_TYPE_TEST and the .type
        // request never did: it typed through XTEST unconditionally, and on this Wayland session
        // XTEST keys do not reach the focused X client -- "15 key(s) delivered, 0 refused" with an
        // empty TextBox afterwards, which reads like the app ignoring the keys and is really the
        // compositor never handing them over.
        private static bool _pathProbed;

        private static async Task ProbePathAsync(Window window)
        {
            if (_pathProbed || window?.Content == null)
            {
                return;
            }

            _pathProbed = true;

            // handledEventsToo: whatever has the focus marks the keys it consumes as handled.
            var arrived = 0;
            window.Content.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s, args) => arrived++), true);

            foreach (var send in new[] { false, true })
            {
                XTestKeyboard.UseSendEvent = send;

                var before = arrived;
                XTestKeyboard.TryKey("End");
                await DelayAsync(400);

                Logger.Info($"type request: probe through {(send ? "XSendEvent" : "XTEST")}: {arrived - before} KeyDown reached the app");

                if (arrived > before)
                {
                    return;
                }
            }

            Logger.Error("type request: NEITHER path delivered a key; typing will do nothing");
        }

        public static async Task TypeAsync(Window window, string text)
        {
            XTestKeyboard.TryActivateOwnWindow();
            await ProbePathAsync(window);

            var typed = 0;
            var failed = 0;

            for (int i = 0; i < text.Length; i++)
            {
                bool sent;

                if (text[i] == '{')
                {
                    var close = text.IndexOf('}', i);

                    if (close < 0)
                    {
                        Logger.Error($"type request: unclosed brace in \"{text}\"");
                        return;
                    }

                    var name = text[(i + 1)..close];

                    // "{Shift+Return}": the composer sends on Enter and makes a line on
                    // Shift+Enter, and those are the same keysym -- only the modifier separates
                    // them, so without a way to hold Shift there is no way to test the second one.
                    var shift = name.StartsWith("Shift+", StringComparison.Ordinal);

                    sent = XTestKeyboard.TryKey(shift ? name["Shift+".Length..] : name, shift);
                    i = close;
                }
                else
                {
                    sent = XTestKeyboard.TryType(text[i]);
                }

                if (sent)
                {
                    typed++;
                }
                else
                {
                    failed++;
                }

                await DelayAsync(40);
            }

            Logger.Info($"type request: {typed} key(s) delivered, {failed} refused");
        }

        public static async Task ClickAsync(Window window, string target)
        {
            var button = 1;
            var holdMs = 0;

            if (target.StartsWith("right:", StringComparison.Ordinal))
            {
                button = 3;
                target = target["right:".Length..];
            }
            else if (target.StartsWith("hold", StringComparison.Ordinal) && target.IndexOf(':') > 0)
            {
                // hold<ms>:<target> - press, wait, release. A tap and a hold are different
                // gestures (the story viewer advances on one and pauses on the other), and a
                // full press+release in one call can only ever be the first of the two.
                var colon = target.IndexOf(':');
                int.TryParse(target[4..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out holdMs);
                target = target[(colon + 1)..];
            }

            var scale = await CalibrateAsync(window);
            var point = Resolve(window, target, false);

            if (point == null)
            {
                Logger.Error($"click request: no target \"{target}\"");
                return;
            }

            var x = (int)Math.Round(point.Value.X * scale);
            var y = (int)Math.Round(point.Value.Y * scale);

            bool sent;

            if (holdMs > 0)
            {
                sent = XTestKeyboard.TryPress(x, y, button);
                Logger.Info($"click request: press-and-hold {holdMs}ms on {target} at window {x},{y} (sent: {sent})");

                await Task.Delay(holdMs);

                sent &= XTestKeyboard.TryRelease(x, y, button);
                Logger.Info($"click request: released after {holdMs}ms (sent: {sent})");
                return;
            }

            sent = XTestKeyboard.TryClick(x, y, button);

            Logger.Info($"click request: {(button == 3 ? "right-click" : "click")} on {target} "
                + $"at layout {point.Value.X:F0},{point.Value.Y:F0} = window {x},{y} (sent: {sent})");
        }

        private static void Parse(string value, bool wheel, List<Step> steps)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':');

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                {
                    Logger.Error($"UNIGRAM_{(wheel ? "WHEEL" : "CLICK")} must start with the seconds to wait: \"{entry}\"");
                    continue;
                }

                if (wheel)
                {
                    if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int notches))
                    {
                        Logger.Error($"UNIGRAM_WHEEL must be <seconds>:<notches>[:<target>]: \"{entry}\"");
                        continue;
                    }

                    steps.Add(new Step(seconds, parts.Length > 2 ? parts[2] : null, notches, true));
                }
                else if (parts.Length > 1)
                {
                    steps.Add(new Step(seconds, parts[1], 0, false));
                }
                else
                {
                    Logger.Error($"UNIGRAM_CLICK must be <seconds>:<target>: \"{entry}\"");
                }
            }
        }

        private static async Task RunAsync(Window window, List<Step> steps)
        {
            var started = Stopwatch.StartNew();

            XTestKeyboard.WindowTitle = window.AppWindow?.Title;

            Logger.Info($"pointer test: target window is {XTestKeyboard.DescribeOwnWindow()} "
                + $"(AppWindow.Title is \"{window.AppWindow?.Title}\")");

            foreach (var step in steps)
            {
                var remaining = (step.Seconds - started.Elapsed.TotalSeconds) * 1000;
                await DelayAsync((int)Math.Max(remaining, 1));

                try
                {
                    await StepAsync(window, step, started);
                }
                catch (Exception ex)
                {
                    Logger.Error("pointer test: threw", ex);
                }
            }
        }

        private static async Task StepAsync(Window window, Step step, Stopwatch started)
        {
            var scale = await CalibrateAsync(window);
            var point = Resolve(window, step.Target, step.Wheel);

            if (point == null)
            {
                Logger.Error($"pointer test: no target \"{step.Target}\"");
                return;
            }

            var x = (int)Math.Round(point.Value.X * scale);
            var y = (int)Math.Round(point.Value.Y * scale);

            var elapsed = started.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

            if (step.Wheel)
            {
                var sent = XTestKeyboard.TryWheel(x, y, step.Notches);
                Logger.Info($"pointer test: wheel {step.Notches:+#;-#;0} over {step.Target ?? "the tallest scrollable"} "
                    + $"at layout {point.Value.X:F0},{point.Value.Y:F0} = window {x},{y} at {elapsed}s (sent: {sent})");
            }
            else
            {
                var sent = XTestKeyboard.TryClick(x, y);
                Logger.Info($"pointer test: click on {step.Target} at layout {point.Value.X:F0},{point.Value.Y:F0} "
                    + $"= window {x},{y} at {elapsed}s (sent: {sent})");
            }
        }

        /// <summary>
        /// How many physical pixels there are to a layout pixel on the pointer path, measured by
        /// sending a motion to a known point of the window and reading back where the app says the
        /// pointer is. Falls back to the display scale when no motion arrives - which is itself
        /// worth knowing, and is logged.
        /// </summary>
        private static async Task<double> CalibrateAsync(Window window)
        {
            if (_scale != 0)
            {
                return _scale;
            }

            const int Probe = 400;

            var reported = new TaskCompletionSource<Point>();

            void moved(object sender, PointerRoutedEventArgs args)
            {
                reported.TrySetResult(args.GetCurrentPoint(null).Position);
            }

            var handler = new PointerEventHandler(moved);
            window.Content.AddHandler(UIElement.PointerMovedEvent, handler, true);

            try
            {
                if (XTestKeyboard.TryMove(Probe, Probe))
                {
                    var arrived = await Task.WhenAny(reported.Task, DelayAsync(1500));

                    if (arrived == reported.Task)
                    {
                        var point = reported.Task.Result;
                        _scale = point.X > 0 ? Probe / point.X : 1;

                        Logger.Info($"pointer test: a motion to window {Probe},{Probe} arrives at layout "
                            + $"{point.X:F1},{point.Y:F1}, so one layout pixel is {_scale:F3} window pixels "
                            + $"(the display scale is {DisplayScale.Current:F3})");

                        return _scale;
                    }
                }

                _scale = DisplayScale.Current;
                Logger.Warning($"pointer test: no PointerMoved arrived, assuming the display scale {_scale:F3}");
            }
            finally
            {
                window.Content.RemoveHandler(UIElement.PointerMovedEvent, handler);
            }

            return _scale;
        }

        /// <summary>
        /// The trees a target may be looked for in, and the offset each one sits at on screen.
        ///
        /// <para>A MenuFlyout, a ContentDialog and the gallery are NOT children of
        /// <c>Window.Content</c>: Uno puts every popup in the popup root, which is a SIBLING of the
        /// content. Resolving against the content alone found no menu entry ever - and the failure
        /// was silent, because a missing target only logs "no target". <see cref="Screenshot"/> and
        /// <see cref="VisualTreeDump"/> already walk the popups; this is the same walk, and it
        /// yields the popups FIRST so that a menu covering a page wins over whatever it covers.</para>
        /// </summary>
        private static IEnumerable<(UIElement Root, Point Origin)> Roots(Window window)
        {
            var popups = new List<(UIElement, Point)>();

            try
            {
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(window.Content.XamlRoot))
                {
                    if (popup.Child is not FrameworkElement child || child.ActualWidth <= 0 || child.ActualHeight <= 0)
                    {
                        continue;
                    }

                    Point origin;

                    try
                    {
                        // In this Uno a popup child IS reachable from the content, and then the
                        // transform already carries the placement. When it is not, the popup's own
                        // offset is the best available answer - same fallback as Screenshot.
                        origin = child.TransformToVisual(window.Content).TransformPoint(new Point());
                    }
                    catch
                    {
                        origin = new Point(popup.HorizontalOffset, popup.VerticalOffset);
                    }

                    popups.Add((child, origin));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"pointer test: popups could not be walked: {ex.Message}");
            }

            // Last opened first: a submenu sits on top of the menu that opened it.
            for (int i = popups.Count - 1; i >= 0; i--)
            {
                yield return popups[i];
            }

            yield return (window.Content, new Point());
        }

        private static Point? Resolve(Window window, string target, bool wheel)
        {
            // A literal point is already in window coordinates, so it must NOT be offset by a
            // popup's origin: resolve it against the content and nothing else.
            if (IsPoint(target))
            {
                return ResolveIn(window.Content, new Point(), target, wheel);
            }

            foreach (var (root, origin) in Roots(window))
            {
                var point = ResolveIn(root, origin, target, wheel);

                if (point != null)
                {
                    return point;
                }
            }

            return null;
        }

        private static Point? ResolveIn(UIElement content, Point origin, string target, bool wheel)
        {
            if (string.IsNullOrEmpty(target))
            {
                return wheel ? CentreOf(FindTallestScrollable(content), content, origin) : null;
            }

            if (target[0] == '#')
            {
                return CentreOf(Find(content, target.Substring(1)), content, origin);
            }

            if (target[0] == '@')
            {
                // @TypeName, or @TypeName#child: by the CLR type of the control rather than by an
                // x:Name. A message bubble's parts are named inside their own template
                // (VoiceNoteContent's play button is just "Button", and so is a photo's), so a name
                // alone names the first bubble on screen of any kind. The type does not.
                var hash = target.IndexOf('#');
                var typeName = hash > 0 ? target[1..hash] : target[1..];

                var host = FindOfType(content, typeName, content, origin);

                if (host == null)
                {
                    return null;
                }

                return CentreOf(hash > 0 ? Find(host, target[(hash + 1)..]) : host, content, origin);
            }

            var bracket = target.IndexOf('[');

            if (bracket > 0 && target.EndsWith(']')
                && int.TryParse(target.AsSpan(bracket + 1, target.Length - bracket - 2), out int index))
            {
                if (Find(content, target.Substring(0, bracket)) is not ItemsControl list)
                {
                    return null;
                }

                return CentreOf(list.ContainerFromIndex(index) as FrameworkElement, content, origin);
            }

            var brace = target.IndexOf('{');

            if (brace == 0 && target.EndsWith('}'))
            {
                // {text} with no list in front: the first thing on screen that says it. Settings
                // rows and menu entries have no x:Name of their own, and their position moves with
                // whatever is above them, so what they say is the only stable way to name them.
                return CentreOf(FindSaying(content, target[1..^1]), content, origin);
            }

            if (brace > 0 && target.EndsWith('}'))
            {
                if (Find(content, target.Substring(0, brace)) is not ItemsControl list)
                {
                    return null;
                }

                // By what the row says rather than by its index: a chat list reorders itself as
                // messages arrive, so an index does not name the same chat twice.
                var text = target.Substring(brace + 1, target.Length - brace - 2);

                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (list.ContainerFromIndex(i) is FrameworkElement container && Says(container, text))
                    {
                        return CentreOf(container, content, origin);
                    }
                }

                return null;
            }

            var comma = target.IndexOf(',');

            if (comma > 0
                && double.TryParse(target.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(target.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                return new Point(x, y);
            }

            return null;
        }

        /// <summary>
        /// The centre of <paramref name="element"/> in the coordinates of the window content.
        /// <paramref name="content"/> is the root of the tree the element lives in - the window
        /// content, or a popup's child - and <paramref name="origin"/> is where that tree sits on
        /// screen. Transforming to <c>XamlRoot.Content</c> instead is what the old code did, and it
        /// throws for an element inside a popup that is not reachable from the content.
        /// </summary>
        private static bool IsPoint(string target)
        {
            var comma = target == null ? -1 : target.IndexOf(',');

            return comma > 0
                && double.TryParse(target.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                && double.TryParse(target.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        private static Point? CentreOf(FrameworkElement element, UIElement content, Point origin)
        {
            if (element == null)
            {
                return null;
            }

            // A target that is collapsed, or that has been arranged away to nothing, still has a
            // position, and the centre of nothing is a real point belonging to whatever is drawn
            // underneath it. That is not a near miss: ChatHistoryArrows sits directly over the
            // composer, so once the history reaches the bottom and the arrows go away, the centre
            // of the control lands on the microphone button and a click that asked to scroll
            // starts recording audio instead. Measured on 2026-09-06, twice, on a real account -
            // the log reads "click on @ChatHistoryArrows at layout 1844,992", and 1844,992 is the
            // microphone. An earlier session sent a sticker into somebody's chat and could never
            // account for it; this is very likely how.
            // Refusing costs a request that does nothing and says so. Clicking costs whatever the
            // control underneath does, to somebody's real account, and there is no undo for that.
            if (!IsHittable(element, out var reason))
            {
                Logger.Warning($"pointer test: refusing to click {element.GetType().Name} - {reason}. " +
                    "The centre of a target nobody can see belongs to whatever is underneath it.");
                return null;
            }

            try
            {
                var transform = element.TransformToVisual(content);
                var centre = transform.TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2));

                return new Point(centre.X + origin.X, centre.Y + origin.Y);
            }
            catch (Exception ex)
            {
                Logger.Warning($"pointer test: could not place an element: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Whether a pointer that lands on <paramref name="element"/> would actually reach it.
        /// </summary>
        /// <remarks>
        /// Deliberately only the three things that make a target a lie about where it is - no size,
        /// collapsed, or invisible - and each is checked up the whole parent chain, because a child
        /// with a perfectly good rect inside a collapsed panel is exactly the case that bit us.
        /// Opacity is read from the property rather than the composition visual: an element mid
        /// fade is still on its way in and is legitimately clickable, while one parked at zero is
        /// what a hidden panel looks like on this port.
        /// </remarks>
        private static bool IsHittable(FrameworkElement element, out string reason)
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                reason = $"it measures {element.ActualWidth:F0}x{element.ActualHeight:F0}";
                return false;
            }

            for (DependencyObject node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is not UIElement ancestor)
                {
                    continue;
                }

                if (ancestor.Visibility == Visibility.Collapsed)
                {
                    reason = ReferenceEquals(ancestor, element)
                        ? "it is collapsed"
                        : $"{ancestor.GetType().Name} above it is collapsed";
                    return false;
                }

                if (ancestor.Opacity <= 0)
                {
                    reason = ReferenceEquals(ancestor, element)
                        ? "it is fully transparent"
                        : $"{ancestor.GetType().Name} above it is fully transparent";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        private static bool Says(DependencyObject container, string text)
        {
            foreach (var element in Descendants(container))
            {
                if (element is TextBlock block && block.Text != null
                    && block.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The first laid out, visible TextBlock that says <paramref name="text"/>. Collapsed and
        /// zero sized ones are skipped: a settings page keeps the rows it does not show, and the
        /// centre of a zero sized element is a point nothing can be clicked at.
        /// </summary>
        private static FrameworkElement FindSaying(UIElement root, string text)
        {
            foreach (var element in Descendants(root))
            {
                if (element is TextBlock block
                    && block.Visibility == Visibility.Visible
                    && block.ActualWidth > 0 && block.ActualHeight > 0
                    && block.Text != null
                    && block.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    return block;
                }
            }

            return null;
        }

        private static FrameworkElement Find(UIElement root, string name)
        {
            foreach (var element in Descendants(root))
            {
                if (element is FrameworkElement fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
                {
                    return fe;
                }
            }

            return null;
        }

        private static FrameworkElement FindOfType(UIElement root, string typeName, UIElement content, Point origin)
        {
            FrameworkElement offscreen = null;

            foreach (var element in Descendants(root))
            {
                if (element is not FrameworkElement fe
                    || !string.Equals(fe.GetType().Name, typeName, StringComparison.Ordinal))
                {
                    continue;
                }

                // On screen wins. A virtualized list keeps containers above and below the viewport
                // realized, so the first one in tree order is regularly one nobody can click - and
                // a click at a negative coordinate goes to whatever window is up there instead.
                var centre = CentreOf(fe, content, origin);

                if (centre is Point point
                    && point.X >= 0 && point.Y >= 0
                    && point.X <= fe.XamlRoot.Size.Width && point.Y <= fe.XamlRoot.Size.Height)
                {
                    return fe;
                }

                offscreen ??= fe;
            }

            return offscreen;
        }

        private static ScrollViewer FindTallestScrollable(UIElement root)
        {
            ScrollViewer best = null;

            foreach (var element in Descendants(root))
            {
                if (element is ScrollViewer scrollViewer
                    && scrollViewer.ScrollableHeight > 0
                    && (best == null || scrollViewer.ActualHeight > best.ActualHeight))
                {
                    best = scrollViewer;
                }
            }

            return best;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var element = pending.Pop();

                if (element is FrameworkElement fe && fe.Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                yield return element;

                var count = VisualTreeHelper.GetChildrenCount(element);

                for (int i = 0; i < count; i++)
                {
                    pending.Push(VisualTreeHelper.GetChild(element, i));
                }
            }
        }

        internal static Task DelayAsync(int milliseconds)
        {
            var tsc = new TaskCompletionSource<Point>();
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds)
            };

            void tick(object sender, object e)
            {
                timer.Tick -= tick;
                timer.Stop();
                tsc.TrySetResult(default);
            }

            timer.Tick += tick;
            timer.Start();

            return tsc.Task;
        }
    }
}
