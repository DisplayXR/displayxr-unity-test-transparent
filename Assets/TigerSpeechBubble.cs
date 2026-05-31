// Copyright 2024-2026, DisplayXR contributors
// SPDX-License-Identifier: BSL-1.0

using System.Runtime.InteropServices;
using DisplayXR;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Demo of the 2D surround feature (issue #131): a high-resolution 2D text
/// bubble composited POST-weave over the woven 3D tiger.
///
/// Unlike the window-space UI HUD (TigerTuningHUD, which composites pre-weave
/// and is only full-res at the display plane), the surround is written after
/// interlacing — so the text is always at full native panel resolution. The
/// tiger is shrunk into a centered canvas sub-rect; the bubble lives in the
/// surround region above it.
///
/// Auto-installs via RuntimeInitializeOnLoadMethod (no scene wiring). Coexists
/// with TigerTuningHUD (wsui) — different runtime mechanisms, no conflict.
/// SHIFT+B toggles visibility. Windows D3D12 built apps only.
/// </summary>
public class TigerSpeechBubble : MonoBehaviour
{
    // Canvas sub-rect for the 3D tiger, as fractions of the window. The bubble
    // sits in the surround region above this rect.
    [Range(0f, 1f)] public float tigerX = 0.20f;
    [Range(0f, 1f)] public float tigerY = 0.28f;
    [Range(0f, 1f)] public float tigerW = 0.60f;
    [Range(0f, 1f)] public float tigerH = 0.66f;

    [Header("Bubble")]
    [TextArea] public string title = "Hi, I'm Leo";
    [TextArea] public string body =
        "your on-screen assistant. I can open apps, find your files, " +
        "and keep your windows tidy. Just ask!\n\nSo, how can I help you today?";

    private GameObject m_CanvasGO;
    private DisplayXRSurround m_Surround;
    private Font m_Font;
    private int m_PanelW, m_PanelH;
    private bool m_Visible = true;

    // The bubble panel + tail + canvas, used to rasterize the bubble's exact
    // window-pixel shape into a click-through mask so the transparent overlay
    // catches clicks on the bubble (it lives in the surround region, outside the
    // 3D silhouette) while the empty area around it routes to the desktop.
    // See PushBubbleMask / TryRectToWindow.
    private RectTransform m_CanvasRT;
    private RectTransform m_BubbleRT;
    private RectTransform m_TailRT;   // hangs below the panel; included in the pushed shape
    private bool m_MaskPushed;
    private RectInt m_LastMaskBox = new RectInt(-1, -1, -1, -1);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (Object.FindAnyObjectByType<TigerSpeechBubble>() != null) return;
        var go = new GameObject("TigerSpeechBubble");
        go.AddComponent<TigerSpeechBubble>();
        Debug.Log("[TigerSpeechBubble] Auto-installed into scene.");
    }

    void OnEnable()
    {
        m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (m_Font == null) m_Font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        // Defer BuildBubble until the runtime reports valid display dims — the
        // surround texture MUST match the window client area or the runtime
        // rejects it (XR_ERROR_VALIDATION_FAILURE). Update() polls and builds.
    }

    private bool TryResolvePanelDims()
    {
        // Use the runtime's actual weave-target size (the bound HWND client area),
        // NOT the display panel dims — the surround texture + canvas sub-rect must
        // match it or the runtime skips the surround blit (#131).
        if (DisplayXRSurround.TryGetTargetSize(Application.isEditor, out int w, out int h))
        {
            m_PanelW = w;
            m_PanelH = h;
            return true;
        }
        return false;
    }

    void OnDisable()
    {
        ClearBubbleMask();
        if (m_CanvasGO != null)
        {
            if (Application.isPlaying) Destroy(m_CanvasGO);
            else DestroyImmediate(m_CanvasGO);
            m_CanvasGO = null;
        }
        m_CanvasRT = null;
        m_BubbleRT = null;
    }

    void BuildBubble()
    {
        // Root canvas (the surround source). DisplayXRSurround takes it over as
        // a WorldSpace canvas and renders it into a panel-sized RGBA8 RT.
        var canvasGO = new GameObject("TigerBubble_Canvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGO.SetActive(false);
        canvasGO.transform.SetParent(transform, false);
        canvasGO.layer = LayerMask.NameToLayer("UI");
        canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay; // surround switches this

        m_Surround = canvasGO.AddComponent<DisplayXRSurround>();
        m_Surround.resolution = new Vector2Int(m_PanelW, m_PanelH);
        m_Surround.setCanvasRect = true;
        m_Surround.canvasRect = new RectInt(
            Mathf.RoundToInt(tigerX * m_PanelW),
            Mathf.RoundToInt(tigerY * m_PanelH),
            Mathf.RoundToInt(tigerW * m_PanelW),
            Mathf.RoundToInt(tigerH * m_PanelH));

        // Dark, translucent comic speech bubble, top-center in the surround region
        // above the tiger, with a tail pointing down at it. Width 50% of window.
        Color bubbleColor = new Color(0.06f, 0.07f, 0.11f, 0.86f); // dark translucent
        m_CanvasRT = canvasGO.GetComponent<RectTransform>();
        var panelGO = MakeUI("Bubble", canvasGO.transform);
        var prt = panelGO.GetComponent<RectTransform>();
        m_BubbleRT = prt;
        prt.anchorMin = new Vector2(0.5f, 1f);
        prt.anchorMax = new Vector2(0.5f, 1f);
        prt.pivot = new Vector2(0.5f, 1f);
        prt.sizeDelta = new Vector2(m_PanelW * 0.50f, m_PanelH * 0.21f);
        prt.anchoredPosition = new Vector2(0f, -m_PanelH * 0.025f); // small top margin
        var img = panelGO.AddComponent<Image>();
        img.sprite = RoundedRect();
        img.type = Image.Type.Sliced;
        img.color = bubbleColor;

        // Comic tail: a triangle hanging off the bubble's bottom edge, pointing
        // straight DOWN at the tiger. Child of the panel so it shows/hides with
        // SHIFT+B, but ignoreLayout so the VerticalLayoutGroup leaves it alone.
        // Centered (x = 0): the tiger's canvas sub-rect is horizontally centered
        // (tigerX + tigerW/2 = 0.50, same as the bubble), so the apex must point
        // straight down to land on it — any horizontal lean detaches the tail
        // from the tiger and reads as a stray nub. Its wide base overlaps up into
        // the bubble bottom so the two read as one continuous shape.
        var tailGO = MakeUI("Tail", panelGO.transform);
        tailGO.AddComponent<LayoutElement>().ignoreLayout = true;
        var trt = tailGO.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0.5f, 0f);   // panel bottom-center
        trt.anchorMax = new Vector2(0.5f, 0f);
        trt.pivot = new Vector2(0.5f, 1f);        // hang downward from the top
        // Small, discrete tail. It hangs below the bubble but must stop ABOVE the
        // tiger's canvas sub-rect (tigerY): the surround only paints the non-canvas
        // region, so a tail dipping into the 3D rect would be clipped there.
        float tailW = m_PanelW * 0.038f;
        float tailH = m_PanelH * 0.035f;
        // Hairline overlap — just closes the seam against the bubble's bottom edge
        // without a visibly darker doubled band (both shapes are translucent, so a
        // larger overlap reads as a second darker shape tucked into the bubble).
        float tailOverlap = m_PanelH * 0.002f;
        trt.sizeDelta = new Vector2(tailW, tailH);
        trt.anchoredPosition = new Vector2(0f, tailOverlap); // centered; base overlaps up into the bubble
        m_TailRT = trt;
        var tailImg = tailGO.AddComponent<Image>();
        tailImg.sprite = TriangleDown();
        tailImg.type = Image.Type.Simple;
        tailImg.color = bubbleColor;

        var layout = panelGO.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(44, 44, 28, 32);
        layout.spacing = 8;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;

        var t = MakeText(panelGO.transform, "Title", title, 40, FontStyle.Bold);
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        var b = MakeText(panelGO.transform, "Body", body, 28, FontStyle.Normal);
        b.alignment = TextAnchor.MiddleCenter;
        b.color = new Color(0.82f, 0.86f, 0.95f, 1f);  // light slate

        m_CanvasGO = canvasGO;
        // Keep the canvas (and its DisplayXRSurround) ALWAYS active so the canvas
        // sub-rect + surround registration persist; SHIFT+B toggles only the
        // bubble panel. Disabling the whole canvas would tear down the surround
        // and clear the sub-rect, snapping the tiger back to full-window.
        canvasGO.SetActive(true);
        m_BubbleRT.gameObject.SetActive(m_Visible);

        // DIAG: prove which build is running. If you don't see this line in
        // Player.log with width≈50% and tail=True, the player is stale — rebuild.
        Debug.Log($"[TigerSpeechBubble] BUILD v6 (taller bubble, discrete tail): panel={prt.sizeDelta.x:F0}x{prt.sizeDelta.y:F0}px " +
                  $"= {(prt.sizeDelta.x / Mathf.Max(1, m_PanelW)):P0} of window {m_PanelW}x{m_PanelH}; " +
                  $"tail={(tailGO != null)} ({trt.sizeDelta.x:F0}x{trt.sizeDelta.y:F0}px, x-offset=0 centered, " +
                  $"hang={(tailH - tailOverlap):F0}px below); cornerRadius=30");
    }

    void Update()
    {
        // Build once the runtime reports valid panel dims.
        if (m_CanvasGO == null)
        {
            if (TryResolvePanelDims()) BuildBubble();
            else return;
        }

        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.bKey.wasPressedThisFrame &&
            (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) &&
            m_CanvasGO != null)
        {
            m_Visible = !m_Visible;
            if (m_BubbleRT != null) m_BubbleRT.gameObject.SetActive(m_Visible);
            Debug.Log($"[TigerSpeechBubble] toggle SHIFT+B → visible={m_Visible}");
        }

        PushBubbleMask();
    }

    // --- Click-through region for the bubble (per-pixel mask) ---

    // The bubble (rounded panel + triangular tail) sits in the 2D surround region,
    // OUTSIDE the 3D tiger silhouette that drives the transparent overlay's
    // SetWindowRgn click-through. We rasterize the bubble's EXACT shape into a CPU
    // alpha mask and hand it to the plugin, which RLE-unions it into the SAME
    // window region as the tiger. Because the surround is flat post-weave 2D (no
    // disparity, no per-view union), the shape is known up front — no GPU
    // silhouette pass like the tiger needs. Per-pixel is what lets the empty
    // corners BESIDE the tail keep routing clicks to the desktop; a single
    // bounding rect would catch them. No-op outside the built transparent app
    // (EntryPointNotFound on the non-Windows stub / older plugin).
    private void PushBubbleMask()
    {
        if (!(m_Visible && m_CanvasGO != null) ||
            !TryRectToWindow(m_BubbleRT, out float pL, out float pT, out float pW, out float pH) ||
            !TryRectToWindow(m_TailRT,   out float tL, out float tT, out float tW, out float tH))
        {
            ClearBubbleMask();
            return;
        }

        // Bounding box over panel + tail (window px, top-left origin).
        float bL = Mathf.Min(pL, tL);
        float bT = Mathf.Min(pT, tT);
        float bR = Mathf.Max(pL + pW, tL + tW);
        float bB = Mathf.Max(pT + pH, tT + tH);
        var box = new RectInt(Mathf.FloorToInt(bL), Mathf.FloorToInt(bT),
                              Mathf.CeilToInt(bR - bL), Mathf.CeilToInt(bB - bT));
        if (box.width <= 0 || box.height <= 0) { ClearBubbleMask(); return; }

        // The bubble is static; only rebuild + re-push when its box changes (window
        // resize). The plugin retains the mask and re-unions it every frame, so the
        // tiger's per-frame hit-mask updates keep the bubble region alive.
        if (m_MaskPushed && box.Equals(m_LastMaskBox)) return;

        // Rasterize at ~1 cell per 4 window px — plenty for hit-testing.
        int mw = Mathf.Clamp(box.width  / 4, 8, 320);
        int mh = Mathf.Clamp(box.height / 4, 8, 320);
        var mask = new byte[mw * mh];

        const float cornerR = 30f;             // panel rounded-corner radius (window px)
        float tailCx = tL + tW * 0.5f;
        for (int j = 0; j < mh; j++)
        {
            float wy = box.y + (j + 0.5f) / mh * box.height;
            for (int i = 0; i < mw; i++)
            {
                float wx = box.x + (i + 0.5f) / mw * box.width;
                bool inside = InRoundedRect(wx, wy, pL, pT, pW, pH, cornerR);
                if (!inside && tH > 0f && wy >= tT && wy <= tT + tH)
                {
                    // Tail triangle: base (full width) at the top edge, apex at the
                    // bottom edge (pointing down at the tiger).
                    float frac  = (wy - tT) / tH;            // 0 base → 1 apex
                    float halfW = (1f - frac) * tW * 0.5f;
                    inside = Mathf.Abs(wx - tailCx) <= halfW;
                }
                if (inside) mask[j * mw + i] = 255;
            }
        }

        var handle = GCHandle.Alloc(mask, GCHandleType.Pinned);
        try
        {
            DisplayXRNative.displayxr_set_overlay_surround_mask(
                handle.AddrOfPinnedObject(), mw, mh,
                box.x, box.y, box.width, box.height);
            m_MaskPushed = true;
            m_LastMaskBox = box;
        }
        catch (System.EntryPointNotFoundException) { /* old plugin / non-Win stub */ }
        finally { handle.Free(); }
    }

    private void ClearBubbleMask()
    {
        if (!m_MaskPushed) return;
        try
        {
            DisplayXRNative.displayxr_set_overlay_surround_mask(
                System.IntPtr.Zero, 0, 0, 0, 0, 0, 0);
        }
        catch (System.EntryPointNotFoundException) { }
        m_MaskPushed = false;
        m_LastMaskBox = new RectInt(-1, -1, -1, -1);
    }

    // Signed-distance test for a rounded rect (window px). Corners within radius r
    // of an inner corner point are outside, matching the bubble sprite's rounding
    // so clicks in the rounded corners still route to the desktop.
    private static bool InRoundedRect(float px, float py,
                                      float L, float T, float W, float H, float r)
    {
        if (px < L || px > L + W || py < T || py > T + H) return false;
        r = Mathf.Min(r, Mathf.Min(W, H) * 0.5f);
        float cx = Mathf.Clamp(px, L + r, L + W - r);
        float cy = Mathf.Clamp(py, T + r, T + H - r);
        float dx = px - cx, dy = py - cy;
        return dx * dx + dy * dy <= r * r;
    }

    // One RectTransform → window-pixel rect (top-left origin, float), via the
    // surround texture mapping (1 UI unit = 1 window px; flipY maps canvas-up to
    // window-up). Robust to anchors/pivot: normalize world corners vs the canvas.
    private bool TryRectToWindow(RectTransform rt,
                                 out float left, out float top, out float w, out float h)
    {
        left = top = w = h = 0f;
        if (rt == null || m_CanvasRT == null || m_Surround == null) return false;
        if (m_PanelW <= 0 || m_PanelH <= 0) return false;
        Rect cr = m_CanvasRT.rect;
        if (cr.width <= 0f || cr.height <= 0f) return false;

        Vector3[] wc = new Vector3[4];
        rt.GetWorldCorners(wc); // 0=BL, 1=TL, 2=TR, 3=BR (world space)
        Vector3 bl = m_CanvasRT.InverseTransformPoint(wc[0]);
        Vector3 tr = m_CanvasRT.InverseTransformPoint(wc[2]);
        float uMin = Mathf.Clamp01((bl.x - cr.xMin) / cr.width);
        float uMax = Mathf.Clamp01((tr.x - cr.xMin) / cr.width);
        float vMin = Mathf.Clamp01((bl.y - cr.yMin) / cr.height);
        float vMax = Mathf.Clamp01((tr.y - cr.yMin) / cr.height);

        left = uMin * m_PanelW;
        w    = (uMax - uMin) * m_PanelW;
        h    = (vMax - vMin) * m_PanelH;
        top  = m_Surround.flipY ? (1f - vMax) * m_PanelH : vMin * m_PanelH;
        return w > 0f && h > 0f;
    }

    // --- UI helpers ---

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

    // Procedural 9-slice rounded-rect sprite (white, alpha inside; tinted via
    // Image.color). Same approach as TigerTuningHUD.
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

    // Procedural downward-pointing triangle (white, alpha inside; tinted via
    // Image.color) for the comic speech-bubble tail. Apex at the bottom-center
    // (texture row 0), base across the top — so it hangs off the bubble's bottom
    // edge and points down at the tiger. ~1px edge antialiasing on the slants.
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
        for (int y = 0; y < size; y++)        // y=0 = bottom row = apex
            for (int x = 0; x < size; x++)
            {
                float t = (float)y / (size - 1);                 // 0 apex (bottom) → 1 base (top)
                float halfW = t * half;                          // triangle half-width at this row
                float d = halfW - Mathf.Abs((x + 0.5f) - half);  // >0 inside (≈px units)
                float a = Mathf.Clamp01(d);                      // 1px AA on the slanted edges
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        // FullRect mesh (not the default Tight): a Tight mesh hugs the alpha and
        // can trim/skew a runtime-generated triangle; FullRect maps the whole
        // texture onto the quad so the triangle renders exactly as drawn.
        s_Tail = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect);
        s_Tail.hideFlags = HideFlags.HideAndDontSave;
        return s_Tail;
    }
}
