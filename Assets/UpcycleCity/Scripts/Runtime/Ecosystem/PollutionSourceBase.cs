using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Shared base class for all visible cause-and-effect pollution sources.
    /// Handles registration with <see cref="CityEcosystemManager"/>, per-source active trash caps,
    /// catalog lookups, and proper tagging.
    /// </summary>
    public abstract class PollutionSourceBase : MonoBehaviour, IPollutionSource
    {
        [Header("Pollution Source")]
        [SerializeField] protected string sourceId = "source";
        [SerializeField] protected string displayName = "Pollution Source";
        [SerializeField] protected WasteCatalog catalogOverride;
        [SerializeField] protected CityDistrict district;
        [SerializeField, Range(1, 30)] protected int maxActiveTrash = 8;

        readonly List<TrashItem> trackedTrash = new List<TrashItem>(12);
        int totalTrashEmitted;

        public string SourceId => string.IsNullOrWhiteSpace(sourceId) ? gameObject.name : sourceId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName;
        public abstract WasteSourceKind SourceKind { get; }
        public Vector3 WorldPosition => transform.position;
        public virtual bool IsActive => isActiveAndEnabled;
        public virtual bool IsMitigated => false;
        public virtual float AirPollutionLoad => 0f;
        public int TotalTrashEmitted => totalTrashEmitted;
        public int ActiveTrashCount => PruneAndCountTrackedTrash();
        public CityDistrict District => district;

        public void BindDistrict(CityDistrict targetDistrict)
        {
            if (targetDistrict != null)
                district = targetDistrict;
        }

        protected WasteCatalog ResolveCatalog()
        {
            if (catalogOverride != null)
                return catalogOverride;
            return CityEcosystemManager.Active != null ? CityEcosystemManager.Active.DefaultCatalog : null;
        }

        protected virtual void Awake()
        {
            if (CompareTag(GameTags.Untagged))
                gameObject.tag = GameTags.PollutionSource;
        }

        protected virtual void OnEnable()
        {
            CityEcosystemManager.RegisterSource(this);
        }

        protected virtual void OnDisable()
        {
            CityEcosystemManager.UnregisterSource(this);
        }

        protected bool CanEmitTrash()
        {
            if (CityEcosystemManager.Active != null && !CityEcosystemManager.Active.CanSpawnMoreTrash)
                return false;
            return PruneAndCountTrackedTrash() < maxActiveTrash;
        }

        protected TrashItem EmitTrash(Vector3 worldPos, Vector3 initialVelocity)
        {
            if (!CanEmitTrash())
                return null;

            WasteCatalog catalog = ResolveCatalog();
            TrashItem spawned = catalog != null
                ? catalog.SpawnTrash(SourceKind, worldPos, initialVelocity)
                : WasteCatalog.SpawnEntry(null, SourceKind, worldPos, initialVelocity);

            if (spawned != null)
            {
                trackedTrash.Add(spawned);
                totalTrashEmitted++;
            }

            return spawned;
        }

        protected int PruneAndCountTrackedTrash()
        {
            for (int i = trackedTrash.Count - 1; i >= 0; i--)
            {
                TrashItem item = trackedTrash[i];
                if (item == null || item.IsDisposed)
                    trackedTrash.RemoveAt(i);
            }
            return trackedTrash.Count;
        }
    }
}
