using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Underwater caustics: puts a tiling caustic texture on the sun as a light cookie and drifts it, so
    /// rippling light crawls over the sea floor and the turtle. Enable it for the underwater shots only;
    /// disabling it removes the cookie again.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Caustics Cookie")]
    public sealed class CausticsCookie : MonoBehaviour
    {
        [SerializeField] Light sun;
        [SerializeField] Texture2D caustics;
        [Tooltip("Metres covered by one tile of the texture.")]
        [SerializeField] Vector2 tileSize = new Vector2(6f, 6f);
        [SerializeField] Vector2 drift = new Vector2(0.35f, 0.2f);

        UniversalAdditionalLightData data;
        Texture previousCookie;
        Vector2 previousSize;

        void OnEnable()
        {
            if (sun == null || caustics == null)
                return;
            data = sun.GetUniversalAdditionalLightData();
            previousCookie = sun.cookie;
            previousSize = data.lightCookieSize;
            sun.cookie = caustics;
            data.lightCookieSize = tileSize;
        }

        void OnDisable()
        {
            if (sun == null || data == null)
                return;
            sun.cookie = previousCookie;
            data.lightCookieSize = previousSize;
            data.lightCookieOffset = Vector2.zero;
        }

        void Update()
        {
            if (data != null)
                data.lightCookieOffset = drift * Time.time + new Vector2(Mathf.Sin(Time.time * 0.7f), Mathf.Cos(Time.time * 0.5f)) * 0.15f;
        }
    }
}
