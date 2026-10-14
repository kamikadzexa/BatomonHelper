# Pantra Training Lab

The LLM route and F10 advice wiring have been removed. This separate WPF application learns through simulated normal Pantra runs without gameplay capture. The helper's catalog, capture and odds features remain available.

**Executable coverage is complete for the imported rules:** 1,164 normal/shiny creature level variants, 40 items, 93 trinkets, 23 normally selectable trainers, and imported normal Pantra encounter choices. The build-25738032 / Balance-25 / 1.3.0 audit has zero missing handlers and permits training with 31 explicitly labelled groups of assumptions. `FullFidelityReady` remains false because those assumptions are not independently verified game behavior.

## Start

```powershell
dotnet build BatomonHelper.sln -c Release
dotnet run --project src/BatomonHelper.Training -c Release --no-build
```

Start, Pause, Resume, Stop and Save, Evaluate and Export operate the separate Python learner and headless .NET simulation host. Training runs until stopped or until the optional duration expires; each game run ends on the win target, when all lives are lost, or at the hard final turn, 16. Evaluation and export require a saved, stopped session. Device, seed, output directory and worker/environment limits are editable before starting.

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

New training uses model **5**, observation encoding **5**, numeric transport **2**, and objective **4**. Four independent policies use a shared entity representation and two relational attention layers (four heads, width 128). Typed tokens cover complete visible unit/counter/payload state, compiled ability effects (including nullable filters), inventory, trainers, pending effects, choices, decision budget and recent action kinds. Species IDs have categorical embeddings; other numeric identity fields are normalized. Explicit team/bench/shop roles, rows and columns expose positional relationships. RNG and future opponents remain excluded.

The actor chooses an action kind and then its candidate. PPO uses the joint probability; greedy evaluation selects the kind head directly before its conditional candidate. Shop Move/Lock actions returning to a visited equivalent layout are masked within a context; every other action resets that context. Required choices and overflow remain legal. Native shop actions have a 64-decision safety limit. Legacy models retain their original candidate balancing and turn counters.

The game reward remains 1 for ten wins and 0 for failure. Objective 4 retains +0.1 per battle win, -0.1 per loss, deployment potential `0.04 × deployed − 0.08 × deployable bench`, and -0.10 per unused deployable bench unit at battle start. Generic decision cost is now -0.0002. Potential differences cancel at terminal states; purchases, sales and moves cannot farm shaping reward. GAE uses discount 1, trace continuation 1 within a shop and 0.95 across battle boundaries. Population entropy coefficients are 0.005, 0.01, 0.02 and 0.04. PPO retains learning rate 3e-4, clip 0.2, up to four epochs and target KL 0.02.

Curriculum resets request 50% normal starts, 25% valid rank 5–8 shop states and 25% valid rank 9+ shop states collected from simulated runs. Missing pools fall back to normal starts. All persistent state and RNG are restored exactly; no stats are invented. Training episode successes/wins include these suffix starts and **are not a full-run performance comparison**. The UI labels this distinction and reports start counts. Fixed evaluations always use normal starts unless an explicit scenario bank is selected.

League allocation is 40% current population, 25% recent generations, 20% champions, 10% independent baselines and 5% approved user boards. Missing user boards give their share to baselines; missing current/recent/champion categories fall back to baselines before the complete same-day pool. Indexed day/category buckets and incremental retention replace full-list scans and per-step regrouping. Frozen evaluations do not prune their league. Strongest-shop baselines fill and replace team slots, merge, use items and reroll within bounded budgets.

A fixed probe runs every five updates (32 episodes by default), with a full 128-episode evaluation before improved champions enter the league. Probe RNG changes are restored before learning resumes. The development suite is frozen separately from active training; when comparison archives exist it uses the archived league. `probe_interval: 0` disables probes for isolated throughput measurements. User demonstrations and old policy trajectories are never inserted into PPO updates.

Numeric publication packs variable-length observations and legal candidates contiguously; one large observation no longer pads every environment or consumes the rollout's memory budget. Inference and PPO bucket by token/candidate size without truncation. CUDA events measure actual compute time; synchronization waiting, copies, encoding, legal actions, combat, state copying, opponent lookup, league and history have separate timings. Per-environment CPU timings sum worker time and are not additive to wall-clock rollout time. Combat caches use recursion-context keys and invalidate at tick/effect/mutation boundaries; the reference path remains uncached. History snapshots use explicit owned copies and batched SQLite transactions/retention.

Checkpoints preserve policies, optimizers, RNG, environments, partial histories, turn memory/budgets, curriculum pools, episode starts and league archives. Model-3/encoding-4 checkpoints remain evaluable and exportable through their legacy adapter, but cannot resume new learning. Earlier implementation prototypes are diagnostic artifacts only. Export contains the exact encoding schema and model version.

## Evaluation and generation ranking

Evaluate and Rank Generations share a reusable host and frozen suite. Standard evaluation uses 128 environments and exactly 128 episodes, balanced across four policies; **Thorough** requests 512 episodes. Stable scenario coordinates control trainer, start seed, battle seed and opponent selection independently of batch size and workers. Finished slots stop stepping. Sampling evaluation uses counter-based random draws rather than batch-dependent RNG. Greedy evaluation uses full float32 matrix precision by default (`evaluation_tf32: true` overrides it).

