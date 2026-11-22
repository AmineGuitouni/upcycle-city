using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Full-screen UI Toolkit layer for cinematics, built in code: letterbox bars, a black fade, a white
    /// flash, one centred line of text, a title card and a small hold-to-skip bar. Layout is set inline so
    /// it works without a style sheet; <c>UI/Intro.uss</c> styles the text.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Cinematic Overlay")]
    [RequireComponent(typeof(UIDocument))]
    public sealed class CinematicOverlay : MonoBehaviour
    {
        [SerializeField] StyleSheet styleSheet;

        VisualElement root, topBar, bottomBar, fade, flash, skip, skipFill;
        Label line, title;
        float letterbox;

        public float FadeAlpha { get; private set; } = 1f;

        void OnEnable()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            root.Clear();
            if (styleSheet != null && !root.styleSheets.Contains(styleSheet))
                root.styleSheets.Add(styleSheet);
            root.AddToClassList("intro-root");
            root.pickingMode = PickingMode.Ignore;
            Stretch(root);

            // Back to front: the promise line sits under the fade (it fades out with the picture);
            // the title card sits above it (shown on black)
            line = AddLabel("intro-line");
            // The line sits in the lower third, just above the bottom letterbox bar
            line.style.top = StyleKeyword.Auto;
            line.style.height = StyleKeyword.Auto;

            topBar = AddLayer("intro-bar", Color.black);
            topBar.style.bottom = StyleKeyword.Auto;
            bottomBar = AddLayer("intro-bar", Color.black);
            bottomBar.style.top = StyleKeyword.Auto;

            flash = AddLayer("intro-flash", new Color(1f, 0.98f, 0.92f));
            fade = AddLayer("intro-fade", Color.black);
            title = AddLabel("intro-title");

            skip = new VisualElement { pickingMode = PickingMode.Ignore };
            skip.AddToClassList("intro-skip");
            skip.style.position = Position.Absolute;
            skip.style.right = 48;
            skip.style.bottom = 36;
            skip.style.width = 150;
            skip.style.height = 4;
            skip.style.backgroundColor = new Color(1f, 1f, 1f, 0.18f);
            skipFill = new VisualElement { pickingMode = PickingMode.Ignore };
            skipFill.AddToClassList("intro-skip-fill");
            skipFill.style.height = Length.Percent(100);
            skipFill.style.backgroundColor = new Color(1f, 1f, 1f, 0.85f);
            skip.Add(skipFill);
            root.Add(skip);

            SetLetterbox(letterbox);
            SetFade(FadeAlpha);
            SetFlash(0f);
            SetSkipProgress(0f);
            HideText();
        }

        static void Stretch(VisualElement e)
        {
            e.style.position = Position.Absolute;
            e.style.left = 0;
            e.style.right = 0;
            e.style.top = 0;
            e.style.bottom = 0;
        }

        VisualElement AddLayer(string className, Color color)
        {
            var layer = new VisualElement { pickingMode = PickingMode.Ignore };
            layer.AddToClassList(className);
            Stretch(layer);
            layer.style.backgroundColor = color;
            root.Add(layer);
            return layer;
        }

        Label AddLabel(string className)
        {
            var label = new Label { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            Stretch(label);
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.color = new Color(0.95f, 0.94f, 0.9f);
            label.style.whiteSpace = WhiteSpace.Normal;
            root.Add(label);
            return label;
        }

        // ---------------------------------------------------------------- immediate setters

        /// <summary>Height of each letterbox bar as a fraction of the screen height.</summary>
        public void SetLetterbox(float fraction)
        {
            letterbox = fraction;
            if (topBar == null)
                return;
            topBar.style.height = Length.Percent(fraction * 100f);
            bottomBar.style.height = Length.Percent(fraction * 100f);
            line.style.bottom = Length.Percent(fraction * 100f + 7f);
        }

        public void SetFade(float alpha)
        {
            FadeAlpha = Mathf.Clamp01(alpha);
            if (fade != null)
                fade.style.opacity = FadeAlpha;
        }

        public void SetFlash(float alpha)
        {
            if (flash != null)
                flash.style.opacity = Mathf.Clamp01(alpha);
        }

        public void SetSkipProgress(float progress)
        {
            if (skip == null)
                return;
            skip.style.opacity = progress > 0f ? 1f : 0f;
            skipFill.style.width = Length.Percent(Mathf.Clamp01(progress) * 100f);
        }

        public void HideText()
        {
            if (line == null)
                return;
            line.style.opacity = 0f;
            title.style.opacity = 0f;
        }

        // ---------------------------------------------------------------- timed transitions

        public IEnumerator FadeTo(float alpha, float seconds)
        {
            float from = FadeAlpha;
            yield return CinematicActor.Tween(seconds, t => SetFade(Mathf.Lerp(from, alpha, t)));
        }

        /// <summary>Washes to white, holds while <paramref name="atPeak"/> swaps the world, then fades back.</summary>
        public IEnumerator Flash(float inSeconds, float holdSeconds, float outSeconds, System.Action atPeak)
        {
            yield return CinematicActor.Tween(inSeconds, t => SetFlash(t));
            atPeak?.Invoke();
            if (holdSeconds > 0f)
                yield return new WaitForSeconds(holdSeconds);
            yield return CinematicActor.Tween(outSeconds, t => SetFlash(1f - t));
        }

        public IEnumerator ShowLine(string text, float seconds) => ShowText(line, text, seconds);

        /// <summary>Reveals the line word by word, each word fading in, as if it were being said.</summary>
        public IEnumerator ShowLineWords(string text, float secondsPerWord, float fadePerWord = 0.6f)
        {
            if (line == null)
                yield break;
            string[] words = text.Replace("\n", " \n ").Split(' ');
            line.style.opacity = 1f;
            float total = (words.Length - 1) * secondsPerWord + fadePerWord;
            var sb = new System.Text.StringBuilder(text.Length * 2);
            for (float t = 0f; t <= total + Time.deltaTime; t += Time.deltaTime)
            {
                sb.Clear();
                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i] == "\n")
                    {
                        sb.Append('\n');
                        continue;
                    }
                    float a = Mathf.Clamp01((t - i * secondsPerWord) / fadePerWord);
                    int alpha = Mathf.RoundToInt(a * 255f);
                    sb.Append("<alpha=#").Append(alpha.ToString("X2")).Append('>').Append(words[i]).Append(' ');
                }
                line.text = sb.ToString();
                yield return null;
            }
            line.text = text;
        }

        /// <summary>Fades the title in while its letters drift apart.</summary>
        public IEnumerator ShowTitleTracking(string text, float seconds, float fromSpacing = 2f, float toSpacing = 14f)
        {
            if (title == null)
                yield break;
            title.text = text;
            yield return CinematicActor.Tween(seconds, t =>
            {
                title.style.opacity = t;
                title.style.letterSpacing = Mathf.Lerp(fromSpacing, toSpacing, t);
            });
        }
        public IEnumerator HideLine(float seconds) => HideText(line, seconds);
        public IEnumerator ShowTitle(string text, float seconds) => ShowText(title, text, seconds);
        public IEnumerator HideTitle(float seconds) => HideText(title, seconds);

        static IEnumerator ShowText(Label label, string text, float seconds)
        {
            if (label == null)
                yield break;
            label.text = text;
            float from = label.resolvedStyle.opacity;
            yield return CinematicActor.Tween(seconds, t => label.style.opacity = Mathf.Lerp(from, 1f, t));
        }

        static IEnumerator HideText(Label label, float seconds)
        {
            if (label == null)
                yield break;
            float from = label.resolvedStyle.opacity;
            yield return CinematicActor.Tween(seconds, t => label.style.opacity = Mathf.Lerp(from, 0f, t));
        }
    }
}
