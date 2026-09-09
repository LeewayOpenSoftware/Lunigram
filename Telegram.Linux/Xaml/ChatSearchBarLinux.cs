//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// PARIDAD A5 / FALTA #4 — LA BARRA DE BUSQUEDA DENTRO DE UN CHAT (Ctrl+F).
//
// Antes de esto, `ChatSearchBar` era un cascaron en ChatViewStubs.cs: `Update` vacio y
// `OnBackRequested` devolviendo **false**. El efecto medido en A5 no era «no pasa nada», eran DOS
// defectos encadenados: Ctrl+F recorria todo el camino y marcaba `Handled` (o sea que la tecla se
// comia), y como `OnBackRequested` contestaba false, `ViewModel.Search` se quedaba distinto de null
// PARA SIEMPRE -- un estado encendido, invisible, que Escape no limpiaba.
//
// POR QUE NO ES `Controls/Chats/ChatSearchBar.xaml` (449 + 547 lineas). Medido, no supuesto:
// anadiendola a fase1/extra-files.txt y compilando con --no-incremental quedan **dos** tipos sin
// resolver, y los dos son justo las sub-funciones que este encargo aparta:
//
//   * `UsernameCollection` (ChatSearchBar.xaml.cs:304, :400, :543) -- el filtro «de quien».
//     **Esta declarada DENTRO de `Controls/Chats/ChatTextBox.cs`, 1.575 lineas**, que es el
//     compositor rico: la prioridad #8 y su propia tanda. Traer el filtro por miembro obliga a
//     traer el editor entero.
//   * `SavedMessagesTagsPanel` (el `x:Name="Tags"` del XAML) -- el filtro por etiqueta de Mensajes
//     Guardados. Ese fichero SI es barato por si solo (~290 lineas), pero da igual: mientras
//     `UsernameCollection` bloquee, la barra de upstream no entra.
//
// Y como en el caso de ChooseChatsPopup, el bloqueo no se puede rodear con un `#if`: es el **XAML**
// el que declara `<messages:SavedMessagesTagsPanel x:Name="Tags">`, y el XAML no tiene
// preprocesador. Asi que entra la parte que BUSCA, escrita aqui, y el resto queda anotado.
//
// LO QUE HACE, que es exactamente el hito pedido:
//   1. Ctrl+F abre una barra de verdad, con el foco puesto en su caja.
//   2. Escribir y pulsar Intro llama a `ChatSearchViewModel.Search(query, null, null, null)`, que
//      es quien manda `searchChatMessages` -- lo hace el propio ViewModel, con
//      `collection.LoadMoreItemsAsync(100)` dentro de Search (ChatSearchViewModel.cs:242). Eso
//      importa comprobarlo: si la coleccion se dejara a la carga incremental de una lista, y aqui
//      no hay lista, NO se enviaria NADA a TDLib y la barra se abriria para no preguntar nunca.
//   3. La barra dice cuantas coincidencias hay (`Items.TotalCount`, que viene de la respuesta de
//      TDLib), o que no hay ninguna. Esa cifra es la prueba visible de que la busqueda ocurrio.
//   4. Escape (o la X) llama a `Dialog.DisposeSearch()`, que pone `Search = null`; el
//      PropertyChanged de `Search` hace que ChatView llame a `Update(null)`
//      (ChatView.xaml.cs:1304, fuera de todo `#if`) y la barra se cierra. **El estado encendido
//      que Escape no limpiaba queda limpiado en su origen**, no tapado.
//
// u-054 — ANTERIOR / SIGUIENTE COINCIDENCIA. Continuacion de lo de arriba: `NextCommand` y
// `PreviousCommand` ya vivian en `ChatSearchViewModel` (empujan `SelectedItem` y disparan
// `Dialog.LoadMessageSliceAsync`); lo que faltaba era la fila de botones que los invocara. No se
// usa `Button.Command` (que si funciona en Uno via x:Bind en el XAML de upstream, pero aqui el
// control se construye a mano en C#, sin binder): se conecta `Click` a mano y el habilitado sigue
// `CanExecuteChanged` a mano, igual que el resto del fichero sigue `PropertyChanged` a mano.
// Mismos glifos que upstream (`&#xE0E4;` / `&#xE0E5;`, ChatSearchBar.xaml:372,385) con la misma
// fuente que ya usa el boton de cerrar de aqui mismo (no `SymbolThemeFontFamily`: es un recurso de
// tema que no esta medido en este arbol).
//
// LO QUE NO HACE, y no se dibuja apagado en ningun sitio:
//   - **Filtrar «de quien»** (necesita el compositor rico, arriba).
//   - **Filtrar por etiqueta** en Mensajes Guardados.
//   - **Saltar por fecha** (el calendario).
//   - **Buscar mientras se escribe**: aqui se busca al pulsar Intro. Upstream teclea contra
//     `ChatSearchTextBox`, que trae su propio retardo y su autocompletado; sin el, buscar en cada
//     pulsacion serian N peticiones a TDLib por palabra.
//   - **La animacion de deslizamiento** sobre la cabecera (`InitializeParent` recibe el `Grid` de
//     la cabecera y el `SlidePanel` justo para eso). La barra aparece y desaparece sin animar.

