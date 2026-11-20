using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Implements the AR Campus Guide v2 Figma wireframe HUD (Screens 1, 3, 4, 5):
///  - Top-left circular logo badge (tap: close drawer or return to Main Menu; hold 0.7s or 3-finger tap: toggle DBG overlays)
///  - Top-center frosted white circular direction arrow badge with cyan arrowhead + "Move Forward" / "Turn Around" cue
///  - Bottom-left circular green wheelchair button + "Assisted Mode ON" indicator (PWD semantic routing)
///  - Bottom-center prompt ("Please select a room to\nstart navigation." / "Adjusting navigation ...")
///  - Bottom-center horizontal pill handle to open/collapse the Room Search Bottom Sheet (Screen 2)
/// </summary>
public class NavigationHUD : MonoBehaviour
{
    [SerializeField] private PathVisualizer pathVisualizer;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private float rerouteToastSeconds = 2.5f;

    [Header("HUD Direction Arrow (Figma Screen 1 & 4)")]
    [SerializeField] private bool showDirectionArrow = true;
    [SerializeField] private RectTransform directionArrowRect;
    [SerializeField] private UnityEngine.UI.Image directionArrowImage;
    [SerializeField] private GameObject directionArrowContainer;
    [SerializeField] private TMP_Text turnHintText;
    [SerializeField] private float lookAheadMeters = 2.0f;
    [SerializeField] private float arrowSmoothSpeed = 12f;
    [SerializeField] private float turnAngleThresholdDeg = 35f;
    [SerializeField] private float turnAnnouncementDistance = 6f;
    [SerializeField] private Color arrowNormalColor = new Color(0.18f, 0.90f, 1f, 1f);
    [SerializeField] private Color arrowOffCourseColor = new Color(0.18f, 0.90f, 1f, 1f);

    [Header("Assisted / PWD Accessibility Button (Figma Screen 1 & 4)")]
    [SerializeField] private bool showPwdToggle = true;
    [SerializeField] private UnityEngine.UI.Button pwdToggleButton;
    [SerializeField] private TMP_Text pwdToggleLabel;
    [SerializeField] private Color pwdOffBgColor = new Color(0.36f, 0.62f, 0.43f, 0.94f);
    [SerializeField] private Color pwdOnBgColor = new Color(0.24f, 0.74f, 0.36f, 0.98f);
    [SerializeField] private Color pwdOnRingColor = new Color(0.10f, 0.92f, 0.30f, 1f);

    [Header("Developer Overlays")]
    [SerializeField] private bool startWithDebugOverlays = false;

    public static bool DebugOverlaysVisible { get; set; } = false;

    private static Sprite cachedCircleSprite;
    private static Sprite cachedRingSprite;
    private static Sprite cachedArrowSprite;
    private static Sprite cachedRoundedPillSprite;
    private static Sprite cachedWheelchairSprite;

    private float toastUntil;
    private float nextRefresh;
    private float currentArrowAngle;
    private bool arrowAngleInitialized;
    private bool autoHudInitialized;
    private string latestTurnCue = "";

    private UnityEngine.UI.Image pwdToggleImage;
    private UnityEngine.UI.Image pwdOuterRingImage;
    private bool autoPwdToggleInitialized;
    private bool pwdToggleBound;

    private GameObject figmaChromeRoot;
    private TMP_Text bottomPromptText;
    private UnityEngine.UI.Button bottomHandleButton;
    private UIManager cachedUiManager;
    private DestinationManager cachedDestinationManager;
    private bool threeFingerLatch;

    private void Awake()
    {
        DebugOverlaysVisible = startWithDebugOverlays;
        cachedUiManager = FindAnyObjectByType<UIManager>();
        cachedDestinationManager = FindAnyObjectByType<DestinationManager>();
    }

    private void OnEnable()
    {
        if (pathVisualizer != null)
        {
            pathVisualizer.Deviated += OnDeviated;
            pathVisualizer.AccessibilityModeChanged += OnAccessibilityModeChanged;
        }
    }

