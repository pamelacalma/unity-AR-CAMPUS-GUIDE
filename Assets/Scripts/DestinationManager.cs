using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Implements the AR Campus Guide v2 Figma Wireframe Room Search Bottom Sheet (Screen 2)
/// while keeping full compatibility with scene destination targets and building codes:
///   M  = St. Miguel Hall
///   SJ = St. Joseph Hall
///   GK = Gokongwei Hall
/// </summary>
public class DestinationManager : MonoBehaviour
{
    [Header("Path Visualizer Reference")]
    public PathVisualizer pathVisualizer;

    [Header("Target Objects in Room")]
    public List<Transform> targets = new List<Transform>();

    [Header("Optional: legacy auto-generate buttons")]
    [SerializeField] private RectTransform buttonContainer = null;
    [SerializeField] private UnityEngine.UI.Button buttonPrefab = null;
    [SerializeField] private bool autoDiscoverBottomButton = true;

    [Header("Figma v2 Bottom Sheet Colors")]
    [SerializeField] private Color normalButtonColor = new Color(0.31f, 0.58f, 0.35f, 0.96f);   // #4F9459 Figma green
    [SerializeField] private Color selectedButtonColor = new Color(0.22f, 0.74f, 0.34f, 1.00f);
    [SerializeField] private Color buttonTextColor = Color.white;

    private class RoomCatalogEntry
    {
        public string code;
        public string displayLabel;
        public string searchKeywords;
        public CampusBuildingCode building;
        public int targetIndex = -1;
        public Transform targetTransform;
    }

    private class DrawerSection
    {
        public GameObject sectionRoot;
        public RectTransform gridRoot;
        public readonly List<RoomButtonBinding> buttons = new List<RoomButtonBinding>();
    }

    private struct RoomButtonBinding
    {
        public RoomCatalogEntry entry;
        public UnityEngine.UI.Button button;
        public UnityEngine.UI.Image image;
    }

    private readonly List<RoomCatalogEntry> allCatalogEntries = new List<RoomCatalogEntry>();
    private readonly List<string> recentCodes = new List<string> { "M101", "GK404", "GK102", "SJ203" };

    [Header("Optional 2D Map Preview")]
    [Tooltip("Keep disabled for the clean Figma room-select sheet over live AR; re-enable later to restore the top 2D map preview.")]
    [SerializeField] private bool showTopMapWhenDrawerOpen = false;

    private GameObject drawerRoot;
    private GameObject topMapCardRoot;
    private UnityEngine.UI.RawImage topMapRawImage;
    private TMP_Text topMapBuildingLabel;
    private Map2DController cachedMapController;
    private float nextMapRefreshTime;
    private TMP_InputField drawerSearchInput;
    private DrawerSection recentSection;
    private readonly Dictionary<CampusBuildingCode, DrawerSection> buildingSections = new Dictionary<CampusBuildingCode, DrawerSection>();

    private static Sprite cachedRoundedCardSprite;
    private static Sprite cachedSearchIconSprite;

    public int SelectedIndex { get; private set; } = -1;
    public string SelectedRoomCode { get; private set; } = "";
    public bool IsDrawerOpen { get; private set; } = true;
    public CampusBuildingCode ActiveBuildingFilter { get; private set; } = CampusBuildingCode.All;
    public string ActiveSearchQuery { get; private set; } = "";

    public event Action<Transform> DestinationChanged;

    private void Start()
    {
        LoadRecentRooms();
        HideLegacyBottomPlaceholderPanel();
        BuildRoomCatalog();
        BuildFigmaBottomSheetDrawer();
        // Auto-open the Apple Maps / Google Maps style Map + Destination Selection sheet after loading
        SetDrawerOpen(true);
    }

    private void Update()
    {
        if (!IsDrawerOpen || !showTopMapWhenDrawerOpen || topMapCardRoot == null || !topMapCardRoot.activeInHierarchy)
            return;

        if (Time.unscaledTime < nextMapRefreshTime)
            return;
        nextMapRefreshTime = Time.unscaledTime + 0.25f;

        RefreshTopMapPreview();
    }

    public static string LabelFor(Transform t)
    {
        if (t == null) return "";
        CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(t);
        if (!string.IsNullOrEmpty(info.roomCode))
        {
            if (info.category == CampusPoiCategory.Restroom)
                return $"{info.BuildingShortCode} \u00B7 {info.displayName}";
            return info.roomCode;
        }

        string n = t.name;
        if (n.StartsWith("Destination_", StringComparison.OrdinalIgnoreCase))
            n = n.Substring("Destination_".Length);
        return n.Replace('_', ' ');
    }

