using Godot;

using SpireBot.Replay;
using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Executes a single chosen <see cref="ReplayCommand"/> through the vendored
/// <see cref="ReplayDispatcher"/>'s execute/retry/watchdog machinery, rather than calling
/// <c>ReplayCommand.Execute()</c> directly.
///
/// ── Option chosen: (a) enqueue into the shim's pending queue ─────────────────────────────
/// <see cref="ReplayEngine"/>'s <c>_pending</c> queue is a REAL <c>Queue&lt;ReplayCommand&gt;</c>
/// (Shims.cs:20) — it is only ever empty in practice because nothing else ever enqueues into
/// it (SpireBot never calls <c>ReplayEngine.Load</c>, which throws NotSupportedException by
/// design, Shims.cs:51-52). Pushing our one command onto it and then nudging the dispatcher
/// (<see cref="ReplayDispatcher.DispatchNow"/>) makes <c>ReplayDispatcher.ExecuteNext</c> run
/// its full real path unmodified: dispatchable-type gate (ReplayDispatcher.cs:1065),
/// <c>CheckPreState</c> diagnostics, <c>cmd.Execute()</c>, and on a non-success result the
/// existing retry-by-delay scheduling (ReplayDispatcher.cs:1106-1120) or
/// not-yet-dispatchable rescheduling (ReplayDispatcher.cs:1069, <c>scheduleDispatchOnDelay</c>)
/// — plus the stall watchdog (ReplayDispatcher.cs:929-968), which force-clears blockers and
/// retries a stuck <c>MapMoveCommand</c> after 5s of no progress. On success,
/// <c>ReplayEngine.ConsumeAny()</c> dequeues our command (Shims.cs:45-49) and the queue goes
/// back to empty. Option (b) — calling a per-command execute method directly — would mean
/// hand-rolling all of that retry/watchdog logic a second time, which is exactly the
/// duplication this task says to avoid ("truer to the vendored machinery").
///
/// ── The IsActive tradeoff ─────────────────────────────────────────────────────────────────
/// <see cref="ReplayEngine.IsActive"/> is defined as <c>_pending.Count > 0 || _replayActive</c>
/// (Shims.cs:25), so for the single-command window between this enqueue and its consumption,
/// <c>IsActive</c> flips true. A repo-wide grep of `IsActive` under SpireBotCode/Replay turns
/// up ~25 call sites; the overwhelming majority are `if (ReplayEngine.IsActive) return;` guards
/// on the vendored RECORD patches (RunSaveLogger.cs:33, TakeCardRecordPatch.cs:37/82,
/// TreasureRoomRecordPatch.cs:22/38, ProceedToNextActRecordPatch.cs:16,
/// HandCardSelectRecordPatch.cs:44/116, PlayerActionBuffer.Record/RecordVerboseOnly/
/// RecordMinimalOnly in Shims.cs:74/86/96, ReplayState.OnAfterAction's TryDispatch nudge is
/// gated the other way). Their intent is "don't log this into the save/action-log buffer
/// while a recorded replay is driving the game" — which is also the correct behavior for a
/// bot-driven action, since SpireBot has no save-log/replay-file use case of its own (Task 11
/// scope) and skipping a duplicate log write during the single command's in-flight window is
/// harmless. The REPLAY-side patches (CardPlayReplayPatch, ShopReplayPatch, FakeMerchantReplayPatch,
/// RestSiteReplayPatch, BattleRewardsReplayPatch, TreasureRoomReplayPatch, ProceedButtonReplayPatch,
/// StartingBonusReplayPatch, SelectCardFromScreenCommand.cs:101, SelectHandCardsCommand.cs:139,
/// CardGridScreenCapture.cs:73) gate the OPPOSITE way (`if (!ReplayEngine.IsActive) return;`) —
/// they only run WHILE IsActive, which is exactly what we want: those patches are what let a
/// PlayCardCommand/SelectHandCardsCommand/etc. actually execute against the live game API in
/// the first place, so we need IsActive true during dispatch, not despite it. Net effect: the
/// window where IsActive is true is scoped to one command's execution (typically well under a
/// second, worst case a few retry cycles), so the vendored save/action-log recorders miss
/// logging bot moves during that narrow window but every command-executing replay patch works
/// exactly as designed the rest of the time. This is the intended tradeoff, not an oversight.
/// </summary>
public static class ActionExecutor
{
    /// <summary>
    /// Enqueues <paramref name="cmd"/> onto the vendored dispatcher's pending queue and
    /// immediately nudges dispatch (bypassing the normal inter-command delay timer, since the
    /// bot — unlike a human-paced replay — has no reason to wait between its own decisions).
    ///
    /// <paramref name="kind"/> (fix-shop-exit-brief.md review round 2, Important #1): the
    /// <see cref="DecisionContext.Kind"/> of the decision that chose to dispatch
    /// <paramref name="cmd"/> — every call site has a live <see cref="DecisionContext"/> in
    /// scope, so this always reflects the REAL originating decision, not just "some kind a
    /// command of this type happens to be legal under" (the bug <see cref="ScreenExitMemory"/>'s
    /// review round 2 fixed: a stuck-guard/fallback dispatch of a command that was merely still
    /// enumerable from an unrelated decision must not be attributed to that decision's kind).
    /// Threaded straight to <see cref="ScreenExitMemory.Record"/>; <see cref="RoomOneShots"/> and
    /// <see cref="RewardClaimMemory"/> don't need it (their gates are per-command-type/per-index,
    /// not per-kind).
    /// </summary>
    public static void Execute(ReplayCommand cmd, DecisionKind kind)
    {
        if (cmd == null)
        {
            GD.PrintErr("[SpireBot] ActionExecutor.Execute called with a null command.");
            return;
        }

        RoomOneShots.Record(cmd);
        RewardClaimMemory.Record(cmd);
        ScreenExitMemory.Record(cmd, kind);
        ReplayEngine._pending.Enqueue(cmd);
        LastEnqueuedTick = Time.GetTicksMsec();
        ReplayDispatcher.DispatchNow();
    }

    /// <summary>When the most recent command was enqueued (ms since engine start).</summary>
    internal static ulong LastEnqueuedTick { get; private set; }

    /// <summary>
    /// Drops commands that can never dispatch. A queued command only executes once its type is
    /// in the dispatchable set, and the game can move on before that happens — a card play that
    /// kills the last enemy leaves our follow-up EndTurnCommand queued while combat is already
    /// over ("TryDispatch miss — cmd=EndTurnCommand dispatchable=[ClaimRewardCommand,...]").
    /// Because <see cref="BotController"/> skips decisions while a command is pending, a jammed
    /// queue silently disables the bot for the rest of the run — that is how it sat on the
    /// post-combat reward screen doing nothing.
    /// </summary>
    internal static void DropStaleCommands(string reason)
    {
        if (ReplayEngine._pending.Count == 0)
            return;

        var dropped = string.Join(",", ReplayEngine._pending.Select(c => c.GetType().Name));
        ReplayEngine._pending.Clear();
        GD.PrintErr($"[SpireBot] ActionExecutor: dropped undispatchable queued command(s) [{dropped}] " +
                    $"— {reason}. The bot will re-decide from the current screen.");
    }
}
