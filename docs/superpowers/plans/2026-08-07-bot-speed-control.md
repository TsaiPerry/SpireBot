# SpireBot Live Speed / Pause / Step Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an in-game control bar to SpireBot's overlay so a running bot's speed can be changed, paused, and single-stepped without restarting the run.

**Architecture:** A pure `SpeedLadder` holds the rung values and stepping logic. `BotController` gains a `Paused`/`StepOnce` gate on its decision loop. A new `BotControlBar` node renders the buttons and is hosted inside the existing `ThinkingOverlay` panel. The speed *engine* already exists — `ReplayDispatcher.GameSpeed` is vendored and working — so this is UI, a gate, and wiring only. No vendored file is modified.

**Tech Stack:** C# 12 (file-scoped namespaces, `null!` fields), .NET / Godot 4 (`Node`, `CanvasLayer`, `HBoxContainer`, `Button`, `Callable.From`), Harmony (untouched here), BaseLib `ModConfig`/`SimpleModConfig` for settings persistence.

**Spec:** `docs/superpowers/specs/2026-08-07-bot-speed-control-design.md`

## Global Constraints

- **Do not modify any file under `SpireBotCode/Replay/`.** That directory is vendored from the RunReplays mod and its divergences are catalogued in `SpireBotCode/Replay/VENDORED-FROM.md`. This feature touches only SpireBot-original files. If a task seems to require a vendored edit, stop and report instead.
- **Stage, do not commit.** This project's convention is to `git add` and leave the commit to Perry. Every task ends with `git add`, never `git commit`.
- **Selected vs. applied speed.** `SpireBotConfig.GameSpeed` is the *selection* (what the label shows, what persists). `ReplayDispatcher.GameSpeed` is the *applied* value (the selection while running, `1.0` while paused). Never assign `Engine.TimeScale` directly — see Task 3's step 3 for why.
- **Speed ladder:** `{0.5, 1.0, 1.5, 2.0, 3.0, 5.0}` — must match `SpireBotConfig.GameSpeed`'s `[ConfigSlider(0.5, 5, 0.5)]` range and step.
- **Self-tests never throw.** Follow `ContractSelfTest.cs`: catch everything, log `[SpireBot] <Name> FAILED: {ex}` via `GD.PrintErr`, and return normally. A failing self-test must not prevent mod load.
- **Build command** (run from the repo root, `c:\Users\Perry\Desktop\SpireBot`): `dotnet build`. Expect `Build succeeded. 0 Warning(s) 0 Error(s)`. The warning count must not increase.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `SpireBotCode/Overlay/SpeedLadder.cs` | Create | Rung values + pure `Next`/`Prev` stepping. Godot-free so it is testable at mod init. |
| `SpireBotCode/BotControlSelfTest.cs` | Create | Startup assertions over `SpeedLadder`, in `ContractSelfTest`'s shape. |
| `SpireBotCode/SpireBotConfig.cs` | Modify (line 32-38) | Widen the `GameSpeed` slider to `[0.5, 5]`. |
| `SpireBotCode/MainFile.cs` | Modify (line 44-47) | Run the new self-test under `SelfChecks`. |
| `SpireBotCode/BotController.cs` | Modify (lines ~54-58, 155, 200-205, 365-376, 399-405) | `Paused` / `StepOnce()` / `IsGated()` gate + run lifecycle. |
| `SpireBotCode/Overlay/BotControlBar.cs` | Create | The buttons, their handlers, and `RefreshControls()`. |
| `SpireBotCode/Overlay/ThinkingOverlay.cs` | Modify (lines ~35-37, 110-124, 180-183) | Host one `BotControlBar` in its panel; pump `RefreshControls()`. |

Task order matters: Task 1 produces `SpeedLadder`, which Task 3 consumes; Task 2 produces the `BotController` gate, which Task 3 consumes. Task 3 is the only task that produces visible UI.

---

### Task 1: Speed ladder + its self-test

Pure logic and its assertions, with no Godot UI and no dependency on the other tasks. Delivers a `SpeedLadder` that later tasks call, plus proof it steps correctly.

**Files:**
- Create: `SpireBotCode/Overlay/SpeedLadder.cs`
- Create: `SpireBotCode/BotControlSelfTest.cs`
- Modify: `SpireBotCode/SpireBotConfig.cs` (lines 32-38)
- Modify: `SpireBotCode/MainFile.cs` (lines 44-47)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces:
  - `SpireBot.SpireBotCode.Overlay.SpeedLadder` — `public static readonly float[] Steps`, `public static float Next(float current)`, `public static float Prev(float current)`.
  - `SpireBot.SpireBotCode.BotControlSelfTest.Run()` — `public static void`, never throws.

