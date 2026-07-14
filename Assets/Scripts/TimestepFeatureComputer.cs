using UnityEngine;
using System.Collections.Generic;
using System;

/// <summary>
/// Computes timestep-level features from HoloLens sensor data
/// Based on extract_timestep_features.py
/// 
/// Computes 12 features per frame:
/// - Hand (4): hand_present, velocity, acceleration, jerk
/// - Gaze (3): velocity, fixation_duration, saccade_velocity
/// - Head (3): angular_velocity, pitch, yaw
/// - Combined (2): gaze_lead_time, joint_attention
/// </summary>
public class TimestepFeatureComputer : MonoBehaviour
{
    [Header("Thresholds")]
    public float fixationVelocityThreshold = 30f;      // deg/sec
    public float saccadeVelocityThreshold = 100f;      // deg/sec
    public float gazeHandAlignmentThreshold = 15f;     // degrees
    public float jointAttentionAngleThreshold = 15f;   // degrees
    public float jointAttentionDistanceThreshold = 0.15f; // meters
    public float handPositionThreshold = 0.001f;       // meters (считаем (0,0,0) = invalid)
    
    [Header("Gap Detection")]
    public float maxDtThreshold = 0.2f;  // seconds - reset state if gap > 0.2s
    
    // State for derivatives
    private SensorFrame prevFrame;
    private float prevTimestamp;
    private float prevPalmVelocity = 0f;
    private float prevGazeVelocity = 0f;
    private bool prevHandPresent = false;
    
    // History buffers
    private Queue<Vector3> palmPositionHistory = new Queue<Vector3>();
    private Queue<float> palmVelocityHistory = new Queue<float>();
    private Queue<GazeHandRecord> gazeHandHistory = new Queue<GazeHandRecord>();
    
    // Fixation state
    private float? fixationStartTime = null;
    private float currentFixationDuration = 0f;
    
    // Statistics
    private int numGaps = 0;
    private int numHandInvalid = 0;
    
    // Buffer sizes
    private const int PALM_POSITION_HISTORY_SIZE = 4;   // For jerk
    private const int PALM_VELOCITY_HISTORY_SIZE = 30;  // For gaze_lead_time
    private const int GAZE_HAND_HISTORY_SIZE = 60;      // 2 seconds @ 30fps
    
    void Start()
    {
        Debug.Log("[TimestepFeatureComputer] Initialized");
    }
    
    /// <summary>
    /// Compute timestep features from current sensor frame
    /// </summary>
    public TimestepFeatures ComputeFeatures(SensorFrame frame)
    {
        TimestepFeatures features = new TimestepFeatures();
        
        // Compute dt
        float dt = 1f / 30f; // default
        if (prevFrame != null)
        {
            dt = frame.timestamp - prevTimestamp;
            
            // Check for gap
            if (dt > maxDtThreshold)
            {
                ResetState();
                numGaps++;
                dt = 1f / 30f;
            }
            
            // Clamp dt
            dt = Mathf.Clamp(dt, 0.001f, 0.3f);
        }
        
        // Extract features
        ExtractHandFeatures(frame, dt, ref features);
        ExtractGazeFeatures(frame, dt, ref features);
        ExtractHeadFeatures(frame, dt, ref features);
        ExtractCombinedFeatures(frame, dt, ref features);
        
        // Update state
        prevFrame = frame;
        prevTimestamp = frame.timestamp;
        
        return features;
    }
    
    void ResetState()
    {
        fixationStartTime = null;
        currentFixationDuration = 0f;
        gazeHandHistory.Clear();
        palmPositionHistory.Clear();
        palmVelocityHistory.Clear();
        prevPalmVelocity = 0f;
        prevGazeVelocity = 0f;
        prevHandPresent = false;
        prevFrame = null;
    }
    
