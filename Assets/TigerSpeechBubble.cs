// Copyright 2024-2026, DisplayXR contributors
// SPDX-License-Identifier: BSL-1.0
//
// Tiger speech bubble — Local2D edition (#439/#491).
//
// REARCHITECTED from the 2D-surround showcase to the modern, mask-based
// Local2D path — the same approach the native VK avatar demo uses. The bubble
// is a single XrCompositionLayerLocal2DEXT layer composited "glass over 3D":
// the woven 3D under the bubble's rect goes flat 2D and the bubble is
// alpha-composited on top. The tiger now fills the whole window (no 2D/3D split
// sub-rect); only the bubble region is flat.
//
// Why this replaced the surround version: the surround path copied each frame
// from a Unity RenderTexture pointer captured once, so when Unity reallocated
// that RT the bubble vanished. The Local2D path (DisplayXRLocal2D) recreates
// its overlay swapchain whenever the RT changes, so the bubble can't go stale.
//
// Editor UX kept minimal: right-drag moves the bubble (persisted); Shift+B
// hides it. The old 2D/3D split + zone-border region editor was surround-only
// and is gone.

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using DisplayXR;

public class TigerSpeechBubble : MonoBehaviour
{
    [TextArea] public string title = "Hi, I'm Leo";
    [TextArea] public string body =
        "Drag me with the right mouse button. I'm a Local2D layer — flat 2D " +
        "glass composited over the 3D tiger.";

    // --- Bubble rect (fractions of the full screen), persisted ---
    private float m_BX = 0.36f, m_BY = 0.06f, m_BW = 0.30f, m_BH = 0.20f;
    private const string kPrefBX = "dxr_l2d_x", kPrefBY = "dxr_l2d_y";
    private const string kPrefBW = "dxr_l2d_w", kPrefBH = "dxr_l2d_h";

    private GameObject m_CanvasGO;
    private DisplayXRLocal2D m_Local2D;
    private Font m_Font;
    private int m_PanelW, m_PanelH;
    private bool m_FullscreenApplied;

    private RectTransform m_BubbleRT;
    private RectTransform m_TailRT;
    private VerticalLayoutGroup m_BubbleLayout;
    private bool m_BubbleHidden;

    // Right-drag translate state.
    private bool m_Translating;
    private bool m_PrevRightDown;
    private int m_TransAnchorPx, m_TransAnchorPy;
    private float m_TransAnchorBX, m_TransAnchorBY;

    /// <summary>True while the bubble is dragging — scene input controllers
    /// consult this and skip mouse handling so the drag doesn't rotate the
    /// tiger. (Kept from the surround version for API compatibility.)</summary>
    public static bool SuppressSceneInput { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        var go = new GameObject("TigerSpeechBubble");
        DontDestroyOnLoad(go);
        go.AddComponent<TigerSpeechBubble>();
    }

    void OnEnable()
    {
        m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (m_Font == null) m_Font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        LoadLayout();
    }

    void OnDisable()
    {
        SuppressSceneInput = false;
        SetFullscreen(false);
        if (m_CanvasGO != null)
        {
            if (Application.isPlaying) Destroy(m_CanvasGO);
            else DestroyImmediate(m_CanvasGO);
            m_CanvasGO = null;
        }
        m_BubbleRT = null; m_TailRT = null; m_Local2D = null;
        m_FullscreenApplied = false;
    }

    private bool TryResolvePanelDims()
    {
        if (DisplayXRSurround.TryGetTargetSize(Application.isEditor, out int w, out int h))
        {
            m_PanelW = w; m_PanelH = h;
            return true;
        }
        return false;
    }

    void Update()
    {
        if (m_CanvasGO == null)
        {
            if (!TryResolvePanelDims()) return;
            // Pin the overlay to its monitor ONCE (disables native move), then
            // re-read dims so the rect math matches the full screen.
            if (!m_FullscreenApplied)
            {
                SetFullscreen(true);
                m_FullscreenApplied = true;
                return;
            }
            if (!TryResolvePanelDims()) return;
            BuildUI();
        }

        bool havePtr = TryGetPointer(out int px, out int py, out bool _, out bool rightDown);
        HandleHotkey();
        if (havePtr) HandleTranslate(px, py, rightDown);
        SuppressSceneInput = m_Translating;

        ApplyLayout();
    }

    // ---------------------------------------------------------------- build ---

