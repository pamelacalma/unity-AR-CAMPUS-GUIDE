using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Holds the list of destinations (doors / POIs) and tells PathVisualizer which is active.
/// Name your target objects "Destination_<Name>"; the label is derived from that.
/// Either bind buttons to SelectDestination(int) by hand, or assign a button prefab +
/// container and the buttons are generated from the list.
/// </summary>
public class DestinationManager : MonoBehaviour
{
    [Header("Path Visualizer Reference")]
    public PathVisualizer pathVisualizer;

    [Header("Target Objects in Room")]
    public List<Transform> targets = new List<Transform>();

    [Header("Optional: auto-generate buttons")]
    [SerializeField] private RectTransform buttonContainer;
    [Tooltip("A Button with a TMP_Text child.")]
    [SerializeField] private Button buttonPrefab;
    [Tooltip("If Button Container / Prefab aren't assigned, automatically turn the scene's TESTING button into a row for all targets.")]
    [SerializeField] private bool autoDiscoverBottomButton = true;

    [Header("Button Colors")]
    [SerializeField] private Color normalButtonColor = new Color(0.18f, 0.24f, 0.34f, 0.92f);
    [SerializeField] private Color selectedButtonColor = new Color(0.05f, 0.65f, 0.78f, 1f);
    [SerializeField] private Color buttonTextColor = Color.white;

    private readonly List<Button> spawnedButtons = new List<Button>();

    public int SelectedIndex { get; private set; } = -1;
    public event Action<Transform> DestinationChanged;

    private void Start() { BuildButtons(); }

    public static string LabelFor(Transform t)
    {
        if (t == null) return "";
        string n = t.name;
        if (n.StartsWith("Destination_")) n = n.Substring("Destination_".Length);
        return n.Replace('_', ' ');
    }

    public void SelectDestination(int targetIndex)
    {
        if (pathVisualizer == null) return;
        if (targetIndex < 0 || targetIndex >= targets.Count) return;

        SelectedIndex = targetIndex;
        pathVisualizer.SetDestination(targets[targetIndex]);
        UpdateButtonHighlights();
        DestinationChanged?.Invoke(targets[targetIndex]);
    }

    public void SetDestinationByTransform(Transform newTarget)
    {
        if (pathVisualizer == null) return;
        SelectedIndex = targets.IndexOf(newTarget);
        pathVisualizer.SetDestination(newTarget);
        UpdateButtonHighlights();
        DestinationChanged?.Invoke(newTarget);
    }

    public void ClearDestination()
    {
        SelectedIndex = -1;
        if (pathVisualizer != null) pathVisualizer.SetDestination(null);
        UpdateButtonHighlights();
        DestinationChanged?.Invoke(null);
    }

    private void BuildButtons()
    {
        if (targets == null || targets.Count == 0) return;

        RectTransform container = buttonContainer;
        Button template = buttonPrefab;
        bool hideTemplateAfter = false;

        if ((container == null || template == null) && autoDiscoverBottomButton)
        {
            template = FindFallbackTemplateButton();
            if (template != null && template.transform.parent != null)
            {
                container = CreateButtonBar(template);
                hideTemplateAfter = true;
            }
        }

        if (container == null || template == null) return;

        spawnedButtons.Clear();
        for (int i = 0; i < targets.Count; i++)
        {
            int index = i; // capture
            Button b = Instantiate(template, container, false);
            b.name = "DestBtn_" + LabelFor(targets[i]);
            b.gameObject.SetActive(true);
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(() => SelectDestination(index));
            ApplyButtonLabel(b, LabelFor(targets[i]));
            spawnedButtons.Add(b);
        }

        if (hideTemplateAfter)
            template.gameObject.SetActive(false);

        UpdateButtonHighlights();
    }

    private Button FindFallbackTemplateButton()
    {
        Button namedTesting = null;
        foreach (Button btn in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (btn == null || string.IsNullOrEmpty(btn.gameObject.scene.name)) continue;
            if (string.Equals(btn.name, "TESTING", StringComparison.OrdinalIgnoreCase))
            {
                namedTesting = btn;
                break;
            }
            for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++)
            {
                if (btn.onClick.GetPersistentTarget(i) == this)
                    return btn;
            }
        }
        return namedTesting;
    }

    private static RectTransform CreateButtonBar(Button template)
    {
        var templateRect = template.GetComponent<RectTransform>();
        float yPos = templateRect != null ? templateRect.anchoredPosition.y : 50f;
        float height = templateRect != null ? Mathf.Clamp(templateRect.sizeDelta.y, 100f, 160f) : 140f;

        var barGo = new GameObject("DestinationButtonsBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        barGo.transform.SetParent(template.transform.parent, false);
        barGo.layer = template.gameObject.layer;

        var barRect = barGo.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0f, 0.5f);
        barRect.anchorMax = new Vector2(1f, 0.5f);
        barRect.pivot = new Vector2(0.5f, 0.5f);
        barRect.anchoredPosition = new Vector2(0f, yPos);
        barRect.sizeDelta = new Vector2(-40f, height);

        var layout = barGo.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 16f;
        layout.padding = new RectOffset(8, 8, 4, 4);
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;
        return barRect;
    }

    private void ApplyButtonLabel(Button btn, string labelText)
    {
        var tmp = btn.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
        {
            tmp.text = labelText;
            tmp.color = buttonTextColor;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.margin = new Vector4(12f, 6f, 12f, 6f);
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 18f;
            tmp.fontSizeMax = 40f;
            return;
        }

        var legacy = btn.GetComponentInChildren<Text>(true);
        if (legacy != null)
        {
            legacy.text = labelText;
            legacy.color = buttonTextColor;
            legacy.alignment = TextAnchor.MiddleCenter;
        }
    }

    private void UpdateButtonHighlights()
    {
        for (int i = 0; i < spawnedButtons.Count; i++)
        {
            Button b = spawnedButtons[i];
            if (b == null) continue;
            Color targetColor = i == SelectedIndex ? selectedButtonColor : normalButtonColor;
            // Keep targetGraphic white because Button's ColorTint multiplies ColorBlock by targetGraphic.color.
            if (b.targetGraphic != null) b.targetGraphic.color = Color.white;

            ColorBlock cb = b.colors;
            cb.colorMultiplier = 1f;
            cb.normalColor = targetColor;
            cb.highlightedColor = Color.Lerp(targetColor, Color.white, 0.12f);
            cb.selectedColor = targetColor;
            cb.pressedColor = Color.Lerp(targetColor, Color.black, 0.2f);
            b.colors = cb;
        }
    }
}
