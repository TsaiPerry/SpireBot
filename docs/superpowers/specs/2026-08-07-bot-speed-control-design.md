# Live speed / pause / step control for SpireBot

**Date:** 2026-08-07
**Status:** implemented 2026-08-07 on branch `bot-speed-control`; in-game acceptance pending

Adds an in-game control bar to SpireBot's overlay so a bot run's speed can be changed, paused,
and single-stepped while it plays — mirroring the control bar the RunReplays mod already has
(`RunReplays/RunOverlay.cs:189-343`).

## Motivation

SpireBot can already run at a non-default speed, but only by setting `SpireBotConfig.GameSpeed`
on the mod config screen *before* starting a run: `BotController.StartBotRun` pins
`ReplayDispatcher.GameSpeed = SpireBotConfig.GameSpeed` once, at `BotController.cs:155`. There is
no way to slow down to watch an interesting fight, speed up through a boring one, or stop the bot
to inspect a board mid-run.

RunReplays solves the same problem for recorded-run playback with a control bar in its overlay.
This ports that affordance to the live bot.

## What already exists

The speed *engine* is already vendored into SpireBot and working:

- `SpireBotCode/Replay/ReplayDispatcher.cs:695-728` — `GameSpeed` (property, floors at `0.1`,
  writes `Engine.TimeScale` when a run is active), `ApplyGameSpeed()`, `RestoreGameSpeed()`.
- `ReplayDispatcher.cs:1112-1114` — re-asserts `TimeScale` in `ExecuteNext` if the game reset it
  during a scene transition.
- `SpireBotConfig.cs:38` — a `GameSpeed` config slider.

So the speed half of this work is UI + wiring only. No new time-scaling machinery.

## What does *not* already exist

Pause/step cannot reuse the vendored pause flag. `ReplayDispatcher.Paused` gates `TryDispatch` and
`ExecuteNext` — the dispatch of *already-enqueued* commands. SpireBot's decision loop does not go
through those: `BotController.OnInputRequired` calls `RunDecision()` directly
(`BotController.cs:365-376`) off the dispatcher's `InputRequired` signal. Setting the vendored flag
would therefore fail to stop the bot from deciding, while potentially stranding a command the bot
had already committed to. A new gate in `BotController` is required.

## Design

### Behavior contract

The control bar renders as the top row of `ThinkingOverlay`'s existing panel, visible only while
`BotController.IsRunning`:

```
⏸ Pause   ◀  2.0x  ▶   ⏭ Step
Decision: Combat
Chosen: #3 Strike → Slime
▓▓▓▓▓▓▓░░ 71.2% Strike
```

- **Pause / Play** — toggles `BotController.Paused`. While paused the game runs at `1.0` so the
  board can be inspected at normal speed; resuming restores the selected multiplier.
- **◀ / ▶** — walk the ladder `{0.5, 1.0, 1.5, 2.0, 3.0, 5.0}` (same ladder as
  `RunOverlay.cs:291`), clamping at both ends. Each change writes `SpireBotConfig.GameSpeed`, calls
  `ModConfig.SaveDebounced<SpireBotConfig>()`, and re-applies the effective speed.
- **⏭ Step** — visible only while paused. Permits exactly one decision, then re-pauses.

#### Selected vs. applied speed

Two distinct values, and conflating them is a bug:

- **Selected** — `SpireBotConfig.GameSpeed`. The user's choice; the single source of truth; what
  the `2.0x` label shows; what persists to disk.
- **Applied** — `ReplayDispatcher.GameSpeed`. What the game is running at *right now*: the
  selection while the bot is deciding, `1.0` while paused.

Every transition (pause, resume, ◀, ▶) recomputes applied from selected:

```csharp
ReplayDispatcher.GameSpeed = BotController.Paused ? 1.0f : SpireBotConfig.GameSpeed;
```

**This must go through `ReplayDispatcher.GameSpeed`, never a direct `Engine.TimeScale` assignment.**
The vendored `ExecuteNext` re-asserts `TimeScale` from its own `_gameSpeed` field on every dispatch
(`ReplayDispatcher.cs:1112-1114`), *above* its paused check. RunReplays can assign `TimeScale`
directly on pause only because its pause also short-circuits `TryDispatch`
(`ReplayDispatcher.cs:1046`), so `ExecuteNext` never runs to fight it. SpireBot's pause deliberately
leaves that path running (see "What does not already exist"), so a directly-assigned `TimeScale`
would be stomped back to the fast value within a frame. Routing through the dispatcher's own
property keeps the re-assert agreeing with us instead of fighting us.

**Pause means "stop deciding", not "freeze the game."** A command already dispatched runs to
completion; the bot simply does not choose a next action. This is the honest semantic given the bot
enqueues into the vendored dispatcher rather than acting directly.

Live speed changes persist (decision: write-back, not run-local). Without write-back,
`StartBotRun`'s re-pin at `BotController.cs:155` would silently discard the change at the next run.

