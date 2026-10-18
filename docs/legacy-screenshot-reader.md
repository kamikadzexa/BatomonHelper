# Historical screenshot reader

This describes the previous screenshot workflow, retained only as regression documentation. The current helper uses the [game bridge](game-bridge.md).

Press `F8` to hide/show the overlay and `F9` / **Capture Board** to refresh the shopping screen manually. The **Board** tab shows the trainer, run counters, six team positions, four bench positions, and five shop offers with their visible levels, stat badges, prices, affordability, and shiny classifications. Catalog filters affect browsing and odds calculations; recognition always uses the complete catalog.

## Full-board capture

The reader captures the game client once and recognizes off the UI thread. The helper uses Windows capture exclusion so it stays visible during F9 screenshots. Its window uses an opaque background because WPF per-pixel transparency prevents capture exclusion. If Windows cannot apply exclusion, explicit F9 capture retains the brief hide-and-restore fallback. Normalized fixed regions support 16:9 views at least 640Ã—360 and verified black pillarbox/letterbox bars. Three shop anchors must agree. Foreground sprite templates recognize normal and shiny variants independently; grayscale offers can leave shiny status uncertain. Pixel-font templates separate HUD, level, badge, ribbon, and price styles. A reused Windows OCR engine receives pixel buffers directly, batches compatible crops, and retries only unresolved text.

Confident, validated boards apply immediately as one accepted board plus its `ShopOddsSnapshot` projection. An uncertain required value opens correction with field crops and named candidates. You can add a missed visible badge or mark a badge absent. Invalid corrections and cancellation preserve the previous accepted board. Absent values, unread values, and numeric zero remain distinct; wiki base stats never replace current stats. An absent item-use counter remains absent. The displayed rarity ribbon is authoritative, with no rank-table substitution.

Sprite, badge, and text regions have independent caches, invalidated by catalog or geometry changes. Repeated F9 presses coalesce into one pending refresh. PNG encoding and serialized history writes happen after publication. Version 2 history retains recognition proposals separately from accepted corrections under `%LOCALAPPDATA%\BatomonHelper\capture-history`, with the existing 20-entry retention limit. V1 JSON remains readable and is not migrated. Install the Windows English OCR language feature if it is unavailable.

Eight supplied PNGs and manual annotations are regression fixtures. Their saved JSON is correction evidence: Day 2's saved 100% Common result is wrong; visible odds are 90/10/0/0/0. The original 2434Ã—1369 Smuggler fixtures cover Ninflora's $37 price, Pylong's visible shop level 2, and grayscale variant review; their [annotation provenance](tests/BatomonHelper.Tests/Fixtures/smuggler-evidence.md) explains the native-pixel correction to the earlier Plunderbird reading. The Treasure Hunter fixtures cover level 3/4 colors, multicast, a locked shop, absent badges, and discounted prices. Every resolved Smuggler field is checked, including fractional scales and pixel phases.

The digit reader retains native masks and separate HUD, HP, rank, ribbon, level, badge, and price families. Colored face geometry and border removal preserve disconnected white strokes. Joined badges use variable-width sequence fitting, stroke coverage, seam and scale checks, and competing-string margins. Ambiguous template candidates survive an empty OCR result. `python tools/inspect-board.py build` reproduces the templates from explicit annotations, using supervised sequence alignment and fractional-scale/pixel-phase samples (requires Pillow). Confirmation editors, dropdowns, selection, caret, focus, disabled states, and DataGrid editors share dark styles. Trinkets, backpack contents, recommendations, and automatic polling remain outside this capture step.

Run the isolated Release benchmark with:

```powershell
dotnet test BatomonHelper.sln -c Release --no-restore --filter FullyQualifiedName~Benchmark_cold_changed_and_unchanged_frames
```

It writes `capture-benchmark.json` in the test output directory. See [measured timings and scope](docs/capture-benchmark.md).


