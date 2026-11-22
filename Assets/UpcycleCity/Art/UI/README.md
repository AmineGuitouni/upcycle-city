# Upcycle City — UI Art Kit Contract

This folder (`Assets/UpcycleCity/Art/UI/`) is the production UI art kit for **Upcycle City** (Unity 6 UI Toolkit / USS). All assets are crisp vector-rendered **@2x RGBA PNGs** with real transparency and smooth drop shadows, automatically imported by [`UIKitImportRules.cs`](../../Scripts/Editor/UIKitImportRules.cs) as `Sprite (2D and UI)` with exact 9-slice `spriteBorder` values pre-baked and zero-banding compression (`Uncompressed` on desktop, `ASTC 4x4` high-quality on Android/iOS).

---

## 1. Resolution & 9-Slice Scaling Rules

- **Design Reference:** `1080 × 608` (matches `UI/MainMenuPanelSettings.asset`). Every asset is authored at **@2x** (twice the on-screen pixel dimensions on `1080 × 608`).
- **Slice Scale in USS:**
  - On **`1080 × 608`** panels (`MainMenuPanelSettings.asset`), add **`-unity-slice-scale: 0.5px;`** to every 9-sliced element so the @2x corner radii, borders, and drop shadows render at crisp 1× layout scale.
  - On **`1920 × 1080`** panels (`UpcycleCityPanelSettings.asset`, which is `1.78×` wider than `1080 × 608`), use **`-unity-slice-scale: 0.9px;`** (or `1px` for chunky mobile touch targets).
- **Drop Shadow Bleed:** Cards, pills, and buttons include a subtle baked drop shadow (`~5–7 px` on-screen / `10–14 px` in @2x texture space) strictly *inside* their 9-slice borders. Add `padding` matching the visual inset if placing flush content inside a card, or use negative margin if aligning edge-to-edge.
- **Border Mapping:** Unity C# `TextureImporter.spriteBorder` uses `(left, bottom, right, top)`, whereas USS uses `-unity-slice-left`, `-unity-slice-top`, `-unity-slice-right`, `-unity-slice-bottom`. Both are already aligned between `UIKitImportRules.cs` and the tables below.

---

## 2. Color & Tinting Reference

Use `-unity-background-image-tint-color` on pure-white tintable sprites (`HUD/hud_bar_fill.png`, `HUD/hud_minibanner.png`, `Touch/touch_btn_glyph_*.png`, `Buttons/swatch_ring_selected.png`):

| Role | Hex | Usage |
| --- | --- | --- |
| **Brand Green** | `#2E8B57` | Clean air/city bars, healthy status, ready/selected highlights |
| **Brand Teal** | `#2F8F8A` | Secondary eco highlights, satisfaction/craft progress |
| **Safety Yellow** | `#F2C21B` | Active tab underline, selected hotbar glow, coin/warning accent |
| **Hi-Vis Orange** | `#FF7A1A` | Primary CTA, active touch press, event countdown banner |
| **Stage 1 Rust** | `#B4562E` | District/location minibanner (`AVENUE HABIB BOURGUIBA`), bulky stream |
| **Danger Red** | `#D9483B` | Thick smog alert banner, overflow/critical pollution bars, quit/leave |
| **Stream: Paper** | `#2E6FB5` | Paper sorting badge / meter / stream square |
| **Stream: Plastic** | `#F2B51B` | Plastic sorting badge / meter / stream square |
| **Stream: Glass** | `#2E8B57` | Glass sorting badge / meter / stream square |
| **Stream: Cans** | `#C8463B` | Cans sorting badge / meter / stream square |
| **Stream: Bulky** | `#B4562E` | Bulky waste badge / stream square |
| **Stream: Organic** | `#78572E` | Organic waste badge / stream square |
| **Surface Dark Card** | `#1E2124` | Base charcoal card tone (92% alpha) |
| **Text Primary** | `#F8FAFC` | Headings, values, button labels (bold rounded sans) |
| **Text Muted** | `#9CA3AF` | Subtitles, secondary descriptions, inactive tabs |

