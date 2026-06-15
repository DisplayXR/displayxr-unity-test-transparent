// Copyright 2024-2026, DisplayXR contributors
// SPDX-License-Identifier: BSL-1.0
//
// Per-eye foreground-only clip for URP (transparent-overlay prototype, #57/#129).
//
// WHY THIS EXISTS
// URP builds each eye's projection from views[i].fov + Camera.farClipPlane and
// IGNORES Camera.SetStereoProjectionMatrix — so the plugin's per-eye foreground
// far (which BiRP injects straight into the projection matrix) can't reach URP
// that way. The only lever today is the single Camera.farClipPlane, shared by
// both eyes → wrong for the off-eye, because in display-centric mode each eye
// sees the virtual display at a DIFFERENT depth (its own eye.z after the m2v
// scaling + ipd/parallax modifiers).
//
// This pass enforces the clip per-eye in screen space instead: after the scene
// renders, reconstruct each fragment's view-space eye Z from the depth texture
// and discard (write transparent black) anything farther than this eye's
// foreground far. The two per-eye fars arrive via the _DXRForegroundFar global,
// set each frame by ForegroundClipURPDriver from the native per-view fars
// (leftProj/rightProj m23/(m22+1) — the exact far_eff Kooima used). View-
// independent in the sense that each eye uses ITS OWN far, so there is no
// single-far approximation.
//
// Authored for Unity's built-in FullScreenPassRendererFeature (URP 17 / Unity 6):
// inject AfterRenderingTransparents, Requirements = Depth. The feature binds the
// camera color to _BlitTexture and the Vert/Varyings come from URP's Blit.hlsl.
Shader "DisplayXR/ForegroundClipURP"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "DisplayXRForegroundClip"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Vert / Varyings / _BlitTexture / sampler_LinearClamp / SAMPLE_TEXTURE2D_X
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            // SampleSceneDepth + _CameraDepthTexture
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // x = left eye far_eff, y = right eye far_eff, z = enable (0/1), w = unused.
            float4 _DXRForegroundFar;

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);

                // Disabled → pure passthrough (lets the driver toggle the clip live).
                if (_DXRForegroundFar.z < 0.5h)
                    return col;

                // View-space distance from the eye to this fragment, in world units.
                // Background (no geometry) reads the far plane → very large eyeZ →
                // clipped to transparent, which is already what the empty overlay wants.
                float rawDepth = SampleSceneDepth(input.texcoord);
                float eyeZ = LinearEyeDepth(rawDepth, _ZBufferParams);

                // Per-eye far: multipass renders each eye in its own pass, so
                // unity_StereoEyeIndex selects which eye's foreground far applies.
                float farEff = (unity_StereoEyeIndex == 0u) ? _DXRForegroundFar.x
                                                            : _DXRForegroundFar.y;

                // Behind the virtual display plane → cut it away (color AND alpha
                // zeroed; alpha-only left the geometry visible — the #129 failure).
                if (eyeZ > farEff)
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);

                return col;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
