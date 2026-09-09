# u-079 — forum "General" opens empty: the card's evidence does not survive checking

*Oscar, 2026-09-03. Branch `port/u079-forum-general`, off `linux` = 624822e (cons-9 tip;
`ChatHistoryView.cs` is byte-identical between 239b1c2 and 624822e, so the base move is inert
for this change). Builds on Jim's live-verification handover
(`agents/jim-mthgxasm/u079-findings-handover.md`: 4 navigations into `Charla General` of forum
-1002346786357, every one painted). Every claim below is re-derived independently from the
archived artefacts rather than taken on trust.*

## Verdict

**No live defect at tip.** The mechanism the card describes is real, is the one already
documented and fixed by the `OnEmptyLayoutUpdated` region of `ChatHistoryView.cs`, and both
pillars of the card's evidence are measurement errors.

### Pillar 1 — "44 messages sit at negative y" is not a fault signature

A container scrolled above the viewport of an `ItemsStackPanel` sits at negative panel-relative
y by construction. Counted over every archived tree dump in the hive containing a
`ChatHistoryViewItem`:

| dumps with history items | ≥1 item at negative y | **every** item at negative y |
|---|---|---|
| 62 | **61** | 5 |

These are healthy, painted histories. Negative y is the normal geometry of virtualization, not
evidence of anything. (61 rather than 62 because the remaining dump's items all sit at y ≥ 0.)

### Pillar 2 — "self-retries forever" is the guard working, not looping

`grep "measured 0 px"` over every session log archived in the hive:

| logs containing the line | total occurrences | occurrences that are **not** `(1/32)` |
|---|---|---|
| 10 | **10** | **0** |

Exactly one firing per session, always the first retry, never a second. The panel fills on that
retry every time — ten independent sessions, several agents, several days. There is no spin.

*(Five identical `38 messages (1/32)` lines turn out to be five separate session logs of the same
chat reopened, not five consecutive passes: each file contains exactly one occurrence.)*

## What was actually left to do

The guard has one genuinely silent state: reaching `MaxEmptyLayouts`. It then stops retrying,
nothing else invalidates the measure, and the history stays empty for the life of the situation —
which is *precisely* the reported symptom. Until now that terminal state was indistinguishable in
a log from the healthy case: both emit the same `measured 0 px … (n/32)` line and the give-up
added nothing after it. That is why this card could be neither reproduced nor closed.

So the change is one thing: **when the cap is reached, say so — once per situation, with the
geometry that would identify the surviving variant.** `Logger.Error`, latched by
`_emptyLayoutsGaveUp`, cleared by the same re-arm that resets the counter. If that line ever
appears in a user log, u-079 is real and the numbers needed to diagnose it are right there; if it
never appears, the card is closed by evidence rather than by failure to reproduce.

It is diagnostics, not behaviour: no retry, threshold, or layout decision changes, and in a
healthy session (10 of 10 observed) the new code never executes at all.

## A trap in the inherited work-in-progress, removed

The worktree carried 58 uncommitted lines of instrumentation, never built. They logged on
`_emptyLayouts == 1` — the pass that, per the table above, succeeds in 10 sessions out of 10 —
and read `panel.FirstCacheIndex` / `panel.LastCacheIndex` **outside** their own try/catch.

`ItemsStackPanel.FirstCacheIndex`/`LastCacheIndex` are a bare `throw` in the deployed Uno
(PORTING.md §6; `ProfilePage.xaml.cs:1146` exists solely to work around that same getter). That
block would therefore have raised `NotImplementedException` out of a `LayoutUpdated` handler on
the first pass of every empty-history situation — taking the layout pass down with it, which its
own comment correctly said must never happen. Strictly worse than the bug it was measuring.

Reverted. The replacement reads `FirstVisibleIndex`/`LastVisibleIndex` only — `[NotImplemented]`
by attribute but with real bodies that answer correctly, which is why the prepend anchor in this
same file already relies on them.

## Recommendation

Close u-079 as **fixed-at-tip by the existing guard**, with the give-up reporter landing as the
falsifiability this card lacked. Jim's residual caveat stands and is worth keeping: the original
failure was timing-dependent (a batch arriving while content < viewport), so ten sessions is
strong but not exhaustive — which is exactly what the new log line is for.
