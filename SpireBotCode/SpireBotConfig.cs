using BaseLib.Config;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Config for the ONNX policy pipeline: which model/contract to load and how to sample from it.
/// SimpleModConfig auto-generates the settings-screen UI from these static properties (see
/// RunReplaysConfig for the established pattern this mirrors).
/// </summary>
public class SpireBotConfig : SimpleModConfig
{
    /// <summary>Path to the exported ONNX policy model.</summary>
    public static string OnnxModelPath { get; set; } = "";

    /// <summary>Path to the observation/action contract (dims, vocab, action-head layout) the model was trained against.</summary>
    public static string ContractPath { get; set; } = "";

    /// <summary>Sampling temperature for the policy's action distribution. 0 = argmax (fully deterministic).</summary>
    [ConfigSlider(0, 2, 0.05)]
    public static float Temperature { get; set; } = 0f;

    /// <summary>Restrict sampling to the top K highest-probability actions (ignored when Temperature is 0).</summary>
    [ConfigSlider(1, 50, 1)]
    public static int TopK { get; set; } = 5;

    /// <summary>
    /// Greedy (Temperature 0) decoding: when true, identical hand instances (same card,
    /// affliction, enchantment, upgrade level and target) pool their probability and the
    /// heaviest GROUP wins, instead of raw per-instance argmax — which let a unique action
    /// (End Turn at 3 energy, a potion) beat a card whose mass was split across duplicates
    /// (2026-08-22 finding; see OnnxPolicyCore.ArgmaxMerged). Mirrors `eval.py --merge-duplicates`.
    /// </summary>
    public static bool MergeDuplicateCards { get; set; } = true;

    /// <summary>If true, dump each decision (observation + action probabilities) to DumpDir for offline inspection.</summary>
    public static bool DumpDecisions { get; set; } = false;

    /// <summary>Directory decisions are dumped to when DumpDecisions is enabled.</summary>
    public static string DumpDir { get; set; } = "";

    /// <summary>
    /// Engine time scale — how fast the GAME runs (animations, effects), config-screen only
    /// since the 2026-08-20 UX pacing change; the overlay's speed buttons now control
    /// <see cref="DecisionSpeed"/> instead. Still pinned by the bot at run start
    /// (BotController.ApplyEffectiveSpeed), because the vendored dispatcher would otherwise
    /// re-assert its own 2.0x replay fast-forward. Decision pacing is unaffected by this
    /// value — dwell timers ignore Engine.TimeScale.
    /// </summary>
    [ConfigSlider(0.5, 5, 0.5)]
    public static float GameSpeed { get; set; } = 1.0f;

    /// <summary>
    /// Bot decision-speed multiplier (2026-08-20 UX pacing spec §2): divides the per-kind
    /// dwell the bot holds each chosen action for before committing it, so the user can see
    /// what it is about to do. 10 means Instant — no dwell at all, the pre-pacing behavior
    /// for hands-off grind runs. Controls DECISION pacing only; it never touches
    /// Engine.TimeScale / game speed (that is <see cref="GameSpeed"/>, config-screen only).
    /// The overlay's ◀/▶ buttons (BotControlBar) write this live and save it debounced.
    /// Range and step must keep covering every SpeedLadder.BotSteps rung — PacingSelfTest
    /// asserts that.
    /// </summary>
    [ConfigSlider(0.5, 10, 0.5)]
    public static float DecisionSpeed { get; set; } = 1.0f;

    /// <summary>
    /// When true, the bot drives every attached run itself from the first room. When false
    /// (the default), it attaches to every eligible run as a paused ADVISOR — the overlay
    /// shows what it would do, and the Autopilot toggle on the overlay hands the run over
    /// (or back) at any time. Replaces the old always-start-paused knob (inverted sense) as part of
    /// the 2026-08-20 UX pacing change, which also removed the main menu's "Bot Run" button:
    /// the bot is now always attached to eligible runs, and this only chooses who drives first.
    /// </summary>
    public static bool AutopilotByDefault { get; set; } = false;

    /// <summary>
    /// Runs the contract self-test at startup and the per-decision observation self-check.
    /// These were originally gated on <c>OS.IsDebugBuild()</c>, which is always false in the
    /// shipped game (it runs as an export_release build) — so they never executed where they
    /// were needed. Cheap enough to leave on; turn off for a performance-sensitive run.
    /// </summary>
    public static bool SelfChecks { get; set; } = true;

    /// <summary>Toggles the in-game "thinking" overlay (ThinkingOverlay) that shows the bot's
    /// most recent decision kind, chosen action, and top-K distribution. The overlay node
    /// itself stays alive regardless of this flag — flipping it back on shows the latest
    /// decision immediately rather than a stale blank state.</summary>
    public static bool ShowOverlay { get; set; } = true;

    /// <summary>
    /// Dev-only (Task 16). When on, SpireBot hooks the separate RunReplays mod's own playback
    /// dispatcher (a different assembly, driving a recorded run from disk) and, for every
    /// replay action it dispatches, captures a decision and dumps one line via the existing
    /// <see cref="DumpDecisions"/>/<see cref="DumpDir"/> pipeline (<see cref="DecisionDumper"/>)
    /// — WITHOUT choosing or executing anything itself. SpireBot is purely an observer in this
    /// mode: the fork drives the run, SpireBot only watches and records obs for the Task 16
    /// parity grind. See <see cref="PassiveDump"/>.
    /// </summary>
    public static bool PassiveDump { get; set; } = false;
}
