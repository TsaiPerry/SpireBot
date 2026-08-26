using System;

using Godot;

using SpireBot.SpireBotCode.Policies;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Startup sanity check for the duplicate-merging greedy decoder
/// (<see cref="OnnxPolicyCore.PlayGroupKeys"/> + <see cref="OnnxPolicyCore.ArgmaxMerged"/>),
/// same shape as <see cref="ContractSelfTest"/>: <see cref="Run"/> executes once at mod init under
/// <see cref="SpireBotConfig.SelfChecks"/>, logs OK or FAILED, never throws. The assertions live
/// in <see cref="Check"/> (throws on failure, no Godot dependency) so a plain console runner can
/// execute them outside the game.
///
/// Why this exists (2026-08-22, runs 13AEBPAE1Y / UNEUS7RD44): the per-instance action space
/// splits one "play a Strike" intent across every Strike copy in hand, so plain argmax picked a
/// UNIQUE action (End Turn at 3 energy, a potion, a lone Defend) over a card whose merged mass
/// was far higher (0.76 across five Strikes vs End Turn 0.24). The group key must keep upgrades,
/// enchantments and afflictions SEPARATE (Perry) — only instances the model cannot tell apart
/// (same hand.ids triple, same upgrade cell, same target slot) merge. Mirrors
/// sts2_rl.evaluation.play_group_keys / merged_greedy_action.
/// </summary>
public static class PolicySelfTest
{
    public static void Run()
    {
        try
        {
            Check();
            GD.Print("[SpireBot] PolicySelfTest OK");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PolicySelfTest FAILED: {ex.Message}");
        }
    }

    public static void Check()
    {
        CheckArgmaxMerged();
        CheckPlayGroupKeys();
        CheckForcedFallbackExclusion();
    }

    /// <summary>
    /// The stuck guard's escape hatch must not re-issue the command that just failed. Before this
    /// rule existed the hatch was plain first-legal, so a wedged lowest-id command was chosen
    /// forever — the act-1 boss reward-screen livelock (run EYXZUNASYP). The failure was silent:
    /// the guard logged "forcing a fallback action" every time while changing nothing.
    /// </summary>
    private static void CheckForcedFallbackExclusion()
    {
        // The livelock's own shape: three legal exits, the wedged one lowest.
        var mask = new[] { true, true, true };
        string?[] cmds = { "skip rewards", "proceed to next act", "proceed to map" };
        string? Key(int i) => cmds[i];

        Expect(BotController.FirstLegalOtherThan(mask, Key, "skip rewards", skip: -1) == 1,
            "must step past the command that just failed");

        // Identical commands in several slots are all the same failed command.
        Expect(BotController.FirstLegalOtherThan(
                   mask, i => i == 2 ? "other" : "skip rewards", "skip rewards", skip: -1) == 2,
            "every slot holding the failed command must be skipped, not just the first");

        // Combat: end-turn is held back so a wedged decision cannot escape by ending the turn
        // while real plays remain legal.
        Expect(BotController.FirstLegalOtherThan(
                   new[] { true, true, true }, i => i == 0 ? "end turn" : "strike",
                   "strike", skip: 0) == -1,
            "end-turn must not be taken while it is the skipped slot");

        // Only the wedged command is legal — report no alternative rather than inventing one.
        Expect(BotController.FirstLegalOtherThan(
                   new[] { true }, _ => "skip rewards", "skip rewards", skip: -1) == -1,
            "a sole wedged command has no alternative");

        // Masked slots are never candidates.
        Expect(BotController.FirstLegalOtherThan(
                   new[] { true, false }, i => i == 0 ? "wedged" : "free", "wedged", skip: -1) == -1,
            "a masked slot must not be offered as the alternative");
    }

