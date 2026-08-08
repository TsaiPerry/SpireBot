using Godot;
using HarmonyLib;

using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Harmony postfix on <c>NMainMenu._Ready()</c> that injects a "Bot Run" button into the main
/// menu, just above the Quit button. Deliberately a minimal fresh implementation rather than a
/// vendored copy — RunReplays' <c>MainMenuButtonInjector.cs</c> (read for the node paths/pattern
/// below) was NOT vendored into SpireBot (see Replay/VENDORED-FROM.md), so this only reproduces
/// the injection mechanics, not any of RunReplays' own submenu/replay-list machinery.
///
/// Node paths and duplication approach mirror MainMenuButtonInjector.Postfix
/// (RunReplays/MainMenuButtonInjector.cs:42-137):
///   - "%MainMenuTextButtons" is the vertical button container (Godot unique-name accessor).
///   - "MainMenuTextButtons/QuitButton" is duplicated (flags 6 = Groups | Scripts, no Signals)
///     as a template so the new button gets the game's own hover/press visuals via its _Ready()
///     rather than us hand-building them; omitting Signals avoids also copying Quit's
///     game-exit connection.
///   - The duplicate is inserted at the Quit button's original index, which pushes Quit down
///     one slot — i.e. placed directly above Quit, matching the original.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class MenuInjection
{
    [HarmonyPostfix]
    public static void Postfix(NMainMenu __instance)
    {
        try
        {
            // Reaching the main menu means any bot-driven run is over, but nothing else ever
            // calls Stop() — without this, BotDriving/heartbeat survive an abandoned bot run
            // and the bot keeps dispatching into whatever is played next (it raced a
            // RunReplays playback to a chest and wedged its OpenChestCommand retry loop).
            if (BotController.IsRunning)
            {
                GD.Print("[SpireBot] MenuInjection: main menu reached with the bot still " +
                         "running — stopping the bot decision loop.");
                BotController.Stop();
            }

            var buttonContainer = __instance.GetNode<Control>("%MainMenuTextButtons");
            if (buttonContainer.HasNode("SpireBotRunButton"))
                return; // _Ready fired again on a menu we already injected into
            var quitButton = __instance.GetNode<NMainMenuTextButton>("MainMenuTextButtons/QuitButton");
            int quitIndex = quitButton.GetIndex();

            var botButton = (NMainMenuTextButton)quitButton.Duplicate(6); // Groups | Scripts, no Signals
            botButton.Name = "SpireBotRunButton";

            // AddChild triggers _Ready() on the duplicate, which initializes its `label` child
            // reference and connects its own internal hover/press signal handlers.
            buttonContainer.AddChild(botButton);
            buttonContainer.MoveChild(botButton, quitIndex);

            if (botButton.label != null)
                botButton.label.Text = "Bot Run";

            botButton.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => OnBotRunPressed()));

            GD.Print("[SpireBot] MenuInjection: 'Bot Run' button added to the main menu.");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[SpireBot] MenuInjection.Postfix failed — 'Bot Run' button not added: {ex}");
        }
    }

    private static void OnBotRunPressed()
    {
        GD.Print("[SpireBot] MenuInjection: 'Bot Run' pressed.");
        BotController.StartBotRun();
    }
}