    void BuildUI()
    {
        // The bubble lives on its own Canvas driven by DisplayXRLocal2D, which
        // renders it into an offscreen RT and submits it as the Local2D layer.
        var canvasGO = new GameObject("TigerBubble_Local2D",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGO.SetActive(false);
        canvasGO.transform.SetParent(transform, false);
        canvasGO.layer = LayerMask.NameToLayer("UI");

        m_Local2D = canvasGO.AddComponent<DisplayXRLocal2D>();
        // RT resolution sized to a typical bubble; the layer stretches it into
        // the dest rect, so exact match isn't required.
        m_Local2D.resolution = new Vector2Int(1024, 640);
        m_Local2D.positionX = m_BX; m_Local2D.positionY = m_BY;
        m_Local2D.width = m_BW;     m_Local2D.height = m_BH;

        BuildBubble(canvasGO.transform);

        m_CanvasGO = canvasGO;
        canvasGO.SetActive(true);

        Debug.Log($"[TigerSpeechBubble] BUILD (Local2D): screen={m_PanelW}x{m_PanelH}; " +
                  $"bubble=[{m_BX:P0},{m_BY:P0},{m_BW:P0},{m_BH:P0}]; right-drag = move, Shift+B = hide");
    }

    void BuildBubble(Transform parent)
    {
        Color bubbleColor = new Color(0.06f, 0.07f, 0.11f, 0.86f);

        // Panel fills the layer (minus a slice at the bottom for the tail).
        var panelGO = MakeUI("Bubble", parent);
        m_BubbleRT = panelGO.GetComponent<RectTransform>();
        m_BubbleRT.anchorMin = new Vector2(0f, 0.12f);
        m_BubbleRT.anchorMax = new Vector2(1f, 1f);
        m_BubbleRT.offsetMin = Vector2.zero;
        m_BubbleRT.offsetMax = Vector2.zero;
        var img = panelGO.AddComponent<Image>();
        img.sprite = RoundedRect();
        img.type = Image.Type.Sliced;
        img.color = bubbleColor;

        var tailGO = MakeUI("Tail", panelGO.transform);
        tailGO.AddComponent<LayoutElement>().ignoreLayout = true;
        m_TailRT = tailGO.GetComponent<RectTransform>();
        m_TailRT.anchorMin = new Vector2(0.5f, 0f);
        m_TailRT.anchorMax = new Vector2(0.5f, 0f);
        m_TailRT.pivot = new Vector2(0.5f, 1f);
        m_TailRT.sizeDelta = new Vector2(70f, 60f);
        m_TailRT.anchoredPosition = new Vector2(0f, 2f);
        var tailImg = tailGO.AddComponent<Image>();
        tailImg.sprite = TriangleDown();
        tailImg.type = Image.Type.Simple;
        tailImg.color = bubbleColor;

        m_BubbleLayout = panelGO.AddComponent<VerticalLayoutGroup>();
        m_BubbleLayout.padding = new RectOffset(40, 40, 30, 30);
        m_BubbleLayout.spacing = 10;
        m_BubbleLayout.childControlWidth = true;
        m_BubbleLayout.childControlHeight = true;
        m_BubbleLayout.childForceExpandWidth = true;

        var t = MakeText(panelGO.transform, "Title", title, 38, FontStyle.Bold);
        t.color = new Color(0.85f, 0.92f, 1f, 1f);
        var b = MakeText(panelGO.transform, "Body", body, 28, FontStyle.Normal);
        b.color = new Color(0.92f, 0.94f, 0.98f, 1f);
    }

    // ----------------------------------------------------------- layout/apply ---

    void ApplyLayout()
    {
        if (m_Local2D == null) return;
        // Push the live fractional rect to the Local2D component (it converts to
        // the client-window pixel rect the layer needs).
        m_Local2D.positionX = m_BX;
        m_Local2D.positionY = m_BY;
        m_Local2D.width = m_BW;
        m_Local2D.height = m_BH;
    }

    // -------------------------------------------------------------- editor IO ---

    void HandleHotkey()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        bool ctrl  = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

        // Shift+B hides/shows the bubble panel (the layer goes inactive when the
        // Canvas renders nothing visible, so the tiger shows through cleanly).
        if (kb.bKey.wasPressedThisFrame && shift && !ctrl)
        {
            m_BubbleHidden = !m_BubbleHidden;
            if (m_BubbleRT != null) m_BubbleRT.gameObject.SetActive(!m_BubbleHidden);
            Debug.Log($"[TigerSpeechBubble] Bubble hidden = {m_BubbleHidden}");
        }
    }

