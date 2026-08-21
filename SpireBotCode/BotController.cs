using System;
using System.Linq;

using Godot;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;

using SpireBot.Replay;
using SpireBot.Replay.Commands;
using SpireBot.SpireBotCode.Obs;
using SpireBot.SpireBotCode.Overlay;
using SpireBot.SpireBotCode.Policies;

// The Obs.Obs type collides with the SpireBot.SpireBotCode.Obs namespace itself once both
// are in scope (CS0118) — alias it rather than fully-qualifying every use.
using ObsResult = SpireBot.SpireBotCode.Obs.Obs;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Drives a bot-played run end to end: starts a fresh Ironclad ascension-0 singleplayer run,
/// then on every settle point (the same "dispatchable set changed" signal the vendored
/// <see cref="ReplayDispatcher"/> uses to drive replay playback) captures a
/// <see cref="DecisionContext"/>, builds its <see cref="ActionMap"/>, asks the configured
/// <see cref="IPolicy"/> to choose an action, and executes it via <see cref="ActionExecutor"/>.
///
/// Implemented as a plain static class rather than a Godot node: every piece of vendored
/// machinery this hooks into (<see cref="ReplayDispatcher"/>, <see cref="ReplayState"/>) is
/// itself static and drives its own per-frame/per-signal work through
/// <c>NGame.Instance.GetTree()</c> timers and a single shared <see cref="DispatchSignalEmitter"/>
/// node it already owns — there is no separate per-frame loop for BotController to own, so a
/// second Node in the tree would add nothing but bookkeeping.
/// </summary>
public static class BotController
{
    /// <summary>Fired every time a decision was captured and a policy chose an action for it.</summary>
    public static event Action<DecisionContext, PolicyResult>? DecisionMade;

    /// <summary>The policy driving decisions. Defaults to <see cref="FirstLegalPolicy"/>;
    /// <see cref="SelectPolicy"/> (Task 14, called from <see cref="StartBotRun"/>) swaps this
    /// to an <see cref="OnnxPolicy"/> when <see cref="SpireBotConfig.OnnxModelPath"/> is set and
    /// the model loads + validates against the contract, logging which policy won either way.
    /// Settable directly too (e.g. tests) — <see cref="StartBotRun"/> always re-derives it from
    /// config on every run start, so a manual override only lasts until the next run starts.</summary>
    public static IPolicy Policy { get; set; } = new FirstLegalPolicy();

    private static bool _running;

    /// <summary>True while the bot decision loop is driving a run (StartBotRun..Stop).
    /// MenuInjection uses this to stop an abandoned bot run when the main menu is reached.</summary>
    public static bool IsRunning => _running;

    private static bool _paused;
    private static bool _stepOnce;

