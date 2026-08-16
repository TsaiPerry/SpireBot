# Paused action preview

**Date:** 2026-08-07
**Status:** approved, not yet implemented
**Builds on:** `2026-08-07-bot-speed-control-design.md` (the pause/step control this extends)

While the bot is paused, compute the action it *would* take and show it in the overlay, then have
Step execute exactly that action.

## Motivation

The speed-control feature added pause and step, but pause holds the loop *before* it decides — so a
paused bot has computed nothing and there is nothing to show. Step is therefore blind: you press it
and find out what the bot wanted only after it has already acted. For a showcase, and for judging
whether a checkpoint is playing sensibly, seeing the intended action *before* it happens is the
whole point.

## The central constraint: what you see must be what happens

A preview that can differ from the executed action is worse than no preview. Two things would make
them diverge if the preview were computed independently:

- `SpireBotConfig.Temperature > 0` makes the policy sample rather than take the argmax, so
  re-deciding draws a different action.
- The stuck guard, auto-advance, and mapping-gap fallback paths can override the policy's choice
  entirely, and a naive preview would not model them.

So the preview is not a separate computation. **The preview *is* the real decision, held at the last
moment before it executes**, and Step commits that exact held command.

## Where the hold goes

`ActionExecutor.Execute(cmd, kind)` (`ActionExecutor.cs:73`) is the single execution choke point.
Every path that acts goes through it, and it is also where the three per-room memories mutate
(`RoomOneShots.Record`, `RewardClaimMemory.Record`, `ScreenExitMemory.Record`). Holding at that
boundary therefore suppresses the side effects for free.

`BotController` calls it from **six** sites:

| Line | Path |
|---|---|
| 563 | `TryAutoAdvance` — pending card claim |
| 596 | `TryAutoAdvance` — interstitial advance (event proceed, open shop, proceed-to-map, next act) |
| 648 | `DispatchWithFallback` — mapping-gap scripted fallback |
| 671 | `DispatchWithFallback` — normal policy dispatch |
| 721 | `HandleUnsupported` — Crystal Sphere random valid click |
| 730 | `HandleUnsupported` — first available command |

All six route through a new private `BotController.Dispatch(...)` instead. `Dispatch` either
executes (normal) or caches (previewing). `ActionExecutor` itself is unchanged — it stays a dumb
utility, and a reader at any call site can see that the hold exists.

**One selection-time side effect sits *upstream* of the choke point and needs its own treatment:**
the card-claim branch of `TryAutoAdvance` calls `RewardClaimMemory.TryBeginAutoAdvance(claim)` as
its *condition* (`BotController.cs:560`), and that call mutates state before anything executes —
it increments the loop-backstop counter (`MaxAutoAdvancesBeforeForcedDecline = 3`) and records the
open reward index. Previewing that branch naively would burn claim attempts the bot never made:
three aborted previews (pause → preview → unpause, repeated) would force-decline a card reward.

Fix: `RewardClaimMemory` gains a **read-only probe** `CanAutoAdvance(claim)` (the same count
comparison, no mutation). While previewing, the branch selects using the probe; the real
`TryBeginAutoAdvance` is deferred to commit time via a `CommitGate` on the held action (Component
2). The probe and the deferred call provably agree: the counter only mutates on commits and on
unpaused dispatches, neither of which can interleave between a preview and its own commit. The
gate's return is still honored defensively — if it ever refuses, the commit is discarded and
logged instead of executing, which matches the unpaused forced-decline path (which also does not
execute the claim). All five other sites have pure selection (verified: shop-exit checks and
policy inference read state; `StallDiagnostics.ReportIfChanged` and the log-once latches are
diagnostics only) and pass no gate.

## Design

### Component 1 — preview mode in `BotController`

`OnInputRequired` no longer returns early when gated. It proceeds into `RunDecision()` with a
private `_previewing` flag set:

```csharp
private static void OnInputRequired()
{
    if (!_running) return;
    if (ReplayEngine.HasPendingCommand) return;

    _previewing = IsGated();   // paused with no step armed: decide, but hold the result
    _stepOnce = false;

    RunDecision();
}
```

Note the gate no longer returns — it *classifies*. When not gated this is exactly today's behavior.

Two ordering requirements in that snippet:

- `_previewing` must be computed **before** `_stepOnce` is cleared, since `IsGated()` reads
  `_stepOnce`.
- The `HasPendingCommand` guard must stay **above** the `_stepOnce = false` line, preserving the
  invariant established when the speed control shipped: a step armed while a command is still in
  flight must survive to the next cycle rather than being silently spent.

### Component 1a — recompute only when the state changes

The heartbeat fires every second while paused, but re-running ONNX inference every second to
produce an identical answer is waste. The loop therefore short-circuits when nothing
decision-relevant has changed since the held action was computed.

`BuildDecisionKey(ctx)` (`BotController.cs:772`) already exists for precisely this judgement —
`AdvanceStuckGuard` uses it to decide whether a decision "recurred with no observed state change".
Reusing it keeps a single definition of that concept rather than inventing a second one:

