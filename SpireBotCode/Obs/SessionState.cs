using System;
using System.Collections.Generic;
using System.Linq;

using Godot;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace SpireBot.SpireBotCode.Obs;

/// <summary>
/// One enemy's displayed-intent snapshot, in the exact shape the observation's
/// intent-history segment (and the live <c>enemies.f</c> intent-preview
/// fields, which share the same underlying computation â€” see
/// <see cref="Compute"/>) needs: the 9 intent-type flags (mirroring
/// sts2-rl's <c>MoveType</c> flags / <c>full_env._enemy_floats</c> fields
/// 9-17), the attack preview (per-hit, hits, total), and StatusIntent's
/// displayed card count. Deliberately does NOT carry <c>post_block</c> â€”
/// sts2-rl's own history record excludes it too (a derived
/// current-player-block combination the sim doesn't retain historically
/// either; see the schema audit's <c>enemy{e}.intent_history.f</c> row).
/// </summary>
public readonly struct EnemyIntentSnapshot
{
    public readonly bool Attack, Defend, Buff, Debuff, StatusCard, Summon, Escape, Heal, Stun;
    public readonly decimal PerHit;
    public readonly int Hits;
    public readonly decimal Total;
    public readonly int? StatusCount;

    /// <summary>The live <c>AttackIntent</c> this snapshot's damage preview was computed
    /// from, if any â€” retained (round-8 fix, Task 1) so <see cref="SessionState"/> can
    /// RE-RUN <c>GetSingleDamage</c> against current creature stats at PUSH time (see
    /// <see cref="SessionState.OnTurnStarted"/>'s doc), mirroring sts2-rl's
    /// <c>_record_intent_history</c> (combat.py:593-627), which explicitly recomputes
    /// <c>preview_incoming_damage</c> at the moment an intent is superseded rather than
    /// reusing whatever was shown live during the turn it was displayed. Not part of the
    /// struct's equality/observable surface for any other consumer â€” <see cref="CombatObsWriter"/>'s
    /// live row never reads it.</summary>
    internal readonly AttackIntent? SourceAttackIntent;

    public EnemyIntentSnapshot(
        bool attack, bool defend, bool buff, bool debuff, bool statusCard,
        bool summon, bool escape, bool heal, bool stun,
        decimal perHit, int hits, decimal total, int? statusCount,
        AttackIntent? sourceAttackIntent = null)
    {
        Attack = attack; Defend = defend; Buff = buff; Debuff = debuff; StatusCard = statusCard;
        Summon = summon; Escape = escape; Heal = heal; Stun = stun;
        PerHit = perHit; Hits = hits; Total = total; StatusCount = statusCount;
        SourceAttackIntent = sourceAttackIntent;
    }

    /// <summary>
    /// Computes one enemy's CURRENTLY DISPLAYED intent, straight off the live
    /// <c>Creature</c> â€” no UI, no session memory. Shared by
    /// <see cref="CombatObsWriter"/>'s live <c>enemies.f</c> row (fields 9-24)
    /// and <see cref="SessionState.OnTurnStarted"/>'s history recorder, so
    /// the two can never drift out of format agreement with each other.
    ///
    /// C# has no <c>MoveType</c> flags enum (confirmed absent from
    /// <c>MonsterMoves/Intents/*.cs</c> â€” sts2-rl's <c>MoveType</c> is a
    /// sim-only convenience over the game's single-valued <c>IntentType</c>
    /// per <c>AbstractIntent</c>). A move can carry SEVERAL simultaneous
    /// <c>AbstractIntent</c>s (<c>Monster.NextMove.Intents</c>), so the flags
    /// below are OR-ed across every intent in the move, matching
    /// <c>full_env._enemy_floats</c>'s <c>intent.has(MoveType.X)</c> checks
    /// field-for-field (fb=9..17 order preserved exactly).
    /// </summary>
    public static EnemyIntentSnapshot? Compute(Creature enemy)
    {
        var move = enemy.Monster?.NextMove;
        if (move == null)
            return null;

        bool attack = false, defend = false, buff = false, debuff = false, statusCard = false;
        bool summon = false, escape = false, heal = false, stun = false;
        AttackIntent? attackIntent = null;
        StatusIntent? statusIntent = null;

        foreach (var intent in move.Intents)
        {
            switch (intent.IntentType)
            {
                case IntentType.Attack: attack = true; attackIntent ??= intent as AttackIntent; break;
                case IntentType.Defend: defend = true; break;
                case IntentType.Buff: buff = true; break;
                case IntentType.Debuff:
                case IntentType.DebuffStrong:
                case IntentType.CardDebuff:
                    debuff = true; break;
                case IntentType.StatusCard: statusCard = true; statusIntent ??= intent as StatusIntent; break;
                case IntentType.Summon: summon = true; break;
                case IntentType.Escape: escape = true; break;
                case IntentType.Heal: heal = true; break;
                case IntentType.Stun:
                case IntentType.Sleep:
                    stun = true; break;
            }
        }
        // enemy.IsStunned (Creature.cs: Monster?.NextMove.Id == "STUNNED") mirrors
        // full_env._enemy_floats's extra `e.stunned` OR-term on the same flag bit.
        if (enemy.IsStunned)
            stun = true;

        decimal perHit = 0m, total = 0m;
        int hits = 0;
        if (attackIntent != null)
        {
            try
            {
                // AttackIntent.GetSingleDamage is public, UI-free, side-effect-free
                // (runs Hook.ModifyDamage with CardPreviewMode.None â€” see Task 1's
                // damage-preview research doc). `HittableEnemies`/`Enemies` on the
                // combat state aren't needed here: GetSingleDamage only reads
                // `owner`/`targets` to resolve hook context, and the player is always
                // the implicit target for an enemy's own attack intent.
                var combatState = enemy.CombatState;
                var player = combatState?.PlayerCreatures.FirstOrDefault();
                if (player != null)
                {
                    perHit = attackIntent.GetSingleDamage(new[] { player }, enemy);
                    hits = attackIntent.Repeats;
                    total = perHit * Math.Max(hits, 1);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SpireBot] EnemyIntentSnapshot.Compute: GetSingleDamage threw for " +
                            $"{enemy.Name}: {ex.Message}");
            }
        }

        int? statusCount = statusIntent?.CardCount;

        return new EnemyIntentSnapshot(
            attack, defend, buff, debuff, statusCard, summon, escape, heal, stun,
            perHit, hits, total, statusCount, attackIntent);
    }

    /// <summary>Re-runs the attack-damage preview against <paramref name="enemy"/>'s
    /// CURRENT stats (Strength, Vulnerable/Weak, etc.), keeping this snapshot's flags and
    /// <see cref="StatusCount"/> unchanged â€” see <see cref="SourceAttackIntent"/>'s doc.
    /// Returns this snapshot unchanged if there is no attack component to recompute (a
    /// pure buff/defend/etc. intent) or the recompute fails for any reason.</summary>
    internal EnemyIntentSnapshot RecomputeDamageAgainst(Creature enemy)
    {
        if (SourceAttackIntent == null) return this;
        try
        {
            var combatState = enemy.CombatState;
            var player = combatState?.PlayerCreatures.FirstOrDefault();
            if (player == null) return this;
            decimal perHit = SourceAttackIntent.GetSingleDamage(new[] { player }, enemy);
            int hits = SourceAttackIntent.Repeats;
            decimal total = perHit * Math.Max(hits, 1);
            return new EnemyIntentSnapshot(
                Attack, Defend, Buff, Debuff, StatusCard, Summon, Escape, Heal, Stun,
                perHit, hits, total, StatusCount, SourceAttackIntent);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] EnemyIntentSnapshot.RecomputeDamageAgainst: GetSingleDamage threw for " +
                        $"{enemy.Name}: {ex.Message}");
            return this;
        }
    }
}

