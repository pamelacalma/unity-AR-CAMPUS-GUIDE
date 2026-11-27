using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using Unity.AI.Navigation;
using Unity.AI.Navigation.Editor;
using Unity.XR.CoreUtils;
using UITK = UnityEngine.UIElements;

public static class Mig1NavMeshSetup
{
    private const string Mig1FolderAssetPath = "Assets/Scenes/MIGUEL/MIGUEL_1st_Floor.fbx";
    private const string Mig1PrimaryAssetPath = "Assets/Scenes/MIGUEL_1st_Floor.fbx";
    private const string Mig1FallbackAssetPath = "Assets/Scenes/mig1.fbx";
    private const string CalibratedMeshDir = "Assets/Scenes/CalibratedMeshes";
    private const string TrimStatePath = "ProjectSettings/Mig1HallwayTrimState.json";
    public const float DefaultCutWestX = -25.0f;
    public const float DefaultCutEastX = -12.5f;
    public const float DefaultTrimMeters = 3.5f;
    private const string HideCanvasInSceneViewPrefKey = "ARCampusGuide.HideCanvasInSceneView";

    private static readonly string[] OrderedFloorNames =
    {
        "MIGUEL_1st_Floor",
        "MIGUEL_2nd_Floor",
        "MIGUEL_3rd_Floor",
        "MIGUEL_4th_Floor",
        "mig1"
    };

    private static readonly string[] OrderedSjFloorNames =
    {
        "SJ_1st_Floor",
        "SJ_2nd_Floor",
        "SJ_3rd_Floor",
        "SJ_4th_Floor",
        "SJ_5th_Floor",
        "SJ_6th_Floor"
    };

    private static readonly string[] OrderedGoksFloorNames =
    {
        "GOKS_1st_Floor",
        "GOKS_2nd_Floor",
        "GOKS_3rd_Floor",
        "GOKS_4th_Floor"
    };

