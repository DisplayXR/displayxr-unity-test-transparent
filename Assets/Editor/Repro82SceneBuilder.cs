// Builds Assets/Repro82.unity — the minimal repro scene for
// DisplayXR/displayxr-unity#82 (transparent overlay + window-space UI).
//
// Run via the menu: DisplayXR -> Build Repro82 Scene.
//
// What it builds: one camera with DisplayXRDisplay (TransparentAutoSetup
// will attach DisplayXRTransparentOverlay at scene load), one WorldSpace
// Canvas with DisplayXRWindowSpaceUI and a single visible Text element,
// one directional light. No tiger, no FarClipDiopterSlider, no
// DisplayXRInputController, no GameViewOverlay. If this scene runs
// without xrEndFrame crashing, #82 is fixed.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using DisplayXR;

public static class Repro82SceneBuilder
{
    const string k_ScenePath = "Assets/Repro82.unity";

    [MenuItem("DisplayXR/Build Repro82 Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var scene = EditorSceneManager.NewScene(
            NewSceneSetup.EmptyScene, NewSceneMode.Single);

        BuildCameraRig();
        BuildLight();
        BuildWsuiPanel();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, k_ScenePath);

        Debug.Log("[Repro82] Built " + k_ScenePath +
                  ". TransparentAutoSetup will attach DisplayXRTransparentOverlay " +
                  "to the camera at Play / preview start.");
    }

    static void BuildCameraRig()
    {
        var go = new GameObject("Repro82_Camera");
        go.transform.position = new Vector3(0f, 0f, -1.5f);
        go.transform.rotation = Quaternion.identity;

        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 0f);
        cam.fieldOfView = 60f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 1000f;

        go.AddComponent<DisplayXRDisplay>();
        go.tag = "MainCamera";
    }

    static void BuildLight()
    {
        var go = new GameObject("Directional Light");
        go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
    }

    static void BuildWsuiPanel()
    {
        var canvasGO = new GameObject("Repro82_WsuiCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGO.AddComponent<CanvasScaler>();
        canvasGO.AddComponent<GraphicRaycaster>();

        var wsui = canvasGO.AddComponent<DisplayXRWindowSpaceUI>();
        wsui.positionX = 0.05f;
        wsui.positionY = 0.05f;
        wsui.width = 0.30f;
        wsui.height = 0.10f;
        wsui.disparity = 0f;
        wsui.resolution = new Vector2Int(1024, 256);

        var bgGO = new GameObject("Background");
        bgGO.transform.SetParent(canvasGO.transform, false);
        var bgRect = bgGO.AddComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.7f);

        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(canvasGO.transform, false);
        var labelRect = labelGO.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        var label = labelGO.AddComponent<Text>();
        label.text = "Repro82: wsui + transparent overlay";
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 72;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
    }
}
