using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Collections;
using Assets.Scripts;

public class PanelTypeBController : MonoBehaviour
{
    public enum PanelState { ExperimentSelect, Intro, Tutorial, Running }
    public enum ExperimentType { None, Simple, ChemLab }

    [Header("Sections")]
    public GameObject experimentSelectSection;
    public GameObject introSection;
    public GameObject tutorialSection;
    public GameObject runningSection;

    [Header("Experiment Select")]
    public TMP_Text selectTitleText;
    public TMP_Text selectBodyText;
    public GameObject simpleTaskButton;
    public GameObject chemLabTaskButton;

    [Header("Intro")]
    public TMP_Text introTitleText;
    public TMP_Text introBodyText;

    [Header("Tutorial")]
    public TMP_Text tutorialTitleText;
    public TMP_Text tutorialBodyText;

    [Header("Running — Step")]
    public TMP_Text stepText;

    [Header("Running — Voice")]
    public Image    voiceIndicatorDot;
    public TMP_Text voiceAnswerText;

    [Header("Running — User State")]
    public TMP_Text userStateLabel;
    public Image    userStateBar;

    [Header("Running — Progress")]
    public TMP_Text progressText;

    [Header("Shared")]
    public GameObject okButton;

    [Header("Calibration Server")]
    public string calibrationServerIP = "192.168.1.25";
    public int    calibrationPort     = 8001;

    [Header("State")]
    public PanelState   currentState       = PanelState.ExperimentSelect;
    public ExperimentType selectedExperiment = ExperimentType.None;

    private static readonly Color DotIdle      = new Color(0.5f, 0.5f, 0.5f, 0.5f);
    private static readonly Color DotListening = new Color(0.2f, 0.85f, 0.4f, 1f);
    private static readonly Color DotThinking  = new Color(1f, 0.8f, 0.1f, 1f);

    private const float StrugglingHigh = 0.65f;
    private const float StrugglingLow  = 0.40f;

    private string _pendingStep    = "";
    private string _pendingMessage = "";
    private int    _pendingDone    = 0;
    private int    _pendingTotal   = 0;
    private bool   _hasPending     = false;

    private float _calibrationStartTime = 0f;

    private void Start() => ShowExperimentSelect();

    // =========================================================================
    // EXPERIMENT SELECTION
    // =========================================================================

    public void ShowExperimentSelect()
    {
        currentState = PanelState.ExperimentSelect;
        experimentSelectSection.SetActive(true);
        introSection.SetActive(false);
        tutorialSection.SetActive(false);
        runningSection.SetActive(false);
        okButton.SetActive(false);

        selectTitleText.text = "Select Task Type";
        selectBodyText.text  = "Please choose the experiment you will perform today.";
    }

    public void OnSimpleTaskSelected()
    {
        selectedExperiment = ExperimentType.Simple;
        Debug.Log("[PanelTypeB] Experiment selected: Simple");
        var yolo = FindObjectOfType<YoloRecognitionHandler>();
        yolo?.SetExperimentType(false);
        // Notify Python coordinator to load simple protocol
        var voiceManager = FindObjectOfType<VoiceQueryManager>();
        voiceManager?.SendControlSignal("EXPERIMENT_TYPE:simple");
        ShowIntro();
    }

    public void OnChemLabTaskSelected()
    {
        selectedExperiment = ExperimentType.ChemLab;
        Debug.Log("[PanelTypeB] Experiment selected: ChemLab");
        var yolo = FindObjectOfType<YoloRecognitionHandler>();
        yolo?.SetExperimentType(true);
        // Notify Python coordinator to load chem lab protocol
        var voiceManager = FindObjectOfType<VoiceQueryManager>();
        voiceManager?.SendControlSignal("EXPERIMENT_TYPE:chemlab");
        ShowIntro();
    }

    // =========================================================================
    // FLOW
    // =========================================================================

    public void OnOkPressed()
    {
        if (currentState == PanelState.Intro)         ShowTutorial();
        else if (currentState == PanelState.Tutorial) ShowRunning();
    }

    public void ShowIntro()
    {
        currentState = PanelState.Intro;
        experimentSelectSection.SetActive(false);
        introSection.SetActive(true);
        tutorialSection.SetActive(false);
        runningSection.SetActive(false);
        okButton.SetActive(true);

        bool isChem = selectedExperiment == ExperimentType.ChemLab;

        introTitleText.text = isChem
            ? "Chemistry Lab Experiment"
            : "Liquid Transfer Experiment";

        introBodyText.text = isChem
            ? "In this task, you will perform a series of chemistry lab procedures.\n\n" +
              "Before the experiment starts, you will have a short practice phase to " +
              "familiarise yourself with the objects and the system.\n\n" +
              "Press OK to continue."
            : "In this task, you will transfer liquid between laboratory containers.\n\n" +
              "Before the experiment starts, you will have a short practice phase to " +
              "familiarise yourself with the objects and the system.\n\n" +
              "Press OK to continue.";
    }

    public void ShowTutorial()
    {
        currentState = PanelState.Tutorial;
        introSection.SetActive(false);
        tutorialSection.SetActive(true);
        runningSection.SetActive(false);
        okButton.SetActive(true);

        tutorialTitleText.text = "Practice Phase";
        tutorialBodyText.text  =
            "Please interact naturally with the objects on the table for a few minutes.\n\n" +
            "1. Scan the table — all objects should show a <color=#FFFFFF>•</color> white indicator.\n" +
            "2. Look at an object — the indicator turns <color=#FF66CC>•</color> pink.\n" +
            "3. Grab an object — the indicator turns <color=#00FF88>•</color> green.\n\n" +
            "When you feel comfortable, press OK to begin the experiment.";

        StartCoroutine(StartCalibration());
    }

