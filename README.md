# BatomonHelper

Personal, external Windows overlay for Batomon Showdown. It is a .NET 10 WPF solution for Visual Studio and never reads process memory, writes to Steam files, or sends game input.

## Run

```powershell
dotnet restore BatomonHelper.sln --source https://api.nuget.org/v3/index.json
dotnet run --project src/BatomonHelper.Overlay
dotnet test BatomonHelper.sln --no-restore
```

Press `F8` to hide/show the overlay. **Pin** enables click-through mode. Focus a visible Batomon shop and press `F9` (or **Capture Shop**) to take a one-shot OCR capture. Review and correct the proposed state before accepting it; the overlay keeps its previous state if the capture is cancelled or invalid.

## OCR shop reader

The supported overlay source is a UI-aware, fixed-layout shop reader. It captures only the foreground game client, strips genuine black ultrawide side bars, and normalizes the 16:9 shop layout before reading the trainer title, six team sprites/levels, shop rank, the five colored rarity percentages, gold, and the fifth-slot item counter/offer. The displayed gray/green/blue/purple/orange ribbon is authoritative: the overlay does not reconstruct it from item or trainer effects.

The review dialog shows recognition confidence and alternatives. Accepted and rejected reviewed captures are retained as a PNG plus structured state under `%LOCALAPPDATA%\BatomonHelper\capture-history`; the oldest entries are pruned so only the latest 20 remain. Install the Windows English OCR language feature if it is unavailable.

### Fixture templates

The optional `reference-templates.json` alongside the overlay contains fixture-derived perceptual hashes for sprites and fixed-format numeric crops. Each entry has `id`, `kind` (`monster`, `item`, `number`, or `fraction`), and a 64-bit `hash`. Until the representative fixture corpus is supplied, unknown textless cards remain explicit review fields rather than being guessed.

## Data and limits

The wiki importer caches public catalog provenance, including Batomon, trainer, and item references. The odds engine starts from the captured ribbon, uses the Starter set, excludes owned Level 3/4 creatures and their base species, and uses four Batomon slots while an item use remains (five otherwise). It does not simulate item purchases or infer hidden effects.

No recommendation engine is enabled yet; the `IRecommendationEngine` interface preserves an explainable, local-only next step.
