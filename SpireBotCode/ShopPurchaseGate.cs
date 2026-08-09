using System.Linq;

using Godot;

using MegaCrit.Sts2.Core.Entities.Merchant;

using SpireBot.Replay;
using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Holds the decision loop while a shop purchase is still resolving.
///
/// ── The bug this exists for ───────────────────────────────────────────────────────────────
/// <c>BuyCardCommand</c>, <c>BuyRelicCommand</c> and <c>BuyPotionCommand</c> call
/// <c>OpenShopCommand.InvokePurchase(entry)</c> and return <c>ExecuteResult.Ok()</c> on the very
/// next line (Replay/Commands/ShopCommands.cs:216-218 and the equivalents). <c>InvokePurchase</c>
/// fire-and-forgets the <c>Task</c> the game hands back, so "Ok" means "the purchase was
/// started", not "the purchase finished". Ok drains the command from the pending queue, and
/// <c>ReplayEngine.HasPendingCommand</c> — the only thing that stops
/// <c>BotController.OnInputRequired</c> from deciding again — immediately goes false.
///
/// The game removes a purchased item from the shelf in <c>MerchantEntry.ClearAfterPurchase</c>,
/// which runs only AFTER the awaited <c>OnTryPurchase</c> completes
/// (MerchantEntry.cs:81-89 in the decompiled source). So in the window between those two
/// moments the item is still stocked and the gold is still unspent, and the bot happily buys it
/// again — and again. That is the "buys everything too quickly" symptom.
///
/// It also crashes the game. <c>OnTryPurchaseWrapper</c> checks <c>IsStocked</c> synchronously
/// and then awaits; <c>MerchantCardEntry.OnTryPurchase</c> dereferences <c>CreationResult.Card</c>
/// after its own awaits (MerchantCardEntry.cs:133/144/146) while another overlapping purchase's
/// <c>ClearAfterPurchase</c> sets <c>CreationResult</c> to null — a NullReferenceException
/// resumed on the main thread mid-await.
///
/// ── Why the fix lives here ────────────────────────────────────────────────────────────────
/// The natural fix is the wait that sibling command <c>BuyCardRemovalCommand</c> already
/// performs — it returns <c>ExecuteResult.Retry(200)</c> until the selection screen actually
/// appears (ShopCommands.cs:406-408). But ShopCommands.cs is VENDORED from the RunReplays mod
/// and must not be edited, so the same condition-based wait is applied from outside instead:
/// after dispatching a purchase, the loop refuses to decide until the shop's stocked-entry set
/// actually changes.
/// </summary>
internal static class ShopPurchaseGate
{
    /// <summary>
    /// Give-up window. A purchase the game refuses (not enough gold, no room in the deck) never
    /// changes the shelf, so waiting on it forever would wedge the bot in the shop for the rest
    /// of the run. Releasing instead lets the loop re-decide, and the ordinary stuck guard
    /// handles a decision that then genuinely repeats. Generous relative to a purchase's real
    /// cost (card-add plus gold-loss animations) so it is a backstop, not the usual exit.
    /// </summary>
    private const ulong TimeoutMs = 3000;

    /// <summary>How long to wait before re-testing a gate that is still closed. Short enough
    /// that shopping stays brisk — the 1 Hz heartbeat alone would cost a second per item.</summary>
    internal const double RecheckSeconds = 0.15;

    private static bool _waiting;
    private static ulong _startedTick;
    private static int _stockedAtDispatch;
    private static string _what = "";

    /// <summary>True while a dispatched purchase has not yet visibly resolved. Read by
    /// <see cref="BotController.OnInputRequired"/>, which declines to decide while it holds.</summary>
    internal static bool IsSettling
    {
        get
        {
            if (!_waiting)
                return false;

            // The shelf changed: the purchase landed (or the shop closed under us). Either way
            // the state the next decision reads is now the post-purchase state.
            int stocked = CountStocked();
            if (stocked != _stockedAtDispatch)
            {
                Release($"'{_what}' settled");
                return false;
            }

            if (Time.GetTicksMsec() - _startedTick >= TimeoutMs)
            {
                GD.PrintErr($"[SpireBot] ShopPurchaseGate: '{_what}' did not change the shop's " +
                            $"stock within {TimeoutMs}ms — releasing the loop. The purchase was " +
                            $"most likely refused (gold or space); the bot will re-decide.");
                Release(null);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Arms the gate if <paramref name="cmd"/> is a purchase that resolves asynchronously.
    /// Called from <see cref="ActionExecutor.Execute"/> for every dispatched command.
    ///
    /// <c>BuyCardRemovalCommand</c> is deliberately NOT gated: it already waits internally for
    /// the card-selection screen, and its purchase is complete by the time it reports Ok.
    /// </summary>
    internal static void Record(ReplayCommand cmd)
    {
        if (cmd is not (BuyCardCommand or BuyRelicCommand or BuyPotionCommand))
            return;

        _waiting = true;
        _startedTick = Time.GetTicksMsec();
        _stockedAtDispatch = CountStocked();
        _what = cmd.Describe();
    }

    /// <summary>Drops any pending wait. Called on run start/stop through
    /// <see cref="Reset"/> so a gate armed in one run can never hold the next one.</summary>
    internal static void Reset() => Release(null);

    private static void Release(string? settled)
    {
        if (_waiting && settled != null)
            GD.Print($"[SpireBot] ShopPurchaseGate: {settled}.");
        _waiting = false;
        _what = "";
    }

    /// <summary>
    /// How many items the shop currently has on its shelves. Counting rather than tracking the
    /// specific entry keeps this independent of which Buy command was dispatched — a purchase
    /// removes exactly one entry, and a restock (Hook.ShouldRefillMerchantEntry) replaces it,
    /// which also changes the count for at least one frame. Zero when no shop is open, which is
    /// itself a change from any in-shop count and so releases the gate.
    /// </summary>
    private static int CountStocked()
    {
        var room = ReplayState.ActiveMerchantRoom;
        if (room == null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
            return 0;

        var entries = OpenShopCommand.GetEntries(room);
        if (entries == null)
            return 0;

        return entries.Count(e => e.IsStocked);
    }
}
