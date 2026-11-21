using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Crowd
{
    /// <summary>An animated character borrowed from the pool.</summary>
    sealed class CrowdRig
    {
        public GameObject Prefab;
        public GameObject Instance;
        public Animator Animator;
        public SkinnedMeshRenderer[] Renderers;
    }

    /// <summary>
    /// Reuses animated rigs, so moving the animation budget from one NPC to another never instantiates
    /// or destroys anything once the pool has warmed up.
    /// </summary>
    sealed class CrowdRigPool
    {
        readonly Dictionary<GameObject, Stack<CrowdRig>> free = new Dictionary<GameObject, Stack<CrowdRig>>();
        readonly Transform storage;
        readonly RuntimeAnimatorController controller;

        public CrowdRigPool(Transform storage, RuntimeAnimatorController controller)
        {
            this.storage = storage;
            this.controller = controller;
        }

        public CrowdRig Get(GameObject prefab, Transform parent)
        {
            CrowdRig rig = free.TryGetValue(prefab, out Stack<CrowdRig> stack) && stack.Count > 0 ? stack.Pop() : Create(prefab);
            rig.Instance.transform.SetParent(parent, false);
            rig.Instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            rig.Instance.SetActive(true);
            return rig;
        }

        public void Return(CrowdRig rig)
        {
            rig.Instance.SetActive(false);
            rig.Instance.transform.SetParent(storage, false);
            if (!free.TryGetValue(rig.Prefab, out Stack<CrowdRig> stack))
                free[rig.Prefab] = stack = new Stack<CrowdRig>();
            stack.Push(rig);
        }

        CrowdRig Create(GameObject prefab)
        {
            GameObject instance = Object.Instantiate(prefab, storage);
            instance.name = prefab.name + " (animated)";
            var rig = new CrowdRig
            {
                Prefab = prefab,
                Instance = instance,
                Animator = instance.GetComponentInChildren<Animator>(),
                Renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true),
            };

            if (rig.Animator != null)
            {
                rig.Animator.runtimeAnimatorController = controller;
                rig.Animator.applyRootMotion = false;
                rig.Animator.cullingMode = AnimatorCullingMode.CullCompletely; // nothing is evaluated off screen
            }

            foreach (SkinnedMeshRenderer skin in rig.Renderers)
            {
                skin.updateWhenOffscreen = false;
                skin.skinnedMotionVectors = false;
                skin.quality = SkinQuality.Bone2;
            }
            return rig;
        }
    }
}
