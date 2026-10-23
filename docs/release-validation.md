# Release validation: 1.0.5

Validated locally on 2026-10-23.

- Full Release suite: **481 passed, 0 failed, 0 skipped**. Includes recorded Chemist/Masked Man outcomes in both resolvers, trainer progress import, integer stat rounding, playback refresh preservation, and fastest-TTK placement scoring that rejects a higher-output healer/shielder.
- Both user recordings: **287 shop snapshots imported, 22/22 outcomes match seed-69 replays**, with optimized/reference agreement on winner, duration and remaining HP. See [recorded-run review](run-validation-1.0.5.md) and [JSON results](run-validation-1.0.5.json). Missing game seeds and trainer progress prevent exact timeline claims.
- Bridge 1.6.0 passes native Godot 4.7.2 compilation and isolated smoke execution, including all six player/opponent sample coordinates and lost-life counter export. New runs use schema version 3 to distinguish corrected frame coordinates.
- Installer built with the production script, which repeats native bridge checks, verifies self-contained runtime/catalog/mod/license payloads and produces a SHA-256 checksum. Training and run-validation tools are excluded.
- No installed game files, saves, loader, or running game were changed during this release. Machine-wide installation/UAC was not repeated. The installer remains unsigned.

## Historical validation: 1.0.4

Validated locally on 2026-10-23.

### Replacement installer: Bridge 1.5.1

- Reproduced the released Bridge 1.5.0 parse failure in Godot 4.7.2: `var frame := read_battle_frame(state)` cannot infer a type from a Variant-returning function. Using an untyped local fixes compilation.
- The fixed production bridge passes native Godot compilation and an isolated execution test for battle frames, stat arrays, counters, missing simulation state, and opponent slot mapping. Installer creation now requires these checks through `-GodotPath`.
- Installed Bridge 1.5.1 locally through the normal installation service with the game closed, retaining its backups. The restarted game's log confirms the mod starts, and the user confirms the helper works.
- Rebuilt the 1.0.4 installer and checksum as replacements at the user's request; the helper version remains 1.0.4. Earlier 1.0.4 installations require a manual download of the replacement installer.

### Original installer validation

- Full Release suite: **474 passed, 0 failed, 0 skipped**. Includes new wiki-format/build matching, complete offline assets, Witch projections, Furnadon duplicate-trinket scaling in cached/reference combat, combat-only training refusal, and battle-frame capture/validation cases.
- Wiki import: Steam build **26078162 / Balance 26**, 148 creatures with complete rules, 143 shiny sprites, 25 trainers, 55 items, 93 trinkets. Region membership: 81 Pantra, 85 Jinto. The five existing missing-shiny declarations remain explicit warnings.
- The helper uses a separate generated `combat-rules.json`: zero unresolved combat rules, all imported creature/trinket rules, and no item-purchase actions. Witch and Bug Catcher exemptions require their exact reviewed shop-only text. Combat-only manifests are rejected by Training Lab. Historical training rules and strategy observations retain their original provenance.
- Includes the concurrent Bridge 1.5.0 / captured-run schema v2 changes at the user's request. Recorded battle frames are game observations, not simulator output.
- Installer build uses a clean checkout and verifies the self-contained runtime, wiki database, combat rules, bridge, and license payloads; writes a SHA-256 checksum; excludes training executables and the third-party loader.
- No live-game run, administrator/UAC installation, or real Mod Loader import was repeated for this release. Historical installation checks below retain their original scope. The installer remains unsigned.

## Historical validation: 1.0.3


Validated locally on 2026-10-20.

- `dotnet test BatomonHelper.sln -c Release --nologo`: **464 passed, 0 failed, 0 skipped**.
- Four new Musician regression cases failed before the fix and passed afterward. They cover the single Electric self-knockout Shock cast in both cached and reference analysis, two Electric creatures losing the bonus, and rejection of a worse second-Electric placement.
- Checked the game's Musician passive implementation: it counts Electric creatures in `run_data.team`, including knocked-out creatures. The simulator now uses the same roster rule.
- The installer is built with `Installer/Build-Installer.ps1`, which verifies runtime, catalog, bridge, and license payloads and writes a SHA-256 checksum. No installed game files are changed by this fix.
- Existing manual installation caveats below still apply; this patch does not repeat the administrator/UAC smoke check.

## Historical validation: 1.0.1

Prepared locally on 2026-10-19. No push, tag, or publication was performed.

## Automated checks

- `dotnet test BatomonHelper.sln -c Release`: **499 passed, 0 failed (initial release checks)**, including bridge parsing, installation readiness, rollback, and UI regression coverage.
- Setup tests use temporary game and loader directories. They cover missing game files, partial loader, conflicting configuration, missing/outdated bridge, missing catalog, running-game refusal, access failure rollback, repair, removal, secondary Steam libraries, and preservation of other mods/configuration/custom files.
- Existing bridge tests cover stale/future timestamps, invalid payloads, and incompatible protocol versions.
- UI tests exercise the F8 button and visibility hotkey, F9 refresh dispatch, standard maximize/restore, ignored removed shortcut, persistent hotkey warnings, automatic setup navigation on startup/disconnection or missing mods, loader checkmark/download selection, and setup rendering at 1440×900, 960×640, and 150% scale.

## Installer smoke check

The production installer was compiled with Inno Setup 6.7.3 and publishes a self-contained win-x64 application. The build verifies required runtime/catalog/mod/license payloads and excludes Training Lab executables and the third-party loader.

An isolated copy of the installer definition was compiled with a separate AppId and lowest privileges, then installed into `work/release-smoke/installed`. Clean installation, upgrade from installer version 1.0.0 to 1.0.1, registry version update, application startup, graceful close, and uninstall all passed. A user-created marker file survived upgrade and uninstall. Logs are retained under ignored `work/release-smoke/`.

The production definition retains its original AppId, administrator requirement, and Program Files destination. The administrator/UAC installation and mod-elevation dialogs require a manual check; this session was not elevated. No existing game mods were changed, and the already-running game was not stopped or relaunched.

## Before public distribution

Verify machine-wide installation and mod elevation/cancellation on a clean Windows account, perform the first-run download/import flow with a real Mod Loader archive and a stopped game, and confirm the applicable redistribution rights for bundled wiki/game images. The installer is unsigned. Third-party asset rights are not covered by the project's 0BSD license.
