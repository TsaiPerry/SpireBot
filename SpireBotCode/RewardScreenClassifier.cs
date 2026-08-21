using System;
using System.Linq;

using Godot;

using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Shared reward-button classification, factored out of
/// <see cref="PassiveDump"/>'s private <c>ResolveClaimedRewardKind</c> (PassiveDump.cs round 8
/// fix 1) so the live <see cref="BotController"/>/<see cref="ActionMap"/> path can ask the exact
/// same question PassiveDump already answers when deciding what to dump: "what TYPE of reward
/// does this ClaimRewardCommand.RewardIndex resolve to on the CURRENT reward screen?"
///
/// Both call sites need byte-identical semantics. PassiveDump uses it to classify which
/// ClaimRewardCommand dispatches are real sim decisions vs. subscreen-open interstitials
/// (PassiveDump.cs:325-350, round 5: a CardReward button only opens the pick-a-card subscreen —
/// the real decision is the <c>TakeCardCommand</c> that follows). The live fix
/// (fix-reward-loop-brief.md) needs the SAME classification to decide which claims must be
/// auto-dispatched instead of offered to the model as a choice — that mismatch (ClaimReward on
/// a card reward mapped as a real decision) is the root cause of the claim/skip infinite loop.
///
/// Resolves via the VENDORED <see cref="ClaimRewardCommand.EnumerateRewardButtons"/> against the
/// VENDORED <see cref="SpireBot.Replay.ReplayState.ActiveRewardsScreen"/> — never the fork's own
/// command/screen types, same as every other resolver in this codebase. Fail-open toward
/// <c>null</c> (caller decides the safe default) whenever the reward can't be resolved (freed
/// screen, out-of-range index, ...).
/// </summary>
internal static class RewardScreenClassifier
{
    internal static RewardKind? ResolveRewardKind(int rewardIndex)
    {
        object? reward = ResolveReward(rewardIndex);
        return reward == null ? null : KindOf(reward);
    }

    /// <summary>
    /// The game-side reward OBJECT a <see cref="ClaimRewardCommand.RewardIndex"/> resolves to on
    /// the CURRENT reward screen, or null when it can't be resolved (freed screen, out-of-range
    /// index). The object — not the index — is a reward's identity: the run-audit
    /// (2026-08-20, JKZ3B820B6 Event@1) proved indexes are NOT stable across claims — a
    /// multi-reward set (Orobas's act-boundary gift = several CardRewards) removes each claimed
    /// button, the rest shift down, and "index 0" names a DIFFERENT reward every time. Anything
    /// remembering per-reward state (<see cref="RewardClaimMemory"/>) must key on this object.
    /// </summary>
    internal static object? ResolveReward(int rewardIndex)
    {
        try
        {
            var screen = SpireBot.Replay.ReplayState.ActiveRewardsScreen;
            if (screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
                return null;

            var buttons = ClaimRewardCommand.EnumerateRewardButtons(screen).ToList();
            if (rewardIndex < 0 || rewardIndex >= buttons.Count)
                return null;

            return buttons[rewardIndex].reward;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] RewardScreenClassifier.ResolveReward threw: {ex.Message} — fail-open (null).");
            return null;
        }
    }

    internal static RewardKind KindOf(object reward)
        => reward.GetType().Name switch
        {
            "RelicReward" => RewardKind.Relic,
            "PotionReward" => RewardKind.Potion,
            "CardReward" => RewardKind.Card,
            _ => RewardKind.None,
        };
}
