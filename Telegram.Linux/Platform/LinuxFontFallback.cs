//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Documents.TextFormatting;
using Windows.UI.Text;

namespace Telegram.Common
{
    /// <summary>
    /// Routes the codepoints the UI font cannot draw to the fonts Unigram packages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows a FontFamily is a comma separated fallback list and the "#Family" suffix of an
    /// ms-appx URI registers the file under that name, which is how upstream gets one TextBlock to
    /// render text with the UI font and emojis with Assets/Emoji/apple.ttf. Uno Skia does neither:
    /// FontDetailsCache truncates FontFamily.Source at the first '#' and then treats the whole
    /// remaining string as a single family, so a list matches no font at all and every run ends up
    /// on whatever fontconfig returns for that bogus name.
    /// </para>
    /// <para>
    /// So on Linux each resource key carries one family (see Theme.UpdateEmojiSet) and the
    /// characters that family is missing come back through here: Uno asks the service in
    /// <c>Uno.UI.FeatureConfiguration.Font.FallbackService</c> for a family that covers the
    /// codepoint before falling back to <c>SKFontManager.Default.MatchCharacter</c>, and the family
    /// we answer with is resolved like any other FontFamily.Source - an ms-appx URI loads the
    /// packaged file.
    /// </para>
    /// <para>
    /// Two limits worth knowing. Uno consults <c>FeatureConfiguration.Font.SymbolsFont</c> (the
    /// Fluent icon font) <em>before</em> this service, and 335 of the 666 codepoints in
    /// Telegram.ttf are also in it, so those render as the Fluent icon of the same codepoint
    /// wherever the run's own family is not Telegram.ttf. Keys that name Telegram.ttf directly
    /// (SymbolThemeFontFamily, TelegramThemeFontFamily) are unaffected. And apple.ttf stores its
    /// emojis as CBDT/CBLC colour bitmaps, which Skia only draws with embedded bitmaps enabled on
    /// the SKFont - Uno does not enable them, so the emojis may come out blank or monochrome; that
    /// is a rendering question this service cannot answer either way.
    /// </para>
    /// </remarks>
    internal sealed class LinuxFontFallback : IFontFallbackService
    {
        public const string EmojiFontFamily = "ms-appx:///Assets/Emoji/apple.ttf";
        public const string SymbolFontFamily = "ms-appx:///Assets/Fonts/Telegram.ttf";

        private static readonly LinuxFontFallback _instance = new();

        private static readonly Task<string> _emoji = Task.FromResult(EmojiFontFamily);
        private static readonly Task<string> _symbol = Task.FromResult(SymbolFontFamily);
        private static readonly Task<string> _none = Task.FromResult<string>(null);
        private static readonly Task<Stream> _noStream = Task.FromResult<Stream>(null);

        /// <summary>
        /// Whether the packaged emoji font answers for the emoji codepoints. It does for the
        /// "apple" set. The "microsoft" set is a 14 KB file that on Windows only adds the few
        /// glyphs the system "Segoe UI Emoji" is missing, so on Linux it maps to nothing and the
        /// codepoints fall through to whatever emoji font fontconfig has (Noto Color Emoji, ...).
        /// </summary>
        public static bool UsePackagedEmoji { get; set; } = true;

        private LinuxFontFallback()
        {
        }

        /// <summary>
        /// Must run before the first text is measured: FontDetailsCache caches the service in a
        /// static readonly field the first time it resolves a font.
        /// </summary>
        public static void Register()
        {
            Uno.UI.FeatureConfiguration.Font.FallbackService = _instance;
        }

        public Task<string> GetFontFamilyForCodepoint(int codepoint)
        {
            // Telegram.ttf first: its private use area overlaps apple.ttf at U+E001.
            if (Covers(SymbolRanges, codepoint))
            {
                return _symbol;
            }

            if (UsePackagedEmoji && Covers(EmojiRanges, codepoint))
            {
                return _emoji;
            }

            return _none;
        }

        public Task<Stream> GetFontStreamForFontFamily(string fontFamily, FontWeight weight, FontStretch stretch, FontStyle style)
        {
            // Every family this service names is an ms-appx URI, which the caller loads itself.
            return _noStream;
        }

