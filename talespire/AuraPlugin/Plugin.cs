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
    [BepInPlugin(Guid, "AuraPlugin", "1.0.0")]
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

        private List<(string Name, Color Value)> colorSteps;

        // One visual GameObject per creature that currently has an aura switched on - either
        // a flat ring (AuraRingFollower) or a dome (AuraBubbleFollower), never both at once.
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
            colorPresetsConfig = Config.Bind("Presets", "ColorSteps", "Gold:#FFD70066,Red:#FF000066,Blue:#1E90FF66,Green:#32CD3266,Purple:#9370DB66",
                "Aura color cycle as Name:RRGGBBAA pairs, comma separated.");
            ringHeightConfig = Config.Bind("Visual", "RingHeightAboveBase", 0.05f, "How far above the tabletop the ring floats, in board units.");
            ringWidthConfig = Config.Bind("Visual", "RingLineWidth", 0.05f, "Thickness of the aura ring line, in board units.");
            bubbleGridAlphaConfig = Config.Bind("Visual", "BubbleGridAlpha", 0.45f, "Transparency of the bubble's latitude/longitude grid lines.");
            bubbleGridRingCountConfig = Config.Bind("Visual", "BubbleGridRingCount", 2, "Number of latitude rings drawn on the bubble between the equator and the pole.");
            bubbleGridMeridianCountConfig = Config.Bind("Visual", "BubbleGridMeridianCount", 6, "Number of longitude arcs drawn over the top of the bubble.");
            bubbleGridLineWidthConfig = Config.Bind("Visual", "BubbleGridLineWidth", 0.015f,
                "Thickness of the bubble's equator/grid lines, in the bubble's own local (unit-radius) space - scales up with the radius, same as the real line's proportions in the reference screenshot.");
            opacityStepPercentConfig = Config.Bind("Presets", "OpacityStepPercent", 10f,
                "How much each click on the Aura Opacity button adds, on the displayed 0-100 scale.");
            opacityRealMaxPercentConfig = Config.Bind("Visual", "OpacityRealMaxPercent", 30f,
                "The Aura Opacity button always displays 0-100%, but that's a rescaled range: this is the actual surface alpha percent applied when the display reads 100%. " +
                "E.g. the default 30 means displayed 100% = 30% real alpha, displayed 50% = 15% real alpha, and so on - a linear rescale, not a cap.");

            ParsePresets();

            Sprite auraIcon = LoadIcon("aura.png");

            // Single top-level "Aura" entry on the character radial menu. Its Action opens
            // our own submenu (see OpenAuraSubmenu) rather than doing anything itself - this
            // is what groups all the aura controls under one branch instead of cluttering the
            // main right-click menu, the same way the native "Status"/"Emotes" buttons work.
            RadialUIPlugin.AddCustomButtonOnCharacter("AuraPlugin.Menu", new MapMenu.ItemArgs
            {
                Title = "Aura",
                Icon = auraIcon,
                CloseMenuOnActivate = false,
                FadeName = false,
                Action = (item, obj) => OpenAuraSubmenu()
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

        // Loads a PNG from this plugin's own "Icons" subfolder (deployed alongside the DLL
        // by the csproj's DeployToProfile target) into a Sprite for use as a radial menu
        // button icon. Returns null - falling back to the button's plain text label, which
        // MapMenuItem already handles fine - rather than throwing if the file's missing, since
        // a missing icon shouldn't be able to take down the whole plugin.
        private Sprite LoadIcon(string fileName)
        {
            string path = Path.Combine(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Icons"), fileName);
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

        // Called when the top-level "Aura" button is clicked. Opens a fresh ring of buttons
        // positioned on the targeted mini, mirroring how RadialUIPlugin's own submenu helper
        // (RadialSubmenu.DisplaySubmenu) works - except we keep the returned MapMenuItem
        // handles so we can refresh their text in place afterwards.
        private void OpenAuraSubmenu()
        {
            CreatureBoardAsset targetCreature = RadialUI.Talespire.RadialMenus.GetTargetCreature();
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
                Action = (item, obj) => CycleColor(identity)
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

        // Click handler for "Aura Color": same idea as StepRadius, cycling through the
        // configured color list instead of stepping a number.
        private void CycleColor(string identity)
        {
            string current = ResolveColorName(identity);
            int index = colorSteps.FindIndex(c => c.Name == current);
            index = (index + 1) % colorSteps.Count;

            AssetDataPlugin.SetInfo(identity, ColorKey, colorSteps[index].Name, false);

            if (openColorItem != null && identity == openSubmenuIdentity)
            {
                RefreshDisplayedValue(openColorItem, colorSteps[index].Name);
            }
        }

        private string GetCurrentShape(string identity)
        {
            string shape = AssetDataPlugin.ReadInfo(identity, ShapeKey);
            return shape == ShapeBubble ? ShapeBubble : ShapeFlat;
        }

        // Click handler for "Aura Shape": toggles between the flat ring and the 3D dome.
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
        // applied to the bubble's material, per OpacityRealMaxPercent - e.g. with the default
        // of 30, a displayed 100% becomes a real alpha of 0.30, displayed 50% becomes 0.15.
        // This is a straight linear rescale, not a clamp: 100% displayed always means "as
        // opaque as this table's aura auras are configured to ever get", not "capped at 30%".
        private float ResolveOpacityAlpha(string identity)
        {
            float displayedPercent = GetCurrentOpacityPercent(identity);
            float realMaxFraction = Mathf.Clamp01(opacityRealMaxPercentConfig.Value / 100f);
            return (displayedPercent / 100f) * realMaxFraction;
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

        private bool GetShowGridLines(string identity)
        {
            string stored = AssetDataPlugin.ReadInfo(identity, GridLinesKey);
            return stored != ToggleOff;
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

        // Cached once and reused for every bubble - a hemisphere (flat circular base at y=0,
        // dome rising to y=1) and a full sphere (centered on y=0, spanning -1..1), both with
        // radius 1. A grounded aura shows the hemisphere (sitting on the tabletop); a flying
        // one shows the full sphere (surrounding the mini instead of sitting under it with a
        // flat cut-off bottom hanging in mid-air). Each bubble instance scales one of these
        // shared meshes via its own transform rather than generating new geometry every time.
        private static Mesh unitHemisphereMesh;
        private static Mesh unitSphereMesh;

        private GameObject CreateBubble(string identity, CreatureBoardAsset asset, float radiusUnits, Color color)
        {
            if (unitHemisphereMesh == null)
            {
                unitHemisphereMesh = BuildUnitDomeMesh(0f, Mathf.PI / 2f, 8, 24, "AuraPlugin_UnitHemisphere");
            }
            if (unitSphereMesh == null)
            {
                // An icosphere, not a lat/lon UV-sphere like the hemisphere above: a UV-sphere
                // converges many thin triangles to a single point at each pole, and with a
                // semi-transparent material those overlapping triangles alpha-blend on top of
                // each other repeatedly, showing up as a visibly darker/distorted band right
                // where a pole sits. A hemisphere only has one pole, usually tucked away from
                // typical viewing angles, so it wasn't noticeable there - but a full sphere has
                // two, and one of them lands right on the visible silhouette from most angles.
                // An icosphere's triangles are evenly distributed with no polar singularity, so
                // there's no point for that artifact to form around.
                unitSphereMesh = BuildIcosphereMesh(3, "AuraPlugin_UnitSphere");
            }

            // Deliberately NOT parented to the mini's own transform: TaleSpire's creature
            // root can tilt for flying-animation purposes, and inheriting that tilt would
            // tip the dome over instead of keeping it looking like an upright shield/bubble
            // (same reasoning as AuraRingFollower not parenting the flat ring). Everything
            // under this root uses local/unit-space coordinates and gets scaled via
            // root.transform.localScale, with only position updated per frame.
            var root = new GameObject("AuraPlugin_Bubble_" + identity);
            root.transform.localScale = Vector3.one * radiusUnits;

            // `color`'s alpha already carries the resolved Aura Opacity value - RebuildRing
            // sets it once, centrally, so both this and CreateFlatRing use the same number.
            var surfaceMaterial = new Material(Shader.Find("Sprites/Default")) { color = color };
            // All grid/equator lines (on both variants) share one material and use their own
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

            var hemisphereVisual = new GameObject("HemisphereVisual");
            hemisphereVisual.transform.SetParent(root.transform, false);
            BuildBubbleVisual(hemisphereVisual.transform, unitHemisphereMesh, surfaceMaterial, lineMaterial,
                color, gridColor, latRings, meridians, mirrorLatRings: false, fullMeridian: false);

            var sphereVisual = new GameObject("SphereVisual");
            sphereVisual.transform.SetParent(root.transform, false);
            BuildBubbleVisual(sphereVisual.transform, unitSphereMesh, surfaceMaterial, lineMaterial,
                color, gridColor, latRings, meridians, mirrorLatRings: true, fullMeridian: true);

            var follower = root.AddComponent<AuraBubbleFollower>();
            follower.Target = asset;
            follower.HeightOffset = ringHeightConfig.Value;
            follower.HemisphereVisual = hemisphereVisual;
            follower.SphereVisual = sphereVisual;
            follower.SurfaceMaterial = surfaceMaterial;
            follower.LineMaterial = lineMaterial;
            follower.OnTargetLost = () =>
            {
                if (activeRings.TryGetValue(identity, out var current) && current == root)
                {
                    activeRings.Remove(identity);
                }
            };
            // Both visuals default to active (Unity's normal GameObject default) as soon as
            // they're created above, and would otherwise stay that way - overlapping - until
            // this component's own Update() next runs, which happens strictly after this
            // frame finishes rendering (CreateBubble runs from an event callback, not from
            // inside AuraBubbleFollower's own loop). Syncing once here immediately, rather
            // than waiting for the first Update(), avoids a guaranteed one-frame flash of
            // both the hemisphere and full sphere rendered on top of each other.
            follower.SyncVisibility();

            return root;
        }

        // Builds one complete visual (dome/sphere surface + equator + grid lines) under
        // `parent`. Used twice per bubble - once for the grounded hemisphere, once for the
        // flying full sphere - since AuraBubbleFollower just toggles which one is active
        // rather than trying to morph a single mesh's geometry at runtime.
        private void BuildBubbleVisual(Transform parent, Mesh mesh, Material surfaceMaterial, Material lineMaterial,
            Color equatorColor, Color gridColor, int latRings, int meridians, bool mirrorLatRings, bool fullMeridian)
        {
            var surfaceObject = new GameObject("Surface");
            surfaceObject.transform.SetParent(parent, false);
            surfaceObject.AddComponent<MeshFilter>().mesh = mesh;
            surfaceObject.AddComponent<MeshRenderer>().material = surfaceMaterial;

            // y=0 is the hemisphere's base (ground level) and also the sphere's true
            // equator (its vertical midpoint) - the same ring works as "the boundary" in
            // both cases without needing a separate y offset per variant.
            AddBubbleLine(parent, lineMaterial, BuildUnitCircle(64, 0f, 1f), equatorColor, loop: true);

            for (int i = 1; i <= latRings; i++)
            {
                float theta = (Mathf.PI / 2f) * i / (latRings + 1);
                AddBubbleLine(parent, lineMaterial, BuildUnitCircle(64, Mathf.Sin(theta), Mathf.Cos(theta)), gridColor, loop: true);
                if (mirrorLatRings)
                {
                    // Full sphere only: the same rings again below the equator, so the grid
                    // looks symmetric rather than only ever covering the upper half.
                    AddBubbleLine(parent, lineMaterial, BuildUnitCircle(64, -Mathf.Sin(theta), Mathf.Cos(theta)), gridColor, loop: true);
                }
            }

            for (int i = 0; i < meridians; i++)
            {
                float phi = Mathf.PI * i / Mathf.Max(1, meridians);
                Vector3[] points = fullMeridian
                    // A closed great-circle loop through both poles (the whole sphere).
                    ? BuildUnitMeridianArc(64, phi, 0f, 2f * Mathf.PI, includeEndpoint: false)
                    // An open arc: equator -> north pole -> equator on the opposite side,
                    // never dipping below y=0 (the hemisphere only has a top half).
                    : BuildUnitMeridianArc(32, phi, 0f, Mathf.PI, includeEndpoint: true);
                AddBubbleLine(parent, lineMaterial, points, gridColor, loop: fullMeridian);
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
        // unit-hemisphere space, i.e. before the bubble root's own scale is applied).
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
        // longitude (phi+180), t=3PI/2 is the south pole, t=2PI is back to the start. This
        // works because cos(t) (the horizontal radius) naturally goes negative past t=PI/2,
        // which flips the point to the opposite side of the same great circle - so one phi
        // value and a t-range covers both an open hemisphere arc (tStart=0, tEnd=PI) and a
        // closed full-sphere loop (tStart=0, tEnd=2*PI) with the same formula, no special
        // casing needed for "which side" a given point falls on.
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

        // Builds a unit dome (radius 1) as a standard UV-sphere grid restricted to the
        // latitude range [thetaStart, thetaEnd] - (0, PI/2) gives a hemisphere with a flat
        // base at y=0; (-PI/2, PI/2) gives a complete sphere centered on y=0. No bottom cap
        // on the hemisphere variant - its base sits at ground level so it's never visible
        // anyway, and skipping it halves the triangle count for no visible difference.
        private static Mesh BuildUnitDomeMesh(float thetaStart, float thetaEnd, int latSegments, int lonSegments, string name)
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            for (int lat = 0; lat <= latSegments; lat++)
            {
                float theta = Mathf.Lerp(thetaStart, thetaEnd, (float)lat / latSegments);
                float y = Mathf.Sin(theta);
                float ringRadius = Mathf.Cos(theta);
                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float phi = 2f * Mathf.PI * lon / lonSegments;
                    vertices.Add(new Vector3(ringRadius * Mathf.Cos(phi), y, ringRadius * Mathf.Sin(phi)));
                    uvs.Add(new Vector2((float)lon / lonSegments, (float)lat / latSegments));
                }
            }

            var triangles = new List<int>();
            int columns = lonSegments + 1;
            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    int i0 = lat * columns + lon;
                    int i1 = i0 + 1;
                    int i2 = i0 + columns;
                    int i3 = i2 + 1;

                    triangles.Add(i0); triangles.Add(i2); triangles.Add(i1);
                    triangles.Add(i1); triangles.Add(i2); triangles.Add(i3);
                }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Builds a unit icosphere (radius 1, centered on the origin) by subdividing a regular
        // icosahedron `subdivisions` times, normalizing each new vertex back onto the unit
        // sphere as it's created. Unlike BuildUnitDomeMesh's lat/lon grid, every vertex here
        // has (approximately) the same number of neighboring triangles - there's no pole
        // vertex that dozens of thin triangles converge onto, which is what a lat/lon sphere
        // needs for the full (both-poles) case and what causes the alpha-blending overdraw
        // artifact a translucent lat/lon sphere shows right at each pole.
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
    // position is updated, never rotation, so the dome always stays upright regardless of
    // any tilt on the mini's own root transform (e.g. during flying animations). The dome
    // mesh and all grid/equator LineRenderers are children of this same transform using
    // local (not world) coordinates, so Unity's normal parenting handles keeping them
    // aligned and scaled - no per-point recomputation needed like AuraRingFollower requires.
    public class AuraBubbleFollower : MonoBehaviour
    {
        public CreatureBoardAsset Target;
        public float HeightOffset;
        public Material SurfaceMaterial;
        public Material LineMaterial;
        public GameObject HemisphereVisual;
        public GameObject SphereVisual;
        public Action OnTargetLost;

        // Tracks which variant is currently active so Update only calls SetActive when the
        // flying state actually changes, rather than every single frame.
        private bool? lastIsFlying;

        private void Update()
        {
            if (Target == null)
            {
                OnTargetLost?.Invoke();
                Destroy(gameObject);
                return;
            }

            transform.position = Target.transform.position + Vector3.up * HeightOffset;
            SyncVisibility();
        }

        // Shows whichever variant matches Target's current flying state and hides the other,
        // skipping the SetActive calls entirely when nothing's changed since last time.
        // Called both from Update() (each frame, to react to the fly toggle) and once
        // immediately at creation time (see the comment at the CreateBubble call site) so
        // there's no frame where both variants are simultaneously visible.
        public void SyncVisibility()
        {
            if (Target == null) return;

            bool isFlying = Target.IsFlying;
            if (lastIsFlying == isFlying) return;

            HemisphereVisual.SetActive(!isFlying);
            SphereVisual.SetActive(isFlying);
            lastIsFlying = isFlying;
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
