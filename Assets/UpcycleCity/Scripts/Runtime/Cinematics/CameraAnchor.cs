using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// A camera pose for a cinematic shot: this transform's position and rotation, a field of view, a focus
    /// distance for the depth of field and a dutch roll. Move it in the scene (or "Align With View" in the
    /// Scene view) to reframe a shot.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Camera Anchor")]
    public sealed class CameraAnchor : MonoBehaviour
    {
        [Range(10f, 90f)] public float fieldOfView = 40f;
        [Tooltip("Metres to the sharpest point. 0: keep whatever the camera is focused on.")]
        [Min(0f)] public float focusDistance;
        [Tooltip("Tilt of the horizon, degrees.")]
        [Range(-15f, 15f)] public float roll;

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.75f, 0.2f, 0.9f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawFrustum(Vector3.zero, fieldOfView, 1.2f, 0.05f, 16f / 9f);
            if (focusDistance > 0f)
                Gizmos.DrawWireSphere(Vector3.forward * focusDistance, 0.08f);
        }
    }
}
