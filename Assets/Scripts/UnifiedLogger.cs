using UnityEngine;
using UnityEngine.XR;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using MixedReality.Toolkit;
using MixedReality.Toolkit.Subsystems;
using MixedReality.Toolkit.Input;

public class UnifiedLogger : MonoBehaviour
{
    [Header("Logging Settings")]
    public float loggingFrequency = 30f;

    [Header("UDP Settings")]
    public string pythonIP   = "192.168.1.25";
    public int    pythonPort = 5007;
    public bool   enableUDP  = true;

    public GazeInteractor gazeInteractor;

    private StreamWriter  writer;
    private float         logInterval;
    private float         timer = 0f;
    private UdpClient     udpClient;

    private HandsAggregatorSubsystem handsAggregator;
    private InputDevice               headDevice;

    // Joints needed for BiGRU — Palm, Wrist, IndexDistal only
    // Order must match RAW_FEATURE_COLS in dataset_builder.py

    void Start()
    {
        logInterval = 1f / loggingFrequency;

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

        // CSV
        string path = Path.Combine(Application.persistentDataPath,
            $"unified_sensors_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        writer = new StreamWriter(path, false);
        writer.WriteLine(
            "Timestamp," +
            "Gaze_OriginX,Gaze_OriginY," +
            "Gaze_DirX,Gaze_DirY,Gaze_DirZ," +
            "Head_RotX,Head_RotY,Head_RotZ,Head_RotW," +
            "Head_PosX,Head_PosY,Head_PosZ," +
            "L_Palm_X,L_Palm_Y,L_Palm_Z," +
            "L_Wrist_X,L_Wrist_Y,L_Wrist_Z," +
            "L_Index_X,L_Index_Y,L_Index_Z," +
            "R_Palm_X,R_Palm_Y,R_Palm_Z," +
            "R_Wrist_X,R_Wrist_Y,R_Wrist_Z," +
            "R_Index_X,R_Index_Y,R_Index_Z"
        );

        // UDP
        if (enableUDP)
        {
            udpClient = new UdpClient();
            Debug.Log($"✅ UDP sender → {pythonIP}:{pythonPort}");
        }

        Debug.Log($"✅ Unified logging started: {path}");
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= logInterval)
        {
            timer = 0f;
            LogFrame();
        }
    }

    void LogFrame()
    {
        // === HEAD ===
        Vector3    headPos = Vector3.zero;
        Quaternion headRot = Quaternion.identity;
        if (headDevice.isValid)
        {
            headDevice.TryGetFeatureValue(CommonUsages.devicePosition, out headPos);
            headDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out headRot);
        }

        // === GAZE ===
        Vector3 gazeOrigin = Vector3.zero;
        Vector3 gazeDir    = Vector3.forward;
        if (gazeInteractor != null && gazeInteractor.rayOriginTransform != null)
        {
            gazeOrigin = gazeInteractor.rayOriginTransform.position;
            gazeDir    = gazeInteractor.rayOriginTransform.forward;
        }

        // === HANDS ===
        Vector3 lPalm  = GetJointPos(XRNode.LeftHand,  TrackedHandJoint.Palm);
        Vector3 lWrist = GetJointPos(XRNode.LeftHand,  TrackedHandJoint.Wrist);
        Vector3 lIndex = GetJointPos(XRNode.LeftHand,  TrackedHandJoint.IndexDistal);
        Vector3 rPalm  = GetJointPos(XRNode.RightHand, TrackedHandJoint.Palm);
        Vector3 rWrist = GetJointPos(XRNode.RightHand, TrackedHandJoint.Wrist);
        Vector3 rIndex = GetJointPos(XRNode.RightHand, TrackedHandJoint.IndexDistal);

        // === 30 values in RAW_FEATURE_COLS order ===
        float[] frame = new float[30]
        {
            gazeOrigin.x, gazeOrigin.y,
            gazeDir.x, gazeDir.y, gazeDir.z,
            headRot.x, headRot.y, headRot.z, headRot.w,
            headPos.x, headPos.y, headPos.z,
            lPalm.x,  lPalm.y,  lPalm.z,
            lWrist.x, lWrist.y, lWrist.z,
            lIndex.x, lIndex.y, lIndex.z,
            rPalm.x,  rPalm.y,  rPalm.z,
            rWrist.x, rWrist.y, rWrist.z,
            rIndex.x, rIndex.y, rIndex.z,
        };

        // === CSV ===
        string timestamp = DateTime.UtcNow.ToString("o");
        var sb = new System.Text.StringBuilder();
        sb.Append(timestamp);
        foreach (var v in frame)
        {
            sb.Append(',');
            sb.Append(v.ToString("F4"));
        }
        writer.WriteLine(sb.ToString());
        writer.Flush();

        // === UDP ===
        if (enableUDP && udpClient != null)
        {
            var data = new
            {
                t_unity = Time.realtimeSinceStartup,
                frame   = frame
            };
            string json  = JsonUtility.ToJson(new FramePacket(Time.realtimeSinceStartup, frame));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            udpClient.Send(bytes, bytes.Length, pythonIP, pythonPort);
        }
    }

    Vector3 GetJointPos(XRNode hand, TrackedHandJoint joint)
    {
        if (handsAggregator.TryGetJoint(joint, hand, out HandJointPose pose))
            return pose.Position;
        return Vector3.zero;
    }

    void OnDestroy()
    {
        writer?.Flush();
        writer?.Close();
        udpClient?.Close();
        Debug.Log("✅ Unified logging stopped");
    }

    [Serializable]
    class FramePacket
    {
        public float   t_unity;
        public float[] frame;
        public FramePacket(float t, float[] f) { t_unity = t; frame = f; }
    }
}