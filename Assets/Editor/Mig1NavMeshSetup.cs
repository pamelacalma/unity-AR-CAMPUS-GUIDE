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
using UITK = UnityEngine.UIElements;

public static class Mig1NavMeshSetup
{
    private const string Mig1AssetPath = "Assets/Scenes/mig1.fbx";
    private const string CalibratedMeshDir = "Assets/Scenes/CalibratedMeshes";
    private const string TrimStatePath = "ProjectSettings/Mig1HallwayTrimState.json";
    public const float DefaultCutWestX = -25.0f;
    public const float DefaultCutEastX = -12.5f;
    public const float DefaultTrimMeters = 3.5f;

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

    [MenuItem("Tools/AR Campus Guide/1. Build NavMesh from mig1 (Keep Mesh Visible)")]
    public static void BuildVisible()
    {
        BuildFromMig1(hideRenderersAfterBake: false);
    }

    [MenuItem("Tools/AR Campus Guide/1b. Build NavMesh from mig1 (Hide Mesh for AR)")]
    public static void BuildHiddenForAR()
    {
        BuildFromMig1(hideRenderersAfterBake: true);
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
                    elem.FindPropertyRelative("anchor").objectReferenceValue = kv.Value;
                    elem.FindPropertyRelative("macAddress").stringValue = "";
                    wiredBle++;
                }
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(ble);
            report.Add($"BlePositionProvider.beacons updated: {wiredBle}");
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

        UIManager uiMgr = UnityEngine.Object.FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
        if (uiMgr != null)
        {
            uiMgr.ShowMainMenu();
            if (uiMgr.mainMenuPanel != null) EditorUtility.SetDirty(uiMgr.mainMenuPanel);
        }

        // 5. Hide mig1 and marker MeshRenderers so AR camera feed stays unobstructed.
        int hiddenMig1 = 0;
        GameObject mig1Obj = GameObject.Find("mig1");
        if (mig1Obj != null)
        {
            foreach (MeshRenderer r in mig1Obj.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.enabled)
                {
                    SetRendererEnabled(r, false, "Hide mig1 Renderer");
                    hiddenMig1++;
                }
            }
        }
        int hiddenMarkers = SetMarkerRenderersVisibleInternal(false);
        report.Add($"Hidden MeshRenderers: mig1={hiddenMig1}, markers={hiddenMarkers}");

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

    [MenuItem("Tools/AR Campus Guide/5a. Add Semantic Zone - Stairs (Blocked in PWD)")]
    public static void AddStairsZone() => CreateSemanticZone("Stairs");

    [MenuItem("Tools/AR Campus Guide/5b. Add Semantic Zone - Ramp (PWD Priority)")]
    public static void AddRampZone() => CreateSemanticZone("Ramp");

    [MenuItem("Tools/AR Campus Guide/5c. Add Semantic Zone - Elevator (PWD Priority)")]
    public static void AddElevatorZone() => CreateSemanticZone("Elevator");

    [MenuItem("Tools/AR Campus Guide/5d. Add Semantic Zone - Classroom")]
    public static void AddClassroomZone() => CreateSemanticZone("Classroom");

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

        GameObject mig1Obj = GameObject.Find("mig1");
        if (mig1Obj == null)
        {
            EditorUtility.DisplayDialog(
                "mig1 Not Found",
                "Could not find 'mig1' in the active scene. Run Tools > AR Campus Guide > 1. Build NavMesh from mig1 first.",
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

    private static void BuildFromMig1(bool hideRenderersAfterBake)
    {
        GameObject mig1Obj = GameObject.Find("mig1");
        if (mig1Obj == null)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Mig1AssetPath);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog(
                    "mig1 Model Not Found",
                    $"Could not find 'mig1' in the scene or at {Mig1AssetPath}.",
                    "OK");
                return;
            }
            mig1Obj = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            mig1Obj.name = "mig1";
            mig1Obj.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Undo.RegisterCreatedObjectUndo(mig1Obj, "Instantiate mig1");
        }

        MeshRenderer[] renderers = mig1Obj.GetComponentsInChildren<MeshRenderer>(true);
        foreach (MeshRenderer r in renderers)
        {
            SetRendererEnabled(r, true, "Show mig1 Renderer");
        }

        NavMeshSurface surface = mig1Obj.GetComponent<NavMeshSurface>();
        if (surface == null)
        {
            surface = Undo.AddComponent<NavMeshSurface>(mig1Obj);
        }

        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
        surface.minRegionArea = 2f;
        surface.enabled = true;
        EditorUtility.SetDirty(surface);

        if (NavMeshAssetManager.instance.IsSurfaceBaking(surface))
        {
            Debug.LogWarning("[Mig1NavMeshSetup] NavMesh bake already in progress for mig1.");
            return;
        }

        NavMeshAssetManager.instance.StartBakingSurfaces(new UnityEngine.Object[] { surface });

        EditorApplication.CallbackFunction onUpdate = null;
        onUpdate = () =>
        {
            if (surface != null && NavMeshAssetManager.instance.IsSurfaceBaking(surface))
                return;

            EditorApplication.update -= onUpdate;

            if (mig1Obj == null) return;

            if (hideRenderersAfterBake)
            {
                MeshRenderer[] currentRenderers = mig1Obj.GetComponentsInChildren<MeshRenderer>(true);
                foreach (MeshRenderer r in currentRenderers)
                {
                    SetRendererEnabled(r, false, "Hide mig1 Renderer");
                }
            }

            EditorUtility.SetDirty(mig1Obj);
            EditorSceneManager.MarkSceneDirty(mig1Obj.scene);
            EditorSceneManager.SaveOpenScenes();
            Selection.activeGameObject = mig1Obj;

            Debug.Log($"[Mig1NavMeshSetup] Baked and saved NavMesh asset from mig1 (renderers {(hideRenderersAfterBake ? "hidden" : "visible")}).");
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
        GameObject mig1Obj = GameObject.Find("mig1");
        if (mig1Obj == null)
        {
            EditorUtility.DisplayDialog(
                "mig1 Not Found",
                "Could not find 'mig1' in the active scene. Run Tools > AR Campus Guide > 1. Build NavMesh from mig1 first.",
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

        // Step B: Ensure source mig1.fbx is readable and load pristine meshes.
        EnsureMig1Readable();
        GameObject fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Mig1AssetPath);
        if (fbxPrefab == null)
        {
            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.DisplayDialog(
                "mig1 Source Missing",
                $"Could not load source FBX asset at '{Mig1AssetPath}'.",
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
        ModelImporter importer = AssetImporter.GetAtPath(Mig1AssetPath) as ModelImporter;
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

        GameObject mig1Obj = GameObject.Find("mig1");
        if (mig1Obj == null)
            return "mig1 not found in active scene.";

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

        GameObject mig1Obj = GameObject.Find("mig1");
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