---

## 3. Complete Asset Catalog & Exact USS Snippets

All asset URLs follow:
```css
url("project://database/Assets/UpcycleCity/Art/UI/<Folder>/<file>.png")
```

---

### 3.1 `Logo/`

| File | Pixel Size (@2x) | On-Screen (`1080×608`) | Slice `L / T / R / B` (px) | Notes |
| --- | --- | --- | --- | --- |
| `logo_upcycle_city.png` | `1400 × 300` | `420 × 90` – `560 × 120` | Not sliced | Bold white `"UPCYCLE CITY"` wordmark + green recycle-leaf emblem + soft shadow. Max size `2048`. |
| `logo_badge_stage.png` | `256 × 88` | `200–260 × 32–38` | `44 / 28 / 44 / 32` | Warm orange pill banner for `"● STAGE 1 • THE GARAGE"` subtitle. |

```css
.uc-logo-wordmark {
    width: 460px;
    height: 98px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Logo/logo_upcycle_city.png");
    -unity-background-scale-mode: scale-to-fit;
}

.uc-stage-badge {
    min-height: 36px;
    padding: 6px 20px 8px 20px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Logo/logo_badge_stage.png");
    -unity-slice-left: 44;
    -unity-slice-top: 28;
    -unity-slice-right: 44;
    -unity-slice-bottom: 32;
    -unity-slice-scale: 0.5px;
}
```

---

### 3.2 `Panels/`

| File | Pixel Size (@2x) | On-Screen (`1080×608`) | Slice `L / T / R / B` (px) | Notes |
| --- | --- | --- | --- | --- |
| `panel_card.png` | `256 × 256` | Flexible (`260 × 320`, etc.) | `48 / 48 / 48 / 48` | Main dark charcoal rounded card (`#1E2124` @ 92%), 1px light rim, soft drop shadow. |
| `panel_card_header.png` | `256 × 112` | Flexible width × `38–48` | `32 / 32 / 32 / 8` | Darker rounded-top header strip with subtle bottom hairline; flush edges to dock inside `panel_card`. |
| `panel_tooltip.png` | `192 × 128` | Flexible (`180 × 70`) | `36 / 36 / 36 / 36` | High-density dark floating tooltip with crisp bright rim. |
| `panel_toast.png` | `256 × 112` | Flexible (`280–420 × 44`) | `44 / 40 / 44 / 46` | Notification pill-card for unlocks, co-op joins, and awards. |
| `panel_modal_backdrop.png` | `640 × 360` | Full screen `100% × 100%` | Not sliced | Soft radial vignette dim overlay behind modals/shops/crafting. |
| `panel_tab_bar.png` | `256 × 96` | Flexible width × `36–44` | `32 / 32 / 32 / 8` | Dark rounded-top tab container bar with bottom border line. |
| `tab_selected.png` | `192 × 96` | Flexible width × `36–44` | `12 / 12 / 12 / 12` | Active tab fill + crisp Safety Yellow (`#F2C21B`) 3px on-screen bottom underline. |
| `divider.png` | `256 × 8` | Flexible width × `2–4` | `36 / 0 / 36 / 0` | Subtle horizontal hairline separator fading gently at left/right edges. |

