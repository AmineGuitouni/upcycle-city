using UnityEngine;
using UnityEngine.UIElements;
using UpcycleCity.Core;

namespace UpcycleCity.UI
{
    /// <summary>
    /// The Graphics settings block shared by the Main Menu Settings card and the pause menu: four big segmented
    /// buttons (Very Low / Low / Medium / High, selected = brand green) with a one-line description, and a frame-rate
    /// row (Auto / 30 / 60). Picks apply instantly and are saved by <see cref="GraphicsQuality"/>. Styled by
    /// <c>Resources/GraphicsSettings.uss</c> (<c>gfx-*</c>), which it loads itself so any UI Toolkit panel can host it.
    /// </summary>
    public sealed class GraphicsSettingsView : VisualElement
    {
        const string StyleResource = "GraphicsSettings";

        static readonly string[] LevelHints =
        {
            "Smoothest on older phones: 60% resolution, no shadows, simpler grime, fewer animated people.",
            "Phones: 75% resolution, short hard shadows, fewer animated people.",
            "Recent phones: 90% resolution, soft shadows, 2x anti-aliasing.",
            "Desktop / flagship phones: full resolution, long soft shadows, 4x anti-aliasing, ambient occlusion.",
        };

        static readonly FrameRateCap[] Caps = { FrameRateCap.Auto, FrameRateCap.Fps30, FrameRateCap.Fps60 };
        static readonly string[] CapNames = { "Auto", "30 fps", "60 fps" };

        readonly Button[] levelButtons = new Button[GraphicsQuality.LevelCount];
        readonly Button[] capButtons = new Button[Caps.Length];
        readonly Label hint;

        public GraphicsSettingsView()
        {
            AddToClassList("gfx-root");
            StyleSheet sheet = Resources.Load<StyleSheet>(StyleResource);
            if (sheet != null)
                styleSheets.Add(sheet);

            Add(MakeLabel("GRAPHICS", "gfx-label"));
            var levels = new VisualElement();
            levels.AddToClassList("gfx-row");
            Add(levels);
            for (int i = 0; i < levelButtons.Length; i++)
            {
                var l = (GraphicsLevel)i;
                Button b = new Button(() => GraphicsQuality.SetLevel(l)) { text = GraphicsQuality.NameOf(l), name = "gfx-level-" + i };
                b.AddToClassList("gfx-seg");
                levels.Add(b);
                levelButtons[i] = b;
            }
            levelButtons[levelButtons.Length - 1].AddToClassList("gfx-seg--last");
            hint = MakeLabel(string.Empty, "gfx-hint");
            Add(hint);

            Add(MakeLabel("FRAME RATE", "gfx-label"));
            var caps = new VisualElement();
            caps.AddToClassList("gfx-row");
            Add(caps);
            for (int i = 0; i < Caps.Length; i++)
            {
                FrameRateCap cap = Caps[i];
                Button b = new Button(() => GraphicsQuality.SetFrameRateCap(cap)) { text = CapNames[i], name = "gfx-fps-" + (int)cap };
                b.AddToClassList("gfx-seg");
                caps.Add(b);
                capButtons[i] = b;
            }
            capButtons[capButtons.Length - 1].AddToClassList("gfx-seg--last");

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                GraphicsQuality.Changed -= OnChanged;
                GraphicsQuality.Changed += OnChanged;
                Refresh();
            });
            RegisterCallback<DetachFromPanelEvent>(_ => GraphicsQuality.Changed -= OnChanged);
            Refresh();
        }

        void OnChanged(GraphicsLevel _) => Refresh();

        /// <summary>Highlights the current level and cap.</summary>
        public void Refresh()
        {
            int current = (int)GraphicsQuality.Level;
            for (int i = 0; i < levelButtons.Length; i++)
                levelButtons[i].EnableInClassList("gfx-seg--active", i == current);
            hint.text = LevelHints[current];
            FrameRateCap cap = GraphicsQuality.FrameRateCap;
            for (int i = 0; i < capButtons.Length; i++)
                capButtons[i].EnableInClassList("gfx-seg--active", Caps[i] == cap);
            capButtons[0].text = cap == FrameRateCap.Auto ? $"Auto ({GraphicsQuality.TargetFrameRate})" : "Auto";
        }

        static Label MakeLabel(string text, string cls)
        {
            var label = new Label(text);
            label.AddToClassList(cls);
            return label;
        }
    }
}
