using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

using MegaCrit.Sts2.Core.Entities.Merchant;

using SpireBot.Replay;
using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Translates between the flat RL action space (contract.json's "actions"
/// block, loaded into <see cref="Contract.Actions"/>) and the concrete,
/// game-truth-legal <see cref="ReplayCommand"/>s for one <see cref="DecisionContext"/>.
///
/// Every mapping block below carries a comment citing the exact sts2-rl
/// Python source it mirrors (file:line, as researched for Task 10 against
/// sts2_rl/run_env.py, sts2_rl/driver.py, sts2_rl/full_env.py). Blocks where
/// the Python semantics could not be reproduced exactly from the C# vendor's
/// data shapes are flagged OPEN QUESTION inline and in Task 10's report —
/// those are deliberate, documented approximations, not silent guesses.
///
/// Mask[i] is pure game truth: true iff <see cref="CommandFor"/> would return
/// a non-null command for action id i. No sim-legality re-derivation happens
/// here — every command placed into a slot came from
/// <c>ReplayDispatcher.GetAvailableCommands()</c> (or, for shop, from the same
/// title-keyed purchase commands matched against the live entry list), which
/// already is game truth.
/// </summary>
public sealed class ActionMap
{
    public bool[] Mask { get; }

    /// <summary>The contract this map was built against — lets a policy resolve action-block
    /// positions (e.g. the combat end-turn id) without re-plumbing the contract to it.</summary>
    public Contract Contract { get; }

    private readonly ReplayCommand?[] _commands;
    private readonly string?[] _labels;

    private ActionMap(Contract contract, int nActions)
    {
        Contract = contract;
        Mask = new bool[nActions];
        _commands = new ReplayCommand?[nActions];
        _labels = new string?[nActions];
    }

    /// <summary>The command bound to this action id, or null if masked off.</summary>
    public ReplayCommand? CommandFor(int actionId)
        => actionId >= 0 && actionId < _commands.Length ? _commands[actionId] : null;

    /// <summary>Human label for the overlay. Falls back to the command's own ToString().</summary>
    public string LabelFor(int actionId)
    {
        if (actionId < 0 || actionId >= _labels.Length)
            return $"<invalid action {actionId}>";
        return _labels[actionId] ?? _commands[actionId]?.ToString() ?? $"<unmapped action {actionId}>";
    }

    private void Set(int actionId, ReplayCommand cmd, string label)
    {
        if (actionId < 0 || actionId >= Mask.Length)
            return; // out of the contract's declared range — never index past it
        Mask[actionId] = true;
        _commands[actionId] = cmd;
        _labels[actionId] = label;
    }

    public static ActionMap Build(Contract contract, DecisionContext ctx, Obs.SessionState session)
    {
        var actions = contract.Actions;
        var map = new ActionMap(contract, actions.NActions);

        switch (ctx.Kind)
        {
            case DecisionKind.Combat:
                BuildCombat(map, actions, ctx);
                break;
            case DecisionKind.SelectCards:
                BuildSelectCards(map, actions, ctx, contract, session);
                break;
            case DecisionKind.Map:
                BuildMap(map, actions, ctx);
                break;
            case DecisionKind.Event:
                BuildEvent(map, actions, ctx);
                break;
            case DecisionKind.Shop:
                BuildShop(map, actions, ctx);
                break;
            case DecisionKind.Rest:
                BuildRest(map, actions, ctx);
                break;
            case DecisionKind.RewardScreen:
                BuildRewardScreen(map, actions, ctx);
                break;
            case DecisionKind.SelectOption:
                // No known C# command family answers this kind yet — see
                // DecisionContext.Classify's comment and Task 10's report.
                break;
            case DecisionKind.Unsupported:
            default:
                break;
        }

        // The belt-potion block is cross-cutting: an AnyTime potion is usable
        // from (almost) any non-combat screen without answering the screen's
        // own question. Mirrors run_env.py._translate decoding POTION_BASE
        // BEFORE the per-kind blocks (run_env.py:858-866) and
        // driver.py.potion_actions() gating on
        // `not (self.in_combat or self.combat is not None)` (driver.py:187-212).
        if (ctx.Kind != DecisionKind.Combat)
            BuildBeltPotion(map, actions, ctx);

        return map;
    }

    // ── Combat ───────────────────────────────────────────────────────────
    //
    // Mirrors full_env.py's shared combat action block (full_env.py:491-509
    // COMBAT_PLAY_BASE/COMBAT_POTION_BASE/combat_action_count/decode_combat_action),
    // reused as-is by run_env.py (run_env.py:323 N_COMBAT_ACTIONS =
    // combat_action_count(MAX_POTION_SLOTS)) and by driver.py's own_actions
    // COMBAT branch (driver.py:262-263: combat_action_masks(self.combat,
    // self.run.max_potions)). Exposed to C# via Contract.Actions.Combat
    // (EndTurn/PlayBase/MaxHand/MaxEnemies/PotionBase/MaxPotions).
    private static void BuildCombat(ActionMap map, ActionLayout actions, DecisionContext ctx)
    {
        var c = actions.Combat;

        // action id == EndTurn (full_env.py:539-543: action<=0 -> "end";
        // always legal on the player's turn).
        var endTurn = ctx.Available.OfType<EndTurnCommand>().FirstOrDefault();
        if (endTurn != null)
            map.Set(c.EndTurn, endTurn, "End Turn");

        // "first living enemy" — full_env.py:545-546:
        //   living = [i for i, e in enumerate(s.enemies) if not e.is_gone and i < MAX_ENEMIES]
        //   first = living[0] if living else 0
        // NOTE this is the enemy's RAW index in the encounter roster, NOT a
        // compacted "n-th living enemy" position — dead slots keep their
        // index, they just never receive a mask bit. This is why
        // DecisionContext captures EncounterEnemies separately from
        // GameStateSnapshot.Enemies (which IS pre-compacted to living-only
        // and would silently produce the wrong `e`).
        var livingEnemies = ctx.EncounterEnemies.Where(s => s.IsAlive).ToList();
        int first = livingEnemies.Count > 0 ? livingEnemies[0].Index : 0;

        // Hand index h: full_env.py:548 `for h, card in enumerate(s.player.hand[:MAX_HAND])`
        // — positional PlayerCombatState.Hand pile order, truncated to MaxHand.
        // GameStateSnapshot.Hand (GameStateSnapshot.cs:88) reads the exact same
        // pile (`combat?.Hand.Cards`) in the exact same order — this is the
        // SAME array a future ObsBuilder (Task 13) must read for hand-position
        // features, by construction.
        var hand = ctx.Snapshot.Hand;
        if (hand != null)
        {
            var playsByCard = ctx.Available.OfType<PlayCardCommand>()
                .GroupBy(p => p.CombatCardIndex)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (int h = 0; h < hand.Count && h < c.MaxHand; h++)
            {
                var cardInfo = hand[h];
                if (cardInfo.CombatId is not uint combatId) continue;
                if (!playsByCard.TryGetValue(combatId, out var plays)) continue;

                // ANY_ENEMY cards: ReplayDispatcher emits one targeted
                // PlayCardCommand per living enemy (ReplayDispatcher.cs:120-124,
                // `card.CanPlayTargeting(enemy)`) — bind each to ITS enemy's raw
                // encounter index (full_env.py:554-556: `for e in living: mask[...+e] = True`).
                foreach (var targeted in plays.Where(p => p.TargetId.HasValue))
                {
                    int e = FindEnemyIndex(ctx, targeted.TargetId!.Value);
                    if (e < 0 || e >= c.MaxEnemies) continue;
                    int actionId = c.PlayBase + h * c.MaxEnemies + e;
                    map.Set(actionId, targeted,
                        $"Play {DescribeCard(cardInfo)} -> {EnemyLabel(ctx, targeted.TargetId!.Value)}");
                }

                // Non-ANY_ENEMY cards (SELF/ALL_ENEMIES/etc): ReplayDispatcher
                // emits one untargeted PlayCardCommand (`card.CanPlayTargeting(null)`)
                // — bind it to the single "first living enemy" slot
                // (full_env.py:557-558: `mask[... + first] = True`).
                var untargeted = plays.FirstOrDefault(p => p.TargetId == null);
                if (untargeted != null && first < c.MaxEnemies)
                {
                    int actionId = c.PlayBase + h * c.MaxEnemies + first;
                    map.Set(actionId, untargeted, $"Play {DescribeCard(cardInfo)}");
                }
            }
        }

        // Potions during combat mirror the SAME "targeted -> living index,
        // untargeted -> first living" rule as cards, one level down
        // (full_env.py:560-568):
        //   if potion.targeted: for e in living: mask[COMBAT_POTION_BASE + p*MAX_ENEMIES + e] = True
        //   else: mask[COMBAT_POTION_BASE + p*MAX_ENEMIES + first] = True
        foreach (var potion in ctx.Available.OfType<UsePotionCommand>())
        {
            if (potion.PotionIndex >= (uint)c.MaxPotions) continue;

            int e;
            string targetLabel;
            if (potion.TargetId.HasValue)
            {
                e = FindEnemyIndex(ctx, potion.TargetId.Value);
                targetLabel = $" -> {EnemyLabel(ctx, potion.TargetId.Value)}";
            }
            else
            {
                e = first;
                targetLabel = "";
            }
            if (e < 0 || e >= c.MaxEnemies) continue;

            int actionId = c.PotionBase + (int)potion.PotionIndex * c.MaxEnemies + e;
            map.Set(actionId, potion, $"Use potion slot {potion.PotionIndex}{targetLabel}");
        }
    }

    private static string DescribeCard(CardInfo card)
        => card.Upgraded ? $"{card.Id}+" : card.Id;

    private static string EnemyLabel(DecisionContext ctx, uint combatId)
    {
        var slot = ctx.EncounterEnemies.FirstOrDefault(s => s.CombatId == combatId);
        return string.IsNullOrEmpty(slot.Name) ? $"enemy#{combatId}" : slot.Name;
    }

    /// <summary>Index of the encounter slot with the given CombatId, or -1 if not found.</summary>
    private static int FindEnemyIndex(DecisionContext ctx, uint combatId)
    {
        foreach (var slot in ctx.EncounterEnemies)
            if (slot.CombatId == combatId)
                return slot.Index;
        return -1;
    }

    // ── Select block (SELECT_CARDS only) ────────────────────────────────
    //
    // run_env.py._translate's SELECT_CARDS branch (run_env.py:865-873):
    //   if request.skippable and action == CHOICE_BASE: return len(request.candidates)
    //   if SELECT_BASE <= action < SELECT_BASE + MAX_SELECT_CANDIDATES:
    //       i = action - SELECT_BASE
    //       order = self._sorted_candidate_order(request)
    //       if i < len(order): return order[i]
    //
    // OPEN QUESTION (flagged in Task 10's report, not silently guessed):
    // `_sorted_candidate_order` (run_env.py:958-1008) sorts candidates by the
    // SAME (ints, floats) row key ObsBuffer.write_rows uses for the
    // `select.candidates` obs rows (`_run_card_row`, tied to the entity obs
    // schema) — that encoder does not exist on the C# side yet (it lands with
    // Task 13's ObsBuilder). Until it does, this uses screen/enumeration
    // order (the order ReplayDispatcher.GetAvailableCommands() produced the
    // candidate commands in — hand pile order for SelectHandCardsCommand,
    // grid layout order for SelectGridCardCommand, on-screen child order for
    // SelectCardFromScreenCommand) as a deterministic placeholder. This is
    // NOT verified to match Python's sort — revisit once Task 13 ports
    // `_run_card_row`/the row-sort key to C#.
    private static void BuildSelectCards(ActionMap map, ActionLayout actions, DecisionContext ctx, Contract contract, Obs.SessionState session)
    {
        var s = actions.Select;

        // DecisionLists.SelectCandidates: the SAME sorted (command, card) list
        // Obs.RunObsWriter's select.candidates rows are written from, in the identical order
        // (Fix 6, round 4 — now content-sorted to match sim's `_select_order`, not screen
        // order; see that method's doc comment).
        var (resolved, skip) = DecisionLists.SelectCandidates(ctx, contract, session);
        var candidates = resolved.Select(r => r.Command).ToList();

        for (int i = 0; i < candidates.Count && i < s.MaxCandidates; i++)
            map.Set(s.Base + i, candidates[i], $"Select candidate [{i}] ({candidates[i]})");

        // Skippable candidate selections answer at the CHOICE block's base
        // slot, NOT inside the SELECT block (run_env.py:868-870:
        // `if request.skippable and action == CHOICE_BASE: return len(request.candidates)`).
        if (skip != null)
            map.Set(actions.Choice.Base, skip, "Skip selection");
    }

    // ── Map ──────────────────────────────────────────────────────────────
    //
    // driver.py own_actions MAP branch (driver.py:216-217):
    //   return list(range(len(self.points)))
    // where self.points = run.travelable_points() (driver.py:446-449) built
    // from run.py:1422-1432:
    //   points = set(self.current_point.children) [+ next row under free travel]
    //   sorted(points, key=lambda p: (p.col, p.row))
    // — column-then-row order. Every MapMoveCommand from the current node
    // shares the same target row (row = current.row + 1, or 0 at act start —
    // MapMoveCommand.cs:72-84), so sorting by Col alone reproduces the
    // (col, row) sort key exactly.
    private static void BuildMap(ActionMap map, ActionLayout actions, DecisionContext ctx)
    {
        // DecisionLists.MapMoves: the SAME Col-sorted list Obs.RunObsWriter's map{m} slots
        // are written from (Task 13) — shared so slot m and action CHOICE_BASE+m always name
        // the same target point.
        var moves = DecisionLists.MapMoves(ctx);
        for (int i = 0; i < moves.Count && i < actions.Choice.Slots; i++)
            map.Set(actions.Choice.Base + i, moves[i], $"Move to column {moves[i].Col}");
    }

    // ── Event ────────────────────────────────────────────────────────────
    //
    // driver.py own_actions EVENT branch (driver.py:220-223):
    //   [i for i, opt in enumerate(self.event.options) if not opt.locked]
    // — index into event.options, filtered to unlocked.
    private static void BuildEvent(ActionMap map, ActionLayout actions, DecisionContext ctx)
    {
        // ReplayDispatcher.cs:199-210 emits one ChooseEventOptionCommand(i)
        // per i in 0..CurrentOptions.Count-1 with NO locked-option filter.
        // Per this task's Mask rule ("pure game truth from Available; no sim
        // legality logic") those are passed straight through as-is — if a
        // locked option turns out to be clickable in the vendor it will just
        // fail/retry at Execute() time, a vendor-level concern outside Task 10.
        // DecisionLists.EventOptions: the SAME list Obs.RunObsWriter's event.options rows
        // are written from (Task 13).
        var options = DecisionLists.EventOptions(ctx);

        foreach (var opt in options)
            if (opt.RecordedIndex < actions.Choice.Slots)
                map.Set(actions.Choice.Base + opt.RecordedIndex, opt, $"Event option {opt.RecordedIndex}");

        // NOTE: RecordedIndex == -1 (PROCEED) has no Python-side decision
        // analog — sts2-rl's sim auto-proceeds when an event finishes, it
        // never asks a "proceed" question. Deliberately left unmapped to any
        // action id; BotController must special-case and auto-dispatch it
        // (OPEN QUESTION, see Task 10 report).
    }

    // ── Shop ─────────────────────────────────────────────────────────────
    //
    // driver.py own_actions SHOP branch (driver.py:225-231):
    //   entries = self.shop.all_entries
    //   legal = [i for i, e in enumerate(entries) if e.is_stocked and e.enough_gold]
    //   legal.append(len(entries))   # leaving is always legal
    // where MerchantInventory.all_entries (shop.py:485-493) orders entries as
    // [character card slots..., colorless card slots..., relic slots...,
    // potion slots..., card-removal entry].
    //
    // OPEN QUESTION (flagged in Task 10's report): the shop's C# purchase
    // commands (BuyCardCommand/BuyRelicCommand/BuyPotionCommand/
    // BuyCardRemovalCommand) are title-keyed, not index-keyed
    // (ReplayDispatcher.cs:331-387 / ShopCommands.cs). To assign each a
    // canonical choice-slot index we walk OpenShopCommand.GetEntries(room) /
    // BuyRelicCommand.GetFakeMerchantEntries(...) — but those are
    // REFLECTION-based scans of NMerchantInventory's fields/properties
    // (ShopCommands.cs:80-134 ScanObject), in field/property DECLARATION
    // order, which has NOT been verified to match shop.py's canonical
    // [character cards, colorless cards, relics, potions, removal] order.
    // This is a real risk of shop action-id misalignment against a
    // Python-trained policy; needs confirmation against the live game's
    // NMerchantInventory layout (Task 16's parity harness), not silently
    // assumed correct here.
    private static void BuildShop(ActionMap map, ActionLayout actions, DecisionContext ctx)
    {
        var choice = actions.Choice;

        // DecisionLists.ShopEntries: the SAME flat entries list Obs.RunObsWriter slices by
        // type (cards/relics/potions/removal) for the shop.* obs segments (Task 13).
        List<MerchantEntry>? entries = DecisionLists.ShopEntries(ctx);

        int usedSlots = 0;
        if (entries != null)
        {
            for (int i = 0; i < entries.Count && i < choice.Slots; i++)
            {
                var (cmd, label) = ResolvePurchase(ctx, entries[i]);
                if (cmd == null) continue;
                map.Set(choice.Base + i, cmd, label!);
                usedSlots = i + 1;
            }
        }

        // "Leave" — CloseShopCommand for a real shop, ProceedToMapCommand
        // otherwise (fake-merchant events have no close button, only proceed).
        // Slot = len(entries), matching driver.py:231 `legal.append(len(entries))`.
        int leaveSlot = entries?.Count ?? usedSlots;
        if (leaveSlot < choice.Slots)
        {
            ReplayCommand? leave = ctx.Available.OfType<CloseShopCommand>().FirstOrDefault();
            leave ??= ctx.Available.OfType<ProceedToMapCommand>().FirstOrDefault();
            if (leave != null)
                map.Set(choice.Base + leaveSlot, leave, "Leave shop");
        }
    }

    private static (ReplayCommand? cmd, string? label) ResolvePurchase(DecisionContext ctx, MerchantEntry entry)
    {
        switch (entry)
        {
            case MerchantCardEntry mce:
            {
                string? title = mce.CreationResult?.Card?.Title;
                if (title == null) return (null, null);
                var cmd = ctx.Available.OfType<BuyCardCommand>().FirstOrDefault(c => c.CardTitle == title);
                return cmd == null ? (null, null) : (cmd, $"Buy card '{title}'");
            }
            case MerchantRelicEntry mre:
            {
                string? title = mre.Model?.Title.GetFormattedText();
                if (title == null) return (null, null);
                var cmd = ctx.Available.OfType<BuyRelicCommand>().FirstOrDefault(c => c.RelicTitle == title);
                return cmd == null ? (null, null) : (cmd, $"Buy relic '{title}'");
            }
            case MerchantPotionEntry mpe:
            {
                string? title = mpe.Model?.Title.GetFormattedText();
                if (title == null) return (null, null);
                var cmd = ctx.Available.OfType<BuyPotionCommand>().FirstOrDefault(c => c.PotionTitle == title);
                return cmd == null ? (null, null) : (cmd, $"Buy potion '{title}'");
            }
            case MerchantCardRemovalEntry:
            {
                var cmd = ctx.Available.OfType<BuyCardRemovalCommand>().FirstOrDefault();
                return cmd == null ? (null, null) : (cmd, "Buy card removal");
            }
            default:
                return (null, null);
        }
    }

    // ── Rest ─────────────────────────────────────────────────────────────
    //
    // driver.py own_actions REST branch (driver.py:238-249) + constants
    // (driver.py:79-80): REST_HEAL=0, REST_SMITH=1, REST_LEAVE=2, then
    // REST_LEAVE+1+i for each of the visit's extra rest_options (relic/card
    // provided), in that snapshot list order.
    //
    // OPEN QUESTION (flagged in Task 10's report): ChooseRestSiteOptionCommand
    // is keyed by a string OptionId (ChooseRestSiteOptionCommand.cs) whose
    // actual values were not discoverable from the vendored source (they live
    // in the un-decompiled MegaCrit assembly's RestSiteOption model). This
    // does CONTENT-SNIFFING on the OptionId string ("heal"/"smith"/"leave"
    // substrings, case-insensitive) to place options at the fixed semantic
    // slots 0/1/2 Python expects, falling back to REST_LEAVE+1+i for anything
    // unrecognized, in Available's enumeration order (== RestSiteSynchronizer
    // .GetLocalOptions() order — ReplayDispatcher.cs:190-196). This has NOT
    // been verified against the live game's real OptionId strings — verify
    // with Task 11's in-game stub smoke before trusting it.
    private static void BuildRest(ActionMap map, ActionLayout actions, DecisionContext ctx)
    {
        const int RestHeal = 0, RestSmith = 1, RestLeave = 2;
        var choice = actions.Choice;

        var options = ctx.Available.OfType<ChooseRestSiteOptionCommand>()
            .Where(RestOptionIsClickable)
            .ToList();
        var extras = new List<ChooseRestSiteOptionCommand>();

        foreach (var opt in options)
        {
            string id = opt.OptionId;
            if (id.Contains("heal", StringComparison.OrdinalIgnoreCase))
                map.Set(choice.Base + RestHeal, opt, "Rest: Heal");
            else if (id.Contains("smith", StringComparison.OrdinalIgnoreCase)
                     || id.Contains("upgrade", StringComparison.OrdinalIgnoreCase))
                map.Set(choice.Base + RestSmith, opt, "Rest: Smith");
            else if (id.Contains("leave", StringComparison.OrdinalIgnoreCase)
                     || id.Contains("proceed", StringComparison.OrdinalIgnoreCase))
                map.Set(choice.Base + RestLeave, opt, "Rest: Leave");
            else
                extras.Add(opt);
        }

        for (int i = 0; i < extras.Count; i++)
        {
            int slot = RestLeave + 1 + i;
            if (slot >= choice.Slots) break;
            map.Set(choice.Base + slot, extras[i], $"Rest: {extras[i].OptionId}");
        }
    }

    /// <summary>
    /// Whether this rest option could actually be clicked by a real player RIGHT NOW — the
    /// delta-17 "Minor symmetry gap" closed at mask level (2026-08-20 audit issue 2): both
    /// execute-side guards (ChooseRestSiteOptionCommand.cs Fix B/C) already refuse, but a
    /// refusal consumes the decision, degrades the next one to Unsupported, and the old
    /// first-available fallback then did something destructive (DiscardPotion at a rest site).
    /// Two layers, mirroring the execute-side pair exactly:
    ///   1. the OPTION's own IsEnabled (Fix C's state-level gate — Smith with nothing to
    ///      upgrade, Cook with &lt;2 removable cards);
    ///   2. the BUTTON's live-click affordance (Fix B's control-level gate — NRestSiteRoom
    ///      .DisableOptions() disables every button while an option resolves, and the buttons
    ///      are also briefly dead while the room animates in; observed live as the double-Smith
    ///      re-ask: the first Smith's SelectOption disables the buttons synchronously, the bot
    ///      re-decided 4ms later and dispatched a second Smith into the refusal —
    ///      diagnostic.log 18:55:06.261-.298).
    /// When everything here masks off, the mask goes all-false with commands still available —
    /// exactly the "game says not-yet" state AdvanceStuckGuard's GatedStuckThreshold patience
    /// was built for; the next settle/heartbeat re-decides once the buttons come back.
    /// </summary>
    private static bool RestOptionIsClickable(ChooseRestSiteOptionCommand cmd)
    {
        var sync = SpireBot.Replay.ReplayState.ActiveRestSiteSynchronizer;
        if (sync == null)
            return false;

        MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption? option = null;
        foreach (var o in sync.GetLocalOptions())
        {
            if (o.OptionId == cmd.OptionId)
            {
                option = o;
                break;
            }
        }

        if (option == null || !option.IsEnabled)
            return false;

        var room = MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
            return false;

        return Affordance.IsLive(room.GetButtonForOption(option));
    }

    // ── RewardScreen ─────────────────────────────────────────────────────
    //
    // OPEN QUESTION (flagged in Task 10's report): sts2-rl models rewards as
    // several separate, type-specific decisions — REWARD_CARD (driver.py:
    // 250-257, 0..n-1 pick a card / n skip / n+1 reroll(Driftwood) / n+2
    // sacrifice(Pael's Wing)), REWARD_POTION and REWARD_RELIC (driver.py:
    // 258-268, fixed take/skip pairs) — asked one at a time as the run state
    // walks the reward list. The C# vendor instead exposes ONE flat rewards
    // screen with a button per reward item, of possibly mixed types
    // (ClaimRewardCommand.RewardIndex == the screen's button position,
    // ClaimRewardCommand.cs:99-119 EnumerateRewardButtons), plus a follow-up
    // TakeCardCommand sub-screen specifically for card-type rewards. These
    // are materially different action-space shapes; reconciling them exactly
    // needs product-level design beyond a mechanical port. This block:
    //   - when a card-reward sub-screen is open (TakeCardCommand candidates
    //     present), reproduces REWARD_CARD's 0..n-1/skip/+2=sacrifice layout
    //     (no C# command was found for Driftwood's reroll slot n+1 — left
    //     unmapped);
    //   - otherwise, passes each ClaimRewardCommand's button index straight
    //     through as the choice slot, and appends treasure-chest / skip /
    //     proceed commands after the button count.
    /// <summary>
    /// Whether the treasure chest could actually be clicked right now — the same test the game
    /// applies to a real click (see <see cref="Affordance"/>). Node path mirrors
    /// OpenChestCommand.Execute's own lookup.
    /// </summary>
    private static bool ChestButtonIsLive()
    {
        var room = SpireBot.Replay.Patches.Replay.TreasureRoomReplayPatch.ActiveRoom;
        if (room == null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
            return false;
        return Affordance.IsLive(room.GetNodeOrNull<NButton>("%Chest"));
    }

    /// <summary>
    /// The chest button exists but is no longer clickable — NTreasureRoom disables it exactly
    /// when the chest opens, so this is the UI-truth "chest has been opened" signal even when
    /// the opening click wasn't the bot's own (a human playing in advisor mode). Deliberately
    /// false while the button hasn't loaded yet (node null): a still-loading room is not an
    /// opened chest, and treating it as one is exactly the take-before-open crash this guards.
    /// </summary>
    private static bool ChestButtonPresentButDead()
    {
        var room = SpireBot.Replay.Patches.Replay.TreasureRoomReplayPatch.ActiveRoom;
        if (room == null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
            return false;
        var chest = room.GetNodeOrNull<NButton>("%Chest");
        return chest != null && GodotObject.IsInstanceValid(chest) && !Affordance.IsLive(chest);
    }

    private static void BuildRewardScreen(ActionMap map, ActionLayout actions, DecisionContext ctx)
    {
        var choice = actions.Choice;

        // DecisionLists.RewardTakeCards: the SAME list Obs.RunObsWriter's reward.cards rows
        // are positionally aligned with via DecisionLists.RewardCardModels (Task 13).
        var takeCards = DecisionLists.RewardTakeCards(ctx);

        if (takeCards.Count > 0)
        {
            for (int i = 0; i < takeCards.Count && i < choice.Slots; i++)
                map.Set(choice.Base + i, takeCards[i], $"Take card [{takeCards[i].CardIndex}]");

            int n = takeCards.Count;
            var skip = ctx.Available.OfType<TakeCardCommand>().FirstOrDefault(t => t.IsSkip);
            if (skip != null && n < choice.Slots)
                map.Set(choice.Base + n, skip, "Skip card reward");

            var sacrifice = ctx.Available.OfType<TakeCardCommand>().FirstOrDefault(t => t.IsSacrifice);
            if (sacrifice != null && n + 2 < choice.Slots)
                map.Set(choice.Base + n + 2, sacrifice, "Sacrifice for card reward");
            // n+1 (Driftwood reroll) intentionally left unmapped — see block comment.
            return;
        }

        // fix-reward-loop-brief.md #2/#3 — a CARD reward's ClaimRewardCommand only opens the
        // pick-a-card subscreen (the real decision is the TakeCardCommand that follows, same
        // shape PassiveDump.cs:325-350 round 5 already classifies), so it must never be offered
        // to the model as a choice here; BotController.TryAutoAdvance auto-dispatches it
        // instead. A declined index (RewardClaimMemory, set once the model actually skips the
        // subscreen) must also never be re-offered/re-opened — that is the loop this whole fix
        // closes: without both exclusions, claiming re-opens the identical still-unclaimed
        // picker forever. Potion/relic/gold claims are unaffected — those grant immediately
        // with no subscreen and stay exactly the sim's REWARD_POTION/REWARD_RELIC ask.
        foreach (var claim in ctx.Available.OfType<ClaimRewardCommand>())
        {
            // Identity + state-level affordance (2026-08-20 audit): resolve the reward OBJECT
            // (indexes shift as claimed buttons disappear — see RewardScreenClassifier
            // .ResolveReward's doc) and only map claims that would actually land
            // (Affordance.RewardClaimWouldLand — the game keeps a terminal NRewardsScreen's
            // buttons enumerable forever, but once the synchronizer's rewardsStack popped, a
            // click throws "not currently viewing any reward set!" inside a swallowed task).
            var reward = RewardScreenClassifier.ResolveReward(claim.RewardIndex);
            if (reward == null || !Affordance.RewardClaimWouldLand(reward))
                continue;
            if (RewardClaimMemory.IsDeclined(reward))
                continue;
            if (RewardScreenClassifier.KindOf(reward) == RewardKind.Card)
                continue;
            if (claim.RewardIndex < choice.Slots)
                map.Set(choice.Base + claim.RewardIndex, claim, $"Claim reward [{claim.RewardIndex}]");
        }

        int nextSlot = ctx.Available.OfType<ClaimRewardCommand>()
            .Select(c => c.RewardIndex + 1)
            .DefaultIfEmpty(0)
            .Max();

        // Masked off once the game itself has disabled the chest button — the enumerator offers
        // OpenChest for the whole treasure room (a recorded replay just never asks twice), but
        // NTreasureRoom disables the button after opening, so a player cannot click it again.
        // Affordance is the real rule; RoomOneShots is a backstop for the destructive case.
        var openChest = ctx.Available.OfType<OpenChestCommand>()
            .FirstOrDefault(c => !RoomOneShots.AlreadyDone(c) && ChestButtonIsLive());
        if (openChest != null && nextSlot < choice.Slots)
            map.Set(choice.Base + nextSlot++, openChest, "Open treasure chest");

        // Only while the game is actually accepting a pick — otherwise PickRelicLocally throws
        // ("relic picking is not active"), which it did on every decision, forever.
        //
        // RelicPickingActive alone is NOT "the chest is open": TreasureRoom.Enter calls
        // BeginRelicPicking() the moment the ROOM loads (TreasureRoom.cs:47), so the backend
        // accepts a pick while the chest is still closed. Such a pick then executes — and the
        // game's own treasure UI crashes (NTreasureRoomRelicCollection.AnimateRelicAwards runs
        // First() over relic nodes that only the open animation creates), wedging the room
        // (observed live: run SXFY52G6VQ act1 floor9, where the policy sampled Take before
        // Open; act0 floor10 had sampled Open first and worked). A human can never click Take
        // first because those relic nodes don't exist yet — mirror that ordering here: Take is
        // offered only once the chest is actually open, signalled either by RoomOneShots (the
        // bot dispatched OpenChest this visit) or by the chest button being present but no
        // longer clickable (NTreasureRoom disables it exactly when it opens — this covers a
        // human opening the chest in advisor mode).
        bool chestOpened =
            ctx.Available.OfType<OpenChestCommand>().Any(RoomOneShots.AlreadyDone)
            || ChestButtonPresentButDead();
        var takeChestRelic = Affordance.RelicPickingActive() && chestOpened
            ? ctx.Available.OfType<TakeChestRelicCommand>().FirstOrDefault()
            : null;
        if (takeChestRelic != null && nextSlot < choice.Slots)
            map.Set(choice.Base + nextSlot++, takeChestRelic, "Take chest relic");

        // fix-reward-loop-brief.md #2 — while a card reward's claim is still outstanding (legal
        // and not yet declined), this screen must not offer SkipRewards/ProceedToNextAct/
        // ProceedToMap either: none of those has a sim-side counterpart of its own
        // (SkipRewardsCommand — PassiveDump.cs round-4 FIX 2 — "this command has no sim-side
        // counterpart and must not be dumped"), so mapping them here would let the policy walk
        // away from (or blanket-skip, via SkipRewards) a reward the live path is required to
        // resolve through the card subscreen first, same as ActionMap already never maps a
        // treasure room's ProceedToMap ahead of the chest. Once TryAutoAdvance dispatches the
        // claim, the TakeCardCommand pick/skip that follows is the real per-sim decision; only
        // once that resolves (picked, sacrificed, or declined) do these exits reappear.
        bool cardClaimPending = ctx.Available.OfType<ClaimRewardCommand>()
            .Any(RewardClaimMemory.IsPendingCardClaim);

        var skipRewards = cardClaimPending
            ? null
            : ctx.Available.OfType<SkipRewardsCommand>().FirstOrDefault();
        if (skipRewards != null && nextSlot < choice.Slots)
            map.Set(choice.Base + nextSlot++, skipRewards, "Skip rewards");

        var proceedAct = cardClaimPending
            ? null
            : ctx.Available.OfType<ProceedToNextActCommand>().FirstOrDefault();
        if (proceedAct != null && nextSlot < choice.Slots)
            map.Set(choice.Base + nextSlot++, proceedAct, "Proceed to next act");

        var proceedMap = cardClaimPending
            ? null
            : ctx.Available.OfType<ProceedToMapCommand>().FirstOrDefault();
        if (proceedMap != null && nextSlot < choice.Slots)
            map.Set(choice.Base + nextSlot++, proceedMap, "Proceed to map");
    }

    // ── Belt potion ──────────────────────────────────────────────────────
    //
    // run_env.py._translate's POTION_BASE branch (run_env.py:858-866):
    //   answer = POTION_ACTION_BASE + (action - POTION_BASE)
    //   return answer if answer in request.potion_actions() else None
    // and driver.py.potion_actions() (driver.py:187-212):
    //   [POTION_ACTION_BASE + slot for slot, potion in enumerate(self.run.potions)
    //    if potion is not None and potion.usage == USAGE_ANY_TIME]
    // — slot p maps directly and positionally to the belt array (run.potions[p]),
    // untargeted (no enemy dimension), only outside combat. ReplayDispatcher's
    // out-of-combat UsePotionCommand enumeration (ReplayDispatcher.cs:129-157)
    // only ever emits an untargeted instance when `!inCombat` (needsEnemyTarget
    // AnyEnemy potions get no instance at all outside combat, since there's no
    // combatState to enumerate enemies from) — so filtering to TargetId == null
    // here is exactly the untargeted belt action, never a stray combat one.
    private static void BuildBeltPotion(ActionMap map, ActionLayout actions, DecisionContext ctx)
    {
        var b = actions.BeltPotion;
        foreach (var potion in ctx.Available.OfType<UsePotionCommand>())
        {
            if (potion.TargetId.HasValue) continue;
            if (potion.PotionIndex >= (uint)b.Slots) continue;
            int actionId = b.Base + (int)potion.PotionIndex;
            map.Set(actionId, potion, $"Drink potion slot {potion.PotionIndex}");
        }
    }
}