    public void SetDrawerOpen(bool open)
    {
        IsDrawerOpen = open;
        if (drawerRoot != null && drawerRoot.activeSelf != open)
            drawerRoot.SetActive(open);
        if (topMapCardRoot != null)
            topMapCardRoot.SetActive(open && showTopMapWhenDrawerOpen);

        if (open)
        {
            RefreshRecentSectionButtons();
            ApplySearchFilter(ActiveSearchQuery);
            if (showTopMapWhenDrawerOpen)
                RefreshTopMapPreview();
        }
    }

    private void RefreshTopMapPreview()
    {
        if (topMapRawImage == null) return;
        if (cachedMapController == null)
            cachedMapController = FindAnyObjectByType<Map2DController>(FindObjectsInactive.Include);
        if (cachedMapController != null)
        {
            Texture2D tex = cachedMapController.GetLiveMapTexture();
            if (tex != null) topMapRawImage.texture = tex;
        }
        if (topMapBuildingLabel != null && CampusBuildingManager.Instance != null)
            topMapBuildingLabel.text = CampusBuildingManager.Instance.GetActiveFloorBadgeText();
    }

    public void ToggleDrawer() => SetDrawerOpen(!IsDrawerOpen);

    public void SelectDestination(int targetIndex)
    {
        if (pathVisualizer == null) return;
        if (targetIndex < 0 || targetIndex >= targets.Count) return;

        SelectedIndex = targetIndex;
        Transform t = targets[targetIndex];
        SelectedRoomCode = LabelFor(t);
        RecordRecentRoom(SelectedRoomCode);

        pathVisualizer.SetDestination(t);
        UpdateAllButtonHighlights();
        SetDrawerOpen(false);
        DestinationChanged?.Invoke(t);
    }

    public void SetDestinationByTransform(Transform newTarget)
    {
        if (pathVisualizer == null) return;
        SelectedIndex = targets.IndexOf(newTarget);
        SelectedRoomCode = LabelFor(newTarget);
        if (!string.IsNullOrEmpty(SelectedRoomCode))
            RecordRecentRoom(SelectedRoomCode);

        pathVisualizer.SetDestination(newTarget);
        UpdateAllButtonHighlights();
        SetDrawerOpen(false);
        DestinationChanged?.Invoke(newTarget);
    }

    public void ClearDestination()
    {
        SelectedIndex = -1;
        SelectedRoomCode = "";
        if (pathVisualizer != null) pathVisualizer.SetDestination(null);
        UpdateAllButtonHighlights();
        DestinationChanged?.Invoke(null);
    }

    public void SetBuildingFilter(CampusBuildingCode buildingCode)
    {
        ActiveBuildingFilter = buildingCode;
        if (CampusBuildingManager.Instance != null)
            CampusBuildingManager.Instance.SetBuildingFilter(buildingCode);
        ApplySearchFilter(ActiveSearchQuery);
    }

    private void SelectCatalogEntry(RoomCatalogEntry entry)
    {
        if (entry == null) return;

        SelectedRoomCode = entry.code;
        RecordRecentRoom(entry.code);

        if (entry.targetIndex >= 0 && entry.targetIndex < targets.Count && targets[entry.targetIndex] != null)
        {
            SelectedIndex = entry.targetIndex;
            if (pathVisualizer != null)
                pathVisualizer.SetDestination(targets[entry.targetIndex]);
            UpdateAllButtonHighlights();
            SetDrawerOpen(false);
            DestinationChanged?.Invoke(targets[entry.targetIndex]);
            return;
        }

        // For catalog rooms from upcoming 3D models (e.g. GK101..GK110, SJ203), fall back
        // to the first available scene target so navigation preview still works smoothly.
        if (targets.Count > 0 && targets[0] != null)
        {
            SelectedIndex = 0;
            if (pathVisualizer != null)
                pathVisualizer.SetDestination(targets[0]);
            UpdateAllButtonHighlights();
            SetDrawerOpen(false);
            DestinationChanged?.Invoke(targets[0]);
        }
    }

    private void HideLegacyBottomPlaceholderPanel()
    {
        if (!autoDiscoverBottomButton && buttonContainer != null)
            return;
        UIManager ui = FindAnyObjectByType<UIManager>();
        if (ui != null && ui.arNavPanel != null)
        {
            Transform legacyBottom = ui.arNavPanel.transform.Find("BottomPanel");
            if (legacyBottom != null)
                legacyBottom.gameObject.SetActive(false);

            Transform legacyTop = ui.arNavPanel.transform.Find("TopPanel");
            if (legacyTop != null)
                legacyTop.gameObject.SetActive(false);
        }
    }

