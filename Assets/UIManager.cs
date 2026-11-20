using System;
using System.Collections;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;

/// <summary>
/// Manages app screen flow:
///  1. Converts MainMenuPanel into an automatic startup Loading Screen (hiding the old START button)
///     with an animated progress bar.
///  2. Auto-transitions after loading directly into the Apple Maps / Google Maps style
///     Map + Destination Selection Bottom Sheet view.
/// </summary>
public class UIManager : MonoBehaviour
{
    public static bool IsMainMenuActive { get; private set; }
    public static bool Is2DMapActive { get; private set; }

    [Header("UI Panels")]
    public GameObject splashPanel;
    public GameObject mainMenuPanel;
    public GameObject arNavPanel;
    public GameObject map2DPanel;
    public GameObject feedbackPanel;

    [Header("Startup Loading Screen")]
    [SerializeField] private float loadingDurationSeconds = 1.8f;

    private RectTransform loadingBarFill;
    private TMP_Text loadingStatusText;
    private TMP_InputField feedbackInputField;
    private TMP_Text feedbackStatusText;
    private CampusBuildingCode feedbackBuilding = CampusBuildingCode.M;
    private Coroutine loadingCoroutine;

    private void Awake()
    {
        EnsureCoreModulesAttached();
    }

    private void Start()
    {
        ConfigureExistingPanelControls();
        ConvertMainMenuToLoadingScreen();
        StartLoadingSequence();
    }

    public void ShowSplash() => StartLoadingSequence();

    /// <summary>
    /// Kept for backward compatibility with existing scene button bindings.
    /// Instead of showing a manual START menu, opens the Map + Destination Selection view
    /// (or runs the startup loading screen if called during initial boot).
    /// </summary>
    public void ShowMainMenu()
    {
        if (Time.time < 0.25f)
        {
            StartLoadingSequence();
            return;
        }
        OpenMapAndDestinationSheet();
    }

    public void ShowARNav()
    {
        ActivatePanel(arNavPanel);
    }

    public void Show2DMap()
    {
        ActivatePanel(map2DPanel);
    }

    public void ShowFeedback()
    {
        ActivatePanel(feedbackPanel);
    }

    /// <summary>
    /// Opens the Apple Maps / Google Maps style view: active navigation screen with
    /// the interactive 2D Map card + Room Search Bottom Sheet open.
    /// </summary>
    public void OpenMapAndDestinationSheet()
    {
        ActivatePanel(arNavPanel);
        DestinationManager dm = FindAnyObjectByType<DestinationManager>();
        if (dm != null)
            dm.SetDrawerOpen(true);
    }

    private void StartLoadingSequence()
    {
        if (loadingCoroutine != null)
            StopCoroutine(loadingCoroutine);

        if (mainMenuPanel != null)
        {
            ActivatePanel(mainMenuPanel);
            loadingCoroutine = StartCoroutine(LoadingScreenRoutine());
        }
        else
        {
            OpenMapAndDestinationSheet();
        }
    }

    private IEnumerator LoadingScreenRoutine()
    {
        float elapsed = 0f;
        float duration = Mathf.Max(0.6f, loadingDurationSeconds);

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float p = Mathf.Clamp01(elapsed / duration);
            float smoothP = Mathf.SmoothStep(0f, 1f, p);

            if (loadingBarFill != null)
            {
                loadingBarFill.anchorMax = new Vector2(Mathf.Max(0.04f, smoothP), 1f);
            }

            if (loadingStatusText != null)
            {
                if (p < 0.45f)
                    loadingStatusText.text = "Loading campus floorplans (M \u00B7 SJ \u00B7 GK)...";
                else if (p < 0.85f)
                    loadingStatusText.text = "Initializing AR & BLE/Wi-Fi positioning...";
                else
                    loadingStatusText.text = "Opening campus map...";
            }

            yield return null;
        }

