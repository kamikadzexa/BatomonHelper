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

The reader captures the game client once and recognizes off the UI thread. The helper uses Windows capture exclusion so it stays visible during F9 screenshots. Its window uses an opaque background because WPF per-pixel transparency prevents capture exclusion. If Windows cannot apply exclusion, explicit F9 capture retains the brief hide-and-restore fallback. Normalized fixed regions support 16:9 views at least 640×360 and verified black pillarbox/letterbox bars. Three shop anchors must agree. Foreground sprite templates recognize normal and shiny variants independently; grayscale offers can leave shiny status uncertain. Pixel-font templates separate HUD, level, badge, ribbon, and price styles. A reused Windows OCR engine receives pixel buffers directly, batches compatible crops, and retries only unresolved text.

Confident, validated boards apply immediately as one accepted board plus its `ShopOddsSnapshot` projection. An uncertain required value opens correction with field crops and named candidates. You can add a missed visible badge or mark a badge absent. Invalid corrections and cancellation preserve the previous accepted board. Absent values, unread values, and numeric zero remain distinct; wiki base stats never replace current stats. An absent item-use counter remains absent. The displayed rarity ribbon is authoritative, with no rank-table substitution.

Sprite, badge, and text regions have independent caches, invalidated by catalog or geometry changes. Repeated F9 presses coalesce into one pending refresh. PNG encoding and serialized history writes happen after publication. Version 2 history retains recognition proposals separately from accepted corrections under `%LOCALAPPDATA%\BatomonHelper\capture-history`, with the existing 20-entry retention limit. V1 JSON remains readable and is not migrated. Install the Windows English OCR language feature if it is unavailable.

Eight supplied PNGs and manual annotations are regression fixtures. Their saved JSON is correction evidence: Day 2's saved 100% Common result is wrong; visible odds are 90/10/0/0/0. The original 2434×1369 Smuggler fixtures cover Ninflora's $37 price, Pylong's visible shop level 2, and grayscale variant review; their [annotation provenance](tests/BatomonHelper.Tests/Fixtures/smuggler-evidence.md) explains the native-pixel correction to the earlier Plunderbird reading. The Treasure Hunter fixtures cover level 3/4 colors, multicast, a locked shop, absent badges, and discounted prices. Every resolved Smuggler field is checked, including fractional scales and pixel phases.

The digit reader retains native masks and separate HUD, HP, rank, ribbon, level, badge, and price families. Colored face geometry and border removal preserve disconnected white strokes. Joined badges use variable-width sequence fitting, stroke coverage, seam and scale checks, and competing-string margins. Ambiguous template candidates survive an empty OCR result. `python tools/inspect-board.py build` reproduces the templates from explicit annotations, using supervised sequence alignment and fractional-scale/pixel-phase samples (requires Pillow). Confirmation editors, dropdowns, selection, caret, focus, disabled states, and DataGrid editors share dark styles. Trinkets, backpack contents, recommendations, and automatic polling remain outside this capture step.

Run the isolated Release benchmark with:

```powershell
dotnet test BatomonHelper.sln -c Release --no-restore --filter FullyQualifiedName~Benchmark_cold_changed_and_unchanged_frames
```

It writes `capture-benchmark.json` in the test output directory. See [measured timings and scope](docs/capture-benchmark.md).

## Data and limits

