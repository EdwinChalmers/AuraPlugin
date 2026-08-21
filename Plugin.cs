using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Bounce.Unmanaged;
using LordAshes;
using RadialUI;
using UnityEngine;

namespace AuraPlugin
{
    // Draws a colored radius ring around a mini (Paladin aura, Spirit Guardians, etc.)
    // that follows it as it moves. Right-click a mini -> "Aura" opens a submenu with
    // the radius/color controls.
    //
    // Networking/persistence is handled entirely by AssetDataPlugin: we just store a
    // couple of key/value strings per creature, and AssetDataPlugin takes care of
    // syncing them to other players and reloading them when the board loads. Movement
    // tracking is our own responsibility, since TaleSpire doesn't expose a "mini moved"
    // event to hook - see AuraShapeFollower at the bottom of this file.
    // Keep this version string in sync with manifest.json's version_number - it's what shows
    // up in the BepInEx log and Config Manager when someone reports a bug.
    [BepInPlugin(Guid, "AuraPlugin", "1.0.5")]
    [BepInDependency("org.hollofox.plugins.RadialUIPlugin")]
    [BepInDependency("org.lordashes.plugins.assetdata")]
    public class AuraPlugin : BaseUnityPlugin
    {
        public const string Guid = "andrew.talespire.auraplugin";

        // One independent aura a creature can have. Two exist - the standing "Aura" and the
        // cast "Spell" - and a creature can have both switched on at once, each with its own
        // radius, colour, shape and opacity. Everything downstream of here is written against a
        // slot rather than against fixed key names, so the two behave identically.
        //
        // All keys are prefixed with our plugin name so the Subscribe("AuraPlugin.*") wildcard
        // only ever sees our own data, not some other plugin's.
        private sealed class AuraSlot
        {
            public readonly string Name;
            public readonly string KeyPrefix;
            public readonly string EnabledKey;
            public readonly string RadiusKey;
            public readonly string ColorKey;
            public readonly string ShapeKey;
            public readonly string OpacityKey;
            public readonly string GridLinesKey;
            public readonly string FacingKey;
            public readonly string DimensionKey;
            public readonly string FillKey;
            public readonly string HeightKey;

            // Which shapes this slot offers. A standing aura is a plain area around the
            // creature, so it gets the two centred shapes; directional templates only make
            // sense for a cast spell, where you're pointing them at something.
            public readonly string[] Shapes;

            // "Radius" reads wrong for a spell whose shape might be a cone or a cube, where the
            // number is a length or a side rather than a radius. "Size" covers all of them.
            public readonly string SizeLabel;

            // Grid lines only ever draw on the 3D sphere, so they're not worth a menu slot
            // everywhere - the Aura is the one left switched on long enough to want them.
            public readonly bool AllowGridLines;

            // Whether this slot shows the generic template lists (Common, Walls). They're all
            // built from shapes only the Spell slot offers, so only it gets them.
            public readonly bool AllowTemplateLists;

            // Whether to offer a height control. Only the Wall, Ring and Cylinder shapes have a
            // height that isn't already implied - a cube's height is its own size, and the
            // circle's 3D form is a sphere - and all three are Spell-only shapes.
            public readonly bool AllowHeight;

            public AuraSlot(string name, string keyPrefix, string[] shapes, string sizeLabel,
                bool allowGridLines, bool allowTemplateLists)
            {
                AllowTemplateLists = allowTemplateLists;
                AllowHeight = allowTemplateLists;
                Name = name;
                KeyPrefix = keyPrefix;
                Shapes = shapes;
                SizeLabel = sizeLabel;
                AllowGridLines = allowGridLines;
                DimensionKey = keyPrefix + "Dimension";
                FillKey = keyPrefix + "Fill";
                HeightKey = keyPrefix + "Height";
                EnabledKey = keyPrefix + "Enabled";
                RadiusKey = keyPrefix + "Radius";
                ColorKey = keyPrefix + "Color";
                ShapeKey = keyPrefix + "Shape";
                OpacityKey = keyPrefix + "Opacity";
                GridLinesKey = keyPrefix + "GridLines";
                FacingKey = keyPrefix + "Facing";
            }
        }

        // The Aura slot deliberately keeps the original UNPREFIXED key names ("AuraPlugin.Radius"
        // and friends) so auras saved by earlier versions keep working untouched; only the new
        // Spell slot carries an extra prefix. That also means SlotSpell's prefix starts with
        // SlotAura's, which is why ResolveSlotFromKey has to test the longer one first.
        private static readonly AuraSlot SlotAura = new AuraSlot("Aura", "AuraPlugin.",
            new[] { ShapeFlat, ShapeCube }, "Radius", true, false);
        private static readonly AuraSlot SlotSpell = new AuraSlot("Spell", "AuraPlugin.Spell.",
            new[] { ShapeFlat, ShapeCone, ShapeLine, ShapeCube, ShapeCubeAhead, ShapeCubeCorner, ShapeCylinder, ShapeWall, ShapeRing },
            "Size", false, true);

