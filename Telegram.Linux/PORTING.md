# Telegram.Linux — convenciones del port a Linux (Uno Platform)

Cabeza Linux de Unigram. Proyecto `Telegram.Linux.csproj` (Uno.Sdk, `net10.0-desktop`, Skia/X11),
generado por `unigram-linux/tools/gen-linux-csproj.py` a partir de `unigram-linux/fase1/files.json`:
**enlaza** los fuentes de `../Telegram` (no los copia). Rama git: `linux`.

## Reglas

1. **Los fuentes compartidos viven en `../Telegram`.** Se editan allí. Para excluir código Windows-only
   se usa `#if !LINUX ... #endif` (o `#if LINUX` para la variante Linux) — el símbolo `LINUX` lo define
   este csproj. Mantener los bloques pequeños y pegados al código que sustituyen, para que el rebase
   sobre upstream sea mecánico. No reformatear ni reordenar nada que no sea necesario.
   En XAML el equivalente es el prefijo condicional de Uno `win:` (`xmlns:win="http://schemas.microsoft.com/winfx/2006/xaml/presentation"`):
   `<win:Style …>`, `<win:DataTemplate …>`, `<win:Border.Background>` se compilan en Windows y se
   descartan (con todo su subárbol) en Skia. Solo vale para elementos del namespace por defecto; un
   control propio fuera del subconjunto (`<chats:ChatTextBox>`) se resuelve con un stub inerte en
   `Telegram.Linux/Xaml/*Stubs.cs` que conserva los miembros que tocan el XAML y el code-behind.
   **Y cuando lo que hay que quitar no es el tipo sino la fila** —un `<controls:SettingsButton>` que
   navega a una página que no está—, `win:` tampoco vale (el prefijo nombra el namespace por
   defecto, no `controls:`). Dos recetas, las dos usadas en la sección de Ajustes (2026-08-24):
   envolver no sirve dentro de un `ItemsControl` (un `<win:StackPanel>` alrededor cambiaría los
   ítems en Windows), así que **se le pone `x:Name` y se quita o se colapsa desde el code-behind en
   un único bloque `#if LINUX`** (`Navigation.Items.Remove(...)`, `Help.Children.Remove(...)`,
   `Fila.Visibility = Collapsed`). Un solo bloque, el XAML compartido intacto salvo el `x:Name`, y
   el rebase sigue siendo mecánico. `<win:StackPanel>`/`<win:GridView>` sí valen cuando el elemento
   a descartar es del namespace por defecto — comprobado en el código generado: con ellos no queda
   ni el campo con `x:Name`, así que los handlers que solo ese subárbol referencia pueden irse
   enteros bajo `#if !LINUX`.
   **Y una tercera receta, medida el 2026-08-27: `win:` vale también sobre un ATRIBUTO**, no sólo
   sobre un elemento (`win:RecentUserHeadChanged="…"`, `win:Background="…"`). Comprobado en el
   `.g.cs` generado para Skia: el atributo prefijado no aparece, y el XAML de Windows queda igual.
   Es lo más barato cuando lo que sobra es **una sola propiedad o un solo manejador** de un control
   que sí entra — y es obligatorio cuando el manejador no compila (ver la nota del generador XAML y
   los tipos sin cualificar en §6).
2. **Lo nuevo va en `Telegram.Linux/`**, en estas carpetas (namespace = el original de Unigram para que
   el código compartido compile sin cambios):
   - `Native/` — sustitutos managed de `Telegram.Native.*` (namespaces `Telegram.Native`,
     `Telegram.Native.Controls`, `Telegram.Native.Calls`, `Telegram.Native.Media`, …). Primero stubs
     inertes; después P/Invoke a `libunigram-native.so` (fase 2) o implementaciones Skia (fase 3).
     **Ficheros de datos de los motores nativos** (`langid_model.smfb.jpg`, `grammars.dat`): en
     Windows eran rutas relativas al paquete (`"Assets\\..."`), que aquí no resuelven porque no hay
     paquete y el directorio de trabajo es desde donde lance el usuario. Se resuelven en C# con
     `Native/NativeAssets.cs` (anclado en `AppContext.BaseDirectory`, no en el CWD) y se pasan a la
     ABI como argumento; el csproj los copia a `Assets/` junto al binario. Cualquier motor nuevo que
     necesite un fichero de datos debe seguir ese camino, nunca una ruta relativa.
   - `Platform/` — servicios Linux: rutas XDG, ajustes, info de dispositivo, cultura, diagnósticos…
     y `Platform/DBus/` — **toda la integración de escritorio** (notificaciones fdo, contador del
     lanzador, icono de bandeja `StatusNotifierItem` + `dbusmenu`, el `.desktop`, la **instancia
     única** y `org.freedesktop.Application`, el **autoarranque**, el **tiempo de inactividad**, el
     **proxy del sistema** y, desde la fase 5, el **reproductor MPRIS2** `MprisPlayer.cs` — las
     teclas de medios, los controles del panel de GNOME y los botones del casco Bluetooth, que en
     Windows eran `SystemMediaTransportControls`). Esa carpeta es deliberadamente **ciega a los tipos de Unigram**: habla
     en cadenas, bytes y callbacks, y quien conoce chats y mensajes es
     `Telegram/Services/NotificationsService.cs` (notificaciones) y
     `Platform/DBus/DesktopActivator.cs` (activación: es el único fichero de la carpeta que toca
     `WindowContext`, y por eso el único que no se puede enlazar en una consola). El proxy sigue la
     misma regla: `DesktopProxy` contesta un `DesktopProxySettings` propio y quien lo convierte en
     `Td.Api.Proxy` es `Platform/ProxyService.cs`. Y el reproductor MPRIS también: `MprisPlayer`
     habla en cadenas, números y callbacks, y quien conoce listas de reproducción y mensajes es
     `Platform/MediaTransport.cs` — que además es quien conecta el
     `SystemMediaTransportControls` declarado en `Xaml/` con el objeto del bus, de forma que el
     `Services/PlaybackService.cs` **compartido** alimenta el panel de GNOME sin saber que existe
     D-Bus. Es lo que permite ejercitarla entera desde una
     consola (`unigram-linux/spikes/DesktopSpike` para notificaciones, contador y bandeja;
     `unigram-linux/spikes/ActivationSpike` para el resto), que es como se ha verificado.
   - `Graphics/` — sustitutos de Win2D (`Microsoft.Graphics.Canvas.*`) sobre SkiaSharp y
     `Windows.Graphics.IGeometrySource2D`.
   - `Xaml/` — controles base (`ControlEx`, `GridEx`, `HyperlinkButtonEx`, `FormattedTextBlockBase`,
     `AnimatedImageBase`…) que en Windows eran C++/WinRT y aquí son C# sobre Uno. Desde la fase 5
     también `LinuxVideoPlayer` (`VideoPlayerBase`), el reproductor de vídeo de la galería: **mpv
     pone el sonido y el reloj, nuestro decodificador de FFmpeg pone los píxeles**, presentados
     contra el `time-pos` de mpv. La ruta Windows (`NativeVideoPlayer`, SwapChainPanel + D3D11 de
     libvlc, y `WebVideoPlayer` sobre WebView2) se queda donde está; los dos exponen la misma
     superficie, así que `GalleryContent` no distingue cuál tiene.
   - `Hubs/` — versiones Linux de los hubs que referencian todo el proyecto (`App`, `BootStrapper`,
     `WindowContext`, `Session.Registrations`, `TLNavigationService`) **solo si** no basta con `#if`.
3. **No se compila en paralelo.** Un único `dotnet build` por ronda (lo lanza el orquestador o el
   agente "builder"); los agentes de corrección solo editan. Log de cada ronda en
   `unigram-linux/fase1/build-NN.log`.
   **Y esta regla tiene dientes, comprobado el 2026-08-24**: dos agentes compilando a la vez sobre
   el mismo `bin/Debug/net10.0-desktop/` producen un binario que **arranca, pinta la ventana y
   luego deja de progresar** — proceso vivo, 32 hilos a 0 % de CPU, ni un `DispatcherTimer` más, es
   decir el mismo síntoma exacto que una excepción que mata el dispatcher. Se persiguió durante
   varias rondas como si fuera un fallo de la app. Antes de dar por bueno un "se congela", comprobar
   que no hay otro `dotnet build` ni otro `MSBuild.dll` en marcha (`pgrep -f 'dotnet build'`,
   `pgrep -f MSBuild.dll`) y que nadie está tocando los fuentes
   (`find Telegram Telegram.Linux -newermt '-120 seconds'`).
   Y el `pkill` de la app: el patrón `"^$HOME/.dotnet/dotnet Unigram"` de más abajo vale
   para `dotnet Unigram.dll`, pero `dotnet run` lanza el **apphost**, que en la tabla de procesos es
   `/home/…/bin/Debug/net10.0-desktop/Unigram`. Para matar ese hace falta
   `pkill -f 'net10.0-desktop/Unigram$'`.
4. **No tocar**: `Telegram.Native/`, `Telegram.Native.Calls/` (C++ Windows), `Telegram.csproj`,
   `Telegram.Modern.csproj`, `Package.appxmanifest`, `Strings/*.resw`, `Libraries/` (salvo el parche
   TDLib ya aplicado). No añadir paquetes NuGet sin anotarlo en `unigram-linux/HANDOFF.md`.
   Paquetes añadidos hasta ahora por el port: **`Tmds.DBus.Protocol` 0.92.0** (fase 4, integración
   de escritorio). `Telegram.Stub/` sí se lee —de ahí salen los tres `.ico` de la bandeja, que el
   csproj despliega como `Assets/Tray*.ico`—, pero no se compila ni se modifica.
5. **Fase 1 = login real + lista de chats + historial.** Lo que no entre ahí se desactiva con `#if`,
   no se reimplementa todavía (llamadas, grabación, WebView, pagos, stickers animados, Win2D de
   gráficas…). Los stubs devuelven valores neutros que no rompan el flujo; nunca lanzan
   `NotImplementedException` en rutas que el login/lista/historial recorren.
   **Los mensajes de servicio ya están todos** (2026-08-25): `Telegram/Controls/Messages/Service/`
   entra **entero** en el subconjunto y `Telegram.Linux/Xaml/ServiceContentStubs.cs` —los diez
   cascarones que heredaban de `MessageService` sin dibujar nada y dejaban una píldora gris vacía—
   **ya no existe**. Entraron `MessagePhotoContent` (cambio de foto de chat, foto sugerida y mención
   de historia), `MessageSuggestBirthdateContent`, `MessageChatSetBackgroundContent`,
   `MessageGiveawayPrizeStarsContent`, `MessageGiftContent`, `MessageUpgradedGiftContent`,
   `MessageUpgradedGiftPurchaseOfferContent`,
   `MessageChatHasProtectedContentDisableRequestedContent`, `MessageHeaderAccountInfoContent` y
   `MessageHeaderMessageTopicContent` (el separador de tema de los foros). Casi todos tal cual; lo
   que costó fue lo de debajo: `chats:ChatBackgroundPresenter` (Win2D) tiene sustituto propio en
   `Telegram.Linux/Xaml/`, `Controls/Cells/ForumTopicCell.Icon.cs` y `Controls/OrbitGenerator.cs` son
   trozos partidos de dos ficheros que el subconjunto no puede tragar enteros, y `PatternBackground`
   + `ProfilePatternCover` pisaron tres trampas de composición que están en la regla 6.
6. **Las APIs que Uno Skia no implementa** (ver `unigram-linux/fase1/scoping-notes.json`):
   `XamlDirect`, `ItemsUpdatingScrollMode`, `FirstCacheIndex/LastCacheIndex`,
   `ChoosingItemContainer`, `GetScrollViewerManipulationPropertySet`, `ScrollViewer.ViewChanging`,
   `DirectManipulation*`, `RichEditBox.Document`, `CoreWindow`, `ApplicationView` (parcial:
   `GetForCurrentView()` y varios miembros CONTESTAN — TitleBar corre en cada construcción de
   VoipPage, `IsViewModeSupported` está medido —; lo muerto son los eventos, `VisibleBoundsChanged`
   nunca dispara, y la familia de pantalla completa, ver más abajo), `CoreApplication`,
   `Windows.Data.Json`, `Win2D`, `WebView2`, `SwapChainPanel`, `InteractionTracker` (parcial).
   Sustituir según `unigram-linux/fase1/scoping-notes.json` y `tools/migrate-namespaces.sh`.
   A esa lista se añaden dos que **compilan y no fallan, simplemente no hacen lo que dicen**:
   - `CompositionTarget.Rendering` no es un reloj de fotogramas en Uno: se dispara **~1 vez por
     segundo**, no una por fotograma compuesto. Todo lo que se anime desde ahí (AnimatedImage,
     `VisualUtilities.QueueCallbackForCompositionRendering`, el tilt, `CompositionVSync`,
     `PremiumProgressBar`/`PremiumSlider`) se queda a 1 fps. Usar
     `Telegram.Common.CompositionRenderingClock` (`Telegram.Linux/Xaml/`) bajo `#if LINUX`; el
     patrón está aplicado ya en `Controls/AnimatedImage.cs`, `Common/VisualUtilities*.cs`,
     `Composition/CompositionVSync.cs`, `Controls/PremiumProgressBar.cs`, `Controls/PremiumSlider.cs`
     y `Views/Gifts/Popups/GiftCraftPopup.xaml.cs` (los cuatro últimos aún fuera del subconjunto:
     el `#if` ya está puesto para que no sean una trampa cuando entren).
     **Matiz medido después** (`unigram-linux/spikes/ClockSpike`, ventana Uno aislada en la SP4):
     ese evento **cuenta fotogramas compuestos**, y su frecuencia la fija lo que esté invalidando
     la ventana — 38 Hz con la ventana quieta y 60 Hz si el propio handler repinta. Las ~1 vez por
     segundo del hallazgo original no se reprodujeron fuera de la app, así que **la cifra hay que
     volver a medirla dentro de Unigram**; lo que sí es firme es que su ritmo depende del resto de
     la app y el del sustituto no (59-60 Hz en los dos casos), que es la razón por la que se
     sustituye.
   - `CompositionTarget.Rendered` **nunca** se dispara. Confirmado por dos vías independientes:
     contador a cero en toda una ejecución de la app y también en el spike (0 ticks con 180
     fotogramas reales saliendo por segundo), y el propio analizador de Uno lo dice en tiempo de
     compilación — `Uno0001: CompositionTarget.Rendered is not implemented in Uno` (en
     `Telegram.Linux` no se ve porque `Uno0001` está silenciado, ver §11).
     Sustituto: **`Telegram.Common.CompositionRenderedClock`** (`Telegram.Linux/Xaml/`), mismo
     contrato de suscripción que el reloj de arriba y **movido por él**: despacha en el **segundo**
     tick de `CompositionRenderingClock` posterior a la suscripción, o sea con **un periodo de
     fotograma completo garantizado** por delante (medido: 32,6 ms mínimo, 34 ms de media). No es
     "el fotograma ya está en pantalla" —eso no se puede observar en Uno—, pero es lo que sus dos
     únicos consumidores necesitan: `Collections/SynchronizedList.cs` solo pide **un fotograma** de
     margen para que la captura de `CompositionVisualSurface` de la fila tenga un commit donde
     capturar (y además revalida su cuenta contra el origen en cada flush, así que un disparador
     aproximado cuesta latencia, nunca corrección), y `QueueCallbackForCompositionRendered` solo
     pide que el callback **corra** (si no, `ContentPopup` deja colgada para siempre la tarea que
     espera `ShowQueuedAsync`). Aplicado con `#if LINUX` en esos dos sitios.
   - `XamlRoot.RasterizationScale` contesta **1** aunque la pantalla sea 2×, y **no** llega ningún
     `XamlRoot.Changed` que lo corrija después. El escalado sí se aplica a la maquetación (ventana
     de 2200×1440 físicos que maqueta como 1100×720), así que el síntoma no es un tamaño mal puesto
     sino **todo lo que se rasteriza a mano a la mitad de resolución y lo estira el compositor**:
     con `DecodeFrameType="Logical"`, cada sticker, emoji, GIF y video-sticker salía de 222×222
     sobre 444 píxeles físicos. Sustituto: `AnimatedImageBase.ResolveRasterizationScale`
     (`Telegram.Linux/Xaml/`), que cae a `Telegram.Common.DisplayScale.Current` (el Xft.dpi de donde
     Uno saca ese escalado, ya leído antes de que exista la ventana) cuando el `XamlRoot` no da nada
     mejor, y deja mandar al `XamlRoot` en cuanto conteste >1 — así el apaño se desactiva solo.
     Aplicado con un `#if LINUX` de una línea en `Controls/AnimatedImage.cs`. Los demás consumidores
     (`EditMediaPopup`, `SendLocationPopup`, `RichMathImage`, `OverlayWindow`, `FormattedTextBox`,
     `Composition/Extensions.cs`, `CompositionDustVisual`, `WindowContext.RasterizationScale`) están
     hoy fuera del subconjunto: mismo patrón cuando entren.
   - **`TextBox.TextChanging` no sirve para editar el texto ni el cursor.** Uno lo dispara, y lo
     dispara *síncrono* dentro del setter de `Text` (`TextChanged`, en cambio, va **encolado**: no
     ha llegado cuando el setter vuelve), pero dos cosas de su contrato WinUI no se cumplen:
     `SelectionStart` leído ahí dentro es el cursor **anterior** a la edición, no el posterior
     (`BackSpace` al final de `"123"` → lee 3), y **asignar `SelectionStart` ahí dentro se
     descarta** — se relee bien dentro del handler y vale 0 cuando el evento termina. Asignar
     `Text` sí surte efecto, pero provoca **dos** `TextChanged`. Medido en
     `unigram-linux/spikes/TextBoxSpike` (`run-03.log`, ALL CHECKS PASSED).
     Consecuencia real: `Controls/PhoneTextBox.cs` deducía la tecla pulsada comparando longitudes y
     recolocaba el cursor a mano desde `TextChanging`; en Uno el cursor se quedaba clavado en 1, así
     que cada dígito nuevo entraba por delante y el número salía del revés — teclear
     `+541155512345` daba `+54 321555114554`, `BackSpace` no hacía nada y el país detectado era
     Rusia (`spikes/TextBoxSpike/phonetextbox-before.log`). Además `Started()` indexa
     `_previousText[SelectionStart]`, que con el cursor al final es `IndexOutOfRangeException`.
     Sustituto: hacerlo **todo en `TextChanged`** (ahí `Text` es lo que el usuario acaba de escribir
     y `SelectionStart` el cursor de después, los dos fiables) y no adivinar la tecla: **contar los
     dígitos a la izquierda del cursor, reformatear la cadena entera a partir de los dígitos y
     devolver el cursor detrás de ese mismo dígito**. Insertar, borrar, pegar y reemplazar una
     selección salen bien sin saber cuál de las cuatro fue. Aplicado con `#if LINUX` en
     `Controls/PhoneTextBox.cs` (`OnPhoneChanged`); la ruta Windows queda intacta. La reentrada la
     corta que el pase sea **idempotente** (`_ignoreOnPhoneChange` solo tapa lo síncrono; el
     `TextChanged` que provoca la propia asignación llega con la bandera ya bajada). El formato es
     el de siempre: `PhoneNumber.Format` da exactamente los mismos grupos que la máscara de
     upstream — comprobado prefijo a prefijo en `unigram-linux/spikes/PhoneFormatCheck`
     (185 prefijos de 17 países, 0 discrepancias).
     Los otros dos campos del login (`AuthorizationCodePage`, `AuthorizationPasswordPage`) **no**
     comparten el patrón: son un `TextBox` y un `PasswordBox` pelados, sin handler de texto y con
     enlace TwoWay a propiedades que no reescriben el valor. Los que sí lo comparten
     (`Controls/Payments/CardTextBox.cs`, `DateTextBox.cs`) están fuera del subconjunto de fase 1:
     mismo patrón cuando entren.
   - **`ContentControl.ContentTemplateRoot` es SIEMPRE `null` si el control tiene plantilla.**
     Uno solo rellena esa propiedad en el caso "content presenter bypass": su
     `IsContentPresenterBypassEnabled` es literalmente
     `Template == null ? !HasDefaultTemplate(GetDefaultStyleKey()) : false`, así que en cuanto hay
     `Template` —y todo `ListViewItem` la tiene— nadie la asigna nunca: la plantilla de ítem la
     expande el `ContentPresenter` que vive DENTRO de la plantilla del contenedor y el resultado se
     queda en el `ContentTemplateRoot` **de ese presenter**, sin propagarse al padre. El fallo es
     silencioso porque el código compartido siempre pregunta con un test de tipo
     (`container.ContentTemplateRoot is ChatCell content`): el test simplemente no casa, no salta
     ninguna excepción, y la celda se queda con lo que declara su XAML. Ese era el motivo de que la
     lista de chats saliera con el nombre vacío, sin avatar y con el `Text="11:10"` de diseño en
     todas las filas. Sustituto: `Telegram.Common.ContentTemplateRootEx`
     (`Telegram.Linux/Xaml/`), `GetContentTemplateRootEx()` / `ContentTemplateRootAs<T>()`, que baja
     al presenter. **Dos avisos para quien lo use**: solo contesta cuando el contenedor ya está en
     el árbol visual (Uno materializa el contenido del presenter desde `ContentPresenter.EnterImpl`,
     y `ContainerContentChanging` se dispara ANTES, desde `ItemsControl.PrepareContainerForIndex`),
     así que hay que reintentar en el `Loaded` del contenedor; y devuelve el primer presenter en
     profundidad, que es la raíz de la plantilla de ítem. Aplicado en
     `Controls/ChatListListView.cs` (un único `#if` en el helper privado `GetCell` + rebind en
     `Loaded`). En el historial se resolvió con un único helper compartido,
     `Extensions.ContentRoot()` (`#if LINUX` dentro; en Windows devuelve la propiedad y nada más).
     **Los cuatro últimos del subconjunto, cerrados el 2026-08-23**: quedaban dos usos con *property
     pattern* (`is MessageSelector { ContentTemplateRoot: MessageBubble bubble }`) en
     `Views/ChatView.Bubbles.xaml.cs` y dos en `Views/ChatView.xaml.cs`
     (`Autocomplete_PointerEntered/Exited`); un patrón de propiedad no se puede reescribir con el
     helper, hay que partirlo en dos comprobaciones. El primero **hacía desaparecer el avatar del
     remitente de todas las burbujas de grupo**: la rama de la foto pegajosa llama a
     `bubble.ShowHidePhoto(false)` por el camino bueno (`selector.ContentRoot()`), pero la rama que
     la vuelve a mostrar preguntaba por `ContentTemplateRoot`, no casaba nunca, y el
     `PhotoRoot.Opacity = 0` se quedaba puesto para siempre — también en los contenedores
     reciclados, que conservan la bandera `_photoCollapsed`.
     **Barrido cerrado el 2026-08-24**: dentro del subconjunto ya no queda ningún
     `ContentTemplateRoot` vivo. Los que faltaban eran `Controls/ChatListListView.cs` (el estado de
     selección múltiple, el `UpdateViewState` al seleccionar un chat, la pulsación larga sobre el
     avatar —que es entrada **táctil**— y el nombre de accesibilidad),
     `Controls/Chats/ChatHistoryViewItem.cs` (los dos peers de accesibilidad del historial y el
     volcado de `INSTRUMENTATION`), `Controls/Messages/MessageSelector.xaml.cs`,
     `Controls/TopNavView.cs` (los estados visuales de las **pestañas superiores**),
     `Controls/TextListViewItem.cs`, `Controls/SelectListView.cs`, `Controls/MultipleListView.cs`,
     `Controls/ContentPopup.cs`, `Controls/ReplyMarkupButton.cs`,
     `Controls/ReplyMarkupInlineButton.cs`, `Common/Extensions.cs` (`ForEach`),
     `Views/Host/RootPage.xaml.cs` y `Views/MainPage.xaml.cs`. `ViewModels/DialogViewModel.Handle.cs`
     y `Common/AnimatedListHandler.cs` ya estaban convertidos. Los únicos que quedan escritos son
     **código muerto de upstream**: los dos métodos de `Controls/TableListView.cs` empiezan por
     `return;`, y el bloque de `ChatListListView.OnContainerContentChanging` que los usa está dentro
     de `#if !LINUX`. Con la sección de Ajustes dentro (2026-08-24) el único
     `ContentTemplateRoot` que entró es el de `SettingsAppearancePage.OnContainerContentChanging`,
     y va dentro del `#if !LINUX` que se lleva la lista de temas de chat entera. Fuera del
     subconjunto siguen las pestañas de perfil y el resto de `Views/Settings/`.
   - **Arrancar la MISMA instancia de `CompositionAnimation` sobre dos visuales lanza — y puede
     dejar la app entera colgada.** `Compositor.RegisterAnimation` de Uno es literalmente
     `_animations.Add(animation, visual)` sobre un `Dictionary` **con la animación de clave**
     (`Uno.UI.Composition/Composition/Compositor.skia.cs`), así que la segunda
     `StartAnimation` con el mismo objeto es `ArgumentException: An item with the same key has
     already been added`. En WinUI compartir una animación entre visuales es legal y Unigram lo hace
     por costumbre. Dónde duele, medido en el log de la app:
     `Views/Host/RootPage.xaml.cs` (`Navigation_PaneOpening`/`PaneClosing`) arrancaba la misma
     `opacity` sobre tres visuales y la misma `scale` sobre dos — y ahí la excepción **se escapa del
     handler de `PaneOpening` por el setter de `SplitView.IsPaneOpen` hasta el despacho de puntero de
     Uno y mata el dispatcher**: el proceso sigue vivo, los 32 hilos a **0 % de CPU**, el panel de
     navegación no se abre y no vuelve a dispararse **ni un `DispatcherTimer`** (ni capturas, ni
     volcado de árbol: por eso el síntoma parece "la app se congela al pulsar el botón de menú").
     `Views/ChatView.xaml.cs` (`ShowHideSideButton`) arrancaba siempre `offset` sobre `field` **y**
     `attach`, y `Controls/Chats/ChatHistoryArrows.xaml.cs` (`ShowHideMessages`) el mismo `translate`
     sobre tres visuales; esas dos sí las cazaba el `NativeDispatcher` y quedaban en el log como
     `NativeDispatcher unhandled exception`. Sustituto: **una instancia por visual**, con una función
     local que la fabrica (`Opacity()`, `Scale()`, `Offset()`, `Translate()`); en Windows el
     resultado es idéntico. Aplicado en los cuatro sitios. Regla: en este port, una
     `CompositionAnimation` **no se comparte**.
     **Quinto sitio, cazado el 2026-08-24 con la galería en pantalla**:
     `Controls/Gallery/GalleryWindow.ShowHideTransport` arrancaba la misma `ScalarKeyFrameAnimation`
     sobre **cuatro** visuales (barra inferior, flecha anterior, flecha siguiente y botón de volver),
     y la excepción salía desde el `Tick` del temporizador de inactividad
     (`Unhandled exception in DispatcherQueueTimer.Tick`), o sea **1,5 s después de abrir la galería
     y sin que nadie tocara nada**. Arreglado con el mismo patrón; medido: 1 excepción antes del
     arreglo, 0 después.
     **Sexto sitio, 2026-08-25, al encender el audio (fase 5)**: la banda de «suena ahora».
     `Controls/MasterDetailView.ShowHideBanner` arrancaba la misma `ScalarKeyFrameAnimation` sobre
     hasta **cuatro** visuales (la banda, `MasterFrame` o `DetailRoot`, y los dos que
     `ChatView.StartBannerAnimation` mueve: `Header` y `ClipperOuter`). No se había visto porque el
     camino era inalcanzable: `IPlaybackService` era inerte y `CurrentItem` siempre null, así que
     **la primera nota de voz que alguien reprodujera** habría sido la primera vez. Arreglado con la
     función local `Translate(from)` en `MasterDetailView`; el caso de `ChatView` no se puede
     resolver así porque la animación es **del llamante** y una `ScalarKeyFrameAnimation` no deja
     leer sus fotogramas clave, de modo que ahí el segundo visual va con una `ExpressionAnimation`
     atada a `header.Translation.Y` (legal porque a `Header` se le acaba de habilitar
     `SetIsTranslationEnabled`) y `CompleteBannerAnimation` la **para** antes de escribir el valor
     final, porque una expresión no termina sola. Regla general que confirma: cuando una animación
     cruza una frontera de API (aquí `IChatPage.StartBannerAnimation`), el receptor no puede
     clonarla; hay que atar el segundo visual al primero.
   - **Una `KeyFrameAnimation` que YA HA TERMINADO sigue mandando sobre su propiedad: escribir el
     valor final sin pararla antes no surte efecto.** Es el hermano de la entrada anterior, y es
     más traicionero porque la animación sí acaba: lo que no acaba es que Uno la tenga registrada,
     y `Compositor.RenderRootVisual` vuelve a evaluarla en cada fotograma
     (`CompositionObject.ReEvaluateAnimation`), machacando cualquier `Properties.InsertVector3`
     posterior. En WinUI el `StopBehavior` por defecto (`LeaveCurrentValue`) hace que escribir
     encima funcione, y por eso el código compartido lo hace sin pensarlo.
     Medido el 2026-08-25 con la banda de «suena ahora»: al ocultarla,
     `MasterDetailView.BatchCompleted` llama a `ChatView.CompleteBannerAnimation`, que **sí** para
     la `ExpressionAnimation` del clipper pero **no** la `ScalarKeyFrameAnimation` de `Header`. Su
     último fotograma clave es −40, así que la cabecera del chat se quedaba 40 px por encima de su
     sitio —es decir, **fuera de la pantalla**— en ese chat y en todos los que se abrieran después,
     hasta cerrar la app (`Grid #Header [535,0 833x48]` en el volcado, contra `[535,40 833x48]` que
     es lo correcto). Arreglado con un `StopAnimation("Translation.Y")` antes de cada escritura, en
     `ChatView.CompleteBannerAnimation` (medido) y en `MasterDetailView.BatchCompleted` para
     `BannerPresenter`, `DetailRoot` y `MasterFrame`, que recorren la misma forma en los dos estados
     en los que son ellos los que se mueven (detalle en blanco y ventana estrecha).
     **Regla: en este port, antes de escribir a mano una propiedad que alguna vez se animó, se para
     la animación.**
   - **`RotationAngleInDegrees` no se puede animar.** Uno registra
     `An exception occurred while setting animation value '220' to property 'RotationAngleInDegrees'`
     y **se traga la animación**: no lanza, simplemente el visual no gira. Visto decenas de veces en
     una sola sesión de la app. Lo mismo con `Size` (`'<0. 24>'` a `Size` en un
     `Vector2KeyFrameAnimation`). Nada que arreglar todavía porque los afectados son decorativos,
     pero conviene no dar por buena una rotación animada sin mirarla.
   - **`SKPath.ParseSvgPathData` contesta `null` para TODO dentro de Unigram** (y funciona fuera).
     Medido sobre `Assets/Background.tgv`: 745 elementos `<path>` encontrados, 745 `null` de vuelta,
     de modo que el fondo del chat salía como el degradado pelado, sin los garabatos. El mismo
     fichero, el mismo `ChatBackgroundRenderer.cs` enlazado y el **mismo** SkiaSharp 3.119.2 y el
     mismo `runtimes/linux-x64/native/libSkiaSharp.so` (md5 idéntico, y `/proc/<pid>/maps` de la app
     confirma que carga ese y ningún otro) sí devuelven la geometría desde
     `spikes/ChatBackgroundSpike`. No se ha encontrado la causa; lo que se ha hecho es dejar de
     preguntar: `ChatBackgroundRenderer.ParseSvgPath` lee el subconjunto de comandos que usan los
     `.tgv` (M/L/H/V/C/S/Q/T/Z, absolutos y relativos) y le pasa a Skia **puntos** en vez de una
     cadena. Verificado contra el resultado anterior de Skia en el spike: mismos 57 063 puntos, misma
     caja (`-0,0,-77,7 – 1440,1,3026,2`) y misma cobertura del tile (15,95 %). Los arcos (`A`/`a`) no
     están implementados a propósito: ningún patrón de Telegram los usa y dibujar un arco como una
     recta sería una forma equivocada en vez de una que falta.
   - **Los stubs de fase 1 que devuelven 0 no siempre son neutros.**
     `StoriesStrip.TopPadding` (`Telegram.Linux/Xaml/MainPageStubs.cs`) devolvía 0, pero ese número
     no es "sin barra de historias": es el hueco que `MainPage` mete en `ChatsList.Margin` para que
     la lista no quede debajo de la barra de título propia (40) y del campo de búsqueda (32). Con 0,
     la lista entera se dibujaba encima de las dos. Ahora hace la misma cuenta que el control real.
     Regla: antes de dejar un stub devolviendo el valor por defecto del tipo, comprobar si alguien
     lo usa como **medida de maquetación**; ahí el valor neutro no existe.
   - **`SoftwareBitmapSource` no pinta nada y `SoftwareBitmap` no suelta sus píxeles.** Medido
     sobre los ensamblados de Uno 6.6.184 que se despliegan (IL de `Uno.UI.dll` y `Uno.dll`, no
     documentación): `Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource.SetBitmapAsync`
     **lanza** `NotImplementedException` — y es lo único que el código compartido hace con ese
     tipo, así que la miniatura borrosa no llegaba a la pantalla ni con `DrawBlurred` implementado;
     además los dos consumidores (`Common/ThumbnailController.cs`, `Common/PlaceholderHelper.cs`)
     envuelven la llamada en `catch { }`, de modo que fallaba en silencio. Del lado de
     `Windows.Graphics.Imaging.SoftwareBitmap`: `LockBuffer` lanza, `CopyFromBuffer`/`CopyToBuffer`
     son **no-ops** que solo registran un aviso, y su `SKBitmap` interno es `internal` de `Uno.UI`,
     así que tampoco sirve de vehículo para los píxeles. Tampoco se puede sustituir por herencia:
     `BitmapImage` es `sealed` y `ImageSource.TryOpenSourceSync/Async` son `private protected`, y
     `WriteableBitmap` fija su tamaño en el constructor (los consumidores crean el `ImageSource`
     antes de saber cuánto mide la imagen). Sustituto: un `using` alias de una línea bajo
     `#if LINUX` — `SoftwareBitmapSource` → `BitmapImage` (el camino que este port ya usa para los
     minithumbnails en `Controls/Cells/ChatCell.xaml.cs`) y `SoftwareBitmap` →
     `Telegram.Native.PixelBitmap`; el `SetBitmapAsync` que a `BitmapImage` le falta es una
     extensión en `Telegram.Linux/Native/Image/PixelBitmap.cs`. Aplicado en
     `Common/PlaceholderHelper.cs`, `Common/ThumbnailController.cs` y `Controls/ImageView.cs`; los
     de fuera del subconjunto (`Common/ImageHelper.cs`, `Controls/Chats/ChatRecordBar.xaml.cs`)
     llevan el mismo patrón cuando entren.
   - **`x:Load="False"` deja un hueco en `Panel.Children` (`ElementStub`).** En WinUI el elemento
     no cargado no ocupa sitio; en Uno sí, así que cualquier código que busque a sus hermanos
     **por índice** se desplaza uno. Caso medido: `Controls/Messages/MessageBubblePanel.cs`
     resuelve `text`/`media`/`third` por posición y decide el footer con
     `third is ReactionsPanel`; con el `ReactionsPanel` del XAML sin cargar, `third` era el stub,
     el test fallaba, `footer` salía **null** y `Grid.GetRow(null)` lanzaba
     `NullReferenceException` **dentro de `MeasureOverride`** — es decir, ninguna burbuja llegaba a
     medirse y **el historial entero salía en blanco**. Sustituto: no buscar por índice lo que se
     puede identificar por posición estable (`Children[^1]` para el footer) o por tipo. Regla: en
     un `Panel` propio, ningún `Children[n]` sin comprobar que `n` no puede desplazarse por un
     `x:Load`.
   - **Un handler de `EffectiveViewportChanged` que lanza deja a la app entera sin layout.**
     `Uno.UI.Xaml.Core.EventManager.RaiseEffectiveViewportChangedEvents` recorre su cola,
     **pone a `default` cada entrada antes de invocarla** y solo hace `Clear()` al terminar el
     bucle: si un handler lanza, la cola se queda con una entrada nula y **todos** los
     `UIElement.UpdateLayout` posteriores mueren con `NullReferenceException`. El disparador aquí
     era que de `EffectiveViewportChangedEventArgs` solo está implementado `EffectiveViewport`:
     `BringIntoViewDistanceX`, `BringIntoViewDistanceY` y `MaxViewport` lanzan
     `NotImplementedException`. Sustituto: calcular la distancia a partir del rectángulo, que en
     ese evento viene **en coordenadas del propio elemento** —
     `max(max(viewport.X - ancho, -viewport.Right), 0)` y su equivalente en Y. Aplicado en
     `Telegram.Linux/Xaml/AnimatedImageBase.cs` y en `Views/ChatView.xaml.cs`
     (`HeaderUnread_EffectiveViewportChanged` + `UpdateMessagesHeaderPadding`, vía
     `ViewportDistanceY`).
   - **Las métricas de texto de `Telegram.Native` no se pueden recalcular: hay que pedírselas a
     Uno.** En Windows salen de DirectWrite (`PlaceholderImageHelper.cpp`), y coinciden con las
     del `TextBlock` porque el texto XAML **también** es DirectWrite. En Skia no: Uno maqueta en
     `Microsoft.UI.Xaml.Documents.UnicodeText` — ICU para el bidi y los puntos de corte de línea,
     HarfBuzz para el shaping, y una cadena de *fallback* por punto de código
     (`FeatureConfiguration.Font.SymbolsFont` → `LinuxFontFallback` → `SKFontManager.MatchCharacter`).
     Rehacer eso sobre `SKFont` daría números que **no** son los que se pintan, que es justo el
     fallo: `MessageBubblePanel` encaja la **hora** al final de la última línea preguntando
     `ContentEnd(...)`, y con el stub de fase 1 (`(0, float.MaxValue)`) la hora salía **encima
     del texto**. Sustituto: `Telegram.Linux/Native/Text/UnoTextLayout.cs` mide con un
     `TextBlock` **suelto** (fuera del árbol) configurado como el que renderiza el mensaje, y le
     consulta la maquetación resultante. Cinco cosas que hay que saber:
     - `TextBlock.ParsedText` es `internal`, y su interfaz `IParsedText` también; se alcanzan por
       **reflexión** (`Rect GetRectForIndex(int)`, `(int start, int length, bool firstLine, bool
       lastLine, int lineIndex) GetLineAt(int)`, `bool IsBaseDirectionRightToLeft`). Resuelto una
       vez y con caída a "sin respuesta" si falla, que es lo que los llamantes ya toleran. Medido
       contra Uno 6.6.184.
     - **La fuente hay que dársela**: el helper de Windows fija `"Segoe UI Emoji"` sobre una
       colección propia; aquí se toma `EmojiThemeFontFamily` de `Theme.Current` (que en Linux es
       **una** familia, no la lista separada por comas de Windows — ver `Theme.UpdateEmojiSet`) y
       `Theme.Current.MonospaceFontFamily` para las entidades `Monospace`. Medir con otra fuente
       descoloca la hora aunque `ContentEnd` esté bien.
     - **Un `TextBlock` cuyo texto se pone por `Text` es el doble de rápido que uno construido con
       `Inlines`**, y tocar `Inlines` una sola vez desactiva ese camino
       (`TextBlock.UseInlinesFastPath`) para siempre en esa instancia: por eso hay **dos** bloques
       de borrador, y el de `Inlines` solo se usa cuando el texto lleva negrita/cursiva/monoespaciado.
     - **Maquetar un texto nuevo cuesta de verdad**: ~0,5 ms para 15 caracteres y ~1-2 ms para 126,
       y no es la reflexión (0,4 µs por `GetRectForIndex`) sino la maquetación de Uno — el mismo
       precio que ya paga el `TextBlock` de verdad al pintar ese mensaje. `ContentEnd` corre dentro
       de `MeasureOverride`, una vez por burbuja y por pasada, así que **está memoizado** por
       (texto, entidades por referencia, tamaño, ancho); repetir una medida baja a 0,5 µs. Los
       otros cuatro métodos son de hover, resaltado y cita: no hace falta.
     - **`GetRectForIndex` no es de fiar dentro de una tirada bidi**: en un párrafo árabe el último
       carácter contesta una posición *más allá* del ancho de la caja, y el índice 0 siempre
       contesta el desplazamiento de alineación. Consecuencias aplicadas: `ContentEnd` de un
       párrafo de derecha a izquierda contesta a propósito **el ancho entero** (respuesta
       conservadora: la hora baja a su propia línea, que es lo correcto porque una línea RTL va
       pegada al borde derecho), y `RangeMetrics` recorre **todos** los índices del tramo en cada
       línea en vez de mirar solo sus dos extremos.
     Verificado fuera de la app en `unigram-linux/spikes/MetricsSpike` (consola, sin ventana:
     Uno mide texto sin host con solo dos empujones —`NativeDispatcher.HasThreadAccessOverride` y
     `UnicodeText.ICU.Init`, los dos cosa del host—), 61 comprobaciones y un PNG por caso con el
     texto dibujado **en las posiciones que da Uno**, la marca de `ContentEnd` y la caja de la hora.
   - **`ConnectedAnimationService.GetForCurrentView()` lanza** y `ChatView.AnimateEntrance()` la
     llamaba **fuera** de su propio `try`, desde `OnNavigatedTo`: la excepción tumbaba la
     navegación entera al chat, así que la página se creaba y nunca se mostraba. Desactivada con
     `#if LINUX` (solo anima la transición de entrada). `PrepareExit()` la llama también, pero
     desde dentro de un `try`.
   - **`Window.CoreWindow` es `null` en Uno.** `ChatView.OnLoaded` suscribía
     `Window.CoreWindow.CharacterReceived`: `NullReferenceException` en cada apertura de chat, que
     se llevaba por delante `ViewVisibleMessages()` y el foco del cuadro de texto. Bajo `#if LINUX`
     no se suscribe; lo que se pierde hasta que haya sustituto de `CoreWindow` es escribir en el
     chat sin pinchar antes en el campo.
   - **Las `ExpressionAnimation` de Uno no entienden `this`, y `Translation` solo se puede leer de
     un visual que la tenga habilitada.** `this.Target.Size.Y` lanza
     `ArgumentException: Unrecognized identifier 'this'` al arrancar la animación, y
     `child.Translation.X` sobre un visual sin
     `ElementCompositionPreview.SetIsTranslationEnabled` lanza
     `Exception: Unable to get property 'Translation'`. Las dos salían de
     `ChatView.ViewVisibleMessages` (cabecera de fecha, cabecera de tema, foto y resumen
     pegajosos) y **abortaban el método entero en cada pasada de layout**, con lo que no se
     marcaba nada como leído ni se reproducía media. Sustituto: pasar el objetivo como parámetro
     de referencia normal (`target.Size.Y` + `SetReferenceParameter("target", …)`) y quitar el
     término `Translation` de las expresiones cuyo visual no la tiene (vale 0 de todos modos).
     **Cuidado con el reemplazo, que se aplicó a medias (corregido el 2026-08-24)**: la cabecera de
     **tema** (`_forumTopicHeaderTranslation` y `_forumTopicHeaderScale`) se quedó con
     `target.Size.Y` en la expresión y **sin** su `SetReferenceParameter("target", …)`, que sí tiene
     la de fecha. Un identificador sin parámetro de referencia lanza igual que `this`, y esas dos
     animaciones solo se arrancan **en foros y en grupos de mensajes directos** (`ViewModel.IsForum`
     y una fila `MessageHeaderMessageTopic` visible), así que el fallo estaba escondido justo en el
     caso que nadie miraba. Regla: cada identificador que aparezca en una `ExpressionAnimation`
     tiene que tener su `SetReferenceParameter` en el mismo bloque; no basta con copiar la
     expresión del vecino.
   - **Una excepción dentro de un callback de contenedor deja el historial MUERTO para siempre, y
     sin dejar rastro.** `VirtualizingPanelLayout.UpdateLayout` (Uno 6.6.184, leído del IL de
     `Uno.UI.dll`) hace `OwnerPanel.ShouldInterceptInvalidate = true` al entrar y `= false` al salir
     **sin `try`/`finally`**, y `UIElement.InvalidateMeasure` se sale de inmediato mientras esa
     bandera está puesta. Dentro de esa ventana Uno levanta **todo** lo que tiene que ver con
     materializar filas: `ItemsControl.PrepareContainerForIndex` → `PrepareContainerForItemOverride`
     → `ContainerContentChanging`, y el `Loaded` del contenedor (porque `AddView` hace
     `OwnerPanel.Children.Add`, o sea que el contenedor entra al árbol **dentro** de `FillLayout`), y
     `ClearContainerForItemOverride` al reciclar. Si cualquiera de esos handlers lanza, el
     `ItemsStackPanel` se queda **sordo a `InvalidateMeasure` para el resto del proceso**: mide 0 px
     de alto, no materializa ni un contenedor, y **no se recupera al abrir otro chat**, porque
     `MasterDetailView` reutiliza el mismo `ChatView` y el mismo panel. El síntoma es un historial
     vacío **sin ninguna excepción cerca**, y por eso se persiguió como un fallo de datos de los
     foros. Defensa aplicada: `try`/`catch` con un `Logger.Error` que nombra el mensaje y su
     `MessageContent` en `ChatView.OnContainerContentChanging` (envoltorio `#if LINUX`) y en las dos
     invocaciones de `ChatHistoryView` (`ContainerCreated` / `ContainerRecycled`, helper `Safely`).
     Una fila mala cuesta ahora una fila y una línea de log, no el historial entero.
     `UNIGRAM_HISTORY_PROBE` lo confirma en una sola ejecución: cuando hay ítems y 0 materializados,
     la línea `probe: view …` añade `intercepting invalidate`, `layout ItemsControl` y
     `panel desired` leídos por reflexión (las tres cosas son `internal` de Uno). Con
     `intercepting invalidate True` es esto; con `layout ItemsControl NULL` es lo otro
     (`MeasureOverride` devuelve `Size(0,0)` si el layout perdió su `ItemsControl`, que se anula en
     el `Unloaded` del panel y solo se repone en su `Loading`).
   - **Y una TERCERA, que era la que quedaba viva: una pagina insertada por delante deja el ancla
     de `VirtualizingPanelLayout` FUERA de la ventana de relleno, y ahi se queda.** Medida el
     2026-08-26 con la sonda leyendo por reflexion el interior del layout (los campos nuevos
     `layout lines ... seed ...@... adj ... avgLine ... window ...` de `HistoryProbe.PanelState`).
     La cadena, entera:
     1. Llega la pagina de mensajes viejos como un lote de inserciones en el indice 0 mientras el
        contenido todavia cabe en el viewport, o sea con `ScrollableHeight == 0`. Ahi
        `IsScrolledToEnd()` (`VerticalOffset >= ScrollableHeight`) es cierto sin mas.
     2. En la medida siguiente, `ApplyCollectionChanges` mueve la semilla dinamica
        `_dynamicSeedStart += (insertados x _averageLineHeight)` y **despues**
        `ApplyScrollAdjustment` toma su rama `IsScrolledToEnd()`: ni mueve el `ScrollViewer` ni
        guarda `_scrollAdjustmentForCollectionChanges`.
     3. La unica puerta de entrada a materializar algo es la guarda de `FillLayout.FillForward`,
        `GetItemsEnd() < ExtendedViewportEnd + extentAdjustment`, y sin ninguna linea
        `GetItemsEnd()` **es** la semilla. Medido en el fallo: 26 insertados,
        `_averageLineHeight` 244,0 -> semilla en 6 344 px con la ventana acabando en 936 y el
        ajuste en 0. El bucle no entra ni una vez, y `FillBackward` no puede arrancar desde cero
        (se sale en la primera vuelta si no hay primera linea). Cero lineas.
     4. Cero lineas hace que `EstimatePanelExtent` devuelva un `0.0` literal, el panel mide 0 px de
        alto, el `ScrollViewer` recorta su extent al viewport, `ScrollableHeight` se queda en 0 --
        y desde ahi **no queda nada que pueda invalidar la medida**: ni `ViewChanged`, ni
        `SizeChanged`, ni mas cambios de coleccion. Ese es el "y ya no se recupera".

     Uno lo imprime el mismo, con `UNIGRAM_UNO_LOG=Debug UNIGRAM_UNO_LOG_ONLY=VirtualizingPanelLayout`.
     La pasada que muere:

     ```
     Calling MeasureOverride(), availableSize=833,Infinity, _availableSize=833,235.5
       NoOfItems=38 FirstMaterialized=(0,13) LastMaterialized=(0,13)
       ExtendedViewportStart=0 ExtendedViewportEnd=936 GetItemsStart()=0 GetItemsEnd()=235,5
     ScrapLayout() seed index=(0,12) seed start=0
     Called UpdateLayout(), NoOfItems=38 FirstMaterialized= LastMaterialized= ... extentAdjustment=
     EstimatePanelSize() => 0 -> 833,0
     ```

     Los tres testigos estan ahi: **ni un solo `AddView`** entre `ScrapLayout` y `UpdateLayout` (o
     sea que `FillForward` no entro ni una vez), `extentAdjustment=` **vacio** (o sea que
     `_scrollAdjustmentForCollectionChanges` nunca se puso, que es exactamente la rama
     `IsScrolledToEnd()`), y `EstimatePanelSize() => 0`. Y la pasada de despues, la que el arreglo
     provoca:

     ```
     Calling MeasureOverride(), availableSize=833,Infinity, _availableSize=833,0 NoOfItems=38 ...
     ScrapLayout() seed index= seed start=0
     AddView() finalRect=0,0,297,57 ... DC=Telegram.ViewModels.MessageViewModel
     ```

     semilla nula, arranque en 0, y a rellenar.

     La firma es indistinguible de las dos causas de arriba si solo se miran los tres campos viejos
     (`intercepting invalidate False`, `layout ItemsControl set`, `panel desired 0.0`); lo que la
     separa son los nuevos: `layout lines 0`, `adj null` -- o sea que el ajuste pegado **no** es la
     causa -- y `measureDirty False`, que dice que no va a haber otra medida.

     **La pasada que lo arregla es la siguiente**: `UpdateLayout` termina con
     `SetDynamicSeed(null, null)`, asi que una medida sin cambios de coleccion pendientes vuelve a
     sembrar desde cero y rellena hacia delante desde el primer item. Comprobado con
     `UNIGRAM_HISTORY_POKE` **antes** de escribir el arreglo: un `InvalidateMeasure` pelado sobre el
     panel colapsado devolvio el historial en 200 ms. Arreglo aplicado en `ChatHistoryView`,
     `#region Ask for one more measure when a layout pass materialized nothing`: tras cualquier
     pasada de maquetacion que deje el historial con mensajes y el panel midiendo 0, se pide una
     medida mas, con un contador que se reinicia en cuanto el panel mide algo y que se planta a las
     32 seguidas. Verificado en el mismo chat y la misma maniobra:
     `anchor Add@0 x26 items 16->41 ... panel desired 0.0, layout lines 0` seguido de
     `The history measured 0 px with 41 messages` y de
     `items 41, realized [37..40] = 4, extent 9738.0`, estable. Vale igual para el historial dentro
     de un tema de foro, que **era el mismo fallo**: `Add@0 x27 items 15->41` con `avgLine 470,5`.

     **El contador de la guarda cuenta intentos de UNA situacion, no de toda la vida de la vista**
     (corregido el 2026-08-26, en la tanda de cierre). `LayoutUpdated` es un evento por pasada de
     maquetacion **de toda la ventana**, no de este panel: cualquier cosa que provoque pasadas
     —un sticker animado, el churn del defecto de abajo— gasta las 32 en bastante menos de un
     segundo, y un contador que solo se reiniciaba al medir bien dejaba la guarda desarmada
     justo mientras el panel seguia colapsado, que es exactamente el estado del que existe para
     salir. O sea: la version original del arreglo volvia a meter la mitad mala del fallo («y ya
     no se recupera») por la puerta de atras. Ahora se rearma con cualquier cosa que cambie lo
     que la pasada siguiente haria —`Items.Count` o la altura del viewport— y se reinicia entero
     en `OnUnloaded`.

     **Lo que este arreglo NO cierra, medido el 2026-08-26 con 6 aperturas del mismo tema de foro:
     1 de 6 acaba igual de inservible**, no vacia sino con **todas** las filas dibujadas como el
     texto `Telegram.ViewModels.MessageViewModel`, estable durante 110 s y doce desplazamientos
     (`fase1/cierre-historial/r1-historial.log`, `r1-fin.png`). No es un fallo del arreglo: es el
     defecto de la entrada siguiente, que la recuperacion enciende. Y la traza da por fin **el
     encendido exacto**, que hasta ahora se describia solo como «cualquier salto mayor que un
     viewport»:

     ```
     ...ChatHistoryView.cs:445 The history measured 0 px with 27 messages ... (1/32)
     +0,25 s  probe: scroll offset 689,0 of 689,0   extent 1313,0   realized [7..26] = 20   <- recuperado
     +0,57 s  probe: scroll offset 7154,5           extent 7778,5   realized [17..21] = 5
     +0,85 s  probe: scroll offset 8730,0           extent 9354,0   realized [185..212] = 0 <- fuera de rango
     ```

     Entre la primera y la segunda linea el log trae un `mov,mp4,...` de ffmpeg: **es la primera
     medida real de una fila de video**. `_averageLineHeight` sube de golpe, `EstimatePanelExtent`
     extrapola el resto de la lista con el valor nuevo, el extent pasa de 1 313 a 7 778, y el
     anclaje de borde lejano (`VerticalAnchorRatio = 1`) persigue el fondo: el offset salta de 648
     a 7 154 **en un solo paso**, 6 506 px con un viewport de 624. Ese es el salto que dispara la
     rama de la entrada siguiente. Regla para quien lo ataque: el encendido no es el desplazamiento
     del usuario, es **el extent estimado cambiando de golpe cuando se mide la primera fila de
     media**, y el anclaje de borde lejano convirtiendo ese cambio en un salto.
   - **ARREGLADO el 2026-08-26 (medido): el layout materializaba indices que no existen y se ponia
     a dar vueltas.** `VirtualizingPanelLayout.OnScrollChanged`, en su rama de salto grande
     (`|delta| > ViewportExtent`), tira toda la disposicion y la vuelve a sembrar por aritmetica,
     `n = (int)(ScrollOffset / _averageLineHeight); SetDynamicSeed(fila n-1, n * _averageLineHeight)`,
     **sin acotar a `NumberOfItems`**; y `ItemsControl.GetIncrementedItemIndex` solo devuelve null
     cuando la fila es exactamente `NumberOfItems - 1`, asi que una semilla ya pasada del final
     sigue entregando filas. `ItemFromIndex` devuelve null fuera de rango y se preparan contenedores
     sin item, que miden poco, que hunden `_averageLineHeight`, que aleja mas la semilla. Medido el
     2026-08-26 en un tema con 172 mensajes: ciclo de seis estados a ~8 Hz con
     `realized [305..332] = 0`, `[336..363] = 0`, `[388..416] = 0`, `avgLine 45,5`, un extent
     inventado entre 14 828 y 21 264 px y 2 800 contenedores creados en 40 s. En pantalla es otra
     vez un historial vacio (la vista aparcada a 14 000 px de donde estan los mensajes), y a veces
     con filas dibujadas como el texto `Telegram.ViewModels.MessageViewModel`. Lo dispara cualquier
     salto mayor que un viewport, y el anclaje de borde lejano (`VerticalAnchorRatio = 1`) los
     produce el solo persiguiendo un extent que es una estimacion.

     **El arreglo, en dos piezas, las dos en `Telegram/Controls/Chats/ChatHistoryView.cs`:**
     1. `#region Hold the far edge ourselves, in steps Uno cannot mis-seed`. El anclaje de Uno pasa
        a `VerticalAnchorRatio = double.NaN` y el pegado al fondo lo hace el port desde
        `LayoutUpdated`, **en pasos de medio viewport**. Esa es la diferencia que importa: la rama
        de la semilla aritmetica esta guardada por `Math.Abs(delta) > ViewportExtent`, asi que
        cualquier movimiento mas corto entra por el camino incremental, que rellena desde las
        lineas que ya existen y termina en `NumberOfItems - 1`. **Con pasos cortos la semilla fuera
        de rango es imposible por construccion**, sin tener que decidir si el extent es de fiar.
        La condicion de disparo es la de Uno, leida del IL y con los numeros de la pasada anterior
        (`_pinOffset + ViewportHeight - _pinExtent > -0.1` **y** que el extent haya CRECIDO), de
        modo que la decision es sin estado y un salto a una respuesta o a un resultado de busqueda
        se queda donde lo dejan.
     2. `#region A fill window wide enough for the average line height to mean something`:
        `ItemsStackPanel.CacheLength` de 1 a **4**. `_averageLineHeight` es la media de las lineas
        **materializadas** y se recalcula desde cero en cada pasada; con la ventana de 1248 px
        quedaban dos o tres lineas materializadas y esa media salto de **383,8 a 95,8 px entre dos
        pasadas consecutivas** del mismo contenido. Es el numero por el que Uno divide el offset
        para sembrar, y el que multiplica para inventar el extent. Con viewport x 5 = 3120 px la
        media se toma sobre bastantes filas y en un historial corto el ultimo item se queda
        materializado, con lo que `EstimatePanelExtent` deja de extrapolar.

     **Tasa medida, misma maniobra antes y despues** (foro real, aperturas de tema encadenadas;
     fallo = alguna instantanea de la sonda con `realized [a..b]` y `a >= items`):

     | | aperturas | con fallo | instantaneas fuera de rango |
     |---|---|---|---|
     | antes | 60 (7 ejecuciones) | **16 (27%)** | 85 de 1 708 |
     | despues | 41 (5 ejecuciones) | **0** | 0 de 1 655 |

     Y la **misma ejecucion de regresion A/B** (chat corto, canal largo, diez desplazamientos de
     +/-800/2 500/3 000 px, galeria), compilada con el arreglo y sin el, en la misma sesion:

     | | instantaneas fuera de rango |
     |---|---|
     | sin el arreglo | 2 y 13 (dos ejecuciones) |
     | con el arreglo | 0 y 0 |

     Eso corrige de paso una conclusion de la tanda anterior: **el defecto NO es solo de foros**.
     Lo que pasaba es que aquellas siete ejecuciones de chat normal no encadenaban aperturas. Y el caracter del fallo tambien cambio por el camino: antes el ciclo se
     quedaba **minutos** (r1: 88 instantaneas seguidas); en la version que aun no comprobaba «el
     extent ha crecido» lo que quedaba era **una sola** instantanea aislada que se curaba en la
     pasada siguiente, siempre detras de un deslizamiento de la ventana de 200 mensajes
     (`anchor Add@0 x47 items 200->200`). Con las dos mitades de la regla puestas, ninguna.

     **Tres formas que se midieron y NO valen** (para no repetir la tarde):
     - «anclar solo cuando el extent este medido, o sea con el ultimo item materializado» —la
       palanca 1 del analisis— **abre todos los chats por arriba**: llegar al fondo de un chat
       largo es precisamente un paseo sobre extents estimados (un tema sano va 1 216 -> 3 715 ->
       9 894 -> 14 052 persiguiendo estimaciones y acierta). Medido: el historial se quedaba en
       `offset 191,5 of 3012`.
     - un pegado armado por una bandera que los eventos de scroll apagan **se desarma a si mismo a
       mitad de su propio paseo**, porque en mitad de un paseo la vista no esta en el fondo. El
       mismo 191,5.
     - acotar el destino a `NumberOfItems * _averageLineHeight` —el ultimo offset cuya semilla cae
       dentro de la lista— **no aguanta**: la media se recalcula cada pasada y oscila un factor
       cuatro entre dos, asi que el numero con el que se calcula el tope no es el numero por el que
       Uno divide. Seguia fallando 1 de cada 8.
     - y **el pegado tiene que seguir al CONTENIDO, no a la vista**: sin la condicion de «el extent
       ha crecido» (la otra mitad de la regla de Uno, `num6 = num2 - unzoomedExtentHeight`), diez
       desplazamientos programados de -800/-2500/-3000 px se deshacian solos en menos de cinco
       segundos y el offset volvia a `7245 of 7245` cada vez: un historial que no se puede subir.

     Dos cosas mas, medidas en la tanda de cierre del 2026-08-26 y que acotan el trabajo:
     - **Cuantas veces pasa.** En 13 ejecuciones: seis aperturas del mismo tema de foro, **una**
       acabo en el ciclo (`r1-historial.log`), las otras cinco quedaron estables y correctas
       (`r3-tema-arbol.log`, `r4-tema-1..4.log`, todas `items 40, realized [36..39] = 4`). En chat
       normal, **cero** de siete, incluidos diez desplazamientos programados de hasta ±3 000 px —
       o sea saltos mayores que el viewport a proposito — que recorren la rama de salto grande sin
       encenderlo (`r2-chats.log`: `containers 46 created - 33 recycled` en toda la sesion, contra
       1 672 creados en 25 s cuando el ciclo esta en marcha). Un salto grande **no basta**: hace
       falta que `_averageLineHeight` este ya lejos de la realidad, que es lo que consigue el
       cambio brusco de extent descrito en la entrada anterior.
     - **Lo que NO es.** El placeholder que aparece en pantalla durante el ciclo no viene de un
       item nulo: `TemplateResolver` es `item => _typeToStrategy[SelectTemplateCore(item)]`, y
       `SelectTemplateCore(null)` devuelve `ChatHistoryViewItemType.Incoming`, o sea una plantilla
       valida. Y en el volcado de arbol de una ejecucion sana las filas son `MessageSelector`, no
       `ImplicitTextBlock` (`r3-tema-arbol.log`, cuatro contenedores en y = 127, 235, -114, -592:
       correctos y colocados fuera de la ventana). Buscar la causa del placeholder por el lado de
       la plantilla es tiempo perdido; sale del reciclado a 5-8 Hz, y se cierra cerrando el ciclo.
   - **`CacheLength` del historial era 1,0, no 0 y no 4.** Medido (`cacheLength 1.0`,
     `window 0.0..936.0` con viewport 624): `ItemsStackPanel` registra la propiedad con 0.0 por
     defecto, pero su constructor la pisa con `FeatureConfiguration.ListViewBase.DefaultCacheLength`.
     La ventana de relleno era por tanto viewport + 312 px, no el viewport pelado. **Desde el
     2026-08-26 el historial la sube a 4** (`ChatHistoryView.WidenTheFillWindow`, ventana de
     viewport x 5): ver la entrada del ciclo de indices fuera de rango, mas arriba. Hay que
     asignarla desde una pasada de maquetacion, no desde `Loaded`: `ItemsPanelRoot` no existe
     todavia ahi.
   - **`ContentTemplateSelector` no está en la plantilla de `ListViewItem`: Uno lo enchufa TARDE, y
     mientras tanto la fila se dibuja como el `ToString()` del ítem.** Medido sobre el código que
     genera el compilador XAML para `CommonStyles` (el `ListViewItem` de Uno): su
     `<ContentPresenter x:Name="ContentPresenter">` enlaza por plantilla `Content`, `ContentTemplate`
     y `ContentTransitions`, pero **no** `ContentTemplateSelector`. Ese lo ata el propio
     `ContentPresenter.OnApplyTemplate`, que corre desde su `OnLoaded` — o sea **después** de que
     `ContentPresenter.EnterImpl` haya llamado ya una vez a `UpdateContentTemplateRoot`. En esa
     primera pasada el presenter tiene `Content` (el `MessageViewModel`, que `ItemsControl` le enlaza
     al `DataContext`) y **ninguna** plantilla, y la respuesta de Uno a "contenido que no es
     `UIElement` y sin plantilla" es `SetContentTemplateRootToPlaceholder()`: un `ImplicitTextBlock`
     enlazado a `Content`, es decir la fila dibujada como el texto crudo
     **`Telegram.ViewModels.MessageViewModel`**. Normalmente se cura en la pasada siguiente (cuando
     llega el selector), así que solo se ve en las filas que no reciben esa segunda pasada — de ahí
     que apareciera de vez en cuando y no se pudiera reproducir a voluntad. Sustituto: resolver la
     plantilla **sobre el contenedor** en `ChatHistoryView.PrepareContainerForItemOverride`
     (`selector.ContentTemplate = resolver.SelectTemplate(item, selector)`, **antes** de `base` —
     ver la entrada siguiente, que es la que dice por qué antes y no después), que
     sí viaja por un `TemplateBinding` existente desde el momento en que se construye el presenter.
     El reciclado no cambia: `_typeToStrategy` guarda una única instancia de `DataTemplate` por tipo,
     así que reasignar la misma no dispara `OnContentTemplateChanged` y el contenedor conserva su
     burbuja. Y `ChatView.OnContainerLoaded` ahora avisa (`Logger.Warning`) cuando la raíz del
     contenedor no es ni `MessageSelector` ni `MessageService`, que es exactamente el placeholder.
   - **Resolver esa plantilla DESPUÉS de `base.PrepareContainerForItemOverride` cuesta el
     historial entero.** La misma línea de la entrada anterior, puesta una línea más abajo, deja el
     `ItemsStackPanel` del historial midiendo **833x0** y sin materializar ni un contenedor, en
     **todos** los chats, no solo en foros — y sin una sola excepción en el log. Medido el
     2026-08-24 sobre el mismo chat y la misma ejecución, cambiando solo el orden:

     | | `extent` | `realized` | contenedores creados |
     |---|---|---|---|
     | asignación **después** de `base` | 624,0 (= viewport) | `[-1..-1]` = 0 | 0 |
     | asignación **antes** de `base`, o sin asignación | 6 540,0 | `[31..39]` = 9 | 24 |

     El síntoma en pantalla es el chat abierto con su cabecera, su fondo y su cuadro de texto y
     **cero mensajes**; el probe dice `intercepting invalidate False` y `layout ItemsControl set`,
     o sea que **no** es ninguna de las dos causas conocidas de esta misma pantalla en blanco.
     Lo que cambia con el orden es que `base` es quien pone el `Content` del contenedor: con el
     `Content` ya puesto, cambiar la plantilla obliga al `ContentPresenter` a rehacer su raíz
     dentro de la ventana en la que `VirtualizingPanelLayout` se está comiendo los
     `InvalidateMeasure`; puesta antes, el presenter nace con la plantilla correcta y no hay nada
     que rehacer. Regla: **en `PrepareContainerForItemOverride`, todo lo que toque plantillas va
     antes de `base`; lo que toque el contenedor ya poblado, después.**
   - **`Loaded` NUNCA se dispara en `GalleryWindow` (una `OverlayWindow`), y ahí cuelga toda su
     inicialización.** Medido el 2026-08-24 con una sonda dentro de `PrepareNext`: **ni una sola
     llamada** en una sesión entera. Lo que sí se ve es la ventana — capa oscura, barra de título,
     barra inferior con «11 de 20» — porque eso lo pone `Load(parameter)` + `Bindings.Update()`;
     lo que falta es todo lo que el handler de `Loaded` de `ShowAsyncInternal` hace: meter las
     imágenes en los tres `GalleryContent` (se quedaban en su tamaño vacío de **48x100**), fijar
     `HasPrevious`/`HasNext` (el carrusel contestaba **`prev False, next False`** a cada
     deslizamiento, así que ningún gesto podía cambiar de foto) y suscribirse a los cambios de la
     colección. Sustituto: lanzar el mismo handler desde el **primer `LayoutUpdated`** de la
     ventana — la misma salida que este port ya usa para `ItemsPanelRoot` — y bajar `_unloaded`
     antes, porque Uno saca la ventana del árbol al entrar (el `Popup` reparenta a su hijo) y ese
     viaje dispara `OnUnloaded` antes que nada. El handler se da de baja de `Loaded` al salir, así
     que no puede ejecutarse dos veces. Aplicado en `Controls/Gallery/GalleryWindow.xaml.cs`.
     Verificado: `gallery PrepareNext(None, initialize True): index 10, items 19` →
     `gallery prepared: item 11 of 19, prev True, next True`, y los tres elementos pasan de
     `48x100` a `414x736` el actual y `331x736`/`340x736` los vecinos.
   - **Y una `OverlayWindow` tampoco se entera de que la ventana cambia de tamaño.** Se dimensiona
     con `UpdateViewBase()` cuando se abre y ya está: en Windows la mantiene al día
     `ApplicationView.VisibleBoundsChanged`, que aquí no existe, y el `OnSizeChanged` que upstream
     dejó escrito tiene el cuerpo comentado **y no está suscrito a nada**. Lo que cuesta se ve en
     cuanto la galería pone la ventana a pantalla completa (que es lo que hace el ajuste
     «galería a pantalla completa», activado en esta cuenta): medido el 2026-08-25, ventana
     `1368x912` con la galería todavía `1368x776`, o sea **el chat asomando por debajo de la capa
     negra** en la franja de 136 px que sobra. Sustituto: suscribirse en `PopupHost_Opened` a
     `WindowContext.SizeChanged` —que ya existe y ya reenvía `Window.SizeChanged`— y darse de baja
     en `PopupHost_Closed`; el handler llama a `UpdateViewBase()`. Verificado después:
     `GalleryWindow [0,0 1368x912]` con la ventana a pantalla completa, y al cerrar la galería la
     ventana vuelve a `2736x1552` en `0,138` con su marco.
   - **La barra de mandos de la galería se esconde animando la opacidad del VISUAL DE COMPOSICIÓN,
     y `UNIGRAM_DUMP_TREE` no lo ve.** `GalleryWindow.ShowHideTransport` anima
     `ElementComposition.GetElementVisual(BottomPanel).Opacity`, no `UIElement.Opacity`, así que el
     volcado del árbol enseña `GalleryTransportControls #Controls [486,824 396x76]` con su caja y
     sus hijos aunque en pantalla no haya absolutamente nada. **El volcado no sirve para decir si la
     barra está visible; la captura sí.** Y para hacerla salir desde fuera del proceso hace falta un
     `MotionNotify` suelto: `UNIGRAM_CLICK` manda motion+press+release juntos y, mientras la barra
     está escondida, `BottomPanel.IsHitTestVisible` es `false`, de modo que el press del propio clic
     se cuela por debajo y cae en la foto. `unigram-linux/tools/pointer-move.py <x> <y>` (unidades
     de maquetación) manda ese movimiento con la misma entrega que `XTestKeyboard.SendPointer`
     —`XSendEvent` con máscara vacía— y después el clic sí aterriza en el botón.
   - **`WriteableBitmap.Invalidate()` COPIA el fotograma entero, cada vez.** Leído del IL de Uno
     6.6.184: `Invalidate` → `InvalidateSource` → `Open` → `WriteableBitmap.TryOpenSourceSync` →
     `SkiaCompositionSurface.CopyPixels` → **`SKImage.FromPixelCopy`**. O sea que cada
     `Invalidate` asigna una imagen nueva del tamaño del fotograma, copia los píxeles dentro y
     tira la anterior. A 1539x2736 eso son 16,8 MB asignados y copiados **por fotograma**, y es
     por lo que «presentar» costaba 12,4 ms de hilo de interfaz y el vídeo se quedaba en 30 fps
     (medido en `unigram-linux/spikes/VideoPresentSpike`, ventana Uno real de 2200x1440).
     Dos consecuencias prácticas:
     - **Un solo buffer basta.** Como la copia a `SKImage` termina antes de que `Invalidate`
       vuelva, en cuanto vuelve los bytes del `PixelBuffer` son otra vez del productor. No hace
       falta doble buffer para un `WriteableBitmap`.
     - **Si el fotograma cambia 60 veces por segundo, `WriteableBitmap` es el sitio equivocado.**
       El sustituto es `SKCanvasElement` (`Uno.WinUI.Graphics2DSK`) sobre memoria propia:
       presentar pasa a ser `Invalidate()` y nada más (0,06 ms) y se sostienen 53 fps a ese mismo
       tamaño. Aplicado en `Telegram.Linux/Xaml/VideoSurface.cs`, que tiene las dos
       implementaciones y elige la de Skia salvo que el host no la tenga.
   - **Los bytes de un `IBuffer` de Uno SÍ se pueden alcanzar, y todas las vías públicas copian.**
     `CopyTo`/`ToArray` de `WindowsRuntimeBufferExtensions` pasan todas por `Buffer.Cast(...)` y
     una copia de `Span`. Detrás hay un `byte[]` normal; lo que es `internal` son los accesos
     (`ArraySegment<byte> GetSegment()`, el campo `Memory<byte> _data`).
     `Telegram.Native.BufferAccess` los resuelve **una vez por buffer** por reflexión y cae al
     camino que copia si Uno cambia de forma. Con eso `VideoAnimation.RenderSync` decodifica
     **dentro** del buffer destino en vez de en un `ArrayPool` que luego se copia: 3,9 ms menos
     por fotograma a 1539x2736. Nota para quien lo reutilice: el `_buffer` interno es null cuando
     el `Buffer` se construyó sobre un `Memory<byte>`, por eso se prefiere `GetSegment()`.
   - **swscale se cae de sus caminos rápidos con un ancho de salida IMPAR, y cuesta el doble.**
     Medido con el decodificador real sobre un clip 2160x3840 (`unigram-linux/spikes/VideoScaleSpike`,
     mediana de tres pasadas de 60 fotogramas): **1539x2736 = 97,8 ms/fotograma**, 1538x2736 =
     40,0, 1536x2736 = 39,3, 1540x2736 = 45,6. Con la altura no pasa (810x1441 mide lo mismo que
     810x1440) y alinear más de 2 no aporta nada. `LinuxVideoPlayer` calculaba el tamaño con
     `Math.Round(ancho * escala)`, que cae en impar la mitad de las veces; ahora lo redondea a par
     `VideoSurface.Choose`. **Regla: cualquier tamaño que se le pida al decodificador se redondea
     a ancho par.**
   - **Decodificar al tamaño de la pantalla, no al del fichero.** El mismo spike: 2160x3840 (sin
     reescalar, solo conversión de espacio de color) 29,4 ms, 810x1440 (lo que ocupa ese clip en
     una ventana de 2200x1440) 32,7 ms, y 1539x2736 97,8. Es decir que pedir menos píxeles no es
     más caro, y presentarlos sí es mucho más barato. `VideoSurface.Choose` toma el tamaño del
     elemento por la escala de rasterización real y **nunca amplía**; el tope de `MaxSide` queda
     solo para cuando el control todavía no se ha maquetado.
   - **`GlobalStaticResources.Initialize()` arranca VEINTIDÓS ensamblados, trece de ellos Hot
     Design.** Está en el código generado (`obj/.../GlobalStaticResources.cs`): `Initialize()`,
     `RegisterDefaultStyles()` y `RegisterResourceDictionariesBySource()` de `Uno.UI.HotDesign.*`
     ×13, `Uno.UI.RemoteControl`, `Uno.UI.App.Mcp.Client`, `Uno.AI.XamlGeneration.Contracts`,
     `Uno.Themes.WinUI`, `Uno.Toolkit.*`, `Uno.UI.Lottie`… y corre desde el **constructor estático**
     de `GlobalStaticResources`, es decir dentro de `App.InitializeComponent()` — el tramo de
     3,15 s del informe de arranque. Solo meterlos en el proceso y resolver sus tipos costaba
     702 ms medidos (`unigram-linux/spikes/StartupSpike`), 333 de ellos de los trece de Hot
     Design. Se quitan con `<UnoDisableHotDesign>true</UnoDisableHotDesign>` (ya puesto en el
     csproj **y en el generador**, `tools/gen-linux-csproj.py`, porque el csproj se regenera):
     15,8 MB menos de ensamblados desplegados y 13 juegos de registros menos en el arranque.
     Release ya los excluía, así que esto solo cambia Debug.
   - **`Themes/Generic.xaml` NO se construye en el arranque**, aunque sea el diccionario más
     grande del proyecto (39 899 líneas generadas). `RegisterDefaultStyleForType` guarda el
     **proveedor**, no el diccionario (`Style.cs`: `_lookup[type] = ProvideStyle`), así que
     `Generic.xaml` se materializa en el primer control que busca su estilo por defecto, no en
     `App()`. Quien sí se construye entero en `App()` es la cadena de `App.xaml`
     (`XamlControlsResources`, `LinuxResources`, `CommonStyles`, `Accent`, los cinco
     `*_themeresources` y `Messages`). **Cualquier recorte para acelerar el arranque va ahí, no
     en `Generic.xaml`.**

   - **El anclaje de scroll de Uno 6.6 no sustituye a `ItemsUpdatingScrollMode` al insertar arriba.**
     Medido con una página de 24 mensajes antiguos insertados uno a uno en el historial:
     `VerticalAnchorRatio = 1` tira la vista al fondo del todo (el ancla se va **−5 429,50 px**) y
     `VerticalAnchorRatio = 0` deja el offset clavado en 0, con lo que el cargador pide otra página
     y otra hasta que la ventana de 200 mensajes se pone a dar vueltas (y además el chat ya no abre
     por el final). El anclaje se deja **solo** para lo que sí hace —quedarse pegado al fondo
     mientras se está ahí, que es la otra mitad de `ItemsUpdatingScrollMode`— y la posición al
     prepender se mantiene a mano: `ChatHistoryView`, `#region Keep position on prepend`. Detalle y
     números en `unigram-linux/HANDOFF.md`.
   - **`ScrollIntoView` a un índice no materializado no hace nada** en el historial: `ScrollToItem`
     al mensaje 0 con 55 cargados dejó el offset igual y el contenedor sin materializar (a un
     mensaje ya materializado sí: lo deja a 0,00 px del borde). Pendiente de sustituto — lo
     necesitan el salto a una respuesta, el resultado de búsqueda y el "primer no leído".
   - **Un `Loaded` no es un buen momento para leer `ItemsPanelRoot`**: Uno crea el panel en la
     primera medida, *después* de `Loaded`. `BidirectionalIncrementalLoader.OnListViewLoaded` se
     rendía ahí y no volvía a intentarlo, así que **el historial no pedía nunca mensajes antiguos**:
     al llegar arriba se paraba con `IsOldestSliceLoaded == false` para siempre. Espera al panel por
     `LayoutUpdated`. Cualquier código que dependa de `ItemsPanelRoot` en `Loaded` tiene el mismo
     problema.
   - **`Compositor.RegisterAnimation` indexa su diccionario por el objeto animación**
     (`Uno.UI.Composition/Composition/Compositor.skia.cs`), así que **reutilizar una misma
     instancia de animación en dos visuales lanza** `An item with the same key has already been
     added`. Unigram guarda animaciones en campos y las reutiliza; medido en
     `Controls/Chats/ChatHistoryArrows.ShowHideMessages`, que deja de mostrar/ocultar las flechas
     de "ir al final". **Pendiente**: clonar la animación por visual (o crearla en cada uso) en
     `ChatHistoryArrows` y en los demás sitios que reutilicen instancias.
   - **`CompositionScopedBatch.Completed` no se dispara**, y lo que se pierde es el desmontaje: el
     handler de fin de lote es donde se quitan los andamios de la animación y se escribe a mano el
     estado final (offsets a cero, clips fuera, un elemento por fin colapsado). Sin él el visual se
     queda a medias — `MainPage.ShowHideTopTabs` deja `ChatTabs` visible tanto si aparecía como si
     desaparecía, la lista se queda con el relleno que la animación tomó prestado y `DialogsPanel`
     con su margen inferior de -40, así que cambiar la disposición de las pestañas de carpetas es un
     viaje de ida. Sustituto: `Telegram.Common.CompositionScopedBatchEx` (`Telegram.Linux/Xaml/`),
     `batch.EndWithCompleted(duración, callback)`, movido por `CompositionRenderingClock` — el final
     de un lote solo depende del tiempo, y el tiempo es `KeyFrameAnimation.Duration`. **Ojo con la
     duración**: Unigram casi nunca la fija, y donde WinUI entiende un segundo, en Uno
     `KeyFrameAnimation.Duration` es una propiedad automática sin valor por defecto, o sea
     `TimeSpan.Zero`, y `KeyFrameEvaluator` contesta `Progress = 1` en su primera evaluación — la
     animación salta a su valor final y para. Por eso la espera es `max(duración, un fotograma)`:
     nunca cero, nunca un segundo por algo que ya terminó. Aplicado en `Views/MainPage.xaml.cs` y en
     `Controls/Gallery/GalleryWindow.xaml.cs` (dos sitios: la barra inferior, que sin esto se queda
     sin recibir toques para siempre, y el cierre, que sin esto **desvanece la galería pero no la
     cierra nunca** y deja el chat tapado) y en `Controls/TopNavView.cs`
     (`AnimateSelectionChanged`, la barra de secciones de Ajustes: su handler de fin de lote es el
     que apaga el indicador saliente y limpia `_prevIndicator`/`_nextIndicator`, así que sin él el
     indicador viejo se queda encendido en su desplazamiento animado y cada selección posterior
     tiene que forzar el final de la anterior; ahí la duración sí está fijada, 300 ms);
     y en `Controls/MasterDetailView.cs` (`ShowHideBanner`, 2026-08-25: su handler es el que baja
     el andamio de la banda de «suena ahora» —quita el margen de -40/-48 y la `Translation`
     prestadas de `DetailRoot` y `MasterFrame`, y vuelve a colapsar la banda—, así que sin él la
     **primera** nota de voz reproducida desplazaba la página principal para siempre; igual que el
     anterior, era código inalcanzable hasta que la fase 5 encendió el `PlaybackService`);
     **pendiente** en `Controls/DownloadsIndicator.cs`, `Controls/RecentUserHeads.cs`,
     `Controls/Stories/StoriesStrip.xaml.cs` y `Composition/CompositionDustVisual.cs`.
     **Dos sitios más, 2026-08-26, tanda de señales vivas** — y los dos se habían quedado fuera
     porque el síntoma es *invisible*, no roto: la animación de desvanecido SÍ corre, así que lo
     que queda detrás no se ve. `Controls/Cells/ChatCell.ShowHideOnlineStatus` (el punto verde de
     «en línea») y su gemelo `ShowHideAutoDelete` escriben `OnlineBadge.Visibility = Visible`
     incondicionalmente y sólo vuelven a `Collapsed` desde el handler de fin de lote. Medido con el
     volcado del árbol, mismo chat, misma fila, con el contacto pasando a desconectado sin tocar
     nada: antes `Border #OnlineBadge [51,252 12x12] c-opacity=0,00 c-scale=0,00,0,00` (sin la
     marca `collapsed`, o sea `Visible`); después `Border #OnlineBadge [51,252 0x0] collapsed`.
     Y `Controls/Chats/ChatHistoryArrows` los CUATRO (`ShowHideMessages`, `ShowHideMentions`,
     `ShowHideReactions`, `ShowHidePollVotes`): antes
     `StackPanel #MessagesPanel [1326,779 48x36] c-opacity=0,00 c-scale=0,00,0,00`, o sea 48x36 px
     de objetivo de clic invisible sobre la esquina inferior derecha del historial, justo encima de
     las últimas burbujas —la misma forma exacta que `DialogsPanel` en el buscador—; después
     `collapsed`. En `ChatHistoryArrows` `EndWithCompleted` **no basta por sí solo**: su handler
     reescribe a mano `Translation` sobre visuales que se acaban de animar, así que lleva delante
     `StopAnimation("Translation.Y")` sobre `pollVotes`/`reactions`/`mentions` por la regla de la
     animación terminada que sigue mandando. Sin ese `StopAnimation` las insignias de mención,
     reacción y votos se quedan 52 px fuera de su sitio en cuanto se oculta una de las de abajo.
   - **`PointerMoved` llega DOS VECES por cada movimiento del dedo.** Medido en
     `unigram-linux/spikes/TouchSpike`: de 89 tandas de `PointerMoved` consecutivas con la misma
     marca de tiempo X, **85 son exactamente dobles** — mismo `Position`, mismo `Timestamp`, mismo
     `OriginalSource`. `PointerPressed`, `PointerReleased`, `PointerEntered` y `PointerExited` llegan
     **una** vez cada uno; solo se duplica el movimiento. Cualquier código que **acumule** deltas
     dentro de `PointerMoved` (arrastres, *swipe to reply*, el *grip* de `MasterDetailPanel`) contará
     el doble. Defensa: descartar el evento cuyo `GetCurrentPoint(...).Timestamp` sea igual al del
     anterior. Los `Manipulation*` **no** están afectados — el reconocedor de gestos da traslaciones
     coherentes.
   - **Un toque enciende y apaga el estado de *hover*.** `X11PointerInputSource` levanta
     `PointerEntered` justo antes del `PointerPressed` y `PointerExited` justo después del
     `PointerReleased` de cada secuencia táctil, así que todo lo que Unigram dibuja al pasar el ratón
     por encima (los botones que asoman en `ChatCell`, los *tooltips*, el *tilt*) parpadea en cada
     toque. Es cosmético, pero con el dedo se nota.
   - **`RichTextBlock` está marcado `[NotImplemented]` para `__SKIA__`** en el propio `Uno.UI.dll`
     (atributo en el tipo, no una nota de documentación): maqueta como una caja vacía y no dibuja
     una sola letra. No lanza y no avisa, así que el síntoma es solo "ahí no pone nada" — era el
     motivo de que **los nombres de las carpetas de la barra lateral no aparecieran** (los iconos sí,
     porque son `FontIcon`). Sustituto: el mismo elemento como `TextBlock`, con los mismos
     `Inlines`. En XAML se hace con el prefijo condicional inverso de `win:`:
     `xmlns:not_win="http://uno.ui/not_win"` + `mc:Ignorable="d not_win"`, y se declaran los dos
     elementos con el **mismo `x:Name`** (`<win:RichTextBlock x:Name="Title" …/>` y
     `<not_win:TextBlock x:Name="Title" …/>`): cada plataforma descarta el del otro, así que el
     campo generado existe una sola vez y solo cambia de tipo. Aplicado en
     `Controls/Cells/ChatFolderCell.xaml` + `.xaml.cs` (`OnTitleChanged` bajo `#if LINUX`, un nivel
     menos de anidamiento porque un `TextBlock` no tiene `Blocks`) y en el mismo `DataTemplate` de
     `Views/MainPage.xaml`. **Pendiente**: el resto de `RichTextBlock` del proyecto
     (`FormattedTextBlock` ya lo evita, ver §2).
   - **Un stub que hereda de un control real y NO fija `DefaultStyleKey` se queda con la plantilla
     del padre — y eso recorta a sus hijos.** `Telegram.Linux/Xaml/ActiveStoriesSegments.cs` deriva
     de `HyperlinkButton` y no fijaba la clave, así que Uno le aplicaba el estilo por defecto de
     `HyperlinkButton` (el de Fluent, con `Padding`, `MinWidth` y `MinHeight` propios) en vez del
     `<Style TargetType="local:ActiveStoriesSegments">` de `Themes/Generic.xaml` —que **no** es
     `win:`, o sea que ya se compila aquí, y es un `Grid` con un `ContentPresenter` estirado y sin
     relleno—. El `ProfilePicture` de 48×48 que va dentro se desbordaba del hueco con relleno, Uno
     recorta el hijo a su hueco de maquetación, y **el círculo que el `Border` sí pinta salía como
     un rectángulo redondeado**: medido sobre `fase1/lista-1.png` y `fase1/historial-1.png`, caja
     visible de 46×34 unidades con los arcos del círculo de radio 23 todavía visibles en las
     esquinas (bordes recto arriba, abajo, izquierda y derecha = círculo recortado por un
     rectángulo). Ojo con el diagnóstico fácil: `CornerRadius` **sí** recorta el `Background` de un
     `Border` en Uno (`BorderVisual` mete el fondo en un `CompositionSpriteShape` con la geometría
     del rectángulo redondeado, y `CornerRadius.GetRadii` no capa nada si el radio es la mitad del
     lado), así que el problema no estaba en `ProfilePicture` sino encima de él. Regla: **todo stub
     que derive de un control con plantilla tiene que fijar `DefaultStyleKey` al tipo real** si el
     proyecto trae un `Style TargetType` para él (`Style.RegisterDefaultStyleForType` es lo que
     emite el generador de XAML para cada `<Style TargetType>` no `win:` de `Generic.xaml`).
   - **No hay `CompositionGraphicsDevice`, así que el fondo de chat no se puede componer: se
     dibuja.** `ChatBackgroundPresenter`/`ChatBackgroundBrush` montan el patrón en un
     `CompositionDrawingSurface` que Direct2D rellena desde el SVG y lo componen con
     `BorderEffect(Wrap)` + `OpacityEffect` + `BlendEffect(SoftLight)` (o `AlphaMaskEffect` si el
     patrón es invertido). Uno **sí** trae la familia de efectos Win2D sobre `SKImageFilter`
     (`AlphaMaskEffect`, `BlendEffect`, `BorderEffect`, `OpacityEffect` están implementados en
     `Uno.UI.Composition`), pero no hay manera de crear la superficie del patrón, y sin ella no hay
     grafo. Sustituto: **`Telegram.Linux/Graphics/ChatBackgroundRenderer.cs`** (Skia puro, sin Uno
     ni TDLib, probado en `unigram-linux/spikes/ChatBackgroundSpike`) y
     **`Telegram.Linux/Xaml/ChatBackgroundCanvas.cs`**, que lo pinta dentro del árbol visual.
     **La degradación es exacta, no aproximada**: el patrón se rasteriza en negro puro, y
     `SoftLight` con fondo negro es idénticamente 0 en las dos mitades de la fórmula del W3C, así
     que el positivo se reduce a `Co = (1 − α)·Cs`, o sea *negro sobre el degradado con alfa =
     intensidad*; el negativo enmascara el degradado con el complemento del patrón sobre el negro
     que el presenter pinta detrás, que es la misma operación. Dos cosas que conviene saber del
     mecanismo:
     - **`SKCanvasElement` (`Uno.WinUI.Graphics2DSK`) es la costura para pintar con Skia dentro del
       árbol visual**, y ya viene con la feature `SkiaRenderer` (no hay que añadir NuGet). Su
       `RenderOverride(canvas, area)` recibe `area` en unidades de maquetación y **el canvas ya trae
       aplicada la transformación de DPI**, así que `canvas.TotalMatrix.ScaleX` es la escala de
       rasterización de verdad — la que `XamlRoot.RasterizationScale` no da.
     - **`RenderOverride` corre en el camino de pintado, no "una vez por resize"**: `SKCanvasVisual`
       lo llama desde `Paint` con `CanPaint()` siempre a true. Rasterizar los 745 garabatos del
       `.tgv` cuesta ~130 ms, así que el resultado se guarda como imagen
       (`ChatBackgroundRenderer.Surface`) y cada pase es un *blit*: medido 131 ms el primero y
       **3–10 ms** los siguientes a 2736×1552.
     - **Su constructor LANZA `PlatformNotSupportedException`** si el host no registró un
       `SKCanvasVisualBaseFactory`. Como el control se instancia desde la plantilla de
       `MasterDetailView`, `ChatBackgroundCanvas` es un `Grid` que crea el `SKCanvasElement` dentro
       de un `try` y comprueba antes `IsSupportedOnCurrentPlatform()`: sin fondo se vive, sin
       `MainPage` no.
     - **Nada de `Dispose` sobre lo que puede estar pintándose.** Soltar un `SKPath`/`SKImage` que el
       compositor esté leyendo es un *use-after-free* nativo, no una excepción: al reemplazar patrón,
       tile o superficie se suelta la referencia y la recoge el finalizador.
   - **`Windows.ApplicationModel.Package.Current.InstalledLocation.Path` + rutas con `\`.**
     `TdExtensions.GetLocalFile("Assets\Background.tgv")` construía la ruta del fondo por defecto
     como si hubiera paquete UWP, y además `\` es un carácter legal de nombre de fichero aquí, así
     que `Path.Combine` devolvía **un fichero llamado `Assets\Background.tgv`**. Corregido con un
     `#if LINUX` que ancla en `AppContext.BaseDirectory` y traduce el separador (misma regla que
     `Telegram.Linux/Native/NativeAssets.cs`, §2). Afecta a todo lo que pase por ahí: los `.tgs` de
     reacciones, el mockup de temas y el patrón del fondo.
   - **`Control.GetTemplateChild` no devuelve un `x:Load="False"` sin materializar por el namescope
     de la plantilla**: `FindNameInScope` descarta explícitamente los `ElementStub`. Lo salva el
     `?? FindName(childName)` que viene detrás, que sí materializa (`ConvertFromStubToElement`), así
     que el patrón de Unigram (`GetTemplateChild(name) as T`) funciona — pero conviene saberlo antes
     de perseguir un `null` por ahí.
   - **El zoom del `ScrollViewer` compila, contesta 1 y no escala nada.** `ZoomMode="Enabled"`,
     `MinZoomFactor`/`MaxZoomFactor`, `ChangeView(h, v, factor)` y `ZoomFactor` existen todos y
     ninguno lanza; simplemente el factor **nunca llega al presenter**: `ScrollViewer.ChangeViewNative`
     llama a `presenter.Set(horizontalOffset, verticalOffset, **null**, ...)` y
     `ScrollContentPresenter.Set` remata con `Update(view, h, v, **1.0**, options)`. `ZoomToFactor`
     es un no-op (`TryRaiseNotImplemented`), `OnZoomModeChanged` tiene el cuerpo vacío, y
     `OnPresenterZoomed` —el único que escribe `ZoomFactor`— **no tiene ningún llamante** en todo el
     ensamblado, así que `ZoomFactor` contesta su valor por defecto, 1, para siempre. Medido en
     `unigram-linux/spikes/GestureSpike` (`run-06.log`): `ChangeView(null, null, 2f)` devuelve `True`
     y deja `ZoomFactor` en 1. Miente **dos** veces, porque
     `GalleryWindow.ScrollingHost_ViewChanged` lee ese 1 constante para decidir si se puede pasar de
     una foto a otra. Sustituto: `Telegram.Linux/Xaml/ZoomViewer.Linux.cs`, un `CompositeTransform`
     sobre el contenido movido por `ManipulationDelta.Scale`, con la misma superficie pública
     (`ZoomFactor`, `CanZoomIn/Out`, `Zoom`, `ViewChanged`, `PanStarting`).
   - **De `InteractionTracker` no se salva ni el `ChangeView`.** El scoping lo daba por "parcial";
     medido sobre `Uno.UI.Composition.dll` y confirmado ejecutándolo:
     `TryUpdatePositionWithAnimation` **lanza** `NotImplementedException` (es el `ChangeView` animado,
     o sea las flechas y el teclado de la galería), `NaturalRestingPosition` lanza,
     `ConfigurePositionXInertiaModifiers` lanza, `TryUpdateScale`/`TryUpdateScaleWithAnimation`
     lanzan, `VisualInteractionSource.IsPositionXRailsEnabled` es un setter no-op y
     `DeltaPosition`/`PositionVelocity`/`Scale` lanzan. A eso se suma que las condiciones de los
     puntos de anclaje están escritas con `this.Target`, que el parser de expresiones de Uno rechaza
     (ver más arriba). Lo que sí está implementado de verdad es el **reconocedor de gestos**
     (`GestureRecognizer.Manipulation`: carriles, umbrales, velocidades e inercia), así que todo lo
     que use `InteractionTracker` se reescribe sobre `Manipulation*`. Aplicado en
     `Telegram.Linux/Xaml/CarouselViewer.Linux.cs`; **pendiente** con el mismo patrón en
     `Controls/ChatListListView.cs`, `Controls/MasterDetailView.cs` y
     `Controls/Messages/MessageSelector.xaml.cs` (los tres, fuera del subconjunto hoy).
   - **Dos elementos anidados no pueden manipular a la vez: el de fuera se queda mudo.**
     `UIElement.PrepareManagedManipulationEventBubbling` llama a `GestureRecognizer.CompleteGesture()`
     en **cada ancestro** por el que burbujea un evento de manipulación que no sea
     `ManipulationStarting`. Efecto medido (`spikes/GestureSpike/run-03.log`) con el pellizco en el
     `ZoomViewer` y el pase en el `CarouselViewer` de dentro: el reconocedor de fuera levantó
     `ManipulationStarting` **una vez por dedo** —porque su manipulación anterior había sido
     completada y `TryAdd` del segundo dedo devolvía false— y **cero** `ManipulationDelta`, mientras
     el de dentro recibía todos. No hay excepción ni aviso: el gesto de fuera simplemente no ocurre.
     Regla: **un solo elemento manipula por rama**; los de encima se suscriben a sus eventos según
     burbujean, con `AddHandler(..., handledEventsToo: true)`, y sacan lo que necesiten de
     `e.Container` (que dice de quién es el reconocedor) y de `e.Position` (que viene en coordenadas
     de ese contenedor, transformaciones incluidas). Aplicado entre `CarouselViewer` (que es el que
     manipula, con `Scale` en su `ManipulationMode`) y `ZoomViewer` (que escucha).
     Dos números de Uno que conviene tener a mano al escribir uno de estos: una manipulación táctil
     no **empieza** hasta 15 px de recorrido (`Manipulation.StartTouch`) y cada delta posterior pide
     2 px (`DeltaTouch`); y las velocidades son **píxeles por milisegundo**.
     Y un aviso: la **inercia** del propio reconocedor está movida por `CompositionTarget.Rendering`
     (`Manipulation.CompositionInertiaProcessorTimer`), el reloj que no es un reloj — con
     `TranslateInertia` puesto, `ManipulationCompleted` no llega hasta que esa inercia termina.
   - **`muxc:AnimatedIcon` no dibuja nada sin `FallbackIconSource`, y los iconos LottieGen son
     stubs inertes.** `Telegram.Assets.Icons` lo genera LottieGen sobre Win2D y aquí son fuentes
     inertes (`Telegram.Linux/Xaml/IconStubs.cs`, `TryCreateAnimatedVisual` devuelve `null`). El
     `AnimatedIcon` de la plantilla de `CheckBox` sobrevive porque esa plantilla **sí** trae
     `<local:AnimatedIcon.FallbackIconSource><local:FontIconSource …>` y se ve el glifo — es el
     camino que ya usa la casilla del modo táctil. El de `IconBadgeButtonStyle`
     (`Themes/BadgeButton_themeresources.xaml`, `Source="{TemplateBinding IconSource}"`) **no** trae
     ninguno, así que las filas de la raíz de Ajustes salen con su columna de 48 px vacía: el texto,
     la insignia y el chevron están, el icono no. No lanza, es cosmético, y la salida es un
     sustituto de LottieGen (fase 3) o un estilo propio con `Glyph` en
     `Telegram.Linux/Hubs/` fusionado **el último** en `App.xaml`.
   - **`ApplicationData.Current.LocalFolder` es un `Lazy` que CACHEA su excepción: tocarlo una vez
     antes de que exista la `Application` deja la carpeta de datos inservible para todo el
     proceso.** Antes de que Uno construya la aplicación, ese getter lanza
     `InvalidOperationException: The Package.Id is not initialized yet`, y como el `Lazy` de Uno
     está en su modo por defecto (`ExecutionAndPublication`), **la excepción queda guardada y se
     relanza para siempre**. El síntoma no aparece donde está la causa: la app muere más tarde, en
     `App..ctor` (`SettingsService.Current` → `LocalSettings` → `LocalFolder`), en una línea que
     lleva funcionando desde la fase 1. Cazado el 2026-08-24 al ejecutar por primera vez la fase 4:
     `Main` llama a `SingleInstance.Startup` **antes** del host, su primer `Logger.Info` leía
     `SettingsService.Current.VerbosityLevel` y `LogFile.Write` pedía la carpeta dentro de un
     `try`/`catch` — el `catch` evitaba el fallo inmediato y no evitaba el envenenamiento.
     Regla: **nada que corra antes de que `Microsoft.UI.Xaml.Application.Current` exista puede
     tocar `ApplicationData`, ni siquiera dentro de un `try`.** La guarda es una comprobación de
     nulo, no un `catch`: `Telegram/Logger.cs` (`TryGetVerbosityLevel`, `#if LINUX`) y
     `Telegram.Linux/Platform/LogFile.cs` (`Open`). Lo que se pierde mientras tanto es la copia de
     esas líneas dentro del log de TDLib; a stderr y a `unigram.log` llegan igual.
   - **`AppWindow.Hide()` y `AppWindow.Show()` no hacen nada en el host X11.** `Hide()` vuelve sin
     lanzar y la ventana sigue `IsViewable`, con el foco y en pantalla, así que "cerrar a la
     bandeja" quedaba en un botón de cerrar que no hace absolutamente nada (el cierre **sí** se
     intercepta: el proceso sobrevive y queda `Close to tray` en el log). Sustituto:
     `Telegram.Common.X11Window` (`Telegram.Linux/Platform/`), que hace lo que hace cualquier
     aplicación de bandeja en X11 — `XWithdrawWindow` para quitarla y `XMapRaised` +
     `_NET_ACTIVE_WINDOW` para devolverla— y encuentra la ventana por `WM_CLASS`, como
     `XTestKeyboard`, porque Uno no publica `_NET_WM_PID`. Tres detalles medidos: el mensaje de
     activación **solo** se manda si la ventana no está ya mapeada y activa (el host de Uno
     selecciona `SubstructureNotify` en la raíz, ve ese mensaje dirigido al gestor de ventanas y
     escribe `XLIB ERROR: received an unexpected ClientMessage event on foreign window` en cada
     activación); el manejador de errores de Xlib por defecto **llama a `exit()`**, así que
     `X11Window` instala uno que registra el código y sigue; y si no encuentra la ventana contesta
     `false`, con lo que el cierre se deja pasar — una ventana que ni se cierra ni se esconde es
     peor que una que se cierra.
   - **`x:Bind` a una ruta estática de cuatro tramos no compila.** El generador XAML de Uno emite
     `default! /* Invalid x:Bind property path count (This should not happen) */` en medio de la
     llamada y el error que se ve es un CS1501/CS1003 dentro del fichero generado, no en el XAML.
     Medido con `IsChecked="{x:Bind services:SettingsService.Current.Diagnostics.ShowIds,
     Mode=TwoWay}"` (`Views/Settings/SettingsAdvancedPage.xaml`). Salida: `win:` en ese elemento y,
     si el grupo se queda vacío, colapsarlo desde el code-behind (regla 1).
   - **`x:Load="False"` dentro de un `ItemsControl` no se materializa NUNCA con `FindName`.** Ni
     desde el constructor (que es donde lo hace la rama Windows) ni desde `Loaded` ni desde
     `LayoutUpdated`: el `ElementStub` se queda en `Items` y en el volcado del árbol se ve como un
     `ContentControl` de 0 px de alto. Medido en `Controls/StartupSwitch.xaml.cs`, cuyos dos
     `CheckBox` (icono de bandeja y arranque minimizado) viven dentro de un `HeaderedControl`, que
     es un `ItemsControl`: la página de Ajustes > Avanzado salía con el autoarranque como única
     fila. Sustituto: **construirlos en código** dentro del `#if LINUX` y meterlos en
     `Headered.Items`, con los mismos textos, el mismo estilo y los mismos handlers; la rama
     Windows no se toca. Lo mismo hará falta en cualquier otro `x:Load` que cuelgue de un
     `ItemsControl`.

   - **El lexer de expresiones de Uno no tiene `_` en su alfabeto: un `SetReferenceParameter("_", …)`
     lanza en cuanto arranca la animación.** `ExpressionAnimationLexer.EatToken` contesta
     `ArgumentException: Unexpected character '_'` y en WinUI ese nombre es legal, así que el
     código compartido lo usa por costumbre para su `CompositionPropertySet` de progreso. Dónde
     dolió, medido el 2026-08-25 al entrar la nota de voz: `Controls/PlaybackSlider.cs` monta esas
     expresiones desde `OnApplyTemplate`, que corre **dentro de `MeasureOverride`**, o sea que la
     excepción es de las mortales (ver la entrada del `x:Load` y la del callback de contenedor):
     **cada nota de voz del historial se llevaba por delante el panel entero**. Sustituto: un
     `const string set = "props"` bajo `#if LINUX` y la expresión interpolada. Los sitios del
     subconjunto ya convertidos: `Controls/PlaybackSlider.cs`,
     `Controls/Messages/Content/VoiceNoteContent.xaml.cs`, `Controls/Messages/MessageSelector.xaml.cs`,
     `Controls/DownloadsIndicator.cs` y `Controls/Chats/ChatActionIndicator.cs`. Fuera del
     subconjunto quedan **más de cien** (todos los `Assets/Icons/*.cs` de LottieGen, `ProfileHeader`,
     `StoriesStrip`, `StoryContent`, `PollOptionContent`, `ChecklistTaskContent`,
     `ProgressRingIndeterminate`, `ChatStickerButton`, `ActiveStoriesCell`,
     `LiveStoryPinnedMessageCell`, `CarouselViewer` en su rama `#else`): **grep de
     `SetReferenceParameter("_"` antes de meter cualquier fichero nuevo en el subconjunto.**
     `PlaybackSlider` llevaba además un `this.Target` en la animación del tooltip, que es la otra
     mitad de la misma trampa.
   - **El `Loaded` del contenedor puede llegar ANTES de que su plantilla se expanda, y rendirse ahí
     cuesta la pantalla entera de mensajes.** El camino Linux del historial ata el mensaje desde el
     `Loaded` del `ChatHistoryViewItem` (porque `ContainerContentChanging` llega antes de que el
     contenedor entre al árbol), y hasta ahora, si `ContentRoot()` todavía contestaba el
     `ImplicitTextBlock` de Uno, se registraba un aviso y se abandonaba. Cuando se materializa una
     pantalla entera de golpe —abrir un chat **en un mensaje concreto**, que es lo que hacen una
     respuesta, un resultado de búsqueda y el primer no leído— eso pasa en casi todas las filas:
     medido el 2026-08-25 en un salto al medio de un historial, **26 filas de la pantalla
     quedaron en blanco a la vez**, dibujadas como una burbuja vacía de 20 px y sin ninguna
     excepción cerca. Sustituto: `ChatView.BindWhenExpanded`, que reintenta desde el
     `LayoutUpdated` **del propio contenedor** hasta ocho pasadas y comprueba en cada una que el
     contenedor no ha sido reciclado a otro mensaje entretanto. Con eso el aviso «No message
     content under the container of …» pasó de 26 por salto a 0.
   - **`OverlayWindow` no recibe hijos de plantilla: `Container` y `BackgroundElement` son
     null.** Su XAML propio es su contenido y el estilo por defecto que lleva esos dos nombres no
     se aplica nunca, así que `OnApplyTemplate` lanzaba `NullReferenceException` **en cada apertura
     de la galería**. La recoge `InvokeLoadedWithTry` de Uno —la galería se abría igual— pero se
     saltaba todo lo que había debajo. Guardado con un `#if LINUX` que avisa y vuelve; lo que se
     pierde mientras tanto es el *light dismiss*, que la galería tiene apagado
     (`IsLightDismissEnabled="False"`).
   - **Séptimo sitio de animación compartida** (la regla es «en este port una `CompositionAnimation`
     no se comparte»): `Views/ChatView.CheckButtonsVisibility` arrancaba las **mismas cuatro**
     animaciones (`Scale` y `Opacity`, mostrar y ocultar) sobre **cinco** visuales cada una — el par
     enviar/grabar más los cuatro botones de adjuntos. La excepción salía por el camino de
     `UpdateChatDraft`, o sea **al abrir un chat con borrador**, y con ella se perdían las dos
     `batch.Completed` que deciden qué botón queda visible. Arreglado con las funciones locales
     `Scale(show)`/`Fade(show)` y `EndWithCompleted` en los dos lotes.
   - **libmpv se NIEGA a arrancar si `LC_NUMERIC` no es `"C"`, y la app está en la locale del
     usuario.** `mpv_create` comprueba `setlocale(LC_NUMERIC, NULL)` antes que nada, y si no
     coincide escribe `Non-C locale detected. This is not supported.` **en stderr** y devuelve
     NULL: sin código de error, sin handle y sin sonido. No se vio en el spike porque un proceso
     .NET arranca en la locale `"C"` y es algo de dentro de la app lo que la mueve —medido:
     `es_ES.UTF-8` para cuando se pulsa la primera nota de voz—, cosa que hacen el host X11 de Uno,
     fontconfig o GTK con su `setlocale(LC_ALL, "")` de rigor. **Cualquier escritorio Linux en una
     locale que escriba los decimales con coma se habría quedado sin audio y sin explicación.**
     Sustituto: `MpvClient.ForceNumericLocale()`, un `setlocale(LC_NUMERIC, "C")` por P/Invoke antes
     de **cada** `mpv_create` (antes de cada uno, no una vez: quien mueve la locale lo hace mientras
     la app arranca). Solo se mueve `LC_NUMERIC`, y a `"C"`, que es lo que ya esperan FFmpeg, TDLib
     y Skia; el formateo de .NET no pasa por la locale de C.

   - **Sitios ocho a once de la animación compartida, y los cuatro estaban en el camino caliente**
     (2026-08-25, primera ejecución de las tres tandas acumuladas). La regla ya estaba escrita;
     lo que faltaba era mirar el log. Medido con `UNIGRAM_HISTORY_PROBE` y un rato de scroll:
     **55 «NativeDispatcher unhandled exception» en dos minutos y medio**, todas
     `ArgumentException: An item with the same key has already been added. Key:
     Microsoft.UI.Composition.ScalarKeyFrameAnimation`.
     - `Views/ChatView.AnimateSizeChanged` (las **dos** sobrecargas): una `anim` creada antes del
       bucle y arrancada sobre cada contenedor visible. 48 de las 55, una por lote de mensajes
       insertados, y **la excepción sale de `ItemsPanelRoot_LayoutUpdated`**, o sea del sitio que
       esta misma sección señala como el que deja el panel del historial sordo.
     - `Views/ChatView.OnCollectionChanged`: lo mismo, y como el manejador es `async void` la
       excepción llegaba por `Task.ThrowAsync`, sin ninguna pista de que fuera una animación.
     - `Controls/Cells/ChatFolderCell.OnCurrentStateChanged`: `offset`, `spring`, `fadeIn` y
       `fadeOut` compartidos entre los dos iconos y el título. Sale de
       `SelectorItem.OnPointerPressed`, así que **cada pulsación en una pestaña de carpeta perdía
       su `PointerPressed` y su `PointerReleased` enteros** (`Failed to raise 'PointerPressedEvent'`).
     - `Controls/Chats/ChatHistoryArrows.ShowHideMentions`: `translate` sobre `PollVotesPanel` y
       `ReactionsPanel`.
     Todos arreglados con una función local que devuelve una instancia nueva por visual. El patrón
     a buscar en el resto del árbol es literal: `StartAnimation(` dos veces con el mismo
     identificador. **Reincidente en `ReactionsMenuFlyout` (u-087)**: la misma instancia arrancada
     en TRES visuales con `"Scale"`, en cada uno de los tres métodos. Merece la pena mirarlo en el
     mismo momento en que se sustituye un muelle: las dos trampas viven en el mismo bloque, y
     arreglar solo una deja la otra para la ronda siguiente.
   - **`Compositor.CreateSpringVector3Animation()` es `[NotImplemented]` y LANZA.** Aparece nada
     más arreglar lo anterior en `ChatFolderCell` (rama «Normal → Selected»), y hay cuatro sitios
     más en el árbol compartido —`Controls/ColorSlider.cs`, `Controls/Messages/ReactionsMenuFlyout.xaml.cs`
     (×3), `Views/Popups/SendLocationPopup.xaml.cs`— que harán lo mismo el día que entren al
     subconjunto. **Los tres de `ReactionsMenuFlyout` entraron con la consolidación #5 e hicieron
     exactamente eso** (u-087): la barra de reacciones abría VACÍA, porque `InitializeCore` moría
     en el muelle con la píldora ya en pantalla y antes de crear un solo `ReactionButton`. Al
     transcribir la curva, **ojo con los números**: `-3 / 9 / 7` son los de SU sitio, que anima una
     traslación; lo reutilizable es la forma (`0`, `0,6`, `1` con sobrepaso) y el pico hay que
     recalcularlo para el amortiguamiento de cada sitio — el sobrepaso de un muelle es
     `exp(-πζ/√(1-ζ²))`, o sea **16% con ζ=0,5 y solo 5% con ζ=0,7**, que es el de esta barra. Sustituto: tres `InsertKeyFrame` con sobrepaso (`0 → -3`, `0.6 → 9`, `1 → 7` para
     un `DampingRatio` de 0,5), que es la misma curva que dibuja el muelle.
   - **Uno anima `RotationAngle` (radianes) y NO `RotationAngleInDegrees`.** No es un no-op: es una
     excepción **por fotograma compuesto**, lanzada desde dentro de `Compositor.RenderRootVisual`,
     que además abandona el resto de las animaciones de ese fotograma. Medido con un solo anillo de
     descarga en pantalla: **15.104 excepciones en siete minutos**. La lista de propiedades que Uno
     sí acepta está en `Visual.SetAnimatableProperty` (AnchorPoint, CenterPoint, Offset, Opacity,
     Orientation, **RotationAngle**, RotationAxis, Size, TransformMatrix, Scale y `Translation` si
     `SetIsTranslationEnabled`); todo lo demás cae al `CompositionPropertySet` de la base y de ahí
     sale el `Unable to set property`. Arreglado en `Controls/ProgressBarRing.cs` y
     `Common/VisualUtilities.Tilt.cs` con una constante `RotationProperty` + un factor
     `RotationUnit` (π/180) bajo `#if LINUX`. Asignar la propiedad directamente
     (`visual.RotationAngleInDegrees = 0`) sigue estando bien; lo que no vale es **animarla**.
   - **La cabecera pegajosa del tema de un foro se destruía con una expresión de `Scale`**
     (`ChatView.Bubbles.ViewVisibleMessages`). El numerador de `scaleExp` es el borde superior del
     separador relativo al viewport, y necesita que `scroll.Translation.Y` y el `Offset` del
     contenedor estén en el mismo espacio; aquí no lo están, porque `ScrollingPropertySet` no es la
     de manipulación del `ScrollViewer` (Uno no tiene
     `GetScrollViewerManipulationPropertySet`) sino una que el port refresca a mano en
     `ViewChanged`. Medido en pantalla: `Scale` entre **-21 y -58** —espejada y gigante— así que la
     píldora del tema no se veía **nunca**, mientras la fila separadora de debajo ya se había
     apagado a opacidad 0 para cederle el sitio: el nombre del tema simplemente desaparecía. El
     mismo numerador mueve `Translation.Y` justo encima y ahí no se nota. Arreglado dejando
     `Scale = 1` bajo `#if LINUX`: la cabecera aparece y desaparece con su animación de opacidad,
     como la de la fecha que tiene al lado, sin encogerse al ceder el relevo.
   - **La animación de composición no se ve en `UNIGRAM_DUMP_TREE` si solo se lee el
     `FrameworkElement`.** `ShowHideDateHeader`, `ShowHideForumTopicHeader`, la barra de la galería
     y media docena más animan `Visual.Opacity`, que **no** es `UIElement.Opacity`: el volcado
     enseñaba la caja entera de un elemento que no pintaba un solo píxel. Desde el 2026-08-25 el
     volcado imprime también `c-opacity`, `c-offset`, `c-scale` y `c-clip` (con los cuatro insets y
     el `Size` del visual, para distinguir un `InsetClip` correcto de uno con la talla vieja)
     cuando no valen lo de por defecto. Es lo que encontró la cabecera del tema en dos minutos
     después de una hora de mirar capturas.

   - **Salir del proceso: `_exit`, no `exit`.** El hilo `TdReceive` es un hilo de fondo aparcado
     dentro de `td_receive()` con 300 s de espera y **nadie lo despierta al salir**. Medido con
     `UNIGRAM_SCREENSHOT=<s>:<png>:exit`, que llama al mismo `Application.Current.Exit()` que el
     «Salir» de la bandeja: el proceso terminaba **139 (SIGSEGV)** —no el SIGABRT que arregló la
     auditoría de la frontera nativa, otro— y el kernel lo dice con nombre y apellidos:

     ```
     TdReceive[2547031]: segfault at bc ip ... in libtdjson.so[3c6c23,...]
     ```

     Es ese hilo caminando sobre variables globales de TDLib que los destructores estáticos de
     `exit()` ya han desmontado. `Platforms/Desktop/Program.cs` se suscribe a `ProcessExit`
     **después** de que `host.Run()` haya vuelto, con lo que es el último manejador del proceso —los
     anteriores (el volcado de ajustes de `LocalSettings`, `unigram_native_shutdown`) corren
     primero— y termina con `_exit(0)`, que no ejecuta ni un destructor. Verificado: `echo $?` = 0 en
     dos salidas seguidas, y ni una línea de `segfault` en el journal. El precio, dicho claramente:
     con `_exit` el código de salida es siempre 0, así que **una salida sucia ya no se delata sola**;
     si algún día hay que buscar una, quitar la suscripción de `Program.cs` es un cambio de una línea.

     **El código de salida 15 de la tanda de cierre: lo que se creía y lo que está medido.** La
     tanda del historial vacío dejó escrito que 13 ejecuciones salieron once veces **0** por el
     camino de `UNIGRAM_SCREENSHOT=<s>:<png>:exit` (`Application.Current.Exit()`, el mismo que el
     «Salir» de la bandeja) y que **dos murieron antes de su hora con código 15**, sin una línea en
     el log, sin excepción y sin traza en el journal ni en `dmesg`, y de ahí dedujo que era un
     `exit(15)` de verdad, que `ProcessExit` no había corrido y que por tanto **se habían perdido
     los ajustes**. La tanda del 2026-08-26 (tarde) fue a medirlo y **las tres inferencias son
     falsas o no se sostienen**. Por orden:

     - **No son dos ejecuciones, son tres, y no las une el clic: las une la ausencia de un cierre
       programado.** Los códigos son `r3-tema-arbol` 15, `r6-galeria` 15, `r8-galeria2` 15,
       `r9-galeria3` 124 (`timeout`, bajo `strace`) y **0 las nueve restantes**. Y la correlación es
       exacta: las **cuatro** ejecuciones sin `UNIGRAM_SCREENSHOT=…:exit` son las tres del 15 más la
       del 124; las **nueve** que lo llevaban salieron 0. `r3` no tenía ni `UNIGRAM_MEDIA_TEST` ni
       galería ni clic sobre una foto, así que «~17-25 s después de un clic sobre una burbuja de
       foto» describe dos casos de tres, no la causa. Con la exposición contada
       (≈1 040 s repartidos en las nueve que salieron 0, ≈250 s en las tres que murieron) el reparto
       no es casualidad: lo que las separa es el arnés, no lo que la app estaba haciendo.
     - **Un código distinto de 0 NO demuestra que `ProcessExit` no corriera.** Medido sobre .NET
       10.0.11 con una app de consola mínima bajo `dotnet run`: `Environment.Exit(15)` **sí**
       dispara `ProcessExit` y el proceso sale 15 igualmente. El `_exit(0)` de `Program.cs` no lo
       tapa porque **solo está suscrito después de que `host.Run()` vuelva**: mientras la app corre,
       ese manejador todavía no existe. Lo que de verdad **no** dispara `ProcessExit` es
       **`SIGTERM`**, y `SIGTERM` sale 143, no 15.
     - **No hubo pérdida de ajustes, y no la habría habido aunque el 15 fuera real.**
       `LocalSettings` no vuelca solo al salir: `MarkDirty` arma un temporizador de **500 ms**
       (`SaveDelay`) y escribe el árbol entero de forma atómica (`.tmp` + `File.Move`). Comprobado
       en marcha: `settings.json` se reescribe mientras la app corre, no al cerrarla. La ventana de
       pérdida es medio segundo de cambios, no la sesión.

     **Y 15 no puede salir de dentro del proceso.** Tres barridos, los tres negativos:

     | qué se buscó | cómo | resultado |
     |---|---|---|
     | una señal que dé 15 | 22 señales al hijo y al `dotnet run`, .NET 10.0.11 | ninguna: las muertes por señal salen **128+N** (`SIGTERM` 143, `SIGKILL` 137) y varias (`INT`, `HUP`, `QUIT`, `SEGV`, `ABRT`, `BUS`) salen **0** con `ProcessExit` corriendo |
     | `exit(15)` en código nativo | `objdump` sobre las **77** `.so` de un `/proc/<pid>/maps` real | ninguna llamada a `exit`/`_exit` con la constante 15; las constantes que existen son 1, 2, 3, 0xa, 0x4a y −1, y los únicos `exit(<registro>)` son los de `libcoreclr`/`libclrjit` |
     | `Environment.Exit(15)` en código gestionado | tabla `MemberRef` de **todos** los ensamblados desplegados | solo `Unigram.dll` referencia `System.Environment.Exit`, y sus dos llamadas pasan **0** (`Platform/DBus/DesktopIntegration.cs`); **nadie** referencia `Environment.ExitCode` |

     O sea: **el 15 no lo puede producir ni la app ni ninguna biblioteca que carga**. Queda el
     arnés de aquella tanda —un `run.sh` que ya no existe— como el sospechoso que no se puede
     descartar; conviene no volver a apuntar el código de salida sin guardar también el script que
     lo apuntó.

     **Y lo que sí se reprodujo —tres veces— es la MUERTE, y tiene nombre: un `SIGTERM` de otra
     tanda.** Con `ExitWatch` puesto, tres de las nueve ejecuciones de esta tarde murieron con la
     misma firma exacta del histórico —el log cortado a mitad del tic de 1 Hz de la sonda, sin
     excepción, sin traza en el journal— y las tres dejaron la línea que faltaba:

     ```
     [exit-watch] signal SIGTERM status=128+N thread=.NET Signal Han
     ```

     Un muestreo de `ps` cada 0,5 s durante una de ellas (`x9`) cierra el círculo: la app muere a
     los 9 s de vida y **cuatro segundos después aparece otro `Unigram` cuyo padre es
     `systemd --user`**, o sea arrancado por **activación de D-Bus** (`org.unigram.linux.service`,
     el que escribe `DesktopEntry.Ensure`), y el journal de usuario enseña a ese proceso creando
     `DialogViewModel` y `ProfileViewModel`: **otra tanda estaba lanzando y conduciendo la app**. Su
     arranque empieza, como el de todos los arneses de este proyecto, matando lo que ya corra:

     ```bash
     pkill -f 'net10\.0-desktop/Unigram$'
     ```

     Ese patrón no distingue **la app de quién** está matando. Con dos tandas vivas, la que llega
     segunda mata a la primera a mitad de medición, y la primera se muere sin decir nada porque
     `SIGTERM` **no ejecuta `ProcessExit`**. Encaja con todo lo del histórico, incluida la
     correlación que parecía inexplicable: las ejecuciones **con** `UNIGRAM_SCREENSHOT=…:exit` duran
     lo que dura su cierre programado y casi siempre terminan antes de que a nadie le toque
     arrancar; las que no lo llevan **se quedan vivas** hasta el `timeout` y son las únicas que dan
     tiempo a que otra tanda pase por encima. Y encaja con el número: **15 es `SIGTERM`**, y `143`
     es `128 + 15`; un arnés que normalice el código de señal (o que lea `WTERMSIG` en vez de
     `$?`) escribe exactamente `15` y deja el `124` del `timeout` intacto, que es justo lo que
     quedó apuntado. Dos reglas que salen de aquí:
     - **matar por patrón es matar a ciegas**: si un arnés tiene que limpiar antes de arrancar, que
       mate por PID propio o que compruebe que no hay otra tanda midiendo, no `pkill -f` sobre el
       nombre del binario;
     - **apuntar el código de salida en crudo**, sin normalizar, y guardar el script que lo apuntó
       junto a los logs: aquí se perdió el arnés y con él la única pieza que faltaba.

     **La receta del defecto, en cambio, no reproduce.** Ocho ejecuciones con la de `r6`/`r8`
     (`UNIGRAM_MEDIA_TEST=30:photo:13` + `UNIGRAM_HISTORY_PROBE` + el mismo clic real sobre la
     burbuja, 200 s cada una, **sin** cierre programado, o sea el grupo que murió): **ninguna murió
     por su cuenta** — cinco llegaron al `timeout` (124) y las tres que se cortaron antes lo hicieron
     por el `SIGTERM` de la otra tanda, con su línea. Un aviso para quien lo intente otra vez:
     `strace` no es la única forma de
     mirar quién sale, y es la peor, porque cambia el reloj. Un `LD_PRELOAD` que interpone
     `exit`/`_exit`/`_Exit`/`quick_exit`/`abort` y vuelca `backtrace_symbols_fd` **no usa `ptrace`**
     y no cambia el ritmo de nada: se valida en veinte segundos contra un `Environment.Exit(15)` de
     mentira (la pila sale nombrando `libcoreclr.so`, que es como se distingue una salida gestionada
     de una nativa). El único hueco es un `DllImport` que resuelva `exit` por `dlsym`, que se salta
     la interposición — el `_exit(0)` de `Program.cs` es justamente uno, y por eso no ensucia.

     **Lo que hay puesto desde ahora, para que la próxima vez el número venga con una línea:**
     `Telegram.Linux/Platform/ExitWatch.cs`, instalado en la primera línea útil de `Main`. Pone una
     miga de pan en las **tres** puertas de salida de un proceso Linux y no depende de ninguna
     variable de entorno:
     - **`on_exit(3)`** — se dispara con **cualquier** `exit(status)`, también uno hecho desde
       código nativo que nunca llega al apagado gestionado, y a diferencia de `atexit` **recibe el
       status**. Es la que separa «alguien llamó a `exit(N)`» de «el runtime se fue por otro
       camino»: si la línea está, la llamada pasó por `exit` de libc; si el proceso muere con un
       código y no hay línea, no pasó por ahí.
     - **`ProcessExit`** — apagado gestionado.
     - **`SIGTERM`/`SIGINT`/`SIGHUP`/`SIGQUIT`** — con `PosixSignalRegistration`, sin cancelar la
       señal (el proceso sigue saliendo 128+N, que es lo que espera quien lo lanzó).
     Las tres escriben `[exit-watch] <puerta> status=<n> thread=<comm>` con un `write(2)` crudo a
     stderr **antes** de tocar nada más —sin lock que trabar y sin asignar— y solo después intentan
     el fichero de log con la pila gestionada. Las dos que no ejecutan `ProcessExit` (la señal y el
     `exit()` nativo) llaman además a `LocalSettingsContainer.FlushNow()`, que cierra la ventana de
     500 ms del temporizador. El `thread=` es lo que más dice: nombra si la salida vino del hilo de
     interfaz, de `TdReceive` o de un hilo de decodificación.

   - **La columna del botón de enviar/grabar acaba en `Scale = 0` y `Opacity = 0`: la caja está y no
     se pinta ni un píxel.** Medido el 2026-08-25 al integrar la tanda de grabación, con la caja de
     mensaje de un chat abierto por el camino de producción y **la nueva instrumentación de
     `UNIGRAM_DUMP_TREE`**, que es lo que lo hace visible de un vistazo:

     ```
     Grid #ButtonRecord [1332,744 48x48] c-opacity=0,00 c-scale=0,00,0,00
       ChatRecordButton #btnVoiceMessage [1332,744 48x48] bg=SolidColorBrush
     Grid #SendMessageButton [1308,720 0x0] collapsed c-opacity=0,00
     Border #btnSuggest [1332,792 8x0] c-opacity=0,00 c-scale=0,00,0,00
     ```

     El contenedor **no está colapsado y mide sus 48x48**, o sea que maquetación, plantilla y estilo
     están bien: lo que falla es el pintado. Confirmado además en píxeles
     (`fase1/integracion-grabacion/caja-mensaje-sin-boton-recorte.png`): la píldora del campo de texto
     y el clip de adjuntar se ven, y **en el extremo derecho no hay nada**.

     El sitio es `ChatView.UpdateSendButtons` (`Views/ChatView.xaml.cs`), que cruza los tres botones
     —enviar, grabar y editar— con cuatro animaciones de composición.

     **ARREGLADO, y NINGUNA de las dos hipótesis que esta entrada dejó abiertas era la causa.** Ni
     el orden descendente de los keyframes (`Vector3KeyFrameAnimation` los guarda en un
     `SortedDictionary`, así que el orden de inserción no decide nada) ni la falta de `Duration`
     (`TimeSpan.Zero` hace que `KeyFrameEvaluator` devuelva `_finalValue`, el keyframe de 1). La
     causa es que **`Compositor.RegisterAnimation` suelta la animación si el visual todavía no
     tiene `CompositionTarget`**, y `CheckButtonsVisibility` se llama por primera vez desde
     `ChatView.OnNavigatedTo`, antes de que la página esté enganchada a un target: sin registro no
     hay re-evaluación, y la única escritura que queda puesta es la que hace `StartAnimation`, o
     sea el keyframe de progreso 0 — `Scale 0` y `Opacity 0` para el botón que *aparece*. El
     arreglo es escribir el estado final a mano en `SendButtonsSettled` bajo `#if LINUX`, parando
     antes las cuatro animaciones (una `KeyFrameAnimation` sigue mandando sobre su propiedad
     aunque haya terminado, ver la entrada de más arriba). Comprobado en la app el 2026-08-26 con
     la caja de mensaje de un chat abierto: caja vacía →
     `Grid #ButtonRecord [1308,787 48x48]` y `Grid #SendMessageButton … collapsed`; al teclear →
     `Grid #SendMessageButton [1308,787 48x48]` y `Grid #ButtonRecord … collapsed c-scale=0,00,0,00`;
     al enviar, los dos vuelven al estado de partida. Es decir: el botón azul aparece y desaparece
     con el texto, que era lo que no se veía.
     Esa misma causa tiene un hermano peor cuando el objetivo de la animación **no es un `Visual`**:
     ver la entrada «`Uno.UI.Composition` sólo anima `Visual`» al final de esta regla.

     Lo que **sí** está resuelto en ese mismo método y conviene no volver a romper: `SendButtonsSettled`
     es lo que fija la `Visibility` final de los tres botones y colgaba de `CompositionScopedBatch.Completed`,
     que en Uno no se dispara nunca; el port lo sustituye por `batch.EndWithCompleted(Constants.FastAnimation,
     SendButtonsSettled)` (`Telegram.Linux/Xaml/CompositionScopedBatchEx.cs`). Eso funciona: en el volcado
     `#SendMessageButton` sale `collapsed`, que es exactamente lo que ese handler decide.
   - **Un `x:Name` en un recurso: Uno genera el campo para unos tipos y para otros no, pero emite
     el `UpdateResourceBindings()` de TODOS.** WinUI convierte
     `<DataTemplate x:Name="ListTemplate">` dentro de `<UserControl.Resources>` en un campo; Uno lo
     mete en `Resources["ListTemplate"]` y además emite un `__UpdateNamedResources` —enganchado a
     `Loading`— con una línea `ListTemplate.UpdateResourceBindings();` por recurso nombrado. Si el
     campo no existe, **el build se rompe desde DENTRO del fichero generado** (`CS0103` con una ruta
     de `obj/…/XamlCodeGenerator/`), aunque el code-behind no nombre el recurso ni una vez.
     Y el reparto es **por tipo**, que es lo que despista: `<Style x:Name="VerticalListViewItemStyle">`
     **sí** recibe su propiedad (el par `ElementNameSubject` de siempre, líneas
     `__that.VerticalListViewItemStyle = __p1;`), mientras que `DataTemplate` y `ItemsPanelTemplate`
     **no**. O sea que en un mismo `<UserControl.Resources>` unos nombres compilan y otros no.
     Sustituto: un `partial` Linux con accesores sobre el diccionario —
     `NamedResource<T>(key)` => `Resources.TryGetValue(key, out var v) ? v as T : null`. Ya estaba
     escrito para `ChatView` (`Telegram.Linux/Xaml/ChatView.Linux.cs`, 16 plantillas) y ahora
     también para `ForumView` (`Telegram.Linux/Xaml/ForumView.Linux.cs`, 6). **Regla: declarar solo
     los que Uno no genera**; declarar además el `Style` es un miembro duplicado.
   - **El modo compacto de la lista de chats NO lo hace `UpdateViewState`: lo hace un clip de
     composición, y sin él la lista se ve entera por debajo de lo que la sustituya.** Lo que queda
     vivo de `ChatCell.UpdateViewState(chat, compact, animate)` en upstream es **solo** cambiar la
     insignia de no leídos por la compacta: el bloque que desplazaba la fila a la izquierda
     (`Translation` + `InsetClip.LeftInset`) está **comentado**. Todo el efecto de «la lista de
     chats se encoge a su carril de avatares» es `chats.Clip = CreateInsetClip(0, 0, ancho, 0)`
     sobre el visual de `VisualTreeHelper.GetChild(ChatsList, 0)`, con
     `ancho = ChatsList.ActualSize.X - 68`. Medido el 2026-08-25 al montar la lista de temas de
     foro: sin ese clip el panel de temas sale **encima** de la lista de chats a tamaño completo, y
     como su `BackgroundRoot` es `SettingsItemBackground` = `#0DFFFFFF` (**5 % de blanco**, pensado
     para apoyarse en el fondo de la página, no en otra lista), las dos se ven **superpuestas y
     mezcladas**. Con el clip puesto: lista de chats 463 → carril de 68 px, `TopicListPresenter`
     ocupa los 395 restantes, que es exactamente su anchura. Aplicado en
     `MainPage.ShowHideTopicList` (`#if LINUX`).
     Dos cosas que se comprobaron de paso y valen para cualquiera que reproduzca un
     `batch.Completed` a mano: la plantilla `local:ChatListListView` de `Themes/Generic.xaml` **no**
     lleva prefijo `win:` y su raíz **sí** es el `Grid` que `GetChild(ChatsList, 0)` espera (visto en
     el volcado), así que ese índice no es una forma de WinUI que aquí no exista; y el segundo clip,
     `dialogs.Clip = CreateInsetClip(0, ChatsList.Margin.Top, 0, 0)`, **no** se come la cabecera del
     panel de temas: medido con la lista abierta, `DialogsPanel` está en `y=-38`,
     `ChatsList.Margin.Top` es 76 —o sea que el clip empieza en `y=38` de ventana— y
     `TopicListPresenter` empieza en `y=100`, con 62 px de margen.
   - **`pkill -f "^$HOME/.dotnet/dotnet Unigram"` NO mata la app lanzada con
     `dotnet run`.** El patrón anclado de §«Cómo compilar / ejecutar» describe el proceso cuando se
     arranca como `dotnet Unigram.dll`; con `dotnet run --no-build` —que es lo que hace
     `tools/run-unigram.sh`— el hijo real tiene como línea de comando
     `…/Telegram.Linux/bin/Debug/net10.0-desktop/Unigram`, sin `dotnet` delante, y el `pkill` del
     propio script pasa por encima sin tocarlo. Se queda una instancia viva que luego parece «otra
     tanda ocupando la máquina». Para contar de verdad lo que hay corriendo, sin contarse a uno
     mismo (un `pgrep -f` casa con la línea de comando del shell que lo ejecuta, y eso hizo perder
     veinte minutos el 2026-08-25 esperando a un fantasma que era el propio comando):
     `ps -eo pid,cmd --no-headers | grep -E '^\s*[0-9]+ (…/dotnet Unigram\.dll|…/net10\.0-desktop/Unigram)'`.

   - **`VisualCollection.RemoveAll()` LANZA SIEMPRE, incluso sobre una colección vacía.** Uno
     construye la notificación de cambio como un `Reset` **llevando la lista de lo que quitó**, y un
     `Reset` sólo puede ir sin elementos: el constructor de `NotifyCollectionChangedEventArgs` lo
     rechaza —«Reset action must be initialized with no changed items»— aunque esa lista venga
     vacía. O sea que no es un caso raro: **toda** llamada a `RemoveAll()` revienta.
     Dónde dolió: `MessageBubble.Highlight` lo llamaba sobre un `ContainerVisual` que él mismo
     acababa de crear veinte líneas antes, así que la llamada era además redundante. Se llevaba por
     delante **cada salto a un mensaje**: responder, un resultado de búsqueda, una mención. La
     excepción salía por el despachador, no por el camino de la llamada, así que el salto se hacía
     a medias y nadie lo relacionaba con el resaltado.
     Quedan dos llamadas más en `Controls/Chats/ChatBackgroundPresenter.cs:402,415`, hoy fuera del
     subconjunto. Ahí la colección **sí** puede tener hijos, así que la salida no es borrar la
     llamada sino quitarlos uno a uno.

   - **`FindName` en Uno es una búsqueda por el ÁRBOL VISUAL, no por el namescope de XAML.**
     `FrameworkElement.FindName` llama a `IFrameworkElementHelper.FindName`, que compara el `Name`
     del propio elemento y luego recorre `FindLastChild` sobre los hijos. El generador de XAML sí
     emite `__nameScope.RegisterName("Header", …)`, pero **nadie vuelve a leer ese diccionario**. La
     consecuencia práctica: un elemento declarado como VALOR DE PROPIEDAD —dentro de
     `<ListView.Header>`, `<ListView.Footer>`, `Flyout`…— no entra en el árbol hasta que el
     `ItemsPresenter` construye su presentador de cabecera, o sea bastante después de navegar, y
     hasta entonces `FindName` contesta `null` sin decir nada. Medido sobre el `Uno.UI.dll`
     desplegado (6.6.184).
     Dónde dolió: `ProfileTabPage.Header` es `FindName(nameof(Header))` y
     `ProfilePage.OnNavigated` hace `tabPage.HeaderHeight = …` acto seguido, así que la
     `NullReferenceException` se escapaba desde dentro del evento `Navigated` del `Frame`: la
     pestaña se caía y el perfil salía con la cabecera y el cuerpo vacío. Sustituto en
     `Views/Profile/ProfileTabPage.cs`: buscar el nombre recorriendo a mano el subárbol que cuelga
     de `ListViewBase.Header`, que es la propiedad donde el XAML lo dejó, esté parentado o no.
     `ScrollingHost` sí se encuentra porque **es** hijo del `Grid` de la página.

   - **El `DataContext` de una página NO ha llegado a lo que cuelga de un `ScrollViewer` cuando
     corre `OnNavigatedTo`.** `NavigationService` hace `page.DataContext = viewModel` antes de
     levantar `OnNavigatedTo`, y en WinUI para entonces el valor está en todos los descendientes.
     En Uno la herencia baja por `DependencyObjectStore.ApplyChildrenBindable`, y ese recorrido
     **se salta** a cualquier candidato cuyo `GetParent()` no sea la instancia que recorre
     (`if (obj2 != null && obj2 != ActualInstance) continue;`). Un `ScrollViewer` no es padre
     directo de su contenido: lo reparenta al `ScrollContentPresenter` al aplicar la plantilla, que
     es en la primera medida —después de navegar—. Así que en `OnNavigatedTo` esa rama es una isla
     sin `DataContext`.
     No es un defecto de pintado: `ProfileHeader.ViewModel` es `DataContext as ProfileViewModel`, y
     `UpdateChat` e `InitializeScrolling` lo desreferencian en su primera línea, o sea que la
     excepción se lleva la navegación entera y la página se construye y no se enseña nunca.
     Sustituto: entregar el `DataContext` a mano, que es lo que `ChatView` ya hacía consigo mismo
     (`DataContext = _viewModel`). En `Telegram.Linux/Xaml/ProfilePage.Linux.cs`, suscribiendo
     `DataContextChanged`, que se dispara cuando `NavigationService` lo asigna: antes que
     `OnNavigatedTo` y antes que el `OnNavigatedToAsync` del view model. De ahí para abajo un
     `ContentControl` ya lo propaga solo.

   - **El lexer de expresiones de composición no tiene NI `&&` NI `||` NI `!` NI `==` NI `!=` NI
     `%`.** Su alfabeto entero, leído de `ExpressionAnimationLexer._knownTokens` y de las dos ramas
     explícitas de `EatToken`, es: `+ - * / . , ( ) ? :` más `< <= > >=` más identificadores y
     números. Un `&&` lanza `ArgumentException: Unexpected character '&'` desde `StartAnimation`
     —no al construir la animación, sino al arrancarla—, y por tanto desde donde esté ese
     `StartAnimation`. En `ProfileHeader.InitializeScrolling` eso significaba desde
     `ProfilePage.OnNavigatedTo`, o sea otra vez la navegación entera al suelo.
     Reescritura: `a && b ? X : Else` es `a ? (b ? X : Else) : Else`. El parser acepta un ternario
     entre paréntesis (`ParsePrimaryExpression` con `OpenParenToken` recurre a `ParseExpression`
     completo), y como son aritmética pura sobre propiedades de visuales, evaluar la cola común dos
     veces en el texto no cuesta ni cambia nada. Hecho en `Controls/ProfileHeader.xaml.cs`; los
     otros tres sitios del subconjunto con `&&`/`||` en una expresión (`ChatListListView.cs:612`,
     `MessageSelector.xaml.cs:820/821`) ya están bajo `#if !LINUX` por lo de `InteractionTracker`.

   - **`ItemsWrapGrid` es un cascarón y su sustituto, `VariableSizedWrapGrid`, engaña al
     `ItemsPresenter` sobre el eje en el que apila cabecera y panel.** Lo primero ya estaba medido
     (tipo `[NotImplemented]`, deriva de `Panel`, sin `MeasureOverride` ni `ArrangeOverride`, y su
     `ItemsWrapGridLayout` deriva de `System.Object`). Lo segundo es nuevo y muerde justo después:
     `ItemsPresenter` decide si coloca `Header / Panel / Footer` en X o en Y con
     `(Panel as Panel)?.PhysicalOrientation`, y en `VariableSizedWrapGrid` esa propiedad interna es
     `internal override Orientation? PhysicalOrientation => Orientation;` — o sea el eje de FLUJO DE
     ELEMENTOS, no el de desplazamiento. `Orientation="Horizontal"` en una rejilla envolvente
     significa «filas, envolviendo hacia abajo», luego se desplaza en VERTICAL; el `ItemsPresenter`
     lee `Horizontal` y pone la cabecera **al lado** de la rejilla. (El mismo tipo implementa
     `IOrientedPanel.PhysicalOrientation` de forma explícita y ahí sí lo invierte bien; el
     `ItemsPresenter` lee la otra.) `ItemsStackPanel` no se ve afectado: en un panel de pila el eje
     de flujo **es** el de desplazamiento.
     Qué se ve: la cabecera de estas pestañas es un separador sin ancho, así que se encoge a nada a
     la izquierda y las miniaturas se dibujan desde arriba del todo, encima de la cabecera del
     perfil, de la tarjeta de datos y de la tira de pestañas.
     `PhysicalOrientation` es `internal` en `Panel`, o sea que no se puede redefinir desde fuera de
     Uno.UI. El desplazamiento se muda al `Padding` superior de la lista, que el `ItemsPresenter`
     aplica como origen de su rectángulo **en los dos ejes**
     (`finalRect = new Rect(new Point(padding.Left, padding.Top), …)`).
     `ProfileTabPage.HeaderStacksBesideItems` marca las tres pestañas de rejilla (Media, GIF,
     Regalos) y `HeaderHeight` escribe el `Padding` en vez de la altura del separador.

   - **Un `ChatBackgroundControl` al que nadie ha llamado `Update()` NO pinta nada en Windows y SÍ
     pinta en Linux.** `ChatBackgroundRenderer` cae a `_defaultColors`
     (`{0xDBDDBB, 0x6BA587, 0xD5D88D, 0x88B884}`, el fondo libre por defecto de Telegram), así que
     un control sin fondo asignado dibuja un degradado verde-amarillo a tamaño completo. Upstream se
     apoya en la suposición contraria: `ProfilePage.OnNavigated` solo se molesta en colapsar
     `BackgroundRoot` `else if (_backgroundUpdated)`. En un perfil eso era cada píxel del área de la
     tarjeta —detrás de las filas de datos, de la tira de pestañas y del cuerpo de la pestaña— y
     dejaba la pantalla lavada (las etiquetas de las pestañas se dibujan encima al 5 % de blanco).
     Colapsado siempre salvo para las pestañas alojadas en un chat (`IProfileChatPage`), que son las
     únicas que quieren fondo detrás.

   - **`VariableSizedWrapGrid.MeasureOverride` puede lanzar `IndexOutOfRangeException`** desde
     `OccupancyMap.FindUsedGridSize` → `OccupancyBlock.GetItem`. Visto una vez al abrir la pestaña
     Media de un chat con muchas fotos; Uno se la traga en su camino de medida y la rejilla se
     maqueta igual en la pasada siguiente, así que hoy es ruido en el log y no una pérdida visible.
     Anotado porque es del panel que sustituye a `ItemsWrapGrid` y porque, si algún día una pestaña
     de rejilla sale vacía, es el primer sitio donde mirar.

   - **`ShapeVisual` no dibuja nada si su `Size` es (0,0), y `RelativeSizeAdjustment` no lo llena.**
     `Visual.RelativeSizeAdjustment` ya estaba en esta lista como `[NotImplemented]` silencioso;
     lo que añade la medida es por qué en un `ShapeVisual` se nota tanto: su `Paint` abre con
     `if (Size.X == 0f || Size.Y == 0f) return;`, y el escalado del `ViewBox` es
     `Size / ViewBox.Size` (que además IGNORA `Stretch`, guardado y nunca leído). O sea que el
     patrón «`RelativeSizeAdjustment = Vector2.One` + `ViewBox` con `Stretch = Uniform`», que es
     como Unigram dibuja las insignias vectoriales, produce un control invisible sin una sola línea
     de aviso. Sitio: `Controls/ProfileRating.cs` (la insignia de nivel del perfil), arreglado
     fijando `visual.Size` a las 24x24 que su propio `MeasureOverride` devuelve.

   - **El esquema de TDLib de este port es MÁS VIEJO que el que compila upstream, y eso no es una
     trampa de Uno pero se disfraza de una.** `Common/PageBlockHelper.cs` (2.333 líneas, Instant
     View) nombra `RichTextButton`, `TextEntityTypeButton`, `InlineButton`, `PageBlockButtonRow`,
     `PageBlockDocument`, `PageBlockExpandableBlockQuote`, `PageBlockUnsupported`,
     `InputPageBlockButtonRow`, `PageBlockTable.IsCompact` y un `InputPageBlockTable` de cinco
     argumentos: **ninguno** está en `Libraries/tdjson/td_api.tl`, así que el generador no los
     emite y el fichero son 55 errores que solo se arreglan recompilando tdjson. Lo que el
     subconjunto usa de verdad (`GetRichText`, `GetPlainText`, `GetLinks`, `FindFirstMedia`) vive
     ahora en `Telegram.Linux/Hubs/PageBlockHelper.cs`, público y en `Telegram.Common`, que además
     absorbe la copia privada que `Td/Api/TdExtensions.cs` llevaba dentro. Antes de dar por «no
     portable» un fichero que no compila, mirar si los tipos que le faltan están en el `.tl`.

   - **Un `ListView` de Uno se queda con TODO el ancho (y el alto) que se le ofrezca, aunque su
     contenido mida cuatro píxeles.** Medido con la tira de historias: un `ListView` con
     `ItemsStackPanel` horizontal y un único item de 72 px se arregló a `417x88` porque eso era lo
     que quedaba libre en la fila. Da igual mientras la lista tenga banda propia; en cuanto
     comparte fila con algo (aquí, la zona de arrastre de la ventana) hay que **escribirle `Width`
     y `Height`**, no dejarlos a la medida. `StoriesStrip` lo hace en dos sitios: `Height` en el
     constructor (`CompactRowHeight`) y `Width` en `UpdateVisibility`, a partir del número de items
     y con un tope (`MaxCompactItems`) para que la tira no pueda comerse la fila del título entera.
   - **`PreviewKeyDown` SÍ está implementado en Uno, y es un túnel de verdad (2026-08-27).**
     Rectifica un hallazgo anterior que lo daba por no implementado y de ahí concluía que los
     teclados de `ChatView`, `GalleryWindow` y `Telegram.Linux/Stories/StoriesWindow.cs` estaban
     muertos: **corren los tres**. El IL de `Uno.UI.dll` no deja dudas —
     `UIElement.PreviewKeyDown` es `AddHandler(PreviewKeyDownEvent, value, handledEventsToo: false)`,
     y `Uno.UI.Xaml.Core.InputManager.OnKey` hace
     `uIElement.RaiseTunnelingEvent(UIElement.PreviewKeyDownEvent, e)` **antes** del
     `RaiseEvent(KeyDownEvent, e)`, **con el mismo objeto de argumentos**. Tres cosas del contrato
     que hay que saber antes de usarlo:
     1. `UIElement.RaiseTunnelingEvent` recorre `this.GetAllParents().Reverse()`, o sea **de la raíz
        hacia el elemento enfocado**, y **NO incluye al propio elemento enfocado**: sólo a sus
        ancestros. Un handler en el elemento que tiene el foco no se dispara en la fase de túnel.
     2. Cuando **no hay nada enfocado**, Uno levanta las dos fases sobre
        `ContentRoot.VisualTree.RootElement`, que está **por encima** de `WindowControl`: ni el túnel
        ni la burbuja pasan por el contenido de la ventana. Por eso `InputListener` engancha **las
        dos** fases y desduplica con una referencia al último `KeyRoutedEventArgs`.
     3. El foco dentro de un popup **no** pasa por `WindowControl` en ninguna de las dos fases:
        `PopupRoot` es **hermano** de `PublicRootVisual`, no hijo. Sigue abierto (`PARIDAD.md`, A12).
     **Regla práctica**: para que un atajo se comporte como el preproceso de Windows
     (`CoreDispatcher.AcceleratorKeyActivated`), hay que engancharlo con
     `AddHandler(UIElement.PreviewKeyDownEvent, …)` sobre un **ancestro** del elemento enfocado.
     Con `KeyDownEvent` a secas se llega el último y sólo si nadie puso `Handled`.
   - **`FocusManager.GetFocusedElement()` —la sobrecarga sin argumentos— contesta `null` en el host
     de escritorio de Uno, sin lanzar (2026-08-27).** Resuelve por
     `DXamlCore.Current.GetHandle().ContentRootCoordinator?.Unsafe_IslandsIncompatible_CoreWindowContentRoot`,
     que en una ventana de escritorio no existe; el `?.` devuelve null y nadie se entera. Está
     marcada `[Obsolete("Use GetFocusedElement overload with XamlRoot parameter.")]` en el propio
     `Uno.UI.dll`, y el aviso hay que tomárselo en serio: **la sobrecarga con `XamlRoot` sí
     resuelve** (`xamlRoot.VisualTree.ContentRoot.FocusManager.FocusedElement`), y es la que usa el
     propio `InputManager` de Uno para repartir las teclas.
     Dónde dolió: `Telegram/Common/Extensions.cs` (`FocusManagerEx.TryGetFocusedElement`) usaba la
     de sin argumentos, así que **todas** las ramas de `ChatView.OnPreviewKeyDown` que preguntan qué
     tiene el foco —Ctrl+C sobre una burbuja, Supr, las guardas de AvPág/RePág— se caían ahí en
     silencio: sin marcar la tecla y sin dejar una línea de log. El síntoma («el teclado del chat no
     hace nada») es indistinguible del de un evento no implementado, y por eso se persiguió por el
     lado equivocado. Sustituto aplicado: `TryGetFocusedElement` admite un `XamlRoot` opcional y en
     Linux lo prefiere; los ocho llamantes de `ChatView.xaml.cs` y el de `GalleryWindow.xaml.cs`
     pasan el suyo. **Regla: en este port, ningún `FocusManager.GetFocusedElement()` sin `XamlRoot`.**
     Quedan sin convertir los llamantes fuera del subconjunto (`SettingsProxyPage`, `SendFilesPopup`,
     `CreatePollPopup`, `ChooseChatsPopup`, `InteractionsView`) y dos que sí compilan pero cuyo
     `XamlRoot` no está a mano (`MainPage.xaml.cs`, `ContentPopup.cs`): mismo patrón cuando toque.
   - **`Telegram/Views/Host/RootPage.xaml.cs` NO está en `Telegram.Linux.csproj`.** El port tiene su
     propia copia, `Telegram.Linux/Hubs/RootPage.xaml.cs`, y editar el compartido **no hace nada y la
     compilación no se queja**. Costó una ejecución entera el 2026-08-27 (el menú lateral seguía
     enseñando «Contactos» y «Llamadas» después de un build limpio). **Antes de editar un fichero de
     `Telegram/` para cambiar algo que se ve en Linux, comprobar que está en el csproj**:
     `grep -c 'Telegram\\Views\\Host\\RootPage.xaml.cs' Telegram.Linux/Telegram.Linux.csproj`.
     Los que hoy tienen copia propia están en `Telegram.Linux/Hubs/` y `Telegram.Linux/Xaml/`.
   - **Los modificadores de un atajo no se pueden inyectar con la máscara del evento: hay que
     mandar la tecla del modificador.** `tools/key.py` pone la máscara en el campo `state` del
     `XSendEvent` y nunca manda un `KeyPress` de `Alt_L`/`Control_L`. La rama Linux de
     `InputListener` **no lee `args.KeyboardModifiers`**: lee `WindowContext.KeyModifiers()`, que
     sale del `KeyboardStateTracker` de Uno, y ese tracker se alimenta de eventos de tecla reales
     (`UIElement.TrackKeyState`, llamado desde `RaiseEvent` **y** desde `RaiseTunnelingEvent`). O
     sea: con `key.py alt+Down` el atajo **nunca** se ejercita, y el resultado parece un fallo del
     código. Para medir un atajo con modificador hay que mandar `KeyPress(Alt_L)`, luego la tecla
     con `state=Mod1Mask`, y luego `KeyRelease(Alt_L)`.
   - **`Microsoft.UI.Xaml.Controls.TextBlock` de Uno no tiene `HasOverflowContent`** (sí tiene
     `IsTextTrimmed`, que Uno calcula en `UpdateIsTextTrimmed` comparando el arrange de los inlines
     con el `ActualWidth`/`ActualHeight`). Por eso `FormattedTextBlock.HasOverflowContent` es
     `false` bajo `#if LINUX`. No basta con cambiarlo: sus tres consumidores son el «Ver más» del
     caption de una historia, y ese mecanismo está montado sobre `RichTextBlock` +
     `RichTextBlockOverflow`, y `RichTextBlock` está `[NotImplemented]` para `__SKIA__`.
   - **`ListViewBase.ChoosingItemContainer` está `[NotImplemented("__SKIA__")]` y sus `add`/`remove`
     sólo llaman a `TryRaiseNotImplemented`**: el manejador **ni siquiera se guarda**. Suscribirse
     compila y no avisa. Dentro del subconjunto lo paga `SuggestTextBox`, que con eso deja de
     preseleccionar el primer resultado de la búsqueda de chats.
     **Ampliación, u-003 (panel de emoji/stickers/GIF, 2026-09-02)**: los cinco cajones lo usaban
     para TODO — plantilla, estilo, `ContextRequested` y el registro en el zoomer. Medido cuál de
     esas partes se pierde de verdad: estilo y plantilla NO, porque el XAML declara
     `ItemTemplate`/`ItemContainerStyle` como elementos de propiedad y el contenedor por defecto ya
     los aplica; sí el menú contextual y el zoomer, reenganchados desde `ContainerContentChanging`
     (`Telegram.Linux/Xaml/DrawerContainers.cs`). La excepción es `EmojiDrawer`, cuya lista
     **no declara plantilla ninguna**: sus cuatro plantillas se eligen por tipo de ítem dentro del
     manejador, así que sin sustituto **todas las celdas salen sin plantilla**. Sustituto: un
     `ItemTemplateSelector` de verdad (Uno lo implementa; solo `DataTemplateSelector.GetElement` y
     `GroupStyleSelector` están fuera), que es el mismo reparto que ya hacía `SearchChatsView`.
   - **`ListViewBase.ChoosingGroupHeaderContainer` está NotImplemented IGUAL que
     `ChoosingItemContainer`** (u-003, 2026-09-02). Medido sobre la `Uno.UI.dll` desplegada
     (6.6.184, `bin/Debug/net10.0-desktop`): su tabla de NotImplemented lleva el literal
     `event TypedEventHandler<ListViewBase, ChoosingGroupHeaderContainerEventArgs>
     ListViewBase.ChoosingGroupHeaderContainer`, y también toda la superficie de
     `ChoosingGroupHeaderContainerEventArgs` (`Group`, `GroupHeaderContainer`, `GroupIndex`);
     `ContainerContentChanging` **no** lleva literal, que es el control negativo. Construir la
     cabecera no se pierde (el XAML declara `GroupStyle.HeaderTemplate` y `HeaderContainerStyle`, y
     Uno implementa los dos; solo `GroupStyle.ContainerStyle`, `.Panel` y `GroupStyleSelector`
     están fuera). Lo que SÍ se pierde es que `EmojiDrawer`/`StickerDrawer` colgaban de ahí la
     **carga perezosa del pack de stickers**: sin sustituto todo pack instalado se queda en
     marcadores y la pestaña es una rejilla de cuadros vacíos. Sustituto: el mismo disparador desde
     `ContainerContentChanging` — un pack sin cargar se materializa como `StickerViewModel` que solo
     llevan su `SetId` (`StickerDrawerViewModel.cs:616`), así que el primer marcador realizado
     nombra el pack que hay que pedir.
   - **El generador XAML de Uno ABANDONA EN SILENCIO el resto de los hijos del elemento que lo
     contiene en cuanto encuentra una propiedad adjunta escrita como elemento de propiedad que
     lleva una colección** (u-003, 2026-09-02). El caso medido es
     `<common:FluidGridView.Triggers>`. No es que falten los `x:Name`: **el subárbol entero no se
     construye**. En `AnimationDrawer` el `Grid` raíz salía con **un** hijo en vez de dos y la
     barra de pestañas de packs no existía en tiempo de ejecución.
     Medición, con controles: con `Triggers` presente el fichero generado llega a la línea 80 de
     150 y la palabra «Toolbar» aparece **cero** veces; quitándolo llega a la 144 y aparecen
     `ProbeB`, `ToolbarContainer`, `SeparatorProbe` y `Toolbar`. Sondas: un `Border` insertado
     ANTES del `GridView` sí se genera y otro insertado DESPUÉS no, o sea que el corte es
     posicional y ocurre al terminar ese `GridView`, no por colisión de nombres. Los cuatro
     ficheros del subconjunto que llevaban `FluidGridView.Triggers` se truncaban y los dos que no
     (`StickerPanel`, 32 de 32 nombres; `EmojiSkinFlyout`, 6 de 6) salían enteros.
     Sustituto: declarar los disparadores **desde el code-behind** con
     `FluidGridView.GetTriggers(list).Add(...)`, que es lo que el propio `EmojiDrawer` ya hacía para
     los modos ChatPhoto/UserPhoto. Va sin `#if`: en Windows el resultado es el mismo.
     **Regla: en este port una propiedad adjunta con colección no se escribe en el XAML.**
   - **Uno no genera el `UnloadObject(DependencyObject)` que sí genera el compilador XAML de
     WinUI** (u-003, 2026-09-02). `x:Load="False"` funciona — los elementos salen como
     `Microsoft.UI.Xaml.ElementStub` y `FindName` los materializa —, pero la cadena «UnloadObject»
     aparece **cero** veces en el parcial generado, así que el code-behind compartido no compila
     (CS0103, tres llamadas en `StickerPanel.UnloadAtIndex`). `ElementStub.Materialize()` y
     `.Dematerialize()` existen y no están en la lista de NotImplemented, pero **no hay camino
     soportado de vuelta al stub**: una vez materializado, Uno mete el elemento real en el padre y
     el parcial generado solo guarda un `ElementNameSubject` con la instancia. Sustituto:
     `Telegram.Linux/Xaml/StickerPanelLinux.cs`, un `UnloadObject` que **no hace nada** a
     conciencia — el llamante ya hizo todo lo que se ve (`Deactivate()`, `DataContext = null`,
     handlers fuera, pestaña colapsada) y lo único que añadía era soltar el elemento, que es
     memoria, no comportamiento. Ojo al efecto colateral, que sí muerde: al quedarse materializado,
     `if (EmojisRoot == null)` deja de significar «sin cablear», así que la segunda visita a una
     pestaña se saltaba la inicialización y devolvía un control vivo sin ViewModel ni handlers. En
     Linux la guarda pregunta por `DataContext == null`.
   - **`ItemsStackPanel`/`ItemsWrapGrid.FirstVisibleIndex` y `LastVisibleIndex` LANZAN, igual que
     los dos `*CacheIndex`** (u-003, 2026-09-02; la entrada de `Extensions.ForEach` ya cubría los
     de caché). Importa dónde: `Common/AnimatedListHandler.UpdateVisibleItems` los leía **sin
     ninguna guarda**, y ese método es todo `LoadVisibleItems`/`UnloadVisibleItems`/
     `ThrottleVisibleItems`, que los cajones y `StickerPanel` llaman al activarse y en cada cambio
     de pestaña: no degradaba la contabilidad de animaciones, **reventaba el primer cambio de
     pestaña**. Y `ScrollingHost_ViewChanged` de `EmojiDrawer`/`StickerDrawer` los lee **en cada
     tic de scroll**. Sustituto, el mismo de `ForEach`: recorrer `ItemsPanelRoot.Children`, que SON
     los contenedores materializados, y decidir visible/no visible por geometría contra el
     `ScrollViewer` (`DrawerContainers.FirstVisibleItem`). De paso cae también
     `ItemsControl.GroupHeaderContainerFromItemContainer`, NotImplemented, que esos dos manejadores
     usaban para saber en qué grupo estás: se resuelve por el `SetId` del primer ítem visible, que
     es la ruta que el propio autor había dejado comentada.
   - **Séptimo sitio de «una `CompositionAnimation` no se comparte»**: `ChatStickerButton`
     (u-003, 2026-09-02). En `Show` y en `Collapse` arrancaba **la misma** instancia `clip` sobre
     `LeftInset` y `TopInset` del **mismo** visual, y la misma `opacity` sobre el panel y la sombra.
     Como el diccionario va con la animación de clave, la segunda llamada lanza aunque el visual
     sea el mismo. Arreglado con el patrón de siempre: función local que fabrica una instancia por
     llamada.
   - **`VisualUtilities.DropShadow` devuelve `null` en esta cabeza, y hay que tratarlo** (u-003,
     2026-09-02). Está documentado dentro de la propia función desde hace tandas, pero cuatro de los
     ficheros que entraron con el panel lo desreferenciaban **en la línea siguiente y dentro de un
     constructor** (`StickerPanel`, `EmojiDrawer`, `StickerDrawer`, `AnimationDrawer`), o sea que el
     control no se podía ni construir. Compila igual, que es justo por lo que hay que buscarlo a
     mano al meter ficheros nuevos: `grep -n DropShadow` sobre todo lo que entra.
     Relacionado: `ElementCompositionPreview.GetElementChildVisual` está NotImplemented (el
     `Set...` no lo está, solo el getter), así que `ChatStickerButton` no puede buscar el visual de
     la sombra del panel: se lo pasa `StickerPanel.ShadowVisual`, y todos sus usos van guardados.


7. `Constants.Secret.cs` (ignorado por git) lo rellena el usuario con su `api_id`/`api_hash`.
   - **`MessageWriter` de Tmds.DBus.Protocol es un `ref struct`: pasarlo por valor CORROMPE el
     mensaje y el demonio desconecta el proceso.** Su posición vive en sus propios campos
     (`_span`, `_offset`, `_buffered`), así que un ayudante `static void WriteHint(MessageWriter w,
     …)` escribe en el búfer compartido y deja la posición del llamante **donde estaba**: el
     siguiente campo pisa al anterior y el cuerpo declarado sale más corto que lo realmente
     escrito. Lo que lo hace difícil de ver es que **con campos pequeños el mensaje sigue siendo
     válido** —tres hints de notificación se pisaban entre sí, sobrevivía el último, y el aviso
     aparecía en pantalla— y solo revienta cuando algo grande desborda el desajuste: al añadir el
     avatar de 36 KB en el hint `image-data`, **dbus-daemon dejó de parsear y cerró la conexión**,
     con el síntoma `Tmds.DBus.Protocol.DisconnectedException: Connection closed by peer` sobre un
     mensaje que el demonio **no registra en ningún log** (un `dbus-monitor` tampoco lo ve: nunca
     llega a enrutarse). Regla: en este port, **todo ayudante que escriba en un `MessageWriter` lo
     toma `ref`**, y como C# no deja pasar por `ref` una variable `using`, esos métodos usan
     `var writer = …; try { … } finally { writer.Dispose(); }`. Aplicado en los cinco sitios de
     `Telegram.Linux/Platform/DBus/`. Medido contra Tmds.DBus.Protocol 0.92.0.
   - **En GNOME no se puede capturar la pantalla desde fuera, y eso condiciona cómo se verifica el
     escritorio.** `org.gnome.Shell.Screenshot.Screenshot` y `.ScreenshotArea` contestan
     `org.freedesktop.DBus.Error.AccessDenied: Screenshot is not allowed`, `org.gnome.Shell.Eval`
     está desactivado (`(false, '')`), el portal exige aprobación humana por captura, y `/dev/fb0`
     no es legible por el usuario. Como el banner de notificación, la bandeja y el contador del
     dock los dibuja el compositor Wayland, el truco de `RenderTargetBitmap` de la app tampoco
     sirve: no son contenido de la ventana. La verificación equivalente es **el diálogo con quien
     dibuja**: el id que devuelve `Notify`, la señal `NotificationClosed(id, 3)` que el servidor
     solo emite para un aviso que tiene vivo, y las llamadas que `gnome-shell` hace **a nuestro
     objeto** (`Properties.GetAll` sobre `/StatusNotifierItem`, `GetLayout`/`AboutToShow` sobre el
     menú) — un host no construye el menú de un icono que no está pintando. Receta y transcripción
     en `unigram-linux/spikes/DesktopSpike/README.md`.
   - **El `Exec=` de un fichero `.service` de D-Bus se ejecuta con un entorno casi vacío, y eso
     rompe el apphost de .NET.** Es lo que hace falta para que un `tg://` pulsado con Unigram
     **cerrado** abra Unigram: `DBusActivatable=true` en el `.desktop` le dice al lanzador que llame
     por D-Bus, y quien sabe **arrancar** el proceso para esa llamada es el bus, leyendo
     `~/.local/share/dbus-1/services/org.unigram.linux.service`. El bus no hereda el entorno de la
     sesión gráfica, así que el *apphost* (`bin/…/Unigram`, que es lo que `Environment.ProcessPath`
     devuelve tanto con `dotnet run` como en una instalación) no encuentra el runtime cuando .NET
     vive fuera de `/usr/share/dotnet` — en esta máquina está en `~/.dotnet`— y muere con
     `You must install .NET`, **código de salida 131**, antes de una sola línea de C#. El síntoma
     por el lado del que pulsa el enlace es
     `org.freedesktop.DBus.Error.Spawn.ChildExited: Process org.unigram.linux exited with status 131`.
     Sustituto: `DesktopEntry.ExecCommand()` prefiere el **muxer**
     (`/home/…/.dotnet/dotnet /home/…/Unigram.dll`), que no necesita entorno ninguno; el apphost
     queda como último recurso, que es lo correcto para una compilación *self-contained*, donde no
     hay muxer al que apuntar. Medido en `unigram-linux/spikes/ActivationSpike` (sección 4, arranque
     en frío de verdad: se escribe el `.service`, se llama al nombre sin nadie escuchando y se
     comprueba que el bus arranca el proceso **y** le entrega la URI).
   - **En Unix, `Uri.TryCreate(ruta, UriKind.Absolute)` contesta `true`.** Una ruta absoluta
     (`/tmp/a.unigram-theme`) es una URI absoluta con `Scheme == "file"`, así que el orden obvio
     —«¿es una URI? si no, ¿es un fichero?»— cuela la ruta **pelada** en un campo que solo puede
     llevar URIs y le entrega a la app un `/does/not/exist` como algo que abrir. En
     `SingleInstance.Parse` la comprobación de ruta va **antes** que la de URI, y una ruta que no
     existe se descarta con una línea de log en vez de viajar.
   - **`org.freedesktop.ScreenSaver` no sirve para el tiempo de inactividad en esta sesión.**
     `GetActive` contesta
     `org.freedesktop.DBus.Error.NotSupported: This method is not part of the idle inhibition specification`:
     en GNOME ese nombre es solo el protocolo de *inhibición*. Quien publica el número es el
     compositor, `org.gnome.Mutter.IdleMonitor.GetIdletime` (milisegundos, teclado + puntero +
     táctil, Wayland y XWayland). Y el número **tiene** que venir del compositor: un cliente X solo
     ve la entrada que se entrega a sus propias ventanas, y bajo Wayland ni eso.
   - **dconf anuncia el cambio de UNA clave con la ruta entera y la lista de cambios vacía.** Medido:
     un `dconf write /unigram-spike/probe 1` llega como
     `ca.desrt.dconf.Writer.Notify("/unigram-spike/probe", [], tag)` — no como el directorio más el
     nombre de la clave, que es lo que uno escribiría filtrando. Un filtro que solo compruebe
     `prefijo + cambio` se pierde exactamente el caso normal. `DesktopProxy.AffectsProxy` trata la
     lista vacía aparte y compara los dos caminos **en las dos direcciones** (la notificación puede
     venir de más arriba: `("/system/", ["proxy/mode"])`).
   - **Un `Path` de interfaz D-Bus tapa `System.IO.Path`.** `IPathMethodHandler` obliga a una
     propiedad `Path` en el tipo, y dentro de ese tipo `Path.GetFullPath(...)` deja de compilar
     (CS0120). Se cualifica: `System.IO.Path.GetFullPath`.
   - **El PRIMER `DBusSession.AddRegistrarAsync` sobre una conexión fría ejecutaba su registrar
     DOS veces** (2026-08-25, cazado por `spikes/MprisSpike` en su primera ejecución). El método
     apuntaba el registrar en la lista y después llamaba a `ConnectAsync()`, que al crear la
     conexión **reproduce todos los registrars apuntados** — incluido el que se acababa de añadir —
     y luego `AddRegistrarAsync` lo volvía a ejecutar. `AddMatchAsync` y `TryRequestNameAsync`
     aguantan que se les llame dos veces, así que no se notó; `connection.AddMethodHandler` no:
     lanza `InvalidOperationException: A method handler is already registered for the path '…'`.
     Y como esa excepción la **captura el propio `AddRegistrarAsync`**, el síntoma nunca fue un
     fallo visible sino que **contestaba `false` para algo que sí se había exportado** en la
     reproducción: un llamante convencido de que no tiene icono de bandeja, ni reproductor MPRIS,
     ni objeto de instancia única. En MPRIS eso significaba objeto en el bus y **cero
     `PropertiesChanged`**, porque la bandera `_registered` se quedaba a false. Arreglado
     invirtiendo el orden: **conectar primero, apuntar y ejecutar el registrar después.**
   - **GNOME no descubre un reproductor MPRIS en el instante en que aparece el nombre.** Medido:
     con el objeto recién exportado y en `Stopped`, a los ~15 s **nadie** del escritorio lo había
     leído; con `PlaybackStatus = Playing` y metadatos puestos, `gnome-shell`, `gsd-media-keys`,
     `mpris-proxy` (los botones del casco Bluetooth) y `wireplumber` leyeron sus propiedades por su
     cuenta. Consecuencia para cualquier prueba futura: comprobar "¿me ve el escritorio?" **después**
     de anunciar que suena algo, no justo tras registrar, o se concluye lo contrario de lo cierto.
   - **XTEST no llega al manejador de teclas del compositor en esta sesión Wayland**, y por tanto
     no sirve para probar las teclas de medios. Probado con la tecla más fácil de observar:
     `xdotool key XF86AudioMute` con el sumidero silenciado **no cambia nada** (`wpctl get-volume`
     antes y después: `0.00 [MUTED]` las dos veces), y `XF86AudioMute` es una tecla que
     `gsd-media-keys` maneja con total seguridad. Es el mismo hallazgo que ya estaba anotado para
     el teclado del login (HANDOFF.md, `XTestKeyboard`), ahora con una medida limpia. Lo que sí se
     puede probar sin inyectar nada es la **otra mitad** de la cadena: que `gsd-media-keys` tenga
     su proxy sobre nuestro objeto (lo lee, luego lo enruta) y que el objeto conteste al mismo
     mensaje `PlayPause` que le mandaría — `spikes/MprisSpike` hace las dos cosas.

   - **Al meter un control en el subconjunto, mirar si su `<Style TargetType>` de `Themes/Generic.xaml`
     lleva `win:`.** Hay 57 estilos así, puestos en bloque cuando ninguno de esos controles estaba
     dentro. Un control **sin plantilla** no avisa de nada: en el mejor caso no se dibuja (era el caso
     de `local:PlaybackSlider`, o sea la barra de posición entera), y en el peor **lanza dentro de una
     medida** y se lleva la maquetación por delante — `local:PlaybackNextButton.OnApplyTemplate` no
     encuentra su `Target` y le pasa `null` a `ElementCompositionPreview.SetElementChildVisual`.
     Quitarles el `win:` (y a su `</win:Style>`) es todo lo que hace falta; hecho con esos dos el
     2026-08-25 y comprobado a píxeles en `unigram-linux/spikes/TransportSpike`. Ojo también con el
     caso contrario, que ya está: `local:OverlayWindow` sigue siendo `win:` y la galería vive sin él.
   - **Y mirar también los `#if !LINUX` que se pusieron POR ese control, que están en otros
     ficheros y no se ven desde el suyo.** Un control que era stub inerte deja cicatrices donde lo
     usaban: el que hay que buscar no es su nombre de tipo sino el del **servicio** con el que
     hablaba. Caso medido el 2026-08-25, y costó toda la funcionalidad: `PlaybackHeader` entró al
     subconjunto y quedó perfecto —plantilla, botones, barra— pero **la banda de «suena ahora» no
     aparecía nunca**, porque las tres líneas que la encienden viven en `Views/MainPage.xaml.cs` y
     seguían dentro de `#if !LINUX` de cuando `IPlaybackService` era inerte: la suscripción a
     `Playback.SourceChanged` del constructor, su baja, y el `ShowHideBanner(...)` de `OnLoaded`.
     Sin la suscripción, `MainPage.ShowHideBanner` no corre jamás, `FindName(nameof(Playback))` no
     llega a llamarse y `BannerPresenter` se queda vacío y colapsado — **sin una sola excepción en
     el log**, que es lo que hace que se persiga como un fallo del control. Receta: al sacar un
     control de la lista de stubs, `grep -n '#if !LINUX' -A3` sobre los ficheros que lo instancian
     **y** sobre los que usan el servicio del que se alimenta.
   - **Una `CompositionPropertySet` no se anima, y una `ExpressionAnimation` no se entera de que su
     property set ha cambiado.** Medido el 2026-08-25 en `unigram-linux/spikes/TransportSpike`
     (ventana Uno aislada, sin una línea de Unigram; A/B/C al final de su `MainPage.xaml.cs`):
     una `KeyFrameAnimation` sobre una propiedad de un `Visual` **sí** corre (`Offset.X` = 104,5 tras
     700 ms de una animación de 4 s); la misma animación arrancada **sobre la property set** no mueve
     nada y `TryGetScalar("Progress")` sigue leyendo **0,000** a los 700 ms; y un `InsertScalar`
     posterior al arranque de la expresión tampoco la mueve (100,0 → 100,0). Lo que sí funciona es
     **rearrancar** la expresión: 100,0 → 300,0. Dos avisos para quien lo vuelva a medir: el visual
     objetivo tiene que estar **en el árbol** (un `SpriteVisual` suelto no lo tica el compositor, y
     la primera versión de la prueba midió eso), y el getter de `Visual.Offset` sí devuelve el valor
     animado, así que no es él quien miente.
     A quién le toca: `Controls/PlaybackSlider.cs` monta las dos cosas — una property set `Progress`
     leída por dos expresiones (el `InsetClip` de la barra y el `Offset.X` del pulgar) y, cuando está
     sonando, una animación lineal sobre esa property set para que la barra **se deslice sola** entre
     avisos de posición. Esa segunda mitad no hace nada aquí. **No es un fallo visible**: como
     `UpdateValue` fabrica y arranca expresiones nuevas en cada llamada (el caso que sí funciona), la
     barra cae siempre en el sitio correcto; lo que se pierde es la interpolación, así que da **un
     paso por aviso**, cuatro por segundo (`AsyncMediaPlayer.PositionInterval` = 250 ms). En un vídeo
     largo eso es medio píxel por paso; en uno de diez segundos, ocho. Afecta igual a la forma de
     onda de la nota de voz, que es el mismo control. Si algún día molesta, la salida barata es bajar
     ese intervalo, no arreglar Uno.
   - **`ApplicationView` no tiene pantalla completa, pero `AppWindow` sí.**
     `TryEnterFullScreenMode`/`ExitFullScreenMode`/`IsFullScreenMode` son de `ApplicationView` (§6, no
     implementado). El sustituto es el **presenter** de `AppWindow`:
     `SetPresenter(AppWindowPresenterKind.FullScreen)` acaba en
     `X11WindowWrapper.ApplyFullScreenPresenter` → `SetFullScreenMode(true)`, que pone
     `_NET_WM_STATE_FULLSCREEN`; y `Presenter is FullScreenPresenter` es el `IsFullScreenMode` que
     falta. Verificado de punta a punta contra Mutter en `unigram-linux/spikes/FullScreenSpike`
     (**20/20**): la ventana pasa de 1024×640 a **2736×1824 en 0,0** con el átomo puesto, y vuelve
     exactamente a 1024×640 en su sitio con el átomo quitado, dos veces seguidas.
     Tres cosas que hay que hacer bien, las tres medidas:
     - **Volver con el presenter GUARDADO**, no con `AppWindowPresenterKind.Overlapped`: ese *kind*
       fabrica un `OverlappedPresenter` nuevo y tira el tamaño mínimo de ventana que
       `WindowContext.SetPreferredMinSize` le puso al arrancar. Con el objeto guardado, el mínimo
       sigue ahí (comprobado: 640×480 antes y después).
     - **`SetPresenter(AppWindowPresenterKind.CompactOverlay)` LANZA** `NotSupportedException` (el
       `switch` de Uno tiene ese caso escrito como un `throw`). Nadie debe pedirlo.
     - **No hay `VisibleBoundsChanged`** que avise después, así que la mitad de maquetación —
       `Controls.IsFullScreen`, el `Padding` de 40 de la barra de título propia y el `Stretch` de las
       tres imágenes — hay que dispararla a mano desde los mismos dos sitios que cambian el modo.
     Aplicado en `Controls/Gallery/GalleryWindow.xaml.cs` (`FullScreen_Click`, el `Loaded` de
     `ShowAsyncInternal` y `OnBackRequestedOverride`). **Ojo con el último**: la galería solo deshace
     la pantalla completa que puso ella misma, y si falta ese camino la ventana **principal** se queda
     a pantalla completa y sin barra de título cuando la galería se cierra.
   - **Que un método de Uno esté implementado no dice que haga lo que promete, pero que lance sí se
     puede leer sin ejecutar nada.** `unigram-linux/spikes/UnoApiProbe` abre los ensamblados de Uno
     **que `Telegram.Linux` despliega** con `System.Reflection.Metadata` y clasifica cualquier
     tipo/miembro que se le pida: implementado (con el IL en hexadecimal, que para 34 bytes se lee a
     mano), `THROWS NotImplementedException`, «solo registra un aviso», no-op, o el atributo
     `[NotImplemented]` encima. Es la forma barata de contestar «¿esto lo tengo?» antes de escribir
     el `#if`. Con él se midió, además de lo de arriba: que `DisplayRequest.RequestActive` solo
     registra un aviso (**la pantalla se puede apagar durante un vídeo**; upstream ya lo envuelve en
     `try`/`catch`, así que solo cuesta una línea de log por play/pause), y que
     `ApplicationView.IsViewModeSupported` contesta `true` **solo** para `Default`, de modo que el
     botón del mini reproductor de `GalleryTransportControls` se colapsa él solo sin necesidad de
     `#if`.
   - **`CompositionVisualSurface` NO captura un visual cuyo `Opacity` sea 0.** Leído del IL de
     `Uno.UI.Composition` 6.6.184: `CompositionVisualSurface.ISkiaSurface.Paint` llama a
     `SourceVisual.RenderRootVisual`, que empieza con `if (Opacity == 0f || !IsVisible) return;`.
     El patrón de WinUI —poner la fuente a `Opacity = 0` para que no se dibuje donde está y
     capturarla en una superficie— produce aquí una **superficie vacía, sin excepción y sin aviso**.
     Sustituto: dejar la fuente opaca y **aparcarla con `Visual.Offset`**, porque en Uno la posición
     de maquetación no es `Offset` sino `ArrangeOffset` (así que `Offset` queda libre) y
     `RenderRootVisual` resta `GetTotalOffset()` antes de capturar, de modo que mover el visual solo
     lo mueve **en pantalla**; un `Clip` en el control anfitrión lo tapa allí. Aplicado en
     `Controls/ProfilePatternCover.cs` (`OnApplyTemplate` + `HideCaptureSource`).
     **Alcance mucho mayor que ese control**: `Controls/AnimatedImage.cs` (en el subconjunto) hace
     exactamente lo mismo en `UpdateBrush` (`LayoutRoot.Opacity = 0` + `surface.SourceVisual =
     LayoutRoot`), así que **hoy todo `AnimatedImage` con `ReplacementColor` se dibuja en blanco**
     (emoji personalizado teñido, el patrón de los regalos, el sello de «verificado por bot»).
     Sin arreglar: lo usa cada sticker y cada emoji de la app.
   - **Un `CompositionSurfaceBrush` sobre una `CompositionVisualSurface` ignora `Stretch`,
     `HorizontalAlignmentRatio`/`VerticalAlignmentRatio`, `BitmapInterpolationMode` y
     `SnapToPixels`.** `CompositionSurfaceBrush.Paint`, cuando la superficie es `ISkiaSurface`, hace
     solo `canvas.ClipRect(bounds)` + `skiaSurface.Paint(canvas, opacity)`: 1:1 recortado al tamaño
     del sprite. Un `SpriteVisual` **más pequeño** que `SourceSize` **recorta** la imagen (la esquina
     superior izquierda) en vez de escalarla. Sustituto: el sprite conserva el tamaño de la
     superficie y el encogimiento se pasa a `Visual.TransformMatrix`, que `Visual.GetTransform`
     multiplica con `Scale` respecto de `CenterPoint`, así que una animación sobre `Scale` sigue
     funcionando. Y de propina: `CompositionVisualSurface.Paint` recorta con
     `SKRect(0, 0, Size.X, Size.X)` — **la altura usa `Size.X`**, así que una `SourceSize` no
     cuadrada sale recortada en cuadrado.
   - **El `CornerRadius` de un `Panel`/`Border` recorta a sus HIJOS en Uno Skia, no solo al fondo.**
     `Microsoft.UI.Composition.BorderVisual` guarda un `_childClipCausedByCornerRadius` que
     `UpdatePathsAndCornerClip` crea **siempre** que el radio no es cero —haya o no `BackgroundBrush`
     o `BorderBrush`— y lo aplica en `ApplyPostPaintingClipping`, o sea antes de pintar los hijos; y
     `Microsoft.UI.Xaml.Controls.Panel` es `IBorderInfoProvider` con un `BorderVisual` por `Visual`.
     En WinUI **no** es así (`ContentPresenter.CornerRadius` solo redondea el fondo). Juega a favor
     donde hace falta —un `Grid` de 120×120 con `CornerRadius="60"` sale redondo de verdad, que es lo
     que hace posible la vista previa del fondo de chat— pero puede **recortar de más** en otro
     sitio: la plantilla de `MessageService` lleva `CornerRadius={TemplateBinding CornerRadius}` = 11,
     así que en Uno la píldora recorta su propio contenido en las cuatro esquinas y en Windows no.
   - **`RichTextBlock` e `InlineUIContainer` llevan LOS DOS `[NotImplementedAttribute]`** en Uno.UI
     6.6.184 (medido con `spikes/UnoApiProbe` sobre los ensamblados desplegados). Consecuencia: la
     receta `win:RichTextBlock` / `not_win:TextBlock` con el mismo `x:Name` **no basta** cuando el
     párrafo lleva un elemento incrustado; ahí hay que **sacar el elemento del flujo de texto** (un
     `StackPanel` horizontal con el icono al lado de un `TextBlock` que envuelve). Aplicado en
     `Controls/Chats/ChatAccountInfo.xaml`; el code-behind no se entera porque las dos ramas
     conservan el `Span x:Name` y el `CustomEmojiIcon x:Name`.
   - **Un `Brush` que no sea `SolidColorBrush` ni `GradientBrush` usado como FOREGROUND DE TEXTO se
     pinta con su `FallbackColor`, no con su brocha de composición.**
     `Microsoft.UI.Xaml.Documents.UnicodeText.BrushToColor`: `SolidColorBrush`→`Color`,
     `GradientBrush`→`FallbackColorWithOpacity`, `XamlCompositionBrushBase`→`FallbackColorWithOpacity`,
     cualquier otra cosa→negro. Regla: una brocha de composición vale como `Background`, no como
     `Foreground`. Se nota hoy en `MessageUnsupportedContent.xaml`, que pinta el círculo del icono con
     `{ThemeResource MessageServiceBackgroundBrush}` (un `SolidGaussianBrush`): sale `#667A8A96`, el
     `FallbackColor` de `Themes/Accent.xaml`, en vez del `TintColor` con el que la misma brocha pinta
     la píldora que lo rodea. Se arregla en una línea de `Accent.xaml` (`FallbackColor` = `TintColor`
     por tema); no se ha tocado porque es XAML compartido.
   - **`Uno.Media.PathMarkupParser` es un parser propio**: el `Data="M18 10C18 …"` de un `<Path>` de
     XAML pasa por `Uno.Media.GeometryConverter` → `PathMarkupParser`, ninguno de los dos marcado como
     no implementado. La trampa de esta sección sobre `SKPath.ParseSvgPathData` **no afecta** al
     mini-lenguaje de rutas del XAML.
   - **`LinearGradientBrush(GradientStopCollection, double angle)` SÍ está implementado** (dos `.ctor`,
     el de dos argumentos con 44 bytes de IL que calcula `StartPoint`/`EndPoint` a partir del ángulo).
     Es el constructor de toda la paleta de temas de Unigram. Se apunta porque el fichero de
     sustituciones del enlazador de Uno tiene un
     `get_Is_Microsoft_UI_Xaml_Media_LinearGradientBrush_Available` con `value="false"` que induce a
     pensar lo contrario: esa entrada no dice nada sobre Skia.
   - **Uno consulta `FeatureConfiguration.Font.SymbolsFont` (`uno-fluentui-assets.ttf`) ANTES que
     `LinuxFontFallback`**, y las dos fuentes solapan 335 puntos de código del área privada. Leída la
     `cmap`: U+E9B0 → glifo 775 en la Fluent (¡lo define!) y 272 en `Telegram.ttf`; U+EACC/U+EACD/U+EB06
     → 0 en la Fluent y 556/557/614 en `Telegram.ttf`. O sea: un glifo de `Telegram.ttf` en un elemento
     que **no** nombre `{StaticResource SymbolThemeFontFamily}` sale bien solo si la Fluent no define
     ese mismo punto de código; si lo define, sale el icono equivocado, sin error.
   - **El solape de 335 puntos de código con la fuente de iconos de Uno alcanza también a los
     AVATARES DE GLIFO, y ahí nadie lo estaba mirando.** Ampliación de la entrada de
     `LinuxFontFallback`. `ProfilePicture` no dibuja siempre iniciales: para «Mensajes guardados»,
     «Autor oculto», «Mis notas», «Archivados», «Respuestas», una cuenta borrada y ocho casos más,
     `ProfilePictureSourceText.GetGlyph(Icons.…)` mete un punto de código de `Telegram.ttf` en el
     mismo `TextBlock #Initials`, cuya `FontFamily` es `EmojiThemeFontFamilyWithRounded` — que en
     Linux es `XamlAutoFontFamily` a secas. O sea: el glifo llega por `LinuxFontFallback`, y Uno
     consulta ANTES `FeatureConfiguration.Font.SymbolsFont`. De los quince glifos que `GetGlyph`
     puede recibir, **siete están en el solapamiento** y salen con el dibujo de Fluent del mismo
     punto de código: `MyNotesFilled` U+EA61, `AuthorHiddenFilled` U+EA62, `CameraAddFilled` U+EA92,
     `ChatStarsFilled` U+E97C, `PersonDeleteFilled` U+EA40, `PersonQuestionMarkFilled` U+EA41,
     `QuestionCircle` U+E9CE y `CallFilled24` U+E91B. Los otros salen bien sólo porque Fluent no
     define su punto (`ArchiveFilled` U+EA0E, `BookmarkFilled` U+EA0F, `ArrowReplyFilled` U+EA7C,
     `GhostFilled` U+E91A, `BotFilled` U+EA77, `LinkDiagonal` U+E9F8, `PersonTagFilled24` U+EAFD) —
     por eso el avatar de «Mensajes guardados» de la lista de chats sale correcto y **no** avisa de
     nada. **Regla: no basta con que la clave de recurso nombre una familia; si el TEXTO es un
     glifo de `Telegram.ttf`, la familia del `Run` tiene que ser el propio fichero.** Arreglo:
     `ProfilePicture` ya sabe cuándo su texto es un glifo (`ProfilePictureSourceText.IsGlyph`, que
     usa para el margen), así que en la misma rama cambia la `FontFamily` a
     `ms-appx:///Assets/Fonts/Telegram.ttf#Telegram` y la devuelve a la de la plantilla cuando
     vuelven las iniciales. Cuesta tres líneas y quita la última vía por la que un icono de Fluent
     se cuela donde debería ir uno de Telegram.
   - **El indicador de «escribiendo…» no dibujaba nada, y era una decisión, no un fallo.**
     `Controls/Chats/ChatActionIndicator.GetVisual` devolvía `null` literal bajo `#if LINUX` porque
     los seis `IAnimatedVisualSource2` de LottieGen (`Telegram/Assets/Icons`) están fuera del
     subconjunto. El control seguía midiendo 20x20 y el `TypingLabel` de `ChatCell` lleva
     `Margin="24,0,0,0"`, así que lo que se veía era el texto correcto precedido de un hueco vacío.
     Sustituto: los tres puntos dibujados a mano, **una `ShapeVisual` por punto** dentro de un
     `ContainerVisual` colgado con `SetElementChildVisual`, con una `ScalarKeyFrameAnimation` de
     `Opacity` por visual (`IterationBehavior.Forever`, 1050 ms) y el reflejo viajero conseguido
     dando a cada punto la misma curva desfasada un tercio de ciclo. Tres cosas de §6 obligan a esa
     forma y no a otra: una `CompositionGeometry` sólo anima `TrimStart`/`TrimEnd`/`TrimOffset`
     (`Size` y `RotationAngleInDegrees` se los traga Uno sin lanzar), una `CompositionAnimation` no
     se comparte entre visuales, y `DelayTime` sería una segunda cosa que creerse: el desfase va en
     los fotogramas clave.
   - **`ContentPresenter` pelado dentro de un `<Button.Template>` SÍ recibe el contenido** (no hace
     falta `Content="{TemplateBinding Content}"`): `ContentPresenter.OnApplyTemplate` hace
     `SetTemplateBinding` de `Content`, `ContentTemplate` y `ContentTemplateSelector` cuando su
     *templated parent* es un `ContentControl`. Y **`Control.GetTemplateChild` materializa un
     `x:Load="False"`** (`FindNameInScope(...) ?? FindName(...)`, y `FindName` llama a
     `ConvertFromStubToElement`), que es lo que salva a `ReplyMarkupInlineButton.OnApplyTemplate`, que
     escribe en su `IconPresenter` sin comprobar nulo. La trampa del `x:Load="False"` es solo la del
     `ItemsControl`.
   - **`Windows.Foundation.Size.ToVector2()` no es un método de instancia** como en WinUI, sino una
     extensión de `Windows.Foundation.SizeExtensions`: hay que **importar el namespace**. Lo que NO
     hay que hacer es añadir un `using Point = Windows.Foundation.Point;` para tapar la colisión con
     `Telegram.Td.Api.Point` que trae esa importación: **`Telegram/CsWinRT.cs` ya declara ese alias
     como `global using`**, y un segundo alias del mismo nombre en un fichero es **CS1537**, no un
     arreglo (costó una ronda de compilación en `Controls/PatternBackground.xaml.cs`). El alias global
     gana sobre la importación de namespace, así que basta con el `using Windows.Foundation;`.
   - **METODOLOGÍA, y esta muerde en una ronda sin compilador: `grep -r` SALTA EN SILENCIO los ficheros
     que considera binarios, y este repo tiene varios.** `Telegram/Common/Locale.cs` es uno (`file`
     dice `data`): `grep -rn 'Declension'` devuelve 472 llamadas a `Locale.Declension` y **cero**
     definiciones, y la conclusión falsa evidente es «ese método no existe, hay que escribirlo». Con
     `grep -a` aparece en `Common/Locale.cs:139`. Regla: en este repo, toda comprobación de «¿existe
     este miembro?» hecha con grep va con **`-a`**, o no vale nada.
   - **Una animación arrancada sobre un visual que TODAVÍA NO TIENE `CompositionTarget` no se
     registra, no se evalúa nunca y deja la propiedad en el valor del fotograma clave 0.** Es la
     causa, medida el 2026-08-26, de que el extremo derecho de la píldora del cuadro de escritura
     estuviera vacío. `Compositor.RegisterAnimation` (IL de `Uno.UI.Composition.dll` 6.6.184)
     empieza por

     ```csharp
     if (!animation.IsTrackedByCompositor || visual is not Visual v) return;
     var target = v.CompositionTarget;
     if (target != null) { _runningAnimations.Add(animation, target); ... }
     ```

     — con `target` nulo **no hay `else`**: la animación no entra en `_runningAnimations`, así que
     `RenderRootVisual` (que recorre justamente ese diccionario llamando a `RaiseAnimationFrame`)
     no la evalúa jamás. Y la única escritura que llega a la propiedad es la que
     `CompositionObject.StartAnimation` hace en el acto con el valor de retorno de
     `animation.Start(...)`, que es **el fotograma clave en progreso 0** (o el valor actual de la
     propiedad si no hay ninguno en 0). Para una animación de aparecer —`InsertKeyFrame(1, 1)` +
     `InsertKeyFrame(0, 0)`— ese valor es **0**: el elemento se maqueta y no pinta un píxel, para
     siempre, sin excepción y sin aviso.
     Medido en `ChatView.CheckButtonsVisibility`, que se llama por primera vez desde
     `OnNavigatedTo`, antes de que la página esté colgada de un `CompositionTarget`: el volcado daba
     `Grid #ButtonRecord [1332,744 48x48] c-opacity=0,00 c-scale=0,00,0,00` y en pantalla el botón
     de grabar no existía. **Descarta las dos hipótesis que esta sección dejaba abiertas**, ninguna
     de las dos era la causa: los fotogramas clave en orden descendente dan igual
     (`Vector3KeyFrameAnimation` los guarda en un `SortedDictionary`, el orden de inserción no
     decide nada) y `Duration` sin fijar tampoco (con `TimeSpan.Zero`, `KeyFrameEvaluator.Evaluate`
     devuelve `_finalValue`, que es el fotograma clave en 1).
     **Regla: donde haya un handler de fin de lote, ese handler escribe el estado final a mano
     —con su `StopAnimation` delante— en vez de fiarlo a la animación.** Aplicado en
     `Views/ChatView.xaml.cs` (`SendButtonsSettled`, `AttachButtonsSettled`) y, con
     `CompositionScopedBatchEx.QueueCompleted` porque no tienen lote, en
     `Controls/AnimatedGlyphButton.cs` y `Controls/Chats/ChatBottomButton.cs`.
   - **`Size` y `Offset` de una geometría de composición no se pueden animar, y eso cuesta UNA
     EXCEPCIÓN POR FOTOGRAMA COMPUESTO.** Ampliación medida de la entrada de
     `RotationAngleInDegrees`. `CompositionRoundedRectangleGeometry`, `CompositionRectangleGeometry`
     y `CompositionEllipseGeometry` **no tienen `SetAnimatableProperty`**, y el de la base
     `CompositionGeometry` solo compara los tres `Trim*` antes de caer al `CompositionPropertySet`
     de `CompositionObject`, que contesta «Unable to set property». Tres sitios corregidos:
     `ChatView.ShowHideInlinePanel` (`rectangle.StartAnimation("Size", …)`, medido: **1 excepción
     por chat abierto**, siempre justo antes de `SetScrollingMode`),
     `ChatView.ShowHideComposerHeader` (`rect.StartAnimation("Offset.Y", …)`, en cada respuesta y
     cada edición) y `ChatCell` (las tres barritas de «llamada en curso», que además iban con
     `IterationBehavior.Forever`, o sea sin final). En los tres, la rama Linux escribe el valor
     final y no anima. Medido antes/después: 1 → **0** líneas `Unable to set property` en una
     sesión que abre cinco chats, un foro con su panel de temas, Ajustes y la galería.
   - **Doceavo y decimotercer sitio de la animación compartida entre visuales**, los dos en
     caminos que se recorren a diario: `Controls/ChatListListView.OnCollectionChanged` (la misma
     `ScalarKeyFrameAnimation` sobre `visual.Clip` y sobre `visual` — o sea en **cada mensaje
     entrante** con la lista en pantalla), `ChatView.ShowHideComposerHeader` (`animClip` sobre
     **cuatro** objetos), `ChatView.ManagePanelAnimateWidth` (`button` sobre `delete` y `forward`),
     `Controls/Cells/ChatCell.UpdateViewState` (`anim3` sobre `_visual` y `_selectionOutline`),
     `MainPage.ShowHideLeftTabs` (`offset` sobre `header` y `visual`) y
     `Controls/Chats/ChatRecordBar.OnRecordingLocked` (`scale` sobre los botones de «ver una vez» y
     de pausa, que era lo que se los llevaba por delante al bloquear la grabación). Todos con el
     patrón de siempre: una función local que fabrica una instancia por visual.
   - **`CompositionScopedBatch.Completed`: cuatro sitios más convertidos a `EndWithCompleted`**,
     todos ellos en la barra de grabación de notas de voz y en la barra de selección múltiple:
     `Controls/Chats/ChatRecordBar.xaml.cs` (arranque de la grabación —su handler es el que arranca
     el reloj de tiempo transcurrido, que sin él se quedaba clavado en «0:00,0», y el que ata las
     dos `ExpressionAnimation` del deslizador y de la elipse—, parada de la grabación —el que
     desmonta la barra entera, cierra su popup y la colapsa, sin el cual se quedaba encima del
     cuadro de escritura—, el aviso de «ver una vez» y el botón de borrar) y
     `ChatView.ShowHideManagePanel` (su handler es el único que colapsa `ManagePanel`, así que sin
     él la barra Denunciar / Reenviar / Borrar se quedaba para siempre encima del cuadro de
     escritura en cuanto se entraba y se salía de la selección múltiple).
   - **`Visual.RelativeOffsetAdjustment` y `Visual.RelativeSizeAdjustment` están `[NotImplemented]`**
     (las dos cadenas «The member … is not implemented» están en el `Uno.UI.Composition.dll`
     desplegado). No lanzan, no guardan el valor y no avisan. Se notaba en
     `Controls/Messages/MessageFooter.InitializeTicks`: los ticks de enviado/leído se colocaban con
     `AnchorPoint = (1,0)` + `RelativeOffsetAdjustment = (1,0,0)` para pegarse al borde derecho de
     la etiqueta de la hora, y como ninguna de las dos cosas se aplica al posicionado, se pintaban
     **encima de los primeros caracteres de la hora**, con los 22 px reservados a su derecha
     vacíos. Sustituto: desplazamiento absoluto, `Offset.X = Label.ActualWidth - container.Size.X`,
     recalculado desde el `SizeChanged` de la etiqueta (su ancho cambia con cada hora y con
     «editado»). Quedan siete usos de `RelativeSizeAdjustment` sin guarda en el subconjunto
     (`Common/CompositionPathParser.cs:144`, `Common/VisualUtilities.Skeleton.cs:89`,
     `Composition/CompositionDustVisual.cs:236`, `Controls/ProfilePatternCover.cs:111`,
     `Controls/ChatListListView.cs:482/:486/:490`), todos ellos en caminos que hoy no se recorren.
   - **`x:Load="False"` también le cobra el `Spacing` a un `StackPanel`.** Ampliación de la entrada
     del `ElementStub`, y es lo que dejaba **un rectángulo de 395 × 60 px de fondo de página desnudo
     encima de la lista de temas**. `StackPanel.MeasureOverride` suma `Spacing * (visibles - 1)`
     contando `uIElement.IsVisible()`, y `ElementStub` es un `FrameworkElement` que nunca toca
     `Visibility`, así que cuenta como visible: el `ChatListHeader` de `MainPage.xaml`
     (`Spacing="60"`, un `<Border Height="92"/>` y un `ChatTabs` con `x:Load="False"`) mide **152**
     en Uno y 92 en WinUI. `ShowHideTopicList` elegía su relleno preguntando `ChatTabs != null`,
     que es la rama que asume 92, y el panel de temas caía en la ventana a y=100 en vez de a y=40.
     Sustituto bajo `#if LINUX`: derivar el relleno de la altura real de la cabecera,
     `padding = 40 + margin - ChatListHeader.ActualHeight` (40 = alto de la barra de título propia).
     Reproduce exactamente los tres valores de upstream (−14 / −74 / −78) y acierta también en el
     cuarto estado, que upstream tampoco cubre. Medido a píxeles: el panel empezaba en y=100,0 y
     ahora empieza en y=40,0, que es el borde superior de `MasterFrame`.
     De paso: `Opacity` **no** entra en el cálculo de impactos de Uno
     (`UIElement.CoerceHitTestVisibility` solo mira `IsLoaded`, `IsHitTestVisible`, `Visibility`,
     `IsEnabledOverride()` e `IsViewHit()`), así que `Header.Opacity = 0` dejaba el campo de
     búsqueda invisible y pulsable; ahora se acompaña de `Header.IsHitTestVisible = !show`.
   - **`Debug` y `Release` arrancan igual: la comparación que faltaba, hecha el 2026-08-26.** Tres
     ejecuciones de cada configuración con `dotnet Unigram.dll` (no `dotnet run`), la misma máquina
     y el mismo minuto, midiendo el retraso desde la primera línea del log:

     | | → `OnLaunched` | → `OnWindowCreated` | → `CallOnStart` | → ventana visible |
     |---|---|---|---|---|
     | Debug (mediana de 3) | 1,76 s | 2,04 s | 2,27 s | **4,37 s** |
     | Release (mediana de 3) | 1,71 s | 2,07 s | 2,26 s | **4,23 s** |

     La diferencia (3 %) está dentro de la dispersión entre ejecuciones de una misma configuración
     (Debug 4,32–4,83 s; Release 4,20–4,35 s). O sea que **«todo lo medido es Debug» deja de ser una
     salvedad que invalide nada**: el JIT sin optimizar no es el cuello del arranque, y compilar en
     Release no es un arreglo de rendimiento. `Unigram.dll` sí baja de 20,3 MB a 16,0 MB y la
     carpeta de despliegue de 388 MB a 382 MB.
     Y de camino, la **re-medición del arranque** que quedaba pendiente desde que se quitaron los
     trece ensamblados de Hot Design: de los **8,18 s** del informe del 24 de agosto a **4,3 s**
     hasta la ventana visible (mediana de seis ejecuciones). La máquina no estaba quieta —carga 10,
     6 GB de swap ocupados por otras sesiones—, así que el número absoluto es pesimista; la
     comparación entre las dos configuraciones no, porque las seis ejecuciones comparten condiciones.
   - **El estilo de un control cuyo único `<Style TargetType>` lleva `win:` se pone en
     `Telegram.Linux/Hubs/LinuxOverrides.xaml`, que se fusiona EL ÚLTIMO en `App.xaml`.** Hay dos
     diccionarios en esa carpeta y no son intercambiables: `LinuxResources.xaml` se fusiona el
     **primero** (justo detrás de `XamlControlsResources`) y solo lleva claves que WinUI define y el
     tema Fluent de Uno no, para que los diccionarios de después puedan hacer `BasedOn` sobre ellas;
     `LinuxOverrides.xaml` se fusiona el **último**, que es donde tiene que ir cualquier cosa con
     `BasedOn` (un `StaticResource` en un diccionario fusionado solo ve los fusionados por delante, y
     uno que no resuelva tumba los recursos de la aplicación entera al arrancar) o que redefina una
     clave existente. Primer inquilino: `local:MoreButton`, el «…» de la cabecera del chat, de la
     galería, del panel de temas de un foro y de la barra de selección múltiple, que medía **0 px de
     ancho** en los cuatro sitios. Quitarle el `win:` al estilo de `Generic.xaml` no habría bastado:
     su plantilla dibuja el icono con `<muxc:AnimatedIcon Source="{TemplateBinding IconSource}">`
     sin `FallbackIconSource` y `Telegram.Assets.Icons` es inerte en este port. El estilo propio se
     apoya en `DefaultGlyphButtonStyle` (que no es `win:`, ya trae el 48×48 y pinta su `Glyph` con
     un `ContentPresenter`) y usa el mismo glifo que declara upstream, `E10C`, que está en
     `Assets/Fonts/Telegram.ttf` (comprobado con `fc-query`: la cara cubre `e10a-e10c`).

   - **UN PANEL INVISIBLE SIGUE COMIÉNDOSE LOS CLICS: `Opacity` a 0 + `Visibility` que solo se
     escribe desde `batch.Completed`.** La combinación más cara medida hasta ahora, porque el
     síntoma no señala a la causa: `MainPage.ShowHideSearch` (`Views/MainPage.xaml.cs:2375`)
     intercambia la lista de chats por el buscador desvaneciendo `DialogsPanel` con una animación de
     composición, y `Visibility = Collapsed` **solo** se escribe dentro de `batch.Completed`, que en
     Uno no se dispara nunca (ver la entrada de `CompositionScopedBatch`). Como además `Opacity` no
     entra en el cálculo de impactos de Uno (`UIElement.CoerceHitTestVisibility` mira `IsLoaded`,
     `IsHitTestVisible`, `Visibility`, `IsEnabledOverride()` e `IsViewHit()`, y nada más),
     `DialogsPanel` se quedaba **`Visible`, invisible y encima** de los resultados de búsqueda,
     tragándose cada pulsación. Lo que se ve desde fuera es «el buscador se pinta y no hace nada»,
     que es indistinguible de una fila sin enlazar o de un menú contextual sin enganchar — y se
     perdió una tarde persiguiendo las dos. **Lo que lo separa en un minuto**: una sonda de
     `PointerPressed` sobre el contenedor de la fila. Si no se dispara ni una vez, no es la fila: es
     que el puntero no llega. Arreglo: `batch.EndWithCompleted(200 ms, completed)`, que arregla los
     **dos** sentidos (sin él, al salir del buscador `DialogsSearchPanel` se queda encima de la lista
     de chats y `Deactivate()` no corre nunca). Comprobado en el volcado del árbol: al entrar
     `DialogsPanel` queda `collapsed`; al volver, `DialogsSearchPanel` queda `collapsed` y la lista
     vuelve a `489x776`. **Corolario para cualquier medida**: en una pantalla donde dos paneles se
     intercambian, un clic que «funciona» puede estar yendo al de debajo. Aquí pasó: un `openChat`
     dado por bueno venía de la fila de la lista de chats, no del resultado de búsqueda, y sólo se
     detectó porque el id del chat era el mismo. Comprobar SIEMPRE contra un elemento que exista en
     un solo panel, o mirar el `PointerPressed`. Ese mismo método comparte además `opacity1` entre
     dos visuales (`panel` y `Header`), que es la trampa de la animación compartida: separado en
     `opacity3` bajo `#if LINUX`.

   - **`ContentRoot()` puede devolver la celda ANTES de que su propia plantilla esté aplicada, y
     entonces el fallo es de los mortales.** Ampliación de la entrada de `ContentTemplateRoot`.
     `ContentTemplateRootEx` contesta en cuanto el `ContentPresenter` ha expandido la plantilla de
     ítem, pero las celdas de este port (`ProfileCell`, `ChatCell`) son `ContentControl` **con
     `ControlTemplate`**, y sus partes con nombre (`TitleLabel`, `Identity`, `Photo`…) se asignan
     desde `OnApplyTemplate`, que Uno corre en la **primera medida**. O sea que hay una ventana en la
     que la celda existe y todos sus miembros son `null`: `ProfileCell.UpdateSearchResultPhase0`
     escribe `TitleLabel.Style` en su segunda línea, y eso es un `NullReferenceException` lanzado
     desde dentro de `VirtualizingPanelLayout.MeasureOverride` — se lleva la pasada de disposición
     entera, no una fila. Se reproduce sobre un contenedor **reciclado** cuyo presenter acaba de
     reconstruir su contenido para otro tipo de fila, que con un `ItemTemplateSelector` es lo
     normal. Sustituto: `content.ApplyTemplate()` antes de escribir nada (es idempotente y corre
     `OnApplyTemplate` en el acto), y envolver el enlace de la fila en un `try/catch` que registre,
     porque corre dentro de una medida. Aplicado en `Controls/Views/SearchChatsView.xaml.cs`.

   - **`ContainerContentChanging` puede ir una pasada por detrás del presenter, y `Loaded` tampoco
     basta.** Con `ItemTemplateSelector`, el contenedor puede llegar a `Loaded` con el presenter aún
     sin expandir: medido, tres filas de búsqueda (cabecera + dos resultados) salían de `Loaded` sin
     celda. La tercera oportunidad que sí llega es `SizeChanged`, que se levanta después de la pasada
     que por fin expande la plantilla. Patrón: enlazar en `ContainerContentChanging`, reintentar en
     `Loaded`, y **solo** si eso también falla enganchar `SizeChanged` una vez — y registrar el fallo
     ahí, que es el único punto en el que la fila se va a quedar en blanco de verdad.

   - **`AutomationProperties.GetControlledPeers` devuelve `null` en Uno** cuando la propiedad adjunta
     no se ha fijado nunca (WinUI devuelve una lista vacía). `Controls/SuggestTextBox.cs:122` hace
     `AutomationProperties.GetControlledPeers(this).Add(newValue)` dentro del callback de
     `ControlledList`, así que es un `NullReferenceException` **dentro del sistema de propiedades de
     Uno**: se lo traga, y lo que se pierde es silencioso — el resto del *setter* no corre. Aquí eso
     eran las flechas arriba/abajo del cuadro de búsqueda, que es lo único que `ControlledList` hace.
     Guardado con `?.` bajo `#if LINUX`. Apareció el día que `MainPage` empezó a entregarle el
     `SearchChatsView` de verdad en vez del stub.

   - **Un `event` de una clase base no se puede pasar como delegado desde una derivada (CS0070).**
     No es de Uno, es de C#, pero muerde en este árbol: `ChatListListView.GetContainerForItemOverride`
     reemplaza el de `TopNavView` **sin llamar a base**, y la línea que hay que reponer es
     `container.ContextRequested += ItemContextRequested`, que solo compila dentro de `TopNavView`.
     Sustituto: un `protected void RaiseItemContextRequested(...)` en `TopNavView` que la derivada
     engancha. Sin esa línea las filas de chat son las únicas de todo `TopNavView` que nunca reciben
     su menú contextual, y en Windows no se nota porque lo cubría `ChoosingItemContainer`.

   - **`Collections/DiffCollectionExtensions.Reuse` estaba bajo `#if !LINUX` sin motivo de API.**
     `KeyedCollection<T>` se declara **dentro** de `ViewModels/SearchChatsViewModel.cs`, que estaba
     fuera del subconjunto, así que el commit de port a granel guardó el método que lo nombra. Al
     entrar el buscador son 14 errores `CS1061` que parecen faltar un `using`. Regla general: un
     `#if !LINUX` que envuelve **un solo miembro** casi siempre es un tipo que vivía en un fichero
     excluido, no una API de Windows; mirar dónde se declara el tipo antes de escribir un sustituto.

   - **Cómo apuntar un clic en las pruebas, aprendido a base de fallar**: el texto de un elemento va
     entre llaves (`{Nuevo grupo}`), sin ellas se interpreta como `x:Name`; `Lista{texto}` acota la
     búsqueda a los contenedores de esa lista, que es lo que hace falta cuando dos paneles se
     solapan; y **entre dos usos seguidos de un `MenuFlyout` hace falta un clic neutro**: medido, el
     segundo clic derecho tras cerrar un flyout no vuelve a abrirlo, y con un clic en un punto vacío
     en medio sí. Además `FindSaying` casa por **subcadena** sobre cualquier `TextBlock` visible, así
     que el nombre de un chat casa también con la **vista previa** de la fila de archivados: apuntar
     con `ChatsList{...}` o `ItemsHost{...}`, nunca con `{...}` a secas, cuando el texto pueda
     repetirse.
   - **CUATRO TRAMPAS DE UNA SOLA CAJA DE TEXTO, y las cuatro se cobran el mensaje entero o parte de
     él. Medidas el 2026-08-26 al cerrar «enviar texto».** El compositor del port es un `TextBox`
     pelado (`Telegram.Linux/Xaml/ChatViewStubs.cs` + `ChatTextBoxSend.cs`), y upstream escribe
     sobre un `RichEditBox`, cuyo documento es la fuente de la verdad. Copiar la línea de upstream
     tal cual sale mal de tres formas distintas y la cuarta la pone TDLib:
     1. **`AcceptsReturn = false` TRUNCA `Text` a su primera línea, en el acto.** Uno.UI 6.6.184,
        leído del IL: `private void OnAcceptsReturnChanged(bool newValue) { if (!newValue) { var
        text = Text; var firstLine = GetFirstLine(text); if (text != firstLine) { Text = firstLine;
        } } ... }`. Es conforme a WinUI para un `TextBox`; lo que no es conforme es la costumbre de
        upstream (`Controls/FormattedTextBox.cs:418` escribe `AcceptsReturn = !send` en **cada**
        Intro, y sobre un `RichEditBox` es inofensivo). Medido: una caja con
        `"holaprueba 01\rsegunda linea"` salió como `sendMessage { text = "holaprueba 01" }`. Sin
        error, sin aviso, media frase perdida.
     2. **Ni `e.Handled = true` ni saltarse `base.OnKeyDown` impiden que el `TextBox` inserte el
        salto de línea.** Uno hace la inserción en `OnPostKeyDown` → `OnKeyDownSkia`, que la
        maquinaria de eventos enrutados llama **después** del manejador de clase y que **no mira
        `args.Handled` ni una vez**. Su única puerta para esta tecla es
        `flag6 = ... || args.Key == VirtualKey.Enter; if (!flag6 || AcceptsReturn) { …insertar… }`.
        O sea: `AcceptsReturn = false` no es el idiomatismo de upstream copiado por gusto, es **lo
        único** que evita que Intro deje un `"\r"` en la caja después de que el mensaje se haya ido.
        Medido al quitarlo: la caja se quedaba en `text="\r"`, con el botón azul puesto y el de
        grabar sin volver nunca.
        **La regla que sale de 1 y 2 juntas, y es la única que aguanta las dos**: en el camino de
        enviar, **primero se lee y se vacía la caja, y sólo después se baja `AcceptsReturn`** —
        cuando ya no queda ninguna línea que perder. Aplicado en `ChatTextBoxSend.OnKeyDown`, con la
        bajada guardada por `if (IsEmpty)` para que el orden sea un requisito y no una casualidad.
     3. **Escribir en `Text` con `AcceptsReturn` en `false` pierde todo menos la primera línea, y
        eso incluye restaurar un borrador.** Es la misma coacción, por el otro lado: el *coerce
        callback* de `Text` es `if (!AcceptsReturn) { text = GetFirstLine(text); } else if
        (_isSkiaTextBox) { text = RemoveLF(text); }`. Medido: un borrador de dos líneas guardado en
        TDLib como `"borrador final\ndos lineas"` volvía a la caja como `"borrador final"`. Basta
        con subir `AcceptsReturn` **antes** de la escritura — `RemoveLF` convierte el `'\n'` en el
        `'\r'` que Uno usa por dentro, así que no hay que traducir nada. Aplicado en
        `DialogViewModel.SetText` (rama `#if LINUX`) y en `ChatTextBox.SetText`, y `ChatTextBoxSend`
        lo vuelve a bajar en cuanto la caja queda vacía, que es el único momento en que es gratis.
     4. **TDLib TIRA los `'\r'` sueltos, así que la conversión tiene que ir ANTES de
        `ParseMarkdown`.** `clean_input_string_with_entities` tiene un literal
        `case '\r': // skip` (`Libraries/tdlib/td/telegram/MessageEntity.cpp:4217`), y
        `parseMarkdown` lo ejecuta. `ComposeViewModel.SendMessageAsync` sí hace
        `text.Replace('\v','\n').Replace('\r','\n')` (`ComposeViewModel.cs:1134`), pero corre
        **después** de que `GetFormattedText` haya pasado el texto por `ParseMarkdown`, o sea que
        nunca llega a ver un `'\r'`. Medido: tres líneas salieron como
        `sendMessage { text = "borrador 03 no enviado editadolinea doslinea tres" }` — las palabras
        soldadas, sin ningún salto. Arreglado con la misma sustitución en la rama `#if LINUX` de
        `DialogViewModel.GetFormattedText`, antes de `ParseMarkdown`.
     Comprobación final, con la respuesta de TDLib delante:
     `sendMessage { chat_id = -1004467136158, text = "uno\ndos\ntres" }`,
     `updateNewMessage { id = 5242881, sending_state = messageSendingStatePending }`,
     `updateMessageSendSucceeded { id = 6291456, old_message_id = 5242881 }`.
   - **`Uno.UI.Composition` sólo anima `Visual`: una animación arrancada sobre CUALQUIER OTRO
     `CompositionObject` escribe su fotograma de progreso 0 y se queda ahí para siempre.** Es el
     hermano gordo de la entrada de `SendButtonsSettled`, y esta vez la causa está leída del IL,
     no deducida:

     ```
     internal void RegisterAnimation(CompositionAnimation animation, CompositionObject visual)
     {
         if (!animation.IsTrackedByCompositor || !(visual is Visual visual2)) return;
         ...
     }
     ```

     y `CompositionObject.StartAnimation` hace **una sola** escritura,
     `SetAnimatableProperty(propertyName2, subPropertyName, animation.Start(...))`, y confía el
     resto a `AnimationFrame += ReEvaluateAnimation`, que sin registro no se dispara nunca. Con un
     `Visual` la animación corre; con un `CompositionClip`, una `CompositionGeometry` o una
     `CompositionPropertySet`, lo que queda escrito es el fotograma clave de progreso 0.
     **Dónde dolía, medido**: `Controls/ChatListListView.OnCollectionChanged` anima
     `visual.Clip.StartAnimation("BottomInset", …)` de `altura → 0` sobre la fila que **se mueve**
     en la lista de chats. Como el objetivo es el clip y no el visual, la fila se quedaba con el
     fotograma 0, o sea `BottomInset = su propia altura`:
     `ChatListListViewItem [4,202 481x64] c-clip=InsetClip(0,0,0,64)` — **una fila de 64 px
     recortada a nada**. Maquetada, invisible, y **se tragaba su propio clic**: la sonda encontraba
     la fila, el clic salía sobre su centro (`sent: True`) y no había ni un `openChat`. Y como los
     contenedores se reciclan, el clip atascado envenena al siguiente chat que caiga en ese
     contenedor. Lo dispara **cualquier cosa que mueva una fila con la lista a la vista**: un
     mensaje entrante, o guardar un borrador. Arreglo (`#if LINUX`): no se anima el clip y el
     `BottomInset` se escribe a 0 a mano por el caso reciclado. Comprobado A/B en la misma sesión —
     antes: `c-clip=InsetClip(0,0,0,64)` y cero `openChat`; después: sin `c-clip` y
     `openChat{-5106207555}` al primer clic.
     **Regla: en este port, una animación de composición se arranca sobre un `Visual` o no se
     arranca.** Si hay que animar un clip, una geometría o un property set, se escribe el valor
     final a mano.
   - **El generador de eventos enrutados de Uno NO añade `KeyUp` por el hecho de que tu clase
     sobrescriba `OnKeyUp`: hereda las banderas del tipo base.** Comprobado sobre el fichero que
     emite `ImplementedRoutedEventsGenerator` para `ChatTextBox`
     (`obj/gen/Uno.UI.SourceGenerators/…/Telegram.Controls.Chats.ChatTextBox_ImplementedRoutedEvents.g.cs`):
     `RegisterImplementedRoutedEvents(typeof(ChatTextBox), … | RoutedEventFlag.KeyDown | …)` —
     **`KeyDown` sí, `KeyUp` no**, porque `TextBox` implementa `OnKeyDown` y no `OnKeyUp`. Un
     `protected override void OnKeyUp` en una parcial nuestra es por tanto **código muerto**, y el
     fallo es silencioso: en el compositor eso dejaba `_shiftDown` pegado a `true` tras el primer
     Mayús, de modo que a partir de ahí **cada Intro habría hecho una línea nueva en vez de
     enviar**. La forma que sí funciona es registrar el manejador a mano:
     `AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(…), true)`. Dos avisos: el fichero
     `.g.cs` de `obj/gen/` **no se reescribe en una compilación incremental**, así que mirar su
     fecha antes de creerse su contenido; y `OnKeyDown` sí se llama, porque esa bandera la trae el
     tipo base.
   - **`WindowContext.KeyModifiers()` SÍ reporta Mayús en X11: medido, no supuesto.** Era la
     incógnita que quedaba abierta de la tanda de enviar texto. Con `UNIGRAM_COMPOSER_PROBE=1` el
     compositor registra una línea por Intro con las dos lecturas al lado:
     `composer: Enter, KeyModifiers()=Shift, backstop shift=False control=False -> newline` y
     `composer: Enter, KeyModifiers()=None, backstop shift=False control=False -> send`. O sea que
     la API real lleva el modificador y el respaldo local de teclas no hizo falta ni una vez.
     `Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread` **no** está en la tabla de
     no-implementados de `Uno.UI.dll` (a diferencia de `GetKeyState`, `GetCurrentKeyState` y
     `GetForIsland`), y el host X11 trae `XModifierMaskToVirtualKeyModifiers` alimentando
     `KeyboardStateTracker`.

    - **`{CustomResource}` sobre una propiedad ADJUNTA no compila: Uno la escribe como propiedad
      directa del elemento destino.** Medido el 2026-08-26 al meter el árbol de Ajustes, leyendo el
      fichero que genera el `XamlCodeGenerator`. Para
      `common:TextBlockHelper.Markdown="{CustomResource TwoStepVerificationPasswordSetInfo}"` emite

      ```
      __p1.Markdown = global::Uno.UI.ResourceResolver.RetrieveCustomResource<string>("TwoStepVerificationPasswordSetInfo", null, null, null);
      ```

      contra `TextBlock`, que no tiene `Markdown` — o sea **CS1061 en tiempo de compilación**, no un
      fallo silencioso, que al menos es de los buenos. No es la propiedad adjunta lo que le sienta
      mal, sino la combinación con `{CustomResource}`: **un `{x:Bind}` a esa MISMA propiedad adjunta
      se genera bien**, y por eso `AuthorizationCodePage`, que la usa con `x:Bind`, lleva meses
      dentro del subconjunto sin dar guerra. Tres sitios en este árbol: `SettingsPasswordIntroPopup`,
      `SettingsPasswordDonePopup` y `SettingsStoragePage`.
      Dos salidas, y cuál toca depende de si el elemento tiene `x:Name` que alguien use:
      **(a)** duplicar el elemento con `win:` / `not_win:` y rellenar el de Skia desde el
      code-behind con `TextBlockHelper.SetMarkdown(...)` — sirve cuando no hay `x:Name`, y deja
      Windows intacto; **(b)** si el code-behind ya nombra el elemento (no puede haber dos campos con
      el mismo `x:Name`), quitar el atributo del XAML y poner el valor inicial en el constructor
      para **las dos** plataformas.
      **Y ojo con el `not_win:` de la salida (a)**: se declara `xmlns:not_win="http://uno.ui/not_win"`
      **y** se añade a `mc:Ignorable`, que es como lo hacen los otros doce XAML del árbol. Apuntarlo
      al namespace de presentación por defecto (copiando el de `win:`) compila y funciona en Skia, y
      en **Windows** deja el elemento visible: sale el texto dos veces.

    - **El generador `BindableTypeProviders` de Uno no sabe cualificar un tipo que produce OTRO
      generador de código, y emite `typeof(global::NombrePelado)`.** Medido el 2026-08-26. Ese
      generador recorre las propiedades **públicas** de cada tipo que registra el XAML y escribe
      `bindableType.AddProperty("X", typeof(global::…), …)`. Para los tipos escritos a mano de
      `Telegram/Td/Api/*.cs` (`FormattedText`, `MessageEffect`…) sale
      `typeof(global::Telegram.Td.Api.FormattedText)`, correcto; para los que construye
      `Telegram.Generators` a partir de `Libraries/tdjson/td_api.tl` (`PasswordState`,
      `EmailAddressAuthenticationCodeInfo`, `Birthdate`, `FileType`) sale `typeof(global::PasswordState)`,
      que es **CS0400 «no se encontró en el espacio de nombres global»** apuntando a código generado
      sin ninguna pista de dónde viene. La frontera es exactamente esa: escrito a mano bien,
      generado mal.
      **Arreglo**: bajar esa propiedad a `internal` bajo `#if LINUX` (el generador sólo recorre las
      públicas). Todos los consumidores de este árbol están en el mismo ensamblado, así que no se
      pierde nada. Seis sitios en la sección de Ajustes: `SettingsLoginEmailAddressPopup.CodeInfo`,
      `SettingsPasswordConfirmPopup.RecoveryEmailAddressCodeInfo`,
      `SettingsPasswordEmailCodePopup.PasswordState`, `SettingsPasswordEmailAddressPopup.PasswordState`,
      `SettingsPasswordHintPopup.PasswordState`, `SettingsBirthdatePopup.Value` y
      `SettingsStorageOptimizationPage.SelectedItems`.
      **Regla general**: un tipo de TDLib expuesto como propiedad pública de un `ContentPopup` o de
      una `Page` es sospechoso; si es de los generados, `internal`.

    - **`LoopingPicker` es la tercera plantilla `win:`-only del árbol y la primera que LANZA.** Las
      dos anteriores (`MoreButton`, `SettingsHeadline`) sin plantilla miden 0x0 y ya está;
      `Controls/LoopingPicker.cs:36-47` desreferencia `ValueText`, `NextButton` y `PrevButton` nada
      más pedirlos, sin comprobar nulo, así que sin estilo es un `NullReferenceException` **dentro
      de la aplicación de plantilla**: se lleva `ChatMutePopup` entero, no el selector. Es el mismo
      patrón que ya se documentó para `ScrollViewerScrim`. Plantilla copiada a
      `Hubs/LinuxOverrides.xaml`. **Corolario para la lista de estilos `win:`-only**: antes de dar
      uno por «cosmético», mirar si su `OnApplyTemplate` comprueba nulo; si no lo hace, no es
      cosmético.

    - **`SettingsRadioButtonStyle`: un `{StaticResource}` a una clave `win:`-only tumba la página
      entera, no el control.** `Themes/RadioButton_themeresources.xaml` declara **dos** estilos para
      `SettingsRadioButton` y los dos llevan `win:`: el implícito (:12) y el que tiene clave (:15).
      El implícito ya estaba cubierto por el `DefaultStyleKey = typeof(RadioButton)` de
      `Controls/SettingsRadioButton.cs`, y por eso `SettingsAppearancePage` y `SettingsLanguagePage`
      —que usan el control **sin** `Style=`— llevaban meses funcionando. Las trece páginas de
      privacidad lo piden **por clave** (41 usos), y eso es otra cosa: una clave que no resuelve se
      lleva las `Resources` de la aplicación. Redefinida en `Hubs/LinuxOverrides.xaml` **sin
      `Setter` de `Template`**, para que la plantilla siga viniendo del `RadioButton` de Uno y el
      estilo sólo aporte fondo, relleno y altura mínima. Lo que se pierde es el
      `DescriptionPresenter` (una segunda línea bajo la etiqueta), que ninguna de las trece usa.
      **Por qué las páginas nombran la clave en vez de fiarse del estilo implícito**: un estilo
      implícito no lo heredan los tipos derivados, y las filas de privacidad son
      `PrivacyRadioButton : SettingsRadioButton`.

    - **`ApplicationData.Current.LocalFolder.Properties` no contesta las claves del Property System
      de Windows, y el `catch` de upstream lo convierte en un cero creíble.**
      `SettingsStorageViewModel.GetSystemTotalBytes` pide `"System.FreeSpace"` y `"System.Capacity"`.
      Medido: ninguna de las dos cadenas aparece en **ninguno** de los 200+ ensamblados desplegados
      junto al binario (0 y 0). La consulta siempre lanza, el `catch` devuelve `(0, 0)` y la pantalla
      de almacenamiento dice «Telegram usa menos del 0 % de tu disco» — un valor inventado con
      pinta de medida, que es peor que no enseñar nada. Sustituto sin P/Invoke:
      `new System.IO.DriveInfo(ApplicationData.Current.LocalFolder.Path)`, que en Unix va a `statvfs`
      por el runtime y además resuelve el punto de montaje que de verdad contiene los datos.
      **Regla general**: cualquier `RetrievePropertiesAsync` con una clave `System.*` es un cero
      disfrazado en este port; y un `catch` que devuelve un valor neutro sin registrar nada es donde
      estos se esconden.


    - **`Page.OnNavigatedTo` corre ANTES de que nadie le haya puesto `DataContext`: `ViewModel` es
      `null` ahí.** Medido el 2026-08-26 abriendo «Privacidad y seguridad» por primera vez
      (`NullReferenceException` en la línea `ViewModel.PropertyChanged += …`). Uno levanta
      `OnNavigatedTo` desde `Frame.ChangeContent`, que corre **dentro** de `Frame.Navigate`;
      Unigram asigna el ViewModel desde `NavigationService.NavigateToAsync`, que cuelga del evento
      `Navigated` que el frame levanta **después**. El orden del log no deja dudas: `Navigate` en
      .112, la excepción saliendo de `Frame.ChangeContent`, y sólo entonces
      `FacadeNavigatedEventHandler` + `NavigateToAsync` en .173.
      Lo que se pierde no es la excepción —la navegación termina y los `x:Bind` resuelven cuando
      llega el contexto— sino **todo lo que ese método iba a hacer**: suscripciones, el `FindName`
      de un bloque `x:Load`, la primera lectura de algo que no tiene binding.
      Sustituto: `PageEx.WhenViewModelReady(isReady, action)`
      (`Telegram.Linux/Xaml/FrameworkElementEx.Linux.cs`), como salida temprana al principio del
      override. **Y el predicado tiene que ser la prueba de TIPO del llamante, no `DataContext !=
      null`**: una `Page` hereda el `DataContext` por el árbol visual, así que una página de
      ajustes llega con el `SettingsViewModel` del marco que la aloja — no nulo, y no el suyo. La
      primera versión, que sólo comprobaba nulo, fue una recursión infinita que mató el proceso con
      un desbordamiento de pila. Afecta a seis páginas del subconjunto de Ajustes.

    - **HISTORIAS, medido con la app delante el 2026-08-26 (tanda de integración). Cinco cosas,
      las cinco con la cuenta real y las capturas en la mano:**
      1. **`BindableTypeProviders` no cualifica un tipo que produce OTRO generador, y esquivarlo
         bajando la propiedad a `internal` ROMPE EL BINDING EN EJECUCIÓN.** El síntoma de
         compilación es `CS0400: 'Story' / 'ChatActiveStories' no se encontró en el espacio de
         nombres global`, desde `BindableMetadata.g.cs`, en cuanto un `DependencyObject` del
         subconjunto expone una propiedad pública de un tipo de `Telegram.Td.Api`. La tentación es
         hacerla `internal`; con `ActiveStoriesCell.Trigger` se hizo, compiló, y en ejecución Uno
         escribió **una línea por celda**: `The property setter for [Trigger] does not exist on
         [Telegram.Controls.Cells.ActiveStoriesCell]` — resuelve el setter de un binding por
         reflexión sobre miembros **públicos**, así que la celda se quedó sin recibir su
         `ChatActiveStories` y los anillos sin datos, sin un solo error. **El arreglo es el de la
         regla 8**: una `partial` vacía en `Telegram.Linux/Xaml/TdApiPartials.cs` (ya están `Chat`,
         `Message`, `Story` y `ChatActiveStories`) y la propiedad **pública** en las dos cabezas.
      2. **Un `ContentPopup` abierto ENCIMA de un `OverlayWindow` se abre y no pinta.** Medido:
         `StoryViewersPopup` (la lista de «quién la ha visto») llamó a `ShowQueuedAsync`, su
         `Opened` se disparó, `getStoryInteractions` fue y volvió con su `total_count`… y la
         captura no tenía un solo píxel suyo: el visor de historias siguió encima. Dos raíces de
         popup, y la segunda pierde. **Sustituto**: meter el panel en el árbol visual de la propia
         ventana con `Canvas.SetZIndex`, que además es lo que permite suspender la historia de
         debajo mientras está arriba (`StoryViewersOverlay`, `Telegram.Linux/Stories/StoryViewers.cs`).
         **Y esto vale para CUALQUIER `MessagePopup` de confirmación abierto desde dentro de un
         `OverlayWindow`**: el «¿seguro que quieres borrar esta historia?» habría sido invisible, o
         sea un borrado que no ocurre nunca y un botón que no hace nada. Por eso la confirmación de
         borrar también es un `StoryConfirmOverlay` dentro de la ventana, y no un `MessagePopup`.
         **Comprobado en las dos direcciones**: con `MessagePopup` la captura no tenía nada; con el
         overlay sale «Eliminar historia / ¿Quieres eliminar esta historia? / Cancelar / Eliminar»
         encima de la tarjeta, y pulsar «Eliminar» llega a `deleteStory` → `Ok`.
      2b. **Un `CS0246` sobre un tipo que `grep` SÍ encuentra en el árbol suele ser el csproj
         atrasado, no un error de quien compila.** Medido: 20 errores nombrando `IVoipService`,
         `VoipCoordinator` y `MediaDevice*`; los cinco tipos existían, en ficheros que
         `fase1/extra-files.txt` ya listaba (escrito a las 21:36) y que el csproj no incluía
         (generado a las 21:17). `tools/gen-linux-csproj.py` y a 0. **Comparar la fecha de
         `extra-files.txt` con la del csproj es lo primero que hay que mirar**, antes de leer una
         sola línea de fuente.
      3. **Un `pgrep -f` que busca `dotnet build` se encuentra a sí mismo**, y con varias tandas
         compilando a la vez eso es un interbloqueo que no avanza nunca: los shells de espera
         llevan el patrón en su propia línea de comandos. Comprobar el proceso de verdad
         (`pgrep -x dotnet` y leer `/proc/<pid>/cmdline`), nunca el patrón.
      4. **Una segunda instancia NO arranca**: `SingleInstance` ve el nombre `org.unigram.linux`
         tomado, reenvía el `activate` y se va (`org.unigram.linux is already owned: forwarding
         activate and exiting`). Con cuatro tandas a la vez eso parece un cuelgue del arranque y es
         otra app viva. Mirar quién tiene el nombre con
         `dbus-send --session --dest=org.freedesktop.DBus … GetConnectionUnixProcessID
         string:org.unigram.linux` antes de tocar nada. **No** usar `UNIGRAM_NO_SINGLE_INSTANCE=1`
         para saltárselo: dos instancias sobre el mismo directorio de datos de TDLib no es una
         prueba, es un riesgo.
      5. **Un toque y un mantener pulsado son gestos distintos y el arnés sólo sabía dar el
         primero.** `UNIGRAM_CLICK`/`.click` mandaba siempre `ButtonPress`+`ButtonRelease` en la
         misma llamada, o sea un toque, que en el visor **avanza** la historia — justo lo
         contrario de lo que había que medir. Añadido el prefijo **`hold<ms>:`** al objetivo
         (`hold9000:684,410`), que separa las dos mitades con su espera en medio
         (`XTestKeyboard.TryPress`/`TryRelease`). Con él, dos capturas separadas 3,3 s dentro del
         mismo mantener salieron **idénticas píxel a píxel**: la historia está pausada de verdad.

    - **CONTESTADAS, midiendo, dos preguntas que estaban abiertas en §6.** Las dos con la app
      delante el 2026-08-26:
      1. **Un `x:Load` con BINDING dentro de un `ItemsControl` SÍ materializa.** La entrada vieja
         («un `x:Load="False"` dentro de un `ItemsControl` no se materializa nunca con `FindName`»)
         sigue siendo cierta **para `FindName`**, pero el camino del binding es otro y funciona: la
         fila «Correo de acceso» de `SettingsPrivacyAndSecurityPage` es
         `x:Load="{x:Bind ViewModel.HasEmailAddress}"` sobre un hijo directo de un
         `HeaderedControl` (que es un `ItemsControl`) y **apareció, con el patrón del correo**.
         O sea: `x:Load` + `{x:Bind}` dentro de un `ItemsControl`, bien; `x:Load="False"` +
         `FindName` dentro de un `ItemsControl`, sigue sin materializar.
      2. **`BindBack` de `x:Bind` entra al subconjunto** con los dos deslizadores de datos y
         almacenamiento (tamaño máximo de descarga automática y «conservar medios»), que eran los
         dos primeros usos del port. Compilan; lo que hacen al arrastrarlos está en la lista de lo
         que queda por mirar en pantalla.

    - **Un `CollectionViewSource` con `IsSourceGrouped="True"` devuelve una vista VACÍA, sin lanzar
      y sin registrar nada.** Medido el 2026-08-26 en «Dispositivos»: `getActiveSessions` contestó
      `sessions = vector[11]`, el ViewModel tenía sus diez sesiones agrupadas en un
      `KeyedList<KeyedGroup, Session>`, y el `ItemsStackPanel` de la lista medía **831x0**. En
      pantalla salía **un solo dispositivo**, el actual — y ése no viene de la lista, lo pinta un
      `x:Bind` directo, que es lo que hacía la pantalla parecer correcta.
      Los tipos existen en `Uno.UI.dll` (`IsSourceGrouped`, `ItemsPath`, `ICollectionViewGroup`),
      así que no es un `[NotImplemented]` de los que se ven: es de los que contestan y no hacen
      nada. Sustituto en `Views/Settings/SettingsSessionsPage.xaml.cs`: aplanar los grupos en una
      `ObservableCollection<T>` y asignarla a `ItemsSource` desde el code-behind. Se pierde la
      cabecera de grupo; se ganan las filas. **Regla general**: antes de dar por buena una lista
      agrupada en este port, contar los elementos que devolvió TDLib y contar los que hay en
      pantalla.

   - **`Windows.System.UserProfile.GlobalizationPreferences` LANZA en todos sus miembros, no
     devuelve un valor por defecto** (2026-08-27, medido con la app en pantalla y confirmado en el
     IL de `Uno.dll`). `Calendars`, `Clocks`, `Currencies`, `HomeGeographicRegion` y `WeekStartsOn`
     están `[NotImplemented(… "__SKIA__" …)]` y su *getter* es un `throw`. Costó la píldora de fecha
     entera: `CalendarPopup` moría **en su constructor** con
     `System.NotImplementedException: The member IReadOnlyList<string> GlobalizationPreferences.Calendars
     is not implemented`, por el `NativeDispatcher`, sin popup y sin más rastro que una línea en el
     log — o sea que el botón parecía muerto y lo que estaba era reventando. Sustitutos, los dos en
     `#if LINUX`: `CalendarIdentifier` **no se toca** (su valor por defecto ya es el gregoriano, que
     es lo que `Calendars.FirstOrDefault()` contesta en cualquier escritorio de estos) y el primer
     día de la semana sale de `System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek`
     (los dos enums valen Sunday=0…Saturday=6, así que el cast es directo), que además es de donde lo
     saca el resto del escritorio. Los otros sitios del árbol que la tocan —`ScheduleMessagePopup`,
     `ChooseDateTimePopup`, `SettingsNightModePage`, `ScheduleVideoChatPopup`…— **no están todavía en
     el subconjunto**: al meterlos hay que hacer lo mismo. El único que ya lo estaba,
     `InputPopup.GetRegionalSettingsAwareDecimalFormatter` (`HomeGeographicRegion`), queda guardado.

   - **El generador XAML de Uno escribe los tipos de los parámetros de un *delegate* de evento SIN
     CUALIFICAR, y eso no compila** (2026-08-27). Al enlazar `Views/Popups/JoinChatPopup.xaml`, el
     `RecentUserHeadChanged="Participants_RecentUserHeadChanged"` produjo
     `error CS0400: 'MessageSender' no se encontró en el espacio de nombres global`: el delegado se
     declara `RecentUserHeadChangedHandler(ProfilePicture sender, MessageSender messageSender)` y
     `MessageSender` sólo resuelve con `using Telegram.Td.Api`, que el `.g.cs` generado no tiene, así
     que el generador emite `global::MessageSender`. **Le pasa a cualquier XAML de este árbol que
     enganche un evento cuyo delegado nombre un tipo de otro espacio de nombres** — sólo en
     `RecentUserHeads` hay cinco atributos así. Receta: **`win:` sobre el ATRIBUTO** y el `+=` en el
     code-behind bajo `#if LINUX`.
     **Y eso deja medido lo otro**: el prefijo condicional de Uno funciona también en **atributos**,
     no sólo en elementos. Comprobado en el `.g.cs` generado para Skia: con
     `win:RecentUserHeadChanged="…"` el manejador **no aparece**, y el XAML de Windows queda intacto.
     Es la tercera receta de la regla 1 y la más barata cuando lo que sobra es una sola propiedad.

   - **`InlineUIContainer` no guarda su hijo NI lo devuelve: el *setter* lo tira y el *getter*
     lanza.** Ampliación de la nota de `RichTextBlock`/`InlineUIContainer` de más arriba, medida el
     2026-08-27 en el IL: `Child`'s setter llama a `ApiInformation.TryRaiseNotImplemented` y
     descarta el valor; su getter es un `throw`. Consecuencias: (a) un `<Border>` declarado dentro de
     un `<InlineUIContainer>` en XAML **no se dibuja nunca** —era la miniatura de 16 px de la fila de
     la lista de chats—; (b) buscarlo desde código **lanza dentro de una pasada de medida**, que es
     lo que hacía `ChatCell.FindMinithumbnailPanel` hasta ese día: la excepción salía de
     `OnApplyTemplate` en la primera fila con miniatura y el resto de `OnApplyTemplate` no corría
     para esa celda. Regla: si el XAML mete un elemento dentro de un `InlineUIContainer`, dar el
     elemento por ausente y guardar a quien lo desreferencie; no hay forma de alcanzarlo.

   - **Una excepción dentro del *setter* de un enlace se convierte en una línea de aviso y el enlace
     se cae entero.** Uno la registra como
     `fail: Microsoft.UI.Xaml.Data.BindingExpression — Failed to apply binding to property […]` con
     la traza completa, pero **no la vuelve a lanzar**: lo que se pierde es que esa propiedad no se
     escribe, y si el enlace es `OneWay` tampoco se reintenta. Caso medido el 2026-08-27, dos veces
     en cada arranque: `MainPage.HideTopicList` llamaba a
     `MasterDetail.NavigationService.GetChatFromBackStack()` mientras el `Frame` **todavía navegaba
     hacia `MainPage`**, o sea con `NavigationService` en null, porque el `SelectedItem` de la tira de
     carpetas se aplica dentro de `ShowHideTopTabs`. Método para buscarlas: `grep 'Failed to apply
     binding'` en el log de un arranque limpio; cada línea es un enlace que no se aplicó.

   - **`obj/gen/` sólo se escribe si se lo pides, y el que hay en el árbol puede ser de otro día.**
     Los `.g.cs` de los generadores de Uno que varias notas de esta sección citan por línea salen de
     `dotnet build -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=obj/gen`;
     una compilación normal **no los actualiza**. El 2026-08-27 la copia del árbol era del día
     anterior y decía que `SendAsMenuFlyoutItemStyle` no se generaba cuando ya se generaba. Mirar la
     fecha del fichero antes de creerse su contenido, y regenerarlos con esas dos propiedades cuando
     se vayan a citar.


8. **Esquema TDLib**: los tipos `Telegram.Td.Api.*` se generan desde `Libraries/tdjson/td_api.tl`
   (TDLib pública 1.8.66, commit 022d602). Upstream compila contra *drops prerelease* de TDLib no
   públicos, así que algunos tipos que usa el C# **no existen** aquí (`CommunityFullInfo`,
   `MessageChatJoinFromCommunity`, `EphemeralMessageContent`, `UpdateMessageEphemeralContent`, y los
   que vayan apareciendo como CS0246 dentro de `Telegram.Td.Api`). No se inventan: el código que los
   usa va bajo `#if !LINUX` hasta que la TDLib pública los incorpore. El generador descarta además los
   campos documentados "; for bots only". Cuando upstream pasa **más argumentos** a un constructor
   generado que la TDLib pública tiene (p. ej. `new Message(...)` con 43 args frente a 42), no se
   tocan los sitios de llamada: se añade una sobrecarga que descarta el extra en
   `Telegram.Linux/Xaml/TdApiPartials.cs` (ver `Message`).
9. Cifras de build: MSBuild imprime cada error dos veces en el log; el total real es el de la línea
   final "N Errores".
10. Extras que el subconjunto necesita y el scoping no listó (añadir vía `extra-files.txt` si son
    portables): `Collections/IncrementalCollectionView.cs`, `Common/DebouncedPropertyWithToken.cs`,
    `Common/AlbumLayout.cs`, `ViewModels/Dialogs/MessageCollection.cs`, interfaces de servicio
    (`ITranslateService`, `IShortcutsService`+`InvokedShortcut`, `IVoipService`, …) con implementación
    inerte en `Hubs/`; namespace `Telegram.Assets.Icons` (iconos LottieGen) → stub o glyphs.
11. Runtime pendiente tras compilar: el resolver de `libtdjson.so` (copiar de
    `unigram-linux/spikes/TdConsole/Program.cs`: `TDJSON_PATH` → junto al binario), fuentes de
    `Assets/Fonts` como `Content` (`ms-appx:///Assets/Fonts/...`), y recorte de `Themes/Generic.xaml`
    (CanvasControl, RichEditBox, WebView2, AnimatedIcon) que hoy solo compila porque `Uno0001`/
    `UXAML0001` están silenciados.

   - **`Microsoft.UI.Xaml.Controls.IAnimatedVisualSource` de Uno.UI NO es la interfaz de WinUI.** La
     real pide un solo miembro, `TryCreateAnimatedVisual`; la de Uno funde dentro lo que WinUI
     guarda aparte en `IDynamicAnimatedVisualSource` — `Update`, `Load`, `Unload`, `Play`, `Stop`,
     `Pause`, `Resume`, `SetProgress`, `Measure`. Se rompen así las salidas de **LottieGen**, que
     son ficheros generados y no se tocan a mano: `Assets/Icons/PollSelect.cs` y
     `ChecklistSelect.cs` daban **CS0535 ×9 por clase**. Como lo único que este árbol llama de
     verdad es `TryCreateAnimatedVisual` —ninguna de las dos se asigna nunca a un
     `AnimatedVisualPlayer.Source`—, los nueve miembros que faltan son inalcanzables: se
     implementan **explícitamente y en blanco** bajo `#if LINUX`.
     **Y la parte que vale para la próxima**: las firmas no se adivinaron, se **midieron
     reflejando el `Uno.UI.dll` realmente construido** (`Assembly.LoadFrom` + `GetMethods()` desde
     un proyecto de consola desechable; diez segundos). Ante un CS0535 contra una interfaz de Uno,
     preguntarle al ensamblado qué miembros pide sale más barato que deducirlo de la documentación
     de WinUI, que es justamente la que no se cumple. Medido por u-004 el 2026-09-02
     (`port/u004-message-contents` @ `27df8bc`); trasladado aquí por u-008, que era la parcela que
     podía tocar este fichero.

   - **El generador XAML de Uno pierde el espacio de nombres del parametro de un delegado propio.**
     Al enganchar un evento desde el marcado cuyo delegado esta declarado en el arbol —
     `RecentUserHeadChanged`, `Controls/RecentUserHeads.cs:26`, cuya firma es
     `(ProfilePicture sender, MessageSender messageSender)` — el codigo generado emite
     `global::MessageSender` **sin cualificar**, y sale un **CS0400** («no se encontro en el espacio
     de nombres global») dentro de un fichero que nadie escribio. El parametro `ProfilePicture`, del
     mismo delegado, si sale cualificado: falla solo el tipo que vive en `Telegram.Td.Api`, que es
     el espacio de nombres que **produce otro generador** en la misma compilacion.
     **No hay `#if` que lo esquive: la suscripcion la declara el XAML**, y el XAML no tiene
     preprocesador; esquivarlo obliga a editar el `.xaml` de upstream o a suscribirse en code-behind.
     Medido por u-013 el 2026-09-02 sobre `Controls/Chats/ChatGroupCallHeader.xaml` y
     `ChatJoinRequestsHeader.xaml`, que son las dos unicas del arbol que usan `RecentUserHeads` desde
     el marcado.
     **ARREGLADO en u-022, y el arreglo no es una divergencia:** se quita el atributo del `.xaml` y
     se suscribe el evento en el code-behind, justo despues de `InitializeComponent()`. Ahi es C#
     normal y el tipo resuelve como siempre. **La linea es identica en Windows y en Linux**, o sea
     que el `.xaml` PIERDE un atributo en vez de ganar un `#if` — que es lo unico que el XAML no
     sabe hacer. Aplicado y compilado en las dos bandas.
     **Y una trampa de XML que costo una compilacion entera:** el comentario que explica esto no
     puede llevar `--` dentro, porque XML prohibe el doble guion dentro de un comentario. El
     generador de Uno **no dice eso**: se cae en silencio, no emite los campos con nombre del XAML,
     y lo que sale son `CS0103` de «`RecentUsers` no existe en el contexto actual» en el
     code-behind. **Un CS0103 sobre un `x:Name` que esta claramente ahi significa que el generador
     no llego a correr; el error a buscar esta en el `.xaml`, no en el `.cs`.**

   - **Con un generador fallando, la lista de errores de una compilacion NO es exhaustiva.**
     Medido en la misma tanda: una compilacion devolvio 2 errores y un fichero parecia limpio;
     anadiendo otro fichero, la siguiente destapo en ESE fichero un tipo sin resolver que llevaba
     ahi todo el tiempo. **Un build verde prueba; un build rojo solo prueba lo que nombra.** Al medir
     el coste de un port por compilacion —que es el metodo de esta casa— hay que iterar hasta el
     verde antes de creerse la lista.

   - **Un cascaron con `DefaultStyleKey` propio es un fallo latente hasta que alguien lo dibuja.**
     El stub de `MoreButton` conservaba `DefaultStyleKey = typeof(MoreButton)`, y el unico
     `Style TargetType="local:MoreButton"` de `Themes/Generic.xaml` esta dentro de un bloque `win:`.
     En Skia el control **no resolvia plantilla ninguna y media cero** — el mismo fallo que hacia
     invisible el boton de `StickerPanel`. Nadie lo vio en meses porque **ningun sitio vivo dibujaba
     un `MoreButton`** hasta que u-013 metio la barra de traduccion. Los estilos `win:`-only de
     `Generic.xaml` son una clase entera de defectos que solo aparecen cuando el control entra en
     escena.

   - **Los estilos implicitos `win:`-only son una clase entera, y ya se midio entera** (u-016,
     2026-09-03). El mecanismo exacto: una clase que conserva `DefaultStyleKey = typeof(ella misma)`
     y cuyo unico `<Style TargetType>` implicito esta dentro de un bloque `win:` **no resuelve
     plantilla en Skia, mide 0x0, y no lanza ni escribe una linea de log**. No es un fallo que se
     busque leyendo: se busca enumerando. Barrido sobre la cabeza `5f999f4`: exactamente **seis**
     controles (siete filas, una es un par de clases). Copiados los estilos a
     `Telegram.Linux/Hubs/LinuxOverrides.xaml` en vez de quitarles el `win:` — asi el arbol de
     upstream no se toca.
     Gate para que no vuelva: `unigram-linux/tools/check-default-style-keys.py`, validado **en los
     dos sentidos** (las seis filas antes, cero despues), evaluando `#if LINUX` — si no lo evalua
     mide el arbol de Windows. Lleva una lista `KNOWN_OK` con un motivo medido por entrada
     (`ButtonEx`, `FileButton`, `OverlayWindow`, `ProfileRating`, y `StorageChart`, que dibuja por
     composicion con `SetElementChildVisual(this, _visual)` y no necesita plantilla).
     **Y hay un estilo que NO se puede escribir todavia**: `DefaultChatTextBoxStyle` tiene
     `TargetType="FormattedTextBox"`, un tipo que no esta en el subconjunto, y **un `Style` cuyo
     `TargetType` falta es un error de compilacion, no algo que se ignore**. Sus 162 lineas quedan
     listas para levantar en `LinuxOverrides.xaml` + `WIN-ONLY-STYLES.md`; entran con el editor de
     FALTA 1.8, apuntando al tipo sustituto que traiga ese lote. Un estilo inalcanzable seria un
     boton que no hace nada.

   - **DES-stubear un control le devuelve su propio `DefaultStyleKey`, y con el, este defecto**
     (u-016, 2026-09-03; regresion propia, medida). u-003 quito el stub de `ChatStickerButton` para
     meter el panel de stickers de verdad. La clase real conserva `DefaultStyleKey =
     typeof(ChatStickerButton)`, cuyo unico estilo implicito estaba bajo `win:`: el boton volvio a
     medir cero **por un segundo motivo distinto** y se publico invisible en el drop `d80d87a`.
     **Regla: al quitar un stub, comprobar que el estilo de la clave que la clase real fija existe
     fuera de `win:`.** El stub, por llevar `DefaultStyleKey` propio o ninguno, tapaba el hueco.

   - **Copiar el estilo o redirigir a la clase base: se mide, no se prefiere** (u-016, 2026-09-03).
     Redirigir (`DefaultStyleKey = typeof(Base)`) es lo barato y fue **incorrecto en los tres sitios
     donde se planteo**: cada clase resuelve piezas de su PROPIA plantilla que ninguna base declara
     — `Icon` (`ChatStickerButton.cs:371`), `CheckedPart` (`GlassToggleButton.cs:37`), `Photo`/
     `Title` (`ChatPill.cs:37-38`). Las tres estan guardadas contra nulo, asi que redirigir **no
     habria lanzado**: habria dado un boton sin icono, un conmutador sin estado marcado y una
     pildora sin avatar. **Antes de redirigir a la base, `grep GetTemplateChild` en la clase.**

   - **`ApiInformation.IsApiContractPresent` contesta `TRUE` para CUALQUIER contrato en Uno**
     (u-012, 2026-09-02; re-medido en u-016). Medido reflejando la `Uno.Foundation.dll` construida
     de verdad (`Assembly.LoadFrom` + invocar el metodo), no supuesto. Quien lo paga:
     `AnimatedGlyphButton`/`AnimatedGlyphToggleButton` deciden con el su `IsRuntimeCompatible()`, y
     con `true` su `OnApplyTemplate` **sale antes de tiempo**, asi que `ContentPresenter1`/
     `ContentPresenter2` no se resuelven nunca y **no existe glifo ninguno** — no es «el icono sale
     mal», es que no hay icono. Arreglado con `#if LINUX return false;` en
     `AnimatedIconToggleButton` (u-012: `MuteUnmute`, `VoiceRecognition`) y en `ChatStickerButton`
     (u-016). El modismo de la casa ya existia con su motivo escrito: `Controls/Chats/
     ChatRecordButton.cs:115`, de una tanda anterior. **Sigue mintiendo y sin arreglar** en
     `Controls/AnimatedIconButton.cs`, que hoy **no esta en el subconjunto**: si algun dia entra,
     entra con este arreglo.
     **Y este defecto SE SUMA al anterior**: el estilo ausente y la comprobacion mentirosa son
     independientes. Arreglar solo el estilo de `ChatStickerButton` daba un boton **con** plantilla
     y **sin** icono, porque `OnSourceChanged:413` toma entonces la rama de LottieGen, cuyas fuentes
     `IconStubs.cs` deja inertes a proposito. Parece arreglado y no lo esta. Al tocar cualquier
     control `win:`-only, mirar tambien si sobreescribe `IsRuntimeCompatible`.

   - **Uno NO valida la ruta de propiedad de un `{TemplateBinding}` en tiempo de compilacion**
     (u-012, 2026-09-02), a diferencia de `{x:Bind}`, que si lo haria. Sonda: un nombre de propiedad
     inventado dentro de un `TemplateBinding` y **la compilacion siguio en verde**. Consecuencia
     practica: en XAML de plantilla, el nombre se comprueba contra el codigo fuente del control, no
     contra el compilador.

   - **Las dos sondas rojas que si prueban algo** (u-016, 2026-09-03), porque un build verde sobre
     XAML o sobre un `#if` no prueba casi nada:
     1. *Diccionario XAML*: meter un tipo inexistente en un `TargetType` — el diccionario generado
        entero muere con `CS0103` sobre `LinuxOverrides_<hash>_ResourceDictionary`. Eso prueba que
        el fichero se esta parseando de verdad.
     2. *Arreglo bajo `#if`*: meter un identificador inexistente **dentro** de la rama `#if LINUX` —
        tiene que fallar. Eso, y no un build verde, es lo que prueba que la rama esta viva; si
        `LINUX` no estuviese definido el verde seria identico.
     Y ninguna de las dos sustituye a arrancar: `DISPLAY=:0 dotnet run --no-build` llega al QR
     (`AuthorizationStateWaitOtherDeviceConfirmation`) **sin cuenta**, y el log dice si algun
     `StaticResource` no resuelve — que se lleva por delante el arbol de recursos entero al arrancar.
     Ojo al cerrar: `dotnet run` lanza un **hijo** (`bin/.../Unigram`); matar el lanzador lo deja
     vivo. Matar tambien al hijo y confirmar que suelta el bus `org.unigram.linux`.

   - **Un pincel resuelto UNA vez en codigo no sigue un cambio de tema en vivo** (reportado por
     Stanley/Kevin; sin medir en el arbol todavia). Se anota aqui para que no se pierda, con su
     estado honesto: **u-011 (temas y fondos) lo busco en su lote y NO ocurre alli** — su unico
     pincel en codigo es `SettingsThemesPage.ConvertAccent`, un conversor de `x:Bind` sobre el color
     del propio acento, que se reevalua. Un resultado negativo tambien es un dato.
     **Donde SI ocurre, localizado al folder esta consolidacion**: `review/PARIDAD.md` lo
     describe en la banda de mensaje anclado — sus colores se resuelven **una vez** con
     `Resources.TryGetValue`, que devuelve un valor, y no con un `{ThemeResource}`, que es una
     referencia de marcado y por eso si se re-resuelve. Esa banda es `ChatPinnedMessageLinux.cs`
     y es de otra tanda: no se toca aqui. **La regla general que deja: en codigo, `TryGetValue`
     sobre un diccionario de recursos congela el color; solo el marcado con `ThemeResource`
     sigue un cambio de tema en caliente.** Y ese cambio en caliente ya es alcanzable desde la
     interfaz, porque la pagina de temas entro con u-011.

   - **El generador XAML de Uno PIERDE la naturaleza ADJUNTA de `prefijo:Tipo.Propiedad` cuando el
     valor es una extension de marcado propia** (u-021, 2026-09-03). Medido, no deducido:
     `common:TextBlockHelper.Markdown="{CustomResource X}"` sale del generador como
     `textBlock.Markdown = ...` —un acceso a propiedad de INSTANCIA sobre el `TextBlock`— y muere
     con `CS1061: "TextBlock" no contiene una definicion para "Markdown"`, dentro de un fichero que
     nadie escribio. Con un **literal** (`common:TextBlockHelper.Markdown="hola"`) o con un
     **`{TemplateBinding}`** el MISMO atributo resuelve perfectamente.
     La sonda que lo aisla: cambiar UN sitio de `{CustomResource …}` a una cadena literal y volver
     a compilar — los errores bajaron de 5 a 4 y el que desaparecio fue exactamente ese. Sin esa
     sonda el sintoma («falta un using») lleva a buscar en el sitio equivocado.
     Por eso el unico uso de `TextBlockHelper.Markdown` que habia en el subconjunto estaba sobre
     `<win:TextBlock>` (`GiveawayContent.xaml`), o sea descartado en Skia: nadie habia pisado este
     camino todavia.
     **Sustituto**: `x:Name` en el elemento y la llamada desde el code-behind
     (`TextBlockHelper.SetMarkdown(Info1, Strings.RevenueSharingAdsInfo1Subtitle)`). No es un
     modismo nuevo — `AboutAdsPopup.xaml.cs` ya lo hacia para su `LongInfo`, una linea mas arriba.
     Va sin `#if`: en Windows el resultado es el mismo. Cinco sitios en `AboutAdsPopup.xaml` y
     `CocoonAboutPopup.xaml`.
     **Regla, hermana de la de `FluidGridView.Triggers`: una propiedad adjunta en este port se
     escribe con un valor literal o con `{TemplateBinding}`, nunca con `{CustomResource}` ni con
     otra extension de marcado propia.**

   - **Y un estilo `win:`-only puede LANZAR en vez de medir 0x0** (u-021, 2026-09-03). La entrada
     de u-016 describe la forma silenciosa; `ScrollViewerScrim` es la ruidosa: su `OnApplyTemplate`
     hace `TopScrim = GetTemplateChild(nameof(TopScrim)) as Rectangle;` y en la linea siguiente
     `TopScrim.Height = _topInset;` — la guarda de nulo esta cuatro lineas MAS ABAJO. Sin plantilla
     eso es una `NullReferenceException` en la pasada de plantilla, que se lleva el popup entero,
     no el degradado. Al mirar un control `win:`-only conviene leer su `OnApplyTemplate` antes de
     suponer que lo peor que pasa es que no se vea.
     Aviso para quien lo retome: darle el estilo **no basta**. Su segundo bloqueo es
     `ElementCompositionPreview.GetScrollViewerManipulationPropertySet`, NotImplemented en Uno
     Skia, que es la unica entrada de las dos `ExpressionAnimation` que mueven los degradados. Las
     dos cosas van en la misma pasada, y esa pasada necesita pantalla.

   - **`strings` NO sirve para saber si una clave de recurso de Uno existe** (u-032, 2026-09-03).
     El truco que si funciona —buscar el literal de un miembro en la `Uno.UI.dll` desplegada para
     confirmar que esta en la tabla de NotImplemented (u-003)— **no vale para los recursos**: las
     claves del tema Fluent de Uno no estan como literales en `Uno.UI.dll` ni en
     `Uno.UI.FluentTheme*.dll`. El control que lo demuestra, y sin el la medicion habria mentido:
     claves que SI resuelven (`DefaultTextBoxStyle`, `TextControlForeground`) salen igual de
     ausentes, y `grep -a` solo las encuentra dentro de `Unigram.dll`. O sea que el metodo esta
     ciego, no que las claves falten.
     Importa porque el fallo es de los caros: **una referencia de recurso que no resuelve se lleva
     el arbol de recursos entero al arrancar**, y un build verde no lo enseña. En un lote que no
     puede lanzar la app, eso es un cheque en blanco.
     **Regla que deja, y que vale mas alla de los recursos: cuando no puedas verificar una
     dependencia, elige el diseño que NO la tiene antes que la copia fiel que trae veintitantas sin
     verificar.** En u-032 eso fue tirar el `<Slider.Template>` inline de `ColorPicker` (200 lineas
     copiadas de WinUI, 23 claves `{ThemeResource Slider*}`) y dejar que el `Slider` caiga al estilo
     por defecto de Uno, que si esta probado en pantalla en este mismo popup. La plantilla que queda
     no referencia NINGUNA clave. Lo que se pierde es aspecto, no significado, y esta escrito en
     `LinuxOverrides.xaml` junto con lo que costaria recuperarlo.

### Compilar 0/0 NO es cargar: las tres cosas que el compilador no vigila (consolidacion #4)

Una vista nueva necesita **tres** cosas y el compilador solo mira la primera. Faltando cualquiera de
las otras dos, el arbol compila **0 errores / 0 advertencias** y la pantalla abre **en blanco**:

1. Estar en `Telegram.Linux.csproj` (eso si es un error de compilacion si falta).
2. Estar registrada en el array de `Telegram/Services/Session.Registrations.cs`, **en el bloque
   `#if LINUX`**. `Session.Resolve<T>()` devuelve **null** para lo que no este ahi. No lanza.
3. Tener su rama en el `switch` de `App.ViewModelForPage`, **si se abre por navegacion o por
   `ShowPopupAsync`**. Y esto importa el doble: `ShowPopupAsync` solo asigna `DataContext` **y llama
   a `OnNavigatedTo`** cuando `ViewModelForPage` devuelve algo distinto de null. Sin rama, el
   `OnNavigatedTo` del popup **no se ejecuta nunca**.

**Los dos caminos de abrir un popup piden cableado distinto.** `ShowPopupAsync` pasa por
`ViewModelForPage`; `ShowQueuedAsync` **no**, y un popup por esa via resuelve su propio modelo en el
constructor — necesita el registro (2) y **no debe** tener rama (3), que seria inalcanzable.
`StickersPopup` es del segundo tipo, `AddFolderPopup` y `ContactsPopup` del primero.

**Verificar con `tools/check-viewmodel-wiring.py`, NUNCA con `grep`.** Los mismos nombres ya
aparecen en el bloque de Windows del mismo fichero, asi que un grep contesta «si, esta registrado» y
te deja seguir. Esa es, casi con seguridad, la forma en que estos huecos se abren. El script lee el
estado real del preprocesador y cruza las ramas Linux contra los registros; esta validado en los dos
sentidos. En la consolidacion #4 encontro **cinco** vistas recien cableadas que abrian sin modelo
(cerrar sesion, perfil propio, cambiar usuario, codigo de bloqueo, proxy) — las cabeceras mismas de
la entrega, y ninguna compilacion por parcela podia verlo.

**El registro (2) tambien hace falta fuera de las vistas, y ahi la puerta NO mira.**
`check-viewmodel-wiring.py` cruza las ramas de `ViewModelForPage` contra los registros, asi que solo
ve modelos de pagina o de popup. Un modelo que se resuelve desde una **fabrica estatica** no aparece
en ese cruce: `EmojiDrawerViewModel.Create(session)` hace `session.Resolve<T>()` y acto seguido
`context.Dispatcher = ...` sin comprobar null, de modo que faltar en el bloque Linux no es un cajon
vacio sino un `NullReferenceException` en el primer uso. En la consolidacion #5 eso dejaba muerta la
barra selectora de reacciones, el boton de estado-emoji y las tres pestanas del panel del compositor
con la familia de los cuatro modelos de `Drawers` aun en el bloque de Windows, y la puerta seguia en verde. La puerta **ya se ha extendido** a cruzar
todo `Resolve<T>` de codigo Linux contra los registros, que es la forma correcta de cerrar la clase.
Si alguna vez hay que repetir el barrido a mano, el detalle que lo hace util es resolver el
preprocesador **en el fichero que llama**, no solo en `Session.Registrations.cs`: sin eso salen
decenas de falsos positivos de `App.xaml.cs` que estan dentro de `#if !LINUX`.

### El generador de XAML de Uno se para a medio fichero, sin decir nada

En `Views/Popups/StickersPopup.xaml` la generacion **termina en el `</GridView>`**: los dos ultimos
hijos del `Grid` raiz no llegan al codigo generado y para `x:Name="MoreButton"` **no se emite
campo**. El sintoma es un `CS1061` en el **codigo de respaldo**, que no apunta al XAML.

**Como se localiza**: compilar con `-p:EmitCompilerGeneratedFiles=true
-p:CompilerGeneratedFilesOutputPath=<dir>` y sacar el maximo de
`grep -oP 'Fichero\.xaml \(Line \K\d+'` sobre el fichero generado: **esa es la ultima linea del
XAML que el generador llego a ver**. Sirve para cualquier «esto no se dibuja y no se por que».

**Causa raiz SIN aislar.** No es el `{x:Bind}` de `FluidGridView.Triggers`, que es a lo que se
parece: sustituirlo por un literal no cambia nada (sonda hecha, disparo en negativo). Y no es el
control: `ScrollViewerScrim` se genera bien en otros doce ficheros. Queda escrito aqui y en el
propio fichero en vez de adivinado. **Actualizacion 2026-09-05**: aparecio una causa que produce
EXACTAMENTE este sintoma — el comentario XML ilegal (entrada siguiente) — y explica otros cuatro
ficheros, pero NO este: `StickersPopup.xaml` pasa `check-xaml-wellformed.py` en limpio (verificado
antes y despues de `3fe7ae6`). Al depurar un subarbol que no se genera, correr el guard PRIMERO;
si sale limpio, estas en este caso residual, que sigue sin aislar.

**Salida cuando toque**: construir el elemento desde el codigo de respaldo con un **campo del mismo
nombre y tipo** bajo `#if LINUX`, y asi las referencias existentes compilan sin tocarse en las dos
plataformas — contra el campo generado en Windows y contra el tuyo aqui.

### Un comentario XAML con `--` es XML ilegal, y Uno lo «acepta» descartando el resto del fichero (u-094c, 2026-09-05)

Un comentario que contiene `--` es ilegal en XML 1.0 (§2.5). El compilador de XAML de Uno lo
acepta con **0 errores / 0 advertencias** y DESCARTA EN SILENCIO lo que queda del fichero: el
subarbol simplemente no aparece en el `InitializeComponent` generado. El sintoma se presenta como
«Uno no soporta este idioma de marcado», apuntando exactamente al sitio equivocado — les costo la
tanda a u-094b y u-094c entre los dos.

**Guard**: `tools/check-xaml-wellformed.py` (tambien en `hive/bin/`) recorre todos los `.xaml` y
nombra cualquier fichero mal formado con linea y columna; corre en <1 s. En su primer paso cazo
**4** ficheros mal formados en `linux`, **3 con daño real ya embarcado**: el campo de descripcion
del canal en `NewChannelPopup`, el `ComboBox` de autoborrado en `NewGroupPopup` y los estilos de
los botones de reply-markup en `LinuxOverrides.xaml` (arreglados en `3fe7ae6`, con el guard
añadido al juego de puertas).

**Regla**: nunca diagnosticar «Uno no soporta este marcado» sin correr el guard antes. Y el
limite medido de la regla: la parada a medio fichero de `StickersPopup.xaml` (entrada anterior)
NO es de esta clase — ese fichero esta bien formado y su causa sigue sin aislar.

### No todo estilo `win:`-only quiere gemelo: mirar antes si su `TargetType` esta dentro

`DefaultChatTextBoxStyle` NO se escribe: su `TargetType` es `controls:FormattedTextBox`, que no esta
en el subconjunto, asi que el estilo **no compilaria**. Y no llega con la siguiente parcela —
`FormattedTextBox : RichEditBox` pertenece a la tanda del editor (FALTA 1.8), y el gemelo hay que
escribirlo contra el sustituto que construya ESA tanda. Las 162 lineas estan listas y comentadas en
`LinuxOverrides.xaml`, sin levantar. Complemento de la regla de u-016: un estilo para una clase que
nunca fija `DefaultStyleKey` es inalcanzable; este es el caso vecino, un estilo cuyo tipo no existe.

### Un worktree recien creado NO compila: tres ficheros que solo viven en el checkout compartido

Por orden de dolor, medido en la consolidacion #4 y antes en u-052:

1. **`Libraries/tdjson/td_api.tl`** — en `.gitignore`. Sin el, **5.290 errores** (falta
   `Telegram.Td.Api` entera) y el unico sintoma util es **un** `CS2001` enterrado entre ellos.
   **Leer el PRIMER error, no el mas repetido.**
2. **`Telegram/Constants.Secret.cs`** — en `.gitignore`, y **no da error**: el generador de csproj
   solo incluye lo que existe, asi que lo **borra de la lista en silencio**. Commitear ese csproj se
   lo lleva tambien del checkout compartido. Paso de verdad: la rama de u-053 llego a la
   consolidacion con esa linea quitada. **Regla: al regenerar el csproj en un worktree, mirar el
   diff y exigir CERO lineas `-`.**
3. **`Libraries/libprisma`** — es un **submodulo sin inicializar**, no un fichero ignorado, asi que
   un barrido de `git ls-files --others --ignored` **no lo ve**. `git submodule update --init
   Libraries/libprisma`.

Y la leccion transversal: un barrido que «demuestra» que no falta nada vale lo que valga su filtro.
El primero que hice filtraba por extension `.cs|.tl|.xaml|.json` y por eso no vio el `.dat` de
libprisma.

### `x:Load="False"` deja un ElementStub: `GetTemplateChild` NO lo ve, `FindName` SI (u-078)

**Un solo `NullReferenceException` dejaba la aplicacion entera sin dialogos, y no salia en el log.**
Stanley lo vio como dos sintomas que parecian independientes: los popups de Contactos y Denunciar
**no dibujaban nada**, y **el primer popup de la sesion clavaba todos los dialogos posteriores**. Son
el mismo fallo.

**La causa.** En `Themes/Generic.xaml` la plantilla de `ContentPopup` declara
`<local:GlyphButton x:Name="DismissButton" x:Load="False" .../>`. Con `x:Load="False"` ese elemento
**no es un boton hasta que alguien lo realiza: es un `ElementStub`**. Y `GetTemplateChild` **se salta
los ElementStub**, mientras que **`FindName` es lo que los materializa** (ya estaba escrito en las
notas de u-015 a proposito de `SlideTextBlock`). `ContentPopup.OnApplyTemplate` hacia:

    DismissButton = GetTemplateChild(nameof(DismissButton)) as Button;
    DismissButton.RequestedTheme = ...;   // <- null

**Por que un NRE se convierte en la app sin dialogos.** Ese `OnApplyTemplate` corre dentro de
`EnsureTemplate`, que corre dentro de `ContentDialog.ShowAsync`. O sea que la excepcion **sale por
`ShowAsync`**. Y `ShowQueuedAsync` lo lanzaba con **`_ = ShowAsync();`** — fuego y olvido, **nadie
observa la tarea**, asi que la excepcion desaparece sin una linea de log. Como el dialogo entonces no
llega a abrirse, **tampoco se dispara su `Closed`**, que es la unica puerta que completa
`_closingTask` (el arreglo de fase 3 estaba intacto: **no puede salvar a un dialogo que nunca se
abrio**). Resultado: el `await` no vuelve, `_currentDialogShowRequest` se queda puesto **para toda la
sesion**, y cualquier dialogo posterior se queda esperando en el `while` de la cola. El segundo clic
**ni siquiera logea**, porque el `Logger.Info` esta despues del `await`.

**El arreglo son dos capas, y hacen falta las dos.**
1. Materializar el stub — `GetTemplateChild(...) ?? FindName(...)` — **y ademas comprobar null**:
   quedarse sin boton de cerrar es un degradado, llevarse el popup entero por delante no lo es.
2. **Observar la tarea de `ShowAsync`** y, si falla, **liberar la cola** con
   `_closingTask.TrySetResult(ContentDialogResult.None)`. Sin esto, la proxima vez que un popup no
   consiga abrirse **por cualquier motivo** se vuelve a quedar la sesion sin dialogos, otra vez en
   silencio. La capa 1 arregla ESTE fallo; la capa 2 arregla la CLASE de fallo.

**Regla que deja:** en este port, `_ = TareaAsincrona();` sobre algo que toca plantillas o arbol
visual es una bomba de relojeria — el `#if LINUX` no hace falta, lo que hace falta es **observar la
tarea**. Y ante «no dibuja», mirar si la parte de plantilla lleva `x:Load`.

**Preexistente, no regresion:** `Telegram/Controls/ContentPopup.cs` es **byte a byte identico** entre
`dda287a` y el tip de la consolidacion #5 (comprobado con `git show`, sin tocar el arbol). Solo se
hizo visible cuando Contactos y Denunciar pasaron a ser alcanzables. **Candidato a backport a 26.10**
si esa rama lleva el mismo `ContentPopup`.

**El caso Border del mismo stub (medido por Oscar en el panel de stickers, 2026-09-05).**
`x:Load="False"` debajo de un **`Border`** (no de un Panel): `FindName` materializa el control y lo
mete en el arbol visual, pero **`Border.Child` sigue apuntando al `ElementStub`**, asi que el Border
mide el cero del stub y el control real **NUNCA SE MIDE** — esta presente, sin `Collapsed`, y sale
0x0 con todo su subarbol a 0x0, incluidas filas `Auto` que de otro modo mediran. El arreglo:
**re-asignar `Border.Child` despues del `FindName`** — re-asienta el hijo e invalida la medida en un
solo gesto. Se encontro en los tres cajones del panel de stickers (`StickerPanel.xaml:63-99`), donde
cada cajon se dibujaba como un rectangulo vacio; verificado en pantalla (0x0 → 320x694, capturas
antes/despues en `agents/oscar-mthl9k2x/proof/`). La nota de arriba (~:355) cubre el hueco en
`Panel.Children`; este es el caso `Border.Child`.

### Resolver un pincel por clave en C# NO es `{ThemeResource}`, y re-resolverlo tampoco basta (u-019)

`ChatPinnedMessageLinux.cs` construia su arbol en codigo y sacaba los colores con un
`Application.Current.Resources.TryGetValue(clave)` **una sola vez, en el constructor**. Compilaba,
se veia bien al arrancar, y **al cambiar de tema en caliente la banda se quedaba con los colores del
tema anterior**: texto `#FF000000` sobre banda oscura. Medido con la app en marcha, comparando el
build parcheado con el de `dda287a` sobre el mismo `#PinnedLine`.

Hay **tres** cosas que saber, y la segunda es la que hace que el arreglo obvio no arregle nada:

1. **Los pinceles se SUSTITUYEN, no se recolorean.** `Theme.Update` llena
   `ThemeDictionaries["Light"]` y `["Dark"]` (`Theme.cs:504`) y `ThemeIncoming` asigna
   `["Light"]`/`["Default"]` **dos `SolidColorBrush` distintos por clave** (`Theme.cs:888`). Una
   referencia tomada una vez apunta para siempre al tema con el que se tomo.
   **Pero el eje accent / tema-personalizado NO esta roto**: `AddOrUpdate` con `create=false`
   **muta el pincel que ya esta en el diccionario** (`Theme.cs:585`), asi que esos cambios si los
   sigue una referencia vieja. **Solo el eje claro/oscuro falla** — por eso el fallo quedo latente
   hasta que u-011 hizo el cambio alcanzable desde la UI.

2. **Re-ejecutar `Application.Current.Resources.TryGetValue` en `ActualThemeChanged` NO ARREGLA
   NADA.** El cambio de tema es `AppearanceSettings.UpdateNightMode` -> `window.RequestedTheme`, y
   `WindowContext` lo pone en el **elemento raiz del contenido** (`WindowContext.cs:706`), **nunca**
   en `Application.Current.RequestedTheme`, que `App.xaml.cs:103` fija al arrancar y no vuelve a
   tocar. El markup `{ThemeResource}` si sigue el cambio porque el framework lo resuelve contra el
   `ActualTheme` de **cada elemento**; un `TryGetValue` pelado sobre el diccionario **no esta
   acotado al elemento** y responde siempre por el tema de ARRANQUE.
   Comprobado en pantalla: en el build sin arreglar, tras cambiar a claro y **reabrir el chat**
   (banda recien construida, no una referencia vieja) la linea seguia en `#FF1F1F1F`. O sea el fallo
   no es "referencia rancia", es **"clavado al tema de arranque para toda instancia futura"**.

3. **La solucion es pasar el tema explicito y recorrer los diccionarios a mano**: `ThemeDictionaries`
   del propio diccionario primero, luego `MergedDictionaries` **de la ultima a la primera** (es el
   orden en que XAML resuelve: `ThemeIncoming` va despues de `Theme` en `App.xaml:521` y su
   `MessageHeaderBorderBrush` tiene que ganar). Y hay que probar **las dos grafias** del lado
   oscuro: `"Dark"` (lo que escribe `Theme`) y `"Default"` (lo que escriben `ThemeIncoming` y
   `App.xaml`, por convenio Fluent).

**Y dos enganches, no uno**: `ActualThemeChanged` **mas** `Loaded`. En el subconjunto actual la unica
ruta viva al interruptor es Ajustes -> Apariencia -> "Cambiar al modo nocturno", y abrir Ajustes
**descarga ChatView**, asi que la banda ni existe cuando se pulsa: quien la repinta al volver es
`Loaded`. `ActualThemeChanged` cubre el caso en que la banda si esta viva durante el cambio (modo
nocturno automatico por horario, `PowerSavingPolicy`). Con un guardia `_appliedTheme` para no
repintar dos veces.

**Donde viven esas claves, por si alguien las busca y no las encuentra:**
`MessageForegroundBrush`, `MessageSubtleForegroundBrush`, `MessageHeaderForegroundBrush` y
`MessageHeaderBorderBrush` **no tienen `x:Key` en ningun `.xaml` ni estan en
`ThemeService.Defaults.cs`**: se declaran en codigo, en los diccionarios estaticos de
`ThemeOutgoing`/`ThemeIncoming` (`Theme.cs:695/815`). Se resuelven a nivel de aplicacion **solo**
porque `App.xaml:521` mergea `<common:ThemeIncoming />`.

**Regla que deja:** cualquier control del port que pinte con `Resources.TryGetValue` en vez de
`{ThemeResource}` tiene este fallo. Buscarlos con `grep -rn "Resources.TryGetValue" Telegram.Linux/`
antes de dar por bueno un tema.


### Un `Contains(StandardDataFormats.X)` en false NO significa que el dato no este (u-050)

`Windows.ApplicationModel.DataTransfer` tiene dos capas y en este backend **solo la de abajo esta
llena**. `X11ClipboardExtension.FillDataPackage` (Uno 6.6.184, leido del IL) hace, por CADA atomo
que anuncia el portapapeles o el arrastre:

```csharp
dataPackage.SetDataProvider(nombreDelAtomo, ct => Task.Run(() => WaitForBytes(...), ct));
```

y solo despues, como casos especiales, llama a `SetText` (si hay un atomo de su tabla `TextFormats`)
y a `SetStorageItems` (si hay `text/uri-list`). **`SetBitmap`, `SetHtmlFormat`, `SetRtf`,
`SetWebLink` y `SetApplicationLink` no se llaman nunca.** Como `DataPackageView.AvailableFormats`
es literalmente `_data.Keys`, el resultado es:

- `package.Contains(StandardDataFormats.Bitmap)` -> **siempre false**, aunque haya un PNG.
- `package.AvailableFormats` -> contiene `"image/png"`, `"text/html"`, `"image/tiff"`... los
  nombres MIME/atomo en crudo.
- `await package.GetDataAsync("image/png")` -> **los bytes del fichero**, `byte[]`, servidos por el
  proveedor.

**Regla:** antes de declarar que un formato «no existe en este backend», mirar `AvailableFormats` en
crudo y pedirlo por su nombre de atomo. Lo que falta es la traduccion al formato tipado de WinRT, no
el dato. Asi entro «pegar una imagen» (`Hubs/DialogViewModel.Paste.Linux.cs`), que esta misma
seccion daba por imposible hasta u-050.

**Y el `Paste` del TextBox no cubre el hueco**: `TextBox.RaisePaste` se llama desde
`PasteFromClipboard(string)`, al que solo se llega desde `PasteFromClipboard()`, que arranca con
`if (content.AvailableFormats.Contains(StandardDataFormats.Text))`. Con una imagen sola en el
portapapeles **el evento no se dispara jamas**: hay que interceptar Ctrl+V en `OnKeyDown`. Y ojo,
interceptarlo no lo CANCELA -- el pegado de texto de Uno vive en `OnKeyDownSkia`, al que se llega
por `OnPostKeyDown`, despues del manejador de clase y sin mirar `args.Handled`; para la V no hay
ninguna propiedad-valvula equivalente al `AcceptsReturn` del Enter.

### Un `CS0246` sobre un tipo que SÍ está compilado: el `using` estaba dentro de un `#if !LINUX` (p3, 2026-09-05)

Al traer una carpeta nueva al subconjunto (p3: `Views/Users/`), el primer build dio cuatro
`CS0246` sobre `UserEditPage` — un tipo que en ese momento **SÍ compilaba**. La causa no era el
csproj (la variante 2b de la regla 2) sino que `using Telegram.Views.Users;` estaba dentro de un
`#if !LINUX` en `DialogViewModel.cs`, `ProfileViewModel.cs` **y** `App.xaml.cs`: en Linux el tipo
existía y era innombrable. `ProfileViewModel.cs` ya tenía un comentario diciendo exactamente esto
(«es el USING el que falla (CS0246), no la llamada») sobre otras cinco carpetas, y la nota no
viajó al siguiente lector. **Regla**: cuando una parcela mete una carpeta al subconjunto, ANTES de
compilar, `grep` los bloques de `using` cercados por ese espacio de nombres:
`grep -rn -B3 "using Telegram\.<Carpeta>" Telegram/ | grep -B1 '#if'` — cada golpe es un `using`
que hay que sacar del cerco (o duplicar fuera), no un fichero que falte.

### Composición: «StartAnimation hace snapshot» es FALSO en Uno, y `Translation` no existe hasta que alguien la escribe (SlidePanel, 2026-09-05)

Dos reglas nuevas, ambas descompiladas de `Uno.UI.Composition` 6.6.184 (no deducidas), y el
motivo por el que sus violaciones son ABORTOS y no líneas de log. Emparientan con las notas ya
escritas arriba (la `ArgumentException` de la misma clave, ~línea 250; los identificadores de
`ExpressionAnimation`, ~429) pero son más generales:

1. **Nunca reutilizar una instancia de `CompositionAnimation` entre visuales** — con un matiz por
   TIPO que decide CUÁL de los dos fallos te toca (medido, documentado en
   `Controls/ProfileHeader.xaml.cs:503`): `Compositor.RegisterAnimation` solo mete la animación en
   su diccionario —el claveado POR LA INSTANCIA que lanza `ArgumentException` con clave repetida—
   cuando `animation.IsTrackedByCompositor`, que es **`true` para las `KeyFrameAnimation` y `false`
   para las `ExpressionAnimation`**. De ahí:
   - **`KeyFrameAnimation` (Scalar/Vector3/…) compartida entre ≥2 visuales distintos a la vez** =
     `ArgumentException: An item with the same key has already been added` → muerte del dispatcher
     (la clase de RootPage, ~línea 250 arriba). Éste es el fallo de las 6 parcelas de
     `u-anim-shared` (ButtonEx, MasterDetailView `fadeOut`, ChecklistTask, Sponsored/WebPage
     `endless`, EmojiSearchBox, FileButton).
   - **`ExpressionAnimation` compartida entre visuales** NO da clave repetida (nunca entra al
     diccionario). Su ÚNICO riesgo es la re-evaluación: WinUI garantiza *snapshot* de la expresión
     y sus parámetros; en Uno `ReferenceParameters` es un `Dictionary` VIVO leído en cada
     evaluación y cada objetivo se suscribe al MISMO `AnimationFrame`. Por eso una Expression
     compartida solo revienta si (a) se **re-apunta la referencia por objetivo** (gana la última),
     (b) la expresión **se refiere a su propio objetivo** (recurre hasta `SIGABRT`), o (c) se
     llama a **`Stop()` mientras sigue compartida** (deshace la expresión parseada para todos).
     Una Expression compartida con una referencia FIJA y sin `Stop` es legítimamente segura —
     `ProfileHeader.titleTranslation` la comparte a propósito y lo deja escrito; `MasterDetailView.slideOut3`
     (`-clamp(-scrollViewer.Translation.Y…)`, ref fija, sin self-ref, sin Stop) es el mismo caso.
   Regla operativa: una instancia por objetivo SIEMPRE es segura y es el arreglo; pero al
   clasificar un aviso del gate, mira el TIPO — una `KeyFrameAnimation` compartida es un fallo casi
   seguro, una `ExpressionAnimation` compartida hay que leerla (ref, self-ref, Stop). Donde un
   comentario de upstream diga «snapshot», leerlo como peligro de porteo.

2. **`Translation` no es propiedad del `Visual` en Uno: sembrarla antes de leerla.**
   `Visual.GetAnimatableProperty` no tiene caso para ella (cae a la bolsa de propiedades) y
   `ElementCompositionPreview.SetIsTranslationEnabled` solo cambia un bool interno — NO crea la
   entrada. Leer `x.Translation` en una expresión antes de que alguien la haya ESCRITO lanza
   `Exception("Unable to get property 'Translation'.")`. El ternario de Uno sí cortocircuita,
   pero no salva: la condición es cierta exactamente cuando ocurre la lectura. Regla: si falta,
   sembrar `Properties.InsertVector3("Translation", Vector3.Zero)` antes de que ninguna
   expresión la lea; el test de existencia que no lanza es
   `Properties.TryGetVector3(name, out _) != CompositionGetValueStatus.Succeeded`.

**Por qué esto mata en vez de loguear**: `CompositionObject.StartAnimation` envuelve su PRIMER
`SetAnimatableProperty` en try/catch con log, pero `ReEvaluateAnimation` no envuelve nada — lo
que lance en una re-evaluación escapa a quien levantó el frame: sin manejar, `SIGABRT`, salida
134. Una expresión de composición mal escrita parece inocua en la primera evaluación y mata la
app en una posterior. (Caso real: la banda de traducir — `SlidePanel` + `ChatTranslateBar` —
abortaba 3 de 4 arranques al navegar a un chat en idioma extranjero; era también la raíz del
`Stack overflow.` intermitente en `ExpressionAnimation.Evaluate`/`Visual.set_Offset` que rondaba
el equipo desde el 2026-09-04. Arreglado en `fix/slidepanel-translation @ de9f3ef`.)

## Trabajo en paralelo

Varios agentes trabajan a la vez sobre este árbol, cada uno con su área. Si aparecen ficheros
recientes que no reconoces, son de un compañero de tu misma tanda: sigue con lo tuyo y, si el
cambio que necesitas cae en su área, descríbelo en tu informe en vez de hacerlo.

**No escribas a otras sesiones de Claude** (`SendMessage`, `ListAgents`): las que veas son del
usuario y no tienen relación con este port. Para coordinarte basta con este fichero, `HANDOFF.md`
y tu informe final.

## Cómo compilar / ejecutar

```bash
cd Telegram.Linux && dotnet build -nologo -v q
DISPLAY=:0 dotnet run --no-build
```

**No lleva `-p:WarningLevel=0`.** Esa bandera es `/warn:0` de csc: apagaba TODAS las advertencias,
no solo las esperables, y por eso todo log verde de `fase1/` decía "0 Advertencia(s)" sin que
nadie lo hubiera medido (u-017, u-037). Retirada tras cerrar las 5 de nulabilidad del borde TDLib
y suprimir puntualmente las 6 `RS2008` de `Telegram.Generators` (su propio
`<NoWarn>RS2008</NoWarn>`, no algo global): un `dotnet build` liso, hoy, es **0 errores y 0
advertencias reales**, medido, no silenciado.

**Si has cambiado la LISTA DE FICHEROS del csproj, compila con `--no-incremental`.** Los
generadores de Uno (`BindableTypeProvidersSourceGenerator` y `XamlCodeGenerator`) reaprovechan su
salida entre compilaciones incrementales y, cuando el conjunto de `<Compile>`/`<Page>` cambia,
**inventan errores sobre tipos que están perfectamente**. Medido el 2026-08-26 al meter el árbol de
Ajustes: 23 errores, entre ellos
`CS0400: 'PasswordState' no se encontró en el espacio de nombres global` (el tipo está en
`Telegram.Td.Api`, generado por el esquema, y `grep -c 'class PasswordState'` lo encuentra) y
`CS1061: TextBlock no contiene una definición para Markdown` desde el `.g.cs` de un popup. Con
`dotnet build --no-incremental`: **0 errores**, sin tocar una sola línea de fuente, y a partir de
ahí las incrementales vuelven a ir bien. Perseguir esos mensajes como si fueran reales cuesta la
tarde: el primer reflejo ante un error que nombra un tipo del esquema o una propiedad adjunta y
que sale de `obj/.../Uno.UI.SourceGenerators/` tiene que ser recompilar entero.
Del mismo lado: el `.g.cs` de `obj/gen/` **no se reescribe en una compilación incremental**, así
que mirar su fecha antes de creerse su contenido.

**Comprobar si la app ya corre: pregúntale al BUS, no a `pgrep`.** Medido el 2026-08-26 con tres
tandas a la vez: `pgrep -af 'net10.0-desktop/Unigram|dotnet Unigram\.dll'` contestó *nada* mientras
había una instancia perfectamente viva, porque estaba lanzada desde una copia
(`/tmp/unigram-ajustes/Unigram`) y ningún patrón basado en la ruta de compilación la nombra. Lo que
sí la nombra es el nombre de bus que `SingleInstance` toma:

```bash
busctl --user call org.freedesktop.DBus /org/freedesktop/DBus \
    org.freedesktop.DBus GetConnectionUnixProcessID s org.unigram.linux
```

Si contesta un pid, la app está corriendo y **es de alguien**: arrancar otra sólo consigue
`org.unigram.linux is already owned: forwarding activate and exiting` y un log de doce líneas que
parece un fallo del arranque y no lo es. `ps -o lstart,args -p <pid>` dice de quién.

Antes de cada lanzamiento, matar la instancia anterior con un patrón **anclado** (nunca uno que
aparezca en la propia línea de comando del que lo lanza), y no compilar con la app corriendo:

```bash
pkill -f "^$HOME/.dotnet/dotnet Unigram"
```

### Lo que la app escribe fuera de su carpeta (desde la fase 4)

Al arrancar, `DesktopEntry.Ensure()` deja cinco cosas en el `$HOME` del usuario. Son idempotentes
—si el contenido ya coincide no se toca nada y no se lanza ningún proceso— y todas se apagan con
`UNIGRAM_NO_DESKTOP_ENTRY=1`:

| Fichero | Para qué |
|---|---|
| `~/.local/share/applications/org.unigram.linux.desktop` | identidad de la app: icono, `WM_CLASS`, `tg://`, la acción «Mensajes guardados», `DBusActivatable` |
| `~/.local/share/icons/hicolor/<n>x<n>/apps/org.unigram.linux.png` | el icono al que apunta la línea de arriba |
| `~/.local/share/dbus-1/services/org.unigram.linux.service` | que el **bus** sepa arrancar la app para un `tg://` pulsado con Unigram cerrado |
| `~/.local/share/mime/packages/org.unigram.linux.xml` | declara `application/x-unigram-theme` (`*.unigram-theme`) |
| `~/.config/mimeapps.list` | asocia los tres tipos a la app — **solo las claves que faltan**: una asociación que ya era de otro programa no se le quita |

Y `~/.config/autostart/org.unigram.linux.desktop` cuando el interruptor de autoarranque está puesto.
Para deshacerlo todo: borrar esos ficheros y volver a lanzar `update-desktop-database
~/.local/share/applications` y `update-mime-database ~/.local/share/mime`.

## Diagnósticos (`Telegram.Linux/Platform/`, todos por variable de entorno)

| Variable | Qué hace |
|---|---|
| `UNIGRAM_SCREENSHOT=<s>:<png>[:exit]` | Captura la ventana en ese segundo (varios instantes separados por comas). Pasa por `RenderTargetBitmap`: bajo XWayland capturar la raíz X da negro. **Desde el 2026-08-24 dibuja también los popups abiertos**: la galería, los flyouts y todo `ContentPopup` viven en el *popup root*, que es **hermano** de `Window.Content`, no hijo, así que hasta entonces la captura salía sin lo único que había encima. Lo mismo vale para `UNIGRAM_DUMP_TREE`, que ahora añade un bloque `--- open popup at x,y ---` por cada uno. |
| `UNIGRAM_DUMP_TREE=<s>` | Vuelca el árbol visual al log (tipo, nombre, caja, visibilidad, fondo). |
| `UNIGRAM_TYPE_TEST=[<s>:]<secuencia>` | Escribe esa secuencia **tecla a tecla** en el campo de texto de la página, y registra `Text` y `SelectionStart` tras cada pulsación; después borra tres dígitos, los reescribe, mete uno en medio y lo quita. `UNIGRAM_TYPE_TEST_CLICK=<botón>` pulsa antes un botón (por `x:Name` o por el texto que muestra, exacto o subcadena — el login abre en el QR, así que hace falta `UNIGRAM_TYPE_TEST_CLICK="número de teléfono"`). |
| `UNIGRAM_SCROLL_TEST=<s>:<px>[,<s>:<px>]` | Desplaza la lista scrollable más alta de la ventana esos píxeles en ese instante (negativo = hacia arriba). Existe porque **la rueda del ratón no se puede inyectar desde fuera bajo XWayland**: mutter mantiene su *guard window* sobre toda la pantalla X, así que los eventos XTEST de `xdotool` se reportan sobre la ventana raíz y no llegan nunca a la superficie de Uno (`xdotool getmouselocation` contesta `WINDOW=0x408` se ponga el puntero donde se ponga). Mueve el `ScrollViewer` directamente: ejercita virtualización, reciclado de contenedores y el re-enlace de una celda reciclada, pero **no** la tubería de la rueda. |
| `UNIGRAM_CLICK=<s>:<objetivo>[;…]` | **Clic real de ratón** en ese instante. El objetivo es `#Nombre` (centro del elemento con ese `x:Name`), `Lista[3]` (centro del contenedor 3 de esa `ItemsControl`), `Lista{texto}` (el contenedor de esa lista que muestre ese texto — lo que hace falta para abrir un chat concreto, porque la lista se reordena sola y un índice no nombra dos veces al mismo) o `120,340` (un punto, en píxeles de maquetación), o **`@Tipo`** / **`@Tipo#Hijo`** (el primer control de ese tipo CLR que esté dentro de la ventana, y dentro de él el hijo con ese `x:Name`: los nombres de una burbuja viven dentro de su propia plantilla — el botón de una nota de voz se llama `Button` y el de una foto también—, así que un nombre a secas nombra la primera burbuja de cualquier clase, y el tipo no). **El objetivo admite dos prefijos**: `right:` para el botón derecho y **`hold<ms>:`** para press-and-hold (`hold9000:684,410`), que es la única forma de medir un gesto de mantener pulsado — un toque y un mantener son gestos distintos y el visor de historias hace cosas opuestas con ellos. |
| `UNIGRAM_WHEEL=<s>:<muescas>[:<objetivo>][;…]` | Rueda real (positivo = hacia abajo) sobre ese objetivo, o sobre la lista scrollable más alta si no se da ninguno. **Con reservas**: ver el aviso de más abajo. |
| `UNIGRAM_HISTORY_PROBE=<s>` | Instrumenta el historial de mensajes y registra las medidas del scoping: desplazamiento del ancla en cada lote de cambios de la colección, distancia al fondo, offset/extent/rango realizado/contenedores creados y reciclados, y una línea por `ViewChanged` no intermedio. Se re-engancha solo cuando se abre otro chat. |
| `UNIGRAM_HISTORY_SCROLL=<s>:<px>[,…]` | Mueve **el historial** esos píxeles (negativo = hacia arriba, hacia los mensajes viejos). `UNIGRAM_SCROLL_TEST` no vale aquí: elige la scrollable más alta, que es la lista de chats. **Necesita `UNIGRAM_HISTORY_PROBE` puesto**: `ScheduleScroll` y `ScheduleGoto` se llaman desde `HistoryProbe.Schedule`, que se sale antes si `UNIGRAM_HISTORY_PROBE` no está, y entonces estas dos variables no hacen nada **ni dejan una línea en el log**. Lo mismo vale para `UNIGRAM_HISTORY_GOTO`. |
| `UNIGRAM_HISTORY_GOTO=<s>:<índice>[,…]` | Salta al mensaje de ese índice de la ventana cargada (negativo cuenta desde el final) por el camino real de una respuesta o un resultado de búsqueda (`ChatHistoryView.ScrollToItem`) y registra a cuántos píxeles del borde superior queda. |
| `UNIGRAM_HISTORY_POKE=<n>` | **Experimento, no arreglo**: cuando la sonda encuentra el historial colapsado (hay mensajes y el panel mide 0 px o no ha materializado ni un contenedor) le pide al `ItemsStackPanel` una medida mas, hasta `n` veces, y registra si volvio. Es lo que separo «el panel ya no se mide» de «se mide y sigue sin rellenar»: con `n=3` el historial volvio en 200 ms las dos veces. Necesita `UNIGRAM_HISTORY_PROBE` puesto. |
| `UNIGRAM_UNO_LOG_ONLY=<subcadena>[,...]` | Estrecha `UNIGRAM_UNO_LOG` a las categorias cuyo nombre contenga una de esas subcadenas; el resto se queda en Warning. Sin esto, `UNIGRAM_UNO_LOG=Debug` son **109 000 lineas en 45 s** (67 000 de `DependencyObjectStore`) y lo que se busca no se encuentra. **La categoria del historial es `VirtualizingPanelLayout`, no `ItemsStackPanelLayout`**: `this.Log()` usa el tipo que declara el metodo, no el instanciado. `UNIGRAM_UNO_LOG=Debug UNIGRAM_UNO_LOG_ONLY=VirtualizingPanelLayout` es la unica forma de ver DENTRO de una pasada de medida sin parchear Uno: imprime `availableSize`, `ExtendedViewportStart/End`, `GetItemsStart()/GetItemsEnd()`, la semilla de `ScrapLayout`, **un `AddView` por fila materializada** y el tamano que devuelve `EstimatePanelSize`. |
| `UNIGRAM_SHOT_REQUESTS=<carpeta>` | **Capturas y volcados a demanda.** Cada 250 ms mira esa carpeta: un fichero `<nombre>.shot` se convierte en `<nombre>.png` a su lado y se borra (que desaparezca la petición es la señal de que el png está entero), y un `<nombre>.tree` vuelca el árbol al log. Existe porque un gesto se inyecta desde fuera y lo que hay que capturar es el instante **posterior** a ese gesto, que nadie sabe de antemano: la app tarda entre 10 y 25 s en llegar al punto donde el gesto tiene sentido y un horario fijo lo falla. Los ayudantes `shot.sh`/`tree.sh` del scratchpad son tres líneas de bash. **Desde la fase 5 la misma carpeta acepta dos peticiones más**: `<nombre>.click`, cuyo contenido es un objetivo de `UNIGRAM_CLICK` y que mete un clic real donde diga; y `<nombre>.hgoto`, cuyo contenido es un id de mensaje de TDLib y que **lo trae a la vista** esté materializado o no (ver `HistoryProbe.GotoMessageAsync`). Las dos borran su fichero al terminar, que es la señal de que ya está hecho. **Desde la fase 2 hay una tercera, `<nombre>.type`**, cuyo contenido se escribe tecla a tecla sobre lo que tenga el foco (`{Return}`, `{BackSpace}`, `{Escape}` son teclas con nombre), y el objetivo de un `.click` admite el prefijo **`right:`** para el botón derecho — sin él no hay forma de abrir un menú contextual desde fuera. |
| `UNIGRAM_GESTURE_PROBE=1` | Los dos gestos reescritos de la galería cuentan lo que hacen: el `CarouselViewer` registra el progreso de cada delta y, al soltar, los tres números de los que sale su decisión (recorrido, velocidad, progreso proyectado) junto a la decisión; el `ZoomViewer` registra factor, traslación y **el punto de contenido anclado antes y después de cada paso**, con su deriva. `GalleryWindow.PrepareNext` registra además `índice / total / prev / next`. |
| `UNIGRAM_NOTIFY_TEST=<s>[:<texto del chat>[:<texto de otro chat>]]` | **Notificación de escritorio sin esperar a que escriban.** Publica en el `IEventAggregator` real un `UpdateNotificationGroup` inventado para un chat real de la cuenta (el mensaje es de mentira; los chats no), de modo que de ahí en adelante todo es el código de producción: `NotificationsService.Handle`, el descarte si ese chat está en pantalla, el título y la vista previa de `ChatCell`, el avatar y el `Notify` de D-Bus con su `replaces_id`. Hace cuatro cosas y las registra: un aviso para el chat A, un segundo mensaje del mismo chat (**mismo id de servidor** = se actualiza en vez de apilarse), uno de un chat B (**otro id**) y la acción por defecto del primero **reproducida** (`DesktopNotifications.ReplayAction`), que abre el chat. Lo último que hace es publicar otro mensaje del chat A con el chat ya abierto, que **no** debe salir (`Chat is open` en el log). La acción se reproduce porque la regla de coincidencia de `ActionInvoked` nombra al servidor como emisor: una señal falsificada desde otra conexión la descarta el bus, y la otra forma de recorrer ese camino es un dedo sobre el banner. |
| `UNIGRAM_LIVE_TEST=<s>[:<paso>]` | **Las señales vivas, sin necesitar a otra persona.** «En línea», «última vez» y «escribiendo…» son cosa de otra cuenta, así que la propiedad que importa —¿el control repinta cuando llega el update, o sólo al abrir la pantalla?— era la que no se podía medir solo. Entrega a `ClientService.OnResult` —el mismo método que llama el hilo receptor de TDLib— un `updateUserStatus` y cuatro `updateChatAction` (escribiendo, grabando voz, cancelar) sobre el primer chat privado de la lista principal, y **de ahí en adelante todo es código de producción**: las cachés, el `Publish` sin filtro, los `Subscribe<T>` y el control. Secuencia: online → escribiendo → grabando voz → cancelar → offline, un paso cada `<paso>` segundos (8 por defecto), con una línea de log por paso para poder capturar justo detrás. **No envía nada**: la única función de TDLib que llama es el `GetChats` de solo lectura con el que encuentra el chat, y registra ids y tipos, nunca títulos ni texto. |
| `UNIGRAM_MEDIA_TEST=<s>:<clase>[:<chats>[:<segundos>[:<saltar>[:play]]]]` | **Abre el historial en un mensaje de esa clase** (`voice`, `audio`, `video`, `videonote`, `photo`, `animation`) sin que nadie tenga que pasearse por la cuenta a mano. Busca con `SearchChatMessages` y el filtro de TDLib sobre los primeros `<chats>` de la lista principal, prefiere lo que ya está descargado (bajar 50 MB de la cuenta del usuario para probar que un reproductor reproduce no es ni rápido ni educado), y navega por el camino de producción del «saltar a un mensaje» — con su **tema** si el chat es un foro, sin el cual el mensaje no está siquiera en la ventana cargada. `<segundos>` es una duración mínima, y **negativa es una máxima**. `<saltar>` se salta ese número de chats con coincidencia. `play` además lo reproduce por la misma llamada que hace el botón (`IPlaybackService.Play`), que es la única forma de llegar a lo que el historial no consigue enseñar. Registra **ids, tipos y tamaños**, nunca títulos ni textos. |
| `UNIGRAM_MEDIA_PROBE=1` | Una línea por cambio de estado del `PlaybackService` y una por segundo mientras suena algo (posición, duración, velocidad, volumen, tipo del ítem y tamaño de la lista), y en `LinuxVideoPlayer` los contadores del vídeo: **el tamaño de la superficie**, fotogramas decodificados, presentados, descartados, resincronizados, el desfase entre el reloj y el fotograma, **los ms que cuesta decodificar uno** y los ms que cuesta presentarlo. El de decodificar incluye el tiempo BLOQUEADO esperando la descarga, que es como se distingue «el decodificador no da abasto» de «no llegan los bytes»: el primero sube con los píxeles, el segundo se dispara a cientos de ms con la superficie pequeña. |
| `UNIGRAM_NO_SINGLE_INSTANCE=1` | No toma el nombre de bus `org.unigram.linux`, de modo que arrancan varias instancias a la vez (comparar dos builds, un segundo directorio de datos). La que ya corre sigue siendo la que contesta al escritorio. |
| `UNIGRAM_NO_DESKTOP_ENTRY=1` | No escribe nada en `~/.local/share` ni en `~/.config`: ni el `.desktop`, ni sus iconos, ni el `.service` de D-Bus, ni el tipo MIME, ni la asociación en `mimeapps.list`. Para quien instale esos ficheros por su cuenta. |
| `UNIGRAM_UNO_LOG=<nivel>` | Nivel del logger propio de Uno. |
| `UNO_DISPLAY_SCALE_OVERRIDE=<factor>` | **De Uno, no nuestra.** Sustituye a `Xft.dpi` como escala de pantalla (`RawPixelsPerViewPixel`, `LogicalDpi`, `ResolutionScale`). Es un **factor**, no un porcentaje: 3 en un panel de 192 dpi es la escala del escritorio (2) al 150%. La lee **una sola vez**, al construir el `DisplayInformation` de la primera ventana, así que hay que ponerla antes de arrancar. Es el camino por el que se aplica el ajuste de escala 100-250% de Unigram — ver `Telegram.Linux/Platform/InterfaceScale.cs`. `Telegram.Common.DisplayScale` la honra también, de modo que las dos coinciden. |

**Apuntar por texto, no por punto ni por índice.** La lista de chats se reordena sola en cuanto
llega un mensaje —comprobado: entre dos ejecuciones separadas por tres minutos, un canal saltó al
primer puesto y desplazó todas las filas 68 unidades—, así que un `120,340` o un `ChatsList[3]`
abren un chat distinto cada vez. `ChatsList{texto}` resuelve por lo que la fila muestra y es el
único que nombra dos veces al mismo chat. Dos avisos: el texto se busca **en cualquier descendiente**
del contenedor, así que un nombre que también aparezca en la vista previa de otra fila casa con la
fila de arriba (el primer contenedor en orden de índice gana); y el botón de menú de la cabecera no
se llama `NavigationButton` sino que su `Name` es la cadena localizada
(`#Abrir menú de navegación` en español), que es lo que `VisualTreeDump` imprime detrás de `#`.

`UNIGRAM_TYPE_TEST` inyecta **teclas X11 reales** (`Platform/XTestKeyboard.cs`) para recorrer todo el
camino de entrada de Uno; la llamada (`XTestFakeKeyEvent`) está en el árbol y no depende de
herramientas externas. Dos avisos medidos: **XTEST no siempre llega** en sesión Wayland (el compositor
decide a qué superficie reinyecta, y a menudo no es el cliente X que tiene el foco), así que el
diagnóstico prueba primero XTEST y, si no llega ni un `KeyDown`, cae a `XSendEvent` contra la ventana
propia; y Uno **no publica `_NET_WM_PID`**, de modo que la ventana propia se reconoce por el título
(`XTestKeyboard.WindowTitle`) — sin eso `XSendEvent` no sabría a quién escribir.

### Ratón: sí se puede, y las tres cosas que hay que saber

`UNIGRAM_CLICK` mete clics **reales** en la ventana con `XSendEvent`, y Uno los procesa por su camino
normal (`X11XamlRootHost` → `X11PointerInputSource.ProcessButtonPressedEvent` → `PointerPressed`);
nada mira el flag `send_event`. Esto **corrige** lo que decía la fila de `UNIGRAM_SCROLL_TEST`: lo que
no funciona es *apuntar* el puntero con XTEST (mutter tiene su *guard window* sobre toda la pantalla
X), no meter el evento. Tres cosas medidas, las tres necesarias para que llegue:

- **La máscara de `XSendEvent` no dice qué evento es, dice a quién se le entrega.** Con una máscara,
  el servidor lo reparte entre los clientes que hayan *seleccionado* ese tipo de evento — y Uno
  selecciona los eventos de puntero del protocolo básico solo cuando no puede usar XI2:
  `X11XamlRootHost` llama a `XSelectInput` con `0x20807C & ~0x74`, que es justamente ButtonPress,
  EnterWindow, LeaveWindow y PointerMotion quitados. Enviado con `ButtonPressMask`, el clic iba
  dirigido a nadie y el servidor lo tiraba en silencio. **Máscara 0** = "el cliente que creó la
  ventana destino", que es exactamente a quien se apunta.
- **La ventana propia se encuentra por `WM_CLASS`, no por `WM_NAME`.** Uno deja el `WM_NAME` vacío en
  esta sesión aunque se le asigne `AppWindow.Title` (`xprop` lee `WM_NAME(STRING) =` sin nada
  detrás), así que la búsqueda por título no encuentra nada; `WM_CLASS` sí trae `"Unigram"`.
  `XTestKeyboard.WindowClass` es lo que se usa ahora, y `DescribeOwnWindow()` lo deja en el log.
- **Y el `WM_CLASS` hay que comprobarlo tambien en la ventana enfocada, no solo al buscar por el
  arbol** (2026-08-26). `FindOwnWindow` probaba antes el foco de entrada aceptandolo si `TitleOf`
  daba `"Unigram"`, y `TitleOf` sube por los ANTEPASADOS cuando una ventana no tiene `WM_NAME`
  propio. Mutter reparenta al cliente X11 dentro de un marco que dibuja **otro proceso**
  (`WM_CLASS "mutter-x11-frames"`) y le da el foco a ese marco: el titulo coincidia, se aceptaba el
  marco, y **todo `XSendEvent` iba a la ventana de otro cliente**. El sintoma es enganoso porque
  `TryClick` sigue contestando `sent: True`: lo que delata el fallo es el log de calibracion, que
  dice `no PointerMoved arrived, assuming the display scale 2,000` en vez de `a motion to window
  400,400 arrives at layout 200,200`, y luego el clic no hace nada. Comparar la linea
  `pointer test: target window is ...` de dos tandas lo ensena a la primera:
  `WM_CLASS "Unigram"` es la buena, `WM_CLASS "mutter-x11-frames"` es la mala. Arreglado
  anteponiendo `IsOurs(focus)` —`WM_CLASS` igual al nuestro— a la prueba del titulo, y bajando la
  busqueda por `WM_NAME` a ultimo recurso por detras de la de `WM_CLASS`, que es la unica que
  distingue el cliente del marco.
- **La escala del camino de puntero es 2, y se mide, no se supone.** Uno divide las coordenadas del
  evento por `XamlRoot.RasterizationScale`; el diagnóstico manda un `MotionNotify` a un punto
  conocido y lee dónde dice la app que está el puntero (medido: ventana 400,400 → maquetación
  200,200). Ojo con el matiz de la nota de `RasterizationScale` de §6: ahí contesta 1 cuando se le
  pregunta al aplicar una plantilla, pero en el camino del puntero ya vale 2.

**Un movimiento del puntero SIN clic**: `unigram-linux/tools/pointer-move.py <x> <y>` (unidades de
maquetación, la misma entrega que `XTestKeyboard.SendPointer`). Hace falta para todo lo que solo
existe mientras el ratón se mueve por encima, y el caso medido es la **barra de mandos de la
galería**: se esconde 1,5 s después del último `PointerMoved` y, mientras está escondida,
`BottomPanel.IsHitTestVisible` es `false`, de modo que el press del clic que la descubriría se cuela
por debajo y cae sobre la foto. La secuencia que funciona es `pointer-move.py` → esperar ~0,3 s →
`<nombre>.click`. Y ojo: la barra se esconde por **opacidad de composición**, así que
`UNIGRAM_DUMP_TREE` la sigue enseñando con su caja entera aunque no se vea (§6).

**Aviso sobre `UNIGRAM_WHEEL`**: el clic se comporta, pero una rueda sobre el historial acabó
moviendo la selección de la **lista de chats** y navegando a otro chat.

> **CAUSA RAÍZ CORREGIDA (2026-09-05, u-wheel-scroll-dead).** Esta nota atribuía el síntoma a que
> `X11PointerInputSource.ProcessButtonPressedEvent` mete el botón de rueda (4-7) en `_pressedButtons`
> y `ProcessButtonReleasedEvent` **se sale antes de quitarlo**, dejando `HasPressedButton` pegado para
> siempre. **Eso es FALSO y se dejó escrito aquí como si fuera un hecho durante diez días.** Se
> mantiene el texto porque llegó a dirigir una tanda entera —el encargo pedía «arreglar el botón
> fantasma»— y borrarlo escondería por qué.
>
> **Lo que dicen las medidas** (Oscar, dos independientes): el *early return* **sigue en el
> `Uno.UI.Runtime.Skia.X11.dll` que enviamos, pero es INERTE**. Los únicos que leen esa máscara son
> `IsLeftButtonPressed`, `IsMiddleButtonPressed` e `IsRightButtonPressed` (bits 1-3); la rueda son los
> botones 4-7, o sea los bits 4-7, que **no los lee nadie**, y `HasPressedButton` sale de
> `PointerPointProperties`, no de la máscara X11. Y con `UNIGRAM_POINTER_TRACE=1` en la raíz de la
> ventana, 5 muescas abajo y 5 arriba llegan como `delta=-120` ×5 y `delta=+120` ×5, **una por
> muesca, signo y magnitud correctos, todas con `handled=False`**: la rueda siempre se entregó bien
> **en esta máquina** —hay una segunda ruta de entrada donde NO, y está más abajo—.
>
> **El agujero de verdad**: Uno Skia deja `ScrollContentPresenter.MouseWheelUp/Down/Left/Right` como
> stubs de `ApiInformation.TryRaiseNotImplemented`, así que **ningún `ScrollViewer` de la app se movía
> con la rueda**. Falta la mitad de abajo de la tubería, no sobra un botón pegado. Sustituto:
> `Telegram.Linux/Xaml/WheelScroll.cs`, un handler en la raíz de cada árbol que sube desde
> `e.OriginalSource` hasta el `ScrollViewer` scrollable más cercano y llama a `ChangeView`.
>
> **Y queda un defecto VIVO que este arreglo NO tapa, en la otra ruta de entrada.** Cuando el evento
> llega por el camino de dispositivo XI2 (`evtype` `XI_ButtonPress`/`XI_ButtonRelease`),
> `CreatePointerEventArgsFromDeviceEvent` decodifica la rueda con
> `data.detail switch { 128 => (-120, true), 32 => (-120, false), 64 => (120, true), 16 => (120, false), _ => (0, false) }`.
> Pero `data.detail` es el **número** de botón —la propia clase declara `SCROLL_UP = 4`,
> `SCROLL_DOWN = 5`, `SCROLL_LEFT = 6`, `SCROLL_RIGHT = 7`— y 16/32/64/128 son `1 << 4` … `1 << 7`,
> o sea la **máscara**. Compara un número contra su propio bit: ninguna muesca casa nunca y todas
> caen en el `_ => (0, false)`. Es decir, **por la ruta XI2 la rueda llega con delta 0 y en silencio**,
> y `WheelScroll.cs` lee `MouseWheelDelta`, así que ahí seguirá sin moverse nada.
>
> Esta máquina **no** pasa por ahí: va por el protocolo CORE, que mapea bien, y por eso los deltas
> medidos son exactamente ±120. De modo que «la rueda siempre se entregó bien» vale **para esta
> máquina**, no en general. Quien vea un ratón muerto tras `43e132a` debe sospechar primero de esta
> ruta —otro dispositivo, otro servidor X, un touchpad— antes de tocar `WheelScroll`. No se arregló
> en su momento a propósito: el arreglo era un handler sin verificar en la ruta de los *press*, y con
> la traza delante ese *press* fantasma **nunca llegaba** en esta máquina, así que no había forma de
> comprobarlo. (Decompilado y medido por Oscar; los valores 16/32/64/128 salen del binario que
> enviamos, no de la memoria.)
>
> **Dos cosas que hay que saber para tocarlo**: hay que suscribirse con **`handledEventsToo: true`**,
> porque Uno **PREFIJA** `Handled` al construir los argumentos
> (`Handled = data.EventWindow != TopX11Window.Window`) y por tanto `Handled` **no** significa «ya
> actuó alguien»; y **los popups necesitan su propio `Attach`**, porque un popup vive en la capa de
> popups del `XamlRoot`, que es **hermana** de `Window.Content` —el mismo límite que documenta la
> sección de más abajo sobre `PointerTest.Resolve`—, así que el handler de la raíz de la ventana no
> está en su ruta.
>
> **La lección de método, que es lo que más caro salió**: esta entrada decía «una causa concreta está
> a la vista en el código de Uno» y se leía como medida cuando era **lectura de código de otro
> repositorio, sin comprobar el binario que enviamos**. Una nota de este documento es **procedencia,
> no verdad**: si va a decidir un parche, se vuelve a medir **antes** de elegirlo, no después.

Para mover el historial sin depender de la rueda se sigue usando `UNIGRAM_HISTORY_SCROLL`, que llama
a `ChangeView` sobre el `ScrollViewer` del historial: ejercita virtualización, reciclado, anclaje y
paginación, pero no la tubería de la rueda.

### Un objetivo de clic vive en el *popup root*, no en `Window.Content` (2026-08-26)

`PointerTest.Resolve` buscaba solo dentro de `window.Content`, y **ninguna entrada de menú está
ahí**: un `MenuFlyout`, un `ContentDialog` y la galería cuelgan del *popup root*, que en Uno es
**hermano** de `Window.Content`. El fallo era mudo —un objetivo que no aparece solo deja
`no target`—, así que parecía que los menús no se podían pulsar. Arreglado recorriendo también
`VisualTreeHelper.GetOpenPopupsForXamlRoot`, igual que ya hacían `Screenshot` y `VisualTreeDump`, y
**los popups van primero**, que es lo que hace que un submenú gane a la página que tapa. El origen
de cada popup se saca de `child.TransformToVisual(window.Content)` con la caída a
`popup.HorizontalOffset/VerticalOffset`, como en `Screenshot`. Un objetivo `x,y` literal se resuelve
aparte, contra el contenido y nada más: ya viene en coordenadas de ventana y sumarle el origen de un
popup lo mandaría a otro sitio.

**Segunda victima del mismo arbol, encontrada en la consolidacion #5: `GetParent<T>()` tampoco
cruza esa frontera.** `ReactionsMenuFlyout.Initialize` empezaba subiendo desde `flyout.Items[0]`
hasta el `MenuFlyoutPresenter` y **se salia** si no lo encontraba; en Uno no lo encuentra nunca, por
la misma razon. El fallo era mudo por partida doble: ni excepcion ni log, y el menu de contexto
seguia abriendo perfectamente debajo, asi que parecia que la barra de reacciones «no estaba
portada» cuando el codigo estaba entero y se rendia en la primera linea. Sustituto:
`Extensions.Presenter()` sobre `MenuFlyout` (el `#if LINUX` va DENTRO, como en `ContentRoot()`),
que intenta el recorrido de siempre y, si falla, busca entre los popups abiertos. **El presenter no
se acepta por tipo**: se comprueba que sus `Items` contienen el item del flyout, porque puede haber
varios menus abiertos a la vez y el submenu gana al padre. Regla general que deja esto: en Skia,
cualquier `GetParent<T>`/`Ancestors<T>` que **empiece dentro de un flyout, un dialogo o la galeria y
espere salir hacia la pagina** esta roto por construccion, y falla devolviendo null, que es la
forma mas cara de fallar.

**Y debajo habia una segunda capa, que es la leccion que de verdad importa.** Arreglada la busqueda
del presenter, `Initialize` avanzaba y moria una capa mas abajo, en
`compositor.CreateDropShadow()` **en crudo** — el ayudante guardado `VisualUtilities.DropShadow` ya
existia y ya devolvia null a proposito en esta cabeza, pero este fichero no lo usaba. El sintoma era
identico al anterior: un popup en vez de dos y el log limpio. **Por que el log estaba limpio: la
excepcion no se pierde por descuido, se pierde por el tipo del metodo.** `Initialize` es
`async void`, y un `async void` NO propaga a quien lo llama: la maquina de estados captura lo que
lanza y lo postea al dispatcher. Un `try/catch` en el sitio de llamada —que es lo primero que uno
escribe— **no lo ve nunca**; hay que envolver el CUERPO. De ahi el patron: cuerpo en
`InitializeCore`, y el `async void` reducido a envoltorio con `try/catch` + `Logger.Error`.
Corolario practico para cualquier fallo de esta ronda: cuando el parte sea «no pasa nada y no hay
log», sospechar de un `async void` antes que de nada mas — llevamos cuatro.

### El botón derecho llega; el menú del chat es un método vacío (2026-08-26) — CERRADO

**Cerrado el 2026-08-26 por la tanda de integración.** El método ya no está vacío y el menú abre
sobre la lista de chats y sobre un resultado de búsqueda. Ejercitado con la respuesta de TDLib
delante: `toggleChatIsPinned`, `toggleChatIsMarkedAsUnread`, `setChatNotificationSettings{mute_for=3600}`,
`addChatToList{chatListArchive}` y su `updateChatPosition`, y `addChatToList{chatListMain}` al
desarchivar. Siguen muertas dos entradas y las dos por falta de pantalla, no de cableado:
«Silenciar…» (duración a medida, necesita `ChatMutePopup`) y «Crear carpeta» (necesita `FolderPage`).
Lo que faltaba de verdad no era el `#if`: era que `ChatListListView.GetContainerForItemOverride`
reemplazaba el de `TopNavView` sin reponer `container.ContextRequested += ItemContextRequested`
(ver la entrada de CS0070 en la regla 6). El texto original, por si sirve de historia:



`XTestKeyboard.TryClick` siempre aceptó el número de botón; lo que faltaba era escribirlo. Con
`right:` delante del objetivo, el clic derecho **sí** recorre el camino real: medido sobre la
descripción de un perfil, `#Description` abre su menú «Abrir / Copiar» en el punto exacto del clic.
Sobre una fila de la lista de chats, en cambio, **no pasa nada** — y no es el inyector:
`MainPage.xaml.cs:3383-3398` deja `Chat_ContextRequested` y
`DialogsSearchPanel_ItemContextRequested` con el **cuerpo vacío** bajo `#if LINUX` («Chat context
menus (MenuFlyoutHelper, popups) are out of Phase 1»). Es decir: silenciar, fijar, archivar, marcar
como leído y borrar un chat desde la lista **no existen** hoy.

### `ContentPopup`: ARREGLADO, con una segunda puerta desde `Closed` (2026-08-26, fase 3)

El arreglo son dos líneas y está en `Telegram/Controls/ContentPopup.cs`, bajo `#if LINUX`:
`Closed += OnClosedLinux;` en el constructor y un `OnClosedLinux` que llama a `Test()`. `Test()` es
un `TrySetResult`, así que la puerta que llegue primero gana y la otra es un no-op; en Windows nada
cambia. `_closingResult` ya lo había puesto `OnClosing` a partir de `args.Result`.

**Medido en la app** (`fase3/runB.log`), justo la secuencia que antes era imposible:

| paso | antes (fase 2) | ahora |
| --- | --- | --- |
| primer diálogo | se abre (`ContentPopup.cs:413 MessagePopup`) | se abre |
| se pulsa un botón | `OnClosing` ve el resultado y ahí muere | el diálogo cierra y `ShowQueuedAsync` reanuda |
| **segundo diálogo** | **no se abre nunca**, la cola queda `BUSY` | **se abre** (segundo `MessagePopup` en el log, captura `06-dialogo-2.png`) |

Se verificó pulsando **Cancelar** las dos veces sobre «¿Quieres bloquear este contacto?» en el
perfil de un bot público, para no escribir nada en la cuenta: `setMessageSenderBlockList` tiene cero
ocurrencias en el log de TDLib de toda la tanda. Que la cola se libere demuestra que `_closingTask`
completó, que era el eslabón roto. **Lo que queda sin medir de punta a punta** es una pasada con
`Primary` que llegue hasta la petición: hoy el único `ShowPopupAsync` alcanzable en el subconjunto
Linux es bloquear/desbloquear a una persona o a un bot, y eso escribe en la cuenta del usuario. La
cadena está probada por composición (la fase 2 midió `OnClosing result=Primary`; la fase 3 mide que
el `Task` vuelve), no por una sola ejecución.

### El histórico del defecto, para no volver a tropezar

### `ContentPopup` se cierra y su `Task` no vuelve NUNCA: toda confirmación es un callejón sin salida (2026-08-26)

**Es el defecto que más superficie tumba de todo el port y está medido.** Secuencia instrumentada
(`fase2/run03.log`): `DIAGPOPUP ShowQueuedAsync enter, queue is free` → el diálogo se dibuja →
`DIAGPOPUP OnClosing result=Primary` al pulsar «OK» → **y ahí se acaba**. `Test()` no corre,
`_closingTask` no se completa, `ShowQueuedAsync` no reanuda, y quien esperaba
(`ProfileViewModel.Block`) **no ejecuta su acción**: cero peticiones a TDLib. Y como
`_currentDialogShowRequest` solo se pone a `null` después de ese `await`, el segundo intento
contesta `DIAGPOPUP ShowQueuedAsync enter, queue is BUSY` y **ya no se abre un diálogo más en toda
la sesión**.

La cadena rota: `ContentPopup.cs:385 ShowQueuedAsync` espera `_closingTask`, que solo completa
`Test()` (`ContentPopup.cs:368`), al que solo llama `OnIsHitTestVisibleChanged`
(`ContentPopup.cs:361`) cuando `LayoutRoot.IsHitTestVisible` pasa a `false`. Y el **único** sitio
que lo pone a `false` es un `ObjectAnimationUsingKeyFrames` dentro de
`<VisualStateGroup.Transitions><VisualTransition To="DialogHidden">` en
`Themes/Generic.xaml:4305-4316`; el estado `DialogHidden` en sí está **vacío**
(`Generic.xaml:4375`). Uno no ejecuta ese *storyboard de transición*, así que la propiedad no se
toca nunca. Confirmado por descarte de la otra sospecha: `QueueCallbackForCompositionRendered` sí
funciona en Linux (`CompositionRenderedClock`), porque otros que lo usan —la selección de la lista
de chats, `MainPage.xaml.cs:2011`— sí corren.

Lo que esto se lleva por delante, todo lo que pasa por `ShowPopupAsync`: **bloquear y desbloquear**
un usuario o un bot (`ProfileViewModel.Block/Unblock`), **borrar o salir de los chats
seleccionados** (`ChatListViewModel.DeleteSelectedChats`, que es el único camino vivo de borrado que
queda en el port), `BanAndReport`, quitar una nota de contacto, y cualquier «¿seguro?» del programa.
Arreglo probable, sin tocar el template: completar `_closingTask` desde `OnClosed`/`OnClosing` en
vez de depender del cambio de `IsHitTestVisible`. *(Es el que se aplicó; ver la entrada de arriba.)*

### El port no tiene NINGÚN camino vivo para borrar un chat (2026-08-26, fase 3) — CERRADO

**Cerrado el 2026-08-26 por la tanda de integración**, por la puerta (2): el menú contextual de la
lista, y también sobre un resultado de búsqueda. `DeleteChatPopup` entró en el subconjunto y
`ChatListViewModel.DeleteChat` pasa `asOwner` cuando `can_be_deleted_for_all_users` lo permite, de
modo que el creador de un supergrupo o canal ve la casilla «Eliminar para todos los suscriptores».
Medido sobre el banco: con la casilla marcada sale **`deleteChat`** y **ningún `leaveChat` en todo el
log**; el canal queda `chatMemberStatusBanned` con `member_count = 0`, que es exactamente el huérfano
que había que evitar. La puerta (1), el «Más» del perfil siendo creador, **sigue cerrada**
(`ProfileHeader.xaml.cs:1792` y `:1809`); la (3), la selección múltiple, vuelve a ser alcanzable
porque el menú restaura `SelectChat`, pero `DeleteSelectedChats` **sigue haciendo
`LeaveChat` + `DeleteChatHistory` sin guardas** y por tanto sigue pudiendo dejar huérfano un
supergrupo propio: no lo he tocado y es lo primero que debería coger la próxima tanda. El texto
original:



Tres puertas y las tres tapiadas. Se midieron en la app, no solo leyendo:

1. **Perfil → «Más»**: siendo creador no ofrece salir. `ProfileHeader.xaml.cs:1792` y `:1809`
   condicionan la entrada a que el estado sea `Member` o `Restricted`. Es de Unigram, no del port.
   Y aunque estuviera, `ProfileViewModel.DeleteChat` (línea 1412) se sale con un `return` porque
   `DeleteChatPopup` no está en el subconjunto.
2. **Menú contextual de la lista de chats**: `MainPage.xaml.cs:3383-3398`, `Chat_ContextRequested`
   con el **cuerpo vacío** bajo `#if LINUX`. Contraprobado: el clic derecho sobre una fila **sí
   llega** (`OnGettingFocus … Mouse ~> Pointer` en `runB.log`) y no sale nada.
3. **Selección múltiple → papelera**: `ChatListViewModel.DeleteSelectedChats` no tiene guardas y
   **es inalcanzable igualmente**. Lo único que pone `SelectionMode = Multiple` es
   `ChatListViewModel.SelectChat`, y su **único** llamante es `MainPage.xaml.cs:3555`, que está
   dentro del `#else` del bloque anterior. O sea: la entrada al modo selección es la entrada del
   menú contextual que no existe. Medido: `#ButtonManage` contesta `no target`, porque `ManagePanel`
   solo se hace visible cuando el modo ya está activo — ese botón es el de *salir* del modo, no el
   de entrar.

Consecuencia práctica: el banco de pruebas hubo que borrarlo por TDLib directo
(`BankSetup.ScheduleTeardown`, `UNIGRAM_BANK_TEARDOWN`), igual que se creó. Y `DeleteSelectedChats`
usa `LeaveChat` + `DeleteChatHistory`, que sobre un supergrupo propio **deja el supergrupo en pie**
sin nadie dentro; para borrarlo de verdad hay que usar `DeleteChat` **antes** de dejar de ser
creador, porque después `can_be_deleted_for_all_users` ya es `false` (comprobado: el segundo intento
contesta `400 The chat can't be deleted`).

### `UNIGRAM_TD_REQUESTS=1`: el volcado de TDLib, solo mientras dure la ejecución (2026-08-26, fase 3)

El tag `td_requests` hace que TDLib escriba **cada petición y cada respuesta, con contenidos**, a
`LocalState/tdlib_log.txt`. Encenderlo por el contenedor persistente `Diagnostics` —que es lo que
hacía la fase 2— deja el volcado activo, y con él las conversaciones reales del usuario, hasta que
alguien se acuerda de apagarlo. `ClientService.InitializeDiagnostics` mira ahora
`UNIGRAM_TD_REQUESTS` bajo `#if LINUX` y aplica el tag a esa ejecución y nada más: al cerrar, el
contenedor `Diagnostics` sigue sin la clave. Comprobado al terminar la fase 3.

### Cerrar la ventana en una prueba: `WM_DELETE_WINDOW`, nunca `xdotool windowclose`

Para ejercitar "cerrar a la bandeja" hay que mandar lo mismo que manda mutter cuando se pulsa la X:
un `ClientMessage` de `WM_PROTOCOLS`/`WM_DELETE_WINDOW` a la ventana (cuatro líneas de python-xlib;
la ventana se encuentra por `WM_CLASS`, que aquí sí trae `"Unigram"`). **`xdotool windowclose`
no vale**: su propio manual dice que *destruye* la ventana, y con la ventana destruida bajo los
pies el host X11 de Uno hace `XQueryTree` sobre ella, Xlib entrega `BadWindow` al manejador por
defecto y ese manejador **llama a `exit()`** — el proceso muere y parece que la app no sobrevive al
cierre, cuando lo que no sobrevive es a que le borren la ventana. (`xdotool windowunmap`/`windowmap`
sí son inofensivos y son la forma rápida de comprobar a mano que esconder y devolver la ventana
funciona.)

### La tira de historias vive en la fila del título, no en una banda propia (2026-08-26)

Unigram dibuja las historias **a la altura del logo de Telegram**, no en una banda debajo del
buscador, y ahí sólo caben avatares. Este port lo hace así desde
`MainPage.MoveStoriesIntoTitleBar` (antes `MoveStoriesIntoHeader`): el control se saca de su ranura
declarada —la superposición sobre el título, que es la ranura *plegada* y cuya coreografía de
plegado no existe en Uno, ver §6— y se mete como hijo de `TitleBarrr`, en la misma columna 2 en la
que está `TitleText`.

Tres números y una regla:

- **`ActiveStoriesCell` es compacta en Linux**: `Side = 36` en vez de 48, sin nombre (`Title`
  colapsado), la foto a `Side - 8` y los dos `ActiveStoriesSegments` a `Side`. 36 es a propósito un
  poco mayor que los 32 del botón del logo que tiene al lado: es la comparación con la que se juzga
  esa fila. La celda entera mide 40x40 y el contenedor del `ListView` también (el
  `ItemContainerStyle` de `StoriesStrip.xaml` ya ponía `Padding` y `MinHeight` a 0).
- **La tira empieza detrás del texto**: `MainPage.UpdateTitleBarStoriesLayout` escribe
  `Stories.Margin.Left = TitleText.ActualWidth + TitleText.Margin.Right + 8`, y se re-ejecuta desde
  `TitleText.SizeChanged` porque ese texto cambia de ancho con el estado de la conexión
  («Telegram», «Actualizando…», «Conectando…»).
- **`TitleBarHandle` se aparta, no se encoge la tira.** El asa es la que se le pasa a
  `SetTitleBar`, es hermana de `TitleBarrr` y se pinta **encima** (`Canvas.ZIndex="1"`), así que sin
  tocarla la tira quedaría debajo y no se podría pulsar. El mismo método le mueve el borde
  izquierdo a `TitleBarrr.Margin.Left + 40 + margen + ancho + 8`, que es la misma aritmética que
  usa upstream en `StoriesStrip.UpdateIndexes` para la banda plegada. Medido con la ventana
  maximizada a 1368x776: tira `[160,0 76x40]`, asa `[244,0 1124x40]`.
- **Sin historias la tira no ocupa nada.** `StoriesStrip.UpdateVisibility` colapsa el control
  cuando `ViewModel.Items.Count == 0`, y entonces el asa vuelve a `[88,0 1280x40]`. El precio es
  que el botón «+» de publicar **vive dentro de la tira** y se va con ella: con cero historias hoy
  no hay forma de publicar una. Su sitio natural es el menú de redacción, que no está portado.

El efecto de rebote es el que pedía el usuario por otro lado: `ChatListHeader` deja de cargar con
los 88 px de la banda y vuelve a **132** (92 + 40), `UpdateChatListTopPadding` lo sigue solo y la
píldora de carpetas queda pegada al buscador. Medido antes: cabecera `489x220`, banda `[0,92
489x88]`, píldora `[0,180 489x40]`, lista `[0,220 489x556]`. Después: cabecera `489x132`,
`StoriesStripHost` `[0,0 0x0]` colapsado, píldora `[0,92 489x40]`, lista `[0,132 489x644]`.

### Arrastrar la ventana NO se puede probar desde un script, y además la decora mutter (2026-08-26)

Lo intentado, y lo que contestó cada cosa:

- **XTEST**: `xdotool mousemove/mousedown/mousemove_relative/mouseup` sobre la zona del asa **no
  mueve la ventana**. Es el mismo agujero que ya estaba medido para el teclado (la sonda de
  `UNIGRAM_TYPE_TEST` de esa misma ejecución: «probe through XTEST: 0 KeyDown reached the app»).
- **La pulsación inyectada por `XSendEvent`** (`UNIGRAM_CLICK`, con el prefijo `hold`) **sí llega a
  la app** —cambia el foco— pero **no hace que Uno pida mover la ventana**. Medido de verdad: un
  oyente de python-xlib con `SubstructureNotifyMask` sobre la raíz, esperando `_NET_WM_MOVERESIZE`,
  vio **0** mensajes durante un `hold3000:700,20` en pleno centro del asa. El oyente **no** es el
  problema: autoprobado enviándole un `_NET_WM_MOVERESIZE_CANCEL` a mano, que sí capturó.
  `Uno.UI.Runtime.Skia.X11.dll` exporta las cadenas `_NET_WM_MOVERESIZE*`, así que el camino existe;
  lo que no lo dispara es una pulsación sintética.
- **La ventana venía maximizada**, y una ventana maximizada no se arrastra igual. Para
  des-maximizarla: aquí **no hay `wmctrl`** y el `xdotool` instalado **no tiene `windowstate`**; se
  hace con un `ClientMessage` de `_NET_WM_STATE` a la raíz (cuatro líneas de python-xlib, igual que
  el `WM_DELETE_WINDOW` de más arriba).
- **La captura de pantalla completa está denegada**: `org.gnome.Shell.Screenshot.Screenshot`
  contesta `AccessDenied: Screenshot is not allowed`. Para ver la ventana sigue valiendo
  `UNIGRAM_SCREENSHOT`/`.shot`, que sólo captura el área del cliente.

Y el dato que cambia el marco de la pregunta: **la ventana lleva decoración del servidor**.
`xprop _NET_FRAME_EXTENTS` contesta `0, 0, 74, 0` y el marco padre es 124 px más alto que el
cliente, o sea que mutter dibuja su propia barra de título de 74 px físicos (37 de maquetación)
**encima** de la fila de título que pinta la app. Arrastrar por ahí funciona pase lo que pase con
`TitleBarHandle`. Conclusión práctica: de una tanda a otra, lo que se puede **medir** del asa es su
caja y que nada se le monte encima; que arrastre de verdad hay que mirarlo con el ratón.

### Táctil: llega, y las cuatro cosas que hay que saber

**El táctil llega a Uno.** Medido de punta a punta (kernel → libinput → mutter → XWayland → XI2 →
Uno) en la Surface Pro 4 con Ubuntu 26.04, GNOME/Wayland y Uno 6.6.184: los eventos aparecen como
`PointerDeviceType.Touch`, con un id por dedo, coordenadas en unidades de maquetación,
`PointerPressed`/`Moved`/`Released`, `Tapped`, `Holding`, `RightTapped` y la familia completa de
`Manipulation*`, inercia incluida. El spike, el detalle y la receta de 30 segundos para volver a
comprobarlo están en `unigram-linux/spikes/TouchSpike/README.md`.

- **Uno abre XInput2 y selecciona los eventos táctiles**, pero solo si se cumplen dos condiciones que
  conviene saber comprobar: `XGetExtensionVersion("XInputExtension")` tiene que contestar **2.2 o
  más** (aquí, 2.4) y **`libXi.so.6`** tiene que estar. Si falla cualquiera de las dos, Uno registra
  *"Falling back to the core protocol implementation for pointer inputs"* y el táctil no llega nunca,
  porque el protocolo básico de X no tiene eventos táctiles. Con las dos, `X11XamlRootHost` llama a
  `XISelectEvents` con la máscara `498 | 0x1C0000`, donde `0x1C0000` es exactamente
  `XI_TouchBegin(18) | XI_TouchUpdate(19) | XI_TouchEnd(20)`.
- **El id del puntero es el `detail` del evento XI2**, o sea el identificador de secuencia táctil del
  servidor: multitáctil de verdad, un id por dedo, y un id nuevo por cada vez que el dedo se levanta
  y vuelve. No es un número de ranura estable: no sirve para identificar "el mismo dedo" entre dos
  gestos.
- **`IsLeftButtonPressed` está en `true` mientras el dedo toca**, que es lo que hace que el código
  escrito para el ratón funcione con el dedo sin tocarlo. Y los eventos con la bandera
  `XIPointerEmulated` se descartan, así que un toque **no** llega dos veces (una como táctil y otra
  como ratón emulado) — el duplicado que sí existe es el de `PointerMoved`, ver §6.
- **Desde fuera del proceso esto no se puede medir.** Un escucha XI2 externo sobre la ventana raíz
  recibe **cero** eventos táctiles mientras la ventana de Uno está delante: XI 2.2 los entrega a la
  ventana *más profunda* que los haya seleccionado. Y para *inyectar* un toque no vale XTEST, que no
  tiene eventos táctiles: hace falta un dispositivo de verdad, que es lo que
  `spikes/TouchSpike/tools/uinput-touch.c` crea con `uinput` (en esta máquina `/dev/uinput` tiene ACL
  para el usuario) y destruye al terminar. Ese toque es real para el escritorio y cae donde caiga,
  así que antes se comprueba con `tools/check-active.py` que la ventana activa es la propia.
- **Cómo se toca la app de verdad, ya envuelto**: `unigram-linux/tools/touch.py`, que habla en
  **unidades de maquetación** (las mismas del volcado del árbol y de `UNIGRAM_CLICK`) y las traduce a
  las normalizadas 0..1 de pantalla que quieren los inyectores — origen de la ventana en la raíz X
  por `WM_CLASS`, más maquetación por la escala (2 aquí), dividido por el tamaño de pantalla.
  `touch.py where | tap x y | hold x y ms | drag x0 y0 x1 y1 ms | pinch cx cy d0 d1 ms | fingers …`,
  y aborta con 1 si la ventana activa no es la propia. El pellizco necesita **dos** dedos, que
  `uinput-touch.c` no sabe hacer: los hace `spikes/TouchSpike/tools/uinput-touch2.c` (dos slots
  multitáctiles, un `tracking id` por dedo, los dos abajo en el mismo SYN).
  Dos avisos medidos el 2026-08-24: la ventana **pierde el foco sola** entre un gesto y el
  siguiente (bajo Wayland el foco se va a una ventana nativa y `_NET_ACTIVE_WINDOW` queda vacío),
  así que conviene reactivarla con un `_NET_ACTIVE_WINDOW` antes de cada inyección; y un
  `drag` de 900 ms llega a la app como una manipulación de ~80 ms cuando el reconocedor ya venía
  de otro gesto, de modo que la **velocidad** que reporta no es la que se pidió: hay que leerla del
  log (`UNIGRAM_GESTURE_PROBE`), no suponerla.

### Anchuras de retorno al declarar APIs de terceros (`libmpv`, Xlib): `long` y punteros a función

Regla 4 de `unigram_native.h` —nada de `long` ni `size_t` en la frontera— vale para **nuestra** ABI,
que la cumple entera. Las bibliotecas de terceros que el port declara a mano no la cumplen ni tienen
por qué, y ahí la anchura hay que copiarla del prototipo C, no del uso. En LP64 (Linux x86-64):

| Prototipo C | Anchura real | Declaración C# correcta | Qué NO vale |
|---|---|---|---|
| `unsigned long mpv_client_api_version(void)` — `/usr/include/mpv/client.h:266` | 8 bytes | `nuint` | `uint` (lee solo EAX) |
| `XErrorHandler XSetErrorHandler(XErrorHandler)` — `/usr/include/X11/Xlib.h:1843-1848`; `XErrorHandler` es `int (*)(Display*, XErrorEvent*)` | 8 bytes (puntero a función) | `IntPtr` | `int` |

**Las dos están ya arregladas** (`MpvClient.cs`, `nuint` + `ApiVersion` a `ulong`; `X11Window.cs`,
`IntPtr`), verificadas contra las cabeceras instaladas en esta máquina. Lo que sigue es el porqué,
que se queda escrito porque explica por qué el fallo no se veía y por qué había que arreglarlo
igualmente: mientras estuvieron con la anchura corta **no rompían nada**, por dos motivos que
conviene entender para no confiarse la próxima vez: en la ABI SysV x86-64 el
retorno viaja en `RAX`, así que leer solo `EAX` **no descuadra la pila ni corrompe nada** —solo trunca
el valor—, y además ninguno de los dos valores truncados se usa entero. `mpv_client_api_version`
devuelve `major << 16 | minor`, que cabe en 32 bits mientras mpv no toque el rango alto; y el retorno
de `XSetErrorHandler` —el manejador anterior— se descarta.

El día que dejen de ser inocuas es concreto, y por eso están escritas aquí:

- `nuint` es la traducción correcta de `unsigned long` en las dos anchuras (8 bytes en LP64, 4 en un
  runtime de 32 bits); `ulong` acertaría solo en la primera. `MpvClient._apiVersion` pasó a `ulong`
  con un cast explícito en la llamada; `>> 16` y `& 0xFFFF` funcionan igual.
- Restaurar el manejador de X11 anterior es **el uso normal** de `XSetErrorHandler`. Con el retorno
  declarado `int`, lo que se guardaba era medio puntero, y devolverlo saltaría a una dirección
  inválida en el primer error de X11 — un fallo lejísimos de la línea que lo causó. Hoy el puntero
  vuelve entero y se sigue descartando: quien quiera encadenar al manejador anterior de Uno ya tiene
  un valor usable, sin tocar la declaración.

Regla general para cualquier declaración nueva contra una biblioteca del sistema: `long`/`unsigned
long` → `nint`/`nuint`; `size_t`/`ssize_t` → `nuint`/`nint`; cualquier puntero, incluido un puntero a
función, → `IntPtr` (o `delegate* unmanaged<...>`); `int`/`unsigned` → `int`/`uint`. Un retorno
declarado más corto que el real no da error de enlace ni excepción: se lee truncado y en silencio.

### Quién posee y quién libera en la frontera nativa (vídeo y mpv)

Tres tandas arreglaron a la vez los once defectos de
`unigram-linux/review/INFORME-frontera-nativa.md`, dos de ellas tocando los **dos** lados del mismo
handle. El riesgo de eso no es que un arreglo esté mal, es que los dos estén bien por separado y
juntos dejen una doble liberación o un objeto que ya no destruye nadie. Ésta es la tabla que hay que
mirar antes de tocar cualquiera de estas vidas, y el invariante que la sostiene:

**Regla única: el lado gestionado NUNCA libera algo que la biblioteca todavía posee, y la
biblioteca NUNCA llama a algo que el lado gestionado ya retiró.** La primera mitad se cumple con
disciplina (quien recibe un handle vivo lo cierra, no suelta el puente); la segunda **no se confía a
la disciplina del otro lado**, porque el hilo de compresión ya demostró que sobrevive al cierre de su
animación: se cumple porque un cookie retirado resuelve determinísticamente a `null`.

| Recurso | Lo posee | Lo libera exactamente una vez | Qué lo hace seguro |
|---|---|---|---|
| `unigram_video_source` (la copia por valor que se lleva `_open`) | la `VideoAnimation` en cuanto `_open` devuelve un handle no nulo | `VideoAnimation::Close()`, protegido por su bandera `closed`, alcanzable sólo desde `~VideoAnimation` en el camino cacheado | el `closed` hace `Close()` idempotente, así que el `Close()` explícito del camino no cacheado y el del destructor no destruyen la fuente dos veces |
| ídem, cuando `_open` devuelve NULL | nadie | `bridge.Release()` desde C#, que es idempotente (`Interlocked.Exchange`) | si la biblioteca ya la destruyó de camino a la salida, el `Release()` del C# es un no-op; si no, es el único que hay. Ninguno de los dos caminos puede quedarse sin liberar ni liberar dos veces |
| ídem, animación servida **desde la caché** | nadie: `CachedVideoAnimation::LoadFromFile` la destruye dentro del propio `_open` porque ya no la va a consumir nadie | ese `source_destroy(copy)` | el `Release()` que dispara llega mientras el puente sigue enraizado por la variable local del C#, y `m_animation` se queda nulo, así que el destructor no vuelve a destruirla |
| `CachedVideoAnimation` (el objeto C++) | el `shared_ptr` del handle **y** el `item` que el hilo de compresión saca de su `weak_ptr` | el último de los dos que suelte, en `~CachedVideoAnimation` | `unigram_cached_video_animation_close` ya **no** llama a `Close()`: llama a `Cancel()`, que sólo levanta banderas. Ésa es la regresión H1, y la regla que restaura es la de Windows: destruir sólo desde el destructor |
| el `VideoAnimationSourceBridge` gestionado | la entrada de `NativeCookieTable<T>` (es la referencia fuerte que antes era el `GCHandle`) | `Release()`, desde el `destroy` de la biblioteca o desde el camino de fallo de `_open` | la tabla reparte contadores que **no se reutilizan jamás**, así que una llamada tardía con un cookie retirado resuelve a `null` y devuelve el «no puedo» que la ABI ya tenía. Un `GCHandle` no podía hacer esto: `IsAllocated` es `_handle != 0` sobre la copia recién construida y no filtra nada |
| el hilo de compresión | un `std::thread*` estático que **nunca se destruye** | `Shutdown()`, que aborta la cola y hace `join` | un `std::thread` **objeto** estático registra su destructor con `__cxa_atexit`, y `~thread()` sobre un hilo joinable llama a `std::terminate()` aunque su función ya haya retornado: eso era el SIGABRT al salir. Con un puntero no hay destructor que registrar, así que las salidas que ningún gancho gestionado alcanza (SIGKILL, `FailFast`) simplemente se saltan el `join` |
| el `mpv_handle` | el `MpvClient`; nadie más ve el puntero (no hay propiedad `Handle`) | la bomba de eventos, que pone `_handle` a cero **bajo el lado exclusivo** de `_gate` y llama a `mpv_terminate_destroy` **fuera** | esperar el lado exclusivo es lo que drena a los que ya habían leído un handle vivo; destruir fuera del cerrojo evita aparcar a todo el mundo detrás de una descarga colgada |
| el cookie del protocolo `tg://` | `MpvStreamProtocol` | `Dispose()`, y sólo cuando `mpv_terminate_destroy` ha **retornado** | `stream_cb.h` dice que la desregistración termina cuando esa llamada vuelve. Si la espera acotada se agota, el jugador llama a `Abandon()` (suelta las fuentes: un `open` tardío recibe un «no puedo abrir» limpio, que es la opción 3 del propio header) y le pasa la liberación de verdad a `MpvClient.WhenTerminated` |

Dos consecuencias que conviene tener presentes:

- **El `destroy` del puente puede llegar en el hilo de compresión de la biblioteca**, no sólo en el
  que cerró la animación, y durante `ProcessExit`. Es reentrada gestionada desde un hilo que el CLR
  no creó: por eso todos los `[UnmanagedCallersOnly]` del puente se tragan sus excepciones, y por eso
  `Release()` no toma ningún cerrojo que el hilo de salida pueda estar sosteniendo.
- **`Cancel()` no espera.** Levanta `m_closed` y para el decodificador; la pasada en vuelo lo nota
  en el fotograma siguiente y abandona sin escribir el trailer, de modo que el `.cache` a medias se
  rechaza en la sesión siguiente (`headerOffset == 0`) y se reconstruye. Si se cambia eso y se deja
  publicar el trailer de una pasada abandonada, el sticker del que el usuario se apartó a mitad de
  cacheo se queda **cortado para siempre**, en silencio: lo cubre el caso H1 de
  `native/engines/video/test/video_regress.c`.
