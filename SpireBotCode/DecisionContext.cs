using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;

using SpireBot.Replay;
using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>
/// The kind of decision currently pending, derived from which
/// <see cref="ReplayCommand"/> types <c>ReplayDispatcher.GetAvailableCommands()</c>
/// produced. There is no existing screen-kind enum on the C# side to reuse
/// (see Task 10's research report) — this mirrors sts2-rl's
/// <c>sts2_rl.driver.DecisionKind</c> (driver.py:62-75) as closely as the
/// vendor's command shapes allow. <see cref="Unsupported"/> is the catch-all
/// for anything ActionMap does not (yet) recognize; BotController is expected
/// to fall back gracefully (e.g. auto-advance) rather than route it through a
/// trained policy.
/// </summary>
public enum DecisionKind
{
    Combat,
    Map,
    Event,
    Shop,
    Rest,
    RewardScreen,
    SelectCards,
    SelectOption,
    Unsupported,
}

/// <summary>Round-8 obs-parity fix: which of sts2-rl's three split reward decision kinds
/// (REWARD_CARD/REWARD_POTION/REWARD_RELIC) a <see cref="DecisionKind.RewardScreen"/>
/// decision actually is. Mirrors <c>RunObsWriter</c>'s private <c>RewardSubKind</c> enum
/// (kept private there; this public copy is the one <see cref="PassiveDump"/> and
/// <c>RunObsWriter</c> both see) — see <see cref="DecisionContext.RewardKindOverride"/>'s
/// doc for why a second copy exists instead of resolving on the fly at capture time.</summary>
public enum RewardKind { None, Card, Potion, Relic }

/// <summary>One enemy's position in the live encounter roster, captured at the
/// same instant as the rest of a <see cref="DecisionContext"/>.
///
/// <see cref="ModelGameId"/> (Task 12 carry-forward item 1): the vendored
/// <see cref="GameStateSnapshot"/> only serializes each enemy's display
/// <c>Name</c>, never its <c>MonsterModel.Id</c> (a <c>ModelId</c>, e.g.
/// "MONSTER.CULTIST") — the ONLY thing <c>Contract.VocabId("monsters", ...)</c>
/// can look up. Rather than widen <see cref="GameStateSnapshot"/> (used by
/// several unrelated call sites and deliberately narrow), this reads
/// <c>e.Monster?.Id.ToString()</c> directly off the live <c>Creature</c>
/// here, alongside the rest of the raw encounter-order capture this record
/// already exists for. Null for a non-monster creature (shouldn't occur for
/// an enemy-side slot) or a slot with no <c>Monster</c> resolved yet.</summary>
public readonly record struct EncounterEnemySlot(
    int Index, uint? CombatId, bool IsAlive, string Name, string? ModelGameId);

/// <summary>
/// One decision point, captured once. Bundles the classified <see cref="Kind"/>,
/// the concrete legal commands (<see cref="Available"/>, straight from
/// <c>ReplayDispatcher.GetAvailableCommands()</c>), a <see cref="GameStateSnapshot"/>
/// taken at the same instant, and (for combat) the raw encounter-order enemy
/// roster that <see cref="GameStateSnapshot"/> alone cannot supply (see
/// <see cref="EncounterEnemies"/> doc below) — all read together so ActionMap
/// never has to re-read live game state and risk a TOCTOU mismatch between two
/// separate captures.
/// </summary>
public sealed class DecisionContext
{
    public DecisionKind Kind { get; }
    public List<ReplayCommand> Available { get; }
    public GameStateSnapshot Snapshot { get; }