    private void OnDisable()
    {
        if (pathVisualizer != null)
        {
            pathVisualizer.Deviated -= OnDeviated;
            pathVisualizer.AccessibilityModeChanged -= OnAccessibilityModeChanged;
        }
        SetArrowVisible(false);
        SetPwdToggleVisible(false);
    }

    private void OnDeviated()
    {
        toastUntil = Time.time + rerouteToastSeconds;
    }

    private void OnAccessibilityModeChanged(bool _)
    {
        nextRefresh = 0f;
        RefreshPwdToggleVisuals();
        RefreshBottomPrompt();
    }

    public void ToggleDebugOverlays()
    {
        DebugOverlaysVisible = !DebugOverlaysVisible;
    }

    private void Update()
    {
        if (pathVisualizer == null) return;

        // 3-finger tap toggles developer overlays without cluttering the Figma UI
        if (Input.touchCount >= 3)
        {
            if (!threeFingerLatch)
            {
                threeFingerLatch = true;
                ToggleDebugOverlays();
            }
        }
        else
        {
            threeFingerLatch = false;
        }

        EnsureFigmaChrome();
        bool drawerOpen = cachedDestinationManager != null && cachedDestinationManager.IsDrawerOpen;
        bool inArView = !UIManager.IsMainMenuActive && !UIManager.Is2DMapActive;

        if (figmaChromeRoot != null && figmaChromeRoot.activeSelf != inArView)
            figmaChromeRoot.SetActive(inArView);

        UpdateDirectionArrow(drawerOpen);
        UpdatePwdToggle(drawerOpen);

        if (Time.time >= nextRefresh)
        {
            nextRefresh = Time.time + 0.15f;
            RefreshBottomPrompt();
        }
    }

    private void RefreshBottomPrompt()
    {
        bool drawerOpen = cachedDestinationManager != null && cachedDestinationManager.IsDrawerOpen;
        bool inArView = !UIManager.IsMainMenuActive && !UIManager.Is2DMapActive;

        if (bottomHandleButton != null)
            bottomHandleButton.gameObject.SetActive(inArView && !drawerOpen);

        if (bottomPromptText == null) return;

        if (!inArView || drawerOpen)
        {
            bottomPromptText.gameObject.SetActive(false);
            return;
        }

        bottomPromptText.gameObject.SetActive(true);

        // Screen 3: "Adjusting navigation ..." when rerouting or off-mesh with a selected destination
        if (Time.time < toastUntil ||
            (pathVisualizer.CurrentDestination != null && pathVisualizer.State == NavState.OffMesh))
        {
            bottomPromptText.text = "Adjusting navigation ...";
            bottomPromptText.color = Color.white;
            if (statusText != null) statusText.text = bottomPromptText.text;
            return;
        }

        // Screen 5: "Please select a room to\nstart navigation." when no destination is chosen
        if (pathVisualizer.State == NavState.NoDestination || pathVisualizer.CurrentDestination == null)
        {
            bottomPromptText.text = "Please select a room to\nstart navigation.";
            bottomPromptText.color = Color.white;
            if (statusText != null) statusText.text = "Please select a room to start navigation.";
            return;
        }

        if (pathVisualizer.State == NavState.NoPath)
        {
            string dest = DestinationManager.LabelFor(pathVisualizer.CurrentDestination);
            bottomPromptText.text = pathVisualizer.PwdAccessibleMode
                ? $"No assisted route to {dest}"
                : $"Adjusting navigation ...";
            bottomPromptText.color = Color.white;
            if (statusText != null) statusText.text = bottomPromptText.text;
            return;
        }

        if (pathVisualizer.State == NavState.Arrived)
        {
            string dest = DestinationManager.LabelFor(pathVisualizer.CurrentDestination);
            bottomPromptText.text = $"Arrived at {dest}";
            bottomPromptText.color = new Color(0.15f, 0.95f, 0.38f, 1f);
            if (statusText != null) statusText.text = bottomPromptText.text;
            return;
        }

        // Screen 4: When Assisted Mode (PWD) is ON, show bright green "Assisted Mode ON"
        if (pathVisualizer.PwdAccessibleMode)
        {
            bottomPromptText.text = "Assisted Mode ON";
            bottomPromptText.color = new Color(0.10f, 0.92f, 0.30f, 1f);
        }
        else
        {
            // Screen 1: clean AR view (no bottom text clutter while actively moving forward)
            bottomPromptText.text = "";
        }

        if (statusText != null)
        {
            string dest = DestinationManager.LabelFor(pathVisualizer.CurrentDestination);
            statusText.text = pathVisualizer.PwdAccessibleMode
                ? "Assisted Mode ON"
                : $"{dest} \u00B7 {pathVisualizer.DistanceRemaining:0} m";
        }
    }