    private static string ResolveMig1AssetPath()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(Mig1FolderAssetPath) != null)
            return Mig1FolderAssetPath;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(Mig1PrimaryAssetPath) != null)
            return Mig1PrimaryAssetPath;
        return Mig1FallbackAssetPath;
    }

    private static GameObject LoadFloorPrefab(string folderPath, string flatPath)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(folderPath);
        if (prefab != null)
            return prefab;
        return AssetDatabase.LoadAssetAtPath<GameObject>(flatPath);
    }

    public static GameObject FindPrimaryFloorObject()
    {
        GameObject firstFloor = FindActiveRootByName("MIGUEL_1st_Floor");
        if (firstFloor != null)
            return firstFloor;
        return FindActiveRootByName("mig1");
    }

    private static GameObject FindActiveRootByName(string exactName)
    {
        foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (go == null || string.IsNullOrEmpty(go.scene.name) || go.transform.parent != null)
                continue;
            if (string.Equals(go.name, exactName, StringComparison.OrdinalIgnoreCase))
                return go;
        }
        return null;
    }

    private static List<GameObject> FindFloorsByOrderedNames(string[] orderedNames)
    {
        var found = new List<GameObject>();
        foreach (string floorName in orderedNames)
        {
            GameObject go = FindActiveRootByName(floorName);
            if (go != null && !found.Contains(go))
                found.Add(go);
        }
        return found;
    }

    private static List<GameObject> FindAllMiguelFloorObjects() => FindFloorsByOrderedNames(OrderedFloorNames);

    private static List<GameObject> FindAllSjFloorObjects() => FindFloorsByOrderedNames(OrderedSjFloorNames);

    private static List<GameObject> FindAllGoksFloorObjects() => FindFloorsByOrderedNames(OrderedGoksFloorNames);

    private static List<GameObject> FindAllBuildingFloorObjects()
    {
        var found = FindAllMiguelFloorObjects();
        foreach (GameObject sj in FindAllSjFloorObjects())
        {
            if (!found.Contains(sj))
                found.Add(sj);
        }
        foreach (GameObject goks in FindAllGoksFloorObjects())
        {
            if (!found.Contains(goks))
                found.Add(goks);
        }
        return found;
    }

    [MenuItem("Tools/AR Campus Guide/6a. Shorten Hallway (Room 102 -> Men CR by 3.5m)")]
    public static void QuickTrimRoom102ToMenCr()
    {
        ApplyHallwaySegmentTrim(DefaultTrimMeters, DefaultCutWestX, DefaultCutEastX, shiftMarkers: true, hideRenderersAfterBake: false);
    }

    [MenuItem("Tools/AR Campus Guide/6b. Open Hallway Trimmer (Fine-Tune)...")]
    public static void OpenHallwayTrimmer()
    {
        Mig1ScaleCalibratorWindow.ShowWindow();
    }

    [MenuItem("Tools/AR Campus Guide/1. Build NavMesh for All Floors (Miguel + SJ + GOKS, Keep Mesh Visible)")]
    public static void BuildVisible()
    {
        BuildFromMig1(hideRenderersAfterBake: false);
    }

    [MenuItem("Tools/AR Campus Guide/1b. Build NavMesh for All Floors (Miguel + SJ + GOKS, Hide Mesh for AR)")]
    public static void BuildHiddenForAR()
    {
        BuildFromMig1(hideRenderersAfterBake: true);
    }

    private static void StackBuildingFloors(
        GameObject baseFloor,
        string folderSubdir,
        (string name, Vector3 localOffset, float yawOffset)[] entries,
        bool instantiateMissing,
        List<string> report)
    {
        Vector3 basePos = baseFloor.transform.position;
        Quaternion baseRot = baseFloor.transform.rotation;
        report.Add($"{baseFloor.name}: pos={basePos:F2}, rotY={baseRot.eulerAngles.y:F1}° (base)");

        foreach (var entry in entries)
        {
            GameObject floorObj = FindActiveRootByName(entry.name);
            if (floorObj == null && instantiateMissing)
            {
                GameObject prefab = LoadFloorPrefab(
                    $"Assets/Scenes/{folderSubdir}/{entry.name}.fbx",
                    $"Assets/Scenes/{entry.name}.fbx");
                if (prefab != null)
                {
                    floorObj = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    floorObj.name = entry.name;
                    Undo.RegisterCreatedObjectUndo(floorObj, $"Instantiate {entry.name}");
                }
            }

            if (floorObj == null)
                continue;

            Undo.RecordObject(floorObj.transform, "Align & Stack Floors");
            Vector3 targetPos = basePos + (baseRot * entry.localOffset);
            Quaternion targetRot = baseRot * Quaternion.Euler(0f, entry.yawOffset, 0f);
            floorObj.transform.SetPositionAndRotation(targetPos, targetRot);
            PrefabUtility.RecordPrefabInstancePropertyModifications(floorObj.transform);
            EditorUtility.SetDirty(floorObj.transform);
            report.Add($"{floorObj.name}: pos={targetPos:F2}, rotY={targetRot.eulerAngles.y:F1}°");
        }
    }

    [MenuItem("Tools/AR Campus Guide/1c. Align & Stack All Floors (Miguel 1F-4F + SJ 1F-6F + GOKS 1F-4F)")]
    public static void AlignAndStackFloors()
    {
        GameObject floor1 = FindPrimaryFloorObject();
        GameObject sj1 = FindActiveRootByName("SJ_1st_Floor");
        GameObject goks1 = FindActiveRootByName("GOKS_1st_Floor");
        if (floor1 == null && sj1 == null && goks1 == null)
        {
            EditorUtility.DisplayDialog(
                "Base Floor Not Found",
                "Could not find 'MIGUEL_1st_Floor' (or 'mig1'), 'SJ_1st_Floor', or 'GOKS_1st_Floor' in the active scene.",
                "OK");
            return;
        }

        var report = new List<string>();
        UnityEngine.SceneManagement.Scene activeScene = floor1 != null
            ? floor1.scene
            : (sj1 != null ? sj1.scene : goks1.scene);

        if (floor1 != null)
        {
            var miguelStackOffsets = new (string name, Vector3 localOffset, float yawOffset)[]
            {
                ("MIGUEL_2nd_Floor", new Vector3(-4.47f, 1.70f, -3.16f), 180f),
                ("MIGUEL_3rd_Floor", new Vector3(-15.10f, 5.47f, -3.01f), 180f),
                ("MIGUEL_4th_Floor", new Vector3(-15.10f, 9.24f, -3.01f), 180f)
            };
            StackBuildingFloors(floor1, "MIGUEL", miguelStackOffsets, instantiateMissing: false, report);
        }

        if (sj1 != null)
        {
            var sjStackOffsets = new (string name, Vector3 localOffset, float yawOffset)[]
            {
                ("SJ_2nd_Floor", new Vector3(0f, 3.35f, -6.59f), 0f),
                ("SJ_3rd_Floor", new Vector3(0f, 6.70f, -6.59f), 0f),
                ("SJ_4th_Floor", new Vector3(0f, 10.05f, -6.59f), 0f),
                ("SJ_5th_Floor", new Vector3(0f, 13.40f, -6.59f), 0f),
                ("SJ_6th_Floor", new Vector3(0f, 16.75f, -6.59f), 0f)
            };
            StackBuildingFloors(sj1, "SJ", sjStackOffsets, instantiateMissing: true, report);
        }

        if (goks1 != null)
        {
            var goksStackOffsets = new (string name, Vector3 localOffset, float yawOffset)[]
            {
                ("GOKS_2nd_Floor", new Vector3(0f, 3.35f, 0f), -90f),
                ("GOKS_3rd_Floor", new Vector3(0f, 6.70f, 0f), -90f),
                ("GOKS_4th_Floor", new Vector3(0f, 10.05f, 0f), -90f)
            };
            StackBuildingFloors(goks1, "GOKS", goksStackOffsets, instantiateMissing: true, report);
        }

        EditorSceneManager.MarkSceneDirty(activeScene);
        EditorSceneManager.SaveOpenScenes();

        string summary = string.Join("\n", report);
        Debug.Log("[Mig1NavMeshSetup] Aligned & stacked floors:\n" + summary);
        EditorUtility.DisplayDialog("Floors Aligned", summary, "OK");
    }

    [MenuItem("Tools/AR Campus Guide/2. Auto-Wire Cylinders & Anchors")]
    public static void AutoWireSceneReferences()
    {
        var destinations = new List<Transform>();
        var fallbackCylinders = new List<Transform>();
        var fallbackCubes = new List<Transform>();
        Transform entranceAnchor = null;
        var bleAnchorByName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);

        GameObject[] allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        Transform destinationsParent = null;
        foreach (GameObject go in allObjects)
        {
            if (string.Equals(go.name, "Destinations", StringComparison.OrdinalIgnoreCase))
            {
                destinationsParent = go.transform;
                break;
            }
        }

        foreach (GameObject go in allObjects)
        {
            if (string.IsNullOrEmpty(go.scene.name)) continue;
            string n = go.name.Trim();

            if (n.StartsWith("Destination_", StringComparison.OrdinalIgnoreCase) ||
                (destinationsParent != null && go.transform.parent == destinationsParent))
            {
                destinations.Add(go.transform);
                continue;
            }

            if (n.IndexOf("entrance", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !n.StartsWith("ESP32_", StringComparison.OrdinalIgnoreCase) &&
                !n.StartsWith("BEACON_", StringComparison.OrdinalIgnoreCase))
            {
                if (entranceAnchor == null || n.Equals("Entrance_1", StringComparison.OrdinalIgnoreCase))
                {
                    entranceAnchor = go.transform;
                }
                continue;
            }

            if (n.StartsWith("BEACON_", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("ESP32_", StringComparison.OrdinalIgnoreCase))
            {
                bleAnchorByName[n] = go.transform;
                continue;
            }

            if (go.GetComponent<MeshFilter>() != null)
            {
                if (n.StartsWith("Cylinder", StringComparison.OrdinalIgnoreCase))
                    fallbackCylinders.Add(go.transform);
                else if (n.StartsWith("Cube", StringComparison.OrdinalIgnoreCase))
                    fallbackCubes.Add(go.transform);
            }
        }

        // Exclude legacy "Destination_Cabinet" if newer destinations exist.
        if (destinations.Count > 1)
        {
            destinations.RemoveAll(t => t.name.Trim().Equals("Destination_Cabinet", StringComparison.OrdinalIgnoreCase));
        }

        // If no Destination_* objects exist yet, use Cylinder* objects as destinations.
        if (destinations.Count == 0 && fallbackCylinders.Count > 0)
        {
            fallbackCylinders.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            destinations.AddRange(fallbackCylinders);
        }
        else
        {
            destinations.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        // Fallback to first Cube* as entrance anchor if no Entrance_* object exists.
        if (entranceAnchor == null && fallbackCubes.Count > 0)
        {
            fallbackCubes.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            entranceAnchor = fallbackCubes[0];
        }

        var report = new List<string>();

        // 1. DestinationManager.targets
        DestinationManager destMgr = UnityEngine.Object.FindFirstObjectByType<DestinationManager>();
        if (destMgr != null)
        {
            Undo.RecordObject(destMgr, "Auto-Wire DestinationManager");
            var so = new SerializedObject(destMgr);
            SerializedProperty targetsProp = so.FindProperty("targets");
            targetsProp.ClearArray();
            for (int i = 0; i < destinations.Count; i++)
            {
                targetsProp.InsertArrayElementAtIndex(i);
                targetsProp.GetArrayElementAtIndex(i).objectReferenceValue = destinations[i];
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(destMgr);

            var destNames = new List<string>();
            foreach (var d in destinations) destNames.Add(d.name);
            report.Add($"DestinationManager.targets ({destinations.Count}): {string.Join(", ", destNames)}");
        }

        // 2. PathVisualizer: clear stale Destination_Cabinet target if excluded
        PathVisualizer pathVis = UnityEngine.Object.FindFirstObjectByType<PathVisualizer>();
        if (pathVis != null)
        {
            Undo.RecordObject(pathVis, "Clean PathVisualizer Target");
            var so = new SerializedObject(pathVis);
            SerializedProperty destProp = so.FindProperty("destinationTarget");
            Transform curDest = destProp.objectReferenceValue as Transform;
            if (curDest == null || !destinations.Contains(curDest))
            {
                destProp.objectReferenceValue = null;
                report.Add("PathVisualizer.destinationTarget: cleared to None (pick a destination at runtime)");
            }
            else
            {
                report.Add($"PathVisualizer.destinationTarget: {curDest.name}");
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(pathVis);
        }

        // 3. MockPositionProvider.beacons
        MockPositionProvider mock = UnityEngine.Object.FindFirstObjectByType<MockPositionProvider>();
        if (mock != null && entranceAnchor != null)
        {
            Undo.RecordObject(mock, "Auto-Wire MockPositionProvider");
            var so = new SerializedObject(mock);
            SerializedProperty beaconsProp = so.FindProperty("beacons");
            if (beaconsProp.arraySize == 0)
            {
                beaconsProp.InsertArrayElementAtIndex(0);
                beaconsProp.GetArrayElementAtIndex(0).FindPropertyRelative("beaconName").stringValue = "entrance";
            }
            beaconsProp.GetArrayElementAtIndex(0).FindPropertyRelative("anchor").objectReferenceValue = entranceAnchor;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(mock);
            report.Add($"MockPositionProvider.beacons[0].anchor: {entranceAnchor.name}");
        }

        // 4. BlePositionProvider.beacons
        BlePositionProvider ble = UnityEngine.Object.FindFirstObjectByType<BlePositionProvider>();
        if (ble != null)
        {
            Undo.RecordObject(ble, "Auto-Wire BlePositionProvider");
            var so = new SerializedObject(ble);
            SerializedProperty beaconsProp = so.FindProperty("beacons");
            int wiredBle = 0;

            for (int i = 0; i < beaconsProp.arraySize; i++)
            {
                SerializedProperty elem = beaconsProp.GetArrayElementAtIndex(i);
                string advName = elem.FindPropertyRelative("advertisedName").stringValue;
                SerializedProperty anchorProp = elem.FindPropertyRelative("anchor");

                if (!string.IsNullOrEmpty(advName) && bleAnchorByName.TryGetValue(advName, out Transform matched))
                {
                    anchorProp.objectReferenceValue = matched;
                    wiredBle++;
                }
                else if (anchorProp.objectReferenceValue == null && entranceAnchor != null)
                {
                    anchorProp.objectReferenceValue = entranceAnchor;
                    wiredBle++;
                }
            }

            foreach (var kv in bleAnchorByName)
            {
                bool exists = false;
                for (int i = 0; i < beaconsProp.arraySize; i++)
                {
                    string existing = beaconsProp.GetArrayElementAtIndex(i).FindPropertyRelative("advertisedName").stringValue;
                    if (string.Equals(existing, kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    int idx = beaconsProp.arraySize;
                    beaconsProp.InsertArrayElementAtIndex(idx);
                    SerializedProperty elem = beaconsProp.GetArrayElementAtIndex(idx);
                    elem.FindPropertyRelative("advertisedName").stringValue = kv.Key;
                    SerializedProperty isWifiProp = elem.FindPropertyRelative("isWifiAp");
                    if (isWifiProp != null) isWifiProp.boolValue = false;
                    elem.FindPropertyRelative("anchor").objectReferenceValue = kv.Value;
                    elem.FindPropertyRelative("macAddress").stringValue = "";
                    wiredBle++;
                }
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(ble);
            report.Add($"BlePositionProvider.beacons updated: {wiredBle}");
        }

        // 5. RadioScanner -> KnnPositionProvider / FingerprintRecorder -> SpatialAnchorAligner
        RadioScanner scanner = UnityEngine.Object.FindAnyObjectByType<RadioScanner>();
        KnnPositionProvider knn = UnityEngine.Object.FindAnyObjectByType<KnnPositionProvider>();
        if (knn != null && scanner != null)
        {
            Undo.RecordObject(knn, "Auto-Wire KnnPositionProvider");
            var so = new SerializedObject(knn);
            SerializedProperty sp = so.FindProperty("scanner");
            if (sp != null) sp.objectReferenceValue = scanner;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(knn);
            report.Add("KnnPositionProvider.scanner wired to RadioScanner");
        }

        FingerprintRecorder recorder = UnityEngine.Object.FindAnyObjectByType<FingerprintRecorder>();
        if (recorder != null)
        {
            Undo.RecordObject(recorder, "Auto-Wire FingerprintRecorder");
            var so = new SerializedObject(recorder);
            if (scanner != null)
            {
                SerializedProperty sp = so.FindProperty("scanner");
                if (sp != null) sp.objectReferenceValue = scanner;
            }
            if (Camera.main != null)
            {
                SerializedProperty camProp = so.FindProperty("cameraTransform");
                if (camProp != null) camProp.objectReferenceValue = Camera.main.transform;
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(recorder);
            report.Add("FingerprintRecorder wired");
        }

        SpatialAnchorAligner aligner = UnityEngine.Object.FindAnyObjectByType<SpatialAnchorAligner>();
        if (aligner != null)
        {
            Undo.RecordObject(aligner, "Auto-Wire SpatialAnchorAligner");
            var so = new SerializedObject(aligner);
            MonoBehaviour primary = knn != null ? (MonoBehaviour)knn : ble;
            if (primary != null)
                so.FindProperty("providerBehaviour").objectReferenceValue = primary;
            if (mock != null)
                so.FindProperty("secondaryProviderBehaviour").objectReferenceValue = mock;
            if (knn != null && ble != null)
            {
                SerializedProperty tertProp = so.FindProperty("tertiaryProviderBehaviour");
                if (tertProp != null) tertProp.objectReferenceValue = ble;
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(aligner);
            report.Add("SpatialAnchorAligner providers wired (Wk-NN + Mock + BLE/Wi-Fi)");
        }

        EditorSceneManager.MarkAllScenesDirty();
        EditorSceneManager.SaveOpenScenes();

        string summary = report.Count > 0 ? string.Join("\n", report) : "No Navigation Manager components found.";
        Debug.Log("[Mig1NavMeshSetup] Auto-Wire complete:\n" + summary);
        EditorUtility.DisplayDialog("AR Campus Guide Auto-Wire", summary, "OK");
    }

    [MenuItem("Tools/AR Campus Guide/3. Hide Marker Mesh Renderers (For AR Build)")]
    public static void HideMarkerRenderers()
    {
        SetMarkerRenderersVisible(false);
    }

    [MenuItem("Tools/AR Campus Guide/3b. Show Marker Mesh Renderers (For Editor)")]
    public static void ShowMarkerRenderers()
    {
        SetMarkerRenderersVisible(true);
    }

    [MenuItem("Tools/AR Campus Guide/3c. Hide UI Canvas in Scene View (Keep Active for AR)")]
    public static void HideCanvasInSceneView()
    {
        EditorPrefs.SetBool(HideCanvasInSceneViewPrefKey, true);
        List<Canvas> canvases = FindSceneOverlayCanvases();
        bool activatedAny = false;

        foreach (Canvas canvas in canvases)
        {
            if (!canvas.gameObject.activeSelf || canvas.transform.localScale == Vector3.zero)
            {
                Undo.RecordObject(canvas.gameObject, "Enable Canvas For AR");
                Undo.RecordObject(canvas.transform, "Fix Canvas Scale");
                canvas.gameObject.SetActive(true);
                if (canvas.transform.localScale == Vector3.zero)
                    canvas.transform.localScale = Vector3.one;
                EditorUtility.SetDirty(canvas.gameObject);
                EditorSceneManager.MarkSceneDirty(canvas.gameObject.scene);
                activatedAny = true;
            }

            SceneVisibilityManager.instance.Hide(canvas.gameObject, true);
        }

        if (activatedAny)
            EditorSceneManager.SaveOpenScenes();

        SceneView.RepaintAll();
        Debug.Log($"[Mig1NavMeshSetup] Hid {canvases.Count} ScreenSpaceOverlay Canvas(es) in Scene View while keeping active for Play Mode / AR builds.");
    }

    [MenuItem("Tools/AR Campus Guide/3d. Show UI Canvas in Scene View (For UI Editing)")]
    public static void ShowCanvasInSceneView()
    {
        EditorPrefs.SetBool(HideCanvasInSceneViewPrefKey, false);
        List<Canvas> canvases = FindSceneOverlayCanvases();

        foreach (Canvas canvas in canvases)
        {
            SceneVisibilityManager.instance.Show(canvas.gameObject, true);
        }

        SceneView.RepaintAll();
        Debug.Log($"[Mig1NavMeshSetup] Restored Scene View visibility for {canvases.Count} ScreenSpaceOverlay Canvas(es).");
    }

    public static void ApplySavedCanvasSceneVisibility()
    {
        bool hide = EditorPrefs.GetBool(HideCanvasInSceneViewPrefKey, true);
        foreach (Canvas canvas in FindSceneOverlayCanvases())
        {
            if (hide)
                SceneVisibilityManager.instance.Hide(canvas.gameObject, true);
            else
                SceneVisibilityManager.instance.Show(canvas.gameObject, true);
        }
        SceneView.RepaintAll();
    }

    private static List<Canvas> FindSceneOverlayCanvases()
    {
        var list = new List<Canvas>();
        foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
        {
            if (canvas == null || string.IsNullOrEmpty(canvas.gameObject.scene.name)) continue;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            list.Add(canvas);
        }
        return list;
    }

    [MenuItem("Tools/AR Campus Guide/4. Prepare Scene for AR Build")]
    public static void PrepareSceneForARBuild()
    {
        var report = new List<string>();

        // 1. Delete legacy scene objects if still present.
        string[] legacyNames = { "Untitled", "Destination_Cabinet", "ESP32_ENTRANCE" };
        var deleted = new List<string>();
        foreach (string legacyName in legacyNames)
        {
            foreach (GameObject go in FindSceneObjectsByName(legacyName))
            {
                Undo.DestroyObjectImmediate(go);
                deleted.Add(legacyName);
            }
        }
        if (deleted.Count > 0)
            report.Add("Deleted legacy objects: " + string.Join(", ", deleted));

        // 2. Clean stale BlePositionProvider entries whose anchor was deleted.
        BlePositionProvider ble = UnityEngine.Object.FindFirstObjectByType<BlePositionProvider>();
        if (ble != null)
        {
            Undo.RecordObject(ble, "Clean BlePositionProvider");
            var so = new SerializedObject(ble);
            SerializedProperty beaconsProp = so.FindProperty("beacons");
            int removedBle = 0;
            for (int i = beaconsProp.arraySize - 1; i >= 0; i--)
            {
                SerializedProperty elem = beaconsProp.GetArrayElementAtIndex(i);
                if (elem.FindPropertyRelative("anchor").objectReferenceValue == null)
                {
                    beaconsProp.DeleteArrayElementAtIndex(i);
                    removedBle++;
                }
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(ble);
            if (removedBle > 0)
                report.Add($"Removed {removedBle} stale BlePositionProvider entry(s)");
        }

        // 3. Disable any stray Camera components (e.g. from imported FBX) and fix Main Camera.
        Camera mainCam = null;
        int disabledCams = 0;
        foreach (Camera cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (string.IsNullOrEmpty(cam.gameObject.scene.name)) continue;
            if (cam.gameObject.name == "Main Camera" || cam.CompareTag("MainCamera"))
            {
                mainCam = cam;
                continue;
            }
            if (cam.enabled)
            {
                Undo.RecordObject(cam, "Disable Stray Camera");
                cam.enabled = false;
                EditorUtility.SetDirty(cam);
                disabledCams++;
            }
        }
        if (disabledCams > 0)
            report.Add($"Disabled {disabledCams} stray Camera component(s)");

        if (mainCam != null)
        {
            Undo.RecordObject(mainCam.transform, "Reset Main Camera Local Pose");
            Undo.RecordObject(mainCam, "Adjust Main Camera Far Clip");
            mainCam.transform.localPosition = Vector3.zero;
            mainCam.transform.localRotation = Quaternion.identity;
            if (mainCam.farClipPlane < 100f)
                mainCam.farClipPlane = 100f;
            EditorUtility.SetDirty(mainCam);
            report.Add("Main Camera: localPos=(0,0,0), farClip=" + mainCam.farClipPlane);
        }

        // 4. Ensure Canvas is active (with valid scale) and UIManager starts cleanly.
        foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (string.IsNullOrEmpty(canvas.gameObject.scene.name)) continue;
            if (!canvas.gameObject.activeSelf || canvas.transform.localScale == Vector3.zero)
            {
                Undo.RecordObject(canvas.gameObject, "Enable Canvas");
                Undo.RecordObject(canvas.transform, "Fix Canvas Scale");
                canvas.gameObject.SetActive(true);
                if (canvas.transform.localScale == Vector3.zero)
                    canvas.transform.localScale = Vector3.one;
                EditorUtility.SetDirty(canvas.gameObject);
                report.Add($"Enabled Canvas '{canvas.name}'");
            }
        }
        ApplySavedCanvasSceneVisibility();

        UIManager uiMgr = UnityEngine.Object.FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
        if (uiMgr != null)
        {
            if (uiMgr.mainMenuPanel != null)
            {
                foreach (Button b in uiMgr.mainMenuPanel.GetComponentsInChildren<Button>(true))
                {
                    if (b != null && b.gameObject != uiMgr.mainMenuPanel && b.gameObject.activeSelf)
                    {
                        Undo.RecordObject(b.gameObject, "Hide Legacy MainMenu START Button");
                        b.gameObject.SetActive(false);
                        EditorUtility.SetDirty(b.gameObject);
                    }
                }
                EditorUtility.SetDirty(uiMgr.mainMenuPanel);
            }
        }

        // 5. Configure all Miguel, SJ, and GOKS walls with Depth-Only AR Occlusion material (so AR arrows hide behind real walls!)
        //    and hide floor & marker MeshRenderers so AR camera feed stays unobstructed.
        int occludedWalls = 0;
        int hiddenFloors = 0;
        foreach (GameObject floorObj in FindAllBuildingFloorObjects())
        {
            ApplyArOcclusionToBuildingModel(floorObj, out int floorWalls, out int floorHidden);
            occludedWalls += floorWalls;
            hiddenFloors += floorHidden;
        }
        int hiddenMarkers = SetMarkerRenderersVisibleInternal(false);
        report.Add($"AR Wall Occlusion applied: {occludedWalls} wall mesh(es), hidden floor={hiddenFloors}, hidden markers={hiddenMarkers}");

        // 5b. Ensure CampusPoiMetadata (M / SJ / GK), CampusBuildingManager, KnnPositionProvider,
        //     NavigationTelemetryLogger, and Map2DController are attached.
        int taggedPois = EnsureCampusModulesAndMetadata();
        report.Add($"Configured Campus modules & tagged {taggedPois} destination(s) with DLSU codes (M/SJ/GK)");

        // 6. Fix any UI Button referencing stale SetDestinationByIndex -> SelectDestination.
        int fixedButtons = 0;
        foreach (Button btn in UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (string.IsNullOrEmpty(btn.gameObject.scene.name)) continue;
            var so = new SerializedObject(btn);
            SerializedProperty calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
            bool changed = false;
            for (int i = 0; i < calls.arraySize; i++)
            {
                SerializedProperty methodProp = calls.GetArrayElementAtIndex(i).FindPropertyRelative("m_MethodName");
                if (methodProp.stringValue == "SetDestinationByIndex")
                {
                    methodProp.stringValue = "SelectDestination";
                    changed = true;
                    fixedButtons++;
                }
            }
            if (changed)
            {
                Undo.RecordObject(btn, "Fix Destination Button Method");
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(btn);
            }
        }
        if (fixedButtons > 0)
            report.Add($"Fixed {fixedButtons} button binding(s) (SetDestinationByIndex -> SelectDestination)");

        EditorSceneManager.MarkAllScenesDirty();
        EditorSceneManager.SaveOpenScenes();

        string summary = string.Join("\n", report);
        Debug.Log("[Mig1NavMeshSetup] Scene prepared for AR build:\n" + summary);
        EditorUtility.DisplayDialog("AR Scene Prepared", summary, "OK");
    }

    [MenuItem("Tools/AR Campus Guide/7. Place Test Camera Inside Miguel Hall (Editor Play Mode)")]
    public static void PlaceTestCameraInsideMiguelHall()
    {
        XROrigin xrOrigin = UnityEngine.Object.FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
        Transform xrOriginTransform = xrOrigin != null ? xrOrigin.transform : null;

        Camera mainCam = xrOrigin != null && xrOrigin.Camera != null
            ? xrOrigin.Camera
            : (xrOriginTransform != null ? xrOriginTransform.GetComponentInChildren<Camera>(true) : null);

        if (mainCam == null)
        {
            foreach (Camera cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            {
                if (cam == null || string.IsNullOrEmpty(cam.gameObject.scene.name)) continue;
                if (string.Equals(cam.gameObject.name, "Main Camera", StringComparison.Ordinal) || cam.CompareTag("MainCamera"))
                {
                    mainCam = cam;
                    break;
                }
            }
        }

        if (xrOriginTransform == null && mainCam != null)
            xrOriginTransform = mainCam.transform.root;

        if (xrOriginTransform == null || mainCam == null)
        {
            EditorUtility.DisplayDialog(
                "Camera / XR Origin Not Found",
                "Could not find XR Origin or Main Camera in the active scene.",
                "OK");
            return;
        }

        if (!mainCam.gameObject.activeSelf)
        {
            Undo.RecordObject(mainCam.gameObject, "Enable Main Camera GameObject");
            mainCam.gameObject.SetActive(true);
            EditorUtility.SetDirty(mainCam.gameObject);
        }

        Undo.RecordObject(mainCam.transform, "Reset Main Camera Local Pose");
        Undo.RecordObject(mainCam, "Configure Main Camera");
        mainCam.enabled = true;
        mainCam.transform.localPosition = Vector3.zero;
        mainCam.transform.localRotation = Quaternion.identity;
        mainCam.farClipPlane = Mathf.Max(mainCam.farClipPlane, 120f);
        mainCam.nearClipPlane = Mathf.Min(mainCam.nearClipPlane, 0.1f);
        EditorUtility.SetDirty(mainCam);

        Transform camOffset = xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null
            ? xrOrigin.CameraFloorOffsetObject.transform
            : xrOriginTransform.Find("Camera Offset");
        float cameraYOffset = (xrOrigin != null && xrOrigin.CameraYOffset > 0.1f) ? xrOrigin.CameraYOffset : 1.1176f;
        if (camOffset != null)
        {
            Undo.RecordObject(camOffset, "Configure Camera Offset");
            camOffset.localPosition = new Vector3(0f, cameraYOffset, 0f);
            camOffset.localRotation = Quaternion.identity;
            EditorUtility.SetDirty(camOffset);
        }

        GameObject[] sceneObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include);
        Transform entrance = null;
        Transform preferredDestination = null;
        var destinationCandidates = new List<Transform>();

        foreach (GameObject go in sceneObjects)
        {
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            string n = go.name.Trim();

            if (n.IndexOf("entrance", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !n.StartsWith("BEACON_", StringComparison.OrdinalIgnoreCase) &&
                !n.StartsWith("ESP32_", StringComparison.OrdinalIgnoreCase))
            {
                if (entrance == null || string.Equals(n, "Entrance_1", StringComparison.OrdinalIgnoreCase))
                    entrance = go.transform;
            }
            else if (n.StartsWith("Destination_", StringComparison.OrdinalIgnoreCase))
            {
                destinationCandidates.Add(go.transform);
                if (string.Equals(n, "Destination_Room 101", StringComparison.OrdinalIgnoreCase))
                    preferredDestination = go.transform;
            }
        }

        Vector3 startPos;
        float floorY;
        if (entrance != null)
        {
            startPos = entrance.position;
            floorY = NavMesh.SamplePosition(entrance.position, out NavMeshHit hit, 5f, NavMesh.AllAreas)
                ? hit.position.y
                : entrance.position.y;
        }
        else
        {
            GameObject floor1 = FindPrimaryFloorObject();
            if (floor1 != null && TryComputeHierarchyBounds(floor1, out Bounds floorBounds))
            {
                Vector3 centerProbe = new Vector3(floorBounds.center.x, floorBounds.min.y + 0.2f, floorBounds.center.z);
                if (NavMesh.SamplePosition(centerProbe, out NavMeshHit hit, 15f, NavMesh.AllAreas))
                {
                    startPos = hit.position;
                    floorY = hit.position.y;
                }
                else
                {
                    startPos = new Vector3(floorBounds.center.x, floorBounds.min.y, floorBounds.center.z);
                    floorY = startPos.y;
                }
            }
            else
            {
                startPos = new Vector3(18.6f, -2.6f, -12.58f);
                floorY = -2.6f;
            }
        }

        Transform lookTarget = preferredDestination;
        if (lookTarget == null)
        {
            float bestDistSq = float.MaxValue;
            foreach (Transform cand in destinationCandidates)
            {
                if (cand == null) continue;
                if (Mathf.Abs(cand.position.y - floorY) > 2.2f) continue;

                Vector3 delta = cand.position - startPos;
                delta.y = 0f;
                float distSq = delta.sqrMagnitude;
                if (distSq < 4.0f) continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    lookTarget = cand;
                }
            }
        }

        float targetYaw = -90f;
        if (lookTarget != null)
        {
            Vector3 dir = lookTarget.position - startPos;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        }

        if (entrance != null)
        {
            Undo.RecordObject(entrance, "Aim Entrance Anchor Down Hallway");
            entrance.rotation = Quaternion.Euler(0f, targetYaw, 0f);
            EditorUtility.SetDirty(entrance);
        }

        Quaternion targetRot = Quaternion.Euler(0f, targetYaw, 0f);
        Vector3 targetCamWorld = new Vector3(startPos.x, floorY + 1.35f, startPos.z);
        Vector3 camLocalPos = xrOriginTransform.InverseTransformPoint(mainCam.transform.position);
        Undo.RecordObject(xrOriginTransform, "Place XR Origin Inside Miguel Hall");
        xrOriginTransform.SetPositionAndRotation(targetCamWorld - (targetRot * camLocalPos), targetRot);
        EditorUtility.SetDirty(xrOriginTransform);

        List<GameObject> buildingFloors = FindAllBuildingFloorObjects();
        foreach (GameObject floorObj in buildingFloors)
            RestoreEditorMaterialsOnBuilding(floorObj);

        foreach (Canvas canvas in FindSceneOverlayCanvases())
        {
            if (!canvas.gameObject.activeSelf || canvas.transform.localScale == Vector3.zero)
            {
                Undo.RecordObject(canvas.gameObject, "Enable Canvas For Editor Testing");
                Undo.RecordObject(canvas.transform, "Fix Canvas Scale");
                canvas.gameObject.SetActive(true);
                if (canvas.transform.localScale == Vector3.zero)
                    canvas.transform.localScale = Vector3.one;
                EditorUtility.SetDirty(canvas.gameObject);
            }
        }
        ApplySavedCanvasSceneVisibility();

        EditorWalkTester tester = xrOriginTransform.GetComponent<EditorWalkTester>();
        if (tester == null)
            tester = Undo.AddComponent<EditorWalkTester>(xrOriginTransform.gameObject);
        else
            Undo.RecordObject(tester, "Configure EditorWalkTester");

        tester.cameraOffset = camOffset;
        tester.startAnchor = entrance;
        EditorUtility.SetDirty(tester);

        EditorSceneManager.MarkAllScenesDirty();
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = xrOriginTransform.gameObject;

        string targetLabel = lookTarget != null ? lookTarget.name : "west (-X) hallway";
        string summary =
            $"Placed Main Camera at {targetCamWorld:F2} facing {targetYaw:F0}° toward {targetLabel}.\n" +
            $"Restored visible Editor materials on {buildingFloors.Count} floor object(s).\n" +
            $"Attached EditorWalkTester to '{xrOriginTransform.name}'.\n\n" +
            "Play Mode controls:\n" +
            "- WASD / Arrow keys: Walk (hold Shift to sprint)\n" +
            "- Right-Click drag or Q/E: Look / turn\n" +
            "- 1-4 or PageUp / PageDown: Switch Miguel floor (1F-4F)\n" +
            "- R: Reset to entrance | F1: Toggle controls overlay\n\n" +
            "Run 'Tools > AR Campus Guide > 4. Prepare Scene for AR Build' before building an Android APK.";

        Debug.Log("[Mig1NavMeshSetup] " + summary.Replace("\n", " "));
        EditorUtility.DisplayDialog("Test Camera Placed in Miguel Hall", summary, "OK");
    }

    private static bool TryComputeHierarchyBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            if (!hasBounds)
            {
                bounds = r.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }
        return hasBounds;
    }

    private static List<GameObject> FindSceneObjectsByName(string exactName)
    {
        var matches = new List<GameObject>();
        foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!string.IsNullOrEmpty(go.scene.name) && string.Equals(go.name, exactName, StringComparison.Ordinal))
                matches.Add(go);
        }
        return matches;
    }

    private static void SetMarkerRenderersVisible(bool visible)
    {
        int count = SetMarkerRenderersVisibleInternal(visible);
        EditorSceneManager.MarkAllScenesDirty();
        EditorSceneManager.SaveOpenScenes();
        Debug.Log($"[Mig1NavMeshSetup] {(visible ? "Enabled" : "Disabled")} MeshRenderer on {count} marker(s).");
    }

    private static int SetMarkerRenderersVisibleInternal(bool visible)
    {
        int count = 0;
        foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (string.IsNullOrEmpty(go.scene.name)) continue;
            string n = go.name.Trim();
            bool isMarker = n.StartsWith("Destination_", StringComparison.OrdinalIgnoreCase) ||
                            n.StartsWith("Entrance", StringComparison.OrdinalIgnoreCase) ||
                            n.StartsWith("BEACON_", StringComparison.OrdinalIgnoreCase) ||
                            n.StartsWith("ESP32_", StringComparison.OrdinalIgnoreCase);
            if (!isMarker) continue;

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                Undo.RecordObject(mr, visible ? "Show Marker" : "Hide Marker");
                mr.enabled = visible;
                EditorUtility.SetDirty(mr);
                count++;
            }
        }
        return count;
    }

    private struct PlannedBleSpot
    {
        public string name;
        public string floorLabel;
        public string refObjectName;
        public Vector3 fallbackPos;
        public float floorYHint;
        public string roleNote;
        public Color accentColor;
    }

    [MenuItem("Tools/AR Campus Guide/8a. Map Recommended BLE Beacon Spots (Miguel 1F-4F)")]
    public static void MapRecommendedMiguelBleSpots()
    {
        GameObject existing = FindActiveRootByName("Recommended_BLE_Spots_Miguel");
        if (existing != null)
            Undo.DestroyObjectImmediate(existing);

        var byName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        GameObject[] sceneObjs = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include);
        foreach (GameObject go in sceneObjs)
        {
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            string n = go.name.Trim();
            if (!byName.ContainsKey(n))
                byName[n] = go.transform;
        }

        bool has2F = byName.ContainsKey("MIGUEL_2nd_Floor");
        bool has3F = byName.ContainsKey("MIGUEL_3rd_Floor");
        bool has4F = byName.ContainsKey("MIGUEL_4th_Floor");

        Color c1 = new Color(0.15f, 0.90f, 1.00f, 0.95f);
        Color c2 = new Color(0.35f, 0.72f, 1.00f, 0.95f);
        Color c3 = new Color(0.74f, 0.55f, 1.00f, 0.95f);
        Color c4 = new Color(1.00f, 0.60f, 0.28f, 0.95f);

        var spots = new List<PlannedBleSpot>
        {
            new PlannedBleSpot { name = "BLE_Spot_M1F_01", floorLabel = "M 1F", refObjectName = "Entrance_1", fallbackPos = new Vector3(18.60f, -2.60f, -12.58f), floorYHint = -2.60f, roleNote = "", accentColor = c1 },
            new PlannedBleSpot { name = "BLE_Spot_M1F_02", floorLabel = "M 1F", refObjectName = "Destination_Room 101", fallbackPos = new Vector3(1.17f, -2.60f, -13.19f), floorYHint = -2.60f, roleNote = "", accentColor = c1 },
            new PlannedBleSpot { name = "BLE_Spot_M1F_03", floorLabel = "M 1F", refObjectName = "Destination_Room 102", fallbackPos = new Vector3(-12.01f, -2.60f, -13.82f), floorYHint = -2.60f, roleNote = "", accentColor = c1 },
            new PlannedBleSpot { name = "BLE_Spot_M1F_04", floorLabel = "M 1F", refObjectName = "BEACON_04", fallbackPos = new Vector3(-22.03f, -2.60f, -12.79f), floorYHint = -2.60f, roleNote = "", accentColor = c1 }
        };

        if (has2F) AddUpperFloorSpots(spots, 2, "M 2F", 1.70f, c2);
        if (has3F) AddUpperFloorSpots(spots, 3, "M 3F", 5.47f, c3);
        if (has4F) AddUpperFloorSpots(spots, 4, "M 4F", 9.24f, c4);

        var rootGo = new GameObject("Recommended_BLE_Spots_Miguel");
        Undo.RegisterCreatedObjectUndo(rootGo, "Map Recommended Miguel BLE Spots");
        rootGo.transform.position = Vector3.zero;

        var reportLines = new List<string>();
        foreach (PlannedBleSpot spot in spots)
        {
            Vector3 probe = spot.fallbackPos;
            if (!string.IsNullOrEmpty(spot.refObjectName) && byName.TryGetValue(spot.refObjectName, out Transform refTransform) && refTransform != null)
            {
                probe = new Vector3(refTransform.position.x, spot.floorYHint, refTransform.position.z);
            }

            Vector3 finalPos = probe;
            if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 8.0f, NavMesh.AllAreas) &&
                Mathf.Abs(hit.position.y - spot.floorYHint) <= 2.2f)
            {
                finalPos = hit.position;
            }

            var child = new GameObject(spot.name);
            child.transform.SetParent(rootGo.transform, false);
            child.transform.position = finalPos;

            BleSpotGizmo gizmo = child.AddComponent<BleSpotGizmo>();
            gizmo.spotLabel = spot.name;
            gizmo.floorLabel = spot.floorLabel;
            gizmo.roleNote = spot.roleNote;
            gizmo.mountHeightMeters = 2.4f;
            gizmo.accentColor = spot.accentColor;

            reportLines.Add($"- {spot.name} ({spot.floorLabel}) at {finalPos:F2}");
        }

        string overlapNote = "";
        if (byName.TryGetValue("BEACON_03", out Transform b3) && byName.TryGetValue("Entrance_1", out Transform ent) &&
            b3 != null && ent != null && Vector3.Distance(b3.position, ent.position) < 5.0f)
        {
            overlapNote =
                "\n\nTip: BEACON_03 is currently right next to Entrance_1 (< 5 m apart), leaving a ~39 m gap in the middle of 1F. " +
                "Moving BEACON_03 near Room 102 and placing BEACON_05 near Room 101 closes that gap.";
        }

        EditorUtility.SetDirty(rootGo);
        EditorSceneManager.MarkAllScenesDirty();
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = rootGo;

        string summary =
            $"Mapped {spots.Count} recommended ESP32-H2 spots across Miguel Hall (mount height 2.4 m):\n\n" +
            string.Join("\n", reportLines) +
            overlapNote;
        Debug.Log("[Mig1NavMeshSetup] " + summary.Replace("\n", " | "));
        EditorUtility.DisplayDialog("Miguel BLE Spots Mapped", summary, "OK");
    }

    private static void AddUpperFloorSpots(List<PlannedBleSpot> spots, int floorNum, string floorLabel, float floorY, Color color)
    {
        Vector3[] offsets =
        {
            new Vector3(-21.80f, floorY, -10.92f),
            new Vector3(-13.52f, floorY, -2.28f),
            new Vector3(-7.50f, floorY, 5.50f),
            new Vector3(5.50f, floorY, -3.50f),
            new Vector3(12.50f, floorY, 2.50f),
            new Vector3(19.00f, floorY, -11.50f)
        };

        for (int i = 0; i < offsets.Length; i++)
        {
            spots.Add(new PlannedBleSpot
            {
                name = $"BLE_Spot_M{floorNum}F_{i + 1:00}",
                floorLabel = floorLabel,
                refObjectName = null,
                fallbackPos = offsets[i],
                floorYHint = floorY,
                roleNote = "",
                accentColor = color
            });
        }
    }

    [MenuItem("Tools/AR Campus Guide/8b. Clear Recommended BLE Spots (Miguel)")]
    public static void ClearRecommendedMiguelBleSpots()
    {
        GameObject existing = FindActiveRootByName("Recommended_BLE_Spots_Miguel");
        if (existing == null)
        {
            Debug.Log("[Mig1NavMeshSetup] No 'Recommended_BLE_Spots_Miguel' helper object found in the scene.");
            return;
        }

        Undo.DestroyObjectImmediate(existing);
        EditorSceneManager.MarkAllScenesDirty();
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[Mig1NavMeshSetup] Cleared 'Recommended_BLE_Spots_Miguel'.");
    }

    [MenuItem("Tools/AR Campus Guide/8c. Copy Miguel 2F BLE Spots -> 3F & 4F")]
    public static void CopyMiguel2FBleSpotsTo3FAnd4F()
    {
        GameObject[] sceneObjs = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include);
        var m2Spots = new List<GameObject>();
        GameObject f2 = null;
        GameObject f3 = null;
        GameObject f4 = null;
        GameObject rootParent = null;

        foreach (GameObject go in sceneObjs)
        {
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            string n = go.name.Trim();

            if (n.StartsWith("BLE_Spot_M2F_", StringComparison.OrdinalIgnoreCase))
                m2Spots.Add(go);
            else if (string.Equals(n, "Recommended_BLE_Spots_Miguel", StringComparison.OrdinalIgnoreCase))
                rootParent = go;
            else if (string.Equals(n, "MIGUEL_2nd_Floor", StringComparison.OrdinalIgnoreCase))
                f2 = go;
            else if (string.Equals(n, "MIGUEL_3rd_Floor", StringComparison.OrdinalIgnoreCase))
                f3 = go;
            else if (string.Equals(n, "MIGUEL_4th_Floor", StringComparison.OrdinalIgnoreCase))
                f4 = go;
        }

        if (m2Spots.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "No 2F BLE Spots Found",
                "Could not find any 'BLE_Spot_M2F_*' objects in the scene. Run 'Tools > AR Campus Guide > 8a. Map Recommended BLE Beacon Spots (Miguel 1F-4F)' first.",
                "OK");
            return;
        }

        m2Spots.Sort((a, b) =>
        {
            int cmpX = a.transform.position.x.CompareTo(b.transform.position.x);
            return cmpX != 0 ? cmpX : a.transform.position.z.CompareTo(b.transform.position.z);
        });

        Transform parentTransform = rootParent != null ? rootParent.transform : m2Spots[0].transform.parent;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Copy Miguel 2F BLE Spots to 3F & 4F");

        if (parentTransform == null)
        {
            var newRoot = new GameObject("Recommended_BLE_Spots_Miguel");
            Undo.RegisterCreatedObjectUndo(newRoot, "Create Recommended BLE Spots Root");
            parentTransform = newRoot.transform;
        }

        for (int i = 0; i < m2Spots.Count; i++)
        {
            GameObject spotGo = m2Spots[i];
            string cleanName = $"BLE_Spot_M2F_{i + 1:00}";
            Undo.RecordObject(spotGo, "Rename Miguel 2F BLE Spot");
            spotGo.name = cleanName;
            EditorUtility.SetDirty(spotGo);

            BleSpotGizmo gizmo2F = spotGo.GetComponent<BleSpotGizmo>();
            if (gizmo2F != null)
            {
                Undo.RecordObject(gizmo2F, "Update Miguel 2F BLE Spot Label");
                gizmo2F.spotLabel = cleanName;
                gizmo2F.floorLabel = "M 2F";
                gizmo2F.roleNote = "";
                EditorUtility.SetDirty(gizmo2F);
            }
        }

        foreach (GameObject go in sceneObjs)
        {
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            string n = go.name.Trim();
            if (n.StartsWith("BLE_Spot_M3F_", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("BLE_Spot_M4F_", StringComparison.OrdinalIgnoreCase))
            {
                Undo.DestroyObjectImmediate(go);
            }
        }

        float delta2To3Y = (f2 != null && f3 != null) ? (f3.transform.position.y - f2.transform.position.y) : 3.77f;
        float delta2To4Y = (f2 != null && f4 != null) ? (f4.transform.position.y - f2.transform.position.y) : 7.54f;

        var targets = new List<(int floorNum, float deltaY, Color color)>();
        bool include3F = f3 != null || (f3 == null && f4 == null);
        bool include4F = f4 != null || (f3 == null && f4 == null);
        if (include3F) targets.Add((3, delta2To3Y, new Color(0.74f, 0.55f, 1.00f, 0.95f)));
        if (include4F) targets.Add((4, delta2To4Y, new Color(1.00f, 0.60f, 0.28f, 0.95f)));

        int createdCount = 0;
        foreach (var target in targets)
        {
            for (int i = 0; i < m2Spots.Count; i++)
            {
                GameObject src = m2Spots[i];
                Vector3 basePos = src.transform.position + new Vector3(0f, target.deltaY, 0f);
                if (NavMesh.SamplePosition(basePos, out NavMeshHit hit, 0.6f, NavMesh.AllAreas) &&
                    Vector2.Distance(new Vector2(hit.position.x, hit.position.z), new Vector2(basePos.x, basePos.z)) <= 0.25f)
                {
                    basePos.y = hit.position.y;
                }

                string spotName = $"BLE_Spot_M{target.floorNum}F_{i + 1:00}";
                var child = new GameObject(spotName);
                Undo.RegisterCreatedObjectUndo(child, "Copy Miguel 2F BLE Spot");
                child.transform.SetParent(parentTransform, false);
                child.transform.position = basePos;

                BleSpotGizmo dstGizmo = child.AddComponent<BleSpotGizmo>();
                BleSpotGizmo srcGizmo = src.GetComponent<BleSpotGizmo>();
                if (srcGizmo != null)
                {
                    dstGizmo.mountHeightMeters = srcGizmo.mountHeightMeters;
                    dstGizmo.closeSnapRadius = srcGizmo.closeSnapRadius;
                    dstGizmo.peakSnapRadius = srcGizmo.peakSnapRadius;
                    dstGizmo.knnOverlapRadius = srcGizmo.knnOverlapRadius;
                    dstGizmo.showKnnRange = srcGizmo.showKnnRange;
                    dstGizmo.roleNote = srcGizmo.roleNote;
                }
                else
                {
                    dstGizmo.mountHeightMeters = 2.4f;
                    dstGizmo.roleNote = "";
                }

                dstGizmo.spotLabel = spotName;
                dstGizmo.floorLabel = $"M {target.floorNum}F";
                dstGizmo.accentColor = target.color;
                createdCount++;
            }
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorUtility.SetDirty(parentTransform.gameObject);
        EditorSceneManager.MarkAllScenesDirty();
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = parentTransform.gameObject;

        string summary =
            $"Renamed {m2Spots.Count} spot(s) on 2F (BLE_Spot_M2F_01..{m2Spots.Count:00}) and copied them to {targets.Count} upper floor(s) ({createdCount} total spots created).\n" +
            $"Vertical offsets used: 2F->3F = +{delta2To3Y:F2}m, 2F->4F = +{delta2To4Y:F2}m.";
        Debug.Log("[Mig1NavMeshSetup] " + summary.Replace("\n", " "));
        EditorUtility.DisplayDialog("Copied 2F BLE Spots", summary, "OK");
    }

    [MenuItem("Tools/AR Campus Guide/5a. Add Semantic Zone - Stairs (Blocked in PWD)")]
    public static void AddStairsZone() => CreateSemanticZone("Stairs");

    [MenuItem("Tools/AR Campus Guide/5b. Add Semantic Zone - Ramp (PWD Priority)")]
    public static void AddRampZone() => CreateSemanticZone("Ramp");

    [MenuItem("Tools/AR Campus Guide/5c. Add Semantic Zone - Elevator (PWD Priority)")]
    public static void AddElevatorZone() => CreateSemanticZone("Elevator");

    [MenuItem("Tools/AR Campus Guide/5d. Add Semantic Zone - Classroom")]
    public static void AddClassroomZone() => CreateSemanticZone("Classroom");

    [MenuItem("Tools/AR Campus Guide/5e. Add Multi-Floor Link - Stairs (Blocked in PWD)")]
    public static void AddStairsMultiFloorLink() => CreateMultiFloorLink("Stairs", new Vector3(0f, 3.2f, 3.5f));

    [MenuItem("Tools/AR Campus Guide/5f. Add Multi-Floor Link - Elevator / Ramp (PWD Priority)")]
    public static void AddElevatorMultiFloorLink() => CreateMultiFloorLink("Elevator", new Vector3(0f, 3.2f, 0.5f));

    private static void CreateMultiFloorLink(string areaName, Vector3 defaultEndOffset)
    {
        int areaIndex = NavMesh.GetAreaFromName(areaName);
        if (areaIndex < 0)
        {
            EditorUtility.DisplayDialog(
                "NavMesh Area Not Found",
                $"NavMesh area '{areaName}' is not defined in ProjectSettings/NavMeshAreas.asset.",
                "OK");
            return;
        }

        GameObject mig1Obj = FindPrimaryFloorObject();
        Transform parentRoot = mig1Obj != null ? mig1Obj.transform : null;
        if (parentRoot != null)
        {
            Transform zonesRoot = parentRoot.Find("SemanticZones");
            if (zonesRoot == null)
            {
                var rootGo = new GameObject("SemanticZones");
                Undo.RegisterCreatedObjectUndo(rootGo, "Create SemanticZones Root");
                rootGo.transform.SetParent(parentRoot, false);
                zonesRoot = rootGo.transform;
            }
            parentRoot = zonesRoot;
        }

        var linkGo = new GameObject($"FloorLink_{areaName}");
        Undo.RegisterCreatedObjectUndo(linkGo, "Create Multi-Floor NavMeshLink");
        if (parentRoot != null)
            linkGo.transform.SetParent(parentRoot, true);

        Vector3 spawnPos = SceneView.lastActiveSceneView != null
            ? new Vector3(SceneView.lastActiveSceneView.pivot.x, -2.68f, SceneView.lastActiveSceneView.pivot.z)
            : new Vector3(-25.0f, -2.68f, -11.5f);
        linkGo.transform.position = spawnPos;
        linkGo.transform.rotation = Quaternion.identity;

        NavMeshLink link = linkGo.AddComponent<NavMeshLink>();
        link.startPoint = Vector3.zero;
        link.endPoint = defaultEndOffset;
        link.width = 1.4f;
        link.bidirectional = true;
        link.area = areaIndex;
        link.autoUpdate = true;

        EditorUtility.SetDirty(linkGo);
        Selection.activeGameObject = linkGo;
        Debug.Log($"[Mig1NavMeshSetup] Created multi-floor NavMeshLink '{linkGo.name}' (area='{areaName}'). Drag its Start and End handles in the Scene View to connect Floor 1F and Floor 2F.");
    }

    private static void CreateSemanticZone(string areaName)
    {
        int areaIndex = NavMesh.GetAreaFromName(areaName);
        if (areaIndex < 0)
        {
            EditorUtility.DisplayDialog(
                "NavMesh Area Not Found",
                $"NavMesh area '{areaName}' is not defined. Open Window > AI > Navigation > Areas to check ProjectSettings/NavMeshAreas.asset.",
                "OK");
            return;
        }

        GameObject mig1Obj = FindPrimaryFloorObject();
        if (mig1Obj == null)
        {
            EditorUtility.DisplayDialog(
                "Primary Floor Not Found",
                "Could not find 'MIGUEL_1st_Floor' or 'mig1' in the active scene. Run Tools > AR Campus Guide > 1. Build NavMesh for All Miguel Floors first.",
                "OK");
            return;
        }

        Transform zonesRoot = mig1Obj.transform.Find("SemanticZones");
        if (zonesRoot == null)
        {
            var rootGo = new GameObject("SemanticZones");
            Undo.RegisterCreatedObjectUndo(rootGo, "Create SemanticZones Root");
            rootGo.transform.SetParent(mig1Obj.transform, false);
            rootGo.transform.localPosition = Vector3.zero;
            rootGo.transform.localRotation = Quaternion.identity;
            rootGo.transform.localScale = Vector3.one;
            zonesRoot = rootGo.transform;
        }

        string uniqueName = GameObjectUtility.GetUniqueNameForSibling(zonesRoot, "Zone_" + areaName);
        var zoneGo = new GameObject(uniqueName);
        Undo.RegisterCreatedObjectUndo(zoneGo, "Create Semantic Zone " + areaName);
        zoneGo.transform.SetParent(zonesRoot, true);

        Vector3 spawnPos = SceneView.lastActiveSceneView != null
            ? new Vector3(SceneView.lastActiveSceneView.pivot.x, -2.75f, SceneView.lastActiveSceneView.pivot.z)
            : new Vector3(0f, -2.75f, 0f);
        zoneGo.transform.position = spawnPos;
        zoneGo.transform.rotation = Quaternion.identity;
        zoneGo.transform.localScale = Vector3.one;

        NavMeshModifierVolume vol = zoneGo.AddComponent<NavMeshModifierVolume>();
        vol.size = new Vector3(3f, 3f, 3f);
        vol.center = new Vector3(0f, 1.5f, 0f);
        vol.area = areaIndex;

        EditorUtility.SetDirty(zoneGo);
        EditorSceneManager.MarkSceneDirty(mig1Obj.scene);
        Selection.activeGameObject = zoneGo;

        Debug.Log($"[Mig1NavMeshSetup] Created '{uniqueName}' (area='{areaName}', index={areaIndex}). Position/resize its volume, then run Tools > AR Campus Guide > 1. Build NavMesh from mig1 to bake it.");
    }

    [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected)]
    private static void DrawSemanticZoneGizmo(NavMeshModifierVolume volume, GizmoType gizmoType)
    {
        if (volume == null || volume.area < 3) return;

        Color gizmoColor;
        string label;
        if (volume.area == NavMesh.GetAreaFromName("Stairs"))
        {
            gizmoColor = new Color(1f, 0.38f, 0.15f, 1f);
            label = "Stairs (Blocked in PWD)";
        }
        else if (volume.area == NavMesh.GetAreaFromName("Ramp"))
        {
            gizmoColor = new Color(0.18f, 0.90f, 0.45f, 1f);
            label = "Ramp (PWD Priority)";
        }
        else if (volume.area == NavMesh.GetAreaFromName("Elevator"))
        {
            gizmoColor = new Color(0.20f, 0.75f, 1f, 1f);
            label = "Elevator (PWD Priority)";
        }
        else if (volume.area == NavMesh.GetAreaFromName("Classroom"))
        {
            gizmoColor = new Color(1f, 0.84f, 0.22f, 1f);
            label = "Classroom";
        }
        else
        {
            gizmoColor = new Color(0.85f, 0.25f, 0.95f, 1f);
            label = $"Area {volume.area}";
        }

        Matrix4x4 prevMatrix = Gizmos.matrix;
        Color prevColor = Gizmos.color;

        Gizmos.matrix = volume.transform.localToWorldMatrix;
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.24f);
        Gizmos.DrawCube(volume.center, volume.size);
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.92f);
        Gizmos.DrawWireCube(volume.center, volume.size);

        Gizmos.matrix = prevMatrix;
        Gizmos.color = prevColor;

        Vector3 labelPos = volume.transform.TransformPoint(volume.center + Vector3.up * (volume.size.y * 0.5f + 0.2f));
        Handles.Label(labelPos, label);
    }

    private static void SetRendererEnabled(MeshRenderer r, bool enabled, string undoName)
    {
        if (r == null) return;
        Undo.RecordObject(r, undoName);
        r.enabled = enabled;
        PrefabUtility.RecordPrefabInstancePropertyModifications(r);
        EditorUtility.SetDirty(r);
    }

    private const string OcclusionShaderPath = "Assets/Shaders/ARWallOcclusion.shader";
    private const string OcclusionMaterialPath = "Assets/Shaders/ARWallOcclusion.mat";

    private static Material GetOrCreateWallOcclusionMaterial()
    {
        EnsureAssetFolder("Assets/Shaders");
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(OcclusionMaterialPath);
        Shader shader = Shader.Find("ARCampusGuide/ARWallOcclusion");
        if (shader == null)
            shader = AssetDatabase.LoadAssetAtPath<Shader>(OcclusionShaderPath);

        if (mat == null && shader != null)
        {
            mat = new Material(shader) { name = "ARWallOcclusion" };
            AssetDatabase.CreateAsset(mat, OcclusionMaterialPath);
            AssetDatabase.SaveAssets();
        }
        else if (mat != null && shader != null && mat.shader != shader)
        {
            mat.shader = shader;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
        }
        return mat;
    }

    private static void RestoreEditorMaterialsOnBuilding(GameObject buildingRoot)
    {
        if (buildingRoot == null) return;
        foreach (MeshRenderer r in buildingRoot.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null) continue;
            Undo.RecordObject(r, "Show mig1 Editor Material");
            MeshRenderer srcRenderer = PrefabUtility.GetCorrespondingObjectFromSource(r);
            if (srcRenderer != null && srcRenderer.sharedMaterials != null && srcRenderer.sharedMaterials.Length > 0)
            {
                r.sharedMaterials = srcRenderer.sharedMaterials;
            }
            r.enabled = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(r);
            EditorUtility.SetDirty(r);
        }
    }

    private static void ApplyArOcclusionToBuildingModel(GameObject buildingRoot, out int occludedWalls, out int hiddenFloors)
    {
        occludedWalls = 0;
        hiddenFloors = 0;
        if (buildingRoot == null) return;

        Material occMat = GetOrCreateWallOcclusionMaterial();
        foreach (MeshRenderer r in buildingRoot.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null) continue;
            bool isWall = r.gameObject.name.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0;

            Undo.RecordObject(r, isWall ? "Apply AR Wall Occlusion" : "Hide Floor Renderer");
            if (isWall && occMat != null)
            {
                int slotCount = Mathf.Max(1, r.sharedMaterials != null ? r.sharedMaterials.Length : 1);
                var mats = new Material[slotCount];
                for (int i = 0; i < slotCount; i++) mats[i] = occMat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.enabled = true;
                occludedWalls++;
            }
            else
            {
                r.enabled = false;
                hiddenFloors++;
            }

            PrefabUtility.RecordPrefabInstancePropertyModifications(r);
            EditorUtility.SetDirty(r);
        }
    }

    private static int EnsureCampusModulesAndMetadata()
    {
        PathVisualizer pv = UnityEngine.Object.FindAnyObjectByType<PathVisualizer>();
        if (pv != null)
        {
            GameObject host = pv.gameObject;
            if (host.GetComponent<CampusBuildingManager>() == null)
                Undo.AddComponent<CampusBuildingManager>(host);
            if (host.GetComponent<KnnPositionProvider>() == null)
                Undo.AddComponent<KnnPositionProvider>(host);
            if (host.GetComponent<NavigationTelemetryLogger>() == null)
                Undo.AddComponent<NavigationTelemetryLogger>(host);
            EditorUtility.SetDirty(host);
        }

        UIManager ui = UnityEngine.Object.FindAnyObjectByType<UIManager>();
        if (ui != null && ui.map2DPanel != null && ui.map2DPanel.GetComponent<Map2DController>() == null)
        {
            Undo.AddComponent<Map2DController>(ui.map2DPanel);
            EditorUtility.SetDirty(ui.map2DPanel);
        }

        int tagged = 0;
        DestinationManager dm = UnityEngine.Object.FindAnyObjectByType<DestinationManager>();
        if (dm != null && dm.targets != null)
        {
            foreach (Transform t in dm.targets)
            {
                if (t == null) continue;
                CampusPoiMetadata meta = t.GetComponent<CampusPoiMetadata>();
                if (meta == null)
                    meta = Undo.AddComponent<CampusPoiMetadata>(t.gameObject);

                CampusPoiInfo inferred = CampusPoiMetadata.InferFromTransform(t);
                meta.building = inferred.building;
                meta.floorNumber = inferred.floorNumber;
                if (string.IsNullOrWhiteSpace(meta.roomCode))
                    meta.roomCode = inferred.roomCode;
                if (string.IsNullOrWhiteSpace(meta.displayName))
                    meta.displayName = inferred.displayName;
                meta.category = inferred.category;
                meta.isPwdAccessible = inferred.isPwdAccessible;
                EditorUtility.SetDirty(meta);
                tagged++;
            }
        }
        return tagged;
    }

    private static void BuildFromMig1(bool hideRenderersAfterBake)
    {
        List<GameObject> floors = FindAllBuildingFloorObjects();
        if (floors.Count == 0)
        {
            string primaryAssetPath = ResolveMig1AssetPath();
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(primaryAssetPath);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog(
                    "Floor Model Not Found",
                    $"Could not find any Miguel, SJ, or GOKS floor objects in the scene or at {primaryAssetPath}.",
                    "OK");
                return;
            }
            GameObject floor1 = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            floor1.name = string.Equals(primaryAssetPath, Mig1PrimaryAssetPath, StringComparison.OrdinalIgnoreCase)
                ? "MIGUEL_1st_Floor"
                : "mig1";
            floor1.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Undo.RegisterCreatedObjectUndo(floor1, "Instantiate 1st Floor");
            floors.Add(floor1);
        }

        var surfaces = new List<NavMeshSurface>(floors.Count);
        var surfaceObjects = new List<UnityEngine.Object>(floors.Count);
        var floorNames = new List<string>(floors.Count);

        foreach (GameObject floorObj in floors)
        {
            RestoreEditorMaterialsOnBuilding(floorObj);

            NavMeshSurface surface = floorObj.GetComponent<NavMeshSurface>();
            if (surface == null)
            {
                surface = Undo.AddComponent<NavMeshSurface>(floorObj);
            }

            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
            surface.minRegionArea = 2f;
            surface.enabled = true;
            EditorUtility.SetDirty(surface);

            if (NavMeshAssetManager.instance.IsSurfaceBaking(surface))
            {
                Debug.LogWarning($"[Mig1NavMeshSetup] NavMesh bake already in progress for {floorObj.name}.");
                return;
            }

            surfaces.Add(surface);
            surfaceObjects.Add(surface);
            floorNames.Add(floorObj.name);
        }

        NavMeshAssetManager.instance.StartBakingSurfaces(surfaceObjects.ToArray());

        EditorApplication.CallbackFunction onUpdate = null;
        onUpdate = () =>
        {
            foreach (NavMeshSurface s in surfaces)
            {
                if (s != null && NavMeshAssetManager.instance.IsSurfaceBaking(s))
                    return;
            }

            EditorApplication.update -= onUpdate;

            GameObject firstFloor = null;
            foreach (GameObject floorObj in floors)
            {
                if (floorObj == null) continue;
                if (firstFloor == null) firstFloor = floorObj;

                if (hideRenderersAfterBake)
                {
                    ApplyArOcclusionToBuildingModel(floorObj, out _, out _);
                }

                EditorUtility.SetDirty(floorObj);
            }

            if (firstFloor == null) return;

            EditorSceneManager.MarkSceneDirty(firstFloor.scene);
            EditorSceneManager.SaveOpenScenes();
            Selection.activeGameObject = firstFloor;

            Debug.Log($"[Mig1NavMeshSetup] Baked and saved NavMesh assets for: {string.Join(", ", floorNames)} ({(hideRenderersAfterBake ? "AR Wall Occlusion enabled" : "editor materials visible")}).");
        };
        EditorApplication.update += onUpdate;
    }

    [Serializable]
    public struct HallwayTrimState
    {
        public float appliedTrimMeters;
        public float xWest;
        public float xEast;
    }

    public static HallwayTrimState LoadTrimState()
    {
        var fallback = new HallwayTrimState
        {
            appliedTrimMeters = 0f,
            xWest = DefaultCutWestX,
            xEast = DefaultCutEastX
        };

        string path = Path.Combine(Directory.GetCurrentDirectory(), TrimStatePath);
        if (!File.Exists(path))
            return fallback;

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return fallback;

            HallwayTrimState loaded = JsonUtility.FromJson<HallwayTrimState>(json);
            if (loaded.xEast <= loaded.xWest + 2.0f)
                return fallback;

            float maxTrim = Mathf.Max(0f, (loaded.xEast - loaded.xWest) - 1.0f);
            loaded.appliedTrimMeters = Mathf.Clamp(loaded.appliedTrimMeters, 0f, maxTrim);
            return loaded;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Mig1NavMeshSetup] Failed to read trim state at '{TrimStatePath}', using defaults: {ex.Message}");
            return fallback;
        }
    }

    public static void SaveTrimState(HallwayTrimState state)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), TrimStatePath);
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonUtility.ToJson(state, true), Encoding.UTF8);
    }

    public static float ForwardMapX(float x, float xWest, float xEast, float trimMeters)
    {
        if (trimMeters <= 1e-4f || xEast <= xWest + 1e-4f || x >= xEast)
            return x;
        if (x <= xWest)
            return x + trimMeters;

        float scaleX = (xEast - xWest - trimMeters) / (xEast - xWest);
        return xEast + (x - xEast) * scaleX;
    }

    public static float InverseMapX(float xMapped, float xWest, float xEast, float trimMeters)
    {
        if (trimMeters <= 1e-4f || xEast <= xWest + 1e-4f || xMapped >= xEast)
            return xMapped;
        if (xMapped <= xWest + trimMeters)
            return xMapped - trimMeters;

        float denom = xEast - xWest - trimMeters;
        if (denom <= 1e-4f)
            return xMapped;

        return xEast + (xMapped - xEast) * ((xEast - xWest) / denom);
    }

    public static bool ResetHallwayTrim(bool shiftMarkers, bool hideRenderersAfterBake)
    {
        return ApplyHallwaySegmentTrim(0f, DefaultCutWestX, DefaultCutEastX, shiftMarkers, hideRenderersAfterBake);
    }

    public static bool ApplyHallwaySegmentTrim(float trimMeters, float xWest, float xEast, bool shiftMarkers, bool hideRenderersAfterBake)
    {
        GameObject mig1Obj = FindPrimaryFloorObject();
        if (mig1Obj == null)
        {
            EditorUtility.DisplayDialog(
                "Primary Floor Not Found",
                "Could not find 'MIGUEL_1st_Floor' or 'mig1' in the active scene. Run Tools > AR Campus Guide > 1. Build NavMesh for All Miguel Floors first.",
                "OK");
            return false;
        }

        if (xWest > xEast)
        {
            float tmp = xWest;
            xWest = xEast;
            xEast = tmp;
        }
        if (xEast - xWest < 2.0f)
        {
            xWest = xEast - 2.0f;
        }

        float maxTrim = Mathf.Max(0f, (xEast - xWest) - 1.0f);
        trimMeters = Mathf.Clamp(trimMeters, 0f, maxTrim);

        List<Transform> markers = CollectSceneMarkers(mig1Obj);
        List<Transform> semanticZones = CollectSemanticZones(mig1Obj);

        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(trimMeters <= 1e-4f ? "Reset mig1 Hallway Trim" : "Shorten mig1 Hallway Segment");

        // Step A: Auto-restore any prior whole-building localScale on mig1 (e.g., 0.92x -> 1.0x).
        Vector3 oldScale = mig1Obj.transform.localScale;
        if (Mathf.Abs(oldScale.x - 1f) > 1e-4f ||
            Mathf.Abs(oldScale.y - 1f) > 1e-4f ||
            Mathf.Abs(oldScale.z - 1f) > 1e-4f)
        {
            float rx = Mathf.Abs(oldScale.x) < 1e-4f ? 1f : 1f / oldScale.x;
            float ry = Mathf.Abs(oldScale.y) < 1e-4f ? 1f : 1f / oldScale.y;
            float rz = Mathf.Abs(oldScale.z) < 1e-4f ? 1f : 1f / oldScale.z;

            Undo.RecordObject(mig1Obj.transform, "Restore mig1 Scale");
            mig1Obj.transform.localScale = Vector3.one;
            PrefabUtility.RecordPrefabInstancePropertyModifications(mig1Obj.transform);
            EditorUtility.SetDirty(mig1Obj.transform);

            if (shiftMarkers)
            {
                Vector3 pivot = mig1Obj.transform.position;
                foreach (Transform t in markers)
                {
                    Vector3 rel = t.position - pivot;
                    Undo.RecordObject(t, "Restore Marker From mig1 Scale");
                    t.position = pivot + new Vector3(rel.x * rx, rel.y * ry, rel.z * rz);
                    EditorUtility.SetDirty(t);
                }
            }
        }

        // Step B: Ensure source 1st-floor FBX is readable and load pristine meshes.
        string primaryAssetPath = ResolveMig1AssetPath();
        EnsureMig1Readable();
        GameObject fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(primaryAssetPath);
        if (fbxPrefab == null)
        {
            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.DisplayDialog(
                "1st Floor Source Missing",
                $"Could not load source FBX asset at '{primaryAssetPath}'.",
                "OK");
            return false;
        }

        // Step C: Build/update trimmed mesh assets (or restore originals when trimMeters == 0).
        bool restoreOriginal = trimMeters <= 1e-4f;
        if (!restoreOriginal)
            EnsureAssetFolder(CalibratedMeshDir);

        int updatedMeshes = 0;
        MeshFilter[] filters = mig1Obj.GetComponentsInChildren<MeshFilter>(true);
        foreach (MeshFilter mf in filters)
        {
            Mesh origMesh = ResolveOriginalFbxMesh(mf, fbxPrefab);
            if (origMesh == null)
                continue;

            Mesh targetMesh = restoreOriginal
                ? origMesh
                : BuildOrUpdateTrimmedMeshAsset(mig1Obj.transform, mf, origMesh, xWest, xEast, trimMeters);

            if (targetMesh == null)
                continue;

            Undo.RecordObject(mf, restoreOriginal ? "Restore Original mig1 Mesh" : "Assign Trimmed Hallway Mesh");
            mf.sharedMesh = targetMesh;
            PrefabUtility.RecordPrefabInstancePropertyModifications(mf);
            EditorUtility.SetDirty(mf);

            MeshCollider mc = mf.GetComponent<MeshCollider>();
            if (mc != null)
            {
                Undo.RecordObject(mc, "Update mig1 MeshCollider");
                if (mc.sharedMesh == targetMesh)
                    mc.sharedMesh = null;
                mc.sharedMesh = targetMesh;
                PrefabUtility.RecordPrefabInstancePropertyModifications(mc);
                EditorUtility.SetDirty(mc);
            }

            updatedMeshes++;
        }

        AssetDatabase.SaveAssets();

        // Step D: Idempotently invert previous trim and apply new trim to markers & semantic zones.
        HallwayTrimState prev = LoadTrimState();
        int shiftedMarkers = 0;
        int shiftedZones = 0;
        if (shiftMarkers)
        {
            shiftedMarkers = ShiftTransformsForTrim(mig1Obj.transform, markers, prev, xWest, xEast, trimMeters);
            shiftedZones = ShiftTransformsForTrim(mig1Obj.transform, semanticZones, prev, xWest, xEast, trimMeters);
        }

        SaveTrimState(new HallwayTrimState
        {
            appliedTrimMeters = restoreOriginal ? 0f : trimMeters,
            xWest = xWest,
            xEast = xEast
        });

        // Step E: Collapse undo, re-bake NavMesh, and log resulting spans.
        Undo.CollapseUndoOperations(undoGroup);
        BuildFromMig1(hideRenderersAfterBake);

        string spanSummary = BuildMarkerSpanLog();
        string actionDesc = restoreOriginal
            ? "Reset mig1 to original un-trimmed FBX geometry"
            : $"Shortened mig1 X-band [{xWest:F2}, {xEast:F2}] by {trimMeters:F2} m";
        Debug.Log($"[Mig1NavMeshSetup] {actionDesc} (updated {updatedMeshes} mesh(es), shifted {shiftedMarkers} marker(s), {shiftedZones} semantic zone(s)).\n{spanSummary}");
        return true;
    }

    private static void EnsureMig1Readable()
    {
        ModelImporter importer = AssetImporter.GetAtPath(ResolveMig1AssetPath()) as ModelImporter;
        if (importer != null && !importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
    }

    private static Mesh ResolveOriginalFbxMesh(MeshFilter mf, GameObject fbxPrefab)
    {
        MeshFilter srcMf = PrefabUtility.GetCorrespondingObjectFromSource(mf);
        if (srcMf != null && srcMf.sharedMesh != null)
            return srcMf.sharedMesh;

        if (fbxPrefab != null)
        {
            foreach (MeshFilter candidate in fbxPrefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (string.Equals(candidate.gameObject.name, mf.gameObject.name, StringComparison.Ordinal) &&
                    candidate.sharedMesh != null)
                {
                    return candidate.sharedMesh;
                }
            }
        }

        return null;
    }

    private static Mesh BuildOrUpdateTrimmedMeshAsset(
        Transform mig1Root,
        MeshFilter mf,
        Mesh origMesh,
        float xWest,
        float xEast,
        float trimMeters)
    {
        Mesh working = UnityEngine.Object.Instantiate(origMesh);
        string baseName = SanitizeAssetName(mf.gameObject.name);
        working.name = baseName + "_Trimmed";

        Matrix4x4 meshToRoot = mig1Root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
        Matrix4x4 rootToMesh = meshToRoot.inverse;
        float scaleX = (xEast - xWest - trimMeters) / (xEast - xWest);

        Vector3[] verts = working.vertices;
        Vector3[] norms = working.normals;
        bool hasNormals = norms != null && norms.Length == verts.Length;

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 pRoot = meshToRoot.MultiplyPoint3x4(verts[i]);
            float origX = pRoot.x;
            pRoot.x = ForwardMapX(origX, xWest, xEast, trimMeters);
            verts[i] = rootToMesh.MultiplyPoint3x4(pRoot);

            if (hasNormals && origX > xWest && origX < xEast && scaleX > 1e-4f)
            {
                Vector3 nRoot = rootToMesh.transpose.MultiplyVector(norms[i]);
                nRoot.x /= scaleX;
                norms[i] = meshToRoot.transpose.MultiplyVector(nRoot.normalized).normalized;
            }
        }

        working.vertices = verts;
        if (hasNormals)
            working.normals = norms;
        working.RecalculateBounds();

        string assetPath = CalibratedMeshDir + "/" + working.name + ".asset";
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing != null)
        {
            EditorUtility.CopySerialized(working, existing);
            EditorUtility.SetDirty(existing);
            UnityEngine.Object.DestroyImmediate(working);
            return existing;
        }

        AssetDatabase.CreateAsset(working, assetPath);
        return working;
    }

    private static void EnsureAssetFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath))
            return;

        string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
        string folderName = Path.GetFileName(folderPath);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureAssetFolder(parent);

        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(folderName))
            AssetDatabase.CreateFolder(parent, folderName);
    }

    private static string SanitizeAssetName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return "Mesh";

        var sb = new StringBuilder(rawName.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in rawName.Trim())
        {
            bool isInvalid = false;
            for (int i = 0; i < invalid.Length; i++)
            {
                if (c == invalid[i])
                {
                    isInvalid = true;
                    break;
                }
            }
            sb.Append(isInvalid ? '_' : c);
        }

        string cleaned = sb.ToString();
        return string.IsNullOrEmpty(cleaned) ? "Mesh" : cleaned;
    }

    private static int ShiftTransformsForTrim(
        Transform mig1Root,
        List<Transform> targets,
        HallwayTrimState prev,
        float xWest,
        float xEast,
        float trimMeters)
    {
        int shifted = 0;
        foreach (Transform t in targets)
        {
            if (t == null) continue;
            Vector3 pRoot = mig1Root.InverseTransformPoint(t.position);
            float baseX = InverseMapX(pRoot.x, prev.xWest, prev.xEast, prev.appliedTrimMeters);
            float newX = ForwardMapX(baseX, xWest, xEast, trimMeters);
            if (Mathf.Abs(newX - pRoot.x) > 1e-5f)
            {
                Undo.RecordObject(t, "Shift Transform For Hallway Trim");
                pRoot.x = newX;
                t.position = mig1Root.TransformPoint(pRoot);
                EditorUtility.SetDirty(t);
                shifted++;
            }
        }
        return shifted;
    }

    private static List<Transform> CollectSceneMarkers(GameObject mig1Obj)
    {
        var markerSet = new HashSet<Transform>();
        foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include))
        {
            if (string.IsNullOrEmpty(go.scene.name)) continue;
            string n = go.name.Trim();
            if (n.StartsWith("Destination_", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("Entrance", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("BEACON_", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("ESP32_", StringComparison.OrdinalIgnoreCase))
            {
                markerSet.Add(go.transform);
            }

            if (string.Equals(n, "Destinations", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < go.transform.childCount; i++)
                    markerSet.Add(go.transform.GetChild(i));
            }
        }

        foreach (DestinationManager destMgr in UnityEngine.Object.FindObjectsByType<DestinationManager>(FindObjectsInactive.Include))
        {
            if (destMgr == null || string.IsNullOrEmpty(destMgr.gameObject.scene.name) || destMgr.targets == null) continue;
            foreach (Transform t in destMgr.targets)
            {
                if (t != null) markerSet.Add(t);
            }
        }

        foreach (MockPositionProvider mock in UnityEngine.Object.FindObjectsByType<MockPositionProvider>(FindObjectsInactive.Include))
        {
            if (mock == null || string.IsNullOrEmpty(mock.gameObject.scene.name)) continue;
            var so = new SerializedObject(mock);
            SerializedProperty beaconsProp = so.FindProperty("beacons");
            if (beaconsProp == null) continue;
            for (int i = 0; i < beaconsProp.arraySize; i++)
            {
                Transform anchor = beaconsProp.GetArrayElementAtIndex(i).FindPropertyRelative("anchor")?.objectReferenceValue as Transform;
                if (anchor != null) markerSet.Add(anchor);
            }
        }

        foreach (BlePositionProvider ble in UnityEngine.Object.FindObjectsByType<BlePositionProvider>(FindObjectsInactive.Include))
        {
            if (ble == null || string.IsNullOrEmpty(ble.gameObject.scene.name)) continue;
            var so = new SerializedObject(ble);
            SerializedProperty beaconsProp = so.FindProperty("beacons");
            if (beaconsProp == null) continue;
            for (int i = 0; i < beaconsProp.arraySize; i++)
            {
                Transform anchor = beaconsProp.GetArrayElementAtIndex(i).FindPropertyRelative("anchor")?.objectReferenceValue as Transform;
                if (anchor != null) markerSet.Add(anchor);
            }
        }

        var result = new List<Transform>();
        foreach (Transform t in markerSet)
        {
            if (t == null || string.IsNullOrEmpty(t.gameObject.scene.name)) continue;
            if (t == mig1Obj.transform || t.IsChildOf(mig1Obj.transform)) continue;

            bool hasAncestorInSet = false;
            Transform p = t.parent;
            while (p != null)
            {
                if (markerSet.Contains(p))
                {
                    hasAncestorInSet = true;
                    break;
                }
                p = p.parent;
            }
            if (!hasAncestorInSet)
                result.Add(t);
        }

        return result;
    }

    private static List<Transform> CollectSemanticZones(GameObject mig1Obj)
    {
        var zones = new List<Transform>();
        Transform zonesRoot = mig1Obj.transform.Find("SemanticZones");
        if (zonesRoot == null)
            return zones;

        for (int i = 0; i < zonesRoot.childCount; i++)
        {
            Transform child = zonesRoot.GetChild(i);
            if (child != null)
                zones.Add(child);
        }
        return zones;
    }

    public static string GetHallwayCalibrationSummary(out HallwayTrimState trimState)
    {
        trimState = LoadTrimState();

        GameObject mig1Obj = FindPrimaryFloorObject();
        if (mig1Obj == null)
            return "MIGUEL_1st_Floor / mig1 not found in active scene.";

        Vector3 s = mig1Obj.transform.localScale;
        string trimLine = trimState.appliedTrimMeters > 1e-4f
            ? $"Applied Hallway Trim: {trimState.appliedTrimMeters:F2} m on X [{trimState.xWest:F1}, {trimState.xEast:F1}]"
            : "Applied Hallway Trim: None (Original mig1.fbx)";

        var lines = new List<string>
        {
            $"mig1 Scale: ({s.x:F3}, {s.y:F3}, {s.z:F3})",
            trimLine
        };

        AppendMarkerSpanLines(lines);
        return string.Join("\n", lines);
    }

    private static string BuildMarkerSpanLog()
    {
        var lines = new List<string>();
        AppendMarkerSpanLines(lines);
        return lines.Count > 0 ? "Resulting X-spans:\n" + string.Join("\n", lines) : string.Empty;
    }

    private static void AppendMarkerSpanLines(List<string> lines)
    {
        bool hasEntrance = TryGetSceneObjectX("Entrance_1", out float ex);
        bool has101 = TryGetSceneObjectX("Destination_Room 101", out float x101);
        bool has102 = TryGetSceneObjectX("Destination_Room 102", out float x102);
        bool hasMenCr = TryGetSceneObjectX("Destination_Men CR", out float xMenCr);

        if (hasEntrance && has101)
            lines.Add($"Entrance_1 \u2192 Room 101: {Mathf.Abs(x101 - ex):F2} m");
        if (has101 && has102)
            lines.Add($"Room 101 \u2192 Room 102: {Mathf.Abs(x102 - x101):F2} m");
        if (has102 && hasMenCr)
            lines.Add($"Room 102 \u2192 Men CR: {Mathf.Abs(xMenCr - x102):F2} m");
        if (hasEntrance && hasMenCr)
            lines.Add($"Entrance_1 \u2192 Men CR (total X): {Mathf.Abs(xMenCr - ex):F2} m");
    }

    private static bool TryGetSceneObjectX(string exactName, out float worldX)
    {
        List<GameObject> matches = FindSceneObjectsByName(exactName);
        if (matches.Count > 0)
        {
            worldX = matches[0].transform.position.x;
            return true;
        }
        worldX = 0f;
        return false;
    }
}

public class Mig1ScaleCalibratorWindow : EditorWindow
{
    private UITK.Label summaryLabel;
    private UITK.Slider trimSlider;
    private UITK.FloatField trimField;
    private UITK.FloatField cutWestField;
    private UITK.FloatField cutEastField;
    private UITK.Toggle shiftMarkersToggle;
    private UITK.Toggle keepVisibleToggle;
    private UITK.Toggle showGuidesToggle;
    private bool syncing;

    public static void ShowWindow()
    {
        var win = GetWindow<Mig1ScaleCalibratorWindow>("mig1 Hallway Trimmer");
        win.minSize = new Vector2(380f, 470f);
        win.Show();
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        SceneView.RepaintAll();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SceneView.RepaintAll();
    }

    public void CreateGUI()
    {
        var root = rootVisualElement;
        root.Clear();
        root.style.paddingLeft = 12;
        root.style.paddingRight = 12;
        root.style.paddingTop = 12;
        root.style.paddingBottom = 12;

        var title = new UITK.Label("mig1 Hallway Segment Trimmer");
        title.style.fontSize = 15;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.marginBottom = 8;
        root.Add(title);

        var summaryBox = new UITK.VisualElement();
        summaryBox.style.backgroundColor = new Color(0f, 0f, 0f, 0.2f);
        summaryBox.style.borderTopLeftRadius = 4;
        summaryBox.style.borderTopRightRadius = 4;
        summaryBox.style.borderBottomLeftRadius = 4;
        summaryBox.style.borderBottomRightRadius = 4;
        summaryBox.style.paddingLeft = 8;
        summaryBox.style.paddingRight = 8;
        summaryBox.style.paddingTop = 6;
        summaryBox.style.paddingBottom = 6;
        summaryBox.style.marginBottom = 10;

        summaryLabel = new UITK.Label();
        summaryLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
        summaryBox.Add(summaryLabel);
        root.Add(summaryBox);

        Mig1NavMeshSetup.HallwayTrimState state = Mig1NavMeshSetup.LoadTrimState();
        float initialTrim = state.appliedTrimMeters > 1e-4f
            ? state.appliedTrimMeters
            : Mig1NavMeshSetup.DefaultTrimMeters;
        float initialWest = state.xWest;
        float initialEast = state.xEast;

        var segmentHeader = new UITK.Label("Segment to Shorten");
        segmentHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
        segmentHeader.style.marginBottom = 4;
        root.Add(segmentHeader);

        var segmentRow = new UITK.VisualElement();
        segmentRow.style.flexDirection = UITK.FlexDirection.Row;
        segmentRow.style.marginBottom = 8;
        segmentRow.Add(CreateSegmentPresetButton("Room 102 \u2192 Men CR (-25.0 to -12.5)", -25.0f, -12.5f));
        segmentRow.Add(CreateSegmentPresetButton("Room 101 \u2192 Men CR (-25.0 to +1.0)", -25.0f, 1.0f));
        root.Add(segmentRow);

        float maxTrim = ComputeMaxTrim(initialWest, initialEast);
        float sliderHigh = Mathf.Max(1f, Mathf.Min(10f, maxTrim));
        initialTrim = Mathf.Clamp(initialTrim, 0f, maxTrim);

        trimSlider = new UITK.Slider("Shorten By (m)", 0f, sliderHigh)
        {
            value = Mathf.Clamp(initialTrim, 0f, sliderHigh),
            showInputField = false
        };
        trimField = new UITK.FloatField
        {
            label = "Exact Trim (m)",
            value = (float)Math.Round(initialTrim, 2)
        };

        UITK.INotifyValueChangedExtensions.RegisterValueChangedCallback(trimSlider, evt =>
        {
            if (syncing) return;
            syncing = true;
            float clamped = ClampTrimToBounds(evt.newValue);
            trimField.SetValueWithoutNotify((float)Math.Round(clamped, 2));
            syncing = false;
            SceneView.RepaintAll();
        });

        UITK.INotifyValueChangedExtensions.RegisterValueChangedCallback(trimField, evt =>
        {
            if (syncing) return;
            syncing = true;
            float clamped = ClampTrimToBounds(evt.newValue);
            trimField.SetValueWithoutNotify((float)Math.Round(clamped, 2));
            trimSlider.SetValueWithoutNotify(Mathf.Clamp(clamped, trimSlider.lowValue, trimSlider.highValue));
            syncing = false;
            SceneView.RepaintAll();
        });

        root.Add(trimSlider);
        root.Add(trimField);

        var presetRow = new UITK.VisualElement();
        presetRow.style.flexDirection = UITK.FlexDirection.Row;
        presetRow.style.marginTop = 6;
        presetRow.style.marginBottom = 10;
        presetRow.Add(CreateTrimPresetButton("2.0 m", 2.0f));
        presetRow.Add(CreateTrimPresetButton("3.5 m (Rec.)", 3.5f));
        presetRow.Add(CreateTrimPresetButton("5.0 m", 5.0f));
        presetRow.Add(CreateTrimPresetButton("0 m (Reset)", 0f));
        root.Add(presetRow);

        var boundsFoldout = new UITK.Foldout
        {
            text = "Cut Region Bounds (World X)",
            value = false
        };
        boundsFoldout.style.marginBottom = 8;

        cutWestField = new UITK.FloatField { label = "Cut West X", value = initialWest };
        cutEastField = new UITK.FloatField { label = "Cut East X", value = initialEast };

        UITK.INotifyValueChangedExtensions.RegisterValueChangedCallback(cutWestField, _ => OnBoundsChanged());
        UITK.INotifyValueChangedExtensions.RegisterValueChangedCallback(cutEastField, _ => OnBoundsChanged());

        boundsFoldout.Add(cutWestField);
        boundsFoldout.Add(cutEastField);
        root.Add(boundsFoldout);

        shiftMarkersToggle = new UITK.Toggle { text = "Shift affected markers & semantic zones", value = true };
        keepVisibleToggle = new UITK.Toggle { text = "Keep mig1 mesh visible after bake", value = true };
        showGuidesToggle = new UITK.Toggle { text = "Show cut guides in Scene View", value = true };
        UITK.INotifyValueChangedExtensions.RegisterValueChangedCallback(showGuidesToggle, _ => SceneView.RepaintAll());

        root.Add(shiftMarkersToggle);
        root.Add(keepVisibleToggle);
        root.Add(showGuidesToggle);

        var applyBtn = new UITK.Button(() =>
        {
            Mig1NavMeshSetup.ApplyHallwaySegmentTrim(
                trimField.value,
                cutWestField.value,
                cutEastField.value,
                shiftMarkersToggle.value,
                !keepVisibleToggle.value);
            RefreshSummary();
            SceneView.RepaintAll();
        })
        {
            text = "Apply Hallway Trim & Re-Bake NavMesh"
        };
        applyBtn.style.height = 30;
        applyBtn.style.marginTop = 10;
        applyBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
        root.Add(applyBtn);

        var resetBtn = new UITK.Button(() =>
        {
            Mig1NavMeshSetup.ResetHallwayTrim(shiftMarkersToggle.value, !keepVisibleToggle.value);
            SetTrimInputs(0f);
            RefreshSummary();
            SceneView.RepaintAll();
        })
        {
            text = "Reset to Original mig1.fbx (0 m)"
        };
        resetBtn.style.marginTop = 4;
        root.Add(resetBtn);

        var refreshBtn = new UITK.Button(() =>
        {
            RefreshSummary();
            SceneView.RepaintAll();
        })
        {
            text = "Refresh Measurements"
        };
        refreshBtn.style.marginTop = 4;
        root.Add(refreshBtn);

        RefreshSummary();
        SceneView.RepaintAll();
    }

    private UITK.Button CreateSegmentPresetButton(string label, float westX, float eastX)
    {
        var btn = new UITK.Button(() =>
        {
            if (cutWestField == null || cutEastField == null) return;
            syncing = true;
            cutWestField.SetValueWithoutNotify(westX);
            cutEastField.SetValueWithoutNotify(eastX);
            syncing = false;
            OnBoundsChanged();
        })
        {
            text = label
        };
        btn.style.flexGrow = 1;
        return btn;
    }

    private UITK.Button CreateTrimPresetButton(string label, float targetTrim)
    {
        var btn = new UITK.Button(() => SetTrimInputs(targetTrim)) { text = label };
        btn.style.flexGrow = 1;
        return btn;
    }

    private void OnBoundsChanged()
    {
        if (cutWestField == null || cutEastField == null || trimSlider == null || trimField == null)
            return;

        float maxTrim = ComputeMaxTrim(cutWestField.value, cutEastField.value);
        syncing = true;
        trimSlider.highValue = Mathf.Max(1f, Mathf.Min(10f, maxTrim));
        float clamped = ClampTrimToBounds(trimField.value);
        trimField.SetValueWithoutNotify((float)Math.Round(clamped, 2));
        trimSlider.SetValueWithoutNotify(Mathf.Clamp(clamped, trimSlider.lowValue, trimSlider.highValue));
        syncing = false;
        SceneView.RepaintAll();
    }

    private void SetTrimInputs(float meters)
    {
        if (trimField == null || trimSlider == null) return;
        float clamped = ClampTrimToBounds(meters);
        syncing = true;
        trimField.SetValueWithoutNotify((float)Math.Round(clamped, 2));
        trimSlider.SetValueWithoutNotify(Mathf.Clamp(clamped, trimSlider.lowValue, trimSlider.highValue));
        syncing = false;
        SceneView.RepaintAll();
    }

    private float ClampTrimToBounds(float meters)
    {
        float west = cutWestField != null ? cutWestField.value : Mig1NavMeshSetup.DefaultCutWestX;
        float east = cutEastField != null ? cutEastField.value : Mig1NavMeshSetup.DefaultCutEastX;
        return Mathf.Clamp(meters, 0f, ComputeMaxTrim(west, east));
    }

    private static float ComputeMaxTrim(float westX, float eastX)
    {
        float span = Mathf.Max(2.0f, eastX - westX);
        return Mathf.Max(0f, span - 1.0f);
    }

    private void RefreshSummary()
    {
        if (summaryLabel == null) return;
        summaryLabel.text = Mig1NavMeshSetup.GetHallwayCalibrationSummary(out _);
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (showGuidesToggle != null && !showGuidesToggle.value)
            return;

        GameObject mig1Obj = Mig1NavMeshSetup.FindPrimaryFloorObject();
        if (mig1Obj == null)
            return;

        float xWest = cutWestField != null ? cutWestField.value : Mig1NavMeshSetup.DefaultCutWestX;
        float xEast = cutEastField != null ? cutEastField.value : Mig1NavMeshSetup.DefaultCutEastX;
        if (xWest > xEast)
        {
            float tmp = xWest;
            xWest = xEast;
            xEast = tmp;
        }
        if (xEast - xWest < 2.0f)
            xWest = xEast - 2.0f;

        float trim = trimField != null ? ClampTrimToBounds(trimField.value) : Mig1NavMeshSetup.DefaultTrimMeters;

        const float yMin = -3.0f;
        const float yMax = 1.5f;
        const float zMin = -15.2f;
        const float zMax = -11.4f;
        float yMid = (yMin + yMax) * 0.5f;
        float yHeight = yMax - yMin;
        float zMid = (zMin + zMax) * 0.5f;
        float zDepth = zMax - zMin;

        Matrix4x4 prevMatrix = Handles.matrix;
        Color prevColor = Handles.color;
        try
        {
            Handles.matrix = Matrix4x4.TRS(mig1Obj.transform.position, mig1Obj.transform.rotation, Vector3.one);

            // Amber volume and floor fill covering the hallway cut band.
            var bandCenter = new Vector3((xWest + xEast) * 0.5f, yMid, zMid);
            var bandSize = new Vector3(xEast - xWest, yHeight, zDepth);
            Handles.color = new Color(1f, 0.72f, 0.15f, 0.9f);
            Handles.DrawWireCube(bandCenter, bandSize);

            var floorQuad = new[]
            {
                new Vector3(xWest, -2.66f, zMin),
                new Vector3(xEast, -2.66f, zMin),
                new Vector3(xEast, -2.66f, zMax),
                new Vector3(xWest, -2.66f, zMax)
            };
            Handles.DrawSolidRectangleWithOutline(
                floorQuad,
                new Color(1f, 0.72f, 0.15f, 0.14f),
                new Color(1f, 0.72f, 0.15f, 0.9f));

            // Green vertical plane outline at Fixed East Cut.
            DrawCutPlane(xEast, yMin, yMax, zMin, zMax, new Color(0.2f, 0.92f, 0.4f, 0.95f),
                $"Fixed East Cut ({xEast:F1})");

            // Orange vertical plane outline at West Cut.
            DrawCutPlane(xWest, yMin, yMax, zMin, zMax, new Color(1f, 0.45f, 0.12f, 0.95f),
                $"West Cut ({xWest:F1})");

            // Cyan preview line / plane where West Cut lands after trimming.
            if (trim > 0.01f)
            {
                float previewX = xWest + trim;
                var cyan = new Color(0.15f, 0.9f, 1f, 0.95f);
                Handles.color = cyan;
                Handles.DrawDottedLine(
                    new Vector3(xWest, -2.62f, zMid),
                    new Vector3(previewX, -2.62f, zMid),
                    4f);
                DrawCutPlane(previewX, yMin, yMax, zMin, zMax, cyan,
                    $"New West Edge (-{trim:F2}m)");
            }
        }
        finally
        {
            Handles.matrix = prevMatrix;
            Handles.color = prevColor;
        }
    }

    private static void DrawCutPlane(float x, float yMin, float yMax, float zMin, float zMax, Color color, string label)
    {
        var verts = new[]
        {
            new Vector3(x, yMin, zMin),
            new Vector3(x, yMax, zMin),
            new Vector3(x, yMax, zMax),
            new Vector3(x, yMin, zMax)
        };
        Handles.DrawSolidRectangleWithOutline(
            verts,
            new Color(color.r, color.g, color.b, 0.10f),
            color);

        Handles.color = color;
        Handles.Label(new Vector3(x, yMax + 0.25f, (zMin + zMax) * 0.5f), label);
    }
}

[InitializeOnLoad]
internal static class CanvasSceneVisibilityInitializer
{
    static CanvasSceneVisibilityInitializer()
    {
        EditorApplication.delayCall += Mig1NavMeshSetup.ApplySavedCanvasSceneVisibility;
        EditorSceneManager.sceneOpened += (_, __) => Mig1NavMeshSetup.ApplySavedCanvasSceneVisibility();
    }
}
