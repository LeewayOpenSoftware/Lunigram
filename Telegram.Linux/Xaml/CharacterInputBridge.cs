//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Reflection;
using Telegram.Common;
using Microsoft.UI.Xaml.Input;

namespace Telegram.Views
{
    /// <summary>
    /// Stands in for the <c>CoreWindow.CharacterReceived</c> that Uno does not have: reads the
    /// character Uno decoded for a key press, and runs the one decision every caller was making
    /// around it.
    ///
    /// <para>Uno has no <c>CharacterReceived</c> to subscribe to. <c>Window.CoreWindow</c> is null,
    /// and the event is not simply missing from that one object: <c>IUnoKeyboardInputSource</c> --
    /// the contract every Skia host implements, X11 included -- declares KeyDown and KeyUp and
    /// nothing else, so no layer above it has a character event to raise. Uno does decode the
    /// character (the X11 host calls <c>Xutf8LookupString</c>) and carries it on the key arguments
    /// as <c>UnicodeKey</c>, which is how its own TextBox types; the property is internal, so this
    /// reads it by reflection.</para>
    ///
    /// <para>The pattern this serves was worked out in <c>ChatView.xaml.cs</c> (u-19): subscribe to
    /// <b>PreviewKeyDown</b> -- the tunnel phase, so the character can be claimed before the
    /// focused element acts on it -- and when the keystroke is claimed, <b>focus the target and
    /// stop</b>. Do not insert the character as well: the box types it itself once it has the
    /// focus, because the tunnel runs before delivery. Inserting here too was measured to put
    /// "hhola" in the box for a typed "hola".</para>
    /// </summary>
    public static class CharacterInputBridge
    {
        private static bool _probed;
        private static PropertyInfo _unicodeKey;
        private static string _owner;

        /// <summary>
        /// The whole tunnel handler: three pages had written this out identically, and the only
        /// parts that differed are the arguments below.
        ///
        /// <para>The receiver is taken as <c>bool (string, bool)</c> -- the shape all three pages
        /// already had, shared with their Windows <c>CharacterReceived</c> handler -- so callers
        /// pass a method group and nobody restates <c>insert: false</c>.</para>
        /// </summary>
        /// <param name="owner">Names the caller in the log lines.</param>
        /// <param name="receiver">
        /// The page's own receiver, <c>bool (string character, bool insert)</c>, shared with the
        /// Windows <c>CharacterReceived</c> handler. Returns true when it claimed the keystroke.
        /// </param>
        /// <param name="claimed">
        /// What claiming did, for the log line only ("opening search", "focusing the message box").
        /// </param>
        public static void Claim(KeyRoutedEventArgs args, string owner, Func<string, bool, bool> receiver, string claimed)
        {
            if (args.Handled)
            {
                return;
            }

            var character = TryGetCharacter(args, owner);

            // insert: false, here rather than at each call site, because it is not a per-page
            // choice: it is what this bridge is. The box types the keystroke itself once it has the
            // focus -- marking the arguments handled does not stop that, since the tunnel runs
            // before delivery -- so inserting as well doubles the first letter. Measured on
            // ChatView: "hhola" for a typed "hola". Paste (U+0016) is focus-only for the same
            // reason; the box handles Ctrl+V itself.
            if (character != null && receiver(character, false))
            {
                // Only on the rare branch that claims a keystroke -- never while somebody types
                // into a box, which is every other keystroke -- so it costs nothing on the hot
                // path and it is the only line that says this wire is alive.
                Logger.Info($"{owner}: keyboard input arrived with no text box focused, {claimed} for U+{(int)character[0]:X4}");

                args.Handled = true;
            }
        }

        /// <param name="owner">Only used to name the caller in the one-off failure log.</param>
        public static string TryGetCharacter(KeyRoutedEventArgs args, string owner)
        {
            if (!_probed)
            {
                _probed = true;
                _owner = owner;

                try
                {
                    _unicodeKey = typeof(KeyRoutedEventArgs).GetProperty("UnicodeKey", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                catch
                {
                    _unicodeKey = null;
                }

                if (_unicodeKey == null)
                {
                    // Loud on purpose: without it every keystroke outside a text box is dropped
                    // again, which is a feature quietly reverting rather than anything that looks
                    // broken.
                    Logger.Error($"{_owner}: KeyRoutedEventArgs.UnicodeKey is gone; typing without focusing a box first will not work anywhere");
                }
            }

            // char? boxes to a char when it has a value, and to null when it does not.
            return _unicodeKey?.GetValue(args) is char value
                ? value.ToString()
                : null;
        }
    }
}
