using System;

using Godot;

using SpireBot.SpireBotCode.Overlay;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Startup sanity check for the decision-pacing pure logic (2026-08-20 UX pacing spec §1–2),
/// in the same shape as <see cref="BotControlSelfTest"/>: runs once at mod init under
/// <see cref="SpireBotConfig.SelfChecks"/>, logs OK or FAILED, never throws.
/// </summary>
public static class PacingSelfTest
{
    // Mirrors [ConfigSlider(0.5, 10, 0.5)] on SpireBotConfig.DecisionSpeed. Duplicated as
    // literals on purpose — fail loudly if the ladder and the slider ever drift apart.
    private const float SliderMin = 0.5f;
    private const float SliderMax = 10.0f;
    private const float SliderStep = 0.5f;
    private const double Tolerance = 0.001;

    public static void Run()
    {
        try
        {
            // Every kind: the reading dwell is never shorter than the between-actions dwell.
            foreach (DecisionKind kind in Enum.GetValues<DecisionKind>())
            {
                double reading = PacingPlan.BaseDwellSeconds(kind, surfaceChanged: true);
                double between = PacingPlan.BaseDwellSeconds(kind, surfaceChanged: false);
                if (reading < between - Tolerance)
                    throw new Exception($"{kind}: reading dwell {reading} < between dwell {between}.");
                if (reading < 0 || between < 0)
                    throw new Exception($"{kind}: negative dwell.");
            }

            // The spec's headline values, verbatim.
            AssertDwell(DecisionKind.Event, true, 2.5);
            AssertDwell(DecisionKind.Shop, true, 2.0);
            AssertDwell(DecisionKind.Rest, true, 1.5);
            AssertDwell(DecisionKind.RewardScreen, true, 1.5);
            AssertDwell(DecisionKind.Combat, false, 0.8);
            AssertDwell(DecisionKind.Map, false, 1.5);

            // Unsupported is stall recovery — it must never dwell.
            AssertDwell(DecisionKind.Unsupported, true, 0.0);
            AssertDwell(DecisionKind.Unsupported, false, 0.0);

            // Speed scaling: divides; Instant skips entirely.
            double at1x = PacingPlan.DwellSeconds(DecisionKind.Event, true, 1.0f);
            double at2x = PacingPlan.DwellSeconds(DecisionKind.Event, true, 2.0f);
            if (Math.Abs(at1x - 2.5) > Tolerance || Math.Abs(at2x - 1.25) > Tolerance)
                throw new Exception($"speed scaling wrong: 1x={at1x}, 2x={at2x}.");
            if (PacingPlan.DwellSeconds(DecisionKind.Event, true, PacingPlan.InstantSpeed) != 0.0)
                throw new Exception("InstantSpeed must produce a zero dwell.");
            if (!PacingPlan.IsInstant(PacingPlan.InstantSpeed) || PacingPlan.IsInstant(3.0f))
                throw new Exception("IsInstant threshold wrong.");

            // Bot ladder: walk up visits every rung in order and clamps at the top, and every
            // rung is representable on the DecisionSpeed config slider.
            float[] steps = SpeedLadder.BotSteps;
            if (steps.Length < 2)
                throw new Exception($"BotSteps needs at least 2 rungs, has {steps.Length}.");
            float v = steps[0];
            for (int i = 1; i < steps.Length; i++)
            {
                v = SpeedLadder.Next(steps, v);
                if (Math.Abs(v - steps[i]) > Tolerance)
                    throw new Exception($"Next() rung {i}: expected {steps[i]}, got {v}.");
            }
            if (Math.Abs(SpeedLadder.Next(steps, v) - v) > Tolerance)
                throw new Exception("Next() at the top of BotSteps moved off the top rung.");
            for (int i = steps.Length - 2; i >= 0; i--)
            {
                v = SpeedLadder.Prev(steps, v);
                if (Math.Abs(v - steps[i]) > Tolerance)
                    throw new Exception($"Prev() rung {i}: expected {steps[i]}, got {v}.");
            }
            if (steps[steps.Length - 1] != PacingPlan.InstantSpeed)
                throw new Exception("BotSteps' top rung must be PacingPlan.InstantSpeed.");
            foreach (float step in steps)
            {
                if (step < SliderMin - Tolerance || step > SliderMax + Tolerance)
                    throw new Exception($"BotSteps value {step} is outside the DecisionSpeed slider range.");
                float ticks = step / SliderStep;
                if (Math.Abs(ticks - MathF.Round(ticks)) > Tolerance)
                    throw new Exception($"BotSteps value {step} is not a multiple of the slider's {SliderStep} step.");
            }

            GD.Print("[SpireBot] PacingSelfTest OK");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PacingSelfTest FAILED: {ex}");
        }
    }

    private static void AssertDwell(DecisionKind kind, bool surfaceChanged, double expected)
    {
        double actual = PacingPlan.BaseDwellSeconds(kind, surfaceChanged);
        if (Math.Abs(actual - expected) > Tolerance)
            throw new Exception($"{kind} surfaceChanged={surfaceChanged}: expected {expected}s, got {actual}s.");
    }
}
