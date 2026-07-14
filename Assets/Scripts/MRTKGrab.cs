using UnityEngine;
using UnityEngine.XR;
using System;
using System.Net.Sockets;
using System.Text;
using MixedReality.Toolkit;
using MixedReality.Toolkit.Subsystems;

[RequireComponent(typeof(BoxCollider))]
public class MRTKGrab : MonoBehaviour
{
    private HandsAggregatorSubsystem hands;

    public string objectName = "UnknownObject";

    [Header("Grabbable Settings")]
    public bool isUngrabbable = false;  // Set true for objects like Rack

    [Header("Entry thresholds")]
    [SerializeField] private float pinchThreshold = 0.025f;
    [SerializeField] private float entryPowerCurlThreshold = 0.090f;
    [SerializeField] private float entryGrabDistance = 0.03f;

    [Header("Release thresholds")]
    [SerializeField] private float releaseOpenCurlThreshold = 0.110f;
    [SerializeField] private float releasePinchThreshold = 0.045f;
    [SerializeField] private int grabFramesRequired = 3;
    [SerializeField] private int releaseFramesRequired = 5;

    [Header("Cooldown")]
    [SerializeField] private float releaseCooldown = 1.0f;

    [Header("Hold Logging")]
    [SerializeField] private float holdLogInterval = 0.1f;

    [Header("UDP Streaming")]
    public string remoteIP   = "192.168.1.25";
    public int    remotePort = 5005;

    private static string _logFilePath;
    private static bool   _logFileInitialized = false;
    private static UdpClient _udpClient;
    private static bool      _udpInitialized = false;

    private bool leftGrabbed = false;
    private bool rightGrabbed = false;

    private int leftGrabFrames = 0;
    private int rightGrabFrames = 0;

    private int leftReleaseFrames = 0;
    private int rightReleaseFrames = 0;

    private float leftReleaseTime = -10f;
    private float rightReleaseTime = -10f;

    private float leftLastHoldLog = 0f;
    private float rightLastHoldLog = 0f;

    // ── Poke state ────────────────────────────────────────────────────
    private bool leftPoking = false;
    private bool rightPoking = false;
    private float pokeCooldownTime = -10f;
    private const float POKE_COOLDOWN = 1.0f;

    private BoxCollider _boxCollider;
    public BoxCollider BoxCollider => _boxCollider;

    // ── Gaze tracking ─────────────────────────────────────────────────
    private bool _isGazed = false;
    private float _lastGazeTime = -10f;
    private float _gazeStartTime = -10f;
    private const float GAZE_GRACE_PERIOD = 0.2f;
    private const float GAZE_DWELL_FOR_GRAB = 0.15f;  // must look for 150ms before grab allowed

    public bool IsGazed => _isGazed;

    // ── Global hand-busy lock ─────────────────────────────────────────
    private static bool _leftHandBusy = false;
    private static bool _rightHandBusy = false;
    private static MRTKGrab _leftHandOwner = null;
    private static MRTKGrab _rightHandOwner = null;

    // 1. Добавь поля в класс
private static MRTKGrab _gazeIntentTarget = null;
private static float    _gazeIntentTime   = -999f;
private const  float    INTENT_LOCK_WINDOW = 1.0f;

    // ── Scene objects ─────────────────────────────────────────────────
    private static string _lastSceneObjects = "";

    public static event Action<string, string, Vector3> OnGrab;
    public static event Action<string, string, Vector3> OnRelease;
    public static event Action<string, string, Vector3> OnPoke;

    public static void SetSceneObjects(string sceneObjectsStr)
    {
        _lastSceneObjects = sceneObjectsStr ?? "";
    }

    // 2. В OnGazeEnter()
public void OnGazeEnter()
{
    _isGazed       = true;
    _gazeStartTime = Time.time;
    _lastGazeTime  = Time.time;
    _gazeIntentTarget = this;
    _gazeIntentTime   = Time.time;
}

    public void OnGazeExit()
    {
        _isGazed = false;
        _lastGazeTime = Time.time;
    }

    public bool IsGazedOrRecent =>
        _isGazed || (Time.time - _lastGazeTime < GAZE_GRACE_PERIOD);

    void Awake()
    {
        _boxCollider = GetComponent<BoxCollider>();

        if (!_logFileInitialized)
        {
            _logFilePath = System.IO.Path.Combine(Application.persistentDataPath,
                $"action_log_{DateTime.Now:yyyyMMdd_HHmm}.csv");
            using (var sw = new System.IO.StreamWriter(_logFilePath, false))
                sw.WriteLine("Timestamp,Event,Hand,ObjectName," +
                             "ObjX,ObjY,ObjZ,HandX,HandY,HandZ," +
                             "TiltUp,TiltFwd,Curl,PinchDist,Dist," +
                             "SceneObjects,UnityTime");
            Debug.Log($"[MRTKGrab] Action log: {_logFilePath}");
            _logFileInitialized = true;
        }

        if (!_udpInitialized)
        {
            try
            {
                _udpClient = new UdpClient();
                _udpClient.Connect(remoteIP, remotePort);
                _udpInitialized = true;
            }
            catch (Exception) { }
        }
    }

