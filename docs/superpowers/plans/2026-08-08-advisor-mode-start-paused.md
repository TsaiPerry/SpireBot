# Advisor Mode (Start Paused) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A bot run starts paused by default so the human plays it manually while the overlay shows the action the policy would take.

**Architecture:** One new config flag (`SpireBotConfig.StartPaused`, default `true`) that `BotController.StartBotRun` reads instead of hard-coding `Paused = false`. The selected-vs-applied speed rule moves from a private method on the overlay bar to a public static on `BotController`, so the run-start path and the pause button share one owner. Everything else — the preview computation, the `Will take:` overlay text, the Play/Step controls — already exists and needs no change.

**Tech Stack:** C# 12 / .NET 8, Godot 4 (`GD.Print`, `Engine.TimeScale`), BaseLib `SimpleModConfig`, Harmony patches. Build with `dotnet build` from the repo root.

## Global Constraints

- Repo root: `C:\Users\Perry\Desktop\SpireBot`. Work on branch `bot-speed-control`. Do not merge to master.
- **Never modify any file under `SpireBotCode/Replay/`** — that directory is vendored from the RunReplays mod.
- Do not add a `Co-Authored-By: Claude` trailer to commit messages.
- Never `git add` the untracked run-dump directories at the repo root (`1D3U20HLS6/`, `272H36NZ0Q/`, `CRTS3QQTN4/`, `D0Z9NRXSXV/`, `JW6UP0B3KF/`, `KCS9V8YYFT/`, `KWNQMUHZMG/`, `MH5WLB7922/`). Stage only the exact files each task names.
- There is no C# test project. Automated assertions live in `BotControlSelfTest.Run()`, which is gated on `SpireBotConfig.SelfChecks` and must never throw out of `Run` (it catches and logs).
- Verification for every task is `dotnet build`: **Build succeeded, 0 errors, exactly 1 warning** — a pre-existing `CS8602` in the vendored `SpireBotCode/Replay/ReplayDispatcher.cs`. Any second warning or any error means the task is not done.
- Speed must be written through `ReplayDispatcher.GameSpeed`, never by assigning `Engine.TimeScale` directly: the vendored `ExecuteNext` re-asserts `TimeScale` from its own `_gameSpeed` field on every dispatch (`Replay/ReplayDispatcher.cs:1112-1114`), above its own paused check, so a direct assignment is stomped within a frame.
- Spec: `docs/superpowers/specs/2026-08-08-advisor-mode-start-paused-design.md`.

---

### Task 1: Effective-speed rule moves to BotController

