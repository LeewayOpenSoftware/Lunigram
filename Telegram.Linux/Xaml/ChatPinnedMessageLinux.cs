//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// FALTA — LA BANDA DEL MENSAJE ANCLADO (P-03a).
//
// Hasta ahora `ChatPinnedMessage` era un cascaron en ChatViewStubs.cs: derivaba de
// `ChatHeaderStub`, o sea de `Control`, nacia `Collapsed` y `UpdateMessage` solo se guardaba el
// mensaje. El efecto medido no es «la banda no se ve»: son DOS cosas encadenadas.
//
//   1. La banda nunca se pintaba, asi que en un chat con mensaje anclado no habia forma de saber
//      que lo habia.
//   2. `Views/ChatView.xaml` le pone `Click="Reply_Click"`, y `Reply_Click` arranca con
//      `if (sender is not MessageReferenceBase referenceBase) return;`. Un `Control` NO es un
//      `MessageReferenceBase`: aunque la banda se hubiera dibujado, el clic se habria caido en la
//      primera linea del manejador. La puerta estaba cerrada dos veces.
//
// POR QUE NO ES `Controls/Chats/ChatPinnedMessage.xaml` (181 + 776 lineas). Medido en el fichero,
// no supuesto: el `.xaml` declara `<chats:ChatPinnedMessageLine>`, y esa clase esta DENTRO del
// propio `ChatPinnedMessage.xaml.cs` (linea 460) construida entera con Win2D y Composition
// (`Microsoft.Graphics.Canvas.Geometry`, `CompositionSpriteShape`, `CompositionGeometricClip`,
// `CompositionPathGeometry`, `CompositionRoundedRectangleGeometry`) para dibujar la barrita
// segmentada que indica «anclado 2 de 5». El code-behind ademas usa `SlidePanel.SlideState`,
// `Telegram.Native.Controls` y dos `Visual` con `CreateScopedBatch` para el cruce de textos.
//
// Y como en ChooseChatsPopup y en ChatSearchBar, el bloqueo no se rodea con un `#if`: lo declara
// el **XAML**, y el XAML no tiene preprocesador. Asi que entra la banda, escrita aqui, con la
// barrita reducida a un `Border` liso.
//
// LO QUE SI ENTRA COMPLETO, y es lo que hace que esto valga la pena: el trabajo de verdad no
// estaba en el `.xaml`, estaba en `Controls/Messages/MessageReferenceBase.cs` (1.299 lineas), que
// YA se compila -- lo trae `MessageReply`, la cita dentro de las burbujas. De ahi salen gratis el
// titulo, el prefijo de servicio («Foto», «GIF», ...), el texto formateado, la miniatura y la
// propiedad `Message`. Esta clase solo pone el cuerpo: los tres miembros abstractos y el arbol
// visual.
//
// LO QUE HACE:
//   1. Se pinta sobre la cabecera con el mensaje anclado: titulo («Mensaje anclado», o
//      «Mensaje anclado #N» cuando hay varios, que es el texto que compone upstream), el texto o
//      el tipo de contenido, y la miniatura cuando la hay.
//   2. Al hacer clic salta al mensaje anclado. La puerta es la de arriba: al derivar de
//      `MessageReferenceBase` el `sender is not MessageReferenceBase` de `Reply_Click` ya deja
//      pasar, y de ahi sale `LoadMessageSliceAsync(null, message.Id)`.
//   3. Se muestra y se esconde con el mismo criterio que upstream (`UpdateMessage` con mensaje o
//      con `known`), y avisa a ChatView para que recalcule el hueco de la cabecera.
//
// LO QUE NO HACE, y NO se dibuja apagado en ningun sitio:
//   - **El boton de lista** (abrir todos los anclados). `DialogViewModel.OpenPinnedMessages()`
//     tiene el CUERPO ENTERO dentro de un `#if !LINUX` (DialogViewModel.cs:4132): en Linux es una
//     funcion vacia. Dibujarlo seria un boton muerto exacto.
//   - **El boton de ocultar / desanclar.** `HidePinnedMessage()` si esta vivo, pero en un chat
//     donde el usuario puede anclar NO oculta: manda `UnpinAllChatMessages` tras un
//     `ShowPopupAsync` de confirmacion. Eso es una accion destructiva sobre la cuenta real detras
//     de una equis pequena, y el popup de confirmacion no esta verificado en Skia. Queda fuera a
//     proposito, para decidirlo aparte y no como efecto secundario de portar una banda.
//   - **El boton de accion** (el teclado inline de una sola tecla, o «unirse a la videollamada»).
//   - **La barrita segmentada** que indica cual de los N anclados se esta viendo: aqui es un
//     `Border` liso. El indice sigue estando a la vista, en el «#N» del titulo.
//   - **Las animaciones**: el deslizamiento al aparecer y el cruce de textos al cambiar de
//     anclado. La banda aparece, cambia y desaparece sin animar; por eso `AnimatedHeight` se
//     responde desde `Visibility` y `GetAnimatableVisuals` no devuelve nada.

