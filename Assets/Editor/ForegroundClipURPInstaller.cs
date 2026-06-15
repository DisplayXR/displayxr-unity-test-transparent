// Copyright 2024-2026, DisplayXR contributors
// SPDX-License-Identifier: BSL-1.0
//
// One-click wiring for the URP foreground-clip prototype:
//   1. Creates a material from the DisplayXR/ForegroundClipURP shader.
//   2. Best-effort adds Unity's built-in FullScreenPassRendererFeature to the
//      URP renderer, pointed at that material, injected AfterRenderingTransparents,
//      with Depth required.
//
// Material creation is reliable; the renderer-feature wiring is wrapped so a
// failure never corrupts the renderer asset — if it can't complete, a dialog
// lists the (3-click) manual steps instead.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

internal static class ForegroundClipURPInstaller
{
    const string kMaterialPath = "Assets/Settings/DXRForegroundClip.mat";
    const string kShaderName = "DisplayXR/ForegroundClipURP";
    const string kFeatureName = "DisplayXR Foreground Clip";

    [MenuItem("DisplayXR/Setup URP Foreground Clip")]
    static void Setup() => Install(interactive: true);

    /// <summary>
    /// Core setup, callable headlessly (interactive=false suppresses the dialogs so
    /// it works under -batchmode -executeMethod). Returns true if the renderer
    /// feature was wired; false means the material exists but manual wiring is needed.
    /// </summary>
    internal static bool Install(bool interactive)
    {
        var shader = Shader.Find(kShaderName);
        if (shader == null)
        {
            string msg = $"Shader '{kShaderName}' not found. Make sure " +
                "Assets/DisplayXRForegroundClipURP.shader imported without errors.";
            if (interactive) EditorUtility.DisplayDialog("Foreground Clip", msg, "OK");
            else Debug.LogError("[ForegroundClipURP] " + msg);
            return false;
        }

        // 1. Material (reliable).
        Directory.CreateDirectory("Assets/Settings");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(kMaterialPath);
        if (mat == null)
        {
            mat = new Material(shader) { name = "DXRForegroundClip" };
            AssetDatabase.CreateAsset(mat, kMaterialPath);
        }
        else if (mat.shader != shader)
        {
            mat.shader = shader;
            EditorUtility.SetDirty(mat);
        }
        AssetDatabase.SaveAssets();

        // 2. Renderer feature (best effort).
        var rendererData = FindRendererData();
        string manual =
            "Manual wiring (3 clicks):\n" +
            "  1. Select Assets/Settings/URP-Renderer.asset\n" +
            "  2. Add Renderer Feature → Full Screen Pass Renderer Feature\n" +
            "  3. Pass Material = DXRForegroundClip,\n" +
            "     Injection Point = Before Rendering Post Processing,\n" +
            "     Requirements = Depth";

        if (rendererData == null)
        {
            string msg = "Material created at " + kMaterialPath + ".\n\n" +
                "Couldn't find URP-Renderer.asset (open the project once so " +
                "URPSetupBootstrap creates it), then:\n\n" + manual;
            if (interactive) EditorUtility.DisplayDialog("Foreground Clip", msg, "OK");
            else Debug.LogWarning("[ForegroundClipURP] " + msg);
            return false;
        }

        bool added;
        try { added = TryAddFullScreenFeature(rendererData, mat); }
        catch (Exception e) { Debug.LogWarning("[ForegroundClipURP] auto-wire failed: " + e.Message); added = false; }

        string result = added
              ? "Done. Material + Full Screen Pass feature wired into " + rendererData.name + ".\n\n" +
                "Run the scene and press C to toggle the clip."
              : "Material created at " + kMaterialPath + ".\n\n" +
                "Auto-wiring the renderer feature didn't complete — do it manually:\n\n" + manual;
        if (interactive) EditorUtility.DisplayDialog("Foreground Clip", result, "OK");
        else Debug.Log("[ForegroundClipURP] " + (added ? "auto-wired Full Screen Pass feature." : result));
        return added;
    }

    static ScriptableRendererData FindRendererData()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(path);
            if (data != null) return data;
        }
        return null;
    }

    // Mirrors what URP's renderer inspector does when you click
    // "Add Renderer Feature": instantiate the feature, add it as a sub-asset,
    // and register it in m_RendererFeatures + m_RendererFeatureMap.
    static bool TryAddFullScreenFeature(ScriptableRendererData rendererData, Material mat)
    {
        var so = new SerializedObject(rendererData);
        var features = so.FindProperty("m_RendererFeatures");
        var map = so.FindProperty("m_RendererFeatureMap");
        if (features == null || map == null) return false;

        // Already installed? (idempotent)
        for (int i = 0; i < features.arraySize; i++)
        {
            var existing = features.GetArrayElementAtIndex(i).objectReferenceValue;
            if (existing is FullScreenPassRendererFeature f && f.name == kFeatureName)
            {
                ConfigureFeature(f, mat);
                EditorUtility.SetDirty(rendererData);
                AssetDatabase.SaveAssets();
                return true;
            }
        }

        var feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
        if (feature == null) return false;
        feature.name = kFeatureName;
        ConfigureFeature(feature, mat);

        AssetDatabase.AddObjectToAsset(feature, rendererData);

        int idx = features.arraySize;
        features.InsertArrayElementAtIndex(idx);
        features.GetArrayElementAtIndex(idx).objectReferenceValue = feature;

        map.InsertArrayElementAtIndex(idx);
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
            map.GetArrayElementAtIndex(idx).longValue = localId;

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(rendererData);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(rendererData));
        return true;
    }

    // FullScreenPassRendererFeature exposes these as PUBLIC fields in URP 17, so set
    // them directly — no SerializedObject name-guessing or fragile enum intValues.
    //
    // NOTE: URP 17.0.4's InjectionPoint enum has only THREE members and NO
    // AfterRenderingTransparents — { BeforeRenderingTransparents=450,
    // BeforeRenderingPostProcessing=550, AfterRenderingPostProcessing=600 } (the
    // values are RenderPassEvent ints, not 0/1/2). The overlay content (tiger/cube)
    // is opaque, so the depth + color buffers are fully populated by the time
    // transparents finish; BeforeRenderingPostProcessing is the first injection point
    // after the transparent queue and is the correct "after everything is drawn"
    // slot here. (The old intValue=1 mapped to no valid enum member.)
    static void ConfigureFeature(FullScreenPassRendererFeature feature, Material mat)
    {
        feature.passMaterial = mat;
        feature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingPostProcessing;
        feature.requirements = ScriptableRenderPassInput.Depth;
        feature.fetchColorBuffer = true;  // binds camera color to _BlitTexture for passthrough
        feature.passIndex = 0;
        EditorUtility.SetDirty(feature);
    }
}
