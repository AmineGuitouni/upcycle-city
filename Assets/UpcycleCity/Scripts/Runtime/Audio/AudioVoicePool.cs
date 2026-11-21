using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Audio
{
    /// <summary>
    /// Zero-GC pooled <see cref="AudioSource"/> engine supporting priority-based voice stealing,
    /// per-cue cooldown &amp; polyphony caps, shuffle-bag variation state, transform attachment, and fades.
    /// </summary>
    internal sealed class AudioVoicePool
    {
        sealed class CueRuntimeState
        {
            public float LastPlayTime = -999f;
            public float LastPitch = -1f;
            public int LastClipIndex = -1;
            public int ActiveCount;
            public int SequentialCursor;
            public int[] ShuffleOrder;
            public int ShuffleCursor;
        }

        struct VoiceSlot
        {
            public AudioSource Source;
            public Transform SourceTransform;
            public SoundCue Cue;
            public SoundCategory Category;
            public Transform FollowTarget;
            public int Generation;
            public float BaseVolume;
            public float StartTime;
            public float StopAtTime;
            public float FadeStartTime;
            public float FadeDuration;
            public float FadeStartVolume;
            public int Priority;
            public bool InUse;
            public bool FadingOut;
        }

        readonly Transform container;
        readonly int maxPoolSize;
        readonly Dictionary<SoundCue, CueRuntimeState> cueStates = new Dictionary<SoundCue, CueRuntimeState>(64);
        VoiceSlot[] slots;
        int allocatedCount;
        int nextGeneration = 1;

        public int ActiveVoiceCount { get; private set; }
        public int AllocatedVoiceCount => allocatedCount;

        public AudioVoicePool(Transform parent, int initialSize, int maxSize)
        {
            container = parent;
            int clampedMax = Mathf.Clamp(maxSize, 8, 128);
            int clampedInit = Mathf.Clamp(initialSize, 4, clampedMax);
            maxPoolSize = clampedMax;
            slots = new VoiceSlot[clampedMax];

            for (int i = 0; i < clampedInit; i++)
                AllocateSlot(i);
        }

        void AllocateSlot(int index)
        {
            var go = new GameObject($"Voice_{index:00}");
            go.transform.SetParent(container, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.dopplerLevel = 0f;

            slots[index] = new VoiceSlot
            {
                Source = src,
                SourceTransform = go.transform,
                InUse = false,
            };
            if (index >= allocatedCount)
                allocatedCount = index + 1;
        }

        public SoundHandle Play(
            SoundCue cue,
            Vector3? worldPosition,
            Transform followTarget,
            float volumeScale,
            float pitchScale,
            float categoryBusVolume)
        {
            if (cue == null || !cue.HasValidClip)
                return SoundHandle.Invalid;

            float now = Time.unscaledTime;
            CueRuntimeState state = GetOrCreateState(cue);

            // Cooldown gate prevents machine-gunning on rapid physics contacts
            if (cue.MinInterval > 0f && (now - state.LastPlayTime) < cue.MinInterval)
                return SoundHandle.Invalid;

            // Polyphony cap per cue
            if (cue.MaxConcurrent > 0 && state.ActiveCount >= cue.MaxConcurrent)
            {
                // Steal the oldest active voice of THIS same cue if we're at cap
                int sameCueSlot = FindOldestSlotForCue(cue);
                if (sameCueSlot < 0)
                    return SoundHandle.Invalid;
                ReleaseSlot(sameCueSlot);
            }

            AudioClip clip = PickClip(cue, state);
            if (clip == null)
                return SoundHandle.Invalid;

            int slotIndex = AcquireSlot(cue.Priority);
            if (slotIndex < 0)
                return SoundHandle.Invalid;

            float sampledVolume = Mathf.Clamp01(cue.SampleVolume() * Mathf.Max(0f, volumeScale));
            float sampledPitch = Mathf.Clamp(cue.SamplePitch(state.LastPitch) * Mathf.Clamp(pitchScale, 0.25f, 3f), 0.15f, 3f);

            state.LastPlayTime = now;
            state.LastPitch = sampledPitch;
            state.ActiveCount++;

            ref VoiceSlot slot = ref slots[slotIndex];
            AudioSource src = slot.Source;

            bool use3D = cue.Spatialize && (worldPosition.HasValue || followTarget != null);
            if (followTarget != null)
                slot.SourceTransform.position = followTarget.position;
            else if (worldPosition.HasValue)
                slot.SourceTransform.position = worldPosition.Value;
            else
                slot.SourceTransform.localPosition = Vector3.zero;

            src.clip = clip;
            src.loop = cue.Loop;
            src.pitch = sampledPitch;
            src.priority = cue.Priority;
            src.spatialBlend = use3D ? cue.SpatialBlend : 0f;
            src.minDistance = cue.MinDistance;
            src.maxDistance = cue.MaxDistance;
            src.rolloffMode = cue.RolloffMode;
            src.dopplerLevel = cue.DopplerLevel;
            src.volume = sampledVolume * categoryBusVolume;
            src.Play();

            int gen = nextGeneration++;
            if (nextGeneration <= 0)
                nextGeneration = 1;

            slot.Cue = cue;
            slot.Category = cue.Category;
            slot.FollowTarget = followTarget;
            slot.Generation = gen;
            slot.BaseVolume = sampledVolume;
            slot.StartTime = now;
            slot.StopAtTime = (!cue.Loop && cue.MaxDuration > 0f) ? (now + cue.MaxDuration) : 0f;
            slot.Priority = cue.Priority;
            slot.InUse = true;
            slot.FadingOut = false;

            ActiveVoiceCount++;
            return new SoundHandle(slotIndex, gen);
        }

        public void Tick(float now, System.Func<SoundCategory, float> getCategoryBusVolume)
        {
            for (int i = 0; i < allocatedCount; i++)
            {
                if (!slots[i].InUse)
                    continue;

                ref VoiceSlot slot = ref slots[i];
                AudioSource src = slot.Source;
                if (src == null)
                {
                    ReleaseSlot(i);
                    continue;
                }

                if (slot.FollowTarget != null)
                    slot.SourceTransform.position = slot.FollowTarget.position;

                if (slot.FadingOut)
                {
                    float t = slot.FadeDuration > 0.001f ? (now - slot.FadeStartTime) / slot.FadeDuration : 1f;
                    if (t >= 1f)
                    {
                        ReleaseSlot(i);
                        continue;
                    }
                    src.volume = Mathf.Lerp(slot.FadeStartVolume, 0f, t);
                }
                else if (slot.StopAtTime > 0f && now >= slot.StopAtTime)
                {
                    // Short smooth fade-out when hitting MaxDuration to avoid clicks
                    BeginFadeOut(ref slot, now, 0.04f);
                }
                else if (!src.isPlaying && (now - slot.StartTime) > 0.05f)
                {
                    ReleaseSlot(i);
                    continue;
                }
                else
                {
                    src.volume = slot.BaseVolume * getCategoryBusVolume(slot.Category);
                }
            }
        }

        public void RefreshCategoryVolumes(System.Func<SoundCategory, float> getCategoryBusVolume)
        {
            for (int i = 0; i < allocatedCount; i++)
            {
                if (!slots[i].InUse || slots[i].FadingOut || slots[i].Source == null)
                    continue;
                slots[i].Source.volume = slots[i].BaseVolume * getCategoryBusVolume(slots[i].Category);
            }
        }

        public bool Stop(SoundHandle handle, float fadeOutSeconds = 0f)
        {
            if (!IsHandleActive(handle))
                return false;

            if (fadeOutSeconds <= 0.001f)
            {
                ReleaseSlot(handle.SlotIndex);
            }
            else
            {
                BeginFadeOut(ref slots[handle.SlotIndex], Time.unscaledTime, fadeOutSeconds);
            }
            return true;
        }

        public void StopAll(SoundCategory? filter = null)
        {
            for (int i = 0; i < allocatedCount; i++)
            {
                if (!slots[i].InUse)
                    continue;
                if (filter.HasValue && slots[i].Category != filter.Value)
                    continue;
                ReleaseSlot(i);
            }
        }

        public bool IsPlaying(SoundHandle handle) => IsHandleActive(handle);

        bool IsHandleActive(SoundHandle handle)
        {
            return handle.IsValid
                && handle.SlotIndex < allocatedCount
                && slots[handle.SlotIndex].InUse
                && slots[handle.SlotIndex].Generation == handle.Generation;
        }

        void BeginFadeOut(ref VoiceSlot slot, float now, float duration)
        {
            slot.FadingOut = true;
            slot.FadeStartTime = now;
            slot.FadeDuration = Mathf.Max(0.01f, duration);
            slot.FadeStartVolume = slot.Source != null ? slot.Source.volume : 0f;
        }

        int AcquireSlot(int incomingPriority)
        {
            // 1. Free already-allocated slot
            for (int i = 0; i < allocatedCount; i++)
            {
                if (!slots[i].InUse)
                    return i;
            }

            // 2. Grow pool up to maxPoolSize
            if (allocatedCount < maxPoolSize)
            {
                int idx = allocatedCount;
                AllocateSlot(idx);
                return idx;
            }

            // 3. Priority + age voice stealing (higher numeric Priority = lower importance)
            int victim = -1;
            int worstPriority = incomingPriority;
            float oldestStart = float.MaxValue;

            for (int i = 0; i < allocatedCount; i++)
            {
                ref VoiceSlot s = ref slots[i];
                if (s.Cue != null && s.Cue.Loop)
                    continue;

                if (s.Priority > worstPriority || (s.Priority == worstPriority && s.StartTime < oldestStart))
                {
                    worstPriority = s.Priority;
                    oldestStart = s.StartTime;
                    victim = i;
                }
            }

            if (victim >= 0)
                ReleaseSlot(victim);
            return victim;
        }

        int FindOldestSlotForCue(SoundCue cue)
        {
            int oldestIdx = -1;
            float oldestTime = float.MaxValue;
            for (int i = 0; i < allocatedCount; i++)
            {
                if (slots[i].InUse && ReferenceEquals(slots[i].Cue, cue) && slots[i].StartTime < oldestTime)
                {
                    oldestTime = slots[i].StartTime;
                    oldestIdx = i;
                }
            }
            return oldestIdx;
        }

        void ReleaseSlot(int index)
        {
            ref VoiceSlot slot = ref slots[index];
            if (!slot.InUse)
                return;

            if (slot.Source != null)
            {
                slot.Source.Stop();
                slot.Source.clip = null;
            }

            if (slot.Cue != null && cueStates.TryGetValue(slot.Cue, out CueRuntimeState st))
                st.ActiveCount = Mathf.Max(0, st.ActiveCount - 1);

            slot.Cue = null;
            slot.FollowTarget = null;
            slot.InUse = false;
            slot.FadingOut = false;
            ActiveVoiceCount = Mathf.Max(0, ActiveVoiceCount - 1);
        }

        CueRuntimeState GetOrCreateState(SoundCue cue)
        {
            if (!cueStates.TryGetValue(cue, out CueRuntimeState state))
            {
                state = new CueRuntimeState();
                cueStates[cue] = state;
            }
            return state;
        }

        static AudioClip PickClip(SoundCue cue, CueRuntimeState state)
        {
            AudioClip[] clips = cue.Clips;
            int count = clips != null ? clips.Length : 0;
            if (count == 0)
                return null;
            if (count == 1)
                return clips[0];

            int chosenIndex;
            switch (cue.SelectionMode)
            {
                case ClipSelectionMode.Sequential:
                    chosenIndex = state.SequentialCursor % count;
                    state.SequentialCursor = (chosenIndex + 1) % count;
                    break;

                case ClipSelectionMode.Random:
                    chosenIndex = Random.Range(0, count);
                    break;

                case ClipSelectionMode.RandomNoRepeat:
                    chosenIndex = Random.Range(0, count);
                    if (chosenIndex == state.LastClipIndex)
                        chosenIndex = (chosenIndex + Random.Range(1, count)) % count;
                    break;

                default: // ShuffleBag
                    if (state.ShuffleOrder == null || state.ShuffleOrder.Length != count)
                    {
                        state.ShuffleOrder = new int[count];
                        for (int i = 0; i < count; i++)
                            state.ShuffleOrder[i] = i;
                        state.ShuffleCursor = count;
                    }
                    if (state.ShuffleCursor >= count)
                    {
                        // Fisher-Yates shuffle
                        for (int i = count - 1; i > 0; i--)
                        {
                            int j = Random.Range(0, i + 1);
                            int tmp = state.ShuffleOrder[i];
                            state.ShuffleOrder[i] = state.ShuffleOrder[j];
                            state.ShuffleOrder[j] = tmp;
                        }
                        // Prevent immediate boundary repeat after reshuffle
                        if (state.ShuffleOrder[0] == state.LastClipIndex && count > 1)
                        {
                            int swap = Random.Range(1, count);
                            int tmp = state.ShuffleOrder[0];
                            state.ShuffleOrder[0] = state.ShuffleOrder[swap];
                            state.ShuffleOrder[swap] = tmp;
                        }
                        state.ShuffleCursor = 0;
                    }
                    chosenIndex = state.ShuffleOrder[state.ShuffleCursor++];
                    break;
            }

            state.LastClipIndex = chosenIndex;
            if (clips[chosenIndex] != null)
                return clips[chosenIndex];

            // Fallback scan if an array slot was left empty in Inspector
            for (int i = 0; i < count; i++)
            {
                if (clips[i] != null)
                    return clips[i];
            }
            return null;
        }
    }
}
