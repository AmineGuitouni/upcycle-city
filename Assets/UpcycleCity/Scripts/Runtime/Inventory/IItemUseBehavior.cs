using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;

namespace UpcycleCity.Inventory
{
    /// <summary>
    /// Execution context passed to <see cref="IItemUseBehavior"/> when the player activates an equipped item.
    /// </summary>
    public readonly struct ItemUseContext
    {
        public readonly PlayerInteractor Interactor;
        public readonly PlayerInventory Inventory;
        public readonly PlayerModifiers Modifiers;
        public readonly PlayerFeedbackHub Feedback;
        public readonly Transform CameraTransform;
        public readonly IInventoryItem Item;
        public readonly IToolTarget ToolTarget;

        public ItemUseContext(
            PlayerInteractor interactor,
            PlayerInventory inventory,
            PlayerModifiers modifiers,
            PlayerFeedbackHub feedback,
            Transform cameraTransform,
            IInventoryItem item,
            IToolTarget toolTarget)
        {
            Interactor = interactor;
            Inventory = inventory;
            Modifiers = modifiers;
            Feedback = feedback;
            CameraTransform = cameraTransform;
            Item = item;
            ToolTarget = toolTarget;
        }
    }

    /// <summary>
    /// Pluggable Strategy interface for item activation (e.g. sweeping trash, repairing machines,
    /// applying paint, placing blueprints, or consuming a speed snack).
    /// Attach any [Serializable] implementation via [SerializeReference] on ItemDefinition.
    /// </summary>
    public interface IItemUseBehavior
    {
        bool CanExecute(in ItemUseContext context);
        bool Execute(in ItemUseContext context);
    }

    /// <summary>
    /// Sweeps nearby physical grabbable litter in front of the player (e.g. Broom tool).
    /// </summary>
    [Serializable]
    public sealed class SweepAreaItemBehavior : IItemUseBehavior
    {
        [SerializeField, Min(0.5f)] float sweepRadius = 2.6f;
        [SerializeField, Min(0.5f)] float sweepDistance = 2.0f;
        [SerializeField, Min(0.5f)] float pushImpulse = 5.5f;
        [SerializeField, Range(0f, 1f)] float upwardLift = 0.28f;

        public SweepAreaItemBehavior() { }

        public SweepAreaItemBehavior(float radius, float distance, float impulse)
        {
            sweepRadius = Mathf.Max(0.5f, radius);
            sweepDistance = Mathf.Max(0.5f, distance);
            pushImpulse = Mathf.Max(0.5f, impulse);
        }

        public bool CanExecute(in ItemUseContext context) => context.CameraTransform != null;

        public bool Execute(in ItemUseContext context)
        {
            if (context.CameraTransform == null)
                return false;

            Vector3 forward = context.CameraTransform.forward;
            Vector3 flatForward = new Vector3(forward.x, 0f, forward.z);
            if (flatForward.sqrMagnitude < 1e-4f)
                flatForward = Vector3.forward;
            flatForward.Normalize();

            Vector3 center = context.CameraTransform.position + flatForward * (sweepDistance * 0.7f) + Vector3.down * 0.55f;
            Collider[] hits = Physics.OverlapSphere(center, sweepRadius, GameLayers.MaskOf(GameLayers.Grabbable), QueryTriggerInteraction.Ignore);

            int sweptCount = 0;
            Vector3 pushDir = (flatForward + Vector3.up * upwardLift).normalized;

            for (int i = 0; i < hits.Length; i++)
            {
                IGrabbable grabbable = hits[i].GetComponentInParent<IGrabbable>();
                if (grabbable == null || grabbable.IsHeld || grabbable.Body == null)
                    continue;

                float factor = grabbable.IsBulky ? 0.35f : 1f;
                grabbable.Body.AddForce(pushDir * (pushImpulse * factor), ForceMode.VelocityChange);
                sweptCount++;
            }

            string msg = sweptCount > 0
                ? $"Swept {sweptCount} {(sweptCount == 1 ? "piece" : "pieces")} of litter!"
                : $"Swung {context.Item.DisplayName}";
            context.Interactor?.NotifyFeedback(msg, context.Item.AccentColor);
            context.Feedback?.Emit(PlayerFeedbackEvent.ToolUse, center, sweptCount > 0 ? 1f : 0.45f, msg, context.Item.AccentColor);
            return true;
        }
    }

    /// <summary>
    /// Activates only on compatible <see cref="IToolTarget"/> world objects (e.g. Wrench on jammed machines,
    /// Paint Tin on benches), falling back to a clear status prompt when no target is aimed at.
    /// </summary>
    [Serializable]
    public sealed class ToolTargetItemBehavior : IItemUseBehavior
    {
        [SerializeField] string noTargetMessage = "Aim at a compatible target first.";

        public ToolTargetItemBehavior() { }

        public ToolTargetItemBehavior(string messageWhenNoTarget)
        {
            noTargetMessage = messageWhenNoTarget;
        }

        public bool CanExecute(in ItemUseContext context) => true;

        public bool Execute(in ItemUseContext context)
        {
            if (context.ToolTarget != null && context.ToolTarget.CanUseTool(context.Interactor, context.Item))
            {
                bool succeeded = context.ToolTarget.UseTool(context.Interactor, context.Item);
                if (succeeded)
                {
                    context.Feedback?.Emit(
                        PlayerFeedbackEvent.ToolUse,
                        context.ToolTarget.Transform != null ? context.ToolTarget.Transform.position : Vector3.zero,
                        1f,
                        context.Item.DisplayName,
                        context.Item.AccentColor);
                }
                return succeeded;
            }

            string fallback = string.IsNullOrEmpty(noTargetMessage)
                ? $"Used {context.Item.DisplayName}"
                : noTargetMessage;
            context.Interactor?.NotifyFeedback(fallback, context.Item.AccentColor);
            return false; // Don't consume consumables if no valid target was hit
        }
    }
}
