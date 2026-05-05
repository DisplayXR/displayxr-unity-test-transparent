# DisplayXR Unity Test Project — Transparent Overlay Variant

A duplicate of [displayxr-unity-test](https://github.com/DisplayXR/displayxr-unity-test)
configured to exercise the **chroma-key transparent overlay mode** added in
[displayxr-unity #57](https://github.com/DisplayXR/displayxr-unity/issues/57).

The rotating cube renders above the Windows desktop with no rectangular
background — magenta is punched through by DWM. Clicks outside the cube's
bounding box fall through to whatever is behind the window.

## What's different from displayxr-unity-test

- `Packages/manifest.json` points at the **local** plugin checkout
  (`file:../../unity-3d-display`) so it picks up the new
  `DisplayXRTransparentOverlay` component before it ships in a tagged release.
- `Assets/TransparentAutoSetup.cs` runs at scene load, attaches
  `DisplayXRTransparentOverlay` to `Camera.main`, and wires the rotating cube
  as the click-through hit region. No edits to `CubeTest.unity` needed.

## Requirements

- **Unity 6000.3 LTS** (Unity 6) or newer
- A **Leia SR Windows** machine for end-to-end verification (the layered-window
  path doesn't run in the editor preview — only in a Windows standalone build)
- The DisplayXR runtime installed
- The `unity-3d-display` repo cloned as a sibling directory:
  ```
  GitHub/
  ├── unity-3d-display/                       (the plugin)
  └── displayxr-unity-test-transparent/       (this project)
  ```

## Quick start

1. Open the project in Unity Hub. First import takes a few minutes.
2. Open `Assets/CubeTest.unity`.
3. **Build Windows standalone** (`File → Build Settings → Build`). Editor
   Play Mode shows the magenta clear color but **does not** apply the
   layered-window chroma key — that's a build-only path.
4. Run the resulting `.exe` on a Leia SR machine.

## Verification checklist

- Cube renders above the desktop with no rectangular background.
- Magenta regions punch through to the desktop (taskbar/browser visible).
- Clicks on the magenta region land on the underlying app.
- Clicks on the cube reach Unity (add a logging script to confirm).
- Cube pops convincingly in stereo. Transparent regions stay clean (no
  shimmer — `L == R` per sub-pixel).

## Reverting to opaque

Comment out the body of `TransparentAutoSetup.Install()` and rebuild — the
scene falls back to the default skybox.

## License

ISC. See [LICENSE](LICENSE).