using System.Collections.Generic;
using Telegram.Common;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Telegram.Controls.Chats
{
    public partial class ChatPinnedMessage : Telegram.Controls.Messages.MessageReferenceBase
    {
        private ChatView _chatView;

        private long _chatId;
        private MessageViewModel _current;

        private readonly TextBlock _titleLabel;
        private readonly Run _serviceLabel;
        private readonly Span _messageSpan;
        private readonly FormattedTextBlock _label;

        private readonly Border _line;
        private readonly Border _separator;

        private readonly Border _thumbRoot;
        private readonly Border _thumbEllipse;
        private readonly ImageBrush _thumbImage;

        // Which theme the brushes below were resolved for, so entering the tree and every later
        // switch re-paints exactly once. ActualTheme is only ever Light or Dark, so Default is a
        // safe "nothing applied yet".
        private ElementTheme _appliedTheme = ElementTheme.Default;

        private static readonly CornerRadius _defaultRadius = new(2);

        public ChatPinnedMessage()
        {
            // A HyperlinkButton by inheritance, so the whole band is the click target and
            // Reply_Click gets a MessageReferenceBase. The default Fluent template would give it
            // accent-coloured text and its own padding; both are cleared here because the content
            // paints its own colours.
            Padding = new Thickness(0);
            Margin = new Thickness(0);
            BorderThickness = new Thickness(0);
            MinHeight = 48;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Top;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Visibility = Visibility.Collapsed;

            // Upstream's ChatPinnedMessageLine: 4px wide, 48 tall, segmented by Win2D geometry to
            // say which of the N pinned messages this is. Here it is a flat rounded bar; the index
            // survives in the title's "#N".
            _line = new Border
            {
                Name = "PinnedLine",
                Width = 4,
                Height = 36,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            _thumbImage = new ImageBrush
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };

            _thumbEllipse = new Border
            {
                Name = "ThumbEllipse",
                Background = _thumbImage,
                CornerRadius = _defaultRadius
            };

            _thumbRoot = new Border
            {
                Name = "ThumbRoot",
                Visibility = Visibility.Collapsed,
                Width = 36,
                Height = 36,
                Margin = new Thickness(6, 0, 2, 0),
                CornerRadius = _defaultRadius,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _thumbEllipse
            };

            _titleLabel = new TextBlock
            {
                Name = "PinnedTitle",
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // Same shape MessageReply's template declares: one Paragraph whose LAST inline is the
            // Span that FormattedTextBlock fills with the message text, with the service word
            // ("Photo", "GIF"...) in a Run in front of it. FormattedTextBlock.OnApplyTemplate
            // adopts these Blocks and remembers that trailing Span -- if the Span is not last,
            // SetText appends a whole new paragraph instead of filling it, which with one visible
            // line means the text never shows.
            _serviceLabel = new Run();
            _messageSpan = new Span();

            var paragraph = new Paragraph();
            paragraph.Inlines.Add(_serviceLabel);
            paragraph.Inlines.Add(_messageSpan);

            _label = new FormattedTextBlock
            {
                Name = "PinnedText",
                MaxLines = 1,
                IsTextSelectionEnabled = false,
                TextSelection = TextSelectionMode.Disabled,
                AutoFontSize = false,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 13
            };
            _label.Blocks.Add(paragraph);

            var content = new StackPanel
            {
                Margin = new Thickness(6, 0, 12, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(_titleLabel);
            content.Children.Add(_label);

            _separator = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(_line, 0);
            Grid.SetColumn(_thumbRoot, 1);
            Grid.SetColumn(content, 2);
            Grid.SetColumnSpan(_separator, 3);

            layout.Children.Add(_separator);
            layout.Children.Add(_line);
            layout.Children.Add(_thumbRoot);
            layout.Children.Add(content);

            Content = layout;

            // The visual tree is built here, not in a template, so the base class can start
            // filling it immediately. Leaving this false makes MessageReferenceBase.UpdateMessage
            // stash the message and wait for an OnApplyTemplate that would never bring anything
            // new.
            _templateApplied = true;

            // The constructor runs before the band is in a tree, so ActualTheme here is the
            // application's theme, not the root's. Painting anyway keeps the band from flashing
            // untinted; Loaded corrects it if the two disagree.
            ApplyTheme();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            ActualThemeChanged += OnActualThemeChanged;
        }

        // WHY THIS IS NOT ONE TryGetValue ON Application.Current.Resources.
        //
        // Every brush this band uses lives in a ThemeDictionaries entry, and the two entries hold
        // DIFFERENT brush objects: Theme.Update fills ThemeDictionaries["Light"] and ["Dark"]
        // (Theme.cs:504) and ThemeIncoming assigns ["Light"] and ["Default"] two separate
        // SolidColorBrush instances per key (Theme.cs:888). So a light/dark switch REPLACES the
        // brush rather than recolouring it, and a reference taken once keeps pointing at the
        // theme it was taken under. (An accent or custom-theme change is the opposite case:
        // Theme.AddOrUpdate mutates the brush already in the dictionary, so those DO follow a
        // once-resolved reference. Only the light/dark axis is broken, which is why this only
        // became visible when u-011 made switching reachable.)
        //
        // Re-running the lookup on ActualThemeChanged is not enough on its own either. The switch
        // is AppearanceSettings.UpdateNightMode -> window.RequestedTheme, and WindowContext puts
        // that on the window's ROOT CONTENT element (WindowContext.cs:706), never on
        // Application.Current.RequestedTheme, which App.xaml.cs:103 sets once at startup and never
        // touches again. Markup {ThemeResource} still follows, because the framework resolves it
        // against each element's ActualTheme -- but a bare TryGetValue on the dictionary is not
        // element-scoped, so it would answer for the startup theme forever and the re-paint would
        // be a no-op. Hence the theme name is passed in explicitly.
        private static Brush LookupBrush(string key, ElementTheme theme)
        {
            var resources = Application.Current?.Resources;
            if (resources == null)
            {
                return null;
            }

            // "Default" is the dark half by Fluent convention; the app writes both spellings
            // (Theme uses "Dark", ThemeIncoming and App.xaml use "Default").
            if (TryLookupThemed(resources, key, theme == ElementTheme.Dark ? "Dark" : "Light", theme == ElementTheme.Dark ? "Default" : null, out Brush themed))
            {
                return themed;
            }

            // Keys that are not themed at all still answer here, and TryGetValue keeps a missing
            // key from throwing.
            if (resources.TryGetValue(key, out object value) && value is Brush brush)
            {
                return brush;
            }

            return null;
        }

        private static bool TryLookupThemed(ResourceDictionary dictionary, string key, string primary, string secondary, out Brush result)
        {
            if (dictionary.ThemeDictionaries != null)
            {
                if (dictionary.ThemeDictionaries.TryGet(primary, out ResourceDictionary first) && first.TryGet(key, out result))
                {
                    return true;
                }

                if (secondary != null && dictionary.ThemeDictionaries.TryGet(secondary, out ResourceDictionary second) && second.TryGet(key, out result))
                {
                    return true;
                }
            }

            // Last merged dictionary wins, which is the order XAML resolves them in: ThemeIncoming
            // is merged after Theme (App.xaml:521) and its MessageHeaderBorderBrush has to be the
            // one that answers.
            var merged = dictionary.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (TryLookupThemed(merged[i], key, primary, secondary, out result))
                {
                    return true;
                }
            }

            result = null;
            return false;
        }

        private void ApplyTheme()
        {
            var theme = ActualTheme;
            if (_appliedTheme == theme)
            {
                return;
            }

            _appliedTheme = theme;

            Background = LookupBrush("PageSubHeaderBackgroundBrush2", theme)
                ?? LookupBrush("PageSubHeaderBackgroundBrush", theme);

            _line.Background = LookupBrush("PinnedMessageBorderBrush", theme)
                ?? LookupBrush("MessageHeaderBorderBrush", theme)
                ?? LookupBrush("SystemControlHighlightAccentBrush", theme);

            _thumbRoot.Background = LookupBrush("SubtleFillColorSecondaryBrush", theme);
            _separator.BorderBrush = LookupBrush("NavigationViewContentGridBorderBrush", theme);

            _titleLabel.Foreground = LookupBrush("MessageHeaderForegroundBrush", theme);
            _serviceLabel.Foreground = LookupBrush("MessageSubtleForegroundBrush", theme);
            _messageSpan.Foreground = LookupBrush("MessageForegroundBrush", theme);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme();
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyTheme();
        }

        private DialogViewModel ViewModel => DataContext as DialogViewModel;

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _chatId = 0;
            _current = null;
            _loading = false;
        }

        /// <summary>
        /// Upstream animates the band in and out with a SlidePanel and reports the interpolated
        /// height so ChatView can pad the history smoothly. Nothing animates here, so the answer is
        /// the band's own height or nothing.
        /// </summary>
        public float AnimatedHeight => Visibility == Visibility.Visible ? 48 : 0;

        public void InitializeParent(ChatView chatView)
        {
            _chatView = chatView;
        }

        /// <summary>
        /// The elements ChatView slides along with the header. Empty because nothing in this band
        /// is animated; the signature is the contract ChatView calls through.
        /// </summary>
        public IEnumerable<UIElement> GetAnimatableVisuals()
        {
            yield break;
        }

        /// <summary>
        /// Called from ChatView.UpdatePinnedMessage (message null, <paramref name="known"/> telling
        /// whether a pinned message is being fetched) and from ViewVisibleMessages, which is the
        /// one that hands over the pinned message currently in view along with its index.
        /// </summary>
        public void UpdateMessage(Chat chat, MessageViewModel message, bool known, int value, int maximum, bool intermediate)
        {
            if (message == null && !known)
            {
                _chatId = 0;
                _current = null;
                _loading = false;

                ShowHide(false);
                return;
            }

            if (chat == null)
            {
                return;
            }

            // The business-bot bar takes the same slot: upstream hides the pinned band while it is
            // up rather than stacking the two.
            ShowHide(chat.BusinessBotManageBar == null);

            if (value < 0)
            {
                value = maximum - 1;
            }
            else if (maximum <= value)
            {
                maximum = value + 1;
            }

            // Upstream's own wording, including the "#2 of 5" suffix it only adds when there is
            // more than one and this is not the last.
            var title = Strings.PinnedMessage + (value >= 0 && maximum > 1 && value + 1 < maximum ? $" #{value + 1}" : "");

            if (_chatId == chat.Id && _current?.Id == message?.Id && !_loading)
            {
                return;
            }

            _chatId = chat.Id;
            _current = message;
            _loading = known && message == null;

            // MessageReferenceBase does the rest: title, the service word for media, the formatted
            // text and the thumbnail, all through the three overrides below.
            UpdateMessage(message, message == null, title);
        }

        private void ShowHide(bool show)
        {
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (Visibility == visibility)
            {
                return;
            }

            Visibility = visibility;

            // Without this the history keeps the padding it had for the previous band layout and
            // the first messages end up under the strip.
            _chatView?.UpdateMessagesHeaderPadding();
        }

        #region Overrides

        protected override void HideThumbnail()
        {
            _thumbnailController?.Recycle();

            _thumbRoot.Visibility = Visibility.Collapsed;
        }

        protected override ImageBrush ShowThumbnail(CornerRadius radius = default)
        {
            _thumbRoot.Visibility = Visibility.Visible;
            _thumbRoot.CornerRadius =
                _thumbEllipse.CornerRadius = radius == default ? _defaultRadius : radius;

            return _thumbImage;
        }

        protected override void SetText(IClientService clientService, MessageViewModel message, bool outgoing, MessageSender sender, string title, string service, FormattedText text, bool manual, bool white)
        {
            _titleLabel.Text = title ?? string.Empty;

            var body = message?.TranslatedText switch
            {
                MessageTranslateResultText translated => message.Delegate?.IsTranslating == true
                    ? translated.Text
                    : message.Text,
                _ => message?.Text
            };

            var prefix = service ?? string.Empty;
            if (!string.IsNullOrEmpty(text?.Text ?? body?.Text) && !string.IsNullOrEmpty(service))
            {
                prefix += ", ";
            }

            _serviceLabel.Text = prefix;

            // Not `text ?? body`: MessageViewModel.Text is a StyledText and the explicit
            // argument is a FormattedText, so the two go to different SetText overloads.
            if (text != null)
            {
                _label.SetText(clientService, text);
            }
            else
            {
                _label.SetText(clientService, body);
            }

            _label.SetQuery(string.Empty);
        }

        #endregion
    }
}
