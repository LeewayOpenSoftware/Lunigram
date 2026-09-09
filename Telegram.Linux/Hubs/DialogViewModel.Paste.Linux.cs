//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// u-050 — PEGAR UNA IMAGEN DEL PORTAPAPELES EN EL COMPOSITOR (Ctrl+V).
//
// LA NOTA QUE ESTE FICHERO CORRIGE. `DialogViewModel.Attach.Linux.cs` (PARIDAD A3) declaraba esto
// como imposible en su lista de «LO QUE NO HACE»:
//     «PEGAR UNA IMAGEN DEL PORTAPAPELES (StandardDataFormats.Bitmap): FillDataPackage no llama
//      nunca a SetBitmap, asi que ese formato no existe en este backend».
// La primera mitad es CIERTA y la comprobe otra vez descompilando
// `Uno.UI.Runtime.Skia.X11.dll` 6.6.184 (`X11ClipboardExtension.FillDataPackage`): solo llama a
// `SetText` (cuando hay un atomo de `TextFormats`) y a `SetStorageItems` (cuando hay
// `text/uri-list`). `SetBitmap` no aparece, y el `WaitForImage` que devuelve un `SKBitmap` esta
// ahi, privado y SIN LLAMANTES. `StandardDataFormats.Bitmap` no llega nunca.
//
// PERO LA CONCLUSION NO SE SEGUIA. La MISMA `FillDataPackage`, antes de esos dos casos especiales,
// hace esto por CADA atomo que anuncia el portapapeles:
//     dataPackage.SetDataProvider(nombreDelAtomo, ct => Task.Run(() => WaitForBytes(...), ct));
// y `DataPackageView.AvailableFormats` es literalmente `_data.Keys`. O sea que si quien copia
// ofrece `image/png`, ESE nombre esta en `AvailableFormats` y `GetDataAsync("image/png")` devuelve
// los BYTES DEL FICHERO PNG tal cual (`DataPackageView.GetData<T>` ve un `DataProviderHandler` y lo
// ejecuta). El formato tipado no existe; los datos SI. **REGLA: en Uno, un `Contains(Standard
// DataFormats.X)` que da false NO significa que el dato no este — significa que nadie lo publico
// con ese nombre. Mirar `AvailableFormats` en crudo antes de dar algo por ausente.**
//
// LO QUE SE HACE CON ESOS BYTES: se escriben a un fichero temporal y se entregan a
// `SendFilesAsync`, que es la puerta unica que A3 ya construyo para el clip y para el arrastre. De
// ahi en adelante NO HAY NADA NUEVO: misma clasificacion (solo `.jpg`/`.jpeg`/`.png` viajan como
// `inputMessagePhoto`, hasta 10 MB, no animados, medidos con `SKCodec`), mismos derechos por
// item, y la MISMA ventana de confirmacion `SendFilesLinuxPopup`. Nada sale sin que el usuario lo
// vea, que es la regla de A3 y no cambia porque el origen sea el portapapeles.
//
// LO QUE NO HACE, heredado de A3 y dicho aqui para que no se lea como una limitacion nueva:
//   - NO RECOMPRIME. Upstream pasa toda foto por `ConversionType.Compress` y manda siempre JPEG;
//     aqui suben los bytes que habia en el portapapeles. Por eso el orden de preferencia de abajo
//     pone `image/png` e `image/jpeg` DELANTE: son los dos unicos que Telegram acepta como foto sin
//     tocar. Un origen que solo ofrezca BMP o TIFF se manda como FICHERO, y la ventana lo dice.
//   - NO AGRUPA EN ALBUM ni edita el medio de un mensaje en edicion (mismo rechazo que el arrastre:
//     pegar sobre una edicion mandaria un mensaje NUEVO).
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using IODirectory = System.IO.Directory;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace Telegram.ViewModels
{
    public partial class DialogViewModel
    {
        // Los nombres son ATOMOS de X11 tal y como los devuelve `XLib.GetAtomName`, no formatos de
        // WinRT: es lo que `FillDataPackage` usa como clave. El ORDEN es la politica entera --
        // se toma el PRIMERO que ofrezca el portapapeles, y los dos que pueden viajar como foto sin
        // recompresor van delante. Los de abajo se envian como fichero, que es mejor que rechazar
        // un pegado con un mensaje que el usuario no puede accionar.
        //
        // Las variantes `image/x-bmp` / `image/x-MS-bmp` no son celo: son los nombres que ofrecen
        // GIMP y varias herramientas de captura junto al `image/bmp` estandar, y una lista que solo
        // mirase el estandar dejaria fuera al que de verdad esta en el portapapeles.
        private static readonly (string Format, string Extension)[] _clipboardImageFormats = new[]
        {
            ("image/png", ".png"),
            ("image/jpeg", ".jpg"),
            ("image/jpg", ".jpg"),
            ("image/webp", ".webp"),
            ("image/gif", ".gif"),
            ("image/tiff", ".tiff"),
            ("image/bmp", ".bmp"),
            ("image/x-bmp", ".bmp"),
            ("image/x-MS-bmp", ".bmp"),
            ("image/x-win-bitmap", ".bmp")
        };

        // Un PNG de pantalla completa a 4K ronda los 10 MB; 64 es holgado para cualquier captura y
        // sigue siendo un techo, que es lo que hace falta cuando los bytes los pone otro proceso.
        private const long ClipboardImageSizeMax = 64L << 20;

        // `WaitForBytes` habla el protocolo de seleccion de X11 (incluido INCR) contra el proceso
        // que copio, en un hilo del pool. Si ese proceso se muere a media transferencia la espera
        // no termina sola. El limite no arregla eso -- el hilo sigue ahi -- pero convierte un
        // pegado que no hace NADA en un pegado que deja una linea en el log y un aviso en pantalla.
        private const int ClipboardImageTimeoutMs = 8000;

        /// <summary>
        /// The clipboard image format this head would take, chosen from what the clipboard offers.
        /// Returns false when there is no image on it.
        /// </summary>
        /// <remarks>
        /// Deliberately synchronous and cheap: it only reads <c>AvailableFormats</c>, which
        /// <c>Clipboard.GetContent()</c> has already filled in. The bytes are NOT fetched here --
        /// that is a round trip to the process that copied, and the key handler has to decide
        /// whether it is taking the keystroke before it can afford one.
        /// </remarks>
        public static bool TryGetClipboardImageFormat(DataPackageView package, out string format, out string extension)
        {
            format = null;
            extension = null;

            if (package == null)
            {
                return false;
            }

            IReadOnlyList<string> available;

            try
            {
                available = package.AvailableFormats;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return false;
            }

            if (available == null || available.Count == 0)
            {
                return false;
            }

            foreach (var (candidate, suffix) in _clipboardImageFormats)
            {
                if (available.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    format = candidate;
                    extension = suffix;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Takes the image on the clipboard into the composer's attach flow: bytes to a temporary
        /// file, then the same confirmation window every other attachment goes through.
        /// </summary>
        public async Task HandleClipboardImageAsync(DataPackageView package, string format, string extension)
        {
            if (package == null || Chat == null || string.IsNullOrEmpty(format))
            {
                return;
            }

            // Same refusal as HandlePackageAsync: replacing the media of a message being edited
            // needs SendFilesPopup, which is out of the subset, so accepting here would send a NEW
            // message instead of editing the one on screen.
            if (ComposerHeader?.Editing != null)
            {
                Logger.Info("paste: image pasted while editing a message, ignored");
                return;
            }

            byte[] bytes;

            try
            {
                var read = package.GetDataAsync(format).AsTask();
                var completed = await Task.WhenAny(read, Task.Delay(ClipboardImageTimeoutMs));

                if (completed != read)
                {
                    Logger.Error("paste: the clipboard owner did not deliver " + format + " within "
                        + ClipboardImageTimeoutMs + " ms");

                    await ShowPopupAsync(Strings.UnsupportedAttachment, Strings.AppName, Strings.OK);
                    return;
                }

                bytes = await read as byte[];
            }
            catch (Exception ex)
            {
                // GetDataAsync wraps whatever the provider threw in an InvalidOperationException,
                // so the inner one is the interesting half and Logger.Error keeps the whole chain.
                Logger.Error("paste: reading " + format + " off the clipboard failed", ex);

                await ShowPopupAsync(Strings.UnsupportedAttachment, Strings.AppName, Strings.OK);
                return;
            }

            if (bytes == null || bytes.Length == 0)
            {
                Logger.Error("paste: the clipboard answered " + format + " with no bytes");

                await ShowPopupAsync(Strings.UnsupportedAttachment, Strings.AppName, Strings.OK);
                return;
            }

            if (bytes.Length > ClipboardImageSizeMax)
            {
                Logger.Error("paste: " + format + " is " + bytes.Length + " bytes, over the clipboard ceiling");

                await ShowPopupAsync(Strings.UnsupportedAttachment, Strings.AppName, Strings.OK);
                return;
            }

            var path = await WriteClipboardImageAsync(bytes, extension);
            if (path == null)
            {
                await ShowPopupAsync(Strings.UnsupportedAttachment, Strings.AppName, Strings.OK);
                return;
            }

            Logger.Info("paste: " + format + ", " + bytes.Length + " bytes -> " + path);

            await SendFilesAsync(new[] { path }, false);
        }

        /// <summary>
        /// Writes the pasted bytes into their own folder under the app's temporary folder and
        /// answers the path, or null when the write failed.
        /// </summary>
        /// <remarks>
        /// The file OUTLIVES this method on purpose: SendFilesAsync hands the path to TDLib, which
        /// reads it while the upload runs, and the confirmation window may sit open for minutes
        /// before that. Nothing here may delete it. What keeps the folder from growing without a
        /// bound is the prune below, which only ever touches files older than a day -- long past
        /// any upload still holding one.
        /// </remarks>
        private static async Task<string> WriteClipboardImageAsync(byte[] bytes, string extension)
        {
            try
            {
                var folder = IOPath.Combine(ApplicationData.Current.TemporaryFolder.Path, "paste");
                IODirectory.CreateDirectory(folder);

                PruneClipboardImages(folder);

                // Seconds are not enough: two Ctrl+V in the same second would collide, and
                // GenerateUniqueName is not available on a plain path. The name is also what the
                // confirmation window shows and what the other end sees when the image travels as
                // a file, so it says where it came from rather than being a random string.
                var name = "clipboard_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + extension;
                var path = IOPath.Combine(folder, name);

                await IOFile.WriteAllBytesAsync(path, bytes);
                return path;
            }
            catch (Exception ex)
            {
                Logger.Error("paste: could not write the pasted image to the temporary folder", ex);
                return null;
            }
        }

        /// <summary>
        /// Drops pasted images left behind by earlier sessions. Best effort: a file that cannot be
        /// deleted is skipped, never reported.
        /// </summary>
        private static void PruneClipboardImages(string folder)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-1);

                foreach (var path in IODirectory.EnumerateFiles(folder, "clipboard_*"))
                {
                    try
                    {
                        if (IOFile.GetLastWriteTimeUtc(path) < cutoff)
                        {
                            IOFile.Delete(path);
                        }
                    }
                    catch
                    {
                        // In use, or gone between the listing and the delete. Either way there is
                        // nothing to do and nothing worth a line in the log.
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }
}
