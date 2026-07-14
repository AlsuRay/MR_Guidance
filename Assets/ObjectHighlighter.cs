using UnityEngine;
using System.Collections;

public class ObjectHighlighter : MonoBehaviour
{
    private ColliderVisualizer visualizer;
    private Renderer cubeRenderer;
    private Color originalColor;
    private bool isHighlighted = false;

    [Tooltip("Delay before removing highlight after user looks at object (seconds)")]
    public float gazeRemoveDelay = 3f;

    void Start()
    {
        visualizer = GetComponent<ColliderVisualizer>();
        Invoke("FindCube", 0.1f);
    }

    void FindCube()
    {
        Transform cubeTransform = transform.Find("ColliderVisualizerCube");
        if (cubeTransform != null)
        {
            cubeRenderer = cubeTransform.GetComponent<Renderer>();
            if (cubeRenderer != null)
            {
                originalColor = cubeRenderer.material.color;
                Debug.Log($"✅ ObjectHighlighter ready for {gameObject.name}");
            }
        }
        else
        {
            Debug.LogWarning($"ColliderVisualizerCube not found on {gameObject.name}");
        }
    }

    /// <summary>
    /// Start highlighting — stays until user looks at the object (then 3s delay).
    /// </summary>
    public void Highlight()
    {
        if (cubeRenderer == null)
        {
            Debug.LogWarning($"[ObjectHighlighter] cubeRenderer is null on {gameObject.name}");
            return;
        }

        Debug.Log($"✨ Highlighting {gameObject.name}");

        StopAllCoroutines();
        CancelInvoke("RemoveHighlight");

        isHighlighted = true;
        cubeRenderer.material.color = Color.yellow;
        StartCoroutine(RotateHighlight());
    }

    /// <summary>
    /// Called by YoloGazeLogger when user looks at this object.
    /// Removes highlight after gazeRemoveDelay seconds.
    /// </summary>
    public void OnGazed()
    {
        if (!isHighlighted) return;
        Debug.Log($"👁️ Gaze detected on {gameObject.name} — removing highlight in {gazeRemoveDelay}s");
        CancelInvoke("RemoveHighlight");
        Invoke("RemoveHighlight", gazeRemoveDelay);
    }

    private IEnumerator RotateHighlight()
    {
        Transform cubeTransform = cubeRenderer.transform;
        while (isHighlighted)
        {
            cubeTransform.Rotate(Vector3.up, 90f * Time.deltaTime);
            yield return null;
        }
    }

    private void RemoveHighlight()
    {
        if (cubeRenderer == null) return;
        isHighlighted = false;
        StopAllCoroutines();
        cubeRenderer.material.color = originalColor;
        cubeRenderer.transform.rotation = Quaternion.identity;
        Debug.Log($"Highlight removed from {gameObject.name}");
    }
}