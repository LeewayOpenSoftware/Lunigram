# u-091 — T2 naked `async void` bodies, and where their throw actually lands

*Oscar, 2026-09-03. Branch `port/u091-async-void` off `linux` = 624822e. Sites from
`unigram-linux/review/ASYNC-VOID-SWEEP.md` (T2). Probe: `review/probes/AsyncVoidLanding.cs`.*

## The probe the card asked for: XAML handler, AppDomain, or neither?

**Neither. On this head a UI-thread `async void` throw is swallowed by Uno's dispatch loop and
reduced to one anonymous log line. It is not `Application.UnhandledException` and it is not
process-fatal.**

The chain, pinned by decompiling the deployed Uno 6.6.184 and then reproduced at runtime:

1. `Microsoft.UI.Xaml.Application.StartPartial` (Uno.UI.dll) runs, once, at startup:
   `SynchronizationContext.SetSynchronizationContext(NativeDispatcher.Main.SynchronizationContext)`.
   So the UI thread always has Uno's context, and every `async void` started on it captures one.
2. The throw goes to `AsyncVoidMethodBuilder.SetException` → `ThrowAsync(ex, capturedContext)` →
   `context.Post(...)`.
3. `NativeDispatcherSynchronizationContext.Post` is `_dispatcher.Enqueue(() => d(state))` — no
   handling of its own.
4. Every dequeued action is run by `NativeDispatcher.RunAction`:
   ```csharp
   try { using (dispatcher.SynchronizationContext.Apply()) { action(); return; } }
   catch (Exception ex) { dispatcher.Log().Error("NativeDispatcher unhandled exception", ex); return; }
   ```

`review/probes/AsyncVoidLanding.cs` runs the real .NET async-void machinery against a faithful
transcription of steps 3–4. Measured, all three cases:

| case | context | outcome |
|---|---|---|
| **A** no `SynchronizationContext` | none | **process-fatal**, exit **134** (SIGABRT); stack is `Task.ThrowAsync` → `ThreadPoolWorkQueue.Dispatch` |
| **B** Uno-shaped context, naked handler | Uno's | process **alive**; handler body **truncated**; **1** anonymous `NativeDispatcher unhandled exception` line |
| **C** Uno-shaped context, wrapped handler | Uno's | **0** dispatcher lines; **1** app log line naming the exception and the handler |

### This corrects the figure u-090 reported, and the correction matters

u-090's T1 probe measured a plain console app and concluded **process-fatal (SIGABRT/134)**. That
is exactly case A — and case A is *not* the application: a console app has no
`SynchronizationContext`, so .NET falls through to `ThreadPool.QueueUserWorkItem` and the throw is
genuinely unhandled. Inside Uno the context always exists, so the app gets case B.

The practical consequence points the same way as u-090 but for a different reason. Nobody gets a
crash dump; they get a working-looking app with one silently dead surface and, at the default
`UNIGRAM_UNO_LOG=Warning`, a single console line that **names no handler** — and it goes to the
console, not to the app's own log, so a packaged run without a terminal loses even that. That is
why "wrap it and log it yourself" is load-bearing rather than cosmetic: it is the difference
between an anonymous dispatcher line and a named stack in the app's own log.

## The five sites

| # | Site | Shape | What a throw costs |
|---|---|---|---|
| 5 | `Telegram.Linux/Xaml/ChatView.Menu.Linux.cs` `ShowMessageMenuLinux` | split | right click dies with nothing on screen — the daily path, §6 throughout |
| 6 | `Telegram/Views/MainPage.xaml.cs` `OnLoaded` | split | folders/tabs/header/session updates silently lost (**has already happened once**) |
| 7a | `Telegram/Controls/Drawers/StickerDrawer.xaml.cs` `_typing.Invoked` | inline | sticker search-as-you-type dead for the panel's session |
| 7b | `Telegram/Views/Popups/ContactsPopup.xaml.cs` `debouncer.Invoked` | inline | contact search dead for the popup's life |
| 8 | `Telegram/Views/Popups/CalendarPopup.xaml.cs` `InitializeCalendar` | inline + **latch release** | calendar stops filling **permanently** — see below |

**Split** = the body is unchanged and becomes `private async Task …Core(…)`; the `async void` the
XAML/caller binds to shrinks to an awaiting try/catch. Chosen for the two long bodies
(`ShowMessageMenuLinux` is 229 lines) so the diff is the wrapper rather than a re-indentation of
everything, which also keeps these two files cheap for god to consolidate.

### CalendarPopup needed more than a log, and the shape of the fix is not the obvious one

`_loadingMessages` is a **latch**: the method returns early while it is set, and the only place
that clears it sits near the end of the body. A throw in between leaves it set for the life of the
popup, so every later call returns immediately and the calendar never fills again — a permanent
failure, not a missed refresh. The catch therefore releases the latch as well as logging.

It is released **in `catch`, not in a `finally`**, and that is deliberate. The success path clears
the latch *before* its recursive tail call, and that call is itself `async void`, so it returns to
us at its first `await` while its own pass is still in flight. A `finally` would then run and
write `false` over the `true` that the in-flight pass had just set, re-opening the guard that pass
depends on. Only the failing path needs the release, and only the failing path gets it.

## Gates

- **Red probe, all five sites.** A deliberate `CS0103` inside each new `try` block; the build named
  all five (`ChatView.Menu.Linux.cs:263`, `MainPage.xaml.cs:1306`, `StickerDrawer.xaml.cs:102`,
  `ContactsPopup.xaml.cs:48`, `CalendarPopup.xaml.cs:129`), proving every wrap is genuinely
  compiled on this head rather than sitting in a dead `#if` branch. Removed, then built green.
- **Build** via `uno-build-lock.sh`: see the commit message for the final figures.
- `check-viewmodel-wiring.py`, `check-default-style-keys.py`, `check-porting-traps.py`.

## What this does not do

It does not make a failed handler succeed — a right click that throws still shows no menu. It
stops the failure being invisible, and stops the CalendarPopup one being permanent. The remaining
T3 family is a by-rule job for the u-088 gate, not parcels; this probe sizes the net under it:
**there is no net, only a swallow**, so rule (b) of the AsyncVoidGateRule (`try/catch-log` or
demonstrably `SendAsync`-only) is the whole of the protection.
