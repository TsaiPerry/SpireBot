using System;
using System.Collections.Generic;
using System.Linq;

using Godot;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.ValueProps;

namespace SpireBot.SpireBotCode.Obs;

/// <summary>
/// Writes the combat half of the observation (every <c>combat.*</c> segment) from a
/// live <see cref="ICombatState"/>, per the schema audit
/// (<c>docs/superpowers/specs/2026-08-04-spirebot-schema-audit.md</c>) and sts2-rl's
/// <c>full_env.py</c> (<c>combat_obs_segments_i/_f</c>, <c>write_combat_obs</c>, ...),
/// which this type mirrors segment-for-segment. Every field write goes through
/// <see cref="Put"/>/<see cref="PutI"/>, which look up the segment's
/// <see cref="LayoutSlice"/> from <see cref="Contract"/> ONCE per segment and then
/// bounds-check every row/field index against that slice's own width — no numeric
/// offsets appear anywhere below, and a writer that steps outside its own segment
/// throws <see cref="ObsBuilderException"/> immediately (a bug, never silently
/// corrupts a neighboring segment).
///
/// Data sources are the SAME live objects the rest of the mod already reads from one
/// <see cref="DecisionContext"/> capture (per this task's brief): hand order comes from
/// <c>PlayerCombatState.Hand.Cards</c> — the exact list <see cref="GameStateSnapshot.Hand"/>
/// was built from, moments earlier, in the same synchronous capture — and enemy slot
/// indices come from <see cref="DecisionContext.EncounterEnemies"/> (raw encounter
/// order, dead slots included), the same array <see cref="ActionMap"/> uses for its own
/// target-index math. <see cref="GameStateSnapshot"/>'s own <c>CardInfo</c>/<c>EnemyInfo</c>
/// records are NOT used here — they don't carry the live <c>CardModel</c>/<c>Creature</c>
/// references this writer needs for dynamic-var previews and power/relic state, so this
/// re-reads the live piles/roster directly instead (documented rather than silent: see
/// the class doc's "KNOWN CARRY-FORWARDS" note in the task brief for the analogous
/// enemy-ModelId gap this closes in <see cref="DecisionContext"/> itself).
/// </summary>
public static class CombatObsWriter
{
    private const float AbsScale = 100f;
    private const float AbsScaleCoarse = 500f;

    private const int MaxHand = 10;
    private const int MaxEnemies = 6;
    private const int MaxPotionRows = 10;
    private const int MaxPowersPlayer = 32;
    private const int MaxPowersEnemy = 16;
    private const int MaxRelicRows = 48;
    private const int MaxCombatCards = 96;
    private const int NCardFeatures = 31;

    /// <summary>Column of the hand.f card row holding clip01(CurrentUpgradeLevel / 5) —
    /// full_env.CARD_UPGRADE_FEATURE. Public so OnnxPolicy's duplicate-merging decoder keys on
    /// the SAME cell this writer fills (WriteCardFeatures).</summary>
    public const int CardUpgradeFeature = 16;

    // Round 6 fix 4 — see the S(23, ...) magic_number scan in WriteCardFeatures for the
    // full citation. Priority order copied verbatim from sts2_rl/cards/base.py's
    // _MAGIC_ATTRS, restricted to the subset with a confirmed 1:1 DynamicVarSet key.
    // Round-7 field-23 fix: the four names below were wrong outright — PowerVar<T>
    // (DynamicVars/PowerVar.cs:9-19) keys itself by `typeof(T).Name`, the POWER MODEL
    // CLASS's literal name (VulnerablePower.cs, WeakPower.cs, FrailPower.cs,
    // StrengthPower.cs), not the shorthand "Vulnerable"/"Weak"/"Frail"/"Strength" the
    // round-6 comment assumed (that shorthand is a `DynamicVars` wrapper *property*
    // name, not the underlying dict key) — confirmed by reading Bash.cs/Taunt.cs/
    // Dominate.cs/Tremble.cs/DemonForm.cs, which all resolve via
    // `DynamicVars["VulnerablePower"|"StrengthPower"]`. "Power" is a separate,
    // confirmed-safe literal key: Uppercut.cs/FeelNoPain.cs/Rage.cs/Corruption.cs/
    // Stampede.cs/Shockwave.cs (colorless_skills.py:729) all register a plain
    // `DynamicVar("Power", n)` and their sts2-rl counterparts all read it back via
    // `_power`/`_power_amount` (cards/{uppercut,feel_no_pain_card,rage,corruption,
    // stampede}.py, colorless_skills.py) — the two sim attr names collapse to one
    // game-side key, which is fine since no single card sets both. "InfernoPower" is
    // Inferno.cs's own `PowerVar<InfernoPower>` key, matching sts2_rl/cards/inferno.py's
    // `_power_amount`. "PlatingPower" is StoneArmor.cs's `PowerVar<PlatingPower>` key,
    // matching sts2_rl/cards/stone_armor.py's `_plating`.
    private static readonly string[] MagicNumberPriority =
    {
        "VulnerablePower", "WeakPower", "FrailPower", "StrengthPower",
        // "CrueltyPower" (round 14): Cruelty.cs's `PowerVar<CrueltyPower>(25m)` —
        // sim `_power_amount` (cards/cruelty.py:29); 933T (2,15) hand.f field-23:
        // sim 25/20→1.0 clipped, game 0.
        "Power", "InfernoPower", "PlatingPower", "CrueltyPower",
        // "ExtraBlock" (round 13): Fasten.cs's own literal key (`DynamicVar("ExtraBlock",
        // 4m)`, upgraded +2) — game-wide grep shows Fasten is its ONLY user, so no
        // allowlist gate is needed; sts2-rl mirrors it as FastenCard._extra
        // (colorless_powers.py:139), the same `_extra` slot ExtraDamage occupies for
        // its cards, hence the same priority position (933T hand.f field-23: sim
        // 4/20=0.2 → upgraded 6/20=0.3, game read 0).
        "Cards", "Energy", "Heal", "ExtraDamage", "ExtraBlock", "Increase", "Attacks", "Repeat",
    };

    // "ExtraDamage" (ExtraDamageVar.cs:14, defaultName/ctor both hardcode "ExtraDamage")
    // and "Repeat" (RepeatVar) are each shared by 20+ cards game-side, but sts2-rl's
    // magic_number only mirrors a handful of them via `_extra`/`_repeat`
    // (grep of sts2_rl/cards for `self._extra =` / `self._repeat =`) — scanning either
    // key unconditionally over-matches every OTHER card that also carries that var
    // (round-7 field-23 diag: Conflagration/TearAsunder both carry a "Repeat" var with
    // no sim-side `_repeat`, producing a false nonzero magic_number that doesn't exist
    // in sts2-rl). Gate both keys to the confirmed card ids whose sts2-rl class actually
    // sets the mirrored attr.
    private static readonly HashSet<string> ExtraDamageMagicNumberCardIds = new(StringComparer.Ordinal)
    {
        "CARD.BULLY", "CARD.REND", "CARD.PERFECTED_STRIKE", "CARD.ASHEN_STRIKE",
    };
    private static readonly HashSet<string> RepeatMagicNumberCardIds = new(StringComparer.Ordinal)
    {
        "CARD.SPITE",
    };

