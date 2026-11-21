using System;
using UnityEngine;

namespace UpcycleCity.Pollution
{
    /// <summary>Must match the POLLUTION_VISIBILITY_* values in Pollution.hlsl.</summary>
    public enum PollutionVisibilityMode
    {
        Always = 0,
        WhenPolluted = 1,
        WhenClean = 2,
    }

    /// <summary>
    /// Makes an object exist only in the polluted city (litter, dead trees, smog masks) or only in the
    /// clean city (flowers, solar panels, the garbage truck). Pollution Lit dissolves it in and out
    /// around <see cref="Threshold"/>. The setting travels per renderer through
    /// Renderer.SetShaderUserValue, so every copy shares its materials and stays SRP-batched.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Pollution Visibility")]
    public sealed class PollutionVisibility : MonoBehaviour
    {
        [SerializeField] PollutionVisibilityMode mode = PollutionVisibilityMode.WhenPolluted;

        [Tooltip("Pollution level at which the object dissolves in or out.")]
        [SerializeField, Range(0.02f, 0.98f)] float threshold = 0.5f;

        [Tooltip("In Play mode, turn this object's colliders off while it is dissolved.")]
        [SerializeField] bool toggleColliders = true;

        Renderer[] renderers = Array.Empty<Renderer>();
        Collider[] colliders = Array.Empty<Collider>();
        bool collidersEnabled = true;

        public PollutionVisibilityMode Mode
        {
            get => mode;
            set { mode = value; Refresh(); }
        }

        public float Threshold
        {
            get => threshold;
            set { threshold = Mathf.Clamp(value, 0.02f, 0.98f); Refresh(); }
        }

        /// <summary>Packs mode and threshold the way Pollution.hlsl unpacks them.</summary>
        public static uint Encode(PollutionVisibilityMode mode, float threshold)
        {
            uint thresholdByte = (uint)Mathf.RoundToInt(Mathf.Clamp01(threshold) * 255f);
            return (uint)mode | (thresholdByte << 8);
        }

        /// <summary>Whether the object is shown at a given pollution level (ignoring the dissolve noise).</summary>
        public bool IsVisibleAt(float pollution)
        {
            switch (mode)
            {
                case PollutionVisibilityMode.WhenPolluted: return pollution > threshold;
                case PollutionVisibilityMode.WhenClean: return pollution < threshold;
                default: return true;
            }
        }

        /// <summary>Re-reads the child renderers and colliders, for objects whose children change at runtime.</summary>
        public void Refresh()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            colliders = toggleColliders ? GetComponentsInChildren<Collider>(true) : Array.Empty<Collider>();
            WriteUserValue(Encode(mode, threshold));
            if (PollutionController.Active != null)
                UpdateColliders(PollutionController.Active);
        }

        void OnEnable()
        {
            PollutionController.Changed += UpdateColliders;
            Refresh();
        }

        void OnDisable()
        {
            PollutionController.Changed -= UpdateColliders;
            WriteUserValue(Encode(PollutionVisibilityMode.Always, 0f));
            SetColliders(true);
        }

        void OnValidate()
        {
            if (isActiveAndEnabled)
                Refresh();
        }

        void OnTransformChildrenChanged() => Refresh();

        void WriteUserValue(uint value)
        {
            foreach (Renderer r in renderers)
            {
                switch (r)
                {
                    case MeshRenderer meshRenderer:
                        meshRenderer.SetShaderUserValue(value);
                        break;
                    case SkinnedMeshRenderer skinnedRenderer:
                        skinnedRenderer.SetShaderUserValue(value);
                        break;
                }
            }
        }

        void UpdateColliders(PollutionController controller)
        {
            if (Application.isPlaying && colliders.Length > 0)
                SetColliders(IsVisibleAt(controller.SampleAt(transform.position)));
        }

        void SetColliders(bool enabled)
        {
            if (collidersEnabled == enabled)
                return;
            collidersEnabled = enabled;
            foreach (Collider c in colliders)
                c.enabled = enabled;
        }
    }
}
