using Godot;
using MegaCrit.Sts2.Core.Combat;
using SpireBot.Replay;
using SpireBot.Replay.Patches.Replay;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Clears in-flight flags that got stuck set, which otherwise disable the bot permanently.
///
/// <c>ReplayDispatcher.GetDispatchableTypes</c> opens with a <c>blocked</c> early-return: if
/// <see cref="ReplayState.CardPlayInFlight"/>, <see cref="ReplayState.PotionInFlight"/>,
/// <see cref="ReplayState.ActionInFlight"/>, <c>MapMoveInFlight</c>, or the end-turn gate is
/// set, it enumerates (almost) nothing. Those flags are set when a command starts and cleared
/// by the replay-side completion patches — but only along paths the replay engine's own
/// dispatch chain drives (<c>CardPlayReplayPatch.AfterActionExecuted</c> returns early unless
/// <c>_dispatching</c> is still true when the action completes). A bot dispatch that loses that
/// race leaves the flag set forever: observed live as <c>cardPlay=True</c> with
/// <c>dispatchable=[]</c> after a single Defend, and the flag was still set back at the main
/// menu.
///
/// RunReplays' own watchdog doesn't cover this — it only force-clears blockers for a QUEUED
/// MapMoveCommand (ReplayDispatcher.WatchdogTick), and the bot's queue is empty between
/// dispatches. Hence this bot-side equivalent, driven from BotController's heartbeat.
///
/// Deliberately conservative: it fires only after the game has offered NOTHING to do for
/// <see cref="StuckSeconds"/> consecutive seconds while a blocker is set, so a normal card
/// animation or enemy turn (both well under that, and both usually with something dispatchable
/// on either side) never trips it. Every clear is logged — each one is a symptom of a real
/// completion-path gap worth fixing at the source.
/// </summary>
internal static class StuckRecovery
{
    private const int StuckSeconds = 5;

    private static int _blockedTicks;

    internal static void Reset() => _blockedTicks = 0;

    /// <summary>Called once per heartbeat tick while a bot run is in progress.</summary>
    internal static void Tick()
    {
        // A queued command only runs once its type is dispatchable, and the game can move past
        // that point — the classic case is our EndTurnCommand outliving the combat that a lethal
        // card play just ended. Since BotController skips decisions while anything is pending,
        // that jams the bot permanently, so give a stuck queue a short grace period and drop it.
        if (ReplayEngine.HasPendingCommand)
        {
            if (Time.GetTicksMsec() - ActionExecutor.LastEnqueuedTick > StuckSeconds * 1000UL)
                ActionExecutor.DropStaleCommands($"still queued after {StuckSeconds}s");
            _blockedTicks = 0;
            return;
        }

        // Semantic fast path, far better than waiting out the timeout: when the game is idle in
        // the player's Play phase it is waiting for input, so by definition nothing is in
        // flight. Clearing here costs at most one heartbeat instead of StuckSeconds, which is
        // what made every single card play stall for 5s.
        if (PlayerIsIdleInCombat() && (ReplayState.CardPlayInFlight || ReplayState.PotionInFlight))
        {
            ReplayState.CardPlayInFlight = false;
            ReplayState.PotionInFlight = false;
            CardPlayReplayPatch._dispatching = false;
            _blockedTicks = 0;
            return;
        }

        if (!Blocked() || ReplayDispatcher.GetDispatchableTypes().Count > 0)
        {
            _blockedTicks = 0;
            return;
        }

        if (++_blockedTicks < StuckSeconds)
            return;

        // The clear itself always happens (below); only the log is conditional. A deliberate
        // pause is an expected, normal way to land here — the user parked the bot mid-animation
        // — so the "completion path never ran, worth fixing at the source" framing would be
        // false alarm noise on every long pause, not a symptom of a real gap.
        if (!BotController.Paused)
        {
            GD.PrintErr($"[SpireBot] StuckRecovery: nothing dispatchable for {_blockedTicks}s with " +
                        $"in-flight flags set ({Describe()}) — force-clearing them. This means a " +
                        "command's completion path never ran; the underlying gap is worth fixing.");
        }

        ReplayState.CardPlayInFlight = false;
        ReplayState.PotionInFlight = false;
        ReplayState.ClearActionInFlight();
        ReplayDispatcher.MapMoveInFlight = false;
        CardPlayReplayPatch._dispatching = false;
        CardPlayReplayPatch.ClearEndTurnGate();   // also resets _dispatching/_waitingForEffects

        _blockedTicks = 0;
    }

    /// <summary>
    /// True when combat is running and the game is sitting in the local player's Play phase —
    /// i.e. it is waiting for a click, so no card/potion can still be resolving.
    /// </summary>
    private static bool PlayerIsIdleInCombat()
    {
        var combat = CombatManager.Instance;
        if (combat == null || !combat.IsInProgress || combat.IsOverOrEnding)
            return false;
        if (ReplayState.ActionInFlight)
            return false;

        return CardPlayReplayPatch.ResolveLocalPlayer()?.PlayerCombatState?.Phase
               == PlayerTurnPhase.Play;
    }

    private static bool Blocked()
        => ReplayState.CardPlayInFlight
        || ReplayState.PotionInFlight
        || ReplayState.ActionInFlight
        || ReplayDispatcher.MapMoveInFlight
        || CardPlayReplayPatch.IsAwaitingEndTurnCompletion;

    private static string Describe()
        => $"cardPlay={ReplayState.CardPlayInFlight},potion={ReplayState.PotionInFlight}," +
           $"action={ReplayState.ActionInFlight},mapMove={ReplayDispatcher.MapMoveInFlight}," +
           $"endTurn={CardPlayReplayPatch.IsAwaitingEndTurnCompletion}";
}
