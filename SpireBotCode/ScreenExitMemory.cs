using System.Collections.Generic;

using Godot;

using SpireBot.Replay;
using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>Which "outlives the visit" screen family a room-scoped exit flag applies to. New
/// families slot in here — see this file's class doc for the two shipped so far.</summary>
internal enum ScreenFamily
{
    RewardScreen,
    Shop,
}

/// <summary>
/// Generalizes the fix-map-after-decline-brief.md design (b) — "exit-state marking" — from a
/// single reward-screen-only flag (formerly <c>RewardScreenExitMemory</c>) into a per-room,
/// per-<see cref="ScreenFamily"/> flag set, because the SAME defect class has now shown up
/// twice: once for a screen already fixed by the change, and one that the shop fix pulls that
/// class out for. Both are victims of the SAME structural issue: <c>ReplayDispatcher
/// .GetDispatchableTypes()</c> keys a whole command family off a Godot node reference that
/// stays <c>IsInstanceValid</c> + <c>IsInsideTree()</c> long after the player has moved on —
/// because the game only ever hides/covers that node (opens the map on top of it), never frees
/// it, until the ROOM itself changes (<c>ReplayDispatcher.OnRoomExited</c>,
/// ReplayDispatcher.cs:958-962, only clears <c>ActiveMerchantRoom</c>; nothing clears
/// <c>ActiveRewardsScreen</c> proactively either — see below). So "the command type is still
/// enumerable" stops being a valid "the screen still owns the moment" signal the instant the
/// player has genuinely finished with that screen but not yet left the room.
///
/// <see cref="ScreenFamily.RewardScreen"/> — unchanged from the original
/// <c>RewardScreenExitMemory</c> (fix-map-after-decline-brief.md): <c>ReplayDispatcher
/// .GetAvailableCommands()</c> adds <c>ClaimRewardCommand</c>/<c>TakeCardCommand</c>/
/// <c>ProceedToMapCommand</c>/<c>SkipRewardsCommand</c> whenever <c>ReplayState
/// .ActiveRewardsScreen</c> is <c>IsInstanceValid</c> + <c>IsInsideTree()</c>
/// (ReplayDispatcher.cs:529-536). A terminal rewards screen's proceed button calls
/// <c>RunManager.ProceedFromTerminalRewardsScreen</c> (RunManager.cs:1329-1349), which — for
/// the ordinary "combat/treasure room, more rooms visited this run" case — only calls
/// <c>NMapScreen.Instance.Open()</c>; it never calls <c>NOverlayStack.Instance.Remove</c> or
/// frees the screen. <c>NRewardsScreen</c> is only ever freed via <c>AfterOverlayClosed</c>
/// &#8594;<c>QueueFreeSafely()</c> (NRewardsScreen.cs:369-373), which only the NON-terminal skip
/// path invokes (NRewardsScreen.cs:358-362) — never a terminal (post-combat/post-treasure)
/// screen. So after a decline + proceed, the map is genuinely open
/// (<c>travelEnabled=True</c>) while <c>ActiveRewardsScreen</c> stays valid and in-tree —
/// merely covered, not hidden/freed.
///
/// <see cref="ScreenFamily.Shop"/> — fix-shop-exit-brief.md, THIRD instance of this class (after
/// the round-13 finished-Event-vs-Map fix, DecisionContext.cs:285-294). A <c>MerchantRoom</c>
/// keeps its shop re-enterable for the whole room: <c>ReplayDispatcher.GetDispatchableTypes()</c>
/// adds every <c>Buy*</c>/<c>OpenShop</c>/<c>CloseShop</c>/<c>ProceedToMap</c> type whenever
/// <c>ReplayState.ActiveMerchantRoom</c> is valid+in-tree (ReplayDispatcher.cs:547-554), and
/// (unlike <c>ActiveRewardsScreen</c>, which the reward branch above ALSO never clears)
/// <c>ActiveMerchantRoom</c> is likewise only ever cleared by <c>OnRoomExited</c>
/// (ReplayDispatcher.cs:962) — i.e. only once the player has actually left the merchant ROOM,
/// not merely closed the shop UI.
///
/// <c>CloseShopCommand</c> is deliberately NOT a trigger: <c>NMerchantInventory.Close()</c>
/// (NMerchantInventory.cs:219-234, invoked by <see cref="CloseShopCommand"/> via reflection on
/// `Close`, CloseShopCommand.cs:14-16,34) only sets <c>IsOpen = false</c> and fires
/// <c>InventoryClosed</c> — which <c>NMerchantRoom.OpenInventory</c>'s handler
/// (NMerchantRoom.cs:191-196) uses to RE-ENABLE the room's own <c>_proceedButton</c> (the
/// "Leave" button) and <c>MerchantButton</c> (the "browse again" button). Closing the shop UI is
/// exactly the mid-visit state the brief's "genuine re-entry mid-visit must still work"
/// constraint describes — the player is still standing in the shop, free to reopen it
/// (<see cref="OpenShopCommand"/>) or leave (<see cref="ProceedToMapCommand"/>) next. Marking
/// exit here would wrongly suppress a shop that is still legitimately open for business.
///
/// <c>NMerchantRoom.HideScreen</c> (NMerchantRoom.cs:160-166) IS the real "leave" action — it's
/// the handler wired to the room's own <c>_proceedButton.Released</c> (NMerchantRoom.cs:79), and
/// it does exactly what <c>RunManager.ProceedFromTerminalRewardsScreen</c> does for rewards:
/// calls <c>NMapScreen.Instance.Open()</c> (line 164) and nothing else — no free, no
/// <c>ActiveMerchantRoom</c> clear. <see cref="ProceedToMapCommand"/>'s shop branch
/// (ProceedToMapCommand.cs:60-66) presses exactly this button (<c>ShopHideScreenMethod</c> =
/// <c>NMerchantRoom.HideScreen</c>), gated on that button being enabled — i.e. only dispatchable
/// once the shop is closed and the player is choosing to leave. This is <see cref="Record"/>'s
/// SOLE Shop trigger, mirroring the reward-screen design.
///
/// ── Review round 2 — three corrections against the first implementation ──────────────────────
///
/// (Important #1 — kind-gating) The first cut marked a family exited on ANY dispatch of its
/// trigger command(s), with no check on which DECISION (kind) actually chose to dispatch it —
/// reasoned to be harmless because a shared trigger command is only ever legal on the right
/// room's own proceed button. That reasoning broke for a command dispatched by
/// <see cref="BotController"/>'s NON-decision-driven paths: the stuck-guard's forced fallback
/// (<c>ctx.Available[0]</c>, BotController.cs's <c>DispatchWithFallback</c>) and
/// <c>TryAutoAdvance</c>'s catch-all both dispatch WHATEVER is first/available with no regard for
/// whether that command represents the CURRENT decision's own intent — so a command that merely
/// happens to still be legal from an entirely different kind of decision could retire a family it
/// was never actually asked about. <see cref="Record"/> now takes the originating
/// <see cref="DecisionKind"/> (the same <c>ctx.Kind</c> the dispatch came from) and only retires
/// <see cref="ScreenFamily.RewardScreen"/> from a <see cref="DecisionKind.RewardScreen"/>
/// decision, and <see cref="ScreenFamily.Shop"/> from a <see cref="DecisionKind.Shop"/> decision.
///
/// (Important #2 — MapMoveCommand dropped as a Shop trigger) The first cut also treated
/// <see cref="MapMoveCommand"/> as a second, "belt-and-suspenders" Shop-exit trigger. That was
/// wrong on its own terms, independent of kind-gating: <c>ActionMap.BuildShop</c>
/// (ActionMap.cs:360-391) never maps <c>MapMoveCommand</c> as one of the shop's own choices — it
/// only ever appears in a Shop-kind decision's <c>ctx.Available</c> because
/// <c>NMerchantRoom._Ready</c> enables map travel unconditionally the moment the room loads
/// (NMerchantRoom.cs:91, <c>NMapScreen.Instance.SetTravelEnabled(enabled: true)</c>) — i.e. for
/// the ENTIRE visit, before the shop is ever opened, not just after leaving. So a
/// <c>MapMoveCommand</c> seen alongside a Shop decision is NEVER itself evidence of a deliberate
/// "I am leaving the shop" choice — it is either the pre-existing map-travel affordance sitting
/// there unused, or (worse) the stuck-guard/fallback's incidental pick of
/// <c>ctx.Available[0]</c> mid-shop (the review's "premature exit" scenario: buying, not leaving,
/// yet a scripted fallback could still walk out via a move dispatched while nothing else was
/// mapped). <see cref="ProceedToMapCommand"/> from a genuine <see cref="DecisionKind.Shop"/>
/// decision — which only becomes legal once the shop is closed and the room's own proceed button
/// is enabled — is the sole, unambiguous, deliberate leave signal; kind-gating this trigger alone
/// (item #1) removes the incidental-fallback risk without needing a second trigger at all.
///
/// (Critical #3 — real merchant rooms only) A fake merchant (<c>NFakeMerchant</c>, spawned inside
/// an <c>EventRoom</c>, e.g. the "Reverse Merchant"/"Future of Potions" events) shares SOME
/// command types with a real shop (<c>BuyRelicCommand</c> falls back to
/// <c>ReplayState.FakeMerchantInstance</c> when no real <c>ActiveMerchantRoom</c> entries exist —
/// ShopCommands.cs:268-269) but is a structurally DIFFERENT node
/// (<c>ReplayState.FakeMerchantInstance</c> is an <c>NFakeMerchant</c>; the real shop's
/// <c>ReplayState.ActiveMerchantRoom</c> is an <c>NMerchantRoom</c> — never the same instance).
/// Once an event with a fake merchant is otherwise "finished" (no more selectable
/// <c>ChooseEventOptionCommand</c> options), <c>DecisionContext.Classify</c> falls through the
/// Event branch straight into the Shop branch purely because <c>OpenFakeShopCommand</c>/
/// <c>BuyRelicCommand</c> are still enumerable — so a fake-merchant-only decision can legitimately
/// be classified <c>Shop</c> too. <see cref="IsRealShopActive"/> is the discriminant that keeps
/// the exit flag scoped to a REAL shop only: it is exactly the same check
/// <c>ProceedToMapCommand.Execute()</c> itself uses to succeed for the shop branch
/// (ProceedToMapCommand.cs:60-66, <c>ReplayState.ActiveMerchantRoom</c> valid+in-tree) — and a
/// fake merchant's own proceed button is never reachable through <c>ProceedToMapCommand</c> AT
/// ALL: <c>NFakeMerchant.HideScreen</c> (NFakeMerchant.cs:195-198) is wired only to
/// <c>NFakeMerchant</c>'s OWN <c>_proceedButton.Released</c> (NFakeMerchant.cs:72-76), which
/// <c>ProceedToMapCommand.Execute</c> never references (it only tries
/// rewards/treasure/<c>ActiveMerchantRoom</c>/rest-site screens — ProceedToMapCommand.cs:42-77).
/// So <see cref="IsRealShopActive"/> can only ever say "not a real shop" for a pure fake-merchant
/// decision — it can never wrongly veto a real shop's exit. Both <see cref="Record"/> (so the
/// flag can never even be SET from a fake-merchant decision) and <c>DecisionContext.Classify</c>'s
/// Shop branch (so a stray flag from earlier in the same room, if one somehow existed, can never
/// shadow a fake merchant either) apply this guard; <c>BotController.TryAutoAdvance</c>'s
/// <c>OpenFakeShopCommand</c> auto-advance is UNGATED (reverted to its original, pre-fix,
/// unconditional behavior) for the same reason — only <c>OpenShopCommand</c> (real) is gated.
///
/// Marking is per-(room, family) like <see cref="RoomOneShots"/>/<see cref="RewardClaimMemory"/>
/// (a fresh room can legitimately reopen either screen) — cleared on room change (see
/// <see cref="OnDecision"/>) and on run start (<see cref="BotController.BeginDriving"/> via
/// <see cref="Reset"/>).
/// </summary>
internal static class ScreenExitMemory
{
    private static string? _roomKey;
    private static readonly HashSet<ScreenFamily> _exited = new();

