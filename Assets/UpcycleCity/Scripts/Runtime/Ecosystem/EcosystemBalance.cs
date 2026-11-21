using UnityEngine;

namespace UpcycleCity.Ecosystem
{
    public enum EcosystemDifficulty
    {
        Relaxed = 0,
        Normal = 1,
        Challenging = 2,
    }

    /// <summary>
    /// One place to tune the living city's difficulty and pacing: how fast bins fill and spill, how often citizens
    /// act and litter, how much trash the city can hold, how hard smog slows the player, how the factory and the
    /// alley pollute, and what cleaning pays. Assigned to the <see cref="CityEcosystemManager"/>; every system reads
    /// it live (falling back to its own Inspector values when no balance is assigned), so edits made in Play Mode
    /// take effect immediately and stay in the asset.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Ecosystem/Ecosystem Balance", fileName = "EcosystemBalance")]
    public sealed class EcosystemBalance : ScriptableObject
    {
        /// <summary>The balance of the running <see cref="CityEcosystemManager"/>, or null.</summary>
        public static EcosystemBalance Active { get; private set; }

        [Header("City")]
        [Tooltip("Most pieces of physical trash allowed in the city at once. Sources stop spawning at the cap.")]
        [Range(15, 200)] public int maxGlobalTrash = 60;
        [Tooltip("Multiplies every district's pollution capacity: higher means it takes more trash to make a street dirty.")]
        [Range(0.25f, 3f)] public float districtCapacityScale = 1f;

        [Header("Smog")]
        [Tooltip("Local pollution above which smog starts slowing a player without a mask or water.")]
        [Range(0.2f, 0.95f)] public float smogSlowThreshold = 0.62f;
        [Tooltip("Player speed multiplier in the thickest smog (1 = no slowdown).")]
        [Range(0.5f, 1f)] public float smogMaxSlowdown = 0.82f;

        [Header("Street Bins")]
        [Tooltip("How fast each bin fills by itself from foot traffic (fraction per second; 0.005 ≈ full from empty in 3+ minutes).")]
        [Range(0f, 0.03f)] public float binPassiveFillPerSecond = 0.0015f;
        [Tooltip("Scales every citizen deposit into a bin. Lower = bigger bins that fill more slowly.")]
        [Range(0.1f, 2f)] public float binDepositScale = 0.35f;
        [Tooltip("Seconds between pieces of trash spilling out of an overflowing bin.")]
        [Range(2f, 60f)] public float binSpillInterval = 18f;
        [Tooltip("Air pollution an overflowing bin adds to its district (stench and grime).")]
        [Range(0f, 10f)] public float binOverflowAirLoad = 3.2f;
        [Tooltip("Coins for emptying a full bin (scaled by how full it is).")]
        [Range(1, 100)] public int binServiceReward = 14;

        [Header("Citizens")]
        [Tooltip("How often walking citizens act (use a bin, litter, sit, look at shops). 1 = normal, 2 = twice as often.")]
        [Range(0.25f, 3f)] public float citizenActivity = 1f;
        [Tooltip("Scales how likely citizens are to litter instead of using a bin.")]
        [Range(0f, 3f)] public float litterChance = 0.7f;
        [Tooltip("Bin fill added each time a visible citizen uses a bin (before the bin deposit scale).")]
        [Range(0.02f, 0.3f)] public float visibleBinDeposit = 0.04f;
        [Tooltip("Bin fill added each time an off-screen (background) citizen uses a bin.")]
        [Range(0.005f, 0.08f)] public float backgroundBinDeposit = 0.005f;
        [Tooltip("Off-screen citizens simulated in batches (applied when the scene starts).")]
        [Range(0, 2000)] public int backgroundPopulation = 80;
        [Tooltip("City satisfaction at which citizens take pride in their streets and almost stop littering.")]
        [Range(0.5f, 0.98f)] public float civicPrideThreshold = 0.8f;

