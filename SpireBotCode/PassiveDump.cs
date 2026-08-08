using System;
using System.Linq;
using System.Reflection;

using Godot;
using HarmonyLib;

using MegaCrit.Sts2.Core.Combat;

using SpireBot.Replay;
using SpireBot.Replay.Commands;
using SpireBot.SpireBotCode.Obs;
using SpireBot.SpireBotCode.Policies;

// Obs.Obs collides with the SpireBot.SpireBotCode.Obs namespace itself once both are in
// scope (CS0118) — alias it, matching BotController.cs/DecisionDumper.cs's identical pattern.
using ObsResult = SpireBot.SpireBotCode.Obs.Obs;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Task 16 dev-only mode: observes a run driven entirely by the SEPARATE RunReplays fork mod
/// (its own assembly, playing back a recorded run from disk) and dumps one decision line per
/// replay action via the existing <see cref="DecisionDumper"/> pipeline — without choosing or
/// dispatching anything itself. Everything the fork's own <c>ReplayDispatcher</c> does
/// (advancing the run, recording its own log) is untouched; this class only watches.
///
/// The fork's types (<c>RunReplays.ReplayDispatcher</c>, <c>RunReplays.ReplayEngine</c>,
/// <c>RunReplays.Commands.ReplayCommand</c>) are DIFFERENT CLR types from the vendored
/// <see cref="SpireBot.Replay"/> ones despite sharing names — everything that touches them
/// here goes through reflection/<c>object</c>, never a cast to a vendored type.
/// </summary>
public static class PassiveDump
{
    private const string HarmonyId = "SpireBot.PassiveDump";

    private static bool _installed;
    private static bool _dead;
    private static object? _lastSeen;
    private static string? _runSeed;
    private static bool _runActive;
    private static bool _warnedDumpMisconfig;

    /// <summary>
    /// True once the Harmony hook on the fork's <c>CheckPreState</c> is installed — independent
    /// of whether <see cref="SpireBotConfig.PassiveDump"/> is currently on. The hook installs
    /// unconditionally at init now (see <see cref="TryInstall"/>); this flag is what
    /// <see cref="SpireBot.Replay.ReplayEngine.PassiveObserving"/> ANDs with the live config
    /// read to decide whether recording is actually happening right now.
    /// </summary>
    public static bool HookInstalled { get; internal set; }

    private static Contract? _contract;
    private static readonly SessionState _session = new();
    private static ObsBuilder? _obsBuilder;
    private static bool _combatHooksSubscribed;
    private static Action<CombatState>? _combatSetUpHandler;
    private static Action<CombatState>? _turnStartedHandler;
    private static Action<CombatState>? _turnEndedHandler;

    private static PropertyInfo? _activeSeedProp;

    /// <summary>Round-5 obs-parity fix: the vendored grid-selection screen an actual
    /// SelectCards decision was already dumped for. <c>NCardGridSelectionScreen</c>
    /// recordings come in two shapes — a single atomic <c>SelectGridCardCommand</c> that
    /// clicks every index AND confirms in one <c>Execute()</c> call, or (older-style /
    /// multi-pick) a run of <c>ClickGridCardCommand</c>s each dispatched separately
    /// followed by a <c>ConfirmGridSelectionCommand</c>. Both shapes must fold into
    /// exactly ONE dumped decision (matching sts2-rl's <c>_card_selector</c>, which asks
    /// once per screen and only loops when a real skippable pick remains) — tracked by
    /// <see cref="SpireBot.Replay.Commands.CardGridScreenCapture.ActiveScreen"/> identity
    /// rather than by command instance, since <see cref="ReferenceEquals(object?, object?)"/>
    /// dedup on <c>__0</c> alone does not catch a freshly re-parsed command instance for
    /// the SAME still-open screen. Reset per <see cref="BeginRun"/>.</summary>
    private static object? _lastSelectCardsScreen;

    /// <summary>FIX 1 residual (round 6, re-keyed round 7, RE-KEYED AGAIN round 8) — dedups a
    /// duplicate back-to-back <c>ClaimRewardCommand</c> re-dispatch of the SAME still-unclaimed
    /// reward BUTTON; see the switch-case doc at the call site. Round 7's content-key (reward
    /// type + underlying model id) was found to over-collide: a legitimate reward screen can
    /// offer two DISTINCT potion buttons of the SAME potion model back to back (89U act0 f11:
    /// Gold/Potion/Potion/Card — two genuinely separate claims), which round 7 silently dropped
    /// as a "duplicate". The dedup is now keyed on the actual button <c>Node</c> reference
    /// (<see cref="SpireBot.Replay.Commands.ClaimRewardCommand.EnumerateRewardButtons"/>'s
    /// <c>button</c> element, not the reward's id) — a genuine duplicate poll of the fork's
    /// double-<c>CheckPreState</c> artifact reports the exact same still-unclaimed button
    /// instance twice (nothing consumed it between polls); two legitimate same-model claims
    /// resolve to two DIFFERENT button instances (the first is consumed/removed from the
    /// screen's enumeration once actually claimed) even though their content key matches.
    /// Reset per <see cref="BeginRun"/>, and — per the "strictly consecutive" artifact shape —
    /// on every dispatch whose command is NOT itself a <c>ClaimRewardCommand</c> (see
    /// <see cref="BeforeExecute"/>), so memory never survives an intervening decision of any
    /// other kind.</summary>
    private static object? _lastClaimedRewardScreen;
    private static object? _lastClaimedRewardButton;

    /// <summary>
    /// Called once from <see cref="MainFile.Initialize"/>, after <c>PatchAll()</c> has
    /// succeeded. ALWAYS installs the Harmony hook regardless of
    /// <see cref="SpireBotConfig.PassiveDump"/>'s current value — the flag used to be read once
    /// here at init time, which meant enabling it later from the in-game config screen could
    /// never take effect (a restart-with-flag-preset trap). Now the hook is a cheap no-op
    /// prefix whenever the flag reads false; <see cref="BeforeExecute"/> re-checks the flag
    /// live on every dispatch, so toggling it mid-session works on the very next dispatch.
    /// </summary>
    public static void TryInstall()
    {
        GD.Print($"[SpireBot] PassiveDump: installing observation hook " +
                 $"(PassiveDump currently {(SpireBotConfig.PassiveDump ? "on" : "off")}).");

        var found = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "RunReplays");
        if (found != null)
        {
            InstallOn(found);
        }
        else
        {
            GD.Print("[SpireBot] PassiveDump: RunReplays assembly not loaded yet — waiting for it " +
                     "(mod load order is not guaranteed).");
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        try
        {
            if (_installed) return;
            if (args.LoadedAssembly.GetName().Name != "RunReplays") return;

            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            InstallOn(args.LoadedAssembly);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.OnAssemblyLoad threw: {ex}");
        }
    }

