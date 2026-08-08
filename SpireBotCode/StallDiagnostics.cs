using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using SpireBot.Replay;
using SpireBot.Replay.Patches.Replay;

namespace SpireBot.SpireBotCode;

/// <summary>
/// One-line dump of every piece of state <c>ReplayDispatcher.GetDispatchableTypes</c> gates on.
///
/// When the bot stalls, the useful question is never "what did the policy pick" but "why did
/// the enumerator offer nothing" — and that answer lives in a handful of scattered flags and
/// screen references. Logged on a stall, throttled to state CHANGES so a stuck loop prints once
/// rather than once per heartbeat.
/// </summary>
internal static class StallDiagnostics
{
    private static string? _lastReported;

    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    internal static void ReportIfChanged(DecisionContext ctx, string reason)
    {
        string snapshot;
        try
        {
            snapshot = Describe(ctx);
        }
        catch (Exception ex)
        {
            snapshot = $"<diagnostics threw: {ex.Message}>";
        }

        if (snapshot == _lastReported)
            return;

        _lastReported = snapshot;
        GD.PrintErr($"[SpireBot] STALL ({reason}): {snapshot}");
    }

    /// <summary>Clears the throttle so the next stall always reports (call on run start).</summary>
    internal static void Reset() => _lastReported = null;

    private static string Describe(DecisionContext ctx)
    {
        var sb = new StringBuilder();

        sb.Append("kind=").Append(ctx.Kind);
        sb.Append(" available=[")
          .Append(string.Join(",", ctx.Available.Select(c => c.GetType().Name).Distinct()))
          .Append(']');

        // Map gate: NMapScreen.Instance != null && IsInstanceValid && IsTravelEnabled.
        var map = NMapScreen.Instance;
        sb.Append(" map=");
        if (map == null) sb.Append("null");
        else if (!GodotObject.IsInstanceValid(map)) sb.Append("freed");
        else sb.Append($"travelEnabled={map.IsTravelEnabled}");

        // Event gate: ActiveEventSynchronizer + Events[0].IsFinished decides whether the
        // enumerator offers real option indices or the lone PROCEED sentinel.
        var sync = ReplayState.ActiveEventSynchronizer;
        sb.Append(" event=");
        if (sync == null) sb.Append("null");
        else
        {
            sb.Append($"events={sync.Events.Count}");
            if (sync.Events.Count > 0)
                sb.Append($",finished={sync.Events[0].IsFinished}");
        }

        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        sb.Append(" room=").Append(state?.CurrentRoom?.GetType().Name ?? "null");
        sb.Append(" coord=").Append(state?.CurrentMapCoord?.ToString() ?? "null");

        // The `blocked` early-return in GetDispatchableTypes: any one of these suppresses
        // everything except the grid/hand screens.
        sb.Append(" blocked[")
          .Append($"potion={ReplayState.PotionInFlight},")
          .Append($"cardPlay={ReplayState.CardPlayInFlight},")
          .Append($"endTurn={CardPlayReplayPatch.IsAwaitingEndTurnCompletion},")
          .Append($"mapMove={ReplayDispatcher.MapMoveInFlight},")
          .Append($"action={ReplayState.ActionInFlight}")
          .Append(']');

        sb.Append(" dispatchable=[")
          .Append(string.Join(",", ReplayDispatcher.GetDispatchableTypes().Select(t => t.Name)))
          .Append(']');

        return sb.ToString();
    }
}