/// <summary>
/// Per-run accumulator for observation state the live game exposes no
/// running counter for (the schema audit's ACCUMULATE rows). Reset once per
/// bot run by <see cref="BotController.BeginDriving"/>; the enemy
/// intent-history table is additionally reset at the start of every combat
/// (<see cref="OnCombatStarted"/>) since sts2-rl's own recorder
/// (<c>CombatState._intent_history</c>) is itself a fresh per-combat dict,
/// not a whole-run one â€” carrying one combat's intent history into the next
/// would let two unrelated enemies "inherit" a predecessor's displayed moves.
///
/// â”€â”€ Turn-start hook choice â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
/// <see cref="OnTurnStarted"/> is wired (in <see cref="BotController"/>) to
/// <c>CombatManager.Instance.TurnStarted</c> (a real, public
/// <c>Action&lt;CombatState&gt;</c> event â€” confirmed already exercised by
/// the vendored <c>CardPlayReplayPatch</c>'s own
/// <c>CombatManager.Instance.TurnStarted += _turnStartedHandler</c> wiring,
/// <c>CardPlayReplayPatch.cs:104</c>), NOT derived from
/// <see cref="BotController"/>'s decision-loop settle signal. Two reasons:
/// (1) fidelity â€” sts2-rl's own recorder
/// (<c>CombatState._roll_enemy_intents</c>) snapshots the ABOUT-TO-BE-
/// REPLACED intent at the exact moment the engine rerolls it, which is a
/// per-round engine event, not a "the bot happened to check" event; a
/// decision-loop-only proxy (comparing <c>PlayerCombatState.TurnNumber</c>
/// across settle points) would only approximate that timing and would
/// silently miss combats where BotController's <c>Policy</c> is later
/// swapped for something that doesn't ask for a decision every single
/// settle. (2) precedent â€” <c>CardPlayReplayPatch</c> already proves this
/// exact event is the vendor's own choice of "a new player turn has begun"
/// signal for driving its own dispatch chain, so reusing it keeps SpireBot's
/// two independent turn-boundary consumers agreement-by-construction rather
/// than by coincidence. <c>CombatManager.Instance.CombatSetUp</c>
/// (<c>Action&lt;CombatState&gt;</c>) is the matching combat-start signal
/// for <see cref="OnCombatStarted"/>.
/// </summary>
public sealed class SessionState
{
    public const int MaxIntentHistory = 3;