    private static void InstallOn(Assembly asm)
    {
        try
        {
            if (_installed) return;

            var dispatcherType = asm.GetType("RunReplays.ReplayDispatcher");
            if (dispatcherType == null)
            {
                GD.PrintErr("[SpireBot] PassiveDump: found RunReplays assembly but no " +
                            "RunReplays.ReplayDispatcher type in it — cannot install.");
                return;
            }

            var checkPreState = dispatcherType.GetMethod(
                "CheckPreState", BindingFlags.NonPublic | BindingFlags.Static);
            if (checkPreState == null)
            {
                GD.PrintErr("[SpireBot] PassiveDump: RunReplays.ReplayDispatcher has no " +
                            "private static CheckPreState(ReplayCommand) method — cannot install " +
                            "(fork version mismatch?).");
                return;
            }

            var engineType = asm.GetType("RunReplays.ReplayEngine");
            _activeSeedProp = engineType?.GetProperty("ActiveSeed", BindingFlags.Public | BindingFlags.Static);
            if (_activeSeedProp == null)
            {
                GD.PrintErr("[SpireBot] PassiveDump: could not resolve RunReplays.ReplayEngine.ActiveSeed " +
                            "via reflection — BeginRun detection will never fire, so nothing will be dumped.");
            }

            var prefix = typeof(PassiveDump).GetMethod(
                nameof(BeforeExecute), BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony(HarmonyId).Patch(checkPreState, prefix: new HarmonyMethod(prefix));

            _installed = true;
            HookInstalled = true;
            GD.Print("[SpireBot] PassiveDump installed — observing RunReplays.ReplayDispatcher.CheckPreState.");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.InstallOn threw: {ex}");
        }
    }

    /// <summary>
    /// Harmony prefix on the fork's <c>ReplayDispatcher.CheckPreState(ReplayCommand cmd)</c> —
    /// called exactly once per dispatch attempt, immediately before <c>cmd.Execute()</c>, and
    /// only once the fork has verified the command is dispatchable (game settled, action not
    /// yet applied). That is exactly when the sim would build its obs. <c>__0</c> is Harmony's
    /// positional-parameter convention — kept as <c>object</c> so no fork type appears in this
    /// signature. Always returns implicitly (void) so the original always runs; wrapped
    /// entirely in try/catch so a bug here can never break the fork's own playback.
    /// </summary>
    private static void BeforeExecute(object __0)
    {
        try
        {
            // Live read, not a value captured at install time — this is what lets a mid-session
            // config-screen toggle take effect on the very next dispatch instead of needing a
            // restart. Nothing else in this method runs while off.
            if (!SpireBotConfig.PassiveDump) return;

            if (!_warnedDumpMisconfig &&
                (!SpireBotConfig.DumpDecisions || string.IsNullOrWhiteSpace(SpireBotConfig.DumpDir)))
            {
                _warnedDumpMisconfig = true;
                GD.PrintErr("[SpireBot] PassiveDump: PassiveDump is on but DumpDecisions is off or " +
                            "DumpDir is empty — observing anyway, but every decision will no-op " +
                            "(nothing will actually be written). Set DumpDecisions + DumpDir to get output.");
            }

            if (_dead) return;
            if (__0 == null) return;

            if (ReferenceEquals(__0, _lastSeen))
            {
                // FIX 8 investigation logging (round 4, DEFERRED) — the round-4 dump session's
                // available log did not cover the specific shop floors the diagnosis flagged
                // (act0 f12, act1 f3, act2 f3/f4/f9; the log this round only captured a single
                // shop visit with 3 distinct-title purchases and zero ReferenceEquals dedup
                // hits against them) so hypothesis (a) — pooled/reused shop command instances
                // getting deduped as a false retry — could NOT be confirmed or refuted this
                // round. Logging the concrete type + a hash of the dropped instance so a future
                // dump session covering the flagged floors can directly confirm/refute it.
                GD.Print($"[SpireBot] PassiveDump: same command instance retried — not re-dumping " +
                         $"(type={__0.GetType().Name} hash={__0.GetHashCode()}).");
                return;
            }
            _lastSeen = __0;

            string? seed = _activeSeedProp?.GetValue(null) as string;
            if (seed != _runSeed || !_runActive)
                BeginRun(seed);

            if (_dead) return;

            string cmdTypeName = __0.GetType().Name;

            // Round 8 — the ClaimRewardCommand dedup below (_lastClaimedRewardScreen /
            // _lastClaimedRewardButton) exists to catch ONE narrow artifact: the fork issuing
            // two back-to-back CheckPreState/dispatch calls for the exact SAME still-unclaimed
            // reward button with no intervening command. That shape is strictly consecutive —
            // any other command type passing through here (map move, event choice, another
            // reward's TakeCard follow-up, etc.) proves the previous claim was NOT immediately
            // re-polled, so the memory can never legitimately apply to whatever ClaimReward
            // comes next and must not be allowed to leak into it.
            if (cmdTypeName != "ClaimRewardCommand")
            {
                _lastClaimedRewardScreen = null;
                _lastClaimedRewardButton = null;
            }

            switch (cmdTypeName)
            {
                case "ProceedToMapCommand":
                case "ProceedToNextActCommand":
                case "OpenShopCommand":
                case "OpenFakeShopCommand":
                // FIX 2 (round 4) — SkipRewardsCommand is the reward-screen "leave/click-
                // through when buttons remain unclaimed" action (fires RewardSkippedFrom for
                // every un-claimed reward button), the exact same interstitial-click shape as
                // ProceedToMapCommand/ProceedToNextActCommand above: sim's driver terminates
                // at the REWARD_CARD/REWARD_POTION/REWARD_RELIC decision itself and never asks
                // a separate "click through the leave button" decision, so this command has no
                // sim-side counterpart and must not be dumped.
                case "SkipRewardsCommand":
                    GD.Print($"[SpireBot] PassiveDump skip interstitial cmd={cmdTypeName}");
                    return;
                case "ChooseEventOptionCommand":
                    if (TryGetRecordedIndex(__0, out int recordedIndex))
                    {
                        if (recordedIndex < 0)
                        {
                            GD.Print("[SpireBot] PassiveDump skip interstitial cmd=ChooseEventOptionCommand " +
                                     "(finished-event proceed)");
                            return;
                        }
                        // FIX 8 (round-3-reworked) — the option-count gate below was WRONG:
                        // sts2_rl/driver.py's _run_event asks a decision on every loop
                        // iteration with no option-count check, so act-1/act-2 shrine events
                        // (which can also present few options) DO get real sim lines and a
                        // blanket "<=1 option" skip would have broken those joins. The actual
                        // sim-side carve-out is narrower: conformance/runner.py:15-19 passes
                        // include_neow=False, meaning the sim structurally never asks a
                        // decision for the act-0 Neow/ancient-node event specifically — not
                        // "any event with one option." Mirror that exactly: skip only when
                        // this is the act-0 ancient-node event.
                        if (IsAct0AncientNodeEvent())
                        {
                            GD.Print("[SpireBot] PassiveDump skip interstitial cmd=ChooseEventOptionCommand " +
                                     "(act-0 ancient/Neow node — sim's include_neow=False)");
                            return;
                        }
                        // Note: the act-2 floor-16 event lines the diagnosis flagged as
                        // "missing in sim" are NOT a single-option-event artifact — they're a
                        // sim replay-coverage tail boundary (the recorded sim replay ends
                        // after the floor-15 combat, one floor short of the game's floor-16
                        // event), so no C# skip applies to them.
                    }
                    else
                    {
                        // Fail-open per the task brief: can't determine option count reliably
                        // either, so dump rather than risk silently dropping a real choice.
                        GD.PrintErr("[SpireBot] PassiveDump: could not reflect ChooseEventOptionCommand." +
                                    "RecordedIndex — dumping anyway (fail-open).");
                    }
                    break;
                case "MapMoveCommand":
                    // FIX 3 (round 4) — mirrors sts2_rl/conformance/runner.py:524
                    // _consume_ancient_map_move: the sim's scripted act-entry sequence
                    // (ProceedToNextAct / VoteForMapCoordAction / MoveToMapCoord <startCol> /
                    // ChooseEventOption <shrine> / MoveToMapCoord <first room>) eats the first
                    // MoveToMapCoord — the one that walks onto the new act's Ancient node — and
                    // never raises a decision for it. Same two-signal fail-open shape as
                    // IsAct0AncientNodeEvent below, generalized to act>=1's map-move equivalent.
                    if (IsActEntryAncientNodeMove(__0))
                    {
                        GD.Print("[SpireBot] PassiveDump skip interstitial cmd=MapMoveCommand " +
                                 "(act-entry ancient-node move — sim's _consume_ancient_map_move)");
                        return;
                    }
                    break;
                case "ClaimRewardCommand":
                    // FIX 7 (round 4), NARROWED round 5 — mirrors sim_obs_dump.py: the sim
                    // only raises a decision for a real reward CHOICE
                    // (REWARD_CARD/REWARD_POTION/REWARD_RELIC), one `_ask` per reward GROUP
                    // (driver.py `_offer_rewards`/`_offer_card_group`/`_reward_selector`).
                    // Cross-checking the vendored commands' own doc comments settles which
                    // game-side button click that is: ClaimRewardCommand's doc says "For
                    // card rewards, this opens the card selection screen; a TakeCard command
                    // follows to select the actual card" — so for a CardReward button, the
                    // ACTUAL pick-a-card decision (matching sim's REWARD_CARD, which offers
                    // the specific cards directly) is the TakeCardCommand dispatch that
                    // follows, not this one; clicking the CardReward button itself is a pure
                    // subscreen-open interstitial, same shape as OpenChestCommand below. Only
                    // a RelicReward/PotionReward button grants immediately with no subscreen
                    // (matching REWARD_RELIC/REWARD_POTION's single take-or-skip ask) and
                    // stays dumped here. Gold/unresolvable stays the round-4 skip. Fail-open
                    // toward DUMPING only when the button/reward can't be resolved at all
                    // (screen freed, index out of range, etc — see
                    // IsChoiceBearingRewardClaim's own doc).
                    if (!IsChoiceBearingRewardClaim(__0))
                    {
                        GD.Print("[SpireBot] PassiveDump skip interstitial cmd=ClaimRewardCommand " +
                                 "(gold auto-claim, or a CardReward subscreen-open — the real pick is " +
                                 "the TakeCardCommand that follows)");
                        return;
                    }
                    // FIX 1 residual (round 6) — investigating the act0 f13 idx2 "missing in
                    // sim" line (task brief flagged it as possibly NOT a treasure case)
                    // found act0 f13's game dump carries TWO byte-identical ClaimRewardCommand
                    // dumps back to back (idx0/idx1 — same screen, same RewardIndex, same
                    // resolved potion id 29, same phase one-hot) immediately before the real
                    // TakeCardCommand pick (idx2). Sim only ever asks ONE decision per reward
                    // group (driver.py _offer_rewards/_offer_potion), so this is a genuine
                    // duplicate re-dispatch of the SAME still-unclaimed button — the fork
                    // (RunReplays, a separate assembly we don't own) evidently issues two
                    // CheckPreState calls for it with two distinct command instances, which
                    // defeats the ReferenceEquals _lastSeen dedup above. Close it the same way
                    // _lastSelectCardsScreen closes the analogous grid-screen double-dispatch:
                    // dedup on (screen identity, RewardIndex) instead of command identity.
                    //
                    // ROUND 7 — this still missed the act2 f7 reward_relic#1 duplicate (relic
                    // id 50 dumped twice, decision_index 0 and 1, byte-identical f/i arrays).
                    // Root cause, found by reading IsChoiceBearingRewardClaim (below) side by
                    // side with this block: BOTH functions independently re-fetch
                    // ReplayState.ActiveRewardsScreen and re-run EnumerateRewardButtons, but
                    // IsChoiceBearingRewardClaim fails OPEN (dumps) whenever the screen can't
                    // be resolved yet, while this dedup block only *records* the pair when
                    // `screenNow != null` — so a dispatch that hits IsChoiceBearingRewardClaim's
                    // fail-open window (screen not yet valid/in-tree) dumps WITHOUT ever being
                    // recorded, leaving the very next (now-resolved) dispatch of the identical
                    // still-unclaimed button with nothing to dedup against. RewardIndex alone is
                    // also not a safe key on its own: a re-dispatch after the screen's button
                    // list has been rebuilt could resolve to a different index for the SAME
                    // unclaimed reward. Fix: key the dedup on the RESOLVED REWARD CONTENT
                    // (reward type name + underlying RelicModel/PotionModel id) instead of raw
                    // RewardIndex, and record it any time it resolves — not only when the
                    // earlier screenNow-must-be-non-null gate happened to pass. This still lets
                    // a legitimate multi-relic/potion screen dump each distinct reward (idx0
                    // claims relic A, idx1 legitimately offers a different relic B — different
                    // content key, not deduped).
                    // ROUND 8 — round 7's content-key rework (see the field doc above) still
                    // over-collided: 89U act0 f11 records Gold/Potion/Potion/Card, two DISTINCT
                    // potion buttons of the same model claimed back to back, and the content key
                    // ("PotionReward:{id}") is identical for both, silently dropping the second
                    // real claim. Re-keyed on the resolved button's Node REFERENCE instead — a
                    // genuine duplicate poll reports the exact same still-unclaimed button
                    // instance twice (the fork's double-CheckPreState artifact fires before
                    // anything has consumed it); two legitimate claims of matching content
                    // resolve to two different button instances because the first is
                    // removed/consumed from the screen's own enumeration once actually claimed.
                    // Only recorded once this claim is actually going to be dumped (never on a
                    // fail-open/unresolved claim — ResolveRewardButton returns null for those,
                    // same fail-open shape as every other helper here), so a skipped/unresolved
                    // claim can never poison the memory for whatever comes next.
                    {
                        object? screenNow = SpireBot.Replay.ReplayState.ActiveRewardsScreen;
                        object? buttonNow = ResolveRewardButton(__0);
                        if (buttonNow != null && screenNow != null)
                        {
                            if (ReferenceEquals(screenNow, _lastClaimedRewardScreen) &&
                                ReferenceEquals(buttonNow, _lastClaimedRewardButton))
                            {
                                GD.Print("[SpireBot] PassiveDump skip interstitial cmd=ClaimRewardCommand " +
                                         "(duplicate re-dispatch of the same still-unclaimed reward button " +
                                         "on the same screen — fork double-CheckPreState artifact)");
                                return;
                            }
                            _lastClaimedRewardScreen = screenNow;
                            _lastClaimedRewardButton = buttonNow;
                        }
                    }
                    break;
                case "OpenChestCommand":
                    // Round 5 — same shape as the ClaimRewardCommand/CardReward case just
                    // above: OpenChestCommand's own doc says "Emits the chest button's
                    // Released signal to trigger OpenChest(). A TakeChestRelic command
                    // follows to pick the relic." Opening the chest is a pure subscreen-open
                    // interstitial with no sim-side ask; TakeChestRelicCommand handles the
                    // actual relic grant.
                    GD.Print("[SpireBot] PassiveDump skip interstitial cmd=OpenChestCommand " +
                             "(subscreen-open — the chest relic is auto-granted by TakeChestRelicCommand, " +
                             "which sim never asks a decision for either — see FIX 1 below)");
                    return;
                case "TakeChestRelicCommand":
                    // FIX 1 (round 6) — sts2_rl/driver.py's `_resolve_room` TREASURE branch is
                    // a one-line comment: "The chest's own gold + relic is resolved inside
                    // enter_point; MAP is a no-op in the sim." — i.e. `RunState.enter_point`
                    // grants the chest's gold+relic directly with NO `_ask` at all (no
                    // REWARD_RELIC decision), confirmed by the vendored command's own doc
                    // ("Always picks relic at index 0 (treasure chests offer one relic)" —
                    // there is no real choice to make; the same auto-grant TreasureRoom.cs
                    // performs in the real game). The task brief's 4 flagged floors (act0 f10,
                    // act0 f13 idx2, act1 f9, act2 f8) turned out on inspection to be a mixed
                    // bag: f10/f9/f8 (act0/1/2) genuinely ARE bare TakeChestRelic auto-grants
                    // with no other reward on the screen; act0 f13 idx2 is actually a Combat
                    // floor's card-reward TakeCardCommand pick (unrelated to treasure — its
                    // "missing in sim" status was really caused by a duplicate ClaimReward
                    // dispatch shifting its index, fixed just above). Skip TakeChestRelicCommand
                    // unconditionally, same interstitial shape as OpenChestCommand.
                    GD.Print("[SpireBot] PassiveDump skip interstitial cmd=TakeChestRelicCommand " +
                             "(treasure-chest relic is auto-granted — sim's RunState.enter_point never " +
                             "asks a REWARD_RELIC decision for it, driver.py's TREASURE branch is a no-op)");
                    return;
                case "ConfirmGridSelectionCommand":
                case "CancelGridSelectionCommand":
                    // Round 5 — the confirm/cancel button-press step of the two-command
                    // (ClickGridCardCommand.. + Confirm/Cancel) recording shape for a grid
                    // selection screen. The atomic SelectGridCardCommand.Execute() already
                    // clicks every index AND confirms in one call, so wherever the recording
                    // used the split shape instead, the pick itself was already captured at
                    // the (first) ClickGridCardCommand dispatch (see the
                    // _lastSelectCardsScreen dedup below) — this confirm/cancel click is a
                    // pure follow-up UI press with no sim-side ask of its own.
                    GD.Print($"[SpireBot] PassiveDump skip interstitial cmd={cmdTypeName} " +
                             "(grid-screen confirm/cancel — the pick was already dumped at ClickGridCardCommand)");
                    return;
                case "SelectGridCardCommand":
                case "ClickGridCardCommand":
                {
                    // Round 5 — fold every click belonging to the SAME still-open grid
                    // screen into one dumped decision (see _lastSelectCardsScreen's doc).
                    var screen = CardGridScreenCapture.ActiveScreen;
                    if (screen != null && ReferenceEquals(screen, _lastSelectCardsScreen))
                    {
                        GD.Print($"[SpireBot] PassiveDump skip interstitial cmd={cmdTypeName} " +
                                 "(same grid screen already dumped this decision)");
                        return;
                    }
                    _lastSelectCardsScreen = screen;
                    break;
                }
                case "UsePotionCommand":
                case "DiscardPotionCommand":
                    // Round 5 — sts2_rl/driver.py never asks a standalone decision for an
                    // out-of-combat potion use: `DecisionRequest.potion_actions()` (driver.py
                    // ~186-211) folds every AnyTime belt potion into the ACTION SPACE of
                    // whatever decision is already pending (Map/Event/Shop/Rest/...), it is
                    // never its own `_ask`. An out-of-combat UsePotionCommand/
                    // DiscardPotionCommand dispatch therefore has no sim-side line at all —
                    // skip it (this also retires the round-4 "Unsupported at
                    // UsePotionCommand, dropped during a room transition" investigation log:
                    // that drop was CORRECT behavior, just previously logged as a mystery
                    // instead of a deliberate skip). An in-combat potion use is a real
                    // COMBAT decision (sim's per-turn action space) and falls through to the
                    // command-kind classification below.
                    if (!(MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress ?? false))
                    {
                        GD.Print($"[SpireBot] PassiveDump skip interstitial cmd={cmdTypeName} " +
                                 "(out-of-combat potion action — sim folds this into the surrounding " +
                                 "decision's own action space, driver.py potion_actions(), never a " +
                                 "standalone ask)");
                        return;
                    }

                    // Investigation (act1 f10 idx=9, f16 idx=0 — ~260 field mismatches all
                    // concentrated at these two decisions): UsePotionCommand.Execute() itself
                    // (Replay/Commands/UsePotionCommand.cs:52-54) waits for
                    // player.PlayerCombatState.Phase == PlayerTurnPhase.Play before doing
                    // anything and otherwise returns ExecuteResult.Retry(200) — i.e. every
                    // dispatch attempt that lands before the play phase (during combat-setup /
                    // pre-initial-draw, or during the enemy's turn between EndTurn and the next
                    // draw) is a no-op retry loop, not a real decision. This BeforeExecute hook
                    // fires on EVERY such dispatch attempt though, so each retry got dumped as
                    // its own spurious Combat decision line (hand empty, mask offering only
                    // potion slots) with no counterpart in sim's stream — sim's driver.py only
                    // ever asks a combat decision once Phase.Play is reached. Skip here too,
                    // mirroring UsePotionCommand's own gate exactly; the real ask (if any) is
                    // captured once Phase reaches Play and the command actually executes.
                    if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.DebugOnlyGetState()?
                            .Players.FirstOrDefault()?.PlayerCombatState?.Phase
                        != MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play)
                    {
                        GD.Print($"[SpireBot] PassiveDump skip interstitial cmd={cmdTypeName} " +
                                 "(in-combat potion action dispatched before PlayerTurnPhase.Play — " +
                                 "command's own Execute() would just retry; no sim-side ask exists " +
                                 "for this pre-draw/enemy-turn window, see UsePotionCommand.cs:52-54)");
                        // Un-mark the instance: _lastSeen was already set at the top of this
                        // method, and the fork RETRIES the SAME command instance until Phase
                        // reaches Play — without this reset the eventual real (Play-phase)
                        // dispatch would be swallowed by the ReferenceEquals retry dedup and
                        // the decision line lost entirely (the round-10 regression that
                        // re-orphaned sim's (1,10) idx17 / (1,16) idx31 potion decisions).
                        _lastSeen = null;
                        return;
                    }
                    break;
            }

            // Round 5 — classify by the FORK COMMAND TYPE being dispatched (known exactly,
            // right here) instead of DecisionContext.Classify's screen-based heuristic. The
            // screen heuristic is unfixable for the passive dump: a decision can be
            // mislabeled by a stale-but-still-enumerable screen reference from the room the
            // bot is STILL standing in (e.g. the Map decision right after a rest-site Smith
            // upgrade, dispatched before any RoomExited signal) — no amount of "clear stale
            // refs on room exit" (rounds 2-4) can fix a decision that fires before the room
            // has exited. DecisionContext.Classify itself is untouched and still used
            // exactly as before for the live bot (BotController never passes an override).
            DecisionKind? cmdKind = ClassifyCommandKind(cmdTypeName);
            if (cmdKind == null)
            {
                // Fail LOUDLY: an unmapped command type (e.g. CrystalSphereClickCommand) falls
                // back to the screen-based Classify this redesign exists to avoid — the line
                // still dumps, but its kind is untrusted until the mapping is extended.
                GD.PrintErr($"[SpireBot] PassiveDump: no command-kind mapping for cmd={cmdTypeName} " +
                            "— falling back to screen-based Classify (kind untrusted; extend ClassifyCommandKind).");
            }

            // Round 8 fix 1 — the CLAIMED button's own reward type (not screen priority),
            // see DecisionContext.RewardKindOverride's doc for why this exists.
            RewardKind? rewardKindOverride = cmdTypeName == "ClaimRewardCommand"
                ? ResolveClaimedRewardKind(__0)
                : null;
            // Fix (reward.relic/reward.potion content bug) — the CLAIMED button's own INDEX,
            // see DecisionContext.RewardButtonIndexOverride's doc: RewardKindOverride alone
            // only fixes which sub-kind a decision reports, not which specific button's
            // model content RunObsWriter reads when more than one button of that kind is on
            // screen (or a claimed button lingers un-removed).
            int? rewardButtonIndexOverride = cmdTypeName == "ClaimRewardCommand"
                ? ResolveClaimedRewardIndex(__0)
                : null;

            DecisionContext ctx;
            try
            {
                ctx = DecisionContext.Capture(_session, cmdKind, rewardKindOverride, rewardButtonIndexOverride);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SpireBot] PassiveDump: DecisionContext.Capture threw for cmd={cmdTypeName}: {ex}");
                return;
            }

            if (ctx.Kind == DecisionKind.Unsupported)
            {
                // FIX 7 investigation logging (round 4), RESOLVED round 5 — round 4 confirmed
                // the dropped command type at both observed drop sites was UsePotionCommand,
                // firing during a room transition (combat not yet IsInProgress), and left
                // open whether sim has ANY decision for an out-of-combat potion use. Answer
                // (sts2_rl/driver.py's DecisionRequest.potion_actions(), ~186-211): no — an
                // AnyTime belt potion is folded into whatever decision is ALREADY pending as
                // extra action-space, never its own `_ask`. So the round-4 drop was correct
                // behavior; it is now an explicit skip in the switch above (case
                // "UsePotionCommand"/"DiscardPotionCommand", out-of-combat branch) instead of
                // reaching this Unsupported fallback at all. Likewise the two round-5 task-
                // brief floors (act1 f10 idx=17, f16 idx=31) were the mirror case — an
                // IN-COMBAT UsePotionCommand as a combat's LAST decision with no
                // PlayCard/EndTurn also available, which the old screen-based Classify had no
                // case for either; ClassifyCommandKind now maps it straight to Combat. This
                // Unsupported branch should be effectively dead for every command type this
                // round's fix set covers; it is kept as a fail-open diagnostic for whatever
                // command type shows up next (e.g. CrystalSphereClickCommand, never observed
                // on either fixture seed) rather than silently dropping it.
                bool combatInProgress = MegaCrit.Sts2.Core.Combat.CombatManager.Instance?.IsInProgress ?? false;
                string? phase = null;
                try
                {
                    phase = MegaCrit.Sts2.Core.Combat.CombatManager.Instance?
                        .DebugOnlyGetState()?.Players.FirstOrDefault()?.PlayerCombatState?.Phase.ToString();
                }
                catch { /* best-effort diagnostic only */ }
                GD.PrintErr($"[SpireBot] PassiveDump: Unsupported at cmd={cmdTypeName} — line skipped " +
                            $"(combatInProgress={combatInProgress} phase={phase ?? "n/a"} " +
                            $"available=[{string.Join(",", ctx.Available.Select(a => a.GetType().Name))}])");
                return;
            }

            if (_contract == null || _obsBuilder == null)
                return; // BeginRun failed and marked us dead, or never ran; nothing to build against.

            var map = ActionMap.Build(_contract, ctx, _session);
            var obs = _obsBuilder.Build(ctx);
            DecisionDumper.Write(ctx, obs, map, new PolicyResult { ActionId = -1 });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.BeforeExecute threw (swallowed — must not break the fork's playback): {ex}");
        }
    }