    // Round-7 field-19 fix: sts2-rl's `base_hits` (cards/base.py:387-390) is
    // `getattr(self, "_hits", 1)` — a Python-side hardcoded instance attribute, NOT
    // derived from any game DynamicVar. Thrash is the one hand-reachable card in this
    // family where that hardcoded count (`_hits = 2`, sts2_rl/cards/thrash.py:38)
    // has no game-side var at all to scan: Thrash.cs's OnPlay calls
    // `DamageCmd.Attack(...).WithHitCount(2)` directly (Thrash.cs:45), never
    // registering a Repeat/HitCount DynamicVar, so the vars-dictionary scan below
    // structurally can't see it. Mirror sts2-rl's hardcoded constant by card id
    // instead of guessing at a nonexistent var.
    private static readonly Dictionary<string, float> HardcodedBaseHitsByCardId = new(StringComparer.Ordinal)
    {
        ["CARD.THRASH"] = 2f,
        // Round-13: Maul's double hit is `WithHitCount(2)` hardcoded in OnPlay
        // (Maul.cs:48), no DynamicVar — same class as Thrash; sts2-rl mirrors it
        // as `_hits = 2` (cards/maul: _init_vars). 933T (2,6) hand.f field-19:
        // sim 2/10=0.2 vs game 1/10=0.1 on every Maul in hand.
        ["CARD.MAUL"] = 2f,
    };
    private const int NEnemyScalars = 25;
    private const int MaxIntentHistory = SessionState.MaxIntentHistory; // 3
    private const int NEnemyHistoryScalars = 15;

    // ── numeric helpers (mirror full_env.py's _clip01/_signed/_abs2 exactly) ──
    private static float Clip01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
    private static float Clip01(decimal x) => Clip01((float)x);
    private static float Signed(float x, float cap) => Clip01((x + cap) / (2f * cap));
    private static (float fine, float coarse) Abs2(float x) => (Clip01(x / AbsScale), Clip01(x / AbsScaleCoarse));

    // ── bounds-checked segment writers ─────────────────────────────────────
    private static void Put(float[] f, LayoutSlice slice, int idx, float v)
    {
        if ((uint)idx >= (uint)slice.Width)
            throw new ObsBuilderException($"CombatObsWriter: float index {idx} out of range (width {slice.Width}) at offset {slice.Offset}.");
        f[slice.Offset + idx] = v;
    }

    private static void PutI(long[] i, LayoutSlice slice, int idx, long v)
    {
        if ((uint)idx >= (uint)slice.Width)
            throw new ObsBuilderException($"CombatObsWriter: int index {idx} out of range (width {slice.Width}) at offset {slice.Offset}.");
        i[slice.Offset + idx] = v;
    }

    private static long Vocab(Contract c, SessionState session, string kind, string? gameId)
    {
        if (gameId == null) return 0;
        int id = c.VocabId(kind, gameId);
        if (id == 0 && session.ShouldLogUnmapped(kind, gameId))
            GD.Print($"[SpireBot] CombatObsWriter: unmapped {kind} game id '{gameId}' (contract has no vocab entry) — writing PAD.");
        return id;
    }

    /// <summary>Writes every <c>combat.*</c> segment, or does nothing if there is no live
    /// combat (leaves the whole block at <see cref="Obs"/>'s zero-fill) — matching the sim,
    /// which always zeroes combat.* for a non-Combat-kind decision.</summary>
    public static void Write(Contract c, Obs obs, DecisionContext ctx, SessionState session)
    {
        // Gate on live combat state, not just a non-null CombatState: DebugOnlyGetState()
        // stays non-null briefly after combat ends (round-3 fix 5), which was leaking a
        // stale combat.* block onto the very next Map/RewardScreen decision. Round-3-rework:
        // ctx.Kind == DecisionKind.Combat is too narrow — sts2-rl (run_env.py:1319-1321)
        // writes combat.* whenever the request has a combat attached, which includes
        // in-combat SelectCards decisions (ctx.Kind != Combat there). Use
        // CombatManager.IsInProgress && !IsOverOrEnding instead, matching the "live combat
        // in progress" check already used elsewhere (StuckRecovery.cs, ReplayDispatcher.cs).
        var mgr = CombatManager.Instance;
        if (mgr == null || !mgr.IsInProgress || mgr.IsOverOrEnding) return;

        var combatState = mgr.DebugOnlyGetState();
        if (combatState == null) return;

        var player = combatState.Players.FirstOrDefault();
        var pcs = player?.PlayerCombatState;
        if (player == null || pcs == null) return;

        var f = obs.F;
        var iarr = obs.I;
        var overflow = new Dictionary<string, bool>();

        WritePlayerVitals(c, f, combatState, player, pcs);
        overflow["player.powers"] = WritePlayerPowers(c, f, iarr, session, player.Creature);
        overflow["player.relics"] = WriteRelics(c, f, iarr, session, player);
        overflow["hand"] = WriteHand(c, f, iarr, session, combatState, player, pcs);
        overflow["enemies"] = WriteEnemies(c, f, iarr, session, ctx, combatState);
        for (int e = 0; e < MaxEnemies; e++)
            overflow[$"enemy{e}.powers"] = WriteEnemyPowers(c, f, iarr, session, e, ctx, combatState);
        WriteIntentHistory(c, f, session, ctx, combatState);
        WriteDamageMatrix(c, f, ctx, combatState, pcs);
        overflow["potions"] = WritePotions(c, f, iarr, session, player);
        overflow["cards"] = WriteCards(c, f, iarr, session, pcs);

        foreach (var (name, truncated) in overflow)
        {
            if (!truncated) continue;
            var slice = c.F($"combat.{name}.overflow");
            Put(f, slice, 0, 1.0f);
        }
    }

