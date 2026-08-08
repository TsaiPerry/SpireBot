using BaseLib.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace SpireBot.SpireBotCode;

// Adding [ModInitializer] disables the game's auto-PatchAll, so we call
// harmony.PatchAll() explicitly here to pick up all [HarmonyPatch] classes.
[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "SpireBot";

    public static void Initialize()
    {
        // Must run before BaseLib's post-mod-init reflection pass forces our dependency loads.
        AssemblyResolution.Install();

        ModConfigRegistry.Register(ModId, new SpireBotConfig());

        try
        {
            new Harmony(ModId).PatchAll();
        }
        catch (System.Exception ex)
        {
            // A failed patch leaves the bot blind (ReplayState never populates) — surface it loudly
            // rather than letting the mod half-load.
            GD.PrintErr($"[SpireBot] PatchAll failed — bot disabled: {ex}");
            return;
        }

        GD.Print("[SpireBot] initialized");

        // Task 16: dev-only passive observation of a fork-driven playback. Must run after
        // PatchAll succeeds — it depends on the vendored Harmony patches (ReplayState etc.)
        // being live, and must not run at all if PatchAll failed and this method already
        // returned above.
        PassiveDump.TryInstall();

        // NOT OS.IsDebugBuild(): the shipped game is an export_release build, so that gate is
        // always false in game and the self-test would never run where it matters.
        if (SpireBotConfig.SelfChecks)
        {
            ContractSelfTest.Run();
        }
    }
}
