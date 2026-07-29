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
        private const string ShapeKey = "AuraPlugin.Shape";

        private const string ShapeFlat = "Flat";
        private const string ShapeBubble = "Bubble";

        private ConfigEntry<float> radiusStepFeetConfig;
        private ConfigEntry<float> radiusMaxFeetConfig;
        private ConfigEntry<float> feetPerTileConfig;
        private ConfigEntry<string> colorPresetsConfig;
        private ConfigEntry<float> ringHeightConfig;
        private ConfigEntry<float> ringWidthConfig;
        private ConfigEntry<float> bubbleSurfaceAlphaConfig;
        private ConfigEntry<float> bubbleGridAlphaConfig;
        private ConfigEntry<int> bubbleGridRingCountConfig;
        private ConfigEntry<int> bubbleGridMeridianCountConfig;
        private ConfigEntry<float> bubbleGridLineWidthConfig;

        private List<(string Name, Color Value)> colorSteps;

        // One visual GameObject per creature that currently has an aura switched on - either
        // a flat ring (AuraRingFollower) or a dome (AuraBubbleFollower), never both at once.
        private readonly Dictionary<string, GameObject> activeRings = new Dictionary<string, GameObject>();

        // Handles into the currently-open "Aura" submenu's buttons, so a click can update
        // the displayed number/color/shape in place without needing to close and reopen the menu.
        private MapMenuItem openRadiusItem;
        private MapMenuItem openColorItem;
        private MapMenuItem openShapeItem;
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
            bubbleSurfaceAlphaConfig = Config.Bind("Visual", "BubbleSurfaceAlpha", 0.18f, "Transparency of the bubble/dome shape's filled surface (0 = invisible, 1 = solid).");
            bubbleGridAlphaConfig = Config.Bind("Visual", "BubbleGridAlpha", 0.45f, "Transparency of the bubble's latitude/longitude grid lines.");
            bubbleGridRingCountConfig = Config.Bind("Visual", "BubbleGridRingCount", 2, "Number of latitude rings drawn on the bubble between the equator and the pole.");
            bubbleGridMeridianCountConfig = Config.Bind("Visual", "BubbleGridMeridianCount", 6, "Number of longitude arcs drawn over the top of the bubble.");
            bubbleGridLineWidthConfig = Config.Bind("Visual", "BubbleGridLineWidth", 0.015f,
                "Thickness of the bubble's equator/grid lines, in the bubble's own local (unit-radius) space - scales up with the radius, same as the real line's proportions in the reference screenshot.");

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

            openShapeItem = subMenu.AddItem(new MapMenu.ItemArgs
            {
                Title = "Aura Shape",
                ValueText = GetCurrentShape(identity),
                CloseMenuOnActivate = false,
                Action = (item, obj) => CycleShape(identity)
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

            // This button has CloseMenuOnActivate=true, so the Aura submenu (and its two
            // pooled MapMenuItems) is about to be recycled by the game. Drop our handles
            // now rather than leaving them dangling - otherwise, if the pooled objects get
            // reused for unrelated buttons before this text box is submitted, hitting "Set"
            // below would reflectively overwrite whatever button now occupies that slot.
            openRadiusItem = null;
            openColorItem = null;
            openShapeItem = null;
            openSubmenuIdentity = null;
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
                bool valid = float.TryParse(customRadiusInputText, NumberStyles.Float, CultureInfo.InvariantCulture, out float feet)
                    && !float.IsNaN(feet) && !float.IsInfinity(feet) && feet >= 0f;

                if (valid)
                {
                    AssetDataPlugin.SetInfo(customRadiusTargetIdentity, RadiusKey, feet.ToString(CultureInfo.InvariantCulture), false);

                    // If the Aura submenu is still open for this same mini, reflect the typed
                    // value on its "Aura Radius" button too, same as the click-to-step path.
                    if (openRadiusItem != null && customRadiusTargetIdentity == openSubmenuIdentity)
                    {
                        RefreshDisplayedValue(openRadiusItem, FormatRadius(feet));
                    }
                    showCustomRadiusInput = false;
                }
                // Invalid input (empty, non-numeric, negative, "Infinity"/"NaN"): leave the
                // box open so the player can correct it instead of silently discarding it.
                if (enterPressed) e.Use();
            }
            else if (cancelClicked || escapePressed)
            {
                showCustomRadiusInput = false;
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

            float radiusFeet = GetCurrentRadiusFeet(identity);
            if (radiusFeet <= 0f) return; // 0/off - no ring

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

        // Cached once and reused for every bubble - a plain hemisphere (flat circular base
        // at y=0, dome rising to y=1) with radius 1. Each bubble instance just scales this
        // shared mesh via its own transform rather than generating new geometry every time.
        private static Mesh unitHemisphereMesh;

        private GameObject CreateBubble(string identity, CreatureBoardAsset asset, float radiusUnits, Color color)
        {
            if (unitHemisphereMesh == null)
            {
                unitHemisphereMesh = BuildUnitHemisphereMesh();
            }

            // Deliberately NOT parented to the mini's own transform: TaleSpire's creature
            // root can tilt for flying-animation purposes, and inheriting that tilt would
            // tip the dome over instead of keeping it looking like an upright shield/bubble
            // (same reasoning as AuraRingFollower not parenting the flat ring). Everything
            // under this root uses local/unit-space coordinates and gets scaled via
            // root.transform.localScale, with only position updated per frame.
            var root = new GameObject("AuraPlugin_Bubble_" + identity);
            root.transform.localScale = Vector3.one * radiusUnits;

            var surfaceObject = new GameObject("Surface");
            surfaceObject.transform.SetParent(root.transform, false);
            surfaceObject.AddComponent<MeshFilter>().mesh = unitHemisphereMesh;
            var surfaceMaterial = new Material(Shader.Find("Sprites/Default"))
            {
                color = new Color(color.r, color.g, color.b, bubbleSurfaceAlphaConfig.Value)
            };
            surfaceObject.AddComponent<MeshRenderer>().material = surfaceMaterial;

            // All grid/equator lines share one material and use their own LineRenderer
            // startColor/endColor for tinting, same pattern as the flat ring - avoids
            // creating a separate material instance per line.
            var lineMaterial = new Material(Shader.Find("Sprites/Default"));

            AddBubbleLine(root.transform, lineMaterial, BuildUnitCircle(64, 0f, 1f), color, loop: true);

            // Clamped at both ends: these directly drive a per-iteration GameObject+LineRenderer
            // creation loop, so an extreme value (mistyped or hand-edited in the config file)
            // shouldn't be able to hang the client trying to instantiate hundreds of them.
            int latRings = Mathf.Clamp(bubbleGridRingCountConfig.Value, 0, 12);
            Color gridColor = new Color(1f, 1f, 1f, bubbleGridAlphaConfig.Value);
            for (int i = 1; i <= latRings; i++)
            {
                float theta = (Mathf.PI / 2f) * i / (latRings + 1);
                AddBubbleLine(root.transform, lineMaterial, BuildUnitCircle(64, Mathf.Sin(theta), Mathf.Cos(theta)), gridColor, loop: true);
            }

            int meridians = Mathf.Clamp(bubbleGridMeridianCountConfig.Value, 0, 24);
            for (int i = 0; i < meridians; i++)
            {
                float phi = Mathf.PI * i / Mathf.Max(1, meridians);
                AddBubbleLine(root.transform, lineMaterial, BuildUnitMeridian(16, phi), gridColor, loop: false);
            }

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

        // A longitude line: an arc from the equator up over the pole and back down to the
        // equator on the opposite side (phi + 180 degrees) - one continuous line rather than
        // two separate quarter-arcs. The pole point is shared between the two halves (added
        // once, at the end of the first loop) so there's no discontinuity or repeated point.
        private static Vector3[] BuildUnitMeridian(int segmentsPerSide, float phi)
        {
            var points = new Vector3[segmentsPerSide * 2 + 1];
            int index = 0;
            for (int i = 0; i <= segmentsPerSide; i++)
            {
                float theta = (Mathf.PI / 2f) * i / segmentsPerSide;
                float y = Mathf.Sin(theta);
                float r = Mathf.Cos(theta);
                points[index++] = new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
            }
            float farPhi = phi + Mathf.PI;
            for (int i = segmentsPerSide - 1; i >= 0; i--)
            {
                float theta = (Mathf.PI / 2f) * i / segmentsPerSide;
                float y = Mathf.Sin(theta);
                float r = Mathf.Cos(theta);
                points[index++] = new Vector3(r * Mathf.Cos(farPhi), y, r * Mathf.Sin(farPhi));
            }
            return points;
        }

        // Builds a unit hemisphere (radius 1, flat circular base at y=0, apex at y=1) as a
        // standard UV-sphere grid restricted to the upper half. No bottom cap - the base
        // sits at ground level so it's never visible anyway, and skipping it halves the
        // triangle count for no visible difference.
        private static Mesh BuildUnitHemisphereMesh()
        {
            const int latSegments = 8;
            const int lonSegments = 24;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            for (int lat = 0; lat <= latSegments; lat++)
            {
                float theta = (Mathf.PI / 2f) * lat / latSegments;
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

            var mesh = new Mesh { name = "AuraPlugin_UnitHemisphere" };
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
        public Action OnTargetLost;

        private void Update()
        {
            if (Target == null)
            {
                OnTargetLost?.Invoke();
                Destroy(gameObject);
                return;
            }

            transform.position = Target.transform.position + Vector3.up * HeightOffset;
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
