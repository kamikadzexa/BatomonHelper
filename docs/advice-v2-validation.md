# Advice v2 validation

Validated on 2026-10-07. The implementation supplies complete rules, structured wiki evidence and the displayed pool context, validates concrete action sequences, and renders concise advice. These checks establish data and action correctness; they do not establish reliable tactical reasoning by the local model.

## Automated checks

`dotnet test BatomonHelper.sln -c Release --no-restore` passed **126 tests**, with zero failures or skipped tests. The final run took 34 seconds and built the application and test projects without compiler warnings or errors. `git diff --check` reported no whitespace errors.

Coverage includes split Next.js payload reconstruction, escaped and plain JSON, hidden composition phases and substitutes, aliases, complete mechanics/formula extraction, preserved pair counts and observation windows, shared tab/planner odds across regions and manual controls, captured maxed species/base exclusions, known unique ownership, cancellation and late replies, supplied citations, and old shopping journal compatibility. Sequence tests cover affordability, consumed offers, occupied destinations, unresolved purchase effects, merges, rerolls, and buying into a slot freed by the first move. Positioning tests check printed directional recipients rather than composition layouts.

## Bundled evidence

The refreshed bundle was retrieved at **2026-10-07 07:23 UTC**. It contains 31 mechanics, 100 complete pair records, 18 compositions and 53 supplementary references. Pair records retain sample numerators and denominators and this observation window: **2026-09-08 through 2026-10-07, ranked, Balance 24, all ranks and rounds**. Composition records retain their own windows, sample information and hidden build details.

The catalog source still declares wiki **1.2.0**, Steam build **25600878**, Balance **24**. The separate 1.3.0 reference layer remains an unverified patch limitation where fields have not been reconciled with the wiki. Captured prices and badges take precedence. Five shiny assets are absent from the wiki. Runtime inference and evidence lookup remain offline apart from the localhost Ollama connection.

## Capture replay

Settings were unchanged: `qwen3.5:9b-q4_K_M`, 16,384 context tokens, thinking disabled, 90-second timeout, localhost Ollama. Hardware was an NVIDIA GeForce RTX 3080 Ti with 12 GB VRAM. These are a single local run with model/cache state affecting latency, not a controlled speed comparison. No game was running and no game input was issued.

Prompt and generated token columns list each inference in order, including the grounding review when performed. Latency is total planner inference time, excluding initial screen recognition.

| Capture | Selected move | Prompt tokens | Generated tokens | Seconds |
| --- | --- | --- | --- | ---: |
| Day 1 | Buy Magmite into Team 1 | 11,453 / 11,916 | 142 / 140 | 9.13 |
| Day 3 | Move Joltail to Team 1 | 12,706 / 13,167 | 151 / 149 | 6.50 |
| Treasure Hunter, Day 9 | Finish shopping | 14,157 | 145 | 5.11 |
| Smuggler, Day 10 | Move Humbolt to Bench 1 | 13,450 | 173 | 5.12 |

All four replies passed the host action/output checks. Grounding review ran for two captures and was skipped for the two larger conversations to preserve the complete context and answer reserve. Every response finished normally; there were no timeouts, validation repairs or validation failures. Generated tokens totaled 900.

The explanations still contain errors. Day 3 describes Battle Start as triggering upon shop entry and proposes buying during battle. The Treasure Hunter next step confuses shopping and battle order. The Smuggler explanation claims that benching an active creature keeps full strength, although it removes an active caster, and leaves the replacement as a conditional next step. Day 1 selects an affordable deployment but its follow-up has a weak stopping/locking rule. These are reasoning failures despite legal selected actions.

Raw results: [capture measurements](advice-v2-benchmark.json) and [capture journal](advice-v2-benchmark.json.jsonl).

## Strategy scenarios

Nine synthetic accepted states were evaluated with the **Jinto** pool. Their reference badges come from the bundled catalog; they are not additional recognition fixtures or measured battle outcomes. The rubric checks supported reasoning without requiring one exact strategic choice. All nine replies passed host validation, all received a grounding review, and all finished normally without timeout or repair. Generated tokens totaled 3,160. None selected a concrete second move; automated model-response tests independently exercise valid two-move output, including a newly freed purchase destination.

| Scenario | Prompt tokens | Generated tokens | Seconds | Review of the final explanation |
| --- | --- | --- | ---: | --- |
| Shock with non-Electric attacks | 11,369 / 11,893 | 200 / 186 | 7.29 | Partial: selects Velocect and describes repeated casts benefiting Shock, but does not clearly explain typing independence and incorrectly calls this the last empty slot. |
| Multicast and self-knockout | 11,136 / 11,673 | 208 / 199 | 7.04 | Not demonstrated: avoids the random-target item, but omits queued-repeat/self-knockout reasoning and overstates the Musician bonus after adding another Electric creature. |
| Trigger chains and positioning | 12,497 / 13,023 | 166 / 177 | 6.78 | Failed: claims all six slots are filled, misses the Bug-above interaction, and confuses an active relocation with bench inactivity. |
| Adjacency and charge | 12,459 / 12,881 | 214 / 212 | 7.57 | Failed: rerolls rather than explaining the available recipient behind Dracana; suggests locking for a hypothetical future offer. |
| Shiny differences | 10,838 / 11,312 | 150 / 156 | 6.01 | Partial: correctly uses the shiny offer's Multicast 4. The explanation remains imprecise about how the trainer bonus reaches a non-Electric attacker. |
| Unavailable build partner | 12,626 / 12,983 | 153 / 151 | 6.46 | Not demonstrated: invents no positive species probability, but never explicitly evaluates the missing partner against the regional pool. |
| One life remaining | 11,561 / 12,042 | 157 / 155 | 6.27 | Partial: buys affordable immediate strength; claims Shock synergy without an active Shock source and suggests bench storage while active slots remain empty. |
| Maxed species | 11,327 / 11,854 | 201 / 179 | 6.99 | Partial: does not chase maxed Velocect. The claim that Gildshell is the only affordable creature conflicts with the other visible affordable offer. |
| Burn scaling | 12,059 / 12,574 | 150 / 146 | 6.27 | Failed: an active relocation does not activate On Cast or fill another slot, and the claimed shielding recipient is unsupported. |

These scenarios are evaluation records, not assertions of tactical success. Supplying the rules and reviewing a reply improve the available grounding, but the current model still loses board facts and invents interactions. Conservative sequence validation protects affordability and known transitions; it cannot prove that a move is strong or that every natural-language claim is true. The narrow explicit Shock/Triggering contradiction checks also do not constitute a complete semantic verifier.

Raw results and human review criteria: [strategy measurements](advice-v2-strategy-evaluation.json) and [strategy journal](advice-v2-strategy-evaluation.json.jsonl). The README contains commands to reproduce both replays.

## Visual verification

The Advice tab was rendered and inspected at **980×720**, **1920×1080**, and **1300×1000** with expanded details. Numbered host-generated moves, reasons, Strategy goal, conditional Likely next, links and assumptions remained readable. The smaller view scrolls, and no text overlap was observed. The previews use a supplied two-move decision to exercise layout, rather than asserting that the model produced that example.

The WPF test writes `advice-ui-normal.png`, `advice-ui-fullscreen.png` and `advice-ui-details.png` into the Release test output directory. A preview can display existing saved user settings; the replays above used the unchanged 16K default.