    // ===== HAND FEATURES =====
    void ExtractHandFeatures(SensorFrame frame, float dt, ref TimestepFeatures features)
    {
        Vector3 palmPos = frame.rightPalmPosition;
        
        // Check hand validity
        bool handPresent = palmPos.magnitude > handPositionThreshold;
        features.hand_present = handPresent ? 1f : 0f;
        
        if (!handPresent)
        {
            features.hand_velocity = float.NaN;
            features.hand_acceleration = float.NaN;
            features.hand_jerk = float.NaN;
            
            palmVelocityHistory.Enqueue(0f);
            if (palmVelocityHistory.Count > PALM_VELOCITY_HISTORY_SIZE)
                palmVelocityHistory.Dequeue();
            
            prevHandPresent = false;
            numHandInvalid++;
            return;
        }
        
        // Hand velocity
        float palmVelocity = 0f;
        if (prevFrame != null && prevHandPresent)
        {
            Vector3 prevPalmPos = prevFrame.rightPalmPosition;
            palmVelocity = Vector3.Distance(palmPos, prevPalmPos) / dt;
        }
        features.hand_velocity = palmVelocity;
        
        // Update history
        palmPositionHistory.Enqueue(palmPos);
        if (palmPositionHistory.Count > PALM_POSITION_HISTORY_SIZE)
            palmPositionHistory.Dequeue();
        
        palmVelocityHistory.Enqueue(palmVelocity);
        if (palmVelocityHistory.Count > PALM_VELOCITY_HISTORY_SIZE)
            palmVelocityHistory.Dequeue();
        
        // Hand acceleration
        float palmAcceleration = 0f;
        if (prevFrame != null && prevHandPresent)
        {
            palmAcceleration = Mathf.Abs(palmVelocity - prevPalmVelocity) / dt;
        }
        features.hand_acceleration = palmAcceleration;
        
        // Hand jerk (simple version for timestep)
        float palmJerk = 0f;
        if (palmPositionHistory.Count >= 4)
        {
            Vector3[] positions = new Vector3[palmPositionHistory.Count];
            palmPositionHistory.CopyTo(positions, 0);
            
            // Compute velocities
            float[] velocities = new float[positions.Length - 1];
            for (int i = 0; i < velocities.Length; i++)
            {
                velocities[i] = Vector3.Distance(positions[i + 1], positions[i]) / dt;
            }
            
            // Compute accelerations
            float[] accelerations = new float[velocities.Length - 1];
            for (int i = 0; i < accelerations.Length; i++)
            {
                accelerations[i] = Mathf.Abs(velocities[i + 1] - velocities[i]) / dt;
            }
            
            // Compute jerks
            float[] jerks = new float[accelerations.Length - 1];
            for (int i = 0; i < jerks.Length; i++)
            {
                jerks[i] = Mathf.Abs(accelerations[i + 1] - accelerations[i]) / dt;
            }
            
            // Mean jerk
            float sum = 0f;
            foreach (float j in jerks) sum += j;
            palmJerk = sum / jerks.Length;
        }
        features.hand_jerk = palmJerk;
        
        prevPalmVelocity = palmVelocity;
        prevHandPresent = true;
    }
    
    // ===== GAZE FEATURES =====
    void ExtractGazeFeatures(SensorFrame frame, float dt, ref TimestepFeatures features)
    {
        Vector3 gazeDir = frame.gazeDirection;
        
        // Gaze velocity
        float gazeVelocity = 0f;
        if (prevFrame != null)
        {
            Vector3 prevGazeDir = prevFrame.gazeDirection;
            float angle = Vector3.Angle(prevGazeDir, gazeDir);
            gazeVelocity = angle / dt;
        }
        features.gaze_velocity = gazeVelocity;
        
        // Fixation duration
        bool isFixating = gazeVelocity < fixationVelocityThreshold;
        
        if (isFixating)
        {
            if (!fixationStartTime.HasValue)
            {
                fixationStartTime = 0f;
                currentFixationDuration = 0f;
            }
            else
            {
                currentFixationDuration += dt;
            }
        }
        else
        {
            fixationStartTime = null;
            currentFixationDuration = 0f;
        }
        features.fixation_duration = currentFixationDuration;
        
        // Saccade velocity
        float saccadeVelocity = gazeVelocity > saccadeVelocityThreshold ? gazeVelocity : 0f;
        features.saccade_velocity = saccadeVelocity;
        
        prevGazeVelocity = gazeVelocity;
    }
    
    // ===== HEAD FEATURES =====
    void ExtractHeadFeatures(SensorFrame frame, float dt, ref TimestepFeatures features)
    {
        Quaternion headRot = frame.headRotation;
        Vector3 euler = headRot.eulerAngles;
        
        // Convert to pitch/yaw
        float pitch = NormalizeAngle(euler.x);
        float yaw = NormalizeAngle(euler.y);
        
        // Head angular velocity
        float headAngularVelocity = 0f;
        if (prevFrame != null)
        {
            Vector3 prevEuler = prevFrame.headRotation.eulerAngles;
            float prevPitch = NormalizeAngle(prevEuler.x);
            float prevYaw = NormalizeAngle(prevEuler.y);
            
            float pitchDiff = NormalizeAngleDiff(pitch - prevPitch);
            float yawDiff = NormalizeAngleDiff(yaw - prevYaw);
            
            headAngularVelocity = Mathf.Sqrt(
                (pitchDiff / dt) * (pitchDiff / dt) + 
                (yawDiff / dt) * (yawDiff / dt)
            );
        }
        features.head_angular_velocity = headAngularVelocity;
        features.head_pitch = pitch;
        features.head_yaw = yaw;
    }
    
