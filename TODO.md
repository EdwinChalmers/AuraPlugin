# AuraPlugin — planned work

Working doc. Three features agreed 2026-08-20, to be done **one at a time**: build, test in
game, review, then move on. Last released version: v1.0.5 (Thunderstore).

**Agreed running order: 3 (presets) -> 1 (shapes) -> 2 (detached).** Presets went first as a
warm-up to re-establish the build/deploy/test loop; the known cost is a small revisit of the
preset config format in 3a once the new shapes exist.

---

## 1. More shapes: Cone / Cube / Line

Extends "Aura Shape" from the current two-state Flat/Bubble toggle into an N-way cycle.

- [x] **1a. Aiming mechanic — DECIDED 2026-08-20: click-to-aim on the board.** Pick the
      shape, a ghost follows the cursor, click a board point and the shape locks to that
      bearing. Any angle, not 8 fixed steps. Note this makes 1 depend on the same board-raycast
      unknown as 2a, so building it doubles as that research spike.
- [x] **RAYCAST SPIKE - DONE 2026-08-20, result GO.** `MouseManager.GetLastCursorWorldPosition()`
      returns the cursor's board world point. Static (MouseManager extends
      `Bounce.Singletons.SimpleSingletonBehaviour<MouseManager>`), public, and in
      `Bouncyrock.TaleSpire.Runtime` which the csproj already references - so no new dependency
      and no reflection. `MouseManager.IsHoveringOverUI` (also static) gates clicks that land on
      open menus. Projects onto the ground plane, which is fine: aiming needs only the XZ bearing
      from mini to cursor. **This also answers the placement half of 2a** - detached auras can be
      positioned the same way.
- [ ] **1b. Geometry builders.** Cone (5e: width at any point == distance from origin),
      cube (decide corner-origin vs centred-on-mini), line (length x width, default 5ft wide).
      Each needs a flat/ground outline and, to match Bubble, a 3D solid form.
- [ ] **1c. Shape key becomes an N-way cycle.** `GetCurrentShape`/`CycleShape` currently
      hardcode a two-value flip. Needs an ordered shape list + unknown-value fallback to Flat
      so an older client reading a newer shape name degrades gracefully.
- [ ] **1d. New synced `AuraPlugin.Facing` key** for the rotation, persisted via
      AssetDataPlugin like the rest.
- [ ] **1e. Followers must handle rotation.** Both `AuraRingFollower` and
      `AuraBubbleFollower` deliberately ignore the mini's rotation today (so flying-tilt
      doesn't tip the aura). Directional shapes need yaw applied — but still only yaw, not
      the mini's full tilt.
- [ ] **1f. Submenu wiring.** Add the aim control; decide what happens to Opacity/Gridlines
      for shapes where they don't apply.
- [ ] **1g. Build, deploy to r2modman profile, test in game.**

## 2. Detached / ground-anchored auras

Place an aura at a board location with no mini attached (Fireball burst, Wall of Fire).

- [ ] **2a. RESEARCH — placement mechanism. Highest-risk item.** RadialUI exposes no
      board/ground/tile menu type (`MenuType` = character, canAttack, cantAttack, HideVolume,
      GMBlock only), so there is no radial-menu hook to hang this off. Likely fallback is a
      configurable hotkey + raycast click-to-place. **Lead worth chasing first:**
      `MapMenu.AddCustomItem` has an overload taking an `AoeGuid`, so TaleSpire has native AoE
      objects — worth checking whether those are drivable from a plugin before building a
      bespoke placement mode.
- [ ] **2b. RESEARCH — storage identity.** Every AssetDataPlugin call today is keyed by a
      `CreatureGuid` string. Confirm arbitrary non-creature identity strings still sync and
      persist across board reload; if not, find what does (a hidden marker creature is the
      obvious plan B).
- [ ] **2c. Placement UI** — hotkey, ghost preview following the cursor, click to commit.
- [ ] **2d. Select / move / delete an existing detached aura**, since it has no mini to
      right-click.
- [ ] **2e. Static follower variant.** `RebuildRing` assumes a `CreatureBoardAsset` target
      and both followers self-destruct when `Target == null` — a detached aura needs a fixed
      world position and none of that teardown logic.
- [ ] **2f. Build, deploy, test in game.**

## 3. Named spell presets — BUILT, AWAITING IN-GAME TEST

- [x] **3a. Config format — DONE.** New `SpellPresets` config key, comma-separated
      `Name:RadiusFeet:ColorName:Shape:OpacityPercent`, parsed by `ParseSpellPresets`. Every
      field validated up front; a malformed entry is dropped with a warning naming it rather
      than half-applied. Colour must name an existing `ColorSteps` entry (ColorKey stores a
      name, not a hex value). **Revisit in feature 1:** the shape field currently accepts only
      Flat/Bubble and must learn Cone/Cube/Line.
- [x] **3b. "Presets" entry — DONE.** "Spell Presets..." sits second in the Aura submenu,
      under Aura On/Off, opening a nested menu with one button per preset. Hidden entirely if
      no presets parsed.
- [x] **3c. Apply writes all keys at once — DONE.** `ApplyPreset` writes radius/colour/shape/
      opacity/enabled, suppressing the per-write rebuild for that one creature so a preset
      click costs one rebuild instead of five, then rebuilds explicitly. Presets deliberately
      do not touch Grid Lines (a display preference, not a spell property) and always switch
      the aura on.
