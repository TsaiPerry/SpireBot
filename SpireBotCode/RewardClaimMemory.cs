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
/// <see cref="TakeCardCommand.IsSkip"/>, this class remembers the claim as declined for the
/// rest of the room so <c>ActionMap.BuildRewardScreen</c>/TryAutoAdvance never re-offer/re-open
/// it (brief #3).
///
/// ── Identity is the reward OBJECT, never the button index (2026-08-20 run audit) ─────────
/// Everything here used to key on <see cref="ClaimRewardCommand.RewardIndex"/>. The JKZ3B820B6
/// audit proved that identity wrong: a reward set can hold SEVERAL card rewards (Orobas's
/// act-boundary gift), each successful claim removes its button and the rest shift down, so
/// "reward [0]" named a different, fresh reward on every auto-advance — the loop backstop
/// counted three legitimate, resolved claims as "the same claim never resolving" and
/// force-declined a brand-new reward (godot.log:7852, RewardLoopGuard in room 'Event@1').
/// Keying by the game's reward object (via
/// <see cref="RewardScreenClassifier.ResolveReward"/>) makes a shifted-down NEW reward start
/// with a clean count, and a resolved reward's entry simply becomes unreachable when its
/// button disappears. Reference equality on the game's own <c>Reward</c> instances; entries
/// are room-scoped (cleared in <see cref="OnDecision"/>) so nothing leaks.
///
/// Per-room like <see cref="RoomOneShots"/> (see that class's doc for why: the record clears
/// whenever the room identity changes) — cleared on room change (<see cref="OnDecision"/>) and
/// on run start (<see cref="Reset"/>).
/// </summary>
internal static class RewardClaimMemory
{
    // Brief #4 — loop backstop: any OTHER non-consuming path (not just TakeCardCommand.IsSkip)
    // would otherwise still spin forever, since ActionExecutor.Execute has no way to know a
    // dispatch "didn't work" beyond ExecuteResult. Once the SAME reward object has been
    // auto-advanced this many times in one room without ever resolving (claimed for good, or
    // explicitly declined via Record below), TryBeginAutoAdvance forces a decline itself.
    private const int MaxAutoAdvancesBeforeForcedDecline = 3;

    private static string? _roomKey;
    private static readonly HashSet<object> _declined = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<object, int> _autoAdvanceCounts = new(ReferenceEqualityComparer.Instance);

    // The card-reward object whose claim was most recently auto-advanced (i.e. whose picker is
    // currently open, or was the last one opened) — set by TryBeginAutoAdvance right before
    // BotController.TryAutoAdvance dispatches the claim, so the TakeCardCommand.IsSkip that
    // follows can be joined back to the reward whose claim opened it. Per the brief #1:
    // "at skip-dispatch time the picker is open, so record the claim that opened it (track it
    // when the claim auto-advances ...); that is the reliable join."
    private static object? _openReward;

    internal static void Reset()
    {
        _roomKey = null;
        _declined.Clear();
        _autoAdvanceCounts.Clear();
        _openReward = null;
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
        _openReward = null;
    }

    /// <summary>True once <paramref name="reward"/> has been explicitly declined (or
    /// force-declined by the loop backstop) this room. <c>ActionMap.BuildRewardScreen</c> must
    /// never map a <see cref="ClaimRewardCommand"/> for a declined reward (brief #3), and
    /// <see cref="IsPendingCardClaim"/> already excludes them from auto-advance too.</summary>
    internal static bool IsDeclined(object reward) => _declined.Contains(reward);

    /// <summary>
    /// True when <paramref name="claim"/> targets a CardReward (the real pick is the
    /// <see cref="TakeCardCommand"/> that follows — see this class's doc / PassiveDump.cs:325-350
    /// round 5), has not already been declined this room, AND the claim would actually land
    /// (<see cref="Affordance.RewardClaimWouldLand"/> — the local player is viewing the reward
    /// set and this reward is on its top; a stale terminal screen's claims resolve fine but
    /// their set was already popped, and clicking them throws inside a swallowed task — the
    /// 2026-08-20 audit's silent no-op claim, godot.log:7774). Pure query, no side effects —
    /// safe to call from both <c>ActionMap.BuildRewardScreen</c> (to decide what NOT to map)
    /// and <see cref="BotController.TryAutoAdvance"/> (to pick an auto-advance candidate).
    /// </summary>
    internal static bool IsPendingCardClaim(ClaimRewardCommand claim)
    {
        object? reward = RewardScreenClassifier.ResolveReward(claim.RewardIndex);
        if (reward == null || IsDeclined(reward))
            return false;
        if (RewardScreenClassifier.KindOf(reward) != RewardKind.Card)
            return false;

        return Affordance.RewardClaimWouldLand(reward);
    }

