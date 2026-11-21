using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Crowd
{
    /// <summary>The characters a crowd can be made of: each one's animated rig and its posed meshes.</summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crowd Character Library", fileName = "CrowdCharacters")]
    public sealed class CrowdCharacterLibrary : ScriptableObject
    {
        [Serializable]
        public sealed class Character
        {
            public string name;
            [Tooltip("Wears a smog mask: for the polluted city's crowd.")]
            public bool masked;
            [Tooltip("Rigged Humanoid model, animated near the camera.")]
            public GameObject rigged;
            public GameObject stand;
            public GameObject walk;
            public GameObject sit;

            public GameObject PoseFor(CrowdActivity activity)
            {
                switch (activity)
                {
                    case CrowdActivity.Walk: return walk;
                    case CrowdActivity.Sit: return sit;
                    default: return stand;
                }
            }
        }

        [SerializeField] List<Character> characters = new List<Character>();

        public IReadOnlyList<Character> Characters => characters;

        /// <summary>Adds the usable characters with or without a mask to <paramref name="results"/>.</summary>
        public void Collect(List<Character> results, bool masked)
        {
            foreach (Character character in characters)
            {
                if (character.masked == masked && character.rigged != null)
                    results.Add(character);
            }
        }

        public void SetCharacters(IEnumerable<Character> newCharacters)
        {
            characters.Clear();
            characters.AddRange(newCharacters);
        }
    }
}
