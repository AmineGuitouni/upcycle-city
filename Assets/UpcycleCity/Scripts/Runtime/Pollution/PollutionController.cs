using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UpcycleCity.Pollution
{
    /// <summary>
    /// The single source of truth for how polluted the city is. It pushes the level, the zones and
    /// the profile's surface look to the shaders, and drives the sun, ambient light, fog and the
    /// smog post-processing volume. Put one in each scene.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Pollution Controller")]
    public sealed class PollutionController : MonoBehaviour
    {
        [Tooltip("Scene-wide pollution: 0 is a clean city, 1 is fully polluted.")]
        [SerializeField, Range(0f, 1f)] float pollution = 1f;

        [Tooltip("Seconds to blend fully from one end to the other when the level is set from code. 0 is instant.")]
        [SerializeField, Min(0f)] float transitionTime = 4f;

        [SerializeField] PollutionProfile profile;

        [Header("Scene")]
        [Tooltip("The directional light. Its colour, intensity and elevation follow the profile.")]
        [SerializeField] Light sun;

        [Tooltip("Compass direction of the sun, in degrees.")]
        [SerializeField, Range(-180f, 180f)] float sunAzimuth = 110f;

        [Tooltip("A post-processing volume with the smog grade. Its weight follows the pollution level.")]
        [SerializeField] Volume smogVolume;

        static readonly List<PollutionZone> Zones = new List<PollutionZone>();

        /// <summary>The controller of the loaded scene, if any.</summary>
        public static PollutionController Active { get; private set; }

        /// <summary>Raised whenever the level, the zones or the profile change.</summary>
        public static event Action<PollutionController> Changed;

        float target;
        bool dirty = true;

        public float Pollution => pollution;
        public float TargetPollution => target;
        public PollutionProfile Profile => profile;

        /// <summary>
        /// Sets a new scene-wide level. In Play mode it blends there over <see cref="transitionTime"/>,
        /// unless <paramref name="instant"/> is set.
        /// </summary>
        public void SetPollution(float value, bool instant = false)
        {
            target = Mathf.Clamp01(value);
            if (instant || transitionTime <= 0f || !Application.isPlaying)
            {
                pollution = target;
                MarkDirty();
            }
        }

        /// <summary>Pollution at a world position, zones included. Mirrors SamplePollution() in Pollution.hlsl.</summary>
        public float SampleAt(Vector3 position)
        {
            float level = pollution;
            int count = Mathf.Min(Zones.Count, PollutionShaderGlobals.MaxZones);
            for (int i = 0; i < count; i++)
                level = Zones[i].Apply(level, position);
            return Mathf.Clamp01(level);
        }

        internal static void RegisterZone(PollutionZone zone)
        {
            if (Zones.Contains(zone))
                return;
            Zones.Add(zone);
            if (Zones.Count > PollutionShaderGlobals.MaxZones)
                Debug.LogWarning($"More than {PollutionShaderGlobals.MaxZones} pollution zones are active; the extra ones are ignored.", zone);
            NotifyZonesChanged();
        }

        internal static void UnregisterZone(PollutionZone zone)
        {
            if (Zones.Remove(zone))
                NotifyZonesChanged();
        }

        internal static void NotifyZonesChanged()
        {
            if (Active != null)
                Active.MarkDirty();
        }

        void OnEnable()
        {
            if (Active != null && Active != this)
                Debug.LogWarning("There is more than one PollutionController; the last one enabled wins.", this);
            Active = this;
            target = pollution;
            MarkDirty();
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        void OnValidate()
        {
            target = pollution;
            MarkDirty(); // applied from Update: lights and render settings can't be changed inside OnValidate
        }

        void Update()
        {
            if (Application.isPlaying && !Mathf.Approximately(pollution, target))
            {
                float step = transitionTime > 0f ? Time.deltaTime / transitionTime : 1f;
                pollution = Mathf.MoveTowards(pollution, target, step);
                dirty = true;
            }

            if (dirty)
                Apply();
        }

        void MarkDirty()
        {
            dirty = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void Apply()
        {
            dirty = false;
            PollutionShaderGlobals.SetLevel(pollution);
            PollutionShaderGlobals.SetZones(Zones);

            if (profile != null)
            {
                PollutionShaderGlobals.SetSurfaceLook(profile);
                ApplyAtmosphere(profile.AtmosphereAt(pollution));
            }

            if (smogVolume != null)
                smogVolume.weight = pollution;

            Changed?.Invoke(this);
        }

        void ApplyAtmosphere(in AtmosphereState atmosphere)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = atmosphere.fogColor;
            RenderSettings.fogDensity = atmosphere.fogDensity;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = atmosphere.ambientSky;
            RenderSettings.ambientEquatorColor = atmosphere.ambientEquator;
            RenderSettings.ambientGroundColor = atmosphere.ambientGround;

            if (sun != null)
            {
                sun.color = atmosphere.sunColor;
                sun.intensity = atmosphere.sunIntensity;
                sun.transform.rotation = Quaternion.Euler(atmosphere.sunElevation, sunAzimuth, 0f);
            }
        }
    }
}
