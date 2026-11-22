using UnityEngine;
using UnityEngine.UIElements;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Resource-loadable UI config holding references to the project's shared <see cref="PanelSettings"/>
    /// and <c>UI/Crafting.uss</c> stylesheet so <see cref="CraftingUI.EnsureInstance"/> works automatically
    /// in any scene (including runtime-spawned machines in future placement modes).
    /// </summary>
    [CreateAssetMenu(menuName = "Upcycle City/Crafting/Crafting UI Config", fileName = "CraftingUIConfig")]
    public sealed class CraftingUIConfig : ScriptableObject
    {
        public const string DefaultResourcesPath = "CraftingUIConfig";

        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet styleSheet;

        public PanelSettings PanelSettings => panelSettings;
        public StyleSheet StyleSheet => styleSheet;

        public static CraftingUIConfig LoadDefault() => Resources.Load<CraftingUIConfig>(DefaultResourcesPath);

        public void Configure(PanelSettings settings, StyleSheet sheet)
        {
            panelSettings = settings;
            styleSheet = sheet;
        }
    }
}
