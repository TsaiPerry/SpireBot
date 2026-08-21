namespace SpireBot.SpireBotCode.Overlay;

/// <summary>
/// The discrete speed multipliers the overlay's ◀/▶ buttons walk between, and the pure stepping
/// logic over them. Same ladder RunReplays uses for replay playback (RunReplays/RunOverlay.cs:291).
///
/// Deliberately Godot-free: this is the part of the speed control with real off-by-one risk, and
/// keeping it free of engine types lets <see cref="BotControlSelfTest"/> /
/// <see cref="PacingSelfTest"/> exercise it at mod init without a scene tree.
///
/// Two ladders: <see cref="Steps"/> is the game-speed ladder (kept for the config-screen
/// GameSpeed slider's self-test), <see cref="BotSteps"/> is the DECISION-speed ladder the
/// overlay now steps through (2026-08-20 UX pacing spec §2) — its top rung is
/// <see cref="PacingPlan.InstantSpeed"/>, "no dwell at all".
/// </summary>
public static class SpeedLadder
{
    /// <summary>Game-speed rungs, ascending. Must stay inside SpireBotConfig.GameSpeed's slider
    /// range and land on its step — BotControlSelfTest asserts both.</summary>
    public static readonly float[] Steps = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, 5.0f };

    /// <summary>Decision-speed rungs, ascending. Must stay inside SpireBotConfig.DecisionSpeed's
    /// slider range and land on its step — PacingSelfTest asserts both, plus that the top rung
    /// is PacingPlan.InstantSpeed.</summary>
    public static readonly float[] BotSteps = { 0.5f, 1.0f, 1.5f, 2.0f, 3.0f, PacingPlan.InstantSpeed };

    /// <summary>Comparison slack. The current speed is a float round-tripped through config JSON,
    /// so exact equality against a rung is not safe.</summary>
    private const float Epsilon = 0.01f;

    /// <summary>The lowest rung strictly above <paramref name="current"/>; the top rung when
    /// already at or above it. Never returns a value off the ladder.</summary>
    public static float Next(float current) => Next(Steps, current);

    /// <summary>The highest rung strictly below <paramref name="current"/>; the bottom rung when
    /// already at or below it. Never returns a value off the ladder.</summary>
    public static float Prev(float current) => Prev(Steps, current);

    /// <summary>Same rule as <see cref="Next(float)"/> over an arbitrary ascending ladder.</summary>
    public static float Next(float[] steps, float current)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            if (steps[i] > current + Epsilon)
                return steps[i];
        }
        return steps[steps.Length - 1];
    }

    /// <summary>Same rule as <see cref="Prev(float)"/> over an arbitrary ascending ladder.</summary>
    public static float Prev(float[] steps, float current)
    {
        for (int i = steps.Length - 1; i >= 0; i--)
        {
            if (steps[i] < current - Epsilon)
                return steps[i];
        }
        return steps[0];
    }
}
