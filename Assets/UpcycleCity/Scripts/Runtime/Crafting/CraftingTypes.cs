using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>Broad grouping for materials displayed in the shared team Material Stock UI.</summary>
    public enum MaterialCategory
    {
        /// <summary>Collected street waste or raw recyclable feedstock (e.g. Plastic Scrap, Old Wheel).</summary>
        Trash = 0,
        /// <summary>Store-bought or consumable processing agents (e.g. Catalyst).</summary>
        Consumable = 1,
        /// <summary>Processed intermediate materials produced by Refining cards.</summary>
        Refined = 2,
        /// <summary>Completed upcycled objects produced by Building cards.</summary>
        Built = 3,
    }

    /// <summary>Display and measurement unit for a <see cref="MaterialDefinition"/>.</summary>
    public enum MaterialUnit
    {
        Kilograms = 0,
        Units = 1,
    }

    /// <summary>Functional category of a <see cref="CraftingCard"/>.</summary>
    public enum CardCategory
    {
        Refining = 0,
        Building = 1,
        Upgrading = 2,
        Energy = 3,
    }

    /// <summary>Structured failure category returned by <see cref="CraftingRules"/>.</summary>
    public enum CraftingFailureKind
    {
        None = 0,
        InvalidArguments = 1,
        IncompatibleMachine = 2,
        MachineBusy = 3,
        LevelTooLow = 4,
        OutOfRange = 5,
        InsufficientResources = 6,
    }

    /// <summary>
    /// Serializable pair of a <see cref="MaterialDefinition"/> and a positive quantity (kg or units).
    /// </summary>
    [Serializable]
    public struct MaterialAmount
    {
        [Tooltip("Material asset referenced by this ingredient or output.")]
        public MaterialDefinition material;

        [Tooltip("Quantity in the material's unit (kg or units).")]
        [Min(0.01f)]
        public float amount;

        public MaterialAmount(MaterialDefinition material, float amount)
        {
            this.material = material;
            this.amount = Mathf.Max(0f, amount);
        }

        public bool IsValid => material != null && amount > 0f;

        public string Format()
        {
            if (material == null)
                return $"{amount:0.##}";
            return material.FormatAmount(amount);
        }
    }

    /// <summary>
    /// Cost to upgrade a machine from its previous level to <see cref="targetLevel"/>.
    /// </summary>
    [Serializable]
    public struct MachineUpgradeCost
    {
        [Tooltip("Level reached after applying this upgrade (e.g. 1 for Level 0 -> Level 1).")]
        [Min(1)]
        public int targetLevel;

        [Tooltip("Shared team coins required.")]
        [Min(0)]
        public int coins;

        [Tooltip("Shared team materials required.")]
        public MaterialAmount[] materials;
    }

    /// <summary>
    /// Details for a single missing input material or energy shortfall reported by <see cref="CraftingRules"/>.
    /// </summary>
    public readonly struct MissingRequirement
    {
        public readonly bool IsEnergy;
        public readonly string MaterialId;
        public readonly string DisplayName;
        public readonly MaterialUnit Unit;
        public readonly float Required;
        public readonly float Available;

        public float Deficit => Mathf.Max(0f, Required - Available);

        public MissingRequirement(bool isEnergy, string materialId, string displayName, MaterialUnit unit, float required, float available)
        {
            IsEnergy = isEnergy;
            MaterialId = materialId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Unit = unit;
            Required = required;
            Available = available;
        }

        public string Format()
        {
            if (IsEnergy)
                return $"Energy (need {Mathf.RoundToInt(Required)} NRG, have {Mathf.RoundToInt(Available)} NRG)";
            if (Unit == MaterialUnit.Units)
                return $"{DisplayName} (need {Required:0.#}, have {Available:0.#})";
            return $"{DisplayName} (need {Required:0.#} kg, have {Available:0.#} kg)";
        }
    }

    /// <summary>
    /// Result of validating whether a <see cref="CraftingCard"/> can be executed at a machine.
    /// </summary>
    public sealed class CraftingValidationResult
    {
        static readonly MissingRequirement[] EmptyMissing = Array.Empty<MissingRequirement>();

        public bool IsValid { get; }
        public CraftingFailureKind FailureKind { get; }
        public string Message { get; }
        public IReadOnlyList<MissingRequirement> Missing { get; }

        CraftingValidationResult(bool isValid, CraftingFailureKind failureKind, string message, IReadOnlyList<MissingRequirement> missing)
        {
            IsValid = isValid;
            FailureKind = failureKind;
            Message = message ?? string.Empty;
            Missing = missing ?? EmptyMissing;
        }

        public static CraftingValidationResult Success() =>
            new CraftingValidationResult(true, CraftingFailureKind.None, string.Empty, EmptyMissing);

        public static CraftingValidationResult Fail(
            CraftingFailureKind kind,
            string message,
            IReadOnlyList<MissingRequirement> missing = null) =>
            new CraftingValidationResult(false, kind, message, missing);
    }

    /// <summary>
    /// Result of attempting to start/execute a <see cref="CraftingCard"/> through <see cref="CraftingRules"/>.
    /// </summary>
    public sealed class CraftingExecutionOutcome
    {
        public bool Succeeded => Validation.IsValid;
        public CraftingValidationResult Validation { get; }
        public string SummaryMessage { get; }

        public CraftingExecutionOutcome(CraftingValidationResult validation, string summaryMessage)
        {
            Validation = validation ?? CraftingValidationResult.Fail(CraftingFailureKind.InvalidArguments, "Invalid validation result.");
            SummaryMessage = summaryMessage ?? Validation.Message;
        }
    }

    /// <summary>
    /// Formatting and naming helpers shared across runtime crafting UI, machines, and validation messages.
    /// </summary>
    public static class CraftingFormat
    {
        /// <summary>Human-readable label for a data-driven <see cref="MachineTypeDefinition"/>.</summary>
        public static string MachineTypeLabel(MachineTypeDefinition typeDef) =>
            typeDef != null ? typeDef.DisplayName : "Workshop Machine";

        /// <summary>Uppercase badge label for a <see cref="CardCategory"/>.</summary>
        public static string CardCategoryTag(CardCategory category) => category switch
        {
            CardCategory.Refining => "REFINING",
            CardCategory.Building => "BUILDING",
            CardCategory.Upgrading => "UPGRADING",
            CardCategory.Energy => "ENERGY",
            _ => category.ToString().ToUpperInvariant(),
        };

        /// <summary>Formats a quantity using the given <see cref="MaterialUnit"/>.</summary>
        public static string FormatQuantity(float amount, MaterialUnit unit)
        {
            if (unit == MaterialUnit.Units)
            {
                float rounded = Mathf.Round(amount);
                if (Mathf.Abs(amount - rounded) < 0.01f)
                    return Mathf.RoundToInt(rounded) == 1 ? "1 unit" : $"{Mathf.RoundToInt(rounded)} units";
                return $"{amount:0.#} units";
            }

            return $"{amount:0.0} kg";
        }
    }
}