    /// <summary>Round-8 obs-parity fix, PassiveDump-only (mirrors <paramref name="kindOverride"/>'s
    /// shape below): when <see cref="Kind"/> is <see cref="DecisionKind.RewardScreen"/>,
    /// <see cref="RunObsWriter"/>'s <c>ClassifyReward</c> previously picked the sub-kind by
    /// SCREEN PRIORITY (first RelicReward button on the whole mixed-type screen, else first
    /// PotionReward button) rather than by which button the dispatched <c>ClaimRewardCommand</c>
    /// actually resolved to. On a multi-item reward screen (e.g. Gold/Potion/Relic all offered
    /// together) that silently relabels a genuine potion claim as a relic claim whenever a
    /// relic button also happens to be present anywhere on the same screen — 12 lines across 6
    /// floors (89U act0 f8/f11/f15, act1 f13, act2 f7/f13) dumped a spurious extra
    /// <c>reward_relic</c> line and were missing their real <c>reward_potion</c> line as a
    /// result. <see cref="PassiveDump"/> resolves the CLAIMED button's own reward type at
    /// <c>CheckPreState</c> time (same reflection lookup <c>ResolveRewardButton</c> already
    /// does) and passes it here; <see cref="RunObsWriter"/> uses it to pick among the SAME
    /// candidate buttons it already enumerates, rather than re-deriving classification from
    /// scratch. Null for every other decision kind and for the live bot (<see cref="BotController"/>
    /// never passes an override, same as <paramref name="kindOverride"/>).</summary>
    public RewardKind? RewardKindOverride { get; }

    /// <summary>Companion to <see cref="RewardKindOverride"/>: the CLAIMED button's own
    /// index on the rewards screen (the dispatched <c>ClaimRewardCommand.RewardIndex</c>,
    /// read by <see cref="PassiveDump"/> at <c>CheckPreState</c> time). <see cref="RewardKindOverride"/>
    /// alone only fixed WHICH sub-kind (relic/potion/card) a decision reports — it does not
    /// tell <c>RunObsWriter.ClassifyReward</c> WHICH button, so a multi-item-of-the-SAME-type
    /// screen (two simultaneous relic buttons, or a claimed button that lingers un-removed
    /// from the tree after being claimed) still falls back to "first RelicReward/PotionReward
    /// found by scanning the whole screen" and can silently report an adjacent or already-
    /// claimed button's content instead of the one actually being claimed at this decision
    /// (confirmed against 89U's REWARD_RELIC ground truth: dumped ids 143/96/59/50/123 vs.
    /// actually-offered 121/(skip)/111/157/(skip) — id 50 in particular is the SAME stale
    /// relic id the round-7 PassiveDump dedup investigation already caught being re-read
    /// twice off one screen). When present and in range, <c>ClassifyReward</c> uses
    /// <c>buttons[RewardButtonIndexOverride]</c> directly instead of the first-found
    /// heuristic. Null for every other decision kind and for the live bot.</summary>
    public int? RewardButtonIndexOverride { get; }

    /// <summary>
    /// The full combat encounter roster (<c>CombatState.Enemies</c>), in
    /// creation/encounter order, INCLUDING dead slots at their original
    /// positions — captured directly rather than reused from
    /// <see cref="GameStateSnapshot.Enemies"/>, whose list is pre-filtered to
    /// living enemies only (GameStateSnapshot.cs:100-101:
    /// <c>combatState?.Enemies.Where(e =&gt; e.IsAlive)</c>), which loses each
    /// enemy's original index. Python's combat action-id math needs that raw
    /// index: <c>full_env.py</c>'s <c>combat_action_masks</c> (full_env.py:545)
    /// builds <c>living = [i for i, e in enumerate(s.enemies) if not e.is_gone
    /// and i &lt; MAX_ENEMIES]</c> — a list of the ORIGINAL indices of living
    /// enemies, not a compacted 0..k list — and both card and potion targets
    /// are mask-bit positions keyed on that raw index (full_env.py:554-568).
    /// Empty outside combat.
    /// </summary>
    public IReadOnlyList<EncounterEnemySlot> EncounterEnemies { get; }

    private DecisionContext(
        DecisionKind kind,
        List<ReplayCommand> available,
        GameStateSnapshot snapshot,
        IReadOnlyList<EncounterEnemySlot> encounterEnemies,
        RewardKind? rewardKindOverride,
        int? rewardButtonIndexOverride)
    {
        Kind = kind;
        Available = available;
        Snapshot = snapshot;
        EncounterEnemies = encounterEnemies;
        RewardKindOverride = rewardKindOverride;
        RewardButtonIndexOverride = rewardButtonIndexOverride;
    }