    // ── player vitals ────────────────────────────────────────────────────
    private static void WritePlayerVitals(Contract c, float[] f, ICombatState combatState, Player player, PlayerCombatState pcs)
    {
        var creature = player.Creature;

        Put(f, c.F("combat.player.hp_ratio"), 0, Clip01((float)creature.CurrentHp / Math.Max(1, creature.MaxHp)));
        var hpAbs = Abs2((float)creature.CurrentHp);
        Put(f, c.F("combat.player.hp_abs"), 0, hpAbs.fine); Put(f, c.F("combat.player.hp_abs"), 1, hpAbs.coarse);
        var maxHpAbs = Abs2((float)creature.MaxHp);
        Put(f, c.F("combat.player.max_hp_abs"), 0, maxHpAbs.fine); Put(f, c.F("combat.player.max_hp_abs"), 1, maxHpAbs.coarse);
        var blockAbs = Abs2((float)creature.Block);
        Put(f, c.F("combat.player.block_abs"), 0, blockAbs.fine); Put(f, c.F("combat.player.block_abs"), 1, blockAbs.coarse);

        Put(f, c.F("combat.player.energy"), 0, Clip01(pcs.Energy / 10f));
        Put(f, c.F("combat.player.strength"), 0, Signed(creature.GetPowerAmount<StrengthPower>(), 30f));
        Put(f, c.F("combat.player.dexterity"), 0, Signed(creature.GetPowerAmount<DexterityPower>(), 30f));

        var pileSlice = c.F("combat.player.pile_sizes");
        Put(f, pileSlice, 0, Clip01((float)pcs.Hand.Cards.Count / MaxHand));
        Put(f, pileSlice, 1, Clip01(pcs.DrawPile.Cards.Count / 40f));
        Put(f, pileSlice, 2, Clip01(pcs.DiscardPile.Cards.Count / 40f));
        Put(f, pileSlice, 3, Clip01(pcs.ExhaustPile.Cards.Count / 40f));

        Put(f, c.F("combat.player.turn"), 0, Clip01(pcs.TurnNumber / 30f));

        var incoming = PreviewTotalIncoming(combatState, creature);
        var incAbs = Abs2((float)incoming);
        Put(f, c.F("combat.player.incoming_post_block"), 0, incAbs.fine);
        Put(f, c.F("combat.player.incoming_post_block"), 1, incAbs.coarse);

        // CombatHistory lives on CombatManager, not on (I)CombatState — see CombatManager.cs:143.
        var history = CombatManager.Instance!.History;
        int cardsPlayedThisTurn = history.Entries.OfType<CardPlayFinishedEntry>()
            .Count(e => e.HappenedThisTurn(combatState) && e.Actor == creature);
        Put(f, c.F("combat.player.cards_played_this_turn"), 0, Clip01(cardsPlayedThisTurn / 10f));

        int attacksThisTurn = history.Entries.OfType<CardPlayFinishedEntry>()
            .Count(e => e.HappenedThisTurn(combatState) && e.Actor == creature
                        && e.CardPlay.Card.Type == CardType.Attack);
        Put(f, c.F("combat.player.attacks_this_turn"), 0, Clip01(attacksThisTurn / 10f));

        decimal dmgTaken = history.Entries.OfType<DamageReceivedEntry>()
            .Where(e => e.HappenedThisTurn(combatState) && e.Receiver == creature)
            .Sum(e => e.Result.UnblockedDamage);
        var dmgAbs = Abs2((float)dmgTaken);
        Put(f, c.F("combat.player.damage_taken"), 0, dmgAbs.fine);
        Put(f, c.F("combat.player.damage_taken"), 1, dmgAbs.coarse);
    }

    /// <summary>Sum of every attacking living enemy's own attack-intent damage preview
    /// (same per-enemy computation <see cref="EnemyIntentSnapshot.Compute"/> uses for
    /// <c>enemies.f</c>'s own preview fields — Task 1's KEEP verdict class), minus the
    /// player's current block, floored at 0. Mirrors <c>previews.preview_total_incoming</c>'s
    /// shape (sum of per-enemy previews, not a single engine field) — see the schema
    /// audit's <c>player.incoming_post_block</c> row.</summary>
    private static decimal PreviewTotalIncoming(ICombatState combatState, Creature player)
    {
        decimal total = 0m;
        foreach (var e in combatState.Enemies)
        {
            if (!e.IsAlive) continue;
            var snap = EnemyIntentSnapshot.Compute(e);
            if (snap is { Attack: true } s)
                total += s.Total;
        }
        decimal postBlock = total - player.Block;
        return postBlock < 0 ? 0 : postBlock;
    }

    // ── generic power/relic row writers ─────────────────────────────────

