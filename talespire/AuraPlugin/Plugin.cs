using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using Bounce.Unmanaged;
using LordAshes;
using RadialUI;
using UnityEngine;

namespace AuraPlugin
{
    [BepInPlugin(Guid, "AuraPlugin", "1.0.0")]
    [BepInDependency("org.hollofox.plugins.RadialUIPlugin")]
    [BepInDependency("org.lordashes.plugins.assetdata")]
    public class AuraPlugin : BaseUnityPlugin
    {
        public const string Guid = "andrew.talespire.auraplugin";

        private const string RadiusKey = "AuraPlugin.Radius";
        private const string ColorKey = "AuraPlugin.Color";

        private ConfigEntry<string> radiusPresetsFeetConfig;
        private ConfigEntry<float> feetPerTileConfig;
        private ConfigEntry<string> colorPresetsConfig;
        private ConfigEntry<float> ringHeightConfig;
        private ConfigEntry<float> ringWidthConfig;

        private float[] radiusStepsFeet;
        private List<(string Name, Color Value)> colorSteps;

        // Local-only UI state so repeated clicks cycle through the preset lists.
        // The actual synced/persisted aura state lives in AssetDataPlugin.
        private readonly Dictionary<string, int> radiusCursor = new Dictionary<string, int>();
        private readonly Dictionary<string, int> colorCursor = new Dictionary<string, int>();

        private readonly Dictionary<string, GameObject> activeRings = new Dictionary<string, GameObject>();

        private void Awake()
        {
            radiusPresetsFeetConfig = Config.Bind("Presets", "RadiusStepsFeet", "0,5,10,15,20,30,60",
                "Radius cycle in feet. 0 means 'aura off'.");
            feetPerTileConfig = Config.Bind("Presets", "FeetPerTile", 5f,
                "Feet represented by one board tile/grid square. Match your table's ruler scale.");
            colorPresetsConfig = Config.Bind("Presets", "ColorSteps", "Gold:#FFD70066,Red:#FF000066,Blue:#1E90FF66,Green:#32CD3266,Purple:#9370DB66",
                "Aura color cycle as Name:RRGGBBAA pairs, comma separated.");
            ringHeightConfig = Config.Bind("Visual", "RingHeightAboveBase", 0.05f, "How far above the tabletop the ring floats, in board units.");
            ringWidthConfig = Config.Bind("Visual", "RingLineWidth", 0.05f, "Thickness of the aura ring line, in board units.");

            ParsePresets();

            RadialUIPlugin.AddCustomButtonOnCharacter("AuraPlugin.Radius", new MapMenu.ItemArgs
            {
                Title = "Aura Radius",
                ValueText = "Cycle",
                CloseMenuOnActivate = false,
                Action = (item, obj) => CycleRadius()
            }, (self, target) => true);

            RadialUIPlugin.AddCustomButtonOnCharacter("AuraPlugin.Color", new MapMenu.ItemArgs
            {
                Title = "Aura Color",
                ValueText = "Cycle",
                CloseMenuOnActivate = false,
                Action = (item, obj) => CycleColor()
            }, (self, target) => true);

            AssetDataPlugin.Subscribe("AuraPlugin.*", OnAuraDataChanged);

            Logger.LogInfo("AuraPlugin loaded.");
        }

        private void ParsePresets()
        {
            var feetParts = radiusPresetsFeetConfig.Value.Split(',');
            radiusStepsFeet = new float[feetParts.Length];
            for (int i = 0; i < feetParts.Length; i++)
            {
                float.TryParse(feetParts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out radiusStepsFeet[i]);
            }

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

        private void CycleRadius()
        {
            NGuid targetGuid = RadialUIPlugin.GetLastRadialTargetCreature();
            string identity = new CreatureGuid(targetGuid).ToString();

            int index = radiusCursor.TryGetValue(identity, out var existing) ? existing : 0;
            index = (index + 1) % radiusStepsFeet.Length;
            radiusCursor[identity] = index;

            AssetDataPlugin.SetInfo(identity, RadiusKey, radiusStepsFeet[index].ToString(CultureInfo.InvariantCulture), false);
        }

        private void CycleColor()
        {
            NGuid targetGuid = RadialUIPlugin.GetLastRadialTargetCreature();
            string identity = new CreatureGuid(targetGuid).ToString();

            int index = colorCursor.TryGetValue(identity, out var existing) ? existing : 0;
            index = (index + 1) % colorSteps.Count;
            colorCursor[identity] = index;

            AssetDataPlugin.SetInfo(identity, ColorKey, colorSteps[index].Name, false);
        }

        private void OnAuraDataChanged(AssetDataPlugin.DatumChange change)
        {
            RebuildRing(change.source);
        }

        private void RebuildRing(string identity)
        {
            if (activeRings.TryGetValue(identity, out var existingRing) && existingRing != null)
            {
                Destroy(existingRing);
            }
            activeRings.Remove(identity);

            string radiusStr = AssetDataPlugin.ReadInfo(identity, RadiusKey);
            float radiusFeet = 0f;
            if (!string.IsNullOrEmpty(radiusStr))
            {
                float.TryParse(radiusStr, NumberStyles.Float, CultureInfo.InvariantCulture, out radiusFeet);
            }
            if (radiusFeet <= 0f) return;

            if (!CreatureGuid.TryParse(identity, out var creatureId)) return;
            if (!CreaturePresenter.TryGetAsset(creatureId, out var asset) || asset == null) return;

            string colorName = AssetDataPlugin.ReadInfo(identity, ColorKey);
            Color color = ResolveColor(colorName);

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

    /// Keeps a ring's LineRenderer centered on its target mini every frame, since
    /// TaleSpire doesn't expose a movement event to hook instead.
    public class AuraRingFollower : MonoBehaviour
    {
        public CreatureBoardAsset Target;
        public float RadiusUnits;
        public float HeightOffset;
        public Action OnTargetLost;

        private LineRenderer lineRenderer;
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
