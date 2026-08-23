using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Godot;

using SpireBot.Replay;

namespace SpireBot.SpireBotCode;

/// <summary>
/// TEMPORARY diagnostic for the 2026-08-21 "frames drop when Autopilot is ON" investigation.
/// Stopwatches the bot's main-thread sections and logs slow sections, slow frames, and a
/// 2-second summary (fps, per-section count/avg/max, loop state) while a run is attached.
/// Remove once the root cause is fixed.
/// </summary>
internal static class PerfProbe
{
    private const double SlowSectionMs = 8.0;
    private const double SlowFrameMs = 50.0;
    private const double SummaryEverySec = 2.0;

    private static readonly Dictionary<string, (int n, double total, double max)> _stats = new();
    private static readonly Stopwatch _wall = Stopwatch.StartNew();
    private static double _lastSummary;
    private static int _frames;
    private static double _maxFrameMs;

    public static void Count(string key) => Add(key, 0);

    public static IDisposable Time(string key) => new Scope(key);

    private static void Add(string key, double ms)
    {
        lock (_stats)
        {
            _stats.TryGetValue(key, out var s);
            _stats[key] = (s.n + 1, s.total + ms, Math.Max(s.max, ms));
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _key;
        private readonly long _t0 = Stopwatch.GetTimestamp();
        public Scope(string key) { _key = key; }
        public void Dispose()
        {
            double ms = (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;
            Add(_key, ms);
            if (ms > SlowSectionMs)
                GD.Print($"[SpireBot][perf] {_key} took {ms:0.0}ms {State()}");
        }
    }

    /// <summary>Call once per rendered frame (ThinkingOverlay._Process).</summary>
    public static void Frame(double delta)
    {
        double ms = delta * 1000.0;
        _frames++;
        if (ms > _maxFrameMs) _maxFrameMs = ms;
        if (ms > SlowFrameMs)
            GD.Print($"[SpireBot][perf] SLOW FRAME {ms:0}ms {State()}");

        double now = _wall.Elapsed.TotalSeconds;
        if (now - _lastSummary < SummaryEverySec) return;
        double span = now - _lastSummary;
        _lastSummary = now;

        if (!BotController.IsRunning) { _frames = 0; _maxFrameMs = 0; lock (_stats) _stats.Clear(); return; }

        var sb = new StringBuilder();
        sb.Append($"[SpireBot][perf] SUMMARY fps={Engine.GetFramesPerSecond():0} frames/{span:0.0}s={_frames} maxFrame={_maxFrameMs:0}ms ");
        lock (_stats)
        {
            foreach (var kv in _stats)
                sb.Append($"{kv.Key}[n={kv.Value.n} avg={(kv.Value.n > 0 ? kv.Value.total / kv.Value.n : 0):0.0} max={kv.Value.max:0.0}] ");
            _stats.Clear();
        }
        sb.Append(State());
        GD.Print(sb.ToString());
        _frames = 0;
        _maxFrameMs = 0;
    }

    private static string State()
        => $"| paused={BotController.Paused} dwell={BotController.DwellArmedForProbe} pending={BotController.HasPendingPreview} " +
           $"isActive={ReplayEngine.IsActive} pendingCmd={ReplayEngine.HasPendingCommand} ts={Engine.TimeScale:0.00}";
}
