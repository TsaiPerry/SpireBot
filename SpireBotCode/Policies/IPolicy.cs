// Obs.Obs collides with the SpireBot.SpireBotCode.Obs namespace itself once both are in
// scope (CS0118) — alias it, matching BotController.cs/DecisionDumper.cs's identical pattern.
using ObsResult = SpireBot.SpireBotCode.Obs.Obs;

namespace SpireBot.SpireBotCode.Policies;

/// <summary>
/// A policy's chosen action for one <see cref="DecisionContext"/>: the flat action id
/// (contract action-space index, as produced by <see cref="ActionMap"/>) plus an optional
/// top-K breakdown for overlay/diagnostic display.
/// </summary>
public sealed class PolicyResult
{
    /// <summary>The chosen action id. Must be a slot <see cref="ActionMap.Mask"/> marks legal.</summary>
    public int ActionId;

    /// <summary>
    /// Up to K (label, probability) pairs describing the policy's ranked choices, most likely
    /// first. Stub policies (e.g. <see cref="FirstLegalPolicy"/>) report a single 100% entry;
    /// <see cref="OnnxPolicy"/> (Task 14) reports its real action-head distribution here.
    /// </summary>
    public (string label, float prob)[] TopK = System.Array.Empty<(string, float)>();
}

/// <summary>
/// Chooses one legal action for a captured decision. Implementations must never return an
/// action id the given <see cref="ActionMap"/> masks off — <see cref="BotController"/> trusts
/// <see cref="PolicyResult.ActionId"/> to resolve directly via <c>ActionMap.CommandFor</c>.
/// </summary>
public interface IPolicy
{
    /// <summary>
    /// <paramref name="obs"/> is the same flat observation <see cref="Obs.ObsBuilder"/> built for
    /// this decision (Task 14 wired <see cref="BotController"/> to always build one, not just
    /// under a debug build/DumpDecisions), or null if that build itself failed for this
    /// decision. A policy that does not need obs (<see cref="FirstLegalPolicy"/>) ignores it;
    /// <see cref="OnnxPolicy"/> requires it to run inference and falls back to
    /// <see cref="FirstLegalPolicy"/> when it is null.
    /// </summary>
    PolicyResult Choose(DecisionContext ctx, ActionMap map, ObsResult? obs);
}
