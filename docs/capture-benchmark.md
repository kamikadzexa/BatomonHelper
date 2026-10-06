# Full-board capture verification

Measured locally on 2026-10-06 with the Release build and the refreshed offline catalog (149 creatures, 144 declared shiny templates, 24 trainer portraits). Raw samples are saved in [capture-benchmark.json](capture-benchmark.json).

| Measurement | Time | Scope |
| --- | ---: | --- |
| Cold reader | 877.5 ms | Reader construction plus first recognition |
| Cold recognition | 842.7 ms | Includes template loading and OCR |
| Cold OCR | 216.5 ms | Two calls; included in cold recognition |
| Warm changed board, p95 | **283.8 ms** | 20 alternating Day 1 / Day 3 frames; target ≤500 ms |
| Unchanged board, p95 | **47.7 ms** | 20 immediate repeated frames; target ≤150 ms |
| Crowded changed board, p95 | **371.4 ms** | 20 alternating Treasure Hunter Day 9 / Day 10 frames |
| Crowded unchanged board, p95 | **60.6 ms** | 20 immediate repeated crowded frames |
| Warm changed-board OCR, p95 | 48.6 ms | Included in changed-board time |
| Numeric-fallback fixture | 173.7 ms | Gold rendered in an unsupported italic font |
| Numeric-fallback OCR | 36.9 ms | Two calls; included in fallback recognition |

Changed frames alter counters, rarity percentages, creatures, badges, shop offers, and reroll count. The crowded pair additionally exercises a locked shop, level 4, shiny creatures, multicast, absent badges, and discounted prices. Every benchmark capture of these four original screenshots is fully resolved and validated. Unchanged captures make zero OCR calls. A synthetic italic gold value deliberately exercises numeric OCR fallback: its proposal requires correction and is never silently accepted. Its timing is one observation, not a p95 estimate.

The measured interval starts with an already loaded screenshot. It includes content-bound detection, foreground sprite matching against the complete catalog, field recognition, cache processing, and OCR. Catalog database loading, PNG decoding, desktop acquisition, overlay hide/show, UI publication, PNG history encoding, disk writes, and user review are excluded. `CaptureAsync` reports live acquisition separately in `BoardCaptureTimings.AcquisitionMilliseconds`; live F9 end-to-end latency has not been measured. These results establish the recognition targets on these fixtures, not an end-to-end capture guarantee or a catalog-wide accuracy claim. Cold-start here means a new reader in a fresh test process, not an OS restart.

The full Release suite passes **77 tests**. All three Treasure Hunter feedback screenshots now parse completely without corrections. The annotations correct errors preserved in earlier accepted records: bench Formiqueen damage is 60, Shogapede poison is 6, Day 9 Stalagrove shield is 60, Day 10 Formiqueen damage is 156, and Purple Gift costs 40. The source JSON files remain unchanged as evidence.

Regression coverage includes the six supplied board annotations, corrected Day 2 odds (90/10/0/0/0), duplicate creatures with different current stats, level progression and level colors, shiny and grayscale ambiguity, evolved creatures, item offers, price changes, scaled views, black bars, occlusion, unsupported screens, corrections, atomic publication, independent cache invalidation, F9 coalescing, cancellation, staged catalog validation, and V1/V2 history retention. UI render tests verify all 15 positions and correction controls. Font templates retain character aspect and enclosed-space counts, and separate HUD, HP, rank, ribbon, level, badge, and price families. Touching badge digits are split at their pixel-font character width. Unresolved numeric OCR always requires review.

Reproduce validation and then run the benchmark separately so concurrent tests do not distort the samples:

```powershell
dotnet test BatomonHelper.sln -c Release --no-restore --verbosity quiet
dotnet test BatomonHelper.sln -c Release --no-build --no-restore --filter FullyQualifiedName~Benchmark_cold_changed_and_unchanged_frames --verbosity quiet
```

The benchmark writes `tests/BatomonHelper.Tests/bin/Release/net10.0-windows10.0.22621.0/win-x64/capture-benchmark.json`. p95 is the nearest-rank 95th percentile of 20 samples. Thresholds are documented rather than asserted in tests because scheduling and machine load change timing; ambiguous recognition still fails automatic acceptance regardless of timing.
