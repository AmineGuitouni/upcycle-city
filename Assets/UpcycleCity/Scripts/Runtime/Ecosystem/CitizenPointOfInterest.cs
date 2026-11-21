using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Marks a real city landmark (shopfront, fountain, bench, cafe) that nearby Tier-1 visible citizens
    /// can naturally pause at, turn toward, and react to based on district cleanliness.
    /// </summary>
    [AddComponentMenu("Upcycle City/Ecosystem/Citizen Point Of Interest")]
    public sealed class CitizenPointOfInterest : MonoBehaviour
    {
        [SerializeField] CitizenPoiKind kind = CitizenPoiKind.ShopWindow;
        [SerializeField] string label = "Shop";
        [SerializeField, Min(1f)] float interactionRadius = 4.2f;

        static readonly List<CitizenPointOfInterest> Registry = new List<CitizenPointOfInterest>(32);

        public static IReadOnlyList<CitizenPointOfInterest> All => Registry;

        public CitizenPoiKind Kind => kind;
        public string Label => string.IsNullOrWhiteSpace(label) ? gameObject.name : label;
        public float InteractionRadius => interactionRadius;
        public Vector3 Position => transform.position;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Registry.Clear();
        }

        public void Configure(CitizenPoiKind poiKind, string poiLabel, float radius = 4.2f)
        {
            kind = poiKind;
            label = poiLabel;
            interactionRadius = Mathf.Max(1f, radius);
        }

        void OnEnable()
        {
            if (!Registry.Contains(this))
                Registry.Add(this);
        }

        void OnDisable()
        {
            Registry.Remove(this);
        }

        public static CitizenPointOfInterest FindNearest(Vector3 worldPos, float maxRadius)
        {
            CitizenPointOfInterest best = null;
            float bestSqr = maxRadius * maxRadius;

            for (int i = 0; i < Registry.Count; i++)
            {
                CitizenPointOfInterest poi = Registry[i];
                if (poi == null || !poi.isActiveAndEnabled)
                    continue;

                Vector3 d = poi.transform.position - worldPos;
                float sqr = d.x * d.x + d.z * d.z;
                float effective = Mathf.Min(maxRadius, poi.interactionRadius);
                if (sqr <= effective * effective && sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = poi;
                }
            }

            return best;
        }
    }
}
