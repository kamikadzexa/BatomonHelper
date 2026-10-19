# Batomon Helper 1.0.1

Windows x64 helper release. Run `BatomonHelper-Setup-1.0.1.exe`; verify it using the accompanying SHA-256 checksum.

- Game setup opens automatically when the game is stopped or mod setup is incomplete; it detects Steam libraries, checks readiness, and launches the game.
- Install, update/repair, or remove the helper bridge directly from the helper.
- Missing Mod Loader: download and extract version 1.2.2, use Select downloaded loader, then Install mod. A read-only checkmark shows whether the loader is installed and enabled. The loader is not bundled.
- Connection messages distinguish a stopped game from missing bridge data or a stale heartbeat. Other warnings remain visible independently.
- Hide · F8 button beside Refresh · F9; F8 restores the hidden helper. Standard window controls remain available.
- Self-contained installer includes .NET; no separate runtime download is needed.
- The helper checks GitHub Releases for a matching installer and SHA-256 checksum. Updates can be started manually or installed automatically when enabled in Game setup.
- Original code uses 0BSD. Third-party material retains its own terms.

Requires Windows 11 22H2 (build 22621) or newer, x64. Training Lab is excluded. The installer is unsigned.

After installation open **Game setup**, confirm the folder, install the mod with the game closed, then select **Launch game**. Catalog browsing works without the game. Remove the bridge through Game setup before uninstalling the helper if you want both removed.

Battle WIN/LOSS comes from the game. Output projections and positioning suggestions are estimates. The bundled wiki baseline is 1.2.0 with a 1.3.0 reference layer.

See [validation results and remaining manual checks](release-validation.md).
