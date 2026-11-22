# AR Campus Guide

Android augmented reality indoor navigation system for De La Salle University (BS ECE thesis `WCEL-3-2526-C1`). The app pairs ARCore tracking with ESP32 BLE beacons, Wi-Fi RSSI fingerprinting, and semantic Unity NavMesh routing to guide students and visitors through campus buildings, with dedicated accessibility routing for persons with disabilities (PWD).

## Building codes

The navigation catalog and room search filter map DLSU building prefixes across the three target halls:

| Code | Building | Status |
|---|---|---|
| `M` | St. Miguel Hall | 1F model (`mig1.fbx`) baked, calibrated, and active in `SampleScene.unity` (`M101`, `M102`, `Men CR`) |
| `SJ` | St. Joseph Hall | Destination catalog and metadata inference ready; 3D floor models pending export |
| `GK` | Gokongwei Hall | Destination catalog and metadata inference ready; 3D floor models pending export |

Additional floor models (`mig2`, `mig3`, `sj1`, `gk1`, etc.) auto-register through `CampusBuildingManager` and connect across elevations via multi-floor `NavMeshLink` components.

## Tech stack

- **Engine:** Unity `6000.6.1f1` (Unity 6.6)
- **Rendering:** Universal Render Pipeline (`com.unity.render-pipelines.universal` `17.6.0`) with custom HLSL depth-only wall occlusion
- **AR:** AR Foundation `6.6.2` and Google ARCore XR Plugin `6.6.2`
- **Navigation:** Unity AI Navigation `2.0.14` (`NavMeshSurface`, `NavMeshModifierVolume`, `NavMeshLink`, `NavMeshQueryFilter`)
- **Input & UI:** Unity Input System `1.20.0`, uGUI `2.6.0`, and TextMeshPro
- **Android Native Plugins:** Custom Java bridges in `Assets/Plugins/Android/` (`RadioScannerPlugin.java` for Wi-Fi + BLE scanning, `CompassHelper.java` for sensor fusion azimuth)
- **Analytics:** Python 3 scripts for spatial traffic heatmaps, BLE range summaries, and statistical benchmarking

## Features by objective

- **SO1 (3D maps & semantic NavMesh):** Custom NavMesh area definitions (`Stairs`, `Ramp`, `Elevator`, `Classroom`) in `ProjectSettings/NavMeshAreas.asset`. Includes a non-destructive corridor segment trimmer in `Assets/Editor/Mig1NavMeshSetup.cs` (shortens `Room 102` to `Men CR` by 3.5 m without distorting adjacent rooms) and editor helpers for placing multi-floor `NavMeshLink`s.
- **SO2 (IoT localization & spatial alignment):** Native Android Wi-Fi and BLE scanner (`RadioScannerPlugin.java` + `RadioScanner.cs`), phone compass orientation (`CompassHelper.java` + `CompassHeading.cs`), hysteresis BLE beacon peak-snapping (`BlePositionProvider.cs`), and an Exponential Moving Average + inverse-distance weighted k-NN engine (`KnnPositionProvider.cs`) that reads recorded RSSI maps from `fingerprints.csv`. `SpatialAnchorAligner.cs` translates `XR Origin` from any `IPositionProvider` fix without fighting ARCore pose updates.
- **SO3 (AR guidance & Figma v2 UI):** Scrolling floor chevron ribbon and pooled 3D directional arrows (`PathVisualizer.cs`, `PathArrowPool.cs`), rotating 3D destination pins (`DestinationMarkerVisual.cs`), distance-aware billboarding door signboards (`CampusBuildingManager.cs`), and an invisible URP wall-occlusion material (`Assets/Shaders/ARWallOcclusion.shader`) that hides virtual arrows behind real walls. At startup, `UIManager.cs` runs an animated progress splash screen and opens straight into the frosted room search bottom sheet (`DestinationManager.cs`) with `Recently Searched` history, paired with a HUD bearing badge and wheelchair accessibility button (`NavigationHUD.cs`), plus an optional top-down NavMesh floorplan module (`Map2DController.cs`).
- **SO4 (PWD routing & traffic heatmaps):** Toggling Assisted / PWD mode builds a `NavMeshQueryFilter` that masks out `Stairs` and `Jump` areas while lowering costs on `Ramp` and `Elevator` paths relative to standard floors. Deviations over 2.0 m trigger automatic rerouting. `NavigationTelemetryLogger.cs` records anonymized 1 Hz trajectory samples and reroute coordinates to `nav_heatmap_log.csv` for plotting with `generate_heatmap.py`.
- **SO5 (Usability & localization benchmarks):** Logs session duration, distance walked, and deviation counts to `usability_trials.csv`. Includes a field BLE range tester (`BleRangeTester1.cs`) and a paired GPS vs. IoT accuracy/latency logger (`so5_gps_vs_iot_benchmark.csv`) analyzed by `evaluate_so5_stats.py` (MAE, MSE, RMSE, 95% confidence intervals, paired t-tests, and OLS linear regression).

## Project structure

