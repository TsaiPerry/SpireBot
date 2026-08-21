using System;

using Godot;

using SpireBot.Replay;
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

            // Effective-speed rule (2026-08-20 UX pacing spec §2): GameSpeed is a plain
            // user setting applied whether or not autopilot is on — pause no longer forces
            // 1.0x, because decision pacing is now DecisionSpeed's job and never TimeScale's.
            // Both the pause flag and the dispatcher speed are restored below — this runs at
            // mod init, and leaving either changed would mis-start the next run.
            bool pausedBefore = BotController.Paused;
            float dispatcherSpeedBefore = ReplayDispatcher.GameSpeed;
            float selected = SpireBotConfig.GameSpeed;

            BotController.Paused = true;
            BotController.ApplyEffectiveSpeed();
            if (Math.Abs(ReplayDispatcher.GameSpeed - selected) > Tolerance)
                throw new Exception(
                    $"paused must still apply the selected {selected}x, got {ReplayDispatcher.GameSpeed}.");

            BotController.Paused = false;
            BotController.ApplyEffectiveSpeed();
            if (Math.Abs(ReplayDispatcher.GameSpeed - selected) > Tolerance)
                throw new Exception(
                    $"unpaused must apply the selected {selected}x, got {ReplayDispatcher.GameSpeed}.");

            BotController.Paused = pausedBefore;
            ReplayDispatcher.GameSpeed = dispatcherSpeedBefore;

            GD.Print($"[SpireBot] BotControlSelfTest OK (ladder: {string.Join(", ", steps)})");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotControlSelfTest FAILED: {ex}");
        }
    }
}