### Component 1 — `BotController` gate

New public surface:

- `public static bool Paused { get; set; }`
- `public static void StepOnce()` — sets a private `_stepOnce` flag (and implies `Paused = true`).
- `private static bool IsGated() => Paused && !_stepOnce;` — the single definition of "the loop is
  currently held", used by both call sites below.

The gate sits in the heartbeat/input path:

```csharp
private static void OnHeartbeat()
{
    if (!_running) return;
    StuckRecovery.Tick();    // self-guarding; see requirement 1 below
    OnInputRequired();
    ScheduleHeartbeat();     // re-arm ALWAYS
}
```

`OnInputRequired` returns early when gated:

```csharp
private static void OnInputRequired()
{
    if (!_running) return;
    if (IsGated()) return;
    if (ReplayEngine.HasPendingCommand) return;    // not a new decision
    _stepOnce = false;                             // consume only once a decision will really run
    RunDecision();
}
```

`_stepOnce` must be cleared only on the path that actually reaches `RunDecision()`. Consuming it
above either guard would silently swallow the step whenever the click landed while a command was
still in flight, and the user would see a dead Step button. (The *relative* order of the two guards
does not matter — neither mutates `_stepOnce`. The invariant is that the consume sits below both.)

Three correctness requirements this satisfies:

1. **`StuckRecovery.Tick()` runs unconditionally, including while paused.** An earlier draft of
   this design gated it, reasoning that a long pause would look like a stall and fire recovery
   mid-inspection. **That premise was false** and the final whole-branch review caught it:
   `Tick()` is already self-guarding — on a healthy idle board no in-flight flag is set, so
   `Blocked()` is false and it zeroes its counter and returns (`StuckRecovery.cs:68-72`). Gating
   therefore bought nothing in the healthy case, while in the one case that matters — a command
   that genuinely wedged — it suppressed recovery for the *entire* pause, leaving the run stuck
   and indistinguishable from healthy in the log.

   The residual cost of ungating is that pausing *mid-animation* leaves a blocker set with nothing
   dispatchable, so after ~5 heartbeats `StuckRecovery` force-clears and would log a
   completion-path bug that isn't one. So the **log line only** is suppressed while
   `BotController.Paused` — the force-clear itself still happens.
2. **The heartbeat must re-arm even while paused**, or unpausing would never resume — the
   `InputRequired` signal is edge-triggered and the heartbeat is the only level-triggered backstop.
   This one *is* load-bearing.
3. **One gate is sufficient.** `OnInputRequired` is the sole call site of `RunDecision`
   (`BotController.cs:375`), verified by search.

Lifecycle changes:

- `StartBotRun` sets `Paused = false` alongside the existing `GameSpeed` pin.
- `Stop()` clears `Paused` and calls `ReplayDispatcher.RestoreGameSpeed()`.

The `Stop()` change also fixes a latent bug beyond the scope of this feature: `Stop()` currently
sets `ReplayEngine.BotDriving = false` (`BotController.cs:201`), which makes `IsActive` false, so
nothing re-asserts *or* resets `Engine.TimeScale` — the game is left running at whatever multiplier
the bot used. This was approved as part of this change.

### Component 2 — `Overlay/SpeedLadder.cs` (new file)

The rung values and the pure stepping logic (`Next(float)` / `Prev(float)`, clamping at both ends,
snapping an off-ladder value onto the ladder). Godot-free by design: this is the part of the
feature with real off-by-one risk, and keeping it free of engine types lets a startup self-test
exercise it without a scene tree (see Verification).

### Component 3 — `Overlay/BotControlBar.cs` (new file)

An `HBoxContainer` subclass owning the buttons and a `RefreshControls()` method (pause/play button
label, `Step` visibility, speed label, whole-bar visibility off `BotController.IsRunning`).

Interface: constructed and added as a child by `ThinkingOverlay`; `RefreshControls()` called from
ThinkingOverlay's `_Process`. Depends on `BotController` (paused state, IsRunning),
`ReplayDispatcher` (GameSpeed/ApplyGameSpeed), and `SpireBotConfig` + `ModConfig` (persistence).
Nothing depends on it.

Buttons are plain Godot `Button`s styled to match ThinkingOverlay's existing look (14px font, white
on the panel's translucent black) — deliberately **not** RunReplays' `MakeArrowButton`, which loads
`res://images/atlases/ui_atlas.sprites/settings_tiny_*_arrow.tres`. Text `◀`/`▶` glyphs keep the
overlay free of game-asset dependencies.

`MouseFilter = Stop` on the bar so clicks do not fall through to the game board underneath.

### Component 4 — `ThinkingOverlay` wiring

Two edits only, keeping the overlay display-only in spirit:

- `_Ready`: construct a `BotControlBar` and add it as the **first** child of the existing `vbox`
  (before `_kindLabel`), so it appears above the decision text inside the same panel.
