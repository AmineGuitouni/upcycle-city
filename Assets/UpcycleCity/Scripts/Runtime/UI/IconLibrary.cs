using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.UI
{
    /// <summary>
    /// Runtime registry of UI icon sprites loaded from <c>Resources/IconLibrary.asset</c>.
    /// All lookups are zero-GC once internal dictionaries are built on first access.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/UI/Icon Library", fileName = "IconLibrary")]
    public sealed class IconLibrary : ScriptableObject
    {
        const string ResourcePath = "IconLibrary";
        const string LootPrefix = "loot_";
        const string MaterialPrefix = "mat_";
        const string StreamPrefix = "stream_";

        [Serializable]
        public struct Entry
        {
            public string key;
            public Sprite sprite;
        }

        [SerializeField] List<Entry> entries = new List<Entry>();

        static IconLibrary _active;
        static bool _activeLoaded;

        Dictionary<string, Sprite> _byKey;
        Dictionary<string, Sprite> _lootById;
        Dictionary<string, Sprite> _materialById;
        Dictionary<string, Sprite> _streamByName;
        bool _lookupBuilt;

        /// <summary>
        /// Active icon library loaded once from <c>Resources/IconLibrary.asset</c>.
        /// </summary>
        public static IconLibrary Active
        {
            get
            {
                if (!_activeLoaded)
                {
                    _active = Resources.Load<IconLibrary>(ResourcePath);
                    _activeLoaded = true;
                }
                return _active;
            }
        }

        /// <summary>Serialized icon entries.</summary>
        public IReadOnlyList<Entry> Entries => entries;

        /// <summary>Total number of serialized entries.</summary>
        public int Count => entries != null ? entries.Count : 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _active = null;
            _activeLoaded = false;
        }

        void OnEnable()
        {
            _lookupBuilt = false;
        }

        void OnValidate()
        {
            _lookupBuilt = false;
        }

        /// <summary>Returns the sprite registered under <paramref name="key"/>, or <c>null</c> if missing.</summary>
        public Sprite Get(string key)
        {
            return TryGet(key, out var sprite) ? sprite : null;
        }

        /// <summary>Tries to fetch the sprite registered under <paramref name="key"/> (zero GC after first lookup).</summary>
        public bool TryGet(string key, out Sprite sprite)
        {
            if (string.IsNullOrEmpty(key))
            {
                sprite = null;
                return false;
            }

            EnsureLookup();
            return _byKey.TryGetValue(key, out sprite) && sprite != null;
        }

        /// <summary>Looks up a player gear/shop item icon by <paramref name="itemId"/>.</summary>
        public Sprite ForItem(string itemId)
        {
            return Get(itemId);
        }

        /// <summary>Looks up a street trash archetype icon by <paramref name="archetypeId"/>.</summary>
        public Sprite ForTrash(string archetypeId)
        {
            return Get(archetypeId);
        }

        /// <summary>
        /// Looks up a bin loot icon by <c>loot_{lootId}</c>, falling back to the matching trash key <paramref name="lootId"/>
        /// (zero GC after first lookup).
        /// </summary>
        public Sprite ForLoot(string lootId)
        {
            if (string.IsNullOrEmpty(lootId))
                return null;

            EnsureLookup();
            if (_lootById.TryGetValue(lootId, out var sprite) && sprite != null)
                return sprite;

            if (_byKey.TryGetValue(lootId, out sprite) && sprite != null)
                return sprite;

            return null;
        }

        /// <summary>Looks up a crafting material icon by <c>mat_{materialId}</c> (zero GC after first lookup).</summary>
        public Sprite ForMaterial(string materialId)
        {
            if (string.IsNullOrEmpty(materialId))
                return null;

            EnsureLookup();
            if (_materialById.TryGetValue(materialId, out var sprite) && sprite != null)
                return sprite;

            if (_byKey.TryGetValue(materialId, out sprite) && sprite != null)
                return sprite;

            return null;
        }

        /// <summary>
        /// Looks up a sorting stream icon by <c>stream_{lowercase streamName}</c>
        /// (case-insensitive, zero GC after first lookup).
        /// </summary>
        public Sprite ForStream(string streamName)
        {
            if (string.IsNullOrEmpty(streamName))
                return null;

            EnsureLookup();
            if (_streamByName.TryGetValue(streamName, out var sprite) && sprite != null)
                return sprite;

            if (_byKey.TryGetValue(streamName, out sprite) && sprite != null)
                return sprite;

            return null;
        }

        /// <summary>Replaces serialized entries and marks lookup caches dirty (intended for editor tooling).</summary>
        public void SetEntries(List<Entry> newEntries)
        {
            entries = newEntries ?? new List<Entry>();
            _lookupBuilt = false;
            if (_active == this)
                EnsureLookup();
        }

        void EnsureLookup()
        {
            if (_lookupBuilt && _byKey != null)
                return;

            int capacity = entries != null ? entries.Count : 0;
            if (_byKey == null)
            {
                _byKey = new Dictionary<string, Sprite>(capacity, StringComparer.OrdinalIgnoreCase);
                _lootById = new Dictionary<string, Sprite>(16, StringComparer.OrdinalIgnoreCase);
                _materialById = new Dictionary<string, Sprite>(16, StringComparer.OrdinalIgnoreCase);
                _streamByName = new Dictionary<string, Sprite>(8, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                _byKey.Clear();
                _lootById.Clear();
                _materialById.Clear();
                _streamByName.Clear();
            }

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (string.IsNullOrEmpty(entry.key) || entry.sprite == null)
                        continue;

                    _byKey[entry.key] = entry.sprite;

                    if (entry.key.StartsWith(LootPrefix, StringComparison.OrdinalIgnoreCase) && entry.key.Length > LootPrefix.Length)
                    {
                        _lootById[entry.key.Substring(LootPrefix.Length)] = entry.sprite;
                    }
                    else if (entry.key.StartsWith(MaterialPrefix, StringComparison.OrdinalIgnoreCase) && entry.key.Length > MaterialPrefix.Length)
                    {
                        _materialById[entry.key.Substring(MaterialPrefix.Length)] = entry.sprite;
                    }
                    else if (entry.key.StartsWith(StreamPrefix, StringComparison.OrdinalIgnoreCase) && entry.key.Length > StreamPrefix.Length)
                    {
                        _streamByName[entry.key.Substring(StreamPrefix.Length)] = entry.sprite;
                    }
                }
            }

            _lookupBuilt = true;
        }
    }
}
