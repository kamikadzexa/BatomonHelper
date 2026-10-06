# BatomonHelper

Personal, external Windows overlay for Batomon Showdown. It is a .NET 10 WPF solution for Visual Studio and never reads process memory, writes to Steam files, or sends game input.

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

Press `F8` to hide/show the overlay and `F9` / **Capture Board** to refresh the shopping screen manually. The **Board** tab shows the trainer, run counters, six team positions, four bench positions, and five shop offers with their visible levels, stat badges, prices, affordability, and shiny classifications. Catalog filters affect browsing and odds calculations; recognition always uses the complete catalog.

## Full-board capture

The reader captures the game client once, restores the overlay immediately after acquisition, and recognizes off the UI thread. Normalized fixed regions support 16:9 views at least 640×360 and verified black pillarbox/letterbox bars. Three shop anchors must agree. Foreground sprite templates recognize normal and shiny variants independently; grayscale offers can leave shiny status uncertain. Pixel-font templates separate HUD, level, badge, ribbon, and price styles. A reused Windows OCR engine receives pixel buffers directly, batches compatible crops, and retries only unresolved text.

Confident, validated boards apply immediately as one accepted board plus its `ShopOddsSnapshot` projection. An uncertain required value opens correction with field crops and named candidates. You can add a missed visible badge or mark a badge absent. Invalid corrections and cancellation preserve the previous accepted board. Absent values, unread values, and numeric zero remain distinct; wiki base stats never replace current stats. An absent item-use counter remains absent. The displayed rarity ribbon is authoritative, with no rank-table substitution.

Sprite, badge, and text regions have independent caches, invalidated by catalog or geometry changes. Repeated F9 presses coalesce into one pending refresh. PNG encoding and serialized history writes happen after publication. Version 2 history retains recognition proposals separately from accepted corrections under `%LOCALAPPDATA%\BatomonHelper\capture-history`, with the existing 20-entry retention limit. V1 JSON remains readable and is not migrated. Install the Windows English OCR language feature if it is unavailable.

Six supplied PNGs and manual annotations are regression fixtures. Their saved JSON is correction evidence: Day 2's saved 100% Common result is wrong; visible odds are 90/10/0/0/0. The Treasure Hunter fixtures cover day 10, lives 7, level 3/4 colors, joined numeric glyphs, multicast, a locked shop, absent badges, and discounted prices. Separate HP and rank fonts plus glyph aspect and enclosed-space checks prevent the observed 6/8 and level/rank mistakes. `python tools/inspect-board.py build` reproduces the templates from explicit annotations and checks for conflicting glyph labels (requires Pillow). Trinkets, backpack contents, tooltips, recommendations, and automatic polling are outside this capture step.

Run the isolated Release benchmark with:

```powershell
dotnet test BatomonHelper.sln -c Release --no-restore --filter FullyQualifiedName~Benchmark_cold_changed_and_unchanged_frames
```

It writes `capture-benchmark.json` in the test output directory. See [measured timings and scope](docs/capture-benchmark.md).

## Data and limits

The app ships an offline wiki snapshot for Steam build **25600878 / Balance 24**, with 149 normal creature sprites, 144 separately declared shiny sprites, and 24 trainer portraits. Five creatures have no shiny asset declared by the wiki; those omissions are reported in the bundle warnings. Runtime capture stays offline. To deliberately refresh the Git-tracked bundle, run `dotnet run --project tools/BatomonHelper.CatalogBundler`. The bundler extracts actual asset URLs, stages downloads, checks PNG structure and CRCs, validates the staged catalog, and restores replaced assets if publication fails.

The odds engine starts from the captured ribbon and retains the existing region, level-three, and item controls. It does not simulate purchases or infer hidden stat effects.

No recommendation engine is enabled yet; the `IRecommendationEngine` interface preserves an explainable, local-only next step.