        [Header("Urban Decay (dumped bulky items)")]
        [Tooltip("District pollution above which people start dumping broken furniture and appliances.")]
        [Range(0.15f, 0.85f)] public float decayThreshold = 0.42f;
        [Tooltip("Seconds between dumped items while a district is neglected.")]
        [Range(4f, 120f)] public float decayDumpInterval = 16f;

        [Header("Factory")]
        [Tooltip("Air pollution from the smokestack while its scrubber is broken.")]
        [Range(1f, 40f)] public float factoryUnfilteredLoad = 16.5f;
        [Tooltip("Air pollution from the smokestack once the scrubber is repaired.")]
        [Range(0f, 10f)] public float factoryFilteredLoad = 1.8f;
        [Tooltip("Seconds between pieces of scrap the factory dumps while unfiltered.")]
        [Range(2f, 60f)] public float factoryScrapInterval = 9.5f;
        [Tooltip("Coins for repairing the scrubber with the Wrench.")]
        [Range(5, 200)] public int factoryRepairReward = 35;

        [Header("Rewards")]
        [Tooltip("Multiplies coins paid by the sorting bins.")]
        [Range(0f, 3f)] public float sortingPayout = 1f;
        [Tooltip("Multiplies what the RÉCUP buy-back pays (on top of its per-stream rates).")]
        [Range(0f, 3f)] public float recyclingPayout = 1f;

        /// <summary>Called by the ecosystem manager when it starts and stops.</summary>
        public static void SetActive(EcosystemBalance balance) => Active = balance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Active = null;

        /// <summary>Overwrites every value with a difficulty preset.</summary>
        public void ApplyPreset(EcosystemDifficulty difficulty)
        {
            // Normal is the demo pace; the others scale it
            maxGlobalTrash = 60;
            districtCapacityScale = 1f;
            smogSlowThreshold = 0.62f;
            smogMaxSlowdown = 0.82f;
            binPassiveFillPerSecond = 0.0015f;
            binDepositScale = 0.35f;
            binSpillInterval = 18f;
            binOverflowAirLoad = 3.2f;
            binServiceReward = 14;
            citizenActivity = 1f;
            litterChance = 0.7f;
            visibleBinDeposit = 0.04f;
            backgroundBinDeposit = 0.005f;
            backgroundPopulation = 80;
            civicPrideThreshold = 0.8f;
            decayThreshold = 0.42f;
            decayDumpInterval = 16f;
            factoryUnfilteredLoad = 16.5f;
            factoryFilteredLoad = 1.8f;
            factoryScrapInterval = 9.5f;
            factoryRepairReward = 35;
            sortingPayout = 1f;
            recyclingPayout = 1f;

            switch (difficulty)
            {
                case EcosystemDifficulty.Relaxed:
                    maxGlobalTrash = 40;
                    districtCapacityScale = 1.5f;
                    smogSlowThreshold = 0.75f;
                    smogMaxSlowdown = 0.9f;
                    binPassiveFillPerSecond = 0.0008f;
                    binDepositScale = 0.2f;
                    binSpillInterval = 28f;
                    citizenActivity = 0.7f;
                    litterChance = 0.4f;
                    backgroundPopulation = 50;
                    civicPrideThreshold = 0.7f;
                    decayDumpInterval = 30f;
                    factoryScrapInterval = 16f;
                    sortingPayout = 1.25f;
                    recyclingPayout = 1.25f;
                    break;
                case EcosystemDifficulty.Challenging:
                    maxGlobalTrash = 90;
                    districtCapacityScale = 0.75f;
                    smogSlowThreshold = 0.5f;
                    smogMaxSlowdown = 0.7f;
                    binPassiveFillPerSecond = 0.003f;
                    binDepositScale = 0.6f;
                    binSpillInterval = 10f;
                    citizenActivity = 1.4f;
                    litterChance = 1.3f;
                    backgroundPopulation = 160;
                    civicPrideThreshold = 0.88f;
                    decayDumpInterval = 10f;
                    factoryScrapInterval = 6f;
                    sortingPayout = 0.8f;
                    recyclingPayout = 0.85f;
                    break;
            }
        }
    }
}
