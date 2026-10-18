# BatomonHelper

Personal Windows helper for Batomon Showdown: a .NET 10 WPF overlay with a read-only companion mod. The mod exports game objects directly; the helper no longer captures screenshots or uses OCR.

## Run

```powershell
dotnet restore BatomonHelper.sln --source https://api.nuget.org/v3/index.json
dotnet run --project src/BatomonHelper.Overlay -c Release
dotnet test BatomonHelper.sln --no-restore
```

## Windows release installer

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run:

```powershell
.\Installer\Build-Installer.ps1
```

The script publishes a framework-dependent x64 build, downloads and embeds the required .NET 10 Windows Desktop Runtime when it is not already staged, and creates `Installer\output\BatomonHelper-Setup-1.0.0.exe`. The installer is machine-wide, creates Start Menu and desktop shortcuts, and has no automatic update capability.

## Direct game state

Install the supplied community Mod Loader 1.2.2 and the companion bridge with the game closed:

```powershell
.\tools\Install-GameBridge.ps1 -GameDirectory 'D:\SteamLibrary\steamapps\common\Batomon Showdown' -LoaderDirectory 'C:\path\to\extracted\modloader'
```

Omit `-LoaderDirectory` if the loader is already installed. Start the game and helper normally. The Board tab refreshes automatically every 500 ms from actual team, bench, shop, prices, final stats and run counters. `F8` toggles visibility, `F9` / **Refresh Game State** refreshes manually, and `F11` toggles fullscreen. No focused game window or screenshot corrections are needed. Stale, incompatible and invalid data preserve the last accepted board with an explicit status.

The bridge and installation script ship with the helper; the third-party loader is a separate download. BatoEyes is optional. See [installation, protocol, verified fields and limitations](docs/game-bridge.md). The old screenshot code and fixtures remain for [historical regression coverage](docs/legacy-screenshot-reader.md).

The Board summary and DPS tab reuse the training analyzer for 32-sample passive-target damage projections, checkpoint DPS, status stacks and time to target HP. Restart the game after upgrading the bridge to 1.1 to enable the additional combat metadata. Simulator assumptions and import limits are shown alongside the results.

## Data and limits

The app ships an offline wiki **1.2.0** snapshot for Steam build **25600878 / Balance 24**, with 149 normal creature sprites, 144 separately declared shiny sprites, and 24 trainer portraits. The [wiki](https://batomon.com/) still declares that version when refreshed on 2026-10-07. A separate [1.3.0 reference layer](assets/catalog-patches/1.3.0.json) records all 18 balance changes from the [official patch notes](https://store.steampowered.com/news/app/4557380/view/686392527015643351?l=english). Creature tooltips show optional cost, cooldown, ability values, changes, and provenance; the catalog header shows both versions. Prices, stats, and percentages received from the game remain authoritative.

Five creatures have no shiny asset declared by the wiki; those omissions are reported in the bundle warnings. Runtime game-state reading stays offline. To deliberately refresh the Git-tracked bundle, run `dotnet run --project tools/BatomonHelper.CatalogBundler`. The bundler imports normal reference data, validates override IDs, values, source, and baseline, and retires the layer when all changed wiki values match. A changed baseline or conflicting value stops publication. It extracts actual asset URLs, stages downloads, checks PNG structure and CRCs, validates the staged catalog, and restores replaced assets if publication fails. `--apply-patch` applies the verified layer to the existing baseline without downloading assets. Older snapshots load with empty patch/reference defaults.

The odds engine starts from the game-provided rarity weights and retains the existing region, level-three, and item controls. It does not simulate purchases or infer hidden stat effects.

## Standalone training lab

The LLM advice route and F10 advice shortcut have been removed. A separate WPF training/history application is available:

```powershell
dotnet run --project src/BatomonHelper.Training -c Release
```

The Pantra rules audit has zero missing handlers. The lab now defaults to strategic search and expert iteration, using wiki pairs/compositions, scripted opponents and full-run outcomes. Play Session and history include board DPS, stacking snapshots and time to upcoming-battle HP. Legacy PPO remains available for comparison. Unsourced mechanics remain explicitly labelled guesses; executable coverage does not certify exact game parity or stronger play. See [strategic training and validation gates](docs/strategic-training.md).
