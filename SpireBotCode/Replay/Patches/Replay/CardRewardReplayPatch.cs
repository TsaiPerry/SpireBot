using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using SpireBot.Replay.Commands;

namespace SpireBot.Replay.Patches.Replay;
using SpireBot.Replay;

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
public static class CardRewardReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCardRewardSelectionScreen __instance)
    {
        if (!ReplayEngine.IsActive)
            return;

        ReplayState.CardRewardSelectionScreen = __instance;
    }
}