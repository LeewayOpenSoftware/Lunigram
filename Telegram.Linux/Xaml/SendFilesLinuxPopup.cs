//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// PARIDAD A3 — la ventana de confirmacion de adjuntos, reducida.
//
// EL SUSTITUTO DE `SendFilesPopup`, Y LO QUE NO ES. Upstream abre
// `Telegram/Views/Popups/SendFilesPopup.xaml(.cs)`: 2.000+ lineas con rejilla de miniaturas,
// reordenacion por arrastre, recorte, `EditMediaPopup`, spoiler, calidad alta, autodestruccion,
// media de pago, album y programacion. Esa pantalla arrastra `StorageMedia`/`StoragePhoto`/
// `StorageVideo`, `ImageHelper`, `MessageFactory` y `GenerationService`, que son
// MediaFoundation/Win2D de punta a punta y NO estan en el subconjunto Linux.
//
// Lo que si hace falta —y es lo unico que este popup promete— es que **enviar un fichero nunca
// ocurra sin que el usuario lo vea y lo confirme**. La ficha A3 dice que hoy el port acepta el
// fichero arrastrado y lo descarta; el arreglo NO puede ser el contrario (aceptarlo y mandarlo
// solo), porque un soltar accidental sobre una conversacion real es una escritura irreversible.
// De ahi que esta ventana sea obligatoria en los dos caminos, el clip y el arrastre.
//
// CADA FILA DICE COMO VA A SALIR. La columna de la derecha de cada fichero es «Foto» o «Archivo»,
// calculada ya por quien abre la ventana (DialogViewModel.Attach.Linux.cs), no una promesa: si
// una imagen no se puede medir, o pesa mas de 10 MB, o es un GIF animado, la fila dice «Archivo»
// ANTES de que se pulse Enviar. Los videos dicen siempre «Archivo»: este port no sabe medir la
// duracion ni las dimensiones de un MP4 y `inputMessageVideo` las necesita, asi que un video sale
// como fichero adjunto integro en vez de como video reproducible. Queda declarado en FALTA.
//
// LA CASILLA «Enviar como archivo» solo aparece cuando hay al menos una fila que iria como foto:
// si todo va a salir como archivo ya, una casilla que no cambia nada seria justo el boton inerte
// que esta tanda viene a quitar.
//
// TRAMPA DE UNO QUE ESTE FICHERO ESQUIVA (PORTING.md §6, fase 6): un `TextBox` con
// `AcceptsReturn = false` **recorta `Text` a su primera linea en el acto**. La caja de pie de foto
// nace con `AcceptsReturn = true` por eso, no por gusto: un pie de dos lineas puesto por el
// llamante se perderia entero al asignarlo. Y al leerlo, Uno guarda el salto como '\r', que TDLib
// tira (`case '\r': // skip`, MessageEntity.cpp:4217): la normalizacion a '\n' se hace aqui, antes
// de construir el FormattedText.
//
using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Converters;
using Telegram.Td.Api;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Views.Popups
{
    /// <summary>
    /// One file about to be sent, already classified. Built by
    /// <c>DialogViewModel.SendFilesAsync</c>; the popup only reads it.
    /// </summary>
    public partial class SendFileEntry
    {
        public string Path { get; set; }

        public string Name { get; set; }

        public long Size { get; set; }

        /// <summary>
        /// True when the file will be sent as <c>inputMessagePhoto</c>. False means
        /// <c>inputMessageDocument</c> — which is what every video, audio, archive and
        /// unmeasurable image gets.
        /// </summary>
        public bool IsPhoto { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        /// <summary>
        /// Why this one is not going as a photo, when it is not. Shown to nobody: it goes to the
        /// log, so that a file that arrived as an attachment can be explained afterwards.
        /// </summary>
        public string Reason { get; set; }

        public string Describe(bool forceDocument)
        {
            var kind = IsPhoto && !forceDocument
                ? Strings.AttachPhoto
                : Strings.ChatDocument;

            return string.Format("{0}  ·  {1}  ·  {2}", Name, FileSizeConverter.Convert(Size, true), kind);
        }
    }

    public sealed partial class SendFilesLinuxPopup : ContentPopup
    {
        private readonly IList<SendFileEntry> _files;
        private readonly TextBox _caption;
        private readonly CheckBox _asFile;
        private readonly IList<TextBlock> _rows = new List<TextBlock>();

        // A drop can carry a whole folder's worth of files. The list is a plain StackPanel, not a
        // virtualized list, so it is capped and the rest is counted.
        private const int MaxRows = 12;

        public SendFilesLinuxPopup(IList<SendFileEntry> files, FormattedText caption)
        {
            _files = files;

            Title = files.All(x => x.IsPhoto)
                ? string.Format(Strings.SendItems, Locale.Declension(Strings.R.Photos, files.Count))
                : string.Format(Strings.SendItems, Locale.Declension(Strings.R.Files, files.Count));

            PrimaryButtonText = Strings.Send;
            SecondaryButtonText = Strings.Cancel;
            DefaultButton = ContentDialogButton.Primary;

            var panel = new StackPanel
            {
                Width = 360
            };

            foreach (var file in files.Take(MaxRows))
            {
                var row = new TextBlock
                {
                    Text = file.Describe(false),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 0, 2)
                };

                _rows.Add(row);
                panel.Children.Add(row);
            }

            if (files.Count > MaxRows)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = string.Format("+{0}", files.Count - MaxRows),
                    Opacity = 0.6,
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }

            // Only offered when it would change something: with no photo in the list every row
            // already says «Archivo».
            if (files.Any(x => x.IsPhoto))
            {
                _asFile = new CheckBox
                {
                    Name = "SendAsFileCheckBox",
                    Content = Strings.SendAsFile,
                    Margin = new Thickness(0, 8, 0, 0)
                };

                _asFile.Checked += OnSendAsFileChanged;
                _asFile.Unchecked += OnSendAsFileChanged;

                panel.Children.Add(_asFile);
            }

            _caption = new TextBox
            {
                Name = "SendFilesCaption",
                PlaceholderText = Strings.AddCaption,
                // See the header: lowering this after the text is in would eat every line but the
                // first, so it is raised before anything is written.
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 96,
                Margin = new Thickness(0, 12, 0, 0)
            };

            if (caption != null && !string.IsNullOrEmpty(caption.Text))
            {
                _caption.Text = caption.Text;
                _caption.SelectionStart = _caption.Text.Length;
            }

            panel.Children.Add(_caption);

            Content = panel;

            // u-charrecv-b: upstream's SendFilesPopup focuses its caption box from OnLoaded
            // (SendFilesPopup.xaml.cs, OnLoaded) and this reduced replacement never did, so the
            // window opened with NOTHING focused and typing a caption went nowhere until you
            // clicked the box. That file is not in the Linux subset, so the CharacterReceived
            // bridge the other two views needed does not apply here -- upstream's own focus call
            // is the whole fix, and a keyboard tunnel to make up for a missing Focus() would be
            // the wrong shape.
            Loaded += OnLoadedFocusCaption;
        }

        private void OnLoadedFocusCaption(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoadedFocusCaption;

            _caption.Focus(FocusState.Keyboard);
            _caption.SelectionStart = _caption.Text.Length;
        }

        private void OnSendAsFileChanged(object sender, RoutedEventArgs e)
        {
            var forceDocument = SendAsFile;

            for (int i = 0; i < _rows.Count && i < _files.Count; i++)
            {
                _rows[i].Text = _files[i].Describe(forceDocument);
            }
        }

        /// <summary>
        /// True when every item must go as <c>inputMessageDocument</c>, photos included.
        /// </summary>
        public bool SendAsFile => _asFile?.IsChecked == true;

        /// <summary>
        /// The caption as typed, with Uno's '\r' line breaks turned into the '\n' TDLib keeps.
        /// Never null; empty text means no caption.
        /// </summary>
        public FormattedText Caption
        {
            get
            {
                var text = _caption?.Text ?? string.Empty;
                text = text.Replace('\v', '\n').Replace('\r', '\n');

                return new FormattedText(text, Array.Empty<TextEntity>());
            }
        }
    }
}
