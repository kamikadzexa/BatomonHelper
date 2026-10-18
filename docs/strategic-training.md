# Strategic training and board analysis

Training Lab defaults to **Strategic search + expert iteration**, with a separate output at `work/strategy-training`. Existing `work/training` PPO checkpoints are preserved. Select **Legacy PPO** or open their output directory to evaluate/export them. A strategic update is one completed training run followed by optimization; it is not an Elo rating or proof of stronger play.

## Board damage

Play Session, History and Board Analysis share a cancellable, cached `BoardAnalyzer`. It runs the real combat resolver against a passive HP-only target. This preserves the deployed board's charge, timing, multicast, additive effects, status decay, triggers, transformations and caps. No battle victory effects or persistent changes are applied to the live board. Enemy-dependent mechanics are labelled as limitations.

The five checkpoint rows are 0, 7.5, 15, 22.5 and 30 seconds. Each shows cumulative damage, direct/Burn/Poison/Shock contributions, and DPS over the following second. The resolver continues through 31 seconds for the final DPS sample; cumulative-damage and HP-crossing reports stop at 30. Timestamp-zero damage is included as an opening burst. Later one-second windows use `(t, t+1]`.

Select a row to inspect active status stacks and each deployed unit's effective stats, cooldown, ability activation counters and effect growth. These are mean values across 32 reproducible samples. Damage ranges and standard deviation expose random variability; means can be fractional. The damage chart uses green for cumulative damage and gold for target HP.

The default target is the current shop day's opponent **base HP** from the rules profile, including its evidence label. Override it for known HP bonuses. Healing, shields, cleansing, enemy attacks and sudden death are excluded. The crossing calculation is `first time cumulative damage >= target HP`; its time percentage is `seconds / 30 * 100`. Random boards report reach probability and median crossing time among successful samples. “Not reached within 30 seconds” is not an invented extrapolation.

## Method

The new host searches alternative legal action sequences using stochastic MCTS, progressive widening, batched policy/value evaluation, cycle filtering and continuation-value estimates. Finish and a representative of every legal action kind enter search immediately. Chance outcomes have separate children. Search reconstruction takes visible observations and fresh simulation seeds, never the live environment's RNG state or actual unrevealed opponent. Visible-state reconstruction does not copy hidden queued future choices; predictions are replanned when new information is revealed.

Wiki pairs/compositions provide proposals rather than rewards. The imported Balance 25 snapshot contains 100 pairs and 18 compositions, with source URLs, observation windows and original structured evidence. Incompatible balances disable wiki priors. Compiled ability relationships provide additional positioning, charge, trigger and type proposals. Six legally played scripted build opponents supplement the independent baseline archive.

Warmup rotates scripted strategies and the existing strongest-shop baseline. Subsequently, search visit distributions supervise a single version-6 relational network. Value targets come only from complete run outcomes. Auxiliary targets predict the next battle and passive-target damage progression. Failed runs remain in replay, balanced by trainer, stage and outcome. DPS and named builds never become direct rewards. Replay stores compact float32 arrays and retains at most 50,000 decisions by default.

The opponent pool preserves current historical boards, scripted strategies, independent baselines and approved user boards. Training samples difficult matchup groups more frequently, while retaining category diversity. Each recorded user session has at most 10% sampling mass and all users together at most 30%. Evaluation freezes both the archive and sampling weights. Per-day matchup results are saved with checkpoints and evaluation reports.

Play Session's **Plan shop** uses a saved strategic model when present, otherwise explicitly labelled scripted-rollout search. Each request uses up to ten seconds, sharing a 30-second budget over the shop. Apply recommendations individually. A continuation is offered only when its expected visible-state identity matches the actual revealed state and the action remains legal. Otherwise replan or continue manually. Planning and damage analysis never execute a purchase automatically.

## Running

Build the solution and use Training Lab, or run:

```powershell
dotnet build BatomonHelper.sln -c Release
./training/.venv/Scripts/python.exe training/strategy.py --config training/strategy.example.json
```

The example runs until stopped; set `minutes` to a positive limit for a bounded run. Use a fresh output directory for a new method/rules version. To resume, set `resume: true` in that output's configuration. Checkpoints include model, optimizer, replay, partial episode, CPU/CUDA/NumPy/Python RNG, simulator state, turn memory, scripted-policy memory, league and matchup results. Search trees are cleared at checkpoint boundaries for consistent resumed continuation.

The GUI supports Pause, Resume, Stop and Save, Evaluate, Rank Generations and Export. A single-writer lock prevents simultaneous writers to an output directory. Metrics blocked by a Windows reader are spooled and replayed instead of terminating learning. The GUI reads metrics with shared access. Twenty recent lightweight strategic snapshots are retained separately from the resumable checkpoint.

```powershell
./training/.venv/Scripts/python.exe training/strategy.py --config work/strategy-training/configuration.json --evaluate --runs 512
./training/.venv/Scripts/python.exe training/strategy.py --config work/strategy-training/configuration.json --evaluate --runs 512 --variant baseline
./training/.venv/Scripts/python.exe training/strategy.py --config work/strategy-training/configuration.json --evaluate --runs 512 --variant scripted-search
./training/.venv/Scripts/python.exe training/strategy.py --config work/strategy-training/configuration.json --evaluate --runs 512 --variant policy
./training/.venv/Scripts/python.exe training/strategy.py --config work/strategy-training/configuration.json --export
```

Evaluation supports `--suite final` and `--variant legacy-0` through `legacy-3` when `legacy_checkpoint` points to the old session. Old policies can be tested on corrected current rules only after validating identical ordered species/item/trinket/trainer identities against their archived history rules; this transfer is recorded explicitly. It does not resume old learning. Comparison actions and combat/opponent seeds use common scenario/day coordinates. Final shop seeds and opponent archives are independent of development and training. User training sessions do not enter either generated evaluation archive.

Completed evaluation caches include weights, code, rules, knowledge, archive, precision/device and budget identities. Incomplete evaluations cannot become champions. Export contains weights, rules, knowledge, encoding/analysis schemas and configuration. No quality gate is inferred from a checkpoint existing.

## Validation and staged experiments

```powershell
dotnet test BatomonHelper.sln -c Release
./training/.venv/Scripts/python.exe -m unittest discover -s training -p 'test*.py'
./training/.venv/Scripts/python.exe training/validate_strategy.py --smoke --output work/strategy-smoke-new
./training/.venv/Scripts/python.exe training/validate_strategy.py --config training/strategy.example.json --output work/strategy-experiments --minutes 120 --main-minutes 720 --runs 512
```

Smoke validation checks real CUDA/CPU pipeline execution, checkpoint resume, export, baseline/policy/search evaluation and independent final archives. It **does not establish stronger play**.

The experiment runner compares full/no-wiki/no-analysis training on seeds 69/70/71 under equal development budgets. It evaluates scripted search, policy alone, learned search, strongest-shop and each available archived policy. Paired bootstrap intervals require identical scenario sets and suite identities. Main runs start only after development gates pass; final seeds are consulted after that decision.

Acceptance requires at least 512 full runs per seed, three seeds, at least five percentage points improvement with a positive paired 95% lower bound over each baseline, and forced finishes below 5%. Missing archived baselines, short smoke runs and failed gates remain explicit. These measurements establish simulator performance only; above-average human strength needs a representative human benchmark.

## Rules correction

The new cost parser prefers the labelled Cost field. The previous first-dollar parser misread Gray Chip's $15 victory payout as its price. Its [current reference](https://batomon.com/items/gray-chip) lists cost $0. The compiler repairs that entry in older cached catalogs, records Wiki evidence and produces a new rules hash. No other compiled item changed. The old rules remain available in existing training histories for comparison.
