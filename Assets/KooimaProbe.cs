// Off-axis projection probe (shared between displayxr-unity-test* repos to compare
// known-good 2d-ui vs the broken transparent overlay). Logs once/sec:
//   - headX: average eye world X from the rig's FlipViewZ'd view matrices (head side)
//   - L/R m00,m02,m03: the RUNTIME per-eye projection horizontal scale (m00) and the
//     two off-center terms — m02 (frustum shear, OpenGL clip) and m03 (post-divide
//     principal-point shift). One of these encodes window-relative off-center.
// Identical output format in both repos so the logs diff directly. Uses only
// DisplayXRFeature.GetStereoMatrices (present in every plugin version) — no native
// getters. URP-only; self-installs at scene load. Purely diagnostic.
using UnityEngine;
using UnityEngine.Rendering;
using DisplayXR;

[DisallowMultipleComponent]
public class KooimaProbe : MonoBehaviour
{
    DisplayXRFeature m_Feature;
    float m_Timer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoInstall()
    {
        if (GraphicsSettings.currentRenderPipeline == null) return; // URP only
        if (FindAnyObjectByType<KooimaProbe>() != null) return;
        var go = new GameObject("Kooima Probe");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<KooimaProbe>();
        Debug.Log("[KooimaProbe] installed.");
    }

    void LateUpdate()
    {
        if (m_Feature == null) m_Feature = DisplayXRFeature.Instance;
        if (m_Feature == null) return;
        if (!m_Feature.GetStereoMatrices(out Matrix4x4 lv, out Matrix4x4 lp,
                                         out Matrix4x4 rv, out Matrix4x4 rp))
            return;

        m_Timer += Time.deltaTime;
        if (m_Timer < 1f) return;
        m_Timer = 0f;

        float headX = 0.5f * (FlipZ(lv).inverse.GetColumn(3).x +
                              FlipZ(rv).inverse.GetColumn(3).x);
        Debug.Log($"[KooimaProbe] headX={headX:F4} " +
                  $"L(m00={lp.m00:F4} m02={lp.m02:F4} m03={lp.m03:F4}) " +
                  $"R(m00={rp.m00:F4} m02={rp.m02:F4} m03={rp.m03:F4})");
    }

    // OpenXR -> Unity handedness flip the rig applies before SetStereoViewMatrix,
    // so the eye world positions match what the renderer/shader see.
    static Matrix4x4 FlipZ(Matrix4x4 m)
    {
        m.m02 = -m.m02; m.m12 = -m.m12; m.m22 = -m.m22; m.m32 = -m.m32;
        return m;
    }
}
