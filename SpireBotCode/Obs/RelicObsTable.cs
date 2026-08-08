using System;
using System.Collections.Generic;

using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SpireBot.SpireBotCode.Obs;

/// <summary>
/// Faithful C# port of sts2-rl's <c>sts2_rl/relic_obs.py</c> — computes the (counter, flag)
/// aux pair for EXACTLY the 50 relics <c>relic_obs.py</c> admits (its
/// <c>_COUNTER_ONLY</c>/<c>_FLAG_ONLY</c>/<c>_BOTH</c> tables), by concrete relic type. Every
/// entry below is cited against <c>relic_obs.py</c>'s own line for the same id. Any relic NOT
/// in this table returns <c>(0, 0)</c> — this is <c>relic_obs.py</c>'s OWN fallback for
/// unlisted ids (<c>relic_row</c>: "spec = _TABLE.get(id); if spec is None: return (0, 0)"),
/// not an approximation. Round-4's diag4-runlevel.md finding 1 established the OLD generic
/// C# rule (<c>ShowCounter</c>/<c>DisplayAmount</c>/<c>Status</c>, formerly inline in
/// <c>RunObsWriter.WriteRelics</c>/<c>CombatObsWriter.WriteRelics</c>) does not reproduce this
/// table in two independent ways (in-combat gating, raw-vs-modulo'd counters) and must be
/// replaced wholesale by this table, not merged with it — see both writers' call sites.
///
/// COUNTER SOURCE CHOICE: rather than reflecting into each relic's private cumulative field
/// (<c>relic_obs.py</c>'s <c>r.cards_added</c> etc — not exposed publicly by every C# relic
/// class), this reads the relic's own <c>DisplayAmount</c> and applies the SAME modulo/
/// inversion/clamp transform <c>relic_obs.py</c> applies to the raw attribute. For every
/// modulo-based relic, C#'s <c>DisplayAmount</c> ALREADY computes <c>attribute % N</c>
/// internally EXCEPT during the ~1s "IsActivating" visual-pulse window where it transiently
/// returns the un-modulo'd threshold value (N) instead of 0 — this was round 4's confirmed
/// <c>book_of_five_rings</c> root cause (diag4-runlevel.md finding 1B: <c>DisplayAmount==5</c>
/// read exactly at the wrap point, where sim's <c>cards_added % 5 == 0</c>). Re-applying
/// "% N" defensively on top of <c>DisplayAmount</c> is a no-op whenever <c>DisplayAmount</c>
/// is already correct and fixes exactly that transient — the same defensive posture
/// <c>relic_obs.py</c> itself documents taking for <c>pen_nib</c>/<c>joss_paper</c> (module
/// docstring: "this module still applies a defensive % N so a future change... cannot
/// silently regress"). FLAG SOURCE CHOICE: <c>relic_obs.py</c>'s flag citations are ALWAYS
/// either display path 2 (Status tint, <c>RelicModel.Status != Normal</c>) or display path 3
/// (<c>IsUsedUp</c>) — both already public on the <c>RelicModel</c> base and exactly what the
/// citation names, so those are read directly rather than reflecting into whatever private
/// latch field each relic derives its own <c>Status</c>/<c>IsUsedUp</c> override from. The
/// three relics with a directly-public flag field (<c>LavaRock.HasTriggered</c>,
/// <c>VenerableTeaSet</c>/<c>FakeVenerableTeaSet.GainEnergyInNextCombat</c>) read that field
/// instead, since it IS the exact attribute the citation names.
/// </summary>
public static class RelicObsTable
{
    // relic_obs.CLAMP_CEILING (relic_obs.py:115) — the ceiling every admitted counter is
    // clamped to before the caller's own /10 scaling.
    private const int ClampCeiling = 9;

    private static int Clamp(int v) => v < 0 ? 0 : v > ClampCeiling ? ClampCeiling : v;

