# AuraPlugin

A [TaleSpire](https://talespire.com/) [BepInEx](https://github.com/BepInEx/BepInEx) mod that draws coloured spell areas and auras around a mini and keeps them on the mini as it's dragged and turned. Nine shapes — circle, cube, cone, line, cylinder, wall and more — each drawable as a flat ground template or a translucent 3D solid, filled or outline-only. Every mini can carry a standing **Aura** and a cast **Spell** at the same time, independently. Synced and persisted for every player at the table via [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/).

## Using it in-game

Right-click a mini. Two entries appear in the radial menu:

- **Aura** — a standing area that belongs to the creature: a paladin's aura, a torch, a dragon's fear radius.
- **Spells** — a cast effect: Fireball, Burning Hands, Wall of Fire.

They are **completely independent**. A paladin can show a gold aura ring and a red cone at the same time; each has its own size, colour, shape and opacity, and switching one off leaves the other alone.

Both menus offer the same controls:

| Button | What it does |
|---|---|
| **On/Off** | Shows or hides this overlay. Everything else is remembered while it's off. An overlay also hides automatically whenever its mini is hidden — so if this button isn't enough to make it appear, the creature itself is probably hidden. |
| **Toggle Radius** / **Toggle Size** | Steps the size up (5 ft per click by default), wrapping back to the smallest step past the configured max. The current value shows on the button. |
| **Toggle Opacity** | Steps the opacity. This is a rescaled 0–100% display, not a direct alpha value — see `OpacityRealMaxPercent` below. |
| **Type Radius** / **Type Size**, **Type Opacity** | Opens a small text box to type an exact number instead of clicking through the steps. |
| **Shape** | Opens a shape picker (see below). |
| **Dimension** | **2D** draws a flat template on the ground; **3D** draws a solid volume. |
| **Fill** | **On** paints the interior; **Off** draws only the outline. The outline is always drawn either way. |
| **Color** | Opens a colour picker: a ring of buttons, one per configured colour, each a filled circle of that colour. Picking one applies it and returns you to the menu. |
| **Show Gridlines** *(Aura only)* | Latitude/longitude lines on the 3D sphere. Off by default; the equator ring stays either way. |
| **Common…** *(Spells only)* | Generic templates by size — 15/30/60 ft cones, 30/60/100 ft lines, 10/15/20/30 ft areas, and a 15 ft cube. |
| **Spell Presets…** *(Spells only)* | Named spells that set size, colour, shape and dimension in one click. |

### Shapes

| Shape | Where | Anchored | 3D form |
|---|---|---|---|
| **Circle** | Aura + Spells | centred on the mini | sphere |
| **Cube** | Aura + Spells | centred on the mini | cube |
| **Cone** | Spells | point on the mini | cone |
| **Line** | Spells | starts at the mini | wall |
| **Cube (Ahead)** | Spells | near face on the mini | cube |
| **Cube (Corner)** | Spells | corner on the mini, diagonal along facing | cube |
| **Cylinder** | Spells | centred on the mini | cylinder |
| **Wall** | Spells | centred on the mini | wall |
| **Ring (Wall)** | Spells | centred on the mini | hollow tube |

**Size means the obvious thing for each shape**: a radius for circles, cylinders and rings; a side length for cubes; a length for cones, lines and walls. So Wall of Fire's "20 feet in diameter" ring is a size of 10.

**Directional shapes follow the mini's own facing.** Hold Alt and turn the model, and the cone, line or wall turns with it — there's no separate aiming control to learn, and because the game already syncs a mini's rotation, everyone at the table sees it pointing the same way.

**Wall sections are centred on their mini**, so a wall gets built from several minis: a 5 ft section fills exactly the square its mini stands in, and sections line up by placing minis on adjacent squares.

### Spell presets

Spirit Guardians, Fireball, Darkness, Silence, Thunderwave, Burning Hands, Lightning Bolt, Moonbeam, Spike Growth, and Wall of Fire Ring. All editable — see `SpellPresets` below.

## Installation

1. **Install [r2modman](https://github.com/ebkr/r2modmanPlus)** if you don't already have it, and pick (or create) the TaleSpire profile you launch the game with.
2. **Find "AuraPlugin" in r2modman's Online tab and click Install.** Its dependencies are installed automatically — you don't need to add them by hand.
3. **Launch TaleSpire with r2modman's "Start modded" button**, not the bare Steam shortcut. TaleSpire has no BepInEx of its own; r2modman injects it via environment variables only when it launches the game itself, so launching from Steam loads no mods at all.
4. **Verify it loaded.** Right-click a mini on the board — you should see an **Aura** entry in the radial menu. If it's missing, see [Troubleshooting](https://github.com/EdwinChalmers/AuraPlugin#troubleshooting).

### What gets installed alongside it

These come down automatically as dependencies; listed here because if something misbehaves, it's usually one of them:

| Dependency | Why it's needed |
|---|---|
| [RadialUIPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RadialUIPlugin/) | Provides the radial menu hooks. Pulls in `SetInjectionFlagPlugin` itself. |
| [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/) | Syncs and persists each mini's aura settings. **Must be 3.6.2 or newer** — older versions crash on current TaleSpire builds, since `CreatureGuid` moved to a different assembly in a game update and old plugin builds still reference the old one. Pulls in `LoggingPlugin`. |
| [RPCPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RPCPlugin/) | AssetDataPlugin needs a "message distribution" plugin to actually send data to other players. Without it the menu still works locally but silently fails to sync — look for `Message cannot be distributed to others` in the log if your auras aren't showing up for anyone else. |

### Installing without Thunderstore (from a GitHub Release zip)

Useful if you want a specific older version, or you built it yourself.

1. Download `AuraPlugin-<version>.zip` from the [Releases page](https://github.com/EdwinChalmers/AuraPlugin/releases) (building from source produces this same zip — see [Building from source](https://github.com/EdwinChalmers/AuraPlugin#building-from-source)).
2. In r2modman, go to **Settings → Profile → Import Local Mod** and select the zip, or drag it onto that screen. r2modman reads its `manifest.json` and installs it like any other mod.
3. Install the three dependencies above yourself via the **Online** tab — an imported local mod won't always prompt for them.
4. Continue from step 3 above.

Fully manual, if you'd rather not use r2modman's importer: find your profile's plugins folder (`%APPDATA%\r2modmanPlus-local\TaleSpire\profiles\<YourProfileName>\BepInEx\plugins\` — r2modman's profile settings screen has a "Browse profile folder" button), create an `AuraPlugin` folder inside it, and copy in `AuraPlugin.dll` and `aura.png` side by side.

## Configuration

After first launch, a config file appears at `BepInEx/config/andrew.talespire.auraplugin.cfg`. Note: BepInEx only writes config *defaults* the first time a key is created — editing a default in the code has no effect on an already-existing config file, since it won't overwrite a value you (or an earlier version) already saved there. To change a setting you've already got, edit the config file itself, with the game closed.

**Size**
- `RadiusStepFeet` (default `5`) — how much each click on Toggle Radius/Size adds.
- `RadiusMaxFeet` (default `60`) — size wraps back to the smallest step past this (use On/Off to hide an overlay, not this).
- `FeetPerTile` (default `5`) — match this to your table's ruler scale; it's how size-in-feet becomes board grid units.

**Colour**
- `ColorSteps` (default `Gold:#FFD70066,Red:#FF000066,Blue:#1E90FF66,Green:#32CD3266,Purple:#9370DB66,White:#FFFFFF66,Black:#00000066`) — the colours offered by the colour picker, as `Name:RRGGBBAA` pairs. The alpha byte is vestigial: the resolved opacity value overwrites it when the aura is drawn.

**Opacity**
- `OpacityStepPercent` (default `25`) — how much each click on Toggle Opacity adds, on the displayed 0–100 scale.
- `OpacityRealMaxPercent` (default `20`) — what the displayed 100% actually maps to as real surface alpha. A linear rescale, not a cap: displayed 50% is always half of whatever this is set to.
- `ColorRealMaxOverrides` (default `Black:50`) — per-colour overrides for the above, as `Name:Percent` pairs. Anything not listed falls back to `OpacityRealMaxPercent`. Black gets a higher ceiling because a dark aura has much less contrast to spend against a dark map than a saturated colour does.

**Shape sizing**
- `LineShapeWidthFeet` (default `5`) — width of the Line shape. Size sets its length.
- `WallThicknessFeet` (default `1`) / `WallHeightFeet` (default `20`) — thickness and height of the Wall and Ring shapes.
- `CylinderHeightFeet` (default `40`) — height of a 3D Cylinder. Matches Moonbeam, Flame Strike and Ice Storm.
- `ConeApexHeightFeet` (default `2.5`) — how high above the tabletop a 3D cone's point sits, so a breath weapon comes out of the creature rather than off the floor.
- `SolidShapeHeightFeet` (default `10`) — height of a 3D Cone or Line. Cubes ignore all of these: a cube's height is its own size.
- `ShapeFacingOffsetDegrees` (default `0`) — added to every directional shape's facing. Leave at 0 unless your minis' models consistently point away from the direction their bases indicate.

**Presets**
- `SpellPresets` — the named spells, comma separated, each as `Name:SizeFeet:ColorName:Shape:OpacityPercent` with an optional sixth `2D` or `3D` field. `ColorName` must be one of the names in `ColorSteps`.
- `CommonPresets` — the generic templates behind the **Common…** button, same format.

**Visual**
- `RingHeightAboveBase` / `RingLineWidth` — how high templates float above the tabletop and how thick their outlines are.
- `BubbleGridAlpha` / `BubbleGridLineWidth` — transparency and thickness of the sphere's grid lines.
- `BubbleGridRingCount` (default `2`) / `BubbleGridMeridianCount` (default `6`) — how many latitude/longitude lines are drawn on the sphere (clamped so a mistyped value can't hang the client building hundreds of them).

## Building from source

### Prerequisites

1. **TaleSpire installed via Steam.** Note the install path — default is `C:\Program Files (x86)\Steam\steamapps\common\TaleSpire`, but confirm via Steam → right-click TaleSpire → Manage → Browse local files.
2. **r2modman**, with a profile that already has RadialUIPlugin and AssetDataPlugin installed (see [Installation](https://github.com/EdwinChalmers/AuraPlugin#installation) above) — the build pulls their DLLs straight out of r2modman's cache, so they need to actually be installed before you can compile against them.
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
6. **Launch TaleSpire through r2modman** ("Start modded") and test — see step 6 under [Installation](https://github.com/EdwinChalmers/AuraPlugin#installation).
7. **(Optional) Package it.** Run `.\package-local-mod.ps1` — it bundles `manifest.json`, `icon.png`, `README.md`, the built DLL, and `aura.png` into `AuraPlugin-<version>.zip`. That zip is both a valid Thunderstore package and something you can hand to someone directly for [Import Local Mod](https://github.com/EdwinChalmers/AuraPlugin#installation). Re-run it any time after rebuilding.

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

- **Two slots**: each creature carries two independent sets of settings. The Aura slot uses the original unprefixed AssetDataPlugin keys (`AuraPlugin.Radius`…) so auras saved by older versions keep working untouched; the Spell slot is prefixed (`AuraPlugin.Spell.*`).
- **Movement and facing**: TaleSpire exposes no "creature moved" event, so the followers poll the mini every frame. Facing comes from `-CreatureBoardAsset.Rotator.right` flattened onto the ground plane — *not* the creature's `transform.forward` (the root never rotates when you turn a mini) and *not* `Rotator.forward` (the Rotator spins about its own local Z, so its forward points straight up). `MovableBoardAsset.RotateTowards` measures a mini's heading against exactly that vector. Only yaw is taken, never the mini's tilt, so a flying animation can't lift a ground template off the table.
- **Shapes**: every 3D solid except the sphere and the ring is the flat outline extruded straight up, which is why the 2D and 3D forms of a shape can never disagree about the area covered. Caps are fan-triangulated, so every footprint must be convex — the ringed wall needs its own builder because an annulus isn't.
- **The sphere** is an icosphere (subdivided icosahedron), not a lat/lon UV-sphere: a UV-sphere's pole-convergent triangles alpha-double-blend on a translucent material into visible banding right at the pole.
- **Hiding**: the followers poll `CreatureBoardAsset.IsVisible` so an overlay disappears with its mini. That's the game's combined visibility flag — dropped in, not explicitly hidden, not in a hide volume, not vision-culled. Note GM mode exempts the *vision* parts but **not** the explicit hide toggle, so a GM who hides a mini loses its aura too even though they still see the mini ghosted. That matches the game's own creature-attached extras. It toggles `Renderer.enabled` rather than `SetActive`, because the follower sits on the same GameObject and deactivating it would stop `Update()` running, leaving no way to ever show the aura again.
- **Sync/persistence**: handled entirely by AssetDataPlugin — a handful of string keys per creature, which it broadcasts to other clients and re-delivers when a board loads. It delivers the same change more than once (local write, backlog, periodic rebroadcast), so each visual records what it was built from and skips rebuilds that wouldn't change anything; without that, picking a shape visibly flickered.
- **Backward compatibility**: `Bubble` used to be a shape and is now Circle + 3D — stored data and preset configs saying `Bubble` are migrated on read rather than rejected. Before the On/Off button existed, radius `0` meant "off", and `GetAuraEnabled` reconstructs that for minis configured back then.
- **The menus**: RadialUIPlugin's public API only adds buttons to existing native categories, with no way to create a new branch. AuraPlugin calls `MapMenuManager.OpenMenu(...)` directly — the same game API RadialUIPlugin's own submenu helper uses — to build its own rings of buttons. Note `MapMenuItem.LeftClick` runs a button's action *before* force-closing the menu when `closeOnActivate` is set, so pickers that reopen the menu they came from must leave that flag off and drive the close themselves.
- **Live button updates**: `MapMenuItem.Setup(...)` — the only public way to change a button's label — also calls `transform.SetAsLastSibling()`, which reorders buttons as a side effect. `RefreshDisplayedValue` instead reaches past it via reflection to update the private `_valueText` field and the centre label directly.
- **The icon**: loaded at runtime from `aura.png` next to the DLL, falling back to a plain text label if the file's missing rather than crashing. Deliberately not in a subfolder — r2modman's Import Local Mod doesn't reliably preserve nested subfolders from a package zip.
