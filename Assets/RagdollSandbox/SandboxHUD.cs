// Ragdoll Sandbox - the whole UI, built in code with uGUI (Canvas + CanvasScaler 1920x1080 match 0.5, an
// EventSystem for whichever input backend is active): animated main menu (offline / host / join / settings /
// quit), pause menu, hotbar with procedural icons, key labels, animated highlight, tooltips and cooldowns,
// vitals panel with bars, status chips and a bone diagram (fractures and missing parts), collapsible tabbed
// settings, toasts, kill feed, chat, player list, per-tool crosshair, hover info, help and live counters.
// Animations and counters are allocation free (cached number strings, no per-frame string building).
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
        public static readonly Color PanelColor = new Color(0.07f, 0.08f, 0.1f, 0.82f);
        public static readonly Color Accent = new Color(1f, 0.72f, 0.3f, 1f);
        public static readonly Color TextColor = new Color(0.92f, 0.93f, 0.95f, 1f);
        public static readonly Color Dim = new Color(0.65f, 0.68f, 0.72f, 1f);
        public static readonly Color ButtonColor = new Color(0.18f, 0.2f, 0.24f, 0.95f);

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

        public static Button Button(Transform parent, string text, Action onClick, float width = -1f, float height = 44f, int fontSize = 22)
        {
            var img = Panel(parent, "Button", ButtonColor);
            var b = img.gameObject.AddComponent<Button>();
            var cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.2f, 1.1f, 1f);
            cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            cb.selectedColor = Color.white;
            cb.colorMultiplier = 1.2f;
            cb.fadeDuration = 0.08f;
            b.colors = cb;
            b.targetGraphic = img;
            var nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;
            var label = Label(img.transform, text, fontSize, TextColor, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            if (onClick != null) b.onClick.AddListener(() => { onClick(); if (Sandbox.Audio != null) Sandbox.Audio.Play2D(Sfx.Click, 0.35f, 1.1f); });
            LE(img.gameObject, width, height, width < 0f ? 1f : -1f);
            img.gameObject.AddComponent<HoverScale>();
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
            var nav = s.navigation; nav.mode = Navigation.Mode.None; s.navigation = nav;
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
            var nav = t.navigation; nav.mode = Navigation.Mode.None; t.navigation = nav;
            if (onChange != null) t.onValueChanged.AddListener(v => onChange(v));
            LE(root.gameObject, 30f, 28f);
            return t;
        }

        public static InputField Input(Transform parent, string value, string placeholder, float width, int size = 20)
        {
            var bg = Panel(parent, "Input", new Color(0.13f, 0.14f, 0.17f, 1f), TextureFactory.RoundedSmall);
            var f = bg.gameObject.AddComponent<InputField>();
            var text = Label(bg.transform, "", size, TextColor, TextAnchor.MiddleLeft);
            text.supportRichText = false;
            UIKit.Stretch(text.rectTransform);
            text.rectTransform.offsetMin = new Vector2(10f, 2f); text.rectTransform.offsetMax = new Vector2(-10f, -2f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            var ph = Label(bg.transform, placeholder, size, new Color(0.6f, 0.62f, 0.66f, 0.7f), TextAnchor.MiddleLeft, FontStyle.Italic);
            UIKit.Stretch(ph.rectTransform);
            ph.rectTransform.offsetMin = new Vector2(10f, 2f); ph.rectTransform.offsetMax = new Vector2(-10f, -2f);
            f.textComponent = text;
            f.placeholder = ph;
            f.targetGraphic = bg;
            f.text = value;
            f.lineType = InputField.LineType.SingleLine;
            f.caretColor = Accent;
            f.selectionColor = new Color(1f, 0.72f, 0.3f, 0.35f);
            var nav = f.navigation; nav.mode = Navigation.Mode.None; f.navigation = nav;
            LE(bg.gameObject, width, 36f, width < 0f ? 1f : -1f);
            return f;
        }
    }

    /// <summary>Smoothly scales a control up while the pointer is over it.</summary>
    public class HoverScale : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        bool over;
        float k = 1f;
        public void OnPointerEnter(PointerEventData e) { over = true; }
        public void OnPointerExit(PointerEventData e) { over = false; }
        void OnDisable() { over = false; k = 1f; transform.localScale = Vector3.one; }
        void Update()
        {
            float target = over ? 1.045f : 1f;
            if (Mathf.Abs(k - target) < 0.0005f) return;
            k = Mathf.MoveTowards(k, target, Time.unscaledDeltaTime * 0.6f);
            transform.localScale = new Vector3(k, k, 1f);
        }
    }

    /// <summary>Shows a tooltip while the pointer hovers a control.</summary>
    public class TooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public string text;
        public void OnPointerEnter(PointerEventData e) { if (Sandbox.Hud != null) Sandbox.Hud.ShowTooltip(text, (RectTransform)transform); }
        public void OnPointerExit(PointerEventData e) { if (Sandbox.Hud != null) Sandbox.Hud.ShowTooltip(null, null); }
    }

    /// <summary>Cached number strings so counters never allocate.</summary>
    public static class NumberCache
    {
        static string[] ints, tenths, percents;

        public static string Int(int n)
        {
            if (ints == null) { ints = new string[10000]; }
            if (n < 0 || n >= ints.Length) return n < 0 ? "-" : "9999+";
            var s = ints[n];
            if (s == null) { s = n.ToString(); ints[n] = s; }
            return s;
        }

        public static string Tenths(float v)
        {
            if (tenths == null) tenths = new string[2000];
            int n = Mathf.RoundToInt(v * 10f);
            if (n < 0 || n >= tenths.Length) return n < 0 ? "0.0" : "199+";
            var s = tenths[n];
            if (s == null) { s = (n / 10f).ToString("0.0"); tenths[n] = s; }
            return s;
        }

        public static string Percent(float v)
        {
            if (percents == null) percents = new string[101];
            int n = Mathf.Clamp(Mathf.RoundToInt(v * 100f), 0, 100);
            var s = percents[n];
            if (s == null) { s = n + "%"; percents[n] = s; }
            return s;
        }
    }
    #endregion

    #region Hud
    public class Hud
    {
        Canvas canvas;
        RectTransform root;
        bool hidden;

        // crosshair / hover
        RectTransform crossRoot;
        readonly Image[] crossLines = new Image[4];
        Image crossDot, crossRing;
        Text hoverText;
        int crossTool = -1;
        float crossGap = 10f, crossLen = 9f, crossRot;
        Color crossColor = Color.white;
        Image vignette;

        // hotbar
        const int Slots = ToolSystem.ToolCount;
        readonly RectTransform[] slotRt = new RectTransform[Slots];
        readonly Image[] slotBg = new Image[Slots];
        readonly Image[] slotGlow = new Image[Slots];
        readonly Image[] slotCool = new Image[Slots];
        readonly Text[] slotCount = new Text[Slots];
        readonly float[] slotAnim = new float[Slots];
        readonly string[] slotCountShown = new string[Slots];
        Text subText;
        int currentTool;

        // vitals
        RectTransform vitalsPanel;
        Text vTitle, vState, vHeart, vHealth, vBlood, vCons, vPain;
        Image bHealth, bBlood, bCons, bPain, heartIcon;
        readonly Image[] diagram = new Image[PartInfo.Count];
        readonly Text[] diagramCount = new Text[PartInfo.Count];
        class Chip { public GameObject go; }
        readonly Chip[] chips = new Chip[8];
        float vitalsTimer;
        RagdollBody vitalsBody;
        int shownState = -1;

        // settings
        RectTransform settingsPanel, settingsHandle;
        readonly RectTransform[] tabPages = new RectTransform[6];
        readonly Image[] tabButtons = new Image[6];
        int currentTab;
        public bool SettingsVisible { get; private set; }
        readonly List<Action> refreshers = new List<Action>(128);
        static readonly string[] TabNames = { "Gameplay", "Graphics", "Physics", "Gore", "Network", "Debug" };
        Text netStatusText;

        // menus
        RectTransform mainMenu, menuButtons, hostPanel, joinPanel, pausePanel, helpPanel;
        readonly List<RectTransform> menuItems = new List<RectTransform>(8);
        Text menuTitle, menuSub, joinStatus, hostIpText;
        InputField nameInput, hostPortInput, joinIpInput, joinPortInput, joinNameInput;
        float menuShownTime;
        public bool HelpVisible { get; private set; }
        public bool JoinPanelVisible { get { return joinPanel != null && joinPanel.gameObject.activeSelf && Sandbox.InMenu; } }
        bool menuVisible;

        // tooltip
        RectTransform tooltip;
        Text tooltipText;

        // toasts
        class Toast { public Image bg; public Text text; public float until, born; public bool active; }
        readonly Toast[] toasts = new Toast[6];
        int nextToast;

        // kill feed
        RectTransform killRoot;
        class FeedLine { public Image bg; public Text text; public float until; public bool active; }
        readonly FeedLine[] feed = new FeedLine[6];
        int nextFeed;

        // chat
        RectTransform chatRoot;
        class ChatLine { public Text text; public float until; public bool active; }
        readonly ChatLine[] chatLines = new ChatLine[9];
        int nextChat;
        InputField chatInput;
        public bool ChatOpen { get; private set; }
        int chatOpenedFrame;

        // player list
        RectTransform playerList;
        readonly Text[] playerRows = new Text[NetSession.MaxPlayersHard];
        float playerListTimer;

        // counters
        Text cFps, cMs, cBodies, cParticles, cDecals, cDroplets;
        float counterTimer;
        Text statusText;
        string statusShown = "";

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
            BuildCounters();
            BuildKillFeed();
            BuildChat();
            BuildPlayerList();
            BuildToasts();
            BuildSettings();
            BuildHelp();
            BuildPause();
            BuildMainMenu();
            BuildTooltip();
            SandboxEvents.Toast += OnToast;
            SandboxEvents.Kill += OnKill;
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

        #region Build: HUD
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
            crossRoot = UIKit.Rect("Crosshair", root);
            UIKit.Anchor(crossRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(60f, 60f));
            var ring = UIKit.Rect("Ring", crossRoot);
            crossRing = ring.gameObject.AddComponent<Image>();
            crossRing.sprite = TextureFactory.Ring != null ? Sprite.Create(TextureFactory.Ring, new Rect(0, 0, TextureFactory.Ring.width, TextureFactory.Ring.height), new Vector2(0.5f, 0.5f)) : TextureFactory.Circle;
            crossRing.color = new Color(1f, 1f, 1f, 0.6f);
            crossRing.raycastTarget = false;
            ring.sizeDelta = new Vector2(36f, 36f);
            for (int i = 0; i < 4; i++)
            {
                var l = UIKit.Rect("Line", crossRoot);
                var img = l.gameObject.AddComponent<Image>();
                img.sprite = TextureFactory.WhiteSprite;
                img.color = new Color(1f, 1f, 1f, 0.85f);
                img.raycastTarget = false;
                crossLines[i] = img;
            }
            var dot = UIKit.Rect("Dot", crossRoot);
            crossDot = dot.gameObject.AddComponent<Image>();
            crossDot.sprite = TextureFactory.Circle;
            crossDot.raycastTarget = false;
            dot.sizeDelta = new Vector2(5f, 5f);
            hoverText = UIKit.Label(root, "", 22, Color.white, TextAnchor.UpperCenter);
            UIKit.Anchor(hoverText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(900f, 30f));
            hoverText.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.85f);
        }

        void BuildHotbar()
        {
            var bar = UIKit.Panel(root, "Hotbar", UIKit.PanelColor);
            UIKit.Anchor(bar.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 16f), Vector2.zero);
            UIKit.HLayout(bar.gameObject, 10, 8, TextAnchor.MiddleCenter);
            UIKit.Fit(bar.gameObject, true, true);
            for (int i = 0; i < Slots; i++)
            {
                var holder = UIKit.Rect("SlotHolder" + i, bar.transform);
                UIKit.LE(holder.gameObject, 72f, 72f);
                var slot = UIKit.Panel(holder, "Slot", new Color(0.16f, 0.17f, 0.2f, 0.95f));
                UIKit.Stretch(slot.rectTransform);
                slotRt[i] = slot.rectTransform;
                slotBg[i] = slot;
                var glow = UIKit.Panel(slot.transform, "Glow", new Color(1f, 0.66f, 0.25f, 0f));
                UIKit.Stretch(glow.rectTransform);
                glow.raycastTarget = false;
                slotGlow[i] = glow;
                var icon = UIKit.Rect("Icon", slot.transform);
                UIKit.Anchor(icon, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 3f), new Vector2(44f, 44f));
                var iimg = icon.gameObject.AddComponent<Image>();
                iimg.sprite = i < TextureFactory.ToolIcons.Length ? TextureFactory.ToolIcons[i] : null;
                iimg.color = new Color(0.95f, 0.95f, 0.95f, 1f);
                iimg.raycastTarget = false;
                var cool = UIKit.Rect("Cooldown", slot.transform);
                UIKit.Stretch(cool);
                var cimg = cool.gameObject.AddComponent<Image>();
                cimg.sprite = TextureFactory.RoundedRect;
                cimg.type = Image.Type.Filled;
                cimg.fillMethod = Image.FillMethod.Radial360;
                cimg.fillOrigin = (int)Image.Origin360.Top;
                cimg.fillClockwise = false;
                cimg.fillAmount = 0f;
                cimg.color = new Color(0f, 0f, 0f, 0.55f);
                cimg.raycastTarget = false;
                slotCool[i] = cimg;
                var key = UIKit.Label(slot.transform, ToolSystem.ToolKeys[i], 15, UIKit.Dim, TextAnchor.UpperLeft, FontStyle.Bold);
                UIKit.Stretch(key.rectTransform);
                key.rectTransform.offsetMin = new Vector2(6f, 0f); key.rectTransform.offsetMax = new Vector2(0f, -3f);
                var cnt = UIKit.Label(slot.transform, "", 13, UIKit.TextColor, TextAnchor.LowerRight, FontStyle.Bold);
                UIKit.Stretch(cnt.rectTransform);
                cnt.rectTransform.offsetMin = new Vector2(0f, 3f); cnt.rectTransform.offsetMax = new Vector2(-6f, 0f);
                slotCount[i] = cnt;
                var tt = slot.gameObject.AddComponent<TooltipTrigger>();
                tt.text = "<b>" + ToolSystem.ToolNames[i] + "</b>  <color=#9AA3AD>[" + ToolSystem.ToolKeys[i] + "]</color>\n" + ToolSystem.ToolHelp[i];
                int idx = i;
                var btn = slot.gameObject.AddComponent<Button>();
                btn.targetGraphic = slot;
                var nav = btn.navigation; nav.mode = Navigation.Mode.None; btn.navigation = nav;
                btn.onClick.AddListener(() => { if (Sandbox.Tools != null) Sandbox.Tools.Select((ToolId)idx); });
            }
            subText = UIKit.Label(root, "", 20, UIKit.Accent, TextAnchor.LowerCenter, FontStyle.Bold);
            UIKit.Anchor(subText.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 118f), new Vector2(900f, 30f));
            subText.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
        }

        Image Bar(Transform parent, string label, Color color, out Text value)
        {
            var row = UIKit.Rect("Row", parent);
            UIKit.HLayout(row.gameObject, 0, 8);
            UIKit.LE(row.gameObject, -1f, 22f);
            var l = UIKit.Label(row, label, 17, UIKit.Dim);
            UIKit.LE(l.gameObject, 104f, 22f);
            var back = UIKit.Panel(row, "Back", new Color(0.18f, 0.19f, 0.22f, 1f), TextureFactory.RoundedSmall);
            UIKit.LE(back.gameObject, 190f, 14f);
            var fill = UIKit.Panel(back.transform, "Fill", color, TextureFactory.RoundedSmall);
            fill.type = Image.Type.Filled;
            fill.sprite = TextureFactory.WhiteSprite;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillAmount = 1f;
            UIKit.Stretch(fill.rectTransform);
            value = UIKit.Label(row, "", 17, UIKit.TextColor);
            UIKit.LE(value.gameObject, 70f, 22f);
            return fill;
        }

        static readonly string[] ChipNames = { "BLEEDING", "FRACTURE", "LIMB LOST", "UNCONSCIOUS", "SHOCK", "CARDIAC ARREST", "DEAD", "POSSESSED" };
        static readonly Color[] ChipColors =
        {
            new Color(0.75f, 0.1f, 0.1f), new Color(0.95f, 0.55f, 0.1f), new Color(0.55f, 0.3f, 0.75f), new Color(0.85f, 0.7f, 0.15f),
            new Color(0.25f, 0.55f, 0.95f), new Color(0.9f, 0.15f, 0.3f), new Color(0.35f, 0.35f, 0.38f), new Color(0.95f, 0.75f, 0.3f)
        };

        void BuildVitals()
        {
            var p = UIKit.Panel(root, "Vitals", UIKit.PanelColor);
            vitalsPanel = p.rectTransform;
            UIKit.Anchor(vitalsPanel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -16f), Vector2.zero);
            UIKit.VLayout(p.gameObject, 12, 5);
            UIKit.Fit(p.gameObject, true, true);
            vTitle = UIKit.Label(p.transform, "Ragdoll", 21, UIKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.LE(vTitle.gameObject, 390f, 26f);
            vState = UIKit.Label(p.transform, "", 16, UIKit.Dim);
            UIKit.LE(vState.gameObject, 390f, 20f);
            bHealth = Bar(p.transform, "Health", new Color(0.35f, 0.85f, 0.42f), out vHealth);
            bBlood = Bar(p.transform, "Blood", new Color(0.8f, 0.1f, 0.1f), out vBlood);
            bCons = Bar(p.transform, "Conscious", new Color(0.35f, 0.7f, 1f), out vCons);
            bPain = Bar(p.transform, "Pain", new Color(1f, 0.6f, 0.2f), out vPain);
            var hr = UIKit.Rect("HeartRow", p.transform);
            UIKit.HLayout(hr.gameObject, 0, 8);
            UIKit.LE(hr.gameObject, -1f, 22f);
            var hl = UIKit.Label(hr, "Heart", 17, UIKit.Dim);
            UIKit.LE(hl.gameObject, 104f, 22f);
            var hi = UIKit.Rect("HeartIcon", hr);
            heartIcon = hi.gameObject.AddComponent<Image>();
            heartIcon.sprite = TextureFactory.Circle;
            heartIcon.color = new Color(0.9f, 0.15f, 0.2f);
            heartIcon.raycastTarget = false;
            UIKit.LE(hi.gameObject, 16f, 16f);
            vHeart = UIKit.Label(hr, "", 17, UIKit.TextColor);
            UIKit.LE(vHeart.gameObject, 60f, 22f);
            var bpm = UIKit.Label(hr, "bpm", 15, UIKit.Dim);
            UIKit.LE(bpm.gameObject, 40f, 22f);
            // status chips (two rows)
            var chipRows = UIKit.Rect("Chips", p.transform);
            var g = chipRows.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = new Vector2(124f, 22f);
            g.spacing = new Vector2(6f, 5f);
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g.constraintCount = 3;
            UIKit.LE(chipRows.gameObject, 390f, -1f);
            UIKit.Fit(chipRows.gameObject, false, true);
            for (int i = 0; i < chips.Length; i++)
            {
                var c = UIKit.Panel(chipRows, "Chip", ChipColors[i], TextureFactory.RoundedSmall);
                var t = UIKit.Label(c.transform, ChipNames[i], 13, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
                UIKit.Stretch(t.rectTransform);
                c.gameObject.SetActive(false);
                chips[i] = new Chip { go = c.gameObject };
            }
            // bone diagram
            var diag = UIKit.Rect("BoneDiagram", p.transform);
            UIKit.LE(diag.gameObject, 390f, 230f);
            float scale = 118f;
            Vector2 offset = new Vector2(195f, 10f);
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
                var fc = UIKit.Label(r, "", 13, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
                UIKit.Stretch(fc.rectTransform);
                fc.rectTransform.offsetMin = new Vector2(-20f, -10f); fc.rectTransform.offsetMax = new Vector2(20f, 10f);
                fc.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.9f);
                diagramCount[i] = fc;
            }
            var legend = UIKit.Label(p.transform, "<color=#5BD96A>healthy</color>  <color=#FFD633>hurt</color>  <color=#E5261F>critical</color>  <color=#FF8C1A>xN fractures</color>  <color=#555>missing</color>", 13, UIKit.Dim);
            UIKit.LE(legend.gameObject, 390f, 18f);
        }

        Text CounterText(Transform parent, string s, Color c, float w)
        {
            var t = UIKit.Label(parent, s, 16, c, TextAnchor.MiddleRight);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            UIKit.LE(t.gameObject, w, 22f);
            return t;
        }

        void BuildCounters()
        {
            var bar = UIKit.Rect("Counters", root);
            UIKit.Anchor(bar, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-14f, 10f), Vector2.zero);
            UIKit.HLayout(bar.gameObject, 0, 4, TextAnchor.MiddleRight);
            UIKit.Fit(bar.gameObject, true, true);
            cFps = CounterText(bar, "0", UIKit.TextColor, 34f); CounterText(bar, "fps", UIKit.Dim, 30f);
            cMs = CounterText(bar, "0.0", UIKit.TextColor, 40f); CounterText(bar, "ms   bodies", UIKit.Dim, 92f);
            cBodies = CounterText(bar, "0", UIKit.TextColor, 26f); CounterText(bar, "particles", UIKit.Dim, 70f);
            cParticles = CounterText(bar, "0", UIKit.TextColor, 44f); CounterText(bar, "droplets", UIKit.Dim, 66f);
            cDroplets = CounterText(bar, "0", UIKit.TextColor, 40f); CounterText(bar, "decals", UIKit.Dim, 52f);
            cDecals = CounterText(bar, "0", UIKit.TextColor, 44f);
            statusText = UIKit.Label(root, "", 26, UIKit.Accent, TextAnchor.UpperCenter, FontStyle.Bold);
            UIKit.Anchor(statusText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -230f), new Vector2(700f, 40f));
            statusText.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
        }

        void BuildKillFeed()
        {
            killRoot = UIKit.Rect("KillFeed", root);
            UIKit.Anchor(killRoot, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-16f, -16f), new Vector2(460f, 0f));
            UIKit.VLayout(killRoot.gameObject, 0, 4, TextAnchor.UpperRight);
            UIKit.Fit(killRoot.gameObject, false, true);
            for (int i = 0; i < feed.Length; i++)
            {
                var bg = UIKit.Panel(killRoot, "Feed", new Color(0.05f, 0.05f, 0.07f, 0.75f), TextureFactory.RoundedSmall);
                UIKit.HLayout(bg.gameObject, 8, 0, TextAnchor.MiddleRight);
                var t = UIKit.Label(bg.transform, "", 18, Color.white, TextAnchor.MiddleRight);
                UIKit.LE(t.gameObject, 440f, 24f);
                bg.gameObject.SetActive(false);
                feed[i] = new FeedLine { bg = bg, text = t };
            }
        }

        void BuildChat()
        {
            chatRoot = UIKit.Rect("Chat", root);
            UIKit.Anchor(chatRoot, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(16f, 130f), new Vector2(560f, 0f));
            UIKit.VLayout(chatRoot.gameObject, 0, 2, TextAnchor.LowerLeft);
            UIKit.Fit(chatRoot.gameObject, false, true);
            for (int i = 0; i < chatLines.Length; i++)
            {
                var t = UIKit.Label(chatRoot, "", 18, Color.white, TextAnchor.MiddleLeft);
                UIKit.LE(t.gameObject, 560f, 22f);
                t.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.9f);
                t.gameObject.SetActive(false);
                chatLines[i] = new ChatLine { text = t };
            }
            chatInput = UIKit.Input(chatRoot, "", "Say something... (Enter to send, Esc to cancel)", 560f, 18);
            chatInput.characterLimit = 120;
            chatInput.onEndEdit.AddListener(OnChatEnd);
            chatInput.gameObject.SetActive(false);
        }

        void BuildPlayerList()
        {
            var p = UIKit.Panel(root, "Players", new Color(0.05f, 0.06f, 0.08f, 0.9f));
            playerList = p.rectTransform;
            UIKit.Anchor(playerList, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 120f), Vector2.zero);
            UIKit.VLayout(p.gameObject, 18, 6, TextAnchor.UpperCenter);
            UIKit.Fit(p.gameObject, true, true);
            var t = UIKit.Label(p.transform, "PLAYERS", 24, UIKit.Accent, TextAnchor.MiddleCenter, FontStyle.Bold);
            UIKit.LE(t.gameObject, 460f, 30f);
            for (int i = 0; i < playerRows.Length; i++)
            {
                playerRows[i] = UIKit.Label(p.transform, "", 20, UIKit.TextColor);
                UIKit.LE(playerRows[i].gameObject, 460f, 26f);
            }
            playerList.gameObject.SetActive(false);
        }

        void BuildToasts()
        {
            var tr = UIKit.Rect("Toasts", root);
            UIKit.Anchor(tr, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(780f, 0f));
            UIKit.VLayout(tr.gameObject, 0, 6, TextAnchor.UpperCenter);
            UIKit.Fit(tr.gameObject, false, true);
            for (int i = 0; i < toasts.Length; i++)
            {
                var bg = UIKit.Panel(tr, "Toast", new Color(0.06f, 0.07f, 0.09f, 0.82f));
                UIKit.HLayout(bg.gameObject, 10, 0, TextAnchor.MiddleCenter);
                var tx = UIKit.Label(bg.transform, "", 20, Color.white, TextAnchor.MiddleCenter);
                UIKit.LE(tx.gameObject, 740f, 28f);
                bg.gameObject.SetActive(false);
                toasts[i] = new Toast { bg = bg, text = tx };
            }
        }

        void BuildTooltip()
        {
            var p = UIKit.Panel(root, "Tooltip", new Color(0.04f, 0.05f, 0.06f, 0.96f));
            tooltip = p.rectTransform;
            tooltip.pivot = new Vector2(0.5f, 0f);
            UIKit.VLayout(p.gameObject, 10, 0);
            UIKit.Fit(p.gameObject, true, true);
            tooltipText = UIKit.Label(p.transform, "", 17, UIKit.TextColor);
            UIKit.LE(tooltipText.gameObject, 380f, -1f);
            p.raycastTarget = false;
            tooltip.gameObject.SetActive(false);
        }
        #endregion

        #region Build: settings
        RectTransform Page(Transform parent, string name)
        {
            var view = UIKit.Rect(name, parent);
            view.anchorMin = Vector2.zero; view.anchorMax = Vector2.one; view.offsetMin = new Vector2(12f, 12f); view.offsetMax = new Vector2(-12f, -96f);
            var vimg = view.gameObject.AddComponent<Image>();
            vimg.color = new Color(1f, 1f, 1f, 0.01f);
            view.gameObject.AddComponent<RectMask2D>();
            var content = UIKit.Rect("Content", view);
            content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
            UIKit.VLayout(content.gameObject, 4, 4);
            UIKit.Fit(content.gameObject, false, true);
            var sr = view.gameObject.AddComponent<ScrollRect>();
            sr.viewport = view; sr.content = content; sr.horizontal = false; sr.vertical = true; sr.scrollSensitivity = 30f; sr.movementType = ScrollRect.MovementType.Clamped;
            return view;
        }

        void Row(Transform parent, string label, Action<Transform> control, string tip = null)
        {
            var row = UIKit.Rect("Row", parent);
            UIKit.HLayout(row.gameObject, 0, 10);
            UIKit.LE(row.gameObject, -1f, 30f);
            var l = UIKit.Label(row, label, 17, UIKit.TextColor);
            UIKit.LE(l.gameObject, 200f, 28f);
            if (tip != null) { l.raycastTarget = true; l.gameObject.AddComponent<TooltipTrigger>().text = tip; }
            control(row);
        }

        void Header(Transform parent, string text)
        {
            var h = UIKit.Label(parent, text, 19, UIKit.Accent, TextAnchor.LowerLeft, FontStyle.Bold);
            UIKit.LE(h.gameObject, -1f, 30f);
        }

        void Note(Transform parent, string text)
        {
            var h = UIKit.Label(parent, text, 15, UIKit.Dim, TextAnchor.UpperLeft);
            UIKit.LE(h.gameObject, -1f, -1f);
        }

        void SliderRow(Transform parent, string label, float min, float max, Func<float> get, Action<float> set, string fmt = "0.00", bool whole = false, string tip = null)
        {
            Row(parent, label, row =>
            {
                Text val = null;
                float shown = get();
                var s = UIKit.Slider(row, min, max, shown, v => { set(v); if (val != null) { shown = v; val.text = v.ToString(fmt); } }, whole);
                UIKit.LE(s.gameObject, 190f, 26f, 1f);
                val = UIKit.Label(row, shown.ToString(fmt), 16, UIKit.Dim, TextAnchor.MiddleRight);
                UIKit.LE(val.gameObject, 64f, 26f);
                refreshers.Add(() =>
                {
                    float v = get();
                    if (Mathf.Abs(v - shown) > 1e-4f) { shown = v; s.SetValueWithoutNotify(v); val.text = v.ToString(fmt); }
                });
            }, tip);
        }

        void ToggleRow(Transform parent, string label, Func<bool> get, Action<bool> set, string tip = null)
        {
            Row(parent, label, row =>
            {
                var t = UIKit.Toggle(row, get(), set);
                refreshers.Add(() => { if (t.isOn != get()) t.SetIsOnWithoutNotify(get()); });
            }, tip);
        }

        void CycleRow(Transform parent, string label, Func<string> get, Action next, string tip = null)
        {
            Row(parent, label, row =>
            {
                Text txt = null;
                string shown = get();
                var b = UIKit.Button(row, shown, () => { next(); if (txt != null) { shown = get(); txt.text = shown; } }, 230f, 30f, 17);
                txt = b.GetComponentInChildren<Text>();
                refreshers.Add(() => { string v = get(); if (!ReferenceEquals(v, shown) && v != shown) { shown = v; txt.text = v; } });
            }, tip);
        }

        void ButtonRow(Transform parent, string label, string button, Action onClick)
        {
            Row(parent, label, row => { UIKit.Button(row, button, onClick, 230f, 30f, 17); });
        }

        static void Shared() { if (Sandbox.Net != null) Sandbox.Net.OnSettingsChanged(); }

        static readonly string[] GoreNames = { "Off", "Reduced", "Full" };
        static readonly string[] GravityNames = { "Earth", "Moon", "Zero" };
        static readonly string[] PresetNames = { "Low", "Medium", "High", "Ultra" };
        static readonly string[] LookNames = { "Clay", "Realistic" };

        void BuildSettings()
        {
            var b = Sandbox.Boot;
            var p = UIKit.Panel(root, "Settings", UIKit.PanelColor);
            settingsPanel = p.rectTransform;
            UIKit.Anchor(settingsPanel, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-14f, 40f), new Vector2(560f, -150f));
            var title = UIKit.Label(p.transform, "SETTINGS", 22, UIKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(-28f, 34f));
            var hint = UIKit.Label(p.transform, "Tab to close", 15, UIKit.Dim, TextAnchor.MiddleRight);
            UIKit.Anchor(hint.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(-50f, -8f), new Vector2(-80f, 34f));
            var close = UIKit.Button(p.transform, "▶", () => { if (SettingsVisible) ToggleSettings(); }, 34f, 30f, 16);
            UIKit.Anchor((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-10f, -10f), new Vector2(34f, 30f));
            // tabs
            var tabs = UIKit.Rect("Tabs", p.transform);
            UIKit.Anchor(tabs, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -48f), new Vector2(-20f, 38f));
            var tl = UIKit.HLayout(tabs.gameObject, 0, 4, TextAnchor.MiddleCenter);
            tl.childForceExpandWidth = true;
            for (int i = 0; i < TabNames.Length; i++)
            {
                int idx = i;
                var tb = UIKit.Button(tabs, TabNames[i], () => SelectTab(idx), 86f, 34f, 16);
                tabButtons[i] = (Image)tb.targetGraphic;
                tabPages[i] = Page(p.transform, TabNames[i]);
            }
            Transform c;

            // Gameplay
            c = tabPages[0].GetChild(0);
            Header(c, "Character");
            CycleRow(c, "Look", () => LookNames[(int)b.character.look], () =>
            {
                if (Sandbox.IsClient) { SandboxEvents.RaiseToast("The host decides the look", Color.white); return; }
                b.character.look = b.character.look == BodyLook.Clay ? BodyLook.Realistic : BodyLook.Clay;
                SandboxEvents.RaiseToast("New ragdolls: " + LookNames[(int)b.character.look] + "  (R to respawn)", Color.white);
            }, "Clay: chunky plasticine with fingerprints. Realistic: skin. Same mesh, applies to new ragdolls.");
            ToggleRow(c, "Shirt", () => b.character.shirt, v => b.character.shirt = v);
            ToggleRow(c, "Pants", () => b.character.pants, v => b.character.pants = v);
            ToggleRow(c, "Shoes", () => b.character.shoes, v => b.character.shoes = v);
            ToggleRow(c, "Hair", () => b.character.hair, v => b.character.hair = v);
            Header(c, "Behaviour");
            ToggleRow(c, "Reactions (master)", () => b.general.reactions, v => { b.general.reactions = v; Shared(); }, "Flinch, stagger, brace, grab, clutch wounds, pain voices.");
            SliderRow(c, "Reaction strength", 0f, 2f, () => b.behaviour.reactionStrength, v => b.behaviour.reactionStrength = v);
            SliderRow(c, "Will to live", 0f, 1f, () => b.vitals.willToLive, v => b.vitals.willToLive = v, "0.00", false, "How hard a hurt body keeps trying to get up, hop, crawl and hold on.");
            ToggleRow(c, "Grab and hold", () => b.behaviour.grabAndHold, v => b.behaviour.grabAndHold = v);
            SliderRow(c, "Grip strength", 0.1f, 3f, () => b.behaviour.gripStrength, v => b.behaviour.gripStrength = v);
            SliderRow(c, "Stiffness", 0.2f, 2f, () => b.behaviour.stiffness, v => b.behaviour.stiffness = v);
            SliderRow(c, "Muscle strength", 0f, 3f, () => b.muscles.strength, v => b.muscles.strength = v);
            SliderRow(c, "Balance assist (Nm)", 0f, 1200f, () => b.balance.assistTorque, v => b.balance.assistTorque = v, "0");
            ToggleRow(c, "Wander (N)", () => b.general.wander, v => b.general.wander = v);
            ToggleRow(c, "Look at player", () => b.general.gazeAtPlayer, v => b.general.gazeAtPlayer = v);
            Header(c, "Time");
            ToggleRow(c, "Slow motion (T)", () => TimeControl.SlowMotion, v => { if (!Sandbox.IsClient) { TimeControl.SlowMotion = v; Shared(); } });
            SliderRow(c, "Slow-motion scale", 0.02f, 1f, () => b.physics.slowMotionScale, v => { b.physics.slowMotionScale = v; Shared(); });
            Header(c, "Player");
            SliderRow(c, "Field of view", 50f, 110f, () => b.player.fieldOfView, v => b.player.fieldOfView = v, "0");
            SliderRow(c, "Mouse sensitivity", 0.1f, 5f, () => b.player.mouseSensitivity, v => b.player.mouseSensitivity = v);
            ToggleRow(c, "Invert Y", () => b.player.invertY, v => b.player.invertY = v);
            ToggleRow(c, "Noclip (fly)", () => b.player.noclip, v => b.player.noclip = v);
            ToggleRow(c, "Kill-cam", () => b.cameraSettings.killCam, v => b.cameraSettings.killCam = v);
            SliderRow(c, "Master volume", 0f, 1f, () => b.audioSettings.master, v => b.audioSettings.master = v);

            // Graphics
            c = tabPages[1].GetChild(0);
            Header(c, "Quality");
            CycleRow(c, "Preset", () => PresetNames[(int)b.graphics.preset], () => b.graphics.preset = (QualityPreset)(((int)b.graphics.preset + 1) % 4));
            ToggleRow(c, "Auto quality", () => b.graphics.autoQuality, v => b.graphics.autoQuality = v, "Lowers blood, decals, particles, physics iterations, shadows and render scale when the frame rate drops.");
            SliderRow(c, "Target FPS", 20f, 144f, () => b.graphics.targetFps, v => b.graphics.targetFps = v, "0", true);
            SliderRow(c, "FPS cap (0 = off)", 0f, 240f, () => b.graphics.fpsCap, v => b.graphics.fpsCap = Mathf.RoundToInt(v), "0", true);
            ToggleRow(c, "VSync", () => b.graphics.vSync, v => { b.graphics.vSync = v; b.Quality.Refresh(b); });
            Header(c, "Rendering");
            ToggleRow(c, "Shadows", () => b.graphics.shadows, v => b.graphics.shadows = v);
            SliderRow(c, "Shadow distance", 10f, 150f, () => b.graphics.shadowDistance, v => { b.graphics.shadowDistance = v; b.Quality.ApplyPreset(b.graphics.preset, b); }, "0");
            SliderRow(c, "Render scale", 0.5f, 1.5f, () => b.graphics.renderScale, v => { b.graphics.renderScale = v; b.Quality.ApplyPreset(b.graphics.preset, b); });
            ToggleRow(c, "Threaded mesh jobs", () => b.graphics.threadedMeshJobs, v => b.graphics.threadedMeshJobs = v, "Cuts, holes and dents are computed on a worker thread.");
            Header(c, "World");
            ToggleRow(c, "Fog", () => b.graphics.fog, v => b.graphics.fog = v);
            ToggleRow(c, "Day/night cycle", () => b.graphics.dayNightCycle, v => b.graphics.dayNightCycle = v);
            SliderRow(c, "Time of day", 0f, 24f, () => b.graphics.timeOfDay, v => b.graphics.timeOfDay = v, "0.0");

            // Physics
            c = tabPages[2].GetChild(0);
            Header(c, "Solver");
            SliderRow(c, "Fixed timestep", 0.005f, 0.02f, () => b.physics.fixedTimestep, v => b.physics.fixedTimestep = v, "0.0000");
            SliderRow(c, "Ragdoll solver iterations", 4f, 30f, () => b.physics.ragdollSolverIterations, v => { b.physics.ragdollSolverIterations = Mathf.RoundToInt(v); b.Quality.ApplyPreset(b.graphics.preset, b); }, "0", true);
            SliderRow(c, "Ragdoll velocity iterations", 1f, 12f, () => b.physics.ragdollVelocityIterations, v => { b.physics.ragdollVelocityIterations = Mathf.RoundToInt(v); for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].ApplySolverSettings(); }, "0", true);
            SliderRow(c, "World solver iterations", 2f, 20f, () => b.physics.solverIterations, v => { b.physics.solverIterations = Mathf.RoundToInt(v); b.ApplyPhysicsSettings(); }, "0", true);
            SliderRow(c, "Max depenetration (m/s)", 1f, 10f, () => b.physics.maxDepenetrationVelocity, v => { b.physics.maxDepenetrationVelocity = v; b.ApplyPhysicsSettings(); }, "0.0");
            CycleRow(c, "Gravity (G)", () => GravityNames[(int)b.physics.gravity], () => { if (Sandbox.IsClient) return; b.physics.gravity = (GravityMode)(((int)b.physics.gravity + 1) % 3); Shared(); });
            ToggleRow(c, "Safety net", () => b.physics.safetyNet, v => b.physics.safetyNet = v, "Recovers bodies from NaN / runaway velocities / falling out of the world.");
            Header(c, "Balance assist");
            SliderRow(c, "Weight support", 0f, 1.2f, () => b.balance.weightSupport, v => b.balance.weightSupport = v);
            SliderRow(c, "Height gain", 0f, 200f, () => b.balance.heightGain, v => b.balance.heightGain = v, "0");
            SliderRow(c, "Velocity gain", 0f, 30f, () => b.balance.velocityGain, v => b.balance.velocityGain = v, "0.0");
            SliderRow(c, "Max push assist (x weight)", 0f, 1f, () => b.balance.maxHorizontalAssist, v => b.balance.maxHorizontalAssist = v);
            Header(c, "Locomotion");
            SliderRow(c, "Walk speed", 0.2f, 3f, () => b.locomotion.walkSpeed, v => b.locomotion.walkSpeed = v);
            SliderRow(c, "Run speed", 1f, 7f, () => b.locomotion.runSpeed, v => b.locomotion.runSpeed = v);
            SliderRow(c, "Max step up (m)", 0.05f, 0.6f, () => b.locomotion.maxStepUp, v => b.locomotion.maxStepUp = v);

            // Gore
            c = tabPages[3].GetChild(0);
            Header(c, "Gore");
            CycleRow(c, "Gore level", () => GoreNames[(int)b.dismember.gore], () =>
            {
                if (Sandbox.IsClient) { SandboxEvents.RaiseToast("The host decides the gore level", Color.white); return; }
                b.dismember.gore = (GoreLevel)(((int)b.dismember.gore + 1) % 3);
                for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) Sandbox.Ragdolls[i].RefreshBreakForces();
                Shared();
            }, "Off: no blood or dismemberment. Reduced: less blood. Full: everything. Clay bodies shed clay crumbs.");
            SliderRow(c, "Blood amount", 0f, 2.5f, () => b.blood.bloodAmount, v => { b.blood.bloodAmount = v; Shared(); });
            ToggleRow(c, "Paint blood on bodies", () => b.blood.paintOnBodies, v => b.blood.paintOnBodies = v);
            ToggleRow(c, "Bleed when gore is off", () => b.vitals.bleedWhenGoreOff, v => b.vitals.bleedWhenGoreOff = v);
            Header(c, "Damage");
            SliderRow(c, "Deformation", 0f, 3f, () => b.damage.deformationScale, v => b.damage.deformationScale = v, "0.00", false, "Dents, squash and stretch strength.");
            SliderRow(c, "Fracture threshold", 0.2f, 4f, () => b.damage.fractureThresholdScale, v => b.damage.fractureThresholdScale = v);
            SliderRow(c, "Impact damage", 0f, 3f, () => b.damage.impactDamageScale, v => b.damage.impactDamageScale = v);
            SliderRow(c, "Laser damage/s", 0f, 200f, () => b.damage.laserDamagePerSecond, v => b.damage.laserDamagePerSecond = v, "0");
            SliderRow(c, "Blade cut speed", 10f, 1000f, () => b.dismember.bladeSeverSpeed, v => b.dismember.bladeSeverSpeed = v, "0");
            SliderRow(c, "Laser cut sweep", 1f, 400f, () => b.tools.laserCutSweep, v => b.tools.laserCutSweep = v, "0");
            Header(c, "Budgets");
            SliderRow(c, "Max blood droplets", 32f, 4000f, () => b.blood.maxDroplets, v => { b.blood.maxDroplets = Mathf.RoundToInt(v); b.Quality.ApplyPreset(b.graphics.preset, b); }, "0", true);
            SliderRow(c, "Max decals", 100f, 4000f, () => b.blood.maxDecals, v => { b.blood.maxDecals = Mathf.RoundToInt(v); b.Quality.ApplyPreset(b.graphics.preset, b); }, "0", true);

            // Network
            c = tabPages[4].GetChild(0);
            Header(c, "Session");
            netStatusText = UIKit.Label(c, "offline", 17, UIKit.TextColor);
            UIKit.LE(netStatusText.gameObject, -1f, 26f);
            Row(c, "Your IP", row =>
            {
                var ip = UIKit.Label(row, Sandbox.Net != null ? Sandbox.Net.LocalIP : "?", 18, UIKit.TextColor);
                UIKit.LE(ip.gameObject, 160f, 28f);
                UIKit.Button(row, "Copy", CopyIP, 80f, 30f, 16);
            });
            ButtonRow(c, "Host / join", "Open online menu", () => { if (!Sandbox.InMenu) Sandbox.Boot.ReturnToMenu(); ShowHostPanel(true); });
            ButtonRow(c, "Leave session", "Disconnect", () => { if (Sandbox.Net != null && Sandbox.Net.Active) Sandbox.Net.Shutdown("left the session"); });
            Header(c, "Rates");
            SliderRow(c, "Snapshot rate (Hz)", 10f, 60f, () => b.network.snapshotRate, v => b.network.snapshotRate = Mathf.RoundToInt(v), "0", true);
            SliderRow(c, "Input rate (Hz)", 10f, 60f, () => b.network.inputRate, v => b.network.inputRate = Mathf.RoundToInt(v), "0", true);
            SliderRow(c, "Interpolation delay (s)", 0.03f, 0.4f, () => b.network.interpolationDelay, v => b.network.interpolationDelay = v);
            SliderRow(c, "Timeout (s)", 2f, 30f, () => b.network.timeout, v => b.network.timeout = v, "0");
            Note(c, "Hosting uses UDP port " + b.network.port + ". Allow it in the host's firewall; for play over the internet forward that UDP port on the host's router and share the public IP.");

            // Debug
            c = tabPages[5].GetChild(0);
            Header(c, "Debug");
            ToggleRow(c, "Debug overlay (F3)", () => b.debug.showOverlay, v => b.debug.showOverlay = v, "COM, capture point, support polygon, foot targets, joint loads, states and counters.");
            ToggleRow(c, "Log self-test", () => b.debug.logSelfTest, v => b.debug.logSelfTest = v);
            ToggleRow(c, "Self-test on start", () => b.general.selfTestOnStart, v => b.general.selfTestOnStart = v);
            ButtonRow(c, "Walking self-test", "Run now", RunSelfTest);
            ToggleRow(c, "Anatomy X-ray (X)", () => Sandbox.XRay, v => { if (v != Sandbox.XRay) b.SetXRay(v); });
            ToggleRow(c, "Rewind (hold Z)", () => b.bonus.rewind, v => b.bonus.rewind = v);
            Note(c, "Self-test: the ragdoll stands for 3 s and walks for 5 s; on failure the balance assist is raised and the change is logged to the console.");

            SelectTab(0);
            // collapsed handle on the right edge
            var handle = UIKit.Button(root, "◀ Settings", () => { if (!SettingsVisible) ToggleSettings(); }, 150f, 34f, 16);
            settingsHandle = (RectTransform)handle.transform;
            UIKit.Anchor(settingsHandle, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-14f, -14f), new Vector2(150f, 34f));
            settingsPanel.gameObject.SetActive(false);
        }

        void SelectTab(int i)
        {
            currentTab = i;
            for (int k = 0; k < tabPages.Length; k++)
            {
                tabPages[k].gameObject.SetActive(k == i);
                tabButtons[k].color = k == i ? new Color(0.95f, 0.62f, 0.2f, 1f) : UIKit.ButtonColor;
            }
        }

        static void CopyIP()
        {
            if (Sandbox.Net == null) return;
            GUIUtility.systemCopyBuffer = Sandbox.Net.LocalIP + ":" + Sandbox.Boot.network.port;
            SandboxEvents.RaiseToast("Copied " + Sandbox.Net.LocalIP + ":" + Sandbox.Boot.network.port, new Color(0.7f, 0.9f, 1f));
        }

        static void RunSelfTest()
        {
            RagdollBody subject = null;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) { var r = Sandbox.Ragdolls[i]; if (r != null && r.Alive && r.OwnerPlayer < 0 && !r.Possessed && !r.Puppet) { subject = r; break; } }
            if (subject == null) { SandboxEvents.RaiseToast("No free ragdoll for the self-test", new Color(1f, 0.7f, 0.6f)); return; }
            Sandbox.Test = new SelfTest();
            Sandbox.Test.Begin(subject);
            SandboxEvents.RaiseToast("Self-test running (see the console / F3)", new Color(0.7f, 0.9f, 1f));
        }
        #endregion

        #region Build: menus
        void BuildHelp()
        {
            var p = UIKit.Panel(root, "Help", new Color(0.05f, 0.06f, 0.08f, 0.94f));
            helpPanel = p.rectTransform;
            UIKit.Anchor(helpPanel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1320f, 760f));
            var t = UIKit.Label(p.transform, "", 18, UIKit.TextColor, TextAnchor.UpperLeft);
            UIKit.Stretch(t.rectTransform);
            t.rectTransform.offsetMin = new Vector2(30f, 24f); t.rectTransform.offsetMax = new Vector2(-680f, -24f);
            t.text =
                "<size=28><color=#FFB84D><b>RAGDOLL SANDBOX</b></color></size>\n\n" +
                "<b>You (first person)</b>\n" +
                "WASD move   Mouse look   Space jump   Shift sprint   C crouch\n" +
                "Tab  settings + free cursor (hold online: player list)   Esc  pause\n" +
                "E  drive / leave the nearest clay car (WASD, Space handbrake)\n\n" +
                "<b>Tools</b>  1-9, 0, - or mouse wheel  (Q = sub-mode)\n" +
                "1 Grab   2 Hammer   3 Blade   4 Pistol   5 Shotgun   6 Explosives\n" +
                "7 Props (+ clay car)   8 Shock   9 Heal   0 Spawn ragdoll   - Laser\n" +
                "LMB primary   RMB secondary   Wheel: grab distance\n" +
                "Shift while grabbing: strong pull (stretches, rips limbs)\n" +
                "Blade: drag across a limb - the drag is the cut plane\n" +
                "Laser: hold to burn, sweep fast across a limb to cut it\n" +
                "Shotgun point blank: one big crater\n" +
                "Ctrl + click ground: order the ragdoll to walk there\n";
            var t2 = UIKit.Label(p.transform, "", 18, UIKit.TextColor, TextAnchor.UpperLeft);
            UIKit.Stretch(t2.rectTransform);
            t2.rectTransform.offsetMin = new Vector2(660f, 24f); t2.rectTransform.offsetMax = new Vector2(-30f, -24f);
            t2.text =
                "\n\n<b>World</b>\n" +
                "R reset ragdolls   Shift+R reset whole scene\n" +
                "T slow motion   G gravity (Earth / Moon / Zero)\n" +
                "V possess a ragdoll (online: your own)   F follow cam\n" +
                "X anatomy X-ray   B detonate C4   N wander on/off\n" +
                "Z (hold) rewind 5 s   H help   F1 hide HUD   F3 debug overlay\n" +
                "Enter chat (online)\n\n" +
                "<b>Possessed ragdoll</b>\n" +
                "WASD walk (camera relative)   Shift run   Space jump\n" +
                "C crouch   LMB / RMB reach and grab with left / right hand\n\n" +
                "<b>Zones</b>: plaza (centre) with the ring road, obstacle course (north),\n" +
                "stairs & ramps (east), demolition (west), hazard pit (south),\n" +
                "tower & cliff (north-east), hills (corners).\n\n" +
                "<color=#9AA3AD>Press H to close</color>";
            helpPanel.gameObject.SetActive(false);
        }

        void BuildPause()
        {
            var p = UIKit.Panel(root, "Pause", new Color(0.05f, 0.06f, 0.08f, 0.95f));
            pausePanel = p.rectTransform;
            UIKit.Anchor(pausePanel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            UIKit.VLayout(p.gameObject, 26, 10, TextAnchor.UpperCenter);
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
            UIKit.Button(p.transform, "Main menu", () => { Sandbox.Boot.SetPaused(false); Sandbox.Boot.ReturnToMenu(); }, 360f);
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

        void BuildMainMenu()
        {
            var b = Sandbox.Boot;
            var bg = UIKit.Rect("MainMenu", root);
            UIKit.Stretch(bg);
            var shade = bg.gameObject.AddComponent<Image>();
            shade.sprite = TextureFactory.Gradient;
            shade.color = new Color(0.03f, 0.035f, 0.05f, 0.78f);
            mainMenu = bg;
            menuTitle = UIKit.Label(bg, "RAGDOLL SANDBOX", 84, UIKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.Anchor(menuTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(120f, -110f), new Vector2(1100f, 110f));
            menuTitle.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
            menuSub = UIKit.Label(bg, "clay physics playground  -  active ragdolls, cuts, holes, fractures, online up to 4 players", 24, UIKit.Dim, TextAnchor.MiddleLeft);
            UIKit.Anchor(menuSub.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(124f, -215f), new Vector2(1300f, 40f));
            // main buttons
            var col = UIKit.Rect("Buttons", bg);
            UIKit.Anchor(col, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(120f, -40f), new Vector2(380f, 0f));
            UIKit.VLayout(col.gameObject, 0, 14, TextAnchor.UpperLeft);
            UIKit.Fit(col.gameObject, false, true);
            menuButtons = col;
            menuItems.Add((RectTransform)UIKit.Button(col, "Play Offline", () => { ShowHostPanel(false); ShowJoinPanel(false); Sandbox.Boot.StartPlaying(); }, 380f, 58f, 26).transform);
            menuItems.Add((RectTransform)UIKit.Button(col, "Host Game", () => ShowHostPanel(true), 380f, 58f, 26).transform);
            menuItems.Add((RectTransform)UIKit.Button(col, "Join Game", () => ShowJoinPanel(true), 380f, 58f, 26).transform);
            menuItems.Add((RectTransform)UIKit.Button(col, "Settings", () => ToggleSettings(), 380f, 58f, 26).transform);
            menuItems.Add((RectTransform)UIKit.Button(col, "Quit", Quit, 380f, 58f, 26).transform);
            var nameRow = UIKit.Rect("NameRow", col);
            UIKit.HLayout(nameRow.gameObject, 0, 10);
            UIKit.LE(nameRow.gameObject, 380f, 40f);
            var nl = UIKit.Label(nameRow, "Name", 19, UIKit.Dim);
            UIKit.LE(nl.gameObject, 70f, 36f);
            nameInput = UIKit.Input(nameRow, b.player.playerName, "Player", 300f);
            nameInput.characterLimit = 16;
            nameInput.onEndEdit.AddListener(v => { if (!string.IsNullOrEmpty(v)) Sandbox.Boot.player.playerName = v.Trim(); });
            menuItems.Add(nameRow);

            // host panel
            var hp = UIKit.Panel(bg, "HostPanel", UIKit.PanelColor);
            hostPanel = hp.rectTransform;
            UIKit.Anchor(hostPanel, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(560f, -40f), new Vector2(620f, 0f));
            UIKit.VLayout(hp.gameObject, 20, 10);
            UIKit.Fit(hp.gameObject, false, true);
            var ht = UIKit.Label(hp.transform, "HOST A GAME", 26, UIKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.LE(ht.gameObject, -1f, 34f);
            Row(hp.transform, "Your local IP", row =>
            {
                hostIpText = UIKit.Label(row, Sandbox.Net != null ? Sandbox.Net.LocalIP : "?", 20, UIKit.TextColor, TextAnchor.MiddleLeft, FontStyle.Bold);
                UIKit.LE(hostIpText.gameObject, 190f, 30f);
                UIKit.Button(row, "Copy", CopyIP, 90f, 32f, 17);
            });
            Row(hp.transform, "UDP port", row => { hostPortInput = UIKit.Input(row, b.network.port.ToString(), "27777", 140f); hostPortInput.contentType = InputField.ContentType.IntegerNumber; });
            SliderRow(hp.transform, "Max players", 2f, 4f, () => b.network.maxPlayers, v => b.network.maxPlayers = Mathf.RoundToInt(v), "0", true);
            Note(hp.transform, "Friends join with your IP and port. Allow the UDP port in your firewall (Windows asks the first time). For internet play forward that UDP port on your router and give them your public IP.");
            var hb = UIKit.Rect("HostButtons", hp.transform);
            UIKit.HLayout(hb.gameObject, 0, 10);
            UIKit.LE(hb.gameObject, -1f, 50f);
            UIKit.Button(hb, "Start hosting", StartHost, 260f, 48f, 22);
            UIKit.Button(hb, "Back", () => ShowHostPanel(false), 140f, 48f, 22);
            hostPanel.gameObject.SetActive(false);

            // join panel
            var jp = UIKit.Panel(bg, "JoinPanel", UIKit.PanelColor);
            joinPanel = jp.rectTransform;
            UIKit.Anchor(joinPanel, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(560f, -40f), new Vector2(620f, 0f));
            UIKit.VLayout(jp.gameObject, 20, 10);
            UIKit.Fit(jp.gameObject, false, true);
            var jt = UIKit.Label(jp.transform, "JOIN A GAME", 26, UIKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            UIKit.LE(jt.gameObject, -1f, 34f);
            Row(jp.transform, "Host IP", row => { joinIpInput = UIKit.Input(row, b.network.lastAddress, "192.168.1.20", 300f); });
            Row(jp.transform, "UDP port", row => { joinPortInput = UIKit.Input(row, b.network.port.ToString(), "27777", 140f); joinPortInput.contentType = InputField.ContentType.IntegerNumber; });
            joinStatus = UIKit.Label(jp.transform, "", 18, UIKit.Dim);
            UIKit.LE(joinStatus.gameObject, -1f, 26f);
            var jb = UIKit.Rect("JoinButtons", jp.transform);
            UIKit.HLayout(jb.gameObject, 0, 10);
            UIKit.LE(jb.gameObject, -1f, 50f);
            UIKit.Button(jb, "Connect", Join, 260f, 48f, 22);
            UIKit.Button(jb, "Back", () => ShowJoinPanel(false), 140f, 48f, 22);
            joinPanel.gameObject.SetActive(false);
            joinNameInput = nameInput;
            mainMenu.gameObject.SetActive(false);
        }

        int ParsePort(InputField f)
        {
            int port;
            if (f == null || !int.TryParse(f.text, out port) || port < 1024 || port > 65535) port = Sandbox.Boot.network.port;
            return port;
        }

        void StartHost()
        {
            var b = Sandbox.Boot;
            b.network.port = ParsePort(hostPortInput);
            if (nameInput != null && !string.IsNullOrEmpty(nameInput.text)) b.player.playerName = nameInput.text.Trim();
            if (Sandbox.Net.StartHost(b.network.port, b.player.playerName))
            {
                ShowHostPanel(false);
                b.StartPlaying();
            }
        }

        void Join()
        {
            var b = Sandbox.Boot;
            int port = ParsePort(joinPortInput);
            string ip = joinIpInput != null ? joinIpInput.text.Trim() : "";
            if (ip.Length == 0) { joinStatus.text = "<color=#FF8A7A>Enter the host's IP address</color>"; return; }
            b.network.lastAddress = ip;
            if (joinNameInput != null && !string.IsNullOrEmpty(joinNameInput.text)) b.player.playerName = joinNameInput.text.Trim();
            if (!Sandbox.Net.Join(ip, port, b.player.playerName)) { joinStatus.text = "<color=#FF8A7A>" + Sandbox.Net.Status + "</color>"; return; }
            joinStatus.text = "Connecting to " + ip + ":" + port + " ...";
        }

        void ShowHostPanel(bool on)
        {
            if (on) { if (!Sandbox.InMenu) Sandbox.Boot.ReturnToMenu(); joinPanel.gameObject.SetActive(false); }
            hostPanel.gameObject.SetActive(on);
            if (on && hostIpText != null && Sandbox.Net != null) hostIpText.text = Sandbox.Net.LocalIP;
        }
        #endregion

        #region Public API
        public void ShowMainMenu(bool on)
        {
            menuVisible = on;
            mainMenu.gameObject.SetActive(on);
            if (on)
            {
                mainMenu.SetAsLastSibling();
                tooltip.SetAsLastSibling();
                menuShownTime = Time.unscaledTime;
                ShowPause(false);
                if (nameInput != null) nameInput.text = Sandbox.Boot.player.playerName;
            }
            else { hostPanel.gameObject.SetActive(false); joinPanel.gameObject.SetActive(false); }
        }

        public void ShowJoinPanel(bool on)
        {
            if (on && !Sandbox.InMenu) Sandbox.Boot.ReturnToMenu();
            if (on) hostPanel.gameObject.SetActive(false);
            joinPanel.gameObject.SetActive(on);
            if (on && Sandbox.Net != null && !Sandbox.Net.Active && Sandbox.Net.Status != "offline") joinStatus.text = "<color=#FF8A7A>" + Sandbox.Net.Status + "</color>";
        }

        public void SetHover(string text, Color c)
        {
            if (hoverText == null) return;
            if (!ReferenceEquals(hoverText.text, text) && hoverText.text != text) hoverText.text = text;
            hoverText.color = c;
            crossColor = string.IsNullOrEmpty(text) ? ToolColor(currentTool) : c;
        }

        public void SetTool(int index, string sub)
        {
            if (subText != null && !ReferenceEquals(subText.text, sub) && subText.text != sub) subText.text = sub;
            currentTool = index;
        }

        public void ShowTooltip(string text, RectTransform anchorRt)
        {
            if (tooltip == null) return;
            if (string.IsNullOrEmpty(text) || anchorRt == null) { tooltip.gameObject.SetActive(false); return; }
            tooltipText.text = text;
            tooltip.gameObject.SetActive(true);
            tooltip.SetAsLastSibling();
            Vector3 world = anchorRt.TransformPoint(new Vector3(0f, anchorRt.rect.height * 0.5f + 12f, 0f));
            tooltip.position = world;
        }

        public void ToggleSettings()
        {
            SettingsVisible = !SettingsVisible;
            settingsPanel.gameObject.SetActive(SettingsVisible);
            settingsHandle.gameObject.SetActive(!SettingsVisible);
            if (SettingsVisible) { settingsPanel.SetAsLastSibling(); tooltip.SetAsLastSibling(); for (int i = 0; i < refreshers.Count; i++) refreshers[i](); }
            CursorControl.SetLocked(!SettingsVisible && !TimeControl.Paused && !Sandbox.InMenu);
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
            if (on) { pausePanel.SetAsLastSibling(); tooltip.SetAsLastSibling(); }
        }

        public void ToggleHidden()
        {
            hidden = !hidden;
            canvas.enabled = !hidden;
        }

        public void ShowPlayerList(bool on)
        {
            if (playerList.gameObject.activeSelf == on) return;
            playerList.gameObject.SetActive(on);
            if (on) { playerList.SetAsLastSibling(); playerListTimer = 0f; }
        }

        public void OpenChat()
        {
            if (ChatOpen) return;
            ChatOpen = true;
            chatOpenedFrame = Time.frameCount;
            chatInput.gameObject.SetActive(true);
            chatInput.text = "";
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(chatInput.gameObject);
            chatInput.ActivateInputField();
            for (int i = 0; i < chatLines.Length; i++) if (chatLines[i].active) chatLines[i].text.gameObject.SetActive(true);
        }

        void OnChatEnd(string text)
        {
            if (!ChatOpen) return;
            bool send = InputBridge.Down(KeyId.Enter) || InputBridge.Held(KeyId.Enter);
            if (send && !string.IsNullOrEmpty(text) && text.Trim().Length > 0 && Sandbox.Net != null) Sandbox.Net.SendChat(text);
            CloseChat();
        }

        void CloseChat()
        {
            ChatOpen = false;
            chatInput.text = "";
            chatInput.DeactivateInputField();
            chatInput.gameObject.SetActive(false);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }

        public void AddChat(string name, string text, Color c)
        {
            var l = chatLines[nextChat];
            nextChat = (nextChat + 1) % chatLines.Length;
            l.text.text = name != null ? "<b>" + name + ":</b> " + text : "<i>" + text + "</i>";
            l.text.color = c;
            l.until = Time.unscaledTime + 12f;
            l.active = true;
            l.text.gameObject.SetActive(true);
            l.text.transform.SetSiblingIndex(chatLines.Length - 1);
            if (Sandbox.Audio != null && name != null) Sandbox.Audio.Play2D(Sfx.Click, 0.3f, 1.6f);
        }

        void OnToast(string text, Color c)
        {
            var t = toasts[nextToast];
            nextToast = (nextToast + 1) % toasts.Length;
            t.text.text = text;
            t.text.color = c;
            t.born = Time.unscaledTime;
            t.until = Time.unscaledTime + 3.4f;
            t.active = true;
            t.bg.gameObject.SetActive(true);
            t.bg.transform.SetAsLastSibling();
        }

        void OnKill(string who, string what, Color c)
        {
            var f = feed[nextFeed];
            nextFeed = (nextFeed + 1) % feed.Length;
            f.text.text = "<b>" + who + "</b>  " + what;
            f.text.color = c;
            f.until = Time.unscaledTime + 7f;
            f.active = true;
            f.bg.gameObject.SetActive(true);
            f.bg.transform.SetAsFirstSibling();
        }
        #endregion

        #region Runtime
        public void Tick()
        {
            float now = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;
            InputBridge.GameplayBlocked = TimeControl.Paused || Sandbox.InMenu || ChatOpen;
            if (ChatOpen && InputBridge.Down(KeyId.Escape)) CloseChat();
            if (ChatOpen && !chatInput.isFocused && Time.frameCount > chatOpenedFrame + 2) CloseChat();
            for (int i = 0; i < toasts.Length; i++)
            {
                var t = toasts[i];
                if (!t.active) continue;
                float left = t.until - now;
                if (left <= 0f) { t.active = false; t.bg.gameObject.SetActive(false); continue; }
                float a = Mathf.Clamp01(left / 0.5f) * Mathf.Clamp01((now - t.born) / 0.12f);
                Color c = t.text.color; c.a = a; t.text.color = c;
                Color b = t.bg.color; b.a = 0.82f * a; t.bg.color = b;
                float s = 0.92f + 0.08f * Mathf.Clamp01((now - t.born) / 0.15f);
                t.bg.rectTransform.localScale = new Vector3(s, s, 1f);
            }
            for (int i = 0; i < feed.Length; i++)
            {
                var f = feed[i];
                if (!f.active) continue;
                float left = f.until - now;
                if (left <= 0f) { f.active = false; f.bg.gameObject.SetActive(false); continue; }
                float a = Mathf.Clamp01(left / 0.8f);
                Color c = f.text.color; c.a = a; f.text.color = c;
                Color b = f.bg.color; b.a = 0.75f * a; f.bg.color = b;
            }
            for (int i = 0; i < chatLines.Length; i++)
            {
                var l = chatLines[i];
                if (!l.active) continue;
                float left = l.until - now;
                if (left <= 0f && !ChatOpen) { l.active = false; l.text.gameObject.SetActive(false); continue; }
                Color c = l.text.color; c.a = ChatOpen ? 1f : Mathf.Clamp01(left / 1f); l.text.color = c;
            }
            // kill feed moves left of the settings panel when it is open
            killRoot.anchoredPosition = new Vector2(SettingsVisible ? -590f : -180f, -16f);
            vitalsTimer -= dt;
            if (vitalsTimer <= 0f) { vitalsTimer = 0.1f; UpdateVitals(); }
            counterTimer -= dt;
            if (counterTimer <= 0f) { counterTimer = 0.25f; UpdateCounters(); }
            if (SettingsVisible && Time.frameCount % 15 == 0)
            {
                for (int i = 0; i < refreshers.Count; i++) refreshers[i]();
                if (netStatusText != null && Sandbox.Net != null && netStatusText.text != Sandbox.Net.Status) netStatusText.text = Sandbox.Net.Status;
            }
            if (playerList.gameObject.activeSelf)
            {
                playerListTimer -= dt;
                if (playerListTimer <= 0f) { playerListTimer = 0.5f; UpdatePlayerList(); }
            }
            if (menuVisible) AnimateMenu(now);
        }

        void AnimateMenu(float now)
        {
            float t = now - menuShownTime;
            float pulse = 1f + 0.015f * Mathf.Sin(now * 2.2f);
            menuTitle.rectTransform.localScale = new Vector3(pulse, pulse, 1f);
            var tc = menuTitle.color; tc.a = Mathf.Clamp01(t / 0.6f); menuTitle.color = tc;
            var sc = menuSub.color; sc.a = Mathf.Clamp01((t - 0.3f) / 0.6f) * 0.9f; menuSub.color = sc;
            for (int i = 0; i < menuItems.Count; i++)
            {
                float k = MathUtil.SmoothStep01((t - 0.15f - i * 0.07f) / 0.35f);
                var rt = menuItems[i];
                rt.localScale = new Vector3(Mathf.Lerp(0.85f, 1f, k), Mathf.Lerp(0.85f, 1f, k), 1f);
                var cg = rt.GetComponent<CanvasGroup>();
                if (cg == null) cg = rt.gameObject.AddComponent<CanvasGroup>();
                cg.alpha = k;
            }
            if (joinPanel.gameObject.activeSelf && Sandbox.Net != null && Sandbox.Net.Connecting && Time.frameCount % 20 == 0 && joinStatus.text != Sandbox.Net.Status) joinStatus.text = Sandbox.Net.Status;
        }

        void UpdatePlayerList()
        {
            var net = Sandbox.Net;
            for (int i = 0; i < playerRows.Length; i++)
            {
                var info = net != null ? net.Infos[i] : null;
                bool used = info != null && info.used;
                playerRows[i].gameObject.SetActive(used);
                if (!used) continue;
                string s = "<color=#" + ColorUtility.ToHtmlStringRGB(NetSession.PlayerColor(i)) + ">●</color>  " + info.name + (info.host ? "  <color=#FFB84D>(host)</color>" : "") +
                           (i == net.LocalId ? "  <color=#9AA3AD>(you)</color>" : "") + (info.host ? "" : "   " + info.ping + " ms");
                playerRows[i].text = s;
            }
        }

        void UpdateCounters()
        {
            var q = Sandbox.Boot.Quality;
            cFps.text = NumberCache.Int(Mathf.RoundToInt(q.Fps));
            cMs.text = NumberCache.Tenths(q.FrameMs);
            int bodies = 0;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) bodies++;
            cBodies.text = NumberCache.Int(bodies);
            cParticles.text = NumberCache.Int(Sandbox.Fx != null ? Sandbox.Fx.ParticleCount : 0);
            cDroplets.text = NumberCache.Int(Sandbox.Blood != null ? Sandbox.Blood.DropletCount : 0);
            cDecals.text = NumberCache.Int(Sandbox.Decals != null ? Sandbox.Decals.Count : 0);
            cFps.color = q.Fps >= Sandbox.Boot.graphics.targetFps * 0.9f ? new Color(0.6f, 1f, 0.6f) : q.Fps >= 30f ? new Color(1f, 0.85f, 0.4f) : new Color(1f, 0.45f, 0.4f);
        }

        static Color ToolColor(int tool)
        {
            switch ((ToolId)tool)
            {
                case ToolId.Laser: return new Color(1f, 0.35f, 0.3f, 0.95f);
                case ToolId.Heal: return new Color(0.5f, 1f, 0.6f, 0.95f);
                case ToolId.Shock: return new Color(0.55f, 0.8f, 1f, 0.95f);
                case ToolId.Blade: return new Color(1f, 0.92f, 0.85f, 0.95f);
                default: return new Color(1f, 1f, 1f, 0.85f);
            }
        }

        public void LateTick()
        {
            float dt = Time.unscaledDeltaTime;
            var tools = Sandbox.Tools;
            // hotbar: animated highlight, cooldown and counts
            for (int i = 0; i < Slots; i++)
            {
                float target = i == currentTool ? 1f : 0f;
                float a = slotAnim[i];
                if (Mathf.Abs(a - target) > 0.001f)
                {
                    a = Mathf.MoveTowards(a, target, dt * 7f);
                    slotAnim[i] = a;
                    float e = MathUtil.SmoothStep01(a);
                    slotRt[i].localScale = new Vector3(1f + 0.1f * e, 1f + 0.1f * e, 1f);
                    slotRt[i].anchoredPosition = new Vector2(0f, 8f * e);
                    var gc = slotGlow[i].color; gc.a = 0.85f * e; slotGlow[i].color = gc;
                }
                float cd = tools != null && i == currentTool ? tools.Local.CooldownFraction : 0f;
                if (Mathf.Abs(slotCool[i].fillAmount - cd) > 0.002f) slotCool[i].fillAmount = cd;
            }
            if (Time.frameCount % 10 == 0) UpdateSlotCounts();
            // crosshair per tool
            bool cross = Sandbox.Cam != null && (Sandbox.Cam.Mode == CameraMode.FirstPerson || Sandbox.Cam.Mode == CameraMode.Follow) && !Sandbox.InMenu;
            if (crossRoot.gameObject.activeSelf != cross) crossRoot.gameObject.SetActive(cross);
            if (cross) UpdateCrosshair(dt);
            // status line
            string s = "";
            if (Sandbox.Rewind != null && Sandbox.Rewind.Rewinding) s = "<< REWIND";
            else if (TimeControl.SlowMotion) s = "SLOW MOTION";
            if (Sandbox.Cam != null && Sandbox.Cam.Mode == CameraMode.KillCam) s = "KILL CAM";
            if (Sandbox.XRay) s = s.Length > 0 ? (s == "SLOW MOTION" ? "SLOW MOTION   X-RAY" : s == "KILL CAM" ? "KILL CAM   X-RAY" : "<< REWIND   X-RAY") : "X-RAY";
            if (!ReferenceEquals(s, statusShown)) { statusShown = s; statusText.text = s; }
            // vignette when the possessed body is hurt
            float hurt = 0f;
            if (Sandbox.Possessed != null && Sandbox.Possessed.Vitals != null)
            {
                var v = Sandbox.Possessed.Vitals;
                hurt = Mathf.Clamp01((1f - v.BloodNorm) * 1.1f + v.Pain * 0.7f + (v.Dead ? 1f : 0f));
                hurt *= 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * Mathf.Max(1f, v.HeartRate) / 60f * Mathf.PI * 2f);
            }
            var vc = vignette.color;
            vc.a = Mathf.MoveTowards(vc.a, hurt * 0.85f, dt * 2f);
            vignette.color = vc;
            bool showHud = !Sandbox.InMenu;
            if (vitalsPanel.gameObject.activeSelf != showHud) vitalsPanel.gameObject.SetActive(showHud);
            if (settingsHandle.gameObject.activeSelf != (!SettingsVisible && showHud)) settingsHandle.gameObject.SetActive(!SettingsVisible && showHud);
        }

        void UpdateSlotCounts()
        {
            SetCount((int)ToolId.Explosives, Sandbox.Explosions != null && Sandbox.Explosions.CountC4() > 0 ? NumberCache.Int(Sandbox.Explosions.CountC4()) : "");
            SetCount((int)ToolId.Pistol, "∞");
            SetCount((int)ToolId.Shotgun, "∞");
            int cars = Sandbox.Vehicles != null ? Sandbox.Vehicles.Cars.Count : 0;
            SetCount((int)ToolId.Props, Sandbox.Tools != null && Sandbox.Tools.Local.PropIndex == 6 ? NumberCache.Int(cars) : "");
            int ragdolls = 0;
            for (int i = 0; i < Sandbox.Ragdolls.Count; i++) if (Sandbox.Ragdolls[i] != null) ragdolls++;
            SetCount((int)ToolId.Spawn, NumberCache.Int(ragdolls));
        }

        void SetCount(int i, string s)
        {
            if (ReferenceEquals(slotCountShown[i], s)) return;
            slotCountShown[i] = s;
            slotCount[i].text = s;
        }

        void UpdateCrosshair(float dt)
        {
            int tool = currentTool;
            float gap, len, rot = 0f, ringSize = 0f, dot = 5f, thick = 2f;
            switch ((ToolId)tool)
            {
                case ToolId.Grab: gap = 12f; len = 6f; ringSize = Sandbox.Tools != null && Sandbox.Tools.Grab.Active ? 18f : 26f; dot = 0f; break;
                case ToolId.Hammer: gap = 6f; len = 6f; thick = 3f; break;
                case ToolId.Blade: gap = 0f; len = 14f; rot = 45f; dot = 0f; break;
                case ToolId.Pistol: gap = 6f; len = 9f; break;
                case ToolId.Shotgun: gap = 16f; len = 6f; ringSize = 44f; dot = 3f; break;
                case ToolId.Explosives: gap = 10f; len = 4f; ringSize = 20f; break;
                case ToolId.Props: gap = 8f; len = 8f; rot = 45f; break;
                case ToolId.Heal: gap = 0f; len = 9f; thick = 4f; dot = 0f; break;
                case ToolId.Shock: gap = 8f; len = 7f; rot = 45f; dot = 4f; break;
                case ToolId.Laser: gap = 4f; len = 4f; dot = 4f; break;
                default: gap = 10f; len = 7f; break;
            }
            if (tool != crossTool) { crossTool = tool; crossColor = ToolColor(tool); }
            crossGap = Mathf.Lerp(crossGap, gap, Mathf.Min(1f, dt * 14f));
            crossLen = Mathf.Lerp(crossLen, len, Mathf.Min(1f, dt * 14f));
            crossRot = Mathf.LerpAngle(crossRot, rot, Mathf.Min(1f, dt * 14f));
            for (int i = 0; i < 4; i++)
            {
                var rt = crossLines[i].rectTransform;
                bool hor = i < 2;
                rt.sizeDelta = hor ? new Vector2(crossLen, thick) : new Vector2(thick, crossLen);
                float o = crossGap * 0.5f + crossLen * 0.5f;
                rt.anchoredPosition = hor ? new Vector2(i == 0 ? -o : o, 0f) : new Vector2(0f, i == 2 ? -o : o);
                crossLines[i].color = crossColor;
            }
            crossRoot.localRotation = Quaternion.Euler(0f, 0f, crossRot);
            bool ring = ringSize > 0f;
            if (crossRing.enabled != ring) crossRing.enabled = ring;
            if (ring) { crossRing.rectTransform.sizeDelta = new Vector2(ringSize, ringSize); var rc = crossColor; rc.a *= 0.55f; crossRing.color = rc; }
            bool hasDot = dot > 0f;
            if (crossDot.enabled != hasDot) crossDot.enabled = hasDot;
            if (hasDot) { crossDot.rectTransform.sizeDelta = new Vector2(dot, dot); crossDot.color = crossColor; }
        }

        static readonly string[] StateNames = Enum.GetNames(typeof(MotorState));

        void UpdateVitals()
        {
            var b = Sandbox.Focus;
            if (b == null || b.Vitals == null || b.Destroyed)
            {
                if (vitalsBody != null || shownState != -2) { vitalsBody = null; shownState = -2; vTitle.text = "No ragdoll  (0 to spawn)"; vState.text = ""; }
                return;
            }
            var v = b.Vitals;
            if (b != vitalsBody) { vitalsBody = b; vTitle.text = b.Label; shownState = -1; }
            int st = (int)b.Brain.State;
            if (st != shownState) { shownState = st; vState.text = StateNames[st]; }
            float health = 0f;
            for (int i = 0; i < PartInfo.Count; i++) { var p = b.Parts[i]; if (p != null && p.attached) health += p.HealthFraction; }
            health /= PartInfo.Count;
            bHealth.fillAmount = health; vHealth.text = NumberCache.Percent(health);
            bBlood.fillAmount = v.BloodFraction; vBlood.text = NumberCache.Percent(v.BloodFraction);
            bCons.fillAmount = v.Consciousness; vCons.text = NumberCache.Percent(v.Consciousness);
            bPain.fillAmount = v.Pain; vPain.text = NumberCache.Percent(v.Pain);
            vHeart.text = NumberCache.Int(Mathf.RoundToInt(v.HeartRate));
            float beat = v.HeartRate > 1f ? Mathf.Exp(-Mathf.Pow(v.HeartPhase * 6f, 2f)) : 0f;
            heartIcon.rectTransform.localScale = Vector3.one * (0.8f + beat * 0.5f);
            // status chips
            bool missing = false;
            int fractures = 0;
            for (int i = 0; i < PartInfo.Count; i++) { var p = b.Parts[i]; if (p == null || !p.attached || p.cutOff) missing = true; fractures += b.FractureCount[i]; }
            SetChip(0, (v.ExternalBleedRate + v.InternalBleedRate) > 0.0005f && !v.Dead);
            SetChip(1, fractures > 0);
            SetChip(2, missing);
            SetChip(3, v.Unconscious && !v.Dead);
            SetChip(4, v.Shock > 0.4f && !v.Dead);
            SetChip(5, v.CardiacArrest && !v.Dead);
            SetChip(6, v.Dead);
            SetChip(7, b.Possessed);
            // bone diagram
            for (int i = 0; i < PartInfo.Count; i++)
            {
                var p = b.Parts[i];
                Color c;
                int fc = b.FractureCount[i];
                if (p == null || !p.attached || p.cutOff) c = new Color(0.2f, 0.2f, 0.22f, 0.35f);
                else
                {
                    float h = p.HealthFraction;
                    c = h > 0.5f ? Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(0.35f, 0.85f, 0.42f), (h - 0.5f) * 2f) :
                        h > 0.15f ? Color.Lerp(new Color(0.9f, 0.15f, 0.12f), new Color(1f, 0.85f, 0.2f), (h - 0.15f) / 0.35f) :
                        Color.Lerp(new Color(0.08f, 0.02f, 0.02f), new Color(0.9f, 0.15f, 0.12f), h / 0.15f);
                    if (fc > 0 || p.fractured) c = Color.Lerp(c, new Color(1f, 0.55f, 0.1f), 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f));
                    if (p.paralyzed) c *= 0.6f;
                }
                diagram[i].color = c;
                string label = fc > 0 ? (fc == 1 ? "x" : NumberCache.Int(fc)) : "";
                if (!ReferenceEquals(diagramCount[i].text, label) && diagramCount[i].text != label) diagramCount[i].text = label;
            }
        }

        void SetChip(int i, bool on) { if (chips[i].go.activeSelf != on) chips[i].go.SetActive(on); }
        #endregion
    }
    #endregion
}
