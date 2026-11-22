using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// A camera pose for a cinematic shot: this transform's position and rotation plus a field of view.
    /// Move it in the scene (or "Align With View" in the Scene view) to reframe a shot.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Camera Anchor")]
    public sealed class CameraAnchor : MonoBehaviour
    {
        [Range(10f, 90f)] public float fieldOfView = 40f;

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.75f, 0.2f, 0.9f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawFrustum(Vector3.zero, fieldOfView, 1.2f, 0.05f, 16f / 9f);
        }
    }
}