```csharp
ctx = DecisionContext.Capture(_session);
RoomOneShots.OnDecision(ctx);        // idempotent room-boundary resets; safe to re-run
RewardClaimMemory.OnDecision(ctx);

// Preview refresh gate: keep the held action when the situation is unchanged.
if (_previewing && _pending != null && BuildDecisionKey(ctx) == _pending.Key)
    return;

map = ActionMap.Build(_contract!, ctx, _session);
...
```

Placed here it skips all three expensive steps — `ActionMap.Build`, `ObsBuilder.Build`, and the
policy's ONNX inference — leaving only `DecisionContext.Capture` and a string comparison per
heartbeat. Both `OnDecision` calls are idempotent room-boundary resets (they no-op unless the room
key changed), so running them above the gate is safe.

**The key must be stored on `_pending`, not compared against `_lastDecisionKey`.**
`AdvanceStuckGuard` owns `_lastDecisionKey` and does not run while previewing, so that field goes
stale during a pause; reading it here would compare against whatever the last *executed* decision
was.

**Known bound on precision.** `BuildDecisionKey` covers kind, room, act, floor, current HP, gold,
and the `ToString()` of every available command — which for a card play is its hand *index* and
target, not the card's identity. It does not cover enemy HP, block, energy, or powers. In practice
this is sufficient, because a paused bot is not acting and the game is idle waiting for input, and
because `_pending` is cleared on every commit so each Step forces a full recompute regardless. If a
stale preview is ever observed in play, the fix is to widen the key — but note that doing so also
changes stuck-guard sensitivity, so it needs its own thought rather than a quick edit.

Each actual recompute logs one line (`preview refreshed: <action>`), which is what makes
"inference is not running every second" observable during verification.

### Suppressions

**Three suppressions on the full-recompute path while `_previewing`.** These are needed because a
preview is a decision that never executes, and both the stuck guard and the dumper assume every
decision they see is one the bot acted on:

1. **`AdvanceStuckGuard`** — skipped, and **not deferred to commit either**. It counts identical
   consecutive decisions against `_lastDecisionKey`, which tracks the last *executed* decision;
   folding previews into that counter corrupts its accounting relative to what the bot actually
   did, and can push `_repeatCount` toward its threshold without a single real repeat.

   Deferring it to commit time is *also* wrong, and this is subtle: `AdvanceStuckGuard` returning
   false makes `RunDecision` call `DispatchWithFallback(..., forceFallback: true)`, which chooses a
   **different** command than the one held. Running it at commit could therefore execute something
   other than what was previewed — the exact guarantee this feature exists to provide. So it simply
   does not run on the preview/step path at all. The cost is that manual stepping through a
   genuinely stuck state will not auto-recover; that is acceptable, because a human is watching and
   stepping deliberately, which is the situation the automatic guard exists to substitute for.
   Normal (unpaused) play is unaffected — the guard runs exactly as it does today.
2. **`DecisionDumper.Write`** — skipped while previewing, or the dump file gains a line for every
   action merely *considered*. This one **is** deferred to commit, so dumps continue to record
   exactly the actions actually taken, which is what makes them usable for offline analysis.
   It is skipped when the held action has no `PolicyResult` (the auto-advance and
   `HandleUnsupported` paths), matching today's behavior where those paths never dump either.
3. **`ActionExecutor.Execute`** — replaced by the cache (see Component 2). This also spares the
   three memory mutations listed above, since they live inside `Execute`.

`DecisionMade` still fires while previewing, so the overlay's top-K rows populate normally.

### Component 2 — the held command

```csharp
private sealed record PendingAction(
    ReplayCommand Cmd, DecisionKind Kind,
    DecisionContext Ctx, ActionMap? Map, ObsResult? Obs, PolicyResult? Result,
    string Key,               // BuildDecisionKey(Ctx) at compute time — see Component 1a
    Func<bool>? CommitGate);  // deferred selection side effect (card-claim TryBeginAutoAdvance)

private static PendingAction? _pending;
public static bool HasPendingPreview => _pending != null;
public static string? PendingPreviewDescription => _pending?.Cmd.Describe();
```

`Result` is nullable because the auto-advance and `HandleUnsupported` paths select a command
without a `PolicyResult`. `Map` is nullable because `TryAutoAdvance(ctx)` has no `ActionMap` in
scope; the deferred dump requires `Result` (and therefore `Map`/`Obs`) to be non-null, which the
normal dispatch path always supplies.

Two distinct entry points, so neither has to branch on how it was reached:

- **`Dispatch(cmd, kind, ctx, map, obs, result)`** — called from all six sites. When `_previewing`,
  overwrite `_pending` and return. Otherwise call `ActionExecutor.Execute(cmd, kind)` directly:
  this is today's path, where `AdvanceStuckGuard` and `DecisionDumper.Write` have already run
  inline in `RunDecision`.
- **`Commit(PendingAction held)`** — called only from `StepOnce`. Runs `held.CommitGate` first
  (discarding the commit with a log line if it refuses — defensive; see "Where the hold goes"),
  then writes the deferred dump (when `held.Result` is non-null), then
  `ActionExecutor.Execute(held.Cmd, held.Kind)`. It does **not** run `AdvanceStuckGuard`, for the
  reason given above.

