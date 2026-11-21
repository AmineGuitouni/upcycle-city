using UnityEngine;
using UpcycleCity.Character;

namespace UpcycleCity.Player
{
    /// <summary>
    /// The player's own character (from the Character Creator) as a shadow-only body: it casts the player's
    /// shadow but is never drawn, so the first-person camera, which sits inside the head, can never clip it.
    /// Squashes towards the crouch height so the shadow crouches with the player.
    /// Sits on a child of the player root next to a <see cref="CharacterAvatar"/> set to Shadows Only.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterAvatar))]
    [AddComponentMenu("Upcycle City/Player/Player Body Shadow")]
    public sealed class PlayerBodyShadow : MonoBehaviour
    {
        [SerializeField, Range(1f, 30f)] float crouchSharpness = 12f;

        PlayerController player;
        float heightScale = 1f;

        void Awake() => player = GetComponentInParent<PlayerController>();

        void LateUpdate()
        {
            if (player == null || player.Motor == null || player.Config == null)
                return;

            PlayerConfig config = player.Config;
            float target = player.Motor.IsCrouching ? config.crouchHeight / Mathf.Max(0.1f, config.standHeight) : 1f;
            heightScale = Mathf.Lerp(heightScale, target, 1f - Mathf.Exp(-crouchSharpness * Time.deltaTime));
            transform.localScale = new Vector3(1f, heightScale, 1f);
        }
    }
}
