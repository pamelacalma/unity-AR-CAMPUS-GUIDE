using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Status line and directional HUD arrow for the AR screen:
/// destination, distance, live target-bearing arrow, turn cues, rerouting, arrival.
/// </summary>
public class NavigationHUD : MonoBehaviour
{
    [SerializeField] private PathVisualizer pathVisualizer;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private float rerouteToastSeconds = 2.5f;

    [Header("HUD Direction Arrow")]
    [SerializeField] private bool showDirectionArrow = true;
    [Tooltip("Optional custom arrow RectTransform (pointing UP at 0 deg). Leave empty to auto-create one on the AR screen.")]
    [SerializeField] private RectTransform directionArrowRect;
    [Tooltip("Optional Image on the arrow for tinting. Auto-detected from directionArrowRect if omitted.")]
    [SerializeField] private Image directionArrowImage;
    [Tooltip("Optional container GameObject to show/hide with navigation state.")]
    [SerializeField] private GameObject directionArrowContainer;
    [Tooltip("Optional separate TMP_Text for turn cues. Leave empty to use the auto-created label or append to statusText.")]
    [SerializeField] private TMP_Text turnHintText;
    [SerializeField] private float lookAheadMeters = 2.0f;
    [SerializeField] private float arrowSmoothSpeed = 12f;
    [SerializeField] private float turnAngleThresholdDeg = 35f;
    [SerializeField] private float turnAnnouncementDistance = 6f;
    [SerializeField] private Color arrowNormalColor = new Color(0f, 0.95f, 1f, 1f);
    [SerializeField] private Color arrowOffCourseColor = new Color(1f, 0.72f, 0.15f, 1f);

    [Header("PWD Accessibility Toggle")]
    [SerializeField] private bool showPwdToggle = true;
    [Tooltip("Optional custom Button to toggle PWD-accessible routing. Leave empty to auto-create one on the AR HUD.")]
    [SerializeField] private UnityEngine.UI.Button pwdToggleButton;
    [SerializeField] private TMP_Text pwdToggleLabel;
    [SerializeField] private Color pwdOffBgColor = new Color(0.10f, 0.14f, 0.20f, 0.86f);
    [SerializeField] private Color pwdOnBgColor = new Color(0.08f, 0.56f, 0.34f, 0.96f);

    private static Sprite cachedCircleSprite;
    private static Sprite cachedRingSprite;
    private static Sprite cachedArrowSprite;

    private float toastUntil;
    private float nextRefresh;
    private float currentArrowAngle;
    private bool arrowAngleInitialized;
    private bool autoHudInitialized;
    private string latestTurnCue = "";
    private UnityEngine.UI.Image pwdToggleImage;
    private bool autoPwdToggleInitialized;
    private bool pwdToggleBound;

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

    private void OnDeviated() { toastUntil = Time.time + rerouteToastSeconds; }
    private void OnAccessibilityModeChanged(bool _) { nextRefresh = 0f; RefreshPwdToggleVisuals(); }

    private void Update()
    {
        if (pathVisualizer == null) return;

        UpdateDirectionArrow();
        UpdatePwdToggle();

        if (statusText == null || Time.time < nextRefresh) return;
        nextRefresh = Time.time + 0.2f;

        if (Time.time < toastUntil) { statusText.text = "Rerouting…"; return; }

        string dest = DestinationManager.LabelFor(pathVisualizer.CurrentDestination);
        switch (pathVisualizer.State)
        {
            case NavState.NoDestination: statusText.text = "Choose a destination"; break;
            case NavState.OffMesh:       statusText.text = "Position unknown. Stand at a beacon to align"; break;
            case NavState.NoPath:
                statusText.text = pathVisualizer.PwdAccessibleMode
                    ? "No PWD-accessible route to " + dest
                    : "No route to " + dest;
                break;
            case NavState.Arrived:       statusText.text = "You have arrived at " + dest; break;
            default:
                string pwdTag = pathVisualizer.PwdAccessibleMode ? " · PWD" : "";
                statusText.text = (turnHintText == null && !string.IsNullOrEmpty(latestTurnCue) && latestTurnCue != "Go straight")
                    ? $"{dest} · {pathVisualizer.DistanceRemaining:0} m · {latestTurnCue}{pwdTag}"
                    : $"{dest} · {pathVisualizer.DistanceRemaining:0} m{pwdTag}";
                break;
        }
    }

