using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBot.SpireBotCode;

/// <summary>
/// "Would a real player's click land right now?" — the bot's rule for touching game UI.
///
/// Every clickable control in the game derives from <see cref="NClickableControl"/>, and a real
/// click is gated in the INPUT path, not in the handler
/// (<c>src/Core/Nodes/GodotExtensions/NClickableControl.cs:107</c>):
/// <code>
/// if (_isEnabled &amp;&amp; IsVisibleInTree() &amp;&amp; IsFocused &amp;&amp; ...) OnReleaseHandler();
/// </code>
/// Because the handler never re-checks, emitting <c>Released</c> directly — which is what the
/// vendored replay commands do, and what <c>NClickableControl.ForceClick()</c> ("Used by AutoSlay
/// for automated testing", :255) does too — bypasses the gate entirely. That is how the bot
/// re-opened an already-opened treasure chest: <c>NTreasureRoom.OnChestButtonReleased</c> calls
/// <c>_chestButton.Disable()</c> right after opening, so a player physically cannot click it
/// again, but a raw signal emit re-ran <c>OpenChest()</c> and minted fresh relics every time.
///
/// A recorded replay never had to care — its script only ever contains clicks a player really
/// made. A live policy does, so the bot re-applies the game's own precondition before acting.
///
/// <see cref="NClickableControl.IsEnabled"/> is public, so no publicizer is needed.
/// <c>IsFocused</c> (hover/controller focus) is deliberately NOT required: it reflects where the
/// mouse happens to be, which is a property of the input device rather than of the affordance.
/// </summary>
internal static class Affordance
{
    /// <summary>True when a real click on this control would be accepted by the game.</summary>
    internal static bool IsLive(NClickableControl? control)
        => control != null
        && GodotObject.IsInstanceValid(control)
        && control.IsInsideTree()
        && control.IsEnabled
        && control.IsVisibleInTree();

    /// <summary>
    /// True when the treasure room is actually accepting a relic pick.
    ///
    /// Not every game precondition is a button: <c>TreasureRoomRelicSynchronizer
    /// .PickRelicLocally</c> throws <c>InvalidOperationException("Attempted to pick relic while
    /// relic picking is not active!")</c> whenever <c>_currentRelics</c> is null
    /// (<c>src/Core/Multiplayer/Game/TreasureRoomRelicSynchronizer.cs:161</c>) — before the
    /// chest is opened and again after the relic is taken. The dispatcher offers
    /// TakeChestRelicCommand for the whole treasure room regardless, so the bot picked it
    /// forever, throwing each time and never reaching the proceed button. The state is public
    /// as <c>CurrentRelics</c>, so the bot can test the same condition the game does.
    /// </summary>
    internal static bool RelicPickingActive()
    {
        var sync = RunManager.Instance?.TreasureRoomRelicSynchronizer;
        return sync?.CurrentRelics is { Count: > 0 };
    }

    /// <summary>
    /// Logs and returns false when the control is not clickable — call sites use this to refuse
    /// the action instead of forcing it through.
    /// </summary>
    internal static bool CheckOrRefuse(NClickableControl? control, string what)
    {
        if (IsLive(control))
            return true;

        GD.Print($"[SpireBot] Affordance: refusing '{what}' — the game has that control " +
                 $"{(control == null || !GodotObject.IsInstanceValid(control) ? "gone" : "disabled/hidden")}, " +
                 "so a player could not click it either.");
        return false;
    }
}
