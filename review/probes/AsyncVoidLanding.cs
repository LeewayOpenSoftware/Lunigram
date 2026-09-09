// u-091 — where does a UI-thread `async void` throw actually land on this head?
//
// RUN (needs nothing but the .NET 10 SDK that already builds the app):
//
//     dotnet run review/probes/AsyncVoidLanding.cs
//
// WHY THIS PROBE EXISTS. The T2 parcel wraps five naked `async void` bodies in try/catch-log, and
// the size of the net underneath them decides whether that is cosmetic or load-bearing. The
// answer was assumed twice and measured once:
//
//   - u-090's T1 probe measured a plain console app and reported PROCESS-FATAL (SIGABRT/134).
//     That is case A below, and it is NOT what the app does: a console app has no
//     SynchronizationContext, so .NET's AsyncMethodBuilderCore.ThrowAsync falls through to
//     ThreadPool.QueueUserWorkItem and the throw is genuinely unhandled.
//   - Inside Uno the UI thread always HAS a context. Microsoft.UI.Xaml.Application.StartPartial
//     (Uno.UI.dll 6.6.184) runs
//         SynchronizationContext.SetSynchronizationContext(NativeDispatcher.Main.SynchronizationContext);
//     once at startup, so every async void started on the UI thread captures it. The rethrow
//     therefore goes through
//         NativeDispatcherSynchronizationContext.Post -> _dispatcher.Enqueue(() => d(state))
//     and every dequeued action is run by NativeDispatcher.RunAction, which is:
//         try { using (dispatcher.SynchronizationContext.Apply()) { action(); return; } }
//         catch (Exception ex) { dispatcher.Log().Error("NativeDispatcher unhandled exception", ex); return; }
//
// So the real landing site is neither the AppDomain nor Application.UnhandledException: it is a
// catch inside Uno's dispatch loop that writes ONE anonymous line through Uno's own logger and
// carries on. Case B below reproduces that shape exactly and case C shows what the parcel buys.
//
// WHAT THIS PROBE DOES AND DOES NOT PROVE. It runs the real .NET async-void machinery against a
// SynchronizationContext whose Post/run loop is a faithful transcription of the two Uno methods
// quoted above; the Uno half is pinned by decompiling the deployed assemblies, not by guessing.
// It does not drive a live Uno window - confirming it in the app means running the daily build
// with UNIGRAM_UNO_LOG=Error, throwing from one of the five handlers, and seeing exactly one
// "NativeDispatcher unhandled exception" line with the process still alive.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// A faithful transcription of Uno's NativeDispatcherSynchronizationContext + NativeDispatcher.
sealed class UnoLikeContext : SynchronizationContext
{
    private readonly Queue<Action> _queue = new();
    public readonly List<string> Log = new();

    public override void Post(SendOrPostCallback d, object? state)
        => _queue.Enqueue(() => d(state));            // NativeDispatcherSynchronizationContext.Post

    public void Pump()                                 // NativeDispatcher.RunAction
    {
        while (_queue.Count > 0)
        {
            var action = _queue.Dequeue();
            try
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { action(); } finally { SetSynchronizationContext(previous); }
            }
            catch (Exception ex)
            {
                Log.Add("NativeDispatcher unhandled exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}

static class Probe
{
    static bool _bodyFinished;

    // The shape of all five T2 sites before the parcel: naked async void, throw after the await.
    static async void NakedHandler()
    {
        await Task.Yield();
        throw new InvalidOperationException("the handler died here");
        #pragma warning disable CS0162
        _bodyFinished = true;                          // never reached - the point of the parcel
        #pragma warning restore CS0162
    }

    // The shape after the parcel.
    static async void WrappedHandler(List<string> appLog)
    {
        try
        {
            await Task.Yield();
            throw new InvalidOperationException("the handler died here");
        }
        catch (Exception ex)
        {
            appLog.Add("Logger.Error(" + ex.GetType().Name + ": " + ex.Message + ") from WrappedHandler");
        }
    }

    static int Main()
    {
        var caseA = Environment.GetEnvironmentVariable("U091_CASE_A") == "1";

        if (caseA)
        {
            // CASE A - no SynchronizationContext, i.e. u-090's console repro and NOT the app.
            // ThrowAsync falls to the thread pool: this process is expected to DIE here.
            Console.WriteLine("A: no SynchronizationContext; expecting the process to be killed...");
            NakedHandler();
            Thread.Sleep(2000);
            Console.WriteLine("A: STILL ALIVE - that would contradict the documented .NET behaviour");
            return 1;
        }

        var ctx = new UnoLikeContext();
        SynchronizationContext.SetSynchronizationContext(ctx);

        // CASE B - the app's real shape: naked async void on a thread that has Uno's context.
        Console.WriteLine("B: naked async void under an Uno-shaped SynchronizationContext");
        NakedHandler();
        ctx.Pump();
        Console.WriteLine("   process alive:            True");
        Console.WriteLine("   handler body finished:    " + _bodyFinished);
        Console.WriteLine("   dispatcher lines:         " + ctx.Log.Count);
        foreach (var line in ctx.Log) Console.WriteLine("     " + line);

        // CASE C - the same handler after the parcel.
        var appLog = new List<string>();
        ctx.Log.Clear();
        Console.WriteLine("C: the same handler wrapped in try/catch-log");
        WrappedHandler(appLog);
        ctx.Pump();
        Console.WriteLine("   dispatcher lines:         " + ctx.Log.Count);
        Console.WriteLine("   app log lines:            " + appLog.Count);
        foreach (var line in appLog) Console.WriteLine("     " + line);

        var ok = !_bodyFinished && appLog.Count == 1 && ctx.Log.Count == 0;
        Console.WriteLine(ok
            ? "\nVERDICT: swallowed by the dispatcher and anonymous when naked; named and owned by the app when wrapped."
            : "\nVERDICT: UNEXPECTED - re-read the cases above.");
        return ok ? 0 : 1;
    }
}
