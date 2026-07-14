using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class YoloGazeLogger : MonoBehaviour
{
    [Header("UDP Streaming")]
    public string remoteIP = "192.168.1.25";
    public int remotePort = 5005;

    private static UdpClient udpClient;
    private static bool udpInitialized = false;

    public string objectName;
    private static string filePath;
    private DateTime? gazeEnterTime = null;
    private float gazeEnterUnityTime = 0f;
    private static bool fileInitialized = false;

    private void Awake()
    {
        if (!fileInitialized)
        {
            filePath = Path.Combine(Application.persistentDataPath,
                $"gaze_log_{DateTime.Now:yyyyMMdd_HHmm}.csv");

            Debug.Log("Logging to: " + filePath);

            using (StreamWriter sw = new StreamWriter(filePath, false))
            {
                sw.WriteLine("Timestamp,Event,ObjectName,DwellTimeSeconds,ObjPosX,ObjPosY,ObjPosZ,UnityTime");
                sw.Flush();
            }

            fileInitialized = true;
        }

        if (!udpInitialized)
        {
            try
            {
                udpClient = new UdpClient();
                udpClient.Connect(remoteIP, remotePort);
                udpInitialized = true;
                Debug.Log($"[YoloGazeLogger] UDP connected to {remoteIP}:{remotePort}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[YoloGazeLogger] UDP init error: {ex.Message}");
            }
        }
    }

    void Start()
    {
        Debug.Log("YoloGazeLogger is active on " + objectName);
    }

    public void OnGazeEntered(HoverEnterEventArgs args)
    {
        string isoTime = DateTime.UtcNow.ToString("o");
        float unityTime = Time.realtimeSinceStartup;

        gazeEnterTime = DateTime.UtcNow;
        gazeEnterUnityTime = unityTime;

        Vector3 objectPosition = transform.position;
        WriteLogToFile(isoTime, "ENTER", 0, objectPosition, unityTime);

        var viz = GetComponent<ColliderVisualizer>();
        if (viz != null) viz.SetGazeActive(true);

        // ── Stop highlight when user looks at this object ─────────────────────
        var highlighter = GetComponent<ObjectHighlighter>();
        if (highlighter != null) highlighter.OnGazed();
    }

    public void OnGazeExited(HoverExitEventArgs args)
    {
        string isoTime = DateTime.UtcNow.ToString("o");
        float unityTime = Time.realtimeSinceStartup;

        double dwellSeconds = 0;
        if (gazeEnterTime.HasValue)
            dwellSeconds = (DateTime.UtcNow - gazeEnterTime.Value).TotalSeconds;

        Vector3 objectPosition = transform.position;
        float actionUnityTime = gazeEnterUnityTime > 0 ? gazeEnterUnityTime : unityTime;

        WriteLogToFile(isoTime, "EXIT", dwellSeconds, objectPosition, actionUnityTime);

        gazeEnterTime = null;
        gazeEnterUnityTime = 0f;

        var viz = GetComponent<ColliderVisualizer>();
        if (viz != null) viz.SetGazeActive(false);
    }

    private void WriteLogToFile(string timestamp, string eventType, double dwellTime,
                                 Vector3 objectPosition, float unityTime)
    {
        string line = $"{timestamp},{eventType},{objectName},{dwellTime:F2}," +
                      $"{objectPosition.x:F4},{objectPosition.y:F4},{objectPosition.z:F4},{unityTime:F4}";

        try
        {
            using (StreamWriter sw = new StreamWriter(filePath, true))
                sw.WriteLine(line);
        }
        catch { }

        if (udpInitialized && udpClient != null)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(line + "\n");
                udpClient.Send(data, data.Length);
            }
            catch { }
        }
    }
}