    private void BuildRoomCatalog()
    {
        allCatalogEntries.Clear();
        var addedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Add all real 3D destination targets currently in the scene (e.g. M101, M102, M Men CR)
        for (int i = 0; i < targets.Count; i++)
        {
            Transform t = targets[i];
            if (t == null) continue;
            CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(t);

            string buttonCode = info.category == CampusPoiCategory.Restroom
                ? $"M1F {info.displayName}"
                : (!string.IsNullOrEmpty(info.roomCode) ? info.roomCode : LabelFor(t));

            allCatalogEntries.Add(new RoomCatalogEntry
            {
                code = buttonCode,
                displayLabel = buttonCode,
                searchKeywords = $"{buttonCode} {info.roomCode} {info.displayName} {info.BuildingShortCode} {info.BuildingFullName}",
                building = info.building,
                targetIndex = i,
                targetTransform = t
            });
            addedCodes.Add(buttonCode);
        }

        // 2. Ensure St. Miguel Hall (M) has a full 2-column grid matching the Figma layout
        string[] miguelDefaults = { "M101", "M102", "M103", "M104", "M105", "M106" };
        foreach (string mc in miguelDefaults)
        {
            if (addedCodes.Contains(mc)) continue;
            allCatalogEntries.Add(new RoomCatalogEntry
            {
                code = mc,
                displayLabel = mc,
                searchKeywords = $"{mc} M St. Miguel Hall Room {mc.Substring(1)}",
                building = CampusBuildingCode.M,
                targetIndex = targets.Count > 0 ? 0 : -1
            });
            addedCodes.Add(mc);
        }

        // 3. Gokongwei Hall (GK) rooms from Figma Screen 2 (GK101..GK110, GK404)
        string[] gkDefaults = { "GK101", "GK102", "GK103", "GK104", "GK105", "GK106", "GK107", "GK108", "GK109", "GK110", "GK404" };
        foreach (string gc in gkDefaults)
        {
            if (addedCodes.Contains(gc)) continue;
            allCatalogEntries.Add(new RoomCatalogEntry
            {
                code = gc,
                displayLabel = gc,
                searchKeywords = $"{gc} GK Gokongwei Hall Room {gc.Substring(2)} Computer Lab",
                building = CampusBuildingCode.GK,
                targetIndex = -1
            });
            addedCodes.Add(gc);
        }

        // 4. St. Joseph Hall (SJ) rooms from Figma Screen 2 (SJ101..SJ106, SJ203)
        string[] sjDefaults = { "SJ101", "SJ102", "SJ103", "SJ104", "SJ201", "SJ203" };
        foreach (string sc in sjDefaults)
        {
            if (addedCodes.Contains(sc)) continue;
            allCatalogEntries.Add(new RoomCatalogEntry
            {
                code = sc,
                displayLabel = sc,
                searchKeywords = $"{sc} SJ St. Joseph Hall Room {sc.Substring(2)} Chem Lab",
                building = CampusBuildingCode.SJ,
                targetIndex = -1
            });
            addedCodes.Add(sc);
        }
    }

    private void BuildFigmaBottomSheetDrawer()
    {
        UIManager ui = FindAnyObjectByType<UIManager>();
        Transform parent = ui != null && ui.arNavPanel != null
            ? ui.arNavPanel.transform
            : (buttonContainer != null ? buttonContainer.parent : null);

        if (parent == null)
        {
            Canvas c = FindAnyObjectByType<Canvas>();
            if (c != null) parent = c.transform;
        }
        if (parent == null) return;

        // Frosted dark translucent Bottom Sheet (Figma Screen 2: lower ~78% of screen over live AR)
        drawerRoot = new GameObject(
            "FigmaRoomSearchDrawer",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image));
        drawerRoot.transform.SetParent(parent, false);

        var drawerRect = drawerRoot.GetComponent<RectTransform>();
        drawerRect.anchorMin = new Vector2(0f, 0f);
        drawerRect.anchorMax = new Vector2(1f, showTopMapWhenDrawerOpen ? 0.74f : 0.78f);
        drawerRect.offsetMin = Vector2.zero;
        drawerRect.offsetMax = Vector2.zero;

        var drawerBg = drawerRoot.GetComponent<UnityEngine.UI.Image>();
        drawerBg.color = new Color(0.18f, 0.19f, 0.18f, 0.88f);

        // Optional 2D Map header above the bottom sheet (disabled by default; ready for future reintegration)
        if (showTopMapWhenDrawerOpen)
            BuildTopMapPreviewCard(parent, ui);

