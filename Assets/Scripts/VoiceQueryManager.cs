using UnityEngine;
using UnityEngine.Windows.Speech;
using UnityEngine.UI;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TMPro;
using System.Threading;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts;

#if WINDOWS_UWP
using Windows.Media.SpeechSynthesis;
using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.Foundation;
using System.Runtime.InteropServices.WindowsRuntime;
#endif

[System.Serializable]
public class LLMResponse
{
    public string answer;
    public string highlight_object;
}

public class VoiceQueryManager : MonoBehaviour
{
    [Header("Network Settings")]
    public string pythonServerIP = "192.168.1.25";
    public int sendPort    = 5010;
    public int receivePort = 5011;

    [Header("Voice Settings")]
    public string[] activationKeywords = new string[] { "tutor", "question", "help" };
    public bool enableTTS = true;

    [Header("UI")]
    public TextMeshProUGUI answerText;

    [Header("Panel Reference")]
    public PanelTypeBController panelController;

    [Header("Highlight Settings")]
    public float highlightSearchTimeout = 15f;
    public float highlightHoldDuration  = 3f;

    [Header("Visual Feedback")]
    public GameObject        indicatorCanvas;
    public Image             indicatorIcon;
    public TextMeshProUGUI   indicatorText;
    public Color listeningColor = Color.green;
    public Color thinkingColor  = Color.yellow;
    public Color idleColor      = new Color(0.5f, 0.5f, 0.5f, 0.3f);
    public float pulseSpeed     = 2f;

    // Voice
    private KeywordRecognizer   keywordRecognizer;
    private DictationRecognizer dictationRecognizer;

    // Network
    private UdpClient sendClient;
    private Socket    receiveSocket;
    private Thread    receiveThread;
    private bool      isReceiving = false;

    // State
    private bool    isListening = false;
    private bool    isThinking  = false;
    private Vector3 originalScale;

    private Coroutine activeSpeakCoroutine;

    // Highlight
    private List<Coroutine>      activeHighlightCoroutines = new List<Coroutine>();
    private YoloRecognitionHandler _yoloHandler;

    // Queue
    private System.Collections.Concurrent.ConcurrentQueue<string> responseQueue =
        new System.Collections.Concurrent.ConcurrentQueue<string>();

    // Logging
    private string logPath;

