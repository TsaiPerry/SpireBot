using System;
using System.Collections.Generic;
using Godot;
using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Suppresses commands that must happen at most once per room, after they have happened.
///
/// <c>ReplayDispatcher.GetDispatchableTypes</c> offers <see cref="OpenChestCommand"/> for as
/// long as the current room is a treasure room — it has no notion of "the chest is already
/// open", because a recorded replay contains exactly one OpenChest and simply never asks
/// again. A live policy re-picks it at every decision, and each dispatch re-runs
/// <c>NTreasureRoom.OpenChest</c> → <c>NTreasureRoomRelicCollection.InitializeRelics</c>,
/// minting a fresh set of relics every time. Observed live as an endless stream of treasure
/// rewards.
///
/// The guard is per-room: the record clears whenever the room identity changes, so the next
/// chest is openable normally.
/// </summary>
internal static class RoomOneShots
{
    /// <summary>Command types that are legitimate at most once per room visit.</summary>
    private static readonly HashSet<Type> OneShotTypes = new()
    {
        typeof(OpenChestCommand),
    };

    private static string? _roomKey;
    private static readonly HashSet<Type> _done = new();

    internal static void Reset()
    {
        _roomKey = null;
        _done.Clear();
    }

    /// <summary>Call once per decision, before building the action map.</summary>
    internal static void OnDecision(DecisionContext ctx)
    {
        string key = $"{ctx.Snapshot.RoomType ?? "<none>"}@{ctx.Snapshot.Floor}";
        if (key == _roomKey)
            return;

        _roomKey = key;
        _done.Clear();
    }

    /// <summary>Records a dispatch so a one-shot command is not offered again in this room.</summary>
    internal static void Record(ReplayCommand cmd)
    {
        if (OneShotTypes.Contains(cmd.GetType()) && _done.Add(cmd.GetType()))
            GD.Print($"[SpireBot] RoomOneShots: '{cmd.Describe()}' is once-per-room — " +
                     $"suppressing it for the rest of {_roomKey}.");
    }

    /// <summary>True if this command already happened in the current room and must not repeat.</summary>
    internal static bool AlreadyDone(ReplayCommand cmd) => _done.Contains(cmd.GetType());
}
