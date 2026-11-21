using UnityEngine;
using UpcycleCity.Player;

namespace UpcycleCity.Shops
{
    /// <summary>
    /// A shopkeeper NPC behind a counter: idles (Humanoid animator), turns a little and looks at the player
    /// (Animator IK) when they come close, and greets them by name of the shop. The shop itself opens from
    /// the <see cref="ShopCounter"/> on the same object.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Shops/Shopkeeper")]
    public sealed class Shopkeeper : MonoBehaviour
    {
        [SerializeField] Animator animator;
        [SerializeField] ShopCatalog catalog;
        [Tooltip("The player is noticed (turn and look) within this distance.")]
        [SerializeField, Min(1f)] float noticeRadius = 7f;
        [Tooltip("The keeper says hello within this distance, at most once per cooldown.")]
        [SerializeField, Min(0.5f)] float greetRadius = 4.5f;
        [SerializeField, Min(1f)] float greetCooldown = 30f;
        [Tooltip("How far the keeper turns from their rest pose towards the player.")]
        [SerializeField, Range(0f, 120f)] float maxTurn = 55f;
        [SerializeField, Min(0.1f)] float turnSharpness = 3f;

        float restYaw;
        float lookWeight;
        float nextGreetTime;
        bool playerWasNear;
        bool focused;
        Vector3 lookTarget;

        public void Configure(Animator keeperAnimator, ShopCatalog shopCatalog)
        {
            animator = keeperAnimator;
            catalog = shopCatalog;
        }

        void Awake()
        {
            restYaw = transform.eulerAngles.y;
            if (animator == null)
                animator = GetComponentInChildren<Animator>();
            if (animator != null)
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
        }

        public void SetFocused(bool value) => focused = value;

        /// <summary>Says hello through the player's toasts, unless greeted recently (or <paramref name="force"/>).</summary>
        public void Greet(bool force = false, bool speak = true)
        {
            if (!force && Time.time < nextGreetTime)
                return;
            nextGreetTime = Time.time + greetCooldown;
            if (speak && catalog != null)
                PlayerContext.ActiveInteractor?.NotifyFeedback($"{catalog.KeeperName}: {catalog.Greeting}", catalog.Accent);
        }

        void Update()
        {
            PlayerController player = PlayerContext.Active;
            Camera cam = Camera.main;
            if (player == null || cam == null)
            {
                lookWeight = Mathf.MoveTowards(lookWeight, 0f, Time.deltaTime);
                return;
            }

            Vector3 toPlayer = player.transform.position - transform.position;
            toPlayer.y = 0f;
            float distance = toPlayer.magnitude;
            bool near = distance < noticeRadius;

            // Turn towards the player, within a comfortable range of the rest pose
            float targetYaw = restYaw;
            if (near && distance > 0.01f)
            {
                float yawToPlayer = Mathf.Atan2(toPlayer.x, toPlayer.z) * Mathf.Rad2Deg;
                targetYaw = restYaw + Mathf.Clamp(Mathf.DeltaAngle(restYaw, yawToPlayer), -maxTurn, maxTurn);
            }
            float yaw = Mathf.LerpAngle(transform.eulerAngles.y, targetYaw, 1f - Mathf.Exp(-turnSharpness * Time.deltaTime));
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            lookTarget = cam.transform.position;
            float targetWeight = near ? (focused ? 1f : 0.8f) : 0f;
            lookWeight = Mathf.MoveTowards(lookWeight, targetWeight, Time.deltaTime * 1.5f);

            bool inGreet = distance < greetRadius;
            if (inGreet && !playerWasNear)
                Greet();
            playerWasNear = inGreet;
        }

        // Called by the Animator on this object when its layer has IK Pass enabled
        void OnAnimatorIK(int layerIndex)
        {
            if (animator == null)
                return;
            animator.SetLookAtWeight(lookWeight, 0.25f, 0.9f, 1f, 0.6f);
            if (lookWeight > 0.001f)
                animator.SetLookAtPosition(lookTarget);
        }
    }
}
