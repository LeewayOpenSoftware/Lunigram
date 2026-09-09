//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// PARIDAD A3 — EL CLIP DE ADJUNTAR Y EL ARRASTRE DE FICHEROS.
//
// EL DEFECTO QUE SE CIERRA AQUI, con las tres piezas que lo formaban:
//   1. `ChatView.Attach_Click` estaba ENTERO bajo `#if !LINUX`, mientras
//      `ChatView.UpdateComposerHeader` le escribia `ButtonAttach.IsEnabled = true` y le cambiaba
//      el glifo: el clip se pintaba, tenia hover, aceptaba el clic y no abria nada.
//   2. `ChatView.OnDragOver` es codigo COMPARTIDO sin `#if` y contestaba
//      `DataPackageOperation.Copy` a cualquier arrastre.
//   3. `ChatView.OnDrop` tenia su unica linea bajo `#if !LINUX` y este
//      `HandlePackageAsync` devolvia `Task.CompletedTask`.
//   El backend X11 de Uno completa el XDND igualmente (`X11DragDropExtension.ProcessXdndDrop`
//   responde `XdndFinished`), asi que **el gestor de ficheros veia una entrega correcta y el
//   fichero no llegaba a ninguna parte**. El usuario creia que lo habia mandado.
//
// DE DONDE SALEN LOS FICHEROS EN LINUX:
//   * El clip: `Windows.Storage.Pickers.FileOpenPicker` -> `LinuxFilePickerExtension` ->
//     xdg-desktop-portal (`org.freedesktop.portal.FileChooser.OpenFile`). Es el mismo camino que
//     ya usan `Platform/ProfilePhotoService.cs` y `Stories/StoryComposer.cs`.
//     `PickMultipleFilesAsync` esta implementado (pasa `multiple = true` al portal), asi que el
//     clip admite varios ficheros de una vez.
//   * El arrastre: `X11ClipboardExtension.FillDataPackage` mete un `SetStorageItems(...)` cuando
//     la fuente ofrece `text/uri-list`, y `ProcessUriList` **solo** devuelve entradas `file:` que
//     existen en disco, resueltas a `StorageFile` con su `Path` real. O sea que un `IStorageItem`
//     que llega aqui es un fichero local de verdad, no una promesa que haya que materializar.
//     OJO: `ProcessUriList` filtra por `file:`, asi que arrastrar un ENLACE de un navegador deja
//     `StorageItems` presente y VACIO; por eso el orden de abajo es «si hay ficheros, ficheros;
//     si no, texto».
//
// LO QUE ESTE CAMINO SI HACE, y es lo unico que promete:
//   - JPEG y PNG -> `inputMessagePhoto`, con las dimensiones medidas con SkiaSharp (`SKCodec`).
//     Solo esos dos: upstream nunca manda el fichero que le dan (lo recomprime a JPEG con
//     `ConversionType.Compress`) y aqui no hay recompresor, asi que lo que hay en disco es lo que
//     sube. Un WebP, un HEIC o un GIF animado salen como fichero.
//   - cualquier otra cosa -> `inputMessageDocument` con `disable_content_type_detection = true`,
//     que es lo que upstream pone en `MessageFactory.CreateDocumentAsync`. El fichero llega
//     integro y con su nombre.
//   - un mensaje por fichero, en orden, con el pie en el ultimo (la regla por item de upstream).
//
// LO QUE NO HACE, declarado a proposito para que nadie lo de por hecho al leer el metodo:
//   - **VIDEO COMO VIDEO.** `inputMessageVideo` necesita duracion, ancho y alto, y este port no
//     tiene con que medir un MP4 (`ImageHelper`/`MediaTranscoder`/`VideoGeneration` son
//     MediaFoundation y no estan en el subconjunto). Un video sale como fichero adjunto, y la
//     ventana de confirmacion lo dice fila a fila antes de enviar.
//   - **ALBUM.** `sendMessageAlbum` existe (`CreateSendMessageAlbum`), pero agrupar necesita la
//     clasificacion de `GetItemsView`, que vive en la mitad `#if !LINUX` de ComposeViewModel y se
//     apoya en `StorageMedia`. Van como mensajes sueltos.
//   - **RECOMPRIMIR, RECORTAR, SPOILER, CALIDAD ALTA, AUTODESTRUCCION, MEDIA DE PAGO,
//     PROGRAMAR.** Todo eso es `SendFilesPopup` + `MessageFactory` + `GenerationService`.
//   - **EDITAR EL MEDIO DE UN MENSAJE.** `EditMedia`/`EditDocument`/`EditCurrent` acaban en
//     `EditMediaAsync` -> `SendFilesPopup`. Por eso, mientras hay un mensaje en edicion,
//     `ChatView.UpdateComposerHeader` **apaga** el clip en Linux y `CanHandlePackage` **rechaza**
//     el arrastre: soltar un fichero encima de una edicion mandaria un mensaje NUEVO, que es
//     justo lo que el usuario no ha pedido.
//   - ~~PEGAR UNA IMAGEN DEL PORTAPAPELES~~ **YA SE HACE (u-050), y esta nota se equivocaba de
//     conclusion, no de dato.** `FillDataPackage` sigue sin llamar a `SetBitmap`, asi que
//     `StandardDataFormats.Bitmap` efectivamente no existe en este backend -- pero la MISMA
//     funcion registra un proveedor de datos por CADA atomo que anuncia el portapapeles, asi que
//     `image/png` esta en `AvailableFormats` y `GetDataAsync` devuelve sus bytes. Esos bytes van a
//     un temporal y entran por `SendFilesAsync`, o sea por esta misma puerta y con esta misma
//     ventana de confirmacion. `StorageMedia.CreateFromBitmapAsync` sigue fuera del subconjunto y
//     no hace falta. Ver `DialogViewModel.Paste.Linux.cs` y `Xaml/ChatTextBoxPaste.cs`.
//   - **CAMARA, UBICACION, LISTA DE TAREAS, CONTACTO, MUSICA, ARTICULO, REGALO y los
//     bots del menu de adjuntos.** Sus destinos (`CameraCaptureUI`, `SendLocationPopup`,
//     `SendAudiosPopup`, `ChooseChatsPopup`, `OpenMiniApp`) estan fuera del
//     subconjunto o son metodos vacios. No se dibujan: el menu de Linux tiene TRES entradas
//     (Foto, Documento, Encuesta; esta ultima portada en u-polls con CreatePollPopup).
//
// NADA SALE SIN CONFIRMACION. Los dos caminos pasan por `SendFilesLinuxPopup`, con el nombre, el
// tamano y el tipo de cada fichero y un pie de foto editable. Un soltar accidental sobre una
// conversacion real es una escritura irreversible, y upstream tampoco envia sin pasar por
// `SendFilesPopup`.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;
using Telegram.Common;
using Telegram.Converters;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.Views.Popups;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace Telegram.ViewModels
{
    public partial class DialogViewModel
    {
        // What the «Foto» entry of the clip offers in the portal dialog. Wider than what can
        // actually travel as a photo on purpose: anything here that cannot goes as a file, and the
        // confirmation window says so per row, which beats a picker that hides the file.
        private static readonly string[] _imageTypes = new[]
        {
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif"
        };

        // And what may become an `inputMessagePhoto`. It is SHORTER than upstream's
        // Constants.PhotoTypes, and the reason is the missing step: upstream never sends the file
        // it was given -- MessageFactory.CreatePhotoAsync runs it through ConversionType.Compress
        // and what reaches Telegram is always a JPEG. This head has no encoder in the subset, so
        // the bytes on disk are the bytes that go up, and only these two families are accepted by
        // Telegram's photo pipeline verbatim. A WebP, a HEIC, a BMP or an animated GIF sent as a
        // photo would come back as an error the user cannot act on; sent as a file they arrive.
        private static readonly string[] _photoTypes = new[]
        {
            ".jpg", ".jpeg", ".png"
        };

        // inputPhoto: "The photo must be at most 10 MB in size" (td_api.tl:5750). Upstream
        // recompresses to fit; this head cannot, so a bigger image is sent whole, as a file.
        private const long PhotoSizeMax = 10L << 20;

        // Same two ceilings ComposeViewModel.SendFilesAsync checks on Windows.
        private const long UploadSizeMax = 4000L << 20;
        private const long UploadSizeMaxFree = 2000L << 20;

        #region Attachment menu

        /// <summary>
        /// The «Foto» entry of the attach menu. Rights are checked per item inside
        /// <see cref="SendFilesAsync"/>, exactly as upstream's SendMedia does.
        /// </summary>
        public async void SendMedia()
        {
            var files = await PickFilesAsync(PickerLocationId.PicturesLibrary, _imageTypes);
            await SendFilesAsync(files, false);
        }

        /// <summary>
        /// The «Archivo» entry of the attach menu.
        /// </summary>
        public async void SendDocument()
        {
            var restricted = await VerifyRightsAsync(x => x.CanSendDocuments,
                Strings.ErrorSendRestrictedDocumentsAll,
                Strings.ErrorSendRestrictedDocuments,
                Strings.ErrorSendRestrictedDocuments);
            if (restricted)
            {
                return;
            }

            var files = await PickFilesAsync(PickerLocationId.DocumentsLibrary, new[] { "*" });
            await SendFilesAsync(files, true);
        }

        private static async Task<IReadOnlyList<string>> PickFilesAsync(PickerLocationId location, IEnumerable<string> types)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = location;

                foreach (var type in types)
                {
                    picker.FileTypeFilter.Add(type);
                }

                var files = await picker.PickMultipleFilesAsync();
                if (files == null)
                {
                    return Array.Empty<string>();
                }

                return files
                    .Where(x => x != null && !string.IsNullOrEmpty(x.Path))
                    .Select(x => x.Path)
                    .ToArray();
            }
            catch (Exception ex)
            {
                // The portal can be missing, refuse, or time out; LinuxFilePickerExtension already
                // answers an empty list for all of those, so this only catches the unexpected.
                Logger.Error(ex);
                return Array.Empty<string>();
            }
        }

        #endregion

        #region Drag & drop

        /// <summary>
        /// Whether a drag hovering the chat is worth accepting. Called from
        /// <c>ChatView.OnDragOver</c>: whatever answers false here gets
        /// <c>DataPackageOperation.None</c>, so the file manager shows «no drop» instead of
        /// completing an XDND that would go nowhere.
        /// </summary>
        public bool CanHandlePackage(DataPackageView package)
        {
            if (package == null || Chat == null)
            {
                return false;
            }

            // Replacing the media of a message being edited needs SendFilesPopup, which is out of
            // the subset. Accepting here would send a NEW message instead of editing.
            if (ComposerHeader?.Editing != null)
            {
                return false;
            }

            try
            {
                // Text has nowhere to land without a composer (a channel one cannot post to draws
                // no text box), so it only counts when there is one.
                return package.Contains(StandardDataFormats.StorageItems)
                    || (package.Contains(StandardDataFormats.Text) && TextField != null);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return false;
            }
        }

        /// <summary>
        /// A dropped (or shared) package. Files win over text: a file manager puts both
        /// <c>text/uri-list</c> and <c>text/plain</c> on the same drag.
        /// </summary>
        public async Task HandlePackageAsync(DataPackageView package)
        {
            if (package == null || Chat == null)
            {
                return;
            }

            if (ComposerHeader?.Editing != null)
            {
                Logger.Info("attach: package dropped while editing a message, ignored");
                return;
            }

            try
            {
                var files = await GetPackageFilesAsync(package);
                if (files.Count > 0)
                {
                    await SendFilesAsync(files, false);
                    return;
                }

                if (package.Contains(StandardDataFormats.Text))
                {
                    var text = await package.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        InsertDroppedText(text);
                        return;
                    }
                }

                // Folders, or a format this head cannot turn into a message. CanHandlePackage let
                // the drag through because StorageItems was there; only now is it known to be
                // empty of files.
                Logger.Info("attach: nothing sendable in the dropped package, formats: "
                    + string.Join(", ", package.AvailableFormats));

                await ShowPopupAsync(Strings.UnsupportedAttachment, Strings.AppName, Strings.OK);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private static async Task<IReadOnlyList<string>> GetPackageFilesAsync(DataPackageView package)
        {
            if (!package.Contains(StandardDataFormats.StorageItems))
            {
                return Array.Empty<string>();
            }

            var items = await package.GetStorageItemsAsync();
            if (items == null)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();

            foreach (var item in items)
            {
                // ProcessUriList answers a StorageFolder for a directory: skipped, there is no
                // «send a folder» in Telegram.
                if (item is StorageFile file && !string.IsNullOrEmpty(file.Path) && IOFile.Exists(file.Path))
                {
                    paths.Add(file.Path);
                }
            }

            return paths;
        }

        /// <summary>
        /// Text dropped on the chat goes into the composer at the caret, as it does on Windows.
        /// </summary>
        /// <remarks>
        /// Upstream writes it with <c>field.Document.GetRange(...).SetText(...)</c>; on this head
        /// the composer is a plain TextBox and its <c>ChatTextDocument</c> is a shell. SetText is
        /// the one that knows about Uno's AcceptsReturn trap, so the whole value goes through it.
        /// </remarks>
        private void InsertDroppedText(string text)
        {
            var field = TextField;
            if (field == null)
            {
                return;
            }

            var current = field.Text ?? string.Empty;
            var at = Math.Clamp(field.SelectionStart, 0, current.Length);

            SetText(current.Insert(at, text), null, true);
        }

        #endregion

        #region Sending

        /// <summary>
        /// The single door every attached file goes through, from the clip and from a drop alike.
        /// </summary>
        /// <param name="paths">Absolute paths of files that already exist on disk.</param>
        /// <param name="forceDocument">
        /// True when the caller already said «as a file» (the «Archivo» menu entry). The
        /// confirmation window can still raise it, never lower it.
        /// </param>
        public async Task SendFilesAsync(IReadOnlyList<string> paths, bool forceDocument)
        {
            if (Chat is not Chat chat || paths == null || paths.Count == 0)
            {
                return;
            }

            if (ComposerHeader?.Editing != null)
            {
                return;
            }

            var entries = new List<SendFileEntry>();

            foreach (var path in paths)
            {
                var entry = Classify(path, forceDocument);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0)
            {
                return;
            }

            // Upload ceiling first: it is the only one that cannot be worked around by sending as
            // a file. ShowLimitReached only writes a log line on this head (TLNavigationService,
            // Linux half), so the message is shown here instead of being swallowed.
            var ceiling = IsPremium ? UploadSizeMax : UploadSizeMaxFree;
            var tooBig = entries.FirstOrDefault(x => x.Size > ceiling);

            if (tooBig != null)
            {
                Logger.Info(string.Format("attach: {0} is {1} bytes, over the {2} ceiling", tooBig.Name, tooBig.Size, ceiling));

                var message = string.Format(Strings.LimitReachedFileSizePremium, FileSizeConverter.Convert(ceiling, true));
                await ShowPopupAsync(ClientEx.ParseMarkdown(message), Strings.AppName, Strings.OK);
                return;
            }

            if (await VerifyAttachmentRightsAsync(chat, entries, forceDocument))
            {
                return;
            }

            // Same trade as upstream: the composer text becomes the caption and the box is left
            // empty, and it is put back if the window is dismissed.
            var formattedText = GetFormattedText(true, false);
            var caption = formattedText.Substring(0, ClientService.Options.MessageCaptionLengthMax);

            var popup = new SendFilesLinuxPopup(entries, caption);

            var confirm = await ShowPopupAsync(popup);
            if (confirm != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                SetFormattedText(formattedText);
                return;
            }

            forceDocument |= popup.SendAsFile;

            // The checkbox can turn every photo into a file, and a chat may allow photos and
            // forbid documents, so the rights are asked again with the final shape.
            if (await VerifyAttachmentRightsAsync(chat, entries, forceDocument))
            {
                SetFormattedText(formattedText);
                return;
            }

            caption = popup.Caption.Substring(0, ClientService.Options.MessageCaptionLengthMax);

            var options = await PickMessageSendOptionsAsync(entries.Count);
            if (options == null)
            {
                // Cannot happen on this head (the Linux override never answers null), but the
                // composer text is already in hand and dropping it would be a silent loss.
                SetFormattedText(formattedText);
                return;
            }

            var reply = GetReply(true);

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                // Upstream's per-item rule: the caption travels with the last message.
                var itemCaption = i < entries.Count - 1 ? null : caption;
                var content = CreateContent(entry, itemCaption, forceDocument);

                Logger.Info(string.Format("attach: sending {0} ({1} bytes) as {2}{3}",
                    entry.Name, entry.Size, content.GetType().Name,
                    entry.Reason != null ? " — " + entry.Reason : string.Empty));

                await SendMessageAsync(reply, content, options);

                // The reply belongs to the first message only.
                reply = null;
            }

            TextField?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }

        /// <summary>
        /// True when something in <paramref name="entries"/> is not allowed here, having already
        /// told the user which one. Mirrors the Validating local function of upstream's
        /// SendFilesAsync, minus the media kinds this head never produces.
        /// </summary>
        private async Task<bool> VerifyAttachmentRightsAsync(Chat chat, IList<SendFileEntry> entries, bool forceDocument)
        {
            var permissions = ClientService.GetPermissions(chat, out bool restricted);

            var photos = entries.Any(x => x.IsPhoto && !forceDocument);
            var documents = entries.Any(x => !x.IsPhoto || forceDocument);

            if (photos && !permissions.CanSendPhotos)
            {
                await ShowPopupAsync(restricted ? Strings.ErrorSendRestrictedPhoto : Strings.ErrorSendRestrictedPhotoAll, Strings.AppName, Strings.OK);
                return true;
            }

            if (documents && !permissions.CanSendDocuments)
            {
                await ShowPopupAsync(restricted ? Strings.ErrorSendRestrictedDocuments : Strings.ErrorSendRestrictedDocumentsAll, Strings.AppName, Strings.OK);
                return true;
            }

            return false;
        }

        private static InputMessageContent CreateContent(SendFileEntry entry, FormattedText caption, bool forceDocument)
        {
            if (entry.IsPhoto && !forceDocument)
            {
                return new InputMessagePhoto(
                    new InputPhoto(new InputFileLocal(entry.Path), null, null, Array.Empty<int>(), entry.Width, entry.Height),
                    caption, false, null, false);
            }

            // disable_content_type_detection = true is what upstream's CreateDocumentAsync ends
            // with, and it is what makes the row of the confirmation window true: a file said
            // «Archivo» and a file is what arrives.
            return new InputMessageDocument(
                new InputDocument(new InputFileLocal(entry.Path), null, true),
                caption);
        }

        #endregion

        #region Classification

        /// <summary>
        /// Decides, before anything is sent and before the window is shown, whether a path can go
        /// as a photo. Null means the file is gone or unreadable.
        /// </summary>
        private static SendFileEntry Classify(string path, bool forceDocument)
        {
            try
            {
                var info = new System.IO.FileInfo(path);
                if (!info.Exists)
                {
                    Logger.Error("attach: " + path + " does not exist any more");
                    return null;
                }

                var entry = new SendFileEntry
                {
                    Path = path,
                    Name = IOPath.GetFileName(path),
                    Size = info.Length
                };

                if (forceDocument)
                {
                    return entry;
                }

                var extension = IOPath.GetExtension(path);
                if (string.IsNullOrEmpty(extension) || !_photoTypes.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    entry.Reason = "not a photo extension";
                    return entry;
                }

                if (info.Length > PhotoSizeMax)
                {
                    entry.Reason = "over the 10 MB photo ceiling";
                    return entry;
                }

                if (TryMeasure(path, out int width, out int height, out string reason))
                {
                    entry.IsPhoto = true;
                    entry.Width = width;
                    entry.Height = height;
                }
                else
                {
                    entry.Reason = reason;
                }

                return entry;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return null;
            }
        }

        /// <summary>
        /// The pixel size TDLib needs for <c>inputPhoto</c>, read from the header only — SKCodec
        /// does not decode the image to answer <c>Info</c>.
        /// </summary>
        /// <remarks>
        /// The three ceilings are the ones <c>get_input_photo_size</c> refuses outright
        /// (<c>Libraries/tdlib/td/telegram/PhotoSize.cpp:423-432</c>): 10000 per side and 10000
        /// for the sum. Everything this answers false to is still sent — as a file.
        /// </remarks>
        private static bool TryMeasure(string path, out int width, out int height, out string reason)
        {
            width = 0;
            height = 0;
            reason = null;

            try
            {
                using var codec = SKCodec.Create(path);
                if (codec == null)
                {
                    reason = "Skia cannot decode it";
                    return false;
                }

                // An animated PNG would arrive as a still frame if it were sent as a photo, and
                // inputMessageAnimation needs a duration this head cannot measure. It goes as a
                // file, which keeps every frame.
                if (codec.FrameCount > 1)
                {
                    reason = string.Format("animated, {0} frames", codec.FrameCount);
                    return false;
                }

                var info = codec.Info;
                var w = info.Width;
                var h = info.Height;

                // EXIF orientations 5 to 8 turn the image on its side, so the sides the other end
                // will see are swapped.
                if (codec.EncodedOrigin is SKEncodedOrigin.LeftTop
                    or SKEncodedOrigin.RightTop
                    or SKEncodedOrigin.RightBottom
                    or SKEncodedOrigin.LeftBottom)
                {
                    (w, h) = (h, w);
                }

                if (w <= 0 || h <= 0)
                {
                    reason = "no readable size";
                    return false;
                }

                if (w > 10000 || h > 10000 || w + h > 10000)
                {
                    reason = string.Format("{0}x{1} is over what inputPhoto accepts", w, h);
                    return false;
                }

                width = w;
                height = h;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        #endregion
    }
}
