using System.Text;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Shared authoritative upgrade helper used by both <see cref="CraftingMachine"/> and
    /// <see cref="ManualEnergyGenerator"/> so level bounds, strict range validation, coin + material
    /// spending, atomic coin refund on material failure, and cost formatting live in one place.
    /// </summary>
    public static class MachineUpgradeHelper
    {
        /// <summary>
        /// Validates and executes a single +1 machine upgrade on the authoritative side (solo or co-op host).
        /// Refunds coins automatically if the material deduction fails.
        /// </summary>
        public static CustomGameActionResult TryExecuteUpgrade(
            MachineDefinition definition,
            int currentLevel,
            string stationDisplayName,
            Vector3 stationWorldPosition,
            in CustomGameActionContext context,
            TeamMaterialStock stock,
            out int upgradedLevel)
        {
            upgradedLevel = currentLevel;

            if (definition == null)
                return CustomGameActionResult.Fail("Machine definition missing.");

            if (!ValidateRange(definition.InteractRange, stationDisplayName, stationWorldPosition, in context, out string rangeError))
                return CustomGameActionResult.Fail(rangeError);

            if (currentLevel >= definition.MaxLevel)
                return CustomGameActionResult.Fail($"{stationDisplayName} is already at max level (Lvl {definition.MaxLevel}).");

            int nextLevel = currentLevel + 1;
            if (!definition.TryGetUpgradeCost(nextLevel, out MachineUpgradeCost cost))
                return CustomGameActionResult.Fail($"No upgrade recipe configured for Level {nextLevel}.");

            if (stock == null)
                return CustomGameActionResult.Fail("Team Material Stock unavailable.");

            int coins = GameAuthority.Current.TeamCoins;
            if (!CraftingRules.CanAffordUpgrade(in cost, coins, stock.Ledger, out string failMsg))
                return CustomGameActionResult.Fail(failMsg);

            if (cost.coins > 0 && !GameAuthority.Current.TrySpendTeamCoins(cost.coins))
                return CustomGameActionResult.Fail($"Not enough team coins (need {cost.coins}).");

            if (cost.materials != null && cost.materials.Length > 0
                && !stock.TryConsumeAuthoritative(cost.materials, 0))
            {
                if (cost.coins > 0)
                    GameAuthority.Current.CreditTeamCoins(cost.coins);
                return CustomGameActionResult.Fail("Not enough team materials for upgrade.");
            }

            upgradedLevel = nextLevel;
            return CustomGameActionResult.Ok($"Upgraded {stationDisplayName} to Level {nextLevel}!");
        }

        /// <summary>
        /// Shared strict range validation for crafting/upgrade/crank custom actions.
        /// In co-op (<see cref="GameAuthority.IsMultiplayer"/>), rejects requests whose sender has no known player position.
        /// </summary>
        public static bool ValidateRange(
            float maxRange,
            string stationDisplayName,
            Vector3 stationWorldPosition,
            in CustomGameActionContext context,
            out string errorMessage)
        {
            if (context.SenderPosition.HasValue)
            {
                if (!context.IsWithinRange(stationWorldPosition, maxRange))
                {
                    errorMessage = $"Move closer to the {stationDisplayName}";
                    return false;
                }
                errorMessage = string.Empty;
                return true;
            }

            if (GameAuthority.IsMultiplayer)
            {
                errorMessage = $"Move closer to the {stationDisplayName}";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }

        /// <summary>
        /// Formats the complete upgrade cost (coins plus EVERY material entry) for UI buttons and tooltips.
        /// </summary>
        public static string FormatFullCost(in MachineUpgradeCost cost)
        {
            var sb = new StringBuilder(96);
            if (cost.coins > 0)
            {
                sb.Append(cost.coins);
                sb.Append(cost.coins == 1 ? " coin" : " coins");
            }

            if (cost.materials != null)
            {
                for (int i = 0; i < cost.materials.Length; i++)
                {
                    MaterialAmount req = cost.materials[i];
                    if (!req.IsValid)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(" + ");
                    sb.Append(req.Format());
                    sb.Append(' ');
                    sb.Append(req.material.DisplayName);
                }
            }

            return sb.Length > 0 ? sb.ToString() : "Free";
        }
    }
}