    private void WriteLog(string msg)
    {
        try { File.AppendAllText(logPath, $"[{System.DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
        Debug.Log(msg);
    }

    void Start()
    {
        logPath = Path.Combine(Application.persistentDataPath, "voice_debug.txt");
        File.WriteAllText(logPath, $"=== Voice Debug Log {System.DateTime.Now} ===\n");
        WriteLog("Start() called");

        _yoloHandler = FindObjectOfType<YoloRecognitionHandler>();
        if (_yoloHandler == null)
            WriteLog("⚠️ YoloRecognitionHandler not found");

        sendClient = new UdpClient();
        WriteLog($"UDP send client → {pythonServerIP}:{sendPort}");

        StartReceiving();

        if (indicatorCanvas != null)
            originalScale = indicatorCanvas.transform.localScale;

        try
        {
            keywordRecognizer = new KeywordRecognizer(activationKeywords);
            keywordRecognizer.OnPhraseRecognized += OnKeywordDetected;
            keywordRecognizer.Start();
            WriteLog($"KeywordRecognizer started: {string.Join(", ", activationKeywords)}");
        }
        catch (System.Exception e)
        {
            WriteLog($"KeywordRecognizer FAILED: {e.Message}");
        }

        SetIndicatorState(IndicatorState.Idle);
        WriteLog("Start() complete");

        panelController?.UpdateVoiceState(
            PanelTypeBController.VoiceState.Idle,
            $"Say '{activationKeywords[0]}' to start"
        );
    }

void StopSpeaking()
{
    // Останавливаем корутину загрузки аудио, если она еще идет
    if (activeSpeakCoroutine != null)
    {
        StopCoroutine(activeSpeakCoroutine);
        activeSpeakCoroutine = null;
    }

    // Останавливаем сам звук
    AudioSource audio = GetComponent<AudioSource>();
    if (audio != null && audio.isPlaying)
    {
        audio.Stop();
        WriteLog("TTS playback interrupted by user.");
    }
    
    // Опционально: можно послать сигнал на Python-сервер, что мы прервали речь
    // SendControlSignal("SPEAKING_END"); 
}

void Speak(string text)
{
    if (!enableTTS) return;
    
    StopSpeaking(); // Останавливаем старую речь перед началом новой
    
    SendControlSignal("SPEAKING_START");
    activeSpeakCoroutine = StartCoroutine(SpeakAzure(text));
}



private IEnumerator SpeakAzure(string text)
{
    string apiKey = "6huRG87EVbaHt6bpAkbj3xSQjIkG0XUIsr3KlNv1M0sr5m6ceT61JQQJ99CEACNns7RXJ3w3AAAYACOGtSsD";
    string region = "koreacentral";

    string safeText = text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    string ssml = $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>
        <voice name='en-US-SaraNeural'>
            <prosody rate='0.95' pitch='+1Hz'>{safeText}</prosody>
        </voice>
    </speak>";

    string url = $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1";

    using var req = new UnityEngine.Networking.UnityWebRequest(url, "POST");
    byte[] bodyRaw = Encoding.UTF8.GetBytes(ssml);
    req.uploadHandler   = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
    req.downloadHandler = new UnityEngine.Networking.DownloadHandlerAudioClip(url, AudioType.MPEG);
    req.SetRequestHeader("Ocp-Apim-Subscription-Key", apiKey);
    req.SetRequestHeader("Content-Type", "application/ssml+xml");
    req.SetRequestHeader("X-Microsoft-OutputFormat", "audio-16khz-128kbitrate-mono-mp3");

    yield return req.SendWebRequest();

    if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
    {
        AudioClip clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(req);
        AudioSource audio = GetComponent<AudioSource>();
        if (audio == null) audio = gameObject.AddComponent<AudioSource>();
        audio.clip = clip;
        audio.Play();
        WriteLog("Azure TTS playing");
        yield return new WaitUntil(() => !audio.isPlaying);
    }
    else
    {
        WriteLog($"Azure TTS error: {req.error} | {req.downloadHandler.text}");
    }

    SendControlSignal("SPEAKING_END");
}

    void StartReceiving()
    {
        try
        {
            receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiveSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            receiveSocket.Bind(new IPEndPoint(IPAddress.Any, receivePort));
            isReceiving   = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();
            WriteLog($"Socket bound to port {receivePort}");
        }
        catch (System.Exception e)
        {
            WriteLog($"StartReceiving FAILED: {e.Message}");
        }
    }

    void ReceiveLoop()
    {
        byte[] buffer = new byte[65536];
        while (isReceiving)
        {
            try
            {
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                int bytes = receiveSocket.ReceiveFrom(buffer, ref remoteEP);
                if (bytes > 0)
                    responseQueue.Enqueue(Encoding.UTF8.GetString(buffer, 0, bytes));
            }
            catch (System.Exception e)
            {
                if (isReceiving) WriteLog($"ReceiveLoop error: {e.Message}");
            }
        }
    }

    void Update()
    {
        while (responseQueue.TryDequeue(out string json))
            ProcessAnswer(json);

        if (indicatorCanvas != null && (isListening || isThinking))
        {
            float scale = 1f + Mathf.Sin(Time.time * pulseSpeed) * 0.15f;
            indicatorCanvas.transform.localScale = originalScale * scale;
            if (indicatorIcon != null)
            {
                Color c = indicatorIcon.color;
                c.a = 0.5f + Mathf.Sin(Time.time * pulseSpeed * 2f) * 0.3f;
                indicatorIcon.color = c;
            }
        }
    }

    void ProcessAnswer(string jsonString)
    {
        Debug.Log($"[RAW RECEIVED] {jsonString}");
        try
        {
            WriteLog($"ProcessAnswer: {jsonString.Substring(0, Mathf.Min(80, jsonString.Length))}");
            LLMResponse response = JsonUtility.FromJson<LLMResponse>(jsonString);

            isThinking = false;
            SetIndicatorState(IndicatorState.Idle);
            panelController?.UpdateVoiceState(PanelTypeBController.VoiceState.Idle, response.answer);
            if (answerText != null)
                answerText.text = response.answer;

            WriteLog($"Answer: {response.answer}");

            if (enableTTS) Speak(response.answer);

            if (!string.IsNullOrEmpty(response.highlight_object))
            {
                foreach (var c in activeHighlightCoroutines)
                    if (c != null) StopCoroutine(c);
                activeHighlightCoroutines.Clear();

                string[] objects = response.highlight_object.Split(',');
                foreach (string obj in objects)
                {
                    string trimmed = obj.Trim().ToLower();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        WriteLog($"Starting highlight for: {trimmed}");
                        activeHighlightCoroutines.Add(StartCoroutine(HighlightWhenVisible(trimmed)));
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            WriteLog($"ProcessAnswer error: {e.Message}");
            isThinking = false;
            SetIndicatorState(IndicatorState.Idle);
            panelController?.UpdateVoiceState(PanelTypeBController.VoiceState.Idle, "Error processing answer");
        }
    }

    // ── Control signals to Python ─────────────────────────────────────────────

    public void SendControlSignal(string signal)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(signal);
            sendClient.Send(bytes, bytes.Length, pythonServerIP, sendPort);
            WriteLog($"Signal sent: {signal}");
        }
        catch (System.Exception e)
        {
            WriteLog($"SendControlSignal FAILED: {e.Message}");
        }
    }

    public void NotifyExperimentStarted() => SendControlSignal("EXPERIMENT_START");

    // ── Highlight ─────────────────────────────────────────────────────────────

    private IEnumerator HighlightWhenVisible(string objectName)
    {
        _yoloHandler?.SetHighlight(objectName, true);
        MRTKGrab grab = FindGrabForObject(objectName);

        float elapsed = 0f;
        bool  gazed   = false;

        while (elapsed < highlightSearchTimeout)
        {
            if (grab != null && grab.IsGazed) { gazed = true; break; }
            yield return new WaitForSeconds(0.1f);
            elapsed += 0.1f;
        }

        if (gazed) yield return new WaitForSeconds(highlightHoldDuration);

        _yoloHandler?.SetHighlight(objectName, false);
        WriteLog($"Highlight cleared: {objectName} (gazed={gazed})");
    }

    private MRTKGrab FindGrabForObject(string objectName)
{
    string normalized = objectName.Replace("_", "").ToLower();
    var allGrabs = FindObjectsOfType<MRTKGrab>();
    foreach (var grab in allGrabs)
    {
        string grabNormalized = grab.objectName.Replace("_", "").ToLower();
        if (grabNormalized.Contains(normalized) || normalized.Contains(grabNormalized))
            return grab;
    }
    return null;
}

    // ── Voice recognition ─────────────────────────────────────────────────────

    void OnKeywordDetected(PhraseRecognizedEventArgs args)
    {
        WriteLog($"Keyword detected: {args.text}");
        StopSpeaking();
        SetIndicatorState(IndicatorState.Listening);
        panelController?.UpdateVoiceState(PanelTypeBController.VoiceState.Listening);
        StopKeywordRecognizer();
        Invoke("StartDictation", 0.3f);
    }

    void StopKeywordRecognizer()
    {
        if (keywordRecognizer != null && keywordRecognizer.IsRunning)
        {
            keywordRecognizer.Stop();
            WriteLog("KeywordRecognizer stopped");
        }
    }

    void StartDictation()
    {
        if (isListening) return;
        isListening = true;
        try
        {
            dictationRecognizer = new DictationRecognizer();
            dictationRecognizer.DictationResult     += OnDictationResult;
            dictationRecognizer.DictationError      += OnDictationError;
            dictationRecognizer.DictationHypothesis += (text) =>
            {
                if (answerText != null) answerText.text = $"Hearing: {text}...";
            };
            dictationRecognizer.Start();
            SendControlSignal("LISTENING_START");  // ← block proactive while listening
            WriteLog("DictationRecognizer started");
            Invoke("StopDictation", 15f);
        }
        catch (System.Exception e)
        {
            WriteLog($"Dictation FAILED: {e.Message}");
            isListening = false;
            SetIndicatorState(IndicatorState.Idle);
            RestartKeywordRecognizer();
        }
    }

    void OnDictationResult(string text, ConfidenceLevel confidence)
    {
        WriteLog($"DictationResult: '{text}'");
        SetIndicatorState(IndicatorState.Thinking);
        panelController?.UpdateVoiceState(PanelTypeBController.VoiceState.Thinking);
        SendQuestion(text);
        StopDictation();
    }

    void OnDictationError(string error, int hresult)
    {
        WriteLog($"DictationError: {error}");
        StopSpeaking(); // Останавливаем воспроизведение TTS при ошибке
        SetIndicatorState(IndicatorState.Idle);
        StopDictation();
    }

    void SendQuestion(string question)
    {
        isThinking = true;
        byte[] bytes = Encoding.UTF8.GetBytes(question);
        sendClient.Send(bytes, bytes.Length, pythonServerIP, sendPort);
        WriteLog($"Sent: {question}");
    }

    void StopDictation()
    {
        CancelInvoke("StopDictation");
        if (dictationRecognizer != null)
        {
            dictationRecognizer.DictationResult -= OnDictationResult;
            dictationRecognizer.DictationError  -= OnDictationError;
            dictationRecognizer.Stop();
            dictationRecognizer.Dispose();
            dictationRecognizer = null;
            WriteLog("DictationRecognizer stopped");
        }
        isListening = false;
        SendControlSignal("LISTENING_END");  // ← unblock proactive
        if (!isThinking) SetIndicatorState(IndicatorState.Idle);
        RestartKeywordRecognizer();
        if (!isThinking)
            panelController?.UpdateVoiceState(
                PanelTypeBController.VoiceState.Idle,
                $"Say '{activationKeywords[0]}' to ask again"
            );
    }

    void RestartKeywordRecognizer()
    {
        if (keywordRecognizer != null && !keywordRecognizer.IsRunning)
        {
            try
            {
                keywordRecognizer.Start();
                WriteLog("KeywordRecognizer restarted");
            }
            catch (System.Exception e)
            {
                WriteLog($"KeywordRecognizer restart FAILED: {e.Message}");
            }
        }
    }

    // ── Indicator ─────────────────────────────────────────────────────────────

    enum IndicatorState { Idle, Listening, Thinking }

    void SetIndicatorState(IndicatorState state)
    {
        switch (state)
        {
            case IndicatorState.Idle:
                if (indicatorIcon   != null) indicatorIcon.color = idleColor;
                if (indicatorText   != null) { indicatorText.text = "Ready"; indicatorText.color = idleColor; }
                if (indicatorCanvas != null) indicatorCanvas.transform.localScale = originalScale;
                break;
            case IndicatorState.Listening:
                if (indicatorIcon != null) indicatorIcon.color = listeningColor;
                if (indicatorText != null) { indicatorText.text = "Listening..."; indicatorText.color = listeningColor; }
                break;
            case IndicatorState.Thinking:
                if (indicatorIcon != null) indicatorIcon.color = thinkingColor;
                if (indicatorText != null) { indicatorText.text = "Thinking..."; indicatorText.color = thinkingColor; }
                break;
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    void OnDestroy()
    {
        CancelInvoke();
        foreach (var c in activeHighlightCoroutines)
            if (c != null) StopCoroutine(c);
        activeHighlightCoroutines.Clear();

        if (keywordRecognizer != null)
        {
            keywordRecognizer.OnPhraseRecognized -= OnKeywordDetected;
            if (keywordRecognizer.IsRunning) keywordRecognizer.Stop();
            keywordRecognizer.Dispose();
        }
        StopDictation();
        sendClient?.Close();
        isReceiving = false;
        receiveSocket?.Close();
        receiveThread?.Join(1000);
    }
}