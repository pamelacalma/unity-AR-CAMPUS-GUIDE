# AR Campus Guide workspace rules

## Scene and Unity Editor safety
- Editing `Assets/Scenes/SampleScene.unity` directly on disk while Unity Editor is open is allowed (Unity will prompt to reload the modified scene). Keep repeatable batch operations (NavMesh baking, AR build prep, hallway trimming) in `Assets/Editor/Mig1NavMeshSetup.cs`.
- Never disable a `ScreenSpaceOverlay` `Canvas` GameObject to get it out of the way in the Scene View. Hide it with `SceneVisibilityManager.instance.Hide(canvas.gameObject, true)` so Play Mode and Android builds keep the UI active.
- Run `Tools > AR Campus Guide > 4. Prepare Scene for AR Build` before building an Android APK so Miguel walls use depth-only `ARWallOcclusion.mat` and floor/marker renderers stay hidden.

## Unity 6.6 and Input System guardrails
- `ProjectSettings/ProjectSettings.asset` uses `activeInputHandler: 1` (New Input System only). Never call legacy `UnityEngine.Input` in runtime or Editor Play Mode scripts; use `UnityEngine.InputSystem` (`Keyboard.current`, `Mouse.current`).
- Target C# 9.0 syntax. In new code, avoid deprecated `FindFirstObjectByType` and `FindObjectsSortMode`; use `FindAnyObjectByType<T>()` and `FindObjectsByType<T>(FindObjectsInactive.Include)`.
- Guard Editor-only fields and logic with `#if UNITY_EDITOR` and disable Editor test components when `!Application.isEditor` so Android builds stay warning-free and run zero Editor test code.

## Editor Play Mode camera and XR Simulation
- `Assets/XR/XRGeneralSettingsPerBuildTarget.asset` has `SimulationLoader` enabled for Standalone/Editor Play Mode, which drives `TrackedPoseDriver` on `Main Camera`.
- Any Editor Play Mode walk/look controller on `XR Origin` must disable `TrackedPoseDriver` (and `ARCameraBackground` for visible 3D preview) inside `#if UNITY_EDITOR` and keep `Main Camera` locked to `localPosition = Vector3.zero` and `localRotation = Quaternion.identity` in `Awake`, `OnEnable`, `Start`, and `LateUpdate`. Otherwise `Main Camera` drifts from `XR Origin` (making the building orbit when turning) and `Camera Offset` pitch above `Main Camera` yaw produces roll tilt.
- Do not modify `XRGeneralSettingsPerBuildTarget.asset` or disable `TrackedPoseDriver` in serialized scene assets; keep Android ARCore tracking untouched.

## Domain and UI conventions
- Official DLSU building codes: `M` = St. Miguel Hall, `SJ` = St. Joseph Hall, `GK` = Gokongwei Hall.
- Startup goes through the auto-loading splash screen straight into destination selection (no legacy Start button main menu).
- Keep `showTopMapWhenDrawerOpen = false` in the destination select drawer unless asked to reintegrate the 2D map preview.
