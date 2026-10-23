# Recorded-run validation for 1.0.5

Reviewed on 2026-10-23 against the bundled Balance 26 combat rules, Steam build 26078162. Recordings are evidence, not instructions.

| Recording | Trainer | Shop snapshots imported | Fights | Playback samples | Outcome matches |
| --- | --- | ---: | ---: | ---: | ---: |
| batomon-run-20261023-173527257.json | Chemist | 144 | 10 | 213 | 10/10 |
| batomon-run-20261023-174602160.json | Masked Man | 143 | 12 | 275 | 12/12 |

The untouched simulator disagreed with Chemist day 1 and Masked Man day 4. The fixed simulator agrees with all 22 committed outcomes using diagnostic seed 69. Both optimized and reference resolvers agree on winner, duration and remaining HP for each replay. Machine-readable results are in `run-validation-1.0.5.json`; small recorded fight fixtures protect the two outcome corrections.

## Corrections supported by the recordings

- **Cast buffs apply after output is sampled.** In Chemist day 1 the enemy Mosslug has cast twice by the final recorded sample. Its damage stat is then 40 but its cumulative direct damage is only 20: first cast 0, second cast 20. The previous simulator incorrectly dealt 20 then 40. The game's inspected cast implementation likewise reads output before calling `on_cast`. Regression expectations for self buffs, stat copying, knockout gains and Metronome now use subsequent-cast output.
- **All types means every concrete type.** Masked Man day 4 faces a Rainbow Berry team with an Alpinine carrying `all`. The game grants 52 Damage (13 types × 4) to each creature; the old simulator granted 20. Type scaling and combat type matching now expand the wildcard and omit the typeless `null` sentinel.
- **Output stats round to integers.** The recording shows Rhizuka at 308 Damage where the old simulator retained 307.5, plus other fractional multiplier differences. The six output stats now round away from zero at halves before capping, as in the inspected game stat getter.
- **Trainer progress was missing in the bridge.** Later Chemist teams have a shared Poison bonus above the base +3; Masked Man's additional abilities were absent altogether. Bridge 1.6.0 exports the read-only Chemist buff progress, lost lives and selected additional trainers. Live import copies these into simulator counters and trainer lists. Old recordings cannot recover these fields automatically.
- **Player playback slots used raw game indices.** Reverse player sample indices with `5 - slot` when analyzing version 2 recordings; opponent sample coordinates were already correct. Bridge 1.6.0 fixes all six player positions. New schema version 3 recordings use screen order for both sides.

## Scope and reproduction

This check imports every recorded shop snapshot and replays each pair of battle-start teams. It does not claim frame-for-frame agreement. Old files omit the game seed, Chemist progress and Masked Man choices. Random trinket recipients can differ, and early zero-second samples can precede completion of the start phase. Sampled playback normally ends before the exact final combat tick. Existing passive-target projection limitations also remain.

From the repository root:

```powershell
dotnet run --project tools/BatomonHelper.RunValidator -c Release -- assets/simulation/combat-rules.json <first-run.json> <second-run.json>
```

The validator uses seed 69, checks both combat resolvers, emits a JSON report, and exits nonzero for an outcome mismatch or resolver disagreement. It is a developer tool and is excluded from the installer.
