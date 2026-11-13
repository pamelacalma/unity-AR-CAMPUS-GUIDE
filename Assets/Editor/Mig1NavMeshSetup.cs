using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using Unity.AI.Navigation;

public static class Mig1NavMeshSetup
{
    private const string Mig1AssetPath = "Assets/Scenes/mig1.fbx";

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
                    Undo.RecordObject(r, "Hide mig1 Renderer");
                    r.enabled = false;
                    EditorUtility.SetDirty(r);
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
            r.enabled = true;
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

        surface.BuildNavMesh();

        if (hideRenderersAfterBake)
        {
            foreach (MeshRenderer r in renderers)
            {
                r.enabled = false;
            }
        }

        EditorUtility.SetDirty(mig1Obj);
        EditorSceneManager.MarkSceneDirty(mig1Obj.scene);
        EditorSceneManager.SaveOpenScenes();
        Selection.activeGameObject = mig1Obj;

        Debug.Log($"[Mig1NavMeshSetup] Baked NavMesh from mig1 (renderers {(hideRenderersAfterBake ? "hidden" : "visible")}).");
    }
}
