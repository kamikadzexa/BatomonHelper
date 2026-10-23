# Batomon Helper 1.0.5

- Stops battle insights blinking during playback. New battle samples continue recording without clearing or recalculating the battle-start projections; WIN/LOSS still updates from the game.
- Corrects cast timing: damage, healing, shielding and status output use stats from before the cast's triggered buffs. Those buffs affect subsequent casts, matching the recorded Mosslug fight.
- Fixes all-type creatures in type checks and scaling, including Rainbow Berry, and rounds the six output stats to whole points like the game.
- Includes Bridge 1.6.0: exports Chemist progress, additional trainer abilities and lost lives, and fixes player battle-frame slot coordinates. New recordings use schema version 3; old version 2 files remain readable.
- Placement suggestions continue to prioritize **fastest time to kill**, with 30-second damage DPS as the tiebreaker. Healing and shielding are shown separately and do not count as damage in this score.

Checked both supplied runs: 287 shop snapshots imported; all 22 recorded outcomes agree with a seed-69 replay in both simulator resolvers. The recordings omit random seeds and trainer progress, so this is outcome agreement, not exact timeline parity. See [validation details](https://github.com/kamikadzexa/BatomonHelper/blob/v1.0.5/docs/run-validation-1.0.5.md).

Install `BatomonHelper-Setup-1.0.5.exe` and verify its accompanying SHA-256 checksum. Then close the game, choose **Game setup → Update/repair mod**, and restart the game to load Bridge 1.6.0. Requires Windows 11 22H2 or newer, x64. The installer is unsigned.
