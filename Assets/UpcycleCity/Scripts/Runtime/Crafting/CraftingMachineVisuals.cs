using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Drives procedural runtime visuals on manual crafting machines and generators with zero per-frame GC:
    /// rotates named Blender pivot children (<c>CrankPivot</c>, <c>FlywheelPivot</c>, <c>PedalPivot</c>,
    /// <c>RotorPivot</c>, <c>RollerPivotA</c>/<c>RollerPivotB</c>, <c>BellowsPivot</c>, <c>DynamoPivot</c>),
    /// pulses <c>GlowMesh</c> emission via <see cref="MaterialPropertyBlock"/>, toggles <c>Upgrade_Lvl1</c>
    /// meshes by machine level, and runs an optional steam/spark burst at <c>SteamPoint</c>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Crafting Machine Visuals")]
    public sealed class CraftingMachineVisuals : MonoBehaviour
    {
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Header("Animation Speeds")]
        [SerializeField] float crankDegreesPerSec = 260f;
        [SerializeField] float flywheelDegreesPerSec = 420f;
        [SerializeField] float pedalDegreesPerSec = 310f;
        [SerializeField] float rotorDegreesPerSec = 380f;
        [SerializeField] float rollerDegreesPerSec = 240f;
        [SerializeField] float dynamoDegreesPerSec = 640f;
        [SerializeField] float bellowsCycleHz = 2.6f;
        [SerializeField] float bellowsAngleAmplitude = 14f;

        [Header("Glow & Steam")]
        [SerializeField] Color activeGlowColor = new Color(1.0f, 0.56f, 0.14f, 1f);
        [SerializeField, Min(0f)] float idleEmissionMultiplier = 0.25f;
        [SerializeField, Min(0.5f)] float activeEmissionMultiplier = 2.6f;
        [SerializeField] bool createSteamEmitter = true;

        Transform crankPivot;
        Transform flywheelPivot;
        Transform pedalPivot;
        Transform rotorPivot;
        Transform rollerPivotA;
        Transform rollerPivotB;
        Transform bellowsPivot;
        Transform dynamoPivot;
        Transform steamPoint;
        Transform outputPoint;
        Transform hopperZone;

        Quaternion bellowsBaseLocalRotation = Quaternion.identity;
        readonly List<GameObject> upgradeLevel1Objects = new List<GameObject>(4);
        readonly List<Renderer> glowRenderers = new List<Renderer>(4);
        MaterialPropertyBlock glowPropertyBlock;
        ParticleSystem steamParticles;

        float activityBlend;
        float pulseTime;
        float burstRemaining;
        bool isActive;
        int currentLevel = -1;
        bool bound;

        public Transform HopperZone => EnsureBoundAndGet(hopperZone);
        public Transform OutputPoint => EnsureBoundAndGet(outputPoint);
        public Transform SteamPoint => EnsureBoundAndGet(steamPoint);

        void Awake()
        {
            BindChildren();
        }

        Transform EnsureBoundAndGet(Transform t)
        {
            if (!bound)
                BindChildren();
            return t;
        }

        /// <summary>Scans the hierarchy once to cache all named Blender pivots, glow meshes, anchors, and upgrade groups.</summary>
        public void BindChildren()
        {
            if (bound)
                return;
            bound = true;

            upgradeLevel1Objects.Clear();
            glowRenderers.Clear();
            glowPropertyBlock ??= new MaterialPropertyBlock();

            Transform[] all = GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == transform)
                    continue;

                string n = t.name;
                if (MatchesName(n, "CrankPivot")) crankPivot = t;
                else if (MatchesName(n, "FlywheelPivot")) flywheelPivot = t;
                else if (MatchesName(n, "PedalPivot")) pedalPivot = t;
                else if (MatchesName(n, "RotorPivot")) rotorPivot = t;
                else if (MatchesName(n, "RollerPivotA")) rollerPivotA = t;
                else if (MatchesName(n, "RollerPivotB")) rollerPivotB = t;
                else if (MatchesName(n, "BellowsPivot"))
                {
                    bellowsPivot = t;
                    bellowsBaseLocalRotation = t.localRotation;
                }
                else if (MatchesName(n, "DynamoPivot")) dynamoPivot = t;
                else if (MatchesName(n, "SteamPoint")) steamPoint = t;
                else if (MatchesName(n, "OutputPoint")) outputPoint = t;
                else if (MatchesName(n, "HopperZone")) hopperZone = t;
                else if (MatchesName(n, "Upgrade_Lvl1"))
                    upgradeLevel1Objects.Add(t.gameObject);
                else if (MatchesName(n, "GlowMesh"))
                {
                    Renderer[] rends = t.GetComponentsInChildren<Renderer>(includeInactive: true);
                    for (int r = 0; r < rends.Length; r++)
                    {
                        if (rends[r] != null && !glowRenderers.Contains(rends[r]))
                            glowRenderers.Add(rends[r]);
                    }
                }
            }

            // Pick a matching default glow hue from the GlowMesh material if available
            if (glowRenderers.Count > 0 && glowRenderers[0].sharedMaterial != null)
            {
                Material mat = glowRenderers[0].sharedMaterial;
                if (mat.HasProperty(EmissionColorId))
                {
                    Color c = mat.GetColor(EmissionColorId);
                    if (c.maxColorComponent > 0.05f)
                    {
                        float norm = Mathf.Max(1f, c.maxColorComponent);
                        activeGlowColor = new Color(c.r / norm, c.g / norm, c.b / norm, 1f);
                    }
                }
                else if (mat.HasProperty(BaseColorId))
                {
                    activeGlowColor = mat.GetColor(BaseColorId);
                }
            }

            if (createSteamEmitter && steamPoint != null)
                EnsureSteamParticles();

            ApplyGlow(idleEmissionMultiplier);
        }

        /// <summary>Shows or hides <c>Upgrade_Lvl1</c> child geometry based on <paramref name="level"/>.</summary>
        public void SetMachineLevel(int level)
        {
            BindChildren();
            if (currentLevel == level)
                return;
            currentLevel = level;

            bool showLevel1 = level >= 1;
            for (int i = 0; i < upgradeLevel1Objects.Count; i++)
            {
                GameObject go = upgradeLevel1Objects[i];
                if (go != null && go.activeSelf != showLevel1)
                    go.SetActive(showLevel1);
            }
        }

        /// <summary>Sets whether the machine is actively running a card.</summary>
        public void SetProcessing(bool running)
        {
            BindChildren();
            isActive = running;
            UpdateSteamEmission();
        }

        /// <summary>Triggers a short spin/glow burst (used by manual energy cranking or completion feedback).</summary>
        public void TriggerActivityBurst(float durationSeconds = 1.15f)
        {
            BindChildren();
            burstRemaining = Mathf.Max(burstRemaining, durationSeconds);
            UpdateSteamEmission();
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (burstRemaining > 0f)
                burstRemaining = Mathf.Max(0f, burstRemaining - dt);

            bool shouldRun = isActive || burstRemaining > 0f;
            float targetBlend = shouldRun ? 1f : 0f;
            float blendRate = shouldRun ? 6f : 3.2f;
            activityBlend = Mathf.MoveTowards(activityBlend, targetBlend, dt * blendRate);

            if (activityBlend > 0.001f)
            {
                pulseTime += dt;
                float spinScale = activityBlend;

                if (crankPivot != null)
                    crankPivot.Rotate(crankDegreesPerSec * spinScale * dt, 0f, 0f, Space.Self);
                if (flywheelPivot != null)
                    flywheelPivot.Rotate(flywheelDegreesPerSec * spinScale * dt, 0f, 0f, Space.Self);
                if (pedalPivot != null)
                    pedalPivot.Rotate(pedalDegreesPerSec * spinScale * dt, 0f, 0f, Space.Self);
                if (rotorPivot != null)
                    rotorPivot.Rotate(0f, rotorDegreesPerSec * spinScale * dt, 0f, Space.Self);
                if (rollerPivotA != null)
                    rollerPivotA.Rotate(rollerDegreesPerSec * spinScale * dt, 0f, 0f, Space.Self);
                if (rollerPivotB != null)
                    rollerPivotB.Rotate(-rollerDegreesPerSec * spinScale * dt, 0f, 0f, Space.Self);
                if (dynamoPivot != null)
                    dynamoPivot.Rotate(dynamoDegreesPerSec * spinScale * dt, 0f, 0f, Space.Self);
                if (bellowsPivot != null)
                {
                    float angle = Mathf.Sin(pulseTime * bellowsCycleHz * Mathf.PI * 2f) * bellowsAngleAmplitude * spinScale;
                    bellowsPivot.localRotation = bellowsBaseLocalRotation * Quaternion.Euler(angle, 0f, 0f);
                }

                float pulse = 0.72f + 0.28f * Mathf.Sin(pulseTime * 9.5f);
                float intensity = Mathf.Lerp(idleEmissionMultiplier, activeEmissionMultiplier * pulse, activityBlend);
                ApplyGlow(intensity);
            }
            else if (bellowsPivot != null)
            {
                bellowsPivot.localRotation = Quaternion.Slerp(bellowsPivot.localRotation, bellowsBaseLocalRotation, dt * 6f);
            }

            UpdateSteamEmission();
        }

        void ApplyGlow(float intensity)
        {
            if (glowRenderers.Count == 0 || glowPropertyBlock == null)
                return;

            Color hdr = activeGlowColor * intensity;
            for (int i = 0; i < glowRenderers.Count; i++)
            {
                Renderer r = glowRenderers[i];
                if (r == null)
                    continue;
                r.GetPropertyBlock(glowPropertyBlock);
                glowPropertyBlock.SetColor(EmissionColorId, hdr);
                r.SetPropertyBlock(glowPropertyBlock);
            }
        }

        void EnsureSteamParticles()
        {
            steamParticles = steamPoint.GetComponentInChildren<ParticleSystem>();
            if (steamParticles != null)
                return;

            var psGo = new GameObject("SteamParticles");
            psGo.transform.SetParent(steamPoint, false);
            steamParticles = psGo.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = steamParticles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.15f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.45f, 0.95f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.22f);
            main.startColor = new Color(0.92f, 0.94f, 0.96f, 0.42f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 24;

            ParticleSystem.EmissionModule emission = steamParticles.emission;
            emission.rateOverTime = 9f;

            ParticleSystem.ShapeModule shape = steamParticles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 16f;
            shape.radius = 0.06f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            ParticleSystemRenderer psr = psGo.GetComponent<ParticleSystemRenderer>();
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var mat = new Material(shader) { name = "CraftingSteamMat" };
                mat.SetColor(BaseColorId, new Color(0.94f, 0.96f, 0.97f, 0.45f));
                psr.sharedMaterial = mat;
            }
            steamParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        void UpdateSteamEmission()
        {
            if (steamParticles == null)
                return;

            bool wantEmit = isActive || burstRemaining > 0f;
            if (wantEmit && !steamParticles.isEmitting)
                steamParticles.Play();
            else if (!wantEmit && steamParticles.isEmitting)
                steamParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        static bool MatchesName(string actual, string expected)
        {
            if (string.Equals(actual, expected, StringComparison.Ordinal))
                return true;
            return actual.StartsWith(expected + ".", StringComparison.Ordinal);
        }
    }
}