    /// <summary>Read-only probe of <see cref="TryBeginAutoAdvance"/>'s decision, for the paused
    /// preview (paused-action-preview spec): selection during a preview must not mutate the
    /// loop-backstop counter, or aborted previews would burn claim attempts the bot never made.
    /// The probe and the deferred real call cannot disagree — the counter mutates only on
    /// commits and unpaused dispatches, neither of which can interleave between a preview and
    /// its own commit. Declined/stale rewards are already excluded upstream by
    /// <see cref="IsPendingCardClaim"/>.</summary>
    internal static bool CanAutoAdvance(ClaimRewardCommand claim)
    {
        object? reward = RewardScreenClassifier.ResolveReward(claim.RewardIndex);
        if (reward == null)
            return false;
        return (_autoAdvanceCounts.TryGetValue(reward, out int c) ? c : 0)
               < MaxAutoAdvancesBeforeForcedDecline;
    }

    /// <summary>
    /// Called by <see cref="BotController.TryAutoAdvance"/> right before it would dispatch
    /// <paramref name="claim"/> as an interstitial. Applies the loop backstop (brief #4): once
    /// this reward has already been auto-advanced <see cref="MaxAutoAdvancesBeforeForcedDecline"/>
    /// times without resolving, forces a decline (loud log) and returns false instead of
    /// dispatching a 4th time. Otherwise records this claim's reward as the currently-open one
    /// (for <see cref="Record"/>'s join), bumps the count, and returns true.
    /// </summary>
    internal static bool TryBeginAutoAdvance(ClaimRewardCommand claim)
    {
        object? reward = RewardScreenClassifier.ResolveReward(claim.RewardIndex);
        if (reward == null)
        {
            // A claim whose reward can't even be resolved must not dispatch (and must not be
            // counted or declined — the prompt's rule: a claim that did NOT execute is not a
            // decline). IsPendingCardClaim already filters these; this is belt-and-braces for
            // a screen freed between selection and here.
            GD.PrintErr($"[SpireBot] RewardClaimMemory: claim [{claim.RewardIndex}] no longer resolves " +
                        "to a reward on the current screen — not dispatching it.");
            return false;
        }

        int count = _autoAdvanceCounts.TryGetValue(reward, out int c) ? c : 0;
        if (count >= MaxAutoAdvancesBeforeForcedDecline)
        {
            GD.PrintErr($"[SpireBot] RewardLoopGuard: reward [{claim.RewardIndex}] ({reward.GetType().Name}) " +
                        $"auto-advanced {count}x in room '{_roomKey}' without ever resolving — forcing a decline " +
                        "to break the loop (fix-reward-loop-brief.md #4 backstop).");
            _declined.Add(reward);
            _openReward = null;
            return false;
        }

        _autoAdvanceCounts[reward] = count + 1;
        _openReward = reward;
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

        if (_openReward is not { } reward)
        {
            // Should not happen on the live path (TryBeginAutoAdvance always sets this right
            // before the claim that opens the subscreen dispatches), but fail loud rather than
            // silently mis-joining: without a reward to key on, this decline can't be
            // remembered and BuildRewardScreen will offer the claim again next decision. The
            // backstop (brief #4) is the safety net if that ever happens.
            GD.PrintErr("[SpireBot] RewardClaimMemory: TakeCardCommand.IsSkip dispatched with no " +
                        "open claim on record — cannot join it back to a reward; relying on " +
                        "the loop backstop instead (fix-reward-loop-brief.md #4).");
            return;
        }

        if (_declined.Add(reward))
            GD.Print($"[SpireBot] RewardClaimMemory: card reward ({reward.GetType().Name}) declined by the " +
                     $"policy's picker skip — suppressing its claim for the rest of room '{_roomKey}'.");

        _openReward = null;
    }
}
