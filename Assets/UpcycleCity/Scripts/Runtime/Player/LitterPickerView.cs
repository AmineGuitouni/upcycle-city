using UnityEngine;
using UpcycleCity.Interaction;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Brings the first-person litter picker (Art/UpcycleProps/Tools/litter_picker.fbx) to life: the jaws rest a little
    /// open, spread wide while the crosshair is on litter, snap shut on a grab and stay closed round whatever they
    /// hold (as far apart as the item is wide); the trigger is squeezed as the jaws close.
    /// <para>
    /// It also tells <see cref="PhysicsGrabController"/> where the claw is (<see cref="IHoldPointProvider"/>). The picker is
    /// drawn by the view-model camera with its own field of view, so the claw is projected from that camera's view
    /// back into the world camera's: held trash sits exactly where the jaws appear on screen.
    /// </para>
    /// Added at runtime by <see cref="PlayerController"/> when a claw tool is selected. No per-frame allocations.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Player/Litter Picker View")]
    public sealed class LitterPickerView : MonoBehaviour, IHoldPointProvider
    {
        const float RestAngle = 7f;
        const float ReadyAngle = 24f;
        const float WideAngle = 32f;
        const float TriggerSqueezeDegrees = 24f;
        // From the model: hinge-to-cup length and the gap left between the cups when the jaws are shut
        const float JawLength = 0.086f;
        const float ShutGap = 0.008f;

        PlayerController player;
        Camera viewCamera;
        Transform jawA, jawB, trigger, claw;
        float signA = 1f, signB = -1f;
        float angle = RestAngle;
        float angleVelocity;
        IGrabbable sizedItem;
        float gripAngle;

        /// <summary>Opening of the jaws in degrees (each jaw from shut).</summary>
        public float JawAngle => angle;

        public void Bind(PlayerController owner, Camera viewModelCamera)
        {
            player = owner;
            viewCamera = viewModelCamera;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                string n = BaseName(t.name);
                if (n == "JawL" || n == "JawR")
                {
                    if (jawA == null) jawA = t;
                    else jawB = t;
                }
                else if (n == "Trigger")
                    trigger = t;
                else if (n == "ClawPoint")
                    claw = t;
            }
            // Open each jaw away from the middle, whichever side the FBX import mirrored it to
            if (jawA != null) signA = jawA.localPosition.x >= 0f ? 1f : -1f;
            if (jawB != null) signB = jawB.localPosition.x >= 0f ? 1f : -1f;
            Apply();
        }

        static string BaseName(string n)
        {
            int dot = n.IndexOf('.');
            return dot < 0 ? n : n.Substring(0, dot);
        }

        void LateUpdate()
        {
            if (player == null)
                return;

            PhysicsGrabController grabber = player.Grabber;
            float target = RestAngle;
            float smoothTime = 0.08f;

            if (grabber != null && grabber.CurrentPickup == PickupStyle.Picker && !grabber.HeldWithPicker)
            {
                target = WideAngle;            // reaching in, jaws spread
                smoothTime = 0.035f;
            }
            else if (grabber != null && grabber.HeldWithPicker)
            {
                target = GripAngleFor(grabber.HeldObject);
                smoothTime = 0.025f;           // snap shut
            }
            else if (player.Interactor != null && player.Interactor.HoveredGrabbable != null
                     && !player.Interactor.HoveredGrabbable.IsBulky && grabber != null && !grabber.IsHolding)
            {
                target = ReadyAngle;           // litter in reach: open, ready
            }

            angle = Mathf.SmoothDamp(angle, target, ref angleVelocity, smoothTime, Mathf.Infinity, Time.deltaTime);
            Apply();
        }

        void Apply()
        {
            if (jawA != null) jawA.localRotation = Quaternion.Euler(0f, signA * angle, 0f);
            if (jawB != null) jawB.localRotation = Quaternion.Euler(0f, signB * angle, 0f);
            if (trigger != null)
            {
                float squeeze = 1f - Mathf.Clamp01(angle / WideAngle);
                trigger.localRotation = Quaternion.Euler(squeeze * TriggerSqueezeDegrees, 0f, 0f);
            }
        }

        /// <summary>Jaw angle that leaves the cups as far apart as the held item is wide (measured once per item).</summary>
        float GripAngleFor(IGrabbable item)
        {
            if (item == null || item.Transform == null)
                return 0f;
            if (!ReferenceEquals(item, sizedItem))
            {
                sizedItem = item;
                float width = MeasureWidth(item.Transform);
                float s = Mathf.Clamp01((width - ShutGap) / (2f * JawLength));
                gripAngle = Mathf.Min(WideAngle, Mathf.Asin(s) * Mathf.Rad2Deg);
            }
            return gripAngle;
        }

        static float MeasureWidth(Transform item)
        {
            bool any = false;
            Bounds b = default;
            foreach (Renderer r in item.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!any)
                return 0.05f;
            Vector3 size = b.size;
            return Mathf.Min(size.x, size.y, size.z);   // gripped across its slimmest side
        }

        public bool TryGetHoldPoint(out Vector3 worldPoint)
        {
            worldPoint = default;
            if (claw == null || !isActiveAndEnabled)
                return false;

            worldPoint = claw.position;
            Camera worldCamera = player != null && player.CameraController != null ? player.CameraController.Camera : null;
            if (viewCamera != null && worldCamera != null)
            {
                // Same eye, different field of view: re-project so the item lines up with the drawn claw
                Vector3 viewport = viewCamera.WorldToViewportPoint(worldPoint);
                if (viewport.z > 0.05f)
                    worldPoint = worldCamera.ViewportToWorldPoint(viewport);
            }
            return true;
        }
    }
}
