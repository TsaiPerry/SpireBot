# Advisor Mode (Start Paused) — Design

**Date:** 2026-08-08
**Branch:** `bot-speed-control`
**Builds on:** `2026-08-07-bot-speed-control-design.md`, `2026-08-07-paused-action-preview-design.md`

## Goal

A bot run starts paused by default, so the human plays the run normally by clicking the game
UI while the overlay continuously shows the action the policy would take. Pressing Play (or
Step) hands control back to the bot at any point, exactly as today.

## Why this is a small change

The paused-action-preview feature already computes and displays a held action while paused,
and the pause gate already stops the bot from acting without stopping the game. A read-only
investigation of this repo, the vendored `SpireBotCode/Replay/` code, and the decompiled game
source confirmed the two facts advisor mode depends on:

1. **Nothing blocks human input while the bot is attached.** Every read of
   `ReplayEngine.IsActive` / `ReplayEngine.BotDriving` is internal bookkeeping — record-side
   gates that suppress SpireBot's own save-log recording, and replay-side Harmony *postfixes*
   that dispatch the next queued bot command after a game event. None intercept or veto a UI
   click. The only `MouseFilter` in the codebase is `Overlay/BotControlBar.cs:32`, which stops
   clicks on the bot's own buttons from falling through to the board.
2. **Nothing auto-advances the run while paused.** Dispatch only happens in reaction to a
   command the bot issued, and `BotController.Paused` stops the bot from issuing any.

So advisor mode is: start paused, and make sure the two things that assume "a run starts
running" still behave.

## Component 1 — `SpireBotConfig.StartPaused`

New property, default `true`, placed beside `GameSpeed` in `SpireBotConfig`:

```csharp
/// <summary>
/// When true (the default), a bot run begins paused: the bot never acts on its own, and the
/// overlay shows the action it would take, so a human can play the run manually with the
/// policy as an advisor. Press Play on the overlay to hand the run over to the bot.
/// Turn this off for autonomous grind runs, which should start driving immediately.
/// </summary>
public static bool StartPaused { get; set; } = true;
```

`SimpleModConfig` auto-generates the settings-screen checkbox from the static property, the
same way it does for `SelfChecks` and `ShowOverlay`. No UI work is needed.

## Component 2 — `StartBotRun` honors it

`BotController.StartBotRun` currently hard-codes `Paused = false` (`BotController.cs:296`) and
pins `ReplayDispatcher.GameSpeed = SpireBotConfig.GameSpeed` unconditionally
(`BotController.cs:294`). Both change.

```csharp
// A pause left over from a previous run must not silently hold the new one, and a step or
// preview held when the last run ended must not survive into this one. The Paused setter
// discards both only on the false transition, so assigning StartPaused directly could carry
// a stale preview across runs — where Step would then commit a command chosen against a
// board that no longer exists. Clear first, then apply the configured starting state.
Paused = false;
Paused = SpireBotConfig.StartPaused;
// Must follow the pause assignment: the effective speed depends on it.
ApplyEffectiveSpeed();
```

The two-assignment shape is deliberate and carries the comment above; it is the reset, not a
redundant write.

## Component 3 — one owner for the effective-speed rule

The selected-vs-applied speed rule (`1.0` while paused, `SpireBotConfig.GameSpeed` otherwise)
currently lives only in `BotControlBar.ApplyEffectiveSpeed`, which is `private` to an overlay
node. A run that starts paused needs the same rule, and a second copy of it would drift.

Move the rule to `BotController` as a public static method, and have
`BotControlBar.ApplyEffectiveSpeed` delegate to it. `BotController` is the right owner: it
holds `Paused`, and it is already the site that writes `ReplayDispatcher.GameSpeed` at run
start.

