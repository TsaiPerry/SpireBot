using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Godot;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

using SpireBot.Replay;
using SpireBot.Replay.Commands;

namespace SpireBot.SpireBotCode.Obs;

/// <summary>
/// Writes every non-<c>combat.*</c> segment of the observation — <c>phase</c>, <c>run.*</c>,
/// <c>map*</c>, <c>event.*</c>, <c>shop.*</c>, <c>reward.*</c>, <c>select.*</c> — per the
/// schema audit (<c>docs/superpowers/specs/2026-08-04-spirebot-schema-audit.md</c>, "Step
/// 1/2 — Run observation (v9)") and sts2-rl's <c>run_env.py</c> (<c>run_obs_segments_f/_i</c>,
/// <c>_build_obs</c>), which this type mirrors segment-for-segment. Follows
/// <see cref="CombatObsWriter"/>'s conventions exactly: every field write goes through the
/// bounds-checked <see cref="Put"/>/<see cref="PutI"/> pair (no numeric offsets anywhere
/// below), unmapped game ids PAD-and-log-once via <see cref="SessionState.ShouldLogUnmapped"/>,
/// and every row block tracks its own truncation flag for the matching
/// <c>&lt;segment&gt;.overflow</c> field.
///
/// Runs on EVERY decision, in and out of combat (unlike <see cref="CombatObsWriter"/>, which
/// only has something to write while a live <see cref="ICombatState"/> exists) — this mirrors
/// <c>run_env._build_obs</c>'s own shape: run vitals/deck/relics/potions/map/boss are written
/// every step, phase-specific blocks (map slots / event / shop / reward / select) only while
/// their own <see cref="DecisionKind"/> is current.
///
/// ── Shared canonical order (task brief's core invariant) ───────────────────────────────
/// Every screen-candidate segment below (map slots, event options, shop entries, reward
/// cards, select candidates) is read from <see cref="DecisionLists"/> — the SAME lists
/// <see cref="ActionMap"/> binds to action ids — rather than re-deriving its own order. See
/// <see cref="DecisionLists"/>'s own doc comment for why, and each write method below for the
/// specific ActionMap block it shares with.
/// </summary>
public static class RunObsWriter
{
    // ── sizing constants (run_env.py's own; see run_env.py's "Fixed-size bounds" section for
    // each name's full citation) ──
    private const int MaxPotionSlots = 10;       // MAX_POTION_SLOTS = full_env.MAX_POTION_ROWS
    private const int MaxDeckRows = 96;          // MAX_DECK_ROWS = MAX_COMBAT_CARDS
    private const int MaxRelicRows = 48;         // full_env.MAX_RELIC_ROWS
    private const int MaxBossIds = 4;
    private const int MapSlots = 7;
    private const int MapGridRows = 15;
    private const int MapWidth = 7;              // _MAP_WIDTH
    private const int NPointTypes = 9;           // len(MapPointType) — MapPointType.cs's 9 values
    private const int MapGridNode = 1 + NPointTypes + MapWidth + 1; // 18: present + type one-hot + child-col mask + current-position bit
    private const int ChoiceSlots = 16;
    private const int ShopCardSlots = 7;
    private const int ShopRelicSlots = 3;
    private const int ShopPotionSlots = 3;
    private const int RewardCardSlots = 3;
    private const int MaxSelectCandidates = 96;

    private const float AbsScale = 100f;
    private const float AbsScaleCoarse = 500f;
    private const float GoldLogFineDenom = 800f;
    private const float GoldLogCoarseDenom = 8000f;
    private const float ShopCostLogDenom = 900f;

    // ── numeric helpers (mirror full_env._clip01/_abs2 + run_env._log1p_scale) ──
    private static float Clip01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    private static (float fine, float coarse) Abs2(float x) => (Clip01(x / AbsScale), Clip01(x / AbsScaleCoarse));
    private static float Log1pScale(float x, float denom)
        => Clip01((float)(Math.Log(1.0 + Math.Max(0.0, x)) / Math.Log(1.0 + denom)));

    // ── bounds-checked segment writers (identical contract to CombatObsWriter's own) ──
    private static void Put(float[] f, LayoutSlice slice, int idx, float v)
    {
        if ((uint)idx >= (uint)slice.Width)
            throw new ObsBuilderException($"RunObsWriter: float index {idx} out of range (width {slice.Width}) at offset {slice.Offset}.");
        f[slice.Offset + idx] = v;
    }

    private static void PutI(long[] i, LayoutSlice slice, int idx, long v)
    {
        if ((uint)idx >= (uint)slice.Width)
            throw new ObsBuilderException($"RunObsWriter: int index {idx} out of range (width {slice.Width}) at offset {slice.Offset}.");
        i[slice.Offset + idx] = v;
    }

    private static long Vocab(Contract c, SessionState session, string kind, string? gameId)
    {
        if (gameId == null) return 0;
        int id = c.VocabId(kind, gameId);
        if (id == 0 && session.ShouldLogUnmapped(kind, gameId))
            GD.Print($"[SpireBot] RunObsWriter: unmapped {kind} game id '{gameId}' (contract has no vocab entry) — writing PAD.");
        return id;
    }

    // ── RunState/player resolution ──────────────────────────────────────────────────────
    // RunManager.State is non-public; ReplayDispatcher/GameStateSnapshot/MapMoveCommand each
    // already keep their own private copy of this exact reflection lookup (no shared helper
    // exists for it in this codebase) — this follows the same established convention rather
    // than introducing a new cross-cutting accessor.
    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private static IRunState? ResolveRunState() => RunStateProp?.GetValue(RunManager.Instance) as IRunState;

    /// <summary>How the current reward screen resolves against sts2-rl's split
    /// REWARD_CARD/REWARD_POTION/REWARD_RELIC decision kinds — see <see cref="ClassifyReward"/>.
    /// </summary>
    private enum RewardSubKind { None, Card, Potion, Relic }

