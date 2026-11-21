using UnityEngine;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// The four core sorting streams plus bulky items meant for repair/upcycling.
    /// Colours mirror docs/game_concept.md so bins, crates, and HUD stay consistent.
    /// </summary>
    public enum WasteStream
    {
        Paper = 0,
        Plastic = 1,
        Glass = 2,
        Cans = 3,
        BulkyUpcyclable = 4,
    }

    /// <summary>Tracks what caused a piece of trash to exist in the city (no magic spawning).</summary>
    public enum WasteSourceKind
    {
        Initial = 0,
        Citizen = 1,
        ShopOverflow = 2,
        FactoryScrap = 3,
        UrbanDecay = 4,
        Event = 5,
    }

    public static class WasteStreamPalette
    {
        public static readonly Color Paper = new Color(0.180f, 0.435f, 0.710f);   // #2E6FB5
        public static readonly Color Plastic = new Color(0.949f, 0.710f, 0.106f); // #F2B51B
        public static readonly Color Glass = new Color(0.180f, 0.545f, 0.341f);   // #2E8B57
        public static readonly Color Cans = new Color(0.784f, 0.275f, 0.231f);    // #C8463B
        public static readonly Color Bulky = new Color(0.706f, 0.337f, 0.180f);   // #B4562E

        public static Color ColorFor(WasteStream stream)
        {
            switch (stream)
            {
                case WasteStream.Paper: return Paper;
                case WasteStream.Plastic: return Plastic;
                case WasteStream.Glass: return Glass;
                case WasteStream.Cans: return Cans;
                default: return Bulky;
            }
        }

        public static string LabelFor(WasteStream stream)
        {
            switch (stream)
            {
                case WasteStream.Paper: return "PAPER";
                case WasteStream.Plastic: return "PLASTIC";
                case WasteStream.Glass: return "GLASS";
                case WasteStream.Cans: return "CANS";
                default: return "BULKY";
            }
        }
    }
}
