using System;
using System.IO;
using Godot;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Debug-build-only sanity check that the configured contract loads and its data looks sane.
/// Runs once at mod init, after PatchAll. Never throws — a bad contract here should be visible
/// in the log, not crash mod load (the config may simply be unset on a first launch).
/// </summary>
public static class ContractSelfTest
{
    public static void Run()
    {
        // Same resolution the bot itself uses, so the self-test validates the file a Bot Run
        // would actually load (a configured directory, or the default next to the mod DLL).
        string? path = ModPaths.ResolveDataFile(SpireBotConfig.ContractPath, "contract.json");
        if (path == null || !File.Exists(path))
        {
            GD.Print($"[SpireBot] ContractSelfTest skipped (no contract.json found; " +
                     $"ContractPath='{SpireBotConfig.ContractPath}').");
            return;
        }

        try
        {
            var contract = Contract.Load(path);

            if (contract.Actions.NActions != contract.NActions)
            {
                throw new ContractException(
                    $"Actions.NActions ({contract.Actions.NActions}) != NActions ({contract.NActions}).");
            }
            if (contract.NActions != 253)
                throw new ContractException($"expected NActions == 253, got {contract.NActions}.");

            // v22 discard block: 10 slots directly after the belt-potion block,
            // closing out the action space (run_env.py: DISCARD_BASE =
            // POTION_BASE + MAX_POTION_SLOTS; N_ACTIONS = DISCARD_BASE + MAX_POTION_SLOTS).
            var discard = contract.Actions.Discard;
            var belt = contract.Actions.BeltPotion;
            if (discard.Base != belt.Base + belt.Slots)
                throw new ContractException(
                    $"Actions.Discard.Base ({discard.Base}) != BeltPotion.Base+Slots ({belt.Base + belt.Slots}).");
            if (discard.Base + discard.Slots != contract.NActions)
                throw new ContractException(
                    $"Discard.Base+Slots ({discard.Base + discard.Slots}) != NActions ({contract.NActions}).");

            // Known real segment names, probed from a generated contract.json (sts2-rl
            // sts2_rl/live/export_contract). If the contract's obs layout ever renames these,
            // this self-test should fail loudly rather than silently pass on a stale probe.
            var phase = contract.F("phase");
            if (phase.Width != 10)
                throw new ContractException($"F('phase') width {phase.Width} != 10.");

            var potionIds = contract.I("run.potions.ids");
            if (potionIds.Width != 10)
                throw new ContractException($"I('run.potions.ids') width {potionIds.Width} != 10.");

            int strikeVocabId = contract.VocabId("cards", "CARD.AGGRESSION");
            if (strikeVocabId == 0)
                throw new ContractException("VocabId('cards', 'CARD.AGGRESSION') returned 0 (PAD); expected a real id.");

            int unknownVocabId = contract.VocabId("cards", "definitely_not_a_card");
            if (unknownVocabId != 0)
                throw new ContractException($"VocabId('cards', 'definitely_not_a_card') returned {unknownVocabId}, expected 0 (PAD).");

            GD.Print($"[SpireBot] ContractSelfTest OK (f={contract.FDim}, i={contract.IDim}, actions={contract.NActions})");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] ContractSelfTest FAILED: {ex}");
        }
    }
}
