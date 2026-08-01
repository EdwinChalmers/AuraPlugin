# AuraPlugin

A [TaleSpire](https://talespire.com/) [BepInEx](https://github.com/BepInEx/BepInEx) mod that draws a colored aura around a mini (Paladin aura, Spirit Guardians, torch light, or anything else with a fixed area) and keeps it centered on the mini as it's dragged around the board. Two shapes are supported: a flat ground ring, or a 3D bubble/dome that becomes a full sphere while the mini is flying. Synced and persisted for every player at the table via [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/).

## Using it in-game

Right-click a mini and choose **Aura** from the radial menu. This opens a submenu with:

- **Aura On/Off** — switches the aura on or off. This is the only thing that controls visibility; radius/color/shape are remembered independently of whether the aura is currently shown.
- **Aura Radius** — click to step the radius up (5ft per click by default, configurable), wrapping back to the smallest step once it passes the configured max. The current value is shown right on the button.
- **Aura Color** — click to cycle through the configured color list, same as above.
- **Aura Shape** — toggles between **Flat** (a ring on the ground) and **Bubble** (a translucent 3D dome with an equator ring and latitude/longitude grid lines). A bubble automatically becomes a full sphere instead of a flat-bottomed dome while the mini is flying.
- **Aura Opacity** *(Bubble only)* — click to step the bubble surface's opacity. This is a rescaled 0–100% display, not a direct alpha value — see `OpacityRealMaxPercent` below.
- **Show Gridlines** *(Bubble only)* — toggles the bubble's latitude/longitude grid lines on or off. The equator ring stays visible either way.
- **Type Exact Radius...** / **Type Exact Opacity...** *(Bubble only for the latter)* — opens a small text box to type an exact number instead of clicking through steps. Shows the current value on the button.

## Installation

AuraPlugin isn't published to Thunderstore, so r2modman can't install or manage it directly — you install its dependencies through r2modman, then drop the plugin's own files in by hand.

1. **Install [r2modman](https://github.com/ebkr/r2modmanPlus)** if you don't already have it, and pick (or create) the TaleSpire profile you launch the game with. This is *not* the same as picking the "AGM" profile if you have one lying around from something else — check the profile name in r2modman's profile switcher.
2. **Install the three required dependency mods**, either by searching for them in r2modman's "Online" tab or via the links below. Install order doesn't matter — r2modman resolves each mod's own sub-dependencies automatically.

   | Dependency | Notes |
   |---|---|
   | [RadialUIPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RadialUIPlugin/) | Provides the radial menu hooks. Pulls in `SetInjectionFlagPlugin` itself. |
   | [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/) | **Must be 3.6.2 or newer.** Older versions crash on current TaleSpire builds — `CreatureGuid` moved to a different assembly in a game update and old plugin builds still reference the old one. Also pulls in `LoggingPlugin` as its own dependency. |
   | [RPCPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RPCPlugin/) | AssetDataPlugin needs a "message distribution" plugin (RPCPlugin or a chat-service equivalent) installed to actually sync data to other players. Without it, the radial menu still works locally but silently fails to sync to other clients — look for `Message cannot be distributed to others` in the log if auras aren't showing up for other players. |

3. **Get the AuraPlugin files.** Either build them yourself (see [Building from source](#building-from-source) below) or obtain a pre-built `AuraPlugin.dll` and its `Icons/` folder from someone who has.
4. **Find your r2modman profile's plugins folder.** It's normally:
   `%APPDATA%\r2modmanPlus-local\TaleSpire\profiles\<YourProfileName>\BepInEx\plugins\`
   (r2modman's profile settings screen has a "Browse profile folder" button that opens this directly, which is the more reliable way to find it than typing the path by hand.)
5. **Create a folder named `AuraPlugin`** inside `plugins\` (i.e. `...\plugins\AuraPlugin\`) and copy in:
   - `AuraPlugin.dll`
   - the whole `Icons\` folder (containing `aura.png`)
6. **Launch TaleSpire through r2modman**, not the bare Steam shortcut — the game has no BepInEx of its own, and r2modman injects it via environment variables only when it launches the game itself. Use r2modman's "Start modded" button.
7. **Verify it loaded.** Right-click a mini on the board; you should see an **Aura** entry in the radial menu. If it's missing, check the log for load errors — see [Troubleshooting](#troubleshooting) below.

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

1. **Clone this repo** and open `talespire/AuraPlugin/AuraPlugin.csproj`.
2. **Check the path properties at the top of the `.csproj`** against your own machine:

   | Property | Should point to |
   |---|---|
   | `TaleSpireDir` | Your TaleSpire Steam install folder |
   | `R2ProfileDir` | Your r2modman profile folder, e.g. `%APPDATA%\r2modmanPlus-local\TaleSpire\profiles\<YourProfileName>` |
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
5. **Confirm the deploy step ran.** A successful build automatically copies `AuraPlugin.dll` and `Icons\aura.png` into `$(R2ProfileDir)\BepInEx\plugins\AuraPlugin\` (the `DeployToProfile` target in the csproj does this) — check that folder's timestamps updated, no manual copying needed.
6. **Launch TaleSpire through r2modman** ("Start modded") and test — see step 7 under [Installation](#installation).

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
- **The bubble shape**: a translucent hemisphere (grounded) or full sphere (flying), built from scratch rather than reusing the game's native Sight-Range-style component (`MapMenuRangeItem`), which is hardwired to the game's own vision system with no generic hook. The full sphere is an **icosphere** (subdivided icosahedron), not a lat/lon UV-sphere — a UV-sphere's pole-convergent triangles alpha-double-blend on a translucent material into a visible banding artifact right at the pole; an icosphere's evenly-distributed triangles have no pole for that to happen around.
- **The icon**: loaded at runtime from `Icons/aura.png` via `Texture2D.LoadImage`/`Sprite.Create` (falls back to the button's plain text label if the file's missing, rather than crashing).
