# Development notes

Build using the .NET 10 SDK. The helper installer publishes only BatomonHelper.Overlay.

## Data and limits

The app ships an offline wiki snapshot for Steam build **26078162 / Balance 26**. Creature tooltips show optional cost, cooldown, ability values, and provenance. Prices, stats, and percentages received from the game remain authoritative.

Creatures without a shiny asset declared by the wiki are reported in the bundle warnings. Runtime game-state reading stays offline. To deliberately refresh the Git-tracked bundle, run `dotnet run --project tools/BatomonHelper.CatalogBundler`. The bundler imports the current reference data, stages downloads, checks PNG structure and CRCs, validates the staged catalog, and restores replaced assets if publication fails. The legacy 1.3.0 patch is applied only when importing its required Balance 24 wiki baseline; Balance 26 data is bundled directly. Full normal/shiny levels come from the shared hover snapshot, which must match the declared Steam build and balance. Older snapshots load with empty patch/reference defaults.

The odds engine starts from the game-provided rarity weights and retains the existing region, level-three, and item controls. It does not simulate purchases or infer hidden stat effects.

## Standalone training lab

After refreshing the wiki bundle, regenerate the helper's combat reference:

```powershell
dotnet run --project tools/BatomonHelper.TrainingHost -c Release -- --combat-reference assets/wiki/batomon-wiki.db assets/simulation/knowledge.json assets/simulation/combat-rules.json
```

The helper reads `combat-rules.json`. It contains no item-purchase actions and excludes Witch/Bug Catcher shop-only audit gaps; unknown creature, trinket, or combat trainer rules still block projections. The manifest is marked combat-only and cannot be used for training. Historical training rules remain in `rules.json`.

The LLM advice route and F10 advice shortcut have been removed. A separate WPF training/history application is available:

```powershell

dotnet run --project src/BatomonHelper.Training -c Release

```

The Balance 26 catalog and combat projections are refreshed. The separate training audit still reports unsupported potion/shop rules and retains its older source-review target; it must be reconciled before training on this patch. The lab now defaults to strategic search and expert iteration, using wiki pairs/compositions, scripted opponents and full-run outcomes. Play Session and history include board DPS, stacking snapshots and time to upcoming-battle HP. Legacy PPO remains available for comparison. Unsourced mechanics remain explicitly labelled guesses; executable coverage does not certify exact game parity or stronger play. See [strategic training and validation gates](strategic-training.md).