Progress updates every second with the current snapshot, episode quota, elapsed time, throughput and ETA. Completed leaderboard rows appear immediately. Stop cancels evaluation/ranking and preserves completed rows; incomplete rows cannot become champions. Completed evaluations are cached by weights, code/rules/suite identity, sampling, precision and decision limits. Repeated unchanged rankings reuse results. Errors, process exits and host timeouts produce a Failed state. Archives supplied in `comparison_archives` join the same leaderboard and can be exported without modifying archive files.

`--suite final` selects independent opponent/battle seeds. `--scenario-bank <file>` evaluates exact legally reached shop states, with additional wins reported separately from their starting wins. `training/scenarios.py` builds disjoint development/final banks from an archived checkpoint; these are diagnostic starting states, not PPO demonstrations.

## Play your sessions

Use **Play Session** in Training Lab to select a trainer/seed, buy and use shop offers, move/swap, sell, lock, reroll, resolve every required choice and battle. Merging is automatic once the required matching creatures are on the team or bench; it is not a player action. The legal-action panel remains visible beside the board. Sessions autosave after each action; Save, Resume and Inspect battles are available explicitly.

Sessions live in `work/user-sessions`, separately from automatic training-history pruning. **Add to opponent pool**, **Remove from pool** and **Enable / disable** control inclusion. Losing and unfinished sessions can contribute completed battle days. Imports replay every action against the exact rules and reject mismatched/tampered state, preserve original board stats/counters/inventory/trainer, deduplicate exact boards and cap each session at 32 boards per day. The pool is loaded at the next training/evaluation launch. Your actions are recorded for replay and inspection; they are used **only to supply opponents**, with no imitation learning or screen capture.

## Compact history

Recent and curated Best-example views filter generation, policy, trainer, outcome and training/evaluation source. The curated view is explicitly not a performance comparison; complete counters and fixed evaluation provide that. Selecting a run lazily loads its compressed day snapshots.

Brotli state records and a SQLite summary index retain the latest 100 completed runs plus separate best-example quotas for training and evaluation in each retained generation. Evaluation batch and exact snapshot update are stored with every ranked run. Pins survive retention and restart.

## Validation

Measured results are in `training-v5-validation.json`. The paired evaluation-speed check completed the same 16 legacy-model episodes 2.08× faster with 16 active slots than with 4 (a requested ceiling of 128 was capped to the episode count). Isolated indexed lookup was over 200× faster, explicit state copying was 9–33× faster, and direct typed encoding had lower median latency on the representative states. These component gains do not establish an end-to-end training speed gain.

**The learning acceptance gate has not passed.** The one-minute, three-seed full/no-curriculum/legacy comparison did not establish stronger play or greedy forced finishes below 5%. Full relational training averaged about 2,430 decisions/s versus 3,853 for unchanged legacy in that short-budget test. The 30% training-throughput target was not met. Keep comparisons explicit and run longer fixed evaluations before promoting the new learner as a quality improvement.


`training/validate_improvement.py` compares full, no-curriculum and unchanged legacy training under equal wall-clock budgets across seeds 69/70/71, using a frozen suite and paired episode intervals. The gate requires improvement over both archived generation 10 and strongest-shop, plus greedy forced finishes below 5%; short-budget failures are recorded explicitly. A final scenario-bank run is needed to assess late-rank and combo behavior. Training counters or lower leftover gold do not establish this gate.

```powershell
./training/.venv/Scripts/python.exe -m unittest discover -s training -p 'test*.py'
dotnet run --project tools/BatomonHelper.TrainingHost -c Release -- --profile assets/simulation/rules.json <checkpoint-host.json> work/profile.json
./training/.venv/Scripts/python.exe training/validate_improvement.py --archive work/archives/training-v3-2026-10-13 --baseline-root <unchanged-checkout> --minutes 3 --runs 128
```


The existing .NET and Python checks are extended with protocol, quota, cache, loop, hierarchical-policy, ownership, replay and combat-equivalence tests, including deployment shaping, canonical swaps, common evaluation schedules, separated history retention, calibration selection, complete masks and CUDA optimizer execution. A real CUDA smoke run exercises encoding 4, objective 3, calibration, telemetry, generation ranking, history metadata and selected-snapshot export.

Three separate Pantra training seeds and initial/trained evaluation reports are recorded in `training-validation.json`. Synthetic measurements from earlier revisions remain explicitly separated from Pantra results. Success rates describe this simulator and its opponent archives; they are not measured against captured game opponents. Remaining uncertainty is the labelled rules evidence, rather than missing executable definitions.

The original ten generations are preserved in `work/archives/training-v3-2026-10-13` with SHA-256 checksums and rules. New learning uses a separate fresh session; original models remain comparison/export artifacts.

```powershell
dotnet test BatomonHelper.sln -c Release
./training/.venv/Scripts/python.exe -m unittest discover -s training -p test_learner.py
dotnet run --project tools/BatomonHelper.TrainingHost -c Release -- --benchmark
```