    /// <summary>Aux value for one power instance — faithful C# port of sts2-rl's
    /// <c>_power_aux</c> (<c>full_env.py:584-680</c>), keyed on the power's CONCRETE
    /// TYPE exactly the way <see cref="RelicObsTable"/> keys relics, not on a generic
    /// "DisplayAmount != Amount" heuristic. That heuristic (the previous implementation
    /// here) is WRONG in two independent ways confirmed by reading the sim's own table
    /// (<c>full_env.py:663-680</c>) in full rather than the two power ids a citation
    /// happened to reference:
    ///   1. It MISSES three of the sim's ten dedicated ids outright — <c>the_bomb</c>,
    ///      <c>toric_toughness</c>, and <c>thievery</c> never override
    ///      <c>DisplayAmount</c> (confirmed by grepping every
    ///      <c>Slay the Spire 2/src/Core/Models/Powers/*.cs</c> override) so the old
    ///      code always fell through to its "display == Amount → 0" branch for them,
    ///      even though the sim reads a real per-instance value
    ///      (<c>pw.damage</c>/<c>pw.block</c>/<c>pw.gold_stolen</c>) for all three. Each
    ///      power DOES carry that exact number publicly via its own <c>DynamicVars</c>
    ///      entry (<c>TheBombPower.SetDamage</c> → <c>DynamicVars["Damage"]</c>,
    ///      <c>ToricToughnessPower.SetBlock</c> → <c>DynamicVars["Block"]</c>,
    ///      <c>ThieveryPower.Steal</c> → <c>DynamicVars["Gold"]</c>), read below the
    ///      same way <c>WitheringPresencePower.DisplayAmount</c> itself reads
    ///      <c>DynamicVars["CardsLeft"]</c>.
    ///   2. It FALSELY POSITIVES on any of the other seven C# powers whose
    ///      <c>DisplayAmount</c> override the sim does NOT admit at all —
    ///      <c>feral</c>, <c>monologue</c>, <c>orbit</c>, <c>outbreak</c>, and
    ///      <c>tag_team</c> (the remaining five of the twelve total C#
    ///      <c>DisplayAmount</c> overrides, confirmed by the same grep) — where the
    ///      sim's default (fall through every <c>if pid ==</c> check, <c>return 0.0</c>
    ///      at <c>full_env.py:680</c>) is a STRUCTURAL zero, not "whatever the display
    ///      path shows". The old code would have written a nonzero aux for all five
    ///      whenever their <c>DisplayAmount</c> happens to differ from <c>Amount</c>.
    ///
    /// Exactly the sim's ten ids are handled below, each cited against its own
    /// <c>full_env.py</c> line; every other power (including the five false-positive
    /// ids above) falls through to the same structural 0.0 the sim's own default
    /// returns.</summary>
    private static float PowerAux(PowerModel pw) => pw switch
    {
        // full_env.py:664-665 — the_bomb: this fuse's own blast damage, ABS_SCALE (/100).
        TheBombPower p => Clip01((float)p.DynamicVars["Damage"].BaseValue / AbsScale),
        // full_env.py:666-667 — toric_toughness: this fuse's own block-on-clear, ABS_SCALE (/100).
        ToricToughnessPower p => Clip01((float)p.DynamicVars["Block"].BaseValue / AbsScale),
        // full_env.py:668-669 — thievery: running gold stolen THIS instance, ABS_SCALE (/100).
        ThieveryPower p => Clip01((float)p.DynamicVars["Gold"].BaseValue / AbsScale),
        // full_env.py:670-671 — withering_presence: countdown to next Wither, /10.0.
        // WitheringPresencePower.DisplayAmount (WitheringPresencePower.cs:24) IS
        // DynamicVars["CardsLeft"].IntValue, the exact _cards_left field the sim cites.
        WitheringPresencePower p => Clip01(p.DisplayAmount / 10f),
        // full_env.py:672-673 — automation/panache: countdown to next tick, /10.0.
        // Both powers' DisplayAmount overrides (AutomationPower.cs:25,
        // PanachePower.cs:33) ARE their cards_left field.
        AutomationPower p => Clip01(p.DisplayAmount / 10f),
        PanachePower p => Clip01(p.DisplayAmount / 10f),
        // full_env.py:674-675 — hardened_shell: max(0, amount - damage_received_this_turn),
        // ABS_SCALE (/100). HardenedShellPower.DisplayAmount (HardenedShellPower.cs:25)
        // computes exactly this.
        HardenedShellPower p => Clip01(p.DisplayAmount / AbsScale),
        // full_env.py:676-677 — sloth/tender: cards played this turn, /10.0.
        // Both DisplayAmount overrides (SlothPower.cs:20, TenderPower.cs:22) ARE that count.
        SlothPower p => Clip01(p.DisplayAmount / 10f),
        TenderPower p => Clip01(p.DisplayAmount / 10f),
        // full_env.py:678-679 — slow: _cards_this_turn / 10.0. SlowPower.DisplayAmount
        // (SlowPower.cs:22) is already SlowAmount*10, i.e. _cards_this_turn*10, so
        // dividing by AbsScale (100) here is the identical float to dividing the raw
        // counter by 10 — see full_env.py:656-661's own note on this equivalence.
        SlowPower p => Clip01(p.DisplayAmount / AbsScale),
        // full_env.py:680 — every other id (including the five C# DisplayAmount
        // overrides the sim does NOT admit: feral, monologue, orbit, outbreak,
        // tag_team) is a structural 0.0, never a fabricated value.
        _ => 0f,
    };

    private static bool WritePlayerPowers(Contract c, float[] f, long[] iarr, SessionState session, Creature player)
        => WritePowerRows(c, f, iarr, session, "combat.player.powers", player, MaxPowersPlayer);

    private static bool WriteEnemyPowers(Contract c, float[] f, long[] iarr, SessionState session, int slot, DecisionContext ctx, ICombatState combatState)
    {
        var name = $"combat.enemy{slot}.powers";
        var enemy = ResolveEnemy(ctx, combatState, slot);
        if (enemy == null || !enemy.IsAlive)
            return false; // nothing to write — row stays PAD/zero, never truncated
        return WritePowerRows(c, f, iarr, session, name, enemy, MaxPowersEnemy);
    }

    /// <summary>Every power INSTANCE on <paramref name="creature"/>, C#'s own
    /// application order (oldest-first, <c>Creature.Powers</c> — already an ordered
    /// list per the phase-0 sim work this mirrors), truncated (never sorted) to
    /// <paramref name="cap"/> — the prefix IS the meaning here (oldest instances
    /// survive a truncation), matching <c>full_env._power_rows</c> + <c>write_rows(...,
    /// sort=False)</c> exactly.</summary>
    private static bool WritePowerRows(Contract c, float[] f, long[] iarr, SessionState session, string name, Creature creature, int cap)
    {
        var idsSlice = c.I($"{name}.ids");
        var fSlice = c.F($"{name}.f");
        var powers = creature.Powers;
        int n = powers.Count;
        bool truncated = n > cap;
        int written = Math.Min(n, cap);

        for (int r = 0; r < written; r++)
        {
            var pw = powers[r];
            long vocabId = Vocab(c, session, "powers", pw.Id.ToString());
            PutI(iarr, idsSlice, r, vocabId);

            Put(f, fSlice, r * 3 + 0, Signed(pw.Amount, 10f));
            Put(f, fSlice, r * 3 + 1, Signed(pw.Amount, 50f));
            Put(f, fSlice, r * 3 + 2, PowerAux(pw));
        }
        return truncated;
    }

    /// <summary>Every relic the player holds, acquisition order (<c>Player.Relics</c>).
    /// FIX 5 (round 4) — ported from sts2_rl/relic_obs.py's per-id table via
    /// <see cref="RelicObsTable"/> (see that class's doc comment for the full port
    /// rationale), replacing the old GENERIC ShowCounter/DisplayAmount/Status rule the
    /// round-4 diagnosis found diverging from the sim in two independent ways
    /// (diag4-runlevel.md finding 1). <c>inCombat: true</c> here is deliberate — this IS
    /// the live in-combat combat.player.relics.f path (as opposed to run.relics.f, which
    /// RunObsWriter.WriteRelics forces to <c>inCombat: false</c> per sim's own
    /// <c>relic_row(relic, in_combat=False)</c> call for the run-level block).
    /// CLAMP_CEILING = 9, matching <c>relic_obs.CLAMP_CEILING</c>.</summary>
    private static bool WriteRelics(Contract c, float[] f, long[] iarr, SessionState session, Player player)
    {
        var idsSlice = c.I("combat.player.relics.ids");
        var fSlice = c.F("combat.player.relics.f");
        var relics = player.Relics;
        int n = relics.Count;
        bool truncated = n > MaxRelicRows;
        int written = Math.Min(n, MaxRelicRows);

        for (int r = 0; r < written; r++)
        {
            var relic = relics[r];
            long vocabId = Vocab(c, session, "relics", relic.Id.ToString());
            PutI(iarr, idsSlice, r, vocabId);

            var (counter, flag) = RelicObsTable.Row(relic, inCombat: true);

            Put(f, fSlice, r * 2 + 0, counter / 10f);
            Put(f, fSlice, r * 2 + 1, flag);
        }
        return truncated;
    }

