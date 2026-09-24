# BatomonHelper

Personal, external Windows overlay for Batomon Showdown. It is a .NET 10 WPF solution for Visual Studio and never reads process memory, writes to Steam files, or sends game input.

## Run

```powershell
dotnet restore BatomonHelper.sln --source https://api.nuget.org/v3/index.json
dotnet run --project src/BatomonHelper.Overlay
dotnet test BatomonHelper.sln --no-restore
```

Press `F8` to hide/show the Creature Browser. **Pin** enables click-through mode. Use its name, region, and rarity filters to browse the bundled catalog; the selected region also filters the eligible creature pool for `F9`/ **Capture Shop**. Review and correct the proposed state before accepting it; the browser keeps its previous state if the capture is cancelled or invalid.

## OCR shop reader

The supported overlay source is a UI-aware, fixed-layout shop reader. It captures only the foreground game client, strips genuine black ultrawide side bars, and normalizes the 16:9 shop layout before reading the trainer title, shop rank, the five colored rarity percentages, gold, and the fifth-slot item counter/offer. The colored percentage glyphs are read through a dedicated high-contrast palette mask. The displayed gray/green/blue/purple/orange ribbon is authoritative: the overlay does not reconstruct it from item or trainer effects.

The review dialog shows recognition confidence and alternatives. Accepted and rejected reviewed captures are retained as a PNG plus structured state under `%LOCALAPPDATA%\BatomonHelper\capture-history`; the oldest entries are pruned so only the latest 20 remain. Install the Windows English OCR language feature if it is unavailable.

## Data and limits

The app ships a version-pinned SQLite wiki snapshot and creature sprites in `assets/wiki`, and never refreshes them at runtime. To deliberately refresh that Git-tracked bundle for a future release, run `dotnet run --project tools/BatomonHelper.CatalogBundler`. The odds engine starts from the captured ribbon, uses the full Starter set while creature identification is disabled, and uses four Batomon slots while an item use remains (five otherwise). It does not simulate item purchases or infer hidden effects.

No recommendation engine is enabled yet; the `IRecommendationEngine` interface preserves an explainable, local-only next step.