    // combatId -> most-recent-first history (index 0 = most recently replaced).
    private readonly Dictionary<uint, List<EnemyIntentSnapshot>> _history = new();
    // combatId -> the intent currently on display (about to be pushed into
    // history the NEXT time OnTurnStarted observes it superseded).
    private readonly Dictionary<uint, EnemyIntentSnapshot> _lastDisplayed = new();

    // Unmapped-game-id log-once latch (Contract.VocabId returned PAD for
    // content this contract build doesn't know about). Keyed "kind::gameId"
    // so the same id under two different kinds logs twice (a real, distinct
    // gap each time).
    private readonly HashSet<string> _loggedUnmapped = new();

    // â”€â”€ stable enemy encounter slots (round-3 fix 2, reworked round 8) â”€â”€â”€â”€â”€â”€
    // Game's CombatState.Enemies SHRINKS when a creature is removed (dies), so
    // re-enumerating it every decision reassigns slot numbers out from under
    // consumers (obs writer, ActionMap targeting) the instant an enemy dies â€”
    // the sim instead keeps a dead enemy as a PAD row at its ORIGINAL slot
    // forever (combat.enemies never Remove()s a corpse; see
    // `is_removed_from_combat`).
    //
    // Round-8 fix (Task 2): "original slot" is NOT first-seen order. The sim's
    // `combat.enemies` list starts in `create_monsters()` append order and is
    // periodically re-sorted by `CombatState.sort_enemies_by_slot_name`
    // (combat.py:1354, C# CombatState.cs:495 `SortEnemiesBySlotName`) â€”
    // `_enemies.Sort((a,b) => Encounter.Slots.IndexOf(a.SlotName) -
    // Encounter.Slots.IndexOf(b.SlotName))`, called every time a creature is
    // added with an explicit named slot (`CreatureCmd.Add(..., slotName)` â€”
    // sim's cmds.py:839-841, only reached by the handful of encounters that
    // declare a non-empty `Encounter.Slots` row: Fabricator, Ovicopter,
    // Obscura/Fogmog illusions, Two-Tailed Rat). A creature with no slot name
    // (or a slot not present in the row â€” including "" for a full row) sorts
    // to the FRONT (IndexOf == -1). List.Sort is unstable but every key here
    // is a distinct game-side slot index (or the shared -1 bucket), and
    // Python's stable sort agrees with the game because ties (multiple -1s)
    // keep their prior relative order â€” so we reproduce that here with an
    // explicit first-seen sequence number as the tie-break instead of relying
    // on .NET's unstable List.Sort.
    //
    // We track EVERY enemy CombatId ever seen this combat (never dropped),
    // its first-seen sequence number (tie-break), and its LAST KNOWN
    // Creature.SlotName (kept once a creature leaves CombatState.Enemies).
    // `RecomputeEnemyOrder` is called after every live-roster capture and
    // reproduces `sort_enemies_by_slot_name`'s result exactly â€” safe to call
    // unconditionally every decision since the sort is idempotent (repeating
    // it with unchanged keys is a no-op), so we don't need to track exactly
    // which spawn events would have triggered it in the game.
    private readonly Dictionary<uint, int> _enemySeq = new();
    private readonly Dictionary<uint, string?> _enemySlotName = new();
    private int _nextEnemySeq;
    private readonly Dictionary<uint, int> _enemySlotByCombatId = new();
    private List<uint> _enemySlotOrder = new(); // slot index -> combatId
    private readonly Dictionary<uint, (string Name, string? ModelGameId)> _enemyLastKnown = new();

