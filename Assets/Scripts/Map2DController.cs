using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// SO3 Deliverable: Interactive 2D Top-Down Campus Floorplan & Mini-Map View.
/// Automatically populates UIManager.map2DPanel, renders the baked NavMesh floorplan
/// (including semantic zones for Stairs, Ramp, Elevator), overlays the active A* route
/// and live user pose/heading, and provides clickable room pins filtered by building code
/// (M = St. Miguel Hall, SJ = St. Joseph Hall, GK = Gokongwei Hall).
/// </summary>
public class Map2DController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private UIManager uiManager;
    [SerializeField] private DestinationManager destinationManager;
    [SerializeField] private PathVisualizer pathVisualizer;
    [SerializeField] private RectTransform mapPanelRoot;

    [Header("2D Map Visual Settings")]
    [SerializeField] private int mapTextureSize = 512;
    [SerializeField] private float worldPaddingMeters = 3.0f;
    [SerializeField] private Color mapBgColor = new Color(0.07f, 0.11f, 0.09f, 1f);
    [SerializeField] private Color walkableFloorColor = new Color(0.18f, 0.32f, 0.23f, 1f);
    [SerializeField] private Color floorBorderColor = new Color(0.38f, 0.68f, 0.46f, 1f);
    [SerializeField] private Color stairsAreaColor = new Color(0.85f, 0.42f, 0.16f, 1f);
    [SerializeField] private Color rampElevatorColor = new Color(0.16f, 0.75f, 0.45f, 1f);

    private Texture2D baseFloorTexture;
    private Texture2D dynamicMapTexture;
    private Color32[] basePixels;
    private Color32[] workingPixels;

    private UnityEngine.UI.RawImage mapRawImage;
    private RectTransform pinOverlayContainer;
    private TMP_Text headerSubtitleText;
    private TMP_Text footerStatusText;
    private UnityEngine.UI.Image pwdBtnImage;
    private TMP_Text pwdBtnText;

    private readonly List<UnityEngine.UI.Button> buildingTabButtons = new List<UnityEngine.UI.Button>();
    private readonly List<CampusBuildingCode> buildingTabCodes = new List<CampusBuildingCode>();
    private readonly List<PinWidget> pinWidgets = new List<PinWidget>();

    private struct PinWidget
    {
        public Transform target;
        public CampusPoiInfo info;
        public RectTransform rect;
        public UnityEngine.UI.Image bg;
        public TMP_Text label;
    }

    private Bounds navBounds;
    private bool hasValidBounds;
    private bool uiBuilt;
    private float nextRedrawTime;
    private CampusBuildingCode selectedBuilding = CampusBuildingCode.All;

    private void Awake()
    {
        AutoDiscoverReferences();
    }

    public Texture2D GetLiveMapTexture()
    {
        AutoDiscoverReferences();
        if (baseFloorTexture == null || dynamicMapTexture == null)
        {
            uiBuilt = true;
            BakeBaseNavMeshFloorplan();
        }
        RedrawDynamicOverlay();
        return dynamicMapTexture;
    }

    private void OnEnable()
    {
        AutoDiscoverReferences();
        EnsureMapUiBuilt();
        BakeBaseNavMeshFloorplan();
        RefreshPinWidgets();
        RedrawDynamicOverlay();
    }

    private void OnDestroy()
    {
        if (baseFloorTexture != null) Destroy(baseFloorTexture);
        if (dynamicMapTexture != null) Destroy(dynamicMapTexture);
    }

    private void AutoDiscoverReferences()
    {
        if (uiManager == null) uiManager = FindAnyObjectByType<UIManager>();
        if (destinationManager == null) destinationManager = FindAnyObjectByType<DestinationManager>();
        if (pathVisualizer == null) pathVisualizer = FindAnyObjectByType<PathVisualizer>();
        if (mapPanelRoot == null && uiManager != null && uiManager.map2DPanel != null)
            mapPanelRoot = uiManager.map2DPanel.GetComponent<RectTransform>();
        if (mapPanelRoot == null)
            mapPanelRoot = GetComponent<RectTransform>();
    }

    public void EnsureMapUiBuilt()
    {
        if (uiBuilt || mapPanelRoot == null) return;
        uiBuilt = true;

        // Clear any placeholder or ensure full-screen stretch
        mapPanelRoot.anchorMin = Vector2.zero;
        mapPanelRoot.anchorMax = Vector2.one;
        mapPanelRoot.offsetMin = Vector2.zero;
        mapPanelRoot.offsetMax = Vector2.zero;

        var rootBg = mapPanelRoot.GetComponent<UnityEngine.UI.Image>();
        if (rootBg == null) rootBg = mapPanelRoot.gameObject.AddComponent<UnityEngine.UI.Image>();
        rootBg.color = new Color(0.05f, 0.09f, 0.07f, 0.98f);

        // 1. Top Header Bar
        RectTransform topBar = CreatePanel(mapPanelRoot, "TopHeaderBar",
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, 0f), new Vector2(0f, 150f),
            new Color(0.09f, 0.25f, 0.13f, 0.98f));

        // Back to AR button
        UnityEngine.UI.Button backBtn = CreateStyledButton(topBar, "Button_BackToAR", "< AR View",
            new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(24f, 0f), new Vector2(210f, 82f),
            new Color(0.16f, 0.42f, 0.20f, 1f), 28f);
        backBtn.onClick.AddListener(() =>
        {
            if (uiManager != null) uiManager.ShowARNav();
        });

        // Header title & subtitle
        TMP_Text titleTmp = CreateLabel(topBar, "Title", "2D CAMPUS FLOORPLAN",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 18f), new Vector2(480f, 54f), 32f, FontStyles.Bold, TextAlignmentOptions.Center);
        titleTmp.color = Color.white;

        headerSubtitleText = CreateLabel(topBar, "Subtitle", "M \u00B7 St. Miguel Hall 1F",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, -26f), new Vector2(520f, 42f), 24f, FontStyles.Normal, TextAlignmentOptions.Center);
        headerSubtitleText.color = new Color(0.75f, 0.95f, 0.80f, 1f);

        // PWD toggle on 2D Map
        UnityEngine.UI.Button pwdBtn = CreateStyledButton(topBar, "Button_MapPwdToggle", "PWD: OFF",
            new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(-24f, 0f), new Vector2(210f, 82f),
            new Color(0.12f, 0.18f, 0.22f, 0.95f), 26f);
        pwdBtnImage = pwdBtn.GetComponent<UnityEngine.UI.Image>();
        pwdBtnText = pwdBtn.GetComponentInChildren<TMP_Text>(true);
        pwdBtn.onClick.AddListener(() =>
        {
            if (pathVisualizer != null)
            {
                pathVisualizer.TogglePwdAccessibleMode();
                UpdatePwdButtonVisual();
                RedrawDynamicOverlay();
            }
        });

        // 2. Building Code Filter Row (ALL | M | SJ | GK)
        RectTransform filterBar = CreatePanel(mapPanelRoot, "BuildingCodeBar",
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -155f), new Vector2(-32f, 88f),
            new Color(0.08f, 0.14f, 0.10f, 0.95f));

        var hLayout = filterBar.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
        hLayout.spacing = 12f;
        hLayout.padding = new RectOffset(14, 14, 10, 10);
        hLayout.childControlWidth = true;
        hLayout.childControlHeight = true;
        hLayout.childForceExpandWidth = true;
        hLayout.childForceExpandHeight = true;

        AddBuildingTab(filterBar, CampusBuildingCode.All, "ALL");
        AddBuildingTab(filterBar, CampusBuildingCode.M, "M \u00B7 Miguel");
        AddBuildingTab(filterBar, CampusBuildingCode.SJ, "SJ \u00B7 St. Joseph");
        AddBuildingTab(filterBar, CampusBuildingCode.GK, "GK \u00B7 Gokongwei");

        // 3. Central Map Viewport
        RectTransform mapContainer = CreatePanel(mapPanelRoot, "MapViewportContainer",
            new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero,
            new Color(0.04f, 0.07f, 0.06f, 1f));
        mapContainer.offsetMin = new Vector2(24f, 220f);
        mapContainer.offsetMax = new Vector2(-24f, -258f);

        var rawGo = new GameObject("FloorplanRawImage", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.RawImage));
        rawGo.transform.SetParent(mapContainer, false);
        var rawRect = rawGo.GetComponent<RectTransform>();
        rawRect.anchorMin = Vector2.zero;
        rawRect.anchorMax = Vector2.one;
        rawRect.offsetMin = new Vector2(8f, 8f);
        rawRect.offsetMax = new Vector2(-8f, -8f);
        mapRawImage = rawGo.GetComponent<UnityEngine.UI.RawImage>();

        pinOverlayContainer = CreatePanel(rawRect, "PinOverlay",
            Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero, Color.clear);
        pinOverlayContainer.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;

        // 4. Bottom Legend & Action Footer
        RectTransform footerBar = CreatePanel(mapPanelRoot, "BottomFooterBar",
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 0f), new Vector2(0f, 200f),
            new Color(0.09f, 0.22f, 0.12f, 0.98f));

        footerStatusText = CreateLabel(footerBar, "FooterStatus",
            "Tap a room pin on the map to set destination. Building codes: M = Miguel, SJ = St. Joseph, GK = Gokongwei.",
            new Vector2(0f, 0.5f), new Vector2(0.68f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(28f, 0f), new Vector2(-36f, 150f), 25f, FontStyles.Normal, TextAlignmentOptions.Left);
        footerStatusText.color = Color.white;
        footerStatusText.textWrappingMode = TextWrappingModes.Normal;

        UnityEngine.UI.Button goArBtn = CreateStyledButton(footerBar, "Button_StartARFromMap", "Navigate in AR >",
            new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
            new Vector2(-24f, 0f), new Vector2(280f, 96f),
            new Color(0.20f, 0.55f, 0.24f, 1f), 28f);
        goArBtn.onClick.AddListener(() =>
        {
            if (uiManager != null) uiManager.ShowARNav();
        });

        UpdateBuildingTabHighlights();
        UpdatePwdButtonVisual();
    }

    private void AddBuildingTab(RectTransform parent, CampusBuildingCode code, string label)
    {
        UnityEngine.UI.Button btn = CreateStyledButton(parent, "Tab_" + code, label,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            Vector2.zero, new Vector2(160f, 64f),
            new Color(0.14f, 0.24f, 0.17f, 1f), 23f);

        buildingTabButtons.Add(btn);
        buildingTabCodes.Add(code);

        btn.onClick.AddListener(() =>
        {
            selectedBuilding = code;
            if (CampusBuildingManager.Instance != null)
                CampusBuildingManager.Instance.SetBuildingFilter(code);
            UpdateBuildingTabHighlights();
            RefreshPinWidgets();
            RedrawDynamicOverlay();
        });
    }

    private void UpdateBuildingTabHighlights()
    {
        for (int i = 0; i < buildingTabButtons.Count; i++)
        {
            UnityEngine.UI.Button b = buildingTabButtons[i];
            if (b == null) continue;
            bool active = buildingTabCodes[i] == selectedBuilding;
            var img = b.GetComponent<UnityEngine.UI.Image>();
            if (img != null)
                img.color = active ? new Color(0.22f, 0.58f, 0.28f, 1f) : new Color(0.14f, 0.22f, 0.16f, 0.95f);
        }
    }

    private void UpdatePwdButtonVisual()
    {
        bool pwd = pathVisualizer != null && pathVisualizer.PwdAccessibleMode;
        if (pwdBtnText != null) pwdBtnText.text = pwd ? "PWD: ON" : "PWD: OFF";
        if (pwdBtnImage != null)
            pwdBtnImage.color = pwd ? new Color(0.12f, 0.62f, 0.36f, 1f) : new Color(0.14f, 0.20f, 0.26f, 0.95f);
    }

    public void BakeBaseNavMeshFloorplan()
    {
        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        int size = Mathf.Clamp(mapTextureSize, 256, 1024);

        if (baseFloorTexture == null || baseFloorTexture.width != size)
        {
            baseFloorTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            dynamicMapTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            basePixels = new Color32[size * size];
            workingPixels = new Color32[size * size];
        }

        Color32 bg32 = mapBgColor;
        for (int i = 0; i < basePixels.Length; i++)
            basePixels[i] = bg32;

        if (tri.vertices == null || tri.vertices.Length < 3 || tri.indices == null || tri.indices.Length < 3)
        {
            hasValidBounds = false;
            baseFloorTexture.SetPixels32(basePixels);
            baseFloorTexture.Apply();
            if (mapRawImage != null) mapRawImage.texture = dynamicMapTexture;
            return;
        }

        Vector3 min = tri.vertices[0];
        Vector3 max = tri.vertices[0];
        for (int i = 1; i < tri.vertices.Length; i++)
        {
            min = Vector3.Min(min, tri.vertices[i]);
            max = Vector3.Max(max, tri.vertices[i]);
        }

        // Include destination targets in bounds so pins never clip off the edge
        if (destinationManager != null && destinationManager.targets != null)
        {
            foreach (Transform t in destinationManager.targets)
            {
                if (t == null) continue;
                min = Vector3.Min(min, t.position);
                max = Vector3.Max(max, t.position);
            }
        }

        min -= new Vector3(worldPaddingMeters, 0f, worldPaddingMeters);
        max += new Vector3(worldPaddingMeters, 0f, worldPaddingMeters);
        navBounds = new Bounds((min + max) * 0.5f, max - min);
        hasValidBounds = true;

        int stairsArea = NavMesh.GetAreaFromName("Stairs");
        int rampArea = NavMesh.GetAreaFromName("Ramp");
        int elevArea = NavMesh.GetAreaFromName("Elevator");

        Color32 walkCol = walkableFloorColor;
        Color32 stairCol = stairsAreaColor;
        Color32 rampCol = rampElevatorColor;
        Color32 edgeCol = floorBorderColor;

        int triCount = tri.indices.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            Vector3 v0 = tri.vertices[tri.indices[t * 3 + 0]];
            Vector3 v1 = tri.vertices[tri.indices[t * 3 + 1]];
            Vector3 v2 = tri.vertices[tri.indices[t * 3 + 2]];

            int area = (tri.areas != null && t < tri.areas.Length) ? tri.areas[t] : 0;
            Color32 fill = walkCol;
            if (area == stairsArea && stairsArea >= 0) fill = stairCol;
            else if ((area == rampArea && rampArea >= 0) || (area == elevArea && elevArea >= 0)) fill = rampCol;

            Vector2Int p0 = WorldToPixel(v0, size);
            Vector2Int p1 = WorldToPixel(v1, size);
            Vector2Int p2 = WorldToPixel(v2, size);

            FillTriangle(basePixels, size, p0, p1, p2, fill);
        }

        // Draw subtle grid lines every 5 meters for scale clarity
        Color32 gridCol = new Color32(35, 58, 45, 255);
        float startX = Mathf.Ceil(navBounds.min.x / 5f) * 5f;
        for (float gx = startX; gx <= navBounds.max.x; gx += 5f)
        {
            Vector2Int a = WorldToPixel(new Vector3(gx, 0f, navBounds.min.z), size);
            Vector2Int b = WorldToPixel(new Vector3(gx, 0f, navBounds.max.z), size);
            DrawLine(basePixels, size, a, b, gridCol, 1);
        }
        float startZ = Mathf.Ceil(navBounds.min.z / 5f) * 5f;
        for (float gz = startZ; gz <= navBounds.max.z; gz += 5f)
        {
            Vector2Int a = WorldToPixel(new Vector3(navBounds.min.x, 0f, gz), size);
            Vector2Int b = WorldToPixel(new Vector3(navBounds.max.x, 0f, gz), size);
            DrawLine(basePixels, size, a, b, gridCol, 1);
        }

        // Outline NavMesh triangle edges weakly so corridor structure pops
        for (int t = 0; t < triCount; t++)
        {
            Vector2Int p0 = WorldToPixel(tri.vertices[tri.indices[t * 3 + 0]], size);
            Vector2Int p1 = WorldToPixel(tri.vertices[tri.indices[t * 3 + 1]], size);
            Vector2Int p2 = WorldToPixel(tri.vertices[tri.indices[t * 3 + 2]], size);
            DrawLine(basePixels, size, p0, p1, edgeCol, 1);
            DrawLine(basePixels, size, p1, p2, edgeCol, 1);
            DrawLine(basePixels, size, p2, p0, edgeCol, 1);
        }

        baseFloorTexture.SetPixels32(basePixels);
        baseFloorTexture.Apply();
        if (mapRawImage != null)
            mapRawImage.texture = dynamicMapTexture;
    }

    public void RefreshPinWidgets()
    {
        if (pinOverlayContainer == null) return;
        for (int i = 0; i < pinWidgets.Count; i++)
        {
            if (pinWidgets[i].rect != null)
                Destroy(pinWidgets[i].rect.gameObject);
        }
        pinWidgets.Clear();

        if (destinationManager == null || destinationManager.targets == null || !hasValidBounds)
            return;

        for (int i = 0; i < destinationManager.targets.Count; i++)
        {
            Transform t = destinationManager.targets[i];
            if (t == null) continue;

            CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(t);
            if (selectedBuilding != CampusBuildingCode.All && info.building != selectedBuilding)
                continue;

            int capturedIdx = i;
            UnityEngine.UI.Button pinBtn = CreateStyledButton(
                pinOverlayContainer,
                "MapPin_" + info.roomCode,
                info.roomCode,
                Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(124f, 48f),
                new Color(0.14f, 0.38f, 0.20f, 0.95f),
                21f);

            RectTransform rt = pinBtn.GetComponent<RectTransform>();
            Vector2 uv = WorldToNormalizedUV(t.position);
            rt.anchorMin = uv;
            rt.anchorMax = uv;
            rt.anchoredPosition = Vector2.zero;

            pinBtn.onClick.AddListener(() =>
            {
                destinationManager.SelectDestination(capturedIdx);
                RedrawDynamicOverlay();
            });

            pinWidgets.Add(new PinWidget
            {
                target = t,
                info = info,
                rect = rt,
                bg = pinBtn.GetComponent<UnityEngine.UI.Image>(),
                label = pinBtn.GetComponentInChildren<TMP_Text>(true)
            });
        }
    }

    private void Update()
    {
        if (!gameObject.activeInHierarchy) return;
        if (Time.unscaledTime < nextRedrawTime) return;
        nextRedrawTime = Time.unscaledTime + 0.2f;
        RedrawDynamicOverlay();
    }

    public void RedrawDynamicOverlay()
    {
        if (!uiBuilt || dynamicMapTexture == null || basePixels == null)
            return;

        Array.Copy(basePixels, workingPixels, basePixels.Length);
        int size = dynamicMapTexture.width;

        bool pwd = pathVisualizer != null && pathVisualizer.PwdAccessibleMode;
        UpdatePwdButtonVisual();

        if (headerSubtitleText != null)
        {
            if ( selectedBuilding != CampusBuildingCode.All &&
                 CampusBuildingManager.Instance != null &&
                 !CampusBuildingManager.Instance.HasLoadedModelForBuilding(selectedBuilding))
            {
                headerSubtitleText.text = $"{CampusPoiMetadata.GetBuildingShortCode(selectedBuilding)} \u00B7 {CampusPoiMetadata.GetBuildingFullName(selectedBuilding)} (3D Model Standby)";
            }
            else if (CampusBuildingManager.Instance != null)
            {
                headerSubtitleText.text = CampusBuildingManager.Instance.GetActiveFloorBadgeText();
            }
        }

        if (hasValidBounds && pathVisualizer != null)
        {
            // Draw active NavMesh route polyline
            IReadOnlyList<Vector3> corners = pathVisualizer.CurrentPathCorners;
            if (corners != null && corners.Count >= 2)
            {
                Color32 routeCol = pwd
                    ? new Color32(45, 245, 130, 255)
                    : new Color32(0, 240, 255, 255);
                for (int i = 0; i < corners.Count - 1; i++)
                {
                    Vector2Int a = WorldToPixel(corners[i], size);
                    Vector2Int b = WorldToPixel(corners[i + 1], size);
                    DrawLine(workingPixels, size, a, b, routeCol, 3);
                }
            }

            // Draw live user position & heading direction line
            Transform user = pathVisualizer.userTransform;
            if (user != null)
            {
                Vector2Int uPx = WorldToPixel(user.position, size);
                Vector3 fwd = Vector3.ProjectOnPlane(user.forward, Vector3.up);
                if (fwd.sqrMagnitude > 1e-4f)
                {
                    Vector2Int tipPx = WorldToPixel(user.position + fwd.normalized * 2.8f, size);
                    DrawLine(workingPixels, size, uPx, tipPx, new Color32(255, 230, 60, 255), 2);
                }
                DrawDisc(workingPixels, size, uPx, 7, new Color32(255, 255, 255, 255));
                DrawDisc(workingPixels, size, uPx, 5, new Color32(30, 140, 255, 255));
            }
        }

        dynamicMapTexture.SetPixels32(workingPixels);
        dynamicMapTexture.Apply();

        // Update room pin highlights & footer info
        Transform activeTarget = pathVisualizer != null ? pathVisualizer.CurrentDestination : null;
        for (int i = 0; i < pinWidgets.Count; i++)
        {
            PinWidget pw = pinWidgets[i];
            if (pw.bg == null) continue;
            bool isSel = pw.target == activeTarget;
            pw.bg.color = isSel
                ? (pwd ? new Color(0.12f, 0.72f, 0.38f, 1f) : new Color(0.05f, 0.65f, 0.78f, 1f))
                : new Color(0.14f, 0.36f, 0.19f, 0.92f);
        }

        if (footerStatusText != null)
        {
            if (selectedBuilding != CampusBuildingCode.All &&
                CampusBuildingManager.Instance != null &&
                !CampusBuildingManager.Instance.HasLoadedModelForBuilding(selectedBuilding))
            {
                footerStatusText.text = $"{CampusPoiMetadata.GetBuildingFullName(selectedBuilding)} ({CampusPoiMetadata.GetBuildingShortCode(selectedBuilding)}) model is being prepared. Switch to M (St. Miguel Hall) to test live routes.";
            }
            else if (activeTarget != null && pathVisualizer != null)
            {
                CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(activeTarget);
                string modeStr = pwd ? "PWD Accessible Route" : "Standard Route";
                footerStatusText.text = $"Selected: <b>{info.FormattedButtonLabel}</b> ({info.BuildingFullName} {info.FloorLabel})\n{modeStr} \u00B7 Distance: {pathVisualizer.DistanceRemaining:0.0} m";
            }
            else
            {
                footerStatusText.text = "Tap any room pin on the floorplan to preview its route. Legend: Cyan = Standard Route, Green = PWD Route, Blue Dot = You.";
            }
        }
    }

    private Vector2 WorldToNormalizedUV(Vector3 worldPos)
    {
        if (!hasValidBounds) return new Vector2(0.5f, 0.5f);
        float spanX = Mathf.Max(1f, navBounds.size.x);
        float spanZ = Mathf.Max(1f, navBounds.size.z);
        float maxSpan = Mathf.Max(spanX, spanZ);

        float u = 0.5f + (worldPos.x - navBounds.center.x) / maxSpan;
        float v = 0.5f + (worldPos.z - navBounds.center.z) / maxSpan;
        return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
    }

    private Vector2Int WorldToPixel(Vector3 worldPos, int texSize)
    {
        Vector2 uv = WorldToNormalizedUV(worldPos);
        int px = Mathf.Clamp(Mathf.RoundToInt(uv.x * (texSize - 1)), 0, texSize - 1);
        int py = Mathf.Clamp(Mathf.RoundToInt(uv.y * (texSize - 1)), 0, texSize - 1);
        return new Vector2Int(px, py);
    }

    private static void FillTriangle(Color32[] buf, int size, Vector2Int v0, Vector2Int v1, Vector2Int v2, Color32 col)
    {
        int minX = Mathf.Max(0, Mathf.Min(v0.x, Mathf.Min(v1.x, v2.x)));
        int maxX = Mathf.Min(size - 1, Mathf.Max(v0.x, Mathf.Max(v1.x, v2.x)));
        int minY = Mathf.Max(0, Mathf.Min(v0.y, Mathf.Min(v1.y, v2.y)));
        int maxY = Mathf.Min(size - 1, Mathf.Max(v0.y, Mathf.Max(v1.y, v2.y)));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2Int p = new Vector2Int(x, y);
                int w0 = EdgeCross(v1, v2, p);
                int w1 = EdgeCross(v2, v0, p);
                int w2 = EdgeCross(v0, v1, p);
                if ((w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0))
                    buf[y * size + x] = col;
            }
        }
    }

    private static int EdgeCross(Vector2Int a, Vector2Int b, Vector2Int c)
    {
        return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }

    private static void DrawLine(Color32[] buf, int size, Vector2Int a, Vector2Int b, Color32 col, int radius)
    {
        int dx = Mathf.Abs(b.x - a.x);
        int dy = Mathf.Abs(b.y - a.y);
        int steps = Mathf.Max(1, Mathf.Max(dx, dy));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = Mathf.RoundToInt(Mathf.Lerp(a.x, b.x, t));
            int y = Mathf.RoundToInt(Mathf.Lerp(a.y, b.y, t));
            DrawDisc(buf, size, new Vector2Int(x, y), radius, col);
        }
    }

    private static void DrawDisc(Color32[] buf, int size, Vector2Int center, int radius, Color32 col)
    {
        int rSq = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            int y = center.y + dy;
            if (y < 0 || y >= size) continue;
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > rSq) continue;
                int x = center.x + dx;
                if (x < 0 || x >= size) continue;
                buf[y * size + x] = col;
            }
        }
    }

    private static RectTransform CreatePanel(
        RectTransform parent,
        string name,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 anchoredPos,
        Vector2 sizeDelta,
        Color bgColor)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = sizeDelta;
        var img = go.GetComponent<UnityEngine.UI.Image>();
        img.color = bgColor;
        return rt;
    }

    private static TMP_Text CreateLabel(
        RectTransform parent,
        string name,
        string text,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 anchoredPos,
        Vector2 sizeDelta,
        float fontSize,
        FontStyles style,
        TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = sizeDelta;

        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.alignment = align;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        return tmp;
    }

    private static UnityEngine.UI.Button CreateStyledButton(
        RectTransform parent,
        string name,
        string labelText,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 anchoredPos,
        Vector2 sizeDelta,
        Color bgColor,
        float fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = sizeDelta;

        var img = go.GetComponent<UnityEngine.UI.Image>();
        img.color = bgColor;

        var btn = go.GetComponent<UnityEngine.UI.Button>();
        btn.targetGraphic = img;

        CreateLabel(rt, "Label", labelText, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, new Vector2(-12f, -6f), fontSize, FontStyles.Bold, TextAlignmentOptions.Center);
        return btn;
    }
}
