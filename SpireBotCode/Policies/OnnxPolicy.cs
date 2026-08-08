using System;
using System.IO;
using System.Linq;

using Godot;

using Microsoft.ML.OnnxRuntime;

// See IPolicy.cs's identical alias comment.
using ObsResult = SpireBot.SpireBotCode.Obs.Obs;

namespace SpireBot.SpireBotCode.Policies;

/// <summary>
/// Task 14: a trained (or, for local testing, stub) ONNX policy. Loads an
/// <see cref="InferenceSession"/> from <see cref="SpireBotConfig.OnnxModelPath"/> and
/// validates its input/output shapes against the <see cref="Contract"/> at construction time
/// (<see cref="OnnxPolicyCore.Validate"/>) — a mismatched model refuses to load rather than
/// running with silently wrong offsets; <see cref="BotController"/> is expected to catch the
/// resulting <see cref="OnnxPolicyException"/> and fall back to <see cref="FirstLegalPolicy"/>
/// with a loud log (this class itself never falls back to a wrong-shaped session).
///
/// <see cref="Choose"/> runs inference on the decision's <see cref="ObsResult"/> + the
/// <see cref="ActionMap.Mask"/>, then:
///   - <see cref="SpireBotConfig.Temperature"/> == 0 -> argmax over legal actions (fully
///     deterministic); the TopK list is still built (from a temperature=1 softmax) purely for
///     overlay/diagnostic display, per the task brief — it never affects which action is chosen.
///   - otherwise -> softmax(logits / temperature) restricted to legal actions, then a sample
///     drawn from the top <see cref="SpireBotConfig.TopK"/> of that distribution.
/// All the actual math (validation, inference tensor plumbing, argmax/softmax/sampling) lives
/// in the Godot-free <see cref="OnnxPolicyCore"/> so it can be exercised from a plain console
/// harness — this class is deliberately thin: GD logging, IPolicy/DecisionContext/ActionMap/
/// SpireBotConfig plumbing only.
/// </summary>
public sealed class OnnxPolicy : IPolicy, IDisposable
{
    private readonly InferenceSession _session;
    private readonly Contract _contract;
    private readonly Random _rng;
    private bool _disposed;

    private OnnxPolicy(InferenceSession session, Contract contract, int? seed)
    {
        _session = session;
        _contract = contract;
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>
    /// Loads and validates an ONNX policy from <paramref name="modelPath"/> against
    /// <paramref name="contract"/>. Throws <see cref="OnnxPolicyException"/> (file-not-found,
    /// a malformed/corrupt ONNX graph, or any input/output shape or dtype mismatch) on any
    /// problem — this method NEVER returns a policy backed by a session it hasn't fully
    /// validated. <paramref name="seed"/> seeds the sampling RNG deterministically (mainly for
    /// the console harness / tests); omit it in the mod for a randomized run.
    /// </summary>
    public static OnnxPolicy Load(string modelPath, Contract contract, int? seed = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new OnnxPolicyException("OnnxPolicy.Load: model path is empty.");
        if (!File.Exists(modelPath))
            throw new OnnxPolicyException($"OnnxPolicy.Load: file not found: '{modelPath}'.");

        InferenceSession session;
        try
        {
            // CPU execution provider is the only one configured (no GPU EP added to
            // SessionOptions) — matches the brief's "loads ... (CPU)" requirement and the
            // exporter's own CPU-only parity gate.
            session = new InferenceSession(modelPath, new SessionOptions());
        }
        catch (Exception ex)
        {
            throw new OnnxPolicyException($"OnnxPolicy.Load: failed to load '{modelPath}': {ex.Message}", ex);
        }

        try
        {
            OnnxPolicyCore.Validate(session, contract.FDim, contract.IDim, contract.NActions);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        GD.Print($"[SpireBot] OnnxPolicy.Load: loaded and validated '{modelPath}' " +
                 $"(f_dim={contract.FDim} i_dim={contract.IDim} n_actions={contract.NActions}).");

        return new OnnxPolicy(session, contract, seed);
    }

    public PolicyResult Choose(DecisionContext ctx, ActionMap map, ObsResult? obs)
    {
        bool[] mask = map.Mask;

        if (obs == null)
        {
            GD.PrintErr("[SpireBot] OnnxPolicy.Choose: no Obs available for this decision " +
                        "(ObsBuilder.Build failed upstream) — falling back to first-legal.");
            return new FirstLegalPolicy().Choose(ctx, map, null);
        }

        if (!mask.Any(b => b))
        {
            // No legal action at all — nothing meaningful to run inference on. Mirrors
            // FirstLegalPolicy's own "well-formed but useless" result rather than throwing;
            // BotController's Unsupported-kind handling / stuck guard deal with this upstream.
            GD.PrintErr("[SpireBot] OnnxPolicy.Choose: mask is all-false (no legal action) — returning no-op.");
            return new PolicyResult { ActionId = -1, TopK = Array.Empty<(string, float)>() };
        }

        float[] logits;
        try
        {
            logits = OnnxPolicyCore.RunLogits(_session, obs.F, obs.I, mask, _contract.NActions);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] OnnxPolicy.Choose: inference threw — falling back to first-legal: {ex}");
            return new FirstLegalPolicy().Choose(ctx, map, obs);
        }

        float temperature = SpireBotConfig.Temperature;
        int topK = Math.Max(1, SpireBotConfig.TopK);

        int chosen;
        float[] displayProbs;
        if (temperature <= 0f)
        {
            // Deterministic argmax. The TopK breakdown reported alongside it is a display-only
            // temperature=1 softmax — it never influences which action gets chosen.
            chosen = OnnxPolicyCore.Argmax(logits, mask);
            displayProbs = OnnxPolicyCore.SoftmaxOverLegal(logits, mask, 1f);
        }
        else
        {
            displayProbs = OnnxPolicyCore.SoftmaxOverLegal(logits, mask, temperature);
            chosen = OnnxPolicyCore.SampleFromTopK(displayProbs, mask, topK, _rng);
        }

        if (chosen < 0)
        {
            // Should be unreachable (mask has at least one true entry, checked above), but
            // guard anyway rather than ever propagating a -1 action id downstream.
            GD.PrintErr("[SpireBot] OnnxPolicy.Choose: action selection produced no id despite " +
                        "a legal mask — falling back to first-legal.");
            return new FirstLegalPolicy().Choose(ctx, map, obs);
        }

        var ranked = OnnxPolicyCore.RankedTopK(displayProbs, mask, topK);
        var topKResult = ranked.Select(r => (map.LabelFor(r.id), r.prob)).ToArray();

        return new PolicyResult { ActionId = chosen, TopK = topKResult };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
