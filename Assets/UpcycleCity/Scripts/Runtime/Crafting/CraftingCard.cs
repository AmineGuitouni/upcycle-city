using System;
using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Data-driven crafting / refining / building card asset executed at a compatible manual or automated machine.
    /// Cards with <see cref="CardArtwork"/> display their illustrated PNG; cards without artwork are rendered
    /// procedurally by <see cref="CraftingUI"/> in the same dark-teal / orange-tag card frame.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crafting/Crafting Card", fileName = "Card_New")]
    public sealed class CraftingCard : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] string cardId = "card_id";
        [SerializeField] string displayName = "Crafting Card";
        [SerializeField, TextArea(2, 4)] string description = "";
        [SerializeField] CardCategory category = CardCategory.Refining;
        [SerializeField] Sprite cardArtwork;

        [Header("Machine Requirements")]
        [Tooltip("Data-driven machine type required to execute this card.")]
        [SerializeField] MachineTypeDefinition requiredMachineType;

        [Tooltip("Minimum machine level required to execute this card (0 = starter manual machine, 1 = upgraded).")]
        [SerializeField, Min(0)] int complexityLevel;

        [Tooltip("Team energy (NRG) consumed when executing this card.")]
        [SerializeField, Min(0)] int energyCost = 10;

        [Tooltip("Approximate process temperature in °C shown on the card (0 = mechanical/ambient).")]
        [SerializeField, Min(0)] int temperatureCelsius;

        [Tooltip("Processing duration in seconds on the machine (0 = instant).")]
        [SerializeField, Min(0f)] float processingSeconds = 2.5f;

        [Header("Inputs & Outputs")]
        [SerializeField] MaterialAmount[] inputs = Array.Empty<MaterialAmount>();
        [SerializeField] MaterialAmount[] outputs = Array.Empty<MaterialAmount>();

        [Tooltip("Optional networked prefab spawned at the machine's OutputPoint when this card completes (e.g. Building cards).")]
        [SerializeField] GameObject outputPrefab;

        public string CardId => cardId;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? cardId : displayName;
        public string Description => description;
        public CardCategory Category => category;
        public Sprite CardArtwork => cardArtwork;
        public MachineTypeDefinition RequiredMachineType => requiredMachineType;
        public int ComplexityLevel => complexityLevel;
        public int EnergyCost => energyCost;
        public int TemperatureCelsius => temperatureCelsius;
        public float ProcessingSeconds => processingSeconds;
        public MaterialAmount[] Inputs => inputs ?? Array.Empty<MaterialAmount>();
        public MaterialAmount[] Outputs => outputs ?? Array.Empty<MaterialAmount>();
        public GameObject OutputPrefab => outputPrefab;

        /// <summary>Programmatic configuration helper for editor builders and unit tests.</summary>
        public void Configure(
            string id,
            string label,
            string desc,
            CardCategory cardCategory,
            MachineTypeDefinition machineType,
            int level,
            int nrgCost,
            int tempC,
            float durationSeconds,
            MaterialAmount[] cardInputs,
            MaterialAmount[] cardOutputs,
            Sprite artwork = null,
            GameObject spawnPrefab = null)
        {
            cardId = id ?? string.Empty;
            displayName = label ?? string.Empty;
            description = desc ?? string.Empty;
            category = cardCategory;
            requiredMachineType = machineType;
            complexityLevel = Mathf.Max(0, level);
            energyCost = Mathf.Max(0, nrgCost);
            temperatureCelsius = Mathf.Max(0, tempC);
            processingSeconds = Mathf.Max(0f, durationSeconds);
            inputs = cardInputs ?? Array.Empty<MaterialAmount>();
            outputs = cardOutputs ?? Array.Empty<MaterialAmount>();
            cardArtwork = artwork;
            outputPrefab = spawnPrefab;
        }
    }
}
