# BatomonHelper

Personal, external Windows overlay for Batomon Showdown. It is a .NET 10 WPF solution for Visual Studio and deliberately never writes to the Steam installation, process memory, or game input.

## Run

```powershell
dotnet restore BatomonHelper.sln --source https://api.nuget.org/v3/index.json
dotnet run --project src/BatomonHelper.Overlay
dotnet test BatomonHelper.sln --no-restore
```

Press `F8` to hide/show the overlay. **Pin** enables click-through mode. Focus a visible Batomon shop and press `F9` (or **Capture Shop**) to take a one-shot OCR capture. Review and correct the proposed state before accepting it; the overlay keeps its previous state if the capture is cancelled or invalid.

## OCR shop reader

The supported overlay source is a UI-aware, fixed-layout shop reader. It captures only the foreground game client, strips genuine black ultrawide side bars, and normalizes the 16:9 shop layout before recognizing individual HUD, board, bench, inventory, lock, and shop-card regions. It uses Windows English OCR plus catalog-constrained candidates and optional sprite templates for textless cards.

The review dialog shows recognition confidence and alternatives. Accepted and rejected reviewed captures are retained as a PNG plus structured state under `%LOCALAPPDATA%\BatomonHelper\capture-history`; the oldest entries are pruned so only the latest 20 remain. Install the Windows English OCR language feature if it is unavailable.

### Fixture templates

The optional `reference-templates.json` alongside the overlay contains fixture-derived perceptual hashes for sprites and fixed-format numeric crops. Each entry has `id`, `kind` (`monster`, `item`, `number`, or `fraction`), and a 64-bit `hash`. Until the representative fixture corpus is supplied, unknown textless cards remain explicit review fields rather than being guessed.

## Data and limits

The wiki importer caches raw provenance: source URLs, retrieval time, page hash, balance, and declared Steam build. It uses only public batomon.com pages and keeps the last valid SQLite snapshot offline if refresh fails. The odds engine covers rank weights, active-set/Level 3+ exclusions, fallback rarities, tickets, fifth-slot item fallback, Research Notes, VIP Pass, Mystic Incense notes, Rainbow Pearl notes, and overflow warnings.

No recommendation engine is enabled yet; the `IRecommendationEngine` interface preserves an explainable, local-only next step.

## Legacy memory tooling

`BatomonHelper.GameReader`, `BatomonHelper.Capture`, and `BatomonHelper.Capture.Ui` remain in the solution as dormant developer tooling. The overlay no longer references their memory-profile, process-reading, or profile-output paths.

```powershell
dotnet run --project src/BatomonHelper.Capture -- baseline --out captures/shop.json --gold 10 --shop-rank 1 --day 1 --wins 0 --lives 3 --free-rerolls 0 --is-shop-locked 0 --team-count 1 --bench-count 0 --shop-count 5
# Make exactly one game action yourself, then enter the values currently visible.
dotnet run --project src/BatomonHelper.Capture -- filter --session captures/shop.json --action reroll-1 --gold 7 --shop-rank 1 --day 1
dotnet run --project src/BatomonHelper.Capture -- observe --session captures/shop.json
```

Repeat each controlled action twice until a field has one candidate, then use `pointers` to search for a module-relative chain. Do not copy a generated path into `profiles/` until it survives a game restart and the reader validates it.
