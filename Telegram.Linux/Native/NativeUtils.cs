//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Telegram.Native
{
    public delegate void FatalErrorCallback(FatalError error);

    public delegate void LogCallback(int level, string message, string member, string filePath, int line);

    public enum TextDirectionality
    {
        Neutral,
        LeftToRight,
        RightToLeft
    }

    [Flags]
    public enum TextStyle : uint
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Monospace = 4,
        Strikethrough = 8,
        Underline = 16,
        Spoiler = 32,
        Mention = 64,
        Url = 128,
        Emoji = 256,
        Quote = 512,
        Subscript = 0x400,
        Superscript = 0x800,
        Marked = 0x1000,
        Icon = 0x2000,
        Math = 0x4000,
        Button = 0x8000,
        Cached = 0x10000
    }

    public static class NativeUtils
    {
        private static FatalErrorCallback _fatalErrorCallback;
        private static LogCallback _logCallback;

        public static bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public static long GetDirectorySize(string path)
        {
            return GetDirectorySize(path, "*");
        }

        public static long GetDirectorySize(string path, string filter)
        {
            try
            {
                long size = 0;

                foreach (var file in Directory.EnumerateFiles(path, filter, SearchOption.AllDirectories))
                {
                    size += new FileInfo(file).Length;
                }

                return size;
            }
            catch
            {
                return 0;
            }
        }

        public static void CleanDirectory(string path, int days)
        {
        }

        public static void Delete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Same as the native side: best effort
            }
        }

        // InactivityHelper computes Environment.TickCount - GetLastInputTime():
        // reporting "now" keeps the idle time at zero, so the online status never drops.
        public static uint GetLastInputTime()
        {
            return unchecked((uint)Environment.TickCount64);
        }

        #region Directionality

        // DECLARED DIVERGENCE, unmeasured. GetDirectionality below is the same algorithm as the
        // Windows original, character for character -- first strong class after a neutral one wins.
        // What is NOT the same is where the class comes from: there it was
        // GetStringTypeEx(CT_CTYPE2), a Microsoft table; here it is the switch in GetCharType plus
        // CharUnicodeInfo. So every difference in behaviour, if there is one, lives in the table and
        // nowhere else, and it shows up as a whole paragraph laid out in the wrong direction in
        // FormattedTextBlock -- never as a wrong character.
        //
        // Read against the Unicode bidi classes, the explicit cases all agree (ES for + -, ET for
        // # $ % U+00B0 U+2030, CS for , . : / NBSP, B/S/WS as listed, LRM/RLM/ALM), and
        // IsRightToLeft covers every strong RTL letter in the BMP. What cannot be settled by reading is the tail: which
        // class Windows assigns to the code points where CT_CTYPE2 and the Unicode class do not
        // have to agree (non-Latin Nd, Nl, unassigned and private-use). Settling it needs the sweep
        // in unigram-linux/review/probes/B-deriva-semantica/B4_directionality_table.cs, which has to
        // run on Windows; until then this is a divergence of unknown magnitude, not a known-good
        // port. Surrogates are skipped here exactly as upstream skipped them.
        //
        // Win32 CT_CTYPE2 classes, the ones GetStringTypeEx hands to the native implementation
        private const int C2_LEFTTORIGHT = 1;
        private const int C2_RIGHTTOLEFT = 2;
        private const int C2_EUROPENUMBER = 3;
        private const int C2_EUROPESEPARATOR = 4;
        private const int C2_EUROPETERMINATOR = 5;
        private const int C2_ARABICNUMBER = 6;
        private const int C2_COMMONSEPARATOR = 7;
        private const int C2_BLOCKSEPARATOR = 8;
        private const int C2_SEGMENTSEPARATOR = 9;
        private const int C2_WHITESPACE = 10;
        private const int C2_OTHERNEUTRAL = 11;

        public static TextDirectionality GetDirectionality(string value)
        {
            return GetDirectionality(value, 0, value?.Length ?? 0);
        }

        public static TextDirectionality GetDirectionality(string value, int offset)
        {
            return GetDirectionality(value, offset, (value?.Length ?? 0) - offset);
        }

        public static TextDirectionality GetDirectionality(string value, int offset, int length)
        {
            if (string.IsNullOrEmpty(value) || offset < 0 || offset >= value.Length)
            {
                return TextDirectionality.Neutral;
            }

            length = Math.Min(length, value.Length - offset);

            var prev = C2_OTHERNEUTRAL;
            for (int i = 0; i < length; i++)
            {
                var c = value[offset + i];
                if (char.IsSurrogate(c))
                {
                    continue;
                }

                var type = GetCharType(c);

                // We use the first strong character after a neutral character.
                if (prev >= C2_BLOCKSEPARATOR && prev <= C2_OTHERNEUTRAL)
                {
                    if (type == C2_LEFTTORIGHT)
                    {
                        return TextDirectionality.LeftToRight;
                    }
                    else if (type == C2_RIGHTTOLEFT)
                    {
                        return TextDirectionality.RightToLeft;
                    }
                }

                prev = type;
            }

            return TextDirectionality.Neutral;
        }

        private static int GetCharType(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return C2_EUROPENUMBER;
            }
            else if ((c >= '\u0660' && c <= '\u0669') || (c >= '\u06F0' && c <= '\u06F9'))
            {
                return C2_ARABICNUMBER;
            }

            switch (c)
            {
                case '+':
                case '-':
                    return C2_EUROPESEPARATOR;
                case '#':
                case '$':
                case '%':
                case '\u00B0':
                case '\u2030':
                    return C2_EUROPETERMINATOR;
                case ',':
                case '.':
                case ':':
                case '/':
                case '\u00A0':
                    return C2_COMMONSEPARATOR;
                case '\n':
                case '\r':
                case '\u001C':
                case '\u001D':
                case '\u001E':
                case '\u0085':
                case '\u2029':
                    return C2_BLOCKSEPARATOR;
                case '\t':
                case '\u000B':
                case '\u001F':
                    return C2_SEGMENTSEPARATOR;
                case ' ':
                case '\u000C':
                case '\u2028':
                    return C2_WHITESPACE;
                case '\u200E':
                    return C2_LEFTTORIGHT;
                case '\u200F':
                case '\u061C':
                    return C2_RIGHTTOLEFT;
            }

            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.LetterNumber:
                    return IsRightToLeft(c) ? C2_RIGHTTOLEFT : C2_LEFTTORIGHT;
                case UnicodeCategory.DecimalDigitNumber:
                    return C2_EUROPENUMBER;
                case UnicodeCategory.SpaceSeparator:
                    return C2_WHITESPACE;
                case UnicodeCategory.ParagraphSeparator:
                    return C2_BLOCKSEPARATOR;
                case UnicodeCategory.CurrencySymbol:
                    return C2_EUROPETERMINATOR;
                default:
                    return C2_OTHERNEUTRAL;
            }
        }

        // Hebrew, Arabic, Syriac, Thaana, NKo, Samaritan, Mandaic and the presentation forms
        private static bool IsRightToLeft(char c)
        {
            return (c >= '\u0590' && c <= '\u08FF')
                || (c >= '\uFB1D' && c <= '\uFDFF')
                || (c >= '\uFE70' && c <= '\uFEFF');
        }

        #endregion

        #region Culture

        public static string GetCurrentCulture()
        {
            var name = CultureInfo.CurrentCulture.Name;
            if (string.IsNullOrEmpty(name))
            {
                return "en";
            }

            var sorting = name.IndexOf('_');
            if (sorting >= 0)
            {
                return name.Substring(0, sorting);
            }

            return name;
        }

        public static string GetKeyboardCulture()
        {
            var culture = CultureInfo.CurrentUICulture;
            if (culture == null || string.IsNullOrEmpty(culture.Name))
            {
                culture = CultureInfo.CurrentCulture;
            }

            var language = culture?.TwoLetterISOLanguageName;
            if (string.IsNullOrEmpty(language) || language == "iv")
            {
                return "en";
            }

            return language;
        }

        #endregion

        #region Date and time

        public static string FormatTime(int value)
        {
            return FormatTime(DateTimeOffset.FromUnixTimeSeconds(value));
        }

        public static string FormatTime(DateTimeOffset value)
        {
            try
            {
                return value.ToLocalTime().DateTime.ToString("t", CultureInfo.CurrentCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string FormatDate(int value, string format)
        {
            return FormatDate(DateTimeOffset.FromUnixTimeSeconds(value), format);
        }

        public static string FormatDate(DateTimeOffset value, string format)
        {
            try
            {
                return value.ToLocalTime().DateTime.ToString(ToDateFormat(format), CultureInfo.CurrentCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string FormatDate(int year, int month, int day, string format)
        {
            try
            {
                return new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local).ToString(ToDateFormat(format), CultureInfo.CurrentCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        // Translates a GetDateFormatEx picture: d/M/y/g runs and quoted text mean the same thing in
        // .NET, but every other letter is a literal there and a specifier here, and a single
        // character would be read as a standard format.
        private static string ToDateFormat(string format)
        {
            if (format == "DATE_SHORTDATE")
            {
                return "d";
            }
            else if (format == "DATE_LONGDATE")
            {
                return "D";
            }
            else if (string.IsNullOrEmpty(format))
            {
                return "d";
            }

            var builder = new StringBuilder(format.Length + 8);

            for (int i = 0; i < format.Length; i++)
            {
                var c = format[i];
                if (c == '\'')
                {
                    var end = format.IndexOf('\'', i + 1);
                    if (end < 0)
                    {
                        builder.Append(format, i, format.Length - i).Append('\'');
                        break;
                    }

                    builder.Append(format, i, end - i + 1);
                    i = end;
                }
                else if (c is 'd' or 'M' or 'y' or 'g')
                {
                    var start = i;
                    while (i + 1 < format.Length && format[i + 1] == c)
                    {
                        i++;
                    }

                    builder.Append(c, i - start + 1);
                }
                else if (char.IsLetter(c) || c is '/' or ':' or '%' or '\\')
                {
                    builder.Append('\\').Append(c);
                }
                else
                {
                    builder.Append(c);
                }
            }

            var result = builder.ToString();
            return result.Length == 1 ? "%" + result : result;
        }

        #endregion

        public static bool IsFileReadable(string path)
        {
            return IsFileReadable(path, out _, out _);
        }

        public static bool IsFileReadable(string path, out long fileSize, out long fileTime)
        {
            fileSize = 0;
            fileTime = 0;

            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    using (info.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fileSize = info.Length;
                        fileTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
                        return true;
                    }
                }
            }
            catch
            {
                // Fall through
            }

            return false;
        }

        public static bool IsMediaSupported()
        {
            return false;
        }

        /// <summary>
        /// Unigram's interface scale. On Windows this pushed a resolution scale onto the current
        /// view and every pixel followed at once; on Uno the equivalent single number can only be
        /// set before the first window exists, so this stores the choice and
        /// <c>Program.Main</c> applies it on the next start - see
        /// <see cref="Telegram.Common.InterfaceScale"/> for the three ways this could have been
        /// done and why a live <c>ScaleTransform</c> is not one of them.
        ///
        /// The value has already been written to <c>AppearanceSettings.Scaling</c> by both callers
        /// (<c>SettingsAppearanceViewModel.Scaling</c> assigns inside the argument;
        /// <c>WindowContext</c> passes back what it just read), so there is nothing left to
        /// persist here.
        /// </summary>
        public static void OverrideScaleForCurrentView(int value)
        {
        }

        /// <summary>
        /// The scale in force for this process, as a percentage of the desktop's own - 100 when
        /// nothing was overridden. <c>SettingsAppearanceViewModel</c> asks for it when the user
        /// picks "Default", to store a concrete number instead of 0.
        /// </summary>
        public static int GetScaleForCurrentView()
        {
            return Common.InterfaceScale.AppliedPercent;
        }

        public static void SetFatalErrorCallback(FatalErrorCallback action)
        {
            _fatalErrorCallback = action;
        }

        public static void SetLogCallback(LogCallback callback)
        {
            _logCallback = callback;
        }

        public static void Log(int level, string message, string member, string filePath, int line)
        {
            _logCallback?.Invoke(level, message, member, filePath, line);
        }

        public static FatalError GetStowedException()
        {
            return null;
        }

        public static FatalError GetBackTrace(string type, string message)
        {
            return new FatalError(type, message, Environment.StackTrace, new System.Collections.Generic.List<FatalErrorFrame>());
        }

        public static string GetLogMessage(long format, long args)
        {
            return string.Empty;
        }

        public static void Crash()
        {
            Environment.FailFast("NativeUtils.Crash");
        }
    }
}
