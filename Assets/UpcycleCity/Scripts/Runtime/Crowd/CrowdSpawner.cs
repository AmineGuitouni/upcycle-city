using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Pollution;

namespace UpcycleCity.Crowd
{
    /// <summary>
    /// Fills paths with walkers when the scene starts: background crowds that would be tedious to
    /// place one by one. Hand-placed <see cref="CrowdAgent"/>s and spawned ones run the same way.
    /// </summary>
    [AddComponentMenu("Upcycle City/Crowd/Crowd Spawner")]
    public sealed class CrowdSpawner : MonoBehaviour
    {
        [SerializeField] CrowdCharacterLibrary characters;

        [Tooltip("Paths the walkers are spread over.")]
        [SerializeField] CrowdPath[] paths;

        [SerializeField, Min(0)] int count = 40;

        [Tooltip("Use the characters that wear a smog mask.")]
        [SerializeField] bool masked;

        [Tooltip("Which city the walkers belong to.")]
        [SerializeField] PollutionVisibilityMode visibility = PollutionVisibilityMode.Always;

        [Tooltip("Walking speed range, in m/s.")]
        [SerializeField] Vector2 walkSpeed = new Vector2(1.1f, 1.5f);

        [Tooltip("Walkers keep within this distance either side of the path, in metres.")]
        [SerializeField, Min(0f)] float laneWidth = 0.35f;

        [SerializeField] int seed = 1;

        void Start() => Spawn();

        /// <summary>Spawns <see cref="count"/> walkers spread along the paths.</summary>
        public void Spawn()
        {
            if (characters == null || paths == null || paths.Length == 0)
                return;
            var cast = new List<CrowdCharacterLibrary.Character>();
            characters.Collect(cast, masked);
            if (cast.Count == 0)
                return;

            var random = new System.Random(seed);
            float U(float a, float b) => a + (float)random.NextDouble() * (b - a);
            for (int i = 0; i < count; i++)
            {
                CrowdPath path = paths[random.Next(paths.Length)];
                if (path == null || path.SegmentCount == 0)
                    continue;
                CrowdCharacterLibrary.Character character = cast[random.Next(cast.Count)];

                Vector3 position = path.PointAt(U(0f, path.Length), out Vector3 tangent);
                Vector3 right = new Vector3(tangent.z, 0f, -tangent.x);
                Vector3 facing = random.NextDouble() < 0.5 ? tangent : -tangent;

                // Built inactive so the agent registers with its settings in place
                var root = new GameObject(character.name);
                root.SetActive(false);
                root.transform.SetParent(transform, false);
                root.transform.SetPositionAndRotation(position + right * U(-laneWidth, laneWidth), Quaternion.LookRotation(facing));
                GameObject visual = character.walk != null ? Instantiate(character.walk, root.transform) : null;
                root.AddComponent<CrowdAgent>().Configure(CrowdActivity.Walk, character.rigged, visual, path, U(walkSpeed.x, walkSpeed.y));
                if (visibility != PollutionVisibilityMode.Always)
                    root.AddComponent<PollutionVisibility>().Mode = visibility;
                root.SetActive(true);
            }
        }
    }
}
