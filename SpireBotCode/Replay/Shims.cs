using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using SpireBot.Replay.Commands;
using SpireBot.SpireBotCode;

namespace SpireBot.Replay;

/// <summary>
/// Minimal stand-ins for RunReplays machinery that was deliberately NOT
/// vendored (see VENDORED-FROM.md), so the vendored files that reference
/// them still compile. Fields/flags are pinned to live-bot values: the bot
/// never loads a recorded playback, so every "is a replay active" query is
/// permanently false and the pending-command queue is permanently empty.
/// </summary>
public static class ReplayEngine
{
    // Real (but always-empty in practice) queue/state fields so
    // ReplayDispatcher's playback bookkeeping compiles and is dead code.
    internal static readonly Queue<ReplayCommand> _pending = new();
    internal static readonly List<ReplayCommand> _recentConsumed = new();
    internal static bool _replayActive;
    internal static List<ReplayCommand> _loadedCommands = new();

    /// <summary>
    /// True while SpireBot is driving a run. Task 8.5 originally pinned <see cref="IsActive"/>
    /// to "a recorded playback is in progress", which was literally true but operationally
    /// wrong: in RunReplays, IsActive really means "a programmatic driver is attached, so
    /// record screen state for it". The replay-side patches and screen captures all gate on
    /// it (<c>if (!ReplayEngine.IsActive) return;</c>) before writing
    /// <see cref="ReplayState"/> — the very state <c>ReplayDispatcher.GetDispatchableTypes</c>
    /// reads. With it pinned false the dispatcher was blind: every decision came back
    /// Unsupported with zero available commands, and since LogDispatchableChanges only emits
    /// InputRequired for a NON-EMPTY set, the bot then never woke again. Set for the whole
    /// bot run by <c>BotController.StartBotRun</c>.
    /// </summary>
    public static bool BotDriving { get; internal set; }

    /// <summary>
    /// True while <c>SpireBot.SpireBotCode.PassiveDump</c> (Task 16) is observing a run driven
    /// by the SEPARATE RunReplays fork mod (its own assembly, its own
    /// <c>RunReplays.ReplayEngine.IsActive</c>) rather than by this bot. Every vendored
    /// record/screen patch gates on <c>if (!ReplayEngine.IsActive) return;</c> before writing
    /// <see cref="ReplayState"/> — the state <c>ReplayDispatcher.GetDispatchableTypes</c> /
    /// <c>GetAvailableCommands</c> read. During fork-driven playback only the fork's own
    /// IsActive is true; this shim's stays false unless told otherwise, so the vendored view
    /// would be blind and every <c>DecisionContext.Capture()</c> would classify Unsupported.
    /// This flag makes the vendored recorders record WITHOUT implying a driver: it does not
    /// touch <see cref="BotDriving"/>, the vendored <see cref="_pending"/> queue stays empty,
    /// and <c>BotController</c> is never running — so nothing here can dispatch anything.
    /// Accepted side effect: while this is on, the vendored record patches (which gate the
    /// OPPOSITE way, e.g. <c>PlayerActionBuffer.Record</c>'s <c>if (ReplayEngine.IsActive)
    /// return;</c>) skip, so SpireBot's own save-log recording is off for the duration — fine
    /// for a dev-only observation mode; the real RunReplays fork still records its own log.
    ///
    /// LIVE computed property, not a settable flag: true iff the passive Harmony hook is
    /// installed AND <see cref="SpireBotConfig.PassiveDump"/> is currently true. Previously this
    /// was a bool set once by <c>PassiveDump.TryInstall()</c> at game-launch config-read time,
    /// which meant flipping the config flag on mid-session (from the in-game config screen,
    /// after launch) could never take effect — a restart-with-flag-preset trap. Reading the
    /// config live means the flag can be toggled at any time; the only caveat is that screens
    /// already open at the moment of toggling were not recorded, so enable the flag BEFORE
    /// starting the replay (from the main menu is enough — the run's first screens open only
    /// after the replay itself starts).
    /// </summary>
    public static bool PassiveObserving =>
        PassiveDump.HookInstalled && SpireBotConfig.PassiveDump;

    public static bool IsActive => _pending.Count > 0 || _replayActive || BotDriving || PassiveObserving;

    /// <summary>
    /// True only while a command SpireBot dispatched is still in flight. Callers that mean
    /// "my own dispatch is mid-execution" must use this rather than <see cref="IsActive"/>,
    /// which is now true for a whole bot run.
    /// </summary>
    public static bool HasPendingCommand => _pending.Count > 0;

    /// <summary>
    /// True for the entire lifetime of a run started as a replay. Always
    /// false for SpireBot — the bot never loads a recorded playback.
    /// </summary>
    public static bool IsReplayRun { get; internal set; }

    public static string? ActiveSeed { get; set; }
    public static string[]? ActiveActs { get; set; }

    /// <summary>Returns the next queued command without consuming it. Always false — the queue is never populated.</summary>
    public static bool PeekNext(out ReplayCommand? cmd) => _pending.TryPeek(out cmd);

    /// <summary>
    /// Dequeues and discards a command. Note: RunReplays' original also
    /// signals the overlay and fires ReplayCompleted when the queue empties
    /// (ReplayEngine.SignalConsumed); that plumbing was not vendored since
    /// RunOverlay is a no-op shim and this queue is never populated.
    /// </summary>
    public static bool ConsumeAny()
    {
        _pending.TryDequeue(out _);
        return true;
    }

    public static void Load(IReadOnlyList<string> commands) =>
        throw new NotSupportedException("SpireBot does not vendor replay playback; use the RunReplays mod for playback.");