        // 1. Top Horizontal Pill Handle (tapping collapses the drawer)
        var handleBtnGo = new GameObject(
            "DrawerTopHandleButton",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button));
        handleBtnGo.transform.SetParent(drawerRect, false);

        var handleBtnRect = handleBtnGo.GetComponent<RectTransform>();
        handleBtnRect.anchorMin = new Vector2(0.5f, 1f);
        handleBtnRect.anchorMax = new Vector2(0.5f, 1f);
        handleBtnRect.pivot = new Vector2(0.5f, 1f);
        handleBtnRect.anchoredPosition = new Vector2(0f, -12f);
        handleBtnRect.sizeDelta = new Vector2(320f, 60f);

        var handleHitImg = handleBtnGo.GetComponent<UnityEngine.UI.Image>();
        handleHitImg.color = new Color(0f, 0f, 0f, 0.01f);

        var handlePillGo = new GameObject("HandlePill", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        handlePillGo.transform.SetParent(handleBtnRect, false);
        var handlePillRect = handlePillGo.GetComponent<RectTransform>();
        handlePillRect.anchorMin = new Vector2(0.5f, 0.5f);
        handlePillRect.anchorMax = new Vector2(0.5f, 0.5f);
        handlePillRect.pivot = new Vector2(0.5f, 0.5f);
        handlePillRect.anchoredPosition = Vector2.zero;
        handlePillRect.sizeDelta = new Vector2(160f, 24f);

        var handlePillImg = handlePillGo.GetComponent<UnityEngine.UI.Image>();
        handlePillImg.sprite = NavigationHUD.GetRoundedPillSprite();
        handlePillImg.color = new Color(0.76f, 0.78f, 0.77f, 0.92f);
        handlePillImg.raycastTarget = false;

        var handleBtn = handleBtnGo.GetComponent<UnityEngine.UI.Button>();
        handleBtn.targetGraphic = handleHitImg;
        handleBtn.onClick.AddListener(() => SetDrawerOpen(false));

        // 2. Rounded Pill Search Bar ("Search for a room... (e.g. M101, SJ203, GK404)")
        var searchBarGo = new GameObject(
            "FigmaSearchBar",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(TMP_InputField));
        searchBarGo.transform.SetParent(drawerRect, false);

        var searchBarRect = searchBarGo.GetComponent<RectTransform>();
        searchBarRect.anchorMin = new Vector2(0f, 1f);
        searchBarRect.anchorMax = new Vector2(1f, 1f);
        searchBarRect.pivot = new Vector2(0.5f, 1f);
        searchBarRect.anchoredPosition = new Vector2(0f, -84f);
        searchBarRect.sizeDelta = new Vector2(-110f, 82f);

        var searchBarImg = searchBarGo.GetComponent<UnityEngine.UI.Image>();
        searchBarImg.sprite = GetRoundedCardSprite();
        searchBarImg.type = UnityEngine.UI.Image.Type.Sliced;
        searchBarImg.color = new Color(0.62f, 0.64f, 0.63f, 0.78f);

        // Magnifying glass icon on the left
        var iconGo = new GameObject("SearchIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        iconGo.transform.SetParent(searchBarRect, false);
        var iconRect = iconGo.GetComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0f, 0.5f);
        iconRect.anchorMax = new Vector2(0f, 0.5f);
        iconRect.pivot = new Vector2(0f, 0.5f);
        iconRect.anchoredPosition = new Vector2(32f, 0f);
        iconRect.sizeDelta = new Vector2(42f, 42f);

        var iconImg = iconGo.GetComponent<UnityEngine.UI.Image>();
        iconImg.sprite = GetSearchIconSprite();
        iconImg.color = Color.white;
        iconImg.raycastTarget = false;

        // InputField Text Area
        var textAreaGo = new GameObject("TextArea", typeof(RectTransform), typeof(UnityEngine.UI.RectMask2D));
        textAreaGo.transform.SetParent(searchBarRect, false);
        var textAreaRect = textAreaGo.GetComponent<RectTransform>();
        textAreaRect.anchorMin = Vector2.zero;
        textAreaRect.anchorMax = Vector2.one;
        textAreaRect.offsetMin = new Vector2(94f, 10f);
        textAreaRect.offsetMax = new Vector2(-28f, -10f);

        var placeholderGo = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        placeholderGo.transform.SetParent(textAreaRect, false);
        var phRect = placeholderGo.GetComponent<RectTransform>();
        phRect.anchorMin = Vector2.zero;
        phRect.anchorMax = Vector2.one;
        phRect.offsetMin = Vector2.zero;
        phRect.offsetMax = Vector2.zero;

        var phTmp = placeholderGo.GetComponent<TextMeshProUGUI>();
        phTmp.text = "Search for a room... (e.g. M101, SJ203, GK404)";
        phTmp.fontSize = 27f;
        phTmp.fontStyle = FontStyles.Normal;
        phTmp.alignment = TextAlignmentOptions.Left;
        phTmp.color = new Color(0.95f, 0.96f, 0.95f, 0.92f);
        phTmp.textWrappingMode = TextWrappingModes.NoWrap;
        phTmp.raycastTarget = false;

        var inputTextGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        inputTextGo.transform.SetParent(textAreaRect, false);
        var itRect = inputTextGo.GetComponent<RectTransform>();
        itRect.anchorMin = Vector2.zero;
        itRect.anchorMax = Vector2.one;
        itRect.offsetMin = Vector2.zero;
        itRect.offsetMax = Vector2.zero;

        var itTmp = inputTextGo.GetComponent<TextMeshProUGUI>();
        itTmp.fontSize = 28f;
        itTmp.fontStyle = FontStyles.Normal;
        itTmp.alignment = TextAlignmentOptions.Left;
        itTmp.color = Color.white;
        itTmp.textWrappingMode = TextWrappingModes.NoWrap;
        itTmp.raycastTarget = false;

        drawerSearchInput = searchBarGo.GetComponent<TMP_InputField>();
        drawerSearchInput.textViewport = textAreaRect;
        drawerSearchInput.textComponent = itTmp;
        drawerSearchInput.placeholder = phTmp;
        drawerSearchInput.targetGraphic = searchBarImg;
        drawerSearchInput.onValueChanged.AddListener(ApplySearchFilter);

        // 3. Scrollable Content Area for "Recently Searched" & Building Room Grids
        var scrollGo = new GameObject(
            "RoomsScrollView",
            typeof(RectTransform),
            typeof(UnityEngine.UI.ScrollRect),
            typeof(UnityEngine.UI.RectMask2D));
        scrollGo.transform.SetParent(drawerRect, false);

        var scrollRectTr = scrollGo.GetComponent<RectTransform>();
        scrollRectTr.anchorMin = Vector2.zero;
        scrollRectTr.anchorMax = Vector2.one;
        scrollRectTr.offsetMin = new Vector2(44f, 24f);
        scrollRectTr.offsetMax = new Vector2(-44f, -196f);

        var contentGo = new GameObject(
            "Content",
            typeof(RectTransform),
            typeof(VerticalLayoutGroup),
            typeof(ContentSizeFitter));
        contentGo.transform.SetParent(scrollRectTr, false);

        var contentRect = contentGo.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = Vector2.zero;

        var vLayout = contentGo.GetComponent<VerticalLayoutGroup>();
        vLayout.spacing = 28f;
        vLayout.padding = new RectOffset(8, 8, 6, 36);
        vLayout.childControlWidth = true;
        vLayout.childControlHeight = true;
        vLayout.childForceExpandWidth = true;
        vLayout.childForceExpandHeight = false;

        var csf = contentGo.GetComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scroll = scrollGo.GetComponent<UnityEngine.UI.ScrollRect>();
        scroll.viewport = scrollRectTr;
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Elastic;
        scroll.scrollSensitivity = 28f;

        // Build "Recently Searched" section
        recentSection = CreateDrawerSection(contentRect, "Recently Searched");
        RefreshRecentSectionButtons();

        // Build Building Room Sections (St. Miguel Hall M, Gokongwei Hall GK, St. Joseph Hall SJ)
        buildingSections.Clear();
        CreateBuildingSection(contentRect, CampusBuildingCode.M, "St. Miguel Hall Rooms");
        CreateBuildingSection(contentRect, CampusBuildingCode.GK, "Gokongwei Hall Rooms");
        CreateBuildingSection(contentRect, CampusBuildingCode.SJ, "St. Joseph Hall Rooms");

        UpdateAllButtonHighlights();
    }

    private void BuildTopMapPreviewCard(Transform parent, UIManager ui)
    {
        topMapCardRoot = new GameObject(
            "AppleMapsTopMapViewport",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image));
        topMapCardRoot.transform.SetParent(parent, false);

        var cardRect = topMapCardRoot.GetComponent<RectTransform>();
        cardRect.anchorMin = new Vector2(0f, 0.74f);
        cardRect.anchorMax = new Vector2(1f, 1f);
        cardRect.offsetMin = Vector2.zero;
        cardRect.offsetMax = Vector2.zero;

        var cardBg = topMapCardRoot.GetComponent<UnityEngine.UI.Image>();
        cardBg.color = new Color(0.06f, 0.10f, 0.08f, 0.96f);

        var rawGo = new GameObject(
            "LiveMiniMapRawImage",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.RawImage),
            typeof(UnityEngine.UI.Button));
        rawGo.transform.SetParent(cardRect, false);

        var rawRect = rawGo.GetComponent<RectTransform>();
        rawRect.anchorMin = Vector2.zero;
        rawRect.anchorMax = Vector2.one;
        rawRect.offsetMin = new Vector2(12f, 10f);
        rawRect.offsetMax = new Vector2(-12f, -10f);

        topMapRawImage = rawGo.GetComponent<UnityEngine.UI.RawImage>();

        // Tapping the top 2D map expands into the full-screen 2D Floorplan Map
        var mapBtn = rawGo.GetComponent<UnityEngine.UI.Button>();
        mapBtn.targetGraphic = topMapRawImage;
        mapBtn.onClick.AddListener(() =>
        {
            if (ui == null) ui = FindAnyObjectByType<UIManager>();
            if (ui != null) ui.Show2DMap();
        });

        // Building / Map title pill in top-right of the map preview
        var pillGo = new GameObject(
            "ExpandMapPill",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button));
        pillGo.transform.SetParent(cardRect, false);

        var pillRect = pillGo.GetComponent<RectTransform>();
        pillRect.anchorMin = new Vector2(1f, 1f);
        pillRect.anchorMax = new Vector2(1f, 1f);
        pillRect.pivot = new Vector2(1f, 1f);
        pillRect.anchoredPosition = new Vector2(-24f, -38f);
        pillRect.sizeDelta = new Vector2(280f, 64f);

        var pillImg = pillGo.GetComponent<UnityEngine.UI.Image>();
        pillImg.sprite = GetRoundedCardSprite();
        pillImg.type = UnityEngine.UI.Image.Type.Sliced;
        pillImg.color = new Color(0.14f, 0.36f, 0.19f, 0.94f);

        var pillBtn = pillGo.GetComponent<UnityEngine.UI.Button>();
        pillBtn.targetGraphic = pillImg;
        pillBtn.onClick.AddListener(() =>
        {
            if (ui == null) ui = FindAnyObjectByType<UIManager>();
            if (ui != null) ui.Show2DMap();
        });

        var lblGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        lblGo.transform.SetParent(pillRect, false);
        var lrt = lblGo.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = new Vector2(10f, 4f);
        lrt.offsetMax = new Vector2(-10f, -4f);

        topMapBuildingLabel = lblGo.GetComponent<TextMeshProUGUI>();
        topMapBuildingLabel.text = "Full 2D Map \u2197";
        topMapBuildingLabel.fontSize = 23f;
        topMapBuildingLabel.fontStyle = FontStyles.Bold;
        topMapBuildingLabel.alignment = TextAlignmentOptions.Center;
        topMapBuildingLabel.color = Color.white;
        topMapBuildingLabel.raycastTarget = false;
    }

    private DrawerSection CreateDrawerSection(RectTransform contentParent, string titleText)
    {
        var secGo = new GameObject(
            "Section_" + titleText,
            typeof(RectTransform),
            typeof(VerticalLayoutGroup));
        secGo.transform.SetParent(contentParent, false);

        var secLayout = secGo.GetComponent<VerticalLayoutGroup>();
        secLayout.spacing = 14f;
        secLayout.childControlWidth = true;
        secLayout.childControlHeight = true;
        secLayout.childForceExpandWidth = true;
        secLayout.childForceExpandHeight = false;

        // Section Header Label
        var headerGo = new GameObject("Header", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(LayoutElement));
        headerGo.transform.SetParent(secGo.transform, false);
        var headerLe = headerGo.GetComponent<LayoutElement>();
        headerLe.preferredHeight = 38f;

        var headerTmp = headerGo.GetComponent<TextMeshProUGUI>();
        headerTmp.text = titleText;
        headerTmp.fontSize = 27f;
        headerTmp.fontStyle = FontStyles.Normal;
        headerTmp.alignment = TextAlignmentOptions.BottomLeft;
        headerTmp.color = Color.white;
        headerTmp.raycastTarget = false;

        // Thin White Horizontal Underline (Figma Screen 2)
        var lineGo = new GameObject("DividerLine", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image), typeof(LayoutElement));
        lineGo.transform.SetParent(secGo.transform, false);
        var lineLe = lineGo.GetComponent<LayoutElement>();
        lineLe.preferredHeight = 2.5f;
        lineLe.minHeight = 2f;

        var lineImg = lineGo.GetComponent<UnityEngine.UI.Image>();
        lineImg.color = new Color(1f, 1f, 1f, 0.82f);
        lineImg.raycastTarget = false;

        // 2-Column Grid for Rounded Green Room Buttons
        var gridGo = new GameObject("ButtonGrid", typeof(RectTransform), typeof(GridLayoutGroup));
        gridGo.transform.SetParent(secGo.transform, false);

        var grid = gridGo.GetComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(420f, 96f);
        grid.spacing = new Vector2(44f, 28f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 2;
        grid.childAlignment = TextAnchor.UpperCenter;
        grid.padding = new RectOffset(0, 0, 8, 4);

        return new DrawerSection
        {
            sectionRoot = secGo,
            gridRoot = gridGo.GetComponent<RectTransform>()
        };
    }

    private void CreateBuildingSection(RectTransform contentParent, CampusBuildingCode building, string sectionTitle)
    {
        DrawerSection sec = CreateDrawerSection(contentParent, sectionTitle);
        buildingSections[building] = sec;

        foreach (RoomCatalogEntry entry in allCatalogEntries)
        {
            if (entry.building != building) continue;
            RoomButtonBinding binding = CreateFigmaRoomButton(sec.gridRoot, entry);
            sec.buttons.Add(binding);
        }
    }

    private void RefreshRecentSectionButtons()
    {
        if (recentSection == null || recentSection.gridRoot == null) return;

        for (int i = 0; i < recentSection.buttons.Count; i++)
        {
            if (recentSection.buttons[i].button != null)
                Destroy(recentSection.buttons[i].button.gameObject);
        }
        recentSection.buttons.Clear();

        int count = 0;
        foreach (string code in recentCodes)
        {
            if (count >= 4) break;
            RoomCatalogEntry entry = allCatalogEntries.Find(e => string.Equals(e.code, code, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            recentSection.buttons.Add(CreateFigmaRoomButton(recentSection.gridRoot, entry));
            count++;
        }
    }

    private RoomButtonBinding CreateFigmaRoomButton(RectTransform gridParent, RoomCatalogEntry entry)
    {
        var btnGo = new GameObject(
            "RoomBtn_" + entry.code,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button));
        btnGo.transform.SetParent(gridParent, false);

        var img = btnGo.GetComponent<UnityEngine.UI.Image>();
        img.sprite = GetRoundedCardSprite();
        img.type = UnityEngine.UI.Image.Type.Sliced;
        img.color = normalButtonColor;

        var btn = btnGo.GetComponent<UnityEngine.UI.Button>();
        btn.targetGraphic = img;
        RoomCatalogEntry captured = entry;
        btn.onClick.AddListener(() => SelectCatalogEntry(captured));

        var txtGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        txtGo.transform.SetParent(btnGo.transform, false);
        var trt = txtGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(12f, 6f);
        trt.offsetMax = new Vector2(-12f, -6f);

        var tmp = txtGo.GetComponent<TextMeshProUGUI>();
        tmp.text = entry.displayLabel;
        tmp.fontSize = 34f;
        tmp.fontStyle = FontStyles.Normal;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = buttonTextColor;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = 20f;
        tmp.fontSizeMax = 36f;
        tmp.raycastTarget = false;

        return new RoomButtonBinding
        {
            entry = entry,
            button = btn,
            image = img
        };
    }

    private void ApplySearchFilter(string query)
    {
        ActiveSearchQuery = query ?? "";
        string q = ActiveSearchQuery.Trim();

        // Filter Recently Searched (hide Recently Searched while typing a specific search query)
        if (recentSection != null && recentSection.sectionRoot != null)
        {
            bool showRecent = string.IsNullOrEmpty(q) &&
                              ActiveBuildingFilter == CampusBuildingCode.All &&
                              recentSection.buttons.Count > 0;
            recentSection.sectionRoot.SetActive(showRecent);
        }

        // Filter each building section (St. Miguel M, Gokongwei GK, St. Joseph SJ)
        foreach (var kv in buildingSections)
        {
            CampusBuildingCode bCode = kv.Key;
            DrawerSection sec = kv.Value;
            if (sec == null || sec.sectionRoot == null) continue;

            if (ActiveBuildingFilter != CampusBuildingCode.All && ActiveBuildingFilter != bCode)
            {
                sec.sectionRoot.SetActive(false);
                continue;
            }

            int visibleInSec = 0;
            for (int i = 0; i < sec.buttons.Count; i++)
            {
                RoomButtonBinding b = sec.buttons[i];
                if (b.button == null) continue;
                bool match = MatchesCatalogEntry(b.entry, q);
                b.button.gameObject.SetActive(match);
                if (match) visibleInSec++;
            }

            sec.sectionRoot.SetActive(visibleInSec > 0);
        }
    }

    private static bool MatchesCatalogEntry(RoomCatalogEntry entry, string query)
    {
        if (entry == null) return false;
        if (string.IsNullOrWhiteSpace(query)) return true;

        string q = query.Trim();
        string qCompact = q.Replace(" ", "").Replace("-", "").Replace("_", "");

        if (entry.code.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            entry.code.Replace(" ", "").IndexOf(qCompact, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (!string.IsNullOrEmpty(entry.searchKeywords) &&
            entry.searchKeywords.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (CampusPoiMetadata.TryDecodeBuildingQuery(q, out CampusBuildingCode decodedBldg, out _) &&
            decodedBldg == entry.building &&
            (q.Length <= 2 || q.IndexOf("MIGUEL", StringComparison.OrdinalIgnoreCase) >= 0 ||
             q.IndexOf("JOSEPH", StringComparison.OrdinalIgnoreCase) >= 0 ||
             q.IndexOf("GOK", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            return true;
        }

        return false;
    }

    private void UpdateAllButtonHighlights()
    {
        UpdateSectionHighlights(recentSection);
        foreach (var kv in buildingSections)
            UpdateSectionHighlights(kv.Value);
    }

    private void UpdateSectionHighlights(DrawerSection sec)
    {
        if (sec == null) return;
        for (int i = 0; i < sec.buttons.Count; i++)
        {
            RoomButtonBinding b = sec.buttons[i];
            if (b.image == null || b.entry == null) continue;
            bool isSelected = !string.IsNullOrEmpty(SelectedRoomCode) &&
                              string.Equals(b.entry.code, SelectedRoomCode, StringComparison.OrdinalIgnoreCase);
            b.image.color = isSelected ? selectedButtonColor : normalButtonColor;
        }
    }

    private void LoadRecentRooms()
    {
        string saved = PlayerPrefs.GetString("ar_campus_recent_rooms", "");
        if (string.IsNullOrWhiteSpace(saved)) return;

        string[] parts = saved.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        recentCodes.Clear();
        foreach (string p in parts)
        {
            string c = p.Trim();
            if (!string.IsNullOrEmpty(c) && !recentCodes.Contains(c))
                recentCodes.Add(c);
        }
    }

    private void RecordRecentRoom(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        code = code.Trim();
        recentCodes.RemoveAll(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        recentCodes.Insert(0, code);
        while (recentCodes.Count > 4)
            recentCodes.RemoveAt(recentCodes.Count - 1);

        PlayerPrefs.SetString("ar_campus_recent_rooms", string.Join("|", recentCodes));
        PlayerPrefs.Save();
    }

    private static Sprite GetRoundedCardSprite()
    {
        if (cachedRoundedCardSprite != null) return cachedRoundedCardSprite;

        const int size = 64;
        const float radius = 16f;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float cx = Mathf.Clamp(p.x, radius, size - radius);
                float cy = Mathf.Clamp(p.y, radius, size - radius);
                float dist = Vector2.Distance(p, new Vector2(cx, cy));
                float a = Mathf.Clamp01((radius - dist) / 1.25f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }

        tex.SetPixels32(px);
        tex.Apply();
        cachedRoundedCardSprite = Sprite.Create(
            tex,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            new Vector4(18, 18, 18, 18));
        return cachedRoundedCardSprite;
    }

    private static Sprite GetSearchIconSprite()
    {
        if (cachedSearchIconSprite != null) return cachedSearchIconSprite;

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];

        Vector2 ringCenter = new Vector2(0.42f, 0.58f);
        const float ringR = 0.24f;
        const float stroke = 0.075f;
        Vector2 handleStart = new Vector2(0.59f, 0.41f);
        Vector2 handleEnd = new Vector2(0.84f, 0.16f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                float dRing = Mathf.Abs(Vector2.Distance(p, ringCenter) - ringR) - stroke * 0.5f;
                float dHandle = NavigationHUD.DistToSegment(p, handleStart, handleEnd) - stroke * 0.5f;
                float sd = Mathf.Min(dRing, dHandle);
                float a = Mathf.Clamp01(0.5f - sd * size * 0.7f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }

        tex.SetPixels32(px);
        tex.Apply();
        cachedSearchIconSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        return cachedSearchIconSprite;
    }
}
