# Direct game state

The helper now reads the original `mods/BatomonHelperBridge` companion mod instead of capturing screenshots. BatoEyes 0.2.0 was useful as a reference for the state-machine structure; it is not required, copied, or modified. The bridge uses the community Mod Loader 1.2.2 API and coexists with BatoEyes.

## Install

Use **Game setup** in the helper: confirm the game directory and close the game. The checkmark shows whether the loader is installed and enabled. If needed, download and extract [community Mod Loader 1.2.2](https://www.nexusmods.com/batomonshowdown/mods/1?tab=files), choose **Select downloaded loader…**, then select **Install mod**. Use **Launch game** when checks pass.

The loader is not bundled because its published permissions prohibit redistribution. Setup preserves existing loader files, other mods, and configuration, backs up replaced files, and rolls back failures. **Uninstall mod** removes only the bridge's two shipped files, retaining custom files and the loader.

The installed `tools/Install-GameBridge.ps1` uses the same installation service (PowerShell 7 required). Supply `-GameDirectory`, optional `-LoaderDirectory`, or `-Uninstall`. From source also supply `-HelperExecutable` pointing to a published helper. Close the game before changing mods; restart after repairs.

Bridge 1.4 selects the captured region in the helper automatically, as well as showing team output and enemy information in the Board tab. Restart the game after updating the installed bridge. Game IDs such as `red_coin` resolve to the catalog's `red-coin`.

Output tables reuse BoardAnalyzer and the training combat resolver, with checkpoints at 0/5/10/15/20/30 seconds. DPS is cumulative damage divided by elapsed battle time at each checkpoint; at 0s it is zero because no time has elapsed. This includes bursts between markers. These remain passive-target projections. The Board panel omits explanatory blocks; the shop summary shows estimated target time in seconds and as a percentage of 30 seconds, and position advice appears only when an improved arrangement is available. Live pre-aura stats, cooldowns, extra types and trinkets are imported; hidden progress and other simulator import limits affect these tables, not the game-provided outcome.

## Transport and fields

Bridge 1.3 exports a `battle` phase containing `player`, `opponent`, and optional `outcome: { playerWon: bool }`. Each side includes day, total HP, trainer, rank, wins, six screen-ordered team slots and pre-aura combat metadata/trinkets. BattleState's `_replay_p1_data` / `_replay_p2_data` supply the starting teams after opponent adjustments and before start effects. Passive HP caches are rebuilt only on isolated copies. The team export is cached for that battle-start template.

BattleState already runs a full ghost battle using the game engine and run seed before visual playback finishes. After it completes, the game increments wins on victory, clears `pending_battle_opponent`, marks `pending_round_start` and promotes the completed run through RunManager. The bridge reads those committed fields, verifies run ID and day against the starting player, and reports WIN or LOSS by comparing win counts. Until the completion markers are present, `outcome` is null. No helper combat resampling, extra game simulations, save writes or changes to the run are needed. Outcome updates do not restart the output analysis. The percentage beside estimated target time is a fraction of the 30-second projection, not a chance of victory.

The mod reads objects on the Godot main thread and atomically replaces `user://batomon-helper/state.json` via a sibling temporary file. On Windows the helper reads `%APPDATA%\Godot\app_userdata\Batomon Showdown\batomon-helper\state.json`. The versioned envelope contains UTC heartbeat, sequence, process ID, phase, and board. It writes a heartbeat every 500 ms, including outside the shop. There are no sockets, outgoing requests, automatic updates, process attachment, input generation, or save writes in the bridge.

Verified against the installed Godot 4.7 build:

- RunManager.data supplies trainer, active species set, day, lives, wins, HP, gold, rank, free rerolls, team and bench. The bridge maps the current game's `starter` and `oshima` set IDs to Pantra and Jinto respectively, then the helper uses that exported region for pool calculations and recorded runs.
- The active shop state's manager supplies its five offers, frozen state and actual reroll cost.
- Creature IDs, levels and shiny flags come from instances. `get_stat` supplies final buffed stats, using the same badge selection rules as the game UI. `get_gold_cost` includes discounts. Item prices come from priced shop instances.
- The rank UI's cached weights supply actual modified probabilities, rather than the helper's rank table. The item-use and win-target label strings are read directly as structured game text, without image capture or OCR.
- All 6/4/5 positions are preserved. Unknown IDs, invalid positions, inconsistent affordability, unsupported protocol versions, and heartbeat age over five seconds reject the update and retain the last accepted board. The status identifies disconnected/outside-shop states.

The existing board model still has its original bounds (including rank 1–14, owned creature levels 1–4, and wins no greater than target). Unsupported overlevel/endless states are rejected explicitly. Inventory/trinket ownership and extra roster eligibility remain manual; exporting a board does not make the catalog pool model an exact copy of the game's pool. Custom Godot user-data locations are not currently auto-discovered.

## Verification

The installed game loaded the production bridge under the supplied loader in headless mode. A temporary local harness instantiated real RunData, MonsterInstance, ShopManager and ItemData objects and exported the committed `game-bridge-v1.json` fixture. It exercised a shiny creature with a +17 damage buff, a -$2 shop discount, a custom item price, free rerolls, empty positions and a locked shop. The fixture contains the bridge's output, not game scripts. Tests validate its mapping to the bundled catalog and reject stale/future timestamps, malformed/partial messages, new IDs and inconsistent values. This is not a completed live player-shop UI walkthrough.

Temporary game inspection scripts, recovered sources and tools remain under ignored `work/game-inspection`; none ship with the helper. The temporary inspector mod is removed from the game's Mods directory after verification. Disable the bridge by closing the game and moving `Mods/BatomonHelperBridge` outside Mods.
