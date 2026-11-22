using Unity.Netcode;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Waste;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Companion behaviour placed alongside <see cref="NetworkedPhysicsItem"/> and <see cref="TrashItem"/>
    /// on the generic <c>NetworkTrashItem</c> prefab. Replicates a compact <see cref="NetworkTrashDescriptor"/>
    /// so host and clients reconstruct the exact matching <see cref="WasteCatalog"/> visual prefab, hitbox,
    /// mass, and waste stream without needing a separate network prefab per litter mesh.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TrashItem))]
    [RequireComponent(typeof(NetworkedPhysicsItem))]
    [AddComponentMenu("Upcycle City/Networking/Network Trash Archetype")]
    public sealed class NetworkTrashArchetype : NetworkBehaviour
    {
        readonly NetworkVariable<NetworkTrashDescriptor> descriptor = new NetworkVariable<NetworkTrashDescriptor>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        bool visualBuilt;
        bool hasPendingDescriptor;
        NetworkTrashDescriptor pendingDescriptor;

        /// <summary>
        /// Server-only setup called before or after <see cref="NetworkObject.Spawn"/>.
        /// Pre-spawn values are buffered and written inside <see cref="OnNetworkSpawn"/> so NGO
        /// never logs a "writing NetworkVariable before spawn" warning.
        /// </summary>
        public void InitializeDescriptor(NetworkTrashDescriptor desc)
        {
            if (IsSpawned)
            {
                if (IsServer)
                    descriptor.Value = desc;
                return;
            }

            pendingDescriptor = desc;
            hasPendingDescriptor = true;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer)
            {
                if (hasPendingDescriptor)
                {
                    descriptor.Value = pendingDescriptor;
                    hasPendingDescriptor = false;
                }
                else if (descriptor.Value.displayName.IsEmpty)
                {
                    TrashItem existing = GetComponent<TrashItem>();
                    if (existing != null && !string.IsNullOrEmpty(existing.DisplayName))
                    {
                        descriptor.Value = NetworkTrashDescriptor.CreateFallback(
                            existing.DisplayName,
                            existing.Stream,
                            existing.Source,
                            existing.PollutionWeight,
                            existing.BaseCoinValue,
                            itemArchetypeId: existing.ArchetypeId);
                    }
                }
            }
            descriptor.OnValueChanged += OnDescriptorChanged;
            ApplyDescriptor(descriptor.Value);
        }

        public override void OnNetworkDespawn()
        {
            descriptor.OnValueChanged -= OnDescriptorChanged;
            base.OnNetworkDespawn();
        }

        void OnDescriptorChanged(NetworkTrashDescriptor previous, NetworkTrashDescriptor current)
        {
            ApplyDescriptor(current);
        }

        void ApplyDescriptor(NetworkTrashDescriptor desc)
        {
            TrashItem trash = GetComponent<TrashItem>();
            Rigidbody rb = GetComponent<Rigidbody>();
            if (trash == null)
                return;

            string itemName = desc.displayName.IsEmpty ? "Litter" : desc.displayName.ToString();
            string itemArchetypeId = desc.archetypeId.IsEmpty ? string.Empty : desc.archetypeId.ToString();
            WasteStream stream = (WasteStream)desc.stream;
            WasteSourceKind sourceKind = (WasteSourceKind)desc.sourceKind;
            bool bulky = desc.isBulky != 0;

            trash.Configure(
                itemName,
                stream,
                sourceKind,
                desc.pollutionWeight > 0.05f ? desc.pollutionWeight : 1f,
                desc.baseCoinValue > 0 ? desc.baseCoinValue : 6,
                bulky,
                desc.carrySpeed > 0.1f ? desc.carrySpeed : 1f,
                desc.holdOffset,
                itemArchetypeId);

            if (rb != null)
                rb.mass = Mathf.Max(0.15f, desc.mass > 0.1f ? desc.mass : 0.5f);

            if (visualBuilt)
                return;
            visualBuilt = true;

            BuildVisualAndHitbox(desc, bulky);
            trash.RefreshColliders();

            if (GetComponent<FocusHighlight>() == null)
                gameObject.AddComponent<FocusHighlight>();

            GameLayers.SetLayerRecursively(gameObject, GameLayers.Grabbable);
            gameObject.tag = GameTags.Trash;
        }

        void BuildVisualAndHitbox(NetworkTrashDescriptor desc, bool bulky)
        {
            // Clear any placeholder children from prefab template
            for (int i = transform.childCount - 1; i >= 0; i--)
                Destroy(transform.GetChild(i).gameObject);

            WasteCatalog.Entry entry = ResolveCatalogEntry(desc.catalogIndex);
            GameObject visual;
            if (entry != null && entry.visualPrefab != null)
            {
                visual = Instantiate(entry.visualPrefab, transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one * entry.visualScale;

                Collider[] imported = visual.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < imported.Length; i++)
                    Destroy(imported[i]);
            }
            else
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "FallbackVisual";
                visual.transform.SetParent(transform, false);
                visual.transform.localScale = bulky ? new Vector3(0.75f, 0.55f, 0.75f) : new Vector3(0.28f, 0.28f, 0.28f);
                Collider primitiveCol = visual.GetComponent<Collider>();
                if (primitiveCol != null)
                    Destroy(primitiveCol);

                Renderer r = visual.GetComponent<Renderer>();
                if (r != null)
                {
                    var block = new MaterialPropertyBlock();
                    Color c = WasteStreamPalette.ColorFor((WasteStream)desc.stream);
                    block.SetColor("_BaseColor", c);
                    block.SetColor("_Color", c);
                    r.SetPropertyBlock(block);
                }
            }

            Bounds localBounds = ComputeLocalBounds(transform, visual);
            BoxCollider box = GetComponent<BoxCollider>();
            if (box == null)
                box = gameObject.AddComponent<BoxCollider>();
            box.center = localBounds.center;
            Vector3 minSize = bulky ? new Vector3(0.45f, 0.35f, 0.45f) : new Vector3(0.22f, 0.20f, 0.22f);
            box.size = Vector3.Max(localBounds.size, minSize);
        }

        static WasteCatalog.Entry ResolveCatalogEntry(short catalogIndex)
        {
            if (catalogIndex < 0)
                return null;
            WasteCatalog catalog = CityEcosystemManager.Active != null ? CityEcosystemManager.Active.DefaultCatalog : null;
            if (catalog == null || catalogIndex >= catalog.Entries.Count)
                return null;
            return catalog.Entries[catalogIndex];
        }

        /// <summary>
        /// Computes tight axis-aligned bounds in <paramref name="root"/>'s local coordinate space by
        /// transforming each child mesh's local bounds rather than inflating rotated world-space AABBs.
        /// </summary>
        public static Bounds ComputeLocalBounds(Transform root, GameObject visual)
        {
            Matrix4x4 worldToRoot = root.worldToLocalMatrix;
            bool hasBounds = false;
            Bounds result = default;

            MeshFilter[] meshFilters = visual.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter mf = meshFilters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;
                EncapsulateChildBounds(ref result, ref hasBounds, worldToRoot * mf.transform.localToWorldMatrix, mf.sharedMesh.bounds);
            }

            SkinnedMeshRenderer[] skinned = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer smr = skinned[i];
                if (smr == null)
                    continue;
                EncapsulateChildBounds(ref result, ref hasBounds, worldToRoot * smr.transform.localToWorldMatrix, smr.localBounds);
            }

            return hasBounds
                ? result
                : new Bounds(Vector3.up * 0.15f, new Vector3(0.25f, 0.25f, 0.25f));
        }

        static void EncapsulateChildBounds(ref Bounds combined, ref bool hasBounds, Matrix4x4 childToRoot, Bounds childLocalBounds)
        {
            Vector3 min = childLocalBounds.min;
            Vector3 max = childLocalBounds.max;

            for (int xi = 0; xi < 2; xi++)
            {
                float x = xi == 0 ? min.x : max.x;
                for (int yi = 0; yi < 2; yi++)
                {
                    float y = yi == 0 ? min.y : max.y;
                    for (int zi = 0; zi < 2; zi++)
                    {
                        float z = zi == 0 ? min.z : max.z;
                        Vector3 corner = childToRoot.MultiplyPoint3x4(new Vector3(x, y, z));
                        if (!hasBounds)
                        {
                            combined = new Bounds(corner, Vector3.zero);
                            hasBounds = true;
                        }
                        else
                        {
                            combined.Encapsulate(corner);
                        }
                    }
                }
            }
        }
    }
}
