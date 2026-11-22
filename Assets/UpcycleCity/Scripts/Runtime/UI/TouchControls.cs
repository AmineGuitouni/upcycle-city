using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UpcycleCity.Player;

namespace UpcycleCity.UI
{
    /// <summary>
    /// Phone on-screen controls built into the gameplay HUD: a floating left joystick (Move), drag-to-look on
    /// the right side (Look), and round buttons for GRAB (Attack, hold to charge a throw), USE (Interact),
    /// TOOL (UseItem), DROP, JUMP, CROUCH and a RUN toggle (Sprint). Values go to <see cref="VirtualTouchInput"/>,
    /// which <see cref="PlayerInputReader"/> merges into the existing Player actions, so keyboard/gamepad keep
    /// working. Multi-touch safe (one captured pointer per control).
    /// Shown automatically on phones/tablets; in the Editor/dev builds press <b>F2</b> to preview them.
    /// </summary>
    public sealed class TouchControls
    {
        /// <summary>Overrides automatic detection (null = auto: mobile platform only).</summary>
        public static bool? ForceVisible;

        readonly VisualElement layer;
        readonly VisualElement stickBase;
        readonly VisualElement stickKnob;
        readonly VisualElement sprintButton;

        int stickPointer = -1;
        Vector2 stickCenter;
        int lookPointer = -1;
        bool sprintToggled;
        bool visible;

        const float StickRadius = 52f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ForceVisible = null;

        public TouchControls(VisualElement parent)
        {
            layer = new VisualElement();
            layer.AddToClassList("uc-touch");
            layer.pickingMode = PickingMode.Ignore;
            parent.Insert(0, layer);

            // Right-side drag-to-look
            var look = new VisualElement();
            look.AddToClassList("uc-look-zone");
            look.pickingMode = PickingMode.Position;
            look.RegisterCallback<PointerDownEvent>(e =>
            {
                if (lookPointer != -1)
                    return;
                lookPointer = e.pointerId;
                look.CapturePointer(e.pointerId);
            });
            look.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (e.pointerId != lookPointer)
                    return;
                VirtualTouchInput.AddLook(PanelToScreenDelta(look, e.deltaPosition));
            });
            look.RegisterCallback<PointerUpEvent>(e => ReleaseLook(look, e.pointerId));
            look.RegisterCallback<PointerCancelEvent>(e => ReleaseLook(look, e.pointerId));
            layer.Add(look);