    void Update()
    {
        // Skip all grab/release processing for ungrabbable objects
        if (isUngrabbable) return;

        if (hands == null)
            hands = XRSubsystemHelpers.GetFirstRunningSubsystem<HandsAggregatorSubsystem>();

        if (hands == null) return;

        ProcessHand(XRNode.LeftHand);
        ProcessHand(XRNode.RightHand);
    }

    void ProcessHand(XRNode node)
    {
        if (!hands.TryGetJoint(TrackedHandJoint.IndexTip, node, out var index) ||
            !hands.TryGetJoint(TrackedHandJoint.ThumbTip, node, out var thumb) ||
            !hands.TryGetJoint(TrackedHandJoint.MiddleTip, node, out var middle) ||
            !hands.TryGetJoint(TrackedHandJoint.Palm, node, out var palm))
            return;

        string hand = node == XRNode.LeftHand ? "LeftHand" : "RightHand";

        ref bool isGrabbed = ref (node == XRNode.LeftHand ? ref leftGrabbed : ref rightGrabbed);
        ref int grabFrames = ref (node == XRNode.LeftHand ? ref leftGrabFrames : ref rightGrabFrames);
        ref int releaseFrames = ref (node == XRNode.LeftHand ? ref leftReleaseFrames : ref rightReleaseFrames);
        ref float releaseTime = ref (node == XRNode.LeftHand ? ref leftReleaseTime : ref rightReleaseTime);
        ref float lastHoldLog = ref (node == XRNode.LeftHand ? ref leftLastHoldLog : ref rightLastHoldLog);

        bool isHandBusy = node == XRNode.LeftHand ? _leftHandBusy : _rightHandBusy;
        MRTKGrab handOwner = node == XRNode.LeftHand ? _leftHandOwner : _rightHandOwner;

        // ───────── FEATURES ─────────

        float pinchDist = Vector3.Distance(index.Position, thumb.Position);

        float thumbCurl = Vector3.Distance(thumb.Position, palm.Position);
        float indexCurl = Vector3.Distance(index.Position, palm.Position);
        float middleCurl = Vector3.Distance(middle.Position, palm.Position);

        float grabCurl;
        if (hands.TryGetJoint(TrackedHandJoint.RingTip, node, out var ring))
        {
            float ringCurl = Vector3.Distance(ring.Position, palm.Position);
            grabCurl = (thumbCurl + indexCurl + middleCurl + ringCurl) / 4f;
        }
        else
        {
            grabCurl = (thumbCurl + indexCurl + middleCurl) / 3f;
        }

        float releaseCurl = (thumbCurl + indexCurl + middleCurl) / 3f;

        float dist = Vector3.Distance(palm.Position, _boxCollider.ClosestPoint(palm.Position));

        float tiltUp  = Vector3.Angle(palm.Up, Vector3.up);
        float tiltFwd = Vector3.Angle(palm.Forward, Vector3.up);

        bool nearObject = dist < entryGrabDistance;
        bool pinchLike = pinchDist < pinchThreshold;
        bool powerLike = grabCurl < entryPowerCurlThreshold;
        bool combinedGrab = grabCurl < (entryPowerCurlThreshold + 0.010f) && pinchDist < 0.040f;

        bool handFree = !isHandBusy || handOwner == this;
        bool gazedLongEnough = _isGazed && (Time.time - _gazeStartTime >= GAZE_DWELL_FOR_GRAB);

        
        // 3. В ProcessHand замени grabLike
        bool isIntendedTarget = (_gazeIntentTarget == this) &&
                                (Time.time - _gazeIntentTime < INTENT_LOCK_WINDOW);

        bool grabLike = (pinchLike || powerLike || combinedGrab)
                && nearObject
                && (gazedLongEnough || isIntendedTarget)
                && handFree
                && (IsClosestGazedObject(palm.Position) || isIntendedTarget);

        bool fingersOpen = releaseCurl > releaseOpenCurlThreshold;
        bool pinchReleased = pinchDist > releasePinchThreshold;
        bool releaseLike = fingersOpen && pinchReleased;

        bool inCooldown = (Time.time - releaseTime) < releaseCooldown;

        // ───────── POKE ─────────

        if (!isGrabbed && _boxCollider != null)
        {
            ref bool isPoking = ref (node == XRNode.LeftHand ? ref leftPoking : ref rightPoking);

            bool indexInside = _boxCollider.bounds.Contains(index.Position);
            bool notPinching = pinchDist > pinchThreshold * 1.5f;
            bool isStopSign = tiltUp > 50f && tiltUp < 100f && tiltFwd < 45f;
            bool pokeNow = indexInside && notPinching && !pinchLike && !powerLike && isStopSign;
            bool pokeNotOnCooldown = (Time.time - pokeCooldownTime) > POKE_COOLDOWN;

            if (pokeNow && !isPoking && pokeNotOnCooldown)
            {
                isPoking = true;
                pokeCooldownTime = Time.time;
                LogEvent("poke", hand, index.Position, tiltUp, tiltFwd, releaseCurl, pinchDist, dist);
                OnPoke?.Invoke(objectName, hand, index.Position);
            }
            else if (!indexInside && isPoking)
            {
                isPoking = false;
            }
        }

        // ───────── GRAB ─────────

        if (!isGrabbed)
        {
            if (grabLike && !inCooldown)
            {
                grabFrames++;
                if (grabFrames >= grabFramesRequired)
                {
                    isGrabbed = true;
                    grabFrames = 0;
                    releaseFrames = 0;
                    lastHoldLog = Time.time;

                    if (node == XRNode.LeftHand)
                    { _leftHandBusy = true; _leftHandOwner = this; }
                    else
                    { _rightHandBusy = true; _rightHandOwner = this; }

                    LogEvent("grab", hand, palm.Position, tiltUp, tiltFwd, grabCurl, pinchDist, dist);
                    OnGrab?.Invoke(objectName, hand, palm.Position);

                    var viz = GetComponent<ColliderVisualizer>();
                    if (viz != null) viz.SetHandActive(true);
                }
            }
            else
            {
                grabFrames = 0;
            }
            return;
        }

        // ───────── HOLD ─────────

        if (Time.time - lastHoldLog >= holdLogInterval)
        {
            LogEvent("hold", hand, palm.Position, tiltUp, tiltFwd, grabCurl, pinchDist, dist);
            lastHoldLog = Time.time;
        }

        // ───────── RELEASE ─────────

        if (releaseLike)
        {
            releaseFrames++;
            if (releaseFrames >= releaseFramesRequired)
            {
                isGrabbed = false;
                releaseFrames = 0;
                releaseTime = Time.time;

                if (node == XRNode.LeftHand && _leftHandOwner == this)
                { _leftHandBusy = false; _leftHandOwner = null; }
                else if (node == XRNode.RightHand && _rightHandOwner == this)
                { _rightHandBusy = false; _rightHandOwner = null; }

                LogEvent("release", hand, palm.Position, tiltUp, tiltFwd, releaseCurl, pinchDist, dist);
                OnRelease?.Invoke(objectName, hand, palm.Position);

                var viz = GetComponent<ColliderVisualizer>();
                if (viz != null) viz.SetHandActive(false);
            }
        }
        else
        {
            releaseFrames = 0;
        }
    }

