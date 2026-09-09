//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Telegram.Common
{
    /// <summary>
    /// What Locale.FormatCurrency gets in place of Windows.Globalization.NumberFormatting's
    /// CurrencyNumberFormatter, which Uno does not provide: the symbol and the fraction digits of
    /// the currency, laid out the way the display language writes money.
    /// </summary>
    public sealed class CurrencyNumberFormatter
    {
        private static readonly Dictionary<string, (string Symbol, int Digits)> _currencies = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _currenciesLock = new();

        private readonly NumberFormatInfo _format;

        public CurrencyNumberFormatter(string currency, IEnumerable<string> languages, string region)
        {
            Currency = currency;

            var culture = FirstCulture(languages) ?? CultureInfo.CurrentCulture;
            var format = (NumberFormatInfo)culture.NumberFormat.Clone();

            if (TryLookup(currency, region, out var info))
            {
                format.CurrencySymbol = info.Symbol;
                format.CurrencyDecimalDigits = info.Digits;
            }
            else
            {
                // Unknown to ICU (TON, XTR): the code itself, the way a bank statement has it.
                format.CurrencySymbol = currency + " ";
            }

            _format = format;
        }

        public string Currency { get; }

        public string Format(double value)
        {
            return value.ToString("C", _format);
        }

        private static CultureInfo FirstCulture(IEnumerable<string> languages)
        {
            foreach (var language in languages)
            {
                if (string.IsNullOrEmpty(language))
                {
                    continue;
                }

                try
                {
                    return CultureInfo.GetCultureInfo(language.Replace('_', '-'));
                }
                catch (CultureNotFoundException)
                {
                    // Try the next one
                }
            }

            return null;
        }

        private static bool TryLookup(string currency, string region, out (string Symbol, int Digits) info)
        {
            var key = region + "/" + currency;

            lock (_currenciesLock)
            {
                if (_currencies.TryGetValue(key, out info))
                {
                    return info.Symbol != null;
                }
            }

            info = default;

            // The home region first, so USD is "$" in the US and "US$" elsewhere, the way the
            // Windows formatter does it; then any region that uses the currency.
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                RegionInfo candidate;
                try
                {
                    candidate = new RegionInfo(culture.Name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!string.Equals(candidate.ISOCurrencySymbol, currency, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                info = (candidate.CurrencySymbol, culture.NumberFormat.CurrencyDecimalDigits);

                if (string.Equals(candidate.TwoLetterISORegionName, region, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            lock (_currenciesLock)
            {
                _currencies[key] = info;
            }

            return info.Symbol != null;
        }
    }
}
