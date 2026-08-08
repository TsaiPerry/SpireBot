using System;

using Godot;
using HarmonyLib;

using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Attaches the bot as a paused ADVISOR to a run the player resumed with the game's own Continue
/// button, so "Will take: ..." shows up on a continued run the same way it does on one started
/// from the mod's own "Bot Run" button (<see cref="MenuInjection"/>).
///
/// Two-stage, because the obvious single hook is mis-timed. <c>RunManager.SetUpSavedSingleplayer</c>
/// is <c>async Task</c> with a real await before it finishes, so a Harmony postfix on it runs at
/// the first await — after <c>RunManager.State</c> is assigned, but BEFORE the caller's
/// <c>NGame.LoadRun</c> has loaded the map, the room, or any screen. Attaching there would leave
/// the decision loop grinding out Unsupported decisions once a second against a run that has not
/// finished loading. So the postfix only ARMS, and the actual attach happens on the first
/// <c>RunManager.RoomEntered</c> — the same readiness signal the vendored dispatcher keys its own
/// dispatching off (Replay/ReplayDispatcher.cs:944-956).
///
/// Deliberately separate from the vendored <c>Replay/Patches/Record/RunContinuePatch.cs</c>, which
/// patches the same method for an unrelated purpose (restoring its action-log buffer). Harmony
/// runs both; that file is vendored and must not be edited.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpSavedSingleplayer))]
public static class RunContinueAttach
{
    /// <summary>The character the policy and contract were trained for. A resumed run of any
    /// other character is left alone: the action space and observation layout are Ironclad's, so
    /// the advice would be confidently wrong rather than absent.</summary>
    private const string SupportedCharacter = "IRONCLAD";

    private static bool _armed;
    private static string _pendingSeed = "";

    [HarmonyPostfix]
    public static void Postfix(SerializableRun save)
    {
        try
        {
            if (!ShouldAttach(save, out string seed))
                return;

            _pendingSeed = seed;
            Arm();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] RunContinueAttach.Postfix failed — the resumed run will " +
                        $"not be advised: {ex}");
        }
    }

    /// <summary>Every reason to decline, each logged once so a run that silently gets no advice
    /// always says why in the log.</summary>
    private static bool ShouldAttach(SerializableRun? save, out string seed)
    {
        seed = "";

        if (save == null)
            return false;

        // The advisor is exactly what StartPaused asks for, so it gates this too rather than
        // adding a second setting. Off means Continue behaves as it did before this feature.
        if (!SpireBotConfig.StartPaused)
            return false;

        if (BotController.IsRunning)
        {
            GD.Print("[SpireBot] RunContinueAttach: a run is already being driven — not attaching.");
            return false;
        }

        if (save.GameMode != GameMode.Standard)
        {
            GD.Print($"[SpireBot] RunContinueAttach: resumed run is {save.GameMode}, not " +
                     $"{GameMode.Standard} — not attaching.");
            return false;
        }

        // Singleplayer saves carry exactly one player; CharacterId is the ModelId the game itself
        // logs for a continued run (NMainMenu.cs:405).
        string? character = save.Players?.Count > 0 ? save.Players[0].CharacterId?.Entry : null;
        if (character != SupportedCharacter)
        {
            GD.Print($"[SpireBot] RunContinueAttach: resumed run is character " +
                     $"'{character ?? "unknown"}', not {SupportedCharacter} — not attaching, " +
                     $"because the policy's action space and observations are {SupportedCharacter}'s.");
            return false;
        }

        seed = save.SerializableRng?.Seed ?? "unknown-seed";
        return true;
    }

    private static void Arm()
    {
        if (_armed) return;

        var manager = RunManager.Instance;
        if (manager == null)
        {
            GD.PrintErr("[SpireBot] RunContinueAttach: RunManager.Instance is null — cannot wait " +
                        "for the room to load, so the resumed run will not be advised.");
            return;
        }

        _armed = true;
        manager.RoomEntered += OnRoomEntered;
        GD.Print($"[SpireBot] RunContinueAttach: armed for resumed run seed='{_pendingSeed}' — " +
                 $"attaching once the first room is entered.");
    }

    private static void OnRoomEntered()
    {
        // One-shot: disarm before attaching, so a throw below cannot leave the handler wired to
        // fire again on the next room of a run that is already being advised.
        Disarm();
        BotController.AttachToRun(_pendingSeed);
    }

    /// <summary>
    /// Drops the pending attach. Called on the first room (the attach itself) and from
    /// <see cref="MenuInjection"/> when the main menu is reached — without that, quitting a
    /// resumed run before its first room ever loaded would leave this subscribed, and the next
    /// run to enter a room would be attached to under the previous run's seed.
    /// </summary>
    public static void Disarm()
    {
        if (!_armed) return;
        _armed = false;

        var manager = RunManager.Instance;
        if (manager != null)
            manager.RoomEntered -= OnRoomEntered;
    }
}