    internal static void Reset()
    {
        _roomKey = null;
        _exited.Clear();
    }

    /// <summary>Call once per decision, from <see cref="DecisionContext.Capture"/> itself
    /// (ahead of <see cref="DecisionContext.Classify"/>, which needs <see cref="HasExited"/> to
    /// already reflect the current room) — unlike <see cref="RoomOneShots.OnDecision"/> and
    /// <see cref="RewardClaimMemory.OnDecision"/>, which only gate <c>ActionMap.Build</c> and so
    /// can run after <c>Capture</c> returns, this one has to run BEFORE <c>Classify</c>, while
    /// only the raw <see cref="GameStateSnapshot"/> exists yet — hence taking a snapshot
    /// directly instead of a <see cref="DecisionContext"/>.</summary>
    internal static void OnDecision(GameStateSnapshot snapshot)
    {
        string key = $"{snapshot.RoomType ?? "<none>"}@{snapshot.Floor}";
        if (key == _roomKey)
            return;

        _roomKey = key;
        _exited.Clear();
    }

    /// <summary>Records a dispatched command — call shape mirrors
    /// <see cref="RoomOneShots.Record"/>/<see cref="RewardClaimMemory.Record"/> from
    /// <see cref="ActionExecutor.Execute"/>, plus the originating <paramref name="kind"/> (the
    /// <c>DecisionContext.Kind</c> the dispatch came from) so a command that merely happens to
    /// still be legal from an unrelated decision can never retire a family it wasn't dispatched
    /// to resolve. See this class's doc — especially the "review round 2" section — for why each
    /// trigger/gate below is exactly right.</summary>
    internal static void Record(ReplayCommand cmd, DecisionKind kind)
    {
        // NRewardsScreen.cs:358-362's skip branch and :318-357's terminal-proceed branch — the
        // only two commands that ever actually leave a rewards screen — AND only when this
        // decision was itself classified RewardScreen (review round 2, Important #1).
        if (kind == DecisionKind.RewardScreen && cmd is SkipRewardsCommand or ProceedToMapCommand)
            MarkExited(ScreenFamily.RewardScreen, cmd);

        // NMerchantRoom.HideScreen via ProceedToMapCommand's shop branch is the SOLE Shop
        // trigger (MapMoveCommand dropped — review round 2, Important #2), only from a
        // Shop-kind decision (Important #1), and only for a REAL merchant room
        // (IsRealShopActive — Critical #3, keeps a fake-merchant decision from ever setting
        // this flag at all).
        if (kind == DecisionKind.Shop && cmd is ProceedToMapCommand && IsRealShopActive())
            MarkExited(ScreenFamily.Shop, cmd);

        // 2026-08-22 (shop-visibility flow): the bot now OPENS the inventory before buying, so the
        // policy's "Leave shop" maps to CloseShopCommand (enumerable only while the inventory is
        // open). Treat that close as leaving too: the sim's leave is terminal (driver.py appends
        // len(entries) as the final shop action and never re-enters), and BotController.TryShopFlow
        // would otherwise reopen the inventory on the very next Shop-kind decision and loop. The
        // earlier "CloseShop is deliberately NOT a trigger" rule above predates the flow that makes
        // CloseShop reachable at all for a bot-driven visit.
        if (kind == DecisionKind.Shop && cmd is CloseShopCommand && IsRealShopActive())
            MarkExited(ScreenFamily.Shop, cmd);
    }

