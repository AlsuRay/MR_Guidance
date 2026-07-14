using UnityEngine;

public class MarkerSmoother : MonoBehaviour
{
    [Tooltip("Higher = follows faster, lower = smoother")]
    public float followSpeed = 15f;

    [Tooltip("Ignore sudden jumps bigger than this (meters)")]
    public float maxJumpMeters = 0.6f;

    private Vector3 target;
    private bool hasTarget;

    public void SetTarget(Vector3 newTarget)
    {
        // First target: snap
        if (!hasTarget)
        {
            target = newTarget;
            transform.position = newTarget;
            hasTarget = true;
            return;
        }

        // Outlier rejection (bad depth/detection spikes)
        if (Vector3.Distance(transform.position, newTarget) > maxJumpMeters)
            return;

        target = newTarget;
    }

    private void LateUpdate()
    {
        if (!hasTarget) return;

        // FPS-independent exponential smoothing
        float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, target, t);
    }
}
