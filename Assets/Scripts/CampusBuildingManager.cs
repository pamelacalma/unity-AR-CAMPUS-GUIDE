using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Manages multi-building (M = St. Miguel Hall, SJ = St. Joseph Hall, GK = Gokongwei Hall)
/// and multi-floor models, plus floating 3D world-space AR room signboards above doors (SO1 / SO3).
/// Automatically discovers 'mig1' and any future floor models added to the scene.
/// </summary>
public class CampusBuildingManager : MonoBehaviour
{
    [Serializable]
    public class FloorModelEntry
    {
        public CampusBuildingCode building = CampusBuildingCode.M;
        public int floorNumber = 1;
        public string floorTitle = "St. Miguel Hall 1F";
        public Transform modelRoot;
        public float floorElevationY = -2.68f;
    }

    public static CampusBuildingManager Instance { get; private set; }

    [Header("References")]
    [SerializeField] private DestinationManager destinationManager;
    [SerializeField] private PathVisualizer pathVisualizer;
    [SerializeField] private Transform arCamera;

    [Header("Registered Building / Floor Models")]
    [SerializeField] private List<FloorModelEntry> floorModels = new List<FloorModelEntry>();

    [Header("3D World-Space Room Signboards (SO3)")]
    [SerializeField] private bool show3DRoomSignboards = true;
    [SerializeField] private float signboardHeightAboveDoor = 0.85f;
    [SerializeField] private float maxSignboardVisibleDistance = 38f;
    [SerializeField] private Color normalSignColor = new Color(0.11f, 0.34f, 0.16f, 0.92f);
    [SerializeField] private Color activeSignColor = new Color(0.04f, 0.62f, 0.76f, 0.96f);
    [SerializeField] private Color pwdActiveSignColor = new Color(0.12f, 0.72f, 0.38f, 0.96f);

    public CampusBuildingCode ActiveBuildingFilter { get; private set; } = CampusBuildingCode.All;
    public int ActiveFloorNumber { get; private set; } = 1;
    public IReadOnlyList<FloorModelEntry> FloorModels => floorModels;

    public event Action<CampusBuildingCode, int> BuildingOrFloorChanged;

    private class SignboardInstance
    {
        public Transform target;
        public CampusPoiInfo info;
        public GameObject root;
        public TextMeshPro tmp;
        public MeshRenderer plateRenderer;
        public Material plateMaterial;
    }

    private readonly List<SignboardInstance> signboards = new List<SignboardInstance>();
    private static Mesh cachedQuadMesh;

    private void Awake()
    {
        Instance = this;
        AutoDiscoverReferences();
        AutoDiscoverFloorModels();
    }

    private void Start()
    {
        RebuildSignboards();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        ClearSignboards();
    }

    public void AutoDiscoverReferences()
    {
        if (destinationManager == null)
            destinationManager = FindAnyObjectByType<DestinationManager>();
        if (pathVisualizer == null)
            pathVisualizer = FindAnyObjectByType<PathVisualizer>();
        if (arCamera == null)
        {
            if (pathVisualizer != null && pathVisualizer.userTransform != null)
                arCamera = pathVisualizer.userTransform;
            else if (Camera.main != null)
                arCamera = Camera.main.transform;
        }
    }

