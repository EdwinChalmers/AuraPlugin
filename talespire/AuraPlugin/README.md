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

## Requirements

Install these three via [r2modman](https://github.com/ebkr/r2modmanPlus) before AuraPlugin will load:

| Dependency | Notes |
|---|---|
| [RadialUIPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RadialUIPlugin/) | Provides the radial menu hooks. Pulls in `SetInjectionFlagPlugin` itself. |
| [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/) | **Must be 3.6.2 or newer.** Older versions crash on current TaleSpire builds — `CreatureGuid` moved to a different assembly in a game update and old plugin builds still reference the old one. Also pulls in `LoggingPlugin` as its own dependency. |
| [RPCPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RPCPlugin/) | AssetDataPlugin needs a "message distribution" plugin (RPCPlugin or a chat-service equivalent) installed to actually sync data to other players. Without it, the menu still works locally but silently fails to sync — look for "Message cannot be distributed to others" in the log. |

AuraPlugin itself isn't published to Thunderstore, so r2modman won't manage it — drop the built DLL (and its `Icons/` folder) into `BepInEx/plugins/AuraPlugin/` in whichever profile you launch with.

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

## Building

Open `AuraPlugin.csproj` — the paths at the top (`TaleSpireDir`, `R2ProfileDir`, `RadialUIDir`, `AssetDataDir`, `SetInjectionFlagDir`) point at a specific machine's Steam install and [r2modman](https://github.com/ebkr/r2modmanPlus) profile/cache; adjust them if yours differ. Requires the .NET Framework 4.8.1 targeting pack (`winget install Microsoft.DotNet.Framework.DeveloperPack_4`) since the project targets `v4.8.1` to match this Unity version's assemblies.

Building via MSBuild automatically copies the output DLL and `Icons/aura.png` into the configured r2modman profile's `BepInEx/plugins/AuraPlugin/` folder (see the `DeployToProfile` target in the csproj) — close TaleSpire first if it's running, since a locked/running instance holds the old DLL open and the copy will fail.

## How it works internally

- **Movement tracking**: TaleSpire doesn't expose a "creature moved" event to plugins, so `AuraRingFollower`/`AuraBubbleFollower` poll the target mini's `transform.position` every frame and reposition accordingly - deliberately not parented to the mini's own transform, so a flying-animation tilt doesn't tip the aura over.
- **Sync/persistence**: handled entirely by AssetDataPlugin. We just call `SetInfo`/`ReadInfo`/`Subscribe` with a handful of string keys per creature — AssetDataPlugin takes care of broadcasting changes to other clients and re-delivering the current values when a board loads.
- **On/off backward compatibility**: before the dedicated Aura On/Off button existed, radius `0` meant "off". `GetAuraEnabled` reconstructs the old visibility for minis configured before this button existed by checking whether their stored radius was ever `> 0`, so upgrading doesn't change any existing table's auras.
- **The "Aura" submenu**: RadialUIPlugin's public API only lets you add buttons to a handful of *existing* native categories (Attacks/Emotes/Status/GM/Kill/Size) — there's no documented "create a brand new branch" method. AuraPlugin instead calls `MapMenuManager.OpenMenu(...)` directly (the same underlying game API RadialUIPlugin's own submenu helper uses) to build its own ring of buttons, all with `FadeName = false` so their labels stay visible without needing to hover.
- **Live button updates**: `MapMenuItem.Setup(...)` — the only public way to change a button's label — also calls `transform.SetAsLastSibling()` internally, which reorders buttons within the radial layout as a side effect. Calling it again just to refresh a number visibly swapped buttons' positions. Instead, `RefreshDisplayedValue` reaches past `Setup()` via reflection to update just the private `_valueText` field and the center text label directly, leaving everything else (including button order) untouched.
- **The bubble shape**: a translucent hemisphere (grounded) or full sphere (flying), built from scratch rather than reusing the game's native Sight-Range-style component (`MapMenuRangeItem`), which is hardwired to the game's own vision system with no generic hook. The full sphere is an **icosphere** (subdivided icosahedron), not a lat/lon UV-sphere — a UV-sphere's pole-convergent triangles alpha-double-blend on a translucent material into a visible banding artifact right at the pole; an icosphere's evenly-distributed triangles have no pole for that to happen around.
- **The icon**: loaded at runtime from `Icons/aura.png` via `Texture2D.LoadImage`/`Sprite.Create` (falls back to the button's plain text label if the file's missing, rather than crashing).