        private static AuraSlot ResolveSlotFromKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.StartsWith(SlotSpell.KeyPrefix, StringComparison.Ordinal)) return SlotSpell;
            if (key.StartsWith(SlotAura.KeyPrefix, StringComparison.Ordinal)) return SlotAura;
            return null;
        }

        // Identifies one drawn visual: a creature can now own two at once, so the creature id
        // alone is no longer unique.
        private static string VisualKey(string identity, AuraSlot slot)
        {
            return identity + "|" + slot.Name;
        }

        private const string ShapeFlat = "Flat";
        // No longer a selectable shape - "solid or not" is the Dimension toggle now. The constant
        // survives purely so stored data and preset configs written when Bubble WAS a shape keep
        // working: both are migrated to Circle + 3D on read. Never put it back in a slot's
        // Shapes list.
        private const string ShapeBubble = "Bubble";

        private const string DimensionTwo = "2D";
        private const string DimensionThree = "3D";
        private const string ShapeCone = "Cone";
        private const string ShapeLine = "Line";
        private const string ShapeCube = "Cube";
        private const string ShapeCubeCorner = "CubeCorner";
        private const string ShapeCubeAhead = "CubeAhead";
        private const string ShapeCylinder = "Cylinder";
        private const string ShapeWall = "Wall";
        private const string ShapeRing = "Ring";

        // One entry per selectable shape. Key is what gets stored in AssetDataPlugin and must
        // stay stable forever - it's written into saved boards and synced to other clients -
        // whereas DisplayName is free to change. NeedsAiming flags the shapes that point
        // somewhere, so the menu can show an aim control only where it means something.
        private struct AuraShapeInfo
        {
            public readonly string Key;
            public readonly string DisplayName;
            public readonly bool NeedsAiming;

            public AuraShapeInfo(string key, string displayName, bool needsAiming)
            {
                Key = key;
                DisplayName = displayName;
                NeedsAiming = needsAiming;
            }
        }

        private static readonly List<AuraShapeInfo> ShapeRegistry = new List<AuraShapeInfo>
        {
            // Key stays "Flat" while the label reads "Circle": the key is written into saved
            // boards and synced to other clients, so renaming it would orphan every existing
            // aura. Only the label is safe to change.
            new AuraShapeInfo(ShapeFlat, "Circle", false),
            new AuraShapeInfo(ShapeCone, "Cone", true),
            new AuraShapeInfo(ShapeLine, "Line", true),
            new AuraShapeInfo(ShapeCube, "Cube", false),
            new AuraShapeInfo(ShapeCubeAhead, "Cube (Ahead)", true),
            new AuraShapeInfo(ShapeCubeCorner, "Cube (Corner)", true),
            // Centred on the mini, so nothing to aim.
            new AuraShapeInfo(ShapeCylinder, "Cylinder", false),
            new AuraShapeInfo(ShapeWall, "Wall", true),
            // Rotationally symmetric, so nothing to aim.
            new AuraShapeInfo(ShapeRing, "Ring (Wall)", false)
        };
        private const string ToggleOn = "On";
        private const string ToggleOff = "Off";

        private ConfigEntry<float> radiusStepFeetConfig;
        private ConfigEntry<float> radiusMaxFeetConfig;
        private ConfigEntry<float> feetPerTileConfig;
        private ConfigEntry<string> colorPresetsConfig;
        private ConfigEntry<float> ringHeightConfig;
        private ConfigEntry<float> ringWidthConfig;
        private ConfigEntry<float> lineShapeWidthFeetConfig;
        private ConfigEntry<float> shapeFacingOffsetConfig;
        private ConfigEntry<float> prismHeightFeetConfig;
        private ConfigEntry<float> coneApexHeightFeetConfig;
        private ConfigEntry<float> cylinderHeightFeetConfig;
        private ConfigEntry<float> wallThicknessFeetConfig;
        private ConfigEntry<float> wallHeightFeetConfig;
        private ConfigEntry<float> bubbleGridAlphaConfig;
        private ConfigEntry<int> bubbleGridRingCountConfig;
        private ConfigEntry<int> bubbleGridMeridianCountConfig;
        private ConfigEntry<float> bubbleGridLineWidthConfig;
        private ConfigEntry<float> opacityStepPercentConfig;
        private ConfigEntry<float> opacityRealMaxPercentConfig;
        private ConfigEntry<string> spellPresetsConfig;
        private ConfigEntry<string> commonPresetsConfig;
        private ConfigEntry<string> colorRealMaxOverridesConfig;

        private List<(string Name, Color Value)> colorSteps;

        // A named one-click combination of the settings the individual buttons already set
        // separately - "Fireball" is just radius 20 / red / bubble / 100%. Deliberately does
        // NOT carry a grid-lines value: that's a per-player display preference rather than a
        // property of the spell, so applying a preset leaves whatever the creature already had.
        private struct SpellPreset
        {
            public string Name;
            public float RadiusFeet;
            public string ColorName;
            public string Shape;
            public string Dimension;
            public float OpacityPercent;
        }

        // Two separate lists, surfaced in two different places: standing character auras
        // (Paladin's aura and friends) live under the Aura menu, cast spells under their own
        // top-level Spells button. Same record shape, same parser - only the menu differs.
        private List<SpellPreset> spellPresets;

        // Unnamed geometry rather than named spells - "30 ft Cone" instead of "Burning Hands".
        // Covers the case where you know the template you need but the spell isn't one of the
        // presets, which is most of them.
        private List<SpellPreset> commonPresets;


        // The Aura slot has no preset list of its own - a standing aura is a couple of clicks to
        // set up and doesn't warrant one. GetPresetsFor hands this back for it, and the menu
        // omits the button entirely when a slot's list is empty.
        private static readonly List<SpellPreset> NoPresets = new List<SpellPreset>();

        // Generated colour-swatch icons for the Aura Color picker, keyed by name AND hex so
        // that editing a colour's value in the config produces a fresh swatch rather than
        // serving a stale one that still shows the old colour under the same name.
        private readonly Dictionary<string, Sprite> colorSwatchCache = new Dictionary<string, Sprite>();

        // Colour name -> its own OpacityRealMaxPercent, for colours that need a different
        // ceiling from the table-wide one. Black is the motivating case: at the global 20% it
        // reads as a faint grey smudge rather than a black aura, because a dark colour has far
        // less contrast to spend against a dark map than a saturated one like Gold or Red.
        private Dictionary<string, float> colorRealMaxOverrides;

        // One visual GameObject per creature that currently has an aura switched on - either
        // a flat outline, a filled area, an extruded solid or a sphere - never more than one per slot.
        private readonly Dictionary<string, GameObject> activeRings = new Dictionary<string, GameObject>();

        // The settings each entry in activeRings was actually built from, so a rebuild request
        // that wouldn't change the drawing can be skipped instead of destroying and recreating
        // an identical visual.
        //
        // This matters because AssetDataPlugin can deliver the same change more than once - the
        // local write, then again through its backlog and its periodic rebroadcast - and every
        // delivery used to tear the aura down and build it again. Spread over a few frames that
        // reads as a visible flicker right after picking a shape or colour.
        private readonly Dictionary<string, string> activeSpecs = new Dictionary<string, string>();

        // Everything that affects what gets drawn, flattened into a comparable string. Anything
        // NOT in here must be something the follower reads live each frame (position, the mini's
        // own rotation, visibility) rather than something baked in at construction - otherwise
        // changing it would silently fail to redraw.
        private string BuildVisualSpec(string identity, AuraSlot slot)
        {
            if (!GetAuraEnabled(identity, slot)) return "off";

            return string.Join("|", new[]
            {
                GetCurrentShape(identity, slot),
                GetCurrentDimension(identity, slot),
                GetCurrentRadiusFeet(identity, slot).ToString(CultureInfo.InvariantCulture),
                ResolveColorName(identity, slot),
                ResolveOpacityAlpha(identity, slot).ToString(CultureInfo.InvariantCulture),
                GetShowGridLines(identity, slot) ? "grid" : "nogrid",
                GetFillEnabled(identity, slot) ? "fill" : "outline",
                GetCurrentHeightFeet(identity, slot).ToString(CultureInfo.InvariantCulture),
                GetCurrentFacing(identity, slot).ToString(CultureInfo.InvariantCulture)
            });
        }

        // Handles into the currently-open "Aura" submenu's buttons, so a click can update
        // the displayed number/color/shape in place without needing to close and reopen the menu.
        private MapMenuItem openEnabledItem;
        private MapMenuItem openRadiusItem;
        private MapMenuItem openColorItem;
        private MapMenuItem openShapeItem;
        private MapMenuItem openOpacityItem;
        private MapMenuItem openGridLinesItem;
        private MapMenuItem openDimensionItem;
        private MapMenuItem openFillItem;
        private MapMenuItem openHeightItem;
        private string openSubmenuIdentity;
        private AuraSlot openSubmenuSlot;

        // Applying a preset writes several AssetDataPlugin keys in a row, and each write
        // independently fires OnAuraDataChanged -> RebuildRing. For a bubble that means tearing
        // down and rebuilding ~15 GameObjects five times over for a single click. This holds the
        // identity currently being bulk-written so those intermediate rebuilds can be skipped,
        // leaving ApplyPreset to do one explicit rebuild at the end instead.
        //
        // Scoped to a single identity rather than being a blanket on/off flag: a remote change
        // for some OTHER creature arriving mid-write must still be honoured or it would be
        // dropped for good. A remote change for THIS creature is safe to skip, because the
        // explicit rebuild re-reads current state anyway.
        private string suppressRebuildForVisual;

        // Which value a typed-number box is currently editing - shared by "Type Exact
        // Radius..." and "Type Exact Opacity...", rather than duplicating the whole
        // OnGUI text box for each.
        private enum CustomInputField { Radius, Opacity }

        // On-screen text box state for typing an exact value instead of clicking through
        // the step buttons. Drawn via OnGUI (Unity's old immediate-mode UI) - simple to do
        // without needing a Canvas/EventSystem set up just for one text field.
        private bool showCustomInput;
        private CustomInputField customInputField;
        private string customInputText = "";
        private string customInputTargetIdentity;
        private AuraSlot customInputSlot;

        // MapMenuItem.Setup(...) - the only *public* way to change a button's text - also
        // calls transform.SetAsLastSibling() internally (confirmed by decompiling the game's
        // MapMenuItem class). The radial menu positions buttons by sibling order, so calling
        // Setup() again to "just update the label" actually swaps the two buttons' on-screen
        // positions as a side effect. To update the label live without that side effect, we
        // reach past Setup() and poke the private fields TextMeshPro text directly instead.
        // This is the same style of trick RadialUIPlugin's own Talespire.RadialMenus helper
        // uses for private game fields, so it's an established pattern in this modding scene,
        // not a one-off hack.
        private static readonly FieldInfo ValueTextField =
            typeof(MapMenuItem).GetField("_valueText", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo CircleTextField =
            typeof(MapMenuItem).GetField("_circleContetText", BindingFlags.NonPublic | BindingFlags.Instance);

        private void Awake()
        {
            radiusStepFeetConfig = Config.Bind("Presets", "RadiusStepFeet", 5f,
                "How much each click on the Aura Radius button adds.");
            radiusMaxFeetConfig = Config.Bind("Presets", "RadiusMaxFeet", 60f,
                "Radius wraps back to the smallest step after exceeding this - use the Aura On/Off button to actually switch the aura off, not this.");
            feetPerTileConfig = Config.Bind("Presets", "FeetPerTile", 5f,
                "Feet represented by one board tile/grid square. Match your table's ruler scale.");
            colorPresetsConfig = Config.Bind("Presets", "ColorSteps", "Gold:#FFD70066,Red:#FF000066,Blue:#1E90FF66,Green:#32CD3266,Purple:#9370DB66,White:#FFFFFF66,Black:#00000066",
                "Aura colours offered by the Aura Color picker, as Name:RRGGBBAA pairs, comma separated. " +
                "The alpha byte here is effectively vestigial - RebuildRing overwrites it with the resolved Aura Opacity value - but it's kept in the format so older config files stay valid.");
            ringHeightConfig = Config.Bind("Visual", "RingHeightAboveBase", 0.05f, "How far above the tabletop the ring floats, in board units.");
            ringWidthConfig = Config.Bind("Visual", "RingLineWidth", 0.05f, "Thickness of the aura ring line, in board units.");
            lineShapeWidthFeetConfig = Config.Bind("Presets", "LineShapeWidthFeet", 5f,
                "Width of the Line shape, in feet. The Aura Radius value sets its length.");
            wallThicknessFeetConfig = Config.Bind("Presets", "WallThicknessFeet", 1f,
                "Thickness of the Wall shape, in feet. The Size value sets its length. Separate from " +
                "LineShapeWidthFeet so a 1ft-thick wall and a 5ft-wide spell line can coexist.");
            wallHeightFeetConfig = Config.Bind("Presets", "WallHeightFeet", 20f,
                "Height of a 3D Wall shape, in feet. Wall of Fire, Wall of Force and friends are all 20ft high.");
            cylinderHeightFeetConfig = Config.Bind("Presets", "CylinderHeightFeet", 40f,
                "Height of a 3D Cylinder shape, in feet. Separate from SolidShapeHeightFeet because a " +
                "cylinder spell's height is usually called out explicitly by the spell.");
            coneApexHeightFeetConfig = Config.Bind("Presets", "ConeApexHeightFeet", 2.5f,
                "How high above the tabletop a 3D cone's point sits, in feet. Roughly chest height on a " +
                "medium mini by default, so a breath weapon comes out of the creature rather than off the floor.");
            prismHeightFeetConfig = Config.Bind("Presets", "SolidShapeHeightFeet", 10f,
                "Height of a 3D cone/line shape, in feet. Cubes ignore this - a cube's height is its own " +
                "size, or it wouldn't be a cube.");
            shapeFacingOffsetConfig = Config.Bind("Visual", "ShapeFacingOffsetDegrees", 0f,
                "Degrees added to every directional shape's facing. The shapes follow the mini's own rotation " +
                "(the one you change by holding Alt), so leave this at 0 unless your minis' models point at a " +
                "consistent angle away from the direction their base markings indicate - then dial in the difference here.");
            bubbleGridAlphaConfig = Config.Bind("Visual", "BubbleGridAlpha", 0.45f, "Transparency of the bubble's latitude/longitude grid lines.");
            bubbleGridRingCountConfig = Config.Bind("Visual", "BubbleGridRingCount", 2, "Number of latitude rings drawn on the bubble between the equator and the pole.");
            bubbleGridMeridianCountConfig = Config.Bind("Visual", "BubbleGridMeridianCount", 6, "Number of longitude arcs drawn over the top of the bubble.");
            bubbleGridLineWidthConfig = Config.Bind("Visual", "BubbleGridLineWidth", 0.015f,
                "Thickness of the bubble's equator/grid lines, in the bubble's own local (unit-radius) space - scales up with the radius, same as the real line's proportions in the reference screenshot.");
            opacityStepPercentConfig = Config.Bind("Presets", "OpacityStepPercent", 25f,
                "How much each click on the Aura Opacity button adds, on the displayed 0-100 scale.");
            opacityRealMaxPercentConfig = Config.Bind("Visual", "OpacityRealMaxPercent", 20f,
                "The Aura Opacity button always displays 0-100%, but that's a rescaled range: this is the actual surface alpha percent applied when the display reads 100%. " +
                "E.g. the default 20 means displayed 100% = 20% real alpha, displayed 50% = 10% real alpha, and so on - a linear rescale, not a cap.");

            spellPresetsConfig = Config.Bind("Presets", "SpellPresets",
                "Spirit Guardians:15:Blue:Flat:100,Fireball:20:Red:Bubble:100,Darkness:15:Black:Bubble:100,Silence:20:Blue:Bubble:100,Thunderwave:15:Blue:CubeAhead:100,Burning Hands:15:Red:Cone:100,Lightning Bolt:100:Blue:Line:100,Moonbeam:5:White:Cylinder:100:3D,Spike Growth:20:Green:Flat:100,Wall of Fire Ring:10:Red:Ring:100:3D",
                "One-click spell presets, comma separated, each as Name:RadiusFeet:ColorName:Shape:OpacityPercent. " +
                "ColorName must be one of the names defined in ColorSteps above. Shape is one of Flat, Cone, Line, Cube, " +
                "CubeAhead, CubeCorner or Cylinder (Bubble is still accepted, and means Flat drawn in 3D). " +
                "An optional sixth field, 2D or 3D, sets whether it is drawn as an outline or a solid. " +
                "Entries not matching that form are skipped with a warning in the log rather than silently applying something unintended.");

            colorRealMaxOverridesConfig = Config.Bind("Visual", "ColorRealMaxOverrides", "Black:50",
                "Per-colour overrides for OpacityRealMaxPercent, as Name:Percent pairs, comma separated. " +
                "A colour listed here uses its own ceiling instead of the table-wide OpacityRealMaxPercent; " +
                "anything not listed falls back to that value. Leave empty for no overrides.");

            commonPresetsConfig = Config.Bind("Presets", "CommonPresets",
                "15 ft Cone:15:Gold:Cone:100,30 ft Cone:30:Gold:Cone:100,60 ft Cone:60:Gold:Cone:100," +
                "30 ft Line:30:Gold:Line:100,60 ft Line:60:Gold:Line:100,100 ft Line:100:Gold:Line:100," +
                "10 ft Area:10:Gold:Flat:100,15 ft Area:15:Gold:Flat:100,20 ft Area:20:Gold:Flat:100,30 ft Area:30:Gold:Flat:100," +
                "15 ft Cube:15:Gold:CubeAhead:100",
                "Generic size/shape templates listed under the Spell menu's \"Common...\" button, for spells that " +
                "aren't in SpellPresets. Same format as SpellPresets: Name:SizeFeet:ColorName:Shape:OpacityPercent[:2D|3D].");

            ParsePresets();
            ParseColorRealMaxOverrides();
            // After ParsePresets, not before - preset validation rejects any preset naming a
            // colour that ColorSteps doesn't define, so colorSteps has to be populated first.
            spellPresets = ParsePresetList(spellPresetsConfig.Value, "spell preset");
            commonPresets = ParsePresetList(commonPresetsConfig.Value, "common template");

            Sprite auraIcon = LoadIcon("aura.png");

            // Single top-level "Aura" entry on the character radial menu. Its Action opens
            // our own submenu (see OpenSlotSubmenu) rather than doing anything itself - this
            // is what groups all the aura controls under one branch instead of cluttering the
            // main right-click menu, the same way the native "Status"/"Emotes" buttons work.
            //
            // FadeName true (the default) means the "Aura" label fades out unless hovered,
            // matching every native top-level button - but MapMenuItem drives the label's alpha
            // to 0 when it's set, so with no icon that would leave a completely blank button.
            // Tie it to the icon actually having loaded: normally hover-only like the natives,
            // but permanently labelled on the fallback path so the button is still identifiable.
            // (The submenu buttons below all pass false deliberately - they need their live
            // values readable without hovering over each one.)
            RadialUIPlugin.AddCustomButtonOnCharacter("AuraPlugin.Menu", new MapMenu.ItemArgs
            {
                Title = "Aura",
                Icon = auraIcon,
                CloseMenuOnActivate = false,
                FadeName = auraIcon != null,
                Action = (item, obj) => OpenSlotSubmenu(RadialUI.Talespire.RadialMenus.GetTargetCreature(), SlotAura)
            }, (self, target) => true);

            // A second top-level entry alongside "Aura", for cast spells rather than standing
            // character auras. Separate button rather than another branch inside the Aura menu:
            // the two are reached at different moments at the table - an aura is set up once and
            // left alone, a spell is cast mid-turn - and burying spells two levels deep behind
            // "Aura" made the common case the slower one.
            //
            // FadeName false because there's no icon: MapMenuItem drives the label's alpha to 0
            // when FadeName is set, so a hover-only label with no icon would render as a
            // completely blank button. Same trade-off the Aura button makes when its icon is
            // missing.
            RadialUIPlugin.AddCustomButtonOnCharacter("AuraPlugin.SpellsMenu", new MapMenu.ItemArgs
            {
                Title = "Spells",
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => OpenSlotSubmenu(RadialUI.Talespire.RadialMenus.GetTargetCreature(), SlotSpell)
            }, (self, target) => true);

            // Fires on every client (including our own) whenever any AuraPlugin.* value
            // changes for any creature - that's how the ring gets (re)drawn/removed both
            // locally and for everyone else at the table.
            AssetDataPlugin.Subscribe("AuraPlugin.*", OnAuraDataChanged);

            Logger.LogInfo("AuraPlugin loaded.");
        }

        // Parses the "Name:RRGGBBAA,Name:RRGGBBAA" config string into the color cycle list.
        private void ParsePresets()
        {
            colorSteps = new List<(string, Color)>();
            foreach (var entry in colorPresetsConfig.Value.Split(','))
            {
                var pieces = entry.Split(':');
                if (pieces.Length != 2) continue;
                if (ColorUtility.TryParseHtmlString("#" + pieces[1].Trim().TrimStart('#'), out var color))
                {
                    colorSteps.Add((pieces[0].Trim(), color));
                }
            }
            if (colorSteps.Count == 0)
            {
                colorSteps.Add(("Gold", new Color(1f, 0.84f, 0f, 0.4f)));
            }
        }

        // Parses the "Name:Percent,..." override string into the per-colour ceiling map.
        // Runs after ParsePresets so an override naming a colour that ColorSteps doesn't define
        // can be reported rather than sitting in the map where it would never be looked up.
        private void ParseColorRealMaxOverrides()
        {
            colorRealMaxOverrides = new Dictionary<string, float>();

            foreach (var entry in colorRealMaxOverridesConfig.Value.Split(','))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                var pieces = entry.Split(':');
                if (pieces.Length != 2)
                {
                    Logger.LogWarning($"AuraPlugin: skipping colour opacity override '{entry.Trim()}' - expected Name:Percent.");
                    continue;
                }

                string colorName = pieces[0].Trim();
                if (!colorSteps.Exists(c => c.Name == colorName))
                {
                    Logger.LogWarning($"AuraPlugin: skipping colour opacity override '{colorName}' - not one of the names defined in the ColorSteps config.");
                    continue;
                }

                if (!float.TryParse(pieces[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float percent)
                    || float.IsNaN(percent) || float.IsInfinity(percent))
                {
                    Logger.LogWarning($"AuraPlugin: skipping colour opacity override '{colorName}' - '{pieces[1].Trim()}' is not a valid percent.");
                    continue;
                }

                colorRealMaxOverrides[colorName] = Mathf.Clamp(percent, 0f, 100f);
            }
        }

        // Parses the "Name:Radius:Colour:Shape:Opacity,..." config string into the preset list.
        // Every field is validated up front and a bad entry is dropped with a warning naming it,
        // rather than being half-applied at runtime: a preset that silently produced the wrong
        // colour or shape would be far harder to diagnose than one that visibly never appears in
        // the menu and says why in the log.
        private List<SpellPreset> ParsePresetList(string configValue, string label)
        {
            var parsed = new List<SpellPreset>();

            foreach (var entry in configValue.Split(','))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                var pieces = entry.Split(':');
                if (pieces.Length != 5 && pieces.Length != 6)
                {
                    Logger.LogWarning($"AuraPlugin: skipping {label} '{entry.Trim()}' - expected 5 or 6 colon-separated fields (Name:SizeFeet:ColorName:Shape:OpacityPercent[:2D|3D]), found {pieces.Length}.");
                    continue;
                }

                string name = pieces[0].Trim();
                if (name.Length == 0)
                {
                    Logger.LogWarning($"AuraPlugin: skipping {label} '{entry.Trim()}' - the name field is empty.");
                    continue;
                }

                if (!float.TryParse(pieces[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float radiusFeet)
                    || float.IsNaN(radiusFeet) || float.IsInfinity(radiusFeet) || radiusFeet <= 0f)
                {
                    Logger.LogWarning($"AuraPlugin: skipping {label} '{name}' - '{pieces[1].Trim()}' is not a radius greater than zero.");
                    continue;
                }

                // Must name an existing ColorSteps entry, because ColorKey stores a colour NAME
                // and ResolveColor/ResolveColorName look it up in that same list - an
                // unrecognised name stored there would silently render as the first colour.
                string colorName = pieces[2].Trim();
                if (!colorSteps.Exists(c => c.Name == colorName))
                {
                    Logger.LogWarning($"AuraPlugin: skipping {label} '{name}' - colour '{colorName}' is not one of the names defined in the ColorSteps config.");
                    continue;
                }

                // Matched case-insensitively against the registry, then normalised to the
                // registry's own spelling so the value stored in AssetDataPlugin always matches
                // what GetCurrentShape will accept back.
                string shape = pieces[3].Trim();
                string dimension = DimensionTwo;

                if (string.Equals(shape, ShapeBubble, StringComparison.OrdinalIgnoreCase))
                {
                    // "Bubble" was a SHAPE before it became the 2D/3D toggle, and every preset
                    // config written until then says Bubble - including the defaults that shipped.
                    // Translating it here rather than rejecting it is what keeps Fireball and
                    // friends working across the change.
                    shape = ShapeFlat;
                    dimension = DimensionThree;
                }
                else
                {
                    string shapeToMatch = shape;
                    int shapeIndex = ShapeRegistry.FindIndex(registered => string.Equals(registered.Key, shapeToMatch, StringComparison.OrdinalIgnoreCase));
                    if (shapeIndex < 0)
                    {
                        Logger.LogWarning($"AuraPlugin: skipping {label} '{name}' - shape '{shape}' is not one of: {string.Join(", ", ShapeRegistry.ConvertAll(registered => registered.Key).ToArray())}, or Bubble.");
                        continue;
                    }
                    shape = ShapeRegistry[shapeIndex].Key;
                }

                // An explicit sixth field wins over anything inferred above.
                if (pieces.Length == 6)
                {
                    string dimensionToken = pieces[5].Trim();
                    if (string.Equals(dimensionToken, DimensionThree, StringComparison.OrdinalIgnoreCase))
                    {
                        dimension = DimensionThree;
                    }
                    else if (string.Equals(dimensionToken, DimensionTwo, StringComparison.OrdinalIgnoreCase))
                    {
                        dimension = DimensionTwo;
                    }
                    else
                    {
                        Logger.LogWarning($"AuraPlugin: skipping {label} '{name}' - '{dimensionToken}' is not {DimensionTwo} or {DimensionThree}.");
                        continue;
                    }
                }

                if (!float.TryParse(pieces[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float opacityPercent)
                    || float.IsNaN(opacityPercent) || float.IsInfinity(opacityPercent))
                {
                    Logger.LogWarning($"AuraPlugin: skipping {label} '{name}' - '{pieces[4].Trim()}' is not a valid opacity percent.");
                    continue;
                }

                parsed.Add(new SpellPreset
                {
                    Name = name,
                    RadiusFeet = radiusFeet,
                    ColorName = colorName,
                    Shape = shape,
                    Dimension = dimension,
                    // Same fixed 0-100 display scale as everything else - see ResolveOpacityAlpha.
                    OpacityPercent = Mathf.Clamp(opacityPercent, 0f, 100f)
                });
            }

            Logger.LogInfo($"AuraPlugin: loaded {parsed.Count} {label}(s).");
            return parsed;
        }

        // Loads a PNG sitting right next to this plugin's own DLL into a Sprite for use as a
        // radial menu button icon. Deliberately not in a subfolder (e.g. "Icons/") - r2modman's
        // Import Local Mod doesn't reliably preserve nested subfolders from a package zip, so a
        // file placed there can silently fail to land next to the DLL on a fresh install even
        // though the zip itself is correct. Returns null - falling back to the button's plain
        // text label, which MapMenuItem already handles fine - rather than throwing if the file's
        // missing, since a missing icon shouldn't be able to take down the whole plugin. The
        // caller pairs a null return with FadeName = false so that fallback label stays visible
        // rather than fading out and leaving nothing on screen at all.
        private Sprite LoadIcon(string fileName)
        {
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", fileName);
            if (!File.Exists(path))
            {
                Logger.LogWarning($"AuraPlugin: icon file not found at '{path}' - button will show its text label instead.");
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                Logger.LogWarning($"AuraPlugin: could not decode icon file '{path}' - button will show its text label instead.");
                return null;
            }

            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }

        // Builds a filled circle sprite in the given colour, used as the icon on each button of
        // the Aura Color picker. The radial menu's buttons are already circular, so a ring of
        // these reads as a colour picker without needing any custom UI drawn over the game.
        //
        // Drawn fully opaque regardless of the colour's own alpha byte: a swatch rendered at the
        // config's ~40% alpha would wash every colour out towards the menu background, and would
        // make the White and Black entries nearly indistinguishable from each other.
        private Sprite GetColorSwatch(string name, Color color)
        {
            // Hex in the key, not just the name - see colorSwatchCache's comment.
            string cacheKey = name + "|" + ColorUtility.ToHtmlStringRGB(color);
            if (colorSwatchCache.TryGetValue(cacheKey, out var cached) && cached != null)
            {
                return cached;
            }

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);

            var centre = new Vector2((size - 1) / 2f, (size - 1) / 2f);
            float outerRadius = size / 2f - 1f;
            float fillRadius = outerRadius - 4f;

            // A mid-grey rim, deliberately not black or white: it has to keep BOTH extremes of
            // the palette visible - a black swatch against the dark radial menu, and a white one
            // against a light background - and only a mid tone contrasts with both.
            var rim = new Color(0.5f, 0.5f, 0.5f, 1f);
            var fill = new Color(color.r, color.g, color.b, 1f);

            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), centre);

                    // One-pixel feather at both boundaries so the circle and its rim don't come
                    // out visibly stair-stepped at this small an icon size.
                    Color pixel = distance <= fillRadius + 0.5f
                        ? Color.Lerp(fill, rim, Mathf.Clamp01(distance - fillRadius + 0.5f))
                        : rim;
                    pixel.a = Mathf.Clamp01(outerRadius - distance);

                    pixels[y * size + x] = pixel;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();

            var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            colorSwatchCache[cacheKey] = sprite;
            return sprite;
        }

        // Called when the top-level "Aura" button is clicked. Opens a fresh ring of buttons
        // positioned on the targeted mini, mirroring how RadialUIPlugin's own submenu helper
        // (RadialSubmenu.DisplaySubmenu) works - except we keep the returned MapMenuItem
        // handles so we can refresh their text in place afterwards.
        // Takes the creature rather than looking it up: the colour picker reopens this menu
        // after calling MapMenuManager.ForceCloseAll(), and the radial menu's notion of "the
        // targeted creature" isn't guaranteed to survive that teardown. Passing the asset that
        // was already resolved when the menu first opened sidesteps the question entirely.
        private void OpenSlotSubmenu(CreatureBoardAsset targetCreature, AuraSlot slot)
        {
            // Unity's overloaded null check - covers the mini having been deleted from the board
            // between opening the colour picker and picking a colour.
            if (targetCreature == null) return;

            string identity = targetCreature.CreatureId.ToString();
            openSubmenuIdentity = identity;
            openSubmenuSlot = slot;

            Vector3 pos = targetCreature.transform.position + Vector3.up * RadialUI.Talespire.RadialMenus.GetHeightDiff();
            MapMenu subMenu = MapMenuManager.OpenMenu(pos, true);

            // Button order is deliberate and matches the order the controls actually get used:
            // switch it on, size it, fade it, then the less frequent shape/colour choices, and
            // the list pickers last. The radial menu lays buttons out by insertion order, so the
            // sequence of AddItem calls below IS the on-screen layout.
            openEnabledItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = slot.Name + " On/Off",
                ValueText = GetAuraEnabled(identity, slot) ? ToggleOn : ToggleOff,
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => CycleAuraEnabled(identity, slot)
            });

            // "Toggle" rather than the slot name: these step through values on each click, and
            // naming them for the action distinguishes them from the "Type" pair below, which
            // set the same two values a different way.
            openRadiusItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Toggle " + slot.SizeLabel,
                ValueText = FormatRadius(GetCurrentRadiusFeet(identity, slot)),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => StepRadius(identity, slot)
            });

            // Shown for the whole slot rather than only for the shapes that use it: the radial
            // menu's button set is fixed once a menu is open, so a button that appeared only for
            // Wall/Ring/Cylinder could never show up when you switched to one of those from
            // inside that same menu. Harmlessly inert for the other shapes.
            if (slot.AllowHeight)
            {
                openHeightItem = subMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = "Toggle Height",
                    ValueText = FormatRadius(GetCurrentHeightFeet(identity, slot)),
                    CloseMenuOnActivate = false,
                    FadeName = false,
                    Action = (item, obj) => StepHeight(identity, slot)
                });
            }

            openOpacityItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Toggle Opacity",
                ValueText = FormatOpacity(GetCurrentOpacityPercent(identity, slot)),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => StepOpacity(identity, slot)
            });

            // Typing an exact number instead of clicking through the steps. These close the
            // submenu, since the on-screen text box takes over input. ValueText shows the value
            // as of the moment the menu opened and is never refreshed - CloseMenuOnActivate
            // means the button is gone before a new value could be set anyway.
            subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Type " + slot.SizeLabel,
                ValueText = FormatRadius(GetCurrentRadiusFeet(identity, slot)),
                CloseMenuOnActivate = true,
                FadeName = false,
                Action = (item, obj) => OpenCustomInput(CustomInputField.Radius, identity, slot)
            });

            subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Type Opacity",
                ValueText = FormatOpacity(GetCurrentOpacityPercent(identity, slot)),
                CloseMenuOnActivate = true,
                FadeName = false,
                Action = (item, obj) => OpenCustomInput(CustomInputField.Opacity, identity, slot)
            });

            openShapeItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = slot.Name + " Shape",
                ValueText = GetShapeDisplayName(GetCurrentShape(identity, slot)),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => OpenShapePickerSubmenu(identity, slot, targetCreature)
            });

            // Directly after Shape, for both slots - the two together decide what gets drawn, and
            // Dimension is meaningless without knowing which footprint it applies to.
            openDimensionItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = slot.Name + " Dimension",
                ValueText = GetCurrentDimension(identity, slot),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => CycleDimension(identity, slot)
            });

            openFillItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Fill",
                ValueText = GetFillEnabled(identity, slot) ? ToggleOn : ToggleOff,
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => CycleFill(identity, slot)
            });

            openColorItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = slot.Name + " Color",
                ValueText = ResolveColorName(identity, slot),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => OpenColorPickerSubmenu(identity, slot, targetCreature)
            });

            // Shown unconditionally rather than only when the aura is already a 3D sphere: the
            // radial menu's button set is fixed for the lifetime of one open submenu, so a button
            // added only for the shapes it applies to could never appear when you switched to one
            // of those shapes from inside that same menu.
            if (slot.AllowGridLines)
            {
                openGridLinesItem = subMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = "Show Gridlines",
                    ValueText = GetShowGridLines(identity, slot) ? ToggleOn : ToggleOff,
                    CloseMenuOnActivate = false,
                    FadeName = false,
                    Action = (item, obj) => CycleGridLines(identity, slot)
                });
            }

            if (slot.AllowTemplateLists && commonPresets.Count > 0)
            {
                subMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = "Common...",
                    CloseMenuOnActivate = false,
                    FadeName = false,
                    Action = (item, obj) => OpenPresetsSubmenu(identity, slot, commonPresets, targetCreature)
                });
            }

            // Omitted entirely when a slot has no presets, rather than opening an empty submenu.
            List<SpellPreset> presets = GetPresetsFor(slot);
            if (presets.Count > 0)
            {
                subMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = slot.Name + " Presets...",
                    CloseMenuOnActivate = false,
                    FadeName = false,
                    Action = (item, obj) => OpenPresetsSubmenu(identity, slot, presets, targetCreature)
                });
            }

        }

        // Each slot draws from its own preset list: standing character auras under Aura, cast
        // spells under Spells.
        private List<SpellPreset> GetPresetsFor(AuraSlot slot)
        {
            return slot == SlotSpell ? spellPresets : NoPresets;
        }

        // Drops every handle into the currently-open Aura submenu's buttons. Called whenever a
        // nested menu takes over, because the parent's pooled MapMenuItems may be recycled for
        // unrelated buttons from that point on - a later RefreshDisplayedValue through a stale
        // handle would reflectively overwrite whatever button now occupies that slot.
        private void ClearOpenSubmenuHandles()
        {
            openEnabledItem = null;
            openRadiusItem = null;
            openColorItem = null;
            openShapeItem = null;
            openOpacityItem = null;
            openGridLinesItem = null;
            openDimensionItem = null;
            openFillItem = null;
            openHeightItem = null;
            openSubmenuIdentity = null;
            openSubmenuSlot = null;
        }

        // Lists a set of presets, one button each. Shared by the Aura menu's "Aura Presets..."
        // and the top-level "Spells" button - the two differ only in which list they're handed
        // and whether applying one drops back to the Aura menu or just closes.
        private void OpenPresetsSubmenu(string identity, AuraSlot slot, List<SpellPreset> presets,
            CreatureBoardAsset targetCreature)
        {
            if (targetCreature == null || presets.Count == 0) return;

            Vector3 pos = targetCreature.transform.position + Vector3.up * RadialUI.Talespire.RadialMenus.GetHeightDiff();
            MapMenu presetMenu = MapMenuManager.OpenMenu(pos, true);

            ClearOpenSubmenuHandles();

            foreach (var preset in presets)
            {
                // Captured into a local: the lambda below outlives this iteration, and closing
                // over the loop variable directly would have every button apply the last preset.
                SpellPreset captured = preset;
                presetMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = captured.Name,
                    ValueText = FormatRadius(captured.RadiusFeet),
                    // MUST stay false: MapMenuItem.LeftClick force-closes everything AFTER the
                    // action runs, which would tear down the menu ApplyPreset reopens. See
                    // SetColorAndReturn.
                    CloseMenuOnActivate = false,
                    FadeName = false,
                    Action = (item, obj) => ApplyPreset(identity, slot, captured, targetCreature)
                });
            }
        }

        // Writes every value a preset carries in one go, then rebuilds once - see
        // suppressRebuildForIdentity for why the intermediate rebuilds are skipped.
        private void ApplyPreset(string identity, AuraSlot slot, SpellPreset preset, CreatureBoardAsset returnTo)
        {
            suppressRebuildForVisual = VisualKey(identity, slot);
            try
            {
                AssetDataPlugin.SetInfo(identity, slot.RadiusKey, preset.RadiusFeet.ToString(CultureInfo.InvariantCulture), false);
                AssetDataPlugin.SetInfo(identity, slot.ColorKey, preset.ColorName, false);
                AssetDataPlugin.SetInfo(identity, slot.ShapeKey, preset.Shape, false);
                AssetDataPlugin.SetInfo(identity, slot.DimensionKey, preset.Dimension, false);
                AssetDataPlugin.SetInfo(identity, slot.OpacityKey, preset.OpacityPercent.ToString(CultureInfo.InvariantCulture), false);
                // Last, and unconditional: picking a named spell is a clear statement that you
                // want to see it, so a preset turns the aura on rather than quietly configuring
                // an aura that stays invisible because it happened to be switched off.
                AssetDataPlugin.SetInfo(identity, slot.EnabledKey, ToggleOn, false);
            }
            finally
            {
                // In a finally so a throw mid-write can't leave this creature permanently
                // unable to rebuild for the rest of the session.
                suppressRebuildForVisual = null;
            }

            ClearOpenSubmenuHandles();

            RebuildRing(identity, slot);

            // Drop back to the slot's own menu so you can keep tweaking what the preset set.
            if (returnTo != null)
            {
                MapMenuManager.ForceCloseAll();
                OpenSlotSubmenu(returnTo, slot);
            }
        }

        // Explicit on/off state takes priority. If it's never been set, fall back to
        // exactly what the OLD "radius > 0 means visible, radius <= 0 means off" convention
        // would have shown, so a mini configured before this button existed doesn't change
        // visibility - in EITHER direction - purely from this upgrade. This has to check the
        // actual stored radius value, not just whether RadiusKey is present: a mini explicitly
        // turned off pre-upgrade still has RadiusKey="0" stored, and treating that presence
        // alone as "was on" would silently re-show an aura the player had deliberately hidden.
        private bool GetAuraEnabled(string identity, AuraSlot slot)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, slot.EnabledKey);
            if (!string.IsNullOrEmpty(stored)) return stored == ToggleOn;

            string radiusStr = AssetDataPlugin.ReadInfo(identity, slot.RadiusKey);
            return !string.IsNullOrEmpty(radiusStr)
                && float.TryParse(radiusStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float storedFeet)
                && storedFeet > 0f;
        }

        // Click handler for "Aura On/Off": a dedicated toggle so switching an aura off
        // doesn't mean cycling Aura Radius all the way around - radius no longer has any
        // "off" value of its own (see StepRadius/GetCurrentRadiusFeet).
        private void CycleAuraEnabled(string identity, AuraSlot slot)
        {
            bool next = !GetAuraEnabled(identity, slot);

            // Turning a never-configured aura on for the first time: GetCurrentRadiusFeet
            // and ResolveColorName would otherwise each synthesize their "no value stored
            // yet" fallback from THIS client's own local BepInEx config (RadiusStepFeet,
            // ColorSteps) purely for display, without ever persisting it - meaning a
            // different player's client, with a different local config, could independently
            // synthesize a DIFFERENT radius or color for the exact same creature, with no
            // way to notice the mismatch since neither client sees an explicit stored value
            // to disagree over. Persisting explicit starting values now, the moment a real
            // user action creates this aura, makes sure every client reads the identical
            // synced numbers from here on instead of each guessing from local config.
            if (next)
            {
                if (string.IsNullOrEmpty(AssetDataPlugin.ReadInfo(identity, slot.RadiusKey)))
                {
                    AssetDataPlugin.SetInfo(identity, slot.RadiusKey, GetCurrentRadiusFeet(identity, slot).ToString(CultureInfo.InvariantCulture), false);
                }
                if (string.IsNullOrEmpty(AssetDataPlugin.ReadInfo(identity, slot.ColorKey)))
                {
                    AssetDataPlugin.SetInfo(identity, slot.ColorKey, ResolveColorName(identity, slot), false);
                }
            }

            AssetDataPlugin.SetInfo(identity, slot.EnabledKey, next ? ToggleOn : ToggleOff, false);

            if (openEnabledItem != null && identity == openSubmenuIdentity && slot == openSubmenuSlot)
            {
                RefreshDisplayedValue(openEnabledItem, next ? ToggleOn : ToggleOff);
            }
        }

        // Radius has no "off" value anymore - that's what the Aura On/Off button is for -
        // so an unset, unparsable, or stale pre-On/Off-button "0" value all fall back to the
        // smallest step instead of 0.
        private float GetCurrentRadiusFeet(string identity, AuraSlot slot)
        {
            string radiusStr = AssetDataPlugin.ReadInfo(identity, slot.RadiusKey);
            if (!string.IsNullOrEmpty(radiusStr)
                && float.TryParse(radiusStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float feet)
                && feet > 0f)
            {
                return feet;
            }
            return Mathf.Max(0.1f, radiusStepFeetConfig.Value);
        }

        private static string FormatRadius(float feet)
        {
            return feet.ToString("0.#", CultureInfo.InvariantCulture) + " ft";
        }

        // Click handler for "Aura Radius": adds one step, wrapping back to the smallest step
        // (never 0 - see GetCurrentRadiusFeet) past the configured max, then updates
        // AssetDataPlugin (which syncs/persists it) and refreshes the button's own displayed
        // text so the change is visible immediately.
        private void StepRadius(string identity, AuraSlot slot)
        {
            float current = GetCurrentRadiusFeet(identity, slot);
            float step = Mathf.Max(0.1f, radiusStepFeetConfig.Value);
            float max = Mathf.Max(step, radiusMaxFeetConfig.Value);

            float next = current + step;
            if (next > max + 0.001f) next = step;

            AssetDataPlugin.SetInfo(identity, slot.RadiusKey, next.ToString(CultureInfo.InvariantCulture), false);

            if (openRadiusItem != null && identity == openSubmenuIdentity && slot == openSubmenuSlot)
            {
                RefreshDisplayedValue(openRadiusItem, FormatRadius(next));
            }
        }

        // Click handler for "Aura Color". Opens a nested menu with one button per configured
        // colour, each icon'd with a filled circle of that colour - replacing the old
        // click-to-cycle behaviour, which needed up to seven clicks to reach the colour you
        // wanted and gave no preview of what was coming next.
        private void OpenColorPickerSubmenu(string identity, AuraSlot slot, CreatureBoardAsset targetCreature)
        {
            if (targetCreature == null) return;

            Vector3 pos = targetCreature.transform.position + Vector3.up * RadialUI.Talespire.RadialMenus.GetHeightDiff();
            MapMenu colorMenu = MapMenuManager.OpenMenu(pos, true);

            ClearOpenSubmenuHandles();

            foreach (var step in colorSteps)
            {
                // Captured into a local: the lambda outlives this iteration, and closing over
                // the loop variable directly would have every button apply the last colour.
                var captured = step;
                Sprite swatch = GetColorSwatch(captured.Name, captured.Value);

                colorMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = captured.Name,
                    Icon = swatch,
                    // Stays open on click, so you can click straight through several colours and
                    // watch the aura change on the board rather than reopening the menu each time.
                    CloseMenuOnActivate = false,
                    // Hover-only label when the swatch rendered, since the circle already says
                    // which colour it is - but a permanent text label if it somehow didn't, so
                    // the button can't end up completely blank. Same trade-off as the top-level
                    // Aura button makes with its icon.
                    FadeName = swatch != null,
                    Action = (item, obj) => SetColorAndReturn(identity, slot, captured.Name, targetCreature)
                });
            }
        }

        // Writes the colour - AssetDataPlugin syncs it and fires the subscription that redraws
        // the aura - then closes the picker and reopens the Aura submenu it was opened from.
        //
        // The close is driven here rather than via CloseMenuOnActivate, and the ordering is the
        // whole reason: decompiling MapMenuItem.LeftClick shows it invokes the button's action
        // FIRST and only then calls MapMenuManager.ForceCloseAll() if closeOnActivate is set. A
        // menu reopened from inside the action would therefore be torn down immediately after.
        // With closeOnActivate left false, LeftClick does nothing after the action returns, so
        // closing and reopening in that order here sticks.
        private void SetColorAndReturn(string identity, AuraSlot slot, string colorName, CreatureBoardAsset targetCreature)
        {
            AssetDataPlugin.SetInfo(identity, slot.ColorKey, colorName, false);

            MapMenuManager.ForceCloseAll();
            // Reopening rebuilds the submenu from current state, so the Aura Color button comes
            // back showing the colour just picked - no RefreshDisplayedValue call needed.
            OpenSlotSubmenu(targetCreature, slot);
        }

        // Falls back to Flat for anything unrecognised, which covers both a never-set value and
        // a shape written by a NEWER version of the plugin than this client is running - an
        // older client then draws a plain ring rather than nothing at all.
        private string GetCurrentShape(string identity, AuraSlot slot)
        {
            string shape = AssetDataPlugin.ReadInfo(identity, slot.ShapeKey);

            // A creature configured before Bubble became a dimension has "Bubble" stored here.
            // It maps to the circle, and GetCurrentDimension reads the same value to decide the
            // aura is solid - so an existing bubble keeps rendering exactly as it did.
            if (shape == ShapeBubble) return ShapeFlat;

            if (!string.IsNullOrEmpty(shape) && ShapeRegistry.Exists(entry => entry.Key == shape))
            {
                return shape;
            }
            return ShapeFlat;
        }

        // 2D draws a ground outline, 3D a translucent solid. Defaults to 2D.
        private string GetCurrentDimension(string identity, AuraSlot slot)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, slot.DimensionKey);
            if (stored == DimensionThree || stored == DimensionTwo) return stored;

            // Legacy: before this toggle existed, a solid aura was the "Bubble" SHAPE. Nothing
            // will have written a Dimension for those, so fall back to reading the shape.
            if (AssetDataPlugin.ReadInfo(identity, slot.ShapeKey) == ShapeBubble) return DimensionThree;

            return DimensionTwo;
        }

        // Whether the shape's interior is painted or only its outline drawn. Applies in both
        // dimensions: a filled 2D template is a translucent patch on the ground, an unfilled 3D
        // one is a wireframe cage.
        private bool GetFillEnabled(string identity, AuraSlot slot)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, slot.FillKey);
            if (stored == ToggleOn) return true;
            if (stored == ToggleOff) return false;

            // Unset: reproduce exactly what the plugin did before this toggle existed, so no
            // existing aura changes appearance on upgrade. A 3D aura was solid; a 2D one was an
            // outline.
            return GetCurrentDimension(identity, slot) == DimensionThree;
        }

        // The height a Wall, Ring or Cylinder is drawn at, in feet. Unset falls back to the
        // shape's configured default, so an aura set up before this control existed keeps the
        // height it always had - and switching shape moves to that shape's own default until you
        // set one explicitly.
        private float GetCurrentHeightFeet(string identity, AuraSlot slot)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, slot.HeightKey);
            if (!string.IsNullOrEmpty(stored)
                && float.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out float feet)
                && !float.IsNaN(feet) && !float.IsInfinity(feet) && feet > 0f)
            {
                return feet;
            }
            return GetDefaultHeightFeet(GetCurrentShape(identity, slot));
        }

        private float GetDefaultHeightFeet(string shape)
        {
            if (shape == ShapeWall || shape == ShapeRing) return wallHeightFeetConfig.Value;
            if (shape == ShapeCylinder) return cylinderHeightFeetConfig.Value;
            return prismHeightFeetConfig.Value;
        }

        // Only the shapes with a free-standing height read the stored value; a cube's height is
        // its own size, and cone/line keep the shared SolidShapeHeightFeet.
        private static bool ShapeUsesHeight(string shape)
        {
            return shape == ShapeWall || shape == ShapeRing || shape == ShapeCylinder;
        }

        // Same step-and-wrap pattern as StepRadius, reusing the radius step and max rather than
        // adding two more config keys for the same 5ft-at-a-time behaviour.
        private void StepHeight(string identity, AuraSlot slot)
        {
            float current = GetCurrentHeightFeet(identity, slot);
            float step = Mathf.Max(0.1f, radiusStepFeetConfig.Value);
            float max = Mathf.Max(step, radiusMaxFeetConfig.Value);

            float next = current + step;
            if (next > max + 0.001f) next = step;

            AssetDataPlugin.SetInfo(identity, slot.HeightKey, next.ToString(CultureInfo.InvariantCulture), false);

            if (openHeightItem != null && identity == openSubmenuIdentity && slot == openSubmenuSlot)
            {
                RefreshDisplayedValue(openHeightItem, FormatRadius(next));
            }
        }

        private void CycleFill(string identity, AuraSlot slot)
        {
            bool next = !GetFillEnabled(identity, slot);
            AssetDataPlugin.SetInfo(identity, slot.FillKey, next ? ToggleOn : ToggleOff, false);

            if (openFillItem != null && identity == openSubmenuIdentity && slot == openSubmenuSlot)
            {
                RefreshDisplayedValue(openFillItem, next ? ToggleOn : ToggleOff);
            }
        }

        private void CycleDimension(string identity, AuraSlot slot)
        {
            string next = GetCurrentDimension(identity, slot) == DimensionTwo ? DimensionThree : DimensionTwo;
            AssetDataPlugin.SetInfo(identity, slot.DimensionKey, next, false);

            if (openDimensionItem != null && identity == openSubmenuIdentity && slot == openSubmenuSlot)
            {
                RefreshDisplayedValue(openDimensionItem, next);
            }
        }

        private static string GetShapeDisplayName(string shapeKey)
        {
            int index = ShapeRegistry.FindIndex(entry => entry.Key == shapeKey);
            return index >= 0 ? ShapeRegistry[index].DisplayName : shapeKey;
        }

        // Per-creature facing OFFSET in degrees, added to the mini's own rotation rather than
        // replacing it. Defaults to 0, i.e. the shape points exactly where the mini points.
        // Nothing writes this yet - it's the hook a manual aim control would use.
        private float GetCurrentFacing(string identity, AuraSlot slot)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, slot.FacingKey);
            if (!string.IsNullOrEmpty(stored)
                && float.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out float degrees)
                && !float.IsNaN(degrees) && !float.IsInfinity(degrees))
            {
                return degrees;
            }
            return 0f;
        }

        // Click handler for "Aura Shape". A picker rather than a cycle: with six shapes,
        // cycling would take up to six clicks to reach the one you want and never shows what's
        // coming next. Mirrors the colour picker, including returning to the Aura menu after -
        // see SetColorAndReturn for why the close/reopen is driven manually.
        private void OpenShapePickerSubmenu(string identity, AuraSlot slot, CreatureBoardAsset targetCreature)
        {
            if (targetCreature == null) return;

            Vector3 pos = targetCreature.transform.position + Vector3.up * RadialUI.Talespire.RadialMenus.GetHeightDiff();
            MapMenu shapeMenu = MapMenuManager.OpenMenu(pos, true);

            ClearOpenSubmenuHandles();

            foreach (var entry in ShapeRegistry)
            {
                if (Array.IndexOf(slot.Shapes, entry.Key) < 0) continue;
            {
                // Captured into a local: the lambda outlives this iteration.
                var captured = entry;
                shapeMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = captured.DisplayName,
                    CloseMenuOnActivate = false,
                    FadeName = false,
                    Action = (item, obj) => SetShapeAndReturn(identity, slot, captured.Key, targetCreature)
                });
            }
            }
        }

        private void SetShapeAndReturn(string identity, AuraSlot slot, string shapeKey, CreatureBoardAsset targetCreature)
        {
            AssetDataPlugin.SetInfo(identity, slot.ShapeKey, shapeKey, false);

            MapMenuManager.ForceCloseAll();
            OpenSlotSubmenu(targetCreature, slot);
        }

        // The displayed/stored opacity is always on a 0-100 scale, regardless of how low
        // OpacityRealMaxPercent is configured - see ResolveOpacityAlpha for where that config
        // actually gets applied. Defaults new auras to 100% (i.e. the real max) rather than
        // some fraction of it, so a freshly-toggled-on bubble starts at its intended opacity.
        private float GetCurrentOpacityPercent(string identity, AuraSlot slot)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, slot.OpacityKey);
            if (!string.IsNullOrEmpty(stored) && float.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
            {
                return Mathf.Clamp(percent, 0f, 100f);
            }
            return 100f;
        }

        private static string FormatOpacity(float percent)
        {
            return percent.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        // Rescales the displayed 0-100 percent down to the real alpha fraction (0-1) actually
        // applied to the aura's material - e.g. against a ceiling of 20, a displayed 100%
        // becomes a real alpha of 0.20 and displayed 50% becomes 0.10. This is a straight
        // linear rescale, not a clamp: 100% displayed always means "as opaque as this colour is
        // configured to ever get", not "capped".
        //
        // The ceiling is per-colour, not table-wide: ResolveColorRealMaxPercent falls back to
        // OpacityRealMaxPercent for colours without their own entry.
        private float ResolveOpacityAlpha(string identity, AuraSlot slot)
        {
            float displayedPercent = GetCurrentOpacityPercent(identity, slot);
            float realMaxFraction = Mathf.Clamp01(ResolveColorRealMaxPercent(ResolveColorName(identity, slot)) / 100f);
            return (displayedPercent / 100f) * realMaxFraction;
        }

        // This colour's own opacity ceiling if ColorRealMaxOverrides gives it one, otherwise the
        // table-wide OpacityRealMaxPercent.
        private float ResolveColorRealMaxPercent(string colorName)
        {
            if (colorName != null && colorRealMaxOverrides.TryGetValue(colorName, out float overridePercent))
            {
                return overridePercent;
            }
            return opacityRealMaxPercentConfig.Value;
        }

        // Click handler for "Aura Opacity": same step-and-wrap pattern as StepRadius, always
        // stepping/wrapping on the fixed 0-100 display scale (not the configurable real max -
        // see ResolveOpacityAlpha for where that gets applied instead).
        private void StepOpacity(string identity, AuraSlot slot)
        {
            float current = GetCurrentOpacityPercent(identity, slot);
            float step = Mathf.Clamp(opacityStepPercentConfig.Value, 0.5f, 100f);

            float next = current + step;
            if (next > 100f + 0.001f) next = 0f;

            AssetDataPlugin.SetInfo(identity, slot.OpacityKey, next.ToString(CultureInfo.InvariantCulture), false);

            if (openOpacityItem != null && identity == openSubmenuIdentity && slot == openSubmenuSlot)
            {
                RefreshDisplayedValue(openOpacityItem, FormatOpacity(next));
            }
        }

        // Off unless explicitly switched on. The equator ring is drawn unconditionally and
        // already marks the bubble's boundary, so the lat/long lines are decoration on top of
        // that - better opt-in than opt-out.
        //
        // Note this compares against ToggleOn rather than simply negating the old ToggleOff
        // check: an absent value and an explicit "Off" must both mean off, so that the default
        // and a deliberate switch-off behave identically.
        private bool GetShowGridLines(string identity, AuraSlot slot)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, slot.GridLinesKey);
            return stored == ToggleOn;
        }

        // Click handler for "Show Gridlines": toggles the latitude/longitude grid lines on
        // the bubble on or off. The equator ring stays visible either way - it's the primary
        // boundary marker, more like the flat ring's outline than "grid" decoration.
        private void CycleGridLines(string identity, AuraSlot slot)
        {
            bool next = !GetShowGridLines(identity, slot);
            AssetDataPlugin.SetInfo(identity, slot.GridLinesKey, next ? ToggleOn : ToggleOff, false);

            if (openGridLinesItem != null && identity == openSubmenuIdentity && slot == openSubmenuSlot)
            {
                RefreshDisplayedValue(openGridLinesItem, next ? ToggleOn : ToggleOff);
            }
        }

        // Updates only the number/text shown in the middle of a button, without touching
        // anything Setup() would (title, icon, sibling order, ...). See the comment on
        // ValueTextField/CircleTextField above for why we can't just call Setup() again.
        private static void RefreshDisplayedValue(MapMenuItem item, string valueText)
        {
            if (item == null) return;

            ValueTextField?.SetValue(item, valueText);

            object textMesh = CircleTextField?.GetValue(item);
            if (textMesh == null) return;

            // TextMeshProUGUI.text via reflection too, so we don't need a TMPro package
            // reference just for this one property.
            PropertyInfo textProperty = textMesh.GetType().GetProperty("text");
            textProperty?.SetValue(textMesh, valueText);
        }

        // Opens the typed-number box for either Radius or Opacity - shared by both
        // "Type Exact Radius..." and "Type Exact Opacity...".
        private void OpenCustomInput(CustomInputField field, string identity, AuraSlot slot)
        {
            customInputField = field;
            customInputTargetIdentity = identity;
            customInputSlot = slot;
            customInputText = field == CustomInputField.Radius
                ? GetCurrentRadiusFeet(identity, slot).ToString("0.#", CultureInfo.InvariantCulture)
                : GetCurrentOpacityPercent(identity, slot).ToString("0", CultureInfo.InvariantCulture);
            showCustomInput = true;

            // Both callers have CloseMenuOnActivate=true, so the Aura submenu (and its
            // pooled MapMenuItems) is about to be recycled by the game. Drop our handles
            // now rather than leaving them dangling - otherwise, if the pooled objects get
            // reused for unrelated buttons before this text box is submitted, hitting "Set"
            // below would reflectively overwrite whatever button now occupies that slot.
            ClearOpenSubmenuHandles();
        }

        // Draws the typed-number box when showCustomInput is true, editing whichever field
        // customInputField currently points at. OnGUI runs every frame regardless of whether
        // the radial menu is open, hence the early-out at the top.
        private void OnGUI()
        {
            if (!showCustomInput) return;

            bool isRadius = customInputField == CustomInputField.Radius;
            string title = isRadius ? "Aura Radius (feet)" : "Aura Opacity (%)";

            const float width = 220f;
            const float height = 100f;
            var box = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);

            GUI.Box(box, title);
            GUI.SetNextControlName("AuraPlugin.CustomInputField");
            customInputText = GUI.TextField(new Rect(box.x + 10, box.y + 30, width - 20, 24), customInputText, 8);
            GUI.FocusControl("AuraPlugin.CustomInputField");

            bool setClicked = GUI.Button(new Rect(box.x + 10, box.y + 64, (width - 30) / 2, 24), "Set");
            bool cancelClicked = GUI.Button(new Rect(box.x + 20 + (width - 30) / 2, box.y + 64, (width - 30) / 2, 24), "Cancel");

            Event e = Event.current;
            bool enterPressed = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            bool escapePressed = e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape;

            if (setClicked || enterPressed)
            {
                // Opacity is always typed/displayed on the 0-100 scale - OpacityRealMaxPercent
                // rescales what that maps to internally (see ResolveOpacityAlpha), it isn't a
                // ceiling on what you can type here. Radius no longer has an "off" meaning of
                // its own (see the Aura On/Off button), so 0 isn't a valid radius anymore either.
                float max = isRadius ? float.MaxValue : 100f;
                float min = isRadius ? 0.1f : 0f;
                bool valid = float.TryParse(customInputText, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    && !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;

                if (valid)
                {
                    // Written against the slot the box was opened for, so typing an exact radius
                    // from the Spell menu can't quietly retune the creature's Aura instead.
                    //
                    // No RefreshDisplayedValue call here: OpenCustomInput drops every submenu
                    // button handle before this box is shown, precisely because those pooled
                    // items may since have been recycled for unrelated buttons. Reopening the
                    // menu picks the new value up from storage anyway.
                    AssetDataPlugin.SetInfo(customInputTargetIdentity,
                        isRadius ? customInputSlot.RadiusKey : customInputSlot.OpacityKey,
                        value.ToString(CultureInfo.InvariantCulture), false);

                    showCustomInput = false;
                }
                // Invalid input (empty, non-numeric, negative, out of range, "Infinity"/"NaN"):
                // leave the box open so the player can correct it instead of silently
                // discarding it.
                if (enterPressed) e.Use();
            }
            else if (cancelClicked || escapePressed)
            {
                showCustomInput = false;
                if (escapePressed) e.Use();
            }
        }

        // Falls back to the first configured color if nothing's stored yet, or if the
        // stored name no longer matches any configured color (e.g. ColorSteps was edited
        // in the config file between sessions) - keeps the button label consistent with
        // what ResolveColor will actually render for the ring.
        private string ResolveColorName(string identity, AuraSlot slot)
        {
            string name = AssetDataPlugin.ReadInfo(identity, slot.ColorKey);
            if (!string.IsNullOrEmpty(name) && colorSteps.Exists(c => c.Name == name))
            {
                return name;
            }
            return colorSteps[0].Name;
        }

        // AssetDataPlugin.Subscribe callback - fires for ANY creature's AuraPlugin.* data,
        // on ANY client, whenever it changes or on initial load. change.source is the
        // creature identity string we used as the AssetDataPlugin key.
        private void OnAuraDataChanged(AssetDataPlugin.DatumChange change)
        {
            // Which slot changed is decided by the key's prefix, so a change to a creature's
            // Spell doesn't pointlessly rebuild its Aura as well.
            AuraSlot slot = ResolveSlotFromKey(change.key);
            if (slot == null) return;

            // Mid-preset writes for this one slot are skipped; ApplyPreset rebuilds once itself
            // once every key is written. Everything else still rebuilds immediately.
            if (change.source != null && VisualKey(change.source, slot) == suppressRebuildForVisual) return;

            RebuildRing(change.source, slot);
        }

        // Destroys and recreates the ring GameObject for one creature based on its current
        // AssetDataPlugin state. Simpler than trying to update an existing ring in place,
        // and toggling an aura on/off is rare enough that the extra object churn doesn't matter.
        private void RebuildRing(string identity, AuraSlot slot)
        {
            string visualKey = VisualKey(identity, slot);
            string spec = BuildVisualSpec(identity, slot);

            // Nothing that affects the drawing changed, and the visual it describes is still
            // alive - so leave it alone. The liveness half matters: if the GameObject was
            // destroyed behind our back (the mini was removed and re-added, say) the spec would
            // still match and we'd skip rebuilding something that no longer exists.
            if (activeSpecs.TryGetValue(visualKey, out string existingSpec) && existingSpec == spec)
            {
                bool stillAlive = activeRings.TryGetValue(visualKey, out var current) && current != null;
                if (stillAlive || spec == "off") return;
            }
            activeSpecs[visualKey] = spec;

            if (activeRings.TryGetValue(visualKey, out var existingRing) && existingRing != null)
            {
                Destroy(existingRing);
            }
            activeRings.Remove(visualKey);

            if (!GetAuraEnabled(identity, slot)) return; // switched off via Aura On/Off - no ring

            float radiusFeet = GetCurrentRadiusFeet(identity, slot);

            if (!CreatureGuid.TryParse(identity, out var creatureId))
            {
                Logger.LogWarning($"AuraPlugin: could not parse identity '{identity}' as a CreatureGuid - aura will not be drawn.");
                return;
            }
            if (!CreaturePresenter.TryGetAsset(creatureId, out var asset) || asset == null)
            {
                Logger.LogWarning($"AuraPlugin: no CreatureBoardAsset found for '{identity}' - aura will not be drawn (mini may not be loaded on this client yet).");
                return;
            }

            string colorName = AssetDataPlugin.ReadInfo(identity, slot.ColorKey);
            Color color = ResolveColor(colorName);
            // Aura Opacity applies to both shapes, not just the bubble - it replaces the
            // color preset's own baked-in alpha byte entirely (rather than multiplying with
            // it) so the two controls don't compound in a way that's hard to reason about.
            // Computed once here rather than separately in CreateFlatRing/CreateBubble so
            // both shapes are guaranteed to use the exact same resolved alpha.
            color.a = ResolveOpacityAlpha(identity, slot);

            // Our radius is stored in feet; the board's own units are tiles, so convert
            // using the configured feet-per-tile scale (defaults to the usual 5ft/tile).
            float radiusUnits = radiusFeet / Mathf.Max(0.01f, feetPerTileConfig.Value);

            string shape = GetCurrentShape(identity, slot);
            bool solid = GetCurrentDimension(identity, slot) == DimensionThree;

            // Shape says what the footprint is, Dimension says whether it's drawn as a ground
            // outline or a solid. The circle's solid form is the sphere that used to be the
            // "Bubble" shape; every other footprint extrudes its outline straight up into a
            // prism, which is what makes a 3D cube an actual cube.
            bool filled = GetFillEnabled(identity, slot);

            GameObject visual;
            if (solid && shape == ShapeCone)
            {
                // A real cone tapers to a point and has a circular base, so it can't be the flat
                // sector extruded upwards the way the prism shapes are - that produces a wedge
                // with a flat top, which reads as a slice of cake rather than a cone.
                visual = CreateConeVisual(identity, slot, asset, radiusUnits, color, filled);
            }
            else if (shape == ShapeRing)
            {
                // An annulus is not convex, so it can't go through BuildOutlineFor /
                // BuildPrismMesh like every other shape - it gets its own builder.
                visual = CreateRingVisual(identity, slot, asset, radiusUnits, color, filled, solid);
            }
            else if (!solid)
            {
                visual = CreateFlatVisual(identity, slot, asset, shape, radiusUnits, color, filled);
            }
            else if (shape == ShapeFlat)
            {
                visual = CreateBubble(identity, slot, asset, radiusUnits, color, filled);
            }
            else
            {
                visual = CreatePrism(identity, slot, asset, shape, radiusUnits, color, filled);
            }

            activeRings[VisualKey(identity, slot)] = visual;
        }

        // The 2D form of any shape: its outline, optionally with the interior painted in. One
        // path for every footprint including the circle - the circle used to have its own
        // creator that rebuilt 64 world-space points every frame, which was only ever necessary
        // because it predated the shared outline builder.
        //
        // Points are in LOCAL space with useWorldSpace=false, so Unity's transform handles
        // following the mini and applying the facing rotation; only the root moves per frame.
        private GameObject CreateFlatVisual(string identity, AuraSlot slot, CreatureBoardAsset asset,
            string shape, float sizeUnits, Color color, bool filled)
        {
            Vector3[] outline = BuildOutlineFor(shape, sizeUnits);

            var root = new GameObject("AuraPlugin_Flat_" + slot.Name + "_" + identity);
            var lineMaterial = new Material(Shader.Find("Sprites/Default"));
            Material surfaceMaterial = null;

            if (filled)
            {
                surfaceMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };
                var surface = new GameObject("Fill");
                surface.transform.SetParent(root.transform, false);
                surface.AddComponent<MeshFilter>().mesh = BuildFlatFillMesh(outline);
                surface.AddComponent<MeshRenderer>().material = surfaceMaterial;
            }

            // Drawn whether or not the interior is filled: the edge is what you actually read the
            // area off, and a fill at aura opacity alone is too faint to place on the grid.
            AddPrismOutline(root.transform, lineMaterial, outline, 0f, color);

            var follower = root.AddComponent<AuraShapeFollower>();
            follower.Target = asset;
            follower.HeightOffset = ringHeightConfig.Value;
            follower.FacingOffsetDegrees = GetCurrentFacing(identity, slot) + shapeFacingOffsetConfig.Value;
            follower.SurfaceMaterial = surfaceMaterial;
            follower.LineMaterial = lineMaterial;
            // Only remove our own dictionary entry, not whatever might have replaced it - a stale
            // follower's delayed cleanup shouldn't evict a newer visual for the same creature.
            follower.OnTargetLost = () =>
            {
                if (activeRings.TryGetValue(VisualKey(identity, slot), out var current) && current == root)
                {
                    activeRings.Remove(VisualKey(identity, slot));
                }
            };

            return root;
        }

        // A ringed wall: a hollow tube standing on the ground, thickness taken from
        // WallThicknessFeet and height from WallHeightFeet, exactly like the straight Wall shape.
        // Size is the OUTER RADIUS, consistent with every other round shape here - so Wall of
        // Fire's "20 feet in diameter" is a size of 10.
        //
        // Built from separate child meshes rather than one merged mesh: the outer and inner walls
        // and the two annular caps are each simple to generate on their own, and merging them
        // would mean hand-managing shared vertex indices for no visual gain.
        private GameObject CreateRingVisual(string identity, AuraSlot slot, CreatureBoardAsset asset,
            float outerRadiusUnits, Color color, bool filled, bool solid)
        {
            const int segments = 48;

            float thickness = Mathf.Max(0.01f, wallThicknessFeetConfig.Value) / Mathf.Max(0.01f, feetPerTileConfig.Value);
            // Clamped so a thickness wider than the ring itself can't invert the inner wall.
            float innerRadiusUnits = Mathf.Max(outerRadiusUnits * 0.05f, outerRadiusUnits - thickness);
            float height = solid
                ? Mathf.Max(0.01f, GetCurrentHeightFeet(identity, slot)) / Mathf.Max(0.01f, feetPerTileConfig.Value)
                : 0f;

            var root = new GameObject("AuraPlugin_Ring_" + slot.Name + "_" + identity);
            var lineMaterial = new Material(Shader.Find("Sprites/Default"));
            Material surfaceMaterial = null;

            if (filled)
            {
                surfaceMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };

                // The floor of the ring, and - when solid - its ceiling and the two walls.
                AddMeshChild(root.transform, BuildAnnulusMesh(outerRadiusUnits, innerRadiusUnits, segments, 0f), surfaceMaterial);
                if (solid)
                {
                    AddMeshChild(root.transform, BuildAnnulusMesh(outerRadiusUnits, innerRadiusUnits, segments, height), surfaceMaterial);
                    AddMeshChild(root.transform, BuildCylinderSideMesh(outerRadiusUnits, height, segments), surfaceMaterial);
                    AddMeshChild(root.transform, BuildCylinderSideMesh(innerRadiusUnits, height, segments), surfaceMaterial);
                }
            }

            Vector3[] outerOutline = BuildCircleOutline(outerRadiusUnits, segments);
            Vector3[] innerOutline = BuildCircleOutline(innerRadiusUnits, segments);
            AddPrismOutline(root.transform, lineMaterial, outerOutline, 0f, color);
            AddPrismOutline(root.transform, lineMaterial, innerOutline, 0f, color);
            if (solid)
            {
                AddPrismOutline(root.transform, lineMaterial, outerOutline, height, color);
                AddPrismOutline(root.transform, lineMaterial, innerOutline, height, color);
            }

            var follower = root.AddComponent<AuraShapeFollower>();
            follower.Target = asset;
            follower.HeightOffset = ringHeightConfig.Value;
            follower.FacingOffsetDegrees = GetCurrentFacing(identity, slot) + shapeFacingOffsetConfig.Value;
            follower.SurfaceMaterial = surfaceMaterial;
            follower.LineMaterial = lineMaterial;
            follower.OnTargetLost = () =>
            {
                if (activeRings.TryGetValue(VisualKey(identity, slot), out var current) && current == root)
                {
                    activeRings.Remove(VisualKey(identity, slot));
                }
            };

            return root;
        }

        // A true cone: apex on the mini, axis running along the facing, opening out to a circular
        // base at the far end.
        //
        // The taper matches the 2D footprint exactly. 5e defines a cone's WIDTH at any distance as
        // equal to that distance, and width is a diameter - so the radius at distance d is d/2,
        // which is the same atan(0.5) half-angle BuildConeOutline uses for the ground sector.
        //
        // The apex is lifted off the tabletop by ConeApexHeightFeet so the cone comes out of the
        // creature rather than off the floor. That does put the lower part of a long cone below
        // the ground, which is fine: the material depth-tests, so opaque terrain hides it.
        private GameObject CreateConeVisual(string identity, AuraSlot slot, CreatureBoardAsset asset,
            float lengthUnits, Color color, bool filled)
        {
            const int segments = 32;

            float apexHeight = Mathf.Max(0f, coneApexHeightFeetConfig.Value) / Mathf.Max(0.01f, feetPerTileConfig.Value);

            var root = new GameObject("AuraPlugin_Cone_" + slot.Name + "_" + identity);
            var lineMaterial = new Material(Shader.Find("Sprites/Default"));
            Material surfaceMaterial = null;

            if (filled)
            {
                surfaceMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };
                AddMeshChild(root.transform, BuildConeMesh(lengthUnits, apexHeight, segments), surfaceMaterial);
            }

            // The ground sector as well as the cone's own base ring: the sector is the area the
            // rules actually care about and the one you read off the grid, so it stays visible
            // even though the solid above it is a different silhouette.
            AddPrismOutline(root.transform, lineMaterial, BuildConeOutline(lengthUnits, 24), 0f, color);
            AddConeBaseOutline(root.transform, lineMaterial, lengthUnits, apexHeight, segments, color);

            var follower = root.AddComponent<AuraShapeFollower>();
            follower.Target = asset;
            follower.HeightOffset = ringHeightConfig.Value;
            follower.FacingOffsetDegrees = GetCurrentFacing(identity, slot) + shapeFacingOffsetConfig.Value;
            follower.SurfaceMaterial = surfaceMaterial;
            follower.LineMaterial = lineMaterial;
            follower.OnTargetLost = () =>
            {
                if (activeRings.TryGetValue(VisualKey(identity, slot), out var current) && current == root)
                {
                    activeRings.Remove(VisualKey(identity, slot));
                }
            };

            return root;
        }

        // The circular rim at the wide end, standing upright in the plane across the facing.
        private static void AddConeBaseOutline(Transform parent, Material material, float lengthUnits,
            float apexHeight, int segments, Color color)
        {
            float baseRadius = lengthUnits / 2f;
            var points = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                points[i] = new Vector3(Mathf.Cos(angle) * baseRadius,
                                        apexHeight + Mathf.Sin(angle) * baseRadius,
                                        lengthUnits);
            }

            var lineObject = new GameObject("BaseRing");
            lineObject.transform.SetParent(parent, false);
            var lineRenderer = lineObject.AddComponent<LineRenderer>();
            lineRenderer.useWorldSpace = false;
            lineRenderer.loop = true;
            lineRenderer.positionCount = points.Length;
            lineRenderer.SetPositions(points);
            lineRenderer.startWidth = lineRenderer.endWidth = 0.03f;
            lineRenderer.material = material;
            lineRenderer.startColor = lineRenderer.endColor = color;
        }

        // Apex at the origin, base circle at distance `lengthUnits` along +Z with radius half
        // that. Sides fanned from the apex, plus a cap so the wide end isn't hollow.
        private static Mesh BuildConeMesh(float lengthUnits, float apexHeight, int segments)
        {
            float baseRadius = lengthUnits / 2f;

            var vertices = new List<Vector3> { new Vector3(0f, apexHeight, 0f) };
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                vertices.Add(new Vector3(Mathf.Cos(angle) * baseRadius,
                                         apexHeight + Mathf.Sin(angle) * baseRadius,
                                         lengthUnits));
            }
            int baseCentre = vertices.Count;
            vertices.Add(new Vector3(0f, apexHeight, lengthUnits));

            var triangles = new List<int>(segments * 6);
            for (int i = 0; i < segments; i++)
            {
                int a = 1 + i;
                int b = 1 + (i + 1) % segments;

                triangles.Add(0); triangles.Add(a); triangles.Add(b);
                triangles.Add(baseCentre); triangles.Add(b); triangles.Add(a);
            }

            var mesh = new Mesh { name = "AuraPlugin_Cone" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddMeshChild(Transform parent, Mesh mesh, Material material)
        {
            var child = new GameObject("Surface");
            child.transform.SetParent(parent, false);
            child.AddComponent<MeshFilter>().mesh = mesh;
            child.AddComponent<MeshRenderer>().material = material;
        }

        // A flat washer at height y - the ring's floor or ceiling. Quads between the outer and
        // inner rims, so no fan triangulation and no convexity requirement.
        private static Mesh BuildAnnulusMesh(float outerRadius, float innerRadius, int segments, float y)
        {
            var vertices = new List<Vector3>(segments * 2);
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                vertices.Add(new Vector3(cos * outerRadius, y, sin * outerRadius));
                vertices.Add(new Vector3(cos * innerRadius, y, sin * innerRadius));
            }

            var triangles = new List<int>(segments * 6);
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                int outerA = i * 2, innerA = i * 2 + 1;
                int outerB = next * 2, innerB = next * 2 + 1;

                triangles.Add(outerA); triangles.Add(outerB); triangles.Add(innerB);
                triangles.Add(outerA); triangles.Add(innerB); triangles.Add(innerA);
            }

            var mesh = new Mesh { name = "AuraPlugin_Annulus" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // The open tube of a cylinder - no caps. Used twice per ring, once for each face of the
        // wall.
        private static Mesh BuildCylinderSideMesh(float radius, float height, int segments)
        {
            var vertices = new List<Vector3>(segments * 2);
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                vertices.Add(new Vector3(cos * radius, 0f, sin * radius));
                vertices.Add(new Vector3(cos * radius, height, sin * radius));
            }

            var triangles = new List<int>(segments * 6);
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                int bottomA = i * 2, topA = i * 2 + 1;
                int bottomB = next * 2, topB = next * 2 + 1;

                triangles.Add(bottomA); triangles.Add(bottomB); triangles.Add(topB);
                triangles.Add(bottomA); triangles.Add(topB); triangles.Add(topA);
            }

            var mesh = new Mesh { name = "AuraPlugin_CylinderSide" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // A convex outline triangulated flat, as a fan from vertex 0 - same constraint and same
        // reasoning as BuildPrismMesh's caps.
        private static Mesh BuildFlatFillMesh(Vector3[] outline)
        {
            var vertices = new List<Vector3>(outline.Length);
            foreach (Vector3 point in outline)
            {
                vertices.Add(new Vector3(point.x, 0f, point.z));
            }

            var triangles = new List<int>();
            for (int i = 1; i < outline.Length - 1; i++)
            {
                triangles.Add(0);
                triangles.Add(i);
                triangles.Add(i + 1);
            }

            var mesh = new Mesh { name = "AuraPlugin_Fill" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // The ground footprint for one of the non-circular shapes, in board units, laid out
        // facing +Z. Shared by the flat outline and the extruded solid so the two can never
        // disagree about what a cone or a cube actually covers.
        //
        // Every outline here is CONVEX, which BuildPrismMesh relies on to triangulate its caps
        // with a simple fan. A concave shape added later would need a real triangulator.
        private Vector3[] BuildOutlineFor(string shape, float sizeUnits)
        {
            switch (shape)
            {
                case ShapeCone:
                    return BuildConeOutline(sizeUnits, 24);
                case ShapeLine:
                    return BuildLineOutline(sizeUnits,
                        Mathf.Max(0.01f, lineShapeWidthFeetConfig.Value) / Mathf.Max(0.01f, feetPerTileConfig.Value));
                case ShapeFlat:
                    return BuildCircleOutline(sizeUnits, 64);
                case ShapeWall:
                    return BuildWallOutline(sizeUnits,
                        Mathf.Max(0.01f, wallThicknessFeetConfig.Value) / Mathf.Max(0.01f, feetPerTileConfig.Value));
                case ShapeCylinder:
                    // Just a circle - what makes it a cylinder rather than a flat disc is being
                    // run through the same extrusion the cube uses. In 2D it draws as a plain
                    // ring, which is exactly what a cylinder's footprint is.
                    return BuildCircleOutline(sizeUnits, 48);
                case ShapeCubeAhead:
                    return BuildCubeAheadOutline(sizeUnits);
                case ShapeCubeCorner:
                    return BuildCubeCornerOutline(sizeUnits);
                default:
                    return BuildCubeCentredOutline(sizeUnits);
            }
        }

        // A 5e cone: its width at any distance equals that distance, which makes the half-angle
        // atan(0.5) (~26.57 degrees, ~53.13 total). Drawn as a circular sector - apex at the
        // mini, arc capping the far end - rather than a flat-ended triangle, matching how most
        // VTTs render cone templates.
        private static Vector3[] BuildConeOutline(float lengthUnits, int arcSegments)
        {
            float halfAngle = Mathf.Atan(0.5f);
            var points = new Vector3[arcSegments + 2];
            points[0] = Vector3.zero;
            for (int i = 0; i <= arcSegments; i++)
            {
                float angle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / arcSegments);
                points[i + 1] = new Vector3(Mathf.Sin(angle) * lengthUnits, 0f, Mathf.Cos(angle) * lengthUnits);
            }
            return points;
        }

        // A wall segment: a long thin rectangle running along the facing direction, CENTRED on
        // the mini rather than starting at it like Line does.
        //
        // Centred because a wall gets built from several minis, one per section: with the mini in
        // the middle of its own segment, a 5ft section fills exactly the square the mini stands
        // in and sections line up by placing minis on adjacent squares. Starting at the mini
        // would offset every section forward by half its length and make them awkward to chain.
        private static Vector3[] BuildWallOutline(float lengthUnits, float thicknessUnits)
        {
            float halfLength = lengthUnits / 2f;
            float halfThickness = thicknessUnits / 2f;
            return new[]
            {
                new Vector3(-halfThickness, 0f, -halfLength),
                new Vector3(halfThickness, 0f, -halfLength),
                new Vector3(halfThickness, 0f, halfLength),
                new Vector3(-halfThickness, 0f, halfLength)
            };
        }

        // A circle centred on the mini, as a closed polygon. Convex, which is what lets
        // BuildPrismMesh triangulate its caps with a simple fan.
        private static Vector3[] BuildCircleOutline(float radiusUnits, int segments)
        {
            var points = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                points[i] = new Vector3(Mathf.Cos(angle) * radiusUnits, 0f, Mathf.Sin(angle) * radiusUnits);
            }
            return points;
        }

        // A rectangle starting at the mini and running out along +Z. Aura Radius sets the
        // length; the width comes from LineShapeWidthFeet.
        private static Vector3[] BuildLineOutline(float lengthUnits, float widthUnits)
        {
            float halfWidth = widthUnits / 2f;
            return new[]
            {
                new Vector3(-halfWidth, 0f, 0f),
                new Vector3(halfWidth, 0f, 0f),
                new Vector3(halfWidth, 0f, lengthUnits),
                new Vector3(-halfWidth, 0f, lengthUnits)
            };
        }

        // A square centred on the mini. Aura Radius is read as the cube's SIDE here, not a
        // radius - a "20ft cube" spell should be 20ft across, not 40.
        private static Vector3[] BuildCubeCentredOutline(float sideUnits)
        {
            float half = sideUnits / 2f;
            return new[]
            {
                new Vector3(-half, 0f, -half),
                new Vector3(half, 0f, -half),
                new Vector3(half, 0f, half),
                new Vector3(-half, 0f, half)
            };
        }

        // A square with one corner on the mini, its diagonal running along the facing direction.
        // Aligning the diagonal (rather than an edge) keeps it symmetric about where you aim, so
        // sweeping the aim rotates it evenly instead of swinging it lopsidedly to one side. It
        // sits diamond-on to the grid, so it covers a different set of squares than a 5e cube
        // does - kept as a free-form template rather than as a rules-accurate spell area.
        private static Vector3[] BuildCubeCornerOutline(float sideUnits)
        {
            float halfDiagonal = sideUnits / Mathf.Sqrt(2f);
            return new[]
            {
                Vector3.zero,
                new Vector3(halfDiagonal, 0f, halfDiagonal),
                new Vector3(0f, 0f, halfDiagonal * 2f),
                new Vector3(-halfDiagonal, 0f, halfDiagonal)
            };
        }

        // A square whose near FACE is centred on the mini, projecting away along the facing
        // direction. This is what 5e means by a cube "originating from you" - Thunderwave and
        // friends put the caster against a face of the cube, not at one of its corners, so the
        // area starts immediately ahead and stays square to the grid.
        private static Vector3[] BuildCubeAheadOutline(float sideUnits)
        {
            float halfSide = sideUnits / 2f;
            return new[]
            {
                new Vector3(-halfSide, 0f, 0f),
                new Vector3(halfSide, 0f, 0f),
                new Vector3(halfSide, 0f, sideUnits),
                new Vector3(-halfSide, 0f, sideUnits)
            };
        }

        // A 3D shape whose footprint is one of the flat outlines, extruded straight up. This is
        // what makes "Cube + 3D" a real cube; it also gives a line its wall-of-fire form and a
        // cone a wedge.
        //
        // Unlike CreateBubble, the geometry is built at true board scale rather than as a unit
        // mesh scaled by the transform - the outlines already come out in board units, and a
        // uniform scale would distort a prism whose height differs from its footprint.
        private GameObject CreatePrism(string identity, AuraSlot slot, CreatureBoardAsset asset, string shape, float sizeUnits, Color color, bool filled)
        {
            Vector3[] outline = BuildOutlineFor(shape, sizeUnits);

            // A cube's height IS its size - anything else wouldn't be a cube. A cylinder's is
            // its own setting, because cylinder spells state a height that has nothing to do with
            // their radius (Moonbeam is 5ft across and 40ft tall). Everything else open-ended
            // falls back to the shared one.
            float height;
            if (shape == ShapeCube || shape == ShapeCubeAhead || shape == ShapeCubeCorner)
            {
                height = sizeUnits;
            }
            else if (ShapeUsesHeight(shape))
            {
                height = Mathf.Max(0.01f, GetCurrentHeightFeet(identity, slot)) / Mathf.Max(0.01f, feetPerTileConfig.Value);
            }
            else
            {
                height = Mathf.Max(0.01f, prismHeightFeetConfig.Value) / Mathf.Max(0.01f, feetPerTileConfig.Value);
            }

            var root = new GameObject("AuraPlugin_Prism_" + slot.Name + "_" + identity);

            var lineMaterial = new Material(Shader.Find("Sprites/Default"));
            Material surfaceMaterial = null;

            // Unfilled leaves just the top and bottom outlines - a wireframe cage marking the
            // volume's extent without painting anything inside it.
            if (filled)
            {
                surfaceMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };
                var surface = new GameObject("Surface");
                surface.transform.SetParent(root.transform, false);
                surface.AddComponent<MeshFilter>().mesh = BuildPrismMesh(outline, height);
                surface.AddComponent<MeshRenderer>().material = surfaceMaterial;
            }

            // Outlines top and bottom, for the same reason the bubble keeps its equator ring: a
            // translucent solid alone reads as a vague haze, and the edges are what let you
            // actually judge where it lands on the grid.
            AddPrismOutline(root.transform, lineMaterial, outline, 0f, color);
            AddPrismOutline(root.transform, lineMaterial, outline, height, color);

            var follower = root.AddComponent<AuraShapeFollower>();
            follower.Target = asset;
            follower.HeightOffset = ringHeightConfig.Value;
            follower.FacingOffsetDegrees = GetCurrentFacing(identity, slot) + shapeFacingOffsetConfig.Value;
            follower.SurfaceMaterial = surfaceMaterial;
            follower.LineMaterial = lineMaterial;
            follower.OnTargetLost = () =>
            {
                if (activeRings.TryGetValue(VisualKey(identity, slot), out var current) && current == root)
                {
                    activeRings.Remove(VisualKey(identity, slot));
                }
            };

            return root;
        }

        private void AddPrismOutline(Transform parent, Material material, Vector3[] outline, float height, Color color)
        {
            var lineObject = new GameObject("Outline");
            lineObject.transform.SetParent(parent, false);

            var points = new Vector3[outline.Length];
            for (int i = 0; i < outline.Length; i++)
            {
                points[i] = new Vector3(outline[i].x, height, outline[i].z);
            }

            var lineRenderer = lineObject.AddComponent<LineRenderer>();
            lineRenderer.useWorldSpace = false;
            lineRenderer.loop = true;
            lineRenderer.positionCount = points.Length;
            lineRenderer.SetPositions(points);
            lineRenderer.startWidth = lineRenderer.endWidth = ringWidthConfig.Value;
            lineRenderer.material = material;
            lineRenderer.startColor = lineRenderer.endColor = color;
        }

        // Extrudes a convex ground outline upwards into a closed solid: the footprint at y=0, a
        // copy at y=height, and a quad joining each pair of adjacent edges.
        //
        // Caps are triangulated as a simple fan from vertex 0, which is only valid because every
        // outline BuildOutlineFor produces is convex. Winding is not fought over because the
        // material is Sprites/Default, which renders both faces - and a translucent solid needs
        // its inside visible anyway, or standing inside a cube would look like standing in
        // nothing at all.
        private static Mesh BuildPrismMesh(Vector3[] outline, float height)
        {
            int count = outline.Length;
            var vertices = new List<Vector3>(count * 2);

            for (int i = 0; i < count; i++)
            {
                vertices.Add(new Vector3(outline[i].x, 0f, outline[i].z));
            }
            for (int i = 0; i < count; i++)
            {
                vertices.Add(new Vector3(outline[i].x, height, outline[i].z));
            }

            var triangles = new List<int>();

            for (int i = 1; i < count - 1; i++)
            {
                triangles.Add(0);
                triangles.Add(i + 1);
                triangles.Add(i);

                triangles.Add(count);
                triangles.Add(count + i);
                triangles.Add(count + i + 1);
            }

            for (int i = 0; i < count; i++)
            {
                int next = (i + 1) % count;
                triangles.Add(i);
                triangles.Add(next);
                triangles.Add(count + next);

                triangles.Add(i);
                triangles.Add(count + next);
                triangles.Add(count + i);
            }

            var mesh = new Mesh { name = "AuraPlugin_Prism" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Cached once and reused for every bubble - a full sphere (centered on y=0, spanning
        // -1..1), radius 1. Each bubble instance scales this shared mesh via its own transform
        // rather than generating new geometry every time.
        private static Mesh unitSphereMesh;

        private GameObject CreateBubble(string identity, AuraSlot slot, CreatureBoardAsset asset, float radiusUnits, Color color, bool filled)
        {
            if (unitSphereMesh == null)
            {
                // An icosphere rather than a lat/lon UV-sphere, to avoid a translucent-material
                // banding artifact at the poles - see BuildIcosphereMesh's own comment.
                unitSphereMesh = BuildIcosphereMesh(3, "AuraPlugin_UnitSphere");
            }

            // Deliberately NOT parented to the mini's own transform: TaleSpire's creature
            // root can tilt for flying-animation purposes, and inheriting that tilt would
            // tip the sphere over instead of keeping it looking like an upright shield/bubble
            // (same reasoning as AuraShapeFollower not parenting its shapes). Everything
            // under this root uses local/unit-space coordinates and gets scaled via
            // root.transform.localScale, with only position updated per frame.
            var root = new GameObject("AuraPlugin_Bubble_" + slot.Name + "_" + identity);
            root.transform.localScale = Vector3.one * radiusUnits;

            // `color`'s alpha already carries the resolved Aura Opacity value - RebuildRing
            // sets it once, centrally, so both this and CreateFlatRing use the same number.
            // Null when unfilled, leaving the equator and grid lines to describe the sphere on
            // their own - a wireframe globe rather than a solid one.
            Material surfaceMaterial = filled
                ? new Material(Shader.Find("Sprites/Default")) { color = color }
                : null;
            // All grid/equator lines share one material and use their own
            // LineRenderer startColor/endColor for tinting, same pattern as the flat ring -
            // avoids creating a separate material instance per line.
            var lineMaterial = new Material(Shader.Find("Sprites/Default"));

            // Clamped at both ends: these directly drive a per-iteration GameObject+LineRenderer
            // creation loop, so an extreme value (mistyped or hand-edited in the config file)
            // shouldn't be able to hang the client trying to instantiate hundreds of them.
            // Forced to 0 when grid lines are toggled off for this creature - the equator
            // ring (added unconditionally in BuildBubbleVisual) stays either way.
            bool showGrid = GetShowGridLines(identity, slot);
            int latRings = showGrid ? Mathf.Clamp(bubbleGridRingCountConfig.Value, 0, 12) : 0;
            int meridians = showGrid ? Mathf.Clamp(bubbleGridMeridianCountConfig.Value, 0, 24) : 0;
            Color gridColor = new Color(1f, 1f, 1f, bubbleGridAlphaConfig.Value);

            var sphereVisual = new GameObject("SphereVisual");
            sphereVisual.transform.SetParent(root.transform, false);
            BuildBubbleVisual(sphereVisual.transform, unitSphereMesh, surfaceMaterial, lineMaterial,
                color, gridColor, latRings, meridians);


            var follower = root.AddComponent<AuraBubbleFollower>();
            follower.Target = asset;
            follower.HeightOffset = ringHeightConfig.Value;
            follower.SurfaceMaterial = surfaceMaterial;
            follower.LineMaterial = lineMaterial;
            follower.OnTargetLost = () =>
            {
                if (activeRings.TryGetValue(VisualKey(identity, slot), out var current) && current == root)
                {
                    activeRings.Remove(VisualKey(identity, slot));
                }
            };

            return root;
        }

        // Builds one complete visual (sphere surface + equator + grid lines) under `parent`.
        private void BuildBubbleVisual(Transform parent, Mesh mesh, Material surfaceMaterial, Material lineMaterial,
            Color equatorColor, Color gridColor, int latRings, int meridians)
        {
            // A null surface material means the aura is set to outline only - skip the sphere
            // mesh entirely rather than adding a renderer with nothing to draw.
            if (surfaceMaterial != null)
            {
                var surfaceObject = new GameObject("Surface");
                surfaceObject.transform.SetParent(parent, false);
                surfaceObject.AddComponent<MeshFilter>().mesh = mesh;
                surfaceObject.AddComponent<MeshRenderer>().material = surfaceMaterial;
            }

            // y=0 is the sphere's true equator (its vertical midpoint).
            AddBubbleLine(parent, lineMaterial, BuildUnitCircle(64, 0f, 1f), equatorColor, loop: true);

            for (int i = 1; i <= latRings; i++)
            {
                float theta = (Mathf.PI / 2f) * i / (latRings + 1);
                // Both above and below the equator, so the grid looks symmetric.
                AddBubbleLine(parent, lineMaterial, BuildUnitCircle(64, Mathf.Sin(theta), Mathf.Cos(theta)), gridColor, loop: true);
                AddBubbleLine(parent, lineMaterial, BuildUnitCircle(64, -Mathf.Sin(theta), Mathf.Cos(theta)), gridColor, loop: true);
            }

            for (int i = 0; i < meridians; i++)
            {
                float phi = Mathf.PI * i / Mathf.Max(1, meridians);
                // A closed great-circle loop through both poles.
                Vector3[] points = BuildUnitMeridianArc(64, phi, 0f, 2f * Mathf.PI, includeEndpoint: false);
                AddBubbleLine(parent, lineMaterial, points, gridColor, loop: true);
            }
        }

        private void AddBubbleLine(Transform parent, Material material, Vector3[] points, Color color, bool loop)
        {
            var lineObject = new GameObject("Line");
            lineObject.transform.SetParent(parent, false);
            var lineRenderer = lineObject.AddComponent<LineRenderer>();
            // Local to the bubble root, not world space - Unity's normal transform
            // hierarchy then handles scaling (and, if it were ever needed, rotation) for
            // free; only the root's position needs updating per frame.
            lineRenderer.useWorldSpace = false;
            lineRenderer.loop = loop;
            lineRenderer.positionCount = points.Length;
            lineRenderer.SetPositions(points);
            // Width is in the same local/unit space as the points, so it scales up
            // proportionally with the bubble's own radius via the parent's localScale.
            lineRenderer.startWidth = lineRenderer.endWidth = bubbleGridLineWidthConfig.Value;
            lineRenderer.material = material;
            lineRenderer.startColor = lineRenderer.endColor = color;
        }

        // Points for a horizontal circle at local height y with the given radius (both in
        // unit-sphere space, i.e. before the bubble root's own scale is applied).
        private static Vector3[] BuildUnitCircle(int segments, float y, float radius)
        {
            var points = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                points[i] = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
            }
            return points;
        }

        // A great-circle arc at longitude phi, parametrized by a single angle t: t=0 is the
        // equator at phi, t=PI/2 is the north pole, t=PI is the equator at the *opposite*
        // longitude (phi+180), t=3PI/2 is the south pole, t=2PI is back to the start.
        private static Vector3[] BuildUnitMeridianArc(int segments, float phi, float tStart, float tEnd, bool includeEndpoint)
        {
            int count = includeEndpoint ? segments + 1 : segments;
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                float t = Mathf.Lerp(tStart, tEnd, (float)i / segments);
                float y = Mathf.Sin(t);
                float horizontalRadius = Mathf.Cos(t);
                points[i] = new Vector3(horizontalRadius * Mathf.Cos(phi), y, horizontalRadius * Mathf.Sin(phi));
            }
            return points;
        }

        // Builds a unit icosphere (radius 1, centered on the origin) by subdividing a regular
        // icosahedron `subdivisions` times, normalizing each new vertex back onto the unit
        // sphere as it's created. Not a lat/lon UV-sphere: every vertex here has
        // (approximately) the same number of neighboring triangles, so there's no pole vertex
        // that dozens of thin triangles converge onto - which is what causes the
        // alpha-blending overdraw artifact a translucent lat/lon sphere shows at each pole.
        private static Mesh BuildIcosphereMesh(int subdivisions, string name)
        {
            float goldenRatio = (1f + Mathf.Sqrt(5f)) / 2f;
            var vertices = new List<Vector3>
            {
                new Vector3(-1, goldenRatio, 0), new Vector3(1, goldenRatio, 0), new Vector3(-1, -goldenRatio, 0), new Vector3(1, -goldenRatio, 0),
                new Vector3(0, -1, goldenRatio), new Vector3(0, 1, goldenRatio), new Vector3(0, -1, -goldenRatio), new Vector3(0, 1, -goldenRatio),
                new Vector3(goldenRatio, 0, -1), new Vector3(goldenRatio, 0, 1), new Vector3(-goldenRatio, 0, -1), new Vector3(-goldenRatio, 0, 1)
            };
            for (int i = 0; i < vertices.Count; i++)
            {
                vertices[i] = vertices[i].normalized;
            }

            var triangles = new List<int>
            {
                0, 11, 5,  0, 5, 1,  0, 1, 7,  0, 7, 10,  0, 10, 11,
                1, 5, 9,   5, 11, 4, 11, 10, 2, 10, 7, 6,  7, 1, 8,
                3, 9, 4,   3, 4, 2,  3, 2, 6,   3, 6, 8,   3, 8, 9,
                4, 9, 5,   2, 4, 11, 6, 2, 10,  8, 6, 7,   9, 8, 1
            };

            // Caches one midpoint vertex per edge, keyed by the (order-independent) pair of
            // endpoint indices, so adjacent triangles sharing an edge reuse the same new
            // vertex instead of creating a duplicate (which would leave visible seams/cracks).
            var midpointCache = new Dictionary<long, int>();
            int GetMidpointIndex(int a, int b)
            {
                long key = a < b ? ((long)a << 32) + b : ((long)b << 32) + a;
                if (midpointCache.TryGetValue(key, out int existingIndex)) return existingIndex;

                Vector3 midpoint = ((vertices[a] + vertices[b]) * 0.5f).normalized;
                vertices.Add(midpoint);
                int newIndex = vertices.Count - 1;
                midpointCache[key] = newIndex;
                return newIndex;
            }

            for (int s = 0; s < subdivisions; s++)
            {
                var subdividedTriangles = new List<int>(triangles.Count * 4);
                for (int i = 0; i < triangles.Count; i += 3)
                {
                    int a = triangles[i];
                    int b = triangles[i + 1];
                    int c = triangles[i + 2];
                    int ab = GetMidpointIndex(a, b);
                    int bc = GetMidpointIndex(b, c);
                    int ca = GetMidpointIndex(c, a);

                    subdividedTriangles.Add(a); subdividedTriangles.Add(ab); subdividedTriangles.Add(ca);
                    subdividedTriangles.Add(b); subdividedTriangles.Add(bc); subdividedTriangles.Add(ab);
                    subdividedTriangles.Add(c); subdividedTriangles.Add(ca); subdividedTriangles.Add(bc);
                    subdividedTriangles.Add(ab); subdividedTriangles.Add(bc); subdividedTriangles.Add(ca);
                }
                triangles = subdividedTriangles;
            }

            // Simple spherical-projection UVs - unused by the untextured tinted material, but
            // present so the mesh always has a complete UV0 stream regardless of shader.
            var uvs = new List<Vector2>(vertices.Count);
            foreach (Vector3 vertex in vertices)
            {
                uvs.Add(new Vector2(
                    0.5f + Mathf.Atan2(vertex.z, vertex.x) / (2f * Mathf.PI),
                    0.5f - Mathf.Asin(Mathf.Clamp(vertex.y, -1f, 1f)) / Mathf.PI));
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Color ResolveColor(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var step in colorSteps)
                {
                    if (step.Name == name) return step.Value;
                }
            }
            return colorSteps[0].Value;
        }
    }

    // Keeps a shape on its target mini and turned to face the same way. Used for every visual
    // except the sphere: the flat outlines, their filled forms, and the extruded solids all have
    // the same needs - follow the mini, apply its yaw, toggle a handful of renderers with its
    // visibility, and free the materials afterwards.
    public class AuraShapeFollower : MonoBehaviour
    {
        public CreatureBoardAsset Target;
        public float HeightOffset;
        public float FacingOffsetDegrees;
        public Material SurfaceMaterial;
        public Material LineMaterial;
        public Action OnTargetLost;

        private Renderer[] renderers;
        private bool? renderersVisible;

        // The creature's heading in degrees, ignoring any pitch/roll.
        //
        // Reads -right, NOT forward. The Rotator spins about its own LOCAL Z axis - decompiling
        // MovableBoardAsset.RotateTowards shows it calling Rotator.Rotate(0, 0, angle,
        // Space.Self) - which means local Z points vertically and Rotator.forward holds no
        // heading whatsoever. That same method measures the mini's current facing as the angle
        // to (-Rotator.right.x, 0, -Rotator.right.z), so this uses exactly the vector the game
        // itself treats as "the way this mini is looking".
        //
        // Projecting onto the ground plane and taking atan2 is also stable in poses where
        // reading eulerAngles.y directly is not - euler decomposition of a tilted rotation can
        // flip the yaw by 180 degrees.
        internal static float GetGroundedYawDegrees(Transform target)
        {
            if (target == null) return 0f;

            Vector3 facing = -target.right;
            var flat = new Vector3(facing.x, 0f, facing.z);

            if (flat.sqrMagnitude < 0.0001f) return 0f;

            return Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        }

        private void Awake()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            // Hidden until the first Update positions the root, so a rebuild can't flash a
            // full-size solid at the world origin for a frame.
            foreach (var r in renderers)
            {
                if (r != null) r.enabled = false;
            }
        }

        private void Update()
        {
            if (Target == null)
            {
                OnTargetLost?.Invoke();
                Destroy(gameObject);
                return;
            }

            transform.position = Target.transform.position + Vector3.up * HeightOffset;
            transform.rotation = Quaternion.Euler(
                0f, GetGroundedYawDegrees(Target.Rotator) + FacingOffsetDegrees, 0f);

            // Follow the mini's own visibility, so hiding a creature hides its aura too.
            //
            // IsVisible is ShaderState's combined flag: dropped in, AND not explicitly hidden,
            // AND not inside a hide volume, AND not culled by vision. GM mode exempts the
            // line-of-sight/vision parts but NOT the explicit hide toggle - CreaturePerception
            // Manager.UpdateExplicitHideState sets that on every client with no GM branch. So a
            // GM who hides a mini loses its aura too, even though they still see the mini
            // ghosted (that ghosting is decided GPU-side, not by this property). That matches
            // how the game treats its own creature-attached extras - FlyingIndicator is hidden
            // on ExplicitlyHidden, and the torch light keys off this same IsVisible.
            //
            // Fail closed when the shader state isn't valid yet: CreatureBoardAsset.IsVisible
            // returns true in that case, and PerformDeleteAssetNoSync clears ShaderStateRef
            // before destroying the object, so trusting it would flash a deleted hidden
            // creature's aura back on for the frame before this follower tears itself down.
            //
            // Toggling renderers rather than the GameObject is deliberate: this component lives
            // on that same GameObject, so deactivating it would stop Update() running and
            // nothing would ever turn the aura back on when the creature is unhidden. Guarded on
            // a change so a bubble's ~15 renderers aren't written to every single frame.
            bool visible = Target.ShaderStateRef.IsValid && Target.IsVisible;
            if (renderersVisible != visible)
            {
                foreach (var r in renderers)
                {
                    if (r != null) r.enabled = visible;
                }
                renderersVisible = visible;
            }
        }

        // RebuildRing creates fresh `new Material(...)` instances for every visual, and
        // destroying the GameObjects that reference them does NOT free them - they leak for the
        // rest of the session unless destroyed explicitly.
        private void OnDestroy()
        {
            if (SurfaceMaterial != null) Destroy(SurfaceMaterial);
            if (LineMaterial != null) Destroy(LineMaterial);
        }
    }

    // Keeps a bubble's root transform centered on its target mini every frame - only
    // position is updated, never rotation, so the sphere always stays upright regardless of
    // any tilt on the mini's own root transform (e.g. during flying animations). The sphere
    // mesh and all grid/equator LineRenderers are children of this same transform using
    // local (not world) coordinates, so Unity's normal parenting handles keeping them
    // aligned and scaled - only the root's position needs updating each frame.
    public class AuraBubbleFollower : MonoBehaviour
    {
        public CreatureBoardAsset Target;
        public float HeightOffset;
        public Material SurfaceMaterial;
        public Material LineMaterial;
        public Action OnTargetLost;

        // The sphere surface plus every equator/grid LineRenderer. Collected once - CreateBubble
        // builds all of them before adding this component, so they already exist by Awake.
        private Renderer[] renderers;
        // Null until the first visibility sync, so that sync always runs at least once.
        private bool? renderersVisible;

        private void Awake()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            // CreateBubble doesn't position the root, so it sits at the world origin until the
            // first Update. Start hidden so a rebuild can't flash a full-size sphere there for
            // a frame. renderersVisible stays null, so the first Update still syncs properly.
            foreach (var r in renderers)
            {
                if (r != null) r.enabled = false;
            }
        }

        private void Update()
        {
            if (Target == null)
            {
                OnTargetLost?.Invoke();
                Destroy(gameObject);
                return;
            }

            transform.position = Target.transform.position + Vector3.up * HeightOffset;

            // Follow the mini's own visibility, so hiding a creature hides its aura too. See
            // AuraShapeFollower.Update for what IsVisible actually covers (including that a GM
            // loses the aura on minis they've hidden), why the shader-state validity check is
            // needed, and why this toggles renderers rather than the GameObject. Guarded on a
            // change so a bubble's ~15 renderers aren't all written to every single frame.
            bool visible = Target.ShaderStateRef.IsValid && Target.IsVisible;
            if (renderersVisible != visible)
            {
                foreach (var r in renderers)
                {
                    if (r != null) r.enabled = visible;
                }
                renderersVisible = visible;
            }
        }

        // Same reasoning as AuraShapeFollower.OnDestroy - materials created with `new
        // Material(...)` aren't freed just by destroying the GameObjects that reference them.
        private void OnDestroy()
        {
            if (SurfaceMaterial != null) Destroy(SurfaceMaterial);
            if (LineMaterial != null) Destroy(LineMaterial);
        }
    }
}
