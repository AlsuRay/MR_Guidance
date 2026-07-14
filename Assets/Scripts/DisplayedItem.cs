using UnityEngine;

namespace Assets.Scripts
{
    public enum TrackingMode
    {
        ByteTrack,
        HandProxy,
        Lost
    }

    internal class DisplayedItem
    {
        public DisplayedItem(YoloItem yoloItem, Vector3 positionInSpace)
        {
            this.YoloItem        = yoloItem;
            this.TimeLastSeen    = Time.time;
            this.TimesSeen       = 1;
            this.PositionInSpace = positionInSpace;
            this.IsInCameraView  = true;
            this.TrackingMode    = TrackingMode.ByteTrack;
            this.TrackId         = -1;
            this.TrackClass      = yoloItem.MostLikelyClass;
        }

        public YoloItem        YoloItem         { get; private set; }
        public Vector3         PositionInSpace  { get; set; }
        public float           TimeLastSeen     { get; set; }
        public int             TimesSeen        { get; private set; }
        public bool            IsInCameraView   { get; set; }
        public GameObject      TrackingMarker   { get; set; }
        public int             TrackId          { get; set; }
        public ObjectClass     TrackClass       { get; set; }
        public TrackingMode    TrackingMode     { get; set; }
        public bool            IsGrabbed        { get; set; }
        public string          GrabbedByHand    { get; set; }
        public Vector3         HandProxyOffset  { get; set; }
        public float           HandProxyStartTime { get; set; }
        public float           HandProxyTimeout { get; set; } = 3f;

        // ── Release tracking (prevents immediate re-grab by TryActivateHandProxy) ──
        public float           LastReleaseTime      { get; set; } = -999f;
        public string          LastReleasedByHand   { get; set; }

        // ── LLM highlight — when true, show object name label instead of dot ──
        public bool            ShowLabel            { get; set; } = false;

        public string SecondHandName     { get; set; }
public float  SecondHandGrabTime { get; set; }

        public void UpdateItem(YoloItem yoloItem, Vector3 positionInSpace)
        {
            this.YoloItem        = yoloItem;
            this.TimeLastSeen    = Time.time;
            this.TimesSeen++;
            this.IsInCameraView  = true;

            // HandProxy — position comes from hand, not YOLO
            if (this.TrackingMode == TrackingMode.HandProxy)
                return;

            // Update position only during:
            // 1. Initial anchoring (first 5 detections)
            // 2. After release (Lost mode) — re-anchor at new position
            if (this.TimesSeen <= 3 || this.TrackingMode == TrackingMode.Lost)
            {
                this.PositionInSpace = positionInSpace;
                this.TrackingMode    = TrackingMode.ByteTrack;
            }
            // Otherwise anchored — keep stable position, only update metadata
        }

        public void UpdateTrackId(int trackId, ObjectClass trackClass)
        {
            this.TrackId    = trackId;
            this.TrackClass = trackClass;
        }

        public void StartHandProxy(string handName, Vector3 handPosition)
        {
            this.IsGrabbed          = true;
            this.GrabbedByHand      = handName;
            this.HandProxyOffset    = this.PositionInSpace - handPosition;
            this.HandProxyStartTime = Time.time;
            this.TrackingMode       = TrackingMode.HandProxy;
            this.SecondHandName     = null;   // ← добавить
            this.SecondHandGrabTime = 0f;

            // Clear release block — this is a new valid grab
            this.LastReleasedByHand = null;
            this.LastReleaseTime    = -999f;
        }

        public void UpdateHandProxy(Vector3 handPosition)
        {
            if (this.TrackingMode != TrackingMode.HandProxy) return;
            this.PositionInSpace = handPosition + this.HandProxyOffset;
        }

        public void OnRelease()
        {
            this.IsGrabbed          = false;
            this.LastReleasedByHand = this.GrabbedByHand;
            this.LastReleaseTime    = Time.time;
            this.GrabbedByHand      = null;
            this.TrackingMode       = TrackingMode.Lost;
            this.SecondHandName     = null;   // ← добавить
            this.SecondHandGrabTime = 0f;     // ← добавить
        }
    }
}