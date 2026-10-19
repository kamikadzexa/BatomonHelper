# Release validation: 1.0.1

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