    /// <summary>
    /// True while the decision loop is held by the overlay's pause control (BotControlBar).
    /// A paused bot stops *choosing* actions; a command already dispatched still runs to
    /// completion, because the bot enqueues into the vendored dispatcher rather than acting
    /// directly. Reset at every <see cref="StartBotRun"/> and <see cref="Stop"/>.
    /// </summary>
    public static bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value)
            {
                // Leaving pause discards an un-consumed step, so pausing again later doesn't
                // hand out a free decision the user never asked for — and discards any held
                // preview, so it can never fire unattended after the user resumes. A dwell
                // armed when the pause began is cancelled the same way (seq bump), so its
                // timer can never commit a stale hold; the next cycle re-decides fresh.
                _stepOnce = false;
                _pending = null;
                _dwellArmed = false;
                _dwellSeq++;
            }
        }
    }

    /// <summary>
    /// Pins the vendored dispatcher's speed to the user's GameSpeed setting. The bot no longer
    /// varies this by its own state (2026-08-20 UX pacing spec §2 — decision pacing is
    /// DecisionSpeed's job, on unscaled timers); the pin itself must stay, because the vendored
    /// ExecuteNext re-asserts TimeScale from its own _gameSpeed field (default 2.0 — replay
    /// fast-forward) on every dispatch while a driver is attached
    /// (Replay/ReplayDispatcher.cs:1112-1114), so an unpinned bot run silently double-speeds
    /// the game. This MUST go through ReplayDispatcher.GameSpeed rather than assigning
    /// Engine.TimeScale directly, for that same re-assertion reason.
    /// </summary>
    public static void ApplyEffectiveSpeed()
    {
        ReplayDispatcher.GameSpeed = SpireBotConfig.GameSpeed;
    }

    /// <summary>With a previewed action held: commit exactly that action, staying paused (the
    /// next heartbeat previews the following one). With nothing held (Step pressed before the
    /// first preview computed, or on a screen where the loop selected nothing): fall back to
    /// arming one full decision, today's pre-preview behavior — the button is never dead.
    /// Implies <see cref="Paused"/> either way.</summary>
    public static void StepOnce()
    {
        _paused = true;

        var held = _pending;
        if (held != null)
        {
            // Invariant: anything that consumes the hold must also cancel the dwell
            // bookkeeping, or _dwellArmed stays true and OnInputRequired's gate blocks the
            // loop until the now-orphaned timer eventually fires and finds a stale seq.
            _pending = null;   // cleared BEFORE executing, so a throw can never double-commit
            _dwellArmed = false;
            _dwellSeq++;
            Commit(held);
            return;
        }

        _stepOnce = true;
    }

    /// <summary>Executes a held preview: deferred commit gate, deferred decision dump, then the
    /// exact held command. Deliberately does NOT run <see cref="AdvanceStuckGuard"/> — its
    /// forced-fallback path chooses a different command than the one held, which would break
    /// the guarantee that Step performs what the overlay showed. A human stepping deliberately
    /// is the situation the automatic guard exists to substitute for.</summary>
    private static void Commit(PendingAction held)
    {
        // The gate's counter only mutates on commits and unpaused dispatches, but the probe
        // that selected this command and this commit can be seconds apart — a pacing dwell,
        // or a whole pause, can sit between them — so a re-check here is not purely defensive.
        // Honor a refusal the same way the unpaused forced-decline path does: execute nothing.
        if (held.CommitGate != null && !held.CommitGate())
        {
            GD.PrintErr($"[SpireBot] BotController.Commit: commit gate refused " +
                        $"'{held.Cmd.Describe()}' — discarding the preview; the next cycle re-decides.");
            return;
        }

        // All dumps happen here, at commit — so dumps record only actions actually taken,
        // whether the hold was a preview or a pacing dwell.
        if (held.Result != null && held.Obs != null && held.Map != null)
            DecisionDumper.Write(held.Ctx, held.Obs, held.Map, held.Result);

        // 2026-08-20 audit lesson: fallback/interstitial actions used to bypass decisions.jsonl
        // entirely, so a dump could look perfectly faithful while the session log carried ~50
        // silent non-policy actions. Every non-policy dispatch (result == null) leaves a
        // minimal source-tagged line here, at actual execution time, so future audits see them
        // in the dump itself.
        if (held.Result == null)
            DecisionDumper.WriteNonPolicy(held.Ctx, held.Cmd, held.Source ?? "auto-advance");

        // The pacing tier for the NEXT decision keys off the surface this one acted on.
        _lastCommittedSurfaceKey = SurfaceKey(held.Ctx);

        try
        {
            ActionExecutor.Execute(held.Cmd, held.Kind);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotController.Commit: ActionExecutor.Execute threw for " +
                        $"{held.Cmd.GetType().Name} — the run will wait for the next settle signal: {ex}");
        }
    }

    /// <summary>
    /// The single seam between "the loop chose a command" and "the game performs it". All six
    /// execution sites route here. Previewing: hold the command for Step. Otherwise: hold it
    /// for the per-kind pacing dwell (2026-08-20 UX pacing spec §1), or commit immediately
    /// when the dwell is zero (DecisionSpeed = Instant, or an undwelled kind).
    /// </summary>
    private static void Dispatch(
        ReplayCommand cmd, DecisionKind kind, DecisionContext ctx,
        ActionMap? map, ObsResult? obs, PolicyResult? result, Func<bool>? commitGate = null,
        string? source = null)
    {
        var pending = new PendingAction(cmd, kind, ctx, map, obs, result,
                                        BuildDecisionKey(ctx), commitGate, source);

        if (_previewing)
        {
            _pending = pending;
            GD.Print($"[SpireBot] BotController: preview refreshed: {cmd.Describe()}");
            return;
        }

        double dwell = PacingPlan.DwellSeconds(
            kind, SurfaceKey(ctx) != _lastCommittedSurfaceKey, SpireBotConfig.DecisionSpeed);
        if (dwell <= 0.0)
        {
            Commit(pending);
            return;
        }

        ArmDwell(pending, dwell);
    }

    /// <summary>The single definition of "the decision loop is currently held", used by both
    /// <see cref="OnHeartbeat"/> and <see cref="OnInputRequired"/> so the two can never disagree.</summary>
    private static bool IsGated() => _paused && !_stepOnce;

    // ── Paused action preview (paused-action-preview spec) ─────────────────────────────────
    //
    // While paused, the loop still decides, but the chosen command is HELD here instead of
    // executing; Step commits exactly this instance (the WYSIWYG guarantee — with Temperature
    // above 0 a re-decide would resample and could act differently than it showed). Refreshed
    // only when BuildDecisionKey changes (Component 1a); cleared on unpause (Paused setter),
    // hence also on StartBotRun/Stop which set Paused = false, and on every commit.
    //
    // Map is nullable because TryAutoAdvance has no ActionMap in scope; the deferred dump
    // needs Result non-null, and the only path that supplies Result supplies Map/Obs too.
    // CommitGate defers the card-claim branch's TryBeginAutoAdvance — the one selection-time
    // side effect that lives upstream of ActionExecutor.Execute (see the spec's "Where the
    // hold goes" for why the probe must not run it early, and why probe and gate cannot
    // disagree) — for every kind of hold, not just a paused preview: a pacing dwell defers it
    // exactly the same way, running the gate only once Commit actually fires.
    private sealed record PendingAction(
        ReplayCommand Cmd, DecisionKind Kind,
        DecisionContext Ctx, ActionMap? Map, ObsResult? Obs, PolicyResult? Result,
        string Key, Func<bool>? CommitGate, string? Source = null);

    private static PendingAction? _pending;

    /// <summary>True while the current RunDecision cycle is a preview — it must hold its
    /// chosen command rather than execute it. Recomputed at every OnInputRequired entry.</summary>
    private static bool _previewing;

    /// <summary>True while a previewed action is held awaiting Step.</summary>
    public static bool HasPendingPreview => _pending != null;

    /// <summary>The held action's description for the overlay, or null when nothing is held.</summary>
    public static string? PendingPreviewDescription => _pending?.Cmd.Describe();

    /// <summary>Self-test seam (BotControlSelfTest): whether a single-step is armed.</summary>
    internal static bool StepArmed => _stepOnce;

    private static bool _subscribed;
    private static Contract? _contract;
    private static Callable _inputRequiredHandler;

    // ── Task 12: ObsBuilder session state ──────────────────────────────────
    //
    // One SessionState per bot run (the ACCUMULATE-shaped observation bookkeeping —
    // enemy intent history, the unmapped-game-id log-once latch — see SessionState's
    // own doc comment for why it's wired to CombatManager's TurnStarted/CombatSetUp
    // events rather than derived from this class's own settle-signal loop). _obsBuilder
    // is rebuilt alongside _session every StartBotRun (it closes over both _contract and
    // _session, and both are per-run).
    private static readonly SessionState _session = new();
    private static ObsBuilder? _obsBuilder;
    private static bool _combatHooksSubscribed;
    private static Action<CombatState>? _turnStartedHandler2;
    private static Action<CombatState>? _turnEndedHandler2;
    private static Action<CombatState>? _combatSetUpHandler;
    private static Action<MegaCrit.Sts2.Core.Rooms.CombatRoom>? _combatEndedHandler;

    // Stuck guard: if the same decision (kind + available commands + a few state scalars)
    // recurs this many times in a row with no state change, force a fallback action instead
    // of spinning forever on a decision the policy can't resolve.
    private const int StuckThreshold = 3;

    /// <summary>
    /// Repeats tolerated when every available command is masked off by an affordance /
    /// precondition gate — i.e. the game is mid-transition and will accept input shortly.
    /// </summary>
    private const int GatedStuckThreshold = 15;

    /// <summary>Backstop re-check interval for the decision loop (see ScheduleHeartbeat).</summary>
    private const double HeartbeatSeconds = 1.0;
    private static string? _lastDecisionKey;
    private static int _repeatCount;

    /// <summary>
    /// Starts a fresh Ironclad, ascension-0, standard-mode singleplayer run and begins the bot
    /// decision loop. Mirrors RunReplays' <c>RunReplayMenu.StartReplay</c> invocation of
    /// <c>NGame.Instance.StartNewSingleplayerRun(...)</c> (RunReplayMenu.cs:638-646), minus the
    /// replay-log loading (<c>ReplayDispatcher.Load</c>/<c>ReplayEngine.ActiveSeed</c>/
    /// <c>ReplayEngine.IsReplayRun</c>) that only applies to played-back recordings.
    /// </summary>
    /// <param name="seed">
    /// Run seed, or null/empty to roll a random one HERE. Task 14's "empty string means
    /// randomize" assumption was FALSE: <c>StartNewSingleplayerRun</c> passes the string
    /// straight into <c>new RunRngSet(seed)</c>, which deterministically hashes whatever it
    /// gets (RunRngSet.cs:104-107) — an empty string is a fixed seed, so every bot run played
    /// the same run. The game itself never relies on empty-means-random: every real entry
    /// point rolls <c>SeedHelper.GetRandomSeed()</c> up front when no seed was given
    /// (NGame.cs:299, StartRunLobby.cs:725, RunState.cs:308), so we do the same.
    /// </param>
    public static void StartBotRun(string? seed = null)
    {
        // See the param doc: empty is a FIXED seed, not "randomize" — roll like the game does.
        if (string.IsNullOrWhiteSpace(seed))
            seed = SeedHelper.GetRandomSeed();

        if (_running)
        {
            GD.Print("[SpireBot] BotController.StartBotRun called while already running — ignoring.");
            return;
        }

        if (!PrepareDriver())
            return;

        if (NGame.Instance == null)
        {
            GD.PrintErr("[SpireBot] BotController.StartBotRun: NGame.Instance is null — cannot start a run.");
            return;
        }

        // RunReplayMenu.StartReplay resolves the character by matching CharacterModel.Id.Entry
        // against a stored log string (RunReplayMenu.cs:620-622). SpireBot always wants
        // Ironclad; "IRONCLAD" is confirmed as the exact Id.Entry value from a real recorded
        // log's header (RunReplays/Resources/89U21BV1TZ/floor_49/actions.sts2replay:1,
        // "# Character: IRONCLAD").
        CharacterModel? character = ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry == "IRONCLAD");
        if (character == null)
        {
            GD.PrintErr("[SpireBot] BotController.StartBotRun: could not resolve the Ironclad " +
                        "CharacterModel (no ModelDb.AllCharacters entry with Id.Entry == \"IRONCLAD\").");
            return;
        }

        BeginDriving(seed, SpireBotConfig.StartPaused);

        GD.Print($"[SpireBot] BotController.StartBotRun — character={character.Id} " +
                 $"seed='{seed}' ascension=0");

        NAudioManager.Instance?.StopMusic();
        SfxCmd.Play(character.CharacterTransitionSfx);

        // RunReplayMenu.cs:638-646 — same call shape, minus the replay-log plumbing:
        //   NGame.Instance.StartNewSingleplayerRun(character, shouldSave, acts, modifiers,
        //       seed, gameMode, ascensionLevel)
        TaskHelper.RunSafely(
            NGame.Instance.StartNewSingleplayerRun(
                character,
                shouldSave: true,
                ActModel.GetDefaultList(),
                [],
                seed,
                GameMode.Standard,
                0));

        Subscribe();
    }

    /// <summary>
    /// Attaches the decision loop to a run that is ALREADY in progress — one the player resumed
    /// with the game's own Continue button (see <see cref="RunContinueAttach"/>) rather than one
    /// this class started. Always attaches PAUSED: the bot advises, the human plays, and Play
    /// hands the run over whenever they want it to.
    ///
    /// Everything <see cref="StartBotRun"/> does per run happens here too, minus the parts that
    /// only make sense for a brand-new run — resolving the character, the transition sfx, and
    /// the <c>StartNewSingleplayerRun</c> call itself. One consequence of attaching mid-run is
    /// that <see cref="SessionState"/> starts empty, so the ACCUMULATE-shaped observation
    /// features (enemy intent history in particular) are blank until the current combat has run
    /// a turn or two. The advice is still well-formed, just slightly less informed on the first
    /// combat after attaching.
    /// </summary>
    /// <param name="seed">The resumed run's seed, used only to name the decision-dump directory.</param>
    public static void AttachToRun(string seed)
    {
        if (_running)
        {
            GD.Print("[SpireBot] BotController.AttachToRun called while already running — ignoring.");
            return;
        }

        if (!PrepareDriver())
            return;

        // startPaused is hard-coded rather than read from config: this entry point exists to
        // advise a human who is playing, so taking over their resumed run unannounced would be
        // the one behavior nobody asked for. SpireBotConfig.StartPaused gates whether we attach
        // at all (RunContinueAttach), not whether we then drive.
        BeginDriving(seed, startPaused: true);

        GD.Print($"[SpireBot] BotController.AttachToRun — advising the in-progress run " +
                 $"seed='{seed}' (paused; press Play to hand it over).");

        Subscribe();
    }

    /// <summary>Loads the contract, picks the policy, and makes sure the overlay exists — the
    /// setup both entry points need before any per-run state is touched. False means the
    /// contract failed to load and the caller must abort without having changed anything.</summary>
    private static bool PrepareDriver()
    {
        if (!TryLoadContract())
            return false;

        SelectPolicy();
        ThinkingOverlay.Ensure();
        return true;
    }

    /// <summary>
    /// Per-run state reset shared by <see cref="StartBotRun"/> and <see cref="AttachToRun"/>:
    /// marks the driver attached, applies the starting pause and speed, and clears every piece
    /// of run-scoped memory. Callers still own their own <see cref="Subscribe"/> call, because
    /// StartBotRun must subscribe only after it has kicked off the new run.
    /// </summary>
    private static void BeginDriving(string seed, bool startPaused)
    {
        _running = true;
        // Tells the vendored replay-side patches/screen captures that a driver is attached, so
        // they populate ReplayState — without this the dispatcher enumerates nothing and the
        // bot stalls on its first decision (see ReplayEngine.BotDriving).
        ReplayEngine.BotDriving = true;
        // A pause left over from a previous run must not silently hold the new one, and a step
        // or preview held when the last run ended must not survive into this one. The Paused
        // setter discards both only on the FALSE transition, so assigning startPaused directly
        // could carry a stale preview across runs — where Step would then commit a command
        // chosen against a board that no longer exists. Clear first, then apply the requested
        // starting state. The two assignments are the reset; neither is redundant.
        Paused = false;
        Paused = startPaused;

        // No dwell from a previous run may survive into this one.
        _dwellArmed = false;
        _dwellSeq++;
        _lastCommittedSurfaceKey = null;

        // The vendored dispatcher fast-forwards a replay (_gameSpeed defaults to 2.0) and
        // re-asserts it whenever IsActive is true, which BotDriving now makes permanent. Pin it
        // so a bot run doesn't silently double-speed the game — and so a run that starts paused
        // for manual play runs at 1.0x. Must follow the pause assignment above: the effective
        // speed depends on it.
        ApplyEffectiveSpeed();
        _lastDecisionKey = null;
        _repeatCount = 0;
        StallDiagnostics.Reset();
        StuckRecovery.Reset();
        RoomOneShots.Reset();
        RewardClaimMemory.Reset();
        ScreenExitMemory.Reset();
        ShopPurchaseGate.Reset();
        _recheckPending = false;
        _session.ResetRun();
        _obsBuilder = new ObsBuilder(_contract!, _session);
        DecisionDumper.ResetRun(seed);
        SubscribeCombatHooks();

        // Idempotent — subscribes ReplayDispatcher.OnRoomExited so ReplayState.ActiveMerchantRoom
        // (and other pending screen refs) actually gets cleared on room exit. Without this call,
        // nothing ever invokes ReplayDispatcher.Reset() and the subscription never happens
        // (round 3 fix — see VENDORED-FROM.md delta 9).
        ReplayDispatcher.Reset();

        // Must come AFTER the Reset() above, which just wiped every ReplayState capture — see
        // the method doc for why an attach can land inside a room whose capture patches have
        // already fired and will never fire again.
        RecaptureOpenRoomState();
    }

    // Same private-State reflection convention as RunObsWriter/PassiveDump — no shared helper
    // exists for it in this codebase.
    private static readonly System.Reflection.PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty(
            "State",
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance);

    /// <summary>
    /// Repopulates the ReplayState captures for a room that was ALREADY open when the driver
    /// attached. The vendored capture patches fire once, at the moment a room's synchronizer
    /// begins (<c>EventSynchronizer.BeginEvent</c> runs inside <c>EventRoom.EnterInternal</c>,
    /// BEFORE <c>RunManager.RoomEntered</c> is raised) — so when <see cref="AttachToRun"/> fires
    /// off that same RoomEntered signal, the capture has already happened and the
    /// <c>ReplayDispatcher.Reset()</c> in <see cref="BeginDriving"/> just erased it. Nothing
    /// re-fires for a still-open room, so without this the decision loop enumerates zero
    /// commands forever (observed: a fresh run's Ancient/Neow bonus stuck on "Unsupported
    /// decision with zero available commands" while the overlay showed "&lt;none yet&gt;").
    ///
    /// Reads the same live <c>RunManager</c> properties the capture patches cache, guarded by
    /// the current room's type so a synchronizer left over from a previous room can never be
    /// misattributed. StartBotRun is unaffected either way: it resets BEFORE the run exists
    /// (no current room), and the new run's patches then fire in the normal order.
    /// </summary>
    private static void RecaptureOpenRoomState()
    {
        var manager = RunManager.Instance;
        if (manager == null)
            return;

        var state = RunStateProp?.GetValue(manager) as IRunState;
        var room = state?.CurrentRoom;

        if (room is MegaCrit.Sts2.Core.Rooms.EventRoom
            && manager.EventSynchronizer is { } eventSync && eventSync.Events.Count > 0)
        {
            ReplayState.ActiveEventSynchronizer = eventSync;
            GD.Print("[SpireBot] BotController: re-captured the open event room's " +
                     "EventSynchronizer (its BeginEvent fired before the driver attached).");
        }
        else if (room is MegaCrit.Sts2.Core.Rooms.RestSiteRoom
                 && manager.RestSiteSynchronizer is { } restSync)
        {
            ReplayState.ActiveRestSiteSynchronizer = restSync;
            GD.Print("[SpireBot] BotController: re-captured the open rest site's " +
                     "RestSiteSynchronizer (its BeginRestSite fired before the driver attached).");
        }
    }

    /// <summary>Stops the bot decision loop. Does not end the in-progress run — it just stops
    /// driving it; the player (or another BotController.StartBotRun call) can take over.</summary>
    public static void Stop()
    {
        _running = false;
        ReplayEngine.BotDriving = false;
        Paused = false;
        _dwellArmed = false;
        _dwellSeq++;
        // A purchase in flight when the loop stops must not hold the next run's first decision.
        ShopPurchaseGate.Reset();
        // BotDriving going false makes ReplayEngine.IsActive false, after which nothing re-asserts
        // OR resets Engine.TimeScale — without this the game is left running at whatever
        // multiplier the bot used, with no way back short of a restart.
        ReplayDispatcher.RestoreGameSpeed();
        Unsubscribe();
        UnsubscribeCombatHooks();
        GD.Print("[SpireBot] BotController.Stop — decision loop stopped.");
    }

    // ── Combat hooks (SessionState wiring — see the field-group comment above) ──
    private static void SubscribeCombatHooks()
    {
        if (_combatHooksSubscribed) return;
        var mgr = CombatManager.Instance;
        if (mgr == null)
        {
            GD.PrintErr("[SpireBot] BotController.SubscribeCombatHooks: CombatManager.Instance is null " +
                        "— SessionState's intent-history accumulation will not run.");
            return;
        }
        _combatSetUpHandler = state => _session.OnCombatStarted(state);
        _turnStartedHandler2 = state => _session.OnTurnStarted(state);
        // Round-15: round-boundary damage recompute — see SessionState.OnTurnEnded.
        _turnEndedHandler2 = state => _session.OnTurnEnded(state);
        // 2026-08-20 audit issue 3: the killing-blow card play's completion hook
        // (CardPlayReplayPatch.OnAfterActionExecuted's PlayCardAction branch) never runs once
        // combat teardown starts, so CardPlayInFlight stayed stuck for the 5s hard timeout on
        // every lethal play (5/5 StuckRecovery firings in run JKZ3B820B6 were exactly this).
        // CombatManager.CombatEnded IS the completion signal for that final play — see
        // StuckRecovery.OnCombatEnded.
        _combatEndedHandler = _ => StuckRecovery.OnCombatEnded();
        mgr.CombatSetUp += _combatSetUpHandler;
        mgr.TurnStarted += _turnStartedHandler2;
        mgr.TurnEnded += _turnEndedHandler2;
        mgr.CombatEnded += _combatEndedHandler;
        _combatHooksSubscribed = true;
    }

    private static void UnsubscribeCombatHooks()
    {
        if (!_combatHooksSubscribed) return;
        var mgr = CombatManager.Instance;
        if (mgr != null)
        {
            if (_combatSetUpHandler != null) mgr.CombatSetUp -= _combatSetUpHandler;
            if (_turnStartedHandler2 != null) mgr.TurnStarted -= _turnStartedHandler2;
            if (_turnEndedHandler2 != null) mgr.TurnEnded -= _turnEndedHandler2;
            if (_combatEndedHandler != null) mgr.CombatEnded -= _combatEndedHandler;
        }
        _combatSetUpHandler = null;
        _combatEndedHandler = null;
        _turnStartedHandler2 = null;
        _turnEndedHandler2 = null;
        _combatHooksSubscribed = false;
    }

    private static bool TryLoadContract()
    {
        string? path = ModPaths.ResolveDataFile(SpireBotConfig.ContractPath, "contract.json");
        if (path == null)
        {
            Fail("no ContractPath is configured and no contract.json was found next to the mod " +
                 $"(looked in '{ModPaths.ModDir ?? "<unknown mod dir>"}' and its model\\ subfolder)");
            return false;
        }

        try
        {
            _contract = Contract.Load(path);
            return true;
        }
        catch (Exception ex)
        {
            Fail($"failed to load the contract from '{path}': {ex.Message}");
            GD.PrintErr(ex.ToString());
            return false;
        }

        static void Fail(string reason)
        {
            string message = $"Bot Run aborted — {reason}. Set ContractPath in SpireBot's config screen.";
            GD.PrintErr($"[SpireBot] BotController.StartBotRun: {message}");
            // Without this the failure is invisible in game: the button appears to do nothing.
            ThinkingOverlay.ShowStatus(message);
        }
    }

    /// <summary>
    /// Task 14 policy selection: if <see cref="SpireBotConfig.OnnxModelPath"/> is set and loads
    /// + validates against <see cref="_contract"/>, drive the run with <see cref="OnnxPolicy"/>;
    /// otherwise (empty path, or any load/validation failure) fall back to
    /// <see cref="FirstLegalPolicy"/>. Always logs which one won, loudly, on both paths — this
    /// is the ONE place that swallows an <see cref="OnnxPolicyException"/> into a fallback
    /// rather than propagating it; every other error path (per-decision inference failures)
    /// falls back inside <see cref="OnnxPolicy.Choose"/>/<see cref="DispatchWithFallback"/>
    /// instead. Disposes a previously-loaded OnnxPolicy first so a second StartBotRun (or a
    /// hot-reload of the model path) never leaks its InferenceSession.
    /// </summary>
    private static void SelectPolicy()
    {
        (Policy as IDisposable)?.Dispose();

        if (string.IsNullOrWhiteSpace(SpireBotConfig.OnnxModelPath))
        {
            GD.Print("[SpireBot] BotController.SelectPolicy: OnnxModelPath is empty — using FirstLegalPolicy.");
            Policy = new FirstLegalPolicy();
            return;
        }

        // A configured directory resolves to model.onnx (then stub.onnx) inside it; a configured
        // file is used as typed. Empty is handled above and keeps meaning "no model, stub bot".
        string modelPath = ModPaths.ResolveDataFile(SpireBotConfig.OnnxModelPath, "model.onnx")
                           ?? ModPaths.ResolveDataFile(SpireBotConfig.OnnxModelPath, "stub.onnx")
                           ?? SpireBotConfig.OnnxModelPath;

        try
        {
            Policy = OnnxPolicy.Load(modelPath, _contract!);
            GD.Print($"[SpireBot] BotController.SelectPolicy: driving with OnnxPolicy ('{modelPath}').");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotController.SelectPolicy: OnnxPolicy failed to load/validate " +
                        $"from '{modelPath}' — falling back to FirstLegalPolicy: {ex}");
            ThinkingOverlay.ShowStatus($"ONNX model unusable ('{modelPath}') — running first-legal stub policy.");
            Policy = new FirstLegalPolicy();
        }
    }

    // ── Settle-signal wiring ─────────────────────────────────────────────────────────────
    //
    // Reuses ReplayDispatcher's own idle/settle signal instead of adding a new poll timer:
    // ReplayDispatcher.StartDispatchPoll() (ReplayDispatcher.cs:538-559) owns a single shared
    // DispatchSignalEmitter node and fires its "InputRequired" signal from
    // LogDispatchableChanges (ReplayDispatcher.cs:573-583) whenever the *set* of dispatchable
    // command types differs from the last-seen set. That function runs from several real
    // trigger points — TryDispatch (ReplayDispatcher.cs:982), ExecuteNext
    // (ReplayDispatcher.cs:1048), and a 0.5s DispatchPollTick fallback
    // (ReplayDispatcher.cs:561-566/585-589) — which is exactly "dispatchable-set change +
    // quiet frames" as specified. We piggyback StartDispatchPoll (idempotent —
    // ReplayDispatcher.cs:540 `if (_dispatchPollRunning) return;`) to guarantee the emitter
    // exists, then add our own listener alongside the vendored one.
    private static void Subscribe()
    {
        if (_subscribed) return;

        ReplayDispatcher.StartDispatchPoll();
        var emitter = ReplayDispatcher.Emitter;
        if (emitter == null || !GodotObject.IsInstanceValid(emitter))
        {
            GD.PrintErr("[SpireBot] BotController.Subscribe: ReplayDispatcher.Emitter is null " +
                        "after StartDispatchPoll — decision loop will not run.");
            return;
        }

        _inputRequiredHandler = Callable.From(OnInputRequired);
        emitter.Connect(DispatchSignalEmitter.SignalInputRequired, _inputRequiredHandler);
        _subscribed = true;

        // Prime the loop immediately rather than waiting for the first natural signal —
        // the run may already have a pending decision (e.g. a starting-bonus screen) by the
        // time StartBotRun's deferred run-start work finishes.
        Callable.From(OnInputRequired).CallDeferred();

        // ...and keep re-checking, because that signal is edge-triggered (see ScheduleHeartbeat).
        ScheduleHeartbeat();
    }

    private static void Unsubscribe()
    {
        if (!_subscribed) return;
        var emitter = ReplayDispatcher.Emitter;
        if (emitter != null && GodotObject.IsInstanceValid(emitter))
            emitter.Disconnect(DispatchSignalEmitter.SignalInputRequired, _inputRequiredHandler);
        _subscribed = false;
    }

    private static void OnInputRequired()
    {
        // Cleared unconditionally at entry: whether this cycle was woken by the re-check timer or
        // by anything else, no timer is outstanding once we are here, and the gate check below
        // schedules a fresh one if it still holds.
        _recheckPending = false;

        if (!_running) return;

        // Skip pulses fired while our own dispatched command is still in flight — those are
        // that dispatch's bookkeeping, not a new decision. Deliberately NOT ReplayEngine
        // .IsActive: that is now true for the whole bot run (ReplayEngine.BotDriving), so
        // testing it here would suppress every decision. Checked before _stepOnce is consumed,
        // so a step armed mid-flight survives to the next cycle instead of being silently spent.
        if (ReplayEngine.HasPendingCommand) return;

        // A chosen action is dwelling — the decision is already made and the user is being
        // shown it. Deciding again would overwrite the held action mid-countdown.
        if (_dwellArmed) return;

        // A dispatched shop purchase reports Ok as soon as it is STARTED, so the pending-command
        // check above goes false while the game is still resolving it — during which the item is
        // still on the shelf and the gold still unspent. Deciding in that window bought the same
        // item repeatedly and crashed the game on a null CreationResult; see ShopPurchaseGate.
        // Re-checked on a short timer rather than only on the 1 Hz heartbeat, so a shop trip
        // doesn't cost a second per item.
        if (ShopPurchaseGate.IsSettling)
        {
            ScheduleRecheck();
            return;
        }

        // The gate CLASSIFIES rather than returns (paused-action-preview spec): paused with no
        // step armed means this cycle decides but HOLDS its result (Dispatch caches it) instead
        // of executing. _previewing must be computed before _stepOnce is cleared below, because
        // IsGated() reads _stepOnce.
        _previewing = IsGated();

        // A step is spent only once a decision will actually run.
        _stepOnce = false;

        RunDecision();
    }

    /// <summary>
    /// Level-triggered backstop for the decision loop. ReplayDispatcher's InputRequired signal
    /// is EDGE-triggered — LogDispatchableChanges only emits when the dispatchable-type SET
    /// changes, and never for an empty set — so any decision we decline to act on (an
    /// Unsupported screen, a throw, a command that changed nothing) leaves nothing to wake us.
    /// This re-checks on a timer while a run is in progress; the stuck guard still handles a
    /// context that genuinely repeats.
    /// </summary>
    private static void ScheduleHeartbeat()
    {
        var tree = NGame.Instance?.GetTree();
        if (tree == null)
        {
            GD.PrintErr("[SpireBot] BotController.ScheduleHeartbeat: no SceneTree — the decision " +
                        "loop will rely on ReplayDispatcher's edge-triggered signal alone.");
            return;
        }

        tree.CreateTimer(HeartbeatSeconds).Connect("timeout", Callable.From(OnHeartbeat));
    }

    /// <summary>
    /// Re-enters the loop sooner than the next heartbeat would. Used only while
    /// <see cref="ShopPurchaseGate"/> holds: that wait normally ends in a few hundred
    /// milliseconds, and falling back to the 1 Hz heartbeat would stretch a five-item shop trip
    /// into five seconds of visible idling. Safe to stack — <see cref="OnInputRequired"/> is
    /// idempotent, and each timer fires once.
    /// </summary>
    private static void ScheduleRecheck()
    {
        // One outstanding re-check at a time. Several things re-enter OnInputRequired during a
        // hold (the dispatcher's own poll, dispatchable-set changes, the heartbeat), and without
        // this each would stack another timer for the same wait.
        if (_recheckPending) return;

        var tree = NGame.Instance?.GetTree();
        // No tree means the heartbeat cannot have been scheduled either; the gate still releases
        // on its own timeout, so this degrades to heartbeat pace rather than stalling.
        if (tree == null) return;

        _recheckPending = true;
        tree.CreateTimer(ShopPurchaseGate.RecheckSeconds)
            .Connect("timeout", Callable.From(OnInputRequired));
    }

    /// <summary>Whether a <see cref="ScheduleRecheck"/> timer is already outstanding.</summary>
    private static bool _recheckPending;

    // ── Decision pacing (2026-08-20 UX pacing spec §1) ──────────────────────────────────────
    //
    // decide → announce → dwell → commit. A chosen action is HELD in _pending (the preview
    // feature's cache — the overlay's "Will take: ..." line is the announce) while an unscaled
    // timer runs, then Commit executes it. SceneTreeTimers cannot be cancelled, so _dwellSeq is
    // the cancellation: anything that invalidates a held action (Stop, BeginDriving, unpause)
    // bumps it, and a firing timer with a stale captured seq is a no-op.
    private static bool _dwellArmed;
    private static int _dwellSeq;

    /// <summary>The surface the last COMMITTED action was chosen on. A decision on a different
    /// surface gets the longer "reading" dwell; same surface gets the between-actions dwell.</summary>
    private static string? _lastCommittedSurfaceKey;

    private static string SurfaceKey(DecisionContext ctx)
        => $"{ctx.Kind}::{ctx.Snapshot.RoomType}::{ctx.Snapshot.Floor}";

    private static void ArmDwell(PendingAction pending, double seconds)
    {
        var tree = NGame.Instance?.GetTree();
        if (tree == null)
        {
            // No tree, no timer — degrade to the pre-pacing instant behavior rather than stall.
            Commit(pending);
            return;
        }

        _pending = pending;
        _dwellArmed = true;
        int seq = ++_dwellSeq;
        GD.Print($"[SpireBot] BotController: dwelling {seconds:0.00}s before '{pending.Cmd.Describe()}'.");
        // ignoreTimeScale: decision pacing must be independent of Engine.TimeScale (the
        // GameSpeed setting) — the whole point of DecisionSpeed being a separate knob.
        tree.CreateTimer(seconds, processAlways: true, processInPhysics: false, ignoreTimeScale: true)
            .Connect("timeout", Callable.From(() => OnDwellElapsed(seq)));
    }

    private static void OnDwellElapsed(int seq)
    {
        if (seq != _dwellSeq) return;   // superseded by Stop/BeginDriving/unpause — dead timer
        _dwellArmed = false;
        if (!_running) return;
        var held = _pending;
        if (held == null) return;       // Step already committed it
        if (_paused) return;            // paused mid-dwell: keep it as a held preview for Step
        _pending = null;
        Commit(held);
    }

    private static void OnHeartbeat()
    {
        if (!_running) return;

        // Unconditional, including while paused: Tick() is self-guarding — on a healthy idle
        // board nothing is in flight/blocked, so it zeroes its counter and returns immediately.
        // Gating it here would only matter for a command that genuinely wedged mid-pause, and
        // that's exactly the case recovery must not be suppressed for — a whole pause of
        // silence would leave the run stuck with no sign of it in the log.
        StuckRecovery.Tick();

        OnInputRequired();

        // Re-arm ALWAYS, including while paused — this is the only level-triggered path back into
        // the loop (ReplayDispatcher's InputRequired signal is edge-triggered), so skipping the
        // re-arm while paused would mean unpausing never resumes.
        ScheduleHeartbeat();
    }

    // ── Decision loop ────────────────────────────────────────────────────────────────────

    private static void RunDecision()
    {
        DecisionContext ctx;
        ActionMap map;

        try
        {
            ctx = DecisionContext.Capture(_session);
            RoomOneShots.OnDecision(ctx);   // must precede Build — it masks spent one-shots
            RewardClaimMemory.OnDecision(ctx);   // must precede Build — it masks declined card claims

            // Preview refresh gate (spec Component 1a): while paused with an action already
            // held, an unchanged situation needs no recompute — skip ActionMap.Build,
            // ObsBuilder.Build, and the policy's ONNX inference entirely. Compared against the
            // key stored on the held preview, NOT _lastDecisionKey: that field belongs to
            // AdvanceStuckGuard, which does not run while previewing, so it tracks the last
            // EXECUTED decision and goes stale across a pause.
            if (_previewing && _pending != null && BuildDecisionKey(ctx) == _pending.Key)
                return;

            map = ActionMap.Build(_contract!, ctx, _session);
        }
        catch (Exception ex)
        {
            // Nothing to fall back on yet — capture/build itself failed. Log and wait for the
            // next settle signal rather than crashing the run.
            GD.PrintErr($"[SpireBot] BotController.RunDecision: DecisionContext.Capture/ActionMap.Build threw: {ex}");
            return;
        }

        // Task 12/13 built this only under a debug build / DumpDecisions, back when nothing
        // downstream actually consumed it. Task 14's OnnxPolicy needs a real Obs on every
        // decision in every build (it is IPolicy.Choose's third argument now) — so this is
        // unconditional. It still doubles as Task 12/13's "exercise ObsBuilder every decision"
        // requirement (ObsBuilder.SelfCheck + every writer's own bounds check) and as
        // DecisionDumper.Write's source Obs when DumpDecisions is on.
        ObsResult? obs = null;
        if (_obsBuilder != null)
        {
            try { obs = _obsBuilder.Build(ctx); }
            catch (Exception ex)
            {
                GD.PrintErr($"[SpireBot] BotController: ObsBuilder.Build threw (non-fatal): {ex}");
            }
        }

        // Skipped while previewing, and NOT deferred to commit either: a preview is a decision
        // that never executed, so folding it into the guard's repeat accounting corrupts the
        // count relative to what the bot actually did — and the guard's forced-fallback path
        // chooses a DIFFERENT command than the one held, which would break the WYSIWYG
        // guarantee if it ran at commit. Manual stepping through a genuinely stuck state
        // therefore won't auto-recover; a human is watching, which is what the guard subs for.
        if (!_previewing && !AdvanceStuckGuard(ctx, map))
        {
            GD.PrintErr($"[SpireBot] BotController: same decision recurred {_repeatCount + 1}x with no " +
                        $"observed state change (kind={ctx.Kind}) — forcing a fallback action.");
            DispatchWithFallback(ctx, map, obs, forceFallback: true);
            _repeatCount = 0;
            return;
        }

        if (ctx.Kind == DecisionKind.Unsupported)
        {
            HandleUnsupported(ctx, map);
            return;
        }

        DispatchWithFallback(ctx, map, obs, forceFallback: false);
    }

    /// <summary>
    /// Dispatches an interstitial screen step that the RL action space has no id for, when the
    /// screen offers nothing else.
    ///
    /// These are UI steps the sim performs implicitly, so it never asks a question about them
    /// and no action id exists: a finished event's PROCEED
    /// (<c>ChooseEventOptionCommand(-1)</c>, emitted alone once <c>Events[0].IsFinished</c> —
    /// ReplayDispatcher.cs:199-210), the proceed-to-map click after a room resolves, and the
    /// act transition. ActionMap deliberately leaves all of them unmapped (see BuildEvent's
    /// closing note); without this the mask is empty and the bot waits forever — which is
    /// exactly how it stalled on the Neow bonus screen after choosing its bonus.
    ///
    /// Only ever consulted when nothing legal exists, so it can never pre-empt a real decision
    /// (skipping a reward, leaving a shop) — those stay the policy's to make.
    /// </summary>
    private static bool TryAutoAdvance(DecisionContext ctx)
    {
        // fix-reward-loop-brief.md #2 — checked FIRST, ahead of the chain below: a pending,
        // un-declined card reward's ClaimRewardCommand only opens the pick-a-card subscreen
        // (PassiveDump.cs round-5 rule — the real decision is the TakeCardCommand that follows),
        // so ActionMap.BuildRewardScreen never maps it as a choice (see
        // RewardClaimMemory.IsPendingCardClaim's call site there) — this method is what resolves
        // it instead. It must win over every candidate in the chain below, notably
        // ProceedToMapCommand: that command is unconditionally game-truth-legal the moment ANY
        // reward screen is open (ReplayDispatcher.cs:529-536 adds it alongside ClaimRewardCommand
        // whenever ActiveRewardsScreen exists, with no notion of "a card claim is still
        // pending"), so if it were left in the chain's normal priority order it would win this
        // exact race and walk the bot off the reward screen leaving the card unclaimed forever —
        // trading the claim/skip loop for a silent reward-abandonment bug. TryBeginAutoAdvance
        // applies the brief #4 loop backstop and — on success — records this claim's RewardIndex
        // as "the picker currently open" so a later TakeCardCommand.IsSkip dispatch (recorded by
        // RewardClaimMemory.Record, wired into ActionExecutor.Execute) can join back to it and
        // remember the decline. If the backstop instead force-declines it, IsPendingCardClaim
        // goes false and this falls through to the normal chain below on the very next decision.
        var cardClaim = ctx.Available.OfType<ClaimRewardCommand>()
            .FirstOrDefault(RewardClaimMemory.IsPendingCardClaim);
        // Probe read-only at decide time in EVERY mode; the mutating TryBeginAutoAdvance is
        // the commit gate, running when the action actually executes (immediately when the
        // dwell is zero, after the dwell otherwise, on Step while paused). Previously only
        // the preview path deferred; the dwell pipeline makes deferral the universal rule.
        if (cardClaim != null && RewardClaimMemory.CanAutoAdvance(cardClaim))
        {
            GD.Print($"[SpireBot] BotController: auto-advancing interstitial step " +
                     $"({cardClaim.Describe()}) — no RL action id models it.");
            Dispatch(cardClaim, ctx.Kind, ctx, map: null, obs: null, result: null,
                     commitGate: () => RewardClaimMemory.TryBeginAutoAdvance(cardClaim));
            return true;
        }

        // fix-shop-exit-brief.md #4, review round 2 Critical #3: once the REAL shop family is
        // exit-marked for this room (the bot deliberately left via a Shop-kind ProceedToMap —
        // see ScreenExitMemory's doc), OpenShop (real) must never auto-advance again —
        // OpenShopCommand stays enumerable for the rest of the MerchantRoom visit
        // (ReplayDispatcher.cs:547-554, ActiveMerchantRoom only clears on a real room exit) and
        // would otherwise silently reopen the shop the bot just walked away from, exactly the
        // "reopened the shop" loop from the evidence log. OpenFakeShopCommand is deliberately
        // NOT gated by this flag — a fake merchant (NFakeMerchant) can never set
        // ScreenFamily.Shop in the first place (ScreenExitMemory.Record's IsRealShopActive
        // guard), so gating it here would only ever be a no-op at best and, before this flag
        // existed at all, its auto-advance was unconditional — reverted to that.
        bool shopExited = ScreenExitMemory.HasExited(ScreenFamily.Shop);

        ReplayCommand? advance =
            ctx.Available.OfType<ChooseEventOptionCommand>().FirstOrDefault(e => e.RecordedIndex < 0)
            // Opening the shop is not a purchase decision — the sim's SHOP question already
            // assumes the wares are visible, so no action id exists for the click that reveals
            // them. Without this the bot found nothing mappable in a merchant room and the
            // fallback walked away via a map move, skipping the shop entirely.
            ?? (ReplayCommand?)(shopExited ? null : ctx.Available.OfType<OpenShopCommand>().FirstOrDefault())
            ?? ctx.Available.OfType<OpenFakeShopCommand>().FirstOrDefault()
            ?? ctx.Available.OfType<ProceedToMapCommand>().FirstOrDefault()
            ?? (ReplayCommand?)ctx.Available.OfType<ProceedToNextActCommand>().FirstOrDefault();

        if (advance == null)
            return false;

        GD.Print($"[SpireBot] BotController: auto-advancing interstitial step " +
                 $"({advance.Describe()}) — no RL action id models it.");
        Dispatch(advance, ctx.Kind, ctx, map: null, obs: null, result: null);
        return true;
    }

    private static void DispatchWithFallback(DecisionContext ctx, ActionMap map, ObsResult? obs, bool forceFallback)
    {
        PolicyResult result;

        try
        {
            result = forceFallback
                ? new FirstLegalPolicy().Choose(ctx, map, obs)
                : (Policy ?? new FirstLegalPolicy()).Choose(ctx, map, obs);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotController: Policy.Choose threw — falling back to first-legal: {ex}");
            result = new FirstLegalPolicy().Choose(ctx, map, obs);
        }

        ReplayCommand? cmd = map.CommandFor(result.ActionId);
        if (cmd == null)
        {
            GD.PrintErr($"[SpireBot] BotController: policy chose action {result.ActionId}, which " +
                        "ActionMap has no legal command for — retrying with first-legal.");
            result = new FirstLegalPolicy().Choose(ctx, map, obs);
            cmd = map.CommandFor(result.ActionId);
        }

        if (cmd == null)
            StallDiagnostics.ReportIfChanged(ctx, "no mapped action");

        // Skipped once the stuck guard has fired: if an auto-advance were going to work, it
        // already would have — retrying it just spins. Fall through to the scripted fallback.
        if (cmd == null && !forceFallback && TryAutoAdvance(ctx))
            return;

        if (cmd == null)
        {
            string available = string.Join(",", ctx.Available.Select(c => c.GetType().Name));

            // Last resort, only once the stuck guard has already fired: the screen offers
            // commands we failed to map to any action id (a mapping gap — e.g. a rest-site
            // option id or shop entry order we got wrong). The plan's Task 11 guard calls for a
            // scripted action here rather than a dead run, so take the first available command.
            // Loud, because every occurrence is a mapping bug worth fixing, not a normal path.
            if (forceFallback && ctx.Available.Count > 0)
            {
                // Never a potion command (2026-08-20 audit issue 2: the first-available pick
                // dispatched DiscardPotion 0 at a rest site) — a potion is always enumerable
                // and never the way a stuck screen advances, so burning one here is pure loss.
                var scripted = ctx.Available.FirstOrDefault(
                    c => c is not UsePotionCommand and not DiscardPotionCommand);
                if (scripted == null)
                {
                    GD.PrintErr($"[SpireBot] BotController: MAPPING GAP — kind={ctx.Kind} has no action id " +
                                $"for any available command (available={available}), and only potion " +
                                "commands are available — waiting rather than burning one.");
                    return;
                }
                GD.PrintErr($"[SpireBot] BotController: MAPPING GAP — kind={ctx.Kind} has no action id " +
                            $"for any available command (available={available}); dispatching " +
                            $"'{scripted.Describe()}' as a scripted fallback to keep the run alive.");
                Dispatch(scripted, ctx.Kind, ctx, map, obs, result: null, source: "mapping-gap");
                return;
            }

            GD.PrintErr($"[SpireBot] BotController: no legal action at all for kind={ctx.Kind} " +
                        $"(available={available}) — waiting for the next settle signal.");
            return;
        }

        try
        {
            DecisionMade?.Invoke(ctx, result);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotController: a DecisionMade subscriber threw (non-fatal): {ex}");
        }

        try
        {
            Dispatch(cmd, ctx.Kind, ctx, map, obs, result);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] BotController: ActionExecutor.Execute threw for {cmd.GetType().Name} — " +
                        $"the run will wait for the next settle signal to retry: {ex}");
        }
    }

    /// <summary>
    /// Kind==Unsupported handling per Task 11's spec: log the dispatchable-type names + screen,
    /// then (1) if a CrystalSphereClickCommand is available, click a random valid one; else
    /// (2) fall back to the first available command; else (3) wait one settle cycle and do
    /// nothing.
    /// </summary>
    private static void HandleUnsupported(DecisionContext ctx, ActionMap map)
    {
        string typeNames = string.Join(",", ctx.Snapshot.AvailableCommands);
        GD.PrintErr($"[SpireBot] BotController: Unsupported decision — dispatchable types=[{typeNames}] " +
                    $"screen/room={ctx.Snapshot.RoomType ?? "<none>"}");
        StallDiagnostics.ReportIfChanged(ctx, "unsupported");

        // (1) Crystal Sphere: random valid click.
        //
        // OPEN QUESTION: as of this vendoring, this branch is dead code in practice.
        // ReplayDispatcher.GetAvailableCommands() has an explicit no-op for
        // CrystalSphereClickCommand ("cells require deep grid reflection; not enumerable
        // without knowing dimensions" — ReplayDispatcher.cs:324-328), so ctx.Available never
        // contains one today. Worse, CrystalSphereReplayPatch.Postfix — the only thing that
        // ever sets ActiveScreen, which is what makes the type dispatchable at all
        // (ReplayDispatcher.cs:453-455) — itself early-returns unless
        // `ReplayEngine.IsActive` is already true at the moment the screen opens
        // (CrystalSpherePatch.cs:131-132). Since the bot only pulses IsActive true for the
        // brief window of its own ActionExecutor.Execute call, and the Crystal Sphere screen
        // opens on its own as a room-entry side effect (not as a result of us dispatching
        // anything), IsActive will essentially never happen to be true at that exact moment —
        // so ActiveScreen never gets set, and CrystalSphereClickCommand never becomes
        // dispatchable for a bot run at all. This is a real gap in the vendored replay
        // machinery for a live (non-replay) driver, not something Task 11 can fix without
        // either patching CrystalSpherePatch's gating condition or teaching
        // GetAvailableCommands to enumerate real cells — out of scope here; implementing the
        // spec'd "random click among available CrystalSphereClickCommand" logic below in case
        // that gap is ever closed, but flagging that a Crystal Sphere event will currently fall
        // through to (2)/(3) instead.
        var crystalClicks = ctx.Available.OfType<CrystalSphereClickCommand>().ToList();
        if (crystalClicks.Count > 0)
        {
            var rng = new Random();
            var chosen = crystalClicks[rng.Next(crystalClicks.Count)];
            GD.Print($"[SpireBot] BotController: Unsupported fallback — random Crystal Sphere click: {chosen}");
            Dispatch(chosen, ctx.Kind, ctx, map, obs: null, result: null, source: "unsupported-fallback");
            return;
        }

        // (2) A safe interstitial advance, if one exists. This used to be "first available
        // command, whatever it is" — and the 2026-08-20 audit (issue 2) caught it dispatching
        // DiscardPotion 0 at rest sites (the bot threw potions away) and UsePotion 0 mid-combat,
        // because a transitional screen's only enumerable commands were the always-available
        // potion ones. Every Unsupported state observed live is transitional (rest options
        // resolving, a room exiting, an enemy turn): the auto-advance chain covers the ones
        // with a real way forward (finished-event PROCEED, shop opens, proceed-to-map/act,
        // pending card claims), and for the rest WAITING is strictly better than burning a
        // resource — the next settle signal or heartbeat re-decides in under a second.
        if (TryAutoAdvance(ctx))
            return;

        // (3) Nothing safe to do — wait for the next settle cycle. Never dispatch a
        // destructive command (UsePotion/DiscardPotion/SkipRewards/...) just to have acted.
        GD.Print(ctx.Available.Count > 0
            ? "[SpireBot] BotController: Unsupported decision with no safe fallback (available=" +
              $"[{string.Join(",", ctx.Available.Select(c => c.GetType().Name))}]) — waiting rather " +
              "than dispatching a destructive command."
            : "[SpireBot] BotController: Unsupported decision with zero available commands — waiting.");
    }

    /// <summary>
    /// Returns false (and expects the caller to force a fallback) once the same decision has
    /// recurred more than <see cref="StuckThreshold"/> times in a row with no observed state
    /// change. "Same decision" is approximated by kind + the exact set of currently-available
    /// commands' string forms + a few cheap state scalars (room/act/floor/HP/gold) — a coarse
    /// but cheap proxy for "nothing changed," good enough to detect a genuine stall (e.g. a
    /// command that keeps failing to execute) without false-positiving on legitimate repeated
    /// decisions (e.g. playing several 0-cost cards in a row naturally revisits DecisionKind.Combat).
    /// </summary>
    private static bool AdvanceStuckGuard(DecisionContext ctx, ActionMap map)
    {
        string key = BuildDecisionKey(ctx);
        if (key == _lastDecisionKey)
        {
            _repeatCount++;
        }
        else
        {
            _lastDecisionKey = key;
            _repeatCount = 0;
        }

        // Be patient while the game is mid-transition. If the screen offers commands but every
        // one is currently masked off, that is the affordance/precondition gates saying "not
        // clickable yet" (a chest button still animating in, a relic screen not yet accepting
        // picks) — not a stuck bot. The short threshold made the scripted fallback bail out via
        // the only unmasked command, a map move, which walked out of a treasure room without
        // ever opening the chest. Still bounded, so a genuinely dead screen recovers.
        bool waitingOnGate = ctx.Available.Count > 0 && !map.Mask.Any(m => m);
        int threshold = waitingOnGate ? GatedStuckThreshold : StuckThreshold;

        return _repeatCount < threshold;
    }

    private static string BuildDecisionKey(DecisionContext ctx)
    {
        var snap = ctx.Snapshot;
        string availableStr = string.Join("|", ctx.Available.Select(c => c.ToString()));
        return $"{ctx.Kind}::{snap.RoomType}::{snap.Act}::{snap.Floor}::{snap.CurrentHp}::{snap.Gold}::{availableStr}";
    }
}
