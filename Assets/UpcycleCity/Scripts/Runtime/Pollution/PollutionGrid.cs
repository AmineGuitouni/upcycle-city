using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Pollution
{
    /// <summary>
    /// A flat grid of pollution cells laid over the city. Every cell holds its own pollution level (0 clean,
    /// 1 polluted) which comes from two things:
    /// <list type="bullet">
    /// <item>the load deposited in the cell this tick (trash lying there, an overflowing bin, smokestack smog), and</item>
    /// <item>pollution leaking in from its 8 neighbours, reduced by <see cref="LeakStrength"/> on every hop,
    /// so a dirty spot stains the streets around it with an amount that decays with distance.</item>
    /// </list>
    /// The grid is uploaded to the shaders as a small R8 texture (<c>_PollutionGridTex</c>, sampled by
    /// <c>SamplePollution()</c> in Pollution.hlsl), so the grime, foliage, smog and clean-up dissolve follow it.
    ///
    /// <para><b>Co-op:</b> only the authority (the host, or the local game in solo) feeds and steps the grid, through
    /// <see cref="BeginDeposits"/>, <see cref="Deposit"/> and <see cref="Simulate"/> (called by CityEcosystemManager).
    /// The cells are replicated to clients in 4×4 chunks through <see cref="SyncedStateRegistry"/> (one byte per cell,
    /// only changed chunks are sent). Every peer smooths the received levels locally for the visuals.</para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-60)]
    [AddComponentMenu("Upcycle City/Pollution Grid")]
    public sealed class PollutionGrid : MonoBehaviour
    {
        /// <summary>Cells per side of one replicated chunk (16 cells = 16 bytes = one <see cref="SyncedStateRecord"/>).</summary>
        public const int ChunkSize = 4;

        [Header("Layout")]
        [Tooltip("World X/Z of the grid's minimum corner.")]
        [SerializeField] Vector2 origin = new Vector2(-50f, -90f);

        [Tooltip("Width of one cell, in metres.")]
        [SerializeField, Min(1f)] float cellSize = 4f;

        [Tooltip("Number of cells along X and Z. Changing it changes the replicated keys: host and clients must use the same scene.")]
        [SerializeField] Vector2Int cellCount = new Vector2Int(32, 48);

        [Header("Leaking")]
        [Tooltip("Load (≈ pieces of trash) that makes one cell fully polluted on its own. Scaled by the balance's district capacity.")]
        [SerializeField, Min(0.1f)] float cellCapacity = 2.5f;

        [Tooltip("Share of a cell's pollution that leaks into each side neighbour. It is applied again on every hop, " +
                 "so a cell N cells away receives about strength^N. Diagonal neighbours receive strength^1.41.")]
        [SerializeField, Range(0f, 0.95f)] float leakStrength = 0.65f;

        [Tooltip("Radius, in metres, over which air sources (smokestacks, overflowing bins, decay) spread their load.")]
        [SerializeField, Min(0f)] float airSpreadRadius = 9f;

        [Tooltip("How fast a cell gets dirtier, per second.")]
        [SerializeField, Min(0.01f)] float riseRate = 0.25f;

        [Tooltip("How fast a cell clears once its cause is gone, per second. Grime clears gradually after clean-up.")]
        [SerializeField, Min(0.01f)] float recoveryRate = 0.14f;

        [Header("Look")]
        [Tooltip("Cells over which the grid fades into the scene-wide level at its border.")]
        [SerializeField, Min(0f)] float edgeFadeCells = 2f;

        [Tooltip("World-space noise that wobbles the lookup, so cell edges read as organic stains instead of squares (in cells).")]
        [SerializeField, Range(0f, 2f)] float edgeJitterCells = 0.7f;

        [Tooltip("Frequency of the jitter noise (1/m).")]
        [SerializeField, Min(0.001f)] float jitterNoiseScale = 0.11f;

        [Tooltip("How quickly the rendered grid catches up with the simulated (or received) levels. " +
                 "Hides the 4 Hz simulation/network steps.")]
        [SerializeField, Min(0.5f)] float displaySharpness = 5f;

        /// <summary>The grid of the loaded scene, if any.</summary>
        public static PollutionGrid Active { get; private set; }

        /// <summary>Raised (at simulation/network rate, not per frame) when the cell levels change.</summary>
        public static event Action<PollutionGrid> Changed;

        float[] level;      // authoritative (host) or last received (client) level per cell
        float[] next;       // simulation scratch
        float[] load;       // load deposited this tick
        float[] display;    // smoothed level shown by the shaders
        byte[] pixels;      // R8 texture payload
        Texture2D texture;
        readonly List<Chunk> chunks = new List<Chunk>(128);
        SyncedStateRegistryInstance registry;
        bool levelsChanged;
        bool texturePending;
        bool allocated;
        bool hasState;      // simulated or received at least once: never re-seed over it

        /// <summary>Multiplies <see cref="cellCapacity"/> (driven by EcosystemBalance.districtCapacityScale).</summary>
        public float CapacityScale { get; set; } = 1f;

        public int CellsX => cellCount.x;
        public int CellsZ => cellCount.y;
        public int CellCount => cellCount.x * cellCount.y;
        public float CellSize => cellSize;
        public Vector2 Origin => origin;
        public float LeakStrength => leakStrength;
        public float AirSpreadRadius => airSpreadRadius;
        public Texture Texture => texture;

        /// <summary>One sync provider per 4×4 chunk, in a stable order.</summary>
        public IReadOnlyList<ISyncedStateProvider> SyncProviders => chunks;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
            Changed = null;
        }

        // ---------------------------------------------------------------- setup

        /// <summary>Sets the layout (before the grid is enabled, e.g. from a scene builder or a test).</summary>
        public void Configure(Vector2 gridOrigin, float size, Vector2Int count)
        {
            origin = gridOrigin;
            cellSize = Mathf.Max(1f, size);
            cellCount = new Vector2Int(Mathf.Clamp(count.x, 1, 256), Mathf.Clamp(count.y, 1, 256));
            if (allocated)
            {
                Release();
                Allocate();
            }
        }

        /// <summary>Sets the leak tuning (for builders and tests).</summary>
        public void ConfigureLeak(float capacity, float strength, float rise, float recovery, float spreadRadius)
        {
            cellCapacity = Mathf.Max(0.1f, capacity);
            leakStrength = Mathf.Clamp(strength, 0f, 0.95f);
            riseRate = Mathf.Max(0.01f, rise);
            recoveryRate = Mathf.Max(0.01f, recovery);
            airSpreadRadius = Mathf.Max(0f, spreadRadius);
        }

        /// <summary>
        /// Replicates through another registry than <see cref="SyncedStateRegistry.Default"/>
        /// (multi-instance host/client tests). Call before or after enabling.
        /// </summary>
        public void SetStateRegistry(SyncedStateRegistryInstance stateRegistry)
        {
            UnregisterChunks();
            registry = stateRegistry;
            if (isActiveAndEnabled && allocated)
                RegisterChunks();
        }

        SyncedStateRegistryInstance Registry => registry ?? SyncedStateRegistry.Default;

        void OnEnable()
        {
            if (Active != null && Active != this)
                Debug.LogWarning("There is more than one PollutionGrid; the last one enabled wins.", this);
            Active = this;
            Allocate();
            RegisterChunks();
            PushShaderGlobals();
        }

        // After every OnEnable, so the controller and the district zones it seeds from are registered,
        // and before CityEcosystemManager.Start (execution order -40) runs the first simulation snap.
        void Start()
        {
            if (!hasState)
                SeedFromScene();
        }

        void OnDisable()
        {
            UnregisterChunks();
            if (Active == this)
            {
                Active = null;
                PollutionShaderGlobals.ClearGrid();
                PollutionController.NotifyZonesChanged();
            }
            Release();
        }

        void Allocate()
        {
            hasState = false;
            int n = CellCount;
            level = new float[n];
            next = new float[n];
            load = new float[n];
            display = new float[n];
            pixels = new byte[n];
            texture = new Texture2D(cellCount.x, cellCount.y, TextureFormat.R8, mipChain: false, linear: true)
            {
                name = "PollutionGrid",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            chunks.Clear();
            int chunksX = (cellCount.x + ChunkSize - 1) / ChunkSize;
            int chunksZ = (cellCount.y + ChunkSize - 1) / ChunkSize;
            for (int cz = 0; cz < chunksZ; cz++)
                for (int cx = 0; cx < chunksX; cx++)
                    chunks.Add(new Chunk(this, cx, cz));
            allocated = true;
        }

        void Release()
        {
            allocated = false;
            if (texture != null)
            {
                if (Application.isPlaying)
                    Destroy(texture);
                else
                    DestroyImmediate(texture);
            }
            texture = null;
            chunks.Clear();
        }

        /// <summary>
        /// Starts every cell from what the scene already shows (scene-wide level and the authored
        /// district zones), so the first frames match the editor and clients look right before the first sync.
        /// </summary>
        void SeedFromScene()
        {
            PollutionController controller = PollutionController.Active;
            for (int z = 0; z < cellCount.y; z++)
            {
                for (int x = 0; x < cellCount.x; x++)
                {
                    int i = x + z * cellCount.x;
                    float value = controller != null ? controller.SampleWithoutGrid(CellCentre(x, z)) : 0f;
                    level[i] = value;
                    display[i] = value;
                }
            }
            WriteTexture(force: true);
        }

        void RegisterChunks()
        {
            SyncedStateRegistryInstance target = Registry;
            for (int i = 0; i < chunks.Count; i++)
                target.Register(chunks[i]);
        }

        void UnregisterChunks()
        {
            SyncedStateRegistryInstance target = Registry;
            for (int i = 0; i < chunks.Count; i++)
                target.Unregister(chunks[i]);
        }

        void PushShaderGlobals()
        {
            if (texture == null)
                return;
            Vector2 worldSize = new Vector2(cellCount.x * cellSize, cellCount.y * cellSize);
            PollutionShaderGlobals.SetGrid(
                texture,
                new Vector4(origin.x, origin.y, 1f / worldSize.x, 1f / worldSize.y),
                new Vector4(1f, cellCount.x, cellCount.y, edgeFadeCells),
                new Vector4(edgeJitterCells, jitterNoiseScale, 0f, 0f));
            PollutionController.NotifyZonesChanged();
        }

        void OnValidate()
        {
            if (allocated && Active == this)
                PushShaderGlobals();
        }

        // ---------------------------------------------------------------- authority (host / solo) API

        /// <summary>Clears this tick's loads. Authority only.</summary>
        public void BeginDeposits()
        {
            if (allocated)
                Array.Clear(load, 0, load.Length);
        }

        /// <summary>
        /// Adds pollution load at a world position. With a radius the load is spread over the cells inside it
        /// (more in the middle); without, it all goes to the cell under the position. Authority only.
        /// </summary>
        public void Deposit(Vector3 position, float amount, float radius = 0f)
        {
            if (!allocated || amount <= 0f)
                return;

            if (radius <= cellSize * 0.5f)
            {
                if (TryGetCell(position, out int cx, out int cz))
                    load[cx + cz * cellCount.x] += amount;
                return;
            }

            int minX = Mathf.Max(0, Mathf.FloorToInt((position.x - radius - origin.x) / cellSize));
            int maxX = Mathf.Min(cellCount.x - 1, Mathf.FloorToInt((position.x + radius - origin.x) / cellSize));
            int minZ = Mathf.Max(0, Mathf.FloorToInt((position.z - radius - origin.y) / cellSize));
            int maxZ = Mathf.Min(cellCount.y - 1, Mathf.FloorToInt((position.z + radius - origin.y) / cellSize));
            if (minX > maxX || minZ > maxZ)
                return;

            // Cone weights, normalised so the total deposited load is preserved
            float total = 0f;
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                    total += SplatWeight(position, x, z, radius);
            if (total <= 0f)
            {
                if (TryGetCell(position, out int cx, out int cz))
                    load[cx + cz * cellCount.x] += amount;
                return;
            }

            float scale = amount / total;
            for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                    load[x + z * cellCount.x] += SplatWeight(position, x, z, radius) * scale;
        }

        float SplatWeight(Vector3 position, int x, int z, float radius)
        {
            float dx = origin.x + (x + 0.5f) * cellSize - position.x;
            float dz = origin.y + (z + 0.5f) * cellSize - position.z;
            return Mathf.Max(0f, 1f - Mathf.Sqrt(dx * dx + dz * dz) / radius);
        }

        /// <summary>
        /// Steps the grid: each cell moves towards the pollution of its own load combined with what leaks in from
        /// its neighbours. With <paramref name="snapImmediate"/> it jumps to the settled state. Authority only.
        /// </summary>
        public void Simulate(float dt, bool snapImmediate = false)
        {
            if (!allocated)
                return;

            if (snapImmediate)
            {
                // Leaks travel one cell per pass: iterate until the field settles
                int maxPasses = cellCount.x + cellCount.y;
                for (int pass = 0; pass < maxPasses; pass++)
                {
                    if (Step(0f, snap: true) < 1e-4f)
                        break;
                }
                Array.Copy(level, display, level.Length);
                WriteTexture(force: true);
            }
            else
            {
                Step(dt, snap: false);
            }

            hasState = true;
            levelsChanged = true;
        }

        /// <returns>The largest change of any cell.</returns>
        float Step(float dt, bool snap)
        {
            int w = cellCount.x;
            int h = cellCount.y;
            float side = leakStrength;
            float diagonal = Mathf.Pow(leakStrength, 1.41421356f);
            float capacity = Mathf.Max(0.1f, cellCapacity * Mathf.Max(0.05f, CapacityScale));
            float rise = riseRate * dt;
            float recover = recoveryRate * dt;
            float maxDelta = 0f;

            for (int z = 0; z < h; z++)
            {
                int row = z * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;

                    // Strongest leak reaching this cell; each hop multiplies by the leak strength, so it fades with distance
                    float leak = 0f;
                    if (x > 0) leak = Mathf.Max(leak, level[i - 1] * side);
                    if (x < w - 1) leak = Mathf.Max(leak, level[i + 1] * side);
                    if (z > 0)
                    {
                        leak = Mathf.Max(leak, level[i - w] * side);
                        if (x > 0) leak = Mathf.Max(leak, level[i - w - 1] * diagonal);
                        if (x < w - 1) leak = Mathf.Max(leak, level[i - w + 1] * diagonal);
                    }
                    if (z < h - 1)
                    {
                        leak = Mathf.Max(leak, level[i + w] * side);
                        if (x > 0) leak = Mathf.Max(leak, level[i + w - 1] * diagonal);
                        if (x < w - 1) leak = Mathf.Max(leak, level[i + w + 1] * diagonal);
                    }

                    // Own load and incoming leak combine without exceeding 1 (and without feeding back above it)
                    float own = Mathf.Clamp01(load[i] / capacity);
                    float target = 1f - (1f - own) * (1f - leak);

                    float current = level[i];
                    float value = snap
                        ? target
                        : Mathf.MoveTowards(current, target, target > current ? rise : recover);
                    next[i] = value;

                    float delta = Mathf.Abs(value - current);
                    if (delta > maxDelta)
                        maxDelta = delta;
                }
            }

            float[] swap = level;
            level = next;
            next = swap;
            return maxDelta;
        }

        // ---------------------------------------------------------------- queries (all peers)

        /// <summary>World-space centre of a cell (y = 0).</summary>
        public Vector3 CellCentre(int x, int z) =>
            new Vector3(origin.x + (x + 0.5f) * cellSize, 0f, origin.y + (z + 0.5f) * cellSize);

        public bool TryGetCell(Vector3 position, out int x, out int z)
        {
            x = Mathf.FloorToInt((position.x - origin.x) / cellSize);
            z = Mathf.FloorToInt((position.z - origin.y) / cellSize);
            return x >= 0 && z >= 0 && x < cellCount.x && z < cellCount.y;
        }

        /// <summary>Level of one cell (0 when out of range).</summary>
        public float GetLevel(int x, int z)
        {
            if (!allocated || x < 0 || z < 0 || x >= cellCount.x || z >= cellCount.y)
                return 0f;
            return level[x + z * cellCount.x];
        }

        /// <summary>Overrides one cell's level (host-side tools and tests). Authority only.</summary>
        public void SetLevel(int x, int z, float value)
        {
            if (!allocated || x < 0 || z < 0 || x >= cellCount.x || z >= cellCount.y)
                return;
            level[x + z * cellCount.x] = Mathf.Clamp01(value);
            hasState = true;
            levelsChanged = true;
        }

        /// <summary>Bilinear level at a world position, clamped to the grid. Mirrors the texture lookup in Pollution.hlsl (without the jitter).</summary>
        public float Sample(Vector3 position)
        {
            if (!allocated)
                return 0f;
            float fx = Mathf.Clamp((position.x - origin.x) / cellSize - 0.5f, 0f, cellCount.x - 1);
            float fz = Mathf.Clamp((position.z - origin.y) / cellSize - 0.5f, 0f, cellCount.y - 1);
            int x0 = (int)fx;
            int z0 = (int)fz;
            int x1 = Mathf.Min(x0 + 1, cellCount.x - 1);
            int z1 = Mathf.Min(z0 + 1, cellCount.y - 1);
            float tx = fx - x0;
            float tz = fz - z0;
            int w = cellCount.x;
            float a = Mathf.Lerp(level[x0 + z0 * w], level[x1 + z0 * w], tx);
            float b = Mathf.Lerp(level[x0 + z1 * w], level[x1 + z1 * w], tx);
            return Mathf.Lerp(a, b, tz);
        }

        /// <summary>How much the grid overrides the scene-wide level here: 1 inside, fading to 0 across the border.</summary>
        public float Coverage(Vector3 position)
        {
            float u = (position.x - origin.x) / cellSize;
            float v = (position.z - origin.y) / cellSize;
            float edge = Mathf.Min(Mathf.Min(u, cellCount.x - u), Mathf.Min(v, cellCount.y - v));
            return edgeFadeCells <= 1e-3f ? (edge >= 0f ? 1f : 0f) : Mathf.Clamp01(edge / edgeFadeCells);
        }

        /// <summary>Blends <paramref name="sceneLevel"/> towards the grid at a world position.</summary>
        public float Apply(float sceneLevel, Vector3 position)
        {
            float coverage = Coverage(position);
            return coverage > 0f ? Mathf.Lerp(sceneLevel, Sample(position), coverage) : sceneLevel;
        }

        /// <summary>Average level of the cells whose centres lie inside a circle (district pollution).</summary>
        public float AverageInCircle(Vector3 centre, float radius)
        {
            if (!allocated)
                return 0f;

            int minX = Mathf.Max(0, Mathf.FloorToInt((centre.x - radius - origin.x) / cellSize));
            int maxX = Mathf.Min(cellCount.x - 1, Mathf.FloorToInt((centre.x + radius - origin.x) / cellSize));
            int minZ = Mathf.Max(0, Mathf.FloorToInt((centre.z - radius - origin.y) / cellSize));
            int maxZ = Mathf.Min(cellCount.y - 1, Mathf.FloorToInt((centre.z + radius - origin.y) / cellSize));

            float sum = 0f;
            int count = 0;
            float r2 = radius * radius;
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = origin.x + (x + 0.5f) * cellSize - centre.x;
                    float dz = origin.y + (z + 0.5f) * cellSize - centre.z;
                    if (dx * dx + dz * dz > r2)
                        continue;
                    sum += level[x + z * cellCount.x];
                    count++;
                }
            }
            return count > 0 ? sum / count : Sample(centre);
        }

        // ---------------------------------------------------------------- presentation (all peers)

        void LateUpdate()
        {
            if (!allocated)
                return;

            if (levelsChanged)
            {
                levelsChanged = false;
                texturePending = true;
                PollutionController.NotifyZonesChanged();   // responders, smog exposure, HUD
                Changed?.Invoke(this);
            }

            if (!texturePending)
                return;

            // Ease the shown levels towards the simulated/received ones so 4 Hz steps never pop
            float t = 1f - Mathf.Exp(-displaySharpness * Time.deltaTime);
            bool settled = true;
            for (int i = 0; i < display.Length; i++)
            {
                float target = level[i];
                float shown = display[i] + (target - display[i]) * t;
                if (Mathf.Abs(target - shown) < 0.002f)
                    shown = target;
                else
                    settled = false;
                display[i] = shown;
            }

            WriteTexture(force: false);
            texturePending = !settled;
        }

        void WriteTexture(bool force)
        {
            if (texture == null)
                return;

            bool changed = force;
            for (int i = 0; i < display.Length; i++)
            {
                byte b = ToByte(display[i]);
                if (pixels[i] != b)
                {
                    pixels[i] = b;
                    changed = true;
                }
            }

            if (!changed)
                return;
            texture.SetPixelData(pixels, 0);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        }

        static byte ToByte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);

        // ---------------------------------------------------------------- replication

        /// <summary>
        /// One 4×4 block of cells, replicated as 16 bytes: cells 0–7 in <c>i0</c>/<c>i1</c>, cells 8–15 as two
        /// bytes per float in <c>v0</c>–<c>v3</c> (whole numbers up to 65535 are exact in a float).
        /// </summary>
        sealed class Chunk : ISyncedStateProvider
        {
            readonly PollutionGrid grid;
            readonly int baseX;
            readonly int baseZ;

            public Chunk(PollutionGrid grid, int chunkX, int chunkZ)
            {
                this.grid = grid;
                baseX = chunkX * ChunkSize;
                baseZ = chunkZ * ChunkSize;
                SyncKey = $"pollutiongrid:{chunkX},{chunkZ}";
            }

            public string SyncKey { get; }

            int Read(int slot)
            {
                int x = baseX + (slot & 3);
                int z = baseZ + (slot >> 2);
                if (x >= grid.cellCount.x || z >= grid.cellCount.y)
                    return 0;
                return ToByte(grid.level[x + z * grid.cellCount.x]);
            }

            void Write(int slot, int value)
            {
                int x = baseX + (slot & 3);
                int z = baseZ + (slot >> 2);
                if (x >= grid.cellCount.x || z >= grid.cellCount.y)
                    return;
                grid.level[x + z * grid.cellCount.x] = (value & 0xFF) / 255f;
            }

            public SyncedStateRecord CaptureSyncState()
            {
                if (!grid.allocated)
                    return default;
                return new SyncedStateRecord
                {
                    i0 = Read(0) | (Read(1) << 8) | (Read(2) << 16) | (Read(3) << 24),
                    i1 = Read(4) | (Read(5) << 8) | (Read(6) << 16) | (Read(7) << 24),
                    v0 = Read(8) | (Read(9) << 8),
                    v1 = Read(10) | (Read(11) << 8),
                    v2 = Read(12) | (Read(13) << 8),
                    v3 = Read(14) | (Read(15) << 8),
                };
            }

            public void ApplySyncState(in SyncedStateRecord state)
            {
                if (!grid.allocated)
                    return;
                for (int s = 0; s < 4; s++)
                {
                    Write(s, state.i0 >> (s * 8));
                    Write(s + 4, state.i1 >> (s * 8));
                }
                Unpack(8, state.v0);
                Unpack(10, state.v1);
                Unpack(12, state.v2);
                Unpack(14, state.v3);
                grid.hasState = true;
                grid.levelsChanged = true;
            }

            void Unpack(int slot, float packed)
            {
                int bits = Mathf.Clamp(Mathf.RoundToInt(packed), 0, 0xFFFF);
                Write(slot, bits);
                Write(slot + 1, bits >> 8);
            }
        }

        // ---------------------------------------------------------------- editor

        /// <summary>Fits the grid around every PollutionZone in the scene, plus a margin.</summary>
        [ContextMenu("Fit To Pollution Zones")]
        public void FitToZones() => FitToZones(12f);

        public void FitToZones(float margin)
        {
            PollutionZone[] zones = FindObjectsByType<PollutionZone>(FindObjectsInactive.Exclude);
            if (zones.Length == 0)
                return;

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (PollutionZone zone in zones)
            {
                Vector3 c = zone.transform.position;
                float r = zone.Radius + margin;
                min = Vector2.Min(min, new Vector2(c.x - r, c.z - r));
                max = Vector2.Max(max, new Vector2(c.x + r, c.z + r));
            }

            Vector2Int count = new Vector2Int(
                Mathf.CeilToInt((max.x - min.x) / cellSize),
                Mathf.CeilToInt((max.y - min.y) / cellSize));
            Configure(min, cellSize, count);
        }

        void OnDrawGizmosSelected()
        {
            Vector3 size = new Vector3(cellCount.x * cellSize, 0f, cellCount.y * cellSize);
            Vector3 centre = new Vector3(origin.x + size.x * 0.5f, transform.position.y, origin.y + size.z * 0.5f);
            Gizmos.color = new Color(0.18f, 0.55f, 0.34f, 0.9f);
            Gizmos.DrawWireCube(centre, size);

            if (!allocated)
                return;

            // Live cell levels: green (clean) to brown (polluted)
            Color clean = new Color(0.3f, 1f, 0.7f, 0.25f);
            Color dirty = new Color(0.6f, 0.4f, 0.15f, 0.55f);
            Vector3 cube = new Vector3(cellSize * 0.92f, 0.05f, cellSize * 0.92f);
            for (int z = 0; z < cellCount.y; z++)
            {
                for (int x = 0; x < cellCount.x; x++)
                {
                    float value = level[x + z * cellCount.x];
                    Gizmos.color = Color.Lerp(clean, dirty, value);
                    Vector3 c = CellCentre(x, z);
                    c.y = transform.position.y + 0.2f;
                    Gizmos.DrawCube(c, cube);
                }
            }
        }
    }
}