    // â”€â”€ run.hp freeze (round-3 fix 6) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // sts2-rl freezes run.hp_ratio/run.hp_abs/run.max_hp_abs at the pre-combat value
    // for the WHOLE fight (conformance/runner.py:859,916; run_env.py:1045-1048) â€”
    // separate from combat.player.hp_* which live-tracks. Snapshotted once per combat.
    public int? CombatStartHp { get; private set; }
    public int? CombatStartMaxHp { get; private set; }

    /// <summary>Full reset â€” called once per bot run by <see cref="BotController.BeginDriving"/>.</summary>
    public void ResetRun()
    {
        _history.Clear();
        _lastDisplayed.Clear();

        _loggedUnmapped.Clear();
        _enemySeq.Clear();
        _enemySlotName.Clear();
        _nextEnemySeq = 0;
        _enemySlotByCombatId.Clear();
        _enemySlotOrder.Clear();
        _enemyLastKnown.Clear();
        CombatStartHp = null;
        CombatStartMaxHp = null;
    }

    /// <summary>Per-combat reset of the intent-history table and the stable enemy-slot
    /// map â€” both are fresh per encounter (a new combat's slot 0 is unrelated to the
    /// previous combat's slot 0). The unmapped-id latch deliberately survives combat
    /// boundaries (it is "once per run", per the task brief, not "once per combat").</summary>
    public void OnCombatStarted(ICombatState? combatState)
    {
        _history.Clear();
        _lastDisplayed.Clear();

        _enemySeq.Clear();
        _enemySlotName.Clear();
        _nextEnemySeq = 0;
        _enemySlotByCombatId.Clear();
        _enemySlotOrder.Clear();
        _enemyLastKnown.Clear();
        CombatStartHp = null;
        CombatStartMaxHp = null;

        if (combatState == null) return;
        foreach (var e in combatState.Enemies)
        {
            if (e.CombatId is not uint id) continue;
            GetOrAssignEnemySlot(id);
            UpdateEnemySlotName(id, e.SlotName);
            RecordEnemySeen(id, e.Name, e.Monster?.Id.ToString());
        }
        RecomputeEnemyOrder(combatState.Encounter?.Slots);

        var player = combatState.Players.FirstOrDefault();
        if (player?.Creature != null)
        {
            CombatStartHp = Math.Max(0, player.Creature.CurrentHp);
            CombatStartMaxHp = player.Creature.MaxHp;
        }
    }

    /// <summary>Registers <paramref name="combatId"/>'s first-seen sequence number (the
    /// sort tie-break â€” see the field doc above) if not already tracked, and returns its
    /// CURRENT slot per the last <see cref="RecomputeEnemyOrder"/>. Callers must follow up
    /// with <see cref="UpdateEnemySlotName"/> and a <see cref="RecomputeEnemyOrder"/> pass
    /// once all of this decision's live enemies are registered, since a new arrival can
    /// shift everyone's slot (a named-slot spawn re-sorts the whole roster, exactly like
    /// the sim's <c>sort_enemies_by_slot_name</c>).</summary>
    public int GetOrAssignEnemySlot(uint combatId)
    {
        if (!_enemySeq.ContainsKey(combatId))
            _enemySeq[combatId] = _nextEnemySeq++;
        return _enemySlotByCombatId.TryGetValue(combatId, out int slot) ? slot : _enemySeq[combatId];
    }

