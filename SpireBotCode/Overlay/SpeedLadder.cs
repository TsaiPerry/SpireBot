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