    private bool IsClosestGazedObject(Vector3 palmPosition)
    {
        MRTKGrab[] allGrabs = FindObjectsOfType<MRTKGrab>();
        float myDist = Vector3.Distance(palmPosition, _boxCollider.ClosestPoint(palmPosition));

        foreach (var other in allGrabs)
        {
            if (other == this) continue;
            if (other.isUngrabbable) continue;  // skip ungrabbable objects in comparison
            if (!other.IsGazedOrRecent) continue;
            float otherDist = Vector3.Distance(palmPosition, other.BoxCollider.ClosestPoint(palmPosition));
            if (otherDist < myDist) return false;
        }
        return true;
    }

    private void LogEvent(string evt, string hand, Vector3 handPos,
                          float tiltUp, float tiltFwd,
                          float curl, float pinchDist, float dist)
    {
        if (string.IsNullOrEmpty(objectName) || objectName == "UnknownObject") return;

        string ts     = DateTime.UtcNow.ToString("o");
        float  t      = Time.realtimeSinceStartup;
        Vector3 objPos = transform.position;

        string sceneObjs = _lastSceneObjects;

        string line = $"{ts},{evt},{hand},{objectName}," +
                      $"{objPos.x:F3},{objPos.y:F3},{objPos.z:F3}," +
                      $"{handPos.x:F3},{handPos.y:F3},{handPos.z:F3}," +
                      $"{tiltUp:F1},{tiltFwd:F1},{curl:F3},{pinchDist:F3},{dist:F3}," +
                      $"{sceneObjs},{t:F4}";

        try { using (var sw = new System.IO.StreamWriter(_logFilePath, true)) sw.WriteLine(line); }
        catch (Exception) { }

        if (_udpInitialized && _udpClient != null)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(line + "\n");
                _udpClient.Send(data, data.Length);
            }
            catch (Exception) { }
        }
    }
}