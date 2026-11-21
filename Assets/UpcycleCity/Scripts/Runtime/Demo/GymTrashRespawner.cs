using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Waste;

namespace UpcycleCity.Demo
{
    /// <summary>
    /// Simple gym pedestal button: pressing E spawns a fresh batch of physical TrashItems
    /// so you can keep testing physics grabbing, throwing, and 4-stream sorting.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Demo/Gym Trash Respawner")]
    public sealed class GymTrashRespawner : MonoBehaviour, IInteractable
    {
        [Serializable]
        public struct SpawnEntry
        {
            public string displayName;
            public WasteStream stream;
            public GameObject visualPrefab;
            public Vector3 colliderSize;
            public float mass;
            public int coinValue;
            public bool isBulky;
            public float carrySpeedMultiplier;
            public float holdOffset;
        }

        [SerializeField] Transform spawnCenter;
        [SerializeField] float spawnRadius = 2.4f;
        [SerializeField] SpawnEntry[] entries = Array.Empty<SpawnEntry>();

        public Transform Transform => transform;
        public string InteractionPrompt => "[E] Spawn Fresh Trash Batch";

        public void Configure(Transform center, SpawnEntry[] spawnEntries)
        {
            spawnCenter = center;
            entries = spawnEntries ?? Array.Empty<SpawnEntry>();
        }

        public bool CanInteract(PlayerInteractor interactor) => entries != null && entries.Length > 0;

        public void Interact(PlayerInteractor interactor)
        {
            SpawnBatch();
            interactor?.NotifyFeedback("Spawned fresh physics trash!", new Color(0.18f, 0.55f, 0.34f));
        }

        public void SpawnBatch()
        {
            if (entries == null || entries.Length == 0)
                return;

            Vector3 origin = spawnCenter != null ? spawnCenter.position : transform.position + transform.forward * 2.5f;
            for (int i = 0; i < entries.Length; i++)
            {
                SpawnEntry entry = entries[i];
                float angle = (i / (float)entries.Length) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.25f, 0.25f);
                float dist = UnityEngine.Random.Range(0.45f, spawnRadius);
                Vector3 pos = origin + new Vector3(Mathf.Cos(angle) * dist, 0.6f + i * 0.08f, Mathf.Sin(angle) * dist);

                var go = new GameObject($"Trash_{entry.displayName}");
                go.tag = GameTags.Trash;
                go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));

                if (entry.visualPrefab != null)
                {
                    GameObject visual = Instantiate(entry.visualPrefab, go.transform);
                    visual.transform.localPosition = Vector3.zero;
                    visual.transform.localRotation = Quaternion.identity;
                }
                else
                {
                    var fallback = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(fallback.GetComponent<Collider>());
                    fallback.transform.SetParent(go.transform, false);
                    fallback.transform.localScale = entry.colliderSize.sqrMagnitude > 0.001f ? entry.colliderSize : Vector3.one * 0.25f;
                }

                BoxCollider box = go.AddComponent<BoxCollider>();
                Vector3 size = entry.colliderSize.sqrMagnitude > 0.001f ? entry.colliderSize : new Vector3(0.24f, 0.28f, 0.24f);
                box.size = size;
                box.center = new Vector3(0f, size.y * 0.5f, 0f);

                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(0.2f, entry.mass);

                TrashItem item = go.AddComponent<TrashItem>();
                item.Configure(
                    entry.displayName,
                    entry.stream,
                    WasteSourceKind.Initial,
                    entry.isBulky ? 2.5f : 1f,
                    Mathf.Max(2, entry.coinValue),
                    entry.isBulky,
                    entry.carrySpeedMultiplier > 0f ? entry.carrySpeedMultiplier : 1f,
                    entry.holdOffset);

                go.AddComponent<FocusHighlight>();
                GameLayers.SetLayerRecursively(go, GameLayers.Grabbable);
            }
        }
    }
}
