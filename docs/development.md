# Development notes

Build using the .NET 10 SDK. The helper installer publishes only BatomonHelper.Overlay.

## Data and limits

The app ships an offline wiki snapshot for Steam build **25738032 / Balance 25**. Creature tooltips show optional cost, cooldown, ability values, and provenance. Prices, stats, and percentages received from the game remain authoritative.

Creatures without a shiny asset declared by the wiki are reported in the bundle warnings. Runtime game-state reading stays offline. To deliberately refresh the Git-tracked bundle, run `dotnet run --project tools/BatomonHelper.CatalogBundler`. The bundler imports the current reference data, stages downloads, checks PNG structure and CRCs, validates the staged catalog, and restores replaced assets if publication fails. The legacy 1.3.0 patch is applied only when importing its required Balance 24 wiki baseline; Balance 25 data is bundled directly. Older snapshots load with empty patch/reference defaults.

The odds engine starts from the game-provided rarity weights and retains the existing region, level-three, and item controls. It does not simulate purchases or infer hidden stat effects.

## Standalone training lab

The LLM advice route and F10 advice shortcut have been removed. A separate WPF training/history application is available:

```powershell

dotnet run --project src/BatomonHelper.Training -c Release

```

The Pantra rules audit has zero missing handlers. The lab now defaults to strategic search and expert iteration, using wiki pairs/compositions, scripted opponents and full-run outcomes. Play Session and history include board DPS, stacking snapshots and time to upcoming-battle HP. Legacy PPO remains available for comparison. Unsourced mechanics remain explicitly labelled guesses; executable coverage does not certify exact game parity or stronger play. See [strategic training and validation gates](strategic-training.md).

