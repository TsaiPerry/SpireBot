using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SpireBot.SpireBotCode.Policies;

/// <summary>
/// Raised for any problem loading, shape-validating, or running the ONNX policy: a missing
/// input/output, a dtype/shape mismatch against the contract, or an inference failure. Task
/// 14's "no silent fallbacks" rule means every one of these must surface here rather than
/// producing a policy that reads/writes garbage — <see cref="OnnxPolicy"/> is the only place
/// that catches this type, and only to degrade to <see cref="FirstLegalPolicy"/> with a loud
/// log, never silently.
/// </summary>
public sealed class OnnxPolicyException : Exception
{
    public OnnxPolicyException(string message) : base(message) { }
    public OnnxPolicyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Godot-free core: ONNX session shape/dtype validation, masked inference, and the
/// argmax/softmax/top-K-sample math <see cref="OnnxPolicy"/> uses to turn a logits row into a
/// <see cref="PolicyResult"/>. Deliberately has no <c>Godot.*</c> reference (only
/// <c>Microsoft.ML.OnnxRuntime</c>, itself plain .NET) so this exact file can be linked into a
/// standalone console harness outside the mod runtime to exercise it against a real
/// <c>stub.onnx</c> + contract without launching the game — see
/// <c>scratchpad/onnx_check</c>. <see cref="OnnxPolicy"/> is the thin Godot-aware wrapper
/// (GD logging, <see cref="IPolicy"/>/<see cref="DecisionContext"/>/<see cref="ActionMap"/>
/// plumbing, <see cref="SpireBotConfig"/> reads) around this.
///
/// Input/output contract, per <c>sts2_rl/live/export_onnx.py</c> (the exporter that produces
/// every model this class loads): inputs <c>f: float32[1, f_dim]</c>, <c>i: int64[1, i_dim]</c>,
/// <c>mask: bool[1, n_actions]</c> (true == legal); output <c>logits: float32[1, n_actions]</c>,
/// with masked-off positions == <see cref="MaskFill"/> (-1e8, the same fill value
/// <c>models.py</c>'s <c>action_logits</c> implementations use). Batch is fixed at 1 — the
/// exporter traces with no dynamic axes.
/// </summary>
/// <summary>
/// Where <see cref="OnnxPolicyCore.PlayGroupKeys"/> finds the hand in the flat obs arrays and the
/// play block in the action space. Built from the Contract by OnnxPolicy (kept Contract-free here
/// so the core stays compilable standalone for the console self-test runner).
/// </summary>
public readonly record struct HandGroupLayout(
    int PlayBase, int MaxHand, int MaxEnemies,
    int HandIdsOffset, int HandFOffset, int CardFeatureWidth, int UpgradeFeatureIndex);

public static class OnnxPolicyCore
{
    /// <summary>Illegal-position fill value the exporter's graph uses in its logits output —
    /// not load-bearing for this class's own math (which reads the mask, never the fill
    /// value, to decide legality) but documented here since it is the shared literal
    /// <c>export_onnx.py</c>'s <c>_MASK_FILL</c> and <c>models.py</c>'s <c>action_logits</c>
    /// both use.</summary>
    public const float MaskFill = -1e8f;

    public static readonly string[] RequiredInputNames = { "f", "i", "mask" };
    public const string OutputName = "logits";

    /// <summary>
    /// Validates that <paramref name="session"/>'s declared input/output names, element types,
    /// and shapes match the contract's <paramref name="fDim"/>/<paramref name="iDim"/>/
    /// <paramref name="nActions"/> exactly. Throws <see cref="OnnxPolicyException"/> with a
    /// specific message identifying exactly what mismatched on any problem — never returns a
    /// partial/best-effort result, per the "refuse rather than silently proceed" requirement.
    /// </summary>
    public static void Validate(InferenceSession session, int fDim, int iDim, int nActions)
    {
        var inputs = session.InputMetadata;
        var outputs = session.OutputMetadata;

        foreach (var name in RequiredInputNames)
        {
            if (!inputs.ContainsKey(name))
            {
                throw new OnnxPolicyException(
                    $"OnnxPolicy: model is missing required input '{name}' " +
                    $"(model declares: [{string.Join(", ", inputs.Keys)}]).");
            }
        }
        if (!outputs.ContainsKey(OutputName))
        {
            throw new OnnxPolicyException(
                $"OnnxPolicy: model is missing required output '{OutputName}' " +
                $"(model declares: [{string.Join(", ", outputs.Keys)}]).");
        }

        CheckTensor("input 'f'", inputs["f"], typeof(float), new[] { 1, fDim });
        CheckTensor("input 'i'", inputs["i"], typeof(long), new[] { 1, iDim });
        CheckTensor("input 'mask'", inputs["mask"], typeof(bool), new[] { 1, nActions });
        CheckTensor("output 'logits'", outputs[OutputName], typeof(float), new[] { 1, nActions });
    }

