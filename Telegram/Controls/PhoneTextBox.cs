//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Text;
using Telegram.Common;
using Telegram.Entities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public partial class PhoneTextBox : TextBox
    {
        private string _previousText = string.Empty;
        private int _selectionStart;

        private int _characterAction = -1;
        private int _actionPosition;

        private bool _ignoreOnPhoneChange;

        private Country _country;

        public PhoneTextBox()
        {
            DefaultStyleKey = typeof(PhoneTextBox);

            Text = "+";

#if LINUX
            // Uno raises TextChanging, but neither the caret it reports nor the caret written from
            // inside it can be trusted, so the formatting pass lives in TextChanged. See
            // OnPhoneChanged.
            TextChanged += OnPhoneChanged;
#else
            TextChanging += OnTextChanging;
            TextChanged += OnTextChanged;
#endif
        }

        private void Started()
        {
            var start = SelectionStart;
            var after = Math.Max(0, Text.Length - _previousText.Length);
            var count = Math.Max(0, _previousText.Length - Text.Length);

            if (count == 0 && after == 1)
            {
                _characterAction = 1;
            }
            else if (count == 1 && after == 0)
            {
                if (_previousText[start] == ' ' && start > 0)
                {
                    _characterAction = 3;
                    _actionPosition = start - 1;
                }
                else
                {
                    _characterAction = 2;
                }
            }
            else
            {
                _characterAction = -1;
            }
        }

        private string GetHint(string text)
        {
            var groups = PhoneNumber.Parse(text);
            if (groups.Length < 1)
            {
                _country = null;
                Country = null;

                return null;
            }

            var builder = new StringBuilder();

            for (int i = 0; i < groups.Length; i++)
            {
                for (int j = 0; j < groups[i]; j++)
                {
                    builder.Append('-');
                }

                if (i + 1 < groups.Length)
                {
                    builder.Append(' ');
                }
            }

            if (Country.KeyedCountries.TryGetValue(text.Substring(0, groups[0]), out Country value))
            {
                _country = value;
                Country = value;
            }
            else
            {
                _country = null;
                Country = null;
            }

            return builder.ToString();
        }

        private void OnTextChanging(TextBox sender, TextBoxTextChangingEventArgs w)
        {
            Started();

            if (_ignoreOnPhoneChange)
            {
                return;
            }

            int start = SelectionStart;
            string phoneChars = "0123456789";
            string str = Text;
            if (_characterAction == 3)
            {
                str = str.Substring(0, _actionPosition) + str.Substring(_actionPosition + 1);
                start--;
            }
            StringBuilder builder = new(str.Length);
            for (int a = 0; a < str.Length; a++)
            {
                string ch = str.Substring(a, 1);
                if (phoneChars.Contains(ch))
                {
                    builder.Append(ch);
                }
            }
            _ignoreOnPhoneChange = true;
            string hint = GetHint(builder.ToString());
            if (hint != null)
            {
                for (int a = 0; a < builder.Length; a++)
                {
                    if (a < hint.Length)
                    {
                        if (hint[a] == ' ')
                        {
                            builder.Insert(a, ' ');
                            a++;
                            if (start == a && _characterAction != 2 && _characterAction != 3)
                            {
                                start++;
                            }
                        }
                    }
                    else
                    {
                        builder.Insert(a, ' ');
                        if (start == a + 1 && _characterAction != 2 && _characterAction != 3)
                        {
                            start++;
                        }
                        break;
                    }
                }
            }
            Text = $"+{builder}";
            if (start + 1 >= 0)
            {
                _selectionStart = start + 1 <= Text.Length ? start + 1 : Text.Length;
                SelectionStart = _selectionStart;
            }
            _ignoreOnPhoneChange = false;

            _previousText = Text;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            SelectionStart = _selectionStart;
        }

#if LINUX
        /// <summary>
        /// The whole of the formatting on Uno. The two handlers above are written against WinUI's
        /// editing contract, and on Uno three pieces of it are not true - measured in
        /// <c>unigram-linux/spikes/TextBoxSpike</c> (run-03.log):
        ///
        /// <list type="bullet">
        /// <item>Assigning <c>SelectionStart</c> inside <c>TextChanging</c> is discarded - it reads
        /// back correctly inside the handler and is 0 once the event is over.</item>
        /// <item><c>SelectionStart</c> inside <c>TextChanging</c> is the caret BEFORE the edit, not
        /// after it. On a BackSpace at the end of "123" it reads 3, so <c>Started</c> would
        /// evaluate <c>_previousText[3]</c> on a three-character string: IndexOutOfRangeException
        /// on a deletion at the end of the number.</item>
        /// <item><c>TextChanged</c> is queued, not raised inside the <c>Text</c> setter, and
        /// assigning <c>Text</c> from <c>TextChanging</c> makes it arrive twice.</item>
        /// </list>
        ///
        /// Typing "+541155512345" into the two handlers above on Uno produced, key by key,
        /// "+54 321555114554" with the caret stuck at 1 and BackSpace doing nothing at all: the
        /// caret written from <c>TextChanging</c> is lost, <c>OnTextChanged</c> restores a position
        /// worked out from it, and every further digit is inserted at that stuck caret - which is
        /// what reorders the number. The key-by-key run is in
        /// <c>unigram-linux/spikes/TextBoxSpike/phonetextbox-before.log</c>, and the same run with
        /// this handler in place in <c>phonetextbox-after.log</c>.
        ///
        /// So none of the "which key was pressed, and by how much does the caret have to move"
        /// bookkeeping survives the port, and it does not need to: inside <c>TextChanged</c> Uno
        /// reports faithfully both the text the user just produced and the caret after the edit,
        /// and those two are enough. The caret is remembered as <em>the number of digits to its
        /// left</em> - the one thing reformatting cannot move - the string is rebuilt from the
        /// digits alone, and the caret is put back after that same digit. Typing, deleting,
        /// pasting and replacing a selection all come out right without the control ever knowing
        /// which of them happened.
        ///
        /// Re-entrancy: <c>_ignoreOnPhoneChange</c> stops the synchronous events (the country
        /// change, <c>TextChanging</c>), but the <c>TextChanged</c> raised by the assignment below
        /// is queued and arrives with the flag already cleared. It is harmless because the pass is
        /// idempotent: on the second run the text is already formatted and the caret already sits
        /// after the same digit, so both assignments are skipped and it stops there.
        /// </summary>
        private void OnPhoneChanged(object sender, TextChangedEventArgs e)
        {
            if (_ignoreOnPhoneChange)
            {
                return;
            }

            var text = Text ?? string.Empty;
            var caret = Math.Clamp(SelectionStart, 0, text.Length);

            var digits = new StringBuilder(text.Length);
            var digitsBeforeCaret = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is >= '0' and <= '9')
                {
                    digits.Append(text[i]);

                    if (i < caret)
                    {
                        digitsBeforeCaret++;
                    }
                }
            }

            var number = digits.ToString();

            _ignoreOnPhoneChange = true;

            // GetHint is what recognises the country and publishes it; PhoneNumber.Format lays the
            // very same groups out, and is what the rest of the app formats numbers with.
            GetHint(number);

            var formatted = PhoneNumber.Format(number);
            var position = OffsetAfterDigits(formatted, digitsBeforeCaret);

            if (!string.Equals(formatted, text, StringComparison.Ordinal))
            {
                Text = formatted;
            }

            if (SelectionStart != position)
            {
                SelectionStart = position;
            }

            _ignoreOnPhoneChange = false;
        }

        /// <summary>
        /// The offset just past the <paramref name="digits"/>-th digit of <paramref name="text"/>,
        /// which is where a caret that had that many digits to its left belongs. Note that it is
        /// never right after a separator, so the digit a BackSpace is about to take is always the
        /// one the user is looking at.
        /// </summary>
        private static int OffsetAfterDigits(string text, int digits)
        {
            if (digits < 1)
            {
                // Just past the leading '+', which this control always keeps.
                return text.Length > 0 && text[0] == '+' ? 1 : 0;
            }

            var seen = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is >= '0' and <= '9' && ++seen == digits)
                {
                    return i + 1;
                }
            }

            return text.Length;
        }
#endif

        #region Country

        public Country Country
        {
            get => (Country)GetValue(CountryProperty);
            set => SetValue(CountryProperty, value);
        }

        public static readonly DependencyProperty CountryProperty =
            DependencyProperty.Register("Country", typeof(Country), typeof(PhoneTextBox), new PropertyMetadata(null, OnCountryChanged));

        private static void OnCountryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((PhoneTextBox)d).OnCountryChanged((Country)e.NewValue, (Country)e.OldValue);
        }

        private void OnCountryChanged(Country newValue, Country oldValue)
        {
            if (newValue?.PhoneCode == oldValue?.PhoneCode)
            {
                return;
            }

            if (newValue?.PhoneCode == _country?.PhoneCode)
            {
                return;
            }

            _ignoreOnPhoneChange = true;
            _selectionStart = $"+{newValue?.PhoneCode}".Length;

            Text = $"+{newValue?.PhoneCode}";

            _country = newValue;

            _ignoreOnPhoneChange = false;
            _previousText = string.Empty;

            SelectionStart = Text.Length;
            Started();
        }

        #endregion
    }
}