    /// <summary>Writes every non-<c>combat.*</c> segment, or leaves the whole block at
    /// <see cref="Obs"/>'s zero-fill if no run is in progress.</summary>
    public static void Write(Contract c, Obs obs, DecisionContext ctx, SessionState session)
    {
        var state = ResolveRunState();
        if (state == null) return;

        var player = state.Players.FirstOrDefault();
        var f = obs.F;
        var iarr = obs.I;

        var rewardKind = ClassifyReward(ctx, out var rewardCardModels, out var rewardTakeCards,
            out var potionButton, out var relicButton);

        WritePhase(c, f, ctx, rewardKind);

        if (player != null)
            WriteVitals(c, f, state, player, ctx, session);

        var overflow = new Dictionary<string, bool>();
        if (player != null)
        {
            overflow["run.potions"] = WritePotions(c, f, iarr, session, player);
            overflow["run.deck"] = WriteDeck(c, f, iarr, session, player);
            overflow["run.relics"] = WriteRelics(c, f, iarr, session, player);
        }
        overflow["run.boss"] = WriteBoss(c, iarr, session, state);

        WriteMapSlots(c, f, ctx, state);
        WriteMapGrid(c, f, state);
        WriteEvent(c, f, iarr, session, ctx);
        WriteShop(c, f, iarr, session, ctx);
        WriteReward(c, f, iarr, session, ctx, rewardKind, rewardTakeCards, rewardCardModels, potionButton, relicButton);
        overflow["select.candidates"] = WriteSelect(c, f, iarr, session, ctx);

        foreach (var (name, truncated) in overflow)
        {
            if (!truncated) continue;
            Put(f, c.F($"{name}.overflow"), 0, 1.0f);
        }
    }

    // ── phase one-hot ────────────────────────────────────────────────────────────────────
    //
    // REDEFINE (schema audit): sts2-rl's DecisionKind is 10-wide (MAP=0, EVENT=1, SHOP=2,
    // REST=3, REWARD_CARD=4, REWARD_POTION=5, COMBAT=6, SELECT_CARDS=7, SELECT_OPTION=8,
    // REWARD_RELIC=9 — driver.py's declaration order) but C#'s own DecisionContext.DecisionKind
    // (Task 10) folds every reward flavor into one RewardScreen value, since ReplayDispatcher
    // shows one mixed-type button screen rather than sts2-rl's one-decision-at-a-time reward
    // walk. RewardSubKind (ClassifyReward, shared with WriteReward below) recovers which of
    // the three reward phases applies; DecisionKind.SelectOption never actually fires today
    // (DecisionContext.Classify's own comment) but is mapped for forward-compatibility.
    private static void WritePhase(Contract c, float[] f, DecisionContext ctx, RewardSubKind rewardKind)
    {
        int? idx = ctx.Kind switch
        {
            DecisionKind.Map => 0,
            DecisionKind.Event => 1,
            DecisionKind.Shop => 2,
            DecisionKind.Rest => 3,
            DecisionKind.Combat => 6,
            DecisionKind.SelectCards => 7,
            DecisionKind.SelectOption => 8,
            DecisionKind.RewardScreen => rewardKind switch
            {
                RewardSubKind.Card => 4,
                RewardSubKind.Potion => 5,
                RewardSubKind.Relic => 9,
                _ => (int?)null, // a claimable button screen sts2-rl has no matching kind for (e.g. gold/chest-only) — no phase bit set
            },
            _ => null,
        };
        if (idx is int i)
            Put(f, c.F("phase"), i, 1.0f);
    }

    // ── run vitals ───────────────────────────────────────────────────────────────────────
    /// <summary>run.hp_ratio/run.hp_abs/run.max_hp_abs FREEZE at the pre-combat value for
    /// the whole fight — sts2-rl (conformance/runner.py:859,916; run_env.py:1045-1048)
    /// deliberately doesn't live-track HP here (combat.player.hp_* already does that);
    /// round-3 fix 6. Falls back to the live creature values whenever no combat-start
    /// snapshot exists (outside combat, or a snapshot somehow wasn't captured).
    /// Round-3-rework: freeze whenever a combat is actually live (CombatManager.
    /// IsInProgress &amp;&amp; !IsOverOrEnding), not just when ctx.Kind == Combat — the sim's
    /// freeze persists for every decision raised while a combat is open, including
    /// in-combat SelectCards (a non-Combat ctx.Kind).</summary>
    private static void WriteVitals(Contract c, float[] f, IRunState state, Player player, DecisionContext ctx, SessionState session)
    {
        var creature = player.Creature;
        var mgr = CombatManager.Instance;
        bool combatLive = mgr != null && mgr.IsInProgress && !mgr.IsOverOrEnding;
        int hp, maxHp;
        if (combatLive && session.CombatStartHp is int snapHp && session.CombatStartMaxHp is int snapMaxHp)
        {
            hp = snapHp;
            maxHp = snapMaxHp;
        }
        else
        {
            hp = Math.Max(0, creature.CurrentHp);
            maxHp = creature.MaxHp;
        }

        Put(f, c.F("run.hp_ratio"), 0, Clip01((float)hp / Math.Max(1, maxHp)));
        var hpAbs = Abs2(hp);
        Put(f, c.F("run.hp_abs"), 0, hpAbs.fine); Put(f, c.F("run.hp_abs"), 1, hpAbs.coarse);
        var maxHpAbs = Abs2(maxHp);
        Put(f, c.F("run.max_hp_abs"), 0, maxHpAbs.fine); Put(f, c.F("run.max_hp_abs"), 1, maxHpAbs.coarse);

        Put(f, c.F("run.gold"), 0, Log1pScale(player.Gold, GoldLogFineDenom));
        Put(f, c.F("run.gold"), 1, Log1pScale(player.Gold, GoldLogCoarseDenom));

        var actSlice = c.F("run.act");
        if (state.CurrentActIndex >= 0 && state.CurrentActIndex < actSlice.Width)
            Put(f, actSlice, state.CurrentActIndex, 1.0f);

        Put(f, c.F("run.floor"), 0, Clip01(state.TotalFloor / 50f));
    }