```css
.uc-card {
    padding: 16px 18px 20px 18px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Panels/panel_card.png");
    -unity-slice-left: 48;
    -unity-slice-top: 48;
    -unity-slice-right: 48;
    -unity-slice-bottom: 48;
    -unity-slice-scale: 0.5px;
}

.uc-card-header {
    min-height: 40px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Panels/panel_card_header.png");
    -unity-slice-left: 32;
    -unity-slice-top: 32;
    -unity-slice-right: 32;
    -unity-slice-bottom: 8;
    -unity-slice-scale: 0.5px;
}

.uc-tooltip {
    padding: 12px 14px 14px 14px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Panels/panel_tooltip.png");
    -unity-slice-left: 36;
    -unity-slice-top: 36;
    -unity-slice-right: 36;
    -unity-slice-bottom: 36;
    -unity-slice-scale: 0.5px;
}

.uc-toast {
    min-height: 44px;
    padding: 10px 18px 12px 18px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Panels/panel_toast.png");
    -unity-slice-left: 44;
    -unity-slice-top: 40;
    -unity-slice-right: 44;
    -unity-slice-bottom: 46;
    -unity-slice-scale: 0.5px;
}

.uc-modal-backdrop {
    position: absolute;
    left: 0; top: 0; right: 0; bottom: 0;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Panels/panel_modal_backdrop.png");
    -unity-background-scale-mode: stretch-to-fill;
}

.uc-tab-bar {
    height: 40px;
    flex-direction: row;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Panels/panel_tab_bar.png");
    -unity-slice-left: 32;
    -unity-slice-top: 32;
    -unity-slice-right: 32;
    -unity-slice-bottom: 8;
    -unity-slice-scale: 0.5px;
}

.uc-tab--selected {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Panels/tab_selected.png");
    -unity-slice-left: 12;
    -unity-slice-top: 12;
    -unity-slice-right: 12;
    -unity-slice-bottom: 12;
    -unity-slice-scale: 0.5px;
}

.uc-divider {
    height: 2px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Panels/divider.png");
    -unity-slice-left: 36;
    -unity-slice-top: 0;
    -unity-slice-right: 36;
    -unity-slice-bottom: 0;
    -unity-slice-scale: 0.5px;
}
```

---

### 3.3 `Buttons/`

| File | Pixel Size (@2x) | On-Screen (`1080×608`) | Slice `L / T / R / B` (px) | Notes |
| --- | --- | --- | --- | --- |
| `btn_primary_orange.png` | `256 × 112` | Flexible × `40–48` | `36 / 36 / 36 / 40` | Hi-Vis Orange primary CTA (normal). |
| `btn_primary_orange_pressed.png` | `256 × 112` | Same | `36 / 36 / 36 / 40` | Hi-Vis Orange primary CTA (`:active`). |
| `btn_primary_orange_disabled.png` | `256 × 112` | Same | `36 / 36 / 36 / 40` | Hi-Vis Orange primary CTA (`:disabled`). |
| `btn_secondary_dark.png` | `256 × 112` | Flexible × `38–46` | `36 / 36 / 36 / 40` | Dark charcoal pill button (`APPEARANCE`, `RECIPES`, `SETTINGS`). |
| `btn_secondary_dark_pressed.png` | `256 × 112` | Same | `36 / 36 / 36 / 40` | Dark charcoal pill button (`:active`). |
| `btn_secondary_dark_disabled.png` | `256 × 112` | Same | `36 / 36 / 36 / 40` | Dark charcoal pill button (`:disabled`). |
| `btn_success_green.png` | `256 × 112` | Flexible × `36–46` | `36 / 36 / 36 / 40` | Brand Green selected / confirm / buy / ready button. |
| `btn_success_green_pressed.png` | `256 × 112` | Same | `36 / 36 / 36 / 40` | Brand Green button (`:active`). |
| `btn_success_green_disabled.png` | `256 × 112` | Same | `36 / 36 / 36 / 40` | Brand Green button (`:disabled`). |
| `btn_danger_red.png` | `256 × 112` | Flexible × `36–46` | `36 / 36 / 36 / 40` | Danger Red quit / leave / cancel button. |
| `btn_danger_red_pressed.png` | `256 × 112` | Same | `36 / 36 / 36 / 40` | Danger Red button (`:active`). |
| `btn_danger_red_disabled.png` | `256 × 112` | Same | `36 / 36 / 36 / 40` | Danger Red button (`:disabled`). |
| `btn_pill_chip.png` | `160 × 72` | Flexible × `24–30` | `36 / 24 / 36 / 28` | Small dark pill button (`↑ Level +1`, filter tags). |
| `btn_round_play.png` | `320 × 320` | `104 × 104` – `120 × 120` | Not sliced | Big circular orange PLAY button with white ring, glow & white play triangle. Triangle sits slightly above center (~42% Y) so a `"PLAY"` label fits cleanly below it! |
| `btn_round_play_pressed.png` | `320 × 320` | Same | Not sliced | Pressed state (`:active`) for `btn_round_play`. |
| `btn_round_icon.png` | `128 × 128` | `38 × 38` – `48 × 48` | Not sliced | Circular dark base for icon-only buttons (Close, Settings, Audio). |
| `btn_round_icon_pressed.png` | `128 × 128` | Same | Not sliced | Pressed state (`:active`) for `btn_round_icon`. |
| `toggle_on.png` | `128 × 72` | `52 × 28` | Not sliced | Mobile toggle ON (Brand Green track, white right knob). |
| `toggle_off.png` | `128 × 72` | `52 × 28` | Not sliced | Mobile toggle OFF (dark slate track, left knob). |
| `swatch_ring_selected.png` | `96 × 96` | `28 × 28` – `36 × 36` | `28 / 28 / 28 / 28` | White rounded-square highlight ring overlaid on selected colour swatches (tintable). |
| `slider_track.png` | `192 × 24` | Flexible width × `8–10` | `12 / 0 / 12 / 0` | Slim translucent horizontal pill track. |
| `slider_knob.png` | `64 × 64` | `20 × 20` – `24 × 24` | Not sliced | Circular white slider thumb with soft drop shadow. |

