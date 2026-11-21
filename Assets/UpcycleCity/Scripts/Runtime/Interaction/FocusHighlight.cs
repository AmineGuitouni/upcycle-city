using UnityEngine;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Zero-allocation MaterialPropertyBlock focus highlight for any IInteractable, IGrabbable,
    /// or IPickupItem. Applies a subtle glow/tint when targeted by PlayerInteractor without
    /// instantiating new materials.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Interaction/Focus Highlight")]
    public sealed class FocusHighlight : MonoBehaviour
    {
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        [SerializeField] Color highlightEmission = new Color(0.28f, 0.24f, 0.08f, 1f);
        [SerializeField, Range(0f, 0.5f)] float baseColorLift = 0.18f;
        [SerializeField] Renderer[] targetRenderers;

        MaterialPropertyBlock propertyBlock;
        Color[] originalBaseColors;
        int[] baseColorPropIds;
        bool initialized;
        bool isHighlighted;

        public bool IsHighlighted => isHighlighted;

        public void Configure(Color emissionColor, float lift = 0.18f)
        {
            highlightEmission = emissionColor;
            baseColorLift = Mathf.Clamp(lift, 0f, 0.5f);
        }

        void Awake()
        {
            EnsureInitialized();
        }

        void OnDisable()
        {
            if (isHighlighted)
                SetHighlighted(false);
        }

        void EnsureInitialized()
        {
            if (initialized)
                return;

            if (targetRenderers == null || targetRenderers.Length == 0)
                targetRenderers = GetComponentsInChildren<Renderer>(true);

            propertyBlock = new MaterialPropertyBlock();
            originalBaseColors = new Color[targetRenderers.Length];
            baseColorPropIds = new int[targetRenderers.Length];

            for (int i = 0; i < targetRenderers.Length; i++)
            {
                Renderer r = targetRenderers[i];
                if (r == null || r.sharedMaterial == null)
                    continue;

                Material mat = r.sharedMaterial;
                if (mat.HasProperty(BaseColorId))
                {
                    baseColorPropIds[i] = BaseColorId;
                    originalBaseColors[i] = mat.GetColor(BaseColorId);
                }
                else if (mat.HasProperty(ColorId))
                {
                    baseColorPropIds[i] = ColorId;
                    originalBaseColors[i] = mat.GetColor(ColorId);
                }
            }

            initialized = true;
        }

        public void SetHighlighted(bool highlighted)
        {
            if (isHighlighted == highlighted)
                return;

            EnsureInitialized();
            isHighlighted = highlighted;

            if (targetRenderers == null)
                return;

            for (int i = 0; i < targetRenderers.Length; i++)
            {
                Renderer r = targetRenderers[i];
                if (r == null)
                    continue;

                if (!highlighted)
                {
                    r.SetPropertyBlock(null);
                    continue;
                }

                r.GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor(EmissionColorId, highlightEmission);

                int propId = baseColorPropIds[i];
                if (propId != 0 && baseColorLift > 0f)
                {
                    Color lifted = Color.Lerp(originalBaseColors[i], Color.white, baseColorLift);
                    propertyBlock.SetColor(propId, lifted);
                }

                r.SetPropertyBlock(propertyBlock);
            }
        }
    }
}