    private void UpdateDirectionArrow()
    {
        if (!showDirectionArrow || UIManager.IsMainMenuActive || pathVisualizer.State != NavState.Navigating)
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
            bool offCourse = Mathf.Abs(bearingDeg) > 95f || Time.time < toastUntil;
            directionArrowImage.color = offCourse ? arrowOffCourseColor : arrowNormalColor;
        }

        latestTurnCue = BuildTurnCue(bearingDeg, hasUpcomingTurn, distanceToTurn, turnAngleDeg);
        if (turnHintText != null) turnHintText.text = latestTurnCue;
    }

    private string BuildTurnCue(float bearingDeg, bool hasUpcomingTurn, float distanceToTurn, float turnAngleDeg)
    {
        if (Mathf.Abs(bearingDeg) >= 135f) return "Turn around";
        if (Mathf.Abs(bearingDeg) >= 55f) return bearingDeg > 0f ? "Turn right" : "Turn left";

        if (hasUpcomingTurn && distanceToTurn <= turnAnnouncementDistance)
        {
            string dir = turnAngleDeg > 0f ? "right" : "left";
            return distanceToTurn <= 1.5f ? $"Turn {dir} now" : $"Turn {dir} in {distanceToTurn:0} m";
        }
        return "Go straight";
    }

    private void SetArrowVisible(bool visible)
    {
        GameObject target = directionArrowContainer != null
            ? directionArrowContainer
            : (directionArrowRect != null ? directionArrowRect.gameObject : null);

        if (target != null && target.activeSelf != visible)
            target.SetActive(visible);
    }

    private void UpdatePwdToggle()
    {
        bool shouldShow = showPwdToggle && !UIManager.IsMainMenuActive && pathVisualizer != null;
        if (!shouldShow)
        {
            SetPwdToggleVisible(false);
            return;
        }

        EnsurePwdToggle();
        SetPwdToggleVisible(true);
        RefreshPwdToggleVisuals();
    }

    private void EnsurePwdToggle()
    {
        if (pwdToggleButton != null)
        {
            if (pwdToggleImage == null)
                pwdToggleImage = pwdToggleButton.GetComponent<UnityEngine.UI.Image>();
            if (pwdToggleLabel == null)
                pwdToggleLabel = pwdToggleButton.GetComponentInChildren<TMP_Text>(true);
            if (!pwdToggleBound)
            {
                pwdToggleButton.onClick.AddListener(OnPwdButtonClicked);
                pwdToggleBound = true;
            }
            return;
        }

        if (autoPwdToggleInitialized) return;
        autoPwdToggleInitialized = true;

        Transform parent = FindHudArrowParent();
        if (parent == null) return;

        var toggleGo = new GameObject(
            "PwdModeToggle",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button),
            typeof(LayoutElement));
        toggleGo.transform.SetParent(parent, false);

        var layout = toggleGo.GetComponent<LayoutElement>();
        layout.ignoreLayout = true;

        var rect = toggleGo.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-24f, -150f);
        rect.sizeDelta = new Vector2(250f, 64f);

        pwdToggleImage = toggleGo.GetComponent<UnityEngine.UI.Image>();
        pwdToggleImage.color = pwdOffBgColor;
        pwdToggleImage.raycastTarget = true;

        pwdToggleButton = toggleGo.GetComponent<UnityEngine.UI.Button>();
        pwdToggleButton.targetGraphic = pwdToggleImage;
        pwdToggleButton.onClick.AddListener(OnPwdButtonClicked);
        pwdToggleBound = true;

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(rect, false);

        var labelRect = labelGo.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(12f, 4f);
        labelRect.offsetMax = new Vector2(-12f, -4f);

        var tmp = labelGo.GetComponent<TextMeshProUGUI>();
        tmp.fontSize = 24f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.raycastTarget = false;
        pwdToggleLabel = tmp;

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
            pwdToggleLabel.text = pwd ? "PWD Route: ON" : "PWD Route: OFF";
        if (pwdToggleImage != null)
            pwdToggleImage.color = pwd ? pwdOnBgColor : pwdOffBgColor;
    }

    private void EnsureHudArrow()
    {
        if (directionArrowRect != null)
        {
            if (directionArrowImage == null)
                directionArrowImage = directionArrowRect.GetComponent<Image>();
            if (directionArrowContainer == null)
                directionArrowContainer = directionArrowRect.gameObject;
            return;
        }

        if (autoHudInitialized) return;
        autoHudInitialized = true;

        Transform parent = FindHudArrowParent();
        if (parent == null) return;

        var badgeGo = new GameObject("NavArrowBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        badgeGo.transform.SetParent(parent, false);

        var layout = badgeGo.GetComponent<LayoutElement>();
        layout.ignoreLayout = true;

        var badgeRect = badgeGo.GetComponent<RectTransform>();
        badgeRect.anchorMin = new Vector2(0.5f, 1f);
        badgeRect.anchorMax = new Vector2(0.5f, 1f);
        badgeRect.pivot = new Vector2(0.5f, 1f);
        badgeRect.anchoredPosition = new Vector2(0f, -260f);
        badgeRect.sizeDelta = new Vector2(132f, 132f);

        var bgImage = badgeGo.GetComponent<Image>();
        bgImage.sprite = GetCircleSprite();
        bgImage.color = new Color(0.05f, 0.09f, 0.12f, 0.72f);
        bgImage.raycastTarget = false;

        var ringGo = new GameObject("Ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        ringGo.transform.SetParent(badgeRect, false);
        var ringRect = ringGo.GetComponent<RectTransform>();
        ringRect.anchorMin = Vector2.zero;
        ringRect.anchorMax = Vector2.one;
        ringRect.offsetMin = Vector2.zero;
        ringRect.offsetMax = Vector2.zero;
        var ringImg = ringGo.GetComponent<Image>();
        ringImg.sprite = GetRingSprite();
        ringImg.color = new Color(0f, 0.95f, 1f, 0.45f);
        ringImg.raycastTarget = false;

        var iconGo = new GameObject("NavArrowIcon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        iconGo.transform.SetParent(badgeRect, false);
        directionArrowRect = iconGo.GetComponent<RectTransform>();
        directionArrowRect.anchorMin = new Vector2(0.5f, 0.5f);
        directionArrowRect.anchorMax = new Vector2(0.5f, 0.5f);
        directionArrowRect.pivot = new Vector2(0.5f, 0.5f);
        directionArrowRect.anchoredPosition = Vector2.zero;
        directionArrowRect.sizeDelta = new Vector2(82f, 82f);

        directionArrowImage = iconGo.GetComponent<Image>();
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
            hintRect.anchoredPosition = new Vector2(0f, -10f);
            hintRect.sizeDelta = new Vector2(380f, 44f);

            var tmp = hintGo.GetComponent<TextMeshProUGUI>();
            tmp.fontSize = 26f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.raycastTarget = false;
            tmp.text = "";
            turnHintText = tmp;
        }

        directionArrowContainer = badgeGo;
        badgeGo.SetActive(false);
    }

    private Transform FindHudArrowParent()
    {
        if (statusText != null)
        {
            Transform cur = statusText.transform.parent;
            while (cur != null && cur.parent != null && cur.parent.GetComponent<Canvas>() == null)
            {
                cur = cur.parent;
            }
            if (cur != null) return cur;
        }

        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas != null) return canvas.transform;

        canvas = FindAnyObjectByType<Canvas>();
        return canvas != null ? canvas.transform : null;
    }

    private static Sprite GetCircleSprite()
    {
        if (cachedCircleSprite == null)
        {
            const float r = 0.48f, aa = 0.02f;
            cachedCircleSprite = BuildRadialSprite(dist => Mathf.Clamp01((r - dist) / aa));
        }
        return cachedCircleSprite;
    }

    private static Sprite GetRingSprite()
    {
        if (cachedRingSprite == null)
        {
            const float outerR = 0.48f, innerR = 0.44f, aa = 0.018f;
            cachedRingSprite = BuildRadialSprite(dist =>
                Mathf.Clamp01((outerR - dist) / aa) * Mathf.Clamp01((dist - innerR) / aa));
        }
        return cachedRingSprite;
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

        Vector2 tip = new Vector2(0.50f, 0.90f);
        Vector2 right = new Vector2(0.84f, 0.14f);
        Vector2 notch = new Vector2(0.50f, 0.32f);
        Vector2 left = new Vector2(0.16f, 0.14f);

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
