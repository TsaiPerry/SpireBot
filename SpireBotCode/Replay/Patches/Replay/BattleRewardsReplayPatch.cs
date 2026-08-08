using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace SpireBot.Replay.Patches.Replay;
using SpireBot.Replay;

/// <summary>
/// Harmony postfix on NRewardsScreen._Ready that captures the active screen
/// and triggers replay dispatch.
/// </summary>
[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
public static class BattleRewardsReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRewardsScreen __instance)
    {
        ReplayState.ActiveRewardsScreen = __instance;
        if (ReplayEngine.IsActive)
            ReplayDispatcher.DispatchNow();
    }
}