    // UnsettlingLamp's finished latch (see its table entry) — private, no public
    // non-inverted surface exists, so reflect once and cache.
    private static readonly System.Reflection.FieldInfo? UnsettlingLampFinishedField =
        typeof(UnsettlingLamp).GetField("_isFinishedTriggering",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static bool UnsettlingLampFinished(RelicModel r)
        => UnsettlingLampFinishedField?.GetValue(r) is true;

    private readonly record struct Spec(
        Func<RelicModel, int>? Counter,
        bool CounterInCombatOnly,
        Func<RelicModel, bool>? Flag,
        bool FlagInCombatOnly = false);

    // relic_obs.py's _IN_COMBAT_ONLY_COUNTERS (10, relic_obs.py:226-230) is encoded per-entry
    // below via each Spec's CounterInCombatOnly flag rather than a separate set.
    private static readonly Dictionary<Type, Spec> Table = new()
    {
        // ── _COUNTER_ONLY (28) — relic_obs.py:139-170 ──
        [typeof(Girya)] = new(r => r.DisplayAmount, false, null),                                    // relic_obs.py:140
        [typeof(BookOfFiveRings)] = new(r => r.DisplayAmount % 5, false, null),                      // :142
        [typeof(IronClub)] = new(r => r.DisplayAmount % 4, false, null),                             // :143
        [typeof(FishingRod)] = new(r => r.DisplayAmount % 3, false, null),                           // :144
        [typeof(LastingCandy)] = new(r => r.DisplayAmount % 2, false, null),                         // :145
        [typeof(PaelsWing)] = new(r => r.DisplayAmount % 2, false, null),                            // :146
        [typeof(Nunchaku)] = new(r => r.DisplayAmount % 10, false, null),                            // :147
        // Round-13: same activation-pulse lie as TuningFork (DisplayAmount returns the
        // threshold during the ~1s IsActivating window while a further attack may have
        // already landed — 933T (0,17) idx6, sim=1 game=0); PenNib also exposes its raw
        // counter publicly (`AttacksPlayed`, PenNib.cs:54) — read that.
        [typeof(PenNib)] = new(r => ((PenNib)r).AttacksPlayed % 10, false, null),                    // :148
        // Round-11 fix: DisplayAmount is NOT activation-safe here — during the ~1s
        // IsActivating pulse it returns the threshold (10, TuningFork.cs:25-35), so
        // "% 10" reads 0 even when a FURTHER skill has already landed inside the
        // window (true counter 1 — the 89U (2,13)/(2,15) run.relics.f+26 transient:
        // sim=1, game=0 for exactly one decision). TuningFork uniquely exposes its
        // raw counter publicly (`SkillsPlayed`, TuningFork.cs:60), so read that —
        // it never lies, activation window or not.
        [typeof(TuningFork)] = new(r => ((TuningFork)r).SkillsPlayed % 10, false, null),             // :149
        [typeof(SwordOfStone)] = new(r => r.DisplayAmount, false, null),                             // :150
        [typeof(EmberTea)] = new(r => Math.Max(0, r.DisplayAmount), false, null),                    // :151
        [typeof(Kunai)] = new(r => r.DisplayAmount % 3, true, null),                                 // :152
        [typeof(Kusarigama)] = new(r => r.DisplayAmount % 3, true, null),                            // :153
        [typeof(LetterOpener)] = new(r => r.DisplayAmount % 3, true, null),                          // :154
        [typeof(OrnamentalFan)] = new(r => r.DisplayAmount % 3, true, null),                         // :155
        [typeof(Shuriken)] = new(r => r.DisplayAmount % 3, true, null),                              // :156
        [typeof(VelvetChoker)] = new(r => r.DisplayAmount, true, null),                              // :157 (raw, capped at 6 by mechanics)
        [typeof(DiamondDiadem)] = new(r => r.DisplayAmount, true, null),                             // :158 (raw, clamped by Clamp())
        [typeof(Pocketwatch)] = new(r => r.DisplayAmount, true, null),                               // :159 — round-4-confirmed run.relics-during-combat case
        [typeof(BrilliantScarf)] = new(r => r.DisplayAmount < 5 ? r.DisplayAmount : 0, true, null),  // :160-162
        [typeof(PaelsLegion)] = new(r => Math.Max(0, r.DisplayAmount), true, null),                  // :163
        [typeof(JossPaper)] = new(r => r.DisplayAmount % 5, false, null),                            // :164
        [typeof(FakeHappyFlower)] = new(r => r.DisplayAmount % 5, false, null),                      // :165
        [typeof(HappyFlower)] = new(r => r.DisplayAmount % 3, false, null),                          // :166
        [typeof(Pendulum)] = new(r => r.DisplayAmount % 3, false, null),                             // :167
        [typeof(PollinousCore)] = new(r => Math.Min(r.DisplayAmount, 4), false, null),               // :168
        [typeof(PaelsTooth)] = new(r => r.DisplayAmount, false, null),                               // :169 (len(), never contents)
        [typeof(PumpkinCandle)] = new(r => r.DisplayAmount, false, null),                            // :170 (uncapped, clamped by Clamp())

        // ── _FLAG_ONLY (18) — relic_obs.py:176-194 ──
        [typeof(ArtOfWar)] = new(null, false, r => r.Status != RelicStatus.Normal),                  // :177
        [typeof(BeltBuckle)] = new(null, false, r => r.Status != RelicStatus.Normal),                // :178
        [typeof(BurningSticks)] = new(null, false, r => r.Status != RelicStatus.Normal),             // :179
        [typeof(LizardTail)] = new(null, false, r => r.IsUsedUp),                                    // :180
        [typeof(MawBank)] = new(null, false, r => r.IsUsedUp),                                       // :181
        [typeof(PaelsEye)] = new(null, false, r => r.Status != RelicStatus.Normal),                  // :182
        [typeof(RedSkull)] = new(null, false, r => r.Status != RelicStatus.Normal),                  // :183
        [typeof(RippleBasin)] = new(null, false, r => r.Status != RelicStatus.Normal),               // :184
        [typeof(SilkenTress)] = new(null, false, r => r.IsUsedUp),                                   // :185
        [typeof(ThrowingAxe)] = new(null, false, r => r.Status != RelicStatus.Normal),               // :186
        // Round-13 fix: the generic Status-tint read is INVERTED for this relic — the
        // game sets Status = ACTIVE while the lamp has NOT finished triggering
        // (BeforeCombatStart, UnsettlingLamp.cs:66-67) and back to Normal exactly when
        // it finishes (:142-143), while sim's flag is `_finished` itself
        // (relic_obs.py:186, IsFinishedTriggering). Status!=Normal therefore read 1 for
        // every pre-trigger combat decision where sim read 0 (933T act-1 f13 onward,
        // 384 cells). IsFinishedTriggering is private → reflect the backing field.
        [typeof(UnsettlingLamp)] = new(null, false, UnsettlingLampFinished),                         // :186
        [typeof(Vambrace)] = new(null, false, r => r.Status != RelicStatus.Normal),                  // :188
        [typeof(RainbowRing)] = new(null, false, r => r.Status != RelicStatus.Normal),               // :189
        [typeof(BoneTea)] = new(null, false, r => r.IsUsedUp),                                       // :190
        [typeof(TeaOfDiscourtesy)] = new(null, false, r => r.IsUsedUp),                              // :191
        [typeof(LavaRock)] = new(null, false, r => ((LavaRock)r).HasTriggered),                      // :192
        [typeof(VenerableTeaSet)] = new(null, false, r => ((VenerableTeaSet)r).GainEnergyInNextCombat),      // :193
        [typeof(FakeVenerableTeaSet)] = new(null, false, r => ((FakeVenerableTeaSet)r).GainEnergyInNextCombat), // :194

        // ── _BOTH (4) — relic_obs.py:202-218 ──
        [typeof(SilverCrucible)] = new(r => Math.Max(0, r.DisplayAmount), false, r => r.IsUsedUp),           // :203-206
        [typeof(WongosMysteryTicket)] = new(r => Math.Max(0, r.DisplayAmount), false, r => ((WongosMysteryTicket)r).GaveRelic), // :207-210
        [typeof(ToyBox)] = new(r => ((r.DisplayAmount % 3) + 3) % 3, false, r => r.IsUsedUp),                // :211-213
        [typeof(WingedBoots)] = new(r => Math.Max(0, r.DisplayAmount), false, r => r.IsUsedUp),              // :214-217
    };

    /// <summary>Port of <c>relic_obs.relic_row</c> (relic_obs.py:254-278): <c>(counter, flag)</c>
    /// for one relic instance, the DISPLAYED values, clamped to <c>[0, CLAMP_CEILING]</c>.
    /// <paramref name="inCombat"/> is mandatory so a caller cannot forget the 10 in-combat-only
    /// counter gates by relying on a default — mirrors the python signature's keyword-only
    /// requirement.</summary>
    public static (int counter, int flag) Row(RelicModel relic, bool inCombat)
    {
        if (!Table.TryGetValue(relic.GetType(), out var spec))
            return (0, 0); // relic_obs.py's own fallback for unlisted ids (relic_row, relic_obs.py:266-268)

        int counter = 0;
        if (spec.Counter != null && (inCombat || !spec.CounterInCombatOnly))
            counter = Clamp(spec.Counter(relic));

        // relic_obs.py's relic_row (relic_obs.py:275): when flag_in_combat_only is set on a
        // spec, the FLAG output is forced to 0 in the non-combat (run.relics) block — the
        // flag lambda is not even evaluated there. No currently-ported relic sets this (a
        // grep of relic_obs.py found zero `flag_in_combat_only=True` assignments as of this
        // port), but the gate is wired so a future relic addition can't silently regress it.
        int flag = 0;
        if (spec.Flag != null && (inCombat || !spec.FlagInCombatOnly))
            flag = spec.Flag(relic) ? 1 : 0;

        return (counter, flag);
    }
}
