// Copyright 2024-2026, DisplayXR contributors
// SPDX-License-Identifier: BSL-1.0

using System.Runtime.InteropServices;
using DisplayXR;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Demo of the 2D surround feature (issue #131) with an in-app REGION EDITOR,
/// rearchitected around a FIXED FULL-SCREEN overlay + a VIRTUAL WINDOW rect.
///
/// The native overlay is pinned to its monitor at the lenticular-aligned origin
/// (displayxr_set_overlay_fullscreen) and never resized. The "window" is a
/// virtual rect inside that full-screen surface; the 2D/3D areas are rects/lines
/// inside the virtual window. Moving/resizing the window is pure rectangle math
/// pushed per-frame (displayxr_set_canvas_rect for the 3D sub-rect, the surround
/// mask for clicks) — no OS move/resize, so no shift-then-snap, no mid-resize
/// content drop, and no #61 phase-snap.
///
/// Layout (all in full-screen pixels, top-left origin):
///   - Virtual window = [m_VX,m_VY,m_VW,m_VH] (fractions of the screen).
///   - 3D tiger rect = the canvas sub-rect = the area from the split line down to
///     the window bottom, full window width, INSET by the outline thickness so a
///     surround border exists for the outline to render in.
///   - 2D zone = the top strip above the split, between left/right borders. The
///     comic bubble fills it (rescales, text wraps). The empty surround routes
///     clicks to the desktop (per-pixel bubble mask).
///
/// Interaction (pointer via displayxr_get_overlay_pointer — Unity's mouse is
/// frozen in overlay mode):
///   - Right-drag (over the bubble/tiger in normal mode, anywhere in Layout
///     mode) → TRANSLATE the virtual window.
///   - Ctrl+Shift+L → toggle Layout mode: the whole virtual window catches
///     clicks and the outline + edge/corner resize handles + internal split/
///     border handles appear. Left-drag an outline edge/corner → resize; left-
///     drag the split/borders → retune the 2D/3D regions.
/// Layout persists across runs (PlayerPrefs).
///
/// Auto-installs via RuntimeInitializeOnLoadMethod. Windows D3D12 built apps
/// (transparent surround renders only in a built player; Editor Preview is black,
/// but geometry still validates).
/// </summary>
public class TigerSpeechBubble : MonoBehaviour
{
    [Header("Bubble text")]
    [TextArea] public string title = "Hi, I'm Leo";
    [TextArea] public string body =
        "your on-screen assistant. I can open apps, find your files, " +
        "and keep your windows tidy. Just ask!\n\nSo, how can I help you today?";

    // --- Virtual window rect (fractions of the full screen), persisted ---
    private float m_VX = 0.22f, m_VY = 0.12f, m_VW = 0.56f, m_VH = 0.74f;
    private const string kPrefVX = "dxr_vw_x", kPrefVY = "dxr_vw_y";
    private const string kPrefVW = "dxr_vw_w", kPrefVH = "dxr_vw_h";
    private const float kMinVW = 0.15f, kMinVH = 0.15f;

    // --- Region split/borders (fractions RELATIVE to the virtual window), persisted ---
    private float m_SplitFrac = 0.30f;   // 2D/3D boundary, fraction of window height
    private float m_ZoneLeft  = 0.10f;   // 2D zone left border, fraction of window width
    private float m_ZoneRight = 0.90f;   // 2D zone right border
    private const string kPrefSplit = "dxr_region_split";
    private const string kPrefLeft  = "dxr_region_left";
    private const string kPrefRight = "dxr_region_right";

    // Editing limits (fractions of the virtual window).
    private const float kMinSplit = 0.12f, kMaxSplit = 0.85f;
    private const float kMinZoneW = 0.10f;   // min 2D zone width
    private const float kZoneEdge = 0.02f;   // keep borders this far off the window edge

    // Gizmo / hit-test geometry (full-screen px).
    private const float kOutline     = 6f;   // outline thickness == 3D sub-rect inset
    private const float kHandle      = 16f;  // corner handle square size
    private const int   kGrab        = 18;   // edge / internal grab tolerance
    private const int   kGrabCorner  = 24;   // corner grab tolerance

    private GameObject m_CanvasGO;
    private DisplayXRSurround m_Surround;
    private RectTransform m_CanvasRT;
    private Font m_Font;
    private int m_PanelW, m_PanelH;            // full-screen (render-target) size
    private bool m_FullscreenApplied;

    // Bubble
    private RectTransform m_BubbleRT;
    private RectTransform m_TailRT;
    private VerticalLayoutGroup m_BubbleLayout;

    // Editor gizmos (children of the surround canvas; shown only in Layout mode)
    private GameObject m_GizmoRoot;
    private RectTransform m_TitleBar;
    private RectTransform m_SplitBar, m_LeftBar, m_RightBar;          // internal handles
    private RectTransform m_OutT, m_OutB, m_OutL, m_OutR;             // window outline
    private RectTransform m_HTL, m_HTR, m_HBL, m_HBR;                 // corner handles
    private bool m_ShowWindow;
    private bool m_BubbleHidden;   // Shift+B toggles just the bubble panel

    // Drag state (pointer comes from the native overlay, not Unity's frozen mouse)
    private enum Handle { None, Split, ZoneLeft, ZoneRight,
                          EdgeL, EdgeR, EdgeT, EdgeB,
                          CornerTL, CornerTR, CornerBL, CornerBR }
    private Handle m_Drag = Handle.None;
    private bool m_PrevLeftDown;

    // Translate (right-drag) state — works in both normal and Layout mode.
    private bool m_Translating;
    private bool m_PrevRightDown;
    private int m_TransAnchorPx, m_TransAnchorPy;
    private float m_TransAnchorVX, m_TransAnchorVY;

    // Click-through mask
    private bool m_MaskPushed;
    private RectInt m_LastMaskBox = new RectInt(-2, -2, -2, -2);
    private bool m_CanvasRectPushed;
    private int m_LastCursor = -1;

    /// <summary>
    /// True while the region editor owns the mouse (Layout mode, or an active
    /// right-drag translate). Scene input scripts (e.g. DragRotateCube) gate on
    /// this so editing the window doesn't also rotate the tiger.
    /// </summary>
    public static bool SuppressSceneInput { get; private set; }

    private const float kBubbleMargin = 0.018f; // bubble inset inside the 2D zone (window-W frac)
    private const float kTailW = 0.035f;        // tail width (window-W frac)
    private const float kTailH = 0.030f;        // tail height (window-H frac)

    // NOTE: the fullscreen-overlay request must happen at SubsystemRegistration
    // (before the OpenXR session is created) — it lives in TransparentAutoSetup
    // alongside the transparent-session request. Doing it here (AfterSceneLoad)
    // or even BeforeSplashScreen is too late: the overlay already exists, so the
    // app would fall back to a post-creation resize (swapchain recreate = flash).
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
        LoadLayout();
    }

    void OnDisable()
    {
        SuppressSceneInput = false;
        SetCursorShape(0);
        SetFullscreen(false);
        ClearMask();
        ClearCanvasRect();
        if (m_CanvasGO != null)
        {
            if (Application.isPlaying) Destroy(m_CanvasGO);
            else DestroyImmediate(m_CanvasGO);
            m_CanvasGO = null;
        }
        m_CanvasRT = null; m_BubbleRT = null; m_TailRT = null;
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
            // Resolving the render-target size implies the overlay HWND exists.
            if (!TryResolvePanelDims()) return;
            // Pin the overlay to its monitor ONCE (disables native move), then
            // re-read dims next frame so the surround RT matches the full screen.
            if (!m_FullscreenApplied)
            {
                SetFullscreen(true);
                m_FullscreenApplied = true;
                return;
            }
            if (!TryResolvePanelDims()) return;
            BuildUI();
        }

        bool havePtr = TryGetPointer(out int px, out int py, out bool leftDown, out bool rightDown);

        HandleHotkey();
        if (havePtr)
        {
            HandleTranslate(px, py, rightDown);          // right-drag move (both modes)
            if (m_ShowWindow) HandleEditorDrag(px, py, leftDown);
        }

        // The editor owns the mouse in Layout mode or during a translate, so
        // scene input (tiger rotation) is suppressed and resize cursors shown.
        SuppressSceneInput = m_ShowWindow || m_Translating;
        UpdateCursor(havePtr, px, py);

        ApplyLayout();   // position bubble + gizmos, push canvas rect
        PushMask();      // catch-region for clicks
    }

    // ---------------------------------------------------------------- build ---

    void BuildUI()
    {
        var canvasGO = new GameObject("TigerBubble_Canvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGO.SetActive(false);
        canvasGO.transform.SetParent(transform, false);
        canvasGO.layer = LayerMask.NameToLayer("UI");
        canvasGO.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay; // surround switches this
        m_CanvasRT = canvasGO.GetComponent<RectTransform>();

        m_Surround = canvasGO.AddComponent<DisplayXRSurround>();
        m_Surround.resolution = new Vector2Int(m_PanelW, m_PanelH);
        m_Surround.setCanvasRect = false; // the editor owns the canvas rect (it moves live)

        BuildBubble(canvasGO.transform);
        BuildGizmos(canvasGO.transform);

        m_CanvasGO = canvasGO;
        canvasGO.SetActive(true);

        if (m_GizmoRoot != null) m_GizmoRoot.SetActive(m_ShowWindow);

        Debug.Log($"[TigerSpeechBubble] BUILD v10 (full-screen + virtual window; cursor + drag fixes): screen={m_PanelW}x{m_PanelH}; " +
                  $"window=[{m_VX:P0},{m_VY:P0},{m_VW:P0},{m_VH:P0}] split={m_SplitFrac:P0} " +
                  $"zone=[{m_ZoneLeft:P0},{m_ZoneRight:P0}]; Ctrl+Shift+L = Layout, right-drag = move, Shift+B = hide bubble");
    }

    void BuildBubble(Transform parent)
    {
        Color bubbleColor = new Color(0.06f, 0.07f, 0.11f, 0.86f);

        var panelGO = MakeUI("Bubble", parent);
        m_BubbleRT = panelGO.GetComponent<RectTransform>();
        var img = panelGO.AddComponent<Image>();
        img.sprite = RoundedRect();
        img.type = Image.Type.Sliced;
        img.color = bubbleColor;

        var tailGO = MakeUI("Tail", panelGO.transform);
        tailGO.AddComponent<LayoutElement>().ignoreLayout = true;
        m_TailRT = tailGO.GetComponent<RectTransform>();
        m_TailRT.anchorMin = new Vector2(0.5f, 0f);
        m_TailRT.anchorMax = new Vector2(0.5f, 0f);
        m_TailRT.pivot = new Vector2(0.5f, 1f); // hang downward from the panel bottom
        var tailImg = tailGO.AddComponent<Image>();
        tailImg.sprite = TriangleDown();
        tailImg.type = Image.Type.Simple;
        tailImg.color = bubbleColor;

        m_BubbleLayout = panelGO.AddComponent<VerticalLayoutGroup>();
        m_BubbleLayout.childAlignment = TextAnchor.MiddleCenter;
        m_BubbleLayout.childControlWidth = true;
        m_BubbleLayout.childControlHeight = true;
        m_BubbleLayout.childForceExpandWidth = true;

        var t = MakeText(panelGO.transform, "Title", title, 40, FontStyle.Bold);
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        var b = MakeText(panelGO.transform, "Body", body, 28, FontStyle.Normal);
        b.alignment = TextAnchor.MiddleCenter;
        b.color = new Color(0.82f, 0.86f, 0.95f, 1f);
    }

    void BuildGizmos(Transform parent)
    {
        m_GizmoRoot = MakeUI("RegionGizmos", parent);
        StretchFull(m_GizmoRoot.GetComponent<RectTransform>());

        Color accent  = new Color(0.20f, 0.80f, 1f, 0.85f);   // internal handles
        Color outline = new Color(0.20f, 0.80f, 1f, 0.65f);   // window outline
        Color corner  = new Color(0.20f, 0.90f, 1f, 0.95f);   // corner grab handles
        Color barBg   = new Color(0.04f, 0.05f, 0.08f, 0.80f); // title hint bar

        m_TitleBar = MakeBar(m_GizmoRoot.transform, "TitleBar", barBg);
        var tlabel = MakeText(m_TitleBar.transform, "TitleBarLabel",
            "DisplayXR — right-drag: move • drag edges/corners: resize • drag split/borders: regions • Ctrl+Shift+L: close",
            22, FontStyle.Bold);
        tlabel.alignment = TextAnchor.MiddleCenter;
        tlabel.color = new Color(0.8f, 0.9f, 1f, 0.9f);
        tlabel.horizontalOverflow = HorizontalWrapMode.Overflow;
        StretchFull(tlabel.rectTransform);

        // Internal region handles.
        m_SplitBar = MakeBar(m_GizmoRoot.transform, "SplitLine", accent);
        m_LeftBar  = MakeBar(m_GizmoRoot.transform, "LeftBorder", accent);
        m_RightBar = MakeBar(m_GizmoRoot.transform, "RightBorder", accent);

        // Virtual-window outline (perimeter) + corner resize handles.
        m_OutT = MakeBar(m_GizmoRoot.transform, "OutlineT", outline);
        m_OutB = MakeBar(m_GizmoRoot.transform, "OutlineB", outline);
        m_OutL = MakeBar(m_GizmoRoot.transform, "OutlineL", outline);
        m_OutR = MakeBar(m_GizmoRoot.transform, "OutlineR", outline);
        m_HTL = MakeBar(m_GizmoRoot.transform, "HandleTL", corner);
        m_HTR = MakeBar(m_GizmoRoot.transform, "HandleTR", corner);
        m_HBL = MakeBar(m_GizmoRoot.transform, "HandleBL", corner);
        m_HBR = MakeBar(m_GizmoRoot.transform, "HandleBR", corner);

        m_GizmoRoot.SetActive(false);
    }

    // ----------------------------------------------------------- layout/apply ---

    // Virtual window rect in full-screen px (top-left origin).
    private void WindowPx(out float vx, out float vy, out float vw, out float vh)
    {
        vx = m_VX * m_PanelW; vy = m_VY * m_PanelH;
        vw = m_VW * m_PanelW; vh = m_VH * m_PanelH;
    }

    void ApplyLayout()
    {
        if (m_BubbleRT == null) return;
        WindowPx(out float vx, out float vy, out float vw, out float vh);
        float splitY = vy + m_SplitFrac * vh;
        float zx = vx + m_ZoneLeft * vw;
        float zw = (m_ZoneRight - m_ZoneLeft) * vw;
        float zh = m_SplitFrac * vh;

        // --- Bubble fills the 2D zone (inset by a margin), tail hangs toward the split.
        float m = kBubbleMargin * vw;
        float tailH = kTailH * vh;
        float bx = zx + m;
        float by = vy + m;
        float bw = Mathf.Max(40f, zw - 2f * m);
        float bh = Mathf.Max(40f, zh - 2f * m - tailH);
        PlaceWindowRect(m_BubbleRT, bx, by, bw, bh);

        int padX = Mathf.RoundToInt(Mathf.Clamp(bw * 0.06f, 16f, 60f));
        int padY = Mathf.RoundToInt(Mathf.Clamp(bh * 0.08f, 12f, 48f));
        m_BubbleLayout.padding = new RectOffset(padX, padX, padY, padY);
        m_BubbleLayout.spacing = 8;

        float tailW = kTailW * vw;
        m_TailRT.sizeDelta = new Vector2(tailW, tailH);
        m_TailRT.anchoredPosition = new Vector2(0f, vh * 0.002f); // hairline overlap

        // --- 3D canvas sub-rect = split line → window bottom, inset by the outline.
        float ins = kOutline;
        int cx = Mathf.RoundToInt(vx + ins);
        int cy = Mathf.RoundToInt(splitY + ins);
        int cw = Mathf.RoundToInt(vw - 2f * ins);
        int ch = Mathf.RoundToInt((vy + vh) - splitY - 2f * ins);
        PushCanvasRect(cx, cy, cw, ch);

        // --- Gizmos track the same numbers (Layout mode only).
        if (m_ShowWindow)
        {
            float barT = 5f;
            float titleH = Mathf.Clamp(vh * 0.045f, 26f, 52f);
            PlaceWindowRect(m_TitleBar, vx, vy, vw, titleH);
            PlaceWindowRect(m_SplitBar, vx, splitY - barT * 0.5f, vw, barT);
            PlaceWindowRect(m_LeftBar,  zx - barT * 0.5f, vy, barT, splitY - vy);
            PlaceWindowRect(m_RightBar, (zx + zw) - barT * 0.5f, vy, barT, splitY - vy);

            float o = kOutline;
            PlaceWindowRect(m_OutT, vx, vy, vw, o);
            PlaceWindowRect(m_OutB, vx, vy + vh - o, vw, o);
            PlaceWindowRect(m_OutL, vx, vy, o, vh);
            PlaceWindowRect(m_OutR, vx + vw - o, vy, o, vh);

            float hs = kHandle, hh = hs * 0.5f;
            PlaceWindowRect(m_HTL, vx - hh,      vy - hh,      hs, hs);
            PlaceWindowRect(m_HTR, vx + vw - hh, vy - hh,      hs, hs);
            PlaceWindowRect(m_HBL, vx - hh,      vy + vh - hh, hs, hs);
            PlaceWindowRect(m_HBR, vx + vw - hh, vy + vh - hh, hs, hs);
        }
    }

    // -------------------------------------------------------------- editor IO ---

    void HandleHotkey()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        bool ctrl  = kb.leftCtrlKey.isPressed  || kb.rightCtrlKey.isPressed;
        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
        // Ctrl+Shift+L ("Layout"). NOT +W — that drives the W/S virtual-display-Z control.
        if (kb.lKey.wasPressedThisFrame && ctrl && shift)
        {
            m_ShowWindow = !m_ShowWindow;
            if (m_GizmoRoot != null) m_GizmoRoot.SetActive(m_ShowWindow);
            m_LastMaskBox = new RectInt(-2, -2, -2, -2); // force mask re-push on mode switch
            if (!m_ShowWindow) { m_Drag = Handle.None; SaveLayout(); }
            Debug.Log($"[TigerSpeechBubble] Layout mode = {m_ShowWindow}");
        }

        // Shift+B hides/shows just the bubble PANEL (not the canvas/surround, so
        // the tiger keeps rendering and the canvas rect stays put).
        if (kb.bKey.wasPressedThisFrame && shift && !ctrl)
        {
            m_BubbleHidden = !m_BubbleHidden;
            if (m_BubbleRT != null) m_BubbleRT.gameObject.SetActive(!m_BubbleHidden);
            m_LastMaskBox = new RectInt(-2, -2, -2, -2); // force mask re-push
            Debug.Log($"[TigerSpeechBubble] Bubble hidden = {m_BubbleHidden}");
        }
    }

    // Right-drag translates the virtual window (the OS window is fixed). Works in
    // both modes; in normal mode it only initiates over the bubble/tiger (the
    // only regions the overlay catches), in Layout mode anywhere in the window.
    void HandleTranslate(int px, int py, bool rightDown)
    {
        if (rightDown && !m_PrevRightDown)
        {
            m_Translating = true;
            m_TransAnchorPx = px; m_TransAnchorPy = py;
            m_TransAnchorVX = m_VX; m_TransAnchorVY = m_VY;
        }
        if (rightDown && m_Translating)
        {
            float dx = (float)(px - m_TransAnchorPx) / m_PanelW;
            float dy = (float)(py - m_TransAnchorPy) / m_PanelH;
            m_VX = Mathf.Clamp(m_TransAnchorVX + dx, 0f, 1f - m_VW);
            m_VY = Mathf.Clamp(m_TransAnchorVY + dy, 0f, 1f - m_VH);
        }
        if (!rightDown && m_PrevRightDown && m_Translating)
        {
            m_Translating = false;
            SaveLayout();
        }
        m_PrevRightDown = rightDown;
    }

    void HandleEditorDrag(int px, int py, bool leftDown)
    {
        if (leftDown && !m_PrevLeftDown)
            m_Drag = HitTestHandle(px, py);          // begin drag

        if (leftDown && m_Drag != Handle.None)
        {
            WindowPx(out float vx, out float vy, out float vw, out float vh);
            float fxw = (vw > 0f) ? Mathf.Clamp01((px - vx) / vw) : 0f; // window-relative
            float fyw = (vh > 0f) ? Mathf.Clamp01((py - vy) / vh) : 0f;
            switch (m_Drag)
            {
                case Handle.Split:     m_SplitFrac = Mathf.Clamp(fyw, kMinSplit, kMaxSplit); break;
                case Handle.ZoneLeft:  m_ZoneLeft  = Mathf.Clamp(fxw, kZoneEdge, m_ZoneRight - kMinZoneW); break;
                case Handle.ZoneRight: m_ZoneRight = Mathf.Clamp(fxw, m_ZoneLeft + kMinZoneW, 1f - kZoneEdge); break;
                case Handle.EdgeL:     ResizeLeft(px); break;
                case Handle.EdgeR:     ResizeRight(px); break;
                case Handle.EdgeT:     ResizeTop(py); break;
                case Handle.EdgeB:     ResizeBottom(py); break;
                case Handle.CornerTL:  ResizeLeft(px);  ResizeTop(py);    break;
                case Handle.CornerTR:  ResizeRight(px); ResizeTop(py);    break;
                case Handle.CornerBL:  ResizeLeft(px);  ResizeBottom(py); break;
                case Handle.CornerBR:  ResizeRight(px); ResizeBottom(py); break;
            }
        }

        if (!leftDown && m_PrevLeftDown && m_Drag != Handle.None)
        {
            m_Drag = Handle.None;                     // end drag
            SaveLayout();
        }
        m_PrevLeftDown = leftDown;
    }

    // Resize one edge, keeping the opposite edge fixed (fractions of the screen).
    private void ResizeLeft(int px)
    {
        float right = m_VX + m_VW;
        m_VX = Mathf.Clamp((float)px / m_PanelW, 0f, right - kMinVW);
        m_VW = right - m_VX;
    }
    private void ResizeRight(int px)
    {
        m_VW = Mathf.Clamp((float)px / m_PanelW - m_VX, kMinVW, 1f - m_VX);
    }
    private void ResizeTop(int py)
    {
        float bottom = m_VY + m_VH;
        m_VY = Mathf.Clamp((float)py / m_PanelH, 0f, bottom - kMinVH);
        m_VH = bottom - m_VY;
    }
    private void ResizeBottom(int py)
    {
        m_VH = Mathf.Clamp((float)py / m_PanelH - m_VY, kMinVH, 1f - m_VY);
    }

    private Handle HitTestHandle(int px, int py)
    {
        WindowPx(out float vx, out float vy, out float vw, out float vh);
        float right = vx + vw, bottom = vy + vh;
        float splitY = vy + m_SplitFrac * vh;
        float zoneLx = vx + m_ZoneLeft * vw;
        float zoneRx = vx + m_ZoneRight * vw;

        // Corners take priority (resize two edges at once).
        if (Near(px, py, vx, vy, kGrabCorner))         return Handle.CornerTL;
        if (Near(px, py, right, vy, kGrabCorner))      return Handle.CornerTR;
        if (Near(px, py, vx, bottom, kGrabCorner))     return Handle.CornerBL;
        if (Near(px, py, right, bottom, kGrabCorner))  return Handle.CornerBR;

        // Window edges.
        bool inRows = py >= vy - kGrab && py <= bottom + kGrab;
        bool inCols = px >= vx - kGrab && px <= right + kGrab;
        if (inRows && Mathf.Abs(px - vx) <= kGrab)     return Handle.EdgeL;
        if (inRows && Mathf.Abs(px - right) <= kGrab)  return Handle.EdgeR;
        if (inCols && Mathf.Abs(py - vy) <= kGrab)     return Handle.EdgeT;
        if (inCols && Mathf.Abs(py - bottom) <= kGrab) return Handle.EdgeB;

        // Internal 2D-zone borders (vertical, in the top strip).
        bool inZoneRows = py >= vy && py <= splitY + kGrab;
        if (inZoneRows && Mathf.Abs(px - zoneLx) <= kGrab) return Handle.ZoneLeft;
        if (inZoneRows && Mathf.Abs(px - zoneRx) <= kGrab) return Handle.ZoneRight;

        // Internal split line (horizontal, across the window width).
        if (px >= vx && px <= right && Mathf.Abs(py - splitY) <= kGrab) return Handle.Split;

        return Handle.None;
    }

    private static bool Near(int px, int py, float cx, float cy, int tol)
    {
        return Mathf.Abs(px - cx) <= tol && Mathf.Abs(py - cy) <= tol;
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

    // Enable/disable the native fixed-full-screen, app-managed window mode.
    // No-op outside the built transparent app (EntryPointNotFound / no overlay).
    private void SetFullscreen(bool on)
    {
        try { DisplayXRNative.displayxr_set_overlay_fullscreen(on ? 1 : 0); }
        catch (System.EntryPointNotFoundException) { }
    }

    // Show a resize/move cursor over the window edges/corners + region lines in
    // Layout mode, and a move cursor while translating. Arrow otherwise.
    private void UpdateCursor(bool havePtr, int px, int py)
    {
        int shape = 0;
        if (m_Translating) shape = 5;                          // size-all (move)
        else if (m_ShowWindow)
        {
            Handle h = (m_Drag != Handle.None) ? m_Drag
                     : (havePtr ? HitTestHandle(px, py) : Handle.None);
            shape = CursorForHandle(h);
        }
        SetCursorShape(shape);
    }

    private static int CursorForHandle(Handle h)
    {
        switch (h)
        {
            case Handle.EdgeL: case Handle.EdgeR:
            case Handle.ZoneLeft: case Handle.ZoneRight: return 1; // size-WE
            case Handle.EdgeT: case Handle.EdgeB:
            case Handle.Split:                            return 2; // size-NS
            case Handle.CornerTL: case Handle.CornerBR:   return 3; // size-NWSE
            case Handle.CornerTR: case Handle.CornerBL:   return 4; // size-NESW
            default:                                      return 0; // arrow
        }
    }

    private void SetCursorShape(int shape)
    {
        if (shape == m_LastCursor) return;
        try { DisplayXRNative.displayxr_set_overlay_cursor(shape); m_LastCursor = shape; }
        catch (System.EntryPointNotFoundException) { m_LastCursor = shape; }
    }

    // ------------------------------------------------------------ canvas rect ---

    private void PushCanvasRect(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        try { DisplayXRNative.displayxr_set_canvas_rect(x, y, (uint)w, (uint)h); }
        catch (System.EntryPointNotFoundException) { }
        m_CanvasRectPushed = true;
    }

    private void ClearCanvasRect()
    {
        if (!m_CanvasRectPushed) return;
        try { DisplayXRNative.displayxr_set_canvas_rect(0, 0, 0, 0); }
        catch (System.EntryPointNotFoundException) { }
        m_CanvasRectPushed = false;
    }

    // --------------------------------------------------------- click-through ---

    // During any active drag — Layout mode, a right-drag translate, or a left-
    // drag of a handle — the whole SCREEN catches clicks. This is essential:
    // resizing drags the cursor past the window edge, and translating can carry
    // it anywhere; if the cursor left the catch region the OS would route the
    // mouse-UP to the desktop and the overlay would never see it, leaving the
    // drag stuck (and a stale "left held" then makes right-drag rotate the
    // tiger too). A full-screen region keeps every release on-target.
    // Otherwise (normal idle) only the bubble's exact shape catches and the
    // empty surround routes to the desktop (the tiger catches via
    // DisplayXRTransparentOverlay's own silhouette mask, unioned in natively).
    private bool WantFullCatch => m_ShowWindow || m_Translating || m_Drag != Handle.None;

    private void PushMask()
    {
        if (WantFullCatch) PushFullScreenMask();
        else               PushBubbleMask();
    }

    private void PushFullScreenMask()
    {
        var box = new RectInt(0, 0, m_PanelW, m_PanelH);
        if (box.width <= 0 || box.height <= 0) { ClearMask(); return; }
        if (m_MaskPushed && box.Equals(m_LastMaskBox)) return;
        var mask = new byte[] { 255, 255, 255, 255 }; // 2x2 solid → whole-rect
        var handle = GCHandle.Alloc(mask, GCHandleType.Pinned);
        try
        {
            DisplayXRNative.displayxr_set_overlay_surround_mask(
                handle.AddrOfPinnedObject(), 2, 2, box.x, box.y, box.width, box.height);
            m_MaskPushed = true; m_LastMaskBox = box;
        }
        catch (System.EntryPointNotFoundException) { }
        finally { handle.Free(); }
    }

    private void PushBubbleMask()
    {
        if (m_BubbleHidden) { ClearMask(); return; } // hidden → nothing to catch
        if (!TryRectToWindow(m_BubbleRT, out float pL, out float pT, out float pW, out float pH) ||
            !TryRectToWindow(m_TailRT,   out float tL, out float tT, out float tW, out float tH))
        {
            ClearMask();
            return;
        }

        float bL = Mathf.Min(pL, tL), bT = Mathf.Min(pT, tT);
        float bR = Mathf.Max(pL + pW, tL + tW), bB = Mathf.Max(pT + pH, tT + tH);
        var box = new RectInt(Mathf.FloorToInt(bL), Mathf.FloorToInt(bT),
                              Mathf.CeilToInt(bR - bL), Mathf.CeilToInt(bB - bT));
        if (box.width <= 0 || box.height <= 0) { ClearMask(); return; }
        if (m_MaskPushed && box.Equals(m_LastMaskBox)) return;

        int mw = Mathf.Clamp(box.width  / 4, 8, 320);
        int mh = Mathf.Clamp(box.height / 4, 8, 320);
        var mask = new byte[mw * mh];

        const float cornerR = 30f;
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
                    float frac = (wy - tT) / tH;
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
                handle.AddrOfPinnedObject(), mw, mh, box.x, box.y, box.width, box.height);
            m_MaskPushed = true; m_LastMaskBox = box;
        }
        catch (System.EntryPointNotFoundException) { }
        finally { handle.Free(); }
    }

    private void ClearMask()
    {
        if (!m_MaskPushed) return;
        try { DisplayXRNative.displayxr_set_overlay_surround_mask(System.IntPtr.Zero, 0, 0, 0, 0, 0, 0); }
        catch (System.EntryPointNotFoundException) { }
        m_MaskPushed = false;
        m_LastMaskBox = new RectInt(-2, -2, -2, -2);
    }

    // ------------------------------------------------------------ persistence ---

    private void LoadLayout()
    {
        m_VX = PlayerPrefs.GetFloat(kPrefVX, m_VX);
        m_VY = PlayerPrefs.GetFloat(kPrefVY, m_VY);
        m_VW = PlayerPrefs.GetFloat(kPrefVW, m_VW);
        m_VH = PlayerPrefs.GetFloat(kPrefVH, m_VH);
        m_SplitFrac = PlayerPrefs.GetFloat(kPrefSplit, m_SplitFrac);
        m_ZoneLeft  = PlayerPrefs.GetFloat(kPrefLeft,  m_ZoneLeft);
        m_ZoneRight = PlayerPrefs.GetFloat(kPrefRight, m_ZoneRight);
    }

    private void SaveLayout()
    {
        PlayerPrefs.SetFloat(kPrefVX, m_VX);
        PlayerPrefs.SetFloat(kPrefVY, m_VY);
        PlayerPrefs.SetFloat(kPrefVW, m_VW);
        PlayerPrefs.SetFloat(kPrefVH, m_VH);
        PlayerPrefs.SetFloat(kPrefSplit, m_SplitFrac);
        PlayerPrefs.SetFloat(kPrefLeft,  m_ZoneLeft);
        PlayerPrefs.SetFloat(kPrefRight, m_ZoneRight);
        PlayerPrefs.Save();
    }

    // -------------------------------------------------------------- UI helpers ---

    // Position a RectTransform at a window-pixel rect on the surround canvas
    // (top-left origin). The surround maps 1 UI unit = 1 render-target px with a
    // vertical flip, so anchoring to the canvas top-left and offsetting down by y
    // lands the element at full-screen (x, y).
    private void PlaceWindowRect(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, -y);
    }

    private static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    private RectTransform MakeBar(Transform parent, string name, Color c)
    {
        var go = MakeUI(name, parent);
        var img = go.AddComponent<Image>();
        img.color = c; // no sprite = solid rect
        return go.GetComponent<RectTransform>();
    }

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

    // One RectTransform → full-screen-pixel rect (top-left origin, float), via the
    // surround mapping (1 UI unit = 1 render-target px; flipY maps canvas-up to
    // window-up).
    private bool TryRectToWindow(RectTransform rt,
                                 out float left, out float top, out float w, out float h)
    {
        left = top = w = h = 0f;
        if (rt == null || m_CanvasRT == null || m_Surround == null) return false;
        if (m_PanelW <= 0 || m_PanelH <= 0) return false;
        Rect cr = m_CanvasRT.rect;
        if (cr.width <= 0f || cr.height <= 0f) return false;

        Vector3[] wc = new Vector3[4];
        rt.GetWorldCorners(wc); // 0=BL, 1=TL, 2=TR, 3=BR
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
