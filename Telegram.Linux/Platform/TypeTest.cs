//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: with UNIGRAM_TYPE_TEST=&lt;sequence&gt; the text box of the page
    /// on screen is typed into, one real key at a time, and <c>Text</c> and <c>SelectionStart</c>
    /// are logged after every single key - which is the only way to tell a formatting bug from a
    /// caret bug in a field that rewrites itself as you type.
    ///
    /// A leading <c>&lt;seconds&gt;:</c> says how long to wait for the page to settle first
    /// (6 by default):
    ///
    ///     UNIGRAM_TYPE_TEST=+541155512345
    ///     UNIGRAM_TYPE_TEST=8:+541155512345
    ///
    /// After typing the sequence it deletes three digits, types them back, moves the caret six
    /// characters to the left, inserts a digit there and takes it out again - so the log shows,
    /// on the same run, that deleting removes one digit per key, that the number survives the
    /// round trip, and that an insertion in the middle leaves the rest of it where it was.
    ///
    /// UNIGRAM_TYPE_TEST_CLICK=&lt;button&gt; presses a button first, matched by x:Name or by the
    /// text it shows. The login page opens on the QR code, so reaching the phone box needs
    /// UNIGRAM_TYPE_TEST_CLICK="Log in with phone number".
    ///
    /// The keys are real X11 key events (see <see cref="XTestKeyboard"/>), not <c>Text</c>
    /// assignments: assigning <c>Text</c> would skip the entire input path under test. There is no
    /// xdotool on this machine, which is why the injector is in-tree.
    /// </summary>
    public static class TypeTest
    {
        private const int KeyIntervalMs = 140;

        public static void Schedule(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_TYPE_TEST");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var seconds = 6d;
            var sequence = value;
            var colon = value.IndexOf(':');

            if (colon > 0 && double.TryParse(value.Substring(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                seconds = parsed;
                sequence = value.Substring(colon + 1);
            }

            if (sequence.Length == 0)
            {
                Logger.Error("UNIGRAM_TYPE_TEST must be [<seconds>:]<sequence to type>");
                return;
            }

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds)
            };

            timer.Tick += (s, args) =>
            {
                timer.Stop();
                _ = RunAsync(window, sequence);
            };

            timer.Start();
        }

        private static async Task RunAsync(Window window, string sequence)
        {
            try
            {
                await TypeAsync(window, sequence);
            }
            catch (Exception ex)
            {
                Logger.Error("type-test: threw", ex);
            }
        }

        private static async Task TypeAsync(Window window, string sequence)
        {
            if (!XTestKeyboard.IsAvailable)
            {
                Logger.Error($"type-test: no key injection available ({XTestKeyboard.Diagnostics})");
                return;
            }

            var click = Environment.GetEnvironmentVariable("UNIGRAM_TYPE_TEST_CLICK");

            if (!string.IsNullOrEmpty(click))
            {
                Logger.Info($"type-test: pressing \"{click}\" first");
                Press(window.Content, click);

                // Long enough for the login page's cross-fade between the two panels to finish.
                await DelayAsync(2500);
            }

            var box = Find(window.Content);
            if (box == null)
            {
                Logger.Error("type-test: no TextBox on the page currently on screen");
                return;
            }

            XTestKeyboard.WindowTitle = window.AppWindow?.Title;

            Logger.Info($"type-test: {XTestKeyboard.Diagnostics}, target {box.GetType().Name} #{box.Name} in window \"{XTestKeyboard.WindowTitle}\"");
            Logger.Info($"type-test: {XTestKeyboard.DescribeActive()}");

            // The injected keys go wherever the keyboard focus is, and the terminal that launched
            // the app is a strong candidate, so ask for the window first.
            XTestKeyboard.TryActivateOwnWindow();
            await DelayAsync(500);
            Logger.Info($"type-test: X keyboard focus is {XTestKeyboard.DescribeFocus()}");

            var focused = box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
            await DelayAsync(300);

            Logger.Info($"type-test: Focus() returned {focused}, XAML focus is on {Describe(FocusManager.GetFocusedElement(box.XamlRoot))}");

            // Counts the key events the app really receives, so a key that changes nothing can be
            // told apart from a key that never arrived. handledEventsToo, because the TextBox
            // marks the ones it consumes as handled.
            var arrived = 0;
            window.Content.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s, args) => arrived++), true);

            // XTEST is the faithful way in - the key walks through the compositor exactly as a real
            // one would - but on this Wayland session whether its keys reach the focused X client
            // is the compositor's business, and often they do not. A harmless End probes it, and
            // the run falls back to delivering the events straight to the window.
            foreach (var send in new[] { false, true })
            {
                XTestKeyboard.UseSendEvent = send;

                var before = arrived;
                XTestKeyboard.TryKey("End");
                await DelayAsync(400);

                Logger.Info($"type-test: probe key through {(send ? "XSendEvent" : "XTEST")}: {arrived - before} KeyDown reached the app");

                if (arrived > before)
                {
                    break;
                }
            }

            var step = 0;
            var counted = arrived;

            async Task Key(string label, bool sent)
            {
                await DelayAsync(KeyIntervalMs);

                var seen = arrived - counted;
                counted = arrived;

                Logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "type-test: [{0,2}] {1,-11} -> Text=\"{2}\" (len {3,2}) SelectionStart={4}{5}{6}",
                    ++step, label, box.Text, box.Text.Length, box.SelectionStart,
                    sent ? string.Empty : "   [KEY NOT SENT]",
                    seen == 1 ? string.Empty : $"   [{seen} KeyDown reached the app]"));
            }

            Logger.Info($"type-test: --- typing \"{sequence}\", one key at a time ---");

            foreach (var character in sequence)
            {
                await Key($"'{character}'", XTestKeyboard.TryType(character));
            }

            var typed = box.Text;

            var undo = Math.Min(3, sequence.Length);

            Logger.Info($"type-test: --- {undo} backspaces: each one must take exactly one digit ---");

            for (int i = 0; i < undo; i++)
            {
                await Key("BackSpace", XTestKeyboard.TryKey("BackSpace"));
            }

            var deleted = box.Text;

            Logger.Info("type-test: --- typing those digits back ---");

            foreach (var character in sequence.Substring(sequence.Length - undo))
            {
                await Key($"'{character}'", XTestKeyboard.TryType(character));
            }

            var restored = box.Text;

            Logger.Info("type-test: --- caret six to the left, then a '7' inserted right there ---");

            for (int i = 0; i < 6; i++)
            {
                await Key("Left", XTestKeyboard.TryKey("Left"));
            }

            var caretBefore = box.SelectionStart;

            await Key("'7'", XTestKeyboard.TryType('7'));

            var inserted = box.Text;
            var caretAfter = box.SelectionStart;

            Logger.Info("type-test: --- taking the '7' back out ---");

            await Key("BackSpace", XTestKeyboard.TryKey("BackSpace"));

            var undone = box.Text;

            await Key("End", XTestKeyboard.TryKey("End"));

            XTestKeyboard.ReleaseSpare();

            Logger.Info("type-test: ============================ verdict ============================");
            Logger.Info($"type-test: typed          \"{typed}\"");
            Logger.Info($"type-test: minus {undo,2} digits \"{deleted}\"");
            Logger.Info($"type-test: typed back     \"{restored}\"   {Verdict(restored == typed, "same as before the deletions", "NOT what it was before the deletions")}");
            Logger.Info($"type-test: '7' inserted   \"{inserted}\"   caret {caretBefore} -> {caretAfter} {Verdict(caretAfter == caretBefore + 1, "moved one place", "did NOT move one place")}");
            Logger.Info($"type-test: '7' removed    \"{undone}\"   {Verdict(undone == typed, "the insertion left no trace", "the insertion MOVED something")}");
            Logger.Info("type-test: =================================================================");
        }

        private static string Verdict(bool ok, string good, string bad)
        {
            return ok ? "OK, " + good : "WRONG, " + bad;
        }

        private static string Describe(object element)
        {
            return element switch
            {
                FrameworkElement fe => string.IsNullOrEmpty(fe.Name) ? fe.GetType().Name : $"{fe.GetType().Name} #{fe.Name}",
                null => "(nothing)",
                _ => element.GetType().Name
            };
        }

        /// <summary>
        /// Presses the button whose x:Name or displayed text is <paramref name="what"/>, through
        /// its automation peer - the same entry point a screen reader uses, so the Click handler
        /// and the command both run exactly as they would from a real press.
        /// </summary>
        private static void Press(UIElement root, string what)
        {
            var buttons = new List<(ButtonBase Button, string Label)>();

            foreach (var element in Descendants(root))
            {
                if (element is ButtonBase button)
                {
                    buttons.Add((button, button.Content switch
                    {
                        string text => text,
                        TextBlock block => block.Text,
                        object other => other.ToString(),
                        _ => null
                    }));
                }
            }

            bool Exact((ButtonBase Button, string Label) x)
            {
                return string.Equals(x.Button.Name, what, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Label, what, StringComparison.OrdinalIgnoreCase);
            }

            // A substring is enough as a second try: the labels come from the translation in use,
            // so an English name typed into the variable has to find a Spanish button as well.
            bool Loose((ButtonBase Button, string Label) x)
            {
                return x.Label != null && x.Label.Contains(what, StringComparison.OrdinalIgnoreCase);
            }

            var match = buttons.Find(Exact);

            if (match.Button == null)
            {
                match = buttons.Find(Loose);
            }

            if (match.Button == null)
            {
                Logger.Error($"type-test: no button named or labelled \"{what}\"; the page has "
                    + string.Join(", ", buttons.ConvertAll(x => $"{x.Button.GetType().Name} #{x.Button.Name} \"{x.Label}\"")));
                return;
            }

            if (FrameworkElementAutomationPeer.CreatePeerForElement(match.Button) is IInvokeProvider provider)
            {
                provider.Invoke();
                return;
            }

            Logger.Error($"type-test: \"{what}\" has no invokable automation peer");
        }

        /// <summary>
        /// The text box to drive: the phone box if the page has one, otherwise the first plain
        /// TextBox. A PasswordBox is reported but not driven - it exposes no caret to check.
        /// </summary>
        private static TextBox Find(UIElement root)
        {
            if (root == null)
            {
                return null;
            }

            var boxes = new List<TextBox>();
            var passwords = 0;

            foreach (var element in Descendants(root))
            {
                if (element is TextBox box)
                {
                    boxes.Add(box);
                }
                else if (element is PasswordBox)
                {
                    passwords++;
                }
            }

            if (passwords > 0)
            {
                Logger.Info($"type-test: the page also has {passwords} PasswordBox, which has no caret to inspect");
            }

            var phone = boxes.Find(x => x is Controls.PhoneTextBox);
            return phone ?? (boxes.Count > 0 ? boxes[0] : null);
        }

        /// <summary>
        /// Every element below <paramref name="root"/>, skipping collapsed subtrees: a control that
        /// is not on screen cannot take the focus, so it is not a candidate for anything here.
        /// </summary>
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

        private static Task DelayAsync(int milliseconds)
        {
            var tsc = new TaskCompletionSource<bool>();
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds)
            };

            void tick(object sender, object e)
            {
                timer.Tick -= tick;
                timer.Stop();
                tsc.TrySetResult(true);
            }

            timer.Tick += tick;
            timer.Start();

            return tsc.Task;
        }
    }
}
