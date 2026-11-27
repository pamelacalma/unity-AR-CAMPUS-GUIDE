using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Dockable Editor tool for enabling, disabling, and soloing St. Miguel Hall
/// per floor (1F-4F) or across the entire building in one click.
/// </summary>
public class MiguelFloorToggleWindow : EditorWindow
{
    private const string IncludeMarkersPrefKey = "ARCampusGuide.MiguelToggler.IncludeMarkers";

    private struct FloorDefinition
    {
        public int floorNumber;
        public string displayTitle;
        public string[] candidateRootNames;
    }

    private static readonly FloorDefinition[] MiguelFloors =
    {
        new FloorDefinition
        {
            floorNumber = 1,
            displayTitle = "1st Floor",
            candidateRootNames = new[] { "MIGUEL_1st_Floor", "mig1" }
        },
        new FloorDefinition
        {
            floorNumber = 2,
            displayTitle = "2nd Floor",
            candidateRootNames = new[] { "MIGUEL_2nd_Floor" }
        },
        new FloorDefinition
        {
            floorNumber = 3,
            displayTitle = "3rd Floor",
            candidateRootNames = new[] { "MIGUEL_3rd_Floor" }
        },
        new FloorDefinition
        {
            floorNumber = 4,
            displayTitle = "4th Floor",
            candidateRootNames = new[] { "MIGUEL_4th_Floor" }
        }
    };

    private bool includeFloorMarkers = true;

    [MenuItem("Tools/AR Campus Guide/Miguel Floors/Open Miguel Floor Toggler...")]
    public static void ShowWindow()
    {
        MiguelFloorToggleWindow window = GetWindow<MiguelFloorToggleWindow>("Miguel Floors");
        window.minSize = new Vector2(360f, 270f);
        window.Show();
    }

    [MenuItem("Tools/AR Campus Guide/Miguel Floors/Enable All Miguel Floors")]
    public static void EnableAllMiguelFloors()
    {
        SetAllMiguelFloorsActive(true, includeFloorMarkers: true);
    }

    [MenuItem("Tools/AR Campus Guide/Miguel Floors/Disable All Miguel Floors")]
    public static void DisableAllMiguelFloors()
    {
        SetAllMiguelFloorsActive(false, includeFloorMarkers: true);
    }

    private void OnEnable()
    {
        includeFloorMarkers = EditorPrefs.GetBool(IncludeMarkersPrefKey, true);
        EditorApplication.hierarchyChanged += Repaint;
    }

    private void OnDisable()
    {
        EditorApplication.hierarchyChanged -= Repaint;
    }

    private void OnGUI()
    {
        GameObject[] sceneObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include);

