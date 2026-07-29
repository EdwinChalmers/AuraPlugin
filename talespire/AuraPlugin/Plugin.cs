using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const string RadiusKey = "AuraPlugin.Radius";
        private const string ColorKey = "AuraPlugin.Color";

        private ConfigEntry<float> radiusStepFeetConfig;
        private ConfigEntry<float> radiusMaxFeetConfig;
        private ConfigEntry<float> feetPerTileConfig;
        private ConfigEntry<string> colorPresetsConfig;
        private ConfigEntry<float> ringHeightConfig;
        private ConfigEntry<float> ringWidthConfig;

        private List<(string Name, Color Value)> colorSteps;

        // One ring GameObject per creature that currently has an aura switched on.
        private readonly Dictionary<string, GameObject> activeRings = new Dictionary<string, GameObject>();

        // Handles into the currently-open "Aura" submenu's buttons, so a click can update
        // the displayed number/color in place without needing to close and reopen the menu.
        private MapMenuItem openRadiusItem;
        private MapMenuItem openColorItem;
        private string openSubmenuIdentity;

        // On-screen text box state for typing an exact radius instead of clicking through
        // the +5ft steps. Drawn via OnGUI (Unity's old immediate-mode UI) - simple to do
        // without needing a Canvas/EventSystem set up just for one text field.
        private bool showCustomRadiusInput;
        private string customRadiusInputText = "";
        private string customRadiusTargetIdentity;

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
                "Radius wraps back to 0 (off) after exceeding this.");
            feetPerTileConfig = Config.Bind("Presets", "FeetPerTile", 5f,
                "Feet represented by one board tile/grid square. Match your table's ruler scale.");
            colorPresetsConfig = Config.Bind("Presets", "ColorSteps", "Gold:#FFD70066,Red:#FF000066,Blue:#1E90FF66,Green:#32CD3266,Purple:#9370DB66",
                "Aura color cycle as Name:RRGGBBAA pairs, comma separated.");
            ringHeightConfig = Config.Bind("Visual", "RingHeightAboveBase", 0.05f, "How far above the tabletop the ring floats, in board units.");
            ringWidthConfig = Config.Bind("Visual", "RingLineWidth", 0.05f, "Thickness of the aura ring line, in board units.");

            ParsePresets();

            // Single top-level "Aura" entry on the character radial menu. Its Action opens
            // our own submenu (see OpenAuraSubmenu) rather than doing anything itself - this
            // is what groups all the aura controls under one branch instead of cluttering the
            // main right-click menu, the same way the native "Status"/"Emotes" buttons work.
            RadialUIPlugin.AddCustomButtonOnCharacter("AuraPlugin.Menu", new MapMenu.ItemArgs
            {
                Title = "Aura",
                CloseMenuOnActivate = false,
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

            openRadiusItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Aura Radius",
                ValueText = FormatRadius(GetCurrentRadiusFeet(identity)),
                CloseMenuOnActivate = false,
                Action = (item, obj) => StepRadius(identity)
            });

            openColorItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Aura Color",
                ValueText = ResolveColorName(identity),
                CloseMenuOnActivate = false,
                Action = (item, obj) => CycleColor(identity)
            });

            // Separate entry for typing an exact number instead of clicking through +5ft
            // steps. Closes the submenu since the on-screen text box takes over input.
            subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Type Exact Radius...",
                CloseMenuOnActivate = true,
                Action = (item, obj) => OpenCustomRadiusInput(identity)
            });
        }

        private float GetCurrentRadiusFeet(string identity)
        {
            string radiusStr = AssetDataPlugin.ReadInfo(identity, RadiusKey);
            float feet = 0f;
            if (!string.IsNullOrEmpty(radiusStr))
            {
                float.TryParse(radiusStr, NumberStyles.Float, CultureInfo.InvariantCulture, out feet);
            }
            return feet;
        }

        private static string FormatRadius(float feet)
        {
            return feet <= 0f ? "Off" : feet.ToString("0.#", CultureInfo.InvariantCulture) + " ft";
        }

        // Click handler for "Aura Radius": adds one step, wrapping back to 0/off past the
        // configured max, then updates AssetDataPlugin (which syncs/persists it) and refreshes
        // the button's own displayed text so the change is visible immediately.
        private void StepRadius(string identity)
        {
            float current = GetCurrentRadiusFeet(identity);
            float step = Mathf.Max(0.1f, radiusStepFeetConfig.Value);
            float max = Mathf.Max(step, radiusMaxFeetConfig.Value);

            float next = current + step;
            if (next > max + 0.001f) next = 0f;

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

        private void OpenCustomRadiusInput(string identity)
        {
            customRadiusTargetIdentity = identity;
            string current = AssetDataPlugin.ReadInfo(identity, RadiusKey);
            customRadiusInputText = string.IsNullOrEmpty(current) ? "" : current;
            showCustomRadiusInput = true;
        }

        // Draws the "type an exact radius" box when showCustomRadiusInput is true.
        // OnGUI runs every frame regardless of whether the radial menu is open, hence the
        // early-out at the top.
        private void OnGUI()
        {
            if (!showCustomRadiusInput) return;

            const float width = 220f;
            const float height = 100f;
            var box = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);

            GUI.Box(box, "Aura Radius (feet)");
            GUI.SetNextControlName("AuraPlugin.CustomRadiusField");
            customRadiusInputText = GUI.TextField(new Rect(box.x + 10, box.y + 30, width - 20, 24), customRadiusInputText, 8);
            GUI.FocusControl("AuraPlugin.CustomRadiusField");

            bool setClicked = GUI.Button(new Rect(box.x + 10, box.y + 64, (width - 30) / 2, 24), "Set");
            bool cancelClicked = GUI.Button(new Rect(box.x + 20 + (width - 30) / 2, box.y + 64, (width - 30) / 2, 24), "Cancel");

            Event e = Event.current;
            bool enterPressed = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            bool escapePressed = e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape;

            if (setClicked || enterPressed)
            {
                if (float.TryParse(customRadiusInputText, NumberStyles.Float, CultureInfo.InvariantCulture, out float feet) && feet >= 0f)
                {
                    AssetDataPlugin.SetInfo(customRadiusTargetIdentity, RadiusKey, feet.ToString(CultureInfo.InvariantCulture), false);

                    // If the Aura submenu is still open for this same mini, reflect the typed
                    // value on its "Aura Radius" button too, same as the click-to-step path.
                    if (openRadiusItem != null && customRadiusTargetIdentity == openSubmenuIdentity)
                    {
                        RefreshDisplayedValue(openRadiusItem, FormatRadius(feet));
                    }
                }
                showCustomRadiusInput = false;
                if (enterPressed) e.Use();
            }
            else if (cancelClicked || escapePressed)
            {
                showCustomRadiusInput = false;
                if (escapePressed) e.Use();
            }
        }

        private string ResolveColorName(string identity)
        {
            string name = AssetDataPlugin.ReadInfo(identity, ColorKey);
            return string.IsNullOrEmpty(name) ? colorSteps[0].Name : name;
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

            float radiusFeet = GetCurrentRadiusFeet(identity);
            if (radiusFeet <= 0f) return; // 0/off - no ring

            if (!CreatureGuid.TryParse(identity, out var creatureId)) return;
            if (!CreaturePresenter.TryGetAsset(creatureId, out var asset) || asset == null) return;

            string colorName = AssetDataPlugin.ReadInfo(identity, ColorKey);
            Color color = ResolveColor(colorName);

            // Our radius is stored in feet; the board's own units are tiles, so convert
            // using the configured feet-per-tile scale (defaults to the usual 5ft/tile).
            float radiusUnits = radiusFeet / Mathf.Max(0.01f, feetPerTileConfig.Value);

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
            follower.OnTargetLost = () => activeRings.Remove(identity);

            activeRings[identity] = ringObject;
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
    }
}