    private void UpdateDirectionArrow(bool drawerOpen)
    {
        if (!showDirectionArrow || UIManager.IsMainMenuActive || UIManager.Is2DMapActive ||
            drawerOpen || Time.time < toastUntil || pathVisualizer.State != NavState.Navigating)
        {
            SetArrowVisible(false);
            arrowAngleInitialized = false;
            latestTurnCue = "";
            if (turnHintText != null) turnHintText.text = "";
            return;
        }

        EnsureHudArrow();
        if (directionArrowRect == null) return;

        if (!pathVisualizer.TryGetNavigationGuidance(
                lookAheadMeters,
                turnAngleThresholdDeg,
                out Vector3 aimPoint,
                out bool hasUpcomingTurn,
                out float distanceToTurn,
                out float turnAngleDeg))
        {
            SetArrowVisible(false);
            return;
        }

        Transform cam = pathVisualizer.userTransform;
        if (cam == null)
        {
            SetArrowVisible(false);
            return;
        }

        SetArrowVisible(true);

        Vector3 camFwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
        if (camFwd.sqrMagnitude < 0.02f)
            camFwd = Vector3.ProjectOnPlane(cam.forward.y < 0f ? cam.up : -cam.up, Vector3.up);
        camFwd = camFwd.sqrMagnitude > 1e-4f ? camFwd.normalized : Vector3.forward;

        Vector3 toAim = Vector3.ProjectOnPlane(aimPoint - cam.position, Vector3.up);
        float bearingDeg = toAim.sqrMagnitude > 1e-4f
            ? Vector3.SignedAngle(camFwd, toAim.normalized, Vector3.up)
            : 0f;

        float targetZ = -bearingDeg;
        if (!arrowAngleInitialized)
        {
            currentArrowAngle = targetZ;
            arrowAngleInitialized = true;
        }
        else
        {
            float t = 1f - Mathf.Exp(-Mathf.Max(1f, arrowSmoothSpeed) * Time.deltaTime);
            currentArrowAngle = Mathf.LerpAngle(currentArrowAngle, targetZ, t);
        }

        directionArrowRect.localRotation = Quaternion.Euler(0f, 0f, currentArrowAngle);

        if (directionArrowImage != null)
        {
            bool offCourse = Mathf.Abs(bearingDeg) > 95f;
            directionArrowImage.color = offCourse ? arrowOffCourseColor : arrowNormalColor;
        }

        latestTurnCue = BuildTurnCue(bearingDeg, hasUpcomingTurn, distanceToTurn, turnAngleDeg);
        if (turnHintText != null) turnHintText.text = latestTurnCue;
    }

    private string BuildTurnCue(float bearingDeg, bool hasUpcomingTurn, float distanceToTurn, float turnAngleDeg)
    {
        if (Mathf.Abs(bearingDeg) >= 135f) return "Turn Around";
        if (Mathf.Abs(bearingDeg) >= 55f) return bearingDeg > 0f ? "Turn Right" : "Turn Left";

        if (hasUpcomingTurn && distanceToTurn <= turnAnnouncementDistance)
        {
            string dir = turnAngleDeg > 0f ? "Right" : "Left";
            return distanceToTurn <= 1.5f ? $"Turn {dir}" : $"Turn {dir} in {distanceToTurn:0} m";
        }
        return "Move Forward";
    }

    private void SetArrowVisible(bool visible)
    {
        GameObject target = directionArrowContainer != null
            ? directionArrowContainer
            : (directionArrowRect != null ? directionArrowRect.gameObject : null);

        if (target != null && target.activeSelf != visible)
            target.SetActive(visible);
    }

