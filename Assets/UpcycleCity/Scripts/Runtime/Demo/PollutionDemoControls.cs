using UnityEngine;
using UnityEngine.InputSystem;
using UpcycleCity.Pollution;

namespace UpcycleCity.Demo
{
    /// <summary>
    /// Play-mode keys for showing the city off: toggle polluted and clean, send a clean-up wave
    /// across the city, and cycle the cameras.
    /// </summary>
    [AddComponentMenu("Upcycle City/Demo/Pollution Demo Controls")]
    public sealed class PollutionDemoControls : MonoBehaviour
    {
        [SerializeField] PollutionController controller;
        [SerializeField] CleanupWave cleanupWave;
        [SerializeField] Camera[] cameras;

        [Header("Keys")]
        [SerializeField] Key togglePollutionKey = Key.P;
        [SerializeField] Key cleanupWaveKey = Key.C;
        [SerializeField] Key nextCameraKey = Key.V;

        int cameraIndex;

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (controller != null && keyboard[togglePollutionKey].wasPressedThisFrame)
                controller.SetPollution(controller.TargetPollution > 0.5f ? 0f : 1f);

            if (cleanupWave != null && keyboard[cleanupWaveKey].wasPressedThisFrame)
                cleanupWave.Play();

            if (cameras.Length > 1 && keyboard[nextCameraKey].wasPressedThisFrame)
            {
                cameraIndex = (cameraIndex + 1) % cameras.Length;
                for (int i = 0; i < cameras.Length; i++)
                    cameras[i].gameObject.SetActive(i == cameraIndex);
            }
        }
    }
}