    /// <summary>Classifies the current decision and captures its state, all in one shot.
    /// <paramref name="session"/> supplies the stable per-CombatId encounter-slot map
    /// (round-3 fix 2) — same <see cref="Obs.SessionState"/> instance the caller's combat
    /// hooks (<c>OnCombatStarted</c>) already feed.
    ///
    /// <paramref name="kindOverride"/> (round-5 obs-parity fix, PassiveDump-only): when
    /// supplied, this exact <see cref="DecisionKind"/> is used instead of the screen-based
    /// <see cref="Classify"/> below. <see cref="PassiveDump"/> passes the kind it derived
    /// from the fork's own dispatched command TYPE (known precisely at
    /// <c>CheckPreState</c> time) rather than from whichever screens happen to still be
    /// enumerable — <see cref="Classify"/>'s screen-precedence heuristic is provably wrong
    /// for the passive dump: a mislabeled decision can fire while STILL inside the
    /// producing room (e.g. the Map decision right after a rest-site Smith upgrade, before
    /// any <c>RoomExited</c> signal), so no amount of "clear stale screen refs on room
    /// exit" fixing (rounds 2-4) can close that gap — the room hasn't exited yet. The live
    /// bot (<see cref="BotController"/>) still calls this with no override and gets the
    /// original screen-based <see cref="Classify"/> behavior, unchanged.</summary>
    public static DecisionContext Capture(
        Obs.SessionState session, DecisionKind? kindOverride = null, RewardKind? rewardKindOverride = null,
        int? rewardButtonIndexOverride = null)
    {
        var dispatchableTypes = ReplayDispatcher.GetDispatchableTypes();
        var available = ReplayDispatcher.GetAvailableCommands();
        var snapshot = new GameStateSnapshot(dispatchableTypes);
        // Must run BEFORE Classify below (see ScreenExitMemory.OnDecision's doc) — the
        // room-identity key it rolls over on needs the snapshot that just got captured.
        ScreenExitMemory.OnDecision(snapshot);
        var encounterEnemies = CaptureEncounterEnemies(session);
        var kind = kindOverride ?? Classify(available);
        return new DecisionContext(kind, available, snapshot, encounterEnemies, rewardKindOverride, rewardButtonIndexOverride);
    }

    /// <summary>
    /// Builds the raw encounter roster from <paramref name="session"/>'s STABLE
    /// CombatId-&gt;slot map (see <see cref="Obs.SessionState.GetOrAssignEnemySlot"/>), not
    /// by re-enumerating <c>CombatState.Enemies</c> positionally — that list SHRINKS when
    /// a creature is removed (game src <c>Core/Combat/CombatState.cs:289,513</c>,
    /// <c>_enemies.Remove(creature)</c>), which would silently reassign every later
    /// enemy's slot the instant an earlier one dies. Any newly-observed CombatId (a
    /// mid-combat summon) is assigned the next free slot on the spot. A slot whose
    /// creature is no longer present in the live list reports a PAD entry (IsAlive=false,
    /// last-known name/model id) at its ORIGINAL slot instead of disappearing.
    /// </summary>
    private static IReadOnlyList<EncounterEnemySlot> CaptureEncounterEnemies(Obs.SessionState session)
    {
        var combatState = CombatManager.Instance?.DebugOnlyGetState();
        if (combatState == null)
            return Array.Empty<EncounterEnemySlot>();

        // Live creatures indexed by CombatId — used to resolve alive slots and to pick up
        // any CombatId not yet tracked (a summon appearing after combat start).
        var live = new Dictionary<uint, MegaCrit.Sts2.Core.Entities.Creatures.Creature>();
        foreach (var e in combatState.Enemies)
        {
            if (e.CombatId is not uint id) continue;
            live[id] = e;
            session.GetOrAssignEnemySlot(id);
            session.UpdateEnemySlotName(id, e.SlotName);
            session.RecordEnemySeen(id, e.Name, e.Monster?.Id.ToString());
            session.SeedDisplayedIntentIfNew(id, e);
        }
        // Round-8 fix (Task 2): re-derive slot order every capture, mirroring the sim's
        // `combat.enemies` list — which is append order UNLESS the encounter declares a
        // named `Slots` row, in which case it's kept sorted by that row
        // (CombatState.SortEnemiesBySlotName / sort_enemies_by_slot_name). See
        // SessionState.RecomputeEnemyOrder's doc for why an unconditional call here is
        // safe (idempotent) and correct (mirrors the game's own live sorted list, which
        // this reproduces without the game's shrink-on-death behavior).
        session.RecomputeEnemyOrder(combatState.Encounter?.Slots);

        var order = session.EnemySlotOrder;
        var result = new List<EncounterEnemySlot>(order.Count);
        for (int slot = 0; slot < order.Count; slot++)
        {
            uint id = order[slot];
            if (live.TryGetValue(id, out var creature))
            {
                result.Add(new EncounterEnemySlot(slot, id, creature.IsAlive, creature.Name, creature.Monster?.Id.ToString()));
            }
            else
            {
                // Removed from CombatState.Enemies (died/left) — PAD at the original slot
                // using the last-known identity, matching the sim's own dead-enemy PAD rule.
                session.TryGetEnemyLastKnown(id, out var known);
                result.Add(new EncounterEnemySlot(slot, id, false, known.Name ?? string.Empty, known.ModelGameId));
            }
        }
        return result;
    }

