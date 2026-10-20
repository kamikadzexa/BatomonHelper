# Batomon Helper 1.0.3

Fixes Musician damage projections and board recommendations for self-knockout Electric creatures such as Electranade.

- Correctly keeps Musician's +150% Shock bonus (2.5× total Shock) when exactly one Electric creature is on the active roster, including its self-knockout cast.
- Fielding a second Electric removes the bonus. A knockout no longer incorrectly enables it during the same battle.
- Board and bench recommendations now score these lineups correctly; bench creatures do not count until placed on the board.

Run `BatomonHelper-Setup-1.0.3.exe` and verify it with the accompanying SHA-256 checksum. Requires Windows 11 22H2 or newer, x64. The installer is unsigned.

Battle output and placement advice remain passive-target estimates.
