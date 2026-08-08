using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Godot;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

using SpireBot.Replay;
using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Canonical, order-preserving lists shared between <see cref="ActionMap"/> (which binds
/// them to action ids — the choice/select blocks) and <see cref="Obs.RunObsWriter"/> (which
/// writes the SAME screens as observation rows — map slots, event options, shop entries,
/// reward candidates, select candidates). Task 13's brief: "screen candidate rows ... must
/// come from the SAME arrays in the SAME order ActionMap used for the choice/select blocks
/// — single capture; read how ActionMap builds its canonical per-kind lists and share the
/// code rather than duplicating logic." Every list-building helper ActionMap needs for that
/// binding now lives here ONCE, so an obs row and the action id that answers it can never
/// silently disagree about which candidate is at index i.
///
/// Where ActionMap's own ordering is itself an open question (map/shop/select — see each
/// method's doc comment, ported from ActionMap.cs's own citations), this deliberately
/// reproduces the SAME order rather than "fixing" it independently: obs and mask must stay
/// aligned even where BOTH are wrong versus the Python sim (Task 16's parity harness is
/// where that gets resolved, for obs and mask together — not here).
/// </summary>
public static class DecisionLists
{
    // ── Map ── (mirrors ActionMap.BuildMap's own citation: driver.py's
    // `sorted(points, key=lambda p: (p.col, p.row))`, reduced to a Col-only sort since every
    // MapMoveCommand from the current node shares one target row.)
    public static List<MapMoveCommand> MapMoves(DecisionContext ctx)
        => ctx.Available.OfType<MapMoveCommand>().OrderBy(m => m.Col).ToList();

    // ── Event ── (mirrors ActionMap.BuildEvent: index order, excluding the -1 PROCEED
    // sentinel which has no Python-side decision analog.)
    public static List<ChooseEventOptionCommand> EventOptions(DecisionContext ctx)
        => ctx.Available.OfType<ChooseEventOptionCommand>()
            .Where(e => e.RecordedIndex >= 0)
            .OrderBy(e => e.RecordedIndex)
            .ToList();

    // ── Shop ── (mirrors ActionMap.BuildShop's OPEN QUESTION: OpenShopCommand.GetEntries /
    // BuyRelicCommand.GetFakeMerchantEntries reflection-scan order, NOT verified to match
    // shop.py's canonical [character cards, colorless cards, relics, potions, removal]
    // order.)
    public static List<MerchantEntry>? ShopEntries(DecisionContext ctx)
    {
        List<MerchantEntry>? entries = null;
        var room = ReplayState.ActiveMerchantRoom;
        if (room != null)
            entries = OpenShopCommand.GetEntries(room);
        if ((entries == null || entries.Count == 0) && ReplayState.FakeMerchantInstance != null)
            entries = BuyRelicCommand.GetFakeMerchantEntries(ReplayState.FakeMerchantInstance!);
        return entries;
    }

    // ── Reward: card sub-screen ── (mirrors ActionMap.BuildRewardScreen's takeCards block.)
    public static List<TakeCardCommand> RewardTakeCards(DecisionContext ctx)
        => ctx.Available.OfType<TakeCardCommand>()
            .Where(t => !t.IsSacrifice && !t.IsSkip)
            .OrderBy(t => t.CardIndex)
            .ToList();

    private static readonly FieldInfo? CardRowField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_cardRow", BindingFlags.NonPublic | BindingFlags.Instance);

    private static PropertyInfo? CardModelHolderProp(Node node)
        => node.GetType().GetProperty("CardModel", BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// Resolves each <see cref="RewardTakeCards"/> entry's underlying <see cref="CardModel"/>,
    /// ONE LIST, positionally aligned with that list (same length, same order) — index i here
    /// is the CardModel for <c>RewardTakeCards(ctx)[i]</c>, the exact command ActionMap binds
    /// to choice slot i. Resolved via the SAME X-position holder sort
    /// <see cref="TakeCardCommand.Execute"/>'s private <c>ExecuteSelectCard</c> uses (so
    /// <c>TakeCardCommand.CardIndex</c> always addresses the row this method reports for it).
    /// A null entry means the model could not be resolved (screen not ready / reflection
    /// miss) — the caller must treat that as an explicit PAD row, never fabricate a value.
    /// </summary>
    public static List<CardModel?> RewardCardModels(DecisionContext ctx)
    {
        var takeCards = RewardTakeCards(ctx);
        var result = new List<CardModel?>(takeCards.Count);
        if (takeCards.Count == 0)
            return result;

        var screen = ReplayState.CardRewardSelectionScreen;
        if (CardRowField?.GetValue(screen) is not Node cardRow)
        {
            for (int i = 0; i < takeCards.Count; i++) result.Add(null);
            return result;
        }

        var holders = new List<Control>();
        foreach (Node child in cardRow.GetChildren())
        {
            if (child is not Control ctrl) continue;
            if (CardModelHolderProp(child)?.GetValue(child) is CardModel)
                holders.Add(ctrl);
        }
        holders.Sort((a, b) => a.Position.X.CompareTo(b.Position.X));

        foreach (var t in takeCards)
        {
            if (t.CardIndex < 0 || t.CardIndex >= holders.Count)
            {
                result.Add(null);
                continue;
            }
            var holder = holders[t.CardIndex];
            result.Add(CardModelHolderProp(holder)?.GetValue(holder) as CardModel);
        }
        return result;
    }

    // ── Select candidates ── (FIX 6, round 4 — CLOSES the prior OPEN QUESTION: now sorts by
    // content-derived key exactly like Python's `_select_order` (sts2_rl/run_env.py:1001-1008,
    // `(tuple(rows[i][0]), tuple(rows[i][1]))` over each candidate's own obs row), using the
    // SAME `Obs.RunObsWriter.CardInstanceRow`/`CompareRow` helpers run.deck/reward.cards
    // already use for their own sort=True ordering — not a re-derivation, the literal same
    // comparator. Built ONCE here and consumed by BOTH `Obs.RunObsWriter.WriteSelect` and
    // `ActionMap.BuildSelectCards`, so SELECT_BASE+i and select.candidates row i always name
    // the same candidate — see the task brief's "single capture, not two independent sorts"
    // requirement. A candidate whose `Card` is null (unresolved) sorts by an all-zero row —
    // same "unresolved = PAD" convention `Obs.RunObsWriter.WriteSelect` already applies.)
    public readonly record struct SelectCandidate(ReplayCommand Command, CardModel? Card);

    private static readonly (int[] ints, float[] floats) ZeroRow = (new[] { 0, 0, 0, 0 }, new[] { 0f, 0f, 0f, 0f });

    public static (List<SelectCandidate> candidates, ReplayCommand? skip) SelectCandidates(
        DecisionContext ctx, Contract contract, Obs.SessionState session)
    {
        var (candidates, skip) = CollectCandidates(ctx);

        candidates.Sort((a, b) =>
        {
            var rowA = a.Card != null ? Obs.RunObsWriter.CardInstanceRow(contract, session, a.Card, pileId: 0) : ZeroRow;
            var rowB = b.Card != null ? Obs.RunObsWriter.CardInstanceRow(contract, session, b.Card, pileId: 0) : ZeroRow;
            return Obs.RunObsWriter.CompareRow(rowA, rowB);
        });

        return (candidates, skip);
    }

    // ── Select purpose (Fix 1, obs-parity grind) ────────────────────────────────────────
    // select.count itself is sourced in RunObsWriter.WriteSelect via
    // CardGridScreenCapture.GetPrefs (that method's own doc comment has the full citation).
    //
    // select.purpose.ids: two out-of-combat grid-screen types are unambiguous by construction
    // — CardSelectCmd.cs's FromDeckForUpgrade is the ONLY caller that opens
    // NDeckUpgradeSelectScreen (purpose "upgrade"), and FromDeckForRemoval -> FromDeckGeneric
    // is the ONLY caller that opens NDeckCardSelectScreen (purpose "remove"; CardSelectCmd.cs
    // has no other FromDeckGeneric call site) — so the concrete screen Type alone determines
    // the purpose string for these two, no per-mechanism table needed. The remaining screen
    // types stay unresolved (documented per-branch below): NDeckTransformSelectScreen could be
    // "transform" but also feeds a couple of transform-adjacent card mechanics sts2-rl hasn't
    // been cross-checked against; NDeckEnchantSelectScreen splits into "enchant" vs
    // "enchant_optional" by a MinSelect==0 screen-prefs check that hasn't been verified either;
    // NSimpleCardSelectScreen and NCombatPileCardSelectScreen are both genuinely
    // multi-purpose (obtain/duplicate/gambling_chip/curse_of_knowledge/from_discard/from_draw/
    // to_draw_top/exhaust/card_reward/bundle) with no single-purpose screen type to key off —
    // resolving those needs the per-mechanism classification table CardSelectCmd.cs's own
    // comment on this file's WriteSelect already documents as out of scope here.
    public static string? SelectPurpose(DecisionContext ctx)
    {
        var gridScreen = CardGridScreenCapture.ActiveScreen;
        return gridScreen switch
        {
            MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen => "upgrade",
            MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen => "remove",
            // Round-13 additions — both are single-call-site screen types, same
            // rationale as upgrade/remove: NDeckTransformSelectScreen.ShowScreen is
            // called ONLY from CardSelectCmd.cs:510 (the transform path;
            // sts2-rl purpose "transform" — Claws' skippable variant maps sim-side
            // to "_unknown" today, so type→"transform" matches sim for every
            // recorded screen), and NDeckEnchantSelectScreen.ShowScreen ONLY from
            // CardSelectCmd.cs:594 (purpose "enchant"; Kifuda's MinSelect-0
            // "enchant_optional" twin uses the same type — distinguish via prefs
            // if it ever shows up in a dump; not exercised by either seed).
            // Round-14 amendment: sts2-rl labels the SKIPPABLE (MinSelect-0) transform
            // grid — the ancient shrines' "transform up to N" and Claws — as the shared
            // "_unknown" purpose (run_env.py's own note: "the pre-existing
            // 'transform_optional' currently does" fall into _unknown; the frozen
            // registry has no transform_optional id). The obs target is the sim, so C#
            // must write the SAME id: only a mandatory (MinSelect > 0) transform screen
            // is "transform". 933T (2,1): game wrote 13/transform where sim wrote
            // 1/_unknown.
            MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen ts =>
                CardGridScreenCapture.GetPrefs(ts) is { MinSelect: 0 } ? "_unknown" : "transform",
            MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen => "enchant",
            _ => null,
        };
    }

    private static (List<SelectCandidate> candidates, ReplayCommand? skip) CollectCandidates(DecisionContext ctx)
    {
        var candidates = new List<SelectCandidate>();
        ReplayCommand? skip = null;

        var combatState = CombatManager.Instance?.DebugOnlyGetState();
        var handCards = combatState?.Players.FirstOrDefault()?.PlayerCombatState?.Hand?.Cards;
        var gridScreen = CardGridScreenCapture.ActiveScreen;
        var gridCards = gridScreen != null ? CardGridScreenCapture.GetSelectableCards(gridScreen) : null;
        var chooseScreen = ChooseACardScreenCapture.ActiveScreen;

        foreach (var cmd in ctx.Available)
        {
            switch (cmd)
            {
                case SelectHandCardsCommand shc:
                {
                    int i = shc.HandIndices.Length > 0 ? shc.HandIndices[0] : -1;
                    CardModel? card = handCards != null && i >= 0 && i < handCards.Count ? handCards[i] : null;
                    candidates.Add(new SelectCandidate(cmd, card));
                    break;
                }
                case SelectGridCardCommand sgc when sgc.Indices.Length == 1:
                {
                    int i = sgc.Indices[0];
                    CardModel? card = gridCards != null && i >= 0 && i < gridCards.Count ? gridCards[i] : null;
                    candidates.Add(new SelectCandidate(cmd, card));
                    break;
                }
                case SelectCardFromScreenCommand scfs when scfs.Index >= 0:
                {
                    CardModel? card = null;
                    if (chooseScreen != null)
                    {
                        var holder = CardGridScreenCapture.FindCardHolderByIndex(chooseScreen, scfs.Index);
                        if (holder != null)
                            card = CardModelHolderProp(holder)?.GetValue(holder) as CardModel;
                    }
                    candidates.Add(new SelectCandidate(cmd, card));
                    break;
                }
                case SelectCardFromScreenCommand scfs when scfs.Index < 0:
                    skip = cmd; // the skip sentinel (Index == -1)
                    break;
            }
        }

        return (candidates, skip);
    }
}