- [ ] **Step 1: Write the failing test**

Create `SpireBotCode/BotControlSelfTest.cs`:

```csharp
using System;

using Godot;

using SpireBot.SpireBotCode.Overlay;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Startup sanity check for the overlay speed control's pure logic, in the same shape as
/// <see cref="ContractSelfTest"/>: runs once at mod init under
/// <see cref="SpireBotConfig.SelfChecks"/>, logs OK or FAILED, and never throws.
///
/// Covers <see cref="SpeedLadder"/> only. The pause/step gate in <see cref="BotController"/> is
/// deliberately not asserted here — its correctness is about integration with the heartbeat and
/// the dispatcher's in-flight-command check, which a static assertion at mod init cannot observe.
/// That is verified in game instead.
/// </summary>
public static class BotControlSelfTest
{
    // Mirrors [ConfigSlider(0.5, 5, 0.5)] on SpireBotConfig.GameSpeed. Deliberately duplicated as
    // literals rather than reflected off the attribute — the whole point is to fail loudly if the
    // ladder and the slider ever drift apart.
    private const float SliderMin = 0.5f;
    private const float SliderMax = 5.0f;
    private const float SliderStep = 0.5f;

    private const float Tolerance = 0.001f;

    public static void Run()
    {
        try
        {
            float[] steps = SpeedLadder.Steps;
            if (steps.Length < 2)
                throw new Exception($"ladder needs at least 2 rungs, has {steps.Length}.");

            // Walking up from the bottom must visit every rung, in order.
            float v = steps[0];
            for (int i = 1; i < steps.Length; i++)
            {
                v = SpeedLadder.Next(v);
                if (Math.Abs(v - steps[i]) > Tolerance)
                    throw new Exception($"Next() rung {i}: expected {steps[i]}, got {v}.");
            }

            // ...and must clamp, not wrap or overshoot, at the top.
            if (Math.Abs(SpeedLadder.Next(v) - v) > Tolerance)
                throw new Exception($"Next() at the top of the ladder moved off {v}.");

            // Symmetrically back down.
            for (int i = steps.Length - 2; i >= 0; i--)
            {
                v = SpeedLadder.Prev(v);
                if (Math.Abs(v - steps[i]) > Tolerance)
                    throw new Exception($"Prev() rung {i}: expected {steps[i]}, got {v}.");
            }
            if (Math.Abs(SpeedLadder.Prev(v) - v) > Tolerance)
                throw new Exception($"Prev() at the bottom of the ladder moved off {v}.");

            // An off-ladder value (a hand-edited config, or a ladder change between versions)
            // must snap onto the ladder rather than sticking.
            float upFromOff = SpeedLadder.Next(2.4f);
            if (Math.Abs(upFromOff - 3.0f) > Tolerance)
                throw new Exception($"Next(2.4) expected 3.0, got {upFromOff}.");
            float downFromOff = SpeedLadder.Prev(2.4f);
            if (Math.Abs(downFromOff - 2.0f) > Tolerance)
                throw new Exception($"Prev(2.4) expected 2.0, got {downFromOff}.");

            // Every rung must be representable on the config slider, or a live speed change would
            // write a value the config screen cannot display (design doc, Component 5).
            foreach (float step in steps)
            {
                if (step < SliderMin - Tolerance || step > SliderMax + Tolerance)
                    throw new Exception(
                        $"ladder value {step} is outside the GameSpeed slider range [{SliderMin}, {SliderMax}].");
                float ticks = step / SliderStep;
                if (Math.Abs(ticks - MathF.Round(ticks)) > Tolerance)
                    throw new Exception(
                        $"ladder value {step} is not a multiple of the slider's {SliderStep} step.");
            }

            GD.Print($"[SpireBot] BotControlSelfTest OK (ladder: {string.Join(", ", steps)})");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotControlSelfTest FAILED: {ex}");
        }
    }
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build`

Expected: FAIL, with several `CS0103` errors naming `SpeedLadder` — the type does not exist yet. (The `using SpireBot.SpireBotCode.Overlay;` itself resolves fine; that namespace already holds `ThinkingOverlay`.) Example:

```
SpireBotCode\BotControlSelfTest.cs(31,13): error CS0103: The name 'SpeedLadder' does not exist in the current context
```

This is the failing-test state: the assertions are written, the thing they assert against is not.

- [ ] **Step 3: Write the minimal implementation**

Create `SpireBotCode/Overlay/SpeedLadder.cs`:

```csharp
namespace SpireBot.SpireBotCode.Overlay;

/// <summary>
/// The discrete speed multipliers the overlay's ◀/▶ buttons walk between, and the pure stepping
/// logic over them. Same ladder RunReplays uses for replay playback (RunReplays/RunOverlay.cs:291).
///
/// Deliberately Godot-free: this is the part of the speed control with real off-by-one risk, and
/// keeping it free of engine types lets <see cref="BotControlSelfTest"/> exercise it at mod init
/// without a scene tree.
/// </summary>
public static class SpeedLadder
{
    /// <summary>Rungs, ascending. Must stay inside SpireBotConfig.GameSpeed's slider range and
    /// land on its step — BotControlSelfTest asserts both.</summary>
    public static readonly float[] Steps = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, 5.0f };

    /// <summary>Comparison slack. The current speed is a float round-tripped through config JSON,
    /// so exact equality against a rung is not safe.</summary>
    private const float Epsilon = 0.01f;

    /// <summary>The lowest rung strictly above <paramref name="current"/>; the top rung when
    /// already at or above it. Never returns a value off the ladder.</summary>
    public static float Next(float current)
    {
        for (int i = 0; i < Steps.Length; i++)
        {
            if (Steps[i] > current + Epsilon)
                return Steps[i];
        }
        return Steps[Steps.Length - 1];
    }

    /// <summary>The highest rung strictly below <paramref name="current"/>; the bottom rung when
    /// already at or below it. Never returns a value off the ladder.</summary>
    public static float Prev(float current)
    {
        for (int i = Steps.Length - 1; i >= 0; i--)
        {
            if (Steps[i] < current - Epsilon)
                return Steps[i];
        }
        return Steps[0];
    }
}
```

- [ ] **Step 4: Widen the config slider so the ladder fits inside it**

In `SpireBotCode/SpireBotConfig.cs`, replace lines 32-38 (the `GameSpeed` block) with:

```csharp
    /// <summary>
    /// Engine time scale during a bot run. The vendored dispatcher fast-forwards playback at
    /// 2.0 and re-asserts that whenever a driver is attached, so this is pinned at run start;
    /// raise it to grind runs faster.
    ///
    /// This is the *selected* speed and the persisted one. The overlay's ◀/▶ buttons
    /// (BotControlBar) write it live and save it debounced, so a change made mid-run survives to
    /// the next run and the next session. Range and step must keep covering every
    /// SpeedLadder.Steps rung — BotControlSelfTest asserts that.
    /// </summary>
    [ConfigSlider(0.5, 5, 0.5)]
    public static float GameSpeed { get; set; } = 1.0f;
```

- [ ] **Step 5: Wire the self-test into mod init**

In `SpireBotCode/MainFile.cs`, replace lines 42-47 with:

```csharp
        // NOT OS.IsDebugBuild(): the shipped game is an export_release build, so that gate is
        // always false in game and the self-test would never run where it matters.
        if (SpireBotConfig.SelfChecks)
        {
            ContractSelfTest.Run();
            BotControlSelfTest.Run();
        }
```

- [ ] **Step 6: Run the build to verify it passes**

Run: `dotnet build`

Expected: `Build succeeded.` with `0 Error(s)` and the same warning count as before this task (0).

- [ ] **Step 7: Stage**

```bash
git add SpireBotCode/Overlay/SpeedLadder.cs SpireBotCode/BotControlSelfTest.cs SpireBotCode/SpireBotConfig.cs SpireBotCode/MainFile.cs
```

Do **not** commit — see Global Constraints.

---

### Task 2: BotController pause / step gate

Adds the ability to hold the decision loop. No UI yet — after this task the gate is reachable only from code, which is exactly what makes it independently reviewable.

**Files:**
- Modify: `SpireBotCode/BotController.cs` (fields ~54-58; `StartBotRun` line 155; `Stop` lines 198-205; `OnInputRequired` lines 365-376; `OnHeartbeat` lines 399-405)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces, all on `SpireBot.SpireBotCode.BotController`:
  - `public static bool Paused { get; set; }` — setting `false` also discards an unconsumed step.
  - `public static void StepOnce()` — `void`, sets paused + arms one decision.
  - (`public static bool IsRunning` already exists and is unchanged.)

