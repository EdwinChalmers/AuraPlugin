# AuraPlugin

A [TaleSpire](https://talespire.com/) [BepInEx](https://github.com/BepInEx/BepInEx) mod that draws a colored aura around a mini (Paladin aura, Spirit Guardians, torch light, or anything else with a fixed area) and keeps it centered on the mini as it's dragged around the board. Two shapes are supported: a flat ground ring, or a translucent 3D sphere. Synced and persisted for every player at the table via [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/).

## Using it in-game

Right-click a mini and choose **Aura** from the radial menu. This opens a submenu with:

- **Aura On/Off** — switches the aura on or off; radius/color/shape are remembered independently of whether the aura is currently shown. An aura also hides automatically whenever its mini is hidden (see below), so this button not being enough to make an aura appear usually means the creature itself is hidden.
- **Aura Radius** — click to step the radius up (5ft per click by default, configurable), wrapping back to the smallest step once it passes the configured max. The current value is shown right on the button.
- **Aura Color** — click to cycle through the configured color list, same as above.
- **Aura Shape** — toggles between **Flat** (a ring on the ground) and **Bubble** (a translucent 3D sphere with an equator ring and latitude/longitude grid lines), regardless of whether the mini is flying or grounded.
- **Aura Opacity** *(Bubble only)* — click to step the bubble surface's opacity. This is a rescaled 0–100% display, not a direct alpha value — see `OpacityRealMaxPercent` below.
- **Show Gridlines** *(Bubble only)* — toggles the bubble's latitude/longitude grid lines on or off. The equator ring stays visible either way.
- **Type Exact Radius...** / **Type Exact Opacity...** *(Bubble only for the latter)* — opens a small text box to type an exact number instead of clicking through steps. Shows the current value on the button.

## Installation

1. **Install [r2modman](https://github.com/ebkr/r2modmanPlus)** if you don't already have it, and pick (or create) the TaleSpire profile you launch the game with.
2. **Find "AuraPlugin" in r2modman's Online tab and click Install.** Its dependencies are installed automatically — you don't need to add them by hand.
3. **Launch TaleSpire with r2modman's "Start modded" button**, not the bare Steam shortcut. TaleSpire has no BepInEx of its own; r2modman injects it via environment variables only when it launches the game itself, so launching from Steam loads no mods at all.
4. **Verify it loaded.** Right-click a mini on the board — you should see an **Aura** entry in the radial menu. If it's missing, see [Troubleshooting](#troubleshooting).

### What gets installed alongside it

These come down automatically as dependencies; listed here because if something misbehaves, it's usually one of them:

| Dependency | Why it's needed |
|---|---|
| [RadialUIPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RadialUIPlugin/) | Provides the radial menu hooks. Pulls in `SetInjectionFlagPlugin` itself. |
| [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/) | Syncs and persists each mini's aura settings. **Must be 3.6.2 or newer** — older versions crash on current TaleSpire builds, since `CreatureGuid` moved to a different assembly in a game update and old plugin builds still reference the old one. Pulls in `LoggingPlugin`. |
| [RPCPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RPCPlugin/) | AssetDataPlugin needs a "message distribution" plugin to actually send data to other players. Without it the menu still works locally but silently fails to sync — look for `Message cannot be distributed to others` in the log if your auras aren't showing up for anyone else. |

<details>
<summary>Installing without Thunderstore (from a GitHub Release zip)</summary>

Useful if you want a specific older version, or you built it yourself.

1. Download `AuraPlugin-<version>.zip` from the [Releases page](https://github.com/EdwinChalmers/AuraPlugin/releases) (building from source produces this same zip — see [Building from source](#building-from-source)).
2. In r2modman, go to **Settings → Profile → Import Local Mod** and select the zip, or drag it onto that screen. r2modman reads its `manifest.json` and installs it like any other mod.
3. Install the three dependencies above yourself via the **Online** tab — an imported local mod won't always prompt for them.
4. Continue from step 3 above.

Fully manual, if you'd rather not use r2modman's importer: find your profile's plugins folder (`%APPDATA%\r2modmanPlus-local\TaleSpire\profiles\<YourProfileName>\BepInEx\plugins\` — r2modman's profile settings screen has a "Browse profile folder" button), create an `AuraPlugin` folder inside it, and copy in `AuraPlugin.dll` and `aura.png` side by side.

</details>

## Configuration

After first launch, a config file appears at `BepInEx/config/andrew.talespire.auraplugin.cfg`. Note: BepInEx only writes config *defaults* the first time a key is created — editing a default in the code has no effect on an already-existing config file, since it won't overwrite a value you (or an earlier version) already saved there.

**Radius**
- `RadiusStepFeet` (default `5`) — how much each click on Aura Radius adds.
- `RadiusMaxFeet` (default `60`) — radius wraps back to the smallest step past this (use Aura On/Off to actually hide the aura, not this).
- `FeetPerTile` (default `5`) — match this to your table's ruler scale; it's how radius-in-feet gets converted to the board's own grid units.

**Color**
- `ColorSteps` (default `Gold:#FFD70066,Red:#FF000066,Blue:#1E90FF66,Green:#32CD3266,Purple:#9370DB66`) — the color cycle, as `Name:RRGGBBAA` pairs.

**Opacity** *(Bubble shape only)*
- `OpacityStepPercent` (default `10`) — how much each click on Aura Opacity adds, on the displayed 0–100 scale.
- `OpacityRealMaxPercent` (default `30`) — what the displayed 100% actually maps to as real surface alpha. This is a linear rescale, not a cap: displayed 50% is always half of whatever this is set to.

**Visual**
- `RingHeightAboveBase` / `RingLineWidth` — how high the flat ring floats and how thick its line is.
- `BubbleGridAlpha` / `BubbleGridLineWidth` — transparency and thickness of the bubble's grid lines.
- `BubbleGridRingCount` (default `2`) / `BubbleGridMeridianCount` (default `6`) — how many latitude/longitude lines are drawn on the bubble (clamped to sane maximums so a mistyped value can't hang the client building hundreds of them).

## Building from source

### Prerequisites

1. **TaleSpire installed via Steam.** Note the install path — default is `C:\Program Files (x86)\Steam\steamapps\common\TaleSpire`, but confirm via Steam → right-click TaleSpire → Manage → Browse local files.
2. **r2modman**, with a profile that already has RadialUIPlugin and AssetDataPlugin installed (see [Installation](#installation) above) — the build pulls their DLLs straight out of r2modman's cache, so they need to actually be installed before you can compile against them.
3. **.NET Framework 4.8.1 Developer Pack**, since the project targets `v4.8.1` to match this version of Unity's own assemblies:
   ```
   winget install Microsoft.DotNet.Framework.DeveloperPack_4
   ```
4. **MSBuild.** Either Visual Studio (with the ".NET desktop development" workload) or the standalone [Build Tools for Visual Studio](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022) — either gives you an `msbuild` you can run from a "Developer Command Prompt" / "Developer PowerShell".
5. *(Optional, only needed if you're changing code that touches undocumented game/plugin APIs)* [`ilspycmd`](https://github.com/icsharpcode/ILSpy) for decompiling TaleSpire's own assemblies to check exact method signatures, since Thunderstore/GitHub docs for these mods are often stale:
   ```
   dotnet tool install -g ilspycmd --version 8.2.0.7535
   ```
   (Pin this version — at time of writing, the newest ilspycmd release fails to install.)

### Steps

1. **Clone this repo** and open `AuraPlugin.csproj`.
2. **Check the path properties at the top of the `.csproj`** against your own machine:

   | Property | Should point to |
   |---|---|
   | `TaleSpireDir` | Your TaleSpire Steam install folder |
   | `R2ProfileDir` | Your r2modman profile folder. Defaults to `%APPDATA%\r2modmanPlus-local\TaleSpire\profiles\Talespire` — only the trailing profile name (`Talespire`) needs changing if your profile is named differently. |
   | `RadialUIDir` | RadialUIPlugin's cached DLL folder under r2modman's cache, e.g. `...\r2modmanPlus-local\TaleSpire\cache\HolloFox_TS-RadialUIPlugin\<version>` |
   | `AssetDataDir` | AssetDataPlugin's cached DLL folder likewise, e.g. `...\cache\LordAshes-AssetDataPlugin\<version>` |
   | `SetInjectionFlagDir` | `$(R2ProfileDir)\BepInEx\plugins\brcoding-SetInjectionFlagPlugin` (installed automatically as a dependency of RadialUIPlugin) |

   Edit any that don't match your setup. The version-numbered folder names under r2modman's `cache\` directory will differ from the example if you installed newer dependency versions.
3. **Close TaleSpire** if it's currently running. The build's deploy step copies the DLL straight into the BepInEx plugins folder, and a running game holds that file locked — the copy step will fail with a file-in-use error if you skip this.
4. **Build it:**
   - From a Developer Command Prompt / Developer PowerShell, in the `AuraPlugin` folder:
     ```
     msbuild AuraPlugin.csproj
     ```
   - Or open the `.csproj` in Visual Studio and hit Build.
5. **Confirm the deploy step ran.** A successful build automatically copies `AuraPlugin.dll` and `aura.png` into `$(R2ProfileDir)\BepInEx\plugins\AuraPlugin\` (the `DeployToProfile` target in the csproj does this) — check that folder's timestamps updated, no manual copying needed.
6. **Launch TaleSpire through r2modman** ("Start modded") and test — see step 6 under [Installation](#installation).
7. **(Optional) Package it.** Run `.\package-local-mod.ps1` — it bundles `manifest.json`, `icon.png`, `README.md`, the built DLL, and `aura.png` into `AuraPlugin-<version>.zip`. That zip is both a valid Thunderstore package and something you can hand to someone directly for [Import Local Mod](#installation). Re-run it any time after rebuilding.

### Releasing

CI can't build this — the csproj references TaleSpire's game assemblies and r2modman's plugin cache, neither of which exists on a runner and neither of which can be vendored here. So the locally built zip is the artifact of record:

1. Bump the version in **both** `manifest.json` (`version_number`) and the `[BepInPlugin(...)]` attribute at the top of `Plugin.cs` — they must match, or the mod manager and the BepInEx log will disagree about which build is running. Thunderstore versions are immutable, so every upload needs a new number.
2. Build, then run `.\package-local-mod.ps1`.
3. Create a GitHub Release and attach the zip:
   ```
   gh release create v<version>-auraplugin AuraPlugin-<version>.zip --title "AuraPlugin v<version>" --notes "..."
   ```
4. The [`publish-thunderstore`](.github/workflows/publish-thunderstore.yml) workflow fires on the published release, downloads that exact asset, and pushes it to Thunderstore — so what's on Thunderstore is byte-identical to what's on Releases. It reads the package name/version/description from the zip's own `manifest.json`, so there's nothing to keep in sync in the workflow file.

Publishing requires a `THUNDERSTORE_TOKEN` repository secret (a Thunderstore **service account** token, created from your team's settings page). Optionally set a `THUNDERSTORE_NAMESPACE` repository variable if your team name isn't `EdwinChalmers`.

### Troubleshooting

- **Build succeeds but changes don't show up in-game:** BepInEx only writes a config key's *default* the first time it's created. If you changed a `Config.Bind(...)` default in `Plugin.cs`, that has no effect on a config file that already exists on disk with the old value — edit or delete `BepInEx/config/andrew.talespire.auraplugin.cfg` in the profile directly.
- **Copy/deploy step fails with a file-in-use error:** TaleSpire (or a leftover process) still has the old DLL open — fully close the game and retry.
- **Plugin doesn't load / crashes on launch:** check the live log at `%USERPROFILE%\AppData\LocalLow\Bouncyrock Entertainment\TaleSpire\Player.log` — this is Unity's own log and reflects what's actually running, unlike `BepInEx\LogOutput.log` in the profile folder which can be stale. Confirm from the log which r2modman profile and plugin versions actually loaded, since it's easy to be pointed at the wrong profile.

## How it works internally

- **Movement tracking**: TaleSpire doesn't expose a "creature moved" event to plugins, so `AuraRingFollower`/`AuraBubbleFollower` poll the target mini's `transform.position` every frame and reposition accordingly - deliberately not parented to the mini's own transform, so a flying-animation tilt doesn't tip the aura over.
- **Hiding**: the followers also poll `CreatureBoardAsset.IsVisible` so an aura disappears with its mini. That's the game's combined visibility flag — dropped in, not explicitly hidden, not in a hide volume, not vision-culled — so it covers hide volumes and per-player line of sight, not just the hide toggle. Note GM mode exempts the *vision* parts but **not** the explicit hide toggle (`UpdateExplicitHideState` has no GM branch), so a GM who hides a mini loses its aura too even though they still see the mini ghosted. That's consistent with the game's own creature-attached extras — the flying indicator and torch light behave the same way. It toggles `Renderer.enabled` rather than `SetActive`, because `AuraRingFollower` sits on the same GameObject as the ring's `LineRenderer` and deactivating it would stop `Update()` running, leaving no way to ever show the aura again.
- **Sync/persistence**: handled entirely by AssetDataPlugin. We just call `SetInfo`/`ReadInfo`/`Subscribe` with a handful of string keys per creature — AssetDataPlugin takes care of broadcasting changes to other clients and re-delivering the current values when a board loads.
- **On/off backward compatibility**: before the dedicated Aura On/Off button existed, radius `0` meant "off". `GetAuraEnabled` reconstructs the old visibility for minis configured before this button existed by checking whether their stored radius was ever `> 0`, so upgrading doesn't change any existing table's auras.
- **The "Aura" submenu**: RadialUIPlugin's public API only lets you add buttons to a handful of *existing* native categories (Attacks/Emotes/Status/GM/Kill/Size) — there's no documented "create a brand new branch" method. AuraPlugin instead calls `MapMenuManager.OpenMenu(...)` directly (the same underlying game API RadialUIPlugin's own submenu helper uses) to build its own ring of buttons, all with `FadeName = false` so their labels stay visible without needing to hover.
- **Live button updates**: `MapMenuItem.Setup(...)` — the only public way to change a button's label — also calls `transform.SetAsLastSibling()` internally, which reorders buttons within the radial layout as a side effect. Calling it again just to refresh a number visibly swapped buttons' positions. Instead, `RefreshDisplayedValue` reaches past `Setup()` via reflection to update just the private `_valueText` field and the center text label directly, leaving everything else (including button order) untouched.
- **The bubble shape**: a translucent full sphere, always — regardless of whether the mini is flying or grounded — built from scratch rather than reusing the game's native Sight-Range-style component (`MapMenuRangeItem`), which is hardwired to the game's own vision system with no generic hook. It's an **icosphere** (subdivided icosahedron), not a lat/lon UV-sphere — a UV-sphere's pole-convergent triangles alpha-double-blend on a translucent material into a visible banding artifact right at the pole; an icosphere's evenly-distributed triangles have no pole for that to happen around.
- **The icon**: loaded at runtime from `aura.png` sitting next to the DLL via `Texture2D.LoadImage`/`Sprite.Create` (falls back to the button's plain text label if the file's missing, rather than crashing). Deliberately not in a subfolder — r2modman's Import Local Mod doesn't reliably preserve nested subfolders from a package zip on extraction.
