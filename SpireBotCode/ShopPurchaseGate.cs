using Godot;

using SpireBot.Replay.Patches.Record;

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
/// <c>ReplayEngine.HasPendingCommand</c> — the only thing that stopped
/// <c>BotController.OnInputRequired</c> from deciding again — immediately goes false.
///
/// The game removes a purchased item from the shelf in <c>MerchantEntry.ClearAfterPurchase</c>,
/// which runs only AFTER the awaited <c>OnTryPurchase</c> completes (MerchantEntry.cs:81-89 in
/// the decompiled source). So in the window between those two moments the item is still stocked
/// and the gold still unspent, and the bot happily buys it again — and again. That is the "buys
/// everything too quickly" symptom.
///
/// It also crashes the game. <c>OnTryPurchaseWrapper</c> checks <c>IsStocked</c> synchronously
/// and then awaits; <c>MerchantCardEntry.OnTryPurchase</c> dereferences <c>CreationResult.Card</c>
/// after its own awaits (MerchantCardEntry.cs:133/144/146) while another overlapping purchase's
/// <c>ClearAfterPurchase</c> sets <c>CreationResult</c> to null — a NullReferenceException
/// resumed on the main thread mid-await.
///
/// ── Why the fix lives here, and what it keys off ──────────────────────────────────────────
/// The natural fix is the wait sibling command <c>BuyCardRemovalCommand</c> already performs —
/// it returns <c>ExecuteResult.Retry(200)</c> until the selection screen appears
/// (ShopCommands.cs:406-408). But ShopCommands.cs is VENDORED from the RunReplays mod and must
/// not be edited, so the wait is applied from outside instead.
///
/// The signal is <see cref="ShopPurchaseState.IsPurchasing"/>, which the vendored record patches
/// already maintain as a true purchase-lifecycle flag: set in the prefix of
/// <c>MerchantEntry.OnTryPurchaseWrapper</c> and cleared in the prefixes of
/// <c>InvokePurchaseCompleted</c> and <c>InvokePurchaseFailed</c>
/// (Replay/Patches/Record/ShopRecordPatch.cs:60-128). Nothing else read it on the dispatch side
/// until now.
///
/// Keying off the lifecycle rather than off the shop's stock is what makes this correct in the
/// cases stock-watching gets wrong: a fake-merchant purchase (which is not in
/// <c>ActiveMerchantRoom</c> at all), a Courier-style restock (which refills the slot, so the
/// shelf count never drops), and a Buy command whose target entry has already gone (which
/// invokes no purchase, so this never arms).
///
/// There is no gap between the command reporting Ok and this flag going true. Harmony's prefix
/// on <c>OnTryPurchaseWrapper</c> runs synchronously at method entry, inside
/// <c>InvokePurchase</c>'s reflective call, inside <c>cmd.Execute()</c> — all before
/// <c>ExecuteNext</c> consumes the command and lets <c>HasPendingCommand</c> go false.
/// </summary>
internal static class ShopPurchaseGate
{
    /// <summary>
    /// Give-up window. Every path through <c>OnTryPurchaseWrapper</c> should end in
    /// <c>InvokePurchaseCompleted</c> or <c>InvokePurchaseFailed</c>, so the flag should always
    /// clear — but a purchase that somehow returned false without invoking either would leave it
    /// set, and waiting on it forever would wedge the bot in the shop for the rest of the run.
    /// Generous relative to a purchase's real cost (card-add plus gold-loss animations) so it is
    /// a backstop, not the usual exit.
    /// </summary>
    private const ulong TimeoutMs = 3000;

    /// <summary>How long to wait before re-testing a gate that is still closed. Short enough
    /// that shopping stays brisk — the 1 Hz heartbeat alone would cost a second per item.</summary>
    internal const double RecheckSeconds = 0.15;

    private static bool _holding;
    private static ulong _startedTick;
    private static bool _gaveUp;

    /// <summary>
    /// True while a purchase is in flight. Read by <see cref="BotController.OnInputRequired"/>,
    /// which declines to decide while it holds.
    ///
    /// Not a pure property: it latches the start of a purchase and the give-up decision. It is
    /// read exactly once per <see cref="BotController.OnInputRequired"/> cycle.
    /// </summary>
    internal static bool IsSettling
    {
        get
        {
            if (!ShopPurchaseState.IsPurchasing)
            {
                // Purchase finished (or none was running). Rearm for the next one.
                _holding = false;
                _gaveUp = false;
                return false;
            }

            if (!_holding)
            {
                _holding = true;
                _gaveUp = false;
                _startedTick = Time.GetTicksMsec();
            }

            // Already gave up on THIS purchase — stay released until the flag clears, so a stuck
            // flag costs one timeout rather than blocking every decision from here on.
            if (_gaveUp)
                return false;

            if (Time.GetTicksMsec() - _startedTick >= TimeoutMs)
            {
                _gaveUp = true;
                GD.PrintErr($"[SpireBot] ShopPurchaseGate: a shop purchase has been in flight for " +
                            $"{TimeoutMs}ms without completing or failing — releasing the loop so " +
                            $"the run can continue. The bot will re-decide from the current screen.");
                return false;
            }

            return true;
        }
    }

    /// <summary>Drops any pending wait. Called from <see cref="BotController"/> on run start and
    /// on stop, so a purchase in flight when one run ends can never hold the next one.</summary>
    internal static void Reset()
    {
        _holding = false;
        _gaveUp = false;
    }
}
