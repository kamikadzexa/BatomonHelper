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

New sessions default to ceilings of 16 workers, 1,024 environments and PPO minibatches of 2,048. A disposable startup calibration measures worker/environment scaling, CPU versus CUDA and safe minibatch throughput without advancing training RNG or history, then records the selected settings in `calibration.json`. Resumes retain their saved settings. UI progress emphasizes the rolling 2,000-run window and reports complete generation counters, action kinds, deployment-improving/reversing moves, finishes with usable bench creatures, opponent mix, host stages, PPO time and CUDA peak memory.

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

Four independently initialized masked-PPO actor–critic policies use a shared entity encoder, two 256-unit hidden layers, complete legal candidate scoring and a value head. Model version 3 supplies each candidate's source/destination entity embeddings and explicit deployment deltas to a residual scorer. Symmetric occupied-slot swaps have one canonical candidate. Logits subtract the log of the legal candidate count for that action kind, so move multiplicity does not crowd out Finish. Observation encoding 4 retains complete ragged visible state and adds deployed delta, deployable-bench delta and finish-with-gap action fields. Packed numeric publications are copied once and vectorized into complete padded batches without truncation.

The simulator's game reward remains 1 for ten wins and 0 for run failure. Training objective version 3 adds +0.1 per battle win, -0.1 per loss and -0.002 per decision. Its deployment potential is `0.04 × deployed − 0.08 × deployable bench`; battle start also applies -0.10 per owned bench creature that could fill an empty team slot. Finish remains legal. Terminal potential is zero, so reversible purchases, sales and moves cannot farm reward. Shop turns publish only Finish after 64 decisions; overflow and required choices remain resolvable. PPO defaults remain learning rate 3e-4, clip 0.2, discount 1, GAE 0.99, up to four epochs, entropy 0.002 and target KL 0.02.

CUDA uses TF32 matrix multiplication and fused Adam. Population inference stacks the four independent policies with `torch.func`/`vmap`, transferring the batch and returning sampled actions/log probabilities/values together. The stack is refreshed each rollout after optimization; policy assignment stays environment index modulo four. Each policy's complete padded rollout is uploaded once and indexed on-device across optimization epochs. Loss/entropy/KL metrics are collected together instead of copying every minibatch metric separately. The host encodes environments in parallel, writes contiguous numeric arrays and reuses the published legal candidate set. `tf32: false` selects full float32 matrix precision. PPO diagnostics and stage timings are retained in `metrics.jsonl`; GPU utilization alone does not measure completed-game throughput.

The league requests 40% current population, 30% recent generations, 20% champions and 10% independent random/strength/merge/economy baselines. Missing categories fall back to available same-day frozen archives. **Rank generations** evaluates the initial policy, every retained generation and the current checkpoint on one frozen league with common episode/day opponent seeds. It ranks Wilson lower-bound success, then average wins and unused deployable bench. The selected ranked snapshot can be inspected or exported; its strongest policy supplies champion boards on resume without rolling back current learners.

Checkpoints occur before the first rollout, every five minutes and on pause/stop. They preserve four policies/optimizers, random states, active environments, partial histories, league archives, configuration and rules identity. Encoding-4/objective-3 training starts fresh; incompatible earlier checkpoints are rejected. Export writes policy weights and observation/action metadata. Evaluation uses greedy actions by default; `evaluation_sampling: true` measures the exploratory policy instead.

## Compact history

Recent and curated Best-example views filter generation, policy, trainer, outcome and training/evaluation source. The curated view is explicitly not a performance comparison; complete counters and fixed evaluation provide that. Selecting a run lazily loads its compressed day snapshots.

Brotli state records and a SQLite summary index retain the latest 100 completed runs plus separate best-example quotas for training and evaluation in each retained generation. Evaluation batch and exact snapshot update are stored with every ranked run. Pins survive retention and restart.

## Validation

The .NET checks cover 429 tests and the Python suite covers 13 checks, including deployment shaping, canonical swaps, common evaluation schedules, separated history retention, calibration selection, complete masks and CUDA optimizer execution. A real CUDA smoke run exercises encoding 4, objective 3, calibration, telemetry, generation ranking, history metadata and selected-snapshot export.

Three separate Pantra training seeds and initial/trained evaluation reports are recorded in `training-validation.json`. Synthetic measurements from earlier revisions remain explicitly separated from Pantra results. Success rates describe this simulator and its opponent archives; they are not measured against captured game opponents. Remaining uncertainty is the labelled rules evidence, rather than missing executable definitions.

Earlier generated checkpoints are intentionally unsupported after the encoding/objective change. Start a fresh calibrated session; the recorded legacy metrics remain useful only as an A/B baseline.

```powershell
dotnet test BatomonHelper.sln -c Release
./training/.venv/Scripts/python.exe -m unittest discover -s training -p test_learner.py
dotnet run --project tools/BatomonHelper.TrainingHost -c Release -- --benchmark
```