    /// <summary>True while <see cref="ReplayState.ActiveMerchantRoom"/> — the REAL
    /// <c>NMerchantRoom</c>, never a fake merchant's <c>NFakeMerchant</c> (a structurally
    /// different type; see <see cref="ReplayState.FakeMerchantInstance"/>) — is the live,
    /// in-tree source of the shop commands. This is the exact discriminant
    /// <see cref="ProceedToMapCommand"/>'s own shop branch uses to succeed
    /// (ProceedToMapCommand.cs:60-66); see this class's doc, Critical #3, for why that makes it
    /// safe both as <see cref="Record"/>'s Shop gate and as <c>DecisionContext.Classify</c>'s
    /// Shop-branch gate.</summary>
    internal static bool IsRealShopActive()
        => ReplayState.ActiveMerchantRoom is { } room
           && GodotObject.IsInstanceValid(room)
           && room.IsInsideTree();

    private static void MarkExited(ScreenFamily family, ReplayCommand cmd)
    {
        if (_exited.Add(family))
            GD.Print($"[SpireBot] ScreenExitMemory: {family} screen exited via " +
                     $"'{cmd.Describe()}' — no longer classifying decisions as {family} for " +
                     $"the rest of room '{_roomKey}' (fix-map-after-decline-brief.md design b / " +
                     "fix-shop-exit-brief.md).");
    }

    /// <summary>True once <paramref name="family"/>'s screen has been left for the current room
    /// (see <see cref="Record"/>). <see cref="DecisionContext.Classify"/> uses this to stop
    /// treating a merely-still-in-tree screen/room reference as the active surface. For
    /// <see cref="ScreenFamily.RewardScreen"/> specifically, Classify still makes one exception —
    /// see DecisionContext.cs's reward branch — while a fresh
    /// <c>ReplayState.CardRewardSelectionScreen</c> is genuinely live. For
    /// <see cref="ScreenFamily.Shop"/>, Classify additionally only CONSULTS this flag at all when
    /// <see cref="IsRealShopActive"/> is true (Critical #3) — a fake-merchant-only decision is
    /// never shadowed by it, and (per <see cref="Record"/>'s own gate) could never have set it in
    /// the first place.</summary>
    internal static bool HasExited(ScreenFamily family) => _exited.Contains(family);
}
