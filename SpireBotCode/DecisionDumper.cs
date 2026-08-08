using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Godot;

using SpireBot.SpireBotCode.Obs;
using SpireBot.SpireBotCode.Policies;

// See BotController.cs's identical alias comment: Obs.Obs collides with the
// SpireBot.SpireBotCode.Obs namespace once both are in scope (CS0118).
using ObsResult = SpireBot.SpireBotCode.Obs.Obs;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Appends one JSON line per decision to
/// <c>{SpireBotConfig.DumpDir}/{seed}/decisions.jsonl</c> for Task 16's offline sim/game
/// join (join key: <c>(act, floor, kind, decision_index)</c>). Entirely gated on
/// <see cref="SpireBotConfig.DumpDecisions"/> — <see cref="ResetRun"/> is safe to call
/// unconditionally (it just resets the counters and skips opening a file when dumping is
/// off), and <see cref="Write"/> no-ops if no file is open.
///
/// <c>decision_index</c> is a per-(act, floor, kind) running counter, reset once per bot run
/// (<see cref="ResetRun"/>, called from <see cref="BotController.StartBotRun"/> alongside
/// <c>SessionState.ResetRun</c>) and incremented on every <see cref="Write"/> call for that
/// exact (act, floor, kind) triple — deterministic given a deterministic decision sequence, per
/// the task brief ("make the counter deterministic"). <c>floor</c> alone is not a run-unique
/// key: it is the per-act <c>ActFloor</c> (1..17, per <see cref="GameStateSnapshot"/>), so a
/// full run folds every act onto the same floor numbers — <c>act</c> (0-based
/// <c>CurrentActIndex</c>) disambiguates them for the offline join.
///
/// Flushed after every line (a crash must not lose the tail of a long dump) and opened in
/// APPEND mode (a run that reconnects/retries after a crash keeps its prior lines rather
/// than silently truncating the file out from under an in-progress dump).
/// </summary>
public static class DecisionDumper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // System.Text.Json's default float/double converter already round-trips (.NET Core
        // 3+ semantics, equivalent to "R"/G17 formatting) — no custom converter needed for
        // the 1e-6-comparable guarantee the task brief asks for; never switch this to a
        // fixed-precision number format.
    };

    private static readonly Dictionary<(int act, int floor, string kind), int> _counters = new();
    private static StreamWriter? _writer;

    /// <summary>Resets the per-(floor,kind) counters and opens a fresh dump file for this
    /// run's seed. Safe to call even when <see cref="SpireBotConfig.DumpDecisions"/> is
    /// false (no file is opened; the counters still reset so a later mid-run toggle starts
    /// clean rather than inheriting stale counts).</summary>
    public static void ResetRun(string? seed)
    {
        _counters.Clear();
        _writer?.Dispose();
        _writer = null;

        if (!SpireBotConfig.DumpDecisions)
            return;

        if (string.IsNullOrWhiteSpace(SpireBotConfig.DumpDir))
        {
            GD.PrintErr("[SpireBot] DecisionDumper.ResetRun: DumpDecisions is on but " +
                        "SpireBotConfig.DumpDir is empty — decisions will not be dumped.");
            return;
        }

        string seedDir = string.IsNullOrWhiteSpace(seed)
            ? $"unseeded_{DateTime.UtcNow:yyyyMMdd_HHmmss}"
            : seed!;

        try
        {
            string dir = Path.Combine(SpireBotConfig.DumpDir, seedDir);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "decisions.jsonl");
            _writer = new StreamWriter(path, append: true) { AutoFlush = false };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] DecisionDumper.ResetRun: failed to open the dump file: {ex}");
            _writer = null;
        }
    }

    /// <summary>
    /// Appends one decision's record, or no-ops entirely if dumping is off / the writer
    /// failed to open. Takes the full <see cref="ActionMap"/> (not a bare mask array) so the
    /// "labels" field — action id -> human label, for every LEGAL action, not just the
    /// policy's own top-K — can be built from <see cref="ActionMap.LabelFor"/>; a bare
    /// <c>bool[]</c> mask alone cannot answer "what does action 37 mean?".
    /// </summary>
    public static void Write(DecisionContext ctx, ObsResult obs, ActionMap map, PolicyResult result)
    {
        if (_writer == null)
            return;

        try
        {
            int act = ctx.Snapshot.Act;
            int floor = ctx.Snapshot.Floor;
            string kind = ctx.Kind.ToString();
            var key = (act, floor, kind);
            int decisionIndex = _counters.TryGetValue(key, out int n) ? n : 0;
            _counters[key] = decisionIndex + 1;

            var labels = new Dictionary<string, string>();
            for (int i = 0; i < map.Mask.Length; i++)
                if (map.Mask[i])
                    labels[i.ToString()] = map.LabelFor(i);

            var topk = new List<TopKEntry>(result.TopK.Length);
            foreach (var (label, prob) in result.TopK)
                topk.Add(new TopKEntry { Label = label, Prob = prob });

            var record = new DecisionRecord
            {
                Act = act,
                Floor = floor,
                Kind = kind,
                DecisionIndex = decisionIndex,
                F = obs.F,
                I = obs.I,
                Mask = map.Mask,
                Action = result.ActionId,
                TopK = topk,
                Labels = labels,
            };

            _writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            _writer.Flush(); // per-line flush — a crash must not lose the tail (task brief)
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] DecisionDumper.Write threw (non-fatal, this decision's line is lost): {ex}");
        }
    }

    private sealed class TopKEntry
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("prob")] public float Prob { get; set; }
    }

    private sealed class DecisionRecord
    {
        [JsonPropertyName("act")] public int Act { get; set; }
        [JsonPropertyName("floor")] public int Floor { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("decision_index")] public int DecisionIndex { get; set; }
        [JsonPropertyName("f")] public float[] F { get; set; } = Array.Empty<float>();
        [JsonPropertyName("i")] public long[] I { get; set; } = Array.Empty<long>();
        [JsonPropertyName("mask")] public bool[] Mask { get; set; } = Array.Empty<bool>();
        [JsonPropertyName("action")] public int Action { get; set; }
        [JsonPropertyName("topk")] public List<TopKEntry> TopK { get; set; } = new();
        [JsonPropertyName("labels")] public Dictionary<string, string> Labels { get; set; } = new();
    }
}
