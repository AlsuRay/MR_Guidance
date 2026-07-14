using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public class ColliderVisualizer : MonoBehaviour
{
    private BoxCollider box;
    private GameObject cube;
    private Renderer rend;

    [Header("Settings")]
    [SerializeField] private bool showOnStart = true;
    [SerializeField] private float alpha = 0.25f;

    private readonly Color defaultColor = Color.green;
    private readonly Color gazeColor = Color.magenta;
    private readonly Color handColor = Color.blue;
    private readonly Color highlightColor = Color.yellow;

    private bool gazeActive = false;
    private bool handActive = false;
    private bool highlighted = false;

    void Start()
    {
        box = GetComponent<BoxCollider>();

        cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "ColliderVisualizerCube";
        cube.transform.SetParent(transform, false);

        cube.transform.localPosition = box.center;
        cube.transform.localScale = box.size;

        rend = cube.GetComponent<Renderer>();

        var mat = new Material(Shader.Find("Standard"));
        mat.color = ApplyAlpha(defaultColor);
        mat.SetFloat("_Mode", 3); // transparent
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;

        rend.material = mat;

        Destroy(cube.GetComponent<Collider>());

        cube.SetActive(showOnStart);
    }

    void Update()
    {
        if (box == null || cube == null) return;

        cube.transform.localPosition = box.center;
        cube.transform.localScale = box.size;

        // Priority: highlighted > hand > gaze > default
        if (highlighted)
            rend.material.color = ApplyAlpha(highlightColor);
        else if (handActive)
            rend.material.color = ApplyAlpha(handColor);
        else if (gazeActive)
            rend.material.color = ApplyAlpha(gazeColor);
        else
            rend.material.color = ApplyAlpha(defaultColor);
    }

    private Color ApplyAlpha(Color c)
    {
        c.a = alpha;
        return c;
    }

    // ───────── CONTROL ─────────

    public void Show(bool value)
    {
        if (cube != null)
            cube.SetActive(value);
    }

    public void Toggle()
    {
        if (cube != null)
            cube.SetActive(!cube.activeSelf);
    }

    public void SetGazeActive(bool active) => gazeActive = active;
    public void SetHandActive(bool active) => handActive = active;
    public void SetHighlighted(bool active) => highlighted = active;
}