    /// <summary>Records the creature's current <c>SlotName</c> (the game-side named-row
    /// slot, e.g. Ovicopter's egg slots) â€” sticky: a null/absent update leaves the
    /// last-known value alone, matching the sim's <c>slot_name</c> field, which is only
    /// ever written by an explicit named-slot add and otherwise persists across turns
    /// (including after death/removal, since it drives sort order for dead-but-retained
    /// PAD rows too).</summary>
    public void UpdateEnemySlotName(uint combatId, string? slotName)
    {
        if (slotName != null)
            _enemySlotName[combatId] = slotName;
        else if (!_enemySlotName.ContainsKey(combatId))
            _enemySlotName[combatId] = null;
    }

    /// <summary>Reproduces <c>CombatState.SortEnemiesBySlotName</c> /
    /// sim's <c>sort_enemies_by_slot_name</c> over every CombatId tracked this combat
    /// (living AND dead-but-retained), and republishes <see cref="EnemySlotOrder"/> /
    /// the CombatId-&gt;slot map from the result. <paramref name="encounterSlots"/> is
    /// <c>ICombatState.Encounter?.Slots</c> â€” null/empty means "no slot rule" (the sim's
    /// no-op branch: <c>encounter.slots</c> empty), which collapses every key to -1 and
    /// this degenerates to pure first-seen order, matching every encounter without a
    /// declared slot row. Idempotent â€” safe to call every capture.</summary>
    public void RecomputeEnemyOrder(IReadOnlyList<string>? encounterSlots)
    {
        if (_enemySeq.Count == 0)
        {
            _enemySlotOrder = new List<uint>();
            _enemySlotByCombatId.Clear();
            return;
        }

        int SlotKey(uint id)
        {
            if (encounterSlots == null || encounterSlots.Count == 0) return -1;
            var name = _enemySlotName.TryGetValue(id, out var n) ? n : null;
            if (name == null) return -1;
            for (int i = 0; i < encounterSlots.Count; i++)
                if (encounterSlots[i] == name)
                    return i;
            return -1; // matching Slots.IndexOf semantics for a not-found name
        }

        var ordered = _enemySeq.Keys
            .OrderBy(SlotKey)
            .ThenBy(id => _enemySeq[id])
            .ToList();

        _enemySlotOrder = ordered;
        _enemySlotByCombatId.Clear();
        for (int i = 0; i < ordered.Count; i++)
            _enemySlotByCombatId[ordered[i]] = i;
    }

    /// <summary>Caches the last-known display name/model id for a CombatId, so a slot
    /// whose creature has been removed from <c>CombatState.Enemies</c> can still report
    /// a PAD row with its last-known identity rather than blank fields.</summary>
    public void RecordEnemySeen(uint combatId, string name, string? modelGameId)
        => _enemyLastKnown[combatId] = (name, modelGameId);

    public bool TryGetEnemyLastKnown(uint combatId, out (string Name, string? ModelGameId) info)
        => _enemyLastKnown.TryGetValue(combatId, out info);

    /// <summary>Slot index -> CombatId, in stable assignment order, for the current combat.</summary>
    public IReadOnlyList<uint> EnemySlotOrder => _enemySlotOrder;

