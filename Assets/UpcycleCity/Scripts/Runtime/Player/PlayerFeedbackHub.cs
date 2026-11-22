using System;
using UnityEngine;
using UpcycleCity.Waste;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Typed gameplay feedback events emitted through <see cref="PlayerFeedbackHub"/> and replicated
    /// over Netcode as a single <see cref="byte"/>.
    /// <para>
    /// Audio, haptics, and VFX listeners route strictly by this enum (never by localized display text).
    /// <b>Wire-compatibility rule:</b> always append new values at the end with explicit numeric IDs
    /// and never renumber existing entries.
    /// </para>
    /// </summary>
    public enum PlayerFeedbackEvent
    {
        Jump = 0,
        Land = 1,
        Footstep = 2,
        Grab = 3,
        Drop = 4,
        Throw = 5,
        Interact = 6,
        PickupItem = 7,
        PickupCoins = 8,
        ToolUse = 9,
        SortCorrect = 10,
        SortWrong = 11,
        InventoryFull = 12,
        FocusChanged = 13,
        CartStow = 14,
        SellTrash = 15,
        BinServiced = 16,
        FactoryRepaired = 17,
        ShopPurchase = 18,
        /// <summary>A shop purchase was refused (not enough coins, already owned, out of range, unknown offer). Buyer-local error cue.</summary>
        ShopPurchaseFailed = 19,
        /// <summary>A crafting machine or manual generator was upgraded to a higher level. Acting-player spend + mechanical cue.</summary>
        MachineUpgraded = 20,
        /// <summary>A crafting card started processing at a machine. Acting-player machine cue.</summary>
        CraftStarted = 21,
        /// <summary>A crafting card execution, crank, or machine upgrade was refused. Acting-player error cue.</summary>
        CraftFailed = 22,
        /// <summary>A crafting card finished processing and produced its outputs. Acting-player completion chime.</summary>
        CraftCompleted = 23,
    }

    public readonly struct FeedbackSignal
    {
        public const byte NoMaterialStream = byte.MaxValue;

        public readonly PlayerFeedbackEvent Event;
        public readonly Vector3 WorldPosition;
        public readonly float Intensity;
        public readonly string Label;
        public readonly Color AccentColor;

        /// <summary>
        /// Compact numeric <see cref="WasteStream"/> code (0..4, with bulky items encoded as
        /// <see cref="WasteStream.BulkyUpcyclable"/>), or <see cref="NoMaterialStream"/> when unused.
        /// </summary>
        public readonly byte MaterialStream;

        public bool HasMaterialStream => MaterialStream != NoMaterialStream;

        public static byte EncodeMaterial(WasteStream stream, bool isBulky = false) =>
            isBulky ? (byte)WasteStream.BulkyUpcyclable : (byte)stream;

        public FeedbackSignal(
            PlayerFeedbackEvent feedbackEvent,
            Vector3 worldPosition,
            float intensity = 1f,
            string label = null,
            Color accentColor = default,
            byte materialStream = NoMaterialStream)
        {
            Event = feedbackEvent;
            WorldPosition = worldPosition;
            Intensity = Mathf.Clamp01(intensity);
            Label = label ?? string.Empty;
            AccentColor = accentColor;
            MaterialStream = materialStream;
        }
    }

    /// <summary>
    /// Implemented by pluggable audio, VFX, camera-shake, or mobile haptics modules
    /// that subscribe to <see cref="PlayerFeedbackHub"/> without modifying gameplay code.
    /// </summary>
    public interface IPlayerFeedbackListener
    {
        void OnPlayerFeedback(in FeedbackSignal signal);
    }

    /// <summary>
    /// Central event-driven feedback hub for player actions (SFX, particles, mobile haptics, HUD hooks).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Player/Player Feedback Hub")]
    public sealed class PlayerFeedbackHub : MonoBehaviour
    {
        [Tooltip("Enable lightweight mobile vibration on key tactile events (Android/iOS builds only).")]
        [SerializeField] bool enableMobileHaptics = true;

        IPlayerFeedbackListener[] localListeners;

        public event Action<FeedbackSignal> SignalEmitted;

        void Awake()
        {
            RefreshLocalListeners();
        }

        public void RefreshLocalListeners()
        {
            localListeners = GetComponentsInChildren<IPlayerFeedbackListener>(true);
        }

        public void Emit(
            PlayerFeedbackEvent feedbackEvent,
            Vector3 worldPosition,
            float intensity = 1f,
            string label = null,
            Color accentColor = default,
            byte materialStream = FeedbackSignal.NoMaterialStream)
        {
            var signal = new FeedbackSignal(feedbackEvent, worldPosition, intensity, label, accentColor, materialStream);
            SignalEmitted?.Invoke(signal);

            if (localListeners != null)
            {
                for (int i = 0; i < localListeners.Length; i++)
                    localListeners[i]?.OnPlayerFeedback(in signal);
            }

            TriggerOptionalHaptics(in signal);
        }

        void TriggerOptionalHaptics(in FeedbackSignal signal)
        {
            if (!enableMobileHaptics || !Application.isMobilePlatform)
                return;

            switch (signal.Event)
            {
                case PlayerFeedbackEvent.Grab:
                case PlayerFeedbackEvent.Throw:
                case PlayerFeedbackEvent.SortCorrect:
                case PlayerFeedbackEvent.SortWrong:
                case PlayerFeedbackEvent.PickupItem:
                case PlayerFeedbackEvent.ShopPurchase:
                case PlayerFeedbackEvent.MachineUpgraded:
                case PlayerFeedbackEvent.CraftStarted:
                case PlayerFeedbackEvent.CraftCompleted:
#if UNITY_ANDROID || UNITY_IOS
                    Handheld.Vibrate();
#endif
                    break;
            }
        }
    }
}
