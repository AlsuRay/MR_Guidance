using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;
using MixedReality.Toolkit;
using MixedReality.Toolkit.Subsystems;
using MixedReality.Toolkit.Input;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System;

// ── Response wrapper from Python ─────────────────────────────────────────────
[Serializable]
public class UserStatePrediction
{
    public float t_unity;
    public string user_state;       // "stable" or "struggling"
    public float p_struggling;      // raw probability
    public float p_struggling_ema;  // EMA-smoothed probability
}

/// <summary>
/// Sends raw sensor frames to Python BiGRU inference server at 30Hz.
/// Replaces old WindowFeatureAggregator approach.
/// </summary>
public class CognitiveLoadSystem : MonoBehaviour
{
    [Header("Components")]
    public GazeInteractor gazeInteractor;

    [Header("Network Settings")]
    public string pythonIP   = "192.168.1.25";
    public int    pythonPort = 5007;
    public int    unityPort  = 5008;

    // ── Public state (read by other scripts e.g. adaptive_server) ────────────
    public string currentUserState     = "unknown";
    public float  pStruggling          = 0f;
    public float  pStrugglingEma       = 0f;
    public float  lastPredictionTime   = 0f;

    // ── Private ───────────────────────────────────────────────────────────────
    private HandsAggregatorSubsystem handsAggregator;
    private InputDevice              headDevice;
    private UdpClient                sendClient;
    private UdpClient                receiveClient;
    private IPEndPoint               pythonEndpoint;

    private string pendingUserState   = null;
    private float  pendingPStruggling = 0f;
    private float  pendingPEma        = 0f;
    private readonly object predLock  = new object();

    private float sensorInterval  = 1f / 30f;
    private float lastSensorTime  = 0f;

    // ── Joints needed for BiGRU (palm, wrist, index tip) ─────────────────────
    private const TrackedHandJoint PALM  = TrackedHandJoint.Palm;
    private const TrackedHandJoint WRIST = TrackedHandJoint.Wrist;
    private const TrackedHandJoint INDEX = TrackedHandJoint.IndexDistal;

    // =========================================================================
    void Start()
    {
        InitializeSubsystems();
        InitializeNetwork();
        Debug.Log($"[UserStateSystem] Ready → {pythonIP}:{pythonPort}, listening on {unityPort}");
    }

    void InitializeSubsystems()
    {
        handsAggregator = XRSubsystemHelpers.GetFirstRunningSubsystem<HandsAggregatorSubsystem>();
        if (handsAggregator == null)
        {
            Debug.LogError("❌ HandsAggregatorSubsystem not found!");
            enabled = false;
            return;
        }

        var headDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.Head, headDevices);
        if (headDevices.Count > 0) headDevice = headDevices[0];

