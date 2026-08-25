using System;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Pure dwell-time logic for the decision pacing pipeline (2026-08-20 UX pacing spec §1–2).
/// Deliberately Godot-free, same pattern as <see cref="Overlay.SpeedLadder"/>, so
/// <see cref="PacingSelfTest"/> can exercise it at mod init without a scene tree.
///
/// Two tiers per <see cref="DecisionKind"/>: a longer "reading" dwell when the decision
/// surface just changed (a new screen the user needs a moment to take in), and a shorter
/// between-actions dwell for repeat decisions on the same surface. The applied dwell is
/// the base divided by the user's DecisionSpeed multiplier; the top rung
/// (<see cref="InstantSpeed"/>) skips dwells entirely — the pre-pacing behavior, for
/// hands-off grind runs.
/// </summary>
public static class PacingPlan
{
    /// <summary>DecisionSpeed at or above this means "no dwell at all". Also the top rung
    /// of <see cref="Overlay.SpeedLadder.BotSteps"/>, rendered as "Inst" on the overlay.</summary>
    public const float InstantSpeed = 10f;

    private const float Epsilon = 0.01f;

    public static bool IsInstant(float decisionSpeed) => decisionSpeed >= InstantSpeed - Epsilon;

    /// <summary>Seconds the pipeline holds a chosen action before committing it, before
    /// speed scaling. Unsupported is 0 on both tiers: those decisions are stall-recovery
    /// fallbacks, and slowing them slows recovery, not watchability. Map is one flat value —
    /// a node pick is a node pick whether or not the map just opened.</summary>
    public static double BaseDwellSeconds(DecisionKind kind, bool surfaceChanged) => kind switch
    {
        DecisionKind.Event => surfaceChanged ? 2.5 : 1.5,
        DecisionKind.Shop => surfaceChanged ? 2.0 : 1.0,
        DecisionKind.Rest => surfaceChanged ? 1.5 : 1.0,
        DecisionKind.RewardScreen => surfaceChanged ? 1.5 : 1.0,
        DecisionKind.SelectCards => surfaceChanged ? 1.5 : 1.0,
        DecisionKind.SelectOption => surfaceChanged ? 1.5 : 1.0,
        DecisionKind.Combat => surfaceChanged ? 1.0 : 0.55,
        DecisionKind.Map => 1.5,
        _ => 0.0,
    };

    /// <summary>The dwell actually applied: base / DecisionSpeed, 0 when instant. The
    /// divisor floor guards a hand-edited config value of 0 from producing an infinite dwell.</summary>
    public static double DwellSeconds(DecisionKind kind, bool surfaceChanged, float decisionSpeed)
    {
        if (IsInstant(decisionSpeed)) return 0.0;
        return BaseDwellSeconds(kind, surfaceChanged) / Math.Max(decisionSpeed, 0.1f);
    }

    /// <summary>Card-pick dwell: a TakeCardCommand on the reward screen (choosing WHICH card
    /// joins the deck) gets a longer hold than the generic RewardScreen clicks (claims, chest,
    /// proceed) that share its DecisionKind — the pressed holder takes the card instantly, so
    /// unlike grid select there is no post-click highlight to hold; all the watch-time must
    /// come before the press. Same shape as BaseDwellSeconds/DwellSeconds.</summary>
    public static double CardPickBaseDwellSeconds(bool surfaceChanged) => surfaceChanged ? 2.5 : 2.0;

    public static double CardPickDwellSeconds(bool surfaceChanged, float decisionSpeed)
    {
        if (IsInstant(decisionSpeed)) return 0.0;
        return CardPickBaseDwellSeconds(surfaceChanged) / Math.Max(decisionSpeed, 0.1f);
    }

    /// <summary>Seconds a grid-card selection stays HIGHLIGHTED before the bot confirms it
    /// (SelectGridCardCommand's click→confirm hold). The pre-decision SelectCards dwell shows
    /// the screen, but click+confirm+close used to land in one frame, so the user never saw
    /// WHICH card was picked. Scaled by DecisionSpeed like every dwell; 0 when instant. Must
    /// stay well under the executor's 5s stale-command drop.</summary>
    private const double SelectionHoldBase = 0.8;

    public static double SelectionHoldSeconds(float decisionSpeed)
    {
        if (IsInstant(decisionSpeed)) return 0.0;
        // Cap keeps a hand-edited sub-slider speed (divisor floor 0.1 → 8s) from tripping
        // the executor's 5s stale-command drop mid-hold.
        return Math.Min(SelectionHoldBase / Math.Max(decisionSpeed, 0.1f), 4.0);
    }
}
