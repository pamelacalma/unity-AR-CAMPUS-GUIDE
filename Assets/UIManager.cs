using UnityEngine;

public class UIManager : MonoBehaviour
{
    [Header("UI Panels")]
    public GameObject splashPanel;
    public GameObject mainMenuPanel;
    public GameObject arNavPanel;
    public GameObject map2DPanel;
    public GameObject feedbackPanel;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        ShowMainMenu();
        
    }
    public void ShowSplash() => ActivatePanel(splashPanel);
    public void ShowMainMenu() => ActivatePanel(mainMenuPanel);
    public void ShowARNav() => ActivatePanel(arNavPanel);
    public void Show2DMap() => ActivatePanel(map2DPanel);
    public void ShowFeedback() => ActivatePanel(feedbackPanel);
    
    private void ActivatePanel(GameObject panelToKeepActive) {
        if (splashPanel != null) splashPanel.SetActive(splashPanel == panelToKeepActive);
        if (mainMenuPanel != null) mainMenuPanel.SetActive(mainMenuPanel == panelToKeepActive);
        if (arNavPanel != null) arNavPanel.SetActive(arNavPanel == panelToKeepActive);
        if (map2DPanel != null) map2DPanel.SetActive(map2DPanel == panelToKeepActive);
        if (feedbackPanel != null) feedbackPanel.SetActive(feedbackPanel == panelToKeepActive);
    }

// Update is called once per frame
void Update()
    {
        
    }
}
