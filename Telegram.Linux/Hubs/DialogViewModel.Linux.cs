//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux half of DialogViewModel. DialogViewModel.Media.cs and DialogViewModel.Messages.cs
// (attachments, pickers, context-menu commands) stay out of the Phase 1 subset; this partial
// supplies the members that the rest of the subset still references.
//
// «Con conducta inerte» ya NO describe este fichero, y esa es la diferencia que hay que leer
// antes de tocarlo: lo que se hace CON un mensaje -- responder, borrar, seleccionar, copiar,
// traducir, abrir un boton en linea, abrir un mensaje de servicio -- se hace de verdad aqui.
// Lo que sigue vacio esta vacio A PROPOSITO y cada uno lleva escrito, en su propia region, QUE
// pantalla de upstream le falta y por que no se trae media. Resumen, para no tener que leer el
// fichero entero:
//
//   VIVOS   ReplyToMessage, DeleteMessage, TryDeleteMessage, DeleteSelectedMessages,
//           SelectMessage, UnselectMessages, CopyMessage, CopySelectedMessages,
//           TranslateMessage, OpenInlineButton, ExecuteServiceMessage, PinMessage (u-055),
//           ForwardMessage / ForwardSelectedMessages (CC-3 trajo ChooseChatsPopup),
//           ReportMessage / ReportSelectedMessages (u-059 trajo ReportChatPopup),
//           KeyboardButtonExecute (cuerpo de upstream; sigue SIN puerta dibujada, ver A10),
//           AddToContacts y ViewSticker (parcela 3).
//   VACIOS  OpenMiniApp / RemoveMiniApp (el contenedor de mini-apps, y sin llamante vivo).
//   MUDADO  HandlePackageAsync, que era `return Task.CompletedTask`, esta VIVO y vive ahora en
//           DialogViewModel.Attach.Linux.cs, junto al clip de adjuntar (PARIDAD A3).
//
// ESTA LISTA SE QUEDO VIEJA UNA VEZ Y NADIE LO NOTO: las tandas que llenaron ForwardMessage,
// ReportSelectedMessages y KeyboardButtonExecute escribieron el cuerpo y dejaron la cabecera
// diciendo VACIO. Lo encontro la auditoria de paridad L10 leyendo el codigo, no una compilacion:
// un comentario mentiroso no da error, y este en concreto es el que decide que entradas dibuja
// el menu contextual, asi que mentia sobre lo unico que este fichero le pide a quien lo lee.
// Quien llene otro de estos, que mueva tambien su linea de aqui.
//
// REGLA para la tanda que construya el menu contextual del mensaje (PARIDAD A1): una entrada de
// menu por cada metodo VIVO, y NI UNA por cada metodo VACIO. Un boton que se dibuja y no hace
// nada es peor que la ausencia del boton.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Converters;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels.Chats;
using Telegram.Views.Popups;
using Telegram.Views.Users;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.ViewModels
{
    public partial class DialogViewModel
    {
        // HandlePackageAsync ya NO esta aqui, y no es un traslado cosmetico: era
        // `return Task.CompletedTask`, o sea el fichero que se arrastraba al chat se aceptaba y se
        // tiraba (PARIDAD A3). Vive, con el clip de adjuntar y el camino de soltar, en
        // Telegram.Linux/Hubs/DialogViewModel.Attach.Linux.cs.

        // «Ver pack» del menu contextual de un sticker en el cajon (ChatView.Drawers.cs:33, fichero
        // que SI se compila aqui): la entrada estaba dibujada y el metodo vacio, o sea el clic
        // derecho ofrecia abrir el paquete y no abria nada. Cuerpo literal de upstream
        // (DialogViewModel.Media.cs:32). Los dos miembros que usa ya estaban vivos y solos: este
        // fichero llama a Delegate.HideStickers unas lineas mas abajo, y OpenSticker
        // (DialogViewModel.Delegate.cs:255) dejo de estar guardado en u-073, cuando StickersPopup
        // entro en el subconjunto -- este era el ultimo llamante que faltaba por conectar.
        public override void ViewSticker(Sticker sticker)
        {
            Delegate?.HideStickers();

            OpenSticker(sticker);
        }

        protected override void HideStickers()
        {
            Delegate?.HideStickers();
        }

        protected override void SetFormattedText(FormattedText text)
        {
            SetText(text);
        }

        protected override bool CanSchedule => Type is DialogType.History or DialogType.Thread;

        public override Task<MessageSendOptions> PickMessageSendOptionsAsync(int messageCount = 1, SchedulingState schedule = SchedulingState.Auto, bool? disableNotification = null, bool reorder = false)
        {
            var options = new MessageSendOptions();
            options.DisableNotification = disableNotification ?? false;
            options.UpdateOrderOfInstalledStickerSets = reorder;

            return Task.FromResult(options);
        }

        // Settable: DialogViewModel.Delegate.cs recomputes them on every selection change.
        private bool _canDeleteSelectedMessages;
        public bool CanDeleteSelectedMessages
        {
            get => _canDeleteSelectedMessages;
            set => Set(ref _canDeleteSelectedMessages, value);
        }

        private bool _canForwardSelectedMessages;
        public bool CanForwardSelectedMessages
        {
            get => _canForwardSelectedMessages;
            set => Set(ref _canForwardSelectedMessages, value);
        }

        // u-059: era `=> false` porque ReportChatPopup estaba fuera del subconjunto (ver la region
        // «Denunciar» mas abajo). Cuerpo literal de upstream
        // (DialogViewModel.Messages.cs:881-895).
        public bool CanReportSelectedMessages
        {
            get
            {
                var chat = Chat;
                if (chat == null)
                {
                    return false;
                }

                var myId = ClientService.Options.MyId;
                return chat.CanBeReported && SelectedItems.Count > 0
                    && SelectedItems.Values.All(x => x.SenderId is MessageSenderChat || (x.SenderId is MessageSenderUser senderUser && senderUser.UserId != myId));
            }
        }

        public bool CanCopySelectedMessage => SelectedItems.Count > 0;

        #region Responder

        // PARIDAD A2 / M15, 2026-08-27. Este metodo estaba VACIO y era el unico camino que
        // quedaba para responder a un mensaje: el doble clic de fabrica (IsQuickReplySelected,
        // que viene encendido) llega a ChatHistoryView.cs:1327, pone e.Handled = true y acababa
        // aqui, en un cuerpo vacio. Ctrl+doble clic, en cambio, cae en ReactToMessage y SI
        // funcionaba: la combinacion por defecto era la unica muerta.
        //
        // El cuerpo es el de upstream (Telegram/ViewModels/DialogViewModel.Messages.cs:168), y
        // no hace falta ningun fichero nuevo: ComposerHeader, MessageComposerHeader,
        // MessageComposerReplyTo, GetReply, DisposeSearch y TextField ya estan en
        // DialogViewModel.cs, y quien lo dibuja -- ChatView.UpdateComposerHeader ->
        // ComposerHeaderReference (Controls/Messages/MessageReply.xaml) -- se compila entero y
        // sin ninguna guarda.
        //
        // La rama «responder en OTRO chat» SI entra desde CC-2 (antes quedaba en una linea de
        // log porque ChooseChatsPopup era una pantalla entera sin portar; CC-1 la porto). El
        // cuerpo de abajo la abre cuando el equivalente inline de ShouldReplyInAnotherChatAsync
        // contesta que si (un canal, o un tema cerrado, en el que no eres creador ni
        // administrador) — el detalle esta comentado en el propio sitio.
        //
        // Lo que SIGUE fuera: ReplyToMessageInAnotherChat / QuoteToMessageInAnotherChat como
        // comandos con nombre no existen en esta cabeza, y el ultimo parametro de
        // MessageComposerReplyTo (CanBeRepliedInAnotherChat) va a false: su unico lector es el
        // menu contextual de la cabecera del compositor (ChatView.xaml.cs:3423), que ofrece
        // precisamente «Responder en otro chat» y aqui no esta cableado.
        public async void ReplyToMessage(MessageViewModel message)
        {
            DisposeSearch();

            if (message == null)
            {
                return;
            }

            if (message.Content is MessageAlbum album)
            {
                message = album.Messages.FirstOrDefault();
            }

            if (message == null)
            {
                return;
            }

            var properties = await ClientService.SendAsync(new GetMessageProperties(message.ChatId, message.Id)) as MessageProperties;

            var chat = message.Chat;
            if (chat != null && ClientService.TryGetSupergroup(chat, out Supergroup supergroup))
            {
                if ((supergroup.IsChannel || ForumTopic?.Info.IsClosed is true)
                    && supergroup.Status is not ChatMemberStatusCreator and not ChatMemberStatusAdministrator
                    && properties?.CanBeRepliedInAnotherChat is true)
                {
                    // CC-2: CC-1 ported ChooseChatsPopup and kept ChooseChatsConfigurationReplyToMessage
                    // live. Body reused from upstream's ReplyToMessage(message, inAnotherChat)
                    // (DialogViewModel.Messages.cs, not itself in the Linux subset -- this file's
                    // simplified inline condition above stands in for its
                    // ShouldReplyInAnotherChatAsync, which is not compiled here).
                    var header = ComposerHeader;
                    var text = GetFormattedText(true, false);

                    GetReply(true);

                    var confirm = await ShowPopupAsync(new ChooseChatsPopup(), new ChooseChatsConfigurationReplyToMessage(message));
                    if (confirm != ContentDialogResult.Primary)
                    {
                        ComposerHeader = header;
                        SetFormattedText(text);
                    }

                    return;
                }
            }

            ComposerHeader = new MessageComposerHeader(ClientService)
            {
                ReplyTo = new MessageComposerReplyTo(message, null, 0, string.Empty, false)
            };

            TextField?.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
        }

        #endregion

        #region Reenviar -- COLAPSADO, con motivo

        // PARIDAD A1/§1.13, 2026-08-27. Los dos metodos de reenvio siguen vacios A PROPOSITO y
        // esta es la declaracion, no un olvido.
        //
        // Reenviar es, de punta a punta, «elige a quien». Upstream lo resuelve con
        // ChooseChatsPopup + ChooseChatsConfigurationShareMessages: 2.021 lineas de code-behind,
        // 314 de XAML, ChooseChatsViewModel (1.108) y MessageToShare, mas SearchChatsCollection y
        // el resto del selector. Es una pantalla, y traer media pantalla es exactamente lo que no
        // sirve: un selector a medias manda mensajes a personas equivocadas, y eso no se deshace.
        //
        // Mientras no este esa pantalla:
        //   - CanForwardSelectedMessages se queda en false (DialogViewModel.Delegate.cs, rama
        //     #if LINUX), o sea que el boton «Reenviar» de la barra de seleccion multiple sale
        //     GRIS en vez de habilitado-y-mudo, que es como estaba antes de §1.13.
        //   - La entrada «Reenviar» NO debe ponerse en el menu contextual del mensaje cuando esa
        //     tanda lo construya. Es la mitad del encargo: primero la accion, luego la puerta.
        //
        // ---------------------------------------------------------------------------------------
        // RESUELTO el 2026-09-02 (PARIDAD §1.21). Los dos metodos YA NO ESTAN VACIOS y ya no viven
        // aqui: estan en Telegram.Linux/Hubs/DialogViewModel.Forward.Linux.cs. En ese momento, con
        // el selector reducido Telegram.Linux/Xaml/ChooseChatsLinuxPopup.cs.
        //
        // El parrafo de arriba se conserva porque su prediccion resulto ser MEDIO cierta y la otra
        // mitad falsa, y las dos mitades valen: traer media pantalla efectivamente no servia --
        // pero la pantalla no habia que traerla entera, habia que traer la PARTE QUE ELIGE. Medido:
        // anadir ChooseChatsViewModel + ChooseChatsPopup.xaml da 16 errores y solo TRES tipos, los
        // tres del compositor del comentario (FormattedTextBox, EmojiDrawerItemClickEventArgs) y
        // del editor de carpetas (ChatFolderElement). Y el XAML de upstream DECLARA esos controles,
        // asi que no hay `#if` que los aparte. El filtro de elegibilidad -- lo unico que de verdad
        // podia mandar un mensaje a quien no toca -- NO se reescribio: es
        // ChooseChatsOptions.PostMessages.Allow, el de upstream, que ya se compilaba.
        //
        // Consecuencias de aquel parrafo, las dos revertidas: CanForwardSelectedMessages vuelve a
        // calcularse, y la entrada «Reenviar» YA entra en el menu contextual.
        //
        // CC-3 (2026-09-04): CC-1 trajo `ChooseChatsPopup.xaml.cs` ENTERO (sustituyendo
        // FormattedTextBox por CaptionTextBox, un TextBox llano, para el compositor del
        // comentario; EmojiDrawer ya estaba en el subconjunto desde u-064). Con eso ya resuelto, el
        // selector reducido dejo de ser necesario: Forward.Linux.cs ahora llama al mismo
        // `ChooseChatsPopup` + `ChooseChatsConfigurationShareMessages` que usa upstream, y
        // ChooseChatsLinuxPopup.cs se retiro (git log lo conserva). El comentario, remove-caption,
        // temas de foro y busqueda dentro del selector, que el parrafo de arriba listaba como
        // ausentes, vienen todos incluidos -- no eran huecos propios de Reenviar, eran huecos del
        // picker reducido.

        #endregion

        #region Borrar

        // PARIDAD A1, 2026-08-27. DeleteMessage y DeleteSelectedMessages estaban vacios, y
        // CanDeleteSelectedMessages contestaba false desde §1.13 para que el boton de la papelera
        // de la barra de seleccion multiple no mintiera. Ahora borran de verdad.
        //
        // QUE SE TRAE Y QUE NO. El cuerpo es el de upstream
        // (Telegram/ViewModels/DialogViewModel.Messages.cs:408, :449 y :557) con UNA sustitucion,
        // declarada:
        //
        //   Upstream confirma con DeleteMessagesPopup (504 + 260 lineas). Ese popup no entra, y
        //   no por su tamano: su DataContext es SupergroupEditRestrictedViewModel (555 lineas),
        //   que a su vez necesita SupergroupEditMemberArgs, declarado dentro de
        //   SupergroupEditAdministratorPopup.xaml(.cs) -- el editor de permisos de un miembro,
        //   que ademas arrastra SupergroupEditAdministratorViewModel, IMemberPopupDelegate y
        //   ScrollViewerScrim real. Nada de eso lo alcanza hoy ninguna otra pantalla del port.
        //
        //   La sustitucion es MessagePopup, que ya se compila y cuya cadena de cierre esta
        //   arreglada y medida (PORTING.md, «ContentPopup: ARREGLADO»). Se copian LITERALES el
        //   titulo, el texto y la casilla «eliminar tambien para X» de la rama BasicRoot del
        //   popup de upstream (DeleteMessagesPopup.xaml.cs:105-193), que es la que sale en un
        //   chat privado, en un grupo basico, en un supergrupo y en un canal.
        //
        //   LO QUE LA SUSTITUCION NO OFRECE, y son las tres casillas de la rama AdditionalRoot:
        //   «denunciar spam», «borrar todo lo de este usuario» y «expulsar / restringir». Solo
        //   aparecen siendo administrador de un supergrupo con mensajes de otros seleccionados, y
        //   las tres son acciones ADICIONALES sobre terceros: no ofrecerlas no rompe el borrado,
        //   deja de ofrecer un extra. Cuando entre el editor de permisos, sustituir este bloque
        //   por `new DeleteMessagesPopup(...)` es un cambio de diez lineas.
        //
        // DialogType.WelcomeMessages tampoco entra: upstream borra con DeleteChatWelcomeMessage y
        // esa funcion NO EXISTE en el TDLib vendorizado (cero ocurrencias en TdDotNetApi.g.cs).
        // Se sale sin tocar nada y se anota.

        public void DeleteMessage(MessageViewModel message)
        {
            if (message == null)
            {
                return;
            }

            var chat = message.Chat;
            if (chat == null)
            {
                return;
            }

            if (message.Content is MessageAlbum album)
            {
                DeleteMessages(chat, album.Messages);
            }
            else
            {
                DeleteMessages(chat, new[] { message });
            }

            TextField?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        }

        // El camino de la tecla Supr sobre una burbuja enfocada (ChatView.OnPreviewKeyDown). Hasta
        // ahora ese `else` de Supr se quedaba con un `_ = selector;` porque este metodo vivia en
        // DialogViewModel.Messages.cs.
        public async void TryDeleteMessage(MessageViewModel message)
        {
            if (message == null)
            {
                return;
            }

            var properties = await ClientService.SendAsync(new GetMessageProperties(message.ChatId, message.Id)) as MessageProperties;
            if (properties == null || (!properties.CanBeDeletedOnlyForSelf && !properties.CanBeDeletedForAllUsers))
            {
                return;
            }

            DeleteMessage(message);
        }

        public void DeleteSelectedMessages()
        {
            var messages = new List<MessageViewModel>(SelectedItems.Values);

            var first = messages.FirstOrDefault();
            if (first == null)
            {
                return;
            }

            var chat = first.Chat;
            if (chat == null)
            {
                return;
            }

            DeleteMessages(chat, messages);
        }

        private async void DeleteMessages(Chat chat, IList<MessageViewModel> messages)
        {
            var first = messages.FirstOrDefault();
            if (first == null)
            {
                return;
            }

            if (Type is DialogType.WelcomeMessages)
            {
                // deleteChatWelcomeMessage no esta en el esquema de TDLib vendorizado.
                Logger.Info("DeleteMessages: DialogType.WelcomeMessages is not supported on Linux (deleteChatWelcomeMessage is not in the vendored TDLib schema)");
                return;
            }

            var items = messages
                .DistinctBy(x => x.Id)
                .ToList<MessageWithOwner>();

            IDictionary<MessageId, MessageProperties> properties;
            if (Type is DialogType.BusinessReplies)
            {
                properties = items.ToDictionary(x => new MessageId(x), y => new MessageProperties
                {
                    CanBeDeletedForAllUsers = true,
                    CanBeDeletedOnlyForSelf = false
                });
            }
            else
            {
                properties = await ClientService.GetMessagePropertiesAsync(items.Select(x => new MessageId(x)));
            }

            var updated = items
                .Where(x => properties.ContainsKey(new MessageId(x)))
                .ToList();

            if (updated.Empty())
            {
                return;
            }

            var popup = CreateDeleteMessagesPopup(chat, updated, properties);

            var confirm = await ShowPopupAsync(popup);
            if (confirm != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                return;
            }

            IsSelectionEnabled = false;

            if (Type == DialogType.BusinessReplies)
            {
                ClientService.Send(new DeleteQuickReplyShortcutMessages(QuickReplyShortcut.Id, messages.Select(x => x.Id).ToVector()));
                return;
            }

            ClientService.Send(new DeleteMessages(chat.Id, messages.Select(x => x.Id).ToVector(), popup.IsChecked == true));
        }

        // Traduccion literal de la rama BasicRoot de DeleteMessagesPopup (upstream
        // Telegram/Views/Popups/DeleteMessagesPopup.xaml.cs:105-193) a un MessagePopup. Las
        // cadenas, el orden de las condiciones y el valor inicial de la casilla son los mismos;
        // lo unico que cambia es el control que los dibuja.
        private Telegram.Controls.MessagePopup CreateDeleteMessagesPopup(Chat chat, IList<MessageWithOwner> messages, IDictionary<MessageId, MessageProperties> properties)
        {
            var savedMessages = TopicId is MessageTopicSavedMessages;

            var popup = new Telegram.Controls.MessagePopup
            {
                Title = messages.Count == 1
                    ? !savedMessages ? Strings.DeleteSingleMessagesTitle : Strings.UnsaveSingleMessagesTitle
                    : string.Format(!savedMessages ? Strings.DeleteMessagesTitle : Strings.UnsaveMessagesTitle, Locale.Declension(Strings.R.messages, messages.Count)),
                PrimaryButtonText = !savedMessages ? Strings.Delete : Strings.Remove,
                SecondaryButtonText = Strings.Cancel
            };

            var mapped = messages.ToDictionary(x => new MessageId(x));
            var scheduled = messages.Any(x => x.SchedulingState != null);

            var canBeDeletedForAllUsers = properties.Values.All(x => x.CanBeDeletedForAllUsers) && !scheduled;
            var anyCanBeDeletedForAllUsers = properties.Any(x => mapped[x.Key].IsOutgoing && x.Value.CanBeDeletedForAllUsers) && !scheduled;

            if (savedMessages)
            {
                popup.Message = messages.Count == 1
                    ? Strings.AreYouSureUnsaveSingleMessage
                    : Strings.AreYouSureUnsaveFewMessages;
            }
            else if (chat.Type is ChatTypePrivate or ChatTypeBasicGroup)
            {
                if (anyCanBeDeletedForAllUsers && !canBeDeletedForAllUsers)
                {
                    Telegram.Td.Api.User user = null;
                    popup.Message = chat.Type is ChatTypePrivate && ClientService.TryGetUser(chat, out user)
                        ? string.Format(Strings.DeleteMessagesText, Locale.Declension(Strings.R.messages, messages.Count), user.FirstName)
                        : string.Format(Strings.DeleteMessagesTextGroup, Locale.Declension(Strings.R.messages, messages.Count));

                    if (user?.Type is not UserTypeBot)
                    {
                        popup.CheckBoxLabel = Strings.DeleteMessagesOption;
                        popup.IsChecked = true;
                    }
                }
                else
                {
                    var user = ClientService.GetUser(chat);
                    popup.Message = messages.Count == 1
                        ? Strings.AreYouSureDeleteSingleMessage
                        : Strings.AreYouSureDeleteFewMessages;

                    if (canBeDeletedForAllUsers && user?.Type is not UserTypeBot)
                    {
                        popup.CheckBoxLabel = chat.Type is ChatTypePrivate && user != null
                            ? string.Format(Strings.DeleteMessagesOptionAlso, user.FirstName)
                            : Strings.DeleteForAll;
                        popup.IsChecked = true;
                    }
                }
            }
            else if (chat.Type is ChatTypeSupergroup super && !super.IsChannel)
            {
                popup.Message = messages.Count == 1
                    ? Strings.AreYouSureDeleteSingleMessageMega
                    : Strings.AreYouSureDeleteFewMessagesMega;
            }
            else
            {
                var now = DateTime.Now.ToTimestamp();
                var paid = messages.FirstOrDefault(x => (x.IsPaidStarSuggestedPost || x.IsPaidGramSuggestedPost) && now < (int)ClientService.Options.SuggestedPostLifetimeMin + x.GetDate());

                if (paid != null && paid.IsPaidStarSuggestedPost)
                {
                    popup.Title = Strings.SuggestionStarsWillBeLost;
                    popup.Message = string.Format(Strings.SuggestionStarsWillBeLostInfo, (ClientService.Options.SuggestedPostLifetimeMin / 3600.0).ToString("N0"));
                    popup.PrimaryButtonText = Strings.SuggestionStarsWillBeLostDelete;
                }
                else if (paid != null && paid.IsPaidGramSuggestedPost)
                {
                    popup.Title = Strings.SuggestionTONWillBeLost;
                    popup.Message = string.Format(Strings.SuggestionTONWillBeLostInfo, (ClientService.Options.SuggestedPostLifetimeMin / 3600.0).ToString("N0"));
                    popup.PrimaryButtonText = Strings.SuggestionStarsWillBeLostDelete;
                }
                else if (messages.Count == 1 && messages[0].Content is MessageGiveaway giveaway)
                {
                    popup.Title = Strings.BoostingGiveawayDeleteMsgTitle;
                    popup.Message = string.Format(Strings.BoostingGiveawayDeleteMsgText, Formatter.DateAt(giveaway.Parameters.WinnersSelectionDate));
                }
                else
                {
                    popup.Message = messages.Count == 1
                        ? Strings.AreYouSureDeleteSingleMessage
                        : Strings.AreYouSureDeleteFewMessages;
                }
            }

            return popup;
        }

        #endregion

        #region Fijar

        // u-055 / FALTA §1.3 «Fijar es un hueco aparte y sigue siendo real». PinMessage no vivia en
        // ningun partial de Linux -- ni vacio, ni con cuerpo: no existia. Sin el, la entrada «Fijar»
        // del menu contextual (ChatView.Menu.Linux.cs) no podia dibujarse aunque tuviera guarda,
        // porque la regla de ese fichero es una entrada de menu por cada metodo VIVO.
        //
        // Cuerpo literal de upstream (Telegram/ViewModels/DialogViewModel.Messages.cs:1186-1261),
        // con la misma sustitucion que ya uso Borrar arriba: el dialogo de confirmacion es
        // MessagePopup -- ya compilado, ya probado (CreateDeleteMessagesPopup, mas arriba) -- en vez
        // de un ContentDialog nuevo. Titulo, texto y casilla se copian tal cual: los cuatro mensajes
        // segun tipo de chat (canal, privado, privado con self, grupo), la advertencia de fijar por
        // encima de un mensaje mas nuevo ya fijado, y la casilla de «notificar» / «tambien para mi»
        // con su valor inicial. Nada de esto es nuevo: es la misma pieza, en el mismo control, que
        // ya se mide funcionando para Borrar.
        public async void PinMessage(MessageViewModel message)
        {
            if (message == null)
            {
                return;
            }

            var chat = message.Chat;
            if (chat == null)
            {
                return;
            }

            if (message.IsPinned)
            {
                var confirm = await ShowPopupAsync(Strings.UnpinMessageAlert, Strings.AppName, Strings.OK, Strings.Cancel);
                if (confirm == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    ClientService.Send(new UnpinChatMessage(chat.Id, message.Id));
                }

                return;
            }

            var channel = chat.Type is ChatTypeSupergroup super && super.IsChannel;
            var self = chat.Type is ChatTypePrivate privata && privata.UserId == ClientService.Options.MyId;

            var last = PinnedMessages.LastOrDefault();

            var popup = new Telegram.Controls.MessagePopup
            {
                Title = Strings.PinMessageAlertTitle,
                PrimaryButtonText = Strings.OK,
                SecondaryButtonText = Strings.Cancel
            };

            if (last != null && last.Id > message.Id)
            {
                popup.Message = Strings.PinOldMessageAlert;
            }
            else if (channel)
            {
                popup.Message = Strings.PinMessageAlertChannel;
            }
            else if (chat.Type is ChatTypePrivate)
            {
                popup.Message = Strings.PinMessageAlertChat;
            }
            else
            {
                popup.Message = Strings.PinMessageAlert;
            }

            if (chat.Type is ChatTypeBasicGroup || chat.Type is ChatTypeSupergroup && !channel)
            {
                popup.CheckBoxLabel = Strings.PinNotify;
                popup.IsChecked = true;
            }
            else if (chat.Type is ChatTypePrivate && !self)
            {
                popup.CheckBoxLabel = string.Format(Strings.PinAlsoFor, chat.Title);
                popup.IsChecked = false;
            }

            var result = await ShowPopupAsync(popup);
            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                return;
            }

            var disableNotification = false;
            var onlyForSelf = false;

            if (chat.Type is ChatTypeBasicGroup || chat.Type is ChatTypeSupergroup && !channel)
            {
                disableNotification = popup.IsChecked == false;
            }
            else if (chat.Type is ChatTypePrivate && !self)
            {
                onlyForSelf = popup.IsChecked == false;
            }

            ClientService.Send(new PinChatMessage(chat.Id, message.Id, disableNotification, onlyForSelf));
        }

        #endregion

        #region Seleccionar

        // PARIDAD A1, 2026-08-27. SelectMessage llamaba a Select(message) y NADA MAS: no entraba
        // en modo seleccion (IsSelectionEnabled se quedaba en false, con lo que la barra de
        // seleccion multiple no salia y el mensaje quedaba marcado sin que se viera donde), no
        // cerraba la busqueda y no agrupaba el album. El cuerpo es ahora el de upstream
        // (DialogViewModel.Messages.cs:901).
        public void SelectMessage(MessageViewModel message)
        {
            DisposeSearch();

            if (message.MediaAlbumId != 0 && _groupedMessages.TryGetValue(message.MediaAlbumId, out MessageViewModel group))
            {
                message = group;
            }

            Select(message);
            IsSelectionEnabled = true;
        }

        // Upstream es exactamente esta linea: el setter de IsSelectionEnabled pasa por
        // ShowHideSelection, que ya hace SelectedItems.Clear(). El Clear() de mas que habia aqui
        // vaciaba el diccionario ANTES, con lo que ShowHideSelection no tenia nada que limpiar y
        // -- peor -- si el modo ya estaba apagado la seleccion se borraba sin que nadie levantara
        // UpdateMessageSelection: las burbujas se quedaban pintadas como seleccionadas.
        public void UnselectMessages()
        {
            IsSelectionEnabled = false;
        }

        #endregion

        // CERRADO 2026-08-27. Estos dos metodos estaban vacios y los tres caminos que los
        // llaman ponian `args.Handled = true` igualmente (ChatView.OnPreviewKeyDown, rama Ctrl+C):
        // la tecla se consumia y el portapapeles se quedaba con lo que hubiera antes. No es una
        // carencia, es una respuesta equivocada. El cuerpo es el de upstream
        // (Telegram/ViewModels/DialogViewModel.Messages.cs:660 y :961) copiado tal cual, porque
        // todo lo que usa esta en el subconjunto: StringBuilder, ClientService, Formatter,
        // PhoneNumber, ClientEx.CustomEmoji y MessageHelper.CopyText, que es el mismo
        // ClipboardEx.TrySetContent que ya usa StorageService.CopyPath.
        // UNICA diferencia con Windows, y esta anotada donde ocurre: la rama MessageRichMessage de
        // CopyMessage necesita PageBlockHelper.GetFormattedText, que no esta en la reduccion Linux
        // de ese helper.

        public void CopySelectedMessages()
        {
            var messages = SelectedItems.Values.OrderBy(x => x.Id).ToList();
            if (messages.Count > 0)
            {
                var builder = new StringBuilder();
                IsSelectionEnabled = false;

                foreach (var message in messages)
                {
                    var chat = message.Chat;
                    var title = chat.Title;

                    if (ClientService.TryGetUser(message.SenderId, out Telegram.Td.Api.User senderUser))
                    {
                        title = senderUser.FullName();
                    }
                    else if (ClientService.TryGetChat(message.SenderId, out Chat senderChat))
                    {
                        title = ClientService.GetTitle(senderChat);
                    }

                    builder.AppendLine(string.Format("{0}, [{1} {2}]", title, Formatter.Date(message.Date), Formatter.Time(message.Date)));

                    if (message.ForwardInfo?.Origin is MessageOriginChat fromChat)
                    {
                        var from = ClientService.GetChat(fromChat.SenderChatId);
                        builder.AppendLine($"[{Strings.ForwardedMessage}]");
                        builder.AppendLine($"[{Strings.From} {ClientService.GetTitle(from)}]");
                    }
                    else if (message.ForwardInfo?.Origin is MessageOriginChannel forwardedPost)
                    {
                        var from = ClientService.GetChat(forwardedPost.ChatId);
                        builder.AppendLine($"[{Strings.ForwardedMessage}]");
                        builder.AppendLine($"[{Strings.From} {ClientService.GetTitle(from)}]");
                    }
                    else if (message.ForwardInfo?.Origin is MessageOriginUser forwardedFromUser)
                    {
                        var from = ClientService.GetUser(forwardedFromUser.SenderUserId);
                        builder.AppendLine($"[{Strings.ForwardedMessage}]");
                        builder.AppendLine($"[{Strings.From} {from.FullName()}]");
                    }
                    else if (message.ForwardInfo?.Origin is MessageOriginHiddenUser forwardedFromHiddenUser)
                    {
                        builder.AppendLine($"[{Strings.ForwardedMessage}]");
                        builder.AppendLine($"[{Strings.From} {forwardedFromHiddenUser.SenderName}]");
                    }
                    else if (message.ImportInfo != null)
                    {
                        builder.AppendLine($"[{Strings.ForwardedMessage}]");
                        builder.AppendLine($"[{Strings.From} {message.ImportInfo.SenderName}]");
                    }

                    if (message.ReplyToItem is MessageViewModel replyToMessage)
                    {
                        if (ClientService.TryGetUser(replyToMessage.SenderId, out Telegram.Td.Api.User replyUser))
                        {
                            builder.AppendLine($"[In reply to {replyUser.FullName()}]");
                        }
                        else if (ClientService.TryGetChat(replyToMessage.SenderId, out Chat replyChat))
                        {
                            builder.AppendLine($"[In reply to {replyChat.Title}]");
                        }
                    }
                    else if (message.ReplyToItem is Story replyToStory)
                    {
                        if (ClientService.TryGetUser(replyToStory.PosterChatId, out Telegram.Td.Api.User replyUser))
                        {
                            builder.AppendLine($"[In reply to {replyUser.FullName()}]");
                        }
                    }

                    if (message.Content is MessagePhoto photo)
                    {
                        builder.Append($"[{(photo.Video != null ? Strings.AttachLivePhoto : Strings.AttachPhoto)}]");

                        if (photo.Caption != null && !string.IsNullOrEmpty(photo.Caption.Text))
                        {
                            builder.AppendLine();
                            builder.AppendLine(photo.Caption.Text);
                        }
                    }
                    else if (message.Content is MessageVoiceNote voiceNote)
                    {
                        builder.Append($"[{Strings.AttachAudio}]");

                        if (voiceNote.Caption != null && !string.IsNullOrEmpty(voiceNote.Caption.Text))
                        {
                            builder.AppendLine();
                            builder.AppendLine(voiceNote.Caption.Text);
                        }
                    }
                    else if (message.Content is MessageVideo video)
                    {
                        builder.Append($"[{Strings.AttachVideo}]");

                        if (video.Caption != null && !string.IsNullOrEmpty(video.Caption.Text))
                        {
                            builder.AppendLine();
                            builder.AppendLine(video.Caption.Text);
                        }
                    }
                    else if (message.Content is MessageVideoNote)
                    {
                        builder.Append($"[{Strings.AttachRound}]");
                    }
                    else if (message.Content is MessageAnimation animation)
                    {
                        builder.Append($"[{Strings.AttachGif}]");

                        if (animation.Caption != null && !string.IsNullOrEmpty(animation.Caption.Text))
                        {
                            builder.AppendLine();
                            builder.AppendLine(animation.Caption.Text);
                        }
                    }
                    else if (message.Content is MessageSticker sticker)
                    {
                        if (!string.IsNullOrEmpty(sticker.Sticker.Emoji))
                        {
                            builder.AppendLine($"[{sticker.Sticker.Emoji} {Strings.AttachSticker}]");
                        }
                        else
                        {
                            builder.AppendLine($"[{Strings.AttachSticker}]");
                        }
                    }
                    else if (message.Content is MessageAudio audio)
                    {
                        builder.Append($"[{Strings.AttachMusic}]");

                        if (audio.Caption != null && !string.IsNullOrEmpty(audio.Caption.Text))
                        {
                            builder.AppendLine();
                            builder.AppendLine(audio.Caption.Text);
                        }
                    }
                    else if (message.Content is MessageLocation location)
                    {
                        builder.AppendLine($"[{Strings.AttachLocation}]");
                        builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "https://www.bing.com/maps/?pc=W8AP&FORM=MAPXSH&where1=44.312783,9.33426&locsearch=1", location.Location.Latitude, location.Location.Longitude));
                    }
                    else if (message.Content is MessageVenue venue)
                    {
                        builder.AppendLine($"[{Strings.AttachLocation}]");
                        builder.AppendLine(venue.Venue.Title);
                        builder.AppendLine(venue.Venue.Address);
                        builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "https://www.bing.com/maps/?pc=W8AP&FORM=MAPXSH&where1=44.312783,9.33426&locsearch=1", venue.Venue.Location.Latitude, venue.Venue.Location.Longitude));
                    }
                    else if (message.Content is MessageContact contact)
                    {
                        builder.AppendLine($"[{Strings.AttachContact}]");
                        builder.AppendLine(contact.Contact.GetFullName());
                        builder.AppendLine(PhoneNumber.Format(contact.Contact.PhoneNumber));
                    }
                    else if (message.Content is MessagePoll poll)
                    {
                        builder.AppendLine($"[{Strings.Poll}: {poll.Poll.Question.Text}]");

                        foreach (var option in poll.Poll.Options)
                        {
                            builder.AppendLine($"- {option.Text.Text}");
                        }
                    }
                    else if (message.Content is MessageChecklist checklist)
                    {
                        builder.AppendLine($"[{Strings.Todo}: {checklist.List.Title.Text}]");

                        foreach (var task in checklist.List.Tasks)
                        {
                            if (task.CompletionDate != 0)
                            {
                                builder.AppendLine($"\u2611 {task.Text.Text}");
                            }
                            else
                            {
                                builder.AppendLine($"\u2610 {task.Text.Text}");
                            }
                        }
                    }
                    else if (message.Content is MessageText text)
                    {
                        builder.AppendLine(text.Text.Text);
                    }

                    if (message != messages.Last())
                    {
                        builder.AppendLine();
                    }
                }

                MessageHelper.CopyText(XamlRoot, builder.ToString());
            }
        }

        public void CopyMessage(MessageViewModel message)
        {
            if (message == null)
            {
                return;
            }

            var input = message.GetCaption();
            if (message.Content is MessageContact contact)
            {
                input = PhoneNumber.Format(contact.Contact.PhoneNumber).AsFormattedText();
            }
            else if (message.Content is MessageAnimatedEmoji animatedEmoji)
            {
                if (animatedEmoji.AnimatedEmoji.Sticker?.FullType is StickerFullTypeCustomEmoji customEmoji)
                {
                    input = ClientEx.CustomEmoji(animatedEmoji.Emoji, customEmoji.CustomEmojiId);
                }
                else
                {
                    input = animatedEmoji.Emoji.AsFormattedText();
                }
            }
            // La rama `message.Content is MessageRichMessage` de upstream se queda fuera: resuelve
            // el texto con PageBlockHelper.GetFormattedText, y la reduccion de ese helper que este
            // port compila (Telegram.Linux/Hubs/PageBlockHelper.cs) solo trae GetRichText,
            // GetPlainText y GetLinks -- el fichero de upstream esta escrito contra un esquema de
            // TDLib mas nuevo que el vendorizado. Es el mensaje de vista instantanea, que este port
            // tampoco dibuja.

            if (input != null)
            {
                MessageHelper.CopyText(XamlRoot, input);
            }
        }

        #region Denunciar

        // u-059 / FALTA §1.3 «Denunciar». Estaba COLAPSADO a proposito porque ReportChatPopup (128
        // + 249 lineas) -- el arbol de motivos que TDLib devuelve por pasos
        // (ReportChatResultOptionRequired, ReportChatResultTextRequired,
        // ReportChatResultMessagesRequired) -- estaba fuera del subconjunto. Ya entro: sus dos
        // dependencias sin XAML propio (GlyphButton, TextListView/TextListViewItem) se anadieron al
        // csproj, y ScrollViewerScrim lo sigue resolviendo el mismo stub inerte que usan otros 20+
        // popups (Telegram.Linux/Xaml/ScrollViewerScrimStub.cs) -- no hizo falta tocarlo.
        //
        // La orquestacion (ReportAsync, ShowHideSelection, IsReportingMessages) YA estaba compilada
        // en DialogViewModel.cs, con su cuerpo entero bajo `#if !LINUX`: la unica pieza que faltaba
        // de verdad era el popup, asi que quitar esa guarda es todo lo que hizo falta alli.
        //
        // Cuerpo literal de upstream (DialogViewModel.Messages.cs:861-877 y :1267-1277).
        public async void ReportSelectedMessages()
        {
            var chat = Chat;
            if (chat == null)
            {
                return;
            }

            var myId = ClientService.Options.MyId;
            var messages = SelectedItems.Values
                .Where(x => x.SenderId is MessageSenderChat || (x.SenderId is MessageSenderUser senderUser && senderUser.UserId != myId))
                .OrderBy(x => x.Id).Select(x => x.Id).ToList();
            if (messages.Count < 1)
            {
                return;
            }

            await ReportAsync(messages.ToVector());
        }

        public async void ReportMessage(MessageViewModel message)
        {
            if (message.Content is MessageSponsored { CanBeReported: true })
            {
                ShowPopup(new ReportAdsPopup(this, ChatId, message.Id, null));
            }
            else
            {
                await ReportAsync(new[] { message.Id });
            }
        }

        #endregion

        #region Anadir a contactos

        // PARIDAD A1 / FALTA §1.3, parcela 3. Estuvo vacio desde 2026-08-27 y su comentario decia
        // por que: UserEditPage no estaba en el subconjunto. Ahora SI lo esta (Telegram.Linux.csproj
        // da de alta la pagina, su code-behind y UserEditViewModel; Session.Registrations.cs la
        // resuelve y App.ViewModelForPage le da su ViewModel), asi que el cuerpo es el de upstream
        // (DialogViewModel.Messages.cs:2166) sin una sola rama fuera.
        //
        // Lo que desbloqueo la pagina fue RE-3: el panel de la nota es un CaptionTextBox, y hasta
        // que ese twin existio la mitad util de esta pantalla -- anadir a alguien CON una nota --
        // no se podia dibujar.
        //
        // Este es el gemelo CON argumento: se alcanza desde el menu contextual de un mensaje de
        // CONTACTO, y su entrada de menu la pone ahora ChatView.Menu.Linux.cs con el mismo
        // predicado que MessageAddContact_Loaded (ChatView.xaml.cs:5397). El gemelo sin argumento
        // vive en DialogViewModel.cs:4334 y tambien perdio su guarda.
        public void AddToContacts(MessageViewModel message)
        {
            var contact = message.Content as MessageContact;
            if (contact == null)
            {
                return;
            }

            var user = ClientService.GetUser(contact.Contact.UserId);
            if (user == null)
            {
                return;
            }

            NavigationService.Navigate(typeof(UserEditPage), user.Id);
        }

        #endregion

        #region Traducir

        // PARIDAD A1, 2026-08-27. Este metodo estaba vacio. Ahora abre el mismo TranslatePopup que
        // Windows, y no hace falta ni un motor nuevo ni una pantalla: Services/TranslateService.cs
        // ya se compila (es el que mueve la barra de traduccion del chat), LanguageIdentification
        // tiene su implementacion Linux sobre libtextclassifier
        // (Telegram.Linux/Native/LanguageIdentification.cs) y MessageTextBlock, TextSelectionManager
        // y el ScrollViewerScrim inerte ya estan dentro. Lo unico que se da de alta son los dos
        // popups: Views/Popups/TranslatePopup.xaml(.cs) y Views/Popups/TranslateToPopup.xaml(.cs),
        // este segundo porque es el destino del enlace «cambiar idioma» que el primero dibuja --
        // dejarlo fuera seria justamente un enlace pintado y muerto.
        //
        // El cuerpo es el de upstream (DialogViewModel.Messages.cs:1352) con UNA rama fuera, la
        // misma que ya se declara en CopyMessage: `message.Content is MessageRichMessage`, el
        // mensaje de vista instantanea. Este port no lo dibuja (InstantContent es un cascaron) y
        // su traduccion pasa por PageBlockHelper.ToInputRichMessage, que no esta en la reduccion
        // Linux de ese helper. Cae por el `else` general, que usa GetTranslatableText: para un
        // MessageRichMessage eso da null y el metodo se sale sin abrir nada.
        public void TranslateMessage(MessageViewModel message)
        {
            FormattedText text;
            long chatId;
            long messageId;

            if (message.Content is MessagePoll poll)
            {
                var builder = new FormattedText(poll.Poll.Question.Text, poll.Poll.Question.Entities.ToVector());

                foreach (var option in poll.Poll.Options)
                {
                    builder = FormattedText.Concat(builder, "\n\U0001F518 ", option.Text);
                }

                text = builder;
                chatId = 0;
                messageId = 0;
            }
            else if (message.Content is MessageChecklist checklist)
            {
                var builder = new FormattedText(checklist.List.Title.Text, checklist.List.Title.Entities.ToVector());

                foreach (var task in checklist.List.Tasks)
                {
                    if (task.CompletionDate != 0)
                    {
                        builder = FormattedText.Concat(builder, "\n\u2611 ", task.Text);
                    }
                    else
                    {
                        builder = FormattedText.Concat(builder, "\n\u2610 ", task.Text);
                    }
                }

                text = builder;
                chatId = 0;
                messageId = 0;
            }
            else if (message.Content is MessageVoiceNote voiceNote && voiceNote.VoiceNote.SpeechRecognitionResult is SpeechRecognitionResultText speechVoiceText)
            {
                if (voiceNote.Caption.Text.Length > 0 && speechVoiceText.Text.Length > 0)
                {
                    text = ClientEx.Format("{0}\n{1}", speechVoiceText.Text, voiceNote.Caption.Text);
                }
                else if (speechVoiceText.Text.Length > 0)
                {
                    text = speechVoiceText.Text.AsFormattedText();
                }
                else
                {
                    return;
                }

                chatId = 0;
                messageId = 0;
            }
            else if (message.Content is MessageVideoNote videoNote && videoNote.VideoNote.SpeechRecognitionResult is SpeechRecognitionResultText speechVideoText)
            {
                if (speechVideoText.Text.Length > 0)
                {
                    text = speechVideoText.Text.AsFormattedText();
                }
                else
                {
                    return;
                }

                chatId = 0;
                messageId = 0;
            }
            else
            {
                var caption = message.GetTranslatableText();
                if (string.IsNullOrEmpty(caption?.Text))
                {
                    return;
                }

                text = caption;
                chatId = message.ChatId;
                messageId = message.Id;

                if (message.Content is MessageAlbum album && album.Messages.Count > 0)
                {
                    messageId = album.IsMedia
                        ? album.Messages[0].Id
                        : album.Messages[^1].Id;
                }
            }

            var language = Telegram.Native.LanguageIdentification.IdentifyLanguage(text.Text);
            var popup = new TranslatePopup(_translateService, chatId, messageId, text, language, Settings.Translate.To, !message.CanBeSaved);
            ShowPopup(popup);
        }

        #endregion

        #region Mini-apps -- COLAPSADAS, con motivo

        // PARIDAD §1.8 / A3, 2026-08-27. Los tres siguen vacios a proposito.
        //
        // OpenMiniApp acaba en NavigationService.NavigateToWebApp, o sea en el contenedor de
        // WebView2 de la mini-app: no hay ningun equivalente en esta cabeza, y por eso §1.8
        // colapso la pildora «Abrir» del bot en vez de dejarla pintada. Sus dos llamantes vivos
        // ya estan colapsados en el mismo sitio donde se dibujan.
        //
        // RemoveMiniApp SI seria implementable en cinco lineas (una confirmacion y
        // toggleBotIsAddedToAttachmentMenu), pero su UNICO llamante es la lista de bots del menu
        // del clip, y Attach_Click esta entero bajo #if !LINUX (A3). Implementarlo ahora seria
        // codigo sin ninguna puerta: entra cuando entre el menu del clip.
        public void OpenMiniApp(string url)
        {
        }

        public void OpenMiniApp(AttachmentMenuBot menuBot)
        {
        }

        public void RemoveMiniApp(AttachmentMenuBot bot)
        {
        }

        #endregion

        #region Teclado personalizado de un bot -- SIN LLAMANTE, medido

        // PARIDAD A10, 2026-08-27 -- Y LA FICHA PARTE DE UNA PREMISA QUE AQUI NO SE CUMPLE.
        //
        // A10 dice «los botones del teclado personalizado de un bot SE DIBUJAN y no hacen nada».
        // En esta cabeza no se dibujan: Telegram.Controls.ReplyMarkupPanel es un cascaron
        // u-066: Controls/ReplyMarkupPanel.cs entro al subconjunto (el stub que devolvia
        // Update()=false siempre ha sido borrado), asi que este metodo ya tiene quien lo llame de
        // verdad: ChatView.ReplyMarkup_ButtonClick, sin guarda desde esta misma tanda.
        //
        // El switch de upstream (DialogViewModel.Messages.cs:1863) tiene cinco ramas segun
        // KeyboardButton.Type. Dos entran de verdad, calcadas del original:
        //   KeyboardButtonTypeText: envia la etiqueta del boton como mensaje -- el caso mas
        //     comun con diferencia para bots de tipo menu.
        //   KeyboardButtonTypeRequestPhoneNumber: SendContactAsync con el contacto propio, tras
        //     el mismo popup de confirmacion que upstream. No pide selector: manda tu propia
        //     tarjeta, ya la tiene ClientService.
        //   KeyboardButtonTypeRequestPoll: SendPollAsync con CreatePollPopup (u-polls).
        // Las otras dos se quedan sin implementar, con motivo medido, no adivinado:
        //   KeyboardButtonTypeRequestLocation: Telegram.Linux/Hubs/LocationService.cs devuelve
        //     null SIEMPRE (Uno no tiene proveedor de geolocalizacion en X11, ya documentado ahi)
        //     -- mostrar el popup de confirmacion para que despues no pase nada seria peor que no
        //     mostrarlo.
        //   KeyboardButtonTypeWebApp: pide WebView2, que Uno Skia no implementa.
        // Las dos restantes no dejan un boton mudo por accidente: son subtipos raros dentro de
        // un control cuyo caso dominante (texto, contacto y encuesta) ya funciona.
        public async void KeyboardButtonExecute(MessageViewModel message, KeyboardButton keyboardButton)
        {
            if (message.ReplyMarkup is ReplyMarkupShowKeyboard { OneTime: true })
            {
                ClientService.Send(new DeleteChatReplyMarkup(message.ChatId, message.Id));
            }

            if (keyboardButton.Type is KeyboardButtonTypeRequestPoll requestPoll)
            {
                await SendPollAsync(false, requestPoll.ForceQuiz, requestPoll.ForceRegular, Chat?.Type is ChatTypeSupergroup super && super.IsChannel);
            }
            else if (keyboardButton.Type is KeyboardButtonTypeText)
            {
                var reply = Chat?.Type is ChatTypeSupergroup or ChatTypeBasicGroup
                    ? new InputMessageReplyToMessage(message.Id, null, 0, string.Empty)
                    : null;

                await SendMessageAsync(keyboardButton.Text, null, null, null, reply);
            }
            else if (keyboardButton.Type is KeyboardButtonTypeRequestPhoneNumber)
            {
                if (ClientService.TryGetUser(ClientService.Options.MyId, out User cached))
                {
                    var content = Strings.AreYouSureShareMyContactInfo;
                    if (Chat?.Type is ChatTypePrivate privata)
                    {
                        var withUser = ClientService.GetUser(privata.UserId);
                        if (withUser != null)
                        {
                            content = withUser.Type is UserTypeBot
                                ? Strings.AreYouSureShareMyContactInfoBot
                                : string.Format(Strings.AreYouSureShareMyContactInfoUser, PhoneNumber.Format(cached.PhoneNumber), withUser.FullName());
                        }
                    }

                    var confirm = await ShowPopupAsync(content, Strings.ShareYouPhoneNumberTitle, Strings.OK, Strings.Cancel);
                    if (confirm == ContentDialogResult.Primary)
                    {
                        await SendContactAsync(new Contact(cached.PhoneNumber, cached.FirstName, cached.LastName, string.Empty, cached.Id), null);
                    }
                }
            }
        }

        #endregion

        public async void OpenInlineButton(MessageViewModel message, InlineKeyboardButton inline)
        {
            if (inline.Type is InlineKeyboardButtonTypeUrl urlButton)
            {
                OpenUrl(urlButton.Url, true);
            }
            else if (inline.Type is InlineKeyboardButtonTypeCallback callback)
            {
                await ClientService.SendAsync(new GetCallbackQueryAnswer(message.ChatId, message.Id, new CallbackQueryPayloadData(callback.Data)));
            }
            // PARIDAD M5, 2026-08-27. Este es el boton «Iniciar sesion con Telegram» que un sitio
            // pone bajo un mensaje de su bot, y hasta ahora caia fuera de las dos ramas de arriba:
            // se dibujaba con su texto y no hacia absolutamente nada. La ficha M5 apuntaba al
            // gemelo de MessageHelper.OpenLoginUrl (el que abria la URL anonima), pero esta es la
            // puerta por la que se entra de verdad a esa funcion.
            //
            // El flujo es el de upstream (DialogViewModel.Messages.cs, fichero que NO entra en el
            // subconjunto) sin recortar el paso que importa: LoginUrlInfoPopup dice a que sitio se
            // entra, con que cuenta y si el bot podra escribirte, y la casilla de escritura viaja a
            // getLoginUrl. Ese popup ya se compila aqui desde esta misma tanda.
            //
            // Unica diferencia con upstream: se llama a OpenUrl(url, false) en vez de a
            // Launcher.LaunchUriAsync. Es el mismo destino (MessageHelper.OpenUrl con untrust en
            // false no pregunta nada y acaba en LaunchUriAsync), y de paso se queda con el
            // TryCreateUri que upstream escribe dos veces a mano.
            else if (inline.Type is InlineKeyboardButtonTypeLoginUrl loginUrl)
            {
                var response = await ClientService.SendAsync(new GetLoginUrlInfo(message.ChatId, message.Id, loginUrl.Id));
                if (response is LoginUrlInfoOpen infoOpen)
                {
                    OpenUrl(infoOpen.Url, !infoOpen.SkipConfirmation);
                }
                else if (response is LoginUrlInfoRequestConfirmation requestConfirmation)
                {
                    var popup = new LoginUrlInfoPopup(ClientService, requestConfirmation);

                    var confirm = await ShowPopupAsync(popup);
                    if (confirm != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                    {
                        return;
                    }

                    response = await ClientService.SendAsync(new GetLoginUrl(message.ChatId, message.Id, loginUrl.Id, popup.AllowWriteAccess));
                    if (response is HttpUrl httpUrl)
                    {
                        OpenUrl(httpUrl.Url, false);
                    }
                    else if (response is Error loginUrlError)
                    {
                        // Como en Windows: si el token no se puede pedir se abre la pagina sin el.
                        // Aqui ademas se anota, porque la diferencia entre entrar identificado y
                        // entrar como un desconocido no se ve desde fuera.
                        Logger.Error("getLoginUrl error " + loginUrlError);
                        OpenUrl(loginUrl.Url, false);
                    }
                }
            }
        }

        // PARIDAD M11, 2026-08-27. Este metodo estaba vacio y NO es una carencia sin consecuencia:
        // los mensajes de servicio que lo llaman dibujan un boton con su texto («Ver la foto»,
        // «Ver el video», la pildora de fecha del historial), o sea que anuncian que hay algo que
        // abrir. De las veinte ramas de upstream (Telegram/ViewModels/DialogViewModel.Messages.cs,
        // fichero que NO entra en el subconjunto) se traen SOLO aquellas cuyo destino esta vivo en
        // esta cabeza; las demas se quedan fuera A PROPOSITO y estan enumeradas debajo, para que
        // nadie las de por implementadas al leer el metodo.
        //
        // Dentro:
        //   - MessageChatUpgradeFrom / MessageChatUpgradeTo -> NavigateToChat.
        //   - MessageHeaderDate -> CalendarPopup + LoadDateSliceAsync (los dos compilan; es el
        //     mismo calendario que ChatView.Date_Click y DialogViewModel.JumpDate, PARIDAD M22).
        //   - MessagePinMessage / MessageGameScore -> LoadMessageSliceAsync.
        //   - MessageChatChangePhoto y MessageSuggestProfilePhoto saliente -> ShowGallery con
        //     ChatPhotosViewModel; los dos tipos estan en el csproj y NavigationService.ShowGallery
        //     no tiene guarda.
        //
        // Fuera, con el motivo:
        //   - MessageSuggestProfilePhoto ENTRANTE: upstream abre EditMediaPopup (recorte de la
        //     foto) y ese popup no se compila aqui. La rama saliente si entra, que es la que solo
        //     mira.
        //   - MessageVideoChatStarted / MessageVideoChatScheduled: JoinGroupCall esta bajo la
        //     guarda de videochats de grupo (Services/Calls/VoipCoordinator.cs, region Group).
        //   - MessagePaymentSuccessful (NavigateToReceipt), MessageChatSetBackground
        //     (BackgroundPopup), MessageSuggestBirthdate (SettingsBirthdatePopup),
        //     MessageGift / MessageUpgradedGift / MessageGiftedStars / MessageGiftedPremium
        //     (ReceivedGiftPopup, Stars.ReceiptPopup, Premium.PromoPopup) y MessageChatEvent
        //     (StickersPopup): ninguno de esos destinos entra en el subconjunto.
        //   - Las cuatro ramas de listas de tareas y encuestas (MessageChecklistTasks*,
        //     MessagePollOption*) y las dos de MessageSuggestedPost*: son LoadMessageSliceAsync
        //     con checklistTaskId / pollOptionId, y esos mensajes no los dibuja esta cabeza.
        //   - MessageChatSetTheme (ChangeTheme) y MessagePremiumGiftCode: el editor de temas y el
        //     enlace interno de codigo de regalo no estan.
        public async void ExecuteServiceMessage(MessageViewModel message)
        {
            if (message.Content is MessageChatUpgradeFrom chatUpgradeFrom)
            {
                var response = await ClientService.SendAsync(new CreateBasicGroupChat(chatUpgradeFrom.BasicGroupId, false));
                if (response is Chat migratedChat)
                {
                    NavigationService.NavigateToChat(migratedChat);
                }
            }
            else if (message.Content is MessageChatUpgradeTo chatUpgradeTo)
            {
                var response = await ClientService.SendAsync(new CreateSupergroupChat(chatUpgradeTo.SupergroupId, false));
                if (response is Chat migratedChat)
                {
                    NavigationService.NavigateToChat(migratedChat);
                }
            }
            else if (message.Content is MessageHeaderDate && Type is DialogType.History or DialogType.Thread)
            {
                var date = Formatter.ToLocalTime(message.Date);

                var dialog = new CalendarPopup(ClientService, ChatId, TopicId, date);
                dialog.MaxDate = DateTimeOffset.Now.Date;

                var confirm = await ShowPopupAsync(dialog);
                if (confirm == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary && dialog.SelectedDates.Count > 0)
                {
                    var first = dialog.SelectedDates.FirstOrDefault();
                    var offset = first.Date.ToTimestamp();

                    await LoadDateSliceAsync(offset);
                }
            }
            else if (message.Content is MessagePinMessage pinMessage && pinMessage.MessageId != 0)
            {
                if (message.ReplyToState != MessageReplyToState.Deleted)
                {
                    await LoadMessageSliceAsync(message.Id, pinMessage.MessageId);
                }
            }
            else if (message.Content is MessageGameScore gameScore && gameScore.GameMessageId != 0)
            {
                if (message.ReplyToState != MessageReplyToState.Deleted)
                {
                    await LoadMessageSliceAsync(message.Id, gameScore.GameMessageId);
                }
            }
            else if (message.Content is MessageChatChangePhoto chatChangePhoto)
            {
                NavigationService.ShowGallery(new ChatPhotosViewModel(ClientService, StorageService, Aggregator, Chat, chatChangePhoto.Photo));
            }
            else if (message.Content is MessageSuggestProfilePhoto suggestProfilePhoto && message.IsOutgoing)
            {
                NavigationService.ShowGallery(new ChatPhotosViewModel(ClientService, StorageService, Aggregator, Chat, suggestProfilePhoto.Photo));
            }
        }
    }
}