    /// <summary>
    /// Fired by <c>CombatManager.Instance.TurnStarted</c> (see this class's
    /// doc comment). For every living enemy with a CombatId: if it already
    /// had a displayed intent recorded from a PRIOR call (i.e. it has
    /// "performed a full turn" already â€” sts2-rl's
    /// <c>performed_first_move</c> gate), push that prior intent onto its
    /// history (most-recent-first, capped at <see cref="MaxIntentHistory"/>)
    /// before overwriting it with the freshly-rolled intent this call
    /// observes. On an enemy's very first observed turn there is nothing to
    /// push yet â€” only the seed capture happens.
    /// </summary>
    public void OnTurnStarted(ICombatState combatState)
    {
        // Fix 9 (round 5) â€” CombatManager.TurnStarted fires TWICE per round
        // (CombatManager.cs:584 for CombatSide.Player, then again at :593 for
        // CombatSide.Enemy), but enemy.PrepareForNextTurn â€” the actual intent
        // ROLL â€” only runs once per round, inside the Player-side branch
        // (CombatManager.cs:478-484), strictly before the Player-side
        // TurnStarted?.Invoke. The Enemy-side invocation therefore observes
        // the SAME (already-rolled) intent a second time with nothing new to
        // report, yet the old code pushed `prior` into history on both calls
        // â€” a spurious duplicate push every round that shifted the ring one
        // slot relative to sts2-rl's single per-round push
        // (CombatState._roll_enemy_intents, combat.py:575-591). Confirmed by
        // replay_r5_godot.log: enemyId=1/combat_id=21108921 logged TWO
        // "push" lines for round=2 (lines 604 and 634), the second a no-op
        // push of an already-current intent. Gating on CurrentSide ==
        // CombatSide.Player restores 1 push per round.
        if (combatState.CurrentSide != CombatSide.Player) return;

        foreach (var enemy in combatState.Enemies)
        {
            if (enemy.CombatId is not uint id) continue;
            // Round 6 fix 5 â€” `!enemy.IsAlive` wrongly dropped a DEAD-BUT-RETAINED
            // enemy (e.g. a withered segment â€” see the "Death does not mean removal"
            // project note: a creature at 0 HP keeps taking turns and keeps rolling/
            // superseding intents whenever `ShouldCreatureBeRemovedFromCombatAfterDeath`
            // says no). sts2_rl/combat.py's own push loop (combat.py:575-578) mirrors
            // this exactly: `if enemy.is_gone and not enemy.retained_after_death:
            // continue` â€” a dead-but-retained enemy still gets pushed. C#'s
            // `combatState.Enemies` already SHRINKS on a real removal (round-3 fix 2's
            // own doc, above), so anything still present in this list is by
            // construction the "not (is_gone and not retained)" case sim's filter
            // computes explicitly â€” no separate IsAlive gate is needed or correct here.
            // Working hypothesis for the round-6 residual (enemy0=1101 down to
            // enemy4=42 â€” heavily skewed toward slot 0, consistent with segmented/
            // retained-after-death enemies most often occupying the first slot):
            // every dead-but-retained enemy's post-death intent rolls were being
            // silently dropped from history entirely under the old IsAlive gate.

            var snap = EnemyIntentSnapshot.Compute(enemy);
            if (snap == null) continue;

            if (_lastDisplayed.TryGetValue(id, out var prior))
            {
                if (!_history.TryGetValue(id, out var list))
                    _history[id] = list = new List<EnemyIntentSnapshot>();
                // Round-15 amendment to the round-8 push-time recompute: the recompute
                // itself is still required (FlailKnight's WAR_CHANT +3 Strength during
                // ITS OWN enemy turn must show in that turn's row — the round-8 trace),
                // but running it HERE (Player-side TurnStarted) runs it one beat too
                // late: START-OF-PLAYER-TURN effects (933T's Brimstone: +1 Strength to
                // every enemy at player turn start) land before this event fires, so
                // the archived row inflated by a strength that did NOT exist during the
                // recorded turn (933T (1,13)/(1,16)/(2,6): sim 4×7=28 — exactly the
                // value displayed all turn — vs game 5×7=35; the +1-per-hit pattern).
                // sts2-rl's recompute moment (`_roll_enemy_intents`) sits at the round
                // boundary BEFORE any start-of-turn effect. The matching game-side
                // moment is CombatManager.TurnEnded on the enemy→player side switch
                // (CombatManager.cs:1398-1424: fires after the whole enemy phase and
                // its end-of-turn cleanup, before the player-turn-start hook chain), so
                // OnTurnEnded below re-bakes every snapshot's damage there, and this
                // push archives `prior` VERBATIM.
                list.Insert(0, prior);
                if (list.Count > MaxIntentHistory)
                    list.RemoveRange(MaxIntentHistory, list.Count - MaxIntentHistory);

            }
            _lastDisplayed[id] = snap.Value;
        }
    }