    public void AutoDiscoverFloorModels()
    {
        var knownRoots = new HashSet<Transform>();
        for (int i = floorModels.Count - 1; i >= 0; i--)
        {
            if (floorModels[i] == null || floorModels[i].modelRoot == null)
                floorModels.RemoveAt(i);
            else
                knownRoots.Add(floorModels[i].modelRoot);
        }

        // Discover mig1, mig2, mig3, sj1..sj4, gk1..gk4 automatically if present in scene
        foreach (GameObject go in FindObjectsByType<GameObject>(FindObjectsInactive.Include))
        {
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            if (go.transform.parent != null) continue;
            if (knownRoots.Contains(go.transform)) continue;

            string n = go.name.Trim().ToUpperInvariant();
            CampusBuildingCode code = CampusBuildingCode.All;
            int floor = 1;

            if (n.StartsWith("MIG") || n == "M1" || n == "M2" || n == "M3" || n == "M4")
            {
                code = CampusBuildingCode.M;
                floor = ExtractTrailingFloor(n, 1);
            }
            else if (n.StartsWith("SJ") || n.StartsWith("STJOSEPH") || n.StartsWith("JOSEPH"))
            {
                code = CampusBuildingCode.SJ;
                floor = ExtractTrailingFloor(n, 1);
            }
            else if (n.StartsWith("GK") || n.StartsWith("GOK"))
            {
                code = CampusBuildingCode.GK;
                floor = ExtractTrailingFloor(n, 1);
            }

            if (code != CampusBuildingCode.All)
            {
                floorModels.Add(new FloorModelEntry
                {
                    building = code,
                    floorNumber = floor,
                    floorTitle = $"{CampusPoiMetadata.GetBuildingFullName(code)} ({CampusPoiMetadata.GetBuildingShortCode(code)}) \u00B7 {floor}F",
                    modelRoot = go.transform,
                    floorElevationY = go.transform.position.y - 2.68f
                });
                knownRoots.Add(go.transform);
            }
        }
    }

    private static int ExtractTrailingFloor(string upperName, int fallback)
    {
        for (int i = upperName.Length - 1; i >= 0; i--)
        {
            if (char.IsDigit(upperName[i]))
                return Mathf.Clamp(upperName[i] - '0', 1, 9);
        }
        return fallback;
    }

    public void SetBuildingFilter(CampusBuildingCode code)
    {
        if (ActiveBuildingFilter == code) return;
        ActiveBuildingFilter = code;
        BuildingOrFloorChanged?.Invoke(ActiveBuildingFilter, ActiveFloorNumber);
    }

    public void SetActiveFloor(int floorNumber)
    {
        floorNumber = Mathf.Clamp(floorNumber, 1, 9);
        if (ActiveFloorNumber == floorNumber) return;
        ActiveFloorNumber = floorNumber;
        BuildingOrFloorChanged?.Invoke(ActiveBuildingFilter, ActiveFloorNumber);
    }

    public bool HasLoadedModelForBuilding(CampusBuildingCode code)
    {
        if (code == CampusBuildingCode.All) return floorModels.Count > 0;
        foreach (FloorModelEntry e in floorModels)
        {
            if (e != null && e.building == code && e.modelRoot != null)
                return true;
        }
        return false;
    }

    public string GetActiveFloorBadgeText()
    {
        if (pathVisualizer != null && pathVisualizer.CurrentDestination != null)
        {
            CampusPoiInfo destInfo = CampusPoiMetadata.GetOrInfer(pathVisualizer.CurrentDestination);
            return $"{destInfo.BuildingShortCode} \u00B7 {destInfo.BuildingFullName} {destInfo.FloorLabel}";
        }

        if (ActiveBuildingFilter != CampusBuildingCode.All)
        {
            return $"{CampusPoiMetadata.GetBuildingShortCode(ActiveBuildingFilter)} \u00B7 {CampusPoiMetadata.GetBuildingFullName(ActiveBuildingFilter)} {ActiveFloorNumber}F";
        }

        if (floorModels.Count > 0 && floorModels[0].modelRoot != null)
            return floorModels[0].floorTitle;

        return "M \u00B7 St. Miguel Hall 1F";
    }

