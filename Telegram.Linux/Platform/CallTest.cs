//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Globalization;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Services.Calls;
using Telegram.Td.Api;
using Telegram.Views.Calls;
using Telegram.Views.Host;
using Microsoft.UI.Xaml;

namespace Telegram.Common
{
    /// <summary>
    /// UNIGRAM_CALL_TEST=&lt;segundos&gt; o UNIGRAM_CALL_TEST=incoming[,&lt;segundos&gt;]:
    /// abre la PANTALLA DE LLAMADA sin llamar a nadie.
    ///
    /// Modos:
    /// - Saliente (defecto): UNIGRAM_CALL_TEST=2 (o cualquier número de segundos).
    ///   Fabrica una llamada saliente (IsOutgoing=true, VoipState.None).
    /// - Entrante: UNIGRAM_CALL_TEST=incoming o UNIGRAM_CALL_TEST=incoming,2
    ///   o UNIGRAM_CALL_TEST_MODE=incoming.
    ///   Fabrica una llamada entrante (IsOutgoing=false, CallStatePending { IsCreated=true, IsReceived=true },
    ///   VoipState.Ringing), reproduciendo exactamente el camino que TDLib recorre en una llamada real.
    ///
    /// En ambos modos la llamada se despacha a través de IViewService.OpenAsync desde un hilo en segundo
    /// plano (Task.Run) para ejercitar la resolución de WindowContext y el salto al dispatcher de la UI.
    /// </summary>
    public static class CallTest
    {
        private const int FakeCallId = -1;

        public static void Schedule(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_CALL_TEST");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var isIncoming = value.Contains("incoming", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("UNIGRAM_CALL_TEST_MODE"), "incoming", StringComparison.OrdinalIgnoreCase);

            double seconds = 2.0;
            var parts = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedSeconds))
                {
                    seconds = parsedSeconds;
                    break;
                }
            }

            _ = RunAsync(seconds, isIncoming);
        }

        private static async Task RunAsync(double seconds, bool isIncoming)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));

            try
            {
                var session = LifetimeService.Current?.ActiveItem;
                if (session?.ClientService == null)
                {
                    Logger.Error("CALL-TEST: no hay sesión activa");
                    return;
                }

                Logger.Info($"CALL-TEST: capa de audio activa = {Native.Calls.UnigramCalls.ActiveAudioLayer} "
                    + "(4 = PulseAudio, 10 = el dummy mudo, -1 = la .so no cargó)");

                var fake = new Call
                {
                    Id = FakeCallId,
                    UserId = session.ClientService.Options.MyId,
                    IsOutgoing = !isIncoming,
                    IsVideo = false,
                    State = isIncoming
                        ? new CallStatePending { IsCreated = true, IsReceived = true }
                        : new CallStatePending { IsCreated = false, IsReceived = false }
                };

                var state = isIncoming ? VoipState.Ringing : VoipState.None;
                var call = new VoipCall(session.ClientService, session.Settings, session.Aggregator, fake, state);

                // Ejecutar desde un hilo de fondo para reproducir el camino de TDLib a través de IViewService.OpenAsync
                await Task.Run(async () =>
                {
                    var service = session.Resolve<IViewService>();
                    var options = new ViewServiceOptions
                    {
                        Width = !isIncoming ? 720 : 320,
                        Height = !isIncoming ? 540 : 320,
                        PersistedId = "Call",
                        Content = window => new VoipWindow(window, call),
                        ViewMode = !isIncoming
                            ? ViewServiceMode.Default
                            : ViewServiceMode.CompactOverlay,
                    };

                    Logger.Info($"CALL-TEST: llamando a ViewService.OpenAsync desde hilo en segundo plano {Environment.CurrentManagedThreadId} (isIncoming={isIncoming})");
                    var windowContext = await service.OpenAsync(options);
                    if (windowContext == null)
                    {
                        Logger.Error("CALL-TEST: OpenAsync devolvió null WindowContext");
                    }
                    else
                    {
                        Logger.Info($"CALL-TEST: VoipWindow presentada con éxito vía ViewService.OpenAsync (WindowId={windowContext.Id})");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"CALL-TEST fallo: {ex}");
            }
        }
    }
}
