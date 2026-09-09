//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// PARIDAD A1/§1.13/§1.21, CC-3 (2026-09-04) — REENVIAR, AHORA SOBRE EL SELECTOR ENTERO.
//
// Hasta CC-1/CC-2 el camino era el de upstream (DialogViewModel.Messages.cs:539, :587, :597)
// con UNA sustitucion: el selector era `ChooseChatsLinuxPopup`, reducido, porque en su momento
// traer `Views/Popups/ChooseChatsPopup.xaml` completo obligaba a traer el cajon de emoji y el
// editor rico (el XAML DECLARA `EmojiDrawer`/`CaptionTextBox` y el XAML no tiene preprocesador).
// Esa cabecera, con la medida completa, se conserva en Telegram.Linux/Xaml/ChooseChatsLinuxPopup.cs
// history (git log) para quien la busque.
//
// CC-1 resolvio esa parte: `ChooseChatsPopup.xaml.cs` ya esta portado entero, usando
// `CaptionTextBox` (TextBox llano, Telegram.Linux/Xaml/Stubs/CaptionTextBox.cs) en vez de
// `FormattedTextBox` para el compositor del comentario, y `EmojiDrawer` ya estaba en el
// subconjunto (u-064). CC-2 desbloqueo 9 consumidores mas de `ChooseChatsPopup`. Esta tanda hace
// lo mismo para Reenviar: el cuerpo es AHORA el de upstream, literal
// (DialogViewModel.Messages.cs:597-616), usando `ChooseChatsConfigurationShareMessages` — la
// misma rama, sin `#if !LINUX` alguno, que ya usa `Profile/MediaTabsViewModelBase.cs` (rama
// Windows) para el Reenviar de la galeria de medios.
//
// LO QUE ESTO DEVUELVE, frente al selector reducido (todo lo que su cabecera declaraba que NO
// hacia): comentario junto al reenvio (CaptionInput/EmojiPanel, ya en la pantalla), «enviar como
// copia» / «quitar el pie» (menu contextual del boton Enviar, `Send_ContextRequested`), temas de
// foro (SendWithChat recibe `topic` de verdad, no `null` fijo), busqueda dentro del selector y
// carpetas como pestanas — todo ello viene GRATIS de haber traido el popup entero, no hay que
// reimplementar nada de eso aqui.
//
// LO QUE SIGUE SIN ENTRAR, porque el picker mismo no lo trae en NINGUNA plataforma para este
// caso: enviar como otro perfil (no aplica a mensajes ajenos) y programar el envio (el picker no
// expone `messageSendOptions` para ShareMessages; upstream tampoco lo hace aqui).
//
// `LoadForwardTargetsAsync`/`ShowForwardedToast`/`ForwardChatCandidateLimit` (el selector
// reducido no era buscable, asi que tenia que precargar y acotar la lista de chats el mismo) ya
// no hacen falta: `ChooseChatsViewModel` carga y filtra su propia lista
// (`Options = ChooseChatsOptions.PostMessages`, el MISMO filtro de elegibilidad que ya se usaba
// aqui) y ya muestra su propio toast (`ShowForwardMessagesToast`, dentro de `SendExecute`).
//
// LA PRUEBA DE QUE ESTO FUNCIONA NO ES QUE SE ABRA LA VENTANA: es la respuesta de TDLib a
// `forwardMessages`. Esta maquina no puede iniciar sesion, asi que aqui se llega a «compila y
// carga» y la comprobacion es de la Surface.

using System.Collections.Generic;
using System.Linq;
using Telegram.Td.Api;
using Telegram.Views.Popups;

namespace Telegram.ViewModels
{
    public partial class DialogViewModel
    {
        public void ForwardMessage(MessageViewModel message)
        {
            if (message == null)
            {
                return;
            }

            IsSelectionEnabled = false;

            if (message.Content is MessageAlbum album)
            {
                ForwardMessagesLinux(album.Messages.ToDictionary(x => new MessageId(x)));
            }
            else
            {
                ForwardMessagesLinux(new[] { message }.ToDictionary(x => new MessageId(x)));
            }
        }

        public void ForwardSelectedMessages()
        {
            var selectedItems = SelectedItems.Values
                .DistinctBy(x => x.Id)
                .ToDictionary(x => new MessageId(x));

            IsSelectionEnabled = false;
            ForwardMessagesLinux(selectedItems);
        }

        private async void ForwardMessagesLinux(Dictionary<MessageId, MessageViewModel> selectedItems)
        {
            if (selectedItems == null || selectedItems.Count == 0)
            {
                return;
            }

            var properties = await ClientService.GetMessagePropertiesAsync(selectedItems.Select(x => x.Key));

            var messages = properties.Where(x => x.Value.CanBeForwarded).OrderBy(x => x.Key.Id).ToList();
            if (messages.Count == 0)
            {
                return;
            }

            var messagesToShare = new List<MessageToShare>(messages.Count);

            foreach (var property in messages)
            {
                if (selectedItems.TryGetValue(property.Key, out var message))
                {
                    messagesToShare.Add(new MessageToShare(message, property.Value));
                }
            }

            await ShowPopupAsync(new ChooseChatsPopup(), new ChooseChatsConfigurationShareMessages(messagesToShare));
            TextField?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }
    }
}
