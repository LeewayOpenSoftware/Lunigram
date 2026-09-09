//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// EL HUECO QUE ESTA CLASE TAPA: en Uno, `FindName` NO es lo que es en WinUI.
//
// WinUI resuelve un elemento `x:Load="False"` por el codigo generado, asi que FindName lo
// materializa se llame cuando se llame. Uno lo resuelve RECORRIENDO EL ARBOL VISUAL
// (`IFrameworkElementHelper.FindName`, descompilado del Uno.UI 6.6.184 que enviamos: baja por
// `FindLastChild` y solo entonces llama a `ConvertFromStubToElement`). Por tanto solo encuentra un
// stub QUE YA SEA ALCANZABLE, y cuando no lo es **devuelve null y no lanza nada**.
//
// Un elemento no es alcanzable mientras el trozo de arbol que lo contiene no se haya realizado, y
// eso pasa mas tarde de lo que parece:
//   * una plantilla de control se aplica durante el MEASURE, no al cargar -- esperar a `Loaded` NO
//     basta, y esta medido: la parcela 3 lo intento primero asi y la pantalla no cambio, porque
//     `IsLoaded` YA es true cuando la navegacion llama al delegado;
//   * los items de un ItemsControl (la cabecera de un ListView, por ejemplo) se realizan aun
//     despues;
//   * y un subarbol COLAPSADO no se mide en absoluto, asi que su contenido nunca llega a existir
//     (el caso del banner de MasterDetailView, cuyo ContentControl arranca en Collapsed).
//
// El resultado, en un puerto, es la peor clase de defecto: `FindName` devuelve null, el codigo de
// arriba desreferencia y el manejador se aborta, o no desreferencia y la tarjeta simplemente no
// sale nunca. Ninguna de las dos cosas la ve una compilacion. Encontrado conduciendo la app en la
// parcela 3 (UserEditPage se quedo sin apellido y sin foto) y luego barrido en frio por Phyllis:
// seis elementos vivos y ya enviados.
//
// LA PUERTA: se pide el elemento por nombre y se da el trabajo que depende de el. Si ya es
// alcanzable, corre en el acto -- que es lo que pasa siempre una vez la pagina esta asentada, y por
// eso esto no cuesta nada en el caso normal. Si no, se reintenta en cada PASADA DE LAYOUT
// completada, que es el primer momento capaz de satisfacer el recorrido, hasta un tope.
//
// Y si se agota el tope, se ESCRIBE EN EL LOG. Es la mitad que importa: el defecto original no era
// que la tarjeta no saliera, era que no salia EN SILENCIO. Un aviso con el nombre del elemento
// convierte «el banner no aparece» en una linea que se puede buscar.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace Telegram.Common
{
    public static class XLoadGate
    {
        // Doce pasadas es holgado a proposito: en la unica medida que hay (UserEditPage, parcela 3)
        // basto UNA. El tope no esta para dar tiempo a un arbol lento, sino para no quedarse
        // suscrito a LayoutUpdated -- que dispara en cada pasada de toda la ventana -- cuando el
        // subarbol esta colapsado y el elemento no va a existir por mucho que se espere.
        private const int MaxPasses = 12;

        private sealed class Gate
        {
            public readonly List<(string Name, Action Then)> Pending = new();
            public int Passes;
            public EventHandler<object> Handler;
        }

        // El Gate referencia a su propio owner (dentro del lambda del manejador). Una
        // ConditionalWeakTable es justamente la tabla que admite eso sin fugar: la pareja
        // clave/valor se recoge junta.
        private static readonly ConditionalWeakTable<FrameworkElement, Gate> _gates = new();

        /// <summary>
        /// Resolves an <c>x:Load</c> element by name and runs <paramref name="then"/> once it
        /// actually exists -- immediately if it is already reachable, otherwise on the first
        /// completed layout pass that makes it reachable.
        /// </summary>
        public static void Materialize(FrameworkElement owner, string name, Action then = null)
        {
            if (owner == null || string.IsNullOrEmpty(name))
            {
                return;
            }

            if (owner.FindName(name) != null)
            {
                then?.Invoke();
                return;
            }

            var gate = _gates.GetValue(owner, static _ => new Gate());

            // Gana el ultimo: dos avisos sobre la misma tarjeta antes de que corra una pasada
            // deben dejar el estado NUEVO, no aplicar los dos en orden.
            gate.Pending.RemoveAll(x => x.Name == name);
            gate.Pending.Add((name, then));

            if (gate.Handler == null)
            {
                gate.Passes = 0;
                gate.Handler = (s, e) => OnLayoutUpdated(owner, gate);

                owner.LayoutUpdated += gate.Handler;
            }
        }

        private static void OnLayoutUpdated(FrameworkElement owner, Gate gate)
        {
            var passes = ++gate.Passes;

            List<Action> ready = null;

            for (int i = gate.Pending.Count - 1; i >= 0; i--)
            {
                var pending = gate.Pending[i];
                if (owner.FindName(pending.Name) != null)
                {
                    gate.Pending.RemoveAt(i);

                    if (pending.Then != null)
                    {
                        (ready ??= new List<Action>()).Add(pending.Then);
                    }
                }
            }

            if (gate.Pending.Count == 0 || passes >= MaxPasses)
            {
                owner.LayoutUpdated -= gate.Handler;
                gate.Handler = null;

                foreach (var (name, _) in gate.Pending)
                {
                    Logger.Warning($"x:Load gate: \"{name}\" did not materialise on {owner.GetType().Name} after {passes} layout pass(es) -- its subtree is most likely collapsed. The next update for it will try again.");
                }

                gate.Pending.Clear();
            }

            // Lo ultimo, y fuera del bucle de arriba: `then` puede volver a llamar a Materialize.
            if (ready != null)
            {
                foreach (var action in ready)
                {
                    action();
                }
            }
        }
    }
}