    private static bool Has<T>(List<ReplayCommand> available) where T : ReplayCommand
        => available.Any(c => c is T);

    /// <summary>
    /// Classification priority mirrors which screen family produced the
    /// commands. sts2-rl's driver.py raises exactly one DecisionRequest.kind
    /// at a time (driver.py:62-75 DecisionKind, set by whichever `_ask(...)`
    /// call is pending), but ReplayDispatcher.GetAvailableCommands() has no
    /// single discriminant — several command families can legitimately appear
    /// together (ReplayDispatcher.cs:510-518 documents events coexisting with
    /// fake-shop/potion/leftover-map commands). We pick the most
    /// decision-specific family present, combat and reward/card-selection
    /// screens first since those "own" the moment even when other commands
    /// (e.g. a stale MapMoveCommand) are technically still enumerable.
    /// </summary>
    private static DecisionKind Classify(List<ReplayCommand> available)
    {
        if (Has<PlayCardCommand>(available) || Has<EndTurnCommand>(available))
            return DecisionKind.Combat;

        // Card-reward selection sub-screen, top-level rewards screen, or a
        // treasure chest (see Task 10 report OPEN QUESTION: sts2-rl's
        // driver.py has no DecisionKind.TREASURE — folded in here so the bot
        // is never stuck with an all-false mask on a chest).
        //
        // fix-map-after-decline-brief.md (design b): these command TYPES stay enumerable for as
        // long as ActiveRewardsScreen is IsInstanceValid + IsInsideTree() — which stays true even
        // after the map has genuinely opened over it (ScreenExitMemory's doc cites the
        // game-source proof: RunManager.ProceedFromTerminalRewardsScreen only opens the map, it
        // never frees or removes the screen). So "the type is enumerable" alone no longer decides
        // this branch once ScreenExitMemory.HasExited(RewardScreen) is true for this room —
        // UNLESS a fresh card-picker sub-screen is genuinely live (the fallback auto-advance path
        // can still open one), which the vendored dispatcher gates the exact same way
        // (ReplayDispatcher.cs:529-536) so checking it here is the correct liveness signal for
        // THAT specific screen.
        if (Has<TakeCardCommand>(available) || Has<ClaimRewardCommand>(available)
            || Has<SkipRewardsCommand>(available) || Has<OpenChestCommand>(available)
            || Has<TakeChestRelicCommand>(available))
        {
            bool freshCardPickerLive = ReplayState.CardRewardSelectionScreen is { } picker
                && GodotObject.IsInstanceValid(picker)
                && picker.IsInsideTree();

            if (!ScreenExitMemory.HasExited(ScreenFamily.RewardScreen) || freshCardPickerLive)
                return DecisionKind.RewardScreen;
        }

        if (Has<SelectHandCardsCommand>(available) || Has<SelectGridCardCommand>(available)
            || Has<SelectCardFromScreenCommand>(available))
            return DecisionKind.SelectCards;

        // Only an event with real, selectable options owns the moment. Once
        // Events[0].IsFinished the dispatcher emits nothing but the PROCEED sentinel
        // (ReplayDispatcher.cs:199-210), which no action id models — so classifying that as
        // Event yields an all-false mask while a perfectly answerable map screen sits right
        // there (the vendor notes the two coexist: "the map screen's MapMoveCommand lingers
        // from the room entry", ReplayDispatcher.cs:510-518). Observed live: kind=Event,
        // available=[MapMoveCommand, ChooseEventOptionCommand], travelEnabled=True — the bot
        // spun on PROCEED until the stuck guard forced a scripted map move seconds later.
        if (available.OfType<ChooseEventOptionCommand>().Any(e => e.RecordedIndex >= 0))
            return DecisionKind.Event;

        // TODO(round4): shop sub-index undercount (root cause 4 from the round-3 diagnosis) is
        // still open — blocked on re-measuring against a post-fix-1 dump, since fix 1
        // (ActiveMerchantRoom leak) was itself corrupting Shop classification/counts.
        //
        // fix-shop-exit-brief.md (THIRD instance of the round-13 defect class): these command
        // TYPES stay enumerable for the whole MerchantRoom visit — ReplayState.ActiveMerchantRoom
        // stays IsInstanceValid + IsInsideTree() even after the shop is closed and the player has
        // proceeded to the (now genuinely open, travelEnabled=True) map, because
        // NMerchantRoom.HideScreen (the "leave shop" proceed handler, NMerchantRoom.cs:160-166)
        // only opens the map — it never frees NMerchantRoom or clears ActiveMerchantRoom (only
        // ReplayDispatcher.OnRoomExited does that, once the room truly changes). So "the type is
        // enumerable" alone no longer decides this branch once
        // ScreenExitMemory.HasExited(Shop) is true for this room — unlike RewardScreen, Shop has
        // no live-subscreen exception: the exit trigger (a Shop-kind ProceedToMap — see
        // ScreenExitMemory's doc) is unambiguous, so once marked there is nothing further to
        // re-check here.
        //
        // Review round 2, Critical #3: the exit flag is only ever CONSULTED when
        // ScreenExitMemory.IsRealShopActive() is true (a real NMerchantRoom, not a fake
        // merchant's NFakeMerchant) — same discriminant ProceedToMapCommand.Execute() itself
        // needs to succeed for the shop branch (ProceedToMapCommand.cs:60-66), and per
        // ScreenExitMemory.Record's matching guard the flag could never have been set from a
        // fake-merchant decision anyway. This keeps a fake merchant's Shop classification
        // (reached here whenever its owning event has no more selectable options — see the Event
        // branch above) from EVER being shadowed by an exit mark, defense-in-depth against the
        // two families somehow sharing a room key.
        if ((Has<BuyCardCommand>(available) || Has<BuyRelicCommand>(available)
            || Has<BuyPotionCommand>(available) || Has<BuyCardRemovalCommand>(available)
            || Has<OpenShopCommand>(available) || Has<OpenFakeShopCommand>(available)
            || Has<CloseShopCommand>(available))
            && (!ScreenExitMemory.IsRealShopActive() || !ScreenExitMemory.HasExited(ScreenFamily.Shop)))
            return DecisionKind.Shop;

        if (Has<ChooseRestSiteOptionCommand>(available))
            return DecisionKind.Rest;

        if (Has<MapMoveCommand>(available))
            return DecisionKind.Map;

        // Standalone ProceedToMap/ProceedToNextAct with nothing else pending
        // (e.g. a rewards/treasure/rest screen that has already been fully
        // resolved and just needs to advance) also folds into RewardScreen.
        if (Has<ProceedToMapCommand>(available) || Has<ProceedToNextActCommand>(available))
            return DecisionKind.RewardScreen;

        // No known C# command family answers sts2-rl's SELECT_OPTION
        // (driver.py:71 "pick a generic option (Scroll Boxes)") — see Task 10
        // report OPEN QUESTION. Nothing is ever classified into this kind
        // yet; it exists in the enum for forward-compatibility with the
        // contract's action space.
        return DecisionKind.Unsupported;
    }
}
