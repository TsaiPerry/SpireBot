using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using SpireBot.Replay.Utils;

namespace SpireBot.Replay.Patches.Replay;
using SpireBot.Replay;

[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
public static class RestSiteReplayPatch
{
    [HarmonyPostfix]
    public static void Postfix(RestSiteSynchronizer __instance)
    {
        RngCheckpointLogger.Log("RestSite (BeginRestSite)");

        if (!ReplayEngine.IsActive)
            return;

        ReplayState.ActiveRestSiteSynchronizer = __instance;
        ReplayDispatcher.DispatchNow();
    }
}