        loadingCoroutine = null;
        OpenMapAndDestinationSheet();
    }

    private void ConvertMainMenuToLoadingScreen()
    {
        if (mainMenuPanel == null) return;

        // Hide all manual menu buttons (START, View 2D Map, Report an Issue) on MainMenuPanel
        foreach (UnityEngine.UI.Button btn in mainMenuPanel.GetComponentsInChildren<UnityEngine.UI.Button>(true))
        {
            if (btn != null && btn.gameObject != mainMenuPanel)
                btn.gameObject.SetActive(false);
        }

        if (mainMenuPanel.transform.Find("LoadingProgressContainer") != null)
            return;

        // Create a sleek loading bar + status text where the START button used to be
        var containerGo = new GameObject(
            "LoadingProgressContainer",
            typeof(RectTransform),
            typeof(UnityEngine.UI.LayoutElement));
        containerGo.transform.SetParent(mainMenuPanel.transform, false);
        containerGo.GetComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;

        var containerRect = containerGo.GetComponent<RectTransform>();
        containerRect.anchorMin = new Vector2(0.5f, 0.5f);
        containerRect.anchorMax = new Vector2(0.5f, 0.5f);
        containerRect.pivot = new Vector2(0.5f, 0.5f);
        containerRect.anchoredPosition = new Vector2(0f, -340f);
        containerRect.sizeDelta = new Vector2(620f, 120f);

        // Track background
        var trackGo = new GameObject(
            "ProgressTrack",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image));
        trackGo.transform.SetParent(containerRect, false);

        var trackRect = trackGo.GetComponent<RectTransform>();
        trackRect.anchorMin = new Vector2(0f, 0.5f);
        trackRect.anchorMax = new Vector2(1f, 0.5f);
        trackRect.pivot = new Vector2(0.5f, 0.5f);
        trackRect.anchoredPosition = new Vector2(0f, 16f);
        trackRect.sizeDelta = new Vector2(0f, 22f);

        var trackImg = trackGo.GetComponent<UnityEngine.UI.Image>();
        trackImg.sprite = NavigationHUD.GetRoundedPillSprite();
        trackImg.type = UnityEngine.UI.Image.Type.Sliced;
        trackImg.color = new Color(0.14f, 0.16f, 0.15f, 0.85f);

        // Fill bar
        var fillGo = new GameObject(
            "ProgressFill",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image));
        fillGo.transform.SetParent(trackRect, false);

        loadingBarFill = fillGo.GetComponent<RectTransform>();
        loadingBarFill.anchorMin = new Vector2(0f, 0f);
        loadingBarFill.anchorMax = new Vector2(0.05f, 1f);
        loadingBarFill.offsetMin = Vector2.zero;
        loadingBarFill.offsetMax = Vector2.zero;

        var fillImg = fillGo.GetComponent<UnityEngine.UI.Image>();
        fillImg.sprite = NavigationHUD.GetRoundedPillSprite();
        fillImg.type = UnityEngine.UI.Image.Type.Sliced;
        fillImg.color = new Color(0.31f, 0.64f, 0.35f, 1f);

        // Loading status label below bar
        var labelGo = new GameObject(
            "LoadingStatusLabel",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(containerRect, false);

        var labelRect = labelGo.GetComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(1f, 0f);
        labelRect.pivot = new Vector2(0.5f, 0f);
        labelRect.anchoredPosition = new Vector2(0f, 6f);
        labelRect.sizeDelta = new Vector2(0f, 44f);

        loadingStatusText = labelGo.GetComponent<TextMeshProUGUI>();
        loadingStatusText.text = "Loading campus floorplans (M \u00B7 SJ \u00B7 GK)...";
        loadingStatusText.fontSize = 26f;
        loadingStatusText.fontStyle = FontStyles.Normal;
        loadingStatusText.alignment = TextAlignmentOptions.Center;
        loadingStatusText.color = new Color(0.90f, 0.96f, 0.91f, 0.95f);
        loadingStatusText.raycastTarget = false;
    }

    private void ActivatePanel(GameObject panelToKeepActive)
    {
        IsMainMenuActive = (mainMenuPanel != null && panelToKeepActive == mainMenuPanel);
        Is2DMapActive = (map2DPanel != null && panelToKeepActive == map2DPanel);

        if (splashPanel != null) splashPanel.SetActive(splashPanel == panelToKeepActive);
        if (mainMenuPanel != null) mainMenuPanel.SetActive(mainMenuPanel == panelToKeepActive);
        if (arNavPanel != null) arNavPanel.SetActive(arNavPanel == panelToKeepActive);
        if (map2DPanel != null) map2DPanel.SetActive(map2DPanel == panelToKeepActive);
        if (feedbackPanel != null) feedbackPanel.SetActive(feedbackPanel == panelToKeepActive);
    }

    private void EnsureCoreModulesAttached()
    {
        PathVisualizer pv = FindAnyObjectByType<PathVisualizer>();
        GameObject host = pv != null ? pv.gameObject : gameObject;

        if (FindAnyObjectByType<CampusBuildingManager>() == null)
            host.AddComponent<CampusBuildingManager>();

        if (FindAnyObjectByType<KnnPositionProvider>() == null)
            host.AddComponent<KnnPositionProvider>();

        if (FindAnyObjectByType<NavigationTelemetryLogger>() == null)
            host.AddComponent<NavigationTelemetryLogger>();

        if (map2DPanel != null && map2DPanel.GetComponent<Map2DController>() == null)
            map2DPanel.AddComponent<Map2DController>();
    }

    private void ConfigureExistingPanelControls()
    {
        if (feedbackPanel != null)
        {
            feedbackInputField = feedbackPanel.GetComponentInChildren<TMP_InputField>(true);
            if (feedbackInputField != null && feedbackInputField.placeholder is TMP_Text ph)
                ph.text = "Describe issue or room feedback (e.g. M101, SJ, GK)...";

            Transform topPanel = feedbackPanel.transform.Find("TopPanel");
            if (topPanel != null)
            {
                Transform backBtnTr = topPanel.Find("Button_Back");
                if (backBtnTr != null)
                {
                    var btn = backBtnTr.GetComponent<UnityEngine.UI.Button>();
                    if (btn != null)
                    {
                        btn.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                        btn.onClick.AddListener(ShowARNav);
                    }
                    var tmp = backBtnTr.GetComponentInChildren<TMP_Text>(true);
                    if (tmp != null) tmp.text = "< Back";
                }
            }

            EnsureFeedbackSubmitButton();
        }
    }

    private void EnsureFeedbackSubmitButton()
    {
        if (feedbackPanel == null) return;
        if (feedbackPanel.transform.Find("Button_SubmitFeedback") != null) return;

        var panelRect = feedbackPanel.GetComponent<RectTransform>();
        if (panelRect == null) return;

        var submitGo = new GameObject(
            "Button_SubmitFeedback",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(UnityEngine.UI.Image),
            typeof(UnityEngine.UI.Button));
        submitGo.transform.SetParent(panelRect, false);

        var rt = submitGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, -120f);
        rt.sizeDelta = new Vector2(460f, 92f);

        var img = submitGo.GetComponent<UnityEngine.UI.Image>();
        img.color = new Color(0.20f, 0.44f, 0.18f, 1f);

        var btn = submitGo.GetComponent<UnityEngine.UI.Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(SubmitFeedbackReport);

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(rt, false);
        var lrt = labelGo.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;

        var tmp = labelGo.GetComponent<TextMeshProUGUI>();
        tmp.text = "Submit Feedback";
        tmp.fontSize = 30f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;

        var statusGo = new GameObject("FeedbackStatus", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        statusGo.transform.SetParent(panelRect, false);
        var srt = statusGo.GetComponent<RectTransform>();
        srt.anchorMin = new Vector2(0.5f, 0.5f);
        srt.anchorMax = new Vector2(0.5f, 0.5f);
        srt.pivot = new Vector2(0.5f, 0.5f);
        srt.anchoredPosition = new Vector2(0f, -220f);
        srt.sizeDelta = new Vector2(760f, 70f);

        feedbackStatusText = statusGo.GetComponent<TextMeshProUGUI>();
        feedbackStatusText.text = "Building codes: M = St. Miguel Hall, SJ = St. Joseph Hall, GK = Gokongwei Hall";
        feedbackStatusText.fontSize = 24f;
        feedbackStatusText.alignment = TextAlignmentOptions.Center;
        feedbackStatusText.color = new Color(0.82f, 0.96f, 0.85f, 1f);
        feedbackStatusText.raycastTarget = false;
    }

    public void SubmitFeedbackReport()
    {
        string text = feedbackInputField != null ? feedbackInputField.text.Trim() : "";
        if (string.IsNullOrEmpty(text))
        {
            if (feedbackStatusText != null)
                feedbackStatusText.text = "Please enter a brief description before submitting.";
            return;
        }

        string path = Path.Combine(Application.persistentDataPath, "user_feedback.csv");
        bool writeHeader = !File.Exists(path);
        string bCode = CampusPoiMetadata.GetBuildingShortCode(feedbackBuilding);
        string safeText = "\"" + text.Replace("\"", "\"\"") + "\"";
        string row = $"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},{bCode},{safeText}{Environment.NewLine}";

        try
        {
            if (writeHeader)
                File.AppendAllText(path, "timestamp_utc,building_code,feedback_text" + Environment.NewLine);
            File.AppendAllText(path, row);
            if (feedbackInputField != null) feedbackInputField.text = "";
            if (feedbackStatusText != null)
                feedbackStatusText.text = "Thank you! Feedback saved.";
        }
        catch (Exception e)
        {
            if (feedbackStatusText != null)
                feedbackStatusText.text = "Could not save feedback: " + e.Message;
        }
    }
}