    /// <summary>Round-5 command-driven kind map — see the call site's doc. Returns null for
    /// a command type this map has no opinion on (falls back to
    /// <see cref="DecisionContext.Classify"/> inside <see cref="DecisionContext.Capture"/>,
    /// fail-open the same way an unrecognized case always has been). Every case here is
    /// cross-checked against <c>sts2_rl/live/sim_obs_dump.py</c>'s documented "KIND MAPPING"
    /// table and the vendored command classes' own doc comments (see the switch above for
    /// the reward/grid-selection carve-outs already handled before this is ever called).
    /// UsePotionCommand/DiscardPotionCommand only reach here already known to be IN COMBAT
    /// (the out-of-combat case returns early, above) — so they always classify as Combat.</summary>
    private static DecisionKind? ClassifyCommandKind(string cmdTypeName) => cmdTypeName switch
    {
        "MapMoveCommand" => DecisionKind.Map,
        "ChooseEventOptionCommand" => DecisionKind.Event,
        "ChooseRestSiteOptionCommand" => DecisionKind.Rest,
        "BuyCardCommand" or "BuyRelicCommand" or "BuyPotionCommand"
            or "BuyCardRemovalCommand" or "CloseShopCommand" => DecisionKind.Shop,
        "PlayCardCommand" or "EndTurnCommand" or "UsePotionCommand" or "DiscardPotionCommand"
            => DecisionKind.Combat,
        // TakeChestRelicCommand is not listed here — round 6 fix 1 now returns/skips it
        // in the switch above before classification is ever reached (it never has a
        // sim-side ask). Kept out of this map entirely so it can't silently regain a
        // classification if that early-return is ever removed without noticing.
        "TakeCardCommand" or "ClaimRewardCommand"
            => DecisionKind.RewardScreen,
        "SelectGridCardCommand" or "ClickGridCardCommand"
            or "SelectHandCardsCommand" or "SelectCardFromScreenCommand" => DecisionKind.SelectCards,
        _ => null,
    };