    private static void CheckTensor(string label, NodeMetadata meta, Type expectedElementType, int[] expectedDims)
    {
        if (!meta.IsTensor)
            throw new OnnxPolicyException($"OnnxPolicy: {label} is not a tensor.");

        if (meta.ElementType != expectedElementType)
        {
            throw new OnnxPolicyException(
                $"OnnxPolicy: {label} has element type {meta.ElementType?.Name ?? "<null>"}, " +
                $"expected {expectedElementType.Name}.");
        }

        int[] dims = meta.Dimensions;
        if (dims.Length != expectedDims.Length)
        {
            throw new OnnxPolicyException(
                $"OnnxPolicy: {label} has rank {dims.Length} ([{string.Join(",", dims)}]), " +
                $"expected rank {expectedDims.Length} ([{string.Join(",", expectedDims)}]).");
        }

        for (int d = 0; d < dims.Length; d++)
        {
            if (dims[d] == expectedDims[d]) continue;
            // -1 (a dynamic/symbolic axis) is tolerated ONLY at the batch axis (d == 0) — every
            // other axis, and the batch axis for a model that hardcodes 1 like the exporter's
            // stub does, must match exactly.
            if (d == 0 && dims[d] == -1) continue;

            throw new OnnxPolicyException(
                $"OnnxPolicy: {label} has shape [{string.Join(",", dims)}], " +
                $"expected [{string.Join(",", expectedDims)}].");
        }
    }

    /// <summary>
    /// Runs <paramref name="session"/> on one <c>(f, i, mask)</c> triple and returns the flat
    /// <paramref name="nActions"/>-length logits row. Throws <see cref="OnnxPolicyException"/>
    /// if the output tensor's length disagrees with <paramref name="nActions"/> (should be
    /// unreachable after <see cref="Validate"/>, but checked again here since this method is
    /// also usable standalone, e.g. from a console harness that skips validation).
    /// </summary>
    public static float[] RunLogits(InferenceSession session, float[] f, long[] i, bool[] mask, int nActions)
    {
        var fTensor = new DenseTensor<float>(f, new[] { 1, f.Length });
        var iTensor = new DenseTensor<long>(i, new[] { 1, i.Length });
        var maskTensor = new DenseTensor<bool>(mask, new[] { 1, mask.Length });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("f", fTensor),
            NamedOnnxValue.CreateFromTensor("i", iTensor),
            NamedOnnxValue.CreateFromTensor("mask", maskTensor),
        };

        using var results = session.Run(inputs);
        var logitsValue = results.FirstOrDefault(r => r.Name == OutputName)
            ?? throw new OnnxPolicyException($"OnnxPolicy: session.Run produced no '{OutputName}' output.");
        var logitsTensor = logitsValue.AsTensor<float>();

        if (logitsTensor.Length != nActions)
        {
            throw new OnnxPolicyException(
                $"OnnxPolicy: '{OutputName}' output has length {logitsTensor.Length}, " +
                $"expected n_actions {nActions}.");
        }

        var logits = new float[nActions];
        for (int a = 0; a < nActions; a++)
            logits[a] = logitsTensor[0, a];
        return logits;
    }

    /// <summary>Uniform tie-break RNG for exact-equal argmax ties — see <see cref="Argmax"/>'s
    /// doc comment for why this matters. A single shared instance is fine: this is called from
    /// the main loop only, no concurrent callers.</summary>
    private static readonly Random TieBreakRng = new();

