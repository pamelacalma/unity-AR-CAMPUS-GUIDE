using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
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

    public enum DebugOverlayTab
    {
        Align = 0,
        Radio = 1,
        Record = 2,
        Bench = 3
    }

    public static bool DebugOverlaysVisible { get; set; } = false;
    public static DebugOverlayTab ActiveDebugTab { get; set; } = DebugOverlayTab.Align;

    private static Sprite cachedCircleSprite;
    private static Sprite cachedRingSprite;
    private static Sprite cachedArrowSprite;
    private static Sprite cachedRoundedPillSprite;
    private static Sprite cachedRoundedCardSprite;
    private static Sprite cachedWheelchairSprite;
    private static readonly System.Collections.Generic.Dictionary<NavManeuverType, Sprite> cachedManeuverSprites =
        new System.Collections.Generic.Dictionary<NavManeuverType, Sprite>();

    private float toastUntil;
    private float nextRefresh;
    private float currentArrowAngle;
    private bool arrowAngleInitialized;
    private bool autoHudInitialized;

    private UnityEngine.UI.Image pwdToggleImage;
    private UnityEngine.UI.Image pwdOuterRingImage;
    private bool autoPwdToggleInitialized;
    private bool pwdToggleBound;

    private GameObject figmaChromeRoot;
    private GameObject topLeftLogoBadge;
    private UnityEngine.UI.Image primaryBannerBg;
    private UnityEngine.UI.Image primaryManeuverIcon;
    private TMP_Text primaryDistanceText;
    private TMP_Text routeSummaryText;
    private GameObject liveHeadingBadge;
    private GameObject thenBannerRoot;
    private UnityEngine.UI.Image thenBannerBg;
    private UnityEngine.UI.Image thenManeuverIcon;
    private TMP_Text thenText;
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

    private static int GetActiveTouchCount()
    {
        Touchscreen ts = Touchscreen.current;
        if (ts == null) return 0;

        int count = 0;
        var touches = ts.touches;
        for (int i = 0; i < touches.Count; i++)
        {
            if (touches[i].press.isPressed)
                count++;
        }
        return count;
    }

    private void Update()
    {
        if (pathVisualizer == null) return;

        // 3-finger tap (or F2 in Editor) toggles developer overlays without cluttering the Figma UI
        if (GetActiveTouchCount() >= 3)
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

        if (Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame)
            ToggleDebugOverlays();

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

        bool topBannerHandlesStatus = showDirectionArrow && pathVisualizer.CurrentDestination != null;

        // Screen 3: "Adjusting navigation ..." when rerouting or off-mesh with a selected destination
        if (Time.time < toastUntil ||
            (pathVisualizer.CurrentDestination != null && pathVisualizer.State == NavState.OffMesh))
        {
            string msg = "Adjusting navigation ...";
            bottomPromptText.text = topBannerHandlesStatus ? "" : msg;
            bottomPromptText.color = Color.white;
            if (statusText != null) statusText.text = msg;
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
            string msg = pathVisualizer.PwdAccessibleMode
                ? $"No assisted route to {dest}"
                : "Adjusting navigation ...";
            bottomPromptText.text = topBannerHandlesStatus ? "" : msg;
            bottomPromptText.color = Color.white;
            if (statusText != null) statusText.text = msg;
            return;
        }

        if (pathVisualizer.State == NavState.Arrived)
        {
            string dest = DestinationManager.LabelFor(pathVisualizer.CurrentDestination);
            string msg = $"Arrived at {dest}";
            bottomPromptText.text = topBannerHandlesStatus ? "" : msg;
            bottomPromptText.color = new Color(0.15f, 0.95f, 0.38f, 1f);
            if (statusText != null) statusText.text = msg;
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
        bool shouldShowBanner = showDirectionArrow &&
                                !UIManager.IsMainMenuActive &&
                                !UIManager.Is2DMapActive &&
                                !drawerOpen &&
                                pathVisualizer != null &&
                                pathVisualizer.CurrentDestination != null;

        if (topLeftLogoBadge != null && topLeftLogoBadge.activeSelf == shouldShowBanner)
            topLeftLogoBadge.SetActive(!shouldShowBanner);

        if (!shouldShowBanner)
        {
            SetArrowVisible(false);
            arrowAngleInitialized = false;
            if (turnHintText != null) turnHintText.text = "";
            if (primaryDistanceText != null) primaryDistanceText.text = "";
            if (routeSummaryText != null) routeSummaryText.text = "";
            if (thenBannerRoot != null) thenBannerRoot.SetActive(false);
            return;
        }

        EnsureHudArrow();
        SetArrowVisible(true);

        Transform destTransform = pathVisualizer.CurrentDestination;
        string destLabel = DestinationManager.LabelFor(destTransform);
        CampusPoiInfo poi = CampusPoiMetadata.GetOrInfer(destTransform);
        string buildingFloor = $"{poi.BuildingFullName} {poi.FloorLabel}";
        bool pwd = pathVisualizer.PwdAccessibleMode;

        Color standardBannerColor = pwd
            ? new Color(0.10f, 0.48f, 0.26f, 0.97f)
            : new Color(0.08f, 0.36f, 0.22f, 0.96f);
        Color amberBannerColor = new Color(0.64f, 0.34f, 0.06f, 0.96f);
        Color arrivedBannerColor = new Color(0.12f, 0.58f, 0.29f, 0.98f);

        if (pathVisualizer.State == NavState.Arrived)
        {
            ApplyBannerContent(
                arrivedBannerColor,
                NavManeuverType.Arrive,
                "Arrived",
                $"You have reached {destLabel}",
                $"{buildingFloor} \u00B7 Tap to pick another room",
                showLiveBadge: false,
                showThen: false,
                thenType: NavManeuverType.Straight,
                thenLabel: "");
            return;
        }

        if (Time.time < toastUntil || pathVisualizer.State == NavState.OffMesh || pathVisualizer.State == NavState.NoPath)
        {
            bool noPath = pathVisualizer.State == NavState.NoPath && Time.time >= toastUntil;
            string headline = (noPath && pwd) ? "No assisted route" : "Rerouting...";
            string instruction = noPath
                ? $"Could not find path to {destLabel}"
                : $"Adjusting navigation to {destLabel}";

            ApplyBannerContent(
                amberBannerColor,
                NavManeuverType.TurnAround,
                headline,
                instruction,
                buildingFloor,
                showLiveBadge: false,
                showThen: false,
                thenType: NavManeuverType.Straight,
                thenLabel: "");
            return;
        }

        if (!pathVisualizer.TryGetDetailedGuidance(lookAheadMeters, Mathf.Clamp(turnAngleThresholdDeg, 15f, 45f), out NavGuidanceSnapshot guidance))
        {
            SetArrowVisible(false);
            if (topLeftLogoBadge != null && !topLeftLogoBadge.activeSelf)
                topLeftLogoBadge.SetActive(true);
            return;
        }

        Transform cam = pathVisualizer.userTransform;
        float bearingDeg = 0f;
        if (cam != null)
        {
            Vector3 camFwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
            if (camFwd.sqrMagnitude < 0.02f)
                camFwd = Vector3.ProjectOnPlane(cam.forward.y < 0f ? cam.up : -cam.up, Vector3.up);
            camFwd = camFwd.sqrMagnitude > 1e-4f ? camFwd.normalized : Vector3.forward;

            Vector3 toAim = Vector3.ProjectOnPlane(guidance.aimPoint - cam.position, Vector3.up);
            if (toAim.sqrMagnitude > 1e-4f)
                bearingDeg = Vector3.SignedAngle(camFwd, toAim.normalized, Vector3.up);
        }

        UpdateLiveHeadingCompass(bearingDeg);

        string etaStr = FormatEta(guidance.estimatedSeconds);
        string pwdSuffix = pwd ? " \u00B7 Assisted" : "";
        string summary = $"{destLabel} \u00B7 {buildingFloor} \u00B7 {guidance.distanceRemaining:0} m left \u00B7 {etaStr}{pwdSuffix}";

        NavManeuverType primaryIcon;
        string distanceHeadline;
        string instructionText;
        Color cardColor = standardBannerColor;
        NavManeuverType thenIcon = NavManeuverType.Arrive;
        string thenString = $"Arrive at {destLabel} ({guidance.distanceRemaining:0} m)";

        float absBearing = Mathf.Abs(bearingDeg);
        if (absBearing >= 135f)
        {
            cardColor = amberBannerColor;
            primaryIcon = NavManeuverType.TurnAround;
            distanceHeadline = "Turn around";
            instructionText = "Face the opposite direction";
            if (guidance.hasNextStep)
            {
                thenIcon = guidance.nextStep.type;
                thenString = DescribeThenStep(guidance.nextStep, pwd);
            }
            else
            {
                thenIcon = NavManeuverType.Straight;
                thenString = $"Continue toward {destLabel} ({guidance.distanceRemaining:0} m)";
            }
        }
        else if (absBearing >= 50f)
        {
            primaryIcon = bearingDeg > 0f ? NavManeuverType.TurnRight : NavManeuverType.TurnLeft;
            distanceHeadline = bearingDeg > 0f ? "Turn right" : "Turn left";
            instructionText = $"Face corridor toward {destLabel}";
            if (guidance.hasNextStep)
            {
                thenIcon = guidance.nextStep.type;
                thenString = DescribeThenStep(guidance.nextStep, pwd);
            }
        }
        else if (guidance.hasNextStep && guidance.nextStep.distanceFromUser <= Mathf.Max(6f, turnAnnouncementDistance))
        {
            bool immediate = guidance.nextStep.distanceFromUser <= 1.8f;
            primaryIcon = guidance.nextStep.type;
            distanceHeadline = immediate ? "Now" : $"{guidance.nextStep.distanceFromUser:0} m";
            instructionText = DescribePrimaryManeuver(guidance.nextStep.type, immediate, pwd);
            if (guidance.hasFollowingStep)
            {
                thenIcon = guidance.followingStep.type;
                thenString = DescribeThenStep(guidance.followingStep, pwd);
            }
        }
        else if (absBearing >= 22f)
        {
            float dist = guidance.hasNextStep ? guidance.nextStep.distanceFromUser : guidance.distanceRemaining;
            primaryIcon = bearingDeg > 0f ? NavManeuverType.SlightRight : NavManeuverType.SlightLeft;
            distanceHeadline = $"{Mathf.Max(1f, dist):0} m";
            instructionText = bearingDeg > 0f ? "Bear slightly right" : "Bear slightly left";
            if (guidance.hasNextStep)
            {
                thenIcon = guidance.nextStep.type;
                thenString = DescribeThenStep(guidance.nextStep, pwd);
            }
        }
        else
        {
            float dist = guidance.hasNextStep ? guidance.nextStep.distanceFromUser : guidance.distanceRemaining;
            primaryIcon = NavManeuverType.Straight;
            distanceHeadline = $"{Mathf.Max(1f, dist):0} m";
            instructionText = guidance.hasNextStep
                ? "Head straight"
                : $"Continue straight to {destLabel}";
            if (guidance.hasNextStep)
            {
                thenIcon = guidance.nextStep.type;
                thenString = DescribeThenStep(guidance.nextStep, pwd);
            }
        }

        ApplyBannerContent(
            cardColor,
            primaryIcon,
            distanceHeadline,
            instructionText,
            summary,
            showLiveBadge: true,
            showThen: true,
            thenType: thenIcon,
            thenLabel: thenString);
    }

    private void UpdateLiveHeadingCompass(float bearingDeg)
    {
        if (directionArrowRect == null) return;

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
            float abs = Mathf.Abs(bearingDeg);
            if (abs <= 35f)
                directionArrowImage.color = arrowNormalColor;
            else if (abs <= 135f)
                directionArrowImage.color = new Color(1f, 0.88f, 0.35f, 1f);
            else
                directionArrowImage.color = new Color(1f, 0.52f, 0.24f, 1f);
        }
    }

    private void ApplyBannerContent(
        Color bgColor,
        NavManeuverType primaryType,
        string distanceHeadline,
        string instruction,
        string summary,
        bool showLiveBadge,
        bool showThen,
        NavManeuverType thenType,
        string thenLabel)
    {
        if (primaryBannerBg != null)
            primaryBannerBg.color = bgColor;

        if (primaryManeuverIcon != null)
            primaryManeuverIcon.sprite = GetManeuverSprite(primaryType);

        if (primaryDistanceText != null)
            primaryDistanceText.text = distanceHeadline;

        if (turnHintText != null)
            turnHintText.text = instruction;

        if (routeSummaryText != null)
            routeSummaryText.text = summary;

        if (liveHeadingBadge != null && liveHeadingBadge.activeSelf != showLiveBadge)
            liveHeadingBadge.SetActive(showLiveBadge);

        if (thenBannerRoot != null)
        {
            if (thenBannerRoot.activeSelf != showThen)
                thenBannerRoot.SetActive(showThen);

            if (showThen)
            {
                if (thenBannerBg != null)
                    thenBannerBg.color = new Color(bgColor.r * 0.62f, bgColor.g * 0.62f, bgColor.b * 0.62f, 0.95f);
                if (thenManeuverIcon != null)
                    thenManeuverIcon.sprite = GetManeuverSprite(thenType);
                if (thenText != null)
                    thenText.text = thenLabel;
            }
        }
    }

    private static string FormatEta(float seconds)
    {
        int secs = Mathf.Max(1, Mathf.RoundToInt(seconds));
        if (secs < 60) return $"~{secs} sec";
        int mins = Mathf.Max(1, Mathf.RoundToInt(seconds / 60f));
        return $"~{mins} min";
    }

    private static string DescribePrimaryManeuver(NavManeuverType type, bool now, bool pwd)
    {
        switch (type)
        {
            case NavManeuverType.Straight:    return "Head straight";
            case NavManeuverType.SlightLeft:  return now ? "Bear slightly left" : "Bear left ahead";
            case NavManeuverType.TurnLeft:    return now ? "Turn left" : "Turn left ahead";
            case NavManeuverType.SharpLeft:   return now ? "Make a sharp left" : "Sharp left ahead";
            case NavManeuverType.SlightRight: return now ? "Bear slightly right" : "Bear right ahead";
            case NavManeuverType.TurnRight:   return now ? "Turn right" : "Turn right ahead";
            case NavManeuverType.SharpRight:  return now ? "Make a sharp right" : "Sharp right ahead";
            case NavManeuverType.TurnAround:  return "Turn around";
            case NavManeuverType.FloorUp:
                return pwd
                    ? (now ? "Take elevator / ramp up" : "Elevator / ramp up ahead")
                    : (now ? "Go up stairs" : "Stairs up ahead");
            case NavManeuverType.FloorDown:
                return pwd
                    ? (now ? "Take elevator / ramp down" : "Elevator / ramp down ahead")
                    : (now ? "Go down stairs" : "Stairs down ahead");
            case NavManeuverType.Arrive:      return "Destination ahead";
            default:                          return "Head straight";
        }
    }

    private static string DescribeThenStep(NavManeuverStep step, bool pwd)
    {
        string distStr = step.distanceFromUser <= 1.5f ? "ahead" : $"in {step.distanceFromUser:0} m";
        switch (step.type)
        {
            case NavManeuverType.SlightLeft:  return $"Bear left {distStr}";
            case NavManeuverType.TurnLeft:    return $"Turn left {distStr}";
            case NavManeuverType.SharpLeft:   return $"Sharp left {distStr}";
            case NavManeuverType.SlightRight: return $"Bear right {distStr}";
            case NavManeuverType.TurnRight:   return $"Turn right {distStr}";
            case NavManeuverType.SharpRight:  return $"Sharp right {distStr}";
            case NavManeuverType.FloorUp:     return pwd ? $"Elevator / ramp up {distStr}" : $"Go up stairs {distStr}";
            case NavManeuverType.FloorDown:   return pwd ? $"Elevator / ramp down {distStr}" : $"Go down stairs {distStr}";
            default:                          return $"Continue {distStr}";
        }
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

        // 1. Top-Left Circular Logo Badge (shown when no route banner is visible)
        var logoBadgeGo = new GameObject(
            "TopLeftLogoBadge",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button));
        logoBadgeGo.transform.SetParent(rootRect, false);
        topLeftLogoBadge = logoBadgeGo;

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

        // Apple Maps / Google Maps style top direction banner
        var bannerRootGo = new GameObject("TopNavBannerRoot", typeof(RectTransform), typeof(LayoutElement));
        bannerRootGo.transform.SetParent(parent, false);
        bannerRootGo.GetComponent<LayoutElement>().ignoreLayout = true;

        var bannerRootRect = bannerRootGo.GetComponent<RectTransform>();
        bannerRootRect.anchorMin = new Vector2(0f, 1f);
        bannerRootRect.anchorMax = new Vector2(1f, 1f);
        bannerRootRect.pivot = new Vector2(0.5f, 1f);
        bannerRootRect.anchoredPosition = new Vector2(0f, -44f);
        bannerRootRect.sizeDelta = new Vector2(-56f, 286f);

        // Primary Banner Card (clickable to switch destination)
        var primaryCardGo = new GameObject(
            "PrimaryBannerCard",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button));
        primaryCardGo.transform.SetParent(bannerRootRect, false);

        var primaryCardRect = primaryCardGo.GetComponent<RectTransform>();
        primaryCardRect.anchorMin = new Vector2(0f, 1f);
        primaryCardRect.anchorMax = new Vector2(1f, 1f);
        primaryCardRect.pivot = new Vector2(0.5f, 1f);
        primaryCardRect.anchoredPosition = Vector2.zero;
        primaryCardRect.sizeDelta = new Vector2(0f, 206f);

        primaryBannerBg = primaryCardGo.GetComponent<UnityEngine.UI.Image>();
        primaryBannerBg.sprite = GetRoundedCardSprite();
        primaryBannerBg.type = UnityEngine.UI.Image.Type.Sliced;
        primaryBannerBg.color = new Color(0.08f, 0.36f, 0.22f, 0.96f);

        var cardBtn = primaryCardGo.GetComponent<UnityEngine.UI.Button>();
        cardBtn.targetGraphic = primaryBannerBg;
        cardBtn.onClick.AddListener(OpenRoomSearchDrawer);

        // Left: Maneuver Icon
        var maneuverGo = new GameObject("PrimaryManeuverIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        maneuverGo.transform.SetParent(primaryCardRect, false);
        var maneuverRect = maneuverGo.GetComponent<RectTransform>();
        maneuverRect.anchorMin = new Vector2(0f, 0.5f);
        maneuverRect.anchorMax = new Vector2(0f, 0.5f);
        maneuverRect.pivot = new Vector2(0f, 0.5f);
        maneuverRect.anchoredPosition = new Vector2(28f, 0f);
        maneuverRect.sizeDelta = new Vector2(108f, 108f);

        primaryManeuverIcon = maneuverGo.GetComponent<UnityEngine.UI.Image>();
        primaryManeuverIcon.sprite = GetManeuverSprite(NavManeuverType.Straight);
        primaryManeuverIcon.color = Color.white;
        primaryManeuverIcon.raycastTarget = false;

        // Center Text Stack (Distance + Primary Instruction + Building/Floor/ETA)
        var textStackGo = new GameObject("CenterTextStack", typeof(RectTransform));
        textStackGo.transform.SetParent(primaryCardRect, false);
        var textStackRect = textStackGo.GetComponent<RectTransform>();
        textStackRect.anchorMin = Vector2.zero;
        textStackRect.anchorMax = Vector2.one;
        textStackRect.offsetMin = new Vector2(156f, 18f);
        textStackRect.offsetMax = new Vector2(-126f, -18f);

        var distGo = new GameObject("PrimaryDistanceText", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        distGo.transform.SetParent(textStackRect, false);
        var distRect = distGo.GetComponent<RectTransform>();
        distRect.anchorMin = new Vector2(0f, 1f);
        distRect.anchorMax = new Vector2(1f, 1f);
        distRect.pivot = new Vector2(0f, 1f);
        distRect.anchoredPosition = Vector2.zero;
        distRect.sizeDelta = new Vector2(0f, 66f);

        primaryDistanceText = distGo.GetComponent<TextMeshProUGUI>();
        primaryDistanceText.fontSize = 54f;
        primaryDistanceText.fontStyle = FontStyles.Bold;
        primaryDistanceText.alignment = TextAlignmentOptions.MidlineLeft;
        primaryDistanceText.color = Color.white;
        primaryDistanceText.textWrappingMode = TextWrappingModes.NoWrap;
        primaryDistanceText.overflowMode = TextOverflowModes.Ellipsis;
        primaryDistanceText.raycastTarget = false;
        primaryDistanceText.text = "";

        var hintGo = new GameObject("TurnHint", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        hintGo.transform.SetParent(textStackRect, false);
        var hintRect = hintGo.GetComponent<RectTransform>();
        hintRect.anchorMin = new Vector2(0f, 1f);
        hintRect.anchorMax = new Vector2(1f, 1f);
        hintRect.pivot = new Vector2(0f, 1f);
        hintRect.anchoredPosition = new Vector2(0f, -72f);
        hintRect.sizeDelta = new Vector2(0f, 44f);

        var hintTmp = hintGo.GetComponent<TextMeshProUGUI>();
        hintTmp.fontSize = 31f;
        hintTmp.fontStyle = FontStyles.Bold;
        hintTmp.alignment = TextAlignmentOptions.MidlineLeft;
        hintTmp.color = new Color(0.95f, 0.99f, 0.96f, 1f);
        hintTmp.textWrappingMode = TextWrappingModes.NoWrap;
        hintTmp.overflowMode = TextOverflowModes.Ellipsis;
        hintTmp.raycastTarget = false;
        hintTmp.text = "Head straight";
        if (turnHintText == null)
            turnHintText = hintTmp;

        var summaryGo = new GameObject("RouteSummaryText", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        summaryGo.transform.SetParent(textStackRect, false);
        var summaryRect = summaryGo.GetComponent<RectTransform>();
        summaryRect.anchorMin = new Vector2(0f, 1f);
        summaryRect.anchorMax = new Vector2(1f, 1f);
        summaryRect.pivot = new Vector2(0f, 1f);
        summaryRect.anchoredPosition = new Vector2(0f, -124f);
        summaryRect.sizeDelta = new Vector2(0f, 38f);

        routeSummaryText = summaryGo.GetComponent<TextMeshProUGUI>();
        routeSummaryText.fontSize = 22f;
        routeSummaryText.fontStyle = FontStyles.Normal;
        routeSummaryText.alignment = TextAlignmentOptions.MidlineLeft;
        routeSummaryText.color = new Color(0.82f, 0.96f, 0.87f, 0.92f);
        routeSummaryText.textWrappingMode = TextWrappingModes.NoWrap;
        routeSummaryText.overflowMode = TextOverflowModes.Ellipsis;
        routeSummaryText.raycastTarget = false;
        routeSummaryText.text = "";

        // Right: Live Heading Compass Pill
        liveHeadingBadge = new GameObject("LiveHeadingBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        liveHeadingBadge.transform.SetParent(primaryCardRect, false);
        var liveBadgeRect = liveHeadingBadge.GetComponent<RectTransform>();
        liveBadgeRect.anchorMin = new Vector2(1f, 0.5f);
        liveBadgeRect.anchorMax = new Vector2(1f, 0.5f);
        liveBadgeRect.pivot = new Vector2(1f, 0.5f);
        liveBadgeRect.anchoredPosition = new Vector2(-22f, 0f);
        liveBadgeRect.sizeDelta = new Vector2(84f, 84f);

        var liveBadgeBg = liveHeadingBadge.GetComponent<UnityEngine.UI.Image>();
        liveBadgeBg.sprite = GetCircleSprite();
        liveBadgeBg.color = new Color(0f, 0f, 0f, 0.28f);
        liveBadgeBg.raycastTarget = false;

        var iconGo = new GameObject("NavArrowIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        iconGo.transform.SetParent(liveBadgeRect, false);
        directionArrowRect = iconGo.GetComponent<RectTransform>();
        directionArrowRect.anchorMin = new Vector2(0.5f, 0.5f);
        directionArrowRect.anchorMax = new Vector2(0.5f, 0.5f);
        directionArrowRect.pivot = new Vector2(0.5f, 0.5f);
        directionArrowRect.anchoredPosition = Vector2.zero;
        directionArrowRect.sizeDelta = new Vector2(54f, 54f);

        directionArrowImage = iconGo.GetComponent<UnityEngine.UI.Image>();
        directionArrowImage.sprite = GetArrowSprite();
        directionArrowImage.color = arrowNormalColor;
        directionArrowImage.raycastTarget = false;

        // Attached Secondary "THEN" Sub-Banner
        thenBannerRoot = new GameObject("ThenBannerCard", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        thenBannerRoot.transform.SetParent(bannerRootRect, false);
        var thenRect = thenBannerRoot.GetComponent<RectTransform>();
        thenRect.anchorMin = new Vector2(0f, 1f);
        thenRect.anchorMax = new Vector2(1f, 1f);
        thenRect.pivot = new Vector2(0.5f, 1f);
        thenRect.anchoredPosition = new Vector2(0f, -214f);
        thenRect.sizeDelta = new Vector2(0f, 68f);

        thenBannerBg = thenBannerRoot.GetComponent<UnityEngine.UI.Image>();
        thenBannerBg.sprite = GetRoundedCardSprite();
        thenBannerBg.type = UnityEngine.UI.Image.Type.Sliced;
        thenBannerBg.color = new Color(0.05f, 0.23f, 0.14f, 0.94f);
        thenBannerBg.raycastTarget = false;

        var thenPrefixGo = new GameObject("ThenPrefixLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        thenPrefixGo.transform.SetParent(thenRect, false);
        var prefixRect = thenPrefixGo.GetComponent<RectTransform>();
        prefixRect.anchorMin = new Vector2(0f, 0.5f);
        prefixRect.anchorMax = new Vector2(0f, 0.5f);
        prefixRect.pivot = new Vector2(0f, 0.5f);
        prefixRect.anchoredPosition = new Vector2(28f, 0f);
        prefixRect.sizeDelta = new Vector2(78f, 68f);

        var prefixTmp = thenPrefixGo.GetComponent<TextMeshProUGUI>();
        prefixTmp.fontSize = 21f;
        prefixTmp.fontStyle = FontStyles.Bold;
        prefixTmp.alignment = TextAlignmentOptions.MidlineLeft;
        prefixTmp.color = new Color(0.68f, 0.92f, 0.76f, 0.95f);
        prefixTmp.textWrappingMode = TextWrappingModes.NoWrap;
        prefixTmp.raycastTarget = false;
        prefixTmp.text = "THEN";

        var thenIconGo = new GameObject("ThenManeuverIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
        thenIconGo.transform.SetParent(thenRect, false);
        var thenIconRect = thenIconGo.GetComponent<RectTransform>();
        thenIconRect.anchorMin = new Vector2(0f, 0.5f);
        thenIconRect.anchorMax = new Vector2(0f, 0.5f);
        thenIconRect.pivot = new Vector2(0f, 0.5f);
        thenIconRect.anchoredPosition = new Vector2(112f, 0f);
        thenIconRect.sizeDelta = new Vector2(36f, 36f);

        thenManeuverIcon = thenIconGo.GetComponent<UnityEngine.UI.Image>();
        thenManeuverIcon.sprite = GetManeuverSprite(NavManeuverType.Arrive);
        thenManeuverIcon.color = Color.white;
        thenManeuverIcon.raycastTarget = false;

        var thenTextGo = new GameObject("ThenText", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        thenTextGo.transform.SetParent(thenRect, false);
        var thenTextRect = thenTextGo.GetComponent<RectTransform>();
        thenTextRect.anchorMin = Vector2.zero;
        thenTextRect.anchorMax = Vector2.one;
        thenTextRect.offsetMin = new Vector2(162f, 8f);
        thenTextRect.offsetMax = new Vector2(-24f, -8f);

        thenText = thenTextGo.GetComponent<TextMeshProUGUI>();
        thenText.fontSize = 26f;
        thenText.fontStyle = FontStyles.Bold;
        thenText.alignment = TextAlignmentOptions.MidlineLeft;
        thenText.color = Color.white;
        thenText.textWrappingMode = TextWrappingModes.NoWrap;
        thenText.overflowMode = TextOverflowModes.Ellipsis;
        thenText.raycastTarget = false;
        thenText.text = "";

        directionArrowContainer = bannerRootGo;
        bannerRootGo.SetActive(false);
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

    internal static Sprite GetRoundedCardSprite()
    {
        if (cachedRoundedCardSprite != null) return cachedRoundedCardSprite;
        const int size = 128;
        const float radius = 24f;
        const float half = size * 0.5f;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];

        Vector2 innerHalf = new Vector2(half - radius, half - radius);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) - half, (y + 0.5f) - half);
                Vector2 q = new Vector2(Mathf.Abs(p.x) - innerHalf.x, Mathf.Abs(p.y) - innerHalf.y);
                Vector2 qPos = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f));
                float sd = qPos.magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
                float a = Mathf.Clamp01(0.5f - sd * 0.75f);
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
            new Vector4(28f, 28f, 28f, 28f));
        return cachedRoundedCardSprite;
    }

    internal static Sprite GetManeuverSprite(NavManeuverType type)
    {
        if (cachedManeuverSprites.TryGetValue(type, out Sprite existing) && existing != null)
            return existing;

        const int size = 128;
        const int samples = 3;
        const float halfStroke = 0.055f;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];

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
                        if (EvaluateManeuverIcon(type, new Vector2(u, v), halfStroke))
                            hits++;
                    }
                }
                byte a = (byte)(hits * 255 / (samples * samples));
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
        }

        tex.SetPixels32(px);
        tex.Apply();
        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        cachedManeuverSprites[type] = sprite;
        return sprite;
    }

    private static bool EvaluateManeuverIcon(NavManeuverType type, Vector2 p, float halfStroke)
    {
        bool mirrorX = type == NavManeuverType.SlightRight ||
                       type == NavManeuverType.TurnRight ||
                       type == NavManeuverType.SharpRight;
        if (mirrorX)
            p.x = 1f - p.x;

        switch (type)
        {
            case NavManeuverType.Straight:
                return DistToSegment(p, new Vector2(0.50f, 0.14f), new Vector2(0.50f, 0.68f)) <= halfStroke ||
                       HitArrowHead(p, new Vector2(0.50f, 0.90f), Vector2.up, 0.24f, 0.19f);

            case NavManeuverType.SlightLeft:
            case NavManeuverType.SlightRight:
                return DistToSegment(p, new Vector2(0.62f, 0.14f), new Vector2(0.62f, 0.44f)) <= halfStroke ||
                       DistToSegment(p, new Vector2(0.62f, 0.44f), new Vector2(0.36f, 0.70f)) <= halfStroke ||
                       HitArrowHead(p, new Vector2(0.21f, 0.85f), new Vector2(-0.7071f, 0.7071f), 0.23f, 0.18f);

            case NavManeuverType.TurnLeft:
            case NavManeuverType.TurnRight:
                return DistToSegment(p, new Vector2(0.68f, 0.14f), new Vector2(0.68f, 0.56f)) <= halfStroke ||
                       DistToSegment(p, new Vector2(0.68f, 0.56f), new Vector2(0.34f, 0.56f)) <= halfStroke ||
                       HitArrowHead(p, new Vector2(0.12f, 0.56f), Vector2.left, 0.24f, 0.19f);

            case NavManeuverType.SharpLeft:
            case NavManeuverType.SharpRight:
                return DistToSegment(p, new Vector2(0.68f, 0.16f), new Vector2(0.68f, 0.68f)) <= halfStroke ||
                       DistToSegment(p, new Vector2(0.68f, 0.68f), new Vector2(0.31f, 0.35f)) <= halfStroke ||
                       HitArrowHead(p, new Vector2(0.15f, 0.21f), new Vector2(-0.7433f, -0.669f), 0.23f, 0.18f);

            case NavManeuverType.TurnAround:
                bool onStem = DistToSegment(p, new Vector2(0.68f, 0.14f), new Vector2(0.68f, 0.60f)) <= halfStroke ||
                              DistToSegment(p, new Vector2(0.32f, 0.60f), new Vector2(0.32f, 0.36f)) <= halfStroke;
                Vector2 arcCenter = new Vector2(0.50f, 0.60f);
                bool onArc = p.y >= 0.60f && Mathf.Abs(Vector2.Distance(p, arcCenter) - 0.18f) <= halfStroke;
                return onStem || onArc || HitArrowHead(p, new Vector2(0.32f, 0.14f), Vector2.down, 0.23f, 0.18f);

            case NavManeuverType.FloorUp:
                float stairStroke = 0.042f;
                bool stairsUp = DistToSegment(p, new Vector2(0.14f, 0.22f), new Vector2(0.34f, 0.22f)) <= stairStroke ||
                                DistToSegment(p, new Vector2(0.34f, 0.22f), new Vector2(0.34f, 0.39f)) <= stairStroke ||
                                DistToSegment(p, new Vector2(0.34f, 0.39f), new Vector2(0.54f, 0.39f)) <= stairStroke ||
                                DistToSegment(p, new Vector2(0.54f, 0.39f), new Vector2(0.54f, 0.56f)) <= stairStroke ||
                                DistToSegment(p, new Vector2(0.54f, 0.56f), new Vector2(0.76f, 0.56f)) <= stairStroke;
                bool arrowUp = DistToSegment(p, new Vector2(0.24f, 0.54f), new Vector2(0.60f, 0.78f)) <= stairStroke ||
                               HitArrowHead(p, new Vector2(0.76f, 0.89f), new Vector2(0.832f, 0.555f), 0.20f, 0.15f);
                return stairsUp || arrowUp;

            case NavManeuverType.FloorDown:
                float stairDownStroke = 0.042f;
                bool stairsDown = DistToSegment(p, new Vector2(0.14f, 0.60f), new Vector2(0.34f, 0.60f)) <= stairDownStroke ||
                                  DistToSegment(p, new Vector2(0.34f, 0.60f), new Vector2(0.34f, 0.43f)) <= stairDownStroke ||
                                  DistToSegment(p, new Vector2(0.34f, 0.43f), new Vector2(0.54f, 0.43f)) <= stairDownStroke ||
                                  DistToSegment(p, new Vector2(0.54f, 0.43f), new Vector2(0.54f, 0.26f)) <= stairDownStroke ||
                                  DistToSegment(p, new Vector2(0.54f, 0.26f), new Vector2(0.76f, 0.26f)) <= stairDownStroke;
                bool arrowDown = DistToSegment(p, new Vector2(0.34f, 0.84f), new Vector2(0.68f, 0.56f)) <= stairDownStroke ||
                                 HitArrowHead(p, new Vector2(0.82f, 0.44f), new Vector2(0.772f, -0.635f), 0.20f, 0.15f);
                return stairsDown || arrowDown;

            case NavManeuverType.Arrive:
                Vector2 pinCenter = new Vector2(0.50f, 0.60f);
                float distCenter = Vector2.Distance(p, pinCenter);
                bool insideOuter = distCenter <= 0.25f ||
                                   PointInTriangle(p, new Vector2(0.50f, 0.12f), new Vector2(0.285f, 0.47f), new Vector2(0.715f, 0.47f));
                return insideOuter && distCenter > 0.105f;

            default:
                return false;
        }
    }

    private static bool HitArrowHead(Vector2 p, Vector2 tip, Vector2 dir, float length, float halfWidth)
    {
        Vector2 baseCenter = tip - dir * length;
        Vector2 perp = new Vector2(-dir.y, dir.x);
        return PointInTriangle(p, tip, baseCenter - perp * halfWidth, baseCenter + perp * halfWidth);
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

    private void OnGUI()
    {
        if (UIManager.IsMainMenuActive || !DebugOverlaysVisible)
            return;

        const float s = 2.5f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        GUILayout.BeginArea(new Rect(8, 56, 300, 34), GUI.skin.box);
        GUILayout.BeginHorizontal();

        DrawTabButton("Align", DebugOverlayTab.Align);
        DrawTabButton("Radio", DebugOverlayTab.Radio);
        DrawTabButton("Record", DebugOverlayTab.Record);
        DrawTabButton("Bench", DebugOverlayTab.Bench);

        GUI.color = new Color(1f, 0.65f, 0.65f, 1f);
        if (GUILayout.Button("X", GUILayout.Width(28)))
            DebugOverlaysVisible = false;
        GUI.color = Color.white;

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private static void DrawTabButton(string label, DebugOverlayTab tab)
    {
        GUI.color = ActiveDebugTab == tab ? new Color(0.45f, 0.95f, 1f, 1f) : Color.white;
        if (GUILayout.Button(label))
            ActiveDebugTab = tab;
        GUI.color = Color.white;
    }
}
