// Los tipos Telegram.Td.Api.* los emite el source generator de Telegram.Generators, y los generadores
// de Roslyn no ven la salida de los demás: cuando el generador XAML de Uno encuentra un evento cuyo
// argumento es uno de esos tipos (ChatCell.StoryClick : EventHandler<Chat>,
// ChatRecordBar.StartTyping : EventHandler<ChatAction>) lo resuelve como `global::Chat` y el build
// falla con CS0400. Una parte `partial` vacía en fuente real hace que el símbolo exista antes de la
// generación. Añadir aquí cualquier otro tipo TdApi que aparezca en una firma de evento usada desde XAML.
using System.Collections.Generic;

namespace Telegram.Td.Api
{
    public partial class Chat
    {
    }

    // Igual que Chat, pero por el generador de BindableTypeProviders y no por el de XAML: en cuanto
    // un DependencyObject del subconjunto expone una propiedad publica de tipo Story
    // (StoryComposerPopup.Result), BindableMetadata.g.cs la emite como `global::Story` y el build
    // cae con CS0400 aunque el tipo exista perfectamente en Telegram.Td.Api. Medido el 2026-08-26.
    public partial class Story
    {
    }

    // Y ChatActiveStories por lo mismo: ActiveStoriesCell.Trigger es un blanco de x:Bind, y
    // BindableTypeProviders emitia `global::ChatActiveStories` (CS0400). La tanda de Ajustes lo
    // habia esquivado bajando Trigger a `internal`, y eso rompia el binding en ejecucion: Uno
    // resuelve el setter por reflexion sobre miembros publicos y dejaba
    // "The property setter for [Trigger] does not exist on ActiveStoriesCell" en cada celda, o sea
    // los anillos sin datos. Medido con la app delante el 2026-08-26. Con la parcial vuelve a ser
    // publica y las dos cosas se cumplen.
    public partial class ChatActiveStories
    {
    }

    // InputMessagePoll y CountryInfo por lo mismo: CreatePollPopup expone Result/Input de tipo
    // InputMessagePoll, y ChooseCountriesPopup expone SelectedItems de tipo IList<CountryInfo>,
    // y BindableTypeProviders emitia `global::InputMessagePoll` y `global::CountryInfo` (CS0400).
    public partial class InputMessagePoll
    {
    }

    public partial class CountryInfo
    {
    }

    public partial class Message
    {
        // Upstream compila contra una TDLib prerelease cuyo `message` ordena los ultimos campos
        // (content, reply_markup, <extra int>); el ctor que el generador emite desde la 1.8.67
        // publica tambien tiene 43 parametros pero acaba en (content, ephemeral_content,
        // reply_markup). Este constructor acepta la forma de upstream -- sus 46 sitios de llamada
        // se quedan como estan -- inserta el ephemeral_content que falta y descarta el extra.
        // COMPROBADO en el fuente GENERADO, no deducido del .tl: el generador no expone
        // ephemeral_message_id ni chat_instance en el constructor.
        //
        // Los tres ultimos argumentos van POR NOMBRE a proposito: este constructor y el generado
        // tienen los dos 43 parametros, asi que una llamada `: this(...)` posicional es ambigua y
        // la resolucion vuelve a ESTE (arg 43 ReplyMarkup -> int). Este ctor no tiene ningun
        // parametro llamado `ephemeralContent`, asi que nombrarlo solo puede casar con el generado.
        public Message(
            long id,
            MessageSender senderId,
            MessageSender? receiverId,
            long chatId,
            MessageSendingState? sendingState,
            MessageSchedulingState? schedulingState,
            bool isOutgoing,
            bool isPinned,
            bool isFromOffline,
            bool canBeSaved,
            bool hasTimestampedMedia,
            bool isChannelPost,
            bool isPaidStarSuggestedPost,
            bool isPaidGramSuggestedPost,
            bool containsUnreadMention,
            bool containsUnreadPollVotes,
            int date,
            int editDate,
            MessageForwardInfo? forwardInfo,
            MessageImportInfo? importInfo,
            MessageInteractionInfo? interactionInfo,
            Vector<UnreadReaction> unreadReactions,
            FactCheck? factCheck,
            SuggestedPostInfo? suggestedPostInfo,
            MessageReplyTo? replyTo,
            MessageTopic? topicId,
            MessageSelfDestructType? selfDestructType,
            double selfDestructIn,
            double autoDeleteIn,
            long viaBotUserId,
            MessageSender? guestBotCallerId,
            long senderBusinessBotUserId,
            int senderBoostCount,
            string senderTag,
            long paidMessageStarCount,
            string authorSignature,
            long mediaAlbumId,
            long effectId,
            RestrictionInfo? restrictionInfo,
            string summaryLanguageCode,
            MessageContent content,
            ReplyMarkup? replyMarkup,
            int prereleaseExtra)
            : this(id, senderId, receiverId, chatId, sendingState, schedulingState, isOutgoing, isPinned, isFromOffline, canBeSaved, hasTimestampedMedia, isChannelPost, isPaidStarSuggestedPost, isPaidGramSuggestedPost, containsUnreadMention, containsUnreadPollVotes, date, editDate, forwardInfo, importInfo, interactionInfo, unreadReactions, factCheck, suggestedPostInfo, replyTo, topicId, selfDestructType, selfDestructIn, autoDeleteIn, viaBotUserId, guestBotCallerId, senderBusinessBotUserId, senderBoostCount, senderTag, paidMessageStarCount, authorSignature, mediaAlbumId, effectId, restrictionInfo, summaryLanguageCode, content: content, ephemeralContent: null, replyMarkup: replyMarkup)
        {
        }

    }

    public partial class MessageReaction
    {
    }

    public partial class MessageEffect
    {
    }

    public partial class InlineKeyboardButton
    {
    }

    public abstract partial class ChatAction
    {
    }

}
