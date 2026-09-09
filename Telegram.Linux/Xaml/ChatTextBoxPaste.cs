//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// u-050 — LA MITAD DE TECLADO DE «PEGAR UNA IMAGEN». La otra mitad, con el porque de que esto se
// pueda hacer, esta en `Telegram.Linux/Hubs/DialogViewModel.Paste.Linux.cs`.
//
// POR QUE ESTO CUELGA DE `OnKeyDown` Y NO DEL EVENTO `Paste`. Upstream engancha
// `FormattedTextBox.Paste += OnPaste` y decide alli (`ChatTextBox.OnPaste`, con el mapa de bits
// primero y el texto despues). En este puerto ese evento NO SIRVE: leido del IL de Uno 6.6.184,
// `TextBox.RaisePaste` se llama desde `PasteFromClipboard(string)`, y a ese solo se llega desde
// `PasteFromClipboard()`, que empieza con
//     if (content.AvailableFormats.Contains(StandardDataFormats.Text)) { ... }
// O sea que con una imagen SOLA en el portapapeles el evento `Paste` no se dispara NUNCA. Un
// manejador ahi habria compilado, no habria dado ningun error y no se habria ejecutado jamas.
//
// Y POR QUE, CUANDO HAY TEXTO *Y* IMAGEN, GANA EL TEXTO — que es lo contrario de Windows. Uno
// pega el texto en `OnKeyDownSkia`, al que se llega por `OnPostKeyDown`, DESPUES del manejador de
// clase y sin mirar `args.Handled`. `ChatTextBoxSend.cs` ya documenta esa trampa para el Enter,
// pero alli habia una salida (`AcceptsReturn = false`, que la propia rama de Uno consulta); para
// la V no hay ninguna:
//     case VirtualKey.V: if (!ctrl) break; goto IL_01a7;   IL_01a7: PasteFromClipboard(); return;
// No hay propiedad que consultar ni forma de que un manejador de clase lo cancele. Asi que con
// ambos formatos en el portapapeles el texto se pega SI O SI, y adjuntar ademas la imagen dejaria
// al usuario con las dos cosas — peor que cualquiera de las dos sola. La regla es entonces: la
// imagen se toma SOLO cuando no hay texto que pegar.
// En la practica eso cubre el caso real: `X11ClipboardExtension.TextFormats` solo cuenta
// `UTF8_STRING`, `text/plain;charset=utf-8`, `UTF16_STRING`, `text/plain;charset=utf-16`,
// `XA_STRING` y `OEMTEXT` -- `text/html` NO esta -- asi que un «Copiar imagen» de un navegador
// (que ofrece `image/png` + `text/html`) llega aqui SIN texto, igual que una captura de pantalla o
// un copiar de GIMP.
//
using System;
using Microsoft.UI.Xaml.Input;
using Telegram.ViewModels;
using Windows.ApplicationModel.DataTransfer;
// Sin `using` para VirtualKey / VirtualKeyModifiers: son alias `global using` de
// Telegram/CsWinRT.cs, y repetirlos aqui seria un alias duplicado (misma nota que ChatTextBoxSend.cs).

namespace Telegram.Controls.Chats
{
    public partial class ChatTextBox
    {
        /// <summary>
        /// Whether this Ctrl+V is an image paste, and if so starts it. Called from
        /// <c>OnKeyDown</c>, so it has to answer without blocking: it only looks at what the
        /// clipboard advertises, never at the bytes.
        /// </summary>
        private bool TryPasteImage(KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.V)
            {
                return false;
            }

            // Same two readings as the Enter path: WindowContext.KeyModifiers() plus the local
            // key backstop, because a modifier lost to a window switch would otherwise turn a
            // bare V into a paste. See the backstop's note in ChatTextBoxSend.cs.
            var control = _controlDown || Navigation.WindowContext.KeyModifiers().HasFlag(VirtualKeyModifiers.Control);
            if (!control)
            {
                return false;
            }

            var viewModel = ViewModel;
            if (viewModel == null)
            {
                return false;
            }

            DataPackageView package;

            try
            {
                package = Clipboard.GetContent();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return false;
            }

            if (!DialogViewModel.TryGetClipboardImageFormat(package, out string format, out string extension))
            {
                return false;
            }

            // The one divergence from Windows, and the file header says why: Uno's own paste is
            // going to insert this text no matter what is answered here, so taking the image too
            // would deliver both.
            try
            {
                if (package.Contains(StandardDataFormats.Text))
                {
                    Logger.Info("paste: clipboard carries " + format + " AND text, pasting the text");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return false;
            }

            PasteImage(viewModel, package, format, extension);
            return true;
        }

        /// <summary>
        /// Fire-and-forget half of <see cref="TryPasteImage"/>.
        /// </summary>
        /// <remarks>
        /// An async void, so what it throws does NOT come back to OnKeyDown: the state machine
        /// posts it to Uno's dispatcher, which logs one anonymous line to the CONSOLE and swallows
        /// it -- invisible in a packaged run. The body is a single await of a Task for that
        /// reason, so every failure below travels back to this try.
        /// </remarks>
        private async void PasteImage(DialogViewModel viewModel, DataPackageView package, string format, string extension)
        {
            try
            {
                await viewModel.HandleClipboardImageAsync(package, format, extension);
            }
            catch (Exception ex)
            {
                Logger.Error("paste: handling the pasted image failed", ex);
            }
        }
    }
}
