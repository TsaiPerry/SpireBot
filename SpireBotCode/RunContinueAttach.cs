using System;

using Godot;
using HarmonyLib;

using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Attaches the bot as a paused ADVISOR to a run the player resumed with the game's own Continue
/// button, so "Will take: ..." shows up on a continued run.
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
    /// the advice would be confidently wrong rather than absent. Shared with
    /// <see cref="RunNewAttach"/>, which applies the same rule to brand-new runs.</summary>
    internal const string SupportedCharacter = "IRONCLAD";

    /// <summary>The ascension the policy was trained at. Shared with <see cref="RunNewAttach"/>.</summary>
    internal const int SupportedAscension = 0;

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

        // Same reasoning as the character check: the policy was trained at ascension 0, and the
        // ascension modifiers change the rules the advice is reasoning about.
        if (save.Ascension != SupportedAscension)
        {
            GD.Print($"[SpireBot] RunContinueAttach: resumed run is ascension {save.Ascension}, " +
                     $"not {SupportedAscension} — not attaching, because the policy was trained " +
                     $"at ascension {SupportedAscension}.");
            return false;
        }

        seed = save.SerializableRng?.Seed ?? "unknown-seed";
        return true;
    }

    /// <summary>
    /// Shared entry point for the other attach trigger (<see cref="RunNewAttach"/>): stores the
    /// seed and arms the same one-shot RoomEntered attach this class uses for continued runs.
    /// Both triggers funnel into one latch on purpose — <see cref="MenuInjection"/>'s single
    /// <see cref="Disarm"/> call then covers every abandoned-before-first-room case.
    /// </summary>
    internal static void ArmForSeed(string seed)
    {
        _pendingSeed = seed;
        Arm();
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

/// <summary>
/// The same paused-advisor attach as <see cref="RunContinueAttach"/>, for a BRAND-NEW run the
/// player starts through the game's own character-select flow — without this, "Will take: ..."
/// only ever showed on continued runs, and a normal new run got no overlay at all.
///
/// Timing mirrors the Continue path exactly. <c>RunManager.SetUpNewSingleplayer</c> runs
/// synchronously inside <c>NGame.StartNewSingleplayerRun</c> (NGame.cs:741) BEFORE
/// <c>StartRun</c> has loaded the map or any room, so attaching here would grind Unsupported
/// decisions against a half-loaded run; the postfix only arms, and the attach happens on the
/// first <c>RunManager.RoomEntered</c> via <see cref="RunContinueAttach.ArmForSeed"/>'s shared
/// latch.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewSingleplayer))]
public static class RunNewAttach
{
    [HarmonyPostfix]
    public static void Postfix(RunState state, DateTimeOffset? dailyTime)
    {
        try
        {
            if (!ShouldAttach(state, dailyTime, out string seed))
                return;

            RunContinueAttach.ArmForSeed(seed);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] RunNewAttach.Postfix failed — the new run will not be " +
                        $"advised: {ex}");
        }
    }

    /// <summary>Same decline-with-a-log contract as <see cref="RunContinueAttach"/>'s
    /// ShouldAttach, reading the live <see cref="RunState"/> instead of a save.</summary>
    private static bool ShouldAttach(RunState? state, DateTimeOffset? dailyTime, out string seed)
    {
        seed = "";

        if (state == null)
            return false;

        // Defensive re-entrancy guard: a run is already being driven/advised, so this new
        // SetUpNewSingleplayer (whatever triggered it) must not double-attach.
        if (BotController.IsRunning)
            return false;

        if (dailyTime != null)
        {
            GD.Print("[SpireBot] RunNewAttach: new run is a daily run — not attaching.");
            return false;
        }

        if (state.GameMode != GameMode.Standard)
        {
            GD.Print($"[SpireBot] RunNewAttach: new run is {state.GameMode}, not " +
                     $"{GameMode.Standard} — not attaching.");
            return false;
        }

        string? character = state.Players?.Count > 0
            ? state.Players[0].Character?.Id.Entry
            : null;
        if (character != RunContinueAttach.SupportedCharacter)
        {
            GD.Print($"[SpireBot] RunNewAttach: new run is character " +
                     $"'{character ?? "unknown"}', not {RunContinueAttach.SupportedCharacter} — " +
                     $"not attaching, because the policy's action space and observations are " +
                     $"{RunContinueAttach.SupportedCharacter}'s.");
            return false;
        }

        if (state.AscensionLevel != RunContinueAttach.SupportedAscension)
        {
            GD.Print($"[SpireBot] RunNewAttach: new run is ascension {state.AscensionLevel}, " +
                     $"not {RunContinueAttach.SupportedAscension} — not attaching, because the " +
                     $"policy was trained at ascension {RunContinueAttach.SupportedAscension}.");
            return false;
        }

        seed = state.Rng?.StringSeed ?? "unknown-seed";
        return true;
    }
}
