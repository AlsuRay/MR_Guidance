using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    ///     Helper class for transforming detected positions in an image to world coordinate system coordinates.
    /// </summary>
    public class PositionCalculator
    {
        private static WebCamTextureAccess WebCamTextureAccess => WebCamTextureAccess.Instance;

        /// <summary>
        ///     Calculates the position of the yolo object in world space.
        ///     Uses a 3x3 grid of spherecasts across the bbox and takes the closest
        ///     hit to the camera — this gives the front surface of the object rather
        ///     than the table/wall behind it.
        ///     Falls back to size-based depth estimation if no mesh hit is found.
        /// </summary>
        public static Vector3? CalculatePointInSpace(YoloItem yoloItem, CameraTransform cameraTransform)
        {
            // Try grid cast first
            Vector3? gridPoint = CastGridOnSpatialMap(yoloItem, cameraTransform);
            if (gridPoint.HasValue)
                return gridPoint.Value;

            // Fallback: single center cast (original behaviour)
            Vector2 centerInImage = ScaleBack(new Vector2(yoloItem.Center.x, yoloItem.Center.y));
            Vector3 centerInSpace  = GetPositionInSpace(cameraTransform, centerInImage);
            Vector3? centerPoint   = CastOnSpatialMap(centerInSpace, cameraTransform);
            if (centerPoint.HasValue)
                return centerPoint.Value;

            // Last resort: size-based depth estimation
            float estimatedDepth = EstimateDepthFallback(yoloItem);
            Vector3 origin    = cameraTransform.Position + Parameters.SphereCastOffset * cameraTransform.Up;
            Vector3 direction = (centerInSpace - origin).normalized;
            return origin + direction * estimatedDepth;
        }

        // ── Grid cast ─────────────────────────────────────────────────────────

        /// <summary>
        ///     Fires spherecasts through a 3×3 grid of points spread across the
        ///     bounding box (avoiding the very edges which often hit the background).
        ///     Returns the hit closest to the camera — i.e. the front surface of
        ///     the object rather than the wall or table behind it.
        /// </summary>
        private static Vector3? CastGridOnSpatialMap(YoloItem yoloItem, CameraTransform cameraTransform)
        {
            Vector3 origin   = cameraTransform.Position + Parameters.SphereCastOffset * cameraTransform.Up;
            float   minDist  = float.MaxValue;
            Vector3? closest = null;

            // 3×3 grid at 20 %, 50 %, 80 % of bbox width and height
            // — stays away from the bbox border where background is common
            float[] offsets = { 0.2f, 0.5f, 0.8f };

            foreach (float px in offsets)
            foreach (float py in offsets)
            {
                // Convert grid point from YOLO model coordinates to normalised camera coords
                Vector2 modelPoint = new Vector2(
                    yoloItem.TopLeft.x + yoloItem.Size.x * px,
                    yoloItem.TopLeft.y + yoloItem.Size.y * py);

                Vector2 scaledPoint  = ScaleBack(modelPoint);
                Vector3 pointInSpace = GetPositionInSpace(cameraTransform, scaledPoint);
                Vector3 direction    = pointInSpace - origin;

                if (PhysicsCaller.SphereCastOnSpatialMesh(origin, direction, out RaycastHit hit))
                {
                    float dist = Vector3.Distance(origin, hit.point);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        closest = hit.point;
                    }
                }
            }

            return closest;
        }

        // ── Original single-point cast (kept as fallback) ─────────────────────

        private static Vector3? CastOnSpatialMap(Vector3 positionInSpace, CameraTransform cameraTransform)
        {
            Vector3 sphereCastOrigin = cameraTransform.Position + Parameters.SphereCastOffset * cameraTransform.Up;
            Vector3 direction        = positionInSpace - sphereCastOrigin;

            if (PhysicsCaller.SphereCastOnSpatialMesh(sphereCastOrigin, direction, out RaycastHit hitInfo))
                return hitInfo.point;

            return null;
        }

        // ── Depth fallback ────────────────────────────────────────────────────

        /// <summary>
        ///     Fallback depth estimation based on apparent object size in the image.
        ///     Only used when all spherecasts miss the spatial mesh.
        /// </summary>
        private static float EstimateDepthFallback(YoloItem item)
        {
            float apparentRatio = item.Size.y / Parameters.ModelImageResolution.y;
            if (apparentRatio < 0.01f) apparentRatio = 0.01f;
            float depth = 0.15f / apparentRatio;
            return Mathf.Clamp(depth, 0.3f, 3f);
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        /// <summary>
        ///     Calculates the corner point positions of the yolo object in world space.
        ///     Unchanged from original.
        /// </summary>
        public static Vector3[] CalculateCornerPoints(YoloItem yoloItem, CameraTransform cameraTransform)
        {
            Vector3[] cornerPoints = new Vector3[4];
            int i = 0;
            Vector2 topRight   = yoloItem.TopLeft    + new Vector2(yoloItem.Size.x, 0);
            Vector2 bottomLeft = yoloItem.BottomRight - new Vector2(yoloItem.Size.x, 0);

            foreach (Vector2 cornerPoint in new[] { yoloItem.TopLeft, topRight, yoloItem.BottomRight, bottomLeft })
            {
                Vector2 scaled     = ScaleBack(cornerPoint);
                Vector3 posInSpace = GetPositionInSpace(cameraTransform, scaled);
                cornerPoints[i++]  = posInSpace;
            }

            return cornerPoints;
        }

        /// <summary>
        ///     Converts YOLO model pixel coordinates to normalised camera coordinates.
        /// </summary>
        private static Vector2 ScaleBack(Vector2 detectedPosition)
        {
            int cameraResolutionX = WebCamTextureAccess.ActualCameraSize.x;
            int cameraResolutionY = WebCamTextureAccess.ActualCameraSize.y;
            return new Vector2(
                (detectedPosition.x / Parameters.ModelImageResolution.x * cameraResolutionX
                    - (float)cameraResolutionX / 2) / cameraResolutionX,
                (detectedPosition.y / Parameters.ModelImageResolution.y * cameraResolutionY
                    - (float)cameraResolutionY / 2) / cameraResolutionY);
        }

        /// <summary>
        ///     Projects a normalised 2D camera point into 3D world space.
        /// </summary>
        public static Vector3 GetPositionInSpace(CameraTransform cameraTransform, Vector2 positionInImage)
        {
            return cameraTransform.Position
                + cameraTransform.Up      * Parameters.HeightOffset
                + cameraTransform.Forward
                + cameraTransform.Right   * (positionInImage.x * Parameters.VirtualProjectionPlane.x)
                - cameraTransform.Up      * (positionInImage.y * Parameters.VirtualProjectionPlane.y);
        }

        /// <summary>
        ///     Determines whether the object is visible in the current camera view.
        /// </summary>
        public static bool IsObjectInCameraView(Vector3 position)
        {
            Vector3 viewPos = Camera.main.WorldToViewportPoint(position);
            return viewPos.x is <= 1f and >= 0f
                && viewPos.y is <= 1f and >= 0f
                && viewPos.z >= 0f;
        }
    }
}