using TMPro;
using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    ///     Manages the visual aspects of the object label.
    /// </summary>
    public class ObjectLabelController : MonoBehaviour
    {
        /// <summary>
        ///     Parent of the displayed label.
        /// </summary>
        public GameObject ContentParent;

        /// <summary>
        ///     Renderer for showing a line between the center of the object and the label.
        /// </summary>
        public LineRenderer LineRenderer;

        /// <summary>
        ///     Text mesh for displaying the class of the object.
        /// </summary>
        public TextMeshPro TextMesh;

        /// <summary>
        ///     Sets the display text.
        /// </summary>
        public string Text
        {
            set => this.TextMesh.text = value;
        }

        void Update()
        {
            // Keep line renderer in sync with marker position every frame
            if (this.LineRenderer != null && this.ContentParent != null)
            {
                this.LineRenderer.SetPosition(0, this.ContentParent.transform.position);
                this.LineRenderer.SetPosition(1, this.transform.position);
            }
        }

        /// <summary>
        ///     Updates the position of the object label.
        /// </summary>
        /// <param name="newPosition">New position of the object.</param>
        public void UpdatePosition(Vector3 newPosition)
        {
            this.transform.position = newPosition;

            // update line between text and center of object
            if (this.LineRenderer != null && this.ContentParent != null)
            {
                this.LineRenderer.SetPosition(0, this.ContentParent.transform.position);
                this.LineRenderer.SetPosition(1, this.transform.position);
            }
        }
    }
}