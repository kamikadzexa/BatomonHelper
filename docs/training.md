# Pantra Training Lab

The LLM route and F10 advice wiring have been removed. This separate WPF application learns through simulated normal Pantra runs without gameplay capture. The helper's catalog, capture and odds features remain available.

**Executable coverage is complete for the imported rules:** 1,164 normal/shiny creature level variants, 40 items, 93 trinkets, 23 normally selectable trainers, and imported normal Pantra encounter choices. The build-25738032 / Balance-25 / 1.3.0 audit has zero missing handlers and permits training with 31 explicitly labelled groups of assumptions. `FullFidelityReady` remains false because those assumptions are not independently verified game behavior.

## Start

```powershell
dotnet build BatomonHelper.sln -c Release
dotnet run --project src/BatomonHelper.Training -c Release --no-build
```

Start, Pause, Resume, Stop and Save, Evaluate and Export operate the separate Python learner and headless .NET simulation host. Training runs until stopped or until the optional duration expires. Evaluation and export require a saved, stopped session. Device, seed, output directory and worker/environment limits are editable before starting.

The isolated environment is installed here. For another checkout, run `./training/Setup.ps1 -Python C:/path/to/python.exe`; add `-Cpu` for CPU wheels. Python 3.12, PyTorch 2.7.1 and NumPy 2.2.6 are pinned. Output defaults to `work/training`, which is ignored by Git.

Defaults start from a ceiling of 12 workers and 256 environments. Startup measures worker concurrency and environment throughput, retaining limits as ceilings. CPU and CUDA are timed with numeric transfers, inference, backward passes and Adam updates. Auto selects the faster measurement; explicit CPU/CUDA overrides are available. Resumes retain their saved device and environment count. UI progress reports completed runs, throughput, success curves, trainer results, generation results and checkpoint status.

## Simulator and rules

The reusable API exposes `Reset`, `GetObservation`, `GetLegalActions`, `Step` and `ResolveBattle`. Persistent runs, temporary combat state, ordered inventory identities, queued purchases/rewards, creature counters, devoured payloads and frozen opponent boards remain distinct and serializable. Random state and upcoming opponents are excluded from observations.

Handlers include buying, item use, selling, overflow, merging and automatic promotion of stacked copies, gifts and consumed modifiers, locks/rerolls, trainer acquisition and timed grants, foreign recruitment, evolution/hatching, encounters and Second Chance. Combat includes positions, cooldowns, charge, multicast, reactive chains, disabling, ongoing effects, statuses, protection, knockout/revival, transformations, victory effects, caps and sudden death.

Your latest corrections take precedence: HP begins at 300; missing/later HP values are extrapolated with visible provenance. Base gold is now **30 + 5 × (day − 1)**, superseding earlier income observations. Scheduled encounters occur on days 3/6/9 at tiers 1/2/3. Percentage bonuses add: two +20% auras give +40%. Same-tick damage uses existing Shock before adding new Shock; defeat is checked after shared resolution phases and simultaneous defeat is a player win. Pipskull uses two knockouts, overriding its retained imported tooltip.

Normal encounters and nested choices resolve before the new shop becomes visible. Trainers and consumables have explicit identity-specific handlers with anchored source descriptions. Inventory and trainer timing, Painter/Swim Coach distributions, growing Gachapod rewards, unspecified revival/cooldown details, the HP curve and other assumptions remain inspectable under **Rule provenance and labelled guesses**.

`assets/simulation/rules.json` preserves compiled parameters, original descriptions and provenance. `user-profile.json` holds observations and editable settings. Unknown descriptions still fail compilation instead of becoming no-ops. The helper's older recognition catalog remains separate from this simulation catalog.

Refresh or audit data with:

```powershell
dotnet run --project tools/BatomonHelper.TrainingHost -c Release -- --refresh assets/simulation
dotnet run --project tools/BatomonHelper.TrainingHost -c Release -- --review-sources assets/simulation
```

Source decisions retain current Coupon, Membership Card and resale values over older guide assertions. The [triggering](https://batomon.com/mechanics/triggering), [multicast](https://batomon.com/mechanics/multicast) and [stat-cap](https://batomon.com/mechanics/stat-caps) references describe the checked scheduler/cap examples. User-confirmed corrections override conflicting imported prose.

## Learner, opponents and persistence

Four independently initialized masked-PPO actor–critic policies use a shared entity encoder, two 256-unit hidden layers, complete legal candidate scoring and a value head. Observation encoding 3 adds ragged numeric records for ordered bags/trainers, every visible counter, stored payloads, full choice/effect parameters, overflow, foreign pools and visible pending effects. Batches pad complete records and candidates without truncating them. The local versioned control channel transfers numeric batches through memory-mapped buffers.

Rewards are 1 for ten wins and 0 for run failure. No repetitive-action or invented-strength rewards are used. Defaults are learning rate 3e-4, clip 0.2, discount 1, GAE 0.95, four optimization epochs and entropy 0.01.

The league requests 40% current population, 30% recent generations, 20% champions and 10% independent random/strength/merge/economy baselines. Missing categories fall back to available same-day frozen archives. A legally simulated baseline supplies late normal-run days; evaluation rejects absent same-day opponents instead of substituting the evaluating run's live board. Archives are bounded by generation/policy/day, trainer and observed win-rate strata. Strength strata affect retention only. Generation weights retain the latest 20 saves; evaluated policies can promote archived boards to the bounded champion pool on resume.

Checkpoints occur every five minutes and on pause/stop. They preserve four policies/optimizers, Python/NumPy/Torch/CUDA random states, active environments, partial histories, league archives, configuration and rules identity. Rules, simulator revision, encoding, seed and environment compatibility are checked before resuming. Export writes policy weights and observation/action metadata for later helper integration.

## Compact history

Recent and Best views filter generation, policy, trainer, outcome and training/evaluation source. Selecting a run lazily loads its compressed day snapshots. Previous/Next steps through pre-battle boards; the opponent toggle displays the frozen opponent. Cards reuse existing sprites and show all six team/four bench slots, levels, shiny status, permanent stats, types, base cooldowns and progress counters. Resources, inventory quantities, choices, battle outcome and resulting wins/lives accompany each day.

Brotli state records and a SQLite summary index retain the latest 100 completed runs plus the best 20 per retained generation, ranked by wins, lives, days and run ID. Pins survive retention and restart. Bounded asynchronous writes freeze records so later simulation does not alter them. A saved matching rules manifest prevents newer catalog data from changing historical cards.

## Validation

The full .NET suite passes 420 tests, covering helper regressions, all imported creature variants, complete runs for every trainer and trinket, item handlers, legality, deterministic restore/replay, immutable history, retention/pins and interaction ordering. Six Python checks include all-candidate masks, ragged state padding, terminal returns, uncertainty, device resume and a CUDA optimizer update. Optimized/reference comparisons use identical seeds; the reference evaluator shares effect handlers and independently advances the tick schedule, so those checks do not independently prove prose interpretation.

Three separate Pantra training seeds and initial/trained evaluation reports are recorded in `training-validation.json`. Synthetic measurements from earlier revisions remain explicitly separated from Pantra results. Success rates describe this simulator and its opponent archives; they are not measured against captured game opponents. Remaining uncertainty is the labelled rules evidence, rather than missing executable definitions.

```powershell
dotnet test BatomonHelper.sln -c Release
./training/.venv/Scripts/python.exe -m unittest discover -s training -p test_learner.py
dotnet run --project tools/BatomonHelper.TrainingHost -c Release -- --benchmark
```
