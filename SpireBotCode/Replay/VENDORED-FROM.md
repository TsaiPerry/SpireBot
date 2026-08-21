# Vendored from RunReplays

- **Source repo:** `c:\Users\Perry\Desktop\RunReplays` (local clone of boardengineer's RunReplays mod, MIT — see `LICENSE` in this directory)
- **Source revision:** `b0d2302ee69bf2ad735e0b6b51aea02408e9ef62` (2026-07-07) **plus** the working tree's one uncommitted delta: `ReplayDispatcher.GetDispatchableTypes()` `private` → `public` (the Task 7 visibility pass; included in this copy)
- **Vendored on:** 2026-08-04
- **Namespace:** `RunReplays[.*]` → `SpireBot.Replay[.*]` (namespace/using lines and namespace-qualified type references only)

## What was copied

- `Commands/*.cs` (26 files — all command classes + capture helpers)
- `ReplayDispatcher.cs`, `ReplayState.cs`, `GameStateSnapshot.cs`
- `Patches/**/*.cs` (30 files — the Harmony screen synchronizers that populate `ReplayState`)
- `Utils/DiagnosticLog.cs`, `Utils/RngCheckpointLogger.cs` (the only Utils the above reference)

## What was deliberately NOT copied

`ReplayEngine.cs` (playback queue), `RunReplayMenu*.cs` / `MainMenuButtonInjector.cs` (menus), `RunOverlay.cs`, `RunReplaysConfig.cs`, `PlayerActionBuffer.cs` (record buffer + its ActionExecutor/CardPlay Harmony patches), autovalidation (`AutoplaySession.cs`), embedded replay resources, `Utils/EncounterRngTracker.cs`, `Utils/ForcedSeedPatch.cs`, `Utils/UnlockAllPatch.cs` (unreferenced by the vendored set; UnlockAll is gameplay-affecting and wrong for a live bot).

## Intentional deltas (each with its justification)

1. **Log-file paths** — `{UserDataDir}/RunReplays/...` → `{UserDataDir}/SpireBot/...` in `RunSaveLogger`, `RunContinuePatch`, `DiagnosticLog`, `RngCheckpointLogger`. *Why:* Workshop RunReplays may be installed alongside SpireBot; both writing the same files would interleave/clobber each other's logs, and RunContinuePatch would restore the wrong mod's buffer.
2. **Manual Harmony id** — `new Harmony("RunReplays.CrystalSphere")` → `"SpireBot.CrystalSphere"` in `CrystalSpherePatch`. *Why:* sharing an id with the real mod would make the two mods' patches indistinguishable / mutually removable.
3. **Game logger name** — `Logger("RunReplays", ...)` → `Logger("SpireBot", ...)` in `ReplayDispatcher`. *Why:* log attribution when both mods are installed.
4. **Shims** (see `Shims.cs`) — minimal stand-ins for the not-copied machinery the vendored files reference, with flags pinned to live-bot values. Build reached 0 errors / 1 pre-existing warning (a nullable-dereference warning already present in vendored `ReplayDispatcher.cs:320`, untouched) with exactly this surface — no rule-(c) ambiguous stops were needed:
   - `ReplayEngine` shim: real (always-empty) `_pending`/`_recentConsumed`/`_replayActive`/`_loadedCommands` fields so `ReplayDispatcher`'s playback bookkeeping compiles but is dead; `IsActive` was originally pinned to `false` on the reasoning "the bot is never inside a recorded playback". **That was wrong and stalled the bot on its first decision (fixed 2026-08-05):** in RunReplays `IsActive` actually means *a programmatic driver is attached*, and every replay-side patch and screen capture gates on it (`if (!ReplayEngine.IsActive) return;`) before writing the `ReplayState` that `GetDispatchableTypes` reads. Pinned false, the dispatcher saw no screens, every decision came back `Unsupported` with zero commands, and because `LogDispatchableChanges` only emits `InputRequired` for a non-empty set, no further wakeup ever fired. `IsActive` now also includes `BotDriving`, set for the whole bot run; use `HasPendingCommand` where "my own dispatch is in flight" is meant. Consequences accepted: the record patches (which guard the opposite way) now skip during bot runs — no duplicate save-logs — and `ReplayDispatcher.GameSpeed` (default 2.0, re-asserted while active) is pinned at run start from `SpireBotConfig.GameSpeed`. `IsReplayRun`, `ActiveSeed`, `ActiveActs`, `PeekNext`, `FireReplayCompleted` are plain pass-throughs per spec. `ConsumeAny()` just dequeues-and-discards — it does **not** reproduce the original's `SignalConsumed` overlay/`ReplayCompleted` signaling, since the queue this bot builds is never populated and `RunOverlay` is a no-op anyway; noted inline in `Shims.cs`. `Load()` throws (`NotSupportedException`) — playback loading was deliberately not vendored; the dev-machine RunReplays fork handles playback.
   - `PlayerActionBuffer` shim: keeps the verbose/minimal record queues (`Record`/`RecordVerboseOnly`/`RecordMinimalOnly`/`UndoLast`/`SnapshotMinimal`/`Restore`) so the Record patches still produce save logs of bot runs, and `GetBattleStateSummary()` copied verbatim (used by the dispatcher's expected-pre-state check). `Record`/`RecordVerboseOnly`/`RecordMinimalOnly` keep the original's `ReplayEngine.IsActive` guard and `ReplayDispatcher.ClearDispatchableCache()` calls; `Record` also keeps the `DiagnosticLog.Write` call (`DiagnosticLog` is vendored). Dev-console logging methods (`LogToDevConsole`, `ForceLogToDevConsole`, `LogDispatcher`, `LogMigrationWarning`) are empty no-ops behind the same `[Conditional]` flags as the original (SpireBot has no dev-console pipeline). The `ActionExecutor` constructor postfix patch, `RecordCardPlayEarly`, `OnReplayCompleted`, `CardPlayRecordPatch`, and the `EntryRecorded` event were not vendored — nothing in the vendored file set calls them.
   - `RunOverlay` shim: no-op statics (`NotifyCardPlayFinished`, `RestoreRecentEntries`) — SpireBot's overlay is `ThinkingOverlay` (Task 14), not RunReplays'.
   - `ModVersion` shim (**not** in the original not-vendored list — found via compile error; rule (a), minimal addition with pinned live-bot semantics): `DiagnosticLog.WriteStartupFingerprint()` and `RunSaveLogger`'s log-file header both reference `ModVersion.Current`. Pinned to the literal `"spirebot-vendored-0.1.0"` (the original's value is `"0.1.0"`) so any diagnostic/log output naming a mod version is visibly distinct from the real RunReplays mod's `"0.1.0"`.
   - `RunReplaysConfig` was **not** needed — no vendored file references it (originally flagged as a possibility in the plan; the vendored set turned out not to read any config settings, so no shim or inline pin was required).
   (No files were deleted and no vendored branches were pruned — every reference resolved via the shim additions above.)

5. **`CardPlayReplayPatch.ClearEndTurnGate()` private → internal** (2026-08-05). *Why:* the bot's `StuckRecovery` watchdog needs to reset the combat gate. The vendored completion path only clears `CardPlayInFlight` when `_dispatching` is still true as the action completes (`AfterActionExecuted`'s `if (!_dispatching) return;`), and a bot-driven play that loses that race leaves the flag set forever — `GetDispatchableTypes`'s `blocked` early-return then disables the bot permanently (observed: one Defend, then `cardPlay=True dispatchable=[]` for the rest of the session). RunReplays' own watchdog doesn't cover it (it only force-clears blockers for a *queued* `MapMoveCommand`). Visibility change only, no behavior change.

