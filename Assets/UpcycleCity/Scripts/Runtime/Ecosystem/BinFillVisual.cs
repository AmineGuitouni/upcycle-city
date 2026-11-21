using UnityEngine;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Shows how full a street bin is with the bin itself instead of a gauge: the rubbish heap inside rises with the
    /// fill level, the lid is pushed open as the bin gets full, and bags pile over the rim while it overflows.
    /// Animates smoothly (so emptying a bin visibly drains it). Reads <see cref="OverflowingBinSource.FillLevel"/>.
    /// Heights are in the bin's local space and match <c>Tools/Blender/bourguiba_street.py</c>.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(OverflowingBinSource))]
    [AddComponentMenu("Upcycle City/Ecosystem/Bin Fill Visual")]
    public sealed class BinFillVisual : MonoBehaviour
    {
        [Header("Parts")]
        [Tooltip("The rubbish heap; its origin is the bottom of the heap.")]
        [SerializeField] Transform contents;
        [Tooltip("The lid; its origin is on the hinge.")]
        [SerializeField] Transform lid;
        [Tooltip("Bags piled above the rim, shown only while overflowing.")]
        [SerializeField] GameObject overflowPile;

        [Header("Heights (bin local space)")]
        [SerializeField] float floorHeight = 0.24f;
        [SerializeField] float rimHeight = 1.2f;
        [Tooltip("Height of the heap model at full size.")]
        [SerializeField, Min(0.05f)] float heapHeight = 0.55f;
        [Tooltip("How far above the rim the heap bulges when the bin is full.")]
        [SerializeField, Min(0f)] float overfill = 0.15f;

        [Header("Lid")]
        [Tooltip("Fill level at which the bags start pushing the lid open (so the player can see inside).")]
        [SerializeField, Range(0f, 1f)] float lidStartsOpening = 0.4f;
        [SerializeField, Range(0f, 120f)] float lidMaxAngle = 80f;
        [Tooltip("Euler axis the lid swings about; negative X lifts the front of a +Z facing bin.")]
        [SerializeField] Vector3 lidOpenAxis = new Vector3(-1f, 0f, 0f);

        [Header("Animation")]
        [SerializeField, Min(0.5f)] float sharpness = 4f;

        OverflowingBinSource bin;
        Quaternion lidClosed;
        float shown;

        public void Configure(Transform heap, Transform binLid, GameObject pile)
        {
            contents = heap;
            lid = binLid;
            overflowPile = pile;
        }

        void Awake()
        {
            bin = GetComponent<OverflowingBinSource>();
            if (lid != null)
                lidClosed = lid.localRotation;
            shown = bin.FillLevel;
            Apply(shown, bin.IsOverflowing);
        }

        void Update()
        {
            shown = Mathf.Lerp(shown, bin.FillLevel, 1f - Mathf.Exp(-sharpness * Time.deltaTime));
            Apply(shown, bin.IsOverflowing);
        }

        void Apply(float fill, bool overflowing)
        {
            if (contents != null)
            {
                float top = floorHeight + fill * (rimHeight - floorHeight + overfill);
                float height = top - floorHeight;
                bool visible = height > 0.01f;
                if (contents.gameObject.activeSelf != visible)
                    contents.gameObject.SetActive(visible);
                Vector3 p = contents.localPosition;
                p.y = Mathf.Max(floorHeight, top - heapHeight);
                contents.localPosition = p;
                Vector3 s = contents.localScale;
                s.y = Mathf.Clamp(height / heapHeight, 0.02f, 1f);
                contents.localScale = s;
            }

            if (lid != null)
            {
                float t = Mathf.InverseLerp(lidStartsOpening, 1f, fill);
                float angle = lidMaxAngle * t * t * (3f - 2f * t);
                if (overflowing)
                    angle += Mathf.Sin(Time.time * 2.3f) * 2f;   // bags shifting under it
                lid.localRotation = lidClosed * Quaternion.Euler(lidOpenAxis * angle);
            }

            if (overflowPile != null && overflowPile.activeSelf != overflowing)
                overflowPile.SetActive(overflowing);
        }
    }
}
