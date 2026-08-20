# AuraPlugin — working notes

Last released version: **v1.0.5** (Thunderstore). Everything below is built and deployed to the
local r2modman `Talespire` profile but **not yet released, and not yet committed**.

---

## Current state

### Two independent slots

A creature can have an **Aura** and a **Spell** active at once, each with its own size, colour,
shape, dimension and opacity. Both are reached from their own top-level radial button.

Storage keys: the Aura slot keeps the ORIGINAL unprefixed names (`AuraPlugin.Radius`, …) so
auras saved by earlier versions still work; the Spell slot is prefixed (`AuraPlugin.Spell.*`).
`ResolveSlotFromKey` must test the Spell prefix first, since it starts with the Aura one.

### Menu layout (insertion order == on-screen order)

| # | Aura | Spell |
|---|---|---|
| 1 | Aura On/Off | Spell On/Off |
| 2 | Toggle Radius | Toggle Size |
| 3 | Toggle Opacity | Toggle Opacity |
| 4 | Type Radius | Type Size |
| 5 | Type Opacity | Type Opacity |
| 6 | Aura Shape | Spell Shape |
| 7 | Aura Dimension | Spell Dimension |
| 8 | Aura Color | Spell Color |
| 9 | Show Gridlines | Common… |
| 10 | — | Spell Presets… |

### Shapes and dimension

Shape is the footprint; Dimension (2D/3D) is whether it's an outline or a solid.

| Shape | Slots | 2D | 3D |
|---|---|---|---|
| Circle (`Flat`) | both | ring | sphere |
| Cube | both | square | cube (height = side) |
| Cone | spell | 5e sector, ~53 deg | wedge prism |
| Line | spell | rectangle | wall prism |
| Cube (Ahead) (`CubeAhead`) | spell | near face on mini | cube |
| Cube (Corner) (`CubeCorner`) | spell | corner on mini, diagonal along facing | cube |
| Cylinder | spell | ring | cylinder (height = `CylinderHeightFeet`) |

3D solids other than the sphere all come from ONE path: `BuildPrismMesh` extrudes the flat
outline straight up. That's why 2D and 3D can never disagree about the area covered. Cap
triangulation is a simple fan, valid only because every footprint is **convex** — a concave
shape added later needs a real triangulator.

Heights: cubes use their own size; cylinders use `CylinderHeightFeet` (40, matching Moonbeam /
Flame Strike / Ice Storm); cone and line use `SolidShapeHeightFeet` (10).

### Facing

Directional shapes follow the mini's own rotation — the Alt-drag. Derived from
**`-CreatureBoardAsset.Rotator.right`** flattened to the ground plane. NOT `transform.forward`
(the root never rotates) and NOT `Rotator.forward` (the Rotator spins about its LOCAL Z, so its
forward points vertically). `MovableBoardAsset.RotateTowards` measures facing against exactly
that vector — decompile it before changing any of this.

`AuraPlugin.Facing` is a per-creature offset added on top, and `ShapeFacingOffsetDegrees` a
table-wide one. Both default to 0 and nothing writes the former yet — it's the hook if a manual
aim control is ever wanted.

### Presets

- **Spell Presets** — Spirit Guardians, Fireball, Darkness, Silence, Thunderwave, Burning
  Hands, Lightning Bolt, Moonbeam.
- **Common** — 11 generic templates: cones 15/30/60, lines 30/60/100, areas 10/15/20/30, and a
  single 15 ft face-anchored cube.
- **Aura has no presets** — removed deliberately; a standing aura is a couple of clicks.

Format: `Name:SizeFeet:ColorName:Shape:OpacityPercent[:2D|3D]`. `Bubble` is still accepted in
the shape field and means Circle + 3D — removing that would break every preset written before
the dimension toggle existed.

### Colours

Gold, Red, Blue, Green, Purple, White, Black. Picked from a ring of generated circular swatches
(drawn opaque with a mid-grey rim so black and white both stay visible). `ColorRealMaxOverrides`
gives a colour its own opacity ceiling — currently `Black:50` against a table-wide 20.

**The alpha byte in `ColorSteps` does nothing** — `RebuildRing` overwrites it with the resolved
opacity. Kept only for config compatibility.

### Rendering

`RebuildRing` is idempotent: each visual records the settings it was built from
(`BuildVisualSpec`) and a rebuild that wouldn't change the drawing returns early. This exists
because AssetDataPlugin delivers the same change several times — local write, backlog, periodic
rebroadcast — and rebuilding on each read as a flicker. **Anything added to the construction
path must be added to that signature**, or changing it won't redraw.

---

## Open / possible next

- [ ] **COMMIT THIS.** ~900 lines uncommitted on `master`. A `git reset --hard` already
      destroyed this work once on 2026-08-20; it was only recoverable because the patch scripts
      happened to still be in the session scratchpad.
- [ ] Per-preset height, so Sleet Storm (20 ft) and Whirlwind (30 ft) aren't forced to the
      shared 40 ft cylinder height. Would be a 7th optional preset field.
- [ ] White may be too faint at the 20% table-wide ceiling — give it a `ColorRealMaxOverrides`
      entry if Moonbeam is hard to see.
- [ ] Detached / ground-anchored auras (the old feature 2). The placement half is solved:
      `MouseManager.GetLastCursorWorldPosition()` is static and public, with
      `MouseManager.IsHoveringOverUI` to gate clicks. Still unknown: whether AssetDataPlugin
      accepts a non-creature identity string and persists it across a board reload.
      `MapMenu.AddCustomItem` has an `AoeGuid` overload — worth checking whether TaleSpire's
      native AoE objects are drivable from a plugin before building anything bespoke.
- [ ] Orphaned config keys left by earlier versions (`AuraPresets`, `OpacityMaxPercent`,
      `RadiusScrubFeetPerPixel`, `RadiusStepsFeet`, `BubbleSurfaceAlpha`). Harmless — BepInEx
      doesn't prune keys it isn't asked about — but the file could be tidied by hand.

## Release checklist

- [ ] Bump the version in **both** `manifest.json` (`version_number`) and the `[BepInPlugin]`
      attribute in `Plugin.cs` — the attribute is compiled in.
- [ ] Rebuild, run `package-local-mod.ps1`, commit, push.
- [ ] `gh release create v<ver>-auraplugin AuraPlugin-<ver>.zip` — publishing triggers the
      Thunderstore upload workflow.
- [ ] Check README rendering **before** cutting: a published Thunderstore version is permanent
      and bakes the README in. Thunderstore strips raw HTML and page-relative anchor links.
- [ ] README is currently well behind the code — it still documents a single aura with a
      Flat/Bubble shape toggle, and none of the slots, shapes, dimension or colour picker.

## Environment gotchas

- **A running TaleSpire locks the DLL** — the build succeeds but the deploy copy fails. Close
  the game before deploying.
- **BepInEx ignores a compiled default once the key exists in the .cfg.** Any change to an
  existing setting must be written to
  `BepInEx/config/andrew.talespire.auraplugin.cfg` as well as to the source. Edit only the
  active `Key = value` line, never the `# Default value:` comment above it — BepInEx regenerates
  that itself on next launch.
- Edit the cfg only with the game closed; BepInEx can flush its in-memory copy over the file.