- [ ] **Step 1: Add the gate state and its accessors**

In `SpireBotCode/BotController.cs`, find:

```csharp
    /// <summary>True while the bot decision loop is driving a run (StartBotRun..Stop).
    /// MenuInjection uses this to stop an abandoned bot run when the main menu is reached.</summary>
    public static bool IsRunning => _running;
```

and insert directly after it:

```csharp
    private static bool _paused;
    private static bool _stepOnce;

    /// <summary>
    /// True while the decision loop is held by the overlay's pause control (BotControlBar).
    /// A paused bot stops *choosing* actions; a command already dispatched still runs to
    /// completion, because the bot enqueues into the vendored dispatcher rather than acting
    /// directly. Reset at every <see cref="StartBotRun"/> and <see cref="Stop"/>.
    /// </summary>
    public static bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            // Leaving pause discards an un-consumed step, so pausing again later doesn't hand out
            // a free decision the user never asked for.
            if (!value) _stepOnce = false;
        }
    }

    /// <summary>Lets exactly one more decision through, then leaves the loop paused. Implies
    /// <see cref="Paused"/>, so it is also the "pause at the next decision" control.</summary>
    public static void StepOnce()
    {
        _paused = true;
        _stepOnce = true;
    }

    /// <summary>The single definition of "the decision loop is currently held", used by both
    /// <see cref="OnHeartbeat"/> and <see cref="OnInputRequired"/> so the two can never disagree.</summary>
    private static bool IsGated() => _paused && !_stepOnce;
```

- [ ] **Step 2: Gate the decision path**

Replace `OnInputRequired` (lines 365-376) in full with:

```csharp
    private static void OnInputRequired()
    {
        if (!_running) return;

        // Held by the overlay's pause control. Checked BEFORE the HasPendingCommand return below
        // so that a Step clicked while a command is still in flight is not silently swallowed by
        // it — the step stays armed until a decision really runs.
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

- [ ] **Step 3: Gate the heartbeat's stuck detector without stopping the heartbeat**

Replace `OnHeartbeat` (lines 399-405) in full with:

```csharp
    private static void OnHeartbeat()
    {
        if (!_running) return;

        // A paused run must not look like a stalled one. StuckRecovery counts no-progress time,
        // so ticking it through a deliberate pause would fire recovery while the user is just
        // inspecting the board.
        if (!IsGated())
            StuckRecovery.Tick();

        OnInputRequired();

        // Re-arm ALWAYS, including while paused — this is the only level-triggered path back into
        // the loop (ReplayDispatcher's InputRequired signal is edge-triggered), so skipping the
        // re-arm while paused would mean unpausing never resumes.
        ScheduleHeartbeat();
    }
```

- [ ] **Step 4: Reset the gate at run start**

In `StartBotRun`, find line 155:

```csharp
        ReplayDispatcher.GameSpeed = SpireBotConfig.GameSpeed;
```

and insert directly after it:

```csharp
        // A pause left over from a previous run must not silently hold the new one.
        Paused = false;
```

- [ ] **Step 5: Reset the gate and the time scale at run stop**

Replace `Stop()` (lines 196-205) in full with:

```csharp
    /// <summary>Stops the bot decision loop. Does not end the in-progress run — it just stops
    /// driving it; the player (or another BotController.StartBotRun call) can take over.</summary>
    public static void Stop()
    {
        _running = false;
        ReplayEngine.BotDriving = false;
        Paused = false;
        // BotDriving going false makes ReplayEngine.IsActive false, after which nothing re-asserts
        // OR resets Engine.TimeScale — without this the game is left running at whatever
        // multiplier the bot used, with no way back short of a restart.
        ReplayDispatcher.RestoreGameSpeed();
        Unsubscribe();
        UnsubscribeCombatHooks();
        GD.Print("[SpireBot] BotController.Stop — decision loop stopped.");
    }
