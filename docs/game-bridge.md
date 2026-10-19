# Direct game state

The helper now reads the original `mods/BatomonHelperBridge` companion mod instead of capturing screenshots. BatoEyes 0.2.0 was useful as a reference for the state-machine structure; it is not required, copied, or modified. The bridge uses the community Mod Loader 1.2.2 API and coexists with BatoEyes.

## Install

Close the game. From the repository (or installed helper directory), run:

```powershell
.\tools\Install-GameBridge.ps1 -GameDirectory 'D:\SteamLibrary\steamapps\common\Batomon Showdown' -LoaderDirectory 'C:\path\to\extracted\modloader'
```

If the loader is installed, omit `-LoaderDirectory`. The script installs the bridge into `Mods/BatomonHelperBridge`, preserves other mods, and backs up an existing `override.cfg` before adding a missing loader autoload. It does not replace an existing loader. The third-party loader is not bundled with this repository. Download: [community Mod Loader](https://www.nexusmods.com/batomonshowdown/mods/1?tab=description).

Start the game normally and run the helper. The Board tab updates every 500 ms in the shop, including when the game is behind another window. F9 / **Refresh Game State** requests a refresh. The helper no longer captures images or opens OCR correction dialogs. Legacy screenshot readers and fixtures remain in the solution for historical regression coverage.

Bridge 1.3 shows team output and enemy information in the Board tab. Restart the game after updating the installed bridge. Game IDs such as `red_coin` resolve to the catalog's `red-coin`.

Output tables reuse BoardAnalyzer and the training combat resolver, with checkpoints at 0/5/10/15/20/30 seconds. These remain passive-target projections. The Board panel omits explanatory blocks; the shop summary shows estimated target time in seconds and as a percentage of 30 seconds, and position advice appears only when an improved arrangement is available. Live pre-aura stats, cooldowns, extra types and trinkets are imported; hidden progress and other simulator import limits affect these tables, not the game-provided outcome.

## Transport and fields

Bridge 1.3 exports a `battle` phase containing `player`, `opponent`, and optional `outcome: { playerWon: bool }`. Each side includes day, total HP, trainer, rank, wins, six screen-ordered team slots and pre-aura combat metadata/trinkets. BattleState's `_replay_p1_data` / `_replay_p2_data` supply the starting teams after opponent adjustments and before start effects. Passive HP caches are rebuilt only on isolated copies. The team export is cached for that battle-start template.

BattleState already runs a full ghost battle using the game engine and run seed before visual playback finishes. After it completes, the game increments wins on victory, clears `pending_battle_opponent`, marks `pending_round_start` and promotes the completed run through RunManager. The bridge reads those committed fields, verifies run ID and day against the starting player, and reports WIN or LOSS by comparing win counts. Until the completion markers are present, `outcome` is null. No helper combat resampling, extra game simulations, save writes or changes to the run are needed. Outcome updates do not restart the output analysis. The percentage beside estimated target time is a fraction of the 30-second projection, not a chance of victory.

The mod reads objects on the Godot main thread and atomically replaces `user://batomon-helper/state.json` via a sibling temporary file. On Windows the helper reads `%APPDATA%\Godot\app_userdata\Batomon Showdown\batomon-helper\state.json`. The versioned envelope contains UTC heartbeat, sequence, process ID, phase, and board. It writes a heartbeat every 500 ms, including outside the shop. There are no sockets, outgoing requests, automatic updates, process attachment, input generation, or save writes in the bridge.

Verified against the installed Godot 4.7 build:

- RunManager.data supplies trainer, active region, day, lives, wins, HP, gold, rank, free rerolls, team and bench. The helper uses the exported run region for pool calculations and recorded runs.
- The active shop state's manager supplies its five offers, frozen state and actual reroll cost.
- Creature IDs, levels and shiny flags come from instances. `get_stat` supplies final buffed stats, using the same badge selection rules as the game UI. `get_gold_cost` includes discounts. Item prices come from priced shop instances.
- The rank UI's cached weights supply actual modified probabilities, rather than the helper's rank table. The item-use and win-target label strings are read directly as structured game text, without image capture or OCR.
- All 6/4/5 positions are preserved. Unknown IDs, invalid positions, inconsistent affordability, unsupported protocol versions, and heartbeat age over five seconds reject the update and retain the last accepted board. The status identifies disconnected/outside-shop states.

The existing board model still has its original bounds (including rank 1–14, owned creature levels 1–4, and wins no greater than target). Unsupported overlevel/endless states are rejected explicitly. Inventory/trinket ownership and extra roster eligibility remain manual; exporting a board does not make the catalog pool model an exact copy of the game's pool. Custom Godot user-data locations are not currently auto-discovered.

## Verification

The installed game loaded the production bridge under the supplied loader in headless mode. A temporary local harness instantiated real RunData, MonsterInstance, ShopManager and ItemData objects and exported the committed `game-bridge-v1.json` fixture. It exercised a shiny creature with a +17 damage buff, a -$2 shop discount, a custom item price, free rerolls, empty positions and a locked shop. The fixture contains the bridge's output, not game scripts. Tests validate its mapping to the bundled catalog and reject stale/future timestamps, malformed/partial messages, new IDs and inconsistent values. This is not a completed live player-shop UI walkthrough.

Temporary game inspection scripts, recovered sources and tools remain under ignored `work/game-inspection`; none ship with the helper. The temporary inspector mod is removed from the game's Mods directory after verification. Disable the bridge by closing the game and moving `Mods/BatomonHelperBridge` outside Mods.