```css
/* Standard 9-sliced rectangular buttons (same slice values across orange/dark/green/red) */
.uc-btn-primary {
    min-height: 44px;
    padding: 8px 18px 10px 18px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_primary_orange.png");
    -unity-slice-left: 36;
    -unity-slice-top: 36;
    -unity-slice-right: 36;
    -unity-slice-bottom: 40;
    -unity-slice-scale: 0.5px;
    color: #FFFFFF;
}
.uc-btn-primary:active {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_primary_orange_pressed.png");
}
.uc-btn-primary:disabled {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_primary_orange_disabled.png");
    color: rgba(255, 255, 255, 0.45);
}

.uc-btn-dark {
    min-height: 42px;
    padding: 8px 16px 10px 16px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_secondary_dark.png");
    -unity-slice-left: 36;
    -unity-slice-top: 36;
    -unity-slice-right: 36;
    -unity-slice-bottom: 40;
    -unity-slice-scale: 0.5px;
    color: #F8FAFC;
}
.uc-btn-dark:active {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_secondary_dark_pressed.png");
}
.uc-btn-dark:disabled {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_secondary_dark_disabled.png");
}

.uc-btn-green {
    min-height: 40px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_success_green.png");
    -unity-slice-left: 36; -unity-slice-top: 36; -unity-slice-right: 36; -unity-slice-bottom: 40;
    -unity-slice-scale: 0.5px;
}
.uc-btn-green:active {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_success_green_pressed.png");
}
.uc-btn-green:disabled {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_success_green_disabled.png");
}

.uc-btn-danger {
    min-height: 40px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_danger_red.png");
    -unity-slice-left: 36; -unity-slice-top: 36; -unity-slice-right: 36; -unity-slice-bottom: 40;
    -unity-slice-scale: 0.5px;
}
.uc-btn-danger:active {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_danger_red_pressed.png");
}
.uc-btn-danger:disabled {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_danger_red_disabled.png");
}

.uc-btn-pill-chip {
    min-height: 26px;
    padding: 3px 12px 5px 12px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_pill_chip.png");
    -unity-slice-left: 36; -unity-slice-top: 24; -unity-slice-right: 36; -unity-slice-bottom: 28;
    -unity-slice-scale: 0.5px;
}

.uc-btn-round-play {
    width: 112px;
    height: 112px;
    justify-content: flex-end;
    align-items: center;
    padding-bottom: 20px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_round_play.png");
    -unity-background-scale-mode: scale-to-fit;
    color: #FFFFFF;
    -unity-font-style: bold;
    font-size: 16px;
}
.uc-btn-round-play:active {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_round_play_pressed.png");
}

.uc-btn-round-icon {
    width: 44px;
    height: 44px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_round_icon.png");
    -unity-background-scale-mode: scale-to-fit;
}
.uc-btn-round-icon:active {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/btn_round_icon_pressed.png");
}

.uc-toggle { width: 52px; height: 28px; background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/toggle_off.png"); }
.uc-toggle--on { background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/toggle_on.png"); }

.uc-swatch-selected {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/swatch_ring_selected.png");
    -unity-slice-left: 28; -unity-slice-top: 28; -unity-slice-right: 28; -unity-slice-bottom: 28;
    -unity-slice-scale: 0.5px;
}

.uc-slider-track {
    height: 8px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/slider_track.png");
    -unity-slice-left: 12; -unity-slice-top: 0; -unity-slice-right: 12; -unity-slice-bottom: 0;
    -unity-slice-scale: 0.5px;
}
.uc-slider-knob {
    width: 22px;
    height: 22px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Buttons/slider_knob.png");
}
```

