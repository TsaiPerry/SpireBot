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

All six route through a new private `BotController.Dispatch(cmd, kind)` instead. `Dispatch` either
executes (normal) or caches (previewing). `ActionExecutor` itself is unchanged — it stays a dumb
utility, and a reader at any call site can see that the hold exists.

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

**Three suppressions while `_previewing`**, all required because the loop now re-runs on every
heartbeat (1 s) while paused rather than not running at all:

1. **`AdvanceStuckGuard`** — skipped, and **not deferred to commit either**. It counts identical
   consecutive decisions with no state change, which is precisely what a pause produces; left
   running it would hit its repeat threshold within a few seconds of pausing and force a fallback
   dispatch.

   Deferring it to commit time is *also* wrong, and this is subtle: `AdvanceStuckGuard` returning
   false makes `RunDecision` call `DispatchWithFallback(..., forceFallback: true)`, which chooses a
   **different** command than the one held. Running it at commit could therefore execute something
   other than what was previewed — the exact guarantee this feature exists to provide. So it simply
   does not run on the preview/step path at all. The cost is that manual stepping through a
   genuinely stuck state will not auto-recover; that is acceptable, because a human is watching and
   stepping deliberately, which is the situation the automatic guard exists to substitute for.
   Normal (unpaused) play is unaffected — the guard runs exactly as it does today.
2. **`DecisionDumper.Write`** — skipped while previewing, or a paused run writes one dump line per
   second. This one **is** deferred to commit, so dumps continue to record actions actually taken.
   It is skipped when the held action has no `PolicyResult` (the auto-advance and
   `HandleUnsupported` paths), matching today's behavior where those paths never dump either.
3. **`ActionExecutor.Execute`** — replaced by the cache (see Component 2). This also spares the
   three memory mutations listed above, since they live inside `Execute`.

`DecisionMade` still fires while previewing, so the overlay's top-K rows populate normally.

### Component 2 — the held command

```csharp
private sealed record PendingAction(
    ReplayCommand Cmd, DecisionKind Kind,
    DecisionContext Ctx, ActionMap Map, ObsResult? Obs, PolicyResult? Result);

private static PendingAction? _pending;
public static bool HasPendingPreview => _pending != null;
public static string? PendingPreviewDescription => _pending?.Cmd.Describe();
```

`Result` is nullable because the auto-advance and `HandleUnsupported` paths select a command
without a `PolicyResult`.

Two distinct entry points, so neither has to branch on how it was reached:

- **`Dispatch(cmd, kind, ctx, map, obs, result)`** — called from all six sites. When `_previewing`,
  overwrite `_pending` and return. Otherwise call `ActionExecutor.Execute(cmd, kind)` directly:
  this is today's path, where `AdvanceStuckGuard` and `DecisionDumper.Write` have already run
  inline in `RunDecision`.
- **`Commit(PendingAction held)`** — called only from `StepOnce`. Writes the deferred dump (when
  `held.Result` is non-null), then `ActionExecutor.Execute(held.Cmd, held.Kind)`. It does **not**
  run `AdvanceStuckGuard`, for the reason given above.

The cache is **overwritten every heartbeat** while paused, so it is at most ~1 s stale. It is
cleared on unpause, on `StartBotRun`, and on `Stop`.

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
paused, heartbeat fires
  -> OnInputRequired: _previewing = IsGated() = true
  -> RunDecision: Capture -> Build -> Obs -> (stuck guard SKIPPED)
       -> policy chooses -> DecisionMade fires (top-K renders)
       -> Dispatch(...) sees _previewing -> _pending = {cmd, ...}; NOTHING executes
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
   - Wait 30 s paused: no dump lines accumulate, no stuck-recovery/forced-fallback fires, and the
     preview refreshes rather than freezing.
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
