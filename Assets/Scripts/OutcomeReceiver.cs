using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[Serializable]
public class OutcomeData
{
    public float  t_unity;
    public string outcome;
    public int    step_id;
    public string message;
    public string action_type;
    public string object_id;
    public string target_id;
    public int    progress_done;
    public int    progress_total;
    public string step_description;
}

[Serializable]
public class UserStateData
{
    public float t_unity;
    public float p_struggling;
}

public class OutcomeReceiver : MonoBehaviour
{
    [Header("Network Settings")]
    [SerializeField] private int listenPort    = 5006;
    [SerializeField] private int userStatePort = 5008;

    [Header("Panel Reference")]
    [SerializeField] private PanelTypeBController panelController;

    [Header("Optional Logging")]
    [SerializeField] private bool   logToFile  = true;
    [SerializeField] private string logFileName = "user_perceived_latency";

    private UdpClient udpClient;
    private UdpClient userStateClient;
    private Thread    receiveThread;
    private Thread    userStateThread;
    private bool      isRunning = false;

    private readonly Queue<OutcomeData> outcomeQueue   = new Queue<OutcomeData>();
    private readonly Queue<float>       userStateQueue = new Queue<float>();
    private readonly object             queueLock      = new object();

    private readonly List<LatencyRecord> latencyRecords = new List<LatencyRecord>();
    private int   totalOutcomes = 0;
    private float totalLatency  = 0f;

    [Serializable]
    public class LatencyRecord
    {
        public float  t_action, t_received, latency_ms;
        public string outcome, action_type, object_id;
    }

    private void Start()
    {
        if (panelController == null)
            Debug.LogWarning("[OutcomeReceiver] PanelTypeBController not assigned.");
        StartReceiving();
    }

    private void Update()
    {
        lock (queueLock)
        {
            while (outcomeQueue.Count > 0)
                ProcessOutcome(outcomeQueue.Dequeue());

            while (userStateQueue.Count > 0)
                panelController?.UpdateUserState(userStateQueue.Dequeue());
        }
    }

    private void OnDestroy()         { StopReceiving(); SaveLatencyLog(); }
    private void OnApplicationQuit() { StopReceiving(); SaveLatencyLog(); }

    private void StartReceiving()
    {
        // Outcome listener (port 5006)
        try
        {
            udpClient     = new UdpClient(listenPort);
            isRunning     = true;
            receiveThread = new Thread(OutcomeLoop) { IsBackground = true };
            receiveThread.Start();
            Debug.Log($"[OutcomeReceiver] Listening on port {listenPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[OutcomeReceiver] Failed to start: {e.Message}");
        }

        // User state listener (port 5008)
        try
        {
            userStateClient = new UdpClient(userStatePort);
            userStateThread = new Thread(UserStateLoop) { IsBackground = true };
            userStateThread.Start();
            Debug.Log($"[UserState] Listening on port {userStatePort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[UserState] Failed to start: {e.Message}");
        }
    }

    private void StopReceiving()
    {
        isRunning = false;
        try { udpClient?.Close();       } catch { }
        try { userStateClient?.Close(); } catch { }
        try { receiveThread?.Join(500);  } catch { }
        try { userStateThread?.Join(500); } catch { }
    }

    private void OutcomeLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        while (isRunning)
        {
            try
            {
                byte[]      data    = udpClient.Receive(ref ep);
                OutcomeData outcome = JsonUtility.FromJson<OutcomeData>(
                    Encoding.UTF8.GetString(data));
                if (outcome == null) continue;
                lock (queueLock) { outcomeQueue.Enqueue(outcome); }
            }
            catch (SocketException) { break; }
            catch (Exception e)
            {
                if (isRunning) Debug.LogError($"[OutcomeReceiver] {e.Message}");
            }
        }
    }

    private void UserStateLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        while (isRunning)
        {
            try
            {
                byte[]        data = userStateClient.Receive(ref ep);
                UserStateData msg  = JsonUtility.FromJson<UserStateData>(
                    Encoding.UTF8.GetString(data));
                if (msg == null) continue;
                lock (queueLock) { userStateQueue.Enqueue(msg.p_struggling); }
            }
            catch (SocketException) { break; }
            catch (Exception e)
            {
                if (isRunning) Debug.LogError($"[UserState] {e.Message}");
            }
        }
    }

    private void ProcessOutcome(OutcomeData data)
    {
        panelController?.UpdateStepUI(
            data.step_description,
            data.message,
            data.progress_done,
            data.progress_total);
        MeasureLatency(data);
        if (data.progress_total > 0 && data.progress_done >= data.progress_total)
            Debug.Log("[OutcomeReceiver] Protocol complete.");
    }

    private void MeasureLatency(OutcomeData data)
    {
        float tNow      = Time.realtimeSinceStartup;
        float latencyMs = data.t_unity > 0f ? (tNow - data.t_unity) * 1000f : 0f;
        latencyRecords.Add(new LatencyRecord
        {
            t_action    = data.t_unity,
            t_received  = tNow,
            latency_ms  = latencyMs,
            outcome     = data.outcome,
            action_type = data.action_type,
            object_id   = data.object_id
        });
        if (latencyMs > 0f)
        {
            totalOutcomes++;
            totalLatency += latencyMs;
            Debug.Log($"[LATENCY] {data.action_type}({data.object_id}) → {data.outcome}: {latencyMs:F1} ms");
        }
    }

    private void SaveLatencyLog()
    {
        if (!logToFile || latencyRecords.Count == 0) return;
        try
        {
            string path = Path.Combine(Application.persistentDataPath,
                $"{logFileName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using var w = new StreamWriter(path);
            w.WriteLine("t_action,t_received,latency_ms,outcome,action_type,object_id");
            foreach (var r in latencyRecords)
                w.WriteLine($"{r.t_action:F4},{r.t_received:F4},{r.latency_ms:F2}," +
                            $"{r.outcome},{r.action_type},{r.object_id}");
            Debug.Log($"[OutcomeReceiver] Saved: {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[OutcomeReceiver] Save failed: {e.Message}");
        }
    }

    public float               GetMeanLatency()     => totalOutcomes == 0 ? 0f : totalLatency / totalOutcomes;
    public int                 GetTotalOutcomes()   => totalOutcomes;
    public List<LatencyRecord> GetLatencyRecords()  => new List<LatencyRecord>(latencyRecords);
}