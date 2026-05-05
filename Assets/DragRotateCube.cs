// Demo handler for the issue #57 transparent overlay tests. Shows how an
// app-side script consumes DisplayXRTransparentOverlay's polled cursor /
// pointer events to drive a drag-to-rotate interaction. Standard Unity input
// (Input.mousePosition, Mouse.current.position, OnMouseDown) doesn't work in
// transparent mode because the cloaked Unity HWND is not OS-foreground —
// see DisplayXRTransparentOverlay's class doc for the full story.
//
// Two interactions wired up here:
//   1. PointerClick on the cube → flash bright red (visual confirmation
//      that left-clicks are reaching us)
//   2. Press + drag with left button while over the cube → rotate the cube
//      based on per-frame cursor delta. While dragging we disable the
//      Animator so the CubeRotate animation doesn't immediately clobber
//      our transform.Rotate calls each frame.

using UnityEngine;
using DisplayXR;

public class DragRotateCube : MonoBehaviour
{
    public DisplayXRTransparentOverlay overlay;
    public Renderer target;
    public float degreesPerPixel = 1f;

    private bool m_Dragging;
    private bool m_Subscribed;
    private Animator m_Animator;
    private bool m_AnimatorWasEnabled;

    void Update()
    {
        // Lazy subscribe — TransparentAutoSetup wires `overlay` and `target`
        // AFTER AddComponent runs OnEnable, so we can't subscribe in OnEnable
        // (target would be null and we'd cache stale references). Polling for
        // first-valid-frame avoids that ordering footgun.
        if (overlay != null && target != null && !m_Subscribed)
        {
            overlay.onPointerDown.AddListener(OnDown);
            overlay.onPointerUp  .AddListener(OnUp);
            overlay.onPointerClick.AddListener(OnClick);
            m_Subscribed = true;
            Debug.Log($"[DragRotateCube] Subscribed to overlay events on {target.name}");
        }

        if (!m_Dragging || overlay == null) return;
        Vector2 d = overlay.PointerDelta;
        if (d == Vector2.zero) return;
        // Both axes inverted so dragging feels like "grab and rotate the
        // cube" rather than "rotate the camera around it". Drag-right →
        // cube turns left (yaw -X), drag-down → cube tilts back (pitch -Y).
        transform.Rotate(Vector3.up,    -d.x * degreesPerPixel, Space.World);
        transform.Rotate(Vector3.right, -d.y * degreesPerPixel, Space.World);
    }

    void OnDestroy()
    {
        if (!m_Subscribed || overlay == null) return;
        overlay.onPointerDown.RemoveListener(OnDown);
        overlay.onPointerUp  .RemoveListener(OnUp);
        overlay.onPointerClick.RemoveListener(OnClick);
    }

    void OnDown(Renderer r)
    {
        if (r != target) return;
        m_Dragging = true;
        // Pause the cube's existing rotation animation so manual rotation
        // sticks. Resume on release.
        if (m_Animator == null) m_Animator = GetComponent<Animator>();
        if (m_Animator != null && m_Animator.enabled)
        {
            m_AnimatorWasEnabled = true;
            m_Animator.enabled = false;
        }
        Debug.Log("[DragRotateCube] drag started");
    }

    void OnUp(Renderer r)
    {
        if (!m_Dragging) return;
        m_Dragging = false;
        if (m_Animator != null && m_AnimatorWasEnabled)
        {
            m_Animator.enabled = true;
            m_AnimatorWasEnabled = false;
        }
        Debug.Log("[DragRotateCube] drag ended");
    }

    void OnClick(Renderer r)
    {
        if (r != target) return;
        // Re-fetch the renderer's material at click time; OnEnable was too
        // early (target wasn't set yet by TransparentAutoSetup).
        var rend = target.GetComponent<Renderer>();
        if (rend == null) return;
        // Use sharedMaterial to avoid the "(Instance)" duplication on every
        // click; for a one-cube test that's fine.
        var mat = rend.material;
        if (mat == null) return;
        mat.color = Color.red;
        Debug.Log("[DragRotateCube] flashed red");
    }
}