    public void ShowRunning()
    {
        StartCoroutine(EndCalibrationAndStart());

        currentState = PanelState.Running;
        experimentSelectSection.SetActive(false);
        introSection.SetActive(false);
        tutorialSection.SetActive(false);
        runningSection.SetActive(true);
        okButton.SetActive(false);

        SetVoiceIndicator(DotIdle);
        if (voiceAnswerText != null)
            voiceAnswerText.text = "Say 'tutor' to ask a question";

        if (userStateLabel != null) userStateLabel.text = "stable  0.00";
        if (userStateBar   != null) userStateBar.fillAmount = 0f;

        if (_hasPending)
            ApplyStepUI();
        else
        {
            if (stepText     != null) stepText.text     = "Waiting for first step...";
            if (progressText != null) progressText.text = "Progress: 0 / 0";
        }
    }

    // =========================================================================
    // CALIBRATION + EXPERIMENT START
    // =========================================================================

    private string CalibrationUrl(string endpoint) =>
        $"http://{calibrationServerIP}:{calibrationPort}{endpoint}";

    private IEnumerator StartCalibration()
    {
        _calibrationStartTime = Time.realtimeSinceStartup;
        Debug.Log("[Calibration] ▶ Starting calibration phase...");

        using var req = new UnityWebRequest(CalibrationUrl("/calibrate/start"), "POST");
        req.uploadHandler   = new UploadHandlerRaw(new byte[0]);
        req.downloadHandler = new DownloadHandlerBuffer();
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            Debug.Log($"[Calibration] ✅ Started — {req.downloadHandler.text}");
        else
            Debug.LogWarning($"[Calibration] ⚠️ Start failed: {req.error}");
    }

    private IEnumerator EndCalibrationAndStart()
    {
        float duration = Time.realtimeSinceStartup - _calibrationStartTime;
        Debug.Log($"[Calibration] ⏹ Ending after {duration:F1}s...");

        using var req = new UnityWebRequest(CalibrationUrl("/calibrate/end"), "POST");
        req.uploadHandler   = new UploadHandlerRaw(new byte[0]);
        req.downloadHandler = new DownloadHandlerBuffer();
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            Debug.Log($"[Calibration] ✅ Ended — {req.downloadHandler.text}");
        else
            Debug.LogWarning($"[Calibration] ⚠️ End failed: {req.error}");

        var voiceManager = FindObjectOfType<VoiceQueryManager>();
        if (voiceManager != null)
        {
            voiceManager.NotifyExperimentStarted();
            Debug.Log("[Panel] ✅ EXPERIMENT_START sent to Python");
        }
        else
        {
            Debug.LogWarning("[Panel] ⚠️ VoiceQueryManager not found — EXPERIMENT_START not sent");
        }
    }

    // =========================================================================
    // STEP / PROGRESS
    // =========================================================================

    public void UpdateStepUI(string stepDescription, string message, int done, int total)
    {
        _pendingStep    = stepDescription;
        _pendingMessage = message;
        _pendingDone    = done;
        _pendingTotal   = total;
        _hasPending     = true;

        if (currentState != PanelState.Running) return;
        ApplyStepUI();
    }

    private void ApplyStepUI()
    {
        if (stepText        != null) stepText.text = _pendingStep;
        if (voiceAnswerText != null && !string.IsNullOrEmpty(_pendingMessage))
            voiceAnswerText.text = _pendingMessage;
        if (progressText    != null) progressText.text = $"Progress: {_pendingDone} / {_pendingTotal}";
    }

    // =========================================================================
    // VOICE
    // =========================================================================

    public enum VoiceState { Idle, Listening, Thinking }

    public void UpdateVoiceState(VoiceState state, string answer = "")
    {
        if (currentState != PanelState.Running) return;

        switch (state)
        {
            case VoiceState.Idle:
                SetVoiceIndicator(DotIdle);
                if (!string.IsNullOrEmpty(answer) && voiceAnswerText != null)
                    voiceAnswerText.text = answer;
                break;
            case VoiceState.Listening:
                SetVoiceIndicator(DotListening);
                if (voiceAnswerText != null) voiceAnswerText.text = "Listening...";
                break;
            case VoiceState.Thinking:
                SetVoiceIndicator(DotThinking);
                if (voiceAnswerText != null) voiceAnswerText.text = "Thinking...";
                break;
        }
    }

    private void SetVoiceIndicator(Color c)
    {
        if (voiceIndicatorDot != null) voiceIndicatorDot.color = c;
    }

    // =========================================================================
    // USER STATE
    // =========================================================================

    public void UpdateUserState(float pStruggling)
{
    if (currentState != PanelState.Running) return;  // ← добавить

    if (userStateBar != null)
        userStateBar.fillAmount = Mathf.Clamp01(pStruggling);

    if (userStateLabel != null)
    {
        if (pStruggling >= StrugglingHigh)
        {
            userStateLabel.text  = $"struggling  {pStruggling:F2}";
            userStateLabel.color = new Color(0.85f, 0.2f, 0.2f);
        }
        else if (pStruggling >= StrugglingLow)
        {
            userStateLabel.text  = $"borderline  {pStruggling:F2}";
            userStateLabel.color = new Color(0.9f, 0.6f, 0.1f);
        }
        else
        {
            userStateLabel.text  = $"stable  {pStruggling:F2}";
            userStateLabel.color = new Color(0.2f, 0.75f, 0.4f);
        }
    }
}
}