        private static bool Covers(int[] ranges, int codepoint)
        {
            // Pairs of inclusive bounds, sorted; binary search for the pair that could contain it.
            var low = 0;
            var high = ranges.Length / 2 - 1;

            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (codepoint < ranges[middle * 2])
                {
                    high = middle - 1;
                }
                else if (codepoint > ranges[middle * 2 + 1])
                {
                    low = middle + 1;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        // The cmap of Assets/Emoji/apple.ttf, minus the ASCII it only carries for keycap sequences
        // (the UI font covers those) and minus its private use area (U+E001-U+E01E, U+EA4F), which
        // would shadow Telegram.ttf. Regenerate by dumping the format 12 subtable of the file.
        private static readonly int[] EmojiRanges =
        {
            0x000A9, 0x000A9, 0x000AE, 0x000AE, 0x0200D, 0x0200D, 0x0203C, 0x0203C, 0x02049, 0x02049,
            0x020E3, 0x020E3, 0x02122, 0x02122, 0x02139, 0x02139, 0x02194, 0x02199, 0x021A9, 0x021AA,
            0x0231A, 0x0231B, 0x02328, 0x02328, 0x023CF, 0x023CF, 0x023E9, 0x023F3, 0x023F8, 0x023FA,
            0x024C2, 0x024C2, 0x025AA, 0x025AB, 0x025B6, 0x025B6, 0x025C0, 0x025C0, 0x025FB, 0x025FE,
            0x02600, 0x02604, 0x0260E, 0x0260E, 0x02611, 0x02611, 0x02614, 0x02615, 0x02618, 0x02618,
            0x0261D, 0x0261D, 0x02620, 0x02620, 0x02622, 0x02623, 0x02626, 0x02626, 0x0262A, 0x0262A,
            0x0262E, 0x0262F, 0x02638, 0x0263A, 0x02640, 0x02640, 0x02642, 0x02642, 0x02648, 0x02653,
            0x0265F, 0x02660, 0x02663, 0x02663, 0x02665, 0x02666, 0x02668, 0x02668, 0x0267B, 0x0267B,
            0x0267E, 0x0267F, 0x02692, 0x02697, 0x02699, 0x02699, 0x0269B, 0x0269C, 0x026A0, 0x026A1,
            0x026A7, 0x026A7, 0x026AA, 0x026AB, 0x026B0, 0x026B1, 0x026BD, 0x026BE, 0x026C4, 0x026C5,
            0x026C8, 0x026C8, 0x026CE, 0x026CF, 0x026D1, 0x026D1, 0x026D3, 0x026D4, 0x026E9, 0x026EA,
            0x026F0, 0x026F5, 0x026F7, 0x026FA, 0x026FD, 0x026FD, 0x02702, 0x02702, 0x02705, 0x02705,
            0x02708, 0x0270D, 0x0270F, 0x0270F, 0x02712, 0x02712, 0x02714, 0x02714, 0x02716, 0x02716,
            0x0271D, 0x0271D, 0x02721, 0x02721, 0x02728, 0x02728, 0x02733, 0x02734, 0x02744, 0x02744,
            0x02747, 0x02747, 0x0274C, 0x0274C, 0x0274E, 0x0274E, 0x02753, 0x02755, 0x02757, 0x02757,
            0x02763, 0x02764, 0x02795, 0x02797, 0x027A1, 0x027A1, 0x027B0, 0x027B0, 0x027BF, 0x027BF,
            0x02934, 0x02935, 0x02B05, 0x02B07, 0x02B1B, 0x02B1C, 0x02B50, 0x02B50, 0x02B55, 0x02B55,
            0x03030, 0x03030, 0x0303D, 0x0303D, 0x03297, 0x03297, 0x03299, 0x03299, 0x1F004, 0x1F004,
            0x1F0CF, 0x1F0CF, 0x1F170, 0x1F171, 0x1F17E, 0x1F17F, 0x1F18E, 0x1F18E, 0x1F191, 0x1F19A,
            0x1F1E6, 0x1F1FF, 0x1F201, 0x1F202, 0x1F21A, 0x1F21A, 0x1F22F, 0x1F22F, 0x1F232, 0x1F23A,
            0x1F250, 0x1F251, 0x1F300, 0x1F321, 0x1F324, 0x1F393, 0x1F396, 0x1F397, 0x1F399, 0x1F39B,
            0x1F39E, 0x1F3F0, 0x1F3F3, 0x1F3F5, 0x1F3F7, 0x1F4FD, 0x1F4FF, 0x1F53D, 0x1F549, 0x1F54E,
            0x1F550, 0x1F567, 0x1F56F, 0x1F570, 0x1F573, 0x1F57A, 0x1F587, 0x1F587, 0x1F58A, 0x1F58D,
            0x1F590, 0x1F590, 0x1F595, 0x1F596, 0x1F5A4, 0x1F5A5, 0x1F5A8, 0x1F5A8, 0x1F5B1, 0x1F5B2,
            0x1F5BC, 0x1F5BC, 0x1F5C2, 0x1F5C4, 0x1F5D1, 0x1F5D3, 0x1F5DC, 0x1F5DE, 0x1F5E1, 0x1F5E1,
            0x1F5E3, 0x1F5E3, 0x1F5E8, 0x1F5E8, 0x1F5EF, 0x1F5EF, 0x1F5F3, 0x1F5F3, 0x1F5FA, 0x1F64F,
            0x1F680, 0x1F6C5, 0x1F6CB, 0x1F6D2, 0x1F6D5, 0x1F6D8, 0x1F6DC, 0x1F6E5, 0x1F6E9, 0x1F6E9,
            0x1F6EB, 0x1F6EC, 0x1F6F0, 0x1F6F0, 0x1F6F3, 0x1F6FC, 0x1F7E0, 0x1F7EB, 0x1F7F0, 0x1F7F0,
            0x1F90C, 0x1F93A, 0x1F93C, 0x1F945, 0x1F947, 0x1F9FF, 0x1FA70, 0x1FA7C, 0x1FA80, 0x1FA8A,
            0x1FA8E, 0x1FAC6, 0x1FAC8, 0x1FAC8, 0x1FACD, 0x1FADC, 0x1FADF, 0x1FAEA, 0x1FAEF, 0x1FAF8,
        };

        // The cmap of Assets/Fonts/Telegram.ttf, minus U+0000-U+0020.
        private static readonly int[] SymbolRanges =
        {
            0x0E001, 0x0E001, 0x0E0E2, 0x0E0E5, 0x0E104, 0x0E104, 0x0E10A, 0x0E10C, 0x0E118, 0x0E118,
            0x0E168, 0x0E168, 0x0E192, 0x0E192, 0x0E1C4, 0x0E1C4, 0x0E248, 0x0E248, 0x0E2B1, 0x0E2B1,
            0x0E600, 0x0E603, 0x0E605, 0x0E605, 0x0E60C, 0x0E60E, 0x0E610, 0x0E610, 0x0E612, 0x0E612,
            0x0E700, 0x0E700, 0x0E710, 0x0E714, 0x0E716, 0x0E717, 0x0E71B, 0x0E71B, 0x0E71F, 0x0E722,
            0x0E72A, 0x0E72E, 0x0E730, 0x0E730, 0x0E734, 0x0E735, 0x0E73E, 0x0E73E, 0x0E74B, 0x0E74B,
            0x0E74D, 0x0E74D, 0x0E74F, 0x0E74F, 0x0E75C, 0x0E75C, 0x0E762, 0x0E762, 0x0E768, 0x0E769,
            0x0E76E, 0x0E76E, 0x0E774, 0x0E774, 0x0E77A, 0x0E77B, 0x0E77F, 0x0E77F, 0x0E783, 0x0E783,
            0x0E785, 0x0E785, 0x0E787, 0x0E787, 0x0E789, 0x0E789, 0x0E792, 0x0E792, 0x0E7A6, 0x0E7A8,
            0x0E7AC, 0x0E7AC, 0x0E7B8, 0x0E7B8, 0x0E7C3, 0x0E7C3, 0x0E7ED, 0x0E7ED, 0x0E81C, 0x0E81C,
            0x0E825, 0x0E825, 0x0E838, 0x0E838, 0x0E840, 0x0E840, 0x0E892, 0x0E894, 0x0E897, 0x0E897,
            0x0E8BD, 0x0E8BD, 0x0E8C6, 0x0E8C6, 0x0E8C8, 0x0E8C8, 0x0E8CB, 0x0E8CB, 0x0E8D2, 0x0E8D2,
            0x0E8D6, 0x0E8D6, 0x0E8D9, 0x0E8D9, 0x0E8DB, 0x0E8DE, 0x0E8FA, 0x0E8FB, 0x0E900, 0x0EB25,
            0x0EB9F, 0x0EB9F, 0x0EC42, 0x0EC42, 0x0EC61, 0x0EC61, 0x0ED15, 0x0ED15, 0x0EDD9, 0x0EDDC,
            0x0EE56, 0x0EE56, 0x0F0E3, 0x0F0E3, 0x0F122, 0x0F122, 0x0F12B, 0x0F12B, 0x0F12E, 0x0F12E,
            0x0F164, 0x0F164, 0x0F166, 0x0F166, 0x0F3B1, 0x0F3B1, 0x0F4A9, 0x0F4AA, 0x0F61B, 0x0F61B,
            0x0F78D, 0x0F78D, 0xE6000, 0xE6000,
        };
    }
}
