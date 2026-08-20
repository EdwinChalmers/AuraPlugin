# AuraPlugin — working notes

Last **released** version: **v1.0.5** (Thunderstore). Everything below is built and deployed to
the local r2modman `Talespire` profile but **not released**.

---

## Current state

### Two independent slots

A creature can have an **Aura** (standing) and a **Spell** (cast) active at once, each with its
own on/off, size, colour, shape, dimension, fill and opacity. Each has its own top-level radial
button.

Storage: the Aura slot keeps the ORIGINAL unprefixed keys (`AuraPlugin.Radius`, …) so auras saved
by earlier versions still work; Spell is prefixed (`AuraPlugin.Spell.*`). `ResolveSlotFromKey`
must test the Spell prefix **first**, since it starts with the Aura one.

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
| 8 | Fill | Fill |
| 9 | Aura Color | Spell Color |
| 10 | Show Gridlines | Common… |
| 11 | — | Spell Presets… |

### Shape / Dimension / Fill

Three separate axes: **Shape** is the footprint, **Dimension** (2D/3D) is outline vs solid,
**Fill** is whether the interior is painted. The outline always draws.

| Shape | Slots | 3D form | Height |
|---|---|---|---|
| Circle (`Flat`) | both | sphere | — |
| Cube | both | cube | = size |
| Cone | spell | wedge prism | `SolidShapeHeightFeet` (10) |
| Line | spell | wall prism | `SolidShapeHeightFeet` (10) |
| Cube (Ahead) (`CubeAhead`) | spell | cube | = size |
| Cube (Corner) (`CubeCorner`) | spell | cube | = size |
| Cylinder | spell | cylinder | `CylinderHeightFeet` (40) |
| Wall | spell | wall | `WallHeightFeet` (20) |
| Ring (Wall) (`Ring`) | spell | hollow tube | `WallHeightFeet` (20) |

`Fill` defaults to *unset*, which reproduces the pre-toggle behaviour exactly (3D solid, 2D
outline) so nothing changes appearance on upgrade.

Anchoring: Circle/Cube/Cylinder/Ring are centred on the mini. Cone/Line start at it.
**Cube (Ahead)** puts the cube's near FACE on the mini (5e "originating from you" — Thunderwave).
**Cube (Corner)** puts a corner on it with the diagonal along the facing (free-form; matches no
5e spell). **Wall** is CENTRED so wall sections chain by placing minis on adjacent squares.

### Geometry constraints

Every solid except the sphere and the ring comes from ONE path: `BuildPrismMesh` extrudes the flat
outline straight up — which is why 2D and 3D can never disagree about the area covered. Caps are
**fan-triangulated, so every footprint must be CONVEX**. The ring needed its own builder (outer
wall + inner wall + two annular caps) because an annulus is not.

### Facing

Directional shapes follow the mini's own rotation (the Alt-drag), derived from
**`-CreatureBoardAsset.Rotator.right`** flattened to the ground plane. NOT `transform.forward`
(the root never rotates) and NOT `Rotator.forward` (the Rotator spins about its LOCAL Z, so its
forward points vertically). `MovableBoardAsset.RotateTowards` measures facing against exactly that
vector — **decompile it before changing any of this**; three attempts were wrong before that.

`AuraPlugin.Facing` (per-creature) and `ShapeFacingOffsetDegrees` (table-wide) are added on top.
Both default to 0 and nothing writes the former — it's the hook if manual aiming is ever wanted.

### Presets

Format: `Name:SizeFeet:ColorName:Shape:OpacityPercent[:2D|3D]`.
**`Bubble` must keep being accepted** in the shape field (means Circle + 3D) or every preset
written before the dimension toggle breaks.

- **Spell Presets** (10): Spirit Guardians, Fireball, Darkness, Silence, Thunderwave, Burning
  Hands, Lightning Bolt, Moonbeam, Spike Growth, Wall of Fire Ring.
- **Common** (11): cones 15/30/60, lines 30/60/100, areas 10/15/20/30, 15 ft face-anchored cube.
- **Aura has no presets** — removed deliberately.

### Colours

Gold, Red, Blue, Green, Purple, White, Black — picked from a ring of generated circular swatches
(opaque, mid-grey rim so black and white both stay visible). `ColorRealMaxOverrides` gives a
colour its own opacity ceiling; currently `Black:50` against a table-wide `OpacityRealMaxPercent`
of 20. **The alpha byte in `ColorSteps` does nothing** — it's overwritten by the resolved opacity.

### Rendering

`RebuildRing` is idempotent: each visual records what it was built from (`BuildVisualSpec`) and a
rebuild that wouldn't change the drawing returns early. This exists because AssetDataPlugin
delivers the same change several times (local write, backlog, periodic rebroadcast) and rebuilding
on each read as a flicker. **Anything added to the construction path must be added to that
signature**, or changing it silently won't redraw.

---

## Open

- [ ] **README is far behind the code** — still documents a single aura with a Flat/Bubble toggle.
      No slots, shapes, dimension, fill, or colour picker. Must be fixed **before** any release:
      a published Thunderstore version is permanent and bakes the README in.
- [ ] Per-preset height, so Sleet Storm (20 ft) and Whirlwind (30 ft) aren't stuck on the shared
      40 ft cylinder height. Would be a 7th optional preset field.
- [ ] White may be too faint at the table-wide 20% ceiling — give it a `ColorRealMaxOverrides`
      entry if Moonbeam is hard to read.
- [ ] Radial menus are getting full (11 buttons on Spell). Watch whether it stays usable.
- [ ] **Detached / ground-anchored auras.** Placement is solved:
      `MouseManager.GetLastCursorWorldPosition()` is static + public, with
      `MouseManager.IsHoveringOverUI` to gate clicks. Still unknown: whether AssetDataPlugin
      accepts a non-creature identity string and persists it across a board reload.
      `MapMenu.AddCustomItem` has an `AoeGuid` overload — check whether TaleSpire's native AoE
      objects are drivable from a plugin before building anything bespoke.
- [ ] Orphaned config keys from earlier versions (`AuraPresets`, `WallPresets`,
      `OpacityMaxPercent`, `RadiusScrubFeetPerPixel`, `RadiusStepsFeet`, `BubbleSurfaceAlpha`).
      Harmless — BepInEx doesn't prune keys it isn't asked about — but the file could be tidied.

## Release checklist

- [ ] Fix the README first (see above).
- [ ] Bump the version in **both** `manifest.json` (`version_number`) and the `[BepInPlugin]`
      attribute in `Plugin.cs` — the attribute is compiled in.
- [ ] Rebuild, run `package-local-mod.ps1`, commit, push.
- [ ] `gh release create v<ver>-auraplugin AuraPlugin-<ver>.zip` — publishing triggers the
      Thunderstore upload workflow.
- [ ] Thunderstore strips raw HTML and page-relative anchor links from the README.

## Environment gotchas

- **A running TaleSpire locks the DLL** — the build succeeds, the deploy copy fails (MSB3021).
  Close the game before deploying.
- **BepInEx ignores a compiled default once the key exists in the .cfg.** Any change to an
  *existing* setting must also be written to
  `BepInEx/config/andrew.talespire.auraplugin.cfg`. Edit only the active `Key = value` line, never
  the `# Default value:` comment above it — BepInEx regenerates that itself. New keys are fine.
- Edit the cfg only with the game closed; BepInEx can flush its in-memory copy over the file.
- Build via Bash needs `MSYS_NO_PATHCONV=1` and `-v:minimal`.