    private void UpdatePwdToggle(bool drawerOpen)
    {
        bool hasDestination = pathVisualizer != null && pathVisualizer.CurrentDestination != null;
        bool shouldShow = showPwdToggle && !UIManager.IsMainMenuActive && !UIManager.Is2DMapActive &&
                          !drawerOpen && hasDestination && Time.time >= toastUntil;
        if (!shouldShow)
        {
            SetPwdToggleVisible(false);
            return;
        }

        EnsurePwdToggle();
        SetPwdToggleVisible(true);
        RefreshPwdToggleVisuals();
    }

    private void EnsureFigmaChrome()
    {
        if (figmaChromeRoot != null) return;

        Transform parent = FindHudArrowParent();
        if (parent == null) return;

        if (cachedUiManager == null) cachedUiManager = FindAnyObjectByType<UIManager>();
        if (cachedDestinationManager == null) cachedDestinationManager = FindAnyObjectByType<DestinationManager>();

        figmaChromeRoot = new GameObject("FigmaHudChrome", typeof(RectTransform), typeof(LayoutElement));
        figmaChromeRoot.transform.SetParent(parent, false);
        figmaChromeRoot.GetComponent<LayoutElement>().ignoreLayout = true;

        var rootRect = figmaChromeRoot.GetComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        // 1. Top-Left Circular Logo Badge (present on all 5 Figma screens)
        var logoBadgeGo = new GameObject(
            "TopLeftLogoBadge",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button));
        logoBadgeGo.transform.SetParent(rootRect, false);

        var logoBadgeRect = logoBadgeGo.GetComponent<RectTransform>();
        logoBadgeRect.anchorMin = new Vector2(0f, 1f);
        logoBadgeRect.anchorMax = new Vector2(0f, 1f);
        logoBadgeRect.pivot = new Vector2(0f, 1f);
        logoBadgeRect.anchoredPosition = new Vector2(36f, -42f);
        logoBadgeRect.sizeDelta = new Vector2(136f, 136f);

        var logoBg = logoBadgeGo.GetComponent<UnityEngine.UI.Image>();
        logoBg.sprite = GetCircleSprite();
        logoBg.color = new Color(0.42f, 0.44f, 0.43f, 0.72f);