    /// <summary>
    /// Round-11 fix â€” seed <c>_lastDisplayed</c> for an enemy first observed
    /// MID-ROUND, i.e. one that was not present at this round's Player-side
    /// <see cref="OnTurnStarted"/> (a transform/spawn during the player's own
    /// phase, e.g. 89U act-0 floor-15's single enemy splitting into four).
    /// Such a creature acts on its spawn-time intent THIS round â€” sim's
    /// <c>performed_first_move</c> is True by the next roll, so sim pushes
    /// that intent into history (combat.py:578-590) â€” but without this seed
    /// C# has no <c>prior</c> at the next TurnStarted and the row is lost
    /// forever (the (0,15) 96-cell all-blank-history residual). Called from
    /// <c>DecisionContext.CaptureEncounterEnemies</c>'s live-roster loop:
    /// any player-phase spawn is necessarily followed by at least one
    /// dispatched command (the player must at minimum end the turn), so
    /// capture always observes it before the reroll. No-op for already-seen
    /// ids, so per-decision calls are idempotent and never overwrite the
    /// TurnStarted-owned current snapshot.
    /// </summary>
    /// <summary>Round-14 generalization (of the round-12 refreshable seed): the
    /// pushable snapshot refreshes for EVERY enemy on EVERY capture, not just for
    /// mid-round spawns. sts2-rl records the intent displayed AT ACT TIME
    /// (`_perform_move` snapshots `current_intent` right before `take_turn`, and the
    /// stunned arm snapshots the stun it displayed â€” combat.py), NOT the roll-time
    /// intent; the two differ whenever a player action changes an enemy's move
    /// mid-round. Round 12's spawn case (transient STUNNED placeholder at first
    /// capture) was one instance; 933T's CeremonialBeast plow-break is the other â€”
    /// the beast rolls PLOW, the player breaks the Plow power mid-turn, its move
    /// becomes the STUN_MOVE, and the sim records a STUN row for that turn while the
    /// frozen roll-time snapshot pushed PLOW+buff (557-cell (0,17) residual). Every
    /// player action is followed by at least one dispatched command (at minimum
    /// EndTurn), so the last capture of the round always observes the final
    /// player-phase display â€” the closest observable proxy for the sim's act-time
    /// snapshot. Decisions only ever happen in the player phase, so a refresh never
    /// reads a mid-enemy-turn transient; a refresh with an unchanged display is a
    /// no-op by value.</summary>
    public void SeedDisplayedIntentIfNew(uint combatId, Creature enemy)
    {
        var snap = EnemyIntentSnapshot.Compute(enemy);
        if (snap != null)
            _lastDisplayed[combatId] = snap.Value;
    }

    /// <summary>
    /// Round-15: the damage-recompute moment for the about-to-be-pushed snapshots —
    /// wired to <c>CombatManager.TurnEnded</c>, which fires on every side switch
    /// (CombatManager.cs:1398-1424). The enemy→player switch (CurrentSide is already
    /// Player when the event fires) is the exact round boundary sts2-rl recomputes
    /// at (`_record_intent_history`'s `preview_incoming_damage`, called from
    /// `_roll_enemy_intents` before any start-of-turn effect): the whole enemy phase
    /// — including same-turn self-buffs like WAR_CHANT — has resolved, but no
    /// player-turn-start effect (Brimstone's enemy Strength) has landed yet. See the
    /// push site in <see cref="OnTurnStarted"/> for the full trace.
    /// </summary>
    public void OnTurnEnded(ICombatState combatState)
    {
        if (combatState.CurrentSide != CombatSide.Player) return;
        foreach (var enemy in combatState.Enemies)
        {
            if (enemy.CombatId is not uint id) continue;
            if (_lastDisplayed.TryGetValue(id, out var snap))
                _lastDisplayed[id] = snap.RecomputeDamageAgainst(enemy);
        }
    }

    /// <summary>Most-recent-first history for one enemy's CombatId, empty if none recorded yet.</summary>
    public IReadOnlyList<EnemyIntentSnapshot> HistoryFor(uint combatId)
        => _history.TryGetValue(combatId, out var list) ? list : Array.Empty<EnemyIntentSnapshot>();

    /// <summary>
    /// Returns true the FIRST time (kind, gameId) is seen unmapped this run
    /// (caller should log); false on every repeat. Callers pass the raw
    /// game-truth id string (e.g. "MONSTER.SOMETHING_UNPORTED") â€” never an
    /// already-PAD'd 0.
    /// </summary>
    public bool ShouldLogUnmapped(string kind, string gameId)
    {
        string key = kind + "::" + gameId;
        return _loggedUnmapped.Add(key);
    }
}
