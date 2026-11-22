using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Character;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Procedural animation for the skinned player body (Art/UpcycleProps/Character/Rigged, built by
    /// Tools/Blender/rig_character_body.py). No Animator or clips: every frame it builds the pose from layers,
    /// all tunable live in the Inspector:
    /// <list type="number">
    /// <item>Locomotion: breathing at rest, or a walk cycle driven by the distance the actor travels (so feet
    /// don't slide), with the stance foot planted by the ground clamp.</item>
    /// <item>Poses blended by weight: kneel on one knee, sit on an edge.</item>
    /// <item>Slump (sadness) and head look-at.</item>
    /// <item>Two-bone IK so either hand can reach a world point.</item>
    /// <item>Ground clamp: the body rises or drops so its lowest foot or knee rests on the ground, so it never
    /// sinks into sand or furniture.</item>
    /// </list>
    /// Angles are in the character's rest frame (it faces +Z, +X on its right), whatever the bones' own axes are.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Character Rig Poser")]
    [DefaultExecutionOrder(100)]
    public sealed class CharacterRigPoser : MonoBehaviour
    {
        public enum Side { Left, Right }

        [Serializable]
        public struct WalkStyle
        {
            [Tooltip("Metres per step.")] public float stride;
            [Tooltip("Thigh swing either way, degrees.")] public float hipSwing;
            [Tooltip("Knee bend at mid-swing, degrees.")] public float kneeSwing;
            [Tooltip("Arm swing either way, degrees.")] public float armSwing;
            [Tooltip("Forward lean of the spine, degrees.")] public float lean;
            [Tooltip("Head bowed, degrees.")] public float headDown;

            public static WalkStyle Tired => new WalkStyle
                { stride = 0.5f, hipSwing = 19f, kneeSwing = 48f, armSwing = 9f, lean = 9f, headDown = 12f };
            public static WalkStyle Steady => new WalkStyle
                { stride = 0.62f, hipSwing = 24f, kneeSwing = 55f, armSwing = 16f, lean = 4f, headDown = 4f };
        }

        [SerializeField] CharacterAvatar avatar;

        [Header("Walk")]
        public WalkStyle walk = WalkStyle.Tired;
        [Tooltip("How fast the walk blends in and out as the actor starts and stops moving.")]
        [SerializeField, Min(0.1f)] float walkBlendSpeed = 4f;

        [Header("Layers (driven by CinematicActor, editable live)")]
        [Range(0f, 1f)] public float kneel;
        [Range(0f, 1f)] public float sit;
        [Tooltip("Extra forward slump of the spine and head, degrees.")]
        [Range(-30f, 40f)] public float slump;
        [Range(0f, 1f)] public float breathe = 1f;

        [Header("Look at")]
        public Transform lookTarget;
        public Vector3 lookPoint;
        [Range(0f, 1f)] public float lookWeight;

        [Header("Hands")]
        public Vector3 reachLeft, reachRight;
        [Range(0f, 1f)] public float reachLeftWeight, reachRightWeight;
        [Tooltip("Elbow bend while carrying something, degrees.")]
        [Range(0f, 90f)] public float carryRight;

        [Header("Ground")]
        [Tooltip("Keep the lowest foot or knee on the ground plane (the actor's height plus this offset).")]
        public bool groundClamp = true;
        public float groundOffset;

        /// <summary>A foot hit the ground during a walk (side, world position of the heel).</summary>
        public event Action<Side, Vector3> FootPlanted;

        // Bones
        static readonly string[] BoneNames =
        {
            "Hips", "Spine", "Chest", "Neck", "Head",
            "UpperLeg_L", "LowerLeg_L", "Foot_L", "Toes_L", "UpperLeg_R", "LowerLeg_R", "Foot_R", "Toes_R",
            "Shoulder_L", "UpperArm_L", "LowerArm_L", "Hand_L", "Shoulder_R", "UpperArm_R", "LowerArm_R", "Hand_R",
        };
        readonly Dictionary<string, BoneRest> bones = new Dictionary<string, BoneRest>(24);
        readonly Dictionary<string, Quaternion> pose = new Dictionary<string, Quaternion>(24);
        Transform space;     // the avatar transform: the character's frame, lifted by the ground clamp
        Transform boundBody;

        // Ground contact points, in bone space at rest
        readonly List<(Transform bone, Vector3 local)> contacts = new List<(Transform, Vector3)>(12);

        float phase;
        float walkWeight;
        Vector3 lastPosition;
        bool hasLastPosition;
        Quaternion headLook = Quaternion.identity;

        struct BoneRest
        {
            public Transform t;
            public Quaternion restLocal;
            public Quaternion restRel;   // rest rotation relative to the character frame
            public Quaternion restRelInv;
        }

        public bool IsBound => boundBody != null;
        public float WalkWeight => walkWeight;
        public Transform Bone(string name) => bones.TryGetValue(name, out BoneRest b) ? b.t : null;
        public Transform Hand(Side side) => Bone(side == Side.Left ? "Hand_L" : "Hand_R");

        /// <summary>The middle of the palm (a little past the wrist), for props and reach targets.</summary>
        public Vector3 Palm(Side side)
        {
            Transform hand = Hand(side);
            Transform fore = Bone(side == Side.Left ? "LowerArm_L" : "LowerArm_R");
            if (hand == null || fore == null)
                return transform.position + Vector3.up;
            return hand.position + (hand.position - fore.position).normalized * 0.09f;
        }

        void Reset() => avatar = GetComponentInChildren<CharacterAvatar>();

        void OnEnable()
        {
            if (avatar != null)
                avatar.BodyRebuilt += Unbind;
            hasLastPosition = false;
        }

        void OnDisable()
        {
            if (avatar != null)
                avatar.BodyRebuilt -= Unbind;
        }

        void Unbind() => boundBody = null;

        bool Bind()
        {
            if (avatar == null || avatar.BodyInstance == null)
                return false;
            if (boundBody == avatar.BodyInstance)
                return true;

            space = avatar.transform;
            space.localPosition = Vector3.zero;
            bones.Clear();
            var all = new Dictionary<string, Transform>();
            foreach (Transform t in avatar.BodyInstance.GetComponentsInChildren<Transform>(true))
                all[t.name] = t;
            Quaternion spaceInv = Quaternion.Inverse(space.rotation);
            foreach (string name in BoneNames)
            {
                if (!all.TryGetValue(name, out Transform t))
                    return false; // a static body: nothing to pose
                Quaternion rel = spaceInv * t.rotation;
                bones[name] = new BoneRest { t = t, restLocal = t.localRotation, restRel = rel, restRelInv = Quaternion.Inverse(rel) };
            }

            // Sole, toe and knee points (character rest frame -> bone local), for the ground clamp
            contacts.Clear();
            foreach (string s in new[] { "L", "R" })
            {
                Transform foot = bones["Foot_" + s].t, toes = bones["Toes_" + s].t, shin = bones["LowerLeg_" + s].t;
                Vector3 ankle = foot.position;
                Vector3 up = space.up, fwd = space.forward;
                float soleY = ankle.y - 0.095f;
                AddContact(foot, new Vector3(ankle.x, soleY, ankle.z) - fwd * 0.07f);  // heel
                AddContact(toes, new Vector3(ankle.x, soleY, ankle.z) + fwd * 0.17f);  // toe tip
                AddContact(shin, shin.position + fwd * 0.065f);                          // knee cap
                AddContact(shin, shin.position - up * 0.1f + fwd * 0.06f);               // shin just below the knee
            }
            boundBody = avatar.BodyInstance;
            return true;
        }

        void AddContact(Transform bone, Vector3 worldAtRest) => contacts.Add((bone, bone.InverseTransformPoint(worldAtRest)));

        // ---------------------------------------------------------------- per frame

        void LateUpdate()
        {
            if (!Bind())
                return;
            float dt = Time.deltaTime;

            // Locomotion driven by how far the actor moved this frame
            Vector3 pos = transform.position;
            float moved = hasLastPosition ? new Vector2(pos.x - lastPosition.x, pos.z - lastPosition.z).magnitude : 0f;
            lastPosition = pos;
            hasLastPosition = true;
            float speed = dt > 0f ? moved / dt : 0f;
            bool walking = speed > 0.08f && kneel < 0.5f && sit < 0.5f;
            walkWeight = Mathf.MoveTowards(walkWeight, walking ? 1f : 0f, walkBlendSpeed * dt);
            float previousPhase = phase;
            phase += moved / Mathf.Max(0.1f, walk.stride * 2f) * Mathf.PI * 2f;
            DetectFootPlants(previousPhase, phase);

            BuildPose();
            ApplyPose();

            space.localPosition = Vector3.zero;
            if (groundClamp)
                ClampToGround();

            ReachIK(Side.Left, reachLeft, reachLeftWeight);
            ReachIK(Side.Right, reachRight, reachRightWeight);
            LookAtLayer(dt);
        }

        void DetectFootPlants(float from, float to)
        {
            if (walkWeight < 0.3f || to <= from)
                return;
            // Heel strike when a thigh reaches its most forward point: phase pi/2 for the left, 3pi/2 for the right
            for (int i = 0; i < 2; i++)
            {
                float strike = i == 0 ? Mathf.PI * 0.5f : Mathf.PI * 1.5f;
                float a = Mathf.Floor((from - strike) / (Mathf.PI * 2f));
                float b = Mathf.Floor((to - strike) / (Mathf.PI * 2f));
                if (b > a)
                {
                    Side side = i == 0 ? Side.Left : Side.Right;
                    Transform foot = Bone(side == Side.Left ? "Foot_L" : "Foot_R");
                    Vector3 heel = foot != null ? foot.position : transform.position;
                    heel.y = transform.position.y + groundOffset;
                    FootPlanted?.Invoke(side, heel);
                }
            }
        }

        static Quaternion Pitch(float degrees) => Quaternion.AngleAxis(degrees, Vector3.right);  // + leans/flexes back-to-front for spine, moves limbs backward
        static Quaternion Yaw(float degrees) => Quaternion.AngleAxis(degrees, Vector3.up);
        static Quaternion Roll(float degrees) => Quaternion.AngleAxis(degrees, Vector3.forward);

        void Set(string bone, Quaternion q) => pose[bone] = q;

        void BuildPose()
        {
            pose.Clear();
            float w = walkWeight;
            float t = Time.time;

            // ---- locomotion (walk blended with idle breathing)
            float sL = Mathf.Sin(phase), cL = Mathf.Cos(phase);
            float sR = Mathf.Sin(phase + Mathf.PI), cR = Mathf.Cos(phase + Mathf.PI);
            float breath = Mathf.Sin(t * 1.7f) * breathe;

            Set("Hips", Yaw(sL * 6f * w) * Roll(-cL * 2.5f * w));
            Set("Spine", Pitch(walk.lean * w * 0.5f + slump * 0.35f) * Yaw(-sL * 4f * w));
            Set("Chest", Pitch(walk.lean * w * 0.5f + slump * 0.4f + breath * 1.2f) * Yaw(-sL * 4f * w));
            Set("Neck", Pitch(walk.headDown * 0.4f * w + slump * 0.15f));
            Set("Head", Pitch(walk.headDown * 0.6f * w + slump * 0.2f - walk.lean * w * 0.5f));
            Leg("L", sL, cL, w);
            Leg("R", sR, cR, w);
            Arm("L", sR, w, breath, 0f);
            Arm("R", sL, w, breath, carryRight);

            // ---- poses
            if (kneel > 0f)
                BlendKneel(kneel);
            if (sit > 0f)
                BlendSit(sit);
        }

        void Leg(string s, float sin, float cos, float w)
        {
            float thighFwd = sin * walk.hipSwing * w;
            float swing = Mathf.Max(0f, cos);                     // leg travelling forward
            float knee = (6f + walk.kneeSwing * swing * swing) * w;
            // Keep the sole near level, with a heel strike and a toe push-off
            float footLevel = thighFwd - knee;
            float roll = (-10f * Mathf.Max(0f, sin) + 14f * Mathf.Max(0f, -cos) * Mathf.Max(0f, -sin)) * w;
            Set("UpperLeg_" + s, Pitch(-thighFwd));
            Set("LowerLeg_" + s, Pitch(knee));
            Set("Foot_" + s, Pitch(footLevel + roll));
            Set("Toes_" + s, Pitch(-Mathf.Max(0f, roll) * 0.8f));
        }

        void Arm(string s, float oppositeLegSin, float w, float breath, float carry)
        {
            float side = s == "R" ? 1f : -1f;
            float swing = oppositeLegSin * walk.armSwing * w;
            Set("Shoulder_" + s, Roll(side * breath * 0.6f));
            Set("UpperArm_" + s, Pitch(-swing) * Roll(side * 4f));
            Set("LowerArm_" + s, Pitch(-(10f + Mathf.Max(0f, swing) * 0.6f + carry)));
            Set("Hand_" + s, Quaternion.identity);
        }

        void BlendKneel(float k)
        {
            // Right knee down on the sand, left foot planted forward, leaning over what's in front of him
            Mix("Hips", Pitch(8f), k);
            Mix("Spine", Pitch(14f + slump * 0.35f), k);
            Mix("Chest", Pitch(10f + slump * 0.4f), k);
            Mix("Neck", Pitch(3f + slump * 0.08f), k);
            Mix("Head", Pitch(4f + slump * 0.1f), k);
            Mix("UpperLeg_L", Pitch(-78f) * Roll(-6f), k);
            Mix("LowerLeg_L", Pitch(84f), k);
            Mix("Foot_L", Pitch(-6f), k);
            Mix("Toes_L", Quaternion.identity, k);
            Mix("UpperLeg_R", Pitch(-8f) * Roll(4f), k);
            Mix("LowerLeg_R", Pitch(98f), k);
            Mix("Foot_R", Pitch(-30f), k);   // toes tucked under
            Mix("Toes_R", Pitch(-40f), k);
            Mix("UpperArm_L", Pitch(-14f) * Roll(-6f), k);
            Mix("LowerArm_L", Pitch(-38f), k);
            Mix("UpperArm_R", Pitch(-10f) * Roll(6f), k);
            Mix("LowerArm_R", Pitch(-30f), k);
        }

        void BlendSit(float k)
        {
            // Sitting on an edge: thighs level, shins down, hands resting on the knees
            Mix("Hips", Pitch(-4f), k);
            Mix("Spine", Pitch(10f + slump * 0.35f), k);
            Mix("Chest", Pitch(8f + slump * 0.4f), k);
            Mix("Neck", Pitch(6f + slump * 0.15f), k);
            Mix("Head", Pitch(6f + slump * 0.2f), k);
            foreach (string s in new[] { "L", "R" })
            {
                float side = s == "R" ? 1f : -1f;
                Mix("UpperLeg_" + s, Pitch(-86f) * Yaw(side * 6f), k);
                Mix("LowerLeg_" + s, Pitch(88f), k);
                Mix("Foot_" + s, Pitch(-2f), k);
                Mix("Toes_" + s, Quaternion.identity, k);
                Mix("UpperArm_" + s, Pitch(-30f) * Roll(side * 4f), k);
                Mix("LowerArm_" + s, Pitch(-52f), k);
            }
        }

        void Mix(string bone, Quaternion target, float k)
        {
            pose[bone] = pose.TryGetValue(bone, out Quaternion q) ? Quaternion.Slerp(q, target, k) : target;
        }

        void ApplyPose()
        {
            foreach (string name in BoneNames)
            {
                BoneRest b = bones[name];
                Quaternion q = pose.TryGetValue(name, out Quaternion p) ? p : Quaternion.identity;
                // Rotate about character-frame axes through the joint, in the parent's (posed) frame
                b.t.localRotation = b.restLocal * (b.restRelInv * q * b.restRel);
            }
        }

        void ClampToGround()
        {
            float lowest = float.MaxValue;
            foreach ((Transform bone, Vector3 local) in contacts)
                lowest = Mathf.Min(lowest, bone.TransformPoint(local).y);
            float ground = transform.position.y + groundOffset;
            space.position += Vector3.up * (ground - lowest);
        }

        // ---------------------------------------------------------------- IK and look-at

        void ReachIK(Side side, Vector3 target, float weight)
        {
            if (weight <= 0.001f)
                return;
            string s = side == Side.Left ? "L" : "R";
            Transform upper = bones["UpperArm_" + s].t, lower = bones["LowerArm_" + s].t, hand = bones["Hand_" + s].t;

            Vector3 a = upper.position, b = lower.position, c = hand.position;
            // Aim the palm, not the wrist, at the target
            Vector3 palmOffset = Palm(side) - c;
            Vector3 goal = Vector3.Lerp(c, target - palmOffset, Mathf.Clamp01(weight));

            float upperLen = (b - a).magnitude, lowerLen = (c - b).magnitude;
            Vector3 toGoal = goal - a;
            float dist = Mathf.Clamp(toGoal.magnitude, 0.02f, upperLen + lowerLen - 0.002f);
            Vector3 dir = toGoal.normalized;

            // Elbow out to the side and back, like a natural reach
            float sideSign = side == Side.Right ? 1f : -1f;
            Vector3 pole = space.right * sideSign * 0.6f - space.forward * 0.4f - space.up * 0.3f;
            Vector3 poleDir = Vector3.ProjectOnPlane(pole, dir).normalized;

            float cosA = Mathf.Clamp((upperLen * upperLen + dist * dist - lowerLen * lowerLen) / (2f * upperLen * dist), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 elbow = a + dir * (cosA * upperLen) + poleDir * (sinA * upperLen);

            upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
            b = lower.position;
            c = hand.position;
            lower.rotation = Quaternion.FromToRotation(c - b, (a + dir * dist) - b) * lower.rotation;
        }

        void LookAtLayer(float dt)
        {
            Transform head = bones["Head"].t, neck = bones["Neck"].t;
            Quaternion target = Quaternion.identity;
            if (lookWeight > 0.001f)
            {
                Vector3 point = lookTarget != null ? lookTarget.position : lookPoint;
                Vector3 dirWorld = point - head.position;
                if (dirWorld.sqrMagnitude > 1e-4f)
                {
                    // Desired direction in the chest's frame, as yaw and pitch from the body's forward
                    Vector3 local = Quaternion.Inverse(space.rotation) * dirWorld.normalized;
                    float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -65f, 65f);
                    // Looking down is mostly the spine's job (kneel, slump): the head only adds a little
                    float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -35f, 22f);
                    target = Quaternion.Slerp(Quaternion.identity, Yaw(yaw) * Pitch(pitch), lookWeight);
                }
            }
            headLook = Quaternion.Slerp(headLook, target, 1f - Mathf.Exp(-5f * dt));
            // Neck takes 40% of the turn, the head the rest, about world axes aligned with the body
            Quaternion neckPart = Quaternion.Slerp(Quaternion.identity, headLook, 0.4f);
            Quaternion headPart = Quaternion.Slerp(Quaternion.identity, headLook, 0.6f);
            Quaternion frame = space.rotation;
            neck.rotation = frame * neckPart * Quaternion.Inverse(frame) * neck.rotation;
            head.rotation = frame * headPart * Quaternion.Inverse(frame) * head.rotation;
        }
    }
}
