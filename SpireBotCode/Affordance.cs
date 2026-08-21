using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBot.SpireBotCode;

/// <summary>
/// "Would a real player's click land right now?" — the bot's rule for touching game UI.
///
/// Every clickable control in the game derives from <see cref="NClickableControl"/>, and a real
/// click is gated in the INPUT path, not in the handler
/// (<c>src/Core/Nodes/GodotExtensions/NClickableControl.cs:107</c>):
/// <code>
/// if (_isEnabled &amp;&amp; IsVisibleInTree() &amp;&amp; IsFocused &amp;&amp; ...) OnReleaseHandler();
/// </code>
/// Because the handler never re-checks, emitting <c>Released</c> directly — which is what the
/// vendored replay commands do, and what <c>NClickableControl.ForceClick()</c> ("Used by AutoSlay
/// for automated testing", :255) does too — bypasses the gate entirely. That is how the bot
/// re-opened an already-opened treasure chest: <c>NTreasureRoom.OnChestButtonReleased</c> calls
/// <c>_chestButton.Disable()</c> right after opening, so a player physically cannot click it
/// again, but a raw signal emit re-ran <c>OpenChest()</c> and minted fresh relics every time.
///
/// A recorded replay never had to care — its script only ever contains clicks a player really
/// made. A live policy does, so the bot re-applies the game's own precondition before acting.
///
/// <see cref="NClickableControl.IsEnabled"/> is public, so no publicizer is needed.
/// <c>IsFocused</c> (hover/controller focus) is deliberately NOT required: it reflects where the
/// mouse happens to be, which is a property of the input device rather than of the affordance.
/// </summary>
internal static class Affordance
{
    /// <summary>True when a real click on this control would be accepted by the game.</summary>
    internal static bool IsLive(NClickableControl? control)
        => control != null
        && GodotObject.IsInstanceValid(control)
        && control.IsInsideTree()
        && control.IsEnabled
        && control.IsVisibleInTree();

    /// <summary>
    /// True when the treasure room is actually accepting a relic pick.
    ///
    /// Not every game precondition is a button: <c>TreasureRoomRelicSynchronizer
    /// .PickRelicLocally</c> throws <c>InvalidOperationException("Attempted to pick relic while
    /// relic picking is not active!")</c> whenever <c>_currentRelics</c> is null
    /// (<c>src/Core/Multiplayer/Game/TreasureRoomRelicSynchronizer.cs:161</c>) — before the
    /// chest is opened and again after the relic is taken. The dispatcher offers
    /// TakeChestRelicCommand for the whole treasure room regardless, so the bot picked it
    /// forever, throwing each time and never reaching the proceed button. The state is public
    /// as <c>CurrentRelics</c>, so the bot can test the same condition the game does.
    /// </summary>
    internal static bool RelicPickingActive()
    {
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
        return sync?.CurrentRelics is { Count: > 0 };
    }

    /// <summary>
    /// True when the LOCAL player is currently viewing a reward set — the exact state-level
    /// precondition <c>RewardsSetSynchronizer.SelectLocalReward</c> enforces
    /// (src/Core/Multiplayer/Game/RewardsSetSynchronizer.cs:201: throws "Tried to sync reward
    /// for local player, but they are not currently viewing any reward set!" whenever
    /// <c>GetRewardStateForPlayer(LocalPlayer).rewardsStack.Count &lt;= 0</c>).
    ///
    /// "Viewing" has no public predicate and no explicit begin/end call: it is implicitly true
    /// from <c>BeginRewardsSet</c>'s push (RewardsSetSynchronizer.cs:166, run synchronously from
    /// <c>RewardsSet.Offer()</c> BEFORE the rewards screen even exists — RewardsSet.cs:161 vs
    /// :192) until the set completes, is skipped, or <c>BeforeLeavingRoom()</c> force-clears the
    /// whole stack on a room exit (RewardsSetSynchronizer.cs:317/344/400). That last writer is
    /// the live trap: the game NEVER frees a terminal NRewardsScreen (showcase bug 2), so its
    /// buttons stay enumerable and Affordance-live after the stack was already popped — clicking
    /// one then throws inside the swallowed task (observed live: JKZ3B820B6 godot.log:7774,
    /// a post-act-transition auto-advance claim). Same defect family as delta 16's chest
    /// (control-level) and the treasure relic pick (state-level, stall #10): re-apply the game's
    /// own precondition, form (2) of the "abide by the game's rules" rule.
    ///
    /// Private members (<c>GetRewardStateForPlayer</c>, <c>LocalPlayer</c>,
    /// <c>PlayerRewardState.rewardsStack</c>) are reachable because the csproj publicizes the
    /// whole sts2 assembly. Fail-closed: any surprise here means "do not click".
    /// </summary>
    internal static bool RewardViewingActive()
    {
        try
        {
            var sync = RunManager.Instance?.RewardsSetSynchronizer;
            if (sync == null)
                return false;
            return sync.GetRewardStateForPlayer(sync.LocalPlayer).rewardsStack.Count > 0;
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[SpireBot] Affordance.RewardViewingActive threw: {ex.Message} — fail-closed (false).");
            return false;
        }
    }

    /// <summary>
    /// True when clicking <paramref name="reward"/>'s button right now would actually land:
    /// the local player is viewing a reward set AND this reward belongs to the set on TOP of
    /// the stack — <c>SelectLocalReward</c> always operates on <c>rewardsStack.Last()</c>
    /// (RewardsSetSynchronizer.cs:205-207), so a reward from any other set would be silently
    /// mis-selected against the wrong set rather than throwing.
    /// </summary>
    internal static bool RewardClaimWouldLand(object? reward)
    {
        try
        {
            if (reward is not MegaCrit.Sts2.Core.Rewards.Reward typed)
                return false;
            var sync = RunManager.Instance?.RewardsSetSynchronizer;
            if (sync == null)
                return false;
            var stack = sync.GetRewardStateForPlayer(sync.LocalPlayer).rewardsStack;
            if (stack.Count == 0)
                return false;
            return stack[stack.Count - 1].set.Rewards.Contains(typed);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[SpireBot] Affordance.RewardClaimWouldLand threw: {ex.Message} — fail-closed (false).");
            return false;
        }
    }

    /// <summary>
    /// Logs and returns false when the control is not clickable — call sites use this to refuse
    /// the action instead of forcing it through.
    /// </summary>
    internal static bool CheckOrRefuse(NClickableControl? control, string what)
    {
        if (IsLive(control))
            return true;

        GD.Print($"[SpireBot] Affordance: refusing '{what}' — the game has that control " +
                 $"{(control == null || !GodotObject.IsInstanceValid(control) ? "gone" : "disabled/hidden")}, " +
                 "so a player could not click it either.");
        return false;
    }
}
