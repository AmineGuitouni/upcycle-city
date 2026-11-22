using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Bitmask selecting which real-world pollution causes are allowed to emit a given waste archetype.
    /// </summary>
    [Flags]
    public enum WasteSourceFlags
    {
        None = 0,
        Citizen = 1 << 0,
        ShopOverflow = 1 << 1,
        FactoryScrap = 1 << 2,
        UrbanDecay = 1 << 3,
        All = Citizen | ShopOverflow | FactoryScrap | UrbanDecay,
    }

    /// <summary>
    /// Data-driven catalog of physical litter &amp; bulky trash archetypes used by all Phase 2 pollution sources.
    /// Keeps trash spawning consistent, mobile-friendly, and properly layered/tagged/highlighted.
    /// </summary>
    [CreateAssetMenu(fileName = "WasteCatalog", menuName = "Upcycle City/Ecosystem/Waste Catalog")]
    public sealed class WasteCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string displayName = "Plastic Bottle";
            public WasteStream stream = WasteStream.Plastic;
            public WasteSourceFlags allowedSources = WasteSourceFlags.All;
            public GameObject visualPrefab;
            [Min(0.05f)] public float visualScale = 1f;
            [Min(0.1f)] public float pollutionWeight = 1f;
            [Min(1)] public int baseCoinValue = 6;
            [Min(0.1f)] public float mass = 0.5f;
            public bool isBulky;
            [Range(0.4f, 1f)] public float carrySpeedMultiplier = 1f;
            [Range(0f, 1.2f)] public float holdDistanceOffset;
        }

        [SerializeField] List<Entry> entries = new List<Entry>();

        readonly List<Entry> scratchPool = new List<Entry>(16);

        public IReadOnlyList<Entry> Entries => entries;

        public void SetEntries(List<Entry> newEntries)
        {
            entries = newEntries ?? new List<Entry>();
        }

        public static WasteSourceFlags ToFlag(WasteSourceKind kind)
        {
            switch (kind)
            {
                case WasteSourceKind.Citizen: return WasteSourceFlags.Citizen;
                case WasteSourceKind.ShopOverflow: return WasteSourceFlags.ShopOverflow;
                case WasteSourceKind.FactoryScrap: return WasteSourceFlags.FactoryScrap;
                case WasteSourceKind.UrbanDecay: return WasteSourceFlags.UrbanDecay;
                default: return WasteSourceFlags.All;
            }
        }

        /// <summary>Returns the index of <paramref name="entry"/> in this catalog, or -1 if not found.</summary>
        public int FindEntryIndex(Entry entry)
        {
            if (entry == null)
                return -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (ReferenceEquals(entries[i], entry))
                    return i;
            }
            return -1;
        }

        /// <summary>Finds a catalog entry matching <paramref name="displayName"/> and <paramref name="stream"/>, or -1.</summary>
        public int FindEntryIndex(string displayName, WasteStream stream)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e != null && e.stream == stream
                    && string.Equals(e.displayName, displayName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e != null && e.stream == stream)
                    return i;
            }
            return -1;
        }

        /// <summary>Picks a matching archetype for <paramref name="sourceKind"/> and spawns a live physical TrashItem.</summary>
        public TrashItem SpawnTrash(
            WasteSourceKind sourceKind,
            Vector3 worldPosition,
            Vector3 initialVelocity,
            Transform parent = null)
        {
            Entry entry = PickEntry(sourceKind);
            return GameAuthority.Current.SpawnTrash(this, entry, sourceKind, worldPosition, initialVelocity, parent);
        }

        public Entry PickEntry(WasteSourceKind sourceKind)
        {
            WasteSourceFlags flag = ToFlag(sourceKind);
            scratchPool.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e != null && (e.allowedSources & flag) != 0)
                    scratchPool.Add(e);
            }

            if (scratchPool.Count > 0)
                return scratchPool[UnityEngine.Random.Range(0, scratchPool.Count)];

            if (entries.Count > 0)
                return entries[UnityEngine.Random.Range(0, entries.Count)];

            return null;
        }

        /// <summary>Spawns a TrashItem through the active <see cref="GameAuthority"/> (local in solo, networked on host).</summary>
        public static TrashItem SpawnEntry(
            Entry entry,
            WasteSourceKind sourceKind,
            Vector3 worldPosition,
            Vector3 initialVelocity,
            Transform parent = null)
        {
            WasteCatalog catalog = CityEcosystemManager.Active != null ? CityEcosystemManager.Active.DefaultCatalog : null;
            return GameAuthority.Current.SpawnTrash(catalog, entry, sourceKind, worldPosition, initialVelocity, parent);
        }

        /// <summary>Creates a local physical TrashItem GameObject (used directly in solo play and tests).</summary>
        public static TrashItem SpawnEntryLocal(
            Entry entry,
            WasteSourceKind sourceKind,
            Vector3 worldPosition,
            Vector3 initialVelocity,
            Transform parent = null)
        {
            string itemName = entry != null ? entry.displayName : "Litter";
            WasteStream stream = entry != null ? entry.stream : WasteStream.Plastic;
            float weight = entry != null ? entry.pollutionWeight : 1f;
            int coinValue = entry != null ? entry.baseCoinValue : 6;
            float mass = entry != null ? entry.mass : 0.5f;
            bool bulky = entry != null && entry.isBulky;
            float carrySpeed = entry != null ? entry.carrySpeedMultiplier : 1f;
            float holdOffset = entry != null ? entry.holdDistanceOffset : 0f;

            var go = new GameObject($"Trash_{itemName.Replace(" ", "")}_{sourceKind}");
            go.SetActive(false);
            if (parent != null)
                go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            go.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);

            GameObject visual = null;
            if (entry != null && entry.visualPrefab != null)
            {
                visual = Instantiate(entry.visualPrefab, go.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one * entry.visualScale;

                // Remove any imported colliders from the FBX so we control a clean single BoxCollider
                Collider[] imported = visual.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < imported.Length; i++)
                    SafeDestroy(imported[i]);
            }
            else
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "FallbackVisual";
                visual.transform.SetParent(go.transform, false);
                visual.transform.localScale = bulky ? new Vector3(0.75f, 0.55f, 0.75f) : new Vector3(0.28f, 0.28f, 0.28f);
                SafeDestroy(visual.GetComponent<Collider>());
            }

            // Fit a clean BoxCollider from renderer bounds with mobile-friendly minimum grab hitbox
            Bounds localBounds = ComputeLocalBounds(go.transform, visual);
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = localBounds.center;
            Vector3 minSize = bulky ? new Vector3(0.45f, 0.35f, 0.45f) : new Vector3(0.22f, 0.20f, 0.22f);
            box.size = Vector3.Max(localBounds.size, minSize);

            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(0.15f, mass);
            rb.linearDamping = 0.25f;
            rb.angularDamping = 0.5f;

            TrashItem trash = go.AddComponent<TrashItem>();
            trash.Configure(itemName, stream, sourceKind, weight, coinValue, bulky, carrySpeed, holdOffset);

            go.AddComponent<FocusHighlight>();
            GameLayers.SetLayerRecursively(go, GameLayers.Grabbable);
            go.tag = GameTags.Trash;

            go.SetActive(true);

            if (Application.isPlaying)
            {
                rb.linearVelocity = initialVelocity;
                rb.angularVelocity = UnityEngine.Random.insideUnitSphere * (bulky ? 1.2f : 3.5f);
            }

            return trash;
        }

        static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        static Bounds ComputeLocalBounds(Transform root, GameObject visual)
        {
            Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(Vector3.up * 0.15f, new Vector3(0.25f, 0.25f, 0.25f));

            Bounds worldBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                worldBounds.Encapsulate(renderers[i].bounds);

            Vector3 center = root.InverseTransformPoint(worldBounds.center);
            Vector3 size = worldBounds.size;
            return new Bounds(center, size);
        }
    }
}