    // ── hand ─────────────────────────────────────────────────────────────

    private static bool WriteHand(Contract c, float[] f, long[] iarr, SessionState session, ICombatState combatState, Player player, PlayerCombatState pcs)
    {
        var idsSlice = c.I("combat.hand.ids");
        var fSlice = c.F("combat.hand.f");
        var hand = pcs.Hand.Cards;
        int n = hand.Count;
        bool truncated = n > MaxHand;
        int written = Math.Min(n, MaxHand);

        for (int h = 0; h < written; h++)
        {
            var card = hand[h];
            PutI(iarr, idsSlice, h * 3 + 0, Vocab(c, session, "cards", card.Id.ToString()));
            PutI(iarr, idsSlice, h * 3 + 1, card.Affliction != null ? Vocab(c, session, "afflictions", card.Affliction.Id.ToString()) : 0);
            PutI(iarr, idsSlice, h * 3 + 2, card.Enchantment != null ? Vocab(c, session, "enchantments", card.Enchantment.Id.ToString()) : 0);

            WriteCardFeatures(f, fSlice, h * NCardFeatures, combatState, player, pcs, card);
        }
        return truncated;
    }

    /// <summary>The 31-float per-hand-card feature row — <c>full_env.card_features</c>
    /// ported field-for-field. See this method's inline comments for the handful of
    /// fields with no confirmed 1:1 C# source (flagged, not guessed).</summary>
    private static void WriteCardFeatures(float[] f, LayoutSlice slice, int baseIdx, ICombatState combatState, Player player, PlayerCombatState pcs, CardModel card)
    {
        void S(int i, float v) => Put(f, slice, baseIdx + i, v);

        // Hand-row cost is a LIVE, hook-modified read in sim (sts2_rl/full_env.py:726-729,
        // `effective_cost = preview_card_energy_cost(s, card)`), unlike the pile rows below
        // which use the modifier-immune canonical/upgrade-tracking cost. `.Canonical` is
        // frozen at construction and never reflects upgrades (CardEnergyCost.cs:31,82-88;
        // UpgradeBy mutates `_base`, not `Canonical` — CardEnergyCost.cs:351-372), so the
        // upgrade-adjusted base must come from GetWithModifiers including CostModifiers.Local
        // (CardEnergyCost.cs:94-121) before the live Global hook is layered on top.
        //
        // Round-5 fix (89U/933T hand.f field-0 residual): this previously read
        // GetWithModifiers(CostModifiers.None), which — per CostModifiers.cs's own doc
        // comment — deliberately excludes `_localModifiers` (SetThisTurn/SetThisCombat/
        // SetUntilPlayed/AddThisTurn/... — CardEnergyCost.cs:175-330), the exact game-side
        // counterpart of sim's per-card `_free_this_turn`/`_cost_this_turn`/
        // `_cost_this_combat`/`_cost_delta_this_turn` (sts2_rl/cards/base.py's `energy_cost`
        // property, read by `preview_card_energy_cost` before the global-hook layer). Any
        // card carrying a live local cost modifier (e.g. BulletTime/Enlightenment/Invoke —
        // see CardEnergyCost.cs:190,212,231) was silently read at its un-discounted base,
        // a hand.f field-0 mismatch on exactly the affected card(s). `CostModifiers.Local`
        // includes the same base-cost computation `.None` did (CardEnergyCost.cs:96-104)
        // plus applies `_localModifiers` (line 109-115) — `.Global` is intentionally left
        // off here since the Hook.ModifyEnergyCostInCombat call below applies it explicitly,
        // in the same Local-then-Global order `GetWithModifiers(CostModifiers.All)` uses.
        // Round-11 fix: an X-cost card's hand-row cost is the player's CURRENT
        // ENERGY, not 0 — sim's `preview_card_energy_cost` (previews.py:257-263)
        // returns `combat.player.energy` for `energy_cost_x`, mirroring the game's
        // own `CardEnergyCost.GetAmountToSpend` (CardEnergyCost.cs:134-141,
        // "For X-cost cards, this is the amount of energy that its owner has").
        // The old `? 0` here read Cascade at 0 all fight while sim tracked the
        // live energy (89U (2,13)/(2,15) hand.f field-0 residual, 14 cells).
        decimal plainCost = card.EnergyCost.CostsX ? 0 : card.EnergyCost.GetWithModifiers(CostModifiers.Local);
        decimal effectiveCost = card.EnergyCost.CostsX
            ? pcs.Energy
            : Hook.ModifyEnergyCostInCombat(combatState, card, plainCost);
        S(0, Clip01((float)effectiveCost / 6f));
        S(1, card.EnergyCost.CostsX ? 1f : 0f);

        S(2, card.Type == CardType.Attack ? 1f : 0f);
        S(3, card.Type == CardType.Skill ? 1f : 0f);
        S(4, card.Type == CardType.Power ? 1f : 0f);
        S(5, card.Type == CardType.Status ? 1f : 0f);
        S(6, card.Type == CardType.Curse ? 1f : 0f);

        S(7, card.TargetType == TargetType.AnyEnemy ? 1f : 0f);
        S(8, card.TargetType == TargetType.AllEnemies ? 1f : 0f);
        S(9, card.TargetType == TargetType.RandomEnemy ? 1f : 0f);
        S(10, card.TargetType == TargetType.Self ? 1f : 0f);
        S(11, card.TargetType == TargetType.None ? 1f : 0f);

        S(12, card.Keywords.Contains(CardKeyword.Exhaust) ? 1f : 0f);
        S(13, card.Keywords.Contains(CardKeyword.Ethereal) ? 1f : 0f);
        // Fix 4 (round 4): sim's is_playable (full_env.py:739, base.py:161/293-298) is a
        // STATIC per-card-class keyword flag — the exact inverse of CardKeyword.Unplayable —
        // never a dynamic multi-gate check. card.CanPlay() (CardModel.cs:1717-1764) ORs in
        // energy/target/hook gates that duplicate field 15 (affordable) and add two more
        // conditions sim never captures here; that was the round-4 diag4-combat.md finding 1
        // root cause. Mirror sim's static check exactly.
        S(14, card.Keywords.Contains(CardKeyword.Unplayable) ? 0f : 1f);
        bool affordable = card.EnergyCost.CostsX || effectiveCost <= pcs.Energy;
        S(15, affordable ? 1f : 0f);
        S(CardUpgradeFeature, Clip01(card.CurrentUpgradeLevel / 5f));

        var vars = card.DynamicVars;
        if (vars.ContainsKey("CalculatedDamage"))
        {
            var ab = Abs2((float)vars.CalculatedDamage.BaseValue);
            S(17, ab.fine); S(18, ab.coarse);
        }
        else if (vars.ContainsKey("Damage"))
        {
            var ab = Abs2((float)vars.Damage.BaseValue);
            S(17, ab.fine); S(18, ab.coarse);
        }

        // base_hits: sts2-rl's card.base_hits (sts2_rl/cards/base.py, full_env.py:748)
        // defaults to 1 for EVERY card — a single-hit card still reports base_hits=1,
        // only a Repeat-carrying card (e.g. Twin Strike) reports something else. Mirror
        // that default here instead of leaving single-hit cards at a structural 0.
        float hits = HardcodedBaseHitsByCardId.TryGetValue(card.Id.ToString(), out float hardcodedHits)
            ? hardcodedHits
            : vars.ContainsKey("Repeat") ? (float)vars.Repeat.BaseValue : 1f;
        S(19, Clip01(hits / 10f));

        decimal? baseBlock = null;
        if (vars.ContainsKey("CalculatedBlock")) baseBlock = vars.CalculatedBlock.BaseValue;
        else if (vars.ContainsKey("Block")) baseBlock = vars.Block.BaseValue;
        if (baseBlock is decimal bb)
        {
            S(20, Clip01((float)bb / AbsScale));
            try
            {
                decimal effBlock = Hook.ModifyBlock(combatState, player.Creature, bb, default, card, null, out _);
                S(21, Clip01((float)effBlock / AbsScale));
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SpireBot] WriteCardFeatures: Hook.ModifyBlock threw for {card.Id}: {ex.Message}");
            }
            // v14 (schema 8): true block under the powered/move pipeline (Dexterity,
            // Frail, Fasten) — sim f[30] (preview_card_block ValueProp.MOVE), unlike
            // field 21's default(ValueProp) parity value above (kept untouched).
            // Reuses this same `baseBlock is decimal bb` guard — sim's condition for
            // f[30] is identical to f[20]/f[21]'s ("card_base_block is not None").
            // Truncated toward zero before scaling: sim's preview_card_block returns
            // max(0, int(...)) (sts2_rl/previews.py:243-245), matching BlockCmd.apply's
            // int cast — a fractional multiplicative result (e.g. Frail's 0.75x) must
            // floor/truncate here too or C# and sim diverge (Frail: sim 3 vs C# 3.75).
            // Negative clamping is already covered by Clip01 below.
            try
            {
                decimal mvBlock = Hook.ModifyBlock(combatState, player.Creature, bb, ValueProp.Move, card, null, out _);
                S(30, Clip01((float)decimal.Truncate(mvBlock) / AbsScale));
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SpireBot] WriteCardFeatures: Hook.ModifyBlock(Move) threw for {card.Id}: {ex.Message}");
            }
        }

        if (vars.ContainsKey("HpLoss"))
            S(22, Clip01((float)vars.HpLoss.BaseValue / AbsScale));

        // magic_number (round 7 fix) — sts2-rl's card.magic_number (cards/base.py:411-419)
        // walks a hand-authored per-card PRIORITY LIST of sim-only instance attrs:
        // _vulnerable, _weak, _frail, _strength, _power_amount, _power, _plating, _cards,
        // _energy, _energy_gain, _heal, _extra, _increase, _attacks, _repeat — returning
        // whichever is set FIRST for that card class (full_env.py:754,
        // `f[23] = clip01(magic/20)` if not None else 0). Round 6 got the PowerVar<T> key
        // wrong: it keys by `typeof(T).Name`, the POWER MODEL CLASS's own name
        // (VulnerablePower/WeakPower/FrailPower/StrengthPower), not the shorthand
        // "Vulnerable"/"Weak"/"Frail"/"Strength" — see MagicNumberPriority's doc comment
        // above for the full source citation and the "Power"/"InfernoPower"/"PlatingPower"/
        // "ExtraDamage" additions this round confirmed via direct source reads (Bash.cs,
        // Taunt.cs, Dominate.cs, Tremble.cs, DemonForm.cs, Uppercut.cs, FeelNoPain.cs,
        // Inferno.cs, StoneArmor.cs, Bully.cs, Rend.cs, PerfectedStrike.cs, AshenStrike.cs)
        // cross-checked against the matching sts2_rl/cards/*.py attribute. "Cards"/"Energy"/
        // "Heal" remain confirmed 1:1 by their own dedicated var classes (CardsVar/EnergyVar/
        // HealVar, each hardcoding that exact name). "ExtraDamage" and "Repeat" are gated to
        // ExtraDamageMagicNumberCardIds/RepeatMagicNumberCardIds above — both var classes are
        // shared by 20+ cards game-side but sts2-rl only mirrors a handful of them as
        // _extra/_repeat, so an unconditional match over-fires for every OTHER card carrying
        // that var (confirmed false positive: Conflagration/TearAsunder both carry "Repeat"
        // with no sts2-rl-side `_repeat`). "Increase" and "Attacks" are carried over from
        // round 6 unverified this round (no card in this dump's mismatch set exercised them).
        string cardGameId = card.Id.ToString();
        foreach (string magicKey in MagicNumberPriority)
        {
            if (!vars.ContainsKey(magicKey))
                continue;
            if (magicKey == "ExtraDamage" && !ExtraDamageMagicNumberCardIds.Contains(cardGameId))
                continue;
            if (magicKey == "Repeat" && !RepeatMagicNumberCardIds.Contains(cardGameId))
                continue;

            S(23, Clip01((float)vars[magicKey].BaseValue / 20f));
            break;
        }

        if (card.Affliction != null)
            S(24, Clip01(card.Affliction.Amount / 10f));
        S(25, card.ExhaustOnNextPlay ? 1f : 0f);
        S(26, card.ShouldRetainThisTurn ? 1f : 0f);
        S(27, card.IsSlyThisTurn ? 1f : 0f);
        S(28, Clip01(card.BaseReplayCount / 3f));
        // v14 (schema 8): the card face's gold-glow condition signal — sim f[29].
        S(29, card.ShouldGlowGold ? 1f : 0f);
    }

    // ── enemies ──────────────────────────────────────────────────────────

    /// <summary>Resolves a stable encounter slot to its live <see cref="Creature"/> by
    /// CombatId (round-3 fix 2) — NEVER by re-indexing <c>combatState.Enemies</c>
    /// positionally, since that list shrinks on removal and a raw index would resolve
    /// to the wrong creature (or throw) the instant an earlier slot's enemy dies.
    /// Returns null for a slot whose creature is no longer in the live list (PAD row —
    /// callers already gate on <c>raw.IsAlive</c> for that case).</summary>
    private static Creature? ResolveEnemy(DecisionContext ctx, ICombatState combatState, int slot)
    {
        if (slot >= ctx.EncounterEnemies.Count) return null;
        var raw = ctx.EncounterEnemies[slot];
        if (raw.CombatId is not uint id) return null;
        return combatState.Enemies.FirstOrDefault(e => e.CombatId == id);
    }

    private static bool WriteEnemies(Contract c, float[] f, long[] iarr, SessionState session, DecisionContext ctx, ICombatState combatState)
    {
        var idsSlice = c.I("combat.enemies.ids");
        var fSlice = c.F("combat.enemies.f");
        int n = ctx.EncounterEnemies.Count;
        bool truncated = n > MaxEnemies;
        int written = Math.Min(n, MaxEnemies);

        for (int slot = 0; slot < written; slot++)
        {
            var raw = ctx.EncounterEnemies[slot];
            var enemy = ResolveEnemy(ctx, combatState, slot);
            if (enemy == null || !raw.IsAlive)
                continue; // PAD row (id 0, all-zero floats) — matches "gone/absent enemy" rule (IsAlive proxy — see class doc)

            PutI(iarr, idsSlice, slot, Vocab(c, session, "monsters", raw.ModelGameId));
            WriteEnemyFloats(f, fSlice, slot * NEnemyScalars, enemy);
        }
        return truncated;
    }

    private static void WriteEnemyFloats(float[] f, LayoutSlice slice, int baseIdx, Creature e)
    {
        void S(int i, float v) => Put(f, slice, baseIdx + i, v);

        S(0, 1f); // presence
        S(1, Clip01((float)e.CurrentHp / Math.Max(1, e.MaxHp)));
        var hpAbs = Abs2((float)e.CurrentHp); S(2, hpAbs.fine); S(3, hpAbs.coarse);
        var maxHpAbs = Abs2((float)e.MaxHp); S(4, maxHpAbs.fine); S(5, maxHpAbs.coarse);
        var blockAbs = Abs2((float)e.Block); S(6, blockAbs.fine); S(7, blockAbs.coarse);
        S(8, Signed(e.GetPowerAmount<StrengthPower>(), 30f));

        var snap = EnemyIntentSnapshot.Compute(e);
        if (snap is { } s)
        {
            const int fb = 9;
            if (s.Attack) S(fb + 0, 1f);
            if (s.Defend) S(fb + 1, 1f);
            if (s.Buff) S(fb + 2, 1f);
            if (s.Debuff) S(fb + 3, 1f);
            if (s.StatusCard) S(fb + 4, 1f);
            if (s.Summon) S(fb + 5, 1f);
            if (s.Escape) S(fb + 6, 1f);
            if (s.Heal) S(fb + 7, 1f);
            if (s.Stun) S(fb + 8, 1f);

            if (s.Attack)
            {
                const int pb = 18;
                S(pb, Clip01((float)s.PerHit / AbsScale));
                S(pb + 1, Clip01(s.Hits / 10f));
                var totalAbs = Abs2((float)s.Total); S(pb + 2, totalAbs.fine); S(pb + 3, totalAbs.coarse);
                decimal postBlock = s.Total - e.CombatState!.PlayerCreatures.FirstOrDefault()!.Block;
                if (postBlock < 0) postBlock = 0;
                var postAbs = Abs2((float)postBlock); S(pb + 4, postAbs.fine); S(pb + 5, postAbs.coarse);
            }

            if (s.StatusCard && s.StatusCount is int sc)
                S(24, Clip01(sc / 10f));
        }
    }

    private static void WriteIntentHistory(Contract c, float[] f, SessionState session, DecisionContext ctx, ICombatState combatState)
    {
        for (int slot = 0; slot < MaxEnemies; slot++)
        {
            var fSlice = c.F($"combat.enemy{slot}.intent_history.f");
            if (slot >= ctx.EncounterEnemies.Count) continue;
            var raw = ctx.EncounterEnemies[slot];
            if (raw.CombatId is not uint id) continue;
            // Match WriteEnemies' PAD gate (`enemy == null || !raw.IsAlive`) and the
            // sim's `_enemy_intent_history_floats` (`e is None or e.is_gone`): a slot
            // whose creature has died/left CombatState.Enemies must read fully
            // unrecorded, even though its CombatId is still tracked in `session`
            // (session._history is keyed by CombatId and never cleared on death —
            // that's WAI for a creature that dies mid-combat and is later revived by
            // something odd, but the intent-history *observation* must go blank the
            // instant the enemy is no longer alive, exactly like its current-turn
            // fields already do). Without this gate a dead enemy's slot kept echoing
            // its last-alive history forever (the enemy0/1/2/3/4.intent_history.f
            // residual: 372/105/66/48/32 mismatches — all post-death stale reads).
            if (ResolveEnemy(ctx, combatState, slot) == null || !raw.IsAlive) continue;

            var history = session.HistoryFor(id);
            for (int h = 0; h < MaxIntentHistory; h++)
            {
                int rowBase = h * NEnemyHistoryScalars;
                if (h >= history.Count) continue; // stays PAD (recorded=0.0)
                var s = history[h];
                void S(int i, float v) => Put(f, fSlice, rowBase + i, v);

                S(0, 1f); // recorded
                if (s.Attack) S(1, 1f);
                if (s.Defend) S(2, 1f);
                if (s.Buff) S(3, 1f);
                if (s.Debuff) S(4, 1f);
                if (s.StatusCard) S(5, 1f);
                if (s.Summon) S(6, 1f);
                if (s.Escape) S(7, 1f);
                if (s.Heal) S(8, 1f);
                if (s.Stun) S(9, 1f);
                S(10, Clip01((float)s.PerHit / AbsScale));
                S(11, Clip01(s.Hits / 10f));
                var totalAbs = Abs2((float)s.Total); S(12, totalAbs.fine); S(13, totalAbs.coarse);
                if (s.StatusCount is int sc) S(14, Clip01(sc / 10f));
            }
        }
    }

    // ── damage matrix ────────────────────────────────────────────────────

    private static void WriteDamageMatrix(Contract c, float[] f, DecisionContext ctx, ICombatState combatState, PlayerCombatState pcs)
    {
        var slice = c.F("combat.damage_matrix");
        var hand = pcs.Hand.Cards;

        for (int h = 0; h < Math.Min(MaxHand, hand.Count); h++)
        {
            var card = hand[h];
            int rowBase = h * MaxEnemies;
            // Stable slot -> creature (round-3 fix 2): iterating ctx.EncounterEnemies keeps
            // column `e` in agreement with combat.enemies.f's own slot numbering — raw
            // combatState.Enemies enumeration order drifts the instant an earlier enemy dies.
            for (int e = 0; e < Math.Min(MaxEnemies, ctx.EncounterEnemies.Count); e++)
            {
                var raw = ctx.EncounterEnemies[e];
                if (!raw.IsAlive) continue;
                var enemy = ResolveEnemy(ctx, combatState, e);
                if (enemy == null) continue;

                try
                {
                    card.UpdateDynamicVarPreview(CardPreviewMode.Normal, enemy, card.DynamicVars);
                    var vars = card.DynamicVars;
                    decimal dmg = vars.ContainsKey("CalculatedDamage") ? vars.CalculatedDamage.PreviewValue
                        : vars.ContainsKey("Damage") ? vars.Damage.PreviewValue
                        : 0m;
                    if (dmg > 0)
                        Put(f, slice, rowBase + e, Clip01((float)dmg / AbsScale));
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[SpireBot] WriteDamageMatrix: preview threw for {card.Id} -> enemy#{e}: {ex.Message}");
                }
            }
        }
    }

    // ── potions ──────────────────────────────────────────────────────────

    private static bool WritePotions(Contract c, float[] f, long[] iarr, SessionState session, Player player)
    {
        var idsSlice = c.I("combat.potions.ids");
        var fSlice = c.F("combat.potions.f");
        // Player.Potions is an IEnumerable<PotionModel> compacted to non-null slots only
        // (Player.cs) — useless for a POSITIONAL row (row p IS belt slot p, matching the
        // combat-action potion-slot index ActionMap already uses). Player.PotionSlots
        // (IReadOnlyList<PotionModel?>) is the raw, nullable-slotted belt.
        var potions = player.PotionSlots;
        int n = potions.Count;
        bool truncated = n > MaxPotionRows;
        int written = Math.Min(n, MaxPotionRows);

        for (int p = 0; p < written; p++)
        {
            var potion = potions[p];
            if (potion == null) continue;
            PutI(iarr, idsSlice, p, Vocab(c, session, "potions", potion.Id.ToString()));
            bool targeted = potion.TargetType == TargetType.AnyEnemy;
            Put(f, fSlice, p, targeted ? 1f : 0f);
        }
        return truncated;
    }

    // ── cards (draw+discard+exhaust, sorted/order-independent) ─────────────

    private readonly struct PileRow
    {
        public readonly int[] Ints;
        public readonly float[] Floats;
        public PileRow(int[] ints, float[] floats) { Ints = ints; Floats = floats; }
    }

    private sealed class PileRowComparer : IComparer<PileRow>
    {
        public static readonly PileRowComparer Instance = new();
        public int Compare(PileRow a, PileRow b)
        {
            for (int i = 0; i < a.Ints.Length; i++)
            {
                int cmp = a.Ints[i].CompareTo(b.Ints[i]);
                if (cmp != 0) return cmp;
            }
            for (int i = 0; i < a.Floats.Length; i++)
            {
                int cmp = a.Floats[i].CompareTo(b.Floats[i]);
                if (cmp != 0) return cmp;
            }
            return 0;
        }
    }

    /// <summary>The ONLY sorted block (OBS_SCHEMA.md §5.3's hidden-information rule):
    /// pile order is never exposed, so every row is sorted lexicographically by
    /// <c>(ints tuple, floats tuple)</c> — <c>ObsBuffer.write_rows(sort=True)</c>'s exact
    /// key, ported verbatim (a plain tuple comparison, no Python-specific tie-breaking
    /// — safe to port 1:1, not an open question). Sort happens BEFORE truncation so an
    /// overflowing pile's surviving rows are a deterministic function of the multiset,
    /// not of draw order (see <c>ObsBuffer.write_rows</c>'s own comment on why the two
    /// orders matter).</summary>
    private static bool WriteCards(Contract c, float[] f, long[] iarr, SessionState session, PlayerCombatState pcs)
    {
        var idsSlice = c.I("combat.cards.ids");
        var fSlice = c.F("combat.cards.f");

        var rows = new List<PileRow>();
        void CollectPile(IReadOnlyList<CardModel> pile, int pileId)
        {
            foreach (var card in pile)
            {
                int cardId = (int)Vocab(c, session, "cards", card.Id.ToString());
                int afflId = card.Affliction != null ? (int)Vocab(c, session, "afflictions", card.Affliction.Id.ToString()) : 0;
                int enchId = card.Enchantment != null ? (int)Vocab(c, session, "enchantments", card.Enchantment.Id.ToString()) : 0;
                float upgrade = Clip01(card.CurrentUpgradeLevel / 5f);
                // v7 REDEFINE: plain printed cost, no hook — these piles are not Hand,
                // so the game's own preview pipeline never hook-modifies them either
                // (schema audit's `cards.f` row). Sim source: sts2_rl/full_env.py
                // `_pile_card_row` (~line 1008-1024) reads `card.canonical_energy_cost`,
                // the upgrade-tracking cost — NOT the frozen `.Canonical` (see
                // CardEnergyCost.cs:31,82-88 / UpgradeBy at 351-372), so use
                // GetWithModifiers(CostModifiers.None) (CardEnergyCost.cs:94-121) which
                // matches sts2_rl's `canonical_energy_cost` semantics exactly.
                float cost = Clip01((float)card.EnergyCost.GetWithModifiers(CostModifiers.None) / 6f);
                float afflAmt = card.Affliction != null ? Clip01(card.Affliction.Amount / 10f) : 0f;
                float exhaustNext = card.ExhaustOnNextPlay ? 1f : 0f;

                rows.Add(new PileRow(
                    new[] { pileId, cardId, afflId, enchId },
                    new[] { upgrade, cost, afflAmt, exhaustNext }));
            }
        }
        CollectPile(pcs.DrawPile.Cards, 1);
        CollectPile(pcs.DiscardPile.Cards, 2);
        CollectPile(pcs.ExhaustPile.Cards, 3);

        rows.Sort(PileRowComparer.Instance);

        int n = rows.Count;
        bool truncated = n > MaxCombatCards;
        int written = Math.Min(n, MaxCombatCards);

        for (int r = 0; r < written; r++)
        {
            var row = rows[r];
            PutI(iarr, idsSlice, r * 4 + 0, row.Ints[0]);
            PutI(iarr, idsSlice, r * 4 + 1, row.Ints[1]);
            PutI(iarr, idsSlice, r * 4 + 2, row.Ints[2]);
            PutI(iarr, idsSlice, r * 4 + 3, row.Ints[3]);
            Put(f, fSlice, r * 4 + 0, row.Floats[0]);
            Put(f, fSlice, r * 4 + 1, row.Floats[1]);
            Put(f, fSlice, r * 4 + 2, row.Floats[2]);
            Put(f, fSlice, r * 4 + 3, row.Floats[3]);
        }
        return truncated;
    }
}
