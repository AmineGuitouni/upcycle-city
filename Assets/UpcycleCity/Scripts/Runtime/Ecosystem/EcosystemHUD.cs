using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Live Phase 2 Ecosystem telemetry dashboard showing City Cleanliness, Resident Satisfaction,
    /// per-district pollution bars, real cause-attributed trash counts (Citizens / Bins / Factory / Decay),
    /// active source statuses, and a smog-exposure alert when standing in heavy pollution.
    /// Positioned cleanly on the right side below the coins badge so it never overlaps PlayerGymHUD.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Ecosystem/Ecosystem HUD")]
    public sealed class EcosystemHUD : MonoBehaviour
    {
        Texture2D whiteTex;
        GUIStyle headerStyle;
        GUIStyle labelStyle;
        GUIStyle smallStyle;
        GUIStyle badgeStyle;
        GUIStyle warnStyle;
        bool stylesReady;

        static readonly Color PanelDark = new Color(0.08f, 0.10f, 0.12f, 0.90f);
        static readonly Color BarBg = new Color(0.18f, 0.20f, 0.23f, 0.95f);
        static readonly Color CleanGreen = new Color(0.18f, 0.68f, 0.40f, 1f);
        static readonly Color WarnAmber = new Color(0.95f, 0.70f, 0.14f, 1f);
        static readonly Color PollutedRed = new Color(0.84f, 0.25f, 0.20f, 1f);
        static readonly Color InfoTeal = new Color(0.18f, 0.62f, 0.78f, 1f);

        void OnDestroy()
        {
            if (whiteTex != null)
                Destroy(whiteTex);
        }

        void EnsureStyles()
        {
            if (stylesReady)
                return;

            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
            };

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.93f, 0.96f, 0.97f) },
            };

            smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.82f, 0.87f, 0.90f) },
            };

            badgeStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            warnStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
            };

            stylesReady = true;
        }

        void OnGUI()
        {
            CityEcosystemManager eco = CityEcosystemManager.Active;
            if (eco == null)
                return;

            EnsureStyles();

            DrawCitizenReactionCues();
            DrawEcosystemDashboard(eco);
            DrawSmogWarningBanner(eco);
        }

        void DrawCitizenReactionCues()
        {
            CitizenSimulationManager sim = CitizenSimulationManager.Active;
            Camera cam = Camera.main;
            if (sim == null || cam == null)
                return;

            IReadOnlyList<CitizenReactionCue> cues = sim.ActiveCues;
            for (int i = 0; i < cues.Count; i++)
            {
                CitizenReactionCue cue = cues[i];
                if (cue.Anchor == null || string.IsNullOrEmpty(cue.Text))
                    continue;

                Vector3 worldPos = cue.Anchor.position + Vector3.up * 2.15f;
                Vector3 screen = cam.WorldToScreenPoint(worldPos);
                if (screen.z <= 0.5f || screen.z > 18f)
                    continue;

                float alpha = Mathf.Clamp01(cue.Remaining / 0.45f);
                float sx = screen.x;
                float sy = Screen.height - screen.y;
                float pillW = Mathf.Clamp(cue.Text.Length * 7.2f + 18f, 84f, 200f);
                float pillH = 22f;

                Rect rect = new Rect(sx - pillW * 0.5f, sy - pillH * 0.5f, pillW, pillH);
                Color bg = new Color(0.08f, 0.10f, 0.12f, 0.82f * alpha);
                Color bar = new Color(cue.AccentColor.r, cue.AccentColor.g, cue.AccentColor.b, alpha);
                DrawBox(rect, bg);
                DrawBox(new Rect(rect.x, rect.y + rect.height - 2f, rect.width, 2f), bar);

                Color prev = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(rect, cue.Text, badgeStyle);
                GUI.color = prev;
            }
        }

        void DrawEcosystemDashboard(CityEcosystemManager eco)
        {
            const float panelW = 324f;
            float x = Screen.width - panelW - 20f;
            float y = 66f;

            IReadOnlyList<CityDistrict> districts = eco.AllDistricts;
            int districtCount = districts.Count;
            CitizenSimulationManager citizenSim = CitizenSimulationManager.Active;
            float extraCitizenRow = citizenSim != null ? 20f : 0f;

            float height = 240f + extraCitizenRow + districtCount * 38f;
            DrawBox(new Rect(x, y, panelW, height), PanelDark);

            float cursorY = y + 8f;
            float innerX = x + 12f;
            float innerW = panelW - 24f;

            // Header + atmosphere status
            float cleanliness = 1f - eco.CityPollution;
            string atmoLabel = AtmosphereLabel(eco.CityPollution);
            Color atmoColor = ColorForPollution(eco.CityPollution);
            GUI.Label(new Rect(innerX, cursorY, innerW - 92f, 20f), "LIVING CITY ECOSYSTEM", headerStyle);
            DrawBox(new Rect(innerX + innerW - 90f, cursorY, 90f, 20f), atmoColor);
            GUI.Label(new Rect(innerX + innerW - 90f, cursorY, 90f, 20f), atmoLabel, badgeStyle);
            cursorY += 26f;

            // City Cleanliness bar
            DrawMetricBar(innerX, cursorY, innerW, $"City Cleanliness: {Mathf.RoundToInt(cleanliness * 100f)}%", cleanliness, atmoColor);
            cursorY += 32f;

            // Resident Satisfaction bar
            Color satColor = ColorForSatisfaction(eco.CitySatisfaction);
            string mood = SatisfactionMood(eco.CitySatisfaction);
            DrawMetricBar(innerX, cursorY, innerW, $"Resident Satisfaction: {Mathf.RoundToInt(eco.CitySatisfaction * 100f)}% ({mood})", eco.CitySatisfaction, satColor);
            cursorY += 36f;

            // Per-district bars
            if (districtCount > 0)
            {
                GUI.Label(new Rect(innerX, cursorY, innerW, 16f), "DISTRICTS (LOCAL SHADER ZONES)", smallStyle);
                cursorY += 18f;

                for (int i = 0; i < districtCount; i++)
                {
                    CityDistrict d = districts[i];
                    if (d == null)
                        continue;

                    string tag = eco.PlayerDistrict == d ? " [HERE]" : string.Empty;
                    float pol = d.CurrentPollution;
                    string bgTag = d.PendingBackgroundTrash > 0 ? $" (+{d.PendingBackgroundTrash} BG)" : string.Empty;
                    string line = $"{d.DistrictName}{tag} · Poll {Mathf.RoundToInt(pol * 100f)}% · Trash {d.ActiveTrashCount}{bgTag}";
                    DrawMetricBar(innerX, cursorY, innerW, line, pol, ColorForPollution(pol));
                    cursorY += 34f;
                }
            }

            cursorY += 4f;

            // Live trash attribution breakdown (zero magic spawning proof)
            int citizenTrash = eco.GetActiveTrashCount(WasteSourceKind.Citizen);
            int binTrash = eco.GetActiveTrashCount(WasteSourceKind.ShopOverflow);
            int factoryTrash = eco.GetActiveTrashCount(WasteSourceKind.FactoryScrap);
            int decayTrash = eco.GetActiveTrashCount(WasteSourceKind.UrbanDecay);

            GUI.Label(new Rect(innerX, cursorY, innerW, 18f), $"ACTIVE TRASH BY CAUSE ({eco.TotalActiveTrash}/{eco.MaxGlobalTrash})", labelStyle);
            cursorY += 19f;
            GUI.Label(new Rect(innerX, cursorY, innerW, 17f), $"Citizens: {citizenTrash}   ·   Overflow Bins: {binTrash}", smallStyle);
            cursorY += 17f;
            GUI.Label(new Rect(innerX, cursorY, innerW, 17f), $"Factory Scrap: {factoryTrash}   ·   Urban Decay: {decayTrash}", smallStyle);
            cursorY += 20f;

            if (citizenSim != null)
            {
                GUI.Label(
                    new Rect(innerX, cursorY, innerW, 17f),
                    $"NPCs: {citizenSim.ActiveTier1Count} Visible (Tier 1) · {citizenSim.BackgroundPopulationCount} Batch BG (Tier 2)",
                    smallStyle);
                cursorY += 20f;
            }

            // Active source quick status
            int overflowingBins = 0;
            int totalBins = eco.AllStreetBins.Count;
            for (int i = 0; i < totalBins; i++)
            {
                if (eco.AllStreetBins[i] != null && eco.AllStreetBins[i].IsOverflowing)
                    overflowingBins++;
            }

            int totalFactories = 0;
            int filteredFactories = 0;
            IReadOnlyList<IPollutionSource> sources = eco.AllSources;
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] is FactorySmokestackSource factory)
                {
                    totalFactories++;
                    if (factory.IsScrubberActive)
                        filteredFactories++;
                }
            }

            string binStatus = overflowingBins > 0 ? $"{overflowingBins}/{totalBins} OVERFLOWING" : $"All {totalBins} Clean";
            string facStatus = totalFactories == 0 ? "None" : $"{filteredFactories}/{totalFactories} Filtered";
            GUI.Label(new Rect(innerX, cursorY, innerW, 17f), $"Street Bins: {binStatus}  |  Scrubbers: {facStatus}", smallStyle);
        }

        void DrawSmogWarningBanner(CityEcosystemManager eco)
        {
            if (eco.PlayerSmogExposure < 0.55f)
                return;

            float w = 360f;
            float x = (Screen.width - w) * 0.5f;
            float y = 20f;

            if (eco.IsPlayerSmogProtected)
            {
                DrawBox(new Rect(x, y, w, 30f), new Color(0.14f, 0.52f, 0.64f, 0.92f));
                GUI.Label(new Rect(x, y, w, 30f), $"RESPIRATOR ACTIVE · Smog {Mathf.RoundToInt(eco.PlayerSmogExposure * 100f)}% (No Slowdown)", warnStyle);
            }
            else if (eco.IsPlayerSlowedBySmog)
            {
                DrawBox(new Rect(x, y, w, 30f), new Color(0.76f, 0.24f, 0.16f, 0.92f));
                GUI.Label(new Rect(x, y, w, 30f), $"THICK SMOG ({Mathf.RoundToInt(eco.PlayerSmogExposure * 100f)}%) · Movement Slowed (Equip Mask!)", warnStyle);
            }
        }

        void DrawMetricBar(float x, float y, float w, string caption, float fill01, Color fillColor)
        {
            GUI.Label(new Rect(x, y, w, 17f), caption, smallStyle);
            float barY = y + 18f;
            DrawBox(new Rect(x, barY, w, 8f), BarBg);
            DrawBox(new Rect(x, barY, w * Mathf.Clamp01(fill01), 8f), fillColor);
        }

        void DrawBox(Rect rect, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, whiteTex);
            GUI.color = prev;
        }

        static Color ColorForPollution(float pollution)
        {
            if (pollution <= 0.25f) return CleanGreen;
            if (pollution <= 0.58f) return WarnAmber;
            return PollutedRed;
        }

        static Color ColorForSatisfaction(float satisfaction)
        {
            if (satisfaction >= 0.72f) return CleanGreen;
            if (satisfaction >= 0.42f) return InfoTeal;
            return PollutedRed;
        }

        static string AtmosphereLabel(float pollution)
        {
            if (pollution <= 0.20f) return "CLEAN";
            if (pollution <= 0.45f) return "RECOVERING";
            if (pollution <= 0.70f) return "HAZY";
            return "TOXIC SMOG";
        }

        static string SatisfactionMood(float satisfaction)
        {
            if (satisfaction >= 0.80f) return "Civic Pride";
            if (satisfaction >= 0.55f) return "Content";
            if (satisfaction >= 0.35f) return "Frustrated";
            return "Littering!";
        }
    }
}