---

### 3.4 `HUD/`

| File | Pixel Size (@2x) | On-Screen (`1080×608`) | Slice `L / T / R / B` (px) | Notes |
| --- | --- | --- | --- | --- |
| `hud_chip.png` | `224 × 96` | Flexible × `34–40` | `40 / 34 / 40 / 40` | Dark translucent HUD pill for Level, Air & Clean, Coins, Join Code. |
| `hud_coin.png` | `128 × 128` | `20 × 20` – `26 × 26` | Not sliced | Crisp front-facing gold Upcycle leaf coin (high-contrast at small HUD sizes). |
| `hud_bar_bg.png` | `256 × 40` | Flexible width × `10–14` | `22 / 12 / 22 / 12` | Recessed dark rounded progress bar track. |
| `hud_bar_fill.png` | `256 × 32` | Flexible width × `8–12` | `18 / 10 / 18 / 10` | **White tintable** rounded progress bar fill (`-unity-background-image-tint-color`). |
| `hotbar_slot.png` | `144 × 144` | `58 × 58` – `64 × 64` | `36 / 34 / 36 / 38` | Dark charcoal rounded-square equipment/tool slot. |
| `hotbar_slot_selected.png` | `144 × 144` | `58 × 58` – `64 × 64` | `36 / 34 / 36 / 38` | Active tool slot with Safety Yellow + Brand Green border, glow & bottom highlight bar. |
| `hud_crosshair.png` | `96 × 96` | `24 × 24` – `30 × 30` | Not sliced | Crisp white dot + thin ring with dark outer halo (reads on bright skies & dark roads). |
| `hud_prompt_key.png` | `96 × 96` | `28 × 28` – `34 × 34` | `26 / 24 / 26 / 30` | Empty tactile 3D keycap frame — overlay a centered bold `"E"` / `"F"` Label. |
| `hud_minibanner.png` | `256 × 96` | Flexible × `30–40` | `38 / 34 / 38 / 40` | **White tintable** rounded warning/event/district banner. |

