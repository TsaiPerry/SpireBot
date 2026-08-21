# Fix the three SpireBot live command-harness gaps found in the 2026-08-20 run audit

## Context

I watched a live SpireBot run (dump `C:\Users\Perry\Desktop\SpireBot\runs\JKZ3B820B6`,
game log `C:\Users\Perry\AppData\Roaming\SlayTheSpire2\logs\godot.log` from that
session) and an audit confirmed: the deployed model is correct (v18_s21, hash-verified)
and the 351 dumped policy decisions are all mask-legal — but the session log shows
~50 NON-POLICY actions (fallbacks, forced actions, auto-advances gone wrong) that
never enter `decisions.jsonl`. Full audit notes are in auto-memory
`spirebot-live-bot-planned.md`, "2026-08-20: live-run audit" section — read it first.

Repo: `C:\Users\Perry\Desktop\SpireBot` (source in `SpireBotCode\`). Game install with
the deployed mod: `D:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\SpireBot\`
(the csproj's CopyToModsFolderOnBuild target auto-deploys on `dotnet build`; game DLLs
referenced from D:, June-19 build — do NOT update the game). The decompiled game
source for reference greps is `c:\Users\Perry\Desktop\Slay the Spire 2\src\`.

Fix each issue with TDD where a test seam exists, as vendored-file deltas where game
UI flow must be mirrored (record each in `VENDORED-FROM.md`, following deltas 16/17).
STAGE everything, never commit (standing rule). Build must end 0 errors and the DLL
auto-deployed. Dispatch investigations to subagents on sonnet where useful.

## Issue 1 — reward claims silently no-op and the reward is then treated as declined (HIGHEST priority: it lost the act-1 boss reward)

Symptom chain in the log (line ~7781 has the exception):
`BotController: auto-advancing interstitial step (claim reward [0])` →
`[ERROR] System.InvalidOperationException: Tried to sync reward for local player,
but they are not currently viewing any reward set!` at
`MegaCrit.Sts2.Core.Multiplayer.Game.RewardsSetSynchronizer.SelectLocalReward` via
`NRewardButton.GetReward()`, swallowed by TaskHelper.LogTaskExceptions inside
`SpireBotCode\Replay\Commands\ClaimRewardCommand.cs:93` (`InvokeGetReward`, called
from `Execute` line 60) → the claim no-ops → `RewardClaimMemory: reward [0]
declined — suppressing it for the rest of room 'Boss@17'` → reward permanently lost.
It happened 3× (Boss@17, Monster@6 ×2) plus one `RewardLoopGuard` forced decline
(Event@1) that is likely the same root cause hitting the 3-attempt backstop.

This is the same click-before-the-UI-is-ready defect family as delta 16's chest
`BeginRelicPicking` bug (see memory + VENDORED-FROM.md). The claim fires while the
player is "not viewing" the reward set — find the game's gate (grep the game src for
`SelectLocalReward` / "viewing" / `RewardsSetSynchronizer` to learn what marks a
reward set as being viewed) and make `ClaimRewardCommand` (and the auto-advance path
in BotController.TryAutoAdvance) wait for / establish that state before clicking, or
refuse-and-retry rather than click into the void. Critically: a claim that did NOT
execute must NOT be recorded as declined — RewardClaimMemory should only record a
decline when the skip/decline actually dispatched (kind-gated recording precedent:
ScreenExitMemory via `ActionExecutor.Execute(cmd, DecisionKind)`; and note
delta-16's lesson that success paths must be verified, not assumed).

## Issue 2 — disabled rest options are still in the action space; the fallback then discards a potion

Log sequence: `Affordance: refusing 'ChooseRestSiteOption SMITH' — the game has that
control disabled/hidden` → `Unsupported decision — dispatchable
types=[ChooseRestSiteOptionCommand,DiscardPotionCommand,ProceedToMapCommand,
UsePotionCommand] screen/room=RestSite` → `Unsupported fallback — first available
command: DiscardPotion 0`. The bot threw a potion away at a rest site.

Root cause is the known delta-17 "Minor symmetry gap" (memory, delta 17 section):
`ActionMap.BuildRest` maps disabled rest options into the action space; the
affordance guard (correct, keep it) refuses the click, the decision degrades to
Unsupported, and first-available fallback does something destructive. Fix:
(a) mask disabled rest options out in `ActionMap.BuildRest` (mirror how the
dispatcher/game exposes `IsEnabled` on `GetLocalOptions`, per delta 17 guard C), so
the policy can only pick clickable options; (b) audit the Unsupported-fallback
"first available command" ordering — a destructive command (DiscardPotion) should
never be the default fallback when a non-destructive one (ProceedToMap) is
available; prefer an explicit safe-command preference order. Check whether other
Build* surfaces (shop? events?) map disabled controls the same way.

## Issue 3 — combat command completion path sometimes never runs (stalls, dropped EndTurn, forced fallbacks)

Log signatures (frequent: 38 forced fallbacks, 15 all-false masks, 5 StuckRecovery
force-clears, 4 dropped EndTurnCommand):
- `ActionExecutor: dropped undispatchable queued command(s) [EndTurnCommand] —
  still queued after 5s`
- `StuckRecovery: nothing dispatchable for 5s with in-flight flags set
  (cardPlay=True,...) — force-clearing them. This means a command's completion path
  never ran; the underlying gap is worth fixing.`
- long `STALL (unsupported): kind=Unsupported available=[] ... room=CombatRoom`
  bursts with everything unblocked and dispatchable=[], ending in
  `same decision recurred 4x — forcing a fallback action`.

This is the pre-existing "watch next run" residue from showcase bug 1 (2026-08-07,
memory), now frequent. Investigate which card-play command's completion callback
fails to fire (the in-flight flag is set on dispatch of a cardPlay command and
cleared on its completion path — find both ends in ActionExecutor/ReplayDispatcher
and identify which game await/signal never resolves; correlate the stall timestamps
with the dumped decisions around them to identify the cards/screens involved —
JKZ3B820B6 rows near the stalls, and the log's surrounding lines, tell you the
combat and turn). Root-cause before patching: this one needs the actual missing
completion signal found, not another timeout backstop (backstops already exist and
are what saved the run).

## Verification bar (all three)

1. Full test suite green (note pre-existing fails documented in memory).
2. `dotnet build` 0 errors, DLL auto-deployed to the D: mods folder.
3. A live verify checklist for Perry, with exact grep-able log strings, covering:
   boss/monster reward claimed (no `RewardClaimMemory ... declined` unless the
   policy really skipped), a rest site with disabled Smith producing NO
   DiscardPotion fallback, and a full combat with zero
   `StuckRecovery`/`dropped undispatchable` lines. Also carry forward the still-open
   delta-17 live verify (one Smith → one upgrade → rest consumed) and delta-16 chest
   end-to-end.
4. Remember the structural lesson from the audit: fallback/interstitial actions
   bypass decisions.jsonl — judge live fidelity from godot.log fallback/STALL
   counts, never from the dump alone. Consider (cheap, optional) also dumping
   non-policy actions to the jsonl with a `source` field so future audits see them.