The app ships an offline wiki **1.2.0** snapshot for Steam build **25600878 / Balance 24**, with 149 normal creature sprites, 144 separately declared shiny sprites, and 24 trainer portraits. The [wiki](https://batomon.com/) still declares that version when refreshed on 2026-10-07. A separate [1.3.0 reference layer](assets/catalog-patches/1.3.0.json) records all 18 balance changes from the [official patch notes](https://store.steampowered.com/news/app/4557380/view/686392527015643351?l=english). Creature tooltips show optional cost, cooldown, ability values, changes, and provenance; the catalog header shows both versions. Captured prices, stats, and percentages remain authoritative.

Five creatures have no shiny asset declared by the wiki; those omissions are reported in the bundle warnings. Runtime capture stays offline. To deliberately refresh the Git-tracked bundle, run `dotnet run --project tools/BatomonHelper.CatalogBundler`. The bundler imports normal reference data, validates override IDs, values, source, and baseline, and retires the layer when all changed wiki values match. A changed baseline or conflicting value stops publication. It extracts actual asset URLs, stages downloads, checks PNG structure and CRCs, validates the staged catalog, and restores replaced assets if publication fails. `--apply-patch` applies the verified layer to the existing baseline without downloading assets. Older snapshots load with empty patch/reference defaults.

The odds engine starts from the captured ribbon and retains the existing region, level-three, and item controls. It does not simulate purchases or infer hidden stat effects.

## F10 advice

Press F9 to capture and accept the shop, then F10 to ask the local model for one or two sequential moves. The Advice tab shows numbered moves with captured costs and host-generated names and slots, short reasons, **Strategy goal**, and **Likely next**. Expand the details for expected results, source links, and assumptions. F10 uses the latest accepted capture without taking another screenshot or changing game focus. After changing the game yourself, use F9 and F10 again. A new capture cancels pending advice; repeated F10 presses during inference are ignored.

Advice and the pool tabs share one immutable odds result: the selected region, displayed rank/ribbon and item-slot scenario, manual Lv. 3 exclusions plus captured Lv. 3/4 creatures and their base species, and marked owned unique trinkets. Manual rank and item changes are labeled assumptions; captured gold, prices, stats and legality stay authoritative. Region, rank, item-slot, exclusions and ownership controls cancel advice and discard late responses. Search and rarity filters only change displayed rows.

Every request includes the complete exact rules and formulas for all 31 mechanics. The refreshed version 2 knowledge bundle also retains normal/shiny triggers and level effects, 100 pair rows with exact counts and observation windows, and 18 composition cores with hidden build phases, substitutes and supporting effects. Relevant partners and meta records are selected locally, compacted to fit, and available through bounded offline lookups. Meta associations are observational evidence; positioning follows printed abilities. Shock benefits direct attacks of any type, including multicast; target/type restrictions, trigger chains and self-knockout still apply.

Backpack contents, sale values, merge progress and additional run eligibility remain unknown when absent; affected moves are excluded and pool-dependent odds are estimates. A second move requires conservative projection of gold, consumed offers, occupied destinations and prerequisites. Unknown purchase effects, random outcomes, unresolved merges and panel changes end the concrete sequence. Positional moves without a supplied positional interaction are omitted from model choices. Earlier suggestions never count as performed actions, and older journals remain readable. The local model can still make unsupported strategic claims; replay results and remaining reasoning limitations are documented in [advice validation](docs/advice-v2-validation.md).

Install Ollama and pull `qwen3.5:9b-q4_K_M`; localhost port 11434, 16K context and a 90-second timeout are the defaults. Model settings remain editable in the helper. There are no approval controls, game input adapters, background polling or automatic battle/result handling. F8 visibility, F9 capture, F10 advice and F11 fullscreen remain available.

The budget includes system text, schema, complete conversation, lookups, repair text and an answer reserve. Optional indexes and meta records are trimmed before required mechanics, state, actions or pool coverage. When the budget allows, one additional grounding review supplies the selected moves' exact names, positions, costs, variant rules and team-count effects; it is logged when skipped for budget. This improves explanations without guaranteeing tactical correctness. Thinking mode reserves up to 4096 generated tokens for reasoning and the final JSON. If the reply reaches its output limit, the single repair requests an answer with thinking disabled and a 2048-token limit. Citations must refer to evidence actually supplied; lookup IDs are checked against the local bundle. Requests, final replies, finish reasons, token counts and validation failures are saved in `%LOCALAPPDATA%\BatomonHelper\shopping-journal`; private thinking traces are not saved. The UI reports the actual validation error if repair fails.

Refresh strategy data deliberately with `dotnet run --project tools/BatomonHelper.CatalogBundler -- --strategy`. Replay accepted captures and synthetic strategy scenarios against the downloaded local model with:

```powershell
dotnet run --project tools/BatomonHelper.ShoppingBenchmark -- docs/advice-v2-benchmark.json tests/BatomonHelper.Tests/Fixtures/board-day1.png tests/BatomonHelper.Tests/Fixtures/board-day3.png tests/BatomonHelper.Tests/Fixtures/treasure-day9.v2.json tests/BatomonHelper.Tests/Fixtures/smuggler-day10.v2.json
dotnet run --project tools/BatomonHelper.ShoppingBenchmark -- docs/advice-v2-strategy-evaluation.json --strategy-eval --region Jinto
```

The scenario report contains human review criteria, not an assertion that one exact move is optimal. Runtime inference and lookups use local assets only.
