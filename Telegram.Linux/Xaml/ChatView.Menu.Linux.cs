//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// PARIDAD A1 / FALTA §1.2 — EL MENU CONTEXTUAL DEL MENSAJE.
//
// Hasta ahora `Message_ContextRequested` era un cuerpo vacio en la rama `#if LINUX` de
// ChatView.xaml.cs, y el handler SI se enganchaba a cada contenedor (ChatView.Bubbles.xaml.cs:1705,
// fuera de todo `#if`): el clic derecho llegaba, se consumia y no pasaba nada. Ese es el «corte
// silencioso» de A1.
//
// LA REGLA QUE MANDA AQUI la dejo escrita la tanda anterior en la cabecera de
// DialogViewModel.Linux.cs: **una entrada de menu por cada metodo VIVO, y NI UNA por cada metodo
// VACIO**. Un boton que se dibuja y no hace nada es peor que la ausencia del boton. Asi que este
// menu no es «el de Windows recortado a ojo»: es la interseccion, comprobada metodo a metodo, con
// lo que este cabezal sabe hacer de verdad.
//
// LO QUE DIBUJA, y por que cada uno esta vivo:
//   * Responder      -> ReplyToMessage        (VIVO; puerta ya existente ademas: el doble clic)
//   * Copiar         -> CopyMessage           (VIVO; mismo predicado que Windows, MessageCopy_Loaded)
//   * Seleccionar    -> SelectMessage         (VIVO)
//   * Traducir       -> TranslateMessage      (VIVO. **Esta es su UNICA puerta**: su otro llamante,
//                                              ChatView.xaml.cs:3085, vive en la mitad `#else`.
//                                              Hasta este fichero, era codigo inalcanzable.)
//   * Eliminar       -> DeleteMessage         (VIVO, destructivo)
//   * Reenviar      -> ForwardMessage         (VIVO desde §1.21; guarda `CanBeForwarded`)
//   * Fijar / Desanclar -> PinMessage         (VIVO desde u-055; guarda `CanBePinned` +
//                                              `DialogType.History`, mismo predicado reducido de
//                                              `MessagePin_Loaded`, ChatView.xaml.cs:4850)
//   * Anadir a contactos -> AddToContacts     (VIVO desde la parcela 3, que metio UserEditPage en
//                                              el subconjunto; guarda `MessageAddContactLinux_Loaded`,
//                                              copia literal de `MessageAddContact_Loaded`,
//                                              ChatView.xaml.cs:5397. Solo sobre un mensaje de
//                                              contacto compartido cuyo usuario no lo sea ya.)
//   * Denunciar      -> ReportMessage          (VIVO desde u-059; guarda `MessageReportLinux_Loaded`,
//                                              copia literal de `MessageReport_Loaded`,
//                                              ChatView.xaml.cs:4922)
//   Y en modo seleccion: Eliminar seleccionados (DeleteSelectedMessages), Reenviar seleccionados
//   (ForwardSelectedMessages), Denunciar seleccionados (ReportSelectedMessages, desde u-059; guarda
//   `CanReportSelectedMessages`), Limpiar seleccion (UnselectMessages) y Copiar seleccionados
//   (CopySelectedMessages), los cinco VIVOS.
//
// LO QUE **NO** DIBUJA, y no es un olvido — cada uno es un metodo vacio o un tipo fuera del
// subconjunto, y dibujarlo seria exactamente el defecto que A1 denuncia:
//   * Editar, Reintentar, Ver en el chat, Estadisticas, Enviar ahora, Reprogramar, Guardar
//     como, Comprobacion de datos, Responder en otro chat, Citar: `EditMessage`,
//     `ResendMessage`, `ViewMessageInChat`, `OpenMessageStatistics`, `SendNowMessage`,
//     `RescheduleMessage`, `FactCheckMessage`, `ReplyToMessageInAnotherChat` y `QuoteToMessage`
//     **no existen en el subconjunto compilado**: viven en DialogViewModel.Messages.cs, que esta
//     fuera. No es que esten vacios: es que nombrarlos no compila. Medido, no supuesto.
//     (`PinMessage`, `ReportMessage` y `ReportSelectedMessages` salieron de esta lista y viven en
//     DialogViewModel.Linux.cs: `PinMessage` en u-055 y el par de denuncia en u-059, que ademas
//     trajo `ReportChatPopup`. Las dos ya estan fusionadas aqui.)
//   * Listas de tareas y encuestas (los submenus de checklist y poll de upstream): sus metodos
//     tampoco estan en el subconjunto.
//
// UN MENU VACIO NO SE ABRE. Si despues de todas las guardas no ha entrado ninguna entrada, el
// metodo se sale sin llamar a ShowAt: abrir un recuadro vacio al clic derecho seria otra forma de
// la misma mentira.
//
// GEOMETRIA Y ANCLAJE: la resolucion del elemento (SelectorItem -> ContentRoot -> MessageSelector /
// StackPanel de servicio) es la de upstream tal cual, porque de ella depende **donde** sale el
// menu, y esa es una de las cosas que este proyecto ha deducido mal dos veces.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Controls.Messages;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Delegates;
using Telegram.Views.Monetization.Popups;
using Telegram.Views.Popups;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Telegram.Views
{
    public sealed partial class ChatView
    {
        /// <summary>
        /// PARIDAD A4: the overflow menu in the chat header. This mirrors upstream's menu, but
        /// only includes actions whose complete Linux path is live. In particular, custom mute
        /// duration, boosts, themes, creating a topic and joining a group call still terminate in
        /// Linux guards or unported UI, so none is drawn here. Reporting was on that list until
        /// u-059 brought ReportChatPopup into the subset and took the guard off
        /// DialogViewModel.ReportAsync; adding a contact was on it until parcel 3 brought
        /// UserEditPage in. Both are drawn now.
        /// </summary>
        private void ShowChatMenuLinux(object sender)
        {
            var chat = ViewModel?.Chat;
            var button = sender as Button;
            if (chat == null || button == null)
            {
                return;
            }

            var flyout = new MenuFlyout();
            flyout.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter));
            flyout.MenuFlyoutPresenterStyle.Setters.Add(new Setter(MinWidthProperty, 180));

            var user = chat.Type is ChatTypePrivate or ChatTypeSecret
                ? ViewModel.ClientService.GetUser(chat)
                : null;
            var basicGroup = chat.Type is ChatTypeBasicGroup basicGroupType
                ? ViewModel.ClientService.GetBasicGroup(basicGroupType.BasicGroupId)
                : null;
            var supergroup = chat.Type is ChatTypeSupergroup supergroupType
                ? ViewModel.ClientService.GetSupergroup(supergroupType.SupergroupId)
                : null;

            if (user != null
                && user.Id == ViewModel.ClientService.Options.MyId
                && ViewModel.SavedMessagesTopic == null)
            {
                flyout.CreateFlyoutItem(ViewModel.ViewAsChats, Strings.SavedViewAsChats, Icons.AppsListDetails);
            }

            flyout.CreateFlyoutItem(Search, Strings.Search, Icons.Search, VirtualKey.F);

            if (ViewModel.SavedMessagesTopic != null || ViewModel.DirectMessagesChatTopic != null)
            {
                flyout.CreateFlyoutItem(ViewModel.DeleteTopic, Strings.DeleteChatUser, Icons.Delete, destructive: true);
                flyout.ShowAt(button, FlyoutPlacementMode.BottomEdgeAlignedRight);
                return;
            }

            // Private calls are backed by libunigram-calls.so. The analogous group-call entry is
            // deliberately absent: its native engine exists, but the group-call UI is not ported.
            if (_compactCollapsed
                && user != null
                && user.Id != ViewModel.ClientService.Options.MyId
                && ViewModel.ClientService.TryGetUserFull(user.Id, out UserFullInfo userFull)
                && userFull.CanBeCalled)
            {
                flyout.CreateFlyoutItem(ViewModel.VoiceCall, Strings.Call, Icons.Call);
                flyout.CreateFlyoutItem(ViewModel.VideoCall, Strings.VideoCall, Icons.Video);
            }

            if (ViewModel.TranslateService.CanTranslate(ViewModel.DetectedLanguage, true) && !chat.IsTranslatable)
            {
                flyout.CreateFlyoutItem(ViewModel.ShowTranslate, Strings.TranslateMessage, Icons.Translate);
            }

            // Denunciar el chat. Estaba fuera cuando se escribio este menu porque
            // DialogViewModel.ReportAsync entero era `#if !LINUX`; u-059 metio ReportChatPopup en
            // el subconjunto y le quito la guarda, asi que ya hay camino completo y la entrada
            // deja de ser un boton que no hace nada. Misma condicion y misma posicion que upstream
            // (ChatView.xaml.cs:3093-3096): justo antes de ViewAsTopics.
            if (supergroup != null
                && supergroup.Status is not ChatMemberStatusCreator
                && (supergroup.IsChannel || supergroup.HasActiveUsername()))
            {
                flyout.CreateFlyoutItem(ViewModel.Report, Strings.ReportChat, Icons.ErrorCircle);
            }

            if (supergroup != null
                && supergroup.IsForum
                && !chat.ViewAsTopics
                && ViewModel.Type == DialogType.History
                && !supergroup.HasForumTabs)
            {
                flyout.CreateFlyoutItem(ViewModel.ViewAsTopics, Strings.TopicViewAsTopics, Icons.ChatEmpty);
                // CreateTopic is not drawn: its popup and entire implementation remain !LINUX.
            }

            // Anadir a contactos (parcela 3). Misma guarda y misma posicion que upstream
            // (ChatView.xaml.cs:3257-3262): un usuario que no es un bot, ni una cuenta borrada, ni
            // uno mismo, y que todavia no es contacto ni soporte. DialogViewModel.AddToContacts()
            // -- el gemelo SIN argumento -- perdio su `#if !LINUX` en esta misma parcela, asi que
            // la entrada abre UserEditPage de verdad.
            if (user != null
                && user.Type is not UserTypeDeleted and not UserTypeBot
                && user.Id != ViewModel.ClientService.Options.MyId
                && !user.IsContact
                && !user.IsSupport)
            {
                flyout.CreateFlyoutItem(ViewModel.AddToContacts, Strings.AddToContacts, Icons.PersonAdd);
            }

            if (!ViewModel.IsSelectionEnabled)
            {
                if (user != null || basicGroup != null
                    || (supergroup != null && !supergroup.IsChannel && !supergroup.HasActiveUsername()))
                {
                    flyout.CreateFlyoutItem(ViewModel.ClearHistory, Strings.ClearHistory, Icons.Broom);
                }

                if (user != null)
                {
                    flyout.CreateFlyoutItem(ViewModel.DeleteChat, Strings.DeleteChatUser, Icons.Delete, destructive: true);
                }
                else if (basicGroup != null)
                {
                    flyout.CreateFlyoutItem(ViewModel.DeleteChat, Strings.DeleteAndExit, Icons.Delete, destructive: true);
                }
                else if (supergroup != null
                    && supergroup.Status is ChatMemberStatusMember or ChatMemberStatusRestricted)
                {
                    flyout.CreateFlyoutItem(ViewModel.DeleteChat,
                        supergroup.IsChannel ? Strings.LeaveChannelMenu : Strings.LeaveMegaMenu,
                        Icons.Delete, destructive: true);
                }
            }

            if ((user != null && user.Type is not UserTypeDeleted
                    && user.Id != ViewModel.ClientService.Options.MyId)
                || basicGroup != null
                || (supergroup != null && !supergroup.IsChannel))
            {
                var muted = ViewModel.ClientService.Notifications.IsMuted(chat);
                var silent = ViewModel.ClientService.Notifications.IsSilent(chat);
                var mute = new MenuFlyoutSubItem
                {
                    Text = Strings.Mute,
                    Icon = MenuFlyoutHelper.CreateIcon(muted ? Icons.Alert : Icons.AlertOff)
                };

                if (!muted)
                {
                    mute.CreateFlyoutItem(ViewModel.SetSound, !silent,
                        silent ? Strings.SoundOn : Strings.SoundOff,
                        silent ? Icons.MusicNote2 : Icons.MusicNoteOff2);
                }

                mute.CreateFlyoutItem<int?>(ViewModel.MuteFor, 60 * 60, Strings.MuteFor1h, Icons.ClockAlarmHour);
                // MuteFor(null) is omitted: ChatMutePopup is still guarded out on Linux.
                mute.CreateFlyoutItem(muted ? ViewModel.Unmute : ViewModel.Mute,
                    muted ? Strings.UnmuteNotifications : Strings.MuteNotifications,
                    muted ? Icons.Speaker3 : Icons.SpeakerOff);
                flyout.Items.Add(mute);
            }

            if (ViewModel.Settings.GetChatPinnedMessage(chat.Id) != 0)
            {
                flyout.CreateFlyoutItem(ViewModel.ShowPinnedMessage, Strings.PinnedMessages, Icons.Pin);
            }

            if (flyout.Items.Count > 0)
            {
                flyout.ShowAt(button, FlyoutPlacementMode.BottomEdgeAlignedRight);
            }
        }

        /// <summary>
        /// The body PARIDAD A1 was missing. Called from <c>Message_ContextRequested</c> in the
        /// <c>#if LINUX</c> branch of ChatView.xaml.cs.
        /// </summary>
        // u-091: the async void surface of the context menu is this wrapper and nothing else.
        //
        // The body below is the daily right-click path and is §6 territory throughout -
        // ContentRoot(), FindName, flyout construction, and the reactions wiring added on top of
        // it. An async void that throws does NOT reach the caller (the state machine captures the
        // exception and re-raises it on the SynchronizationContext), so the try/catch at
        // Message_ContextRequested is structurally blind to it and the right click simply dies
        // with nothing on screen.
        //
        // Measured where that throw actually lands on this head (review/probes/AsyncVoidLanding.cs
        // and the decompiled Uno 6.6.184): NativeDispatcherSynchronizationContext.Post enqueues it
        // and NativeDispatcher.RunAction wraps every dequeued action in a catch that writes one
        // "NativeDispatcher unhandled exception" line through Uno's own logger. So it is not
        // process-fatal and it is not Application.UnhandledException - it is a console line that
        // names no handler, which is the same thing as silence for anyone reading a bug report.
        //
        // Splitting rather than indenting 229 lines: the core keeps the whole body verbatim and
        // becomes async Task, so its exceptions travel on the returned task to the await below
        // instead of onto the dispatcher, and the void method that XAML calls is small enough to
        // see at a glance that nothing can escape it.
        private async void ShowMessageMenuLinux(UIElement sender, ContextRequestedEventArgs args)
        {
            try
            {
                await ShowMessageMenuLinuxCore(sender, args);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private async Task ShowMessageMenuLinuxCore(UIElement sender, ContextRequestedEventArgs args)
        {
            var element = sender as FrameworkElement;
            var message = Messages.ItemFromContainer(element) as MessageViewModel;

            // Upstream's anchor resolution, kept verbatim: the flyout has to hang off the bubble,
            // not off the row, or it opens in the wrong place on wide windows.
            if (sender is SelectorItem container && container.ContentRoot() is FrameworkElement content)
            {
                if (content is MessageSelector selector)
                {
                    element = selector.Content as MessageBubble;
                }
                else if (content is StackPanel panel)
                {
                    element = panel.FindName("Service") as FrameworkElement;
                }
                else
                {
                    element = content;
                }
            }

            var chat = message?.Chat;
            if (chat == null)
            {
                return;
            }

            if (message.Id == 0)
            {
                // message.Id == 0 is a sponsored message, and upstream builds it a menu of its own
                // (ChatView.xaml.cs:3746). That menu used to be unreachable here because it hangs
                // off ReportMessage, which lives in DialogViewModel.Messages.cs and is not in the
                // subset. u-021 brought ReportAdsPopup in, so the menu can be built directly --
                // ReportMessage's whole sponsored branch IS `new ReportAdsPopup(...)`, so nothing
                // is being reimplemented, only reached by a shorter road.
                ShowSponsoredMenuLinux(message, element, args);
                return;
            }

            var flyout = new MenuFlyout();
            flyout.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter));
            flyout.MenuFlyoutPresenterStyle.Setters.Add(new Setter(MinWidthProperty, 180));

            var selected = ViewModel.SelectedItems;
            if (selected.Count > 0)
            {
                if (selected.ContainsKey(message.Id))
                {
                    // CanDeleteSelectedMessages is already recomputed from getMessageProperties by
                    // DialogViewModel.Delegate.cs (the #if LINUX branch), which is the same source
                    // the multi-selection bar's bin button reads. One source of truth, not two.
                    if (ViewModel.CanDeleteSelectedMessages)
                    {
                        flyout.CreateFlyoutItem(ViewModel.DeleteSelectedMessages, Strings.DeleteSelected, Icons.Delete, destructive: true);
                    }

                    if (ViewModel.CanForwardSelectedMessages)
                    {
                        flyout.CreateFlyoutItem(ViewModel.ForwardSelectedMessages, Strings.ForwardSelected, Icons.Share);
                    }

                    // Denunciar seleccionados (u-059). Mismo orden que upstream: entre Reenviar y
                    // Eliminar seleccionados.
                    if (ViewModel.CanReportSelectedMessages)
                    {
                        flyout.CreateFlyoutItem(ViewModel.ReportSelectedMessages, Strings.ReportSelectedMessages, Icons.ShieldError);
                    }

                    flyout.CreateFlyoutItem(ViewModel.UnselectMessages, Strings.ClearSelection);

                    if (selected.Values.All(x => x.CanBeSaved))
                    {
                        flyout.CreateFlyoutSeparator();
                        flyout.CreateFlyoutItem(ViewModel.CopySelectedMessages, Strings.CopySelectedMessages, Icons.Copy);
                    }
                }
                else
                {
                    flyout.CreateFlyoutItem(ViewModel.SelectMessage, message, Strings.Select, Icons.CheckmarkCircle);
                }

                if (flyout.Items.Count == 0)
                {
                    return;
                }

                flyout.ShowAt(element, args, FlyoutShowMode.Auto);
                return;
            }

            // F3 (u-093 / PERF-COUNT-INVESTIGATION.md §A): this used to await GetMessageProperties
            // before building a single item, so a right-click waited behind whatever the TDLib
            // actor queue was already doing -- during a chat open, that queue is the anchor walk's
            // GetChatHistory flood, and the menu sat there empty for the same seconds the walk
            // takes. The round trip is only needed for Reply/Forward/Pin/Delete; Copy, Select and
            // Translate never touch it, so build and show those immediately and splice the rest
            // in when the properties land.
            //
            // Splicing into an ALREADY-OPEN MenuFlyout is not a guess: decompiled from the shipped
            // Uno.WinUI 6.6.184 (Microsoft.UI.Xaml.Controls.MenuFlyout, Uno.UI.dll),
            // MenuFlyoutItemBaseCollection.OnCollectionChanged calls QueueRefreshItemsSource on
            // every Add/Insert, which -- if the presenter is up, i.e. the flyout is open -- clears
            // and re-sets MenuFlyoutPresenter.ItemsSource on the dispatcher queue. An Insert after
            // ShowAt lands on screen; it does not sit there inert.
            var propertiesTask = ViewModel.ClientService.SendAsync(new GetMessageProperties(message.ChatId, message.Id));

            // Denunciar (u-059 / FALTA 1.3). Mismo guard que upstream's MessageReport_Loaded
            // (ChatView.xaml.cs:4922): chat.CanBeReported, sin mensaje de servicio, sin ser el
            // propio, sin ser un evento del registro de administracion. Va en el grupo inmediato y
            // no en el diferido a proposito: su puerta es un predicado local sobre el chat y el
            // mensaje, no necesita GetMessageProperties, asi que hacerla esperar al viaje de ida y
            // vuelta seria pagar la latencia que u-093 acaba de quitar sin ganar nada.
            flyout.CreateFlyoutItem(MessageReportLinux_Loaded, ViewModel.ReportMessage, message, Strings.ReportChat, Icons.ErrorCircle);

            flyout.CreateFlyoutItem(MessageCopyLinux_Loaded, ViewModel.CopyMessage, message, Strings.Copy, Icons.Copy);
            flyout.CreateFlyoutItem(ViewModel.SelectMessage, message, Strings.Select, Icons.CheckmarkCircle);

            // TranslateMessage's door. The gate is the one MessageHelper already uses for the
            // link menu (CanTranslateText over the message's own translatable text), not
            // DialogViewModel.CanTranslate, which asks whether the WHOLE CHAT is translatable
            // and would hide the entry on every chat the user has not opted in.
            var translatable = message.GetTranslatableText();
            if (translatable != null && !string.IsNullOrEmpty(translatable.Text)
                && ViewModel.TranslateService.CanTranslateText(translatable))
            {
                flyout.CreateFlyoutItem(ViewModel.TranslateMessage, message, Strings.TranslateMessage, Icons.Translate);
            }

            // Anadir a contactos sobre un mensaje de contacto compartido (parcela 3 / FALTA §1.3,
            // el ultimo hueco real de este menu). El predicado es MessageAddContact_Loaded literal
            // (ChatView.xaml.cs:5397), copiado y no llamado porque el original vive en la mitad
            // `#else` del fichero. Va en el grupo inmediato por la misma razon que Denunciar: es un
            // predicado local sobre el contenido del mensaje, no necesita GetMessageProperties.
            flyout.CreateFlyoutItem(MessageAddContactLinux_Loaded, ViewModel.AddToContacts, message, Strings.AddContactTitle, Icons.Person);

            // Reaccionar por primera vez (u-064 / FALTA 1.9). Literal upstream wiring
            // (ChatView.xaml.cs:4219-4234): subscribe to Opened, THEN fetch
            // GetMessageAvailableReactions and inject the quick-reaction bar as the flyout's
            // header. Gated the same way upstream is -- element already resolved to the
            // MessageBubble earlier in this method, and MessageBubble implements
            // IReactionsDelegate (checked, not assumed).
            //
            // La suscripcion tiene que quedar ANTES del ShowAt, y con la reestructuracion de u-093
            // eso dejo de ser gratis: antes el ShowAt era la ultima linea del metodo y cualquier
            // sitio valia. Ahora el menu se muestra en cuanto estan Copiar/Seleccionar/Traducir, de
            // modo que suscribirse despues seria suscribirse a un evento YA disparado -- la barra
            // no saldria nunca y no habria ni excepcion ni linea de log que lo dijera. Lo que
            // garantiza que flyout.Items[0] existe cuando ReactionsMenuFlyout.Initialize lo lee
            // sigue siendo lo mismo: Opened se dispara con el menu inmediato ya construido.
            if (element is IReactionsDelegate reactionsDelegate)
            {
                flyout.Opened += async (s, args2) =>
                {
                    var response = await ViewModel.ClientService.SendAsync(new GetMessageAvailableReactions(message.ChatId, message.Id, 8));

                    // Los tres contadores que decide TDLib. Sin esto, "la barra de reacciones no
                    // sale" tiene dos causas indistinguibles desde fuera: que TDLib no devuelva
                    // ninguna reaccion disponible para ESE mensaje (nada que dibujar, correcto), o
                    // que si las devuelva y el fallo este en el dibujado. El log las separa.
                    if (response is AvailableReactions available)
                    {
                        Logger.Info($"reactions for {message.ChatId}/{message.Id}: top={available.TopReactions.Count} popular={available.PopularReactions.Count} recent={available.RecentReactions.Count} flyoutOpen={flyout.IsOpen}");
                    }
                    else
                    {
                        Logger.Info($"reactions for {message.ChatId}/{message.Id}: GetMessageAvailableReactions returned {response?.GetType().Name ?? "null"}");
                    }

                    if (response is AvailableReactions reactions && flyout.IsOpen)
                    {
                        if (reactions.TopReactions.Count > 0
                            || reactions.PopularReactions.Count > 0
                            || reactions.RecentReactions.Count > 0)
                        {
                            // Observed on purpose. Everything ReactionsMenuFlyout does before
                            // opening its popup runs synchronously, but this handler is an async
                            // void that has already awaited, so a throw from there would be posted
                            // to the dispatcher and lost -- and "the bar did not appear, nothing in
                            // the log" is precisely the symptom of the bug this replaced.
                            try
                            {
                                ReactionsMenuFlyout.ShowAt(reactions, message, reactionsDelegate, flyout);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error("reactions bar failed to open", ex);
                            }
                        }
                    }
                };
            }

            if (flyout.Items.Count == 0)
            {
                return;
            }

            flyout.ShowAt(element, args, FlyoutShowMode.Auto);

            // One round trip, reused by Reply, Pin and Delete. Upstream asks the same question in
            // the same place; asking twice would be two answers about one message.
            var properties = await propertiesTask as MessageProperties;
            if (properties == null)
            {
                return;
            }

            // Prepended in this order so they still land as Reply, Forward, Pin ahead of the
            // Copy/Select/Translate that are already on screen -- CreateFlyoutItem only appends,
            // so it builds these into a side list first and inserts that in place at the front.
            var prepend = new List<MenuFlyoutItemBase>();

            prepend.CreateFlyoutItem(_ => properties.CanBeReplied, ViewModel.ReplyToMessage, message, Strings.Reply, Icons.ArrowReply);
            prepend.CreateFlyoutItem(_ => properties.CanBeForwarded, ViewModel.ForwardMessage, message, Strings.Forward, Icons.Share);

            // Fijar (u-055 / FALTA §1.3). Same guard as upstream's MessagePin_Loaded
            // (ChatView.xaml.cs:4850): CanBePinned plus "this is a place a pin makes sense" --
            // reduced here to DialogType.History, the one this head actually opens messages in.
            if (ViewModel.Type == DialogType.History)
            {
                prepend.CreateFlyoutItem(_ => properties.CanBePinned, ViewModel.PinMessage, message, message.IsPinned ? Strings.UnpinMessage : Strings.PinMessage, message.IsPinned ? Icons.PinOff : Icons.Pin);
            }

            for (int i = 0; i < prepend.Count; i++)
            {
                flyout.Items.Insert(i, prepend[i]);
            }

            if (properties.CanBeDeletedOnlyForSelf || properties.CanBeDeletedForAllUsers)
            {
                flyout.CreateFlyoutSeparator();
                flyout.CreateFlyoutItem(ViewModel.DeleteMessage, message, Strings.Delete, Icons.Delete, destructive: true);
            }
        }

        /// <summary>
        /// The right-click menu of a sponsored message. Upstream's is at ChatView.xaml.cs:3746;
        /// this is the same menu with two differences, both deliberate and both measured.
        /// </summary>
        /// <remarks>
        /// 1. Upstream's two "about" entries are literally <c>() => { }</c> with a
        ///    <c>// TODO: about</c> beside them -- dead entries on Windows too. Here they open
        ///    <see cref="AboutAdsPopup"/>, which is the popup that TODO is waiting for: its own
        ///    title resource is <c>AboutRevenueSharingAds</c>, the same string upstream labels the
        ///    dead entry with. This is the one place this port is AHEAD of upstream rather than
        ///    behind it, so it is worth knowing on a rebase.
        /// 2. "Remove ads" is drawn only for a Premium account, for the same measured reason as
        ///    the band's x (ChatSponsoredHeader.xaml.cs): HideSponsoredMessage only hides for
        ///    Premium, and its other branch is ShowPromo, which TLNavigationService answers on
        ///    Linux with a log line and nothing else. Drawing it for everyone would be a dead
        ///    button, which is the thing this port does not do.
        /// </remarks>
        private void ShowSponsoredMenuLinux(MessageViewModel message, FrameworkElement element, ContextRequestedEventArgs args)
        {
            // The SponsoredMessage object itself is the ViewModel's, not the bubble's: a sponsored
            // MessageViewModel carries MessageSponsored as its Content and Id 0, and it is
            // DialogViewModel.SponsoredMessage that holds the MessageId the two popups need.
            if (message.Content is not MessageSponsored || ViewModel?.SponsoredMessage is not SponsoredMessage sponsored)
            {
                return;
            }

            var flyout = new MenuFlyout();
            flyout.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter));
            flyout.MenuFlyoutPresenterStyle.Setters.Add(new Setter(MinWidthProperty, 180));

            flyout.CreateFlyoutItem(AboutAds, sponsored.CanBeReported
                ? Strings.AboutRevenueSharingAds
                : Strings.SponsoredMessageInfo, Icons.Info);

            if (sponsored.CanBeReported)
            {
                flyout.CreateFlyoutItem(ReportAd, Strings.ReportAd, Icons.HandRight);
            }

            if (ViewModel.IsPremium)
            {
                flyout.CreateFlyoutSeparator();
                flyout.CreateFlyoutItem(RemoveAds, Strings.RemoveAds, Icons.DismissCircle);
            }

            flyout.ShowAt(element, args, FlyoutShowMode.Auto);

            void AboutAds()
            {
                ViewModel.ShowPopup(new AboutAdsPopup(ViewModel, sponsored));
            }

            void ReportAd()
            {
                ViewModel.ShowPopup(new ReportAdsPopup(ViewModel, ViewModel.Chat.Id, sponsored.MessageId, null));
            }

            void RemoveAds()
            {
                ViewModel.HideSponsoredMessage(message);
            }
        }

        /// <summary>
        /// MessageCopy_Loaded minus the instant-view message. NOT a stylistic split: upstream's
        /// predicate is <c>message.Content.HasCaption()</c>, and for a
        /// <c>MessageRichMessage</c> HasCaption reaches into
        /// <c>richMessage.Message.Blocks[0]</c> -- an unguarded indexer, on shared code, now
        /// newly reachable because this menu is the first thing on this head that calls the
        /// predicate on an arbitrary right-clicked message.
        ///
        /// And even if it answered, the entry would be dead: CopyMessage's MessageRichMessage
        /// branch is deliberately out of this head (PageBlockHelper.GetFormattedText is not in
        /// the Linux reduction of that helper), so Copy would resolve a null caption and put
        /// nothing on the clipboard. A drawn entry that copies nothing is exactly what A1 is
        /// about, so it is not drawn.
        /// </summary>
        /// <summary>
        /// Same predicate as upstream's MessageReport_Loaded (ChatView.xaml.cs:4922), literally.
        /// </summary>
        private bool MessageReportLinux_Loaded(MessageViewModel message)
        {
            var chat = ViewModel.Chat;
            if (chat == null || !chat.CanBeReported || message.Event != null || message.IsService || message.IsOutgoing)
            {
                return false;
            }

            return true;
        }

        private bool MessageCopyLinux_Loaded(MessageViewModel message)
        {
            if (message?.Content is null or MessageRichMessage)
            {
                return false;
            }

            return MessageCopy_Loaded(message);
        }

        /// <summary>
        /// Same predicate as upstream's MessageAddContact_Loaded (ChatView.xaml.cs:5397), literally.
        /// Copied rather than called: the original sits in the <c>#else</c> half of ChatView.xaml.cs
        /// and does not exist in this build.
        /// </summary>
        private bool MessageAddContactLinux_Loaded(MessageViewModel message)
        {
            if (message?.Content is MessageContact contact)
            {
                var user = ViewModel.ClientService.GetUser(contact.Contact.UserId);
                if (user == null)
                {
                    return false;
                }

                return !user.IsContact;
            }

            return false;
        }
    }
}