```

- [ ] **Step 6: Run the build to verify it passes**

Run: `dotnet build`

Expected: `Build succeeded.` with `0 Error(s)` and no new warnings.

- [ ] **Step 7: Stage**

```bash
git add SpireBotCode/BotController.cs
```

---

### Task 3: The control bar and its overlay wiring

The visible deliverable. Consumes `SpeedLadder` from Task 1 and the gate from Task 2.

**Files:**
- Create: `SpireBotCode/Overlay/BotControlBar.cs`
- Modify: `SpireBotCode/Overlay/ThinkingOverlay.cs` (fields ~35-37; `_Ready` 110-124; `_Process` 180-183)

**Interfaces:**
- Consumes:
  - `SpeedLadder.Next(float)` / `SpeedLadder.Prev(float)` (Task 1).
  - `BotController.Paused` (get/set), `BotController.StepOnce()`, `BotController.IsRunning` (Task 2).
  - `ReplayDispatcher.GameSpeed` (setter — vendored, already exists), `SpireBotConfig.GameSpeed`, `ModConfig.SaveDebounced<T>()`.
- Produces: `SpireBot.SpireBotCode.Overlay.BotControlBar` — a `sealed partial class : HBoxContainer` with `public void RefreshControls()`.

- [ ] **Step 1: Create the control bar**

Create `SpireBotCode/Overlay/BotControlBar.cs`:

```csharp
using System;

using BaseLib.Config;
using Godot;

using SpireBot.Replay;

namespace SpireBot.SpireBotCode.Overlay;

/// <summary>
/// The overlay's live transport controls: pause/play, a speed ladder (◀ 2.0x ▶), and a step button
/// that only appears while paused. Mirrors RunReplays' replay control bar
/// (RunReplays/RunOverlay.cs:189-343) for the live bot.
///
/// Owned by <see cref="ThinkingOverlay"/>, which adds one instance as the first row of its panel
/// and pumps <see cref="RefreshControls"/> from its own <c>_Process</c>. Everything this touches
/// (<see cref="BotController.Paused"/>, <see cref="SpireBotConfig.GameSpeed"/>,
/// <see cref="ReplayDispatcher.GameSpeed"/>) is static, so this node holds no state of its own
/// beyond its child widgets — which is why all the handlers below are static too.
/// </summary>
public sealed partial class BotControlBar : HBoxContainer
{
    private Button? _pauseButton;
    private Button? _stepButton;
    private Label? _speedLabel;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 4);
        // These buttons sit over the live game board; without this, clicks fall through to
        // whatever is underneath (a card, a map node).
        MouseFilter = MouseFilterEnum.Stop;

        _pauseButton = MakeButton("⏸ Pause", OnPausePressed);
        AddChild(_pauseButton);

        AddChild(MakeButton("◀", OnSpeedDown));

        _speedLabel = new Label
        {
            CustomMinimumSize = new Vector2(48, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _speedLabel.AddThemeFontSizeOverride("font_size", 14);
        _speedLabel.AddThemeColorOverride("font_color", Colors.White);
        AddChild(_speedLabel);

        AddChild(MakeButton("▶", OnSpeedUp));

        _stepButton = MakeButton("⏭ Step", OnStepPressed);
        AddChild(_stepButton);

        RefreshControls();
    }

    /// <summary>Plain Godot Buttons styled to match ThinkingOverlay's existing 14px white-on-black
    /// look. Deliberately NOT RunReplays' MakeArrowButton, which loads
    /// res://images/atlases/ui_atlas.sprites/settings_tiny_*_arrow.tres — text glyphs keep this
    /// overlay free of game-asset dependencies.</summary>
    private static Button MakeButton(string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.AddThemeFontSizeOverride("font_size", 14);
        button.Connect(BaseButton.SignalName.Pressed, Callable.From(onPressed));
        return button;
    }

    private static void OnPausePressed()
    {
        BotController.Paused = !BotController.Paused;
        ApplyEffectiveSpeed();
    }

    private static void OnStepPressed() => BotController.StepOnce();

    private static void OnSpeedDown() => SetSelectedSpeed(SpeedLadder.Prev(SpireBotConfig.GameSpeed));

    private static void OnSpeedUp() => SetSelectedSpeed(SpeedLadder.Next(SpireBotConfig.GameSpeed));

    /// <summary>Records the user's chosen multiplier: in memory, on disk (debounced), and — via
    /// <see cref="ApplyEffectiveSpeed"/> — in the running game. Writing SpireBotConfig is what
    /// makes a live change survive StartBotRun's re-pin (BotController.cs:155) into the next
    /// run.</summary>
    private static void SetSelectedSpeed(float speed)
    {
        SpireBotConfig.GameSpeed = speed;
        ModConfig.SaveDebounced<SpireBotConfig>();
        ApplyEffectiveSpeed();
    }

    /// <summary>
    /// Pushes the multiplier the game should actually be running at right now: the user's
    /// selection while the bot is deciding, 1.0 while paused so the board can be read at normal
    /// speed.
    ///
    /// This MUST go through ReplayDispatcher.GameSpeed rather than assigning Engine.TimeScale
    /// directly. The vendored ExecuteNext re-asserts TimeScale from its own _gameSpeed field on
    /// every dispatch (Replay/ReplayDispatcher.cs:1112-1114), above its own paused check.
    /// RunReplays can assign TimeScale directly on pause only because its pause also
    /// short-circuits TryDispatch (Replay/ReplayDispatcher.cs:1046) so ExecuteNext never runs;
    /// SpireBot's pause deliberately leaves that path running, so a direct assignment here would
    /// be stomped back to the fast value within a frame.
    /// </summary>
    private static void ApplyEffectiveSpeed()
    {
        ReplayDispatcher.GameSpeed = BotController.Paused ? 1.0f : SpireBotConfig.GameSpeed;
    }

    /// <summary>Re-renders the bar from current state. Cheap and idempotent — ThinkingOverlay
    /// calls it every frame rather than wiring change events for a five-widget bar.</summary>
    public void RefreshControls()
    {
        // _Ready may not have run yet the first frame after construction.
        if (_pauseButton == null || _stepButton == null || _speedLabel == null) return;

        Visible = BotController.IsRunning;
        if (!Visible) return;

        bool paused = BotController.Paused;
        _pauseButton.Text = paused ? "▶ Play" : "⏸ Pause";
        _stepButton.Visible = paused;
        _speedLabel.Text = $"{SpireBotConfig.GameSpeed:0.0}x";
    }
}
```

- [ ] **Step 2: Run the build to verify the bar compiles on its own**

Run: `dotnet build`

Expected: `Build succeeded.`, `0 Error(s)`. The bar exists but nothing constructs it yet — that is the next step.

- [ ] **Step 3: Host the bar in ThinkingOverlay**

In `SpireBotCode/Overlay/ThinkingOverlay.cs`, find the field block:

```csharp
    private Label _kindLabel = null!;
    private Label _actionLabel = null!;
    private VBoxContainer _topKBox = null!;
