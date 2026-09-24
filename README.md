# BatomonHelper

Personal, external Windows overlay for Batomon Showdown. It is a .NET 10 WPF solution for Visual Studio and deliberately never writes to the Steam installation, process memory, or game input.

## Run

```powershell
dotnet restore BatomonHelper.sln --source https://api.nuget.org/v3/index.json
dotnet run --project src/BatomonHelper.Overlay
dotnet test BatomonHelper.sln --no-restore
```

Press `F8` to hide/show the overlay. **Pin** enables click-through mode. Wiki snapshots and optional run history live only in `%LOCALAPPDATA%\BatomonHelper\batomon-helper.db`.

## Current integration state

The repository contains a versioned, read-only memory-profile engine but intentionally does **not** ship guessed offsets. `profiles/25377295.example.json` is a schema example, not a working profile. A profile must be captured and validated against a running game build before live-state reading is enabled. Until then, the overlay safely reports that no complete profile is installed.

When a profile does not exactly match the Steam build and executable SHA-256, the selected beta policy keeps the reader available but marks every result **UNVERIFIED**. The overlay also compares the installed Steam build with the patch/build declared by the fetched wiki snapshot.

## Profile contract

A usable JSON profile supplies a Steam build ID, executable SHA-256, scalar pointer chains, team/bench/shop record layouts, and numeric-ID maps. The reader opens `batomon_showdown.exe` with only `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ`; it has no write, injection, hook, automation, or network-capture path. Invalid pointer reads, unsupported IDs, invalid counts, and invalid run-state invariants reject a snapshot.

## Data and limits

The wiki importer caches raw provenance: source URLs, retrieval time, page hash, balance, and declared Steam build. It uses only public batomon.com pages and keeps the last valid SQLite snapshot offline if refresh fails. The odds engine covers rank weights, active-set/Level 3+ exclusions, fallback rarities, tickets, fifth-slot item fallback, Research Notes, VIP Pass, Mystic Incense notes, Rainbow Pearl notes, and overflow warnings.

No recommendation engine is enabled yet; the `IRecommendationEngine` interface preserves an explainable, local-only next step.

## Capturing a verified profile

`BatomonHelper.Capture` is a read-only, local console workflow for the running game. It scans only committed writable process pages, stores candidate addresses and manually supplied visible values, and verifies the installed Steam build/hash before every follow-up scan. It neither creates a process dump nor writes to the game.

```powershell
dotnet run --project src/BatomonHelper.Capture -- baseline --out captures/shop.json --gold 10 --shop-rank 1 --day 1 --wins 0 --lives 3 --free-rerolls 0 --is-shop-locked 0 --team-count 1 --bench-count 0 --shop-count 5
# Make exactly one game action yourself, then enter the values currently visible.
dotnet run --project src/BatomonHelper.Capture -- filter --session captures/shop.json --action reroll-1 --gold 7 --shop-rank 1 --day 1
dotnet run --project src/BatomonHelper.Capture -- observe --session captures/shop.json
```

Repeat each controlled action twice until a field has one candidate, then use `pointers` to search for a module-relative chain. Do not copy a generated path into `profiles/` until it survives a game restart and the reader validates it.

For a guided workflow, start the WPF capture UI instead:

```powershell
dotnet run --project src/BatomonHelper.Capture.Ui
```

The UI performs the same baseline, filter, observe, and pointer-path steps without sending any input to the game.