```csharp
/// <summary>
/// Pushes the multiplier the game should actually be running at right now: the user's
/// selected speed while the bot is deciding, 1.0 while paused so the board can be read — and
/// so a run that starts paused for manual play does not run the game at grind speed.
///
/// This MUST go through ReplayDispatcher.GameSpeed rather than assigning Engine.TimeScale
/// directly. The vendored ExecuteNext re-asserts TimeScale from its own _gameSpeed field on
/// every dispatch (Replay/ReplayDispatcher.cs:1112-1114), above its own paused check, so a
/// direct assignment here would be stomped back to the fast value within a frame.
/// </summary>
public static void ApplyEffectiveSpeed()
{
    ReplayDispatcher.GameSpeed = Paused ? 1.0f : SpireBotConfig.GameSpeed;
}
```

The full TimeScale rationale currently in `BotControlBar.ApplyEffectiveSpeed`'s doc comment
moves with the code (condensed above); the delegating method keeps a one-line summary
pointing at the new owner. Vendored files under `SpireBotCode/Replay/` are not touched.

## Component 4 — refresh cadence while playing manually

No code change; this section records the expected behavior so acceptance can check it.

While paused, `OnHeartbeat` (1 Hz) calls `OnInputRequired`, which classifies the cycle as a
preview and calls `RunDecision`. `RunDecision` returns early when `BuildDecisionKey(ctx)`
equals the held preview's key, skipping `ActionMap.Build`, `ObsBuilder.Build`, and ONNX
inference entirely. The key covers decision kind, room type, act, floor, current HP, gold, and
the available-command list, so a card the human plays changes the available set and refreshes
the advice within one second, while an idle board costs no inference.

## Component 5 — overlay

No change. `ThinkingOverlay` already renders `Will take: {command}` from
`BotController.PendingPreviewDescription` whenever a preview is held, and `StartBotRun`
already calls `ThinkingOverlay.Ensure()`. `BotControlBar.RefreshControls` already renders the
pause button as `▶ Play` and shows Step whenever `BotController.Paused` is true, so a run that
starts paused shows the correct controls on its first frame with no extra wiring.

## Error handling

No new failure modes. `ApplyEffectiveSpeed` is a single field write against a static the mod
already owns. If `OnnxModelPath` is unset the run falls back to `FirstLegalPolicy` exactly as
it does today — advisor mode then displays that policy's choice, which is existing, logged
behavior rather than a new one.

## Testing

Automated assertions go in `BotControlSelfTest.Run()`, the established pattern here (there is
no C# test project; the self-test is gated on `SpireBotConfig.SelfChecks` and never throws out
of `Run`). It executes at mod init with no run active, so it may only exercise pure state
transitions:

- `ApplyEffectiveSpeed()` with `Paused == true` leaves `ReplayDispatcher.GameSpeed` at `1.0`.
- `ApplyEffectiveSpeed()` with `Paused == false` leaves it at `SpireBotConfig.GameSpeed`.
- The test restores `BotController.Paused` and `ReplayDispatcher.GameSpeed` to their
  pre-test values afterward, so the self-test cannot leave the mod in a paused or
  wrong-speed state.

`StartBotRun` itself cannot be exercised at init (it starts a real run), so "a run starts
paused" is verified in-game.

Manual acceptance, folded into the outstanding acceptance pass for the two prior features:

1. With `StartPaused` on (default), start a bot run: it begins paused, the button reads
   `▶ Play`, Step is visible, and the overlay shows `Will take: ...` within about a second.
2. Play several actions manually by clicking the game UI. The run advances, and the advice
   updates after each action.
3. Confirm the game is running at `1.0x` while paused even with the speed selector set
   above `1.0x`, and that pressing Play switches to the selected speed.
4. Press Play mid-run: the bot takes over from the current board state and plays on.
5. Turn `StartPaused` off and start a run: it begins driving immediately, as before.

## Out of scope

- Attaching the bot as an advisor to a run started from the game's own New Run menu.
  `BotController` only observes runs it launched itself; that attach path is separate work.
- Any change to the two non-blocking Minors ledgered from the preview review.