        if (gazeInteractor == null)
            gazeInteractor = FindObjectOfType<GazeInteractor>();
    }

    void InitializeNetwork()
    {
        sendClient     = new UdpClient();
        pythonEndpoint = new IPEndPoint(IPAddress.Parse(pythonIP), pythonPort);
        receiveClient  = new UdpClient(unityPort);
        receiveClient.BeginReceive(OnReceivePrediction, null);
    }

    // =========================================================================
    void Update()
    {
        // apply pending prediction on main thread
        lock (predLock)
        {
            if (pendingUserState != null)
            {
                currentUserState   = pendingUserState;
                pStruggling        = pendingPStruggling;
                pStrugglingEma     = pendingPEma;
                lastPredictionTime = Time.realtimeSinceStartup;
                pendingUserState   = null;

                var panel = FindObjectOfType<PanelTypeBController>();
                panel?.UpdateUserState(pStrugglingEma);
            }
        }

        // send raw frame at 30Hz
        if (Time.realtimeSinceStartup - lastSensorTime >= sensorInterval)
        {
            SendRawFrame();
            lastSensorTime = Time.realtimeSinceStartup;
        }
    }

    // =========================================================================
    void SendRawFrame()
    {
        // ── Head ──────────────────────────────────────────────────────────────
        Vector3    headPos = Vector3.zero;
        Quaternion headRot = Quaternion.identity;
        if (headDevice.isValid)
        {
            headDevice.TryGetFeatureValue(CommonUsages.devicePosition, out headPos);
            headDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out headRot);
        }

        // ── Gaze ──────────────────────────────────────────────────────────────
        Vector3 gazeOrigin = Vector3.zero;
        Vector3 gazeDir    = Vector3.forward;
        if (gazeInteractor != null && gazeInteractor.rayOriginTransform != null)
        {
            gazeOrigin = gazeInteractor.rayOriginTransform.position;
            gazeDir    = gazeInteractor.rayOriginTransform.forward;
        }

        // ── Hands: palm, wrist, index ─────────────────────────────────────────
        Vector3 lPalm  = Vector3.zero, lWrist = Vector3.zero, lIndex = Vector3.zero;
        Vector3 rPalm  = Vector3.zero, rWrist = Vector3.zero, rIndex = Vector3.zero;

        if (handsAggregator != null)
        {
            if (handsAggregator.TryGetJoint(PALM,  XRNode.LeftHand,  out HandJointPose p)) lPalm  = p.Position;
            if (handsAggregator.TryGetJoint(WRIST, XRNode.LeftHand,  out HandJointPose w)) lWrist = w.Position;
            if (handsAggregator.TryGetJoint(INDEX, XRNode.LeftHand,  out HandJointPose i)) lIndex = i.Position;
            if (handsAggregator.TryGetJoint(PALM,  XRNode.RightHand, out HandJointPose rp)) rPalm  = rp.Position;
            if (handsAggregator.TryGetJoint(WRIST, XRNode.RightHand, out HandJointPose rw)) rWrist = rw.Position;
            if (handsAggregator.TryGetJoint(INDEX, XRNode.RightHand, out HandJointPose ri)) rIndex = ri.Position;
        }

        // ── Build JSON matching FRAME_KEYS in Python ──────────────────────────
        // Key order: gaze_ox,oy | gaze_dx,dy,dz | head_rx,ry,rz,rw | head_px,py,pz
        //            lp | lw | li | rp | rw | ri  (xyz each)
        var sb = new System.Text.StringBuilder();
        sb.Append("{");
        sb.Append($"\"t\":{Time.realtimeSinceStartup:F3},");

        // gaze origin (2)
        sb.Append($"\"gaze_ox\":{gazeOrigin.x:F4},\"gaze_oy\":{gazeOrigin.y:F4},");
        // gaze direction (3)
        sb.Append($"\"gaze_dx\":{gazeDir.x:F4},\"gaze_dy\":{gazeDir.y:F4},\"gaze_dz\":{gazeDir.z:F4},");
        // head rotation (4)
        sb.Append($"\"head_rx\":{headRot.x:F4},\"head_ry\":{headRot.y:F4},\"head_rz\":{headRot.z:F4},\"head_rw\":{headRot.w:F4},");
        // head position (3)
        sb.Append($"\"head_px\":{headPos.x:F4},\"head_py\":{headPos.y:F4},\"head_pz\":{headPos.z:F4},");
        // left palm (3)
        sb.Append($"\"lp_x\":{lPalm.x:F4},\"lp_y\":{lPalm.y:F4},\"lp_z\":{lPalm.z:F4},");
        // left wrist (3)
        sb.Append($"\"lw_x\":{lWrist.x:F4},\"lw_y\":{lWrist.y:F4},\"lw_z\":{lWrist.z:F4},");
        // left index (3)
        sb.Append($"\"li_x\":{lIndex.x:F4},\"li_y\":{lIndex.y:F4},\"li_z\":{lIndex.z:F4},");
        // right palm (3)
        sb.Append($"\"rp_x\":{rPalm.x:F4},\"rp_y\":{rPalm.y:F4},\"rp_z\":{rPalm.z:F4},");
        // right wrist (3)
        sb.Append($"\"rw_x\":{rWrist.x:F4},\"rw_y\":{rWrist.y:F4},\"rw_z\":{rWrist.z:F4},");
        // right index (3) — no trailing comma
        sb.Append($"\"ri_x\":{rIndex.x:F4},\"ri_y\":{rIndex.y:F4},\"ri_z\":{rIndex.z:F4}");
        sb.Append("}");

        byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
        sendClient.Send(data, data.Length, pythonEndpoint);
    }

    // =========================================================================
    void OnReceivePrediction(IAsyncResult result)
    {
        try
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] data = receiveClient.EndReceive(result, ref remoteEP);
            string json = Encoding.UTF8.GetString(data);

            UserStatePrediction pred = JsonUtility.FromJson<UserStatePrediction>(json);

            if (pred != null && !string.IsNullOrEmpty(pred.user_state))
            {
                lock (predLock)
                {
                    pendingUserState   = pred.user_state;
                    pendingPStruggling = pred.p_struggling;
                    pendingPEma        = pred.p_struggling_ema;
                }
                Debug.Log($"[UserState] {pred.user_state} | p={pred.p_struggling:F2} ema={pred.p_struggling_ema:F2}");
            }

            receiveClient.BeginReceive(OnReceivePrediction, null);
        }
        catch (Exception e)
        {
            Debug.LogError($"[UserState] Receive error: {e.Message}");
            receiveClient.BeginReceive(OnReceivePrediction, null);
        }
    }

    // =========================================================================
    void OnDestroy()
    {
        sendClient?.Close();
        receiveClient?.Close();
    }

    // ── Public accessors for other scripts ───────────────────────────────────
    public string GetUserState()           => currentUserState;
    public float  GetPStruggling()         => pStruggling;
    public float  GetPStrugglingEma()      => pStrugglingEma;
    public bool   IsStruggling()           => currentUserState == "struggling";
    public float  GetTimeSinceLastPred()   => Time.realtimeSinceStartup - lastPredictionTime;
}