The cache is refreshed only when `BuildDecisionKey` changes (Component 1a), so a held action
persists across an idle pause instead of being recomputed each second. It is cleared on unpause, on
`StartBotRun`, on `Stop`, and on every commit.

### Component 3 — Step commits the held action

```csharp
public static void StepOnce()
{
    _paused = true;
    if (_pending != null) { Commit(_pending); _pending = null; return; }
    _stepOnce = true;   // nothing held yet — fall back to today's "arm one decision"
}
```

The fallback matters: if Step is pressed before the first heartbeat has produced a preview, the
button must still do something. It is also the path taken on any screen where the loop declined to
select an action at all.

After a commit the bot stays paused, and the next heartbeat (once the command clears
`HasPendingCommand`) computes the next preview.

### Component 4 — overlay

`ThinkingOverlay` renders the pending action from `BotController.PendingPreviewDescription` in its
`_Process`, not from the `DecisionMade` event:

```csharp
string? preview = BotController.PendingPreviewDescription;
if (preview != null)
    _actionLabel.Text = $"Will take: {preview}";
```

**Why not relabel inside `ApplyDecision`** (the obvious first idea): `HandleUnsupported` never fires
`DecisionMade`, so two of the six paths would cache a preview the overlay never showed — it would
sit displaying the *previous* decision's "Chosen:" line while a different action waited to run.
Sourcing the text from the cache instead covers all six paths uniformly.

When no preview is pending, `_actionLabel` keeps whatever `ApplyDecision` last wrote, so a
non-paused run looks exactly as it does today. `_Process` runs after any deferred `ApplyDecision`,
so while a preview is pending it wins — which is the intent.

The top-K rows continue to come from `DecisionMade` and are simply absent on the two
`HandleUnsupported` paths, which have no policy distribution to show.

## Data flow

```
paused, heartbeat fires, nothing has changed
  -> OnInputRequired: _previewing = IsGated() = true
  -> RunDecision: Capture -> key == _pending.Key -> RETURN
       (no ActionMap.Build, no ObsBuilder.Build, no ONNX inference)

paused, heartbeat fires, state HAS changed (or no preview held yet)
  -> OnInputRequired: _previewing = IsGated() = true
  -> RunDecision: Capture -> key differs -> Build -> Obs -> (stuck guard SKIPPED)
       -> policy chooses -> DecisionMade fires (top-K renders)
       -> Dispatch(...) sees _previewing -> _pending = {cmd, ..., key}; NOTHING executes
  -> _Process: _actionLabel = "Will take: Play Strike -> Slime"

user clicks Step
  -> StepOnce: _pending != null -> Commit
       -> DecisionDumper.Write (deferred; skipped if Result is null)
       -> ActionExecutor.Execute  (memories mutate here, as normal)
       -> _pending = null
     (AdvanceStuckGuard deliberately does NOT run — it could force a different command)
  -> still paused; next heartbeat previews the following action

user clicks Play
  -> Paused = false -> _pending = null (discarded)
  -> next cycle decides and executes normally
```

## Error handling

- A throw inside the preview cycle is already covered: `RunDecision`'s existing try/catch around
  capture/build logs and returns, leaving `_pending` null — Step then falls back to arming one
  decision.
- `_pending` holding a command that has since become undispatchable is not a new failure mode: the
  vendored dispatcher's retry/watchdog path and `StuckRecovery.DropStaleCommands` already handle a
  queued command that cannot run, exactly as for a non-previewed dispatch.
- Unpausing always discards `_pending` rather than executing it, so a preview can never fire
  unattended after the user resumes.

## Verification

`BotControlSelfTest` can assert only the pure part — that setting `Paused = false` clears both
`_stepOnce` and `_pending` — via a small testable seam. Everything else is integration and is
verified in game:

1. `dotnet build` — 0 errors; warning count unchanged (1 pre-existing `CS8602` in vendored
   `ReplayDispatcher.cs`).
2. In game (launch via Steam):
   - Pause mid-run. Within ~1 s the overlay shows `Will take: ...` and the bot does **not** act.
   - Wait 30 s paused: no dump lines accumulate, no stuck-recovery/forced-fallback fires, and —
     with the game idle — exactly **one** `preview refreshed:` line appears for the whole pause,
     not one per second. That line is the evidence inference is state-driven, not timer-driven.
   - Press Step: the bot takes **exactly** the previewed action, then re-pauses and previews the
     next one.
   - Set `Temperature` above 0 and repeat — the executed action must still match the preview
     shown, which is the case this design exists for.
   - Press Play instead of Step: the held action is discarded, not replayed, and the run continues
     normally.
   - Confirm a non-paused run's overlay is unchanged from today.

## Out of scope

- **Previewing more than one action ahead.** Would require speculative execution of game state.
- **Editing or overriding the previewed action** before stepping. Read-only preview only.
- **A preview when the bot is not paused.** The label only changes while an action is held.