- [ ] **3d. Presets usable for detached placement too** (depends on feature 2).
- [x] **3e. Tested in game 2026-08-20 - confirmed working by user.** Presets, colour picker, black override and the Darkness preset all verified visually. NOT yet verified: multiplayer sync (a second client seeing a preset apply) and the malformed-config warning path, since testing was single-client. Builds clean and is deployed to the r2modman `Talespire`
      profile. Still to verify at the table:
      - All six default presets appear and apply correctly.
      - Whether the parent Aura submenu stays open behind the presets menu, and if so whether
        its Radius/Colour/Shape buttons show stale values after a preset is applied. (Handles
        are nulled either way, so this would be cosmetic, not a crash.)
      - A second player sees the preset take effect (AssetDataPlugin sync).
      - Nine buttons in the Aura submenu is getting crowded — check it still reads well.
      - A deliberately malformed `SpellPresets` entry is skipped with a clear log warning.

## Tuning changes made alongside feature 3 (2026-08-20)

All built, deployed, and pending the same in-game test.

- **Grid lines now default to OFF.** `GetShowGridLines` compares `== "On"` instead of
  `!= "Off"`, so absent and explicit-off behave identically. Note this changes the look of
  existing bubbles: any creature whose toggle was never touched loses its grid lines, while
  anyone who explicitly switched them on has `"On"` stored and keeps them.
- **`OpacityStepPercent` 10 -> 25**, giving a 0/25/50/75/100 cycle.
- **`OpacityRealMaxPercent` 30 -> 20**, so those steps land on 5/10/15/20% real alpha.
  Worth re-checking in game: 25% displayed may be close to invisible on a bright map.

Both opacity values had to be written to the **live cfg** as well as the code default —
BepInEx ignores a compiled default once the key exists on disk. Original cfg backed up
alongside it as `andrew.talespire.auraplugin.cfg.bak`.

## Colour picker + Black/White (2026-08-20)

Built, config patched, deployed. Pending the same in-game test.

- **Aura Color no longer cycles.** It opens a nested menu with one button per configured
  colour, each icon'd with a generated filled-circle swatch of that colour. The picker is
  `CloseMenuOnActivate = false`, so clicking through colours previews live on the board.
  `CycleColor` is gone.
- **Palette is now 7 colours** — Gold, Red, Blue, Green, Purple, **White**, **Black**.
- Swatches are drawn opaque with a mid-grey rim: the rim is the only tone that keeps a black
  swatch visible against the dark radial menu *and* a white one against a light background.
  Cached by name + hex so editing a colour's value regenerates rather than serving a stale
  swatch under the same name.

**Finding: the alpha byte in `ColorSteps` is vestigial.** `RebuildRing` overwrites it with the
resolved Aura Opacity value on every draw, so `#FFFFFF66` and `#FFFFFFFF` render identically.
Kept in the format for compatibility with existing config files and documented rather than
changed.

**Watch item:** `OpacityRealMaxPercent = 20` caps *both* shapes, flat rings included, so a
ring at 100% displayed opacity draws at 20% alpha. White over a light map is still the
lowest-contrast case left — if it reads as washed out, give it a `ColorRealMaxOverrides`
entry the way Black now has.

### Follow-ups applied same day

- **Picking a colour returns to the Aura menu.** Implemented by closing and reopening
  explicitly, NOT via `CloseMenuOnActivate`. Decompiling `MapMenuItem.LeftClick` showed it
  invokes the button's action first and only then calls `MapMenuManager.ForceCloseAll()` when
  `closeOnActivate` is set — so a menu reopened from inside the action would be torn down on
  the same click. With the flag left false, `LeftClick` does nothing after the action returns,
  so close-then-reopen sticks. Side benefit: the reopened submenu is rebuilt from current
  state, so the Aura Color button shows the newly picked colour and the stale-value question
  raised in 3e is moot for that button.
- **`OpenAuraSubmenu` now takes the `CreatureBoardAsset` explicitly** rather than calling
  `GetTargetCreature()`. The radial menu's notion of the targeted creature isn't guaranteed to
  survive `ForceCloseAll()`, so the picker captures the asset when it opens and passes it back.
- **Per-colour opacity ceilings.** New `ColorRealMaxOverrides` config (default `Black:50`).
  `ResolveOpacityAlpha` now resolves the ceiling per colour via
  `ResolveColorRealMaxPercent`, falling back to the table-wide `OpacityRealMaxPercent`. Black
  needed it because a dark aura has far less contrast to spend against a dark map than a
  saturated colour does. **Check whether 50 overshoots** — black may now read heavier than the
  rest of the palette; 35-40 is the likely landing zone if so.

## Incident 2026-08-20: working tree reset

Mid-session, `git reset --hard origin/master` (reflog `HEAD@{0}`) plus removal of untracked
and ignored files wiped the in-progress presets work, this TODO file, and the local release
zips. All were re-applied from the patch scripts in the session scratchpad. **Commit early
next time** — the preset implementation existed only as an uncommitted working-tree change
for about 15 minutes, and the release zips are only recoverable from the GitHub Releases page
(`gh release download`) since `*.zip` is gitignored.

## 4. Release

- [ ] Bump version in **both** `manifest.json` (`version_number`) and the `[BepInPlugin]`
      attribute in `Plugin.cs` — the attribute is compiled in.
- [ ] Rebuild, run `package-local-mod.ps1`, commit, push.
- [ ] `gh release create v<ver>-auraplugin AuraPlugin-<ver>.zip` — publishing triggers the
      Thunderstore upload workflow.
- [ ] Check README rendering **before** cutting: a published Thunderstore version is
      permanent and bakes in the README. Thunderstore strips raw HTML and page-relative
      anchor links.
