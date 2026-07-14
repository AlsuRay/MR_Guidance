using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    ///     Represents a Yolo item in version v8.
    /// </summary>
    public struct YoloItem
    {
        public static YoloItem FromVersion8(Vector2 center, Vector2 size, float confidence, int classIndex)
        {
            return new YoloItem
            {
                Center = center,
                Size = size,
                Confidence = confidence,
                MostLikelyClass = (ObjectClass)classIndex,
                TopLeft = center - size / 2,
                BottomRight = center + size / 2
            };
        }

        public static YoloItem FromVersion10(Vector2 topLeft, Vector2 bottomRight, float confidence, int classIndex)
        {
            YoloItem yoloItem = new()
            {
                TopLeft = topLeft,
                BottomRight = bottomRight,
                Size = bottomRight - topLeft,
                Confidence = confidence,
                MostLikelyClass = (ObjectClass)classIndex
            };
            yoloItem.Center = topLeft + yoloItem.Size / 2;

            return yoloItem;
        }

        /// <summary>
        /// Returns a copy of this YoloItem with a different center (and recalculated TopLeft/BottomRight).
        /// Used to inject EMA-smoothed center before raycasting.
        /// </summary>
        public YoloItem WithCenter(Vector2 newCenter)
        {
            return new YoloItem
            {
                Center = newCenter,
                Size = this.Size,
                Confidence = this.Confidence,
                MostLikelyClass = this.MostLikelyClass,
                TopLeft = newCenter - this.Size / 2,
                BottomRight = newCenter + this.Size / 2
            };
        }

        public Vector2 Center { get; private set; }
        public Vector2 Size { get; private set; }
        public Vector2 TopLeft { get; private set; }
        public Vector2 BottomRight { get; private set; }
        public float Confidence { get; private set; }
        public ObjectClass MostLikelyClass { get; private set; }
    }
}