    private static void CheckArgmaxMerged()
    {
        var logits = new[] { 1.0f, 0.9f, 0.9f, 0.0f };
        var mask = new[] { true, true, true, false };
        var keys = new string?[] { null, "strike", "strike", null };

        // Plain argmax takes action 0; merged, the two strikes (0.32+0.32) beat it (0.36).
        Expect(OnnxPolicyCore.Argmax(logits, mask) == 0, "baseline argmax should pick 0");
        int merged = OnnxPolicyCore.ArgmaxMerged(logits, mask, keys);
        Expect(merged == 1 || merged == 2, $"merged argmax should pick a strike, got {merged}");

        // Within the winning group the highest-logit member is dispatched.
        Expect(OnnxPolicyCore.ArgmaxMerged(new[] { 1.0f, 0.9f, 0.95f, 0.0f }, mask, keys) == 2,
            "highest-logit member of the winning group should win");

        // Singletons only -> identical to argmax.
        Expect(OnnxPolicyCore.ArgmaxMerged(logits, mask, new string?[4]) == 0,
            "all-null keys should reduce to argmax");

        // A masked-out member contributes nothing to its group.
        int m2 = OnnxPolicyCore.ArgmaxMerged(new[] { 1.0f, 0.9f, 0.9f, 5.0f }, mask,
            new string?[] { null, "s", "s", "s" });
        Expect(m2 == 1 || m2 == 2, $"masked member must not add mass, got {m2}");

        // No legal action -> -1, like Argmax.
        Expect(OnnxPolicyCore.ArgmaxMerged(logits, new bool[4], keys) == -1,
            "no legal action should return -1");

        // keys shorter than logits: missing entries are singletons, no throw.
        Expect(OnnxPolicyCore.ArgmaxMerged(logits, mask, new string?[] { null }) == 0,
            "short keys array should be tolerated");
    }

    private static void CheckPlayGroupKeys()
    {
        // Tiny synthetic layout: 3 hand slots x 2 enemies; play ids 1..6; ids block at 0
        // (3 ints per slot); hand.f block at 0 (width 31 per slot, upgrade at 16).
        var L = new HandGroupLayout(
            PlayBase: 1, MaxHand: 3, MaxEnemies: 2,
            HandIdsOffset: 0, HandFOffset: 0, CardFeatureWidth: 31, UpgradeFeatureIndex: 16);
        int nActions = 1 + 3 * 2 + 2; // end turn, 6 plays, 2 "potion" ids
        var i = new long[9];
        var f = new float[3 * 31];
        // slot 0 and slot 1: identical Strike (card 5, no affliction, no enchantment, upgrade 0)
        i[0] = 5; i[3] = 5;
        // slot 2: Defend (card 9)
        i[6] = 9;
        var mask = new bool[nActions];
        for (int a = 1; a <= 6; a++) mask[a] = true;
        mask[0] = true; mask[7] = true;

        var keys = OnnxPolicyCore.PlayGroupKeys(f, i, mask, L);
        Expect(keys.Length == nActions, "one key per action id");
        // same card, same target -> same key
        Expect(keys[1] != null && keys[1] == keys[3], "identical cards on the same target must share a key");
        // different target -> different key
        Expect(keys[1] != keys[2], "different target slot must be a different group");
        // different card id -> different key
        Expect(keys[5] != keys[1], "different card id must be a different group");
        // non-play and masked-out -> null
        Expect(keys[0] == null && keys[7] == null && keys[8] == null, "end turn / potion ids carry no key");
        mask[3] = false;
        Expect(OnnxPolicyCore.PlayGroupKeys(f, i, mask, L)[3] == null, "masked-out play id carries no key");
        mask[3] = true;

        // upgrade level splits the group
        f[1 * 31 + 16] = 0.2f;
        Expect(OnnxPolicyCore.PlayGroupKeys(f, i, mask, L)[3] != keys[1], "upgraded copy must not merge");
        f[1 * 31 + 16] = 0f;
        // enchantment id splits the group
        i[3 + 2] = 7;
        Expect(OnnxPolicyCore.PlayGroupKeys(f, i, mask, L)[3] != keys[1], "enchanted copy must not merge");
        i[3 + 2] = 0;
        // affliction id splits the group
        i[3 + 1] = 4;
        Expect(OnnxPolicyCore.PlayGroupKeys(f, i, mask, L)[3] != keys[1], "afflicted copy must not merge");
        i[3 + 1] = 0;
        // back to identical -> merges again
        Expect(OnnxPolicyCore.PlayGroupKeys(f, i, mask, L)[3] == keys[1], "restored copy must merge again");
    }

    private static void Expect(bool cond, string message)
    {
        if (!cond) throw new Exception(message);
    }
}