        Texture logoTex = FindMainMenuLogoTexture();
        if (logoTex != null)
        {
            var rawGo = new GameObject("LogoArt", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.RawImage));
            rawGo.transform.SetParent(logoBadgeRect, false);
            var rawRect = rawGo.GetComponent<RectTransform>();
            rawRect.anchorMin = Vector2.zero;
            rawRect.anchorMax = Vector2.one;
            rawRect.offsetMin = new Vector2(6f, 6f);
            rawRect.offsetMax = new Vector2(-6f, -6f);
            var rawImg = rawGo.GetComponent<UnityEngine.UI.RawImage>();
            rawImg.texture = logoTex;
            rawImg.raycastTarget = false;
        }

        var logoBtn = logoBadgeGo.GetComponent<UnityEngine.UI.Button>();
        logoBtn.targetGraphic = logoBg;
        logoBtn.onClick.AddListener(() =>
        {
            if (cachedDestinationManager == null)
                cachedDestinationManager = FindAnyObjectByType<DestinationManager>();

            if (cachedDestinationManager != null)
            {
                cachedDestinationManager.ToggleDrawer();
                return;
            }

            if (cachedUiManager == null)
                cachedUiManager = FindAnyObjectByType<UIManager>();
            if (cachedUiManager != null)
                cachedUiManager.Show2DMap();
        });

        // 2. Bottom-Center Prompt Text ("Please select a room to\nstart navigation." / "Adjusting navigation ..." / "Assisted Mode ON")
        var promptGo = new GameObject(
            "BottomCenterPrompt",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI),
            typeof(UnityEngine.UI.Button));
        promptGo.transform.SetParent(rootRect, false);

        var promptRect = promptGo.GetComponent<RectTransform>();
        promptRect.anchorMin = new Vector2(0.5f, 0f);
        promptRect.anchorMax = new Vector2(0.5f, 0f);
        promptRect.pivot = new Vector2(0.5f, 0f);
        promptRect.anchoredPosition = new Vector2(0f, 92f);
        promptRect.sizeDelta = new Vector2(820f, 128f);

        bottomPromptText = promptGo.GetComponent<TextMeshProUGUI>();
        bottomPromptText.fontSize = 44f;
        bottomPromptText.fontStyle = FontStyles.Normal;
        bottomPromptText.alignment = TextAlignmentOptions.Center;
        bottomPromptText.color = Color.white;
        bottomPromptText.textWrappingMode = TextWrappingModes.Normal;
        bottomPromptText.text = "Please select a room to\nstart navigation.";

        var promptBtn = promptGo.GetComponent<UnityEngine.UI.Button>();
        promptBtn.targetGraphic = bottomPromptText;
        promptBtn.onClick.AddListener(OpenRoomSearchDrawer);

        // 3. Bottom-Center Pill Handle (opens the Screen 2 Room Search Bottom Sheet)
        var handleTapGo = new GameObject(
            "BottomDrawerHandleHitbox",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button));
        handleTapGo.transform.SetParent(rootRect, false);

        var handleTapRect = handleTapGo.GetComponent<RectTransform>();
        handleTapRect.anchorMin = new Vector2(0.5f, 0f);
        handleTapRect.anchorMax = new Vector2(0.5f, 0f);
        handleTapRect.pivot = new Vector2(0.5f, 0f);
        handleTapRect.anchoredPosition = new Vector2(0f, 12f);
        handleTapRect.sizeDelta = new Vector2(360f, 76f);

        var hitImg = handleTapGo.GetComponent<UnityEngine.UI.Image>();
        hitImg.color = new Color(0f, 0f, 0f, 0.01f);

        var pillVisualGo = new GameObject("HandlePill", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        pillVisualGo.transform.SetParent(handleTapRect, false);
        var pillRect = pillVisualGo.GetComponent<RectTransform>();
        pillRect.anchorMin = new Vector2(0.5f, 0.5f);
        pillRect.anchorMax = new Vector2(0.5f, 0.5f);
        pillRect.pivot = new Vector2(0.5f, 0.5f);
        pillRect.anchoredPosition = new Vector2(0f, -6f);
        pillRect.sizeDelta = new Vector2(160f, 24f);

        var pillImg = pillVisualGo.GetComponent<UnityEngine.UI.Image>();
        pillImg.sprite = GetRoundedPillSprite();
        pillImg.color = new Color(0.76f, 0.78f, 0.77f, 0.92f);
        pillImg.raycastTarget = false;

        bottomHandleButton = handleTapGo.GetComponent<UnityEngine.UI.Button>();
        bottomHandleButton.targetGraphic = hitImg;
        bottomHandleButton.onClick.AddListener(OpenRoomSearchDrawer);
    }

    private void OpenRoomSearchDrawer()
    {
        if (cachedDestinationManager == null)
            cachedDestinationManager = FindAnyObjectByType<DestinationManager>();
        if (cachedDestinationManager != null)
            cachedDestinationManager.SetDrawerOpen(true);
    }

    private static Texture FindMainMenuLogoTexture()
    {
        foreach (UnityEngine.UI.RawImage raw in FindObjectsByType<UnityEngine.UI.RawImage>(FindObjectsInactive.Include))
        {
            if (raw != null && raw.texture != null && raw.texture.name.IndexOf("Logo", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return raw.texture;
        }
        return null;
    }

    private void EnsurePwdToggle()
    {
        if (pwdToggleButton != null)
        {
            if (pwdToggleImage == null)
                pwdToggleImage = pwdToggleButton.GetComponent<UnityEngine.UI.Image>();
            if (!pwdToggleBound)
            {
                pwdToggleButton.onClick.AddListener(OnPwdButtonClicked);
                pwdToggleBound = true;
            }
            return;
        }

        if (autoPwdToggleInitialized) return;
        autoPwdToggleInitialized = true;

        Transform parent = figmaChromeRoot != null ? figmaChromeRoot.transform : FindHudArrowParent();
        if (parent == null) return;

        // Bottom-left circular green wheelchair button (Figma Screens 1 & 4)
        var toggleGo = new GameObject(
            "PwdWheelchairButton",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button),
            typeof(LayoutElement));
        toggleGo.transform.SetParent(parent, false);

        var layout = toggleGo.GetComponent<LayoutElement>();
        layout.ignoreLayout = true;

        var rect = toggleGo.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(40f, 84f);
        rect.sizeDelta = new Vector2(142f, 142f);

        pwdToggleImage = toggleGo.GetComponent<UnityEngine.UI.Image>();
        pwdToggleImage.sprite = GetCircleSprite();
        pwdToggleImage.color = pwdOffBgColor;
        pwdToggleImage.raycastTarget = true;

        pwdToggleButton = toggleGo.GetComponent<UnityEngine.UI.Button>();
        pwdToggleButton.targetGraphic = pwdToggleImage;
        pwdToggleButton.onClick.AddListener(OnPwdButtonClicked);
        pwdToggleBound = true;

        // Outer neon green ring (highlighted when Assisted Mode is ON, soft grey border when OFF)
        var ringGo = new GameObject("OuterRing", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        ringGo.transform.SetParent(rect, false);
        var ringRect = ringGo.GetComponent<RectTransform>();
        ringRect.anchorMin = Vector2.zero;
        ringRect.anchorMax = Vector2.one;
        ringRect.offsetMin = new Vector2(-4f, -4f);
        ringRect.offsetMax = new Vector2(4f, 4f);
        pwdOuterRingImage = ringGo.GetComponent<UnityEngine.UI.Image>();
        pwdOuterRingImage.sprite = GetRingSprite();
        pwdOuterRingImage.raycastTarget = false;

        // Crisp procedural white wheelchair icon inside the button
        var iconGo = new GameObject("WheelchairIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        iconGo.transform.SetParent(rect, false);
        var iconRect = iconGo.GetComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(96f, 96f);

        var iconImg = iconGo.GetComponent<UnityEngine.UI.Image>();
        iconImg.sprite = GetWheelchairSprite();
        iconImg.color = Color.white;
        iconImg.raycastTarget = false;

        RefreshPwdToggleVisuals();
    }

    private void OnPwdButtonClicked()
    {
        if (pathVisualizer != null)
            pathVisualizer.TogglePwdAccessibleMode();
    }

    private void SetPwdToggleVisible(bool visible)
    {
        if (pwdToggleButton != null && pwdToggleButton.gameObject.activeSelf != visible)
            pwdToggleButton.gameObject.SetActive(visible);
    }

    private void RefreshPwdToggleVisuals()
    {
        bool pwd = pathVisualizer != null && pathVisualizer.PwdAccessibleMode;
        if (pwdToggleLabel != null)
            pwdToggleLabel.text = pwd ? "Assisted Mode ON" : "";
        if (pwdToggleImage != null)
            pwdToggleImage.color = pwd ? pwdOnBgColor : pwdOffBgColor;
        if (pwdOuterRingImage != null)
            pwdOuterRingImage.color = pwd
                ? pwdOnRingColor
                : new Color(0.65f, 0.68f, 0.66f, 0.65f);
    }

    private void EnsureHudArrow()
    {
        if (directionArrowRect != null)
        {
            if (directionArrowImage == null)
                directionArrowImage = directionArrowRect.GetComponent<UnityEngine.UI.Image>();
            if (directionArrowContainer == null)
                directionArrowContainer = directionArrowRect.gameObject;
            return;
        }

        if (autoHudInitialized) return;
        autoHudInitialized = true;

        Transform parent = figmaChromeRoot != null ? figmaChromeRoot.transform : FindHudArrowParent();
        if (parent == null) return;

        // Frosted white circular badge at top-center (Figma Screens 1 & 4)
        var badgeGo = new GameObject("NavArrowBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image), typeof(LayoutElement));
        badgeGo.transform.SetParent(parent, false);

        var layout = badgeGo.GetComponent<LayoutElement>();
        layout.ignoreLayout = true;

        var badgeRect = badgeGo.GetComponent<RectTransform>();
        badgeRect.anchorMin = new Vector2(0.5f, 1f);
        badgeRect.anchorMax = new Vector2(0.5f, 1f);
        badgeRect.pivot = new Vector2(0.5f, 1f);
        badgeRect.anchoredPosition = new Vector2(0f, -155f);
        badgeRect.sizeDelta = new Vector2(210f, 210f);

        var bgImage = badgeGo.GetComponent<UnityEngine.UI.Image>();
        bgImage.sprite = GetCircleSprite();
        bgImage.color = new Color(0.92f, 0.94f, 0.95f, 0.78f);
        bgImage.raycastTarget = false;

        var iconGo = new GameObject("NavArrowIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        iconGo.transform.SetParent(badgeRect, false);
        directionArrowRect = iconGo.GetComponent<RectTransform>();
        directionArrowRect.anchorMin = new Vector2(0.5f, 0.5f);
        directionArrowRect.anchorMax = new Vector2(0.5f, 0.5f);
        directionArrowRect.pivot = new Vector2(0.5f, 0.5f);
        directionArrowRect.anchoredPosition = Vector2.zero;
        directionArrowRect.sizeDelta = new Vector2(138f, 138f);

        directionArrowImage = iconGo.GetComponent<UnityEngine.UI.Image>();
        directionArrowImage.sprite = GetArrowSprite();
        directionArrowImage.color = arrowNormalColor;
        directionArrowImage.raycastTarget = false;

        if (turnHintText == null)
        {
            var hintGo = new GameObject("TurnHint", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            hintGo.transform.SetParent(badgeRect, false);
            var hintRect = hintGo.GetComponent<RectTransform>();
            hintRect.anchorMin = new Vector2(0.5f, 0f);
            hintRect.anchorMax = new Vector2(0.5f, 0f);
            hintRect.pivot = new Vector2(0.5f, 1f);
            hintRect.anchoredPosition = new Vector2(0f, -12f);
            hintRect.sizeDelta = new Vector2(440f, 52f);

            var tmp = hintGo.GetComponent<TextMeshProUGUI>();
            tmp.fontSize = 34f;
            tmp.fontStyle = FontStyles.Normal;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.raycastTarget = false;
            tmp.text = "Move Forward";
            turnHintText = tmp;
        }

        directionArrowContainer = badgeGo;
        badgeGo.SetActive(false);
    }

    private Transform FindHudArrowParent()
    {
        UIManager ui = FindAnyObjectByType<UIManager>();
        if (ui != null && ui.arNavPanel != null)
            return ui.arNavPanel.transform;

        if (statusText != null)
        {
            Transform cur = statusText.transform.parent;
            while (cur != null && cur.parent != null && cur.parent.GetComponent<Canvas>() == null)
                cur = cur.parent;
            if (cur != null) return cur;
        }

        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas != null) return canvas.transform;

        canvas = FindAnyObjectByType<Canvas>();
        return canvas != null ? canvas.transform : null;
    }

    internal static Sprite GetCircleSprite()
    {
        if (cachedCircleSprite == null)
        {
            const float r = 0.48f, aa = 0.02f;
            cachedCircleSprite = BuildRadialSprite(dist => Mathf.Clamp01((r - dist) / aa));
        }
        return cachedCircleSprite;
    }

    internal static Sprite GetRingSprite()
    {
        if (cachedRingSprite == null)
        {
            const float outerR = 0.49f, innerR = 0.43f, aa = 0.018f;
            cachedRingSprite = BuildRadialSprite(dist =>
                Mathf.Clamp01((outerR - dist) / aa) * Mathf.Clamp01((dist - innerR) / aa));
        }
        return cachedRingSprite;
    }

    internal static Sprite GetRoundedPillSprite()
    {
        if (cachedRoundedPillSprite != null) return cachedRoundedPillSprite;
        const int w = 128, h = 64;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[w * h];
        const float radius = 28f;
        Vector2 cLeft = new Vector2(32f, 32f);
        Vector2 cRight = new Vector2(96f, 32f);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float clampedX = Mathf.Clamp(p.x, cLeft.x, cRight.x);
                float d = Vector2.Distance(p, new Vector2(clampedX, 32f));
                float a = Mathf.Clamp01((radius - d) / 1.5f);
                px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        cachedRoundedPillSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(30, 30, 30, 30));
        return cachedRoundedPillSprite;
    }

    private static Sprite GetWheelchairSprite()
    {
        if (cachedWheelchairSprite != null) return cachedWheelchairSprite;
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];

        // Normalized [0..1] coordinates matching the Figma wheelchair symbol
        Vector2 headCenter = new Vector2(0.45f, 0.80f);
        const float headR = 0.068f;

        Vector2 wheelCenter = new Vector2(0.43f, 0.34f);
        const float wheelR = 0.21f;
        const float stroke = 0.042f;

        Vector2 spineTop = new Vector2(0.45f, 0.68f);
        Vector2 spineBottom = new Vector2(0.45f, 0.44f);
        Vector2 armStart = new Vector2(0.45f, 0.57f);
        Vector2 armEnd = new Vector2(0.64f, 0.57f);
        Vector2 seatEnd = new Vector2(0.67f, 0.44f);
        Vector2 legEnd = new Vector2(0.75f, 0.25f);
        Vector2 footEnd = new Vector2(0.82f, 0.28f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);

                // Head circle
                float dHead = Vector2.Distance(p, headCenter) - headR;

                // Wheel arc (open around top-right where the seat/body sits)
                float dWheelRing = Mathf.Abs(Vector2.Distance(p, wheelCenter) - wheelR) - stroke * 0.5f;
                Vector2 fromWheel = p - wheelCenter;
                float ang = Mathf.Atan2(fromWheel.y, fromWheel.x) * Mathf.Rad2Deg;
                if (ang > -5f && ang < 78f)
                    dWheelRing = 1f;

                // Body & chair segments
                float dSeg = Mathf.Min(
                    DistToSegment(p, spineTop, spineBottom),
                    Mathf.Min(
                        DistToSegment(p, armStart, armEnd),
                        Mathf.Min(
                            DistToSegment(p, spineBottom, seatEnd),
                            Mathf.Min(
                                DistToSegment(p, seatEnd, legEnd),
                                DistToSegment(p, legEnd, footEnd))))
                ) - stroke * 0.5f;

                float sd = Mathf.Min(dHead, Mathf.Min(dWheelRing, dSeg));
                float a = Mathf.Clamp01(0.5f - sd * size * 0.65f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }

        tex.SetPixels32(px);
        tex.Apply();
        cachedWheelchairSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        return cachedWheelchairSprite;
    }

    internal static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        float t = lenSq > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    private static Sprite BuildRadialSprite(System.Func<float, float> alphaAtRadius)
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            float dy = (y + 0.5f) / size - 0.5f;
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size - 0.5f;
                float a = alphaAtRadius(Mathf.Sqrt(dx * dx + dy * dy));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite GetArrowSprite()
    {
        if (cachedArrowSprite != null) return cachedArrowSprite;
        const int size = 128;
        const int samples = 4;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];

        Vector2 tip = new Vector2(0.50f, 0.88f);
        Vector2 right = new Vector2(0.84f, 0.16f);
        Vector2 notch = new Vector2(0.50f, 0.34f);
        Vector2 left = new Vector2(0.16f, 0.16f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < samples; sy++)
                {
                    float v = (y + (sy + 0.5f) / samples) / size;
                    for (int sx = 0; sx < samples; sx++)
                    {
                        float u = (x + (sx + 0.5f) / samples) / size;
                        Vector2 p = new Vector2(u, v);
                        if (PointInTriangle(p, tip, notch, left) || PointInTriangle(p, tip, right, notch))
                            hits++;
                    }
                }
                byte a = (byte)(hits * 255 / (samples * samples));
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
        }

        tex.SetPixels32(px);
        tex.Apply();
        cachedArrowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        return cachedArrowSprite;
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = CrossSign(p, a, b);
        float d2 = CrossSign(p, b, c);
        float d3 = CrossSign(p, c, a);
        bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
        bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
        return !(hasNeg && hasPos);
    }

    private static float CrossSign(Vector2 p1, Vector2 p2, Vector2 p3)
    {
        return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }
}
