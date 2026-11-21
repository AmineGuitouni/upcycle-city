using System;
using UnityEngine;

namespace UpcycleCity.Player
{
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
    }

    public readonly struct FeedbackSignal
    {
        public readonly PlayerFeedbackEvent Event;
        public readonly Vector3 WorldPosition;
        public readonly float Intensity;
        public readonly string Label;
        public readonly Color AccentColor;

        public FeedbackSignal(
            PlayerFeedbackEvent feedbackEvent,
            Vector3 worldPosition,
            float intensity = 1f,
            string label = null,
            Color accentColor = default)
        {
            Event = feedbackEvent;
            WorldPosition = worldPosition;
            Intensity = Mathf.Clamp01(intensity);
            Label = label ?? string.Empty;
            AccentColor = accentColor;
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
            Color accentColor = default)
        {
            var signal = new FeedbackSignal(feedbackEvent, worldPosition, intensity, label, accentColor);
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
#if UNITY_ANDROID || UNITY_IOS
                    Handheld.Vibrate();
#endif
                    break;
            }
        }
    }
}