6. **`OpenChestCommand.Execute` now checks the button's affordance before emitting `Released`** (2026-08-05). *Why:* a real click is gated in the input path — `NClickableControl.HandleMouseRelease` requires `_isEnabled && IsVisibleInTree() && IsFocused` (`src/Core/Nodes/GodotExtensions/NClickableControl.cs:107`) — and the handler never re-checks, so emitting the signal directly bypasses it. `NTreasureRoom.OnChestButtonReleased` calls `_chestButton.Disable()` after opening, so a player cannot reopen a chest; the bot could, re-running `OpenChest()` and minting a fresh relic set every decision. The other two emit sites (`ConfirmGridSelectionCommand`, `CancelGridSelectionCommand`) already filter on `btn.IsEnabled`, so this restores the convention the rest of the vendored commands follow. See `SpireBotCode/Affordance.cs`.

7. **`ReplayEngine` shim gained `PassiveObserving`** (2026-08-05, Task 16 step 2). *Why:* Task 16's parity grind needs a game-side observation dump produced while the SEPARATE RunReplays fork mod (its own assembly, `RunReplays.ReplayEngine`) drives a recorded run's playback — SpireBot only observes each decision and dumps obs via `DecisionDumper`, without dispatching anything. During fork-driven playback only the fork's own `IsActive` is true; this shim's stayed permanently false (no `_pending`, no `BotDriving`), so every vendored record/screen patch that gates on `if (!ReplayEngine.IsActive) return;` before writing `ReplayState` would skip, leaving `GetDispatchableTypes`/`GetAvailableCommands` blind and every `DecisionContext.Capture()` Unsupported. `PassiveObserving` (ORed into `IsActive` alongside `BotDriving`) makes the vendored recorders record without implying a driver: it does not touch `BotDriving`, `_pending` stays empty, and `BotController` never runs — nothing here can dispatch. **Fix round 1 (2026-08-05):** originally a settable bool that `PassiveDump.TryInstall()` set once, at game-launch, from `SpireBotConfig.PassiveDump`'s value at that moment — a restart-with-flag-preset trap, since enabling the flag later from the in-game config screen could never take effect. It is now a LIVE computed property, `PassiveDump.HookInstalled && SpireBotConfig.PassiveDump`: the Harmony hook on the fork's `CheckPreState` installs unconditionally at init (a no-op prefix while the flag is off), and the config flag is re-read on every dispatch, so toggling it mid-session takes effect on the very next dispatch. Caveat unchanged from before: screens already open at the moment of toggling were not recorded, so enable the flag BEFORE starting the replay (from the main menu is enough — the run's first screens open only after the replay itself starts). Accepted side effect, same shape as delta 4's `BotDriving` note: the record patches (opposite gate) skip while this is on, so SpireBot's own save-log recording is off for the duration — fine for a dev-only mode; the real RunReplays fork still records its own log independently.

8. **`ReplayDispatcher.GetAvailableCommands()` gained `GodotObject.IsInstanceValid` guards before every remaining ReplayState screen-node deref** (2026-08-05, Task 16 passive-dump fix round 2). *Why:* the first real passive dump (89U replay) lost 92 of ~657 expected decision lines to `ObjectDisposedException` (`Godot.Node.GetChildren` on a disposed `Godot.Control`, 69x at `ClaimRewardCommand`, 23x at `MapMoveCommand`). `GetDispatchableTypes()` already guards its own screen reads with `IsInstanceValid` (see e.g. `NMapScreen.Instance`, `ActiveRewardsScreen` checks there), but `GetAvailableCommands()` re-reads the SAME `ReplayState` screen references a second time to expand each dispatchable type into concrete commands, and several of those second reads were missing the guard — `IsInsideTree()` alone does not catch a freed node (a disposed `Control` can still answer `IsInsideTree()` before the deref that throws). This is the same trap the `RunObsWriter.ClassifyReward` fix (Task 15) predicted would recur at other `ReplayState` screen refs. Fixed at every site: `MapMoveCommand` (`NMapScreen.Instance`), `ClaimRewardCommand` (`ActiveRewardsScreen`), `TakeCardCommand` (`CardRewardSelectionScreen`), and the four grid-selection commands (`CardGridScreenCapture.ActiveScreen`, `ChooseACardScreenCapture.ActiveScreen`) for defense-in-depth against the screen being freed in the gap between `GetDispatchableTypes()`'s check and this loop running. A freed screen is now treated as "screen not present" (that command family is skipped for this poll) instead of throwing — `DecisionContext.Capture`'s try/catch was already keeping the run alive on the throw, but the decision line for that poll was still lost; this fixes the loss at the source instead of masking it downstream. Also explains a second observed symptom (surplus Shop/Event lines, decisions relabeled with a stale kind): a stale `ActiveMerchantRoom`/similar reference pointing at a freed screen could make `GetDispatchableTypes` believe a shop/event screen was still open. *Why this is a vendored-logic edit and not a SpireBot-side workaround:* live/passive bot drivers keep polling across screen transitions far more aggressively than the original recorded-playback consumer ever did, so screens go stale mid-poll in a way the original vendored code never had to handle.

9. **`ReplayDispatcher.GetDispatchableTypes()` gained an `IsInstanceValid`/`IsInsideTree` guard on the `ReplayState.ActiveMerchantRoom != null` check** (2026-08-06, round-3 obs-parity fix). *Why:* nothing in either bot driver ever called `ReplayDispatcher.Reset()` (which calls `SubscribeToRoomEvents()`), so `RunManager.Instance.RoomExited` was never subscribed and `OnRoomExited` — the only place that clears `ReplayState.ActiveMerchantRoom` — never fired. The Harmony postfix in `Replay/Patches/Replay/ShopReplayPatch.cs:15` sets `ActiveMerchantRoom` once and it then stuck non-null for the rest of the run, so `GetDispatchableTypes()` kept unconditionally unioning in the shop command types and `DecisionContext.Classify()` (which checks Shop before Rest/Map) mislabelled every post-shop Map/Rest decision as Shop. Fixed at the root by having `PassiveDump.BeginRun()` and `BotController.StartBotRun()` both call `ReplayDispatcher.Reset()` so the subscription actually happens (SpireBot-side change, not itself a vendored delta). This guard is defense-in-depth for the same freed/stale-reference class of bug already fixed at every other `ReplayState` screen ref (see delta 8) — matches the existing `NMapScreen.Instance`/`ActiveRewardsScreen` pattern exactly.

10. **`ReplayDispatcher.OnRoomExited()` gained a `CardGridScreenCapture.Clear()` call** (2026-08-06,
    round-4 obs-parity fix 1). *Why:* `CardGridScreenCapture.ActiveScreen` (`Commands/CardGridScreenCapture.cs:40`)
    is set in the Harmony `Prefix` on `NCardGridSelectionScreen.CardsSelected` whenever a real
    card-grid screen resolves (Neow bonus removal, rest-site Smith upgrade/remove/transform,
    Seeker Strike, etc.), and was cleared in exactly one place — `SelectGridCardCommand.Execute()`
    (`Commands/SelectGridCardCommand.cs:54`) — plus a `Clear()` helper (`CardGridScreenCapture.cs:106-109`)
    that was never called anywhere. `SelectGridCardCommand.Execute()` only runs when a driver
    itself dispatches the command; `PassiveDump` never does (it only observes `CheckPreState`),
    so in passive-dump mode the one reset path for `ActiveScreen` never fired. Once a genuine
    SelectCards screen appeared, the stale `NCardGridSelectionScreen` node stayed
    `IsInstanceValid`+`IsInsideTree` (Godot doesn't free/detach it immediately — same lifecycle
    trap delta 9 fixed for `ActiveMerchantRoom`), so every subsequent `DecisionContext.Classify`
    call (which checks SelectCards-family commands before Map/Event/Shop/Rest) mislabelled every
    following decision as `SelectCards` until an unrelated room transition happened to free the
    node — matching the round-4 diagnosis's "19 missing Map idx=0 floors" / "25 carved-out
    SelectCards lines" evidence. Fixed at the root, same shape as delta 9: `ReplayDispatcher.
    OnRoomExited()` now also calls `CardGridScreenCapture.Clear()` alongside its existing
    `ReplayState.ActiveMerchantRoom = null`, so the stale screen can never outlive its own room.

11. **No vendored-file edits this round — SpireBot-side only** (2026-08-06, round-5
    obs-parity fix). *Why noted here anyway:* round 5 replaced `PassiveDump`'s reliance on
    `DecisionContext.Classify`'s screen-based kind with a command-type-driven kind derived
    from the exact `ReplayCommand` the fork is about to dispatch (`PassiveDump.
    ClassifyCommandKind`, `DecisionContext.Capture`'s new optional `kindOverride`
    parameter) — the screen heuristic is provably unfixable for the passive dump (a
    decision can be mislabeled by a stale-but-still-enumerable screen from the room the bot
    is STILL standing in, before any `RoomExited`, so no more "clear the stale ref on room
    exit" vendored edit like deltas 9/10 above can close it). Reading this round's fixes
    required re-reading several vendored command docs closely (`ClaimRewardCommand`,
    `OpenChestCommand`, `SelectGridCardCommand` vs `ClickGridCardCommand`/
    `ConfirmGridSelectionCommand`, `UsePotionCommand`'s out-of-combat branch) to get the
    kind map and the reward/grid-selection interstitial skips right, but none of those
    files themselves needed a behavior change — every fix landed in `PassiveDump.cs`/
    `DecisionContext.cs` instead. Findings, for the record: (a) `ClaimRewardCommand` for a
    `CardReward` button only opens the card-pick subscreen — the real REWARD_CARD-matching
    decision is the `TakeCardCommand` that follows, so the claim click itself is now
    skipped for card rewards (still dumped for `RelicReward`/`PotionReward`, which grant
    immediately); (b) `OpenChestCommand` is the same shape — it only opens the chest,
    `TakeChestRelicCommand` is the real pick — now skipped; (c) the two-step grid-selection
    recording shape (`ClickGridCardCommand`×N + `ConfirmGridSelectionCommand`) was
    producing one dumped line per click instead of one per screen (sim's `_card_selector`
    asks once per screen); now deduped by `CardGridScreenCapture.ActiveScreen` identity,
    with `ConfirmGridSelectionCommand`/`CancelGridSelectionCommand` always skipped as pure
    follow-up clicks; (d) an out-of-combat `UsePotionCommand`/`DiscardPotionCommand` has NO
    sim-side decision at all (`driver.py`'s `DecisionRequest.potion_actions()` folds
    AnyTime belt potions into whatever decision is already pending instead of asking a
    standalone one) — now an explicit skip instead of falling through to `Unsupported`; an
    IN-COMBAT potion use, previously falling through to `Unsupported` and being silently
    dropped whenever it was a combat's last decision with no `PlayCardCommand`/
    `EndTurnCommand` also available, now classifies straight to `Combat`.

12. **No vendored-file edits this round either — SpireBotCode-side only** (2026-08-06,
    round-6 obs-parity fix). All six fixes landed outside `Replay\`: `PassiveDump.cs`
    (treasure-chest auto-grant skip, duplicate `ClaimRewardCommand` dedup, act-0-floor-1
    Neow-event skip hardening), `Obs/CombatObsWriter.cs` (hand-row `magic_number` scan),
    `Obs/SessionState.cs` (intent-history push no longer drops dead-but-retained enemies),
    `Obs/RunObsWriter.cs` (act-0's first Map decision's row derivation). Findings for the
    record: (a) `TakeChestRelicCommand` was still being dumped as a `RewardScreen` decision
    even though `sts2_rl/driver.py`'s TREASURE branch auto-grants the chest relic inside
    `enter_point` with no `_ask` at all — now skipped exactly like `OpenChestCommand`;
    (b) a duplicate `ClaimRewardCommand` re-dispatch for the same still-open reward button
    (confirmed byte-identical at act0 floor13 idx0/idx1 in the 89U game dump) was inflating
    the RewardScreen decision count by one per affected floor — deduped on (rewards-screen
    identity, RewardIndex); (c) the act-0 Neow-event skip's sole signal
    (`ReplayState.ActiveEventSynchronizer`) can read null at the very start of a run before
    this assembly's own event-synchronizer patches have populated it (a real timing gap
    against the fork's `CheckPreState` prefix) — `IRunState.ActFloor == 1` is now an
    additional, timing-independent, strictly-narrower-than-"any event" skip signal (act 0's
    floor 1 is always the ancient/Neow node); (d) shop-leave was investigated and found to be
    a genuine sim/game structural mismatch, not a C# bug — see the round-6 report for the
    full rationale; no code change for it.

13. **`ReplayDispatcher.GetAvailableCommands()`'s `MapMoveCommand` enumeration now calls
    `MapTravel.GetTravelablePointsFrom(state, currentPoint.Point)`** (2026-08-06, round-7
    obs-parity + live-bot-correctness fix). *Why:* the vendored code read
    `currentPoint.Point.Children` directly, which is only correct absent a free-travel
    effect. `MapTravel.GetTravelablePointsFrom` (`Core/Map/MapTravel.cs:14-21`, public
    static, no reflection needed) is the game's actual rule: `currentPoint.Children`
    normally, but — whenever `Hook.ShouldAllowFreeTravel(runState)` is true (Winged Boots
    and any other `AbstractModel.ShouldAllowFreeTravel` override) — the WHOLE next row via
    `runState.Map.GetPointsInRow(currentPoint.coord.row + 1)` instead. This is not merely a
    parity gap: it is a live-bot correctness bug — the action mask built from
    `GetDispatchableTypes()`'s `MapMoveCommand` set illegally denied every legal
    free-travel destination outside the current point's own children whenever a free-travel
    relic was held, in addition to mismatching passive-dump obs against the sim (which
    already mirrors `GetTravelablePointsFrom`, confirmed across both the 89U and 933T
    seeds — see `sts2_rl/run.py:1422-1432`'s `travelable_points()` docstring). Fixed at the
    same two sites the doc comment in `Obs/RunObsWriter.cs:408-439` (`WriteMapSlots`)
    already flagged as needing to agree with `ReplayDispatcher`'s enumeration:
    `ReplayDispatcher.cs`'s `MapMoveCommand` branch (~line 184) and
    `RunObsWriter.WriteMapSlots`'s `children` list (~line 438) both now call
    `MapTravel.GetTravelablePointsFrom` instead of reading `.Children`. Ordering is
    preserved: `ActMap.GetPointsInRow` (`Core/Map/ActMap.cs:49-61`) already yields points in
    ascending-column order, and `sts2_rl/run.py:1422-1432`'s `travelable_points()` sorts by
    `(col, row)` — trivially equivalent to a col-only sort here since every candidate in a
    free-travel call shares one row, and every child of a single point already shares one
    row too — so `DecisionLists.MapMoves`'s existing `OrderBy(m => m.Col)` and
    `RunObsWriter`'s existing `OrderBy(p => p.coord.col)` both still line up slot-for-slot
    with the sim without further changes. The act-entry "offer every row-0 node" special
    case (`ReplayDispatcher.cs`'s `else` branch keyed on `dict.TryGetValue` failing to find
    a `currentCoord`) is untouched — it runs before `MapTravel` is ever consulted, since
    there is no current point yet to call it with.

14. **`ReplayDispatcher.GetAvailableCommands()`'s `MapMoveCommand` enumeration gained a
    pre-boss-row special case** (2026-08-06, round-8 obs-parity + live-bot-correctness
    fix). *Why:* `NMapScreen.RecalculateTravelability` (`Core/Nodes/Screens/Map/
    NMapScreen.cs:401-405`) marks the boss node travelable directly once the player's
    current row is the last row before the boss (`mapCoord.row == map.GetRowCount() - 1`),
    bypassing `MapTravel.GetTravelablePointsFrom`/`Children` entirely — because not every
    `ActMap` generator wires the boss point into every last-row point's `Children`
    (`GoldenPathActMap.cs:66` only adds it to ONE last-row point). Without this, the
    enumerated legal-move set at the pre-boss row silently omitted the only real move,
    denying it to the live bot's own action mask (a correctness bug, not just an obs
    mismatch) — this produced the map0 idx0 (present) mismatches at act0 floor16 / act1
    floor15 also fixed in `Obs/RunObsWriter.WriteMapSlots` (see that file's own
    "PRE-BOSS ROW FIX" comment, ~line 443). Fixed at the same enumeration root as delta 13:
    when `currentPoint.Point.coord.row == map.GetRowCount() - 1`, the branch now adds a
    single `MapMoveCommand(map.BossMapPoint.coord.col)` instead of enumerating
    `MapTravel.GetTravelablePointsFrom`. A RECORDED replay move to the boss was never
    affected by this gap — `MapMoveCommand.Execute`/`AutoSelectMapNode` looks its
    destination up by raw `(col, row)` coordinate directly in `NMapScreen`'s own
    `_mapPointDictionary` (which always contains the boss node, since the UI renders the
    whole graph rather than filtering by travelability), not through this enumeration — so
    this was a live-mask/obs-parity gap only, never a replay-dispatch failure.

15. **`CardGridScreenCapture` gained a `GetPrefs` helper (+ `PrefsFieldCache`)**
    (2026-08-06, round-8 obs-parity fix, retroactively logged). *Why:* every
    `NCardGridSelectionScreen` subclass (`NSimpleCardSelectScreen`, `NDeckCardSelectScreen`,
    `NDeckUpgradeSelectScreen`, `NDeckTransformSelectScreen`, `NDeckEnchantSelectScreen`,
    `NCombatPileCardSelectScreen`) stores its own private `CardSelectorPrefs _prefs` field
    (same name/type, declared per-subclass — there is no shared base-class field) that
    `CardSelectCmd` populated from `MinSelect`/`MaxSelect` when the screen was opened.
    `GetPrefs` (`Commands/CardGridScreenCapture.cs:55-64`) reflects it off the screen's
    CONCRETE runtime type, caching the `FieldInfo` per `System.Type` in
    `PrefsFieldCache` (`:53`), so `RunObsWriter.WriteSelect` can source `select.count` the
    same way `sts2_rl/run_env.py`'s `request.count_remaining` does (`driver.py`'s
    `_card_selector` sets it to `count - i`; at the first ask, `i == 0`, the only case
    observed live, so that's the full `count` == this screen's `MaxSelect`). Purely
    additive — no existing vendored method's behavior changed.

16. **`TakeChestRelicCommand.Execute` gained a chest-must-be-open refusal** (2026-08-16,
    live-bot fix). *Why:* `Affordance.RelicPickingActive()` (delta from Task 16) mirrors the
    backend precondition, but the backend accepts a pick from ROOM ENTRY —
    `TreasureRoom.Enter` calls `BeginRelicPicking()` when the room loads
    (`TreasureRoom.cs:47`), before the chest opens. A pre-open pick then executes and crashes
    the game's own treasure UI (`NTreasureRoomRelicCollection.AnimateRelicAwards` runs
    `First()` over relic nodes only the open animation creates), wedging the room — observed
    live in run SXFY52G6VQ (act1 floor9) when the policy sampled Take before Open. A real
    player is physically held to open-first because the relic buttons don't exist yet; the
    delta refuses the pick while the `%Chest` button is still clickable, mirroring that.
    `ActionMap.BuildRewardScreen` gates its "Take chest relic" mapping the same way
    (non-vendored side of the same fix). Recorded replays are unaffected — recordings always
    carry OpenChest before TakeChestRelic.

17. **`SelectGridCardCommand.Execute` gained the game's own overlay-close step, and
    `ChooseRestSiteOptionCommand.Execute` gained a button-affordance refusal**
    (2026-08-16, live-bot fix). *Why:* run SXFY52G6VQ (act0 floor12) showed the decision
    dump alternating `Rest:Smith`→`SelectCards` eight times at a constant hp of 0.84; the
    save (`floor_23/run.save`) recorded only two real upgrades (TWIN_STRIKE,
    DRAMATIC_ENTRANCE, both `current_upgrade_level=1`) — attempts 3-8 opened a select screen
    and minted nothing, and the loop only ended when a 3%-probability Heal was sampled
    instead of Smith. Root cause, found by comparing `SelectGridCardCommand.Execute`
    against the game's own single-select confirm path
    (`NDeckUpgradeSelectScreen.CheckIfSelectionComplete:248-257`,
    `Core/Nodes/Screens/CardSelection/NDeckUpgradeSelectScreen.cs`): the real game pairs
    `_completionSource.SetResult(...)` with `NOverlayStack.Instance.Remove(this)`
    (`Core/Nodes/Screens/Overlays/NOverlayStack.cs`) in the same handler — removing the
    screen from the overlay stack is not a side effect, it *is* how the screen closes.
    `SelectGridCardCommand.Execute` only did the reflection-based
    `CardGridScreenCapture.ConfirmSelection` (`tcs.TrySetResult(cards)`) half of that pair,
    so the `NDeckUpgradeSelectScreen` instance stayed on the overlay stack as a ghost after
    every Smith pick. **Fix A** (`SelectGridCardCommand.cs:53-65`): after
    `ConfirmSelection`, defer a call that mirrors the game's own close — validity-check the
    screen, then `NOverlayStack.Instance?.Remove(screen)` — before clearing
    `CardGridScreenCapture.ActiveScreen`. `NOverlayStack` lives in
    `MegaCrit.Sts2.Core.Nodes.Screens.Overlays` (not the `...Core.Nodes` namespace the
    brief's starting hypothesis guessed); `NCardGridSelectionScreen : Control,
    IOverlayScreen, ...` (`NCardGridSelectionScreen.cs:28`) so the captured `screen` value
    can be passed to `Remove(IOverlayScreen)` directly, no cast needed. Research answers for
    the record (`RestSiteSynchronizer.cs`, `NRestSiteRoom.cs`): (1) yes —
    `RestSiteSynchronizer.ChooseOption` (`:123-153`) only calls `restSite.options.RemoveAt`
    /`.Clear()` for the chosen option when `option.OnSelect()` returns `true`; every other
    surviving option (and, if `OnSelect()` returns `false`, the chosen one too) stays in the
    list `GetLocalOptions()` returns, and `SmithRestSiteOption.OnSelect`
    (`Core/Entities/RestSite/SmithRestSiteOption.cs:56-74`) returns `false` whenever the
    resolved selection is empty — so a still-listed, still-`IsEnabled`
    (`Deck.UpgradableCardCount != 0`) SMITH option is exactly what the dispatcher's rest
    enumeration (`ReplayDispatcher.cs:221-227`, which calls `sync.GetLocalOptions()`
    directly) can re-offer while a prior Smith pick's screen is still an unresolved ghost
    behind a newer one; (2) `NRestSiteRoom.AfterSelectingOption` (`:176-179`) just forwards
    to `AfterSelectingOptionAsync` (`:363-379`), which plays VFX, calls
    `UpdateRestSiteOptions()` to rebuild one `NRestSiteButton` per option still in
    `Options` (`== _room.Options == sync.GetLocalOptions()`, confirmed in
    `Core/Rooms/RestSiteRoom.cs:23` — there is no second options list to desync from), and
    re-shows the choices container; our deferred call at
    `ChooseRestSiteOptionCommand.cs:76` doesn't itself throw or fail silently, but it races
    the SAME ghost-overlay problem — the room happily rebuilds and re-enables its buttons
    underneath an overlay stack whose top is still the unclosed screen, so nothing in the
    UI layer stops a second dispatch from targeting the newly-rebuilt (or still-there)
    SMITH button; (3) attempts 3-8 opened real, distinct `NDeckUpgradeSelectScreen`
    instances (the Harmony capture patch fires per-instance on `CardsSelected`) and a real
    `SelectGridCard` decision was recorded for each, but `SmithRestSiteOption.OnSelect`
    calls `CardCmd.Upgrade(item, ...)` on whichever card the bot's deterministic policy
    picked at that grid index; once the two genuinely-upgradable candidates the policy
    favors at hp 0.84 were already smithed by attempts 1-2, later attempts kept re-selecting
    an already-upgraded (or otherwise non-upgradable) card at the same index, so the click
    round-tripped through a real screen but `CardCmd.Upgrade` had nothing left to do —
    consistent with "opened select screens but applied no upgrade" while `IsEnabled`
    (`UpgradableCardCount != 0`, driven by the rest of the deck) kept SMITH selectable.
    **Fix B** (`ChooseRestSiteOptionCommand.cs`, defense-in-depth, same shape as delta 16's
    chest-button gate) went through two revisions. **Revision 1** (initial) checked
    `Affordance.IsLive` on the option's `NRestSiteButton` but then dispatched by calling
    `RestSiteSynchronizer.ChooseLocalOption` directly (the pre-existing mechanism) — review
    caught that this never observes the race it claims to guard: the *only* code that
    disables rest-site buttons is `NRestSiteButton.SelectOption` (`:139-171`), reached
    exclusively from the button's own `OnRelease()` override (`:130-137`), which a direct
    synchronizer call never goes through. So during the exact ghost-overlay window Fix A
    closes, `IsEnabled` was still `true` and the check let the dispatch through anyway — the
    only case it actually caught (option already removed from `Options`) was already caught
    by the preceding null-lookup retry. **Revision 2** (current, `ChooseRestSiteOptionCommand.
    cs:56-73`) fixes this by driving the button's own click path instead of the synchronizer:
    after the `Affordance.CheckOrRefuse` gate passes, it calls `button.ForceClick()`
    (`NClickableControl.cs:255-259`, "Used by AutoSlay for automated testing") rather than
    `EmitSignal(Released, ...)` the way delta 16's chest/proceed buttons are driven — those
    are wired via an external `.Connect(SignalName.Released, ...)` subscriber, but
    `NRestSiteButton` never `Connect`s one, so a raw `Released` emit does nothing here;
    `SelectOption` only runs from `OnRelease()`, which only a real click or `ForceClick()`
    invokes. `ForceClick()` calls `OnRelease()` synchronously, which calls
    `TaskHelper.RunSafely(SelectOption(Option))`; `SelectOption`'s async body runs
    synchronously up to its first `await`, and `NRestSiteRoom.DisableOptions()` sits before
    that `await` (`:146-147`), so by the time `ForceClick()` returns to `Execute()` every
    rest-site button — including the one just clicked — already has `IsEnabled == false`.
    This makes the affordance check load-bearing: a second dispatch landing in the same
    window now sees a genuinely disabled button and is refused, where revision 1 would have
    let it through. `SelectOption` also performs the identical
    `RestSiteSynchronizer.ChooseLocalOption(...)` call and, on success, the identical
    `NRestSiteRoom.AfterSelectingOption(option)` call this command used to make by hand — so
    the multiplayer message-send path (`ChooseLocalOption` → `OptionIndexChosenMessage` via
    `_netService.SendMessage`) is unchanged; only the gating and error-recovery
    (`SelectOption`'s `finally` re-enables options on failure, which the hand-written version
    didn't do either) now come from the game's own vetted code instead of being partially
    reimplemented. Both fixes now coexist: Fix A closes the hole that let a second Smith
    dispatch reach a live button in the first place; Fix B (revision 2) refuses it — for real,
    against actually-observed `IsEnabled` state — even if Fix A is somehow bypassed or a
    future option type has the same `OnSelect`-returns-`false` shape as Smith's. A recorded
    replay never exercised either bug — it only ever contains clicks/confirms a real player
    made, which already obey the game's UI gating (including the disable/enable this
    revision now drives through), so recorded-replay playback masked this the same way it
    masked the chest bug in delta 16.

    **Fix C** (`ChooseRestSiteOptionCommand.cs`, added 2026-08-16 final-review pass, third
    guard): the button-affordance gate (Fix B) checks `NClickableControl.IsEnabled`, which is
    driven only by `NRestSiteRoom.DisableOptions()`/`EnableOptions()` — it says nothing about
    whether the chosen *option itself* is usable. `RestSiteOption.IsEnabled`
    (`Core/Entities/RestSite/RestSiteOption.cs:37`, e.g.
    `SmithRestSiteOption.IsEnabled => Deck.UpgradableCardCount != 0`, or Cook's `>=2` removable
    requirement) is instead snapshotted once into `NRestSiteButton._isUnclickable` at
    `NRestSiteButton.Create` (`:~107`) and checked inside the button's own `OnRelease()`
    override (`if (!_isUnclickable)`) — a layer *below* where `Affordance.IsLive` looks.
    `ReplayDispatcher.cs:221-227` enumerates every `sync.GetLocalOptions()` entry with no
    `IsEnabled` filter, so a policy naming an option on a deck with nothing left to
    smith/remove sails past Fix B's gate, reaches `ForceClick()` → `OnRelease()`, and silently
    no-ops on the `_isUnclickable` check — `Execute()` still returns `Ok()`, the room re-offers
    the same dead option, and a live policy can loop on it indefinitely (the pre-Fix-B direct
    `ChooseLocalOption` dispatch executed regardless of `IsEnabled`, so this is a regression
    class delta 17 exists to eliminate, not a pre-existing one). Fix: check
    `chosenOption.IsEnabled` before the button lookup / `CheckOrRefuse` gate and refuse with the
    same log-and-`Ok()` idiom used throughout this file and `TreasureCommands.cs` — the
    dispatcher does not treat the option as consumed, so it re-decides on the next tick instead
    of believing a real click landed.

18. **`ClaimRewardCommand.Execute()` gained a state-level "is the local player actually viewing
    this reward set" refusal** (2026-08-20, live-run-audit fix — issue 1 of
    `PROMPT-fix-live-gaps-20260820.md`). *Why:* the game keeps a terminal `NRewardsScreen`
    alive forever (showcase bug 2's root fact — `RunManager.ProceedFromTerminalRewardsScreen`
    only opens the map), so its `NRewardButton`s stay enumerable and Affordance-live after
    `RewardsSetSynchronizer` already popped the set's `rewardsStack` entry (set completed,
    skipped, or `BeforeLeavingRoom()` on a room exit — `RewardsSetSynchronizer.cs:317/344/400`).
    Invoking `GetReward()` on such a button reaches `SelectLocalReward`, which throws
    `"Tried to sync reward for local player, but they are not currently viewing any reward
    set!"` (`RewardsSetSynchronizer.cs:201-203`) **inside `TaskHelper`'s swallowed task** — the
    command still returns `Ok()`, so the claim silently no-ops while looking executed (observed
    live: run JKZ3B820B6, godot.log:7774, an auto-advance claim fired during the act-1→act-2
    transition). "Viewing" has no public predicate — it is implicitly
    `GetRewardStateForPlayer(LocalPlayer).rewardsStack.Count > 0`, established synchronously by
    `BeginRewardsSet` before the screen even exists (`RewardsSet.cs:161` vs `:192`) — so the
    delta re-applies the game's own precondition via the publicized private members
    (`Affordance.RewardClaimWouldLand`: viewing active AND the clicked button's `Reward` is on
    the TOP stack entry, because `SelectLocalReward` always operates on `rewardsStack.Last()`)
    and refuses with the standard log-and-`Ok()` idiom. Same "state-level precondition" form
    as the treasure-relic gate (stall #10) and delta 16's chest. The mask/auto-advance layers
    (`ActionMap.BuildRewardScreen`, `RewardClaimMemory.IsPendingCardClaim`) apply the same
    predicate SpireBot-side, so this refusal is defense-in-depth, not the primary gate.

## Maintenance rule

Phase 5 grind fixes that touch vendored logic must be applied here AND noted in this file (and optionally mirrored to the local RunReplays fork).