    // ── potion belt ──────────────────────────────────────────────────────────────────────
    private static bool WritePotions(Contract c, float[] f, long[] iarr, SessionState session, Player player)
    {
        var idsSlice = c.I("run.potions.ids");
        var fSlice = c.F("run.potions.f");
        var potions = player.PotionSlots;
        int n = potions.Count;
        bool truncated = n > MaxPotionSlots;
        int written = Math.Min(n, MaxPotionSlots);

        for (int p = 0; p < written; p++)
        {
            var potion = potions[p];
            if (potion != null)
                PutI(iarr, idsSlice, p, Vocab(c, session, "potions", potion.Id.ToString()));
            Put(f, fSlice, p * 2 + 0, potion != null ? 1f : 0f);
            // "slot exists": Player.PotionSlots.Count IS Player.MaxPotionCount (Player.cs:42
            // `MaxPotionCount => _potionSlots.Count`) — every slot within the written range
            // trivially exists, matching python's `p < run.max_potions`.
            Put(f, fSlice, p * 2 + 1, 1f);
        }
        return truncated;
    }

    // ── shared card-instance row (deck / reward cards / select candidates) ─────────────
    //
    // Ports full_env.card_instance_row verbatim: `(pile_id, card_id, affliction_id,
    // enchantment_id)` ints, `(upgrade, effective_cost, affliction_amount,
    // exhaust_on_next_play)` floats. `pileId` is always PAD (0) for every run-side caller —
    // there is no pile concept outside combat, matching run_env._run_card_row. Cost is the
    // card's upgrade-tracking printed cost (no live CombatState to hook-modify through) —
    // same v7 REDEFINE rule CombatObsWriter.WriteCards already applies to non-Hand piles.
    // Sim source: sts2_rl/run_env.py `_run_card_row` (~line 623-657) uses
    // `card.canonical_energy_cost`, which mirrors GetWithModifiers(CostModifiers.None)
    // (CardEnergyCost.cs:94-121) — NOT the frozen `.Canonical` field (CardEnergyCost.cs:31,
    // 82-88), which never reflects upgrades (UpgradeBy mutates `_base` only, CardEnergyCost.cs
    // :351-372).
    // Internal (not private): shared with DecisionLists.SelectCandidates (Fix 6, round 4) so
    // the select-candidate sort key used there is byte-identical to the one used here for
    // run.deck/reward.cards/select.candidates — see DecisionLists.SelectCandidates's doc
    // comment for why a SEPARATE re-derivation would silently risk disagreeing on order.
    internal static (int[] ints, float[] floats) CardInstanceRow(Contract c, SessionState session, CardModel card, int pileId)
    {
        int cardId = (int)Vocab(c, session, "cards", card.Id.ToString());
        int afflId = card.Affliction != null ? (int)Vocab(c, session, "afflictions", card.Affliction.Id.ToString()) : 0;
        int enchId = card.Enchantment != null ? (int)Vocab(c, session, "enchantments", card.Enchantment.Id.ToString()) : 0;
        float upgrade = Clip01(card.CurrentUpgradeLevel / 5f);
        float cost = Clip01((float)card.EnergyCost.GetWithModifiers(CostModifiers.None) / 6f);
        float afflAmt = card.Affliction != null ? Clip01(card.Affliction.Amount / 10f) : 0f;
        float exhaustNext = card.ExhaustOnNextPlay ? 1f : 0f;
        return (new[] { pileId, cardId, afflId, enchId }, new[] { upgrade, cost, afflAmt, exhaustNext });
    }