        int foundCount = 0;
        int activeCount = 0;
        var resolvedFloors = new GameObject[MiguelFloors.Length];
        for (int i = 0; i < MiguelFloors.Length; i++)
        {
            GameObject root = FindSceneRootByNames(sceneObjects, MiguelFloors[i].candidateRootNames);
            resolvedFloors[i] = root;
            if (root != null)
            {
                foundCount++;
                if (root.activeSelf)
                    activeCount++;
            }
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField($"St. Miguel Hall ({activeCount} / {foundCount} floors active)", EditorStyles.boldLabel);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        GUI.enabled = foundCount > 0;
        if (GUILayout.Button("Enable All Miguel", GUILayout.Height(26f)))
            SetAllMiguelFloorsActive(true, includeFloorMarkers);

        if (GUILayout.Button("Disable All Miguel", GUILayout.Height(26f)))
            SetAllMiguelFloorsActive(false, includeFloorMarkers);
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4f);
        bool nextIncludeMarkers = EditorGUILayout.ToggleLeft(
            "Also toggle floor rooms & BLE spots",
            includeFloorMarkers);
        if (nextIncludeMarkers != includeFloorMarkers)
        {
            includeFloorMarkers = nextIncludeMarkers;
            EditorPrefs.SetBool(IncludeMarkersPrefKey, includeFloorMarkers);
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Per-Floor Visibility", EditorStyles.boldLabel);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        for (int i = 0; i < MiguelFloors.Length; i++)
        {
            FloorDefinition def = MiguelFloors[i];
            GameObject floorGo = resolvedFloors[i];

            EditorGUILayout.BeginHorizontal();
            if (floorGo == null)
            {
                GUI.enabled = false;
                EditorGUILayout.ToggleLeft($"{def.displayTitle} ({def.candidateRootNames[0]} - not in scene)", false);
                GUILayout.Button("Solo", GUILayout.Width(52f));
                GUILayout.Button("Focus", GUILayout.Width(56f));
                GUI.enabled = true;
            }
            else
            {
                bool wasActive = floorGo.activeSelf;
                bool wantActive = EditorGUILayout.ToggleLeft($"{def.displayTitle} ({floorGo.name})", wasActive);
                if (wantActive != wasActive)
                    SetSingleFloorActive(def.floorNumber, floorGo, wantActive, includeFloorMarkers);

                if (GUILayout.Button("Solo", GUILayout.Width(52f)))
                    SoloMiguelFloor(def.floorNumber, includeFloorMarkers);

                if (GUILayout.Button("Focus", GUILayout.Width(56f)))
                {
                    Selection.activeGameObject = floorGo;
                    SceneView view = SceneView.lastActiveSceneView;
                    if (view != null)
                        view.FrameSelected();
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndVertical();
    }

    public static void SetAllMiguelFloorsActive(bool active, bool includeFloorMarkers)
    {
        GameObject[] sceneObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include);

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(active ? "Enable All Miguel Floors" : "Disable All Miguel Floors");

        int changedCount = 0;
        var dirtyScenes = new HashSet<Scene>();

        for (int i = 0; i < MiguelFloors.Length; i++)
        {
            FloorDefinition def = MiguelFloors[i];
            GameObject floorGo = FindSceneRootByNames(sceneObjects, def.candidateRootNames);
            if (floorGo != null && TrySetObjectActiveWithUndo(floorGo, active, dirtyScenes))
                changedCount++;

            if (includeFloorMarkers)
            {
                foreach (GameObject assoc in FindFloorAssociatedObjects(sceneObjects, def.floorNumber))
                {
                    if (TrySetObjectActiveWithUndo(assoc, active, dirtyScenes))
                        changedCount++;
                }
            }
        }

        Undo.CollapseUndoOperations(undoGroup);
        MarkDirtyScenes(dirtyScenes);
        Debug.Log($"[MiguelFloorToggleWindow] {(active ? "Enabled" : "Disabled")} Miguel floors (updated {changedCount} object(s)).");
    }

    public static void SoloMiguelFloor(int targetFloorNumber, bool includeFloorMarkers)
    {
        GameObject[] sceneObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include);

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName($"Solo Miguel {targetFloorNumber}F");

        var dirtyScenes = new HashSet<Scene>();
        for (int i = 0; i < MiguelFloors.Length; i++)
        {
            FloorDefinition def = MiguelFloors[i];
            bool shouldBeActive = def.floorNumber == targetFloorNumber;

            GameObject floorGo = FindSceneRootByNames(sceneObjects, def.candidateRootNames);
            if (floorGo != null)
                TrySetObjectActiveWithUndo(floorGo, shouldBeActive, dirtyScenes);

            if (includeFloorMarkers)
            {
                foreach (GameObject assoc in FindFloorAssociatedObjects(sceneObjects, def.floorNumber))
                    TrySetObjectActiveWithUndo(assoc, shouldBeActive, dirtyScenes);
            }
        }

        Undo.CollapseUndoOperations(undoGroup);
        MarkDirtyScenes(dirtyScenes);
    }

    private static void SetSingleFloorActive(int floorNumber, GameObject floorGo, bool active, bool includeFloorMarkers)
    {
        GameObject[] sceneObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include);

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName($"{(active ? "Enable" : "Disable")} Miguel {floorNumber}F");

        var dirtyScenes = new HashSet<Scene>();
        TrySetObjectActiveWithUndo(floorGo, active, dirtyScenes);

        if (includeFloorMarkers)
        {
            foreach (GameObject assoc in FindFloorAssociatedObjects(sceneObjects, floorNumber))
                TrySetObjectActiveWithUndo(assoc, active, dirtyScenes);
        }

        Undo.CollapseUndoOperations(undoGroup);
        MarkDirtyScenes(dirtyScenes);
    }

    private static bool TrySetObjectActiveWithUndo(GameObject go, bool active, HashSet<Scene> dirtyScenes)
    {
        if (go == null || go.activeSelf == active)
            return false;

        Undo.RecordObject(go, active ? "Enable Miguel Floor Object" : "Disable Miguel Floor Object");
        go.SetActive(active);
        EditorUtility.SetDirty(go);
        if (go.scene.IsValid())
            dirtyScenes.Add(go.scene);
        return true;
    }

    private static void MarkDirtyScenes(HashSet<Scene> dirtyScenes)
    {
        foreach (Scene scene in dirtyScenes)
        {
            if (scene.IsValid())
                EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    private static GameObject FindSceneRootByNames(GameObject[] sceneObjects, params string[] names)
    {
        if (sceneObjects == null || names == null)
            return null;

        foreach (string candidateName in names)
        {
            foreach (GameObject go in sceneObjects)
            {
                if (go == null || string.IsNullOrEmpty(go.scene.name) || go.transform.parent != null)
                    continue;
                if (string.Equals(go.name, candidateName, StringComparison.OrdinalIgnoreCase))
                    return go;
            }
        }
        return null;
    }

    private static List<GameObject> FindFloorAssociatedObjects(GameObject[] sceneObjects, int floorNumber)
    {
        var results = new List<GameObject>();
        if (sceneObjects == null)
            return results;

        string bleSpotPrefix = $"BLE_Spot_M{floorNumber}F_";
        string roomPrefix = $"Destination_Room {floorNumber}";

        foreach (GameObject go in sceneObjects)
        {
            if (go == null || string.IsNullOrEmpty(go.scene.name))
                continue;

            string n = go.name.Trim();
            if (n.StartsWith(bleSpotPrefix, StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith(roomPrefix, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(go);
                continue;
            }

            if (floorNumber == 1 &&
                (string.Equals(n, "Entrance_1", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(n, "Destination_Men CR", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(n, "BEACON_03", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(n, "BEACON_04", StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(go);
            }
        }
        return results;
    }
}
