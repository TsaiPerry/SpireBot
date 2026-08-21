using Godot;
using HarmonyLib;

using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Harmony postfix on <c>NMainMenu._Ready()</c>: run-boundary cleanup. Reaching the main menu
/// means any bot-attached run is over, but nothing else ever calls Stop() — without this,
/// BotDriving/heartbeat survive an abandoned run and the bot keeps dispatching into whatever
/// is played next. Likewise for an armed-but-not-yet-attached run (RunContinueAttach): a run
/// abandoned before its first room loaded would otherwise leave that latch waiting, and the
/// next run to enter a room would be attached under the abandoned run's seed.
///
/// This class used to also inject a "Bot Run" main-menu button; the 2026-08-20 UX pacing
/// change deleted it — the bot now attaches to every eligible run (RunNewAttach /
/// RunContinueAttach), so a dedicated bot-started run no longer exists.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class MenuInjection
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try
        {
            RunContinueAttach.Disarm();

            if (BotController.IsRunning)
            {
                GD.Print("[SpireBot] MenuInjection: main menu reached with the bot still " +
                         "attached — stopping the bot decision loop.");
                BotController.Stop();
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[SpireBot] MenuInjection.Postfix cleanup failed: {ex}");
        }
    }
}
