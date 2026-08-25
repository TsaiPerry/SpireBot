# Releasing SpireBot to the Steam Workshop

Item: https://steamcommunity.com/sharedfiles/filedetails/?id=<see scripts/publishedfileid.txt>
App: Slay the Spire 2 (2868840). Windows-only. Main branch only (currently v0.107.1).

## Shipping an update (new model or code)
1. If the model changed: copy the new `model.onnx` / `contract.json` (and `stub.onnx` if changed)
   into `dist/model/`, verify sizes/hashes against the trainer's export, and note the model
   generation (e.g. v23) in the changenote.
2. Bump `version` in `SpireBot.json`. If the game's main branch moved, retest against it and
   update `min_game_version` (game version = `release_info.json` in the install folder).
3. `scripts\stage-workshop.ps1` - must end `Staged OK` with exactly 10 files, no .pdb/.bak.
4. Rehearse: `scripts\steamtest-install.ps1`, launch game, verify ONNX policy active from the
   STEAMTEST copy, then `scripts\steamtest-restore.ps1`.
5. `scripts\publish-workshop.ps1 -Login <user> -Visibility 0 -ChangeNote "<what changed>"`.
6. Spot-check the subscribed copy updates in `steamapps\workshop\content\2868840\<id>\`.

## Gotchas
- If `steamtest-install.ps1` fails partway, the dev mod may be left shelved at
  `mods\SpireBot._localdev` - just run `steamtest-restore.ps1`; it is safe after a partial install.
- The local `mods\SpireBot` dev copy shadows the workshop copy (loader dedup) - shelve it
  (rename) whenever verifying workshop behavior.
- The game loads mods once at startup; any change needs a game restart.
- A game-build promotion to main breaks the mod until it's rebuilt against the new build -
  watch Mega Crit's branch changes.
