using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SpireBot.Replay.Patches.Replay;
using SpireBot.Replay;

[HarmonyPatch(typeof(NDeckUpgradeSelectScreen), "ShowScreen")]
public static class UpgradeCardReplayPatch
{
    internal static NCardGridSelectionScreen? selectionScreen;
    
    [HarmonyPostfix]
    public static void Postfix(NDeckUpgradeSelectScreen __result, IReadOnlyList<CardModel> cards)
    {
        selectionScreen = __result;
    }
}
