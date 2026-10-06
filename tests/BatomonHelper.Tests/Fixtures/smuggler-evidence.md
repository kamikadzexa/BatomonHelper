# Smuggler capture evidence

The PNGs are the original **2434×1369** capture-history frames. Their accompanying V2 JSON files are preserved byte-for-byte as proposals and accepted corrections; they are not training labels or ground truth.

| Fixture | Original capture-history basename |
| --- | --- |
| smuggler-day9 | 20261006164831333-bd5fdd3bcaa445c999101c5fc62cadaa |
| smuggler-day10 | 20261006165114998-e9e1a25a725e4b2293c9f816cdf823fe |

`tools/annotate-smuggler-fixtures.py` reproduces the explicit visual annotations. Each position, visible badge, level, price, HUD number, rank, percentage, and reroll value is annotated. Absent labels remain null. Display aliases retain stable catalog IDs: Electranade → galvanade, Pyronade → infernade, Braulmantis → brawlmantis.

Native magnification corrects the earlier plan's reading of Day 9 Plunderbird: its badges are **damage 8, heal 55, multicast ×5**, rather than 65 / ×3. Day 10 shows **8 / 68 / ×5**. Day 10 Ninflora costs **37**, and the final shop Pylong visibly has **level 2**. Grayscale Zephyrex, Prismagon, and Ninflora retain unknown shiny status; Pylong's normal silhouette/luminance pattern has a sufficient template margin.

Native tests assert all slots and every resolved field, then require every readable number to resolve. Fractional-scale and pixel-phase tests assert every resolved proposal against these same annotations, so a confident wrong value fails even when other values require review.