    // ===== COMBINED FEATURES =====
    void ExtractCombinedFeatures(SensorFrame frame, float dt, ref TimestepFeatures features)
    {
        Vector3 gazeDir = frame.gazeDirection;
        Vector3 gazeOrigin = frame.gazeOrigin;
        Vector3 palmPos = frame.rightPalmPosition;
        
        // Check hand validity
        bool handPresent = palmPos.magnitude > handPositionThreshold;
        
        if (!handPresent)
        {
            features.joint_attention = 0f;
            features.gaze_lead_time = 0f;
            return;
        }
        
        // Joint attention
        features.joint_attention = ComputeJointAttention(gazeOrigin, gazeDir, palmPos);
        
        // Gaze-hand alignment (for lead time)
        Vector3 gazeToHand = palmPos - gazeOrigin;
        float alignmentAngle = Vector3.Angle(gazeDir, gazeToHand);
        int gazeHandAligned = alignmentAngle < gazeHandAlignmentThreshold ? 1 : 0;
        
        // Gaze lead time (simple heuristic)
        float handVelocity = palmVelocityHistory.Count > 0 ? 
            palmVelocityHistory.ToArray()[palmVelocityHistory.Count - 1] : 0f;
        
        gazeHandHistory.Enqueue(new GazeHandRecord {
            gazeAligned = gazeHandAligned,
            handMoving = handVelocity > 0.1f ? 1 : 0
        });
        
        if (gazeHandHistory.Count > GAZE_HAND_HISTORY_SIZE)
            gazeHandHistory.Dequeue();
        
        // Check if gaze was aligned before hand started moving
        float gazeLeadTime = 0f;
        if (gazeHandHistory.Count >= 10)
        {
            GazeHandRecord[] history = gazeHandHistory.ToArray();
            GazeHandRecord current = history[history.Length - 1];
            
            for (int i = history.Length - 10; i < history.Length - 1; i++)
            {
                if (history[i].gazeAligned == 1 && 
                    history[i].handMoving == 0 && 
                    current.handMoving == 1)
                {
                    gazeLeadTime = (history.Length - 1 - i) * dt;
                    break;
                }
            }
        }
        features.gaze_lead_time = gazeLeadTime;
    }
    
    float ComputeJointAttention(Vector3 gazeOrigin, Vector3 gazeDir, Vector3 handPos)
    {
        Vector3 gazeDirNorm = gazeDir.normalized;
        
        // 1. Angular alignment
        Vector3 eyeToHand = (handPos - gazeOrigin).normalized;
        float angle = Vector3.Angle(eyeToHand, gazeDirNorm);
        
        // 2. Spatial proximity
        Vector3 originToHand = handPos - gazeOrigin;
        float t = Mathf.Max(0, Vector3.Dot(originToHand, gazeDirNorm));
        Vector3 closestPoint = gazeOrigin + t * gazeDirNorm;
        float distance = Vector3.Distance(handPos, closestPoint);
        
        // 3. Joint attention if BOTH conditions met
        bool angleAligned = angle < jointAttentionAngleThreshold;
        bool spatiallyClose = distance < jointAttentionDistanceThreshold;
        
        return (angleAligned && spatiallyClose) ? 1f : 0f;
    }
    
    // ===== UTILITY FUNCTIONS =====
    
    float NormalizeAngle(float angle)
    {
        // Convert [0, 360] to [-180, 180]
        if (angle > 180f) angle -= 360f;
        return angle;
    }
    
    float NormalizeAngleDiff(float angleDiff)
    {
        // Normalize angle difference to [-180, 180]
        while (angleDiff > 180f) angleDiff -= 360f;
        while (angleDiff < -180f) angleDiff += 360f;
        return angleDiff;
    }
    
    public void GetStatistics(out int gaps, out int handInvalidCount)
    {
        gaps = numGaps;
        handInvalidCount = numHandInvalid;
    }
}

// ===== DATA STRUCTURES =====

[System.Serializable]
public class SensorFrame
{
    public float timestamp;
    
    // Head
    public Vector3 headPosition;
    public Quaternion headRotation;
    
    // Gaze
    public Vector3 gazeOrigin;
    public Vector3 gazeDirection;
    
    // Right hand
    public Vector3 rightPalmPosition;
}

[System.Serializable]
public class TimestepFeatures
{
    // Hand (4)
    public float hand_present;
    public float hand_velocity;
    public float hand_acceleration;
    public float hand_jerk;
    
    // Gaze (3)
    public float gaze_velocity;
    public float fixation_duration;
    public float saccade_velocity;
    
    // Head (3)
    public float head_angular_velocity;
    public float head_pitch;
    public float head_yaw;
    
    // Combined (2)
    public float gaze_lead_time;
    public float joint_attention;
}

struct GazeHandRecord
{
    public int gazeAligned;
    public int handMoving;
}