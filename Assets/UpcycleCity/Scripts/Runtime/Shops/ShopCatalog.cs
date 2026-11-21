using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Inventory;

namespace UpcycleCity.Shops
{
    /// <summary>
    /// What one shop sells and how its keeper greets the player. Tune prices here for demo balancing;
    /// the shop window (<see cref="ShopUI"/>) reads it live.
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Shops/Shop Catalog", fileName = "Shop_New")]
    public sealed class ShopCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Offer
        {
            public ItemDefinition item;
            [Min(0)] public int price = 10;
            [Tooltip("How many units one purchase gives.")]
            [Min(1)] public int quantity = 1;
        }

        [SerializeField] string shopName = "Shop";
        [SerializeField] string keeperName = "Shopkeeper";
        [SerializeField, TextArea(2, 3)] string greeting = "Ahla! What can I get you?";
        [SerializeField] Color accent = new Color(0.18f, 0.55f, 0.34f);
        [Tooltip("The RÉCUP buy-back point shows what it pays for trash instead of (or as well as) its offers.")]
        [SerializeField] bool showsSellPrices;
        [SerializeField] List<Offer> offers = new List<Offer>();

        public string ShopName => shopName;
        public string KeeperName => keeperName;
        public string Greeting => greeting;
        public Color Accent => accent;
        public bool ShowsSellPrices => showsSellPrices;
        public IReadOnlyList<Offer> Offers => offers;

        public void Configure(string shop, string keeper, string hello, Color accentColor, bool sellPrices, IEnumerable<Offer> shopOffers)
        {
            shopName = shop;
            keeperName = keeper;
            greeting = hello;
            accent = accentColor;
            showsSellPrices = sellPrices;
            offers.Clear();
            if (shopOffers != null)
                offers.AddRange(shopOffers);
        }
    }
}