            // Left floating joystick (touch anywhere in the zone recentres it)
            var stickZone = new VisualElement();
            stickZone.AddToClassList("uc-stick-zone");
            stickZone.pickingMode = PickingMode.Position;
            layer.Add(stickZone);
            stickBase = new VisualElement();
            stickBase.AddToClassList("uc-stick-base");
            stickBase.pickingMode = PickingMode.Ignore;
            stickKnob = new VisualElement();
            stickKnob.AddToClassList("uc-stick-knob");
            stickKnob.pickingMode = PickingMode.Ignore;
            stickBase.Add(stickKnob);
            layer.Add(stickBase);
            stickZone.RegisterCallback<PointerDownEvent>(e =>
            {
                if (stickPointer != -1)
                    return;
                stickPointer = e.pointerId;
                stickZone.CapturePointer(e.pointerId);
                Rect r = stickBase.worldBound;
                stickCenter = r.center;
                // Jump the base under the finger when touching away from it
                if (Vector2.Distance(e.position, stickCenter) > StickRadius * 1.4f)
                {
                    Vector2 local = stickBase.parent.WorldToLocal(e.position);
                    stickBase.style.left = local.x - r.width * 0.5f;
                    stickBase.style.bottom = stickBase.parent.layout.height - local.y - r.height * 0.5f;
                    stickCenter = e.position;
                }
                UpdateStick(e.position);
            });
            stickZone.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (e.pointerId == stickPointer)
                    UpdateStick(e.position);
            });
            stickZone.RegisterCallback<PointerUpEvent>(e => ReleaseStick(stickZone, e.pointerId));
            stickZone.RegisterCallback<PointerCancelEvent>(e => ReleaseStick(stickZone, e.pointerId));

            HoldButton("uc-tbtn--grab", "GRAB", "ui_hands", v => VirtualTouchInput.AttackHeld = v);
            HoldButton("uc-tbtn--use", "USE", null, v => VirtualTouchInput.InteractHeld = v);
            HoldButton("uc-tbtn--tool", "TOOL", "tool_wrench", v => VirtualTouchInput.UseItemHeld = v);
            HoldButton("uc-tbtn--drop", "DROP", null, v => VirtualTouchInput.DropHeld = v);
            HoldButton("uc-tbtn--jump", "JUMP", null, v => VirtualTouchInput.JumpHeld = v);
            HoldButton("uc-tbtn--crouch", "DUCK", null, v => VirtualTouchInput.CrouchHeld = v);
            sprintButton = HoldButton("uc-tbtn--sprint", "RUN", null, v =>
            {
                if (!v)
                    return;
                sprintToggled = !sprintToggled;
                VirtualTouchInput.SprintHeld = sprintToggled;
                sprintButton.EnableInClassList("uc-tbtn--on", sprintToggled);
            });

            SetVisible(false);
        }

        static Vector2 PanelToScreenDelta(VisualElement element, Vector3 panelDelta)
        {
            float panelWidth = element.panel != null ? element.panel.visualTree.layout.width : 0f;
            float scale = panelWidth > 1f ? Screen.width / panelWidth : 1f;
            // Panel Y grows downwards; look Y grows upwards
            return new Vector2(panelDelta.x * scale, -panelDelta.y * scale);
        }

        void ReleaseLook(VisualElement look, int pointerId)
        {
            if (pointerId != lookPointer)
                return;
            look.ReleasePointer(pointerId);
            lookPointer = -1;
        }

        void UpdateStick(Vector2 pointer)
        {
            Vector2 d = pointer - stickCenter;
            if (d.magnitude > StickRadius)
                d = d.normalized * StickRadius;
            stickKnob.style.translate = new Translate(d.x, d.y);
            VirtualTouchInput.Move = new Vector2(d.x / StickRadius, -d.y / StickRadius);
        }

        void ReleaseStick(VisualElement zone, int pointerId)
        {
            if (pointerId != stickPointer)
                return;
            zone.ReleasePointer(pointerId);
            stickPointer = -1;
            stickKnob.style.translate = new Translate(0f, 0f);
            stickBase.style.left = StyleKeyword.Null;
            stickBase.style.bottom = StyleKeyword.Null;
            VirtualTouchInput.Move = Vector2.zero;
        }

        VisualElement HoldButton(string cls, string label, string iconKey, System.Action<bool> setHeld)
        {
            var b = new VisualElement();
            b.AddToClassList("uc-tbtn");
            b.AddToClassList(cls);
            b.pickingMode = PickingMode.Position;
            // Glyph: kit touch glyphs come from GameHud.uss per button class; an IconLibrary key overrides it
            var glyph = new VisualElement();
            glyph.AddToClassList("uc-tbtn-icon");
            glyph.pickingMode = PickingMode.Ignore;
            Sprite icon = iconKey != null && IconLibrary.Active != null ? IconLibrary.Active.Get(iconKey) : null;
            if (icon != null)
            {
                glyph.style.backgroundImage = new StyleBackground(icon);
                glyph.AddToClassList("uc-tbtn-icon--full-colour");
            }
            b.Add(glyph);
            var l = new Label(label);
            l.AddToClassList("uc-tbtn-label");
            l.pickingMode = PickingMode.Ignore;
            b.Add(l);

            int owner = -1;
            b.RegisterCallback<PointerDownEvent>(e =>
            {
                if (owner != -1)
                    return;
                owner = e.pointerId;
                b.CapturePointer(e.pointerId);
                b.AddToClassList("uc-tbtn--held");
                setHeld(true);
                e.StopPropagation();
            });
            EventCallback<PointerUpEvent> up = e =>
            {
                if (e.pointerId != owner)
                    return;
                b.ReleasePointer(owner);
                owner = -1;
                b.RemoveFromClassList("uc-tbtn--held");
                setHeld(false);
            };
            b.RegisterCallback(up);
            b.RegisterCallback<PointerCancelEvent>(e =>
            {
                if (e.pointerId != owner)
                    return;
                b.ReleasePointer(owner);
                owner = -1;
                b.RemoveFromClassList("uc-tbtn--held");
                setHeld(false);
            });
            layer.Add(b);
            return b;
        }

        /// <summary>Whether the controls should be on screen right now.</summary>
        public static bool ShouldShow()
        {
            if (ForceVisible.HasValue)
                return ForceVisible.Value;
            return Application.isMobilePlatform;
        }

        /// <summary>Polls the F2 preview toggle (Editor/dev builds) and applies visibility.</summary>
        public void Tick(bool hudHidden)
        {
            if (DevHud.Allowed)
            {
                Keyboard kb = Keyboard.current;
                if (kb != null && kb.f2Key.wasPressedThisFrame)
                    ForceVisible = !ShouldShow();
            }

            bool show = !hudHidden && ShouldShow();
            if (show != visible)
                SetVisible(show);
        }

        void SetVisible(bool show)
        {
            visible = show;
            layer.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            VirtualTouchInput.Active = show;
            if (!show)
            {
                VirtualTouchInput.ReleaseAll();
                sprintToggled = false;
                stickPointer = -1;
                lookPointer = -1;
                sprintButton?.RemoveFromClassList("uc-tbtn--on");
            }
        }

        /// <summary>Hides the controls and releases every virtual input (HUD destroyed/disabled).</summary>
        public void Shutdown() => SetVisible(false);
    }
}
