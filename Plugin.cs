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
    // event to hook - see AuraRingFollower at the bottom of this file.
    // Keep this version string in sync with manifest.json's version_number - it's what shows
    // up in the BepInEx log and Config Manager when someone reports a bug.
    [BepInPlugin(Guid, "AuraPlugin", "1.0.5")]
    [BepInDependency("org.hollofox.plugins.RadialUIPlugin")]
    [BepInDependency("org.lordashes.plugins.assetdata")]
    public class AuraPlugin : BaseUnityPlugin
    {
        public const string Guid = "andrew.talespire.auraplugin";

        // AssetDataPlugin keys. Prefixed with our plugin name so our Subscribe("AuraPlugin.*")
        // wildcard below only ever sees our own data, not some other plugin's.
        private const string EnabledKey = "AuraPlugin.Enabled";
        private const string RadiusKey = "AuraPlugin.Radius";
        private const string ColorKey = "AuraPlugin.Color";
        private const string ShapeKey = "AuraPlugin.Shape";
        private const string OpacityKey = "AuraPlugin.Opacity";
        private const string GridLinesKey = "AuraPlugin.GridLines";

        private const string ShapeFlat = "Flat";
        private const string ShapeBubble = "Bubble";
        private const string ToggleOn = "On";
        private const string ToggleOff = "Off";

        private ConfigEntry<float> radiusStepFeetConfig;
        private ConfigEntry<float> radiusMaxFeetConfig;
        private ConfigEntry<float> feetPerTileConfig;
        private ConfigEntry<string> colorPresetsConfig;
        private ConfigEntry<float> ringHeightConfig;
        private ConfigEntry<float> ringWidthConfig;
        private ConfigEntry<float> bubbleGridAlphaConfig;
        private ConfigEntry<int> bubbleGridRingCountConfig;
        private ConfigEntry<int> bubbleGridMeridianCountConfig;
        private ConfigEntry<float> bubbleGridLineWidthConfig;
        private ConfigEntry<float> opacityStepPercentConfig;
        private ConfigEntry<float> opacityRealMaxPercentConfig;
        private ConfigEntry<string> spellPresetsConfig;
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
            public float OpacityPercent;
        }

        private List<SpellPreset> spellPresets;

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
        // a flat ring (AuraRingFollower) or a sphere (AuraBubbleFollower), never both at once.
        private readonly Dictionary<string, GameObject> activeRings = new Dictionary<string, GameObject>();

        // Handles into the currently-open "Aura" submenu's buttons, so a click can update
        // the displayed number/color/shape in place without needing to close and reopen the menu.
        private MapMenuItem openEnabledItem;
        private MapMenuItem openRadiusItem;
        private MapMenuItem openColorItem;
        private MapMenuItem openShapeItem;
        private MapMenuItem openOpacityItem;
        private MapMenuItem openGridLinesItem;
        private string openSubmenuIdentity;

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
        private string suppressRebuildForIdentity;

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
                "Paladin Aura:10:Gold:Flat:100,Spirit Guardians:15:Blue:Flat:100,Fireball:20:Red:Bubble:100,Darkness:15:Purple:Bubble:100,Silence:20:Blue:Bubble:100,Antilife Shell:10:Green:Bubble:100",
                "One-click spell presets, comma separated, each as Name:RadiusFeet:ColorName:Shape:OpacityPercent. " +
                "ColorName must be one of the names defined in ColorSteps above, and Shape must be Flat or Bubble. " +
                "Entries not matching that form are skipped with a warning in the log rather than silently applying something unintended.");

            colorRealMaxOverridesConfig = Config.Bind("Visual", "ColorRealMaxOverrides", "Black:50",
                "Per-colour overrides for OpacityRealMaxPercent, as Name:Percent pairs, comma separated. " +
                "A colour listed here uses its own ceiling instead of the table-wide OpacityRealMaxPercent; " +
                "anything not listed falls back to that value. Leave empty for no overrides.");

            ParsePresets();
            ParseColorRealMaxOverrides();
            // After ParsePresets, not before - preset validation rejects any preset naming a
            // colour that ColorSteps doesn't define, so colorSteps has to be populated first.
            ParseSpellPresets();

            Sprite auraIcon = LoadIcon("aura.png");

            // Single top-level "Aura" entry on the character radial menu. Its Action opens
            // our own submenu (see OpenAuraSubmenu) rather than doing anything itself - this
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
                Action = (item, obj) => OpenAuraSubmenu(RadialUI.Talespire.RadialMenus.GetTargetCreature())
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
        private void ParseSpellPresets()
        {
            spellPresets = new List<SpellPreset>();

            foreach (var entry in spellPresetsConfig.Value.Split(','))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                var pieces = entry.Split(':');
                if (pieces.Length != 5)
                {
                    Logger.LogWarning($"AuraPlugin: skipping spell preset '{entry.Trim()}' - expected 5 colon-separated fields (Name:RadiusFeet:ColorName:Shape:OpacityPercent), found {pieces.Length}.");
                    continue;
                }

                string name = pieces[0].Trim();
                if (name.Length == 0)
                {
                    Logger.LogWarning($"AuraPlugin: skipping spell preset '{entry.Trim()}' - the name field is empty.");
                    continue;
                }

                if (!float.TryParse(pieces[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float radiusFeet)
                    || float.IsNaN(radiusFeet) || float.IsInfinity(radiusFeet) || radiusFeet <= 0f)
                {
                    Logger.LogWarning($"AuraPlugin: skipping spell preset '{name}' - '{pieces[1].Trim()}' is not a radius greater than zero.");
                    continue;
                }

                // Must name an existing ColorSteps entry, because ColorKey stores a colour NAME
                // and ResolveColor/ResolveColorName look it up in that same list - an
                // unrecognised name stored there would silently render as the first colour.
                string colorName = pieces[2].Trim();
                if (!colorSteps.Exists(c => c.Name == colorName))
                {
                    Logger.LogWarning($"AuraPlugin: skipping spell preset '{name}' - colour '{colorName}' is not one of the names defined in the ColorSteps config.");
                    continue;
                }

                string shape = pieces[3].Trim();
                if (string.Equals(shape, ShapeBubble, StringComparison.OrdinalIgnoreCase))
                {
                    shape = ShapeBubble;
                }
                else if (string.Equals(shape, ShapeFlat, StringComparison.OrdinalIgnoreCase))
                {
                    shape = ShapeFlat;
                }
                else
                {
                    Logger.LogWarning($"AuraPlugin: skipping spell preset '{name}' - shape '{shape}' is not {ShapeFlat} or {ShapeBubble}.");
                    continue;
                }

                if (!float.TryParse(pieces[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float opacityPercent)
                    || float.IsNaN(opacityPercent) || float.IsInfinity(opacityPercent))
                {
                    Logger.LogWarning($"AuraPlugin: skipping spell preset '{name}' - '{pieces[4].Trim()}' is not a valid opacity percent.");
                    continue;
                }

                spellPresets.Add(new SpellPreset
                {
                    Name = name,
                    RadiusFeet = radiusFeet,
                    ColorName = colorName,
                    Shape = shape,
                    // Same fixed 0-100 display scale as everything else - see ResolveOpacityAlpha.
                    OpacityPercent = Mathf.Clamp(opacityPercent, 0f, 100f)
                });
            }

            Logger.LogInfo($"AuraPlugin: loaded {spellPresets.Count} spell preset(s).");
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
        private void OpenAuraSubmenu(CreatureBoardAsset targetCreature)
        {
            // Unity's overloaded null check - covers the mini having been deleted from the board
            // between opening the colour picker and picking a colour.
            if (targetCreature == null) return;

            string identity = targetCreature.CreatureId.ToString();
            openSubmenuIdentity = identity;

            Vector3 pos = targetCreature.transform.position + Vector3.up * RadialUI.Talespire.RadialMenus.GetHeightDiff();
            MapMenu subMenu = MapMenuManager.OpenMenu(pos, true);

            openEnabledItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Aura On/Off",
                ValueText = GetAuraEnabled(identity) ? ToggleOn : ToggleOff,
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => CycleAuraEnabled(identity)
            });

            // Sits second, directly under Aura On/Off: it's the fastest path to a fully
            // configured aura, so it wants to be high in the list, but not at the cost of
            // displacing the button existing users already reach for first. Omitted entirely
            // when no presets parsed successfully, rather than opening an empty submenu.
            if (spellPresets.Count > 0)
            {
                subMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = "Spell Presets...",
                    CloseMenuOnActivate = false,
                    FadeName = false,
                    Action = (item, obj) => OpenPresetsSubmenu(identity)
                });
            }

            openRadiusItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Aura Radius",
                ValueText = FormatRadius(GetCurrentRadiusFeet(identity)),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => StepRadius(identity)
            });

            openColorItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Aura Color",
                ValueText = ResolveColorName(identity),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => OpenColorPickerSubmenu(identity)
            });

            openShapeItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Aura Shape",
                ValueText = GetCurrentShape(identity),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => CycleShape(identity)
            });

            // Opacity/grid-lines only visibly do anything for the Bubble shape, but they're
            // still shown unconditionally for Flat too (rather than only shown once the shape
            // is already Bubble) - the native radial menu's button set is fixed for the
            // lifetime of one open submenu, so if these were only added when GetCurrentShape
            // was ALREADY Bubble at open time, switching Flat -> Bubble via "Aura Shape"
            // within that same open menu couldn't make them appear until the whole submenu
            // was closed and reopened. Showing them all from the start avoids that.
            openOpacityItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Aura Opacity",
                ValueText = FormatOpacity(GetCurrentOpacityPercent(identity)),
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => StepOpacity(identity)
            });

            openGridLinesItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Show Gridlines",
                ValueText = GetShowGridLines(identity) ? ToggleOn : ToggleOff,
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => CycleGridLines(identity)
            });

            // Separate entries for typing an exact number instead of clicking through the
            // step buttons. Close the submenu since the on-screen text box takes over input.
            // ValueText shows the current value at the moment the menu opens - these buttons
            // never refresh it afterward since CloseMenuOnActivate=true means the button
            // (and the whole submenu) is gone by the time a new value could be set anyway.
            subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Type Exact Radius...",
                ValueText = FormatRadius(GetCurrentRadiusFeet(identity)),
                CloseMenuOnActivate = true,
                FadeName = false,
                Action = (item, obj) => OpenCustomInput(CustomInputField.Radius, identity)
            });

            subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Type Exact Opacity...",
                ValueText = FormatOpacity(GetCurrentOpacityPercent(identity)),
                CloseMenuOnActivate = true,
                FadeName = false,
                Action = (item, obj) => OpenCustomInput(CustomInputField.Opacity, identity)
            });
        }

        // Opens a second nested menu listing the configured presets, one button each. Same
        // MapMenuManager.OpenMenu call the Aura submenu itself uses - nesting one more level
        // needs nothing special.
        private void OpenPresetsSubmenu(string identity)
        {
            CreatureBoardAsset targetCreature = RadialUI.Talespire.RadialMenus.GetTargetCreature();
            if (targetCreature == null) return;

            Vector3 pos = targetCreature.transform.position + Vector3.up * RadialUI.Talespire.RadialMenus.GetHeightDiff();
            MapMenu presetMenu = MapMenuManager.OpenMenu(pos, true);

            foreach (var preset in spellPresets)
            {
                // Captured into a local: the lambda below outlives this iteration, and closing
                // over the loop variable directly would have every button apply the last preset.
                SpellPreset captured = preset;
                presetMenu.AddItem(new MapMenu.ItemArgs
                {
                    Title = captured.Name,
                    ValueText = FormatRadius(captured.RadiusFeet),
                    CloseMenuOnActivate = true,
                    FadeName = false,
                    Action = (item, obj) => ApplyPreset(identity, captured)
                });
            }
        }

        // Writes every value a preset carries in one go, then rebuilds once - see
        // suppressRebuildForIdentity for why the intermediate rebuilds are skipped.
        private void ApplyPreset(string identity, SpellPreset preset)
        {
            suppressRebuildForIdentity = identity;
            try
            {
                AssetDataPlugin.SetInfo(identity, RadiusKey, preset.RadiusFeet.ToString(CultureInfo.InvariantCulture), false);
                AssetDataPlugin.SetInfo(identity, ColorKey, preset.ColorName, false);
                AssetDataPlugin.SetInfo(identity, ShapeKey, preset.Shape, false);
                AssetDataPlugin.SetInfo(identity, OpacityKey, preset.OpacityPercent.ToString(CultureInfo.InvariantCulture), false);
                // Last, and unconditional: picking a named spell is a clear statement that you
                // want to see it, so a preset turns the aura on rather than quietly configuring
                // an aura that stays invisible because it happened to be switched off.
                AssetDataPlugin.SetInfo(identity, EnabledKey, ToggleOn, false);
            }
            finally
            {
                // In a finally so a throw mid-write can't leave this creature permanently
                // unable to rebuild for the rest of the session.
                suppressRebuildForIdentity = null;
            }

            // The preset buttons all use CloseMenuOnActivate=true, so the menus are being
            // recycled by the game right now. Drop our handles to the Aura submenu's buttons
            // rather than leaving them dangling - the same hazard OpenCustomInput guards
            // against, where a pooled MapMenuItem reused for an unrelated button would get its
            // text reflectively overwritten by a later RefreshDisplayedValue call.
            openEnabledItem = null;
            openRadiusItem = null;
            openColorItem = null;
            openShapeItem = null;
            openOpacityItem = null;
            openGridLinesItem = null;
            openSubmenuIdentity = null;

            RebuildRing(identity);
        }

        // Explicit on/off state takes priority. If it's never been set, fall back to
        // exactly what the OLD "radius > 0 means visible, radius <= 0 means off" convention
        // would have shown, so a mini configured before this button existed doesn't change
        // visibility - in EITHER direction - purely from this upgrade. This has to check the
        // actual stored radius value, not just whether RadiusKey is present: a mini explicitly
        // turned off pre-upgrade still has RadiusKey="0" stored, and treating that presence
        // alone as "was on" would silently re-show an aura the player had deliberately hidden.
        private bool GetAuraEnabled(string identity)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, EnabledKey);
            if (!string.IsNullOrEmpty(stored)) return stored == ToggleOn;

            string radiusStr = AssetDataPlugin.ReadInfo(identity, RadiusKey);
            return !string.IsNullOrEmpty(radiusStr)
                && float.TryParse(radiusStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float storedFeet)
                && storedFeet > 0f;
        }

        // Click handler for "Aura On/Off": a dedicated toggle so switching an aura off
        // doesn't mean cycling Aura Radius all the way around - radius no longer has any
        // "off" value of its own (see StepRadius/GetCurrentRadiusFeet).
        private void CycleAuraEnabled(string identity)
        {
            bool next = !GetAuraEnabled(identity);

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
                if (string.IsNullOrEmpty(AssetDataPlugin.ReadInfo(identity, RadiusKey)))
                {
                    AssetDataPlugin.SetInfo(identity, RadiusKey, GetCurrentRadiusFeet(identity).ToString(CultureInfo.InvariantCulture), false);
                }
                if (string.IsNullOrEmpty(AssetDataPlugin.ReadInfo(identity, ColorKey)))
                {
                    AssetDataPlugin.SetInfo(identity, ColorKey, ResolveColorName(identity), false);
                }
            }

            AssetDataPlugin.SetInfo(identity, EnabledKey, next ? ToggleOn : ToggleOff, false);

            if (openEnabledItem != null && identity == openSubmenuIdentity)
            {
                RefreshDisplayedValue(openEnabledItem, next ? ToggleOn : ToggleOff);
            }
        }

        // Radius has no "off" value anymore - that's what the Aura On/Off button is for -
        // so an unset, unparsable, or stale pre-On/Off-button "0" value all fall back to the
        // smallest step instead of 0.
        private float GetCurrentRadiusFeet(string identity)
        {
            string radiusStr = AssetDataPlugin.ReadInfo(identity, RadiusKey);
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
        private void StepRadius(string identity)
        {
            float current = GetCurrentRadiusFeet(identity);
            float step = Mathf.Max(0.1f, radiusStepFeetConfig.Value);
            float max = Mathf.Max(step, radiusMaxFeetConfig.Value);

            float next = current + step;
            if (next > max + 0.001f) next = step;

            AssetDataPlugin.SetInfo(identity, RadiusKey, next.ToString(CultureInfo.InvariantCulture), false);

            if (openRadiusItem != null && identity == openSubmenuIdentity)
            {
                RefreshDisplayedValue(openRadiusItem, FormatRadius(next));
            }
        }

        // Click handler for "Aura Color". Opens a nested menu with one button per configured
        // colour, each icon'd with a filled circle of that colour - replacing the old
        // click-to-cycle behaviour, which needed up to seven clicks to reach the colour you
        // wanted and gave no preview of what was coming next.
        private void OpenColorPickerSubmenu(string identity)
        {
            CreatureBoardAsset targetCreature = RadialUI.Talespire.RadialMenus.GetTargetCreature();
            if (targetCreature == null) return;

            Vector3 pos = targetCreature.transform.position + Vector3.up * RadialUI.Talespire.RadialMenus.GetHeightDiff();
            MapMenu colorMenu = MapMenuManager.OpenMenu(pos, true);

            // Drop the Aura submenu's button handles before wiring up the picker. Whether the
            // parent menu survives underneath a nested one isn't something we control, so its
            // pooled MapMenuItems may be recycled for unrelated buttons from here on - the same
            // hazard OpenCustomInput and ApplyPreset guard against. Nothing below calls
            // RefreshDisplayedValue, so there's nothing to lose by clearing them.
            openEnabledItem = null;
            openRadiusItem = null;
            openColorItem = null;
            openShapeItem = null;
            openOpacityItem = null;
            openGridLinesItem = null;
            openSubmenuIdentity = null;

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
                    Action = (item, obj) => SetColorAndReturn(identity, captured.Name, targetCreature)
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
        private void SetColorAndReturn(string identity, string colorName, CreatureBoardAsset targetCreature)
        {
            AssetDataPlugin.SetInfo(identity, ColorKey, colorName, false);

            MapMenuManager.ForceCloseAll();
            // Reopening rebuilds the submenu from current state, so the Aura Color button comes
            // back showing the colour just picked - no RefreshDisplayedValue call needed.
            OpenAuraSubmenu(targetCreature);
        }

        private string GetCurrentShape(string identity)
        {
            string shape = AssetDataPlugin.ReadInfo(identity, ShapeKey);
            return shape == ShapeBubble ? ShapeBubble : ShapeFlat;
        }

        // Click handler for "Aura Shape": toggles between the flat ring and the 3D sphere.
        private void CycleShape(string identity)
        {
            string next = GetCurrentShape(identity) == ShapeFlat ? ShapeBubble : ShapeFlat;

            AssetDataPlugin.SetInfo(identity, ShapeKey, next, false);

            if (openShapeItem != null && identity == openSubmenuIdentity)
            {
                RefreshDisplayedValue(openShapeItem, next);
            }
        }

        // The displayed/stored opacity is always on a 0-100 scale, regardless of how low
        // OpacityRealMaxPercent is configured - see ResolveOpacityAlpha for where that config
        // actually gets applied. Defaults new auras to 100% (i.e. the real max) rather than
        // some fraction of it, so a freshly-toggled-on bubble starts at its intended opacity.
        private float GetCurrentOpacityPercent(string identity)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, OpacityKey);
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
        private float ResolveOpacityAlpha(string identity)
        {
            float displayedPercent = GetCurrentOpacityPercent(identity);
            float realMaxFraction = Mathf.Clamp01(ResolveColorRealMaxPercent(ResolveColorName(identity)) / 100f);
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
        private void StepOpacity(string identity)
        {
            float current = GetCurrentOpacityPercent(identity);
            float step = Mathf.Clamp(opacityStepPercentConfig.Value, 0.5f, 100f);

            float next = current + step;
            if (next > 100f + 0.001f) next = 0f;

            AssetDataPlugin.SetInfo(identity, OpacityKey, next.ToString(CultureInfo.InvariantCulture), false);

            if (openOpacityItem != null && identity == openSubmenuIdentity)
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
        private bool GetShowGridLines(string identity)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, GridLinesKey);
            return stored == ToggleOn;
        }

        // Click handler for "Show Gridlines": toggles the latitude/longitude grid lines on
        // the bubble on or off. The equator ring stays visible either way - it's the primary
        // boundary marker, more like the flat ring's outline than "grid" decoration.
        private void CycleGridLines(string identity)
        {
            bool next = !GetShowGridLines(identity);
            AssetDataPlugin.SetInfo(identity, GridLinesKey, next ? ToggleOn : ToggleOff, false);

            if (openGridLinesItem != null && identity == openSubmenuIdentity)
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
        private void OpenCustomInput(CustomInputField field, string identity)
        {
            customInputField = field;
            customInputTargetIdentity = identity;
            customInputText = field == CustomInputField.Radius
                ? GetCurrentRadiusFeet(identity).ToString("0.#", CultureInfo.InvariantCulture)
                : GetCurrentOpacityPercent(identity).ToString("0", CultureInfo.InvariantCulture);
            showCustomInput = true;

            // Both callers have CloseMenuOnActivate=true, so the Aura submenu (and its
            // pooled MapMenuItems) is about to be recycled by the game. Drop our handles
            // now rather than leaving them dangling - otherwise, if the pooled objects get
            // reused for unrelated buttons before this text box is submitted, hitting "Set"
            // below would reflectively overwrite whatever button now occupies that slot.
            openEnabledItem = null;
            openRadiusItem = null;
            openColorItem = null;
            openShapeItem = null;
            openOpacityItem = null;
            openGridLinesItem = null;
            openSubmenuIdentity = null;
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
                    if (isRadius)
                    {
                        AssetDataPlugin.SetInfo(customInputTargetIdentity, RadiusKey, value.ToString(CultureInfo.InvariantCulture), false);
                        if (openRadiusItem != null && customInputTargetIdentity == openSubmenuIdentity)
                        {
                            RefreshDisplayedValue(openRadiusItem, FormatRadius(value));
                        }
                    }
                    else
                    {
                        AssetDataPlugin.SetInfo(customInputTargetIdentity, OpacityKey, value.ToString(CultureInfo.InvariantCulture), false);
                        if (openOpacityItem != null && customInputTargetIdentity == openSubmenuIdentity)
                        {
                            RefreshDisplayedValue(openOpacityItem, FormatOpacity(value));
                        }
                    }
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
        private string ResolveColorName(string identity)
        {
            string name = AssetDataPlugin.ReadInfo(identity, ColorKey);
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
            // Mid-preset writes for this one creature are skipped; ApplyPreset rebuilds once
            // itself once every key is written. Everything else still rebuilds immediately.
            if (change.source != null && change.source == suppressRebuildForIdentity) return;

            RebuildRing(change.source);
        }

        // Destroys and recreates the ring GameObject for one creature based on its current
        // AssetDataPlugin state. Simpler than trying to update an existing ring in place,
        // and toggling an aura on/off is rare enough that the extra object churn doesn't matter.
        private void RebuildRing(string identity)
        {
            if (activeRings.TryGetValue(identity, out var existingRing) && existingRing != null)
            {
                Destroy(existingRing);
            }
            activeRings.Remove(identity);

            if (!GetAuraEnabled(identity)) return; // switched off via Aura On/Off - no ring

            float radiusFeet = GetCurrentRadiusFeet(identity);

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

            string colorName = AssetDataPlugin.ReadInfo(identity, ColorKey);
            Color color = ResolveColor(colorName);
            // Aura Opacity applies to both shapes, not just the bubble - it replaces the
            // color preset's own baked-in alpha byte entirely (rather than multiplying with
            // it) so the two controls don't compound in a way that's hard to reason about.
            // Computed once here rather than separately in CreateFlatRing/CreateBubble so
            // both shapes are guaranteed to use the exact same resolved alpha.
            color.a = ResolveOpacityAlpha(identity);

            // Our radius is stored in feet; the board's own units are tiles, so convert
            // using the configured feet-per-tile scale (defaults to the usual 5ft/tile).
            float radiusUnits = radiusFeet / Mathf.Max(0.01f, feetPerTileConfig.Value);

            GameObject visual = GetCurrentShape(identity) == ShapeBubble
                ? CreateBubble(identity, asset, radiusUnits, color)
                : CreateFlatRing(identity, asset, radiusUnits, color);

            activeRings[identity] = visual;
        }

        private GameObject CreateFlatRing(string identity, CreatureBoardAsset asset, float radiusUnits, Color color)
        {
            var ringObject = new GameObject("AuraPlugin_Ring_" + identity);
            var lineRenderer = ringObject.AddComponent<LineRenderer>();
            lineRenderer.useWorldSpace = true;
            lineRenderer.loop = true;
            lineRenderer.positionCount = 64;
            lineRenderer.startWidth = lineRenderer.endWidth = ringWidthConfig.Value;
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            lineRenderer.startColor = lineRenderer.endColor = color;

            var follower = ringObject.AddComponent<AuraRingFollower>();
            follower.Target = asset;
            follower.RadiusUnits = radiusUnits;
            follower.HeightOffset = ringHeightConfig.Value;
            // Only remove our own dictionary entry, not whatever might have replaced it -
            // a stale follower's delayed cleanup shouldn't be able to evict a newer ring
            // that RebuildRing has since created for the same identity.
            follower.OnTargetLost = () =>
            {
                if (activeRings.TryGetValue(identity, out var current) && current == ringObject)
                {
                    activeRings.Remove(identity);
                }
            };

            return ringObject;
        }

        // Cached once and reused for every bubble - a full sphere (centered on y=0, spanning
        // -1..1), radius 1. Each bubble instance scales this shared mesh via its own transform
        // rather than generating new geometry every time.
        private static Mesh unitSphereMesh;

        private GameObject CreateBubble(string identity, CreatureBoardAsset asset, float radiusUnits, Color color)
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
            // (same reasoning as AuraRingFollower not parenting the flat ring). Everything
            // under this root uses local/unit-space coordinates and gets scaled via
            // root.transform.localScale, with only position updated per frame.
            var root = new GameObject("AuraPlugin_Bubble_" + identity);
            root.transform.localScale = Vector3.one * radiusUnits;

            // `color`'s alpha already carries the resolved Aura Opacity value - RebuildRing
            // sets it once, centrally, so both this and CreateFlatRing use the same number.
            var surfaceMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };
            // All grid/equator lines share one material and use their own
            // LineRenderer startColor/endColor for tinting, same pattern as the flat ring -
            // avoids creating a separate material instance per line.
            var lineMaterial = new Material(Shader.Find("Sprites/Default"));

            // Clamped at both ends: these directly drive a per-iteration GameObject+LineRenderer
            // creation loop, so an extreme value (mistyped or hand-edited in the config file)
            // shouldn't be able to hang the client trying to instantiate hundreds of them.
            // Forced to 0 when grid lines are toggled off for this creature - the equator
            // ring (added unconditionally in BuildBubbleVisual) stays either way.
            bool showGrid = GetShowGridLines(identity);
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
                if (activeRings.TryGetValue(identity, out var current) && current == root)
                {
                    activeRings.Remove(identity);
                }
            };

            return root;
        }

        // Builds one complete visual (sphere surface + equator + grid lines) under `parent`.
        private void BuildBubbleVisual(Transform parent, Mesh mesh, Material surfaceMaterial, Material lineMaterial,
            Color equatorColor, Color gridColor, int latRings, int meridians)
        {
            var surfaceObject = new GameObject("Surface");
            surfaceObject.transform.SetParent(parent, false);
            surfaceObject.AddComponent<MeshFilter>().mesh = mesh;
            surfaceObject.AddComponent<MeshRenderer>().material = surfaceMaterial;

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

    // Keeps a ring's LineRenderer centered on its target mini every frame. TaleSpire doesn't
    // expose a "creature moved" event to plugins, so polling position each frame is the only
    // way to make the ring follow a mini being dragged around the board.
    public class AuraRingFollower : MonoBehaviour
    {
        public CreatureBoardAsset Target;
        public float RadiusUnits;
        public float HeightOffset;
        public Action OnTargetLost;

        private LineRenderer lineRenderer;

        // Unit circle points computed once in Awake and reused every frame - only the
        // center (Target's position) changes, not the shape, so there's no need to
        // recompute the trig every Update.
        private Vector3[] unitCircle;

        private void Awake()
        {
            lineRenderer = GetComponent<LineRenderer>();
            // CreateFlatRing sizes the LineRenderer but never fills in its points, so until the
            // first Update every position is still the origin. Start disabled so a rebuild
            // (any radius/colour/shape change) can't flash a degenerate ring at the board
            // origin for a frame - including on creatures that are meant to be hidden.
            lineRenderer.enabled = false;
            int count = lineRenderer.positionCount;
            unitCircle = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                float angle = i * Mathf.PI * 2f / count;
                unitCircle[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            }
        }

        private void Update()
        {
            // Target gets destroyed (Unity's overloaded null-check) if the mini is removed
            // from the board - clean up our own ring rather than leaving it floating in place.
            if (Target == null)
            {
                OnTargetLost?.Invoke();
                Destroy(gameObject);
                return;
            }

            // Follow the mini's own visibility, so hiding a creature hides its aura too.
            //
            // IsVisible is ShaderState's combined flag: dropped in, AND not explicitly hidden,
            // AND not inside a hide volume, AND not culled by vision. GM mode exempts the
            // line-of-sight/vision parts but NOT the explicit hide toggle - CreaturePerception
            // Manager.UpdateExplicitHideState sets that on every client with no GM branch. So
            // a GM who hides a mini loses its aura too, even though they still see the mini
            // ghosted (that ghosting is decided GPU-side, not by this property). That matches
            // how the game treats its own creature-attached extras - FlyingIndicator is hidden
            // on ExplicitlyHidden, and the torch light keys off this same IsVisible.
            //
            // Fail closed when the shader state isn't valid yet: CreatureBoardAsset.IsVisible
            // returns true in that case, and PerformDeleteAssetNoSync clears ShaderStateRef
            // before destroying the object, so trusting it would flash a deleted hidden
            // creature's aura back on for the frame before this follower tears itself down.
            //
            // Toggling the renderer rather than the GameObject is deliberate: this component
            // lives on that same GameObject, so deactivating it would stop Update() running
            // and nothing would ever turn the aura back on when the creature is unhidden.
            lineRenderer.enabled = Target.ShaderStateRef.IsValid && Target.IsVisible;

            // Positions are updated even while hidden, so unhiding can't show one frame of
            // ring left behind at wherever the mini used to be.
            Vector3 center = Target.transform.position + Vector3.up * HeightOffset;
            for (int i = 0; i < unitCircle.Length; i++)
            {
                lineRenderer.SetPosition(i, center + unitCircle[i] * RadiusUnits);
            }
        }

        // RebuildRing creates a fresh `new Material(...)` for every ring (every radius
        // step and color change, not just on/off toggles), since it's simplest to just
        // destroy-and-recreate the whole ring rather than update one in place. Destroying
        // the GameObject/LineRenderer does not free that material - it has to be destroyed
        // explicitly or it leaks for the rest of the session.
        private void OnDestroy()
        {
            if (lineRenderer != null && lineRenderer.material != null)
            {
                Destroy(lineRenderer.material);
            }
        }
    }

    // Keeps a bubble's root transform centered on its target mini every frame - only
    // position is updated, never rotation, so the sphere always stays upright regardless of
    // any tilt on the mini's own root transform (e.g. during flying animations). The sphere
    // mesh and all grid/equator LineRenderers are children of this same transform using
    // local (not world) coordinates, so Unity's normal parenting handles keeping them
    // aligned and scaled - no per-point recomputation needed like AuraRingFollower requires.
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
            // AuraRingFollower.Update for what IsVisible actually covers (including that a GM
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

        // Same reasoning as AuraRingFollower.OnDestroy - materials created with `new
        // Material(...)` aren't freed just by destroying the GameObjects that reference them.
        private void OnDestroy()
        {
            if (SurfaceMaterial != null) Destroy(SurfaceMaterial);
            if (LineMaterial != null) Destroy(LineMaterial);
        }
    }
}
