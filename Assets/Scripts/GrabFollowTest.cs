using UnityEngine;
using UnityEngine.XR;
using MixedReality.Toolkit;
using MixedReality.Toolkit.Subsystems;

public class GrabFollowTest : MonoBehaviour
{
    private HandsAggregatorSubsystem _hands;
    private bool _isGrabbing = false;
    private string _grabbingHand = "";

    void Start()
    {
        _hands = XRSubsystemHelpers
            .GetFirstRunningSubsystem<HandsAggregatorSubsystem>();

        MRTKGrab.OnGrab    += OnGrab;
        MRTKGrab.OnRelease += OnRelease;
    }

    void OnDestroy()
    {
        MRTKGrab.OnGrab    -= OnGrab;
        MRTKGrab.OnRelease -= OnRelease;
    }

    void OnGrab(string objName, string handName, Vector3 pos)
    {
        Debug.Log($"[GrabFollowTest] GRAB {objName} {handName}");
        _isGrabbing   = true;
        _grabbingHand = handName;
    }

    void OnRelease(string objName, string handName, Vector3 pos)
    {
        Debug.Log($"[GrabFollowTest] RELEASE {objName} {handName}");
        _isGrabbing = false;
    }

    void Update()
    {
        if (!_isGrabbing || _hands == null) return;

        var node = _grabbingHand == "LeftHand" ? XRNode.LeftHand : XRNode.RightHand;
        if (_hands.TryGetJoint(TrackedHandJoint.Palm, node, out var pose))
            transform.position = pose.Position;
    }
}