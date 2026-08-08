using System.Collections.Generic;

using Godot;

using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Remembers card-reward claims the live bot has explicitly DECLINED, per current room, so
/// <see cref="ActionMap.Build"/>/<see cref="BotController"/> stop re-offering/re-opening the
/// same claim forever.
///
/// Root cause (fix-reward-loop-brief.md): <c>ActionMap.BuildRewardScreen</c> used to map a card
/// reward's <see cref="ClaimRewardCommand"/> as a model decision; the model claims it (opening
/// the card-pick subscreen), then picks "Skip card reward" =
/// <see cref="TakeCardCommand.IsSkip"/>, whose game-side alternative is
/// <c>EndSelectionAndDoNotCompleteReward</c> (TakeCardCommand.cs:119-120) — a NON-CONSUMING
/// back-out. The reward stays claimable, obs are identical, the deterministic policy repeats
/// forever. Fix: a card reward's claim is now auto-dispatched as an interstitial
/// (<see cref="BotController.TryAutoAdvance"/>, brief #2) rather than offered as a choice; once
/// the model, inside the subscreen that opens, actually declines via
/// <see cref="TakeCardCommand.IsSkip"/>, this class remembers the claim's RewardIndex as
/// declined for the rest of the room so <c>ActionMap.BuildRewardScreen</c>/TryAutoAdvance never
/// re-offer/re-open it (brief #3).
///
/// Per-room like <see cref="RoomOneShots"/> (see that class's doc for why: the record clears
/// whenever the room identity changes) — cleared on room change (<see cref="OnDecision"/>) and
/// on run start (<see cref="Reset"/>).
/// </summary>
internal static class RewardClaimMemory
{
    // Brief #4 — loop backstop: any OTHER non-consuming path (not just TakeCardCommand.IsSkip)
    // would otherwise still spin forever, since ActionExecutor.Execute has no way to know a
    // dispatch "didn't work" beyond ExecuteResult. Once the SAME RewardIndex has been
    // auto-advanced this many times in one room without ever resolving (claimed for good, or
    // explicitly declined via Record below), TryBeginAutoAdvance forces a decline itself.
    private const int MaxAutoAdvancesBeforeForcedDecline = 3;

    private static string? _roomKey;
    private static readonly HashSet<int> _declined = new();
    private static readonly Dictionary<int, int> _autoAdvanceCounts = new();

    // RewardIndex of the card-reward claim most recently auto-advanced (i.e. whose picker is
    // currently open, or was the last one opened) — set by TryBeginAutoAdvance right before
    // BotController.TryAutoAdvance dispatches the claim, so the TakeCardCommand.IsSkip that
    // follows can be joined back to the ClaimRewardCommand that opened it. Per the brief #1:
    // "at skip-dispatch time the picker is open, so record the claim RewardIndex that opened it
    // (track it when the claim auto-advances ...); that is the reliable join."
    private static int? _openRewardIndex;

    internal static void Reset()
    {
        _roomKey = null;
        _declined.Clear();
        _autoAdvanceCounts.Clear();
        _openRewardIndex = null;
    }

    /// <summary>Call once per decision, before building the action map — mirrors
    /// <see cref="RoomOneShots.OnDecision"/>'s room-identity key exactly, so both memories reset
    /// on the same room boundary.</summary>
    internal static void OnDecision(DecisionContext ctx)
    {
        string key = $"{ctx.Snapshot.RoomType ?? "<none>"}@{ctx.Snapshot.Floor}";
        if (key == _roomKey)
            return;

        _roomKey = key;
        _declined.Clear();
        _autoAdvanceCounts.Clear();
        _openRewardIndex = null;
    }

    /// <summary>True once <paramref name="rewardIndex"/> has been explicitly declined (or
    /// force-declined by the loop backstop) this room. <c>ActionMap.BuildRewardScreen</c> must
    /// never map a <see cref="ClaimRewardCommand"/> for a declined index (brief #3), and
    /// <see cref="IsPendingCardClaim"/> already excludes them from auto-advance too.</summary>
    internal static bool IsDeclined(int rewardIndex) => _declined.Contains(rewardIndex);

    /// <summary>
    /// True when <paramref name="claim"/> targets a CardReward (the real pick is the
    /// <see cref="TakeCardCommand"/> that follows — see this class's doc / PassiveDump.cs:325-350
    /// round 5) and has not already been declined this room. Pure query, no side effects — safe
    /// to call from both <c>ActionMap.BuildRewardScreen</c> (to decide what NOT to map) and
    /// <see cref="BotController.TryAutoAdvance"/> (to pick an auto-advance candidate).
    /// </summary>
    internal static bool IsPendingCardClaim(ClaimRewardCommand claim)
    {
        if (IsDeclined(claim.RewardIndex))
            return false;

        return RewardScreenClassifier.ResolveRewardKind(claim.RewardIndex) == RewardKind.Card;
    }

    /// <summary>
    /// Called by <see cref="BotController.TryAutoAdvance"/> right before it would dispatch
    /// <paramref name="claim"/> as an interstitial. Applies the loop backstop (brief #4): once
    /// this RewardIndex has already been auto-advanced <see cref="MaxAutoAdvancesBeforeForcedDecline"/>
    /// times without resolving, forces a decline (loud log) and returns false instead of
    /// dispatching a 4th time. Otherwise records this claim as the currently-open one (for
    /// <see cref="Record"/>'s join), bumps the count, and returns true.
    /// </summary>
    /// <summary>Read-only probe of <see cref="TryBeginAutoAdvance"/>'s decision, for the paused
    /// preview (paused-action-preview spec): selection during a preview must not mutate the
    /// loop-backstop counter, or aborted previews would burn claim attempts the bot never made.
    /// The probe and the deferred real call cannot disagree — the counter mutates only on
    /// commits and unpaused dispatches, neither of which can interleave between a preview and
    /// its own commit. Declined indexes are already excluded upstream by
    /// <see cref="IsPendingCardClaim"/>.</summary>
    internal static bool CanAutoAdvance(ClaimRewardCommand claim)
        => (_autoAdvanceCounts.TryGetValue(claim.RewardIndex, out int c) ? c : 0)
           < MaxAutoAdvancesBeforeForcedDecline;

    internal static bool TryBeginAutoAdvance(ClaimRewardCommand claim)
    {
        int count = _autoAdvanceCounts.TryGetValue(claim.RewardIndex, out int c) ? c : 0;
        if (count >= MaxAutoAdvancesBeforeForcedDecline)
        {
            GD.PrintErr($"[SpireBot] RewardLoopGuard: reward [{claim.RewardIndex}] auto-advanced " +
                        $"{count}x in room '{_roomKey}' without ever resolving — forcing a decline " +
                        "to break the loop (fix-reward-loop-brief.md #4 backstop).");
            _declined.Add(claim.RewardIndex);
            _openRewardIndex = null;
            return false;
        }

        _autoAdvanceCounts[claim.RewardIndex] = count + 1;
        _openRewardIndex = claim.RewardIndex;
        return true;
    }

    /// <summary>
    /// Records a dispatched command — call shape mirrors <see cref="RoomOneShots.Record"/> from
    /// <see cref="ActionExecutor.Execute"/>. Only <see cref="TakeCardCommand.IsSkip"/> is
    /// meaningful here (brief #1: "IsSacrifice? NO — sacrifice consumes the reward by itself,
    /// leave it alone" — a sacrifice pick actually resolves the reward, it is not a
    /// non-consuming back-out like Skip, so it must not be recorded as declined).
    /// </summary>
    internal static void Record(ReplayCommand cmd)
    {
        if (cmd is not TakeCardCommand take || !take.IsSkip)
            return;

        if (_openRewardIndex is not int rewardIndex)
        {
            // Should not happen on the live path (TryBeginAutoAdvance always sets this right
            // before the claim that opens the subscreen dispatches), but fail loud rather than
            // silently mis-joining: without a RewardIndex to key on, this decline can't be
            // remembered and BuildRewardScreen will offer the claim again next decision. The
            // backstop (brief #4) is the safety net if that ever happens.
            GD.PrintErr("[SpireBot] RewardClaimMemory: TakeCardCommand.IsSkip dispatched with no " +
                        "open claim on record — cannot join it back to a RewardIndex; relying on " +
                        "the loop backstop instead (fix-reward-loop-brief.md #4).");
            return;
        }

        if (_declined.Add(rewardIndex))
            GD.Print($"[SpireBot] RewardClaimMemory: reward [{rewardIndex}] declined — " +
                     $"suppressing it for the rest of room '{_roomKey}'.");

        _openRewardIndex = null;
    }
}
