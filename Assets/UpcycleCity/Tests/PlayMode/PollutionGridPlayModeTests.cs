using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UpcycleCity.Core;
using UpcycleCity.Pollution;

namespace UpcycleCity.Tests.PlayMode
{
    /// <summary>
    /// Solo behaviour of <see cref="PollutionGrid"/>: pollution leaks from a dirty cell to its neighbours with an
    /// amount that decays per hop, clears gradually once the cause is gone, and its 4×4 chunk sync records
    /// round-trip every cell (the host/client loopback test lives in <see cref="NetworkingPlayModeTests"/>).
    /// </summary>
    public sealed class PollutionGridPlayModeTests
    {
        const float Leak = 0.65f;
        const float Capacity = 2.5f;

        readonly List<GameObject> roots = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = roots.Count - 1; i >= 0; i--)
                if (roots[i] != null)
                    Object.DestroyImmediate(roots[i]);
            roots.Clear();
            SyncedStateRegistry.Clear();
        }

        PollutionGrid CreateGrid(Vector2Int cells, SyncedStateRegistryInstance registry = null)
        {
            var go = new GameObject("TestPollutionGrid");
            go.SetActive(false);
            roots.Add(go);
            PollutionGrid grid = go.AddComponent<PollutionGrid>();
            grid.Configure(Vector2.zero, 4f, cells);
            grid.ConfigureLeak(Capacity, Leak, rise: 0.25f, recovery: 0.14f, spreadRadius: 9f);
            grid.SetStateRegistry(registry ?? new SyncedStateRegistryInstance());
            go.SetActive(true);
            return grid;
        }

        [UnityTest]
        public IEnumerator Leak_DecaysPerHop_AndClearsGraduallyAfterCleanup()
        {
            PollutionGrid grid = CreateGrid(new Vector2Int(11, 11));
            yield return null;   // Start (seeding) has run

            grid.BeginDeposits();
            grid.Deposit(grid.CellCentre(5, 5), Capacity);   // one fully polluted cell
            grid.Simulate(0.25f, snapImmediate: true);

            float centre = grid.GetLevel(5, 5);
            float oneHop = grid.GetLevel(6, 5);
            float twoHops = grid.GetLevel(7, 5);
            float threeHops = grid.GetLevel(8, 5);
            float diagonal = grid.GetLevel(6, 6);

            Assert.AreEqual(1f, centre, 0.001f, "A cell at capacity is fully polluted.");
            Assert.AreEqual(Leak, oneHop, 0.02f, "A side neighbour receives the leak strength.");
            Assert.AreEqual(Leak * Leak, twoHops, 0.02f, "The leak is applied again on every hop.");
            Assert.AreEqual(Leak * Leak * Leak, threeHops, 0.02f);
            Assert.AreEqual(Mathf.Pow(Leak, 1.41421356f), diagonal, 0.02f, "Diagonals leak as strength^√2.");
            Assert.Less(grid.GetLevel(10, 10), 0.1f, "Far cells stay almost clean.");
            Assert.AreEqual(grid.GetLevel(4, 5), oneHop, 0.001f, "Leaking is symmetric.");

            // Cause removed: the cell clears at the recovery rate, not instantly
            grid.BeginDeposits();
            grid.Simulate(0.5f);
            float afterHalfSecond = grid.GetLevel(5, 5);
            Assert.Less(afterHalfSecond, centre);
            Assert.Greater(afterHalfSecond, 0.85f, "Grime clears gradually after clean-up.");

            for (int i = 0; i < 80; i++)
            {
                grid.BeginDeposits();
                grid.Simulate(0.25f);
            }
            Assert.Less(grid.GetLevel(5, 5), 0.01f, "With no cause left the whole field returns to clean.");
            Assert.Less(grid.GetLevel(6, 5), 0.01f);
        }

        [UnityTest]
        public IEnumerator Deposit_WithRadius_SpreadsLoad_AndSampleBlendsAtBorder()
        {
            PollutionGrid grid = CreateGrid(new Vector2Int(16, 16));
            yield return null;

            grid.ConfigureLeak(Capacity, 0f, rise: 0.25f, recovery: 0.14f, spreadRadius: 9f);   // no leak: splat only
            grid.BeginDeposits();
            grid.Deposit(grid.CellCentre(8, 8), 6f, radius: 10f);
            grid.Simulate(0.25f, snapImmediate: true);

            Assert.Greater(grid.GetLevel(8, 8), grid.GetLevel(10, 8), "A spread deposit is strongest in the middle.");
            Assert.Greater(grid.GetLevel(10, 8), 0f, "A spread deposit reaches cells inside its radius.");
            Assert.AreEqual(0f, grid.GetLevel(12, 8), 0.001f, "Nothing outside the radius without leaking.");

            Assert.AreEqual(1f, grid.Coverage(grid.CellCentre(8, 8)), 0.001f);
            Assert.AreEqual(0f, grid.Coverage(new Vector3(-10f, 0f, 30f)), 0.001f, "Outside the grid the scene level is kept.");
            Assert.AreEqual(0.4f, grid.Apply(0.4f, new Vector3(-10f, 0f, 30f)), 0.001f);
        }

        [UnityTest]
        public IEnumerator ChunkRecords_RoundTripEveryCell_IncludingPartialEdgeChunks()
        {
            // 10×6 cells: the last chunk column/row is partial (cells 8–9 / 4–5)
            PollutionGrid host = CreateGrid(new Vector2Int(10, 6));
            PollutionGrid client = CreateGrid(new Vector2Int(10, 6));
            yield return null;

            var random = new System.Random(1234);
            for (int z = 0; z < host.CellsZ; z++)
                for (int x = 0; x < host.CellsX; x++)
                    host.SetLevel(x, z, (float)random.NextDouble());

            Assert.AreEqual(6, host.SyncProviders.Count, "3×2 chunks of 4×4 cells.");
            for (int i = 0; i < host.SyncProviders.Count; i++)
            {
                Assert.AreEqual(host.SyncProviders[i].SyncKey, client.SyncProviders[i].SyncKey, "Keys are deterministic on every peer.");
                SyncedStateRecord record = host.SyncProviders[i].CaptureSyncState();
                client.SyncProviders[i].ApplySyncState(in record);
            }

            for (int z = 0; z < host.CellsZ; z++)
                for (int x = 0; x < host.CellsX; x++)
                    Assert.AreEqual(host.GetLevel(x, z), client.GetLevel(x, z), 1f / 255f + 1e-4f, $"Cell {x},{z}");
        }
    }
}