```

and replace it with:

```csharp
    private Label _kindLabel = null!;
    private Label _actionLabel = null!;
    private VBoxContainer _topKBox = null!;
    private BotControlBar _controlBar = null!;
```

Then in `_Ready`, find:

```csharp
        _kindLabel = MakeLabel("Decision: <none yet>");
        _actionLabel = MakeLabel("Chosen: <none yet>");
        _topKBox = new VBoxContainer();

        vbox.AddChild(_kindLabel);
        vbox.AddChild(_actionLabel);
        vbox.AddChild(_topKBox);
```

and replace it with:

```csharp
        _controlBar = new BotControlBar();
        _kindLabel = MakeLabel("Decision: <none yet>");
        _actionLabel = MakeLabel("Chosen: <none yet>");
        _topKBox = new VBoxContainer();

        // First row, above the decision text — same placement as RunReplays' control bar.
        vbox.AddChild(_controlBar);
        vbox.AddChild(_kindLabel);
        vbox.AddChild(_actionLabel);
        vbox.AddChild(_topKBox);
```

- [ ] **Step 4: Pump RefreshControls from the existing per-frame hook**

In `SpireBotCode/Overlay/ThinkingOverlay.cs`, replace `_Process` (lines 180-183) with:

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

- [ ] **Step 5: Run the build to verify it passes**

Run: `dotnet build`

Expected: `Build succeeded.` with `0 Error(s)` and no new warnings.

- [ ] **Step 6: Stage**

```bash
git add SpireBotCode/Overlay/BotControlBar.cs SpireBotCode/Overlay/ThinkingOverlay.cs
```

---

### Task 4: In-game acceptance

The gate's correctness is an integration property — it cannot be asserted at mod init. This task is where it is actually verified. Nothing here is optional; a failure means going back to Task 2 or 3.

**Files:**
- Modify: `docs/superpowers/specs/2026-08-07-bot-speed-control-design.md` (the `**Status:**` line)

**Interfaces:**
- Consumes: the built mod from Tasks 1-3.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Confirm the build copied to the mods folder**

Run: `dotnet build`

Expected: the output includes `Copying .dll .pdb .json and native runtime files to mods folder.` and `SpireBot.dll` under `D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\SpireBot\` has a current timestamp.

- [ ] **Step 2: Launch and check the self-test**

Launch the game **via Steam** — launching `SlayTheSpire2.exe` directly fails Steamworks init.

Expected in the log/console:

```
[SpireBot] BotControlSelfTest OK (ladder: 0.5, 1, 1.5, 2, 3, 5)
```

If it prints `BotControlSelfTest FAILED: ...` instead, stop — the ladder or the slider range is wrong (Task 1).

- [ ] **Step 3: Verify the bar appears and speed tracks**

Set `OnnxModelPath` on the config screen first (an empty value silently drives the run with `FirstLegalPolicy` — see `.superpowers/task-18-showcase-checklist.md`), then press **Bot Run**.

Check, in order:
- The control bar renders as the top row of the overlay panel, and only after the run starts.
- Clicking **▶** walks the label 1.0x → 1.5x → 2.0x → 3.0x → 5.0x and stops there; the animation rate visibly tracks it.
- Clicking **◀** walks back down and stops at 0.5x.

- [ ] **Step 4: Verify pause, step, and the stuck detector**

- Click **⏸ Pause**: the button becomes **▶ Play**, **⏭ Step** appears, decisions stop, and the game runs at normal speed.
- Leave it paused for 30+ seconds and watch the log: **no** `STALL (...)` lines and no `StuckRecovery` activity should accumulate. Any that do mean the `IsGated()` check in `OnHeartbeat` (Task 2, step 3) is wrong.
- Click **⏭ Step** once: exactly one decision fires (one new `Decision:`/`Chosen:` in the overlay) and the bot returns to paused.
- Click **▶ Play**: the run resumes at the *previously selected* multiplier, not 1.0.

- [ ] **Step 5: Verify stop and persistence**

- Return to the main menu to end the bot run. The game must return to normal speed — this exercises the `RestoreGameSpeed()` added in Task 2, step 5.
- Quit and relaunch. The config screen's **GameSpeed** slider shows the last speed selected in the overlay.

- [ ] **Step 6: Mark the spec implemented and stage**

In `docs/superpowers/specs/2026-08-07-bot-speed-control-design.md`, change:

```markdown
**Status:** approved, not yet implemented
```

to:

```markdown
**Status:** implemented 2026-08-07
```

```bash
git add docs/superpowers/specs/2026-08-07-bot-speed-control-design.md
```

---

## Notes for the implementer

- **`ReplayEngine` and `ReplayDispatcher` live in namespace `SpireBot.Replay`**, not `SpireBot.SpireBotCode.Replay`. `BotController.cs` already has the `using`; `BotControlBar.cs` needs `using SpireBot.Replay;` (included in the code above).
- **`ThinkingOverlay` is in `SpireBot.SpireBotCode.Overlay`** and references `BotController` unqualified — the parent namespace is in scope, so `BotControlBar` can do the same.
- **Line numbers in this plan are from the pre-change files.** They will drift as you apply edits; anchor on the quoted code, not the numbers.
- If a step's "expected" output does not match what you see, stop and report rather than adapting the plan — a surprise here usually means an assumption in the spec was wrong.

---

## Post-execution amendment (2026-08-07)

**Task 2, step 3 as written above is superseded.** The final whole-branch review found its premise
false: `StuckRecovery.Tick()` is already self-guarding (`StuckRecovery.cs:68-72` — on a healthy idle
board `Blocked()` is false, so it zeroes its counter and returns), so gating it behind `IsGated()`
bought nothing in the healthy case while suppressing recovery of a genuinely wedged command for the
whole pause.

Shipped behaviour instead: `StuckRecovery.Tick()` runs **unconditionally** in `OnHeartbeat`, and
only its force-clear *log line* is suppressed while `BotController.Paused` (the force-clear itself
still happens). See commit `89fb272` and the updated spec's Component 1, requirement 1.

The unconditional `ScheduleHeartbeat()` re-arm in the same method is unaffected — that one is
genuinely load-bearing.

Task 2, step 2's claim that the guard *ordering* in `OnInputRequired` is load-bearing was also
imprecise: neither guard mutates `_stepOnce`, so their relative order is irrelevant. The real
invariant is that `_stepOnce = false` sits below both. Corrected in commit `89fb272`.