    internal static int CompareRow((int[] ints, float[] floats) a, (int[] ints, float[] floats) b)
    {
        for (int i = 0; i < a.ints.Length; i++)
        {
            int cmp = a.ints[i].CompareTo(b.ints[i]);
            if (cmp != 0) return cmp;
        }
        for (int i = 0; i < a.floats.Length; i++)
        {
            int cmp = a.floats[i].CompareTo(b.floats[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    // ── deck (R2 rows, SORTED — a multiset, order-independent; no action addresses a deck
    //    row directly, so this is free to match run_env's own sort=True exactly) ──────────
    private static bool WriteDeck(Contract c, float[] f, long[] iarr, SessionState session, Player player)
    {
        var idsSlice = c.I("run.deck.ids");
        var fSlice = c.F("run.deck.f");

        var rows = player.Deck.Cards.Select(card => CardInstanceRow(c, session, card, pileId: 0)).ToList();
        rows.Sort(CompareRow);

        int n = rows.Count;
        bool truncated = n > MaxDeckRows;
        int written = Math.Min(n, MaxDeckRows);
        for (int r = 0; r < written; r++)
            WriteRow(iarr, f, idsSlice, fSlice, r, rows[r]);
        return truncated;
    }

    private static void WriteRow(long[] iarr, float[] f, LayoutSlice idsSlice, LayoutSlice fSlice, int row, (int[] ints, float[] floats) data)
    {
        for (int k = 0; k < data.ints.Length; k++) PutI(iarr, idsSlice, row * data.ints.Length + k, data.ints[k]);
        for (int k = 0; k < data.floats.Length; k++) Put(f, fSlice, row * data.floats.Length + k, data.floats[k]);
    }

    // ── relics ───────────────────────────────────────────────────────────────────────────
    //
    // FIX 5 (round 4) — ported from sts2_rl/relic_obs.py's per-id table via RelicObsTable
    // (see that class's doc comment for the full port rationale), replacing the old GENERIC
    // ShowCounter/DisplayAmount/Status rule the round-4 diagnosis found diverging in two
    // independent ways (diag4-runlevel.md finding 1). `inCombat: false` here is deliberate,
    // not a bug — sts2_rl/run_env.py:1097 calls `relic_row(relic, in_combat=False)`
    // UNCONDITIONALLY for the run.relics block, even on a live Combat-kind decision (the
    // run-level block always represents the "outside combat" view; combat.player.relics.f
    // below is the correctly-live-gated combat counterpart, CombatObsWriter.WriteRelics).
    private static bool WriteRelics(Contract c, float[] f, long[] iarr, SessionState session, Player player)
    {
        var idsSlice = c.I("run.relics.ids");
        var fSlice = c.F("run.relics.f");
        var relics = player.Relics;
        int n = relics.Count;
        bool truncated = n > MaxRelicRows;
        int written = Math.Min(n, MaxRelicRows);

        for (int r = 0; r < written; r++)
        {
            var relic = relics[r];
            PutI(iarr, idsSlice, r, Vocab(c, session, "relics", relic.Id.ToString()));

            var (counter, flag) = RelicObsTable.Row(relic, inCombat: false);

            Put(f, fSlice, r * 2 + 0, counter / 10f);
            Put(f, fSlice, r * 2 + 1, flag);
        }
        return truncated;
    }

    // ── boss identity ────────────────────────────────────────────────────────────────────
    //
    // ActModel._rooms is a protected field with no public accessor — reflected into once,
    // same convention as every other "non-public engine field" reflection in this codebase.
    // OPEN QUESTION: EncounterModel.AllPossibleMonsters ("every monster class this encounter
    // COULD spawn") is used as the proxy for sts2-rl's `monster_classes` (the EXACT class
    // list a specific boss draws from) — these are not confirmed identical; a boss with
    // spawn-time variants could report more ids here than sts2-rl's vocabulary would for the
    // same encounter. Flagged for Task 16's parity harness, not resolved here.
    private static readonly FieldInfo? RoomsField =
        typeof(MegaCrit.Sts2.Core.Models.ActModel).GetField("_rooms", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool WriteBoss(Contract c, long[] iarr, SessionState session, IRunState state)
    {
        var idsSlice = c.I("run.boss.ids");
        var rows = new List<long>();
        try
        {
            var act = state.Act;
            if (act != null && RoomsField?.GetValue(act) is MegaCrit.Sts2.Core.Rooms.RoomSet roomSet)
            {
                var boss = roomSet.NextBossEncounter;
                if (boss != null)
                {
                    foreach (var monster in boss.AllPossibleMonsters)
                    {
                        long vocabId = Vocab(c, session, "monsters", monster.Id.ToString());
                        if (vocabId != 0)
                            rows.Add(vocabId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] RunObsWriter.WriteBoss: failed to resolve the act's boss encounter (non-fatal, leaving PAD): {ex.Message}");
        }

        bool truncated = rows.Count > MaxBossIds;
        int written = Math.Min(rows.Count, MaxBossIds);
        for (int r = 0; r < written; r++)
            PutI(iarr, idsSlice, r, rows[r]);
        return truncated;
    }

    // ── map: action-aligned 1-ply slots ──────────────────────────────────────────────────
    private static void WriteMapSlots(Contract c, float[] f, DecisionContext ctx, IRunState state)
    {
        if (ctx.Kind != DecisionKind.Map) return;
        var map = state.Map;
        if (map == null) return;

        // DecisionLists.MapMoves: the SAME Col-sorted list ActionMap.BuildMap binds to
        // CHOICE_BASE+m (Task 13) — slot m here and that action always name the same point.
        var moves = DecisionLists.MapMoves(ctx);

        // Round-7 fix: resolve each slot's point directly off the CURRENT point's own
        // `Children` (Col-sorted, matching `moves`'s own Col order) instead of re-deriving a
        // single shared `row = CurrentMapCoord.row + 1` and looking it up via
        // `map.GetPoint(col, row)`. That re-derivation is the sim's `run.travelable_points()`
        // in spirit (`sorted(current_point.children, key=(col,row))`,
        // sts2_rl/run.py:1422-1432) but implemented as an independent grid lookup rather than
        // reading the edge itself — whenever `row` disagreed with the point's ACTUAL row (the
        // observed divergence: 87/79/74/47/27/6 mismatches across map0-5, traced to act1
        // floor9 Map idx0 in the 89U dump, where sim's 5 travelable children shrank to 2
        // populated game slots and even those 2 carried the WRONG MapPointType — idx6 vs sim's
        // idx5 for slot 0, idx5 vs sim's idx3 for slot 1 — a pattern only explained by
        // `GetPoint(col, row)` hitting an unrelated node at the wrong row rather than any
        // PointType enum mismatch, since a real enum mismatch would shift every slot by the
        // SAME constant instead of a different amount each time), `GetPoint` either silently
        // returned null (an all-zero slot for a real, present child) or returned a real node
        // from the WRONG row that merely happens to share that column (a present slot with a
        // garbage PointType/children histogram). Reading `Children` directly can never pick the
        // wrong row — the edge already names the exact point `moves[m]` is going to travel to.
        var currentPoint = state.CurrentMapPoint;
        // MapTravel.GetTravelablePointsFrom (Core/Map/MapTravel.cs) is the game's authoritative
        // travel rule: normally the point's own Children, but the WHOLE next row when a
        // free-travel effect (e.g. Winged Boots) is active. Reading `.Children` directly used to
        // silently drop free-travel destinations, producing an obs/mask mismatch against the sim
        // (which already mirrors GetTravelablePointsFrom) and illegally denying legal live-bot moves.
        //
        // PRE-BOSS ROW FIX: MapTravel.GetTravelablePointsFrom is NOT the whole story —
        // NMapScreen.RecalculateTravelability (NMapScreen.cs:401-405) special-cases the row
        // right before the boss: `if (mapCoord.row == map.GetRowCount() - 1) { bossPointNode.State
        // = Travelable; return; }`, bypassing Children/MapTravel entirely. This matters because
        // not every ActMap generator wires the boss point into every last-row point's Children —
        // e.g. GoldenPathActMap.Build (GoldenPathActMap.cs:66) only adds it as a child of ONE
        // last-row point (Grid[3, ...]), so a plain Children/GetTravelablePointsFrom read misses
        // the boss destination for every other last-row point. Missing this produced the map0
        // idx0 (present) mismatches at act0 floor16 / act1 floor15. Mirrored exactly here: at the
        // pre-boss row the ONLY travelable point is the boss node itself.
        List<MapPoint> children;
        if (currentPoint != null && map.GetRowCount() > 0 && currentPoint.coord.row == map.GetRowCount() - 1)
        {
            children = new List<MapPoint> { map.BossMapPoint };
        }
        else
        {
            children = currentPoint != null
                ? MapTravel.GetTravelablePointsFrom(state, currentPoint).OrderBy(p => p.coord.col).ToList()
                : new List<MapPoint>();
        }

        for (int m = 0; m < moves.Count && m < MapSlots; m++)
        {
            MapPoint? point = null;
            if (m < children.Count && children[m].coord.col == moves[m].Col)
            {
                point = children[m];
            }
            else
            {
                // Fallback (should not fire when `moves` and `children` agree, per the doc
                // comment above) — look the move's column up among the current point's real
                // children rather than the whole grid row, so a mismatch here still can't
                // resolve to an unrelated node from a different row.
                point = children.FirstOrDefault(p => p.coord.col == moves[m].Col);
            }
            if (point == null) continue;

            var slice = c.F($"map{m}");
            Put(f, slice, 0, 1f); // present
            Put(f, slice, 1 + (int)point.PointType, 1f);

            var counts = new int[NPointTypes];
            foreach (var child in point.Children)
                counts[(int)child.PointType]++;
            for (int t = 0; t < NPointTypes; t++)
                if (counts[t] > 0)
                    Put(f, slice, 1 + NPointTypes + t, Clip01(counts[t] / 3f));
        }
    }

    // ── map: whole-act grid (visible every step, like the map screen) ───────────────────
    //
    // OPEN QUESTION: ActMap.GetRowCount()'s exact semantics (whether it includes the
    // off-grid Ancient row 0) were not confirmed from source within this task's budget —
    // this mirrors run_env._map_grid_block's `top = min(MAP_GRID_ROWS, act_map.map_length -
    // 1)` clamp using GetRowCount() in place of `map_length`; if that's off by one the grid's
    // topmost/bottommost row may be shifted, a cosmetic risk flagged for Task 16 rather than
    // guessed away.
    private static void WriteMapGrid(Contract c, float[] f, IRunState state)
    {
        var map = state.Map;
        if (map == null) return;

        var gridSlice = c.F("run.map.grid");
        int top = Math.Min(MapGridRows, map.GetRowCount() - 1);
        for (int row = 1; row <= top; row++)
        {
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point.coord.col < 0 || point.coord.col >= MapWidth) continue;
                int nb = ((row - 1) * MapWidth + point.coord.col) * MapGridNode;
                Put(f, gridSlice, nb, 1f);
                Put(f, gridSlice, nb + 1 + (int)point.PointType, 1f);
                foreach (var child in point.Children)
                {
                    if (child.coord.row < map.GetRowCount() && child.coord.col >= 0 && child.coord.col < MapWidth)
                        Put(f, gridSlice, nb + 1 + NPointTypes + child.coord.col, 1f);
                }
            }
        }

        var metaSlice = c.F("run.map.meta");
        var current = state.CurrentMapPoint;
        if (current != null)
        {
            if (current == map.StartingMapPoint)
                Put(f, metaSlice, 0, 1f);
            else if (current == map.BossMapPoint || current == map.SecondBossMapPoint)
                Put(f, metaSlice, 1, 1f);
            else if (current.coord.row >= 1 && current.coord.row <= MapGridRows
                     && current.coord.col >= 0 && current.coord.col < MapWidth)
            {
                int nb = ((current.coord.row - 1) * MapWidth + current.coord.col) * MapGridNode;
                Put(f, gridSlice, nb + MapGridNode - 1, 1f);
            }
        }
    }

    // ── event ────────────────────────────────────────────────────────────────────────────
    private static void WriteEvent(Contract c, float[] f, long[] iarr, SessionState session, DecisionContext ctx)
    {
        if (ctx.Kind != DecisionKind.Event) return;

        var sync = ReplayState.ActiveEventSynchronizer;
        var evt = sync != null && sync.Events.Count > 0 ? sync.Events[0] : null;
        if (evt == null) return;

        Put(f, c.F("event.present"), 0, 1f);
        PutI(iarr, c.I("event.ids"), 0, Vocab(c, session, "events", evt.Id.ToString()));

        // event.page — round-11 fix, closing round 3's OPEN QUESTION. sts2-rl's writer
        // (run_env.py:1185-1186) is `1.0 if event.page != "INITIAL" else 0.0`. Round 3
        // concluded the game retains no queryable page state, but it missed the one it
        // DOES retain: `EventModel.Description` (public get, EventModel.cs:87) is set by
        // every `SetEventState` call (EventModel.cs:563-572) to the CURRENT page's
        // description, and the INITIAL page's description is exactly the public
        // `InitialDescription` property (`.pages.INITIAL.description`, EventModel.cs:68,
        // assigned verbatim by `SetInitialEventState`, :267). Every page transition in
        // every concrete event flows through SetEventState with that page's own
        // `.pages.<PAGE>.description` LocString, so comparing L10N keys
        // (`LocString.LocEntryKey`, LocString.cs:22 — a fresh instance per property get,
        // hence key comparison, never reference equality) reproduces the sim's boolean:
        // still-initial ⇔ Description's key == InitialDescription's key. Fixed the 89U
        // (0,3) Event idx 1/2 residual (sim=1 game=0 after the first option advanced
        // the page).
        if (evt.Description?.LocEntryKey != evt.InitialDescription?.LocEntryKey)
            Put(f, c.F("event.page"), 0, 1f);

        var optSlice = c.F("event.options");
        var options = evt.CurrentOptions;
        for (int i = 0; i < options.Count && i < ChoiceSlots; i++)
        {
            Put(f, optSlice, 2 * i, 1f);
            if (options[i].IsLocked)
                Put(f, optSlice, 2 * i + 1, 1f);
        }
    }

    // ── shop ─────────────────────────────────────────────────────────────────────────────
    private static void WriteShop(Contract c, float[] f, long[] iarr, SessionState session, DecisionContext ctx)
    {
        if (ctx.Kind != DecisionKind.Shop) return;

        // DecisionLists.ShopEntries: the SAME flat entries list ActionMap.BuildShop indexes
        // for CHOICE_BASE+i (Task 13) — sliced by type below, preserving each entry's
        // relative position within that one list.
        var entries = DecisionLists.ShopEntries(ctx);
        if (entries == null) return;

        WriteShopCards(c, f, iarr, session, entries.OfType<MerchantCardEntry>().ToList());
        WriteShopRelics(c, f, iarr, session, entries.OfType<MerchantRelicEntry>().ToList());
        WriteShopPotions(c, f, iarr, session, entries.OfType<MerchantPotionEntry>().ToList());

        var removal = entries.OfType<MerchantCardRemovalEntry>().FirstOrDefault();
        if (removal != null && removal.IsStocked)
        {
            var slice = c.F("shop.removal");
            Put(f, slice, 0, 1f);
            Put(f, slice, 1, Log1pScale(removal.Cost, ShopCostLogDenom));
            Put(f, slice, 2, removal.EnoughGold ? 1f : 0f);
        }
    }

    private static void WriteShopCards(Contract c, float[] f, long[] iarr, SessionState session, List<MerchantCardEntry> entries)
    {
        var idsSlice = c.I("shop.cards.ids");
        var fSlice = c.F("shop.cards.f");
        for (int i = 0; i < ShopCardSlots && i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.IsStocked || e.CreationResult?.Card == null) continue; // explicit PAD row
            PutI(iarr, idsSlice, i, Vocab(c, session, "cards", e.CreationResult.Card.Id.ToString()));
            Put(f, fSlice, i * 4 + 0, 1f);
            Put(f, fSlice, i * 4 + 1, Log1pScale(e.Cost, ShopCostLogDenom));
            Put(f, fSlice, i * 4 + 2, e.EnoughGold ? 1f : 0f);
            Put(f, fSlice, i * 4 + 3, e.IsOnSale ? 1f : 0f);
        }
    }

    private static void WriteShopRelics(Contract c, float[] f, long[] iarr, SessionState session, List<MerchantRelicEntry> entries)
    {
        var idsSlice = c.I("shop.relics.ids");
        var fSlice = c.F("shop.relics.f");
        for (int i = 0; i < ShopRelicSlots && i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.IsStocked || e.Model == null) continue;
            PutI(iarr, idsSlice, i, Vocab(c, session, "relics", e.Model.Id.ToString()));
            Put(f, fSlice, i * 3 + 0, 1f);
            Put(f, fSlice, i * 3 + 1, Log1pScale(e.Cost, ShopCostLogDenom));
            Put(f, fSlice, i * 3 + 2, e.EnoughGold ? 1f : 0f);
        }
    }

    private static void WriteShopPotions(Contract c, float[] f, long[] iarr, SessionState session, List<MerchantPotionEntry> entries)
    {
        var idsSlice = c.I("shop.potions.ids");
        var fSlice = c.F("shop.potions.f");
        for (int i = 0; i < ShopPotionSlots && i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.IsStocked || e.Model == null) continue;
            PutI(iarr, idsSlice, i, Vocab(c, session, "potions", e.Model.Id.ToString()));
            Put(f, fSlice, i * 3 + 0, 1f);
            Put(f, fSlice, i * 3 + 1, Log1pScale(e.Cost, ShopCostLogDenom));
            Put(f, fSlice, i * 3 + 2, e.EnoughGold ? 1f : 0f);
        }
    }

    // ── reward classification (shared by WritePhase and WriteReward) ───────────────────
    //
    // C#'s ReplayDispatcher shows every reward item (cards/potion/relic/gold/chest) on one
    // mixed-type button screen; sts2-rl asks one DecisionKind at a time. This picks the
    // single sub-kind this decision "is" for both the phase one-hot and the reward.* block,
    // by priority: an open card-selection sub-screen (REWARD_CARD) first, else the first
    // RelicReward button (REWARD_RELIC), else the first PotionReward button (REWARD_POTION),
    // else None (a gold/chest-only screen sts2-rl has no matching decision kind for at all).
    private static RewardSubKind ClassifyReward(
        DecisionContext ctx,
        out List<CardModel?> cardModels,
        out List<TakeCardCommand> takeCards,
        out (Node button, object reward)? potionButton,
        out (Node button, object reward)? relicButton)
    {
        takeCards = DecisionLists.RewardTakeCards(ctx);
        cardModels = takeCards.Count > 0 ? DecisionLists.RewardCardModels(ctx) : new List<CardModel?>();
        potionButton = null;
        relicButton = null;

        if (takeCards.Count > 0)
            return RewardSubKind.Card;

        // IsInsideTree() alone is not enough: ActiveRewardsScreen keeps pointing at the node
        // after it is freed, and touching a freed node throws ObjectDisposedException (seen on
        // every post-combat reward decision). IsInstanceValid must be checked first.
        var screen = ReplayState.ActiveRewardsScreen;
        if (screen != null && GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
        {
            // Fix (reward.relic/reward.potion content bug) — when the CLAIMED button's own
            // index is known (ctx.RewardButtonIndexOverride, PassiveDump-only), read THAT
            // button directly rather than "first RelicReward/PotionReward found by scanning
            // the whole screen" below. The first-found heuristic silently returns the wrong
            // button's model on any screen offering more than one item of the same reward
            // type, or where an already-claimed button lingers un-removed from the tree —
            // both produce a stale/adjacent relic id instead of the one actually offered at
            // this decision (ground-truthed on 89U: dumped ids 143/96/59/50/123 vs.
            // actually-offered 121/(skip)/111/157/(skip)). See
            // DecisionContext.RewardButtonIndexOverride's doc for the full trace.
            if (ctx.RewardButtonIndexOverride is int idx)
            {
                var buttons = ClaimRewardCommand.EnumerateRewardButtons(screen).ToList();
                if (idx >= 0 && idx < buttons.Count)
                {
                    var claimed = buttons[idx];
                    if (claimed.reward is RelicReward crr && crr.Relic != null)
                        relicButton = claimed;
                    else if (claimed.reward is PotionReward cpr && cpr.Potion != null)
                        potionButton = claimed;
                }
            }

            if (relicButton == null && potionButton == null)
            {
                foreach (var pair in ClaimRewardCommand.EnumerateRewardButtons(screen))
                {
                    if (relicButton == null && pair.reward is RelicReward rr && rr.Relic != null)
                        relicButton = pair;
                    if (potionButton == null && pair.reward is PotionReward pr && pr.Potion != null)
                        potionButton = pair;
                }
            }
        }

        // Round 8 fix 1 — PassiveDump resolves the CLAIMED button's own reward type from the
        // dispatched ClaimRewardCommand (DecisionContext.RewardKindOverride's doc) and hands
        // it in as ctx.RewardKindOverride; prefer it over screen priority whenever it names a
        // button this method actually found on screen. A multi-item reward screen (e.g.
        // Gold/Potion/Relic offered together) previously always reported Relic here — the
        // first-found button by priority — even when the decision being dumped was for the
        // POTION claim, because both buttons are simultaneously present on the same screen.
        // Falls through to the old priority heuristic (live-bot BotController path, or any
        // fail-open null override) unchanged.
        switch (ctx.RewardKindOverride)
        {
            case RewardKind.Relic when relicButton != null: return RewardSubKind.Relic;
            case RewardKind.Potion when potionButton != null: return RewardSubKind.Potion;
            case RewardKind.Card: return RewardSubKind.Card; // takeCards.Count > 0 already returned above when real
            case RewardKind.None: return RewardSubKind.None;
        }

        if (relicButton != null) return RewardSubKind.Relic;
        if (potionButton != null) return RewardSubKind.Potion;
        return RewardSubKind.None;
    }

    // ── reward ───────────────────────────────────────────────────────────────────────────
    // Round-5/round-6 fix: gate strictly on ctx.Kind == RewardScreen (matches run_env.py's own
    // `rewards = request.rewards if ... request.kind in (REWARD_CARD, REWARD_POTION) else None`
    // gate at run_env.py:1250-1252 — reward.* is only ever populated on a reward-ask decision).
    // ClassifyReward's `ReplayState.ActiveRewardsScreen` lookup can still resolve a stale-but-
    // IsInstanceValid/IsInsideTree screen reference on the VERY NEXT decision after the reward
    // screen closes (e.g. the Map line the player lands on right after claiming/skipping), which
    // previously left WriteReward populating reward.potion.ids/f (and would do the same for
    // reward.relic.*) off that stale screen even though the live decision is no longer a reward
    // ask at all. Checking ctx.Kind here — not just relying on WritePhase's existing per-call
    // rewardKind gate, which only ever controlled the phase one-hot, never this block's own
    // writes — zeroes/PADs reward.* on every non-RewardScreen decision, regardless of whether
    // ActiveRewardsScreen is still sitting around un-freed.
    private static void WriteReward(
        Contract c, float[] f, long[] iarr, SessionState session, DecisionContext ctx,
        RewardSubKind kind, List<TakeCardCommand> takeCards, List<CardModel?> cardModels,
        (Node button, object reward)? potionButton, (Node button, object reward)? relicButton)
    {
        if (ctx.Kind != DecisionKind.RewardScreen)
            return;

        if (kind == RewardSubKind.Card)
        {
            var idsSlice = c.I("reward.cards.ids");
            var fSlice = c.F("reward.cards.f");
            // Positional (the action space picks a reward by choice slot index) — an
            // unresolved candidate is left as an explicit PAD row, matching run_env's own
            // "None card is None card" positional rule for reward.cards.
            for (int i = 0; i < RewardCardSlots; i++)
            {
                if (i >= cardModels.Count || cardModels[i] == null) continue;
                var row = CardInstanceRow(c, session, cardModels[i]!, pileId: 0);
                WriteRow(iarr, f, idsSlice, fSlice, i, row);
            }
        }
        else if (kind == RewardSubKind.Potion && potionButton is { reward: PotionReward pr } && pr.Potion != null)
        {
            PutI(iarr, c.I("reward.potion.ids"), 0, Vocab(c, session, "potions", pr.Potion.Id.ToString()));
            Put(f, c.F("reward.potion.f"), 0, 1f);
        }
        else if (kind == RewardSubKind.Relic && relicButton is { reward: RelicReward rr } && rr.Relic != null)
        {
            long vocabId = Vocab(c, session, "relics", rr.Relic.Id.ToString());
            if (vocabId != 0)
            {
                PutI(iarr, c.I("reward.relic.ids"), 0, vocabId);
                Put(f, c.F("reward.relic.f"), 0, 1f);
            }
        }
    }

    // ── select ───────────────────────────────────────────────────────────────────────────
    //
    // FIX 6 (round 4) — now MATCHES run_env.py: sts2-rl sorts select.candidates
    // (`_select_order`, run_env.py:1001-1008 — `(tuple(rows[i][0]), tuple(rows[i][1]))`, i.e.
    // by each candidate's OWN content-derived row, ints then floats). DecisionLists.
    // SelectCandidates now builds that SAME sorted order ONCE (via RunObsWriter.CardInstanceRow/
    // CompareRow, the identical row-builder run.deck/reward.cards already use) and hands it to
    // BOTH this obs-writing loop AND ActionMap.BuildSelectCards, so SELECT_BASE+i always binds
    // to candidates[i] in the SAME order this method writes row i — see DecisionLists.
    // SelectCandidates's doc comment for the single-source-of-truth reasoning.
    private static bool WriteSelect(Contract c, float[] f, long[] iarr, SessionState session, DecisionContext ctx)
    {
        if (ctx.Kind != DecisionKind.SelectCards)
            return false; // SelectOption: no candidate source exists yet (ActionMap has none either)

        var (candidates, skip) = DecisionLists.SelectCandidates(ctx, c, session);

        // Round-13 skippable fix: `skip != null` only detects a separate skip/confirm
        // COMMAND among Available — the atomic SelectGridCardCommand shape (one command
        // carrying every pick) has no such sibling, so a genuinely declinable grid
        // (MinSelect 0, e.g. 933T (2,1)'s ancient-shrine "up to 5" pick — sim
        // skippable=1) read 0. The grid's own CardSelectorPrefs carries the truth:
        // sim's `_card_selector` asks skippably exactly when picks-so-far >= MinSelect,
        // which at the first (and only game-dumped) ask is MinSelect == 0.
        bool selectSkippable = skip != null;
        var gridScreenForPrefs = CardGridScreenCapture.ActiveScreen;
        if (gridScreenForPrefs != null
            && CardGridScreenCapture.GetPrefs(gridScreenForPrefs) is { } skipPrefs
            && skipPrefs.MinSelect == 0)
        {
            selectSkippable = true;
        }
        Put(f, c.F("select.skippable"), 0, selectSkippable ? 1f : 0f);

        // select.count (FIX 1, closes the prior OPEN QUESTION): sts2-rl's request.count_remaining
        // (run_env.py:1321, `_clip01(request.count_remaining / 5.0)`) is driver.py's
        // `_card_selector`'s `count - i` (driver.py:399) — at i=0, the first (and, per the
        // observed mismatches, only live) ask of a screen, that's just the screen's full
        // pick-count, i.e. CardSelectorPrefs.MaxSelect. CardGridScreenCapture.GetPrefs reflects
        // the ACTIVE grid screen's own `_prefs` field (every NCardGridSelectionScreen subclass —
        // NSimpleCardSelectScreen et al. — carries one; see that method's doc comment) — the
        // SAME CardSelectorPrefs CardSelectCmd built from its caller's MinSelect/MaxSelect.
        var gridScreen = CardGridScreenCapture.ActiveScreen;
        if (gridScreen != null && CardGridScreenCapture.GetPrefs(gridScreen) is { } prefs)
        {
            Put(f, c.F("select.count"), 0, Clip01(prefs.MaxSelect / 5f));
        }
        //
        // select.purpose.ids: PARTIALLY CLOSED (round-4 re-research, DecisionLists.SelectPurpose).
        // Reason (1) from the prior note is FIXED — Contract.LoadVocab/VocabTableId (round-3
        // fix 9) parses contract.json's "vocab.purposes" table, so a purpose string CAN be
        // turned into an id (Contract.VocabTableId("purposes", name)) once one is known.
        // Reason (2), the real blocker, is that sts2-rl's request.purpose (run_env.py:1284-1289,
        // PURPOSE_IDS at run_env.py:351-370) is set by driver.py PER CALL SITE based on which
        // game mechanic opened the selection screen, not by anything the live selection screen
        // itself generically exposes — ReplayDispatcher's SelectHandCardsCommand/
        // SelectGridCardCommand/SelectCardFromScreenCommand carry no "why was this screen
        // opened" field, and CardSelectCmd.cs (the command that opens these screens) has no
        // generic Purpose-shaped property. BUT two of the out-of-combat grid screens ARE
        // unambiguous by their own concrete C# Type, independent of any Purpose field:
        // CardSelectCmd.cs's FromDeckForUpgrade is the ONLY caller of
        // NDeckUpgradeSelectScreen.ShowScreen (-> purpose "upgrade"), and FromDeckForRemoval's
        // only path is through FromDeckGeneric -> NDeckCardSelectScreen.Create (-> purpose
        // "remove"; CardSelectCmd.cs has no other FromDeckGeneric call site). DecisionLists.
        // SelectPurpose keys off the active screen's Type for exactly these two; every other
        // grid-screen type (NSimpleCardSelectScreen, NCombatPileCardSelectScreen,
        // NDeckTransformSelectScreen, NDeckEnchantSelectScreen) is genuinely multi-purpose with
        // no single-purpose screen type to key off, and stays a structural PAD — resolving those
        // still needs the full per-mechanism classification table, out of this fix's scope.
        var purposeName = DecisionLists.SelectPurpose(ctx);
        if (purposeName != null)
        {
            int purposeId = c.VocabTableId("purposes", purposeName);
            if (purposeId != 0)
                PutI(iarr, c.I("select.purpose.ids"), 0, purposeId);
        }

        var idsSlice = c.I("select.candidates.ids");
        var fSlice = c.F("select.candidates.f");
        int n = candidates.Count;
        bool truncated = n > MaxSelectCandidates;
        int written = Math.Min(n, MaxSelectCandidates);
        for (int i = 0; i < written; i++)
        {
            var card = candidates[i].Card;
            if (card == null) continue; // unresolved candidate — PAD row; the action id may still be legal via ActionMap
            var row = CardInstanceRow(c, session, card, pileId: 0);
            WriteRow(iarr, f, idsSlice, fSlice, i, row);
        }
        return truncated;
    }
}
