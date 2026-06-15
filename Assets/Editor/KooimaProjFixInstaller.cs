// Wires KooimaProjectionFixFeature into the URP renderer asset. Callable headlessly
// (Unity.exe -executeMethod KooimaProjFixInstaller.Install) so a Player can be built
// with the feature already attached; also exposed as a menu item.
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

internal static class KooimaProjFixInstaller
{
    const string kFeatureName = "Kooima Projection Fix";

    [MenuItem("DisplayXR/Setup Kooima Projection Fix")]
    public static void Install()
    {
        var rendererData = FindRendererData();
        if (rendererData == null)
        {
            Debug.LogError("[KooimaProjFix] URP-Renderer.asset not found.");
            return;
        }

        var so = new SerializedObject(rendererData);
        var features = so.FindProperty("m_RendererFeatures");
        var map = so.FindProperty("m_RendererFeatureMap");
        if (features == null || map == null) { Debug.LogError("[KooimaProjFix] renderer props missing."); return; }

        for (int i = 0; i < features.arraySize; i++)
        {
            if (features.GetArrayElementAtIndex(i).objectReferenceValue is KooimaProjectionFixFeature)
            {
                Debug.Log("[KooimaProjFix] already installed.");
                return;
            }
        }

        var feature = ScriptableObject.CreateInstance<KooimaProjectionFixFeature>();
        feature.name = kFeatureName;
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
        Debug.Log("[KooimaProjFix] installed into " + rendererData.name);
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
}