- `_Process`: call `bar.RefreshControls()` alongside the existing `UpdateVisibility()`.

`SpireBotConfig.ShowOverlay = false` hides the whole panel including the control bar. This is
existing, accepted behavior — the config screen remains the escape hatch.

### Component 5 — config

Widen the slider so it can represent the whole ladder; otherwise a live 5.0x writes a value the
config screen cannot display:

```csharp
[ConfigSlider(0.5, 5, 0.5)]   // was (0.5, 4, 0.5)
public static float GameSpeed { get; set; } = 1.0f;
```

Every ladder value (0.5, 1.0, 1.5, 2.0, 3.0, 5.0) is representable on a 0.5-step slider over
[0.5, 5].

## Data flow

```
user clicks ▶
  -> BotControlBar.OnSpeedUp
       -> SpeedLadder.Next(SpireBotConfig.GameSpeed)
       -> SpireBotConfig.GameSpeed = v         (the selection)
       -> ModConfig.SaveDebounced<SpireBotConfig>()
       -> ApplyEffectiveSpeed()                (dispatcher <- v, or 1.0 if paused)
       -> _Process -> RefreshControls()        (label re-renders "3.0x")

user clicks ⏸
  -> BotControlBar.OnPausePressed
       -> BotController.Paused = true
       -> ApplyEffectiveSpeed()                (dispatcher <- 1.0; selection untouched)
       -> _Process -> RefreshControls()        (button becomes "▶ Play", Step becomes visible)

heartbeat fires while paused
  -> BotController.OnHeartbeat
       -> gated: StuckRecovery.Tick() skipped, RunDecision() skipped
       -> ScheduleHeartbeat() still re-arms

user clicks ⏭
  -> BotController.StepOnce()                  (_stepOnce = true)
  -> next heartbeat/signal: one decision runs, _stepOnce cleared, still Paused
```

## Error handling

- Bar is hidden and inert when no run is active; `Paused` is reset at every `StartBotRun`.
- Speed steps clamp at the ladder ends rather than walking off; `ReplayDispatcher.GameSpeed`'s
  setter independently floors at `0.1`.
- If `SaveDebounced` throws, BaseLib logs it internally and the in-memory change still took effect
  — a failed persist degrades to run-local behavior rather than breaking the control.
- All UI mutation stays on the main thread. `RefreshControls()` is driven from `_Process`, not from
  `BotController.DecisionMade` (which can fire from dispatch-signal context — see ThinkingOverlay's
  class comment), so no `CallDeferred` juggling is needed for the bar.

## Verification

No C# test project exists in this repo. The established pattern for automated checks here is a
startup self-test under `SpireBotConfig.SelfChecks`, run from `MainFile.Initialize()`, that logs
OK/FAILED and never throws (`ContractSelfTest.cs`). This design follows it:

- **`BotControlSelfTest`** (new, same shape as `ContractSelfTest`) asserts `SpeedLadder`: walking
  up visits every rung in order and stops at the top, walking down is symmetric, an off-ladder
  value snaps onto the ladder, and every rung is representable on the config slider's
  `[0.5, 5]` range at its 0.5 step.
- The pause/step gate is **not** asserted there. Its correctness is about integration with the
  heartbeat and the dispatcher's in-flight-command check, which a static assertion at mod init
  cannot observe — it is verified in game instead.

Then build + in-game:

1. `dotnet build` — clean, 0 errors. Warning count must not increase; the nullable-dereference
   warning at vendored `ReplayDispatcher.cs:320` is pre-existing and stays untouched.
2. Launch once and confirm `[SpireBot] BotControlSelfTest OK (ladder: ...)` in the log.
3. In-game (launch via Steam, not the exe directly):
   - Start a Bot Run; the control bar appears in the overlay.
   - Step speed both directions; animation rate visibly tracks the label.
   - Pause; decisions stop and the game stays interactive.
   - Pause *immediately after a decision dispatches* (not just while idle) and wait 30s+. The
     force-clear may fire, but it must stay silent while paused — a `StuckRecovery: nothing
     dispatchable ...` line appearing during a pause means requirement 1's log suppression is
     wrong. On resume the run must continue normally rather than dumping a recovery burst.
   - Step once; exactly one decision fires and the bot re-pauses.
   - Resume; the previously selected multiplier is restored (not 1.0).
   - Stop the run; `Engine.TimeScale` returns to 1.0.
   - Restart the game; the last live speed is still selected on the config screen.

## Out of scope

- **Keyboard shortcuts.** Neither SpireBot nor RunReplays handles key input today; adding it means
  new input-conflict surface with the game's own bindings. Buttons only, matching RunReplays.
- **A ⏹ Stop button.** RunReplays has one; SpireBot has no in-game way to end a bot run early. That
  is a separate feature, deliberately not bundled here.
- **Any change to vendored files.** `VENDORED-FROM.md`'s divergence list stays accurate — this
  design touches only SpireBot-original files.