    public void RebuildSignboards()
    {
        ClearSignboards();
        AutoDiscoverReferences();
        if (destinationManager == null || destinationManager.targets == null) return;

        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Sprites/Default");

        foreach (Transform t in destinationManager.targets)
        {
            if (t == null) continue;
            CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(t);

            var rootGo = new GameObject("RoomSign3D_" + info.roomCode);
            rootGo.transform.SetParent(transform, false);
            rootGo.transform.position = t.position + Vector3.up * signboardHeightAboveDoor;

            // Background plate quad
            var plateGo = new GameObject("BackPlate");
            plateGo.transform.SetParent(rootGo.transform, false);
            plateGo.transform.localPosition = new Vector3(0f, 0f, 0.02f);
            plateGo.transform.localScale = new Vector3(1.55f, 0.46f, 1f);

            var mf = plateGo.AddComponent<MeshFilter>();
            mf.sharedMesh = GetOrCreateQuadMesh();

            var mr = plateGo.AddComponent<MeshRenderer>();
            Material mat = PathArrowPool.CreateUnlitMaterial(null, normalSignColor, unlitShader);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // 3D TextMeshPro label
            var textGo = new GameObject("LabelTMP");
            textGo.transform.SetParent(rootGo.transform, false);
            textGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);

            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.text = $"{info.roomCode}\n<size=72%>{info.displayName}</size>";
            tmp.fontSize = 1.65f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.rectTransform.sizeDelta = new Vector2(1.48f, 0.44f);

            signboards.Add(new SignboardInstance
            {
                target = t,
                info = info,
                root = rootGo,
                tmp = tmp,
                plateRenderer = mr,
                plateMaterial = mat
            });
        }
    }

    private void Update()
    {
        if (signboards.Count == 0 && destinationManager != null && destinationManager.targets != null && destinationManager.targets.Count > 0)
            RebuildSignboards();

        bool allowVisible = show3DRoomSignboards && !UIManager.IsMainMenuActive && !UIManager.Is2DMapActive;
        Transform cam = arCamera != null ? arCamera : (Camera.main != null ? Camera.main.transform : null);
        Transform activeDest = pathVisualizer != null ? pathVisualizer.CurrentDestination : null;
        bool pwd = pathVisualizer != null && pathVisualizer.PwdAccessibleMode;

        for (int i = 0; i < signboards.Count; i++)
        {
            SignboardInstance s = signboards[i];
            if (s.root == null || s.target == null) continue;

            if (!allowVisible || cam == null)
            {
                if (s.root.activeSelf) s.root.SetActive(false);
                continue;
            }

            s.root.transform.position = s.target.position + Vector3.up * signboardHeightAboveDoor;
            float dist = Vector3.Distance(cam.position, s.target.position);
            bool isSelected = s.target == activeDest;

            if (!isSelected && dist > maxSignboardVisibleDistance)
            {
                if (s.root.activeSelf) s.root.SetActive(false);
                continue;
            }

            if (!s.root.activeSelf) s.root.SetActive(true);

            // Billboard toward camera around Y axis
            Vector3 toSign = s.root.transform.position - cam.position;
            toSign.y = 0f;
            if (toSign.sqrMagnitude > 1e-4f)
                s.root.transform.rotation = Quaternion.LookRotation(toSign.normalized, Vector3.up);

            // Scale slightly with distance so labels stay readable down long hallways
            float scale = Mathf.Clamp(dist * 0.12f, 0.75f, 1.85f);
            s.root.transform.localScale = Vector3.one * scale;

            Color plateCol = isSelected ? (pwd ? pwdActiveSignColor : activeSignColor) : normalSignColor;
            PathArrowPool.ApplyMaterialColor(s.plateMaterial, plateCol);

            if (s.tmp != null)
            {
                string distTag = isSelected && pathVisualizer != null && pathVisualizer.State == NavState.Navigating
                    ? $"{pathVisualizer.DistanceRemaining:0} m"
                    : $"{dist:0} m";
                s.tmp.text = $"{s.info.roomCode} \u00B7 {distTag}\n<size=72%>{s.info.displayName}</size>";
            }
        }
    }

    private void ClearSignboards()
    {
        for (int i = 0; i < signboards.Count; i++)
        {
            if (signboards[i].root != null)
                Destroy(signboards[i].root);
            if (signboards[i].plateMaterial != null)
                Destroy(signboards[i].plateMaterial);
        }
        signboards.Clear();
    }

    private static Mesh GetOrCreateQuadMesh()
    {
        if (cachedQuadMesh != null) return cachedQuadMesh;
        cachedQuadMesh = new Mesh { name = "SignboardPlateQuad" };
        cachedQuadMesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3( 0.5f, -0.5f, 0f),
            new Vector3( 0.5f,  0.5f, 0f),
            new Vector3(-0.5f,  0.5f, 0f)
        };
        cachedQuadMesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f)
        };
        cachedQuadMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        cachedQuadMesh.RecalculateNormals();
        cachedQuadMesh.RecalculateBounds();
        return cachedQuadMesh;
    }
}
