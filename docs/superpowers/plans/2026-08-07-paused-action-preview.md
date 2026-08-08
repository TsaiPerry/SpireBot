# Paused Action Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** While the bot is paused, compute the action it would take, show it in the overlay as `Will take: ...`, and have Step execute exactly that held action.

**Architecture:** The pause gate stops *returning early* and starts *classifying*: a gated cycle still runs the full decision path, but every execution site routes through a new `BotController.Dispatch(...)` which, while previewing, caches the chosen command instead of executing it. Step commits the cached command verbatim. A refresh gate keyed on the existing `BuildDecisionKey` skips `ActionMap.Build`/`ObsBuilder.Build`/ONNX inference when nothing decision-relevant changed, so a paused bot runs inference only on state changes, not per heartbeat.

**Tech Stack:** C# 12 / .NET (Godot 4 mod), records, ONNX Runtime (untouched — inference goes through the existing `OnnxPolicy`), BaseLib config (untouched).

**Spec:** `docs/superpowers/specs/2026-08-07-paused-action-preview-design.md` — read the "Where the hold goes" and Component 1a sections before Task 1; the reasoning there is load-bearing for several orderings below.

## Global Constraints

- **Branch and commits:** work on the existing `bot-speed-control` branch (this feature builds on the unmerged pause/step control). Per-task commits, conventional-commit style. Do **not** add a `Co-Authored-By` trailer.
- **Do not modify any file under `SpireBotCode/Replay/`** — vendored from the RunReplays mod. Calling into it is fine; editing it is not.
- **WYSIWYG guarantee (the feature's core invariant):** the executed action on Step must be the exact `ReplayCommand` instance that was previewed. Nothing on the commit path may re-select, re-sample, or override it. This is why `AdvanceStuckGuard` must not run on the preview or commit paths.
- **Inference only on state change:** while paused with a held preview, an unchanged `BuildDecisionKey` must skip `ActionMap.Build`, `ObsBuilder.Build`, and `IPolicy.Choose` entirely.
- **Build harness** (repo root `c:\Users\Perry\Desktop\SpireBot`): `dotnet build`. Expect `Build succeeded.`, `0 Error(s)`, and exactly **1** pre-existing warning (`CS8602` in vendored `Replay/ReplayDispatcher.cs`). The warning count must not increase. There is no C# test project; automated assertions live in the startup self-test (`BotControlSelfTest`, gated on `SpireBotConfig.SelfChecks`), which never throws.
- **Line numbers below are from the pre-change files** and will drift as edits land. Anchor on the quoted code, not the numbers.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `SpireBotCode/BotController.cs` | Modify | Preview state (`_pending`, `_previewing`), `Dispatch`/`Commit`, gate classification, refresh gate, six call-site reroutes, suppressions. |
| `SpireBotCode/RewardClaimMemory.cs` | Modify | New read-only probe `CanAutoAdvance(claim)` beside `TryBeginAutoAdvance`. |
| `SpireBotCode/BotControlSelfTest.cs` | Modify | Gate-state assertions (step arming, unpause discards). |
| `SpireBotCode/Overlay/ThinkingOverlay.cs` | Modify | Render `Will take: ...` from the preview cache in `_Process`. |

Task 1 is the whole decision-loop change (one coherent unit — splitting it would leave an intermediate `Dispatch` with dead parameters). Task 2 is the overlay. Task 3 is manual in-game acceptance.

---

### Task 1: BotController preview core

**Files:**
- Modify: `SpireBotCode/BotController.cs` (fields ~60-91; `OnInputRequired` ~406-425; `RunDecision` ~469-521; `TryAutoAdvance` card-claim branch ~558-565 and interstitial dispatch ~594-597; `DispatchWithFallback` ~600-678; `HandleUnsupported` two dispatches ~716-732)
- Modify: `SpireBotCode/RewardClaimMemory.cs` (beside `TryBeginAutoAdvance`, ~line 104)
- Modify: `SpireBotCode/BotControlSelfTest.cs` (append assertions inside `Run()`'s try block)

**Interfaces:**
- Consumes (all existing): `IsGated()`, `_paused`/`_stepOnce`, `BuildDecisionKey(DecisionContext)`, `AdvanceStuckGuard(ctx, map)`, `ActionExecutor.Execute(ReplayCommand, DecisionKind)`, `DecisionDumper.Write(ctx, obs, map, result)`, `ReplayEngine.HasPendingCommand`, `RewardClaimMemory.IsPendingCardClaim` / `TryBeginAutoAdvance(ClaimRewardCommand)`.
- Produces (Task 2 relies on these exact members):
  - `public static bool BotController.HasPendingPreview { get; }`
  - `public static string? BotController.PendingPreviewDescription { get; }`
  - `internal static bool BotController.StepArmed { get; }` (self-test seam)
  - `internal static bool RewardClaimMemory.CanAutoAdvance(ClaimRewardCommand claim)`

- [ ] **Step 1: Write the failing self-test assertions**

In `SpireBotCode/BotControlSelfTest.cs`, inside `Run()`'s `try` block, directly **before** the final `GD.Print($"[SpireBot] BotControlSelfTest OK ...` line, insert:

```csharp
            // Pause/step/preview gate state machine (paused-action-preview spec). Safe to
            // exercise at mod init: no run is active (BotController._running is false), so
            // arming a step here can never fire a decision, and StartBotRun resets Paused
            // anyway. Only the pure state transitions are asserted — the preview cache itself
            // can only fill during a live run, so here it must stay empty throughout.
            BotController.StepOnce();
            if (!BotController.Paused)
                throw new Exception("StepOnce() must imply Paused.");
            if (!BotController.StepArmed)
                throw new Exception("StepOnce() with nothing held must arm a step.");
            if (BotController.HasPendingPreview)
                throw new Exception("no preview can exist before any decision has run.");
            BotController.Paused = false;
            if (BotController.StepArmed)
                throw new Exception("unpausing must discard an armed step.");
            if (BotController.HasPendingPreview)
                throw new Exception("unpausing must leave no held preview.");
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build`

Expected: FAIL with `CS0117` errors — `'BotController' does not contain a definition for 'StepArmed'` and `'HasPendingPreview'`. (`StepOnce` and `Paused` already exist from the speed-control feature.) This failing build is the evidence the assertions are real; capture the error text.

- [ ] **Step 3: Add the preview state to BotController**

In `SpireBotCode/BotController.cs`, find:

```csharp
    /// <summary>The single definition of "the decision loop is currently held", used by both
    /// <see cref="OnHeartbeat"/> and <see cref="OnInputRequired"/> so the two can never disagree.</summary>
    private static bool IsGated() => _paused && !_stepOnce;
```

and insert directly after it:

```csharp
    // ── Paused action preview (paused-action-preview spec) ─────────────────────────────────
    //
    // While paused, the loop still decides, but the chosen command is HELD here instead of
    // executing; Step commits exactly this instance (the WYSIWYG guarantee — with Temperature
    // above 0 a re-decide would resample and could act differently than it showed). Refreshed
    // only when BuildDecisionKey changes (Component 1a); cleared on unpause (Paused setter),
    // hence also on StartBotRun/Stop which set Paused = false, and on every commit.
    //
    // Map is nullable because TryAutoAdvance has no ActionMap in scope; the deferred dump
    // needs Result non-null, and the only path that supplies Result supplies Map/Obs too.
    // CommitGate defers the card-claim branch's TryBeginAutoAdvance — the one selection-time
    // side effect that lives upstream of ActionExecutor.Execute (see the spec's "Where the
    // hold goes" for why previewing must not run it, and why probe and gate cannot disagree).
    private sealed record PendingAction(
        ReplayCommand Cmd, DecisionKind Kind,
        DecisionContext Ctx, ActionMap? Map, ObsResult? Obs, PolicyResult? Result,
        string Key, Func<bool>? CommitGate);

    private static PendingAction? _pending;

    /// <summary>True while the current RunDecision cycle is a preview — it must hold its
    /// chosen command rather than execute it. Recomputed at every OnInputRequired entry.</summary>
    private static bool _previewing;

    /// <summary>True while a previewed action is held awaiting Step.</summary>
    public static bool HasPendingPreview => _pending != null;

    /// <summary>The held action's description for the overlay, or null when nothing is held.</summary>
    public static string? PendingPreviewDescription => _pending?.Cmd.Describe();

    /// <summary>Self-test seam (BotControlSelfTest): whether a single-step is armed.</summary>
    internal static bool StepArmed => _stepOnce;
```

- [ ] **Step 4: Clear the preview on unpause; commit it on Step**

Replace the `Paused` setter body:

```csharp
        set
        {
            _paused = value;
            // Leaving pause discards an un-consumed step, so pausing again later doesn't hand out
            // a free decision the user never asked for.
            if (!value) _stepOnce = false;
        }
```

with:

```csharp
        set
        {
            _paused = value;
            if (!value)
            {
                // Leaving pause discards an un-consumed step, so pausing again later doesn't
                // hand out a free decision the user never asked for — and discards any held
                // preview, so it can never fire unattended after the user resumes.
                _stepOnce = false;
                _pending = null;
            }
        }
```

Replace `StepOnce()`:

```csharp
    /// <summary>Lets exactly one more decision through, then leaves the loop paused. Implies
    /// <see cref="Paused"/>, so it is also the "pause at the next decision" control.</summary>
    public static void StepOnce()
    {
        _paused = true;
        _stepOnce = true;
    }
```

with:

```csharp
    /// <summary>With a previewed action held: commit exactly that action, staying paused (the
    /// next heartbeat previews the following one). With nothing held (Step pressed before the
    /// first preview computed, or on a screen where the loop selected nothing): fall back to
    /// arming one full decision, today's pre-preview behavior — the button is never dead.
    /// Implies <see cref="Paused"/> either way.</summary>
    public static void StepOnce()
    {
        _paused = true;

        var held = _pending;
        if (held != null)
        {
            _pending = null;   // cleared BEFORE executing, so a throw can never double-commit
            Commit(held);
            return;
        }

        _stepOnce = true;
    }

    /// <summary>Executes a held preview: deferred commit gate, deferred decision dump, then the
    /// exact held command. Deliberately does NOT run <see cref="AdvanceStuckGuard"/> — its
    /// forced-fallback path chooses a different command than the one held, which would break
    /// the guarantee that Step performs what the overlay showed. A human stepping deliberately
    /// is the situation the automatic guard exists to substitute for.</summary>
    private static void Commit(PendingAction held)
    {
        // Defensive: the gate's counter only mutates on commits and unpaused dispatches, so it
        // cannot have changed since the probe selected this command — but honor a refusal
        // anyway, matching the unpaused forced-decline path (which also executes nothing).
        if (held.CommitGate != null && !held.CommitGate())
        {
            GD.PrintErr($"[SpireBot] BotController.Commit: commit gate refused " +
                        $"'{held.Cmd.Describe()}' — discarding the preview; the next cycle re-decides.");
            return;
        }

        // Deferred from the preview cycle so dumps record only actions actually taken.
        if (held.Result != null && held.Obs != null && held.Map != null)
            DecisionDumper.Write(held.Ctx, held.Obs, held.Map, held.Result);

        try
        {
            ActionExecutor.Execute(held.Cmd, held.Kind);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotController.Commit: ActionExecutor.Execute threw for " +
                        $"{held.Cmd.GetType().Name} — the run will wait for the next settle signal: {ex}");
        }
    }
```

- [ ] **Step 5: Add the Dispatch choke point**

Insert directly after `Commit` (from step 4):

```csharp
    /// <summary>
    /// The single seam between "the loop chose a command" and "the game performs it". All six
    /// execution sites route here. Previewing: hold the command (and log the refresh — the
    /// evidence, per the spec's verification section, that inference is state-driven rather
    /// than timer-driven). Normal: execute immediately, exactly as before this feature.
    /// </summary>
    private static void Dispatch(
        ReplayCommand cmd, DecisionKind kind, DecisionContext ctx,
        ActionMap? map, ObsResult? obs, PolicyResult? result, Func<bool>? commitGate = null)
    {
        if (_previewing)
        {
            _pending = new PendingAction(cmd, kind, ctx, map, obs, result,
                                         BuildDecisionKey(ctx), commitGate);
            GD.Print($"[SpireBot] BotController: preview refreshed: {cmd.Describe()}");
            return;
        }

        // Non-preview: selection-time side effects (TryBeginAutoAdvance) already ran inline at
        // the call site, and the dump already ran in DispatchWithFallback — commitGate unused.
        ActionExecutor.Execute(cmd, kind);
    }
```

- [ ] **Step 6: Make the gate classify instead of return**

Replace `OnInputRequired` in full:

```csharp
    private static void OnInputRequired()
    {
        if (!_running) return;

        // Held by the overlay's pause control. Neither this guard nor the HasPendingCommand one
        // below consumes _stepOnce — that happens only just above RunDecision(), after both — so
        // a step stays armed across any number of early returns until a decision actually runs.
        if (IsGated()) return;

        // Skip pulses fired while our own dispatched command is still in flight — those are
        // that dispatch's bookkeeping, not a new decision. Deliberately NOT ReplayEngine
        // .IsActive: that is now true for the whole bot run (ReplayEngine.BotDriving), so
        // testing it here would suppress every decision.
        if (ReplayEngine.HasPendingCommand) return;

        // A step is spent only once a decision will actually run.
        _stepOnce = false;

        RunDecision();
    }
```

with:

```csharp
    private static void OnInputRequired()
    {
        if (!_running) return;

        // Skip pulses fired while our own dispatched command is still in flight — those are
        // that dispatch's bookkeeping, not a new decision. Deliberately NOT ReplayEngine
        // .IsActive: that is now true for the whole bot run (ReplayEngine.BotDriving), so
        // testing it here would suppress every decision. Checked before _stepOnce is consumed,
        // so a step armed mid-flight survives to the next cycle instead of being silently spent.
        if (ReplayEngine.HasPendingCommand) return;

        // The gate CLASSIFIES rather than returns (paused-action-preview spec): paused with no
        // step armed means this cycle decides but HOLDS its result (Dispatch caches it) instead
        // of executing. _previewing must be computed before _stepOnce is cleared below, because
        // IsGated() reads _stepOnce.
        _previewing = IsGated();

        // A step is spent only once a decision will actually run.
        _stepOnce = false;

        RunDecision();
    }
```

- [ ] **Step 7: Refresh gate + stuck-guard/dump suppression in the decision path**

In `RunDecision`, replace:

```csharp
        try
        {
            ctx = DecisionContext.Capture(_session);
            RoomOneShots.OnDecision(ctx);   // must precede Build — it masks spent one-shots
            RewardClaimMemory.OnDecision(ctx);   // must precede Build — it masks declined card claims
            map = ActionMap.Build(_contract!, ctx, _session);
        }
```

with:

```csharp
        try
        {
            ctx = DecisionContext.Capture(_session);
            RoomOneShots.OnDecision(ctx);   // must precede Build — it masks spent one-shots
            RewardClaimMemory.OnDecision(ctx);   // must precede Build — it masks declined card claims

            // Preview refresh gate (spec Component 1a): while paused with an action already
            // held, an unchanged situation needs no recompute — skip ActionMap.Build,
            // ObsBuilder.Build, and the policy's ONNX inference entirely. Compared against the
            // key stored on the held preview, NOT _lastDecisionKey: that field belongs to
            // AdvanceStuckGuard, which does not run while previewing, so it tracks the last
            // EXECUTED decision and goes stale across a pause.
            if (_previewing && _pending != null && BuildDecisionKey(ctx) == _pending.Key)
                return;

            map = ActionMap.Build(_contract!, ctx, _session);
        }
```

Then replace:

```csharp
        if (!AdvanceStuckGuard(ctx, map))
```

with:

```csharp
        // Skipped while previewing, and NOT deferred to commit either: a preview is a decision
        // that never executed, so folding it into the guard's repeat accounting corrupts the
        // count relative to what the bot actually did — and the guard's forced-fallback path
        // chooses a DIFFERENT command than the one held, which would break the WYSIWYG
        // guarantee if it ran at commit. Manual stepping through a genuinely stuck state
        // therefore won't auto-recover; a human is watching, which is what the guard subs for.
        if (!_previewing && !AdvanceStuckGuard(ctx, map))
```

Then in `DispatchWithFallback`, replace:

```csharp
        if (obs != null)
            DecisionDumper.Write(ctx, obs, map, result);
```

with:

```csharp
        // Deferred to Commit while previewing, so dumps record only actions actually taken.
        if (obs != null && !_previewing)
            DecisionDumper.Write(ctx, obs, map, result);
```

(`DecisionMade?.Invoke(ctx, result)` directly below stays unconditional — the overlay's top-K rows render from it during previews.)

- [ ] **Step 8: Add the read-only probe to RewardClaimMemory**

In `SpireBotCode/RewardClaimMemory.cs`, insert directly **before** `internal static bool TryBeginAutoAdvance(ClaimRewardCommand claim)`:

```csharp
    /// <summary>Read-only probe of <see cref="TryBeginAutoAdvance"/>'s decision, for the paused
    /// preview (paused-action-preview spec): selection during a preview must not mutate the
    /// loop-backstop counter, or aborted previews would burn claim attempts the bot never made.
    /// The probe and the deferred real call cannot disagree — the counter mutates only on
    /// commits and unpaused dispatches, neither of which can interleave between a preview and
    /// its own commit. Declined indexes are already excluded upstream by
    /// <see cref="IsPendingCardClaim"/>.</summary>
    internal static bool CanAutoAdvance(ClaimRewardCommand claim)
        => (_autoAdvanceCounts.TryGetValue(claim.RewardIndex, out int c) ? c : 0)
           < MaxAutoAdvancesBeforeForcedDecline;
```

- [ ] **Step 9: Reroute all six execution sites through Dispatch**

Site 1 — `TryAutoAdvance`, card-claim branch. Replace:

```csharp
        var cardClaim = ctx.Available.OfType<ClaimRewardCommand>()
            .FirstOrDefault(RewardClaimMemory.IsPendingCardClaim);
        if (cardClaim != null && RewardClaimMemory.TryBeginAutoAdvance(cardClaim))
        {
            GD.Print($"[SpireBot] BotController: auto-advancing interstitial step " +
                     $"({cardClaim.Describe()}) — no RL action id models it.");
            ActionExecutor.Execute(cardClaim, ctx.Kind);
            return true;
        }
```

with:

```csharp
        var cardClaim = ctx.Available.OfType<ClaimRewardCommand>()
            .FirstOrDefault(RewardClaimMemory.IsPendingCardClaim);
        // Previewing selects via the read-only probe and defers the REAL TryBeginAutoAdvance
        // (which mutates the loop-backstop counter) to commit time via the CommitGate — see
        // the spec's "Where the hold goes". Non-preview: inline, exactly as before.
        if (cardClaim != null &&
            (_previewing ? RewardClaimMemory.CanAutoAdvance(cardClaim)
                         : RewardClaimMemory.TryBeginAutoAdvance(cardClaim)))
        {
            GD.Print($"[SpireBot] BotController: auto-advancing interstitial step " +
                     $"({cardClaim.Describe()}) — no RL action id models it.");
            Dispatch(cardClaim, ctx.Kind, ctx, map: null, obs: null, result: null,
                     commitGate: () => RewardClaimMemory.TryBeginAutoAdvance(cardClaim));
            return true;
        }
```

Site 2 — `TryAutoAdvance`, interstitial chain. Replace:

```csharp
        GD.Print($"[SpireBot] BotController: auto-advancing interstitial step " +
                 $"({advance.Describe()}) — no RL action id models it.");
        ActionExecutor.Execute(advance, ctx.Kind);
        return true;
```

with:

```csharp
        GD.Print($"[SpireBot] BotController: auto-advancing interstitial step " +
                 $"({advance.Describe()}) — no RL action id models it.");
        Dispatch(advance, ctx.Kind, ctx, map: null, obs: null, result: null);
        return true;
```

Site 3 — `DispatchWithFallback`, mapping-gap scripted fallback. Replace:

```csharp
                ActionExecutor.Execute(scripted, ctx.Kind);
                return;
```

with:

```csharp
                Dispatch(scripted, ctx.Kind, ctx, map, obs, result: null);
                return;
```

Site 4 — `DispatchWithFallback`, normal policy dispatch. Replace:

```csharp
        try
        {
            ActionExecutor.Execute(cmd, ctx.Kind);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotController: ActionExecutor.Execute threw for {cmd.GetType().Name} — " +
                        $"the run will wait for the next settle signal to retry: {ex}");
        }
```

with:

```csharp
        try
        {
            Dispatch(cmd, ctx.Kind, ctx, map, obs, result);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotController: ActionExecutor.Execute threw for {cmd.GetType().Name} — " +
                        $"the run will wait for the next settle signal to retry: {ex}");
        }
```

Site 5 — `HandleUnsupported`, Crystal Sphere click. Replace:

```csharp
            GD.Print($"[SpireBot] BotController: Unsupported fallback — random Crystal Sphere click: {chosen}");
            ActionExecutor.Execute(chosen, ctx.Kind);
            return;
```

with:

```csharp
            GD.Print($"[SpireBot] BotController: Unsupported fallback — random Crystal Sphere click: {chosen}");
            Dispatch(chosen, ctx.Kind, ctx, map, obs: null, result: null);
            return;
```

Site 6 — `HandleUnsupported`, first available. Replace:

```csharp
            GD.Print($"[SpireBot] BotController: Unsupported fallback — first available command: {first}");
            ActionExecutor.Execute(first, ctx.Kind);
            return;
```

with:

```csharp
            GD.Print($"[SpireBot] BotController: Unsupported fallback — first available command: {first}");
            Dispatch(first, ctx.Kind, ctx, map, obs: null, result: null);
            return;
```

After this step, verify completeness: `grep -n "ActionExecutor.Execute" SpireBotCode/BotController.cs` must show exactly **one** call — the one inside `Commit`. Any other hit is a missed site.

- [ ] **Step 10: Run the build to verify it passes**

Run: `dotnet build`

Expected: `Build succeeded.`, `0 Error(s)`, 1 pre-existing warning (`CS8602`, vendored). If `CS8602`'s count increased or new nullable warnings appear on the record fields, fix before committing.

- [ ] **Step 11: Commit**

```bash
git add SpireBotCode/BotController.cs SpireBotCode/RewardClaimMemory.cs SpireBotCode/BotControlSelfTest.cs
git commit -m "feat(bot): compute and hold a previewed action while paused; Step commits it"
```

**Accepted behaviors to know about (not bugs — do not "fix" these):**
- When the loop selects *no* command (nothing legal, or `HandleUnsupported`'s waiting case), `_pending` stays null, so the refresh gate cannot short-circuit and each heartbeat re-runs the full decision, logs included. This matches today's unpaused heartbeat behavior on those screens; Step there falls back to arming a decision.
- The `auto-advancing interstitial step` log line also fires during a preview of that path (the `preview refreshed:` line printed immediately after disambiguates).
- `HandleUnsupported`'s Crystal Sphere branch re-rolls its random pick on each state-change recompute; whatever was cached is what Step executes, so WYSIWYG holds. (The branch is dead code today per its own comment.)

---

### Task 2: Overlay renders the held action

**Files:**
- Modify: `SpireBotCode/Overlay/ThinkingOverlay.cs` (`_Process`, ~lines 187-195)

**Interfaces:**
- Consumes (from Task 1): `BotController.PendingPreviewDescription` — `public static string?`, null when nothing is held.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Render the preview in `_Process`**

In `SpireBotCode/Overlay/ThinkingOverlay.cs`, replace:

```csharp
    public override void _Process(double delta)
    {
        UpdateVisibility();
        // Driven from _Process rather than from BotController.DecisionMade: that event can fire
        // from dispatch-signal context (see this class's header comment), whereas _Process is
        // always the main thread, so the bar needs no CallDeferred juggling.
        _controlBar.RefreshControls();
    }
```

with:

```csharp
    public override void _Process(double delta)
    {
        UpdateVisibility();
        // Driven from _Process rather than from BotController.DecisionMade: that event can fire
        // from dispatch-signal context (see this class's header comment), whereas _Process is
        // always the main thread, so the bar needs no CallDeferred juggling.
        _controlBar.RefreshControls();

        // Paused action preview (paused-action-preview spec, Component 4): while an action is
        // held, the action line reads as a forecast. Sourced from the cache rather than
        // DecisionMade because HandleUnsupported's two paths never fire that event — relabeling
        // in ApplyDecision would leave them previewing an action the overlay never showed.
        // _Process runs after any deferred ApplyDecision, so while a preview is pending this
        // wins; once it clears (commit or unpause), the label falls back to whatever
        // ApplyDecision last wrote — after a commit that is the same action, now accurately
        // labeled "Chosen:".
        string? preview = BotController.PendingPreviewDescription;
        if (preview != null)
            _actionLabel.Text = $"Will take: {preview}";
    }
```

- [ ] **Step 2: Run the build to verify it passes**

Run: `dotnet build`

Expected: `Build succeeded.`, `0 Error(s)`, 1 pre-existing warning.

- [ ] **Step 3: Commit**

```bash
git add SpireBotCode/Overlay/ThinkingOverlay.cs
git commit -m "feat(overlay): show 'Will take:' forecast while a previewed action is held"
```

**Known cosmetic corner (accepted):** after stepping a `HandleUnsupported`-path action (which fires no `DecisionMade`), the label falls back to the *previous* decision's `Chosen:` line until the next preview computes ~1 s later. Not worth new plumbing.

---

### Task 3: In-game acceptance (MANUAL — Perry)

The preview's correctness is an integration property; this pass is where it is verified. A failure sends the work back to Task 1 or 2.

**Files:**
- Modify: `docs/superpowers/specs/2026-08-07-paused-action-preview-design.md` (the `**Status:**` line)

- [ ] **Step 1: Build, launch via Steam, confirm the self-test**

`dotnet build`, launch through Steam (direct exe fails Steamworks init). Expect in the log:

```
[SpireBot] BotControlSelfTest OK (ladder: 0.5, 1, 1.5, 2, 3, 5)
```

A `BotControlSelfTest FAILED:` line means the gate state machine regressed — stop and report.

- [ ] **Step 2: Verify the preview appears without acting**

Set `OnnxModelPath` (empty silently runs the first-legal stub), start a Bot Run, pause mid-combat. Within ~1 s the overlay's action line reads `Will take: ...` and the bot does **not** act.

- [ ] **Step 3: Verify inference is state-driven, not timer-driven**

Stay paused on an idle screen 30 s. Expect exactly **one** `preview refreshed:` line for the whole pause — not one per second. Also expect: no dump-file growth, no stuck-recovery force-clear consequences (a paused mid-animation force-clear is silent by design from the speed-control feature), no forced-fallback (`same decision recurred`) lines.

- [ ] **Step 4: Verify Step commits exactly the preview**

Press Step: the bot performs **exactly** the previewed action, stays paused, and previews the next one. Then set `Temperature` to 0.8 in config and repeat several steps — the executed action must still always match the shown `Will take:` line. This is the case the feature exists for: a resampling policy must not show one action and take another.

- [ ] **Step 5: Verify discard paths**

- Pause, wait for a preview, press Play: the held action is discarded (watch that the *next* action taken can differ, and nothing executes at the instant of unpausing beyond the normal loop resuming).
- Pause on a card-reward screen (auto-advance path): preview shows the claim; Step takes it. Repeat pause/unpause cycles on the same claim a few times, then confirm no `RewardLoopGuard: ... forcing a decline` line ever appears from previews alone.
- Confirm a fully unpaused run's overlay and logs look exactly as before this feature.

- [ ] **Step 6: Mark the spec implemented and commit**

Change the spec's status line to `**Status:** implemented 2026-08-07` (adjust date if later), then:

```bash
git add docs/superpowers/specs/2026-08-07-paused-action-preview-design.md
git commit -m "docs: mark paused action preview implemented after in-game acceptance"
```

---

## Notes for the implementer

- `ObsResult` is a file-local alias (`using ObsResult = SpireBot.SpireBotCode.Obs.Obs;`) already present at the top of `BotController.cs` — the record's field type resolves without new usings. `Func<bool>` needs `System`, also already imported.
- `ReplayEngine`/`ReplayDispatcher` live in namespace `SpireBot.Replay` (already imported in both files you touch).
- `BotControlSelfTest` reaches `internal` members of `BotController` — same assembly, no attribute needed.
- The `Stop()` and `StartBotRun` methods need **no** edits: both set `Paused = false`, and the setter (Task 1 step 4) is what clears the held preview. If you find yourself editing either method, you've drifted from the design.
- If any anchor text above fails to match the file exactly, stop and report rather than adapting — the file has been through several reviewed rounds and a mismatch means this plan is stale, not that the code is wrong.