    // Right-drag translates the bubble (fractions of the screen).
    void HandleTranslate(int px, int py, bool rightDown)
    {
        if (rightDown && !m_PrevRightDown)
        {
            m_Translating = true;
            m_TransAnchorPx = px; m_TransAnchorPy = py;
            m_TransAnchorBX = m_BX; m_TransAnchorBY = m_BY;
        }
        if (rightDown && m_Translating && m_PanelW > 0 && m_PanelH > 0)
        {
            float dx = (float)(px - m_TransAnchorPx) / m_PanelW;
            float dy = (float)(py - m_TransAnchorPy) / m_PanelH;
            m_BX = Mathf.Clamp(m_TransAnchorBX + dx, 0f, 1f - m_BW);
            m_BY = Mathf.Clamp(m_TransAnchorBY + dy, 0f, 1f - m_BH);
        }
        if (!rightDown && m_PrevRightDown && m_Translating)
        {
            m_Translating = false;
            SaveLayout();
        }
        m_PrevRightDown = rightDown;
    }

    private bool TryGetPointer(out int x, out int y, out bool leftDown, out bool rightDown)
    {
        x = y = 0; leftDown = rightDown = false;
        try
        {
            DisplayXRNative.displayxr_get_overlay_pointer(out int px, out int py, out int buttons);
            if (px < 0 || py < 0) return false;
            x = px; y = py;
            leftDown  = (buttons & 1) != 0;
            rightDown = (buttons & 2) != 0;
            return true;
        }
        catch (System.EntryPointNotFoundException) { return false; }
    }

    private void SetFullscreen(bool on)
    {
        try { DisplayXRNative.displayxr_set_overlay_fullscreen(on ? 1 : 0); }
        catch (System.EntryPointNotFoundException) { }
    }

    // ------------------------------------------------------------ persistence ---

    private void LoadLayout()
    {
        m_BX = PlayerPrefs.GetFloat(kPrefBX, m_BX);
        m_BY = PlayerPrefs.GetFloat(kPrefBY, m_BY);
        m_BW = PlayerPrefs.GetFloat(kPrefBW, m_BW);
        m_BH = PlayerPrefs.GetFloat(kPrefBH, m_BH);
    }

    private void SaveLayout()
    {
        PlayerPrefs.SetFloat(kPrefBX, m_BX);
        PlayerPrefs.SetFloat(kPrefBY, m_BY);
        PlayerPrefs.SetFloat(kPrefBW, m_BW);
        PlayerPrefs.SetFloat(kPrefBH, m_BH);
        PlayerPrefs.Save();
    }

    // -------------------------------------------------------------- UI helpers ---

    GameObject MakeUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.layer = LayerMask.NameToLayer("UI");
        return go;
    }

    Text MakeText(Transform parent, string name, string content, int size, FontStyle style)
    {
        var go = MakeUI(name, parent);
        var t = go.AddComponent<Text>();
        t.font = m_Font;
        t.fontSize = size;
        t.fontStyle = style;
        t.text = content;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    // Procedural 9-slice rounded-rect sprite (white; tinted via Image.color).
    private static Sprite s_Rounded;
    private static Sprite RoundedRect()
    {
        if (s_Rounded != null) return s_Rounded;
        const int size = 64, radius = 30;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int dx = (x < radius) ? (radius - 1 - x) : (x >= size - radius) ? (x - (size - radius)) : 0;
                int dy = (y < radius) ? (radius - 1 - y) : (y >= size - radius) ? (y - (size - radius)) : 0;
                float a = 1f;
                if (dx > 0 || dy > 0)
                    a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        s_Rounded = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        s_Rounded.hideFlags = HideFlags.HideAndDontSave;
        return s_Rounded;
    }

    // Procedural downward-pointing triangle (white; tinted via Image.color).
    private static Sprite s_Tail;
    private static Sprite TriangleDown()
    {
        if (s_Tail != null) return s_Tail;
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var px = new Color32[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)        // y=0 = bottom = apex
            for (int x = 0; x < size; x++)
            {
                float t = (float)y / (size - 1);
                float halfW = t * half;
                float d = halfW - Mathf.Abs((x + 0.5f) - half);
                float a = Mathf.Clamp01(d);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        s_Tail = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect);
        s_Tail.hideFlags = HideFlags.HideAndDontSave;
        return s_Tail;
    }
}
