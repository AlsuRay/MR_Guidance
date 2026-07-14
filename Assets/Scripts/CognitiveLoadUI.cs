using UnityEngine;
using TMPro;

/// <summary>
/// Displays user state (stable/struggling) and P(struggling) on UI.
/// </summary>
public class CognitiveLoadUI : MonoBehaviour
{
    [Header("References")]
    public CognitiveLoadSystem cognitiveLoadSystem;
    public TextMeshProUGUI cognitiveLoadText;
    public TextMeshProUGUI affectText;  // reused for p_struggling display

    [Header("Display Settings")]
    public float updateInterval = 0.5f;

    private float lastUpdateTime = 0f;

    void Start()
    {
        if (cognitiveLoadSystem == null)
            cognitiveLoadSystem = FindObjectOfType<CognitiveLoadSystem>();

        if (cognitiveLoadSystem == null)
        {
            Debug.LogError("[CognitiveLoadUI] CognitiveLoadSystem not found!");
            enabled = false;
            return;
        }

        if (cognitiveLoadText == null || affectText == null)
        {
            foreach (var text in GetComponentsInChildren<TextMeshProUGUI>())
            {
                if (text.gameObject.name == "CognitiveLoadText") cognitiveLoadText = text;
                else if (text.gameObject.name == "AffectText")   affectText        = text;
            }
        }

        if (cognitiveLoadText == null || affectText == null)
        {
            Debug.LogError("[CognitiveLoadUI] Text components not found!");
            enabled = false;
            return;
        }

        cognitiveLoadText.text = "User State: Initializing...";
        affectText.text        = "P(struggling): --";
    }

    void Update()
    {
        if (Time.realtimeSinceStartup - lastUpdateTime >= updateInterval)
        {
            UpdateUI();
            lastUpdateTime = Time.realtimeSinceStartup;
        }
    }

    void UpdateUI()
    {
        string userState  = cognitiveLoadSystem.GetUserState();
        float  pStruggle  = cognitiveLoadSystem.GetPStruggling();
        float  pEma       = cognitiveLoadSystem.GetPStrugglingEma();
        float  timeSince  = cognitiveLoadSystem.GetTimeSinceLastPred();

        string color = userState == "struggling" ? "#FF4444" :
                       userState == "stable"     ? "#44FF88" : "#FFFFFF";

        cognitiveLoadText.text = $"User State: <color={color}>{userState.ToUpper()}</color>";
        affectText.text        = $"P(struggling): {pStruggle:F2}  EMA: {pEma:F2}";

        if (timeSince > 15f)
        {
            cognitiveLoadText.text += " <color=#808080>(stale)</color>";
            affectText.text        += " <color=#808080>(stale)</color>";
        }
    }
}