    private static bool TryGetRecordedIndex(object cmd, out int value)
    {
        value = 0;
        var prop = cmd.GetType().GetProperty("RecordedIndex", BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || prop.PropertyType != typeof(int))
            return false;
        value = (int)prop.GetValue(cmd)!;
        return true;
    }

    private static readonly PropertyInfo? RunStateProp =
        typeof(MegaCrit.Sts2.Core.Runs.RunManager).GetProperty("State",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>FIX 8 helper (round-3-reworked) — true only for the act-0 Neow/ancient-node
    /// event, matching sim's <c>include_neow=False</c> (conformance/runner.py:15-19), which
    /// structurally skips exactly that one event, not "any event with few options."
    /// Uses two independent game-side signals so a change to either alone can't silently
    /// widen or narrow the skip: (1) <see cref="MegaCrit.Sts2.Core.Runs.IRunState.CurrentActIndex"/>
    /// == 0 (the same act-index field <c>GameStateSnapshot.Act</c> already reads), and
    /// (2) the event's canonical type is <c>AncientEventModel</c> — the exact type the game
    /// itself uses to identify the ancient/starting-bonus node
    /// (<c>StartingBonusReplayPatch.cs</c> keys off the same type for the same node).
    /// Reads the VENDORED <c>ReplayState.ActiveEventSynchronizer</c> (already populated during
    /// fork-driven playback — see class doc), not the fork's own event state. Fail-open:
    /// returns false (treated as "dump it") whenever either signal can't be determined.</summary>
    private static bool IsAct0AncientNodeEvent()
    {
        try
        {
            var state = RunStateProp?.GetValue(MegaCrit.Sts2.Core.Runs.RunManager.Instance)
                as MegaCrit.Sts2.Core.Runs.IRunState;
            if (state == null || state.CurrentActIndex != 0) return false;

            // Round 6 — ADDITIONAL, strictly narrower signal, checked before the
            // ActiveEventSynchronizer lookup below.
            //
            // DIAGNOSIS (verified by reading the patch classes side by side, not guessed):
            // ActiveEventSynchronizer is set by EventOptionReplayPatch, a [HarmonyPostfix] on
            // EventSynchronizer.BeginEvent (EventOptionReplayPatch.cs:16-24). The dispatch that
            // reads it — ReplayDispatcher.DispatchNow()/TryDispatch(), which is what fires
            // RunReplays.ReplayDispatcher.CheckPreState → this class's BeforeExecute prefix →
            // this method — is triggered from a SECOND, entirely separate [HarmonyPostfix] on
            // the SAME original method, StartingBonusReplayPatch.cs:14-24 (guarded to
            // AncientEventModel only, which the act-0 Neow node is). Harmony gives no ordering
            // guarantee between two independent postfix patch classes on one target absent an
            // explicit HarmonyPriority/before/after — whichever PatchAll() happens to apply
            // second runs second. When StartingBonusReplayPatch's postfix (dispatch) runs
            // BEFORE EventOptionReplayPatch's postfix (sets ActiveEventSynchronizer) — which is
            // exactly what a fresh run's very first event, the act-0 ancient node, hits, since
            // nothing else has forced patch-application order between the two classes by then —
            // this method reads ActiveEventSynchronizer while it is still null (or stale from a
            // previous run), so the check below silently fails open (dumps it). This is exactly
            // the task brief's hypothesis, confirmed here rather than assumed.
            //
            // ActFloor 1 needs neither postfix to have run first — it is read straight off
            // IRunState, not off any patch-populated cache — so add it as an independent,
            // strictly-narrower-than-"any Event" skip signal: floor 1 of act 0 (checked via
            // CurrentActIndex == 0 above) is ALWAYS the ancient/Neow node (see the "Ancient node
            // is an event room" project note — every act's Ancient/Neow/Orobas/Tanx node is the
            // first floor of that act), so this can never fire for a normal event. Every other
            // act/floor combination is untouched — fail-open below is unchanged for them.
            if (state.ActFloor == 1) return true;

            var sync = SpireBot.Replay.ReplayState.ActiveEventSynchronizer;
            if (sync == null || sync.Events.Count == 0) return false;
            return sync.Events[0] is MegaCrit.Sts2.Core.Models.AncientEventModel;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.IsAct0AncientNodeEvent threw: {ex.Message} — fail-open (dumping).");
            return false;
        }
    }

    /// <summary>FIX 3 helper (round 4) — true only for the act-entry <c>MapMoveCommand</c>
    /// that walks onto a new act's Ancient node, matching sim's <c>_consume_ancient_map_move</c>
    /// (conformance/runner.py:524), which structurally eats exactly that one move as part of
    /// the scripted act-entry sequence and never raises a decision for it. Two independent
    /// game-side signals, same fail-open shape as <see cref="IsAct0AncientNodeEvent"/>:
    /// (1) <see cref="MegaCrit.Sts2.Core.Runs.IRunState.CurrentActIndex"/> &gt;= 1 (act 0 opens
    /// standing ON Neow already — no such move exists there, so this must never fire for act 0),
    /// (2) <c>CurrentMapCoord</c> not yet set (the first move of the act — mirrors
    /// <c>MapMoveCommand.AutoSelectMapNode</c>'s own "first move of the act — row 0" derivation)
    /// AND the row-0 destination's <c>PointType</c> is <c>MapPointType.Ancient</c> (the same type
    /// <c>StandardActMap</c>/<c>SpoilsActMap</c> stamp onto <c>StartingMapPoint</c>). Fail-open:
    /// returns false (dump it) whenever any signal can't be determined.</summary>
    private static bool IsActEntryAncientNodeMove(object cmd)
    {
        try
        {
            var state = RunStateProp?.GetValue(MegaCrit.Sts2.Core.Runs.RunManager.Instance)
                as MegaCrit.Sts2.Core.Runs.IRunState;
            if (state == null || state.CurrentActIndex < 1) return false;
            if (state.CurrentMapCoord.HasValue) return false; // not the first move of the act

            var colProp = cmd.GetType().GetProperty("Col", BindingFlags.Public | BindingFlags.Instance);
            if (colProp == null || colProp.PropertyType != typeof(int)) return false;
            int col = (int)colProp.GetValue(cmd)!;

            var map = state.Map;
            var point = map?.GetPoint(col, 0);
            return point != null && point.PointType == MegaCrit.Sts2.Core.Map.MapPointType.Ancient;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.IsActEntryAncientNodeMove threw: {ex.Message} — fail-open (dumping).");
            return false;
        }
    }

    /// <summary>FIX 7 helper, NARROWED round 5 — true when the reward button
    /// <paramref name="cmd"/> (the fork's own <c>ClaimRewardCommand</c>, reflected for its
    /// <c>RewardIndex</c>) grants immediately with no subscreen (relic/potion), false for a
    /// bare gold auto-claim OR a card reward (which only OPENS the card-pick subscreen —
    /// the real sim-matching decision is the <c>TakeCardCommand</c> that follows; see the
    /// round-5 switch-case doc at the call site). Resolves
    /// the actual reward via the VENDORED <c>ClaimRewardCommand.EnumerateRewardButtons</c>
    /// against the vendored <c>ReplayState.ActiveRewardsScreen</c> (populated in parallel
    /// during fork-driven playback — see class doc), never the fork's own command/screen
    /// types. Fail-open: returns true (dump) whenever the reward can't be resolved at all
    /// (index/property reflection miss, freed screen, ...).</summary>
    private static bool IsChoiceBearingRewardClaim(object cmd)
    {
        try
        {
            var idxProp = cmd.GetType().GetProperty("RewardIndex", BindingFlags.Public | BindingFlags.Instance);
            if (idxProp == null || idxProp.PropertyType != typeof(int))
                return true;
            int index = (int)idxProp.GetValue(cmd)!;

            var screen = SpireBot.Replay.ReplayState.ActiveRewardsScreen;
            if (screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
                return true;

            var buttons = SpireBot.Replay.Commands.ClaimRewardCommand.EnumerateRewardButtons(screen).ToList();
            if (index < 0 || index >= buttons.Count)
                return true;

            string rewardTypeName = buttons[index].reward.GetType().Name;
            return rewardTypeName is "RelicReward" or "PotionReward";
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.IsChoiceBearingRewardClaim threw: {ex.Message} — fail-open (dumping).");
            return true;
        }
    }

    /// <summary>Round 8 — resolves the SAME (screen, RewardIndex) → reward-button lookup
    /// <see cref="IsChoiceBearingRewardClaim"/> already does, but returns the button <c>Node</c>
    /// instance itself (replacing round 7's <c>ResolveRewardContentKey</c>, which returned a
    /// content key of reward-type + underlying model id and over-collided: two DISTINCT reward
    /// buttons offering the same potion model resolve to the same content key but are different
    /// button instances). Used by the dedup block at the <c>ClaimRewardCommand</c> switch case.
    /// Returns null (never dedup-recorded — fail-open, same shape as every other helper here)
    /// whenever the screen/button can't be resolved.</summary>
    private static object? ResolveRewardButton(object cmd)
    {
        try
        {
            var idxProp = cmd.GetType().GetProperty("RewardIndex", BindingFlags.Public | BindingFlags.Instance);
            if (idxProp == null || idxProp.PropertyType != typeof(int))
                return null;
            int index = (int)idxProp.GetValue(cmd)!;

            var screen = SpireBot.Replay.ReplayState.ActiveRewardsScreen;
            if (screen == null || !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
                return null;

            var buttons = SpireBot.Replay.Commands.ClaimRewardCommand.EnumerateRewardButtons(screen).ToList();
            if (index < 0 || index >= buttons.Count)
                return null;

            return buttons[index].button;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.ResolveRewardButton threw: {ex.Message} — fail-open (not deduped).");
            return null;
        }
    }

    /// <summary>Round 8 fix 1 — resolves the SAME (screen, RewardIndex) lookup
    /// <see cref="ResolveRewardButton"/> and <see cref="IsChoiceBearingRewardClaim"/> already
    /// do, but returns the CLAIMED button's own reward TYPE as a <see cref="RewardKind"/>
    /// instead of the button <c>Node</c> or a bool — this is what gets threaded through as
    /// <see cref="DecisionContext.RewardKindOverride"/> so <c>RunObsWriter</c> reports the
    /// sub-kind that was actually claimed rather than picking by screen priority among every
    /// button present. Fail-open: returns null (no override — falls back to the pre-existing
    /// screen-priority heuristic) whenever the reward can't be resolved.</summary>
    private static RewardKind? ResolveClaimedRewardKind(object cmd)
    {
        try
        {
            var idxProp = cmd.GetType().GetProperty("RewardIndex", BindingFlags.Public | BindingFlags.Instance);
            if (idxProp == null || idxProp.PropertyType != typeof(int))
                return null;
            int index = (int)idxProp.GetValue(cmd)!;

            // fix-reward-loop-brief.md — the screen/button → RewardKind lookup this used to do
            // inline is now shared with the live BotController/ActionMap path (which needs the
            // identical classification to decide what to auto-dispatch), factored into
            // RewardScreenClassifier. Same fail-open-to-null behavior as before.
            return RewardScreenClassifier.ResolveRewardKind(index);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.ResolveClaimedRewardKind threw: {ex.Message} — fail-open (no override).");
            return null;
        }
    }

    /// <summary>Companion to <see cref="ResolveClaimedRewardKind"/> — returns the CLAIMED
    /// button's own <c>RewardIndex</c> (the fork's <c>ClaimRewardCommand</c> property,
    /// reflected since <paramref name="cmd"/> is the fork's own CLR type). No screen/button
    /// lookup needed, unlike the other Resolve* helpers here: the index is a plain property
    /// read straight off the dispatched command. Feeds
    /// <see cref="DecisionContext.RewardButtonIndexOverride"/> so <c>RunObsWriter.ClassifyReward</c>
    /// can pick <c>buttons[index]</c> directly instead of "first button of this type found by
    /// scanning the whole screen" — the latter silently returns an adjacent or already-
    /// claimed button's model on any screen offering more than one item of the same reward
    /// type. Fail-open: returns null (falls back to the pre-existing first-found heuristic)
    /// whenever the index can't be reflected.</summary>
    private static int? ResolveClaimedRewardIndex(object cmd)
    {
        try
        {
            var idxProp = cmd.GetType().GetProperty("RewardIndex", BindingFlags.Public | BindingFlags.Instance);
            if (idxProp == null || idxProp.PropertyType != typeof(int))
                return null;
            return (int)idxProp.GetValue(cmd)!;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.ResolveClaimedRewardIndex threw: {ex.Message} — fail-open (no override).");
            return null;
        }
    }

    private static void BeginRun(string? seed)
    {
        string? path = ModPaths.ResolveDataFile(SpireBotConfig.ContractPath, "contract.json");
        if (path == null)
        {
            GD.PrintErr("[SpireBot] PassiveDump.BeginRun: no ContractPath configured and no " +
                        "contract.json found — PassiveDump cannot dump; disabling for the rest of this session.");
            _dead = true;
            return;
        }

        try
        {
            _contract = Contract.Load(path);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] PassiveDump.BeginRun: failed to load the contract from '{path}': {ex} " +
                        "— disabling for the rest of this session.");
            _dead = true;
            return;
        }

        // The fork's own ActiveSeed was observed empty at BeginRun time in the 89U passive dump
        // ("BeginRun — seed=''"), which named the dump folder `unseeded_<timestamp>` even though
        // the replay itself was seeded — cosmetic only (folder naming), but worth a real seed
        // when one is cheaply available. `RunManager.ToSave()` (a real game method, already
        // Harmony-patched elsewhere in this mod by the vendored `RunSaveLogger`) would give one
        // via `SerializableRun.SerializableRng.Seed`, but calling it ourselves mid-run is NOT
        // "trivially reachable": it also fires every other patch on `RunManager.ToSave` (writes
        // save-log files, etc.) as an unwanted side effect of what should be a read-only lookup,
        // and `IRunState` itself exposes no plain seed string (only per-stream numeric
        // `Rng.*.Seed`). Not worth that risk for a cosmetic folder name — keep the timestamp
        // fallback and just log when the fork seed was empty, per the task brief.
        string? dumpSeed = seed;
        if (string.IsNullOrEmpty(dumpSeed))
            GD.Print("[SpireBot] PassiveDump.BeginRun: fork ActiveSeed was empty — " +
                     "falling back to a timestamped dump folder name (cosmetic only).");

        _session.ResetRun();
        _obsBuilder = new ObsBuilder(_contract, _session);
        DecisionDumper.ResetRun(dumpSeed);
        SubscribeCombatHooks();
        _lastSelectCardsScreen = null;
        _lastClaimedRewardScreen = null;
        _lastClaimedRewardButton = null;

        // Idempotent — subscribes ReplayDispatcher.OnRoomExited so ReplayState.ActiveMerchantRoom
        // (and other pending screen refs) actually gets cleared on room exit. Without this call,
        // nothing ever invokes ReplayDispatcher.Reset() and the subscription never happens
        // (round 3 fix — see VENDORED-FROM.md delta 9).
        SpireBot.Replay.ReplayDispatcher.Reset();

        _runSeed = seed;
        _runActive = true;
        GD.Print($"[SpireBot] PassiveDump.BeginRun — seed='{seed}'");
    }

    // ── Combat hooks — deliberately a private copy of BotController.SubscribeCombatHooks'
    // wiring, not a call into it: PassiveDump is fully self-contained and must not touch
    // BotController's private state (BotController may not even be running in this mode). ──
    private static void SubscribeCombatHooks()
    {
        if (_combatHooksSubscribed) return;
        var mgr = CombatManager.Instance;
        if (mgr == null)
        {
            GD.PrintErr("[SpireBot] PassiveDump.SubscribeCombatHooks: CombatManager.Instance is null " +
                        "— SessionState's intent-history accumulation will not run.");
            return;
        }
        _combatSetUpHandler = s => _session.OnCombatStarted(s);
        _turnStartedHandler = s => _session.OnTurnStarted(s);
        // Round-15: TurnEnded is the round-boundary damage-recompute moment for the
        // pushable intent snapshots — see SessionState.OnTurnEnded's doc.
        _turnEndedHandler = s => _session.OnTurnEnded(s);
        mgr.CombatSetUp += _combatSetUpHandler;
        mgr.TurnStarted += _turnStartedHandler;
        mgr.TurnEnded += _turnEndedHandler;
        _combatHooksSubscribed = true;
    }
}