```text
Assets/
  Editor/
    Mig1NavMeshSetup.cs              # NavMesh bake, semantic zones, floor links, hallway trimmer, AR prep
  Plugins/Android/
    AndroidManifest.xml              # Camera, Location, BLE, and Wi-Fi runtime permissions
    CompassHelper.java               # Android RotationVector / geomagnetic compass bridge
    RadioScannerPlugin.java          # Android WifiManager + BluetoothLeScanner bridge
  Scenes/
    CalibratedMeshes/                # Non-destructively trimmed corridor mesh assets
    SampleScene/NavMesh-mig1.asset   # Baked NavMesh data for St. Miguel Hall 1F
    mig1.fbx                         # Base CAD model for St. Miguel Hall 1F
    SampleScene.unity                # Main AR navigation scene
  Scripts/
    Analytics/                       # Trajectory telemetry + Python heatmap & SO5 stats scripts
    Radio/                           # RadioScanner, FingerprintRecorder, KnnPositionProvider
    BlePositionProvider.cs           # Peak-snapping BLE localization provider
    BleRangeTester1.cs               # Field beacon range measurement tool
    CampusBuildingManager.cs         # Multi-building registry & 3D door signboards
    CampusPoiMetadata.cs             # Building code (M / SJ / GK) & room inference
    DestinationManager.cs            # Frosted bottom sheet room search & catalog
    NavigationHUD.cs                 # Top bearing badge, prompts, wheelchair toggle, debug shortcut
    PathVisualizer.cs                # A* NavMesh pathfinding, PWD filter, deviation checks
    SpatialAnchorAligner.cs          # XR Origin alignment from IPositionProvider fixes
  Shaders/
    ARWallOcclusion.shader           # Depth-only URP occlusion pass (ColorMask 0, ZWrite On)
  UIManager.cs                       # Startup loading screen and panel state machine
ProjectSettings/
  Mig1HallwayTrimState.json          # Saved hallway mesh trim offsets
  NavMeshAreas.asset                 # Custom navigation area costs (Stairs, Ramp, Elevator, Classroom)
Wireframes/                          # UI layout references
```

## Getting started

On Windows, enable long Git paths before cloning because of the proposal filename inside `Project Docu/`:

```powershell
git config --global core.longpaths true
git clone https://github.com/pamelacalma/unity-AR-CAMPUS-GUIDE.git
```

1. Install **Unity 6.6 (`6000.6.1f1`)** with the **Android Build Support** module (SDK, NDK, OpenJDK) in Unity Hub.
2. Open the cloned repository folder as a Unity project.
3. Load `Assets/Scenes/SampleScene.unity`.

## Unity editor tools

All scene automation commands live under `Tools > AR Campus Guide` in the Unity menu bar:

| Menu item | Purpose |
|---|---|
| `1. Build NavMesh from mig1 (Keep Mesh Visible)` | Restores visible editor materials and bakes `NavMesh-mig1.asset` |
| `1b. Build NavMesh from mig1 (Hide Mesh for AR)` | Bakes NavMesh and switches walls to `ARWallOcclusion.mat` |
| `2. Auto-Wire Cylinders & Anchors` | Connects `Destination_*`, `Entrance_*`, and beacon anchors to managers |
| `3. Hide Marker Mesh Renderers (For AR Build)` | Hides placeholder destination cylinders and floor cubes |
| `3b. Show Marker Mesh Renderers (For Editor)` | Re-enables placeholder marker meshes for editing |
| `4. Prepare Scene for AR Build` | Disables stray FBX cameras, hides markers, applies wall occlusion, tags POIs, and saves the scene |
| `5a.` – `5d. Add Semantic Zone` | Spawns `NavMeshModifierVolume` boxes for `Stairs`, `Ramp`, `Elevator`, or `Classroom` |
| `5e.` – `5f. Add Multi-Floor Link` | Spawns vertical `NavMeshLink` connectors for stairs or elevators/ramps |
| `6a. Shorten Hallway (Room 102 -> Men CR by 3.5m)` | Applies the calibrated 3.5 m corridor trim and re-bakes NavMesh |
| `6b. Open Hallway Trimmer (Fine-Tune)...` | Opens the UI Toolkit window with live Scene View cut guides |

## Building for Android

Player settings target **Android 8.0+ (Min SDK 26)**, **IL2CPP**, **ARM64**, and **OpenGLES3**, with Camera, Fine Location, Bluetooth Scan/Connect, and Nearby Wi-Fi permissions declared in `Assets/Plugins/Android/AndroidManifest.xml`.

1. Run `Tools > AR Campus Guide > 4. Prepare Scene for AR Build`.
2. Open `File > Build Profiles`, select **Android**, and click **Switch Profile** if it is not yet active.
3. Confirm `Assets/Scenes/SampleScene.unity` is checked in the scene list.
4. Enable **USB debugging** on an ARCore-supported phone (with Google Play Services for AR installed), select the device under **Run Device**, and click **Build And Run** (or click **Build** to export a standalone `.apk`).

Accept the Camera, Location, and Nearby Devices permission prompts on first launch, and keep Bluetooth and Location turned on while navigating.

## On-device debug controls and field data

Field testing overlays start hidden so the live AR view stays uncluttered. Tap anywhere on the screen with **three fingers** in AR view to toggle developer overlays (`MockPositionProvider`, `BlePositionProvider`, `KnnPositionProvider`, `BleRangeTester`, `FingerprintRecorder`, and `NavigationTelemetryLogger`).

Recorded CSV files are saved in `Application.persistentDataPath` on the phone:
- `fingerprints.csv` (Wi-Fi + BLE reference scans for k-NN localization)
- `ble_range_test.csv` (RSSI distance and packet detection trials)
- `nav_heatmap_log.csv` (1 Hz trajectory coordinates and deviation events)
- `usability_trials.csv` (per-session completion time, distance walked, and reroute counts)
- `so5_gps_vs_iot_benchmark.csv` (paired IoT vs. smartphone GPS error and latency samples)
- `user_feedback.csv` (in-app issue reports)

Pull CSVs via `adb pull` and run the bundled Python scripts against them:

```bash
python Assets/Scripts/Analytics/generate_heatmap.py path/to/nav_heatmap_log.csv
python Assets/Scripts/Analytics/evaluate_so5_stats.py path/to/so5_gps_vs_iot_benchmark.csv path/to/usability_trials.csv
python Assets/Scripts/ble_range_summary.py path/to/ble_range_test.csv --threshold -65
```
