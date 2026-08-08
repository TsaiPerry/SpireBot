using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

using SpireBot.Replay.Patches.Replay;
namespace SpireBot.Replay.Commands;

/// <summary>
/// Open the treasure chest.
/// Recorded as: "OpenChest # {relicTitle}"
///
/// Emits the chest button's Released signal to trigger OpenChest().
/// A TakeChestRelic command follows to pick the relic.
/// </summary>
public sealed class OpenChestCommand : ReplayCommand
{
    private const string Prefix = "OpenChest";

    public OpenChestCommand() : base("") { }

    public override string ToString() => Prefix;

    public override string Describe()
        => Comment != null ? $"open chest ({Comment})" : "open chest";

    public override ExecuteResult Execute()
    {
        NTreasureRoom? room = TreasureRoomReplayPatch.ActiveRoom;
        if (room == null || !room.IsInsideTree())
            return ExecuteResult.Retry(200);

        NButton? chest = room.GetNodeOrNull<NButton>("%Chest");
        if (chest == null)
        {
            PlayerActionBuffer.LogToDevConsole("[OpenChest] Chest button node not found.");
            return ExecuteResult.Retry(200);
        }

        // SpireBot delta: obey the same gate a real click passes
        // (NClickableControl.HandleMouseRelease). OnChestButtonReleased calls
        // _chestButton.Disable() after opening, so without this a re-emitted signal re-runs
        // OpenChest() and mints another relic set — something no player can do.
        if (!SpireBot.SpireBotCode.Affordance.CheckOrRefuse(chest, "open chest"))
            return ExecuteResult.Ok();

        chest.EmitSignal(NClickableControl.SignalName.Released, chest);
        return ExecuteResult.Ok();
    }

    public static OpenChestCommand? TryParse(string raw)
    {
        if (raw == Prefix)
            return new OpenChestCommand();

        return null;
    }
}

/// <summary>
/// Pick the relic from the opened treasure chest.
/// Recorded as: "TakeChestRelic"
///
/// Always picks relic at index 0 (treasure chests offer one relic).
/// </summary>
public sealed class TakeChestRelicCommand : ReplayCommand
{
    private const string Cmd = "TakeChestRelic";

    public TakeChestRelicCommand() : base("") { }

    public override string ToString() => Cmd;

    public override string Describe() => "take chest relic";

    public override ExecuteResult Execute()
    {
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;

        // SpireBot delta: obey the same precondition PickRelicLocally enforces
        // (TreasureRoomRelicSynchronizer.cs:161 throws when _currentRelics is null). The
        // dispatcher offers this command for the whole treasure room, so a live driver would
        // otherwise call it before the chest is open / after the relic is taken and throw.
        if (!SpireBot.SpireBotCode.Affordance.RelicPickingActive())
        {
            PlayerActionBuffer.LogDispatcher("[TakeChestRelic] relic picking is not active — skipping.");
            return ExecuteResult.Ok();
        }

        PlayerActionBuffer.LogDispatcher("[TakeChestRelic] PickRelicLocally(0)");
        Callable.From(() => sync.PickRelicLocally(0)).CallDeferred();
        return ExecuteResult.Ok();
    }

    public static TakeChestRelicCommand? TryParse(string raw)
    {
        if (raw == Cmd)
            return new TakeChestRelicCommand();

        return null;
    }
}