**Files:**
- Modify: `SpireBotCode/BotController.cs` (add a public static method next to the `Paused` property, around line 84)
- Modify: `SpireBotCode/Overlay/BotControlBar.cs:91-107` (replace the private method body with a delegation)
- Test: `SpireBotCode/BotControlSelfTest.cs` (assertions appended inside `Run()`'s `try` block, before the final `GD.Print` at line 101)

**Interfaces:**
- Consumes: `BotController.Paused` (existing `public static bool`), `SpireBotConfig.GameSpeed` (existing `public static float`), `ReplayDispatcher.GameSpeed` (existing `public static float` on the vendored dispatcher — read and written, never `Engine.TimeScale`).
- Produces: `public static void BotController.ApplyEffectiveSpeed()` — writes `ReplayDispatcher.GameSpeed`, returns nothing. Task 2 calls this from `StartBotRun`.

- [ ] **Step 1: Write the failing test**

Append this inside the `try` block of `BotControlSelfTest.Run()` in `SpireBotCode/BotControlSelfTest.cs`, immediately after the existing preview-gate assertions (the line `throw new Exception("unpausing must leave no held preview.");` closes that block) and immediately before the final `GD.Print($"[SpireBot] BotControlSelfTest OK ...")` line:

```csharp
            // Effective-speed rule (advisor-mode spec, Component 3): one owner for
            // selected-vs-applied speed, so the pause button and a run that starts paused can
            // never disagree. Both the pause flag and the dispatcher speed are restored below —
            // this runs at mod init, and leaving either changed would mis-start the next run.
            bool pausedBefore = BotController.Paused;
            float dispatcherSpeedBefore = ReplayDispatcher.GameSpeed;
            float selected = SpireBotConfig.GameSpeed;

            BotController.Paused = true;
            BotController.ApplyEffectiveSpeed();
            if (Math.Abs(ReplayDispatcher.GameSpeed - 1.0f) > Tolerance)
                throw new Exception(
                    $"paused must apply 1.0x, got {ReplayDispatcher.GameSpeed}.");

            BotController.Paused = false;
            BotController.ApplyEffectiveSpeed();
            if (Math.Abs(ReplayDispatcher.GameSpeed - selected) > Tolerance)
                throw new Exception(
                    $"unpaused must apply the selected {selected}x, got {ReplayDispatcher.GameSpeed}.");

            BotController.Paused = pausedBefore;
            ReplayDispatcher.GameSpeed = dispatcherSpeedBefore;
```

`Tolerance` is an existing constant in this file (used by the ladder assertions above). If
`Math` is not already imported, add `using System;` to the top of the file — check the existing
`using` list first and do not duplicate it.

If `ReplayDispatcher` is not already in scope in this file, add the same `using` that
`SpireBotCode/Overlay/BotControlBar.cs` uses for it (read that file's `using` block and copy the
matching line).

- [ ] **Step 2: Run the build to verify it fails**

```bash
cd "C:/Users/Perry/Desktop/SpireBot" && dotnet build
```

Expected: FAIL. One error, `CS0117` or `CS1061`, saying `BotController` does not contain a
definition for `ApplyEffectiveSpeed`.

- [ ] **Step 3: Add the method to BotController**

In `SpireBotCode/BotController.cs`, insert this immediately after the closing brace of the
`Paused` property (the property ends at line 84, just before the `StepOnce` doc comment):

```csharp
    /// <summary>
    /// Pushes the multiplier the game should actually be running at right now: the user's
    /// selected speed (<see cref="SpireBotConfig.GameSpeed"/>) while the bot is deciding, 1.0
    /// while paused so the board can be read at normal speed — and so a run that starts paused
    /// for manual play does not run the game at grind speed.
    ///
    /// This MUST go through ReplayDispatcher.GameSpeed rather than assigning Engine.TimeScale
    /// directly. The vendored ExecuteNext re-asserts TimeScale from its own _gameSpeed field on
    /// every dispatch (Replay/ReplayDispatcher.cs:1112-1114), above its own paused check.
    /// RunReplays can assign TimeScale directly on pause only because its pause also
    /// short-circuits TryDispatch (Replay/ReplayDispatcher.cs:1046) so ExecuteNext never runs;
    /// SpireBot's pause deliberately leaves that path running, so a direct assignment here would
    /// be stomped back to the fast value within a frame.
    /// </summary>
    public static void ApplyEffectiveSpeed()
    {
        ReplayDispatcher.GameSpeed = Paused ? 1.0f : SpireBotConfig.GameSpeed;
    }
```

- [ ] **Step 4: Delegate from the overlay bar**

In `SpireBotCode/Overlay/BotControlBar.cs`, replace the whole existing `ApplyEffectiveSpeed`
method — its doc comment (lines 91-103) and body (lines 104-107) — with:

```csharp
    /// <summary>Pushes the multiplier the game should actually be running at right now.
    /// The rule and its full TimeScale rationale live on
    /// <see cref="BotController.ApplyEffectiveSpeed"/>, which a run that starts paused also
    /// calls — one owner, so the two paths can never disagree.</summary>
    private static void ApplyEffectiveSpeed() => BotController.ApplyEffectiveSpeed();
```

Leave the three existing call sites (`OnPausePressed`, `SetSelectedSpeed`) untouched — they keep
calling the local private method, which now forwards.

- [ ] **Step 5: Run the build to verify it passes**

```bash
cd "C:/Users/Perry/Desktop/SpireBot" && dotnet build
```

Expected: Build succeeded, 0 errors, exactly 1 warning (the pre-existing vendored `CS8602`).

- [ ] **Step 6: Commit**

```bash
cd "C:/Users/Perry/Desktop/SpireBot" && git add SpireBotCode/BotController.cs SpireBotCode/Overlay/BotControlBar.cs SpireBotCode/BotControlSelfTest.cs && git commit -m "refactor(bot): give the effective-speed rule one owner on BotController"
```

---

### Task 2: StartPaused config and a run that honors it

**Files:**
- Modify: `SpireBotCode/SpireBotConfig.cs` (new property after `GameSpeed`, which ends at line 43)
- Modify: `SpireBotCode/BotController.cs:291-296` (inside `StartBotRun`)

**Interfaces:**
- Consumes: `BotController.ApplyEffectiveSpeed()` from Task 1 — `public static void`, no arguments, writes `ReplayDispatcher.GameSpeed` from the current `Paused` flag and `SpireBotConfig.GameSpeed`.
- Produces: `public static bool SpireBotConfig.StartPaused` (default `true`). Nothing later in this plan consumes it.

- [ ] **Step 1: Add the config property**

In `SpireBotCode/SpireBotConfig.cs`, insert immediately after the `GameSpeed` property (which
ends with `public static float GameSpeed { get; set; } = 1.0f;` at line 43) and before the
`SelfChecks` doc comment:

```csharp
    /// <summary>
    /// When true (the default), a bot run begins paused: the bot never acts on its own, and the
    /// overlay shows the action it would take, so a human can play the run manually with the
    /// policy as an advisor. Press Play on the overlay to hand the run over to the bot at any
    /// point. Turn this off for autonomous grind runs, which should start driving immediately.
    /// </summary>
    public static bool StartPaused { get; set; } = true;
```

`SimpleModConfig` auto-generates the settings-screen checkbox from this static property, the
same way it already does for `SelfChecks` and `ShowOverlay`. No UI code is needed — do not add
any.

- [ ] **Step 2: Honor it at run start**

In `SpireBotCode/BotController.cs`, inside `StartBotRun`, find this existing block (lines
291-296, immediately after `ReplayEngine.BotDriving = true;`):

```csharp
        // The vendored dispatcher fast-forwards a replay (_gameSpeed defaults to 2.0) and
        // re-asserts it whenever IsActive is true, which BotDriving now makes permanent. Pin it
        // to the configured speed so a bot run doesn't silently double-speed the game.
        ReplayDispatcher.GameSpeed = SpireBotConfig.GameSpeed;
        // A pause left over from a previous run must not silently hold the new one.
        Paused = false;
```

Replace those six lines with:

```csharp
        // A pause left over from a previous run must not silently hold the new one, and a step
        // or preview held when the last run ended must not survive into this one. The Paused
        // setter discards both only on the FALSE transition, so assigning StartPaused directly
        // could carry a stale preview across runs — where Step would then commit a command
        // chosen against a board that no longer exists. Clear first, then apply the configured
        // starting state. The two assignments are the reset; neither is redundant.
        Paused = false;
        Paused = SpireBotConfig.StartPaused;

        // The vendored dispatcher fast-forwards a replay (_gameSpeed defaults to 2.0) and
        // re-asserts it whenever IsActive is true, which BotDriving now makes permanent. Pin it
        // so a bot run doesn't silently double-speed the game — and so a run that starts paused
        // for manual play runs at 1.0x. Must follow the pause assignment above: the effective
        // speed depends on it.
        ApplyEffectiveSpeed();
```

- [ ] **Step 3: Run the build**

```bash
cd "C:/Users/Perry/Desktop/SpireBot" && dotnet build
```

Expected: Build succeeded, 0 errors, exactly 1 warning (the pre-existing vendored `CS8602`).

- [ ] **Step 4: Verify no stray direct speed pin remains in the run-start path**

```bash
cd "C:/Users/Perry/Desktop/SpireBot" && grep -n "ReplayDispatcher.GameSpeed" SpireBotCode/BotController.cs SpireBotCode/Overlay/BotControlBar.cs
```

Expected: exactly one hit, inside `BotController.ApplyEffectiveSpeed`. A hit anywhere else in
these two files means a second copy of the speed rule survived and must be routed through
`ApplyEffectiveSpeed()`.

- [ ] **Step 5: Commit**

```bash
cd "C:/Users/Perry/Desktop/SpireBot" && git add SpireBotCode/SpireBotConfig.cs SpireBotCode/BotController.cs && git commit -m "feat(bot): start runs paused by default so the policy advises while you play"
```

---

### Task 3: In-game acceptance (MANUAL — Perry)

**Files:** none — this task is played, not coded.

**Interfaces:**
- Consumes: the built mod from Tasks 1-2, plus the already-merged speed control and paused-action-preview features on this branch.
- Produces: the go/no-go for merging `bot-speed-control` to master.

This task cannot be performed by an agent. Launch the game **via Steam only**, with
`SpireBotConfig.OnnxModelPath` set to a real exported model (left empty, the bot silently falls
back to `FirstLegalPolicy` and the advice shown will not be the trained policy's).

- [ ] **Step 1: A run starts paused and advises**

With `StartPaused` on (the default), start a bot run. Expect: the control bar reads `▶ Play`,
the Step button is visible, and within about a second the overlay shows `Will take: ...`.

- [ ] **Step 2: Manual play refreshes the advice**

Play several actions yourself by clicking the game UI — a card, an end turn, a map node. Expect:
the run advances normally under your clicks, and the advice updates after each action.

- [ ] **Step 3: A paused start runs at 1.0x**

Set the speed selector above `1.0x` before starting a run. Expect: while paused the game runs at
normal speed, and pressing `▶ Play` switches it to the selected multiplier.

- [ ] **Step 4: Play hands over mid-run**

Press `▶ Play` partway through a manually played run. Expect: the bot takes over from the
current board state and plays on without a restart.

- [ ] **Step 5: The flag turns it off**

Turn `StartPaused` off in the settings screen and start a run. Expect: it begins driving
immediately, exactly as before this feature.

- [ ] **Step 6: The two prior features still pass their own acceptance**

Over a 30 s idle pause, expect exactly ONE `preview refreshed:` line in the log (proving
inference is state-driven, not timer-driven), and with `Temperature` at `0.8` confirm Step
executes exactly the action the overlay forecast.

---

## Self-Review

**Spec coverage.** Component 1 (`StartPaused`) → Task 2 Step 1. Component 2 (`StartBotRun`
honors it, with the stale-preview reset) → Task 2 Step 2. Component 3 (one owner for the
effective-speed rule) → Task 1 Steps 3-4. Component 4 (refresh cadence) is explicitly no-code
in the spec and is verified by Task 3 Step 2. Component 5 (overlay unchanged) needs no task —
correctly, since no file changes. Testing section → Task 1 Step 1 (both speed assertions plus
the restore) and Task 3 (manual). No gaps.

**Placeholder scan.** Every code step carries complete code; every command is exact with its
expected output. No TBDs.

**Type consistency.** `ApplyEffectiveSpeed()` is `public static void` with no arguments in
Task 1 Step 3, called with that exact shape in Task 1 Step 4 and Task 2 Step 2.
`SpireBotConfig.StartPaused` is `public static bool` in Task 2 Step 1 and read as a bool in
Step 2. `Tolerance` in the new assertions is the file's existing constant, matching the ladder
assertions above them.
