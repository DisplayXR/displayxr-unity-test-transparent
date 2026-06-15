// Copyright 2024-2026, DisplayXR contributors
// SPDX-License-Identifier: BSL-1.0
//
// Drives the per-eye foreground-clip shader (DisplayXRForegroundClipURP) on URP.
//
// The native plugin already computes each eye's foreground far (far_eff) and
// embeds it in the per-view projection matrices it hands back via
// DisplayXRFeature.GetStereoMatrices — leftProj/rightProj. far_eff is recovered
// with the same formula the plugin's BiRP/URP path uses:
//     far = proj.m23 / (proj.m22 + 1)
// For the OpenXR/GL projection built from fov+far (m22 = -(f+n)/(f-n),
// m23 = -2fn/(f-n)) this evaluates exactly to f. We read BOTH eyes (URP's
// built-in path only keeps the left far for the shared Camera.farClipPlane) and
// publish them to the clip shader via the _DXRForegroundFar global.
//
// This is the test-repo PROTOTYPE for the per-eye URP clip. Once validated, the
// shader + the FullScreenPassRendererFeature + this global push move into the
// plugin (behind a URP-guarded assembly) so any app gets it for free.
//
// Self-installs at scene load; press C to toggle the clip live.

using UnityEngine;
using UnityEngine.Rendering;
using DisplayXR;

[DisallowMultipleComponent]
public class ForegroundClipURPDriver : MonoBehaviour
{
    static readonly int s_ForegroundFarId = Shader.PropertyToID("_DXRForegroundFar");

    [Tooltip("Master enable. Press C at runtime to toggle.")]
    public bool clipEnabled = true;

    [Tooltip("Far plane (m) forced onto the rig camera so EACH eye renders the full "
           + "scene; the per-eye clip then happens in the shader. Prevents the rig's "
           + "URP single-Camera.farClipPlane clamp from pre-clipping the off-eye.")]
    public float renderFarOverride = 1000f;

    [Tooltip("Log the per-eye fars + their disagreement once per second.")]
    public bool diagnosticLog = false;

    DisplayXRFeature m_Feature;
    float m_LogTimer;

    // Only spawn under URP — under BiRP the plugin's projection override already
    // delivers exact per-eye foreground clip and this prototype is a no-op.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoInstall()
    {
        if (GraphicsSettings.currentRenderPipeline == null) return; // BiRP → skip
        if (FindFirstObjectByType<ForegroundClipURPDriver>() != null) return;
        var go = new GameObject("DXR Foreground Clip Driver (URP)");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<ForegroundClipURPDriver>();
        Debug.Log("[ForegroundClipURP] Driver installed (URP detected).");
    }

    void OnEnable()
    {
        m_Feature = DisplayXRFeature.Instance;
        // Run AFTER the rig's OnSRPBeginCamera (it subscribes in its own OnEnable
        // at scene load; we subscribe later) so our farClipPlane reset wins over
        // the rig's single-far clamp.
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        // Leave the clip disabled in the shader so a torn-down driver can't strand
        // geometry as clipped.
        Shader.SetGlobalVector(s_ForegroundFarId, new Vector4(0f, 0f, 0f, 0f));
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.C))
        {
            clipEnabled = !clipEnabled;
            Debug.Log($"[ForegroundClipURP] clip {(clipEnabled ? "ON" : "OFF")}");
        }
    }

    void LateUpdate()
    {
        if (m_Feature == null) m_Feature = DisplayXRFeature.Instance;
        if (m_Feature == null) return;

        if (!m_Feature.GetStereoMatrices(out _, out Matrix4x4 leftProj,
                                         out _, out Matrix4x4 rightProj))
            return;

        float leftFar = FarOf(leftProj);
        float rightFar = FarOf(rightProj);

        // Valid only when foregroundOnlyClip is active on the rig (otherwise the
        // matrices carry Camera.farClipPlane, ~1000m, and there is nothing to clip).
        // ClipAtDisplayPlane in TransparentAutoSetup turns that on.
        bool haveFar = leftFar > 0f && rightFar > 0f && leftFar < renderFarOverride * 0.99f;
        float enable = (clipEnabled && haveFar) ? 1f : 0f;

        Shader.SetGlobalVector(s_ForegroundFarId,
            new Vector4(leftFar, rightFar, enable, 0f));

        if (diagnosticLog)
        {
            m_LogTimer += Time.deltaTime;
            if (m_LogTimer >= 1f)
            {
                m_LogTimer = 0f;
                float diff = Mathf.Abs(leftFar - rightFar);
                Debug.Log($"[ForegroundClipURP] farL={leftFar:F4} farR={rightFar:F4} " +
                          $"Δ={diff * 1000f:F1}mm enable={enable:F0}");
            }
        }
    }

    // Keep the rig camera's far large so each eye renders the WHOLE scene; the
    // shader does the precise per-eye clip. Runs after the rig's own begin-camera
    // handler, overriding its single-far clamp.
    void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (!clipEnabled) return;
        bool isRig = cam.GetComponent<DisplayXRDisplay>() != null
                  || cam.GetComponent<DisplayXRCamera>() != null;
        if (isRig && cam.farClipPlane < renderFarOverride)
            cam.farClipPlane = renderFarOverride;
    }

    // far = m23 / (m22 + 1) for the GL/OpenXR projection (Z clip [-1,1]).
    static float FarOf(Matrix4x4 p)
    {
        float denom = p.m22 + 1f;
        if (Mathf.Abs(denom) < 1e-6f) return -1f;
        return p.m23 / denom;
    }
}
