# AuraPlugin

A [TaleSpire](https://talespire.com/) [BepInEx](https://github.com/BepInEx/BepInEx) mod that draws a colored radius ring around a mini (Paladin aura, Spirit Guardians, torch light, or anything else with a fixed area) and keeps it centered on the mini as it's dragged around the board. Synced and persisted for every player at the table via [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/).

## Using it in-game

Right-click a mini and choose **Aura** from the radial menu. This opens a submenu with:

- **Aura Radius** — click to step the radius up by 5ft (configurable), wrapping back to "Off" once it passes the configured max. The current value is shown right on the button.
- **Aura Color** — click to cycle through the configured color list, same as above.
- **Type Exact Radius...** — opens a small text box to type an exact number instead of clicking through steps.

Setting a radius of 0 (or cycling back around to it) removes the aura.

## Requirements

Install these three via r2modman before AuraPlugin will load:

| Dependency | Notes |
|---|---|
| [RadialUIPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RadialUIPlugin/) | Provides the radial menu hooks. Pulls in `SetInjectionFlagPlugin` itself. |
| [AssetDataPlugin](https://thunderstore.io/c/talespire/p/LordAshes/AssetDataPlugin/) | **Must be 3.6.2 or newer.** Older versions crash on current TaleSpire builds — `CreatureGuid` moved to a different assembly in a game update and old plugin builds still reference the old one. |
| [RPCPlugin](https://thunderstore.io/c/talespire/p/HolloFox_TS/RPCPlugin/) | AssetDataPlugin needs a "message distribution" plugin (RPCPlugin or a chat-service equivalent) installed to actually sync data to other players. Without it, the menu still works locally but silently fails to sync. |

AuraPlugin itself isn't published to Thunderstore, so r2modman won't manage it — drop the built DLL into `BepInEx/plugins/AuraPlugin/` in whichever profile you launch with.

## Configuration

After first launch, a config file appears at `BepInEx/config/andrew.talespire.auraplugin.cfg`:

- `RadiusStepFeet` (default `5`) — how much each click on Aura Radius adds.
- `RadiusMaxFeet` (default `60`) — radius wraps back to 0 past this.
- `FeetPerTile` (default `5`) — match this to your table's ruler scale; it's how radius-in-feet gets converted to the board's own grid units.
- `ColorSteps` (default `Gold:#FFD70066,Red:#FF000066,Blue:#1E90FF66,Green:#32CD3266,Purple:#9370DB66`) — the color cycle, as `Name:RRGGBBAA` pairs.
- `RingHeightAboveBase` / `RingLineWidth` — cosmetic tweaks to how the ring itself is drawn.

## Building

Open `AuraPlugin.csproj` — the paths at the top (`TaleSpireDir`, `R2ProfileDir`, `RadialUIDir`, `AssetDataDir`, `SetInjectionFlagDir`) point at a specific machine's Steam install and r2modman profile/cache; adjust them if yours differ. Requires the .NET Framework 4.8.1 targeting pack (`winget install Microsoft.DotNet.Framework.DeveloperPack_4`) since the project targets `v4.8.1` to match this Unity version's assemblies.

Building via MSBuild automatically copies the output DLL into the configured r2modman profile's `BepInEx/plugins/AuraPlugin/` folder (see the `DeployToProfile` target in the csproj) — close TaleSpire first if it's running, since a locked/running instance holds the old DLL open and the copy will fail.

## How it works internally

- **Movement tracking**: TaleSpire doesn't expose a "creature moved" event to plugins, so `AuraRingFollower` polls the target mini's `transform.position` every frame and repositions the ring's `LineRenderer` accordingly.
- **Sync/persistence**: handled entirely by AssetDataPlugin. We just call `SetInfo`/`ReadInfo`/`Subscribe` with a couple of string keys per creature — AssetDataPlugin takes care of broadcasting changes to other clients and re-delivering the current values when a board loads.
- **The "Aura" submenu**: RadialUIPlugin's public API only lets you add buttons to a handful of *existing* native categories (Attacks/Emotes/Status/GM/Kill/Size) — there's no documented "create a brand new branch" method. AuraPlugin instead calls `MapMenuManager.OpenMenu(...)` directly (the same underlying game API RadialUIPlugin's own submenu helper uses) to build its own ring of buttons.
- **Live button updates**: `MapMenuItem.Setup(...)` — the only public way to change a button's label — also calls `transform.SetAsLastSibling()` internally, which reorders buttons within the radial layout as a side effect. Calling it again just to refresh a number visibly swapped the Aura Radius/Aura Color buttons' positions. Instead, `RefreshDisplayedValue` reaches past `Setup()` via reflection to update just the private `_valueText` field and the center text label directly, leaving everything else (including button order) untouched.
