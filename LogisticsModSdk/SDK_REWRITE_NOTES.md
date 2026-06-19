# LogisticsModSdk Rewrite Notes

This is the side-by-side SDK-based LogisticsMod build.

- Plugin ID: `com.logisticsmod.sdk`
- Depends on: `com.solarexpanse.sdk`
- Game deploy folder: `BepInEx/plugins/logisticsmodsdk`
- Config file: `BepInEx/plugins/logisticsmodsdk/LogisticsModSdk.cfg`
- Save file: `BepInEx/saves/<saveName>/LogisticsSdkData.json`

The original `LogisticsMod` folder and `com.logisticsmod` plugin ID are intentionally left untouched. If the original plugin is loaded, this SDK rewrite disables itself at startup to avoid duplicate logistics dispatch.

The first SDK-backed implementation moves save/load timing, daily tick dispatch, and object-info UI attachment/refresh into `SolarExpanseSdk`. The complex logistics mission-planning patch remains in the side-by-side mod for parity while the SDK mission hook surface stabilizes.