```css
.uc-hud-chip {
    min-height: 36px;
    padding: 6px 14px 8px 14px;
    flex-direction: row;
    align-items: center;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hud_chip.png");
    -unity-slice-left: 40; -unity-slice-top: 34; -unity-slice-right: 40; -unity-slice-bottom: 40;
    -unity-slice-scale: 0.5px;
}

.uc-hud-coin {
    width: 22px;
    height: 22px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hud_coin.png");
}

.uc-bar-bg {
    height: 12px;
    padding: 2px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hud_bar_bg.png");
    -unity-slice-left: 22; -unity-slice-top: 12; -unity-slice-right: 22; -unity-slice-bottom: 12;
    -unity-slice-scale: 0.5px;
}

.uc-bar-fill {
    height: 100%;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hud_bar_fill.png");
    -unity-slice-left: 18; -unity-slice-top: 10; -unity-slice-right: 18; -unity-slice-bottom: 10;
    -unity-slice-scale: 0.5px;
    -unity-background-image-tint-color: #2E8B57; /* tint per metric: #2E8B57 / #F2C21B / #FF7A1A / #D9483B */
}

.uc-hotbar-slot {
    width: 60px;
    height: 60px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hotbar_slot.png");
    -unity-slice-left: 36; -unity-slice-top: 34; -unity-slice-right: 36; -unity-slice-bottom: 38;
    -unity-slice-scale: 0.5px;
}
.uc-hotbar-slot--selected {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hotbar_slot_selected.png");
}

.uc-crosshair {
    width: 26px;
    height: 26px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hud_crosshair.png");
}

.uc-prompt-key {
    min-width: 30px;
    height: 30px;
    padding: 0 8px 3px 8px;
    align-items: center;
    justify-content: center;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hud_prompt_key.png");
    -unity-slice-left: 26; -unity-slice-top: 24; -unity-slice-right: 26; -unity-slice-bottom: 30;
    -unity-slice-scale: 0.5px;
}

.uc-minibanner {
    min-height: 34px;
    padding: 6px 16px 8px 16px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/HUD/hud_minibanner.png");
    -unity-slice-left: 38; -unity-slice-top: 34; -unity-slice-right: 38; -unity-slice-bottom: 40;
    -unity-slice-scale: 0.5px;
    -unity-background-image-tint-color: #D9483B; /* #D9483B smog, #FF7A1A event, #B4562E district, #2E8B57 clean */
}
```

---

### 3.5 `Touch/` (Phone-First On-Screen Controls)

All Touch sprites are **not sliced** and scale cleanly with `-unity-background-scale-mode: scale-to-fit`. All `touch_btn_glyph_*.png` sprites are **pure white** with subtle shadow and can be tinted via `-unity-background-image-tint-color`.

| File | Pixel Size (@2x) | On-Screen (`1080×608`) | Notes |
| --- | --- | --- | --- |
| `joystick_base.png` | `288 × 288` | `124 × 124` – `144 × 144` | Translucent dark ring with crisp white outer hairline & directional chevrons. |
| `joystick_knob.png` | `128 × 128` | `52 × 52` – `62 × 62` | Frosted white circular thumb pad. |
| `touch_btn_base.png` | `160 × 160` | `56 × 56` – `74 × 74` | Translucent dark circular touch button base. |
| `touch_btn_base_pressed.png` | `160 × 160` | Same | Active/pressed Hi-Vis Orange glowing circular base. |
| `touch_btn_glyph_interact.png` | `128 × 128` | `32 × 32` – `40 × 40` | Open grabbing hand glyph (white, tintable). |
| `touch_btn_glyph_jump.png` | `128 × 128` | `32 × 32` – `40 × 40` | Upward jump arrow + arc glyph (white, tintable). |
| `touch_btn_glyph_sprint.png` | `128 × 128` | `32 × 32` – `40 × 40` | Double-chevron sprint glyph (white, tintable). |
| `touch_btn_glyph_bag.png` | `128 × 128` | `32 × 32` – `40 × 40` | Recycling bag / inventory glyph (white, tintable). |
| `touch_btn_glyph_throw.png` | `128 × 128` | `32 × 32` – `40 × 40` | Arcing throw trajectory glyph (white, tintable). |
| `touch_btn_glyph_crouch.png` | `128 × 128` | `32 × 32` – `40 × 40` | Downward crouch chevrons glyph (white, tintable). |
| `touch_btn_glyph_menu.png` | `128 × 128` | `28 × 28` – `36 × 36` | 3-bar hamburger menu glyph (white, tintable). |

