# AuraPlugin

A [TaleSpire](https://talespire.com/) [BepInEx](https://github.com/BepInEx/BepInEx) mod that draws a colored aura around a mini (Paladin aura, Spirit Guardians, torch light, or anything else with a fixed area) and keeps it centered on the mini as it's dragged around the board. Two shapes are supported: a flat ground ring, or a translucent 3D sphere. Synced and persisted for every player at the table via [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/).

## Using it in-game

Right-click a mini and choose **Aura** from the radial menu. This opens a submenu with:

- **Aura On/Off** — switches the aura on or off. This is the only thing that controls visibility; radius/color/shape are remembered independently of whether the aura is currently shown.
- **Aura Radius** — click to step the radius up (5ft per click by default, configurable), wrapping back to the smallest step once it passes the configured max. The current value is shown right on the button.
- **Aura Color** — click to cycle through the configured color list, same as above.
- **Aura Shape** — toggles between **Flat** (a ring on the ground) and **Bubble** (a translucent 3D sphere with an equator ring and latitude/longitude grid lines), regardless of whether the mini is flying or grounded.
- **Aura Opacity** *(Bubble only)* — click to step the bubble surface's opacity. This is a rescaled 0–100% display, not a direct alpha value — see `OpacityRealMaxPercent` below.
- **Show Gridlines** *(Bubble only)* — toggles the bubble's latitude/longitude grid lines on or off. The equator ring stays visible either way.
- **Type Exact Radius...** / **Type Exact Opacity...** *(Bubble only for the latter)* — opens a small text box to type an exact number instead of clicking through steps. Shows the current value on the button.

## Installation

AuraPlugin isn't published to Thunderstore, so r2modman can't find or auto-update it — but it can still be *imported* as a local mod so you don't have to hand-copy files into the plugins folder.

1. **Install [r2modman](https://github.com/ebkr/r2modmanPlus)** if you don't already have it, and pick (or create) the TaleSpire profile you launch the game with.
2. **Download `AuraPlugin-<version>.zip`** from the [Releases page](https://github.com/EdwinChalmers/AuraPlugin/releases). (If you're building from source instead, `package-local-mod.ps1` produces this same zip — see [Building from source](#building-from-source) below.)
3. **In r2modman, go to Settings → Profile → Import Local Mod**, and select the zip (or just drag the zip onto that screen). r2modman reads its `manifest.json` and installs it — including its icon and version — the same way it would a Thunderstore mod, and drops the DLL/`aura.png` into the right place automatically.
4. **Install the mod's dependencies.** r2modman lists them from `manifest.json` (RadialUIPlugin, AssetDataPlugin 3.6.2+, RPCPlugin) — accept the prompt to install them, or add them yourself via the "Online" tab if it doesn't prompt automatically.

   | Dependency | Notes |
   |---|---|
   | [RadialUIPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RadialUIPlugin/) | Provides the radial menu hooks. Pulls in `SetInjectionFlagPlugin` itself. |
   | [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/) | **Must be 3.6.2 or newer.** Older versions crash on current TaleSpire builds — `CreatureGuid` moved to a different assembly in a game update and old plugin builds still reference the old one. Also pulls in `LoggingPlugin` as its own dependency. |
   | [RPCPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RPCPlugin/) | AssetDataPlugin needs a "message distribution" plugin (RPCPlugin or a chat-service equivalent) installed to actually sync data to other players. Without it, the radial menu still works locally but silently fails to sync to other clients — look for `Message cannot be distributed to others` in the log if auras aren't showing up for other players. |

5. **Launch TaleSpire through r2modman**, not the bare Steam shortcut — the game has no BepInEx of its own, and r2modman injects it via environment variables only when it launches the game itself. Use r2modman's "Start modded" button.
6. **Verify it loaded.** Right-click a mini on the board; you should see an **Aura** entry in the radial menu. If it's missing, check the log for load errors — see [Troubleshooting](#troubleshooting) below.

<details>
<summary>Manual install (if you'd rather not use Import Local Mod)</summary>

1. Install the three dependencies above through r2modman's "Online" tab as normal.
2. Find your r2modman profile's plugins folder: `%APPDATA%\r2modmanPlus-local\TaleSpire\profiles\<YourProfileName>\BepInEx\plugins\` (r2modman's profile settings screen has a "Browse profile folder" button that opens this directly).
3. Create a folder named `AuraPlugin` inside `plugins\` and copy in `AuraPlugin.dll` and `aura.png`.
4. Continue from step 5 above.

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
7. **(Optional) Package it for distribution.** Run `.\package-local-mod.ps1` from the `AuraPlugin` folder — it bundles `manifest.json`, `icon.png`, `README.md`, the built DLL, and `aura.png` into `AuraPlugin-<version>.zip`, ready to hand to someone else for [Import Local Mod](#installation) in r2modman. Re-run it any time after rebuilding to refresh the zip.

### Troubleshooting

- **Build succeeds but changes don't show up in-game:** BepInEx only writes a config key's *default* the first time it's created. If you changed a `Config.Bind(...)` default in `Plugin.cs`, that has no effect on a config file that already exists on disk with the old value — edit or delete `BepInEx/config/andrew.talespire.auraplugin.cfg` in the profile directly.
- **Copy/deploy step fails with a file-in-use error:** TaleSpire (or a leftover process) still has the old DLL open — fully close the game and retry.
- **Plugin doesn't load / crashes on launch:** check the live log at `%USERPROFILE%\AppData\LocalLow\Bouncyrock Entertainment\TaleSpire\Player.log` — this is Unity's own log and reflects what's actually running, unlike `BepInEx\LogOutput.log` in the profile folder which can be stale. Confirm from the log which r2modman profile and plugin versions actually loaded, since it's easy to be pointed at the wrong profile.

## How it works internally

- **Movement tracking**: TaleSpire doesn't expose a "creature moved" event to plugins, so `AuraRingFollower`/`AuraBubbleFollower` poll the target mini's `transform.position` every frame and reposition accordingly - deliberately not parented to the mini's own transform, so a flying-animation tilt doesn't tip the aura over.
- **Sync/persistence**: handled entirely by AssetDataPlugin. We just call `SetInfo`/`ReadInfo`/`Subscribe` with a handful of string keys per creature — AssetDataPlugin takes care of broadcasting changes to other clients and re-delivering the current values when a board loads.
- **On/off backward compatibility**: before the dedicated Aura On/Off button existed, radius `0` meant "off". `GetAuraEnabled` reconstructs the old visibility for minis configured before this button existed by checking whether their stored radius was ever `> 0`, so upgrading doesn't change any existing table's auras.
- **The "Aura" submenu**: RadialUIPlugin's public API only lets you add buttons to a handful of *existing* native categories (Attacks/Emotes/Status/GM/Kill/Size) — there's no documented "create a brand new branch" method. AuraPlugin instead calls `MapMenuManager.OpenMenu(...)` directly (the same underlying game API RadialUIPlugin's own submenu helper uses) to build its own ring of buttons, all with `FadeName = false` so their labels stay visible without needing to hover.
- **Live button updates**: `MapMenuItem.Setup(...)` — the only public way to change a button's label — also calls `transform.SetAsLastSibling()` internally, which reorders buttons within the radial layout as a side effect. Calling it again just to refresh a number visibly swapped buttons' positions. Instead, `RefreshDisplayedValue` reaches past `Setup()` via reflection to update just the private `_valueText` field and the center text label directly, leaving everything else (including button order) untouched.
- **The bubble shape**: a translucent full sphere, always — regardless of whether the mini is flying or grounded — built from scratch rather than reusing the game's native Sight-Range-style component (`MapMenuRangeItem`), which is hardwired to the game's own vision system with no generic hook. It's an **icosphere** (subdivided icosahedron), not a lat/lon UV-sphere — a UV-sphere's pole-convergent triangles alpha-double-blend on a translucent material into a visible banding artifact right at the pole; an icosphere's evenly-distributed triangles have no pole for that to happen around.
- **The icon**: loaded at runtime from `aura.png` sitting next to the DLL via `Texture2D.LoadImage`/`Sprite.Create` (falls back to the button's plain text label if the file's missing, rather than crashing). Deliberately not in a subfolder — r2modman's Import Local Mod doesn't reliably preserve nested subfolders from a package zip on extraction.