    internal static void FireReplayCompleted(IReadOnlyList<ReplayCommand> commands) { }
}

/// <summary>
/// Shim retaining RunReplays' real record-queue behavior (verbose + minimal
/// action logs, undo, snapshot/restore, battle-state summary) so the
/// vendored Record patches still produce save logs of bot runs. Dev-console
/// logging methods are no-ops — SpireBot has no dev-console pipeline.
/// The original's Harmony-patched ActionExecutor postfix, RecordCardPlayEarly,
/// OnReplayCompleted, and CardPlayRecordPatch were deliberately not vendored
/// (see VENDORED-FROM.md); this shim keeps only the public record API surface
/// that other vendored files call directly.
/// </summary>
public static class PlayerActionBuffer
{
    private static readonly ConcurrentQueue<(string Timestamp, string Action)> _verboseEntries = new();
    private static readonly ConcurrentQueue<string> _minimalEntries = new();

    public static void Record(string text)
    {
        if (ReplayEngine.IsActive)
            return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _verboseEntries.Enqueue((timestamp, text));
        _minimalEntries.Enqueue(text);
        Utils.DiagnosticLog.Write("Record", text);
        ReplayDispatcher.ClearDispatchableCache();
    }

    public static void RecordVerboseOnly(string text)
    {
        if (ReplayEngine.IsActive)
            return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _verboseEntries.Enqueue((timestamp, text));
        ReplayDispatcher.ClearDispatchableCache();
    }

    public static void RecordMinimalOnly(string text)
    {
        if (ReplayEngine.IsActive)
            return;

        _minimalEntries.Enqueue(text);
        ReplayDispatcher.ClearDispatchableCache();
    }

    /// <summary>
    /// Removes the most recently recorded entry from both queues.
    /// Used to undo a speculatively recorded action (e.g. a shop purchase
    /// that later fails).
    /// </summary>
    public static void UndoLast()
    {
        RemoveLast(_verboseEntries);
        RemoveLast(_minimalEntries);
    }

    private static void RemoveLast<T>(ConcurrentQueue<T> queue)
    {
        int count = queue.Count;
        if (count == 0) return;
        for (int i = 0; i < count - 1; i++)
        {
            if (queue.TryDequeue(out T? item))
                queue.Enqueue(item);
        }
        queue.TryDequeue(out _);
    }

    /// <summary>Minimal snapshot: action text only, no timestamps.</summary>
    public static IReadOnlyList<string> SnapshotMinimal()
    {
        return new List<string>(_minimalEntries);
    }

    /// <summary>
    /// Restores both queues from previously-saved log files (used when a run is continued).
    /// </summary>
    public static void Restore(
        IReadOnlyList<(string Timestamp, string Action)> verboseEntries,
        IReadOnlyList<string> minimalEntries)
    {
        foreach (var entry in verboseEntries)
            _verboseEntries.Enqueue(entry);
        foreach (var entry in minimalEntries)
            _minimalEntries.Enqueue(entry);
    }

    /// <summary>
    /// Returns a compact summary of the current battle state:
    ///   Hand: [card1, card2] Enemies: [Monster 42/44, ...]
    /// Returns null when not in combat. Copied verbatim from RunReplays —
    /// reads live game state and is used by ReplayDispatcher's
    /// expected-pre-state check.
    /// </summary>
    internal static string? GetBattleStateSummary()
    {
        if (!MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
            return null;

        var state = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState();
        if (state == null)
            return null;

        MegaCrit.Sts2.Core.Entities.Players.Player? me;
        try { me = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(state); }
        catch { me = System.Linq.Enumerable.FirstOrDefault(state.Players); }
        if (me == null)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.Append("Hand: [");
        var hand = me.PlayerCombatState?.Hand?.Cards;
        if (hand != null)
        {
            for (int i = 0; i < hand.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(hand[i].Title);
            }
        }
        sb.Append("] Enemies: [");
        bool first = true;
        foreach (var enemy in state.Enemies)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(enemy.Name).Append(' ')
              .Append(enemy.CurrentHp).Append('/').Append(enemy.MaxHp);
        }
        sb.Append(']');
        return sb.ToString();
    }

    // ── Dev-console logging: no-ops. SpireBot has no dev-console pipeline. ──
    // Conditional attributes kept so call sites still compile out under the
    // original's build flags (Conditional requires a void return, which all
    // of these have).

    [System.Diagnostics.Conditional("RUNREPLAYS_VERBOSE")]
    internal static void LogToDevConsole(string entry) { }

    [System.Diagnostics.Conditional("RUNREPLAYS_DEVCONSOLE")]
    internal static void ForceLogToDevConsole(string entry) { }

    [System.Diagnostics.Conditional("RUNREPLAYS_DISPATCHER")]
    internal static void LogDispatcher(string entry) { }

    [System.Diagnostics.Conditional("RUNREPLAYS_MIGRATION")]
    public static void LogMigrationWarning(string entry) { }
}

/// <summary>
/// No-op shim — SpireBot's overlay is ThinkingOverlay (Task 14), not
/// RunReplays' RunOverlay.
/// </summary>
public static class RunOverlay
{
    public static void NotifyCardPlayFinished() { }
    public static void RestoreRecentEntries(IReadOnlyList<string> entries) { }
}

/// <summary>
/// Not in the original not-vendored list, but referenced by DiagnosticLog's
/// startup fingerprint and RunSaveLogger's log-file header. Pinned to a
/// placeholder distinct from the original RunReplays version so log output
/// is not misread as coming from the real mod.
/// </summary>
internal static class ModVersion
{
    internal const string Current = "spirebot-vendored-0.1.0";
}
