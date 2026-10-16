// Ragdoll Sandbox - HUD built entirely in code with uGUI: canvas + scaler, EventSystem for the active input
// backend, crosshair + hover info, hotbar with procedural icons and tooltips, vitals panel with a 17-part body
// diagram, settings panel, help overlay, toasts, pause menu, hurt vignette and status indicators.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RagdollSandbox
{
    #region UIKit
    public static class UIKit
    {
        public static Font Font;
        public static readonly Color PanelColor = new Color(0.07f, 0.08f, 0.1f, 0.78f);
        public static readonly Color Accent = new Color(1f, 0.72f, 0.3f, 1f);
        public static readonly Color TextColor = new Color(0.92f, 0.93f, 0.95f, 1f);
        public static readonly Color Dim = new Color(0.65f, 0.68f, 0.72f, 1f);

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.pivot = pivot; rt.anchoredPosition = pos; rt.sizeDelta = size;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        public static Image Panel(Transform parent, string name, Color color, Sprite sprite = null)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite != null ? sprite : TextureFactory.RoundedRect;
            img.type = Image.Type.Sliced;
            img.color = color;
            return img;
        }

        public static Text Label(Transform parent, string text, int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal)
        {
            var rt = Rect("Text", parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = true;
            return t;
        }

        public static LayoutElement LE(GameObject go, float prefW, float prefH, float flexW = -1f, float minH = -1f)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            if (prefW >= 0f) le.preferredWidth = prefW;
            if (prefH >= 0f) le.preferredHeight = prefH;
            if (flexW >= 0f) le.flexibleWidth = flexW;
            if (minH >= 0f) le.minHeight = minH;
            return le;
        }

        public static VerticalLayoutGroup VLayout(GameObject go, int pad, float spacing, TextAnchor align = TextAnchor.UpperLeft)
        {
            var v = go.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(pad, pad, pad, pad);
            v.spacing = spacing;
            v.childAlignment = align;
            v.childControlWidth = true; v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            return v;
        }

        public static HorizontalLayoutGroup HLayout(GameObject go, int pad, float spacing, TextAnchor align = TextAnchor.MiddleLeft)
        {
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(pad, pad, pad, pad);
            h.spacing = spacing;
            h.childAlignment = align;
            h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            return h;
        }

        public static ContentSizeFitter Fit(GameObject go, bool h, bool v)
        {
            var f = go.AddComponent<ContentSizeFitter>();
            f.horizontalFit = h ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
            f.verticalFit = v ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
            return f;
        }

        public static Button Button(Transform parent, string text, Action onClick, float width = -1f, float height = 44f)
        {
            var img = Panel(parent, "Button", new Color(0.18f, 0.2f, 0.24f, 0.95f));
            var b = img.gameObject.AddComponent<Button>();
            var cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.2f, 1.1f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.colorMultiplier = 1.2f;
            b.colors = cb;
            b.targetGraphic = img;
            var label = Label(img.transform, text, 22, TextColor, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            if (onClick != null) b.onClick.AddListener(() => onClick());
            LE(img.gameObject, width, height, width < 0f ? 1f : -1f);
            return b;
        }

        public static Slider Slider(Transform parent, float min, float max, float value, Action<float> onChange, bool whole = false)
        {
            var root = Rect("Slider", parent);
            var s = root.gameObject.AddComponent<Slider>();
            var bg = Panel(root, "Background", new Color(0.2f, 0.22f, 0.26f, 1f), TextureFactory.RoundedSmall);
            var bgr = bg.rectTransform;
            bgr.anchorMin = new Vector2(0f, 0.35f); bgr.anchorMax = new Vector2(1f, 0.65f); bgr.offsetMin = Vector2.zero; bgr.offsetMax = Vector2.zero;
            var fillArea = Rect("Fill Area", root);
            fillArea.anchorMin = new Vector2(0f, 0.35f); fillArea.anchorMax = new Vector2(1f, 0.65f); fillArea.offsetMin = new Vector2(4f, 0f); fillArea.offsetMax = new Vector2(-8f, 0f);
            var fill = Panel(fillArea, "Fill", Accent, TextureFactory.RoundedSmall);
            fill.rectTransform.anchorMin = Vector2.zero; fill.rectTransform.anchorMax = new Vector2(0f, 1f); fill.rectTransform.sizeDelta = new Vector2(8f, 0f);
            var handleArea = Rect("Handle Slide Area", root);
            Stretch(handleArea);
            handleArea.offsetMin = new Vector2(8f, 0f); handleArea.offsetMax = new Vector2(-8f, 0f);
            var handle = Panel(handleArea, "Handle", Color.white, TextureFactory.Circle);
            handle.type = Image.Type.Simple;
            handle.rectTransform.sizeDelta = new Vector2(20f, 0f);
            handle.rectTransform.anchorMin = new Vector2(0f, 0f); handle.rectTransform.anchorMax = new Vector2(0f, 1f);
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.targetGraphic = handle;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = min; s.maxValue = max; s.wholeNumbers = whole;
            s.value = Mathf.Clamp(value, min, max);
            if (onChange != null) s.onValueChanged.AddListener(v => onChange(v));
            LE(root.gameObject, -1f, 26f, 1f);
            return s;
        }

        public static Toggle Toggle(Transform parent, bool value, Action<bool> onChange)
        {
            var root = Rect("Toggle", parent);
            var t = root.gameObject.AddComponent<Toggle>();
            var bg = Panel(root, "Background", new Color(0.2f, 0.22f, 0.26f, 1f), TextureFactory.RoundedSmall);
            bg.rectTransform.anchorMin = new Vector2(0f, 0.5f); bg.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            bg.rectTransform.pivot = new Vector2(0f, 0.5f); bg.rectTransform.sizeDelta = new Vector2(26f, 26f); bg.rectTransform.anchoredPosition = Vector2.zero;
            var check = Panel(bg.transform, "Checkmark", Accent, TextureFactory.RoundedSmall);
            check.rectTransform.anchorMin = new Vector2(0.2f, 0.2f); check.rectTransform.anchorMax = new Vector2(0.8f, 0.8f);
            check.rectTransform.offsetMin = Vector2.zero; check.rectTransform.offsetMax = Vector2.zero;
            t.targetGraphic = bg;
            t.graphic = check;
            t.isOn = value;
            if (onChange != null) t.onValueChanged.AddListener(v => onChange(v));
            LE(root.gameObject, 30f, 28f);
            return t;
        }
    }

    /// <summary>Shows a tooltip while the pointer hovers a hotbar slot.</summary>
    public class TooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public string text;
        public void OnPointerEnter(PointerEventData e) { if (Sandbox.Hud != null) Sandbox.Hud.ShowTooltip(text, (RectTransform)transform); }
        public void OnPointerExit(PointerEventData e) { if (Sandbox.Hud != null) Sandbox.Hud.ShowTooltip(null, null); }
    }
    #endregion

    #region Hud
    public class Hud
    {
        Canvas canvas;
        RectTransform root;
        Text hoverText, subText, fpsText, statusText, crossDot;
        Image[] crossLines = new Image[4];
        Image vignette;
        Image[] slotBg = new Image[10];
        int shownTool = -1;
        // vitals
        RectTransform vitalsPanel;
        Text vTitle, vBlood, vHeart, vCons, vPain, vOxy, vState;
        Image bBlood, bCons, bPain, bOxy, heartIcon;
        Image[] diagram = new Image[PartInfo.Count];
        // settings
        RectTransform settingsPanel;
        public bool SettingsVisible { get; private set; }
        readonly List<Action> refreshers = new List<Action>();
        // help / pause / tooltip
        RectTransform helpPanel, pausePanel, tooltip;
        Text tooltipText;
        public bool HelpVisible { get; private set; }
        bool hidden;
        // toasts
        RectTransform toastRoot;
        class Toast { public Image bg; public Text text; public float until; public bool active; }
        readonly Toast[] toasts = new Toast[6];
        int nextToast;
        float vitalsTimer;
        int lookPreset;

        public void Build(Transform parent)
        {
            UIKit.Font = Compat.BuiltinFont();
            var go = new GameObject("HUD");
            go.transform.SetParent(parent, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            root = (RectTransform)go.transform;
            EnsureEventSystem(parent);

            BuildVignette();
            BuildCrosshair();
            BuildHotbar();
            BuildVitals();
            BuildSettings();
            BuildHelp();
            BuildPause();
            BuildToasts();
            BuildTooltip();
            BuildStatus();
            SandboxEvents.Toast += OnToast;
        }

        static void EnsureEventSystem(Transform parent)
        {
            if (EventSystem.current != null && EventSystem.current.isActiveAndEnabled) return;
            var es = new GameObject("EventSystem");
            es.transform.SetParent(parent, false);
            es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            var m = es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            try { if (m.actionsAsset == null) m.AssignDefaultActions(); } catch (Exception) { }
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }

        #region Build
        void BuildVignette()
        {
            var rt = UIKit.Rect("Vignette", root);
            UIKit.Stretch(rt);
            vignette = rt.gameObject.AddComponent<Image>();
            vignette.sprite = TextureFactory.Vignette;
            vignette.color = new Color(0.6f, 0f, 0f, 0f);
            vignette.raycastTarget = false;
        }

        void BuildCrosshair()
        {
            var c = UIKit.Rect("Crosshair", root);
            UIKit.Anchor(c, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40f, 40f));
            for (int i = 0; i < 4; i++)
            {
                var l = UIKit.Rect("Line", c);
                var img = l.gameObject.AddComponent<Image>();
                img.sprite = TextureFactory.WhiteSprite;
                img.color = new Color(1f, 1f, 1f, 0.85f);
                img.raycastTarget = false;
                bool hor = i < 2;
                l.sizeDelta = hor ? new Vector2(9f, 2f) : new Vector2(2f, 9f);
                l.anchoredPosition = hor ? new Vector2(i == 0 ? -10f : 10f, 0f) : new Vector2(0f, i == 2 ? -10f : 10f);
                crossLines[i] = img;
            }
            var dot = UIKit.Label(c, "•", 18, Color.white, TextAnchor.MiddleCenter);
            UIKit.Stretch(dot.rectTransform);
            crossDot = dot;
            hoverText = UIKit.Label(root, "", 22, Color.white, TextAnchor.UpperCenter);
            UIKit.Anchor(hoverText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(900f, 30f));
            var sh = hoverText.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.8f);
        }

        void BuildHotbar()
        {
            var bar = UIKit.Panel(root, "Hotbar", UIKit.PanelColor);
            UIKit.Anchor(bar.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), Vector2.zero);
            UIKit.HLayout(bar.gameObject, 10, 8, TextAnchor.MiddleCenter);
            UIKit.Fit(bar.gameObject, true, true);
            for (int i = 0; i < 10; i++)
            {
                var slot = UIKit.Panel(bar.transform, "Slot" + i, new Color(0.16f, 0.17f, 0.2f, 0.95f));
                UIKit.LE(slot.gameObject, 76f, 76f);
                slotBg[i] = slot;
                var icon = UIKit.Rect("Icon", slot.transform);
                UIKit.Anchor(icon, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 4f), new Vector2(46f, 46f));
                var iimg = icon.gameObject.AddComponent<Image>();
                iimg.sprite = TextureFactory.ToolIcons[i];
                iimg.color = new Color(0.95f, 0.95f, 0.95f, 1f);
                iimg.raycastTarget = false;
                var key = UIKit.Label(slot.transform, ((i + 1) % 10).ToString(), 16, UIKit.Dim, TextAnchor.UpperLeft, FontStyle.Bold);
                UIKit.Anchor(key.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 1f), new Vector2(6f, -2f), new Vector2(-6f, -2f));
                key.rectTransform.offsetMin = new Vector2(6f, 0f); key.rectTransform.offsetMax = new Vector2(0f, -3f);
                var tt = slot.gameObject.AddComponent<TooltipTrigger>();
                tt.text = "<b>" + ToolSystem.ToolNames[i] + "</b>\n" + ToolSystem.ToolHelp[i];
                int idx = i;
                var btn = slot.gameObject.AddComponent<Button>();
                btn.targetGraphic = slot;
                btn.onClick.AddListener(() => { if (Sandbox.Tools != null) Sandbox.Tools.Select((ToolId)idx); });
            }
            subText = UIKit.Label(root, "", 20, UIKit.Accent, TextAnchor.LowerCenter, FontStyle.Bold);
            UIKit.Anchor(subText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 122f), new Vector2(900f, 30f));
            subText.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
        }

        Image Bar(Transform parent, string label, Color color, out Text value)
        {
            var row = UIKit.Rect("Row", parent);
            UIKit.HLayout(row.gameObject, 0, 8);
            UIKit.LE(row.gameObject, -1f, 24f);
            var l = UIKit.Label(row, label, 18, UIKit.Dim);
            UIKit.LE(l.gameObject, 118f, 24f);
            var back = UIKit.Panel(row, "Back", new Color(0.18f, 0.19f, 0.22f, 1f), TextureFactory.RoundedSmall);
            UIKit.LE(back.gameObject, 170f, 16f);
            var fill = UIKit.Panel(back.transform, "Fill", color, TextureFactory.RoundedSmall);
            fill.type = Image.Type.Filled;
            fill.sprite = TextureFactory.WhiteSprite;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillAmount = 1f;
            UIKit.Stretch(fill.rectTransform);
            value = UIKit.Label(row, "", 18, UIKit.TextColor);
            UIKit.LE(value.gameObject, 92f, 24f);
            return fill;
        }

        void BuildVitals()
        {
            var p = UIKit.Panel(root, "Vitals", UIKit.PanelColor);
            vitalsPanel = p.rectTransform;
            UIKit.Anchor(vitalsPanel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(18f, -18f), Vector2.zero);
            UIKit.VLayout(p.gameObject, 14, 6);
            UIKit.Fit(p.gameObject, true, true);
            vTitle = UIKit.Label(p.transform, "Ragdoll", 22, UIKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.LE(vTitle.gameObject, 420f, 28f);
            bBlood = Bar(p.transform, "Blood", new Color(0.8f, 0.1f, 0.1f), out vBlood);
            var hr = UIKit.Rect("HeartRow", p.transform);
            UIKit.HLayout(hr.gameObject, 0, 8);
            UIKit.LE(hr.gameObject, -1f, 24f);
            var hl = UIKit.Label(hr, "Heart", 18, UIKit.Dim);
            UIKit.LE(hl.gameObject, 118f, 24f);
            var hi = UIKit.Rect("HeartIcon", hr);
            heartIcon = hi.gameObject.AddComponent<Image>();
            heartIcon.sprite = TextureFactory.Circle;
            heartIcon.color = new Color(0.9f, 0.15f, 0.2f);
            heartIcon.raycastTarget = false;
            UIKit.LE(hi.gameObject, 18f, 18f);
            vHeart = UIKit.Label(hr, "", 18, UIKit.TextColor);
            UIKit.LE(vHeart.gameObject, 240f, 24f);
            bCons = Bar(p.transform, "Conscious", new Color(0.35f, 0.7f, 1f), out vCons);
            bOxy = Bar(p.transform, "Oxygen", new Color(0.5f, 0.85f, 0.95f), out vOxy);
            bPain = Bar(p.transform, "Pain", new Color(1f, 0.6f, 0.2f), out vPain);
            vState = UIKit.Label(p.transform, "", 18, UIKit.TextColor);
            UIKit.LE(vState.gameObject, 420f, 46f);
            // body diagram (T-pose, like the reference mannequin)
            var diag = UIKit.Rect("BodyDiagram", p.transform);
            UIKit.LE(diag.gameObject, 420f, 250f);
            float scale = 125f;
            Vector2 offset = new Vector2(210f, 12f);
            for (int i = 0; i < PartInfo.Count; i++)
            {
                Vector3 a = PartInfo.Origin[i], b = PartInfo.Tip[i];
                PartId id = (PartId)i;
                if (id == PartId.Pelvis) { a = new Vector3(0f, 0.86f, 0f); b = new Vector3(0f, 1.03f, 0f); }
                if (id == PartId.FootL || id == PartId.FootR) { a = new Vector3(a.x, 0.0f, 0f); b = new Vector3(a.x + PartInfo.SideSign(id) * 0.1f, 0.08f, 0f); }
                Vector3 mid = (a + b) * 0.5f;
                Vector2 extent = new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
                float w = PartInfo.ColSize[i].x * 2f;
                if (id == PartId.Pelvis || id == PartId.Abdomen || id == PartId.Chest) w = PartInfo.ColSize[i].y + PartInfo.ColSize[i].x * 0.5f;
                if (id == PartId.Head) w = 0.19f;
                Vector2 size = new Vector2(Mathf.Max(extent.x, w), Mathf.Max(extent.y, w)) * scale;
                if (PartInfo.IsArm(id)) size.y = Mathf.Max(10f, PartInfo.ColSize[i].x * 2f * scale);
                if (id == PartId.HandL || id == PartId.HandR) size = new Vector2(0.18f * scale, 0.08f * scale);
                if (id == PartId.Head) size = new Vector2(0.17f * scale, 0.21f * scale);
                var r = UIKit.Rect(PartInfo.Names[i], diag);
                r.anchorMin = r.anchorMax = Vector2.zero;
                r.pivot = new Vector2(0.5f, 0.5f);
                r.anchoredPosition = offset + new Vector2(mid.x, mid.y) * scale;
                r.sizeDelta = size;
                var img = r.gameObject.AddComponent<Image>();
                img.sprite = TextureFactory.RoundedSmall;
                img.type = Image.Type.Sliced;
                img.color = new Color(0.4f, 0.85f, 0.45f);
                img.raycastTarget = false;
                diagram[i] = img;
            }
        }

        void Row(Transform parent, string label, Action<Transform> control)
        {
            var row = UIKit.Rect("Row", parent);
            UIKit.HLayout(row.gameObject, 0, 10);
            UIKit.LE(row.gameObject, -1f, 30f);
            var l = UIKit.Label(row, label, 18, UIKit.TextColor);
            UIKit.LE(l.gameObject, 190f, 28f);
            control(row);
        }

        void Header(Transform parent, string text)
        {
            var h = UIKit.Label(parent, text, 20, UIKit.Accent, TextAnchor.LowerLeft, FontStyle.Bold);
            UIKit.LE(h.gameObject, -1f, 30f);
        }

        void SliderRow(Transform parent, string label, float min, float max, Func<float> get, Action<float> set, string fmt = "0.00")
        {
            Row(parent, label, row =>
            {
                Text val = null;
                var s = UIKit.Slider(row, min, max, get(), v => { set(v); if (val != null) val.text = v.ToString(fmt); });
                UIKit.LE(s.gameObject, 210f, 26f, 1f);
                val = UIKit.Label(row, get().ToString(fmt), 17, UIKit.Dim, TextAnchor.MiddleRight);
                UIKit.LE(val.gameObject, 62f, 26f);
                refreshers.Add(() => { if (Mathf.Abs(s.value - get()) > 1e-4f) s.SetValueWithoutNotify(get()); val.text = get().ToString(fmt); });
            });
        }

        void ToggleRow(Transform parent, string label, Func<bool> get, Action<bool> set)
        {
            Row(parent, label, row =>
            {
                var t = UIKit.Toggle(row, get(), set);
                refreshers.Add(() => { if (t.isOn != get()) t.SetIsOnWithoutNotify(get()); });
            });
        }

        void CycleRow(Transform parent, string label, Func<string> get, Action next)
        {
            Row(parent, label, row =>
            {
                Text txt = null;
                var b = UIKit.Button(row, get(), () => { next(); if (txt != null) txt.text = get(); }, 230f, 30f);
                txt = b.GetComponentInChildren<Text>();
                txt.fontSize = 18;
                refreshers.Add(() => { txt.text = get(); });
            });
        }

        void BuildSettings()
        {
            var b = Sandbox.Boot;
            var p = UIKit.Panel(root, "Settings", UIKit.PanelColor);
            settingsPanel = p.rectTransform;
            UIKit.Anchor(settingsPanel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-18f, -18f), new Vector2(520f, 760f));
            var title = UIKit.Label(p.transform, "Settings  (Tab to close)", 22, UIKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(-28f, 34f));
            // scroll view
            var view = UIKit.Rect("Viewport", p.transform);
            view.anchorMin = Vector2.zero; view.anchorMax = Vector2.one; view.offsetMin = new Vector2(12f, 12f); view.offsetMax = new Vector2(-12f, -46f);
            var vimg = view.gameObject.AddComponent<Image>();
            vimg.color = new Color(1f, 1f, 1f, 0.01f);
            view.gameObject.AddComponent<RectMask2D>();
            var content = UIKit.Rect("Content", view);
            content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
            UIKit.VLayout(content.gameObject, 4, 4);
            UIKit.Fit(content.gameObject, false, true);
            var sr = p.gameObject.AddComponent<ScrollRect>();
            sr.viewport = view; sr.content = content; sr.horizontal = false; sr.vertical = true; sr.scrollSensitivity = 30f; sr.movementType = ScrollRect.MovementType.Clamped;
            Transform c = content;

            Header(c, "Gore & blood");
            CycleRow(c, "Gore level", () => b.dismember.gore.ToString(), () => { b.dismember.gore = (GoreLevel)(((int)b.dismember.gore + 1) % 3); for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].RefreshBreakForces(); });
            SliderRow(c, "Blood amount", 0f, 2.5f, () => b.blood.bloodAmount, v => b.blood.bloodAmount = v);
            ToggleRow(c, "Paint blood on bodies", () => b.blood.paintOnBodies, v => b.blood.paintOnBodies = v);
            Header(c, "Simulation");
            SliderRow(c, "Slow-motion scale", 0.02f, 1f, () => b.physics.slowMotionScale, v => b.physics.slowMotionScale = v);
            CycleRow(c, "Gravity", () => b.physics.gravity.ToString(), () => b.physics.gravity = (GravityMode)(((int)b.physics.gravity + 1) % 3));
            CycleRow(c, "Quality preset", () => b.graphics.preset.ToString(), () => b.graphics.preset = (QualityPreset)(((int)b.graphics.preset + 1) % 4));
            SliderRow(c, "Physics timestep", 0.005f, 0.02f, () => b.physics.fixedTimestep, v => b.physics.fixedTimestep = v, "0.0000");
            ToggleRow(c, "Auto reduce effects", () => b.graphics.autoReduce, v => b.graphics.autoReduce = v);
            Header(c, "Ragdoll brain & muscles");
            ToggleRow(c, "Override active level", () => b.muscles.overrideActiveLevel, v => b.muscles.overrideActiveLevel = v);
            SliderRow(c, "Active level", 0f, 1f, () => b.muscles.activeLevel, v => b.muscles.activeLevel = v);
            SliderRow(c, "Muscle strength", 0f, 3f, () => b.muscles.strength, v => b.muscles.strength = v);
            SliderRow(c, "Balance assist (Nm)", 0f, 600f, () => b.balance.assistTorque, v => b.balance.assistTorque = v, "0");
            SliderRow(c, "Will to live", 0f, 1f, () => b.vitals.willToLive, v => b.vitals.willToLive = v);
            ToggleRow(c, "Reactions", () => b.general.reactions, v => b.general.reactions = v);
            ToggleRow(c, "Wander (N)", () => b.general.wander, v => b.general.wander = v);
            ToggleRow(c, "Look at player", () => b.general.gazeAtPlayer, v => b.general.gazeAtPlayer = v);
            Header(c, "Look (new ragdolls)");
            CycleRow(c, "Preset", () => LookName(lookPreset), () => { lookPreset = (lookPreset + 1) % 5; ApplyLook(lookPreset); });
            ToggleRow(c, "Shirt", () => b.character.shirt, v => b.character.shirt = v);
            ToggleRow(c, "Shorts", () => b.character.shorts, v => b.character.shorts = v);
            ToggleRow(c, "Shoes", () => b.character.shoes, v => b.character.shoes = v);
            Header(c, "World & camera");
            ToggleRow(c, "Day/night cycle", () => b.graphics.dayNightCycle, v => b.graphics.dayNightCycle = v);
            SliderRow(c, "Time of day", 0f, 24f, () => b.graphics.timeOfDay, v => b.graphics.timeOfDay = v, "0.0");
            ToggleRow(c, "Fog", () => b.graphics.fog, v => b.graphics.fog = v);
            ToggleRow(c, "Kill-cam", () => b.cameraSettings.killCam, v => b.cameraSettings.killCam = v);
            SliderRow(c, "Field of view", 50f, 110f, () => b.player.fieldOfView, v => b.player.fieldOfView = v, "0");
            SliderRow(c, "Mouse sensitivity", 0.1f, 5f, () => b.player.mouseSensitivity, v => b.player.mouseSensitivity = v);
            ToggleRow(c, "Invert Y", () => b.player.invertY, v => b.player.invertY = v);
            ToggleRow(c, "Noclip (fly)", () => b.player.noclip, v => b.player.noclip = v);
            SliderRow(c, "Master volume", 0f, 1f, () => b.audioSettings.master, v => b.audioSettings.master = v);
            settingsPanel.gameObject.SetActive(false);
        }

        static string LookName(int i)
        {
            switch (i) { case 0: return "Mannequin"; case 1: return "Light skin"; case 2: return "Tan skin"; case 3: return "Dark skin"; default: return "Crash dummy"; }
        }

        void ApplyLook(int i)
        {
            var ch = Sandbox.Boot.character;
            switch (i)
            {
                case 0: ch.skinColor = new Color(0.83f, 0.83f, 0.84f); ch.shortsColor = new Color(0.32f, 0.33f, 0.36f); break;
                case 1: ch.skinColor = new Color(0.93f, 0.78f, 0.68f); ch.shortsColor = new Color(0.2f, 0.28f, 0.45f); break;
                case 2: ch.skinColor = new Color(0.76f, 0.56f, 0.42f); ch.shortsColor = new Color(0.25f, 0.25f, 0.27f); break;
                case 3: ch.skinColor = new Color(0.42f, 0.29f, 0.22f); ch.shortsColor = new Color(0.5f, 0.15f, 0.12f); break;
                default: ch.skinColor = new Color(0.95f, 0.78f, 0.25f); ch.shortsColor = new Color(0.12f, 0.12f, 0.12f); break;
            }
            SandboxEvents.RaiseToast("New ragdolls will use: " + LookName(i) + " (spawn with 0, or R to reset)", Color.white);
        }

        void BuildHelp()
        {
            var p = UIKit.Panel(root, "Help", new Color(0.05f, 0.06f, 0.08f, 0.92f));
            helpPanel = p.rectTransform;
            UIKit.Anchor(helpPanel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1240f, 720f));
            var t = UIKit.Label(p.transform, "", 19, UIKit.TextColor, TextAnchor.UpperLeft);
            UIKit.Stretch(t.rectTransform);
            t.rectTransform.offsetMin = new Vector2(30f, 24f); t.rectTransform.offsetMax = new Vector2(-640f, -24f);
            t.text =
                "<size=28><color=#FFB84D><b>RAGDOLL SANDBOX</b></color></size>\n\n" +
                "<b>You (first person)</b>\n" +
                "WASD move   Mouse look   Space jump   Shift sprint   C crouch\n" +
                "Tab  settings + free cursor      Esc  pause menu\n\n" +
                "<b>Tools</b>  1-9, 0 or mouse wheel  (Q = sub-mode)\n" +
                "1 Grab   2 Hammer   3 Blade   4 Pistol   5 Shotgun\n" +
                "6 Explosives   7 Props   8 Shock   9 Heal   0 Spawn ragdoll\n" +
                "LMB primary   RMB secondary   Wheel: grab distance\n" +
                "Shift while grabbing: strong pull (can rip limbs off)\n" +
                "Ctrl + click ground: order the ragdoll to walk there\n";
            var t2 = UIKit.Label(p.transform, "", 19, UIKit.TextColor, TextAnchor.UpperLeft);
            UIKit.Stretch(t2.rectTransform);
            t2.rectTransform.offsetMin = new Vector2(640f, 24f); t2.rectTransform.offsetMax = new Vector2(-30f, -24f);
            t2.text =
                "\n\n<b>World</b>\n" +
                "R reset ragdolls   Shift+R reset whole scene\n" +
                "T slow motion   G gravity (Earth / Moon / Zero)\n" +
                "V possess the ragdoll   F follow cam (Ctrl+wheel zoom)\n" +
                "X anatomy X-ray   B detonate C4   N wander on/off\n" +
                "Z (hold) rewind 5 s   H help   F1 hide HUD\n\n" +
                "<b>Possessed ragdoll</b>\n" +
                "WASD walk (camera relative)   Shift run   Space jump\n" +
                "C crouch   LMB / RMB reach and grab with left / right hand\n" +
                "Hands latch onto whatever they touch (ledges too)\n\n" +
                "<b>Zones</b>: plaza (centre), obstacle course (north), stairs\n" +
                "& ramps (east), demolition (west), hazard pit (south),\n" +
                "tower & cliff (north-east), hills (corners).\n\n" +
                "<color=#9AA3AD>Press H to close</color>";
            helpPanel.gameObject.SetActive(false);
        }

        void BuildPause()
        {
            var p = UIKit.Panel(root, "Pause", new Color(0.05f, 0.06f, 0.08f, 0.94f));
            pausePanel = p.rectTransform;
            UIKit.Anchor(pausePanel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            UIKit.VLayout(p.gameObject, 26, 12, TextAnchor.UpperCenter);
            UIKit.Fit(p.gameObject, true, true);
            var t = UIKit.Label(p.transform, "PAUSED", 34, UIKit.Accent, TextAnchor.MiddleCenter, FontStyle.Bold);
            UIKit.LE(t.gameObject, 360f, 50f);
            UIKit.Button(p.transform, "Resume", () => Sandbox.Boot.SetPaused(false), 360f);
            UIKit.Button(p.transform, "Reset ragdolls", () => { Sandbox.Boot.SetPaused(false); Sandbox.Boot.ResetSandbox(false); }, 360f);
            UIKit.Button(p.transform, "Reset whole scene", () => { Sandbox.Boot.SetPaused(false); Sandbox.Boot.ResetSandbox(true); }, 360f);
            UIKit.Button(p.transform, "Spawn another ragdoll", () =>
            {
                Sandbox.Boot.SetPaused(false);
                Vector3 p0 = Sandbox.World.RagdollSpawn + new Vector3(UnityEngine.Random.Range(-3f, 3f), 0f, UnityEngine.Random.Range(-1f, 2f));
                Sandbox.Boot.SpawnRagdoll(p0, Sandbox.World.RagdollSpawnYaw);
            }, 360f);
            UIKit.Button(p.transform, "Settings", () => { Sandbox.Boot.SetPaused(false); if (!SettingsVisible) ToggleSettings(); }, 360f);
            UIKit.Button(p.transform, "Help", () => ShowHelp(true), 360f);
            UIKit.Button(p.transform, "Quit", Quit, 360f);
            pausePanel.gameObject.SetActive(false);
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void BuildToasts()
        {
            toastRoot = UIKit.Rect("Toasts", root);
            UIKit.Anchor(toastRoot, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(760f, 0f));
            UIKit.VLayout(toastRoot.gameObject, 0, 6, TextAnchor.UpperCenter);
            UIKit.Fit(toastRoot.gameObject, false, true);
            for (int i = 0; i < toasts.Length; i++)
            {
                var bg = UIKit.Panel(toastRoot, "Toast", new Color(0.06f, 0.07f, 0.09f, 0.8f));
                UIKit.HLayout(bg.gameObject, 10, 0, TextAnchor.MiddleCenter);
                var tx = UIKit.Label(bg.transform, "", 21, Color.white, TextAnchor.MiddleCenter);
                UIKit.LE(tx.gameObject, 720f, 30f);
                bg.gameObject.SetActive(false);
                toasts[i] = new Toast { bg = bg, text = tx };
            }
        }

        void BuildTooltip()
        {
            var p = UIKit.Panel(root, "Tooltip", new Color(0.04f, 0.05f, 0.06f, 0.95f));
            tooltip = p.rectTransform;
            tooltip.pivot = new Vector2(0.5f, 0f);
            UIKit.VLayout(p.gameObject, 10, 0);
            UIKit.Fit(p.gameObject, true, true);
            tooltipText = UIKit.Label(p.transform, "", 18, UIKit.TextColor);
            UIKit.LE(tooltipText.gameObject, 380f, -1f);
            p.raycastTarget = false;
            tooltip.gameObject.SetActive(false);
        }

        void BuildStatus()
        {
            fpsText = UIKit.Label(root, "", 16, UIKit.Dim, TextAnchor.LowerRight);
            UIKit.Anchor(fpsText.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-16f, 12f), new Vector2(520f, 60f));
            statusText = UIKit.Label(root, "", 26, UIKit.Accent, TextAnchor.UpperCenter, FontStyle.Bold);
            UIKit.Anchor(statusText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -250f), new Vector2(600f, 40f));
            statusText.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
        }
        #endregion

        #region Runtime
        public void SetHover(string text, Color c)
        {
            if (hoverText == null) return;
            if (hoverText.text != text) hoverText.text = text;
            hoverText.color = c;
            Color cc = string.IsNullOrEmpty(text) ? new Color(1f, 1f, 1f, 0.85f) : c;
            for (int i = 0; i < 4; i++) crossLines[i].color = cc;
        }

        public void SetTool(int index, string sub)
        {
            if (subText != null && subText.text != sub) subText.text = sub;
            if (index == shownTool) return;
            shownTool = index;
            for (int i = 0; i < slotBg.Length; i++) slotBg[i].color = i == index ? new Color(0.95f, 0.62f, 0.2f, 0.95f) : new Color(0.16f, 0.17f, 0.2f, 0.95f);
        }

        public void ShowTooltip(string text, RectTransform anchorRt)
        {
            if (tooltip == null) return;
            if (string.IsNullOrEmpty(text) || anchorRt == null) { tooltip.gameObject.SetActive(false); return; }
            tooltipText.text = text;
            tooltip.gameObject.SetActive(true);
            Vector3 world = anchorRt.TransformPoint(new Vector3(0f, anchorRt.rect.height * 0.5f + 12f, 0f));
            tooltip.position = world;
        }

        void OnToast(string text, Color c)
        {
            var t = toasts[nextToast];
            nextToast = (nextToast + 1) % toasts.Length;
            t.text.text = text;
            t.text.color = c;
            t.until = Time.unscaledTime + 3.2f;
            t.active = true;
            t.bg.gameObject.SetActive(true);
            t.bg.transform.SetAsLastSibling();
        }

        public void ToggleSettings()
        {
            SettingsVisible = !SettingsVisible;
            settingsPanel.gameObject.SetActive(SettingsVisible);
            if (SettingsVisible) for (int i = 0; i < refreshers.Count; i++) refreshers[i]();
            CursorControl.SetLocked(!SettingsVisible && !TimeControl.Paused);
        }

        public void ShowHelp(bool on)
        {
            HelpVisible = on;
            helpPanel.gameObject.SetActive(on);
            if (on) helpPanel.SetAsLastSibling();
        }

        public void ShowPause(bool on)
        {
            pausePanel.gameObject.SetActive(on);
            if (on) pausePanel.SetAsLastSibling();
        }

        public void ToggleHidden()
        {
            hidden = !hidden;
            canvas.enabled = !hidden;
        }

        public void Tick()
        {
            float now = Time.unscaledTime;
            InputBridge.GameplayBlocked = TimeControl.Paused;
            for (int i = 0; i < toasts.Length; i++)
            {
                var t = toasts[i];
                if (!t.active) continue;
                float left = t.until - now;
                if (left <= 0f) { t.active = false; t.bg.gameObject.SetActive(false); continue; }
                float a = Mathf.Clamp01(left / 0.5f);
                Color c = t.text.color; c.a = a; t.text.color = c;
                Color b = t.bg.color; b.a = 0.8f * a; t.bg.color = b;
            }
            vitalsTimer -= Time.unscaledDeltaTime;
            if (vitalsTimer <= 0f) { vitalsTimer = 0.1f; UpdateVitals(); }
            if (SettingsVisible && Time.frameCount % 15 == 0) for (int i = 0; i < refreshers.Count; i++) refreshers[i]();
        }

        public void LateTick()
        {
            // crosshair visible only in first person / follow
            bool cross = Sandbox.Cam != null && Sandbox.Cam.Mode != CameraMode.KillCam;
            crossDot.enabled = cross;
            for (int i = 0; i < 4; i++) crossLines[i].enabled = cross;
            // status
            string s = "";
            if (Sandbox.Rewind != null && Sandbox.Rewind.Rewinding) s = "<< REWIND";
            else if (TimeControl.SlowMotion) s = "SLOW MOTION";
            if (Sandbox.Cam != null && Sandbox.Cam.Mode == CameraMode.KillCam) s = "KILL CAM";
            if (Sandbox.XRay) s += (s.Length > 0 ? "   " : "") + "X-RAY";
            if (statusText.text != s) statusText.text = s;
            // vignette when the possessed body is hurt
            float hurt = 0f;
            if (Sandbox.Possessed != null)
            {
                var v = Sandbox.Possessed.Vitals;
                hurt = Mathf.Clamp01((1f - v.BloodNorm) * 1.1f + v.Pain * 0.7f + (v.Dead ? 1f : 0f));
                hurt *= 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * Mathf.Max(1f, v.HeartRate) / 60f * Mathf.PI * 2f);
            }
            var vc = vignette.color;
            vc.a = Mathf.MoveTowards(vc.a, hurt * 0.85f, Time.unscaledDeltaTime * 2f);
            vignette.color = vc;
        }

        void UpdateVitals()
        {
            var q = Sandbox.Boot.Quality;
            fpsText.text = Mathf.RoundToInt(q != null ? q.Fps : 0f) + " fps   ragdolls " + Sandbox.Ragdolls.Count + "   decals " + (Sandbox.Decals != null ? Sandbox.Decals.Count : 0) +
                           "   puddles " + (Sandbox.Blood != null ? Sandbox.Blood.PuddleCount : 0) + "   C4 " + (Sandbox.Explosions != null ? Sandbox.Explosions.CountC4() : 0);
            var b = Sandbox.Focus;
            if (b == null || b.Vitals == null)
            {
                vTitle.text = "No ragdoll  (0 to spawn)";
                return;
            }
            var v = b.Vitals;
            string state = v.Dead ? "<color=#FF6B5E>DEAD</color> (" + v.CauseOfDeath + ")" : v.Unconscious ? "<color=#FFC24D>Unconscious</color>" : "<color=#7DFF8A>Conscious</color>";
            vTitle.text = "Ragdoll " + (b.Index + 1) + (b.Possessed ? " (possessed)" : "") + "   " + state;
            bBlood.fillAmount = v.BloodFraction;
            vBlood.text = v.BloodVolume.ToString("0.00") + " L";
            vHeart.text = Mathf.RoundToInt(v.HeartRate) + " bpm" + (v.CardiacArrest ? "  <color=#FF6B5E>ARREST</color>" : "") + "   " + Mathf.RoundToInt(v.BreathRate) + " br/min";
            float beat = v.HeartRate > 1f ? Mathf.Exp(-Mathf.Pow(v.HeartPhase * 6f, 2f)) : 0f;
            heartIcon.rectTransform.localScale = Vector3.one * (0.8f + beat * 0.5f);
            bCons.fillAmount = v.Consciousness; vCons.text = Mathf.RoundToInt(v.Consciousness * 100f) + "%";
            bOxy.fillAmount = v.BrainOxygen; vOxy.text = Mathf.RoundToInt(v.BrainOxygen * 100f) + "%";
            bPain.fillAmount = v.Pain; vPain.text = Mathf.RoundToInt(v.Pain * 100f) + "%";
            float bleed = (v.ExternalBleedRate + v.InternalBleedRate) * 60f;
            vState.text = "State: " + b.Brain.State + "   Bleeding: " + bleed.ToString("0.00") + " L/min" + (v.InternalBleedRate > 0.001f ? " (internal)" : "") +
                          "\nAdrenaline " + Mathf.RoundToInt(v.Adrenaline * 100f) + "%   Shock " + Mathf.RoundToInt(v.Shock * 100f) + "%   Wounds " + b.Wounds.ActiveCount;
            for (int i = 0; i < PartInfo.Count; i++)
            {
                var p = b.Parts[i];
                Color c;
                if (p == null || !p.attached) c = new Color(0.12f, 0.12f, 0.13f, 0.5f);
                else
                {
                    float h = p.HealthFraction;
                    c = h > 0.5f ? Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(0.35f, 0.85f, 0.42f), (h - 0.5f) * 2f) :
                        h > 0.15f ? Color.Lerp(new Color(0.9f, 0.15f, 0.12f), new Color(1f, 0.85f, 0.2f), (h - 0.15f) / 0.35f) :
                        Color.Lerp(new Color(0.08f, 0.02f, 0.02f), new Color(0.9f, 0.15f, 0.12f), h / 0.15f);
                    if (p.fractured) c = Color.Lerp(c, new Color(1f, 0.55f, 0.1f), 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f));
                    if (p.paralyzed) c *= 0.6f;
                }
                diagram[i].color = c;
            }
        }

        #endregion
    }
    #endregion
}