    /// <summary>
    /// Index of the highest-logit LEGAL action, with exact-equal ties (bit-for-bit equal float
    /// values, no epsilon) broken uniformly at random rather than always keeping the first.
    /// Returns -1 if no mask entry is true.
    ///
    /// Why this matters: with <c>Temperature=0</c> (the config default) and the stub ONNX model
    /// (every legal logit exactly 0.0f), a strict-<c>&gt;</c> first-wins argmax always returned
    /// the lowest legal action id — action 0, end_turn, in combat — so the stub bot ended every
    /// turn without acting and died (2026-08-05 in-game run). A trained model's logits
    /// essentially never tie exactly, so this change does not alter its argmax behavior.
    /// </summary>
    public static int Argmax(float[] logits, bool[] mask)
    {
        float bestVal = float.NegativeInfinity;
        var bestIndices = new List<int>();
        for (int a = 0; a < logits.Length && a < mask.Length; a++)
        {
            if (!mask[a]) continue;
            if (logits[a] > bestVal)
            {
                bestVal = logits[a];
                bestIndices.Clear();
                bestIndices.Add(a);
            }
            else if (logits[a] == bestVal)
            {
                bestIndices.Add(a);
            }
        }
        if (bestIndices.Count == 0) return -1;
        if (bestIndices.Count == 1) return bestIndices[0];
        return bestIndices[TieBreakRng.Next(bestIndices.Count)];
    }

    /// <summary>
    /// Greedy decode over GROUPS of legal actions (2026-08-22, runs 13AEBPAE1Y/UNEUS7RD44):
    /// softmax the legal logits (temperature 1), sum the probability of every action sharing a
    /// non-null <paramref name="keys"/> entry (null = its own singleton group), take the
    /// heaviest group, and dispatch its highest-logit member (ties within the group fall to
    /// <see cref="Argmax"/>'s rule). With all-null keys this IS <see cref="Argmax"/>. Returns
    /// -1 when nothing is legal. Mirrors sts2_rl.evaluation.merged_greedy_action.
    ///
    /// Why: the per-instance action space splits one "play a Strike" intent across every Strike
    /// in hand, so plain argmax took a UNIQUE action (End Turn at 3 energy, a potion, a lone
    /// Defend) over a card whose merged mass was far higher (0.76 over five Strikes vs 0.24).
    /// </summary>
    public static int ArgmaxMerged(float[] logits, bool[] mask, string?[] keys)
    {
        float[] probs = SoftmaxOverLegal(logits, mask, 1f);
        var mass = new Dictionary<string, double>();
        var members = new Dictionary<string, List<int>>();
        string? bestSingleKey = null;
        double bestMass = double.NegativeInfinity;
        int n = Math.Min(logits.Length, mask.Length);
        for (int a = 0; a < n; a++)
        {
            if (!mask[a]) continue;
            string key = a < keys.Length && keys[a] != null ? keys[a]! : $"\u0000single:{a}";
            if (!mass.TryGetValue(key, out double m)) { m = 0; members[key] = new List<int>(); }
            m += probs[a];
            mass[key] = m;
            members[key].Add(a);
        }
        if (mass.Count == 0) return -1;
        foreach (var kv in mass)
        {
            if (kv.Value > bestMass) { bestMass = kv.Value; bestSingleKey = kv.Key; }
        }
        var group = members[bestSingleKey!];
        if (group.Count == 1) return group[0];
        // Highest-logit member of the winning group; exact ties -> Argmax's tie rule.
        var groupMask = new bool[mask.Length];
        foreach (int a in group) groupMask[a] = true;
        return Argmax(logits, groupMask);
    }

    /// <summary>
    /// One group key per action id (null = no group). Legal PLAY actions whose hand instance the
    /// model cannot tell apart share a key: the hand.ids triple (card id, affliction id,
    /// enchantment id), the hand.f upgrade-level cell and the target slot. Upgraded / enchanted /
    /// afflicted copies therefore NEVER merge with their plain counterparts (Perry, 2026-08-22).
    /// End turn, potions, out-of-combat ids and masked-out ids are null. Built from the SAME
    /// f/i arrays the model reads, so it is exactly the model's own notion of "identical".
    /// Mirrors sts2_rl.evaluation.play_group_keys.
    /// </summary>
    public static string?[] PlayGroupKeys(float[] f, long[] i, bool[] mask, HandGroupLayout L)
    {
        var keys = new string?[mask.Length];
        for (int h = 0; h < L.MaxHand; h++)
        {
            int idsBase = L.HandIdsOffset + h * 3;
            int upIdx = L.HandFOffset + h * L.CardFeatureWidth + L.UpgradeFeatureIndex;
            if (idsBase + 2 >= i.Length || upIdx >= f.Length) break;
            long cardId = i[idsBase], afflId = i[idsBase + 1], enchId = i[idsBase + 2];
            float upgrade = f[upIdx];
            for (int e = 0; e < L.MaxEnemies; e++)
            {
                int a = L.PlayBase + h * L.MaxEnemies + e;
                if (a < 0 || a >= mask.Length || !mask[a]) continue;
                keys[a] = $"play:{cardId}:{afflId}:{enchId}:{upgrade:F4}:{e}";
            }
        }
        return keys;
    }

    /// <summary>
    /// Softmax of <c>logits / temperature</c>, restricted to legal (mask-true) positions —
    /// illegal positions are exactly 0 in the result, never a near-zero residual from the
    /// masked logit value. Legal-position probabilities sum to 1 (or are all 0 if no mask
    /// entry is true). <paramref name="temperature"/> must be > 0 (callers route the
    /// temperature==0/argmax case separately; this method still degrades gracefully — treating
    /// temperature<=0 as 1.0 — rather than dividing by zero if ever called with it).
    /// </summary>
    public static float[] SoftmaxOverLegal(float[] logits, bool[] mask, float temperature)
    {
        int n = logits.Length;
        var probs = new float[n];
        float t = temperature > 0f ? temperature : 1f;

        float max = float.NegativeInfinity;
        bool anyLegal = false;
        for (int a = 0; a < n && a < mask.Length; a++)
        {
            if (!mask[a]) continue;
            anyLegal = true;
            float scaled = logits[a] / t;
            if (scaled > max) max = scaled;
        }
        if (!anyLegal)
            return probs; // all zero — caller (OnnxPolicy) is responsible for the no-legal-action guard

        double sum = 0;
        for (int a = 0; a < n && a < mask.Length; a++)
        {
            if (!mask[a]) continue;
            double e = Math.Exp((logits[a] / t) - max);
            probs[a] = (float)e;
            sum += e;
        }
        if (sum > 0)
        {
            for (int a = 0; a < n; a++)
                if (mask[a]) probs[a] = (float)(probs[a] / sum);
        }
        return probs;
    }

    /// <summary>Up to <paramref name="topK"/> (action id, probability) pairs among legal
    /// positions, sorted highest-probability first. <paramref name="topK"/> &lt;= 0 means
    /// "no cap" (return every legal entry).</summary>
    public static List<(int id, float prob)> RankedTopK(float[] probs, bool[] mask, int topK)
    {
        var legal = new List<(int id, float prob)>();
        for (int a = 0; a < probs.Length && a < mask.Length; a++)
            if (mask[a]) legal.Add((a, probs[a]));

        legal.Sort((x, y) => y.prob.CompareTo(x.prob));

        if (topK > 0 && legal.Count > topK)
            legal = legal.GetRange(0, topK);
        return legal;
    }

    /// <summary>
    /// Samples one action id from the top <paramref name="topK"/> legal actions by
    /// probability, re-normalized among just that subset (not the full legal set — matches the
    /// "restrict sampling to the top K" contract of <see cref="SpireBotConfig.TopK"/>). Returns
    /// -1 if there are no legal actions at all.
    /// </summary>
    public static int SampleFromTopK(float[] probs, bool[] mask, int topK, Random rng)
    {
        var ranked = RankedTopK(probs, mask, topK);
        if (ranked.Count == 0) return -1;

        double total = ranked.Sum(r => (double)r.prob);
        if (total <= 0)
            return ranked[0].id; // degenerate (e.g. every top-K prob rounded to 0) — take the highest-logit one

        double roll = rng.NextDouble() * total;
        double cum = 0;
        foreach (var (id, prob) in ranked)
        {
            cum += prob;
            if (roll <= cum) return id;
        }
        return ranked[^1].id; // floating-point rounding fallback — last entry
    }
}