```css
.uc-joystick-base {
    width: 132px; height: 132px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/joystick_base.png");
}
.uc-joystick-knob {
    width: 56px; height: 56px;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/joystick_knob.png");
}
.uc-touch-btn {
    width: 66px; height: 66px;
    align-items: center; justify-content: center;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_base.png");
}
.uc-touch-btn:active {
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_base_pressed.png");
}
.uc-touch-glyph {
    width: 36px; height: 36px;
    -unity-background-scale-mode: scale-to-fit;
}
.uc-touch-glyph--interact { background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_glyph_interact.png"); }
.uc-touch-glyph--jump     { background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_glyph_jump.png"); }
.uc-touch-glyph--sprint   { background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_glyph_sprint.png"); }
.uc-touch-glyph--bag      { background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_glyph_bag.png"); }
.uc-touch-glyph--throw    { background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_glyph_throw.png"); }
.uc-touch-glyph--crouch   { background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_glyph_crouch.png"); }
.uc-touch-glyph--menu     { background-image: url("project://database/Assets/UpcycleCity/Art/UI/Touch/touch_btn_glyph_menu.png"); }
```

---

### 3.6 `Backdrops/`

| File | Pixel Size | On-Screen (`1080×608`) | Slice | Notes |
| --- | --- | --- | --- | --- |
| `menu_vignette.png` | `1024 × 576` | Full screen `100% × 100%` | Not sliced | Transparent center with soft dark side/top/bottom vignette so text & cards read cleanly over the **live 3D menu camera**. Set `picking-mode: Ignore`! |
| `loading_screen.png` | `1920 × 1080` | Full screen `100% × 100%` | Not sliced | Illustrated Tunis Avenue Habib Bourguiba dusk-to-dawn skyline (Bab Bhar, Clock Tower, wind turbines) with dark lower third for loading progress bar & tips. Max size `2048`. |

```css
.uc-menu-vignette {
    position: absolute;
    left: 0; top: 0; right: 0; bottom: 0;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Backdrops/menu_vignette.png");
    -unity-background-scale-mode: stretch-to-fill;
}

.uc-loading-backdrop {
    position: absolute;
    left: 0; top: 0; right: 0; bottom: 0;
    background-image: url("project://database/Assets/UpcycleCity/Art/UI/Backdrops/loading_screen.png");
    -unity-background-scale-mode: scale-and-crop;
}
```

---

## 4. Upgraded Item & Stream Icons (`Assets/UpcycleCity/Art/Icons/`)

The following 6 icons were redone at higher scale and contrast for instant legibility at `64 px` on dark cards/hotbars (keys & paths unchanged; `Resources/IconLibrary.asset` rebuilt):

- `Gear/tool_litter_picker.png` — Chunky diagonal grabber with Hi-Vis Orange pistol trigger handle, thick brushed-steel shaft, teal collar & wide green rubber pincer pads.
- `Gear/tool_respirator.png` — Iconic 3/4-front half-face respirator mask with sculpted teal cup, central metallic exhale valve, dual Safety Yellow side filter cartridges & orange head strap.
- `Trash/glibettes_cone.png` — Rolled Tunisian newsprint snack cone overflowing with high-contrast black-and-white striped sunflower seeds.
- `Trash/crushed_bottle.png` — Bright faceted icy-cyan crushed PET bottle with vivid blue cap and crumpled label.
- `Trash/scrap_pipe.png` — Chunky metallic scrap pipe with warm rust bands, dark hollow near opening & bolted middle flange.
- `UI/stream_bulky.png` — Single clean white sofa/armchair silhouette on a glossy 3D beveled Rust (`#B4562E`) badge matching `stream_paper` and `stream_plastic`.
