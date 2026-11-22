using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Pure C# crafting rule evaluator and atomic executor (no scenes, MonoBehaviours, or networking required).
    /// Enforces deterministic check order:
    /// <list type="number">
    /// <item>Valid arguments &amp; machine compatibility (<see cref="CraftingFailureKind.IncompatibleMachine"/>)</item>
    /// <item>Machine level &gt;= card complexity (<c>"Requires machine level X (this machine is level Y)"</c>) — checked BEFORE busy</item>
    /// <item>Player in interaction range (<c>"Move closer to the &lt;machine&gt;"</c>)</item>
    /// <item>Machine idle / not busy (<see cref="CraftingFailureKind.MachineBusy"/>)</item>
    /// <item>All inputs &amp; energy present — lists EVERY missing item/energy and consumes NOTHING on failure</item>
    /// </list>
    /// </summary>
    public static class CraftingRules
    {
        /// <summary>
        /// Zero-allocation fast validation used by UI refresh paths to classify card readiness without
        /// allocating result objects, lists, dictionaries, or strings.
        /// </summary>
        public static CraftingFailureKind ValidateQuick(
            CraftingCard card,
            MachineDefinition machine,
            int machineLevel,
            bool machineBusy,
            MaterialStockLedger stock,
            bool isPlayerInRange = true)
        {
            if (card == null || machine == null || stock == null)
                return CraftingFailureKind.InvalidArguments;

            if (!machine.AcceptsCard(card))
                return CraftingFailureKind.IncompatibleMachine;

            // 1. Machine level >= card complexity (checked before range & busy)
            if (machineLevel < card.ComplexityLevel)
                return CraftingFailureKind.LevelTooLow;

            // 2. Player in range
            if (!isPlayerInRange)
                return CraftingFailureKind.OutOfRange;

            // 3. Machine busy
            if (machineBusy)
                return CraftingFailureKind.MachineBusy;

            // 4. All inputs & energy present (zero-alloc duplicate aggregation over small input arrays)
            MaterialAmount[] inputs = card.Inputs;
            for (int i = 0; i < inputs.Length; i++)
            {
                MaterialAmount entry = inputs[i];
                if (!entry.IsValid)
                    continue;

                string id = entry.material.MaterialId;
                bool alreadyChecked = false;
                for (int prev = 0; prev < i; prev++)
                {
                    if (inputs[prev].IsValid && string.Equals(inputs[prev].material.MaterialId, id, StringComparison.Ordinal))
                    {
                        alreadyChecked = true;
                        break;
                    }
                }
                if (alreadyChecked)
                    continue;

                float needed = entry.amount;
                for (int next = i + 1; next < inputs.Length; next++)
                {
                    if (inputs[next].IsValid && string.Equals(inputs[next].material.MaterialId, id, StringComparison.Ordinal))
                        needed += inputs[next].amount;
                }

                float have = stock.GetAmount(id);
                if (have + MaterialStockLedger.QuantityEpsilon < needed)
                    return CraftingFailureKind.InsufficientResources;
            }

            if (card.EnergyCost > 0 && !stock.HasEnergy(card.EnergyCost))
                return CraftingFailureKind.InsufficientResources;

            return CraftingFailureKind.None;
        }

        /// <summary>
        /// Validates whether <paramref name="card"/> can be executed at <paramref name="machine"/>
        /// using <paramref name="stock"/>.
        /// </summary>
        public static CraftingValidationResult Validate(
            CraftingCard card,
            MachineDefinition machine,
            int machineLevel,
            bool machineBusy,
            MaterialStockLedger stock,
            bool isPlayerInRange = true)
        {
            if (card == null)
                return CraftingValidationResult.Fail(CraftingFailureKind.InvalidArguments, "No crafting card selected.");
            if (machine == null)
                return CraftingValidationResult.Fail(CraftingFailureKind.InvalidArguments, "No machine available.");
            if (stock == null)
                return CraftingValidationResult.Fail(CraftingFailureKind.InvalidArguments, "Team Material Stock is unavailable.");

            // 1. Machine compatibility
            if (!machine.AcceptsCard(card))
            {
                string expected = CraftingFormat.MachineTypeLabel(card.RequiredMachineType);
                return CraftingValidationResult.Fail(
                    CraftingFailureKind.IncompatibleMachine,
                    $"This card requires a {expected}.");
            }

            // 2. Machine level >= card complexity (must report locked even while machine is busy)
            if (machineLevel < card.ComplexityLevel)
            {
                return CraftingValidationResult.Fail(
                    CraftingFailureKind.LevelTooLow,
                    $"Requires machine level {card.ComplexityLevel} (this machine is level {machineLevel})");
            }

            // 3. Player in range
            if (!isPlayerInRange)
            {
                return CraftingValidationResult.Fail(
                    CraftingFailureKind.OutOfRange,
                    $"Move closer to the {machine.DisplayName}");
            }

            // 4. Machine busy
            if (machineBusy)
            {
                return CraftingValidationResult.Fail(
                    CraftingFailureKind.MachineBusy,
                    $"{machine.DisplayName} is currently processing.");
            }

            // 5. All inputs & energy present (collect EVERY shortfall before failing)
            List<MissingRequirement> missing = CollectMissingRequirements(card, stock);
            if (missing.Count > 0)
            {
                return CraftingValidationResult.Fail(
                    CraftingFailureKind.InsufficientResources,
                    BuildMissingMessage(missing),
                    missing);
            }

            return CraftingValidationResult.Success();
        }

        /// <summary>
        /// Validates and atomically deducts inputs + energy from <paramref name="stock"/>.
        /// If <paramref name="grantOutputsImmediately"/> is true, also credits all output materials immediately;
        /// otherwise the caller credits outputs via <see cref="GrantOutputs"/> when the machine finishes processing.
        /// </summary>
        public static CraftingExecutionOutcome TryExecute(
            CraftingCard card,
            MachineDefinition machine,
            int machineLevel,
            bool machineBusy,
            MaterialStockLedger stock,
            bool isPlayerInRange = true,
            bool grantOutputsImmediately = true)
        {
            CraftingValidationResult validation = Validate(card, machine, machineLevel, machineBusy, stock, isPlayerInRange);
            if (!validation.IsValid)
                return new CraftingExecutionOutcome(validation, validation.Message);

            if (!stock.TryConsumeAll(card.Inputs, card.EnergyCost))
            {
                // Defensive re-check if stock raced between Validate and Consume
                List<MissingRequirement> missing = CollectMissingRequirements(card, stock);
                CraftingValidationResult fallback = CraftingValidationResult.Fail(
                    CraftingFailureKind.InsufficientResources,
                    missing.Count > 0 ? BuildMissingMessage(missing) : "Insufficient resources.",
                    missing);
                return new CraftingExecutionOutcome(fallback, fallback.Message);
            }

            if (grantOutputsImmediately)
            {
                GrantOutputs(card, stock);
                return new CraftingExecutionOutcome(validation, BuildSuccessMessage(card, startedOnly: false));
            }

            return new CraftingExecutionOutcome(validation, BuildSuccessMessage(card, startedOnly: true));
        }

        /// <summary>Credits all output materials defined on <paramref name="card"/> to <paramref name="stock"/>.</summary>
        public static void GrantOutputs(CraftingCard card, MaterialStockLedger stock)
        {
            if (card == null || stock == null)
                return;
            stock.AddAll(card.Outputs);
        }

        /// <summary>
        /// Checks whether a machine upgrade cost can be paid with <paramref name="availableCoins"/> and <paramref name="stock"/>,
        /// returning a human-readable list of every missing coin/material requirement.
        /// </summary>
        public static bool CanAffordUpgrade(
            in MachineUpgradeCost cost,
            int availableCoins,
            MaterialStockLedger stock,
            out string failureMessage)
        {
            var parts = new List<string>(4);
            if (cost.coins > 0 && availableCoins < cost.coins)
                parts.Add($"Coins (need {cost.coins}, have {availableCoins})");

            if (cost.materials != null && stock != null)
            {
                for (int i = 0; i < cost.materials.Length; i++)
                {
                    MaterialAmount req = cost.materials[i];
                    if (!req.IsValid)
                        continue;
                    float have = stock.GetAmount(req.material.MaterialId);
                    if (have + MaterialStockLedger.QuantityEpsilon < req.amount)
                    {
                        var mr = new MissingRequirement(false, req.material.MaterialId, req.material.DisplayName, req.material.Unit, req.amount, have);
                        parts.Add(mr.Format());
                    }
                }
            }

            if (parts.Count == 0)
            {
                failureMessage = string.Empty;
                return true;
            }

            failureMessage = "Cannot upgrade — missing: " + string.Join(", ", parts);
            return false;
        }

        /// <summary>
        /// Populates <paramref name="results"/> with EVERY missing input material and energy deficit
        /// without allocating helper lists or dictionaries.
        /// </summary>
        public static void CollectMissingRequirementsNonAlloc(CraftingCard card, MaterialStockLedger stock, List<MissingRequirement> results)
        {
            if (results == null)
                return;
            results.Clear();
            if (card == null || stock == null)
                return;

            MaterialAmount[] inputs = card.Inputs;
            for (int i = 0; i < inputs.Length; i++)
            {
                MaterialAmount entry = inputs[i];
                if (!entry.IsValid)
                    continue;

                string id = entry.material.MaterialId;
                bool alreadyProcessed = false;
                for (int prev = 0; prev < i; prev++)
                {
                    if (inputs[prev].IsValid && string.Equals(inputs[prev].material.MaterialId, id, StringComparison.Ordinal))
                    {
                        alreadyProcessed = true;
                        break;
                    }
                }
                if (alreadyProcessed)
                    continue;

                float needed = entry.amount;
                for (int next = i + 1; next < inputs.Length; next++)
                {
                    if (inputs[next].IsValid && string.Equals(inputs[next].material.MaterialId, id, StringComparison.Ordinal))
                        needed += inputs[next].amount;
                }

                float have = stock.GetAmount(id);
                if (have + MaterialStockLedger.QuantityEpsilon < needed)
                {
                    results.Add(new MissingRequirement(false, id, entry.material.DisplayName, entry.material.Unit, needed, have));
                }
            }

            if (card.EnergyCost > 0 && !stock.HasEnergy(card.EnergyCost))
            {
                results.Add(new MissingRequirement(true, "energy", "Energy", MaterialUnit.Units, card.EnergyCost, stock.Energy));
            }
        }

        /// <summary>Collects EVERY missing input material and energy deficit without short-circuiting.</summary>
        public static List<MissingRequirement> CollectMissingRequirements(CraftingCard card, MaterialStockLedger stock)
        {
            var missing = new List<MissingRequirement>(4);
            CollectMissingRequirementsNonAlloc(card, stock, missing);
            return missing;
        }

        /// <summary>Builds a concise user-facing summary of completed or started card execution.</summary>
        public static string BuildSuccessMessage(CraftingCard card, bool startedOnly)
        {
            if (card == null)
                return string.Empty;

            var sb = new StringBuilder(96);
            sb.Append(startedOnly ? "Started " : "Completed ");
            sb.Append(card.DisplayName);

            MaterialAmount[] outputs = card.Outputs;
            if (outputs.Length > 0)
            {
                sb.Append(startedOnly ? " → " : " · +");
                for (int i = 0; i < outputs.Length; i++)
                {
                    if (!outputs[i].IsValid)
                        continue;
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(outputs[i].material.DisplayName);
                    sb.Append(' ');
                    sb.Append(outputs[i].Format());
                }
            }
            else if (card.OutputPrefab != null)
            {
                sb.Append(startedOnly ? " (building...)" : $" · Built {card.DisplayName}");
            }

            return sb.ToString();
        }

        static string BuildMissingMessage(List<MissingRequirement> missing)
        {
            var sb = new StringBuilder(96);
            sb.Append("Missing: ");
            for (int i = 0; i < missing.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(missing[i].Format());
            }
            return sb.ToString();
        }
    }
}
