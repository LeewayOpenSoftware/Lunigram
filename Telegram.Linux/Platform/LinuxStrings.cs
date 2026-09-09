//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;

namespace Telegram.Common
{
    /// <summary>
    /// The handful of strings this port adds that Telegram's translation platform does not have.
    ///
    /// <para><b>Why they are not in <c>Strings/*.resw</c>.</b> Those files are generated from
    /// Telegram's own translations (<c>Resources.cs</c> says so in its header) and the port does not
    /// touch them - <c>PORTING.md</c> rule 4. A key added by hand there would be lost the next time
    /// they are regenerated, and it would not be in the language pack TDLib downloads either, which
    /// is where <c>LocaleService.GetString</c> looks first.</para>
    ///
    /// <para><b>How they are reached.</b> <c>{CustomResource Key}</c> in XAML resolves through
    /// <c>CustomXamlResourceLoader.Current</c> - on Uno as much as on WinUI: Uno's
    /// <c>ResourceResolver.RetrieveCustomResource</c> calls straight into it - and Unigram's loader
    /// is <c>Telegram.Common.XamlResourceLoader</c>, which asks this table first and falls through
    /// to <c>LocaleService</c> for everything it does not know. Asking here first is also what keeps
    /// an unknown key from costing a synchronous <c>Client.Execute(GetLanguagePackString)</c> on
    /// every single binding, since LocaleService does not remember the keys it failed to find.</para>
    ///
    /// <para><b>The one thing to watch.</b> Asking here first means a key defined here <i>shadows</i>
    /// the same key upstream. If Telegram ever ships a string with one of these names, delete the
    /// entry here and the real translation takes over - that is the whole migration.</para>
    ///
    /// <para>Two languages, because those are the two this port is written and used in. Anything
    /// else falls back to English, which is what the rest of the app does for a missing
    /// translation.</para>
    /// </summary>
    public static class LinuxStrings
    {
        private static readonly Dictionary<string, string> _english = new(StringComparer.Ordinal)
        {
            ["TouchMode"] = "Touch mode",
            ["TouchModeInfo"] = "One column and finger-sized targets, for using Unigram on a touch screen: the chat list fills the window and a chat opens over it with a back button, and the interface is scaled up. The scale changes on the next start.",
            ["InterfaceScaleInfo"] = "The interface scale is read once, when Unigram starts, so a change here shows on the next start.",
            // MessageHelper.OnUnsupportedLink. Telegram has no string for "this build cannot open
            // this kind of link": upstream never needed one, because on Windows the switch has a
            // case for all 49 classes.
            ["LinkNotSupported"] = "This Unigram build can't open this kind of link yet.",
        };

        private static readonly Dictionary<string, string> _spanish = new(StringComparer.Ordinal)
        {
            ["TouchMode"] = "Modo táctil",
            ["TouchModeInfo"] = "Una sola columna y objetivos más grandes, pensado para pantalla táctil: la lista de chats ocupa la ventana, el chat se abre encima con un botón de volver y la interfaz se agranda. La escala cambia al reiniciar.",
            ["InterfaceScaleInfo"] = "La escala de la interfaz se lee una sola vez, al arrancar Unigram, así que un cambio aquí se ve al reiniciar.",
            ["LinkNotSupported"] = "Esta versión de Unigram todavía no sabe abrir este tipo de enlace.",
        };

        public static bool TryGetString(string key, out string value)
        {
            if (key == null)
            {
                value = null;
                return false;
            }

            var table = IsSpanish() ? _spanish : _english;
            if (table.TryGetValue(key, out value))
            {
                return true;
            }

            // A key this table knows in English but not in the current language still belongs to
            // us: answering in English beats falling through to a lookup that cannot succeed.
            return _english.TryGetValue(key, out value);
        }

        private static bool IsSpanish()
        {
            // LocaleService.Id is the language Telegram is set to, which is the one the rest of the
            // interface is showing - not the system's, which may differ.
            var id = Services.LocaleService.Current?.Id;
            return id != null && id.StartsWith("es", StringComparison.OrdinalIgnoreCase);
        }
    }
}
