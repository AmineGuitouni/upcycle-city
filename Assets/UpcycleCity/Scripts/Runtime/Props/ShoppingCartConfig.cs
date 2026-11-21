using UnityEngine;

namespace UpcycleCity.Props
{
    /// <summary>
    /// Tuning profile for the pushable, rolling <see cref="ShoppingCart"/>. All fields are read live,
    /// so Inspector edits apply immediately in Play Mode (jam retuning needs no recompile).
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Props/Shopping Cart Config", fileName = "ShoppingCartConfig")]
    public sealed class ShoppingCartConfig : ScriptableObject
    {
        [Header("Look")]
        [Tooltip("Cart model (Art/UpcycleProps/Tools/shopping_cart.fbx): wire basket, handle, chassis and Wheel_* castors. " +
                 "Left empty, a plain stand-in is built.")]
        public GameObject visualPrefab;

        [Header("Mass & Stability")]
        [Tooltip("Cart body mass in kg.")]
        [Range(2f, 40f)] public float mass = 12f;
        [Tooltip("Local centre-of-mass height (m). Low keeps it from tipping.")]
        [Range(-0.5f, 0.5f)] public float centerOfMassHeight = -0.15f;

        [Header("Push / Follow")]
        [Tooltip("How far in front of the player the handle is held while pushing (m).")]
        [Range(0.5f, 2f)] public float pushDistance = 1.05f;
        [Tooltip("Height of the handle above the floor while pushing (m).")]
        [Range(0.4f, 1.3f)] public float handleHeight = 0.95f;
        [Tooltip("Max speed the cart will chase its held target (m/s).")]
        [Range(2f, 16f)] public float maxPushSpeed = 9f;
        [Tooltip("Stiffness of the follow spring while pushing. Higher = tighter to the player.")]
        [Range(4f, 40f)] public float followStiffness = 18f;
        [Tooltip("How quickly the cart yaws to face its push direction.")]
        [Range(2f, 30f)] public float turnSharpness = 12f;
        [Tooltip("Move-speed multiplier applied to the player while pushing the cart.")]
        [Range(0.4f, 1f)] public float pushMoveSpeedMultiplier = 0.82f;

        [Header("Rolling (free / released)")]
        [Tooltip("Fraction of sideways velocity removed each FixedUpdate so the cart tracks straight (wheels grip).")]
        [Range(0f, 1f)] public float lateralGrip = 0.85f;
        [Tooltip("Rolling resistance: linear damping applied while free-rolling.")]
        [Range(0f, 4f)] public float rollDamping = 0.6f;

        [Header("Wheels")]
        [Tooltip("Visual wheel radius (m) used to convert travel into spin.")]
        [Range(0.03f, 0.4f)] public float wheelRadius = 0.065f;

        [Header("Basket")]
        [Tooltip("Most pieces of trash the basket takes (fewer when big pieces fill it first). Full = new trash bounces out.")]
        [Range(1, 30)] public int basketCapacity = 12;
        [Tooltip("How far the pile may rise above the rim (m) before the basket counts as full.")]
        [Range(0f, 0.3f)] public float heapAboveRim = 0.1f;
        [Tooltip("Pieces a little too long for the basket are squeezed to fit, down to this scale; bigger ones don't fit.")]
        [Range(0.3f, 1f)] public float minSquash = 0.6f;

        [Header("Release / Throw")]
        [Tooltip("Hold the grab button longer than this (s) to THROW the cart instead of gently letting go.")]
        [Range(0.1f, 1f)] public float throwHoldThreshold = 0.28f;
        [Tooltip("Forward launch speed when the cart is thrown (m/s), scaled by how long you held.")]
        [Range(2f, 16f)] public float throwSpeed = 8f;
        [Tooltip("Hold duration (s) that reaches full throw speed.")]
        [Range(0.2f, 1.5f)] public float throwChargeTime = 0.7f;
    }
}
