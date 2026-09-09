//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// PARIDAD A3 — el menu del clip, mitad Linux.
//
// POR QUE ESTA AQUI Y NO EN EL `#else` DE `ChatView.xaml.cs`: ese fichero es compartido y hay
// varias tandas escribiendo en el a la vez (A1 el menu del mensaje, A4 el «⋮» de la cabecera).
// Lo que queda alli son tres lineas y un `#else` que llama a este metodo.
//
// EL MENU DE WINDOWS TIENE HASTA NUEVE ENTRADAS mas los bots del menu de adjuntos
// (`ChatView.Attach_Click`, rama `#if !LINUX`). Este tiene TRES (Foto, Documento, Encuesta), y la lista de las que faltan no es
// una omision: cada una de las otras acaba en una pantalla que no se compila en este subconjunto
// —`CameraCaptureUI`, `SendLocationPopup`, `CreateChecklistPopup`,
// `SendAudiosPopup`, `ChooseChatsPopup.PickUserAsync`, el editor de articulos, la tienda de
// regalos y el contenedor de mini-apps— o en un metodo vacio (`OpenMiniApp`, `RemoveMiniApp`).
// Dibujarlas seria exactamente lo que esta tanda viene a quitar: una entrada de menu que no hace
// nada. El detalle de cada una esta en DialogViewModel.Attach.Linux.cs.
//
// Y CUANDO NO SE PUEDE ENVIAR NADA: upstream no muestra el menu (`if (flyout.Items.Count > 0)`) y
// el clic se queda sin respuesta. Aqui no: se pide el permiso por el camino normal, que enseña el
// motivo («no puedes enviar fotos en este grupo»). Es la misma pregunta que ya haria el envio, un
// paso antes.
//
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Telegram.Views
{
    public sealed partial class ChatView
    {
        private async void AttachLinux_Click()
        {
            var viewModel = ViewModel;
            if (viewModel?.Chat is not Chat chat)
            {
                return;
            }

            // Replacing the media of a message being edited needs SendFilesPopup. The button is
            // disabled while editing (UpdateComposerHeader, rama #if LINUX), so this is a
            // backstop, not the usual path.
            if (viewModel.ComposerHeader?.Editing != null)
            {
                return;
            }

            // VerifyRights answers TRUE when the right is missing, which is why upstream negates
            // it into a «…Rights» local.
            var photoRights = !viewModel.VerifyRights(chat, x => x.CanSendPhotos);
            var documentRights = !viewModel.VerifyRights(chat, x => x.CanSendDocuments);
            var pollRights = !viewModel.VerifyRights(chat, x => x.CanSendPolls);

            var pollsAllowed = chat.Type is ChatTypeSupergroup or ChatTypeBasicGroup;
            if (!pollsAllowed && viewModel.ClientService.TryGetUser(chat, out User user))
            {
                pollsAllowed = user.Type is UserTypeBot || user.Id == viewModel.ClientService.Options.MyId;
            }

            if (!photoRights && !documentRights && !(pollRights && pollsAllowed))
            {
                // Nothing to show. Ask for the one that covers most of what the clip is for, so
                // the click ends in an explanation instead of in silence.
                await viewModel.VerifyRightsAsync(x => x.CanSendDocuments,
                    Strings.ErrorSendRestrictedDocumentsAll,
                    Strings.ErrorSendRestrictedDocuments,
                    Strings.ErrorSendRestrictedDocuments);
                return;
            }

            var flyout = new MenuFlyout();

            if (photoRights)
            {
                // «Foto», not upstream's «Foto o vídeo»: a video goes out as a file on this head
                // (no way to measure an MP4), so the entry does not promise one. The picker still
                // offers WebP, HEIC and GIF; those become files, and the confirmation window says
                // so on their row before anything is sent.
                flyout.CreateFlyoutItem(viewModel.SendMedia, Strings.AttachPhoto, Icons.Image);
            }

            if (documentRights)
            {
                flyout.CreateFlyoutItem(viewModel.SendDocument, Strings.ChatDocument, Icons.Document);
            }

            if (pollRights && pollsAllowed)
            {
                flyout.CreateFlyoutItem(viewModel.SendPoll, Strings.Poll, Icons.Poll);
            }

            flyout.ShowAt(ButtonAttach, FlyoutPlacementMode.TopEdgeAlignedLeft);
        }
    }
}
