using TMPro;
using UnityEngine;

public class PanelTypeAController : MonoBehaviour
{
    public enum PanelState { Intro, Tutorial, Running }

    [Header("Sections")]
    public GameObject introSection;
    public GameObject tutorialSection;
    public GameObject runningSection;

    [Header("Intro")]
    public TMP_Text introTitleText;
    public TMP_Text introBodyText;

    [Header("Tutorial")]
    public TMP_Text tutorialTitleText;
    public TMP_Text tutorialBodyText;

    [Header("Running")]
    public TMP_Text stepText;
    public TMP_Text feedbackText;
    public TMP_Text progressText;

    [Header("Shared")]
    public GameObject okButton;

    [Header("State")]
    public PanelState currentState = PanelState.Intro;

    private void Start() => ShowIntro();

    public void OnOkPressed()
    {
        if (currentState == PanelState.Intro) ShowTutorial();
        else if (currentState == PanelState.Tutorial) ShowRunning();
    }

    public void ShowIntro()
    {
        currentState = PanelState.Intro;
        introSection.SetActive(true);
        tutorialSection.SetActive(false);
        runningSection.SetActive(false);
        okButton.SetActive(true);

        introTitleText.text = "Liquid Transfer Experiment";
        introBodyText.text = "In this task, you will transfer liquid between laboratory containers.\n\nPress OK to continue.";
    }

    public void ShowTutorial()
    {
        currentState = PanelState.Tutorial;
        introSection.SetActive(false);
        tutorialSection.SetActive(true);
        runningSection.SetActive(false);
        okButton.SetActive(true);

        tutorialTitleText.text = "Before you begin";
        tutorialBodyText.text =
            "1. Scan the table and make sure all objects have a <color=#FFFFFF>•</color> white indicator.\n" +
            "2. Look at an object and make sure the indicator turns <color=#FF66CC>•</color> pink.\n" +
            "3. After grabbing it, the indicator should turn <color=#00FF88>•</color> green.\n\n" +
            "If everything is clear, press OK.";
    }

    public void ShowRunning()
    {
        currentState = PanelState.Running;
        introSection.SetActive(false);
        tutorialSection.SetActive(false);
        runningSection.SetActive(true);
        okButton.SetActive(false);

        stepText.text = "Waiting for first step...";
        feedbackText.text = "";
        progressText.text = "Progress: 0 / 0";
    }

    public void UpdateStepUI(string step, string feedback, int done, int total)
    {
        if (currentState != PanelState.Running) return;
        stepText.text = step;
        feedbackText.text = feedback;
        progressText.text = $"Progress: {done} / {total}";
    }
}