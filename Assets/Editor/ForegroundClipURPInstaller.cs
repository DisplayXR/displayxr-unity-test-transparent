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
    static void Setup()
    {
        var shader = Shader.Find(kShaderName);
        if (shader == null)
        {
            EditorUtility.DisplayDialog("Foreground Clip",
                $"Shader '{kShaderName}' not found. Make sure " +
                "Assets/DisplayXRForegroundClipURP.shader imported without errors.", "OK");
            return;
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
            "     Injection Point = After Rendering Transparents,\n" +
            "     Requirements = Depth";

        if (rendererData == null)
        {
            EditorUtility.DisplayDialog("Foreground Clip",
                "Material created at " + kMaterialPath + ".\n\n" +
                "Couldn't find URP-Renderer.asset (open the project once so " +
                "URPSetupBootstrap creates it), then:\n\n" + manual, "OK");
            return;
        }

        bool added;
        try { added = TryAddFullScreenFeature(rendererData, mat); }
        catch (Exception e) { Debug.LogWarning("[ForegroundClipURP] auto-wire failed: " + e.Message); added = false; }

        EditorUtility.DisplayDialog("Foreground Clip",
            added
              ? "Done. Material + Full Screen Pass feature wired into " + rendererData.name + ".\n\n" +
                "Run the scene and press C to toggle the clip."
              : "Material created at " + kMaterialPath + ".\n\n" +
                "Auto-wiring the renderer feature didn't complete — do it manually:\n\n" + manual,
            "OK");
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

    // FullScreenPassRendererFeature exposes these as public fields in URP 17.
    static void ConfigureFeature(FullScreenPassRendererFeature feature, Material mat)
    {
        var fso = new SerializedObject(feature);
        SetIfPresent(fso, "passMaterial", mat);
        // InjectionPoint enum: 0 BeforeRenderingTransparents, 1 AfterRenderingTransparents,
        // 2 AfterRenderingPostProcessing. Clip after transparents so the whole overlay
        // (opaque tiger/cube) is present in the depth buffer.
        SetEnumIfPresent(fso, "injectionPoint", 1);
        // ScriptableRenderPassInput.Depth == 4 (Color=1, Depth=4, Normal=8, Motion=16).
        SetEnumIfPresent(fso, "requirements", (int)ScriptableRenderPassInput.Depth);
        SetIntIfPresent(fso, "passIndex", 0);
        fso.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(feature);
    }

    static void SetIfPresent(SerializedObject so, string name, UnityEngine.Object val)
    {
        var p = so.FindProperty(name);
        if (p != null) p.objectReferenceValue = val;
    }
    static void SetEnumIfPresent(SerializedObject so, string name, int val)
    {
        var p = so.FindProperty(name);
        if (p != null) p.intValue = val;
    }
    static void SetIntIfPresent(SerializedObject so, string name, int val)
    {
        var p = so.FindProperty(name);
        if (p != null) p.intValue = val;
    }
}