using System.ComponentModel;
using Telegram.Common;
using Telegram.ViewModels.Chats;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Telegram.Controls.Chats
{
    public partial class ChatSearchBar : Grid
    {
        private readonly TextBox _field;
        private readonly TextBlock _status;
        private readonly Button _previous;
        private readonly Button _next;

        private ChatSearchViewModel _viewModel;
        private ElementTheme? _appliedTheme;

        public ChatSearchBar()
        {
            // Top-aligned strip over the header. The XAML gives this element RowSpan=3 and
            // ZIndex=3 so it can cover the whole chat; only the strip is painted, the rest stays
            // hit-transparent so the history underneath keeps working.
            VerticalAlignment = VerticalAlignment.Top;

            // This tree is built in code, so it cannot use {ThemeResource}. Resolve against the
            // element's theme explicitly: Application.Current.Resources.TryGetValue alone stays
            // pinned to the startup theme in Uno. Loaded covers returning from Appearance (the
            // chat is unloaded while the switch is made); ActualThemeChanged covers a live switch.
            ApplyTheme();
            Loaded += OnLoaded;
            ActualThemeChanged += OnActualThemeChanged;

            _field = new TextBox
            {
                Name = "ChatSearchField",
                PlaceholderText = Strings.Search,
                Margin = new Thickness(8, 8, 8, 8)
            };
            _field.KeyDown += Field_KeyDown;

            _status = new TextBlock
            {
                Name = "ChatSearchStatus",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Opacity = 0.6
            };

            var iconFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets");

            _previous = new Button
            {
                Name = "ChatSearchPrevious",
                Content = "",
                FontFamily = iconFamily,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                IsEnabled = false
            };
            ToolTipService.SetToolTip(_previous, Strings.AccDescrSearchPrev);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_previous, Strings.AccDescrSearchPrev);
            _previous.Click += Previous_Click;

            _next = new Button
            {
                Name = "ChatSearchNext",
                Content = "",
                FontFamily = iconFamily,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = false
            };
            ToolTipService.SetToolTip(_next, Strings.AccDescrSearchNext);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_next, Strings.AccDescrSearchNext);
            _next.Click += Next_Click;

            var close = new Button
            {
                Name = "ChatSearchClose",
                Content = "",
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            close.Click += Close_Click;

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            SetColumn(_field, 0);
            SetColumn(_status, 1);
            SetColumn(_previous, 2);
            SetColumn(_next, 3);
            SetColumn(close, 4);

            Children.Add(_field);
            Children.Add(_status);
            Children.Add(_previous);
            Children.Add(_next);
            Children.Add(close);

            Visibility = Visibility.Collapsed;
        }

        private static Brush LookupBrush(string key, ElementTheme theme)
        {
            var resources = Application.Current?.Resources;
            if (resources == null)
            {
                return null;
            }

            if (TryLookupThemed(resources, key, theme == ElementTheme.Dark ? "Dark" : "Light", theme == ElementTheme.Dark ? "Default" : null, out Brush themed))
            {
                return themed;
            }

            return resources.TryGetValue(key, out object value) && value is Brush brush
                ? brush
                : null;
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
            Background = LookupBrush("PageSubHeaderBackgroundBrush", theme);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme();
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyTheme();
        }

        /// <summary>
        /// Upstream slides the bar over the header and moves the floating date with it. Not ported:
        /// the parameters are kept because ChatView calls this on load (ChatView.xaml.cs:350) and
        /// the signature is the contract, but nothing is animated here.
        /// </summary>
        public void InitializeParent(Grid header, SlidePanel clipper, StackPanel dateHeader)
        {
        }

        public void Update(ChatSearchViewModel viewModel)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.NextCommand.CanExecuteChanged -= NextCommand_CanExecuteChanged;
                _viewModel.PreviousCommand.CanExecuteChanged -= PreviousCommand_CanExecuteChanged;
            }

            _viewModel = viewModel;

            if (viewModel == null)
            {
                Visibility = Visibility.Collapsed;
                _field.Text = string.Empty;
                _status.Text = string.Empty;
                _previous.IsEnabled = false;
                _next.IsEnabled = false;
                return;
            }

            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            viewModel.NextCommand.CanExecuteChanged += NextCommand_CanExecuteChanged;
            viewModel.PreviousCommand.CanExecuteChanged += PreviousCommand_CanExecuteChanged;

            _field.Text = viewModel.Query ?? string.Empty;
            _status.Text = string.Empty;
            _previous.IsEnabled = viewModel.PreviousCommand.CanExecute(null);
            _next.IsEnabled = viewModel.NextCommand.CanExecute(null);

            Visibility = Visibility.Visible;

            // Ctrl+F that opens a bar the caret is not in would still make the user click.
            _field.Focus(FocusState.Programmatic);
            _field.SelectAll();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Search() is async void: it sets Items only after searchChatMessages has answered.
            // Reading TotalCount straight after calling Search would always read 0.
            if (e.PropertyName == nameof(ChatSearchViewModel.Items))
            {
                UpdateStatus();
            }
        }

        private void UpdateStatus()
        {
            var items = _viewModel?.Items;
            if (items == null)
            {
                _status.Text = string.Empty;
            }
            else if (items.TotalCount == 0)
            {
                _status.Text = Strings.NoResult;
            }
            else
            {
                _status.Text = items.TotalCount.ToString();
            }
        }

        private void NextCommand_CanExecuteChanged(object sender, System.EventArgs e)
        {
            _next.IsEnabled = _viewModel?.NextCommand.CanExecute(null) ?? false;
        }

        private void PreviousCommand_CanExecuteChanged(object sender, System.EventArgs e)
        {
            _previous.IsEnabled = _viewModel?.PreviousCommand.CanExecute(null) ?? false;
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.NextCommand.CanExecute(null) == true)
            {
                _viewModel.NextCommand.Execute(null);
            }
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.PreviousCommand.CanExecute(null) == true)
            {
                _viewModel.PreviousCommand.Execute(null);
            }
        }

        private void Field_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                _viewModel?.Search(_field.Text, null, null, null);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            OnBackRequested();
        }

        /// <summary>
        /// Escape reaches here through ChatView.OnBackRequested, which only calls it while
        /// ViewModel.Search is not null. The stub returned false and did nothing, which is why the
        /// state stayed on for good; this clears it at the source.
        /// </summary>
        public bool OnBackRequested()
        {
            _viewModel?.Dialog?.DisposeSearch();
            return true;
        }
    }
}
