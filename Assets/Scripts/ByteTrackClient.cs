using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Assets.Scripts
{
    /// <summary>
    /// Sends YOLO detections to ByteTrack server on PC,
    /// receives tracked results with stable track_ids back.
    /// </summary>
    public class ByteTrackClient : MonoBehaviour
    {
        [Header("Network Settings")]
        //public string serverIP    = "192.168.0.5";
        public string serverIP    = "192.168.1.25";
        public int    sendPort    = 5020;
        public int    receivePort = 5021;

        [Header("Debug (read-only)")]
        public int activeTrackCount = 0;

        // Network
        private UdpClient  _sendClient;
        private UdpClient  _receiveClient;
        private IPEndPoint _serverEndpoint;

        // Latest tracked results — thread safe
        private List<TrackedObject> _latestTracks = new();
        private readonly object _lock = new object();

        // Frame counter
        private int _frameId = 0;

        void Start()
        {
            _sendClient     = new UdpClient();
            _serverEndpoint = new IPEndPoint(IPAddress.Parse(serverIP), sendPort);
            _receiveClient  = new UdpClient(receivePort);
            _receiveClient.BeginReceive(OnReceive, null);

            Debug.Log($"[ByteTrackClient] → {serverIP}:{sendPort}, ← :{receivePort}");
        }

        /// <summary>
        /// Call this from YoloModelExecutor after getting YOLO results.
        /// </summary>
        public void SendDetections(List<YoloItem> detections,
                                   CameraTransform camTransform,
                                   Vector2 modelResolution)
        {
            var dets = new StringBuilder();
            dets.Append("[");
            bool first = true;

            foreach (var item in detections)
            {
                float x1 = item.TopLeft.x     / modelResolution.x;
                float y1 = item.TopLeft.y     / modelResolution.y;
                float x2 = item.BottomRight.x / modelResolution.x;
                float y2 = item.BottomRight.y / modelResolution.y;

                Vector3? worldPos = PositionCalculator.CalculatePointInSpace(item, camTransform);
                float wx = worldPos.HasValue ? worldPos.Value.x : 0f;
                float wy = worldPos.HasValue ? worldPos.Value.y : 0f;
                float wz = worldPos.HasValue ? worldPos.Value.z : 0f;

                if (!first) dets.Append(",");
                first = false;

                dets.Append("{");
                dets.Append($"\"class\":{(int)item.MostLikelyClass},");
                dets.Append($"\"class_name\":\"{item.MostLikelyClass}\",");
                dets.Append($"\"confidence\":{item.Confidence.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},");
                dets.Append($"\"x1\":{x1.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)},");
                dets.Append($"\"y1\":{y1.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)},");
                dets.Append($"\"x2\":{x2.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)},");
                dets.Append($"\"y2\":{y2.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)},");
                dets.Append($"\"wx\":{wx.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},");
                dets.Append($"\"wy\":{wy.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},");
                dets.Append($"\"wz\":{wz.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                dets.Append("}");
            }
            dets.Append("]");

            string packet = "{" +
                $"\"frame_id\":{_frameId++}," +
                $"\"timestamp\":{Time.realtimeSinceStartup.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                $"\"camera\":{{" +
                    $"\"px\":{camTransform.Position.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"py\":{camTransform.Position.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"pz\":{camTransform.Position.z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"fx\":{camTransform.Forward.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"fy\":{camTransform.Forward.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"fz\":{camTransform.Forward.z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}" +
                $"}}," +
                $"\"detections\":{dets}" +
            "}";

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(packet);
                if (data.Length < 65000)
                    _sendClient.Send(data, data.Length, _serverEndpoint);
                else
                    Debug.LogWarning("[ByteTrackClient] Packet too large");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ByteTrackClient] Send error: {e.Message}");
            }
        }

        /// <summary>
        /// Send reset signal to ByteTrack server — call on experiment type switch.
        /// </summary>
        public void SendReset()
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes("{\"reset\":true}");
                _sendClient.Send(data, data.Length, _serverEndpoint);
                Debug.Log("[ByteTrackClient] Reset sent to server");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ByteTrackClient] Reset error: {e.Message}");
            }
        }

        /// <summary>
        /// Get latest tracked objects — call from YoloRecognitionHandler.
        /// </summary>
        public List<TrackedObject> GetLatestTracks()
        {
            lock (_lock)
            {
                return new List<TrackedObject>(_latestTracks);
            }
        }

        void OnReceive(IAsyncResult result)
        {
            try
            {
                IPEndPoint ep   = new IPEndPoint(IPAddress.Any, 0);
                byte[]     data = _receiveClient.EndReceive(result, ref ep);
                string     json = Encoding.UTF8.GetString(data);

                Debug.Log($"[ByteTrackClient] Received: {json.Substring(0, Mathf.Min(200, json.Length))}");

                var tracks = ParseTracks(json);
                lock (_lock)
                {
                    _latestTracks       = tracks;
                    activeTrackCount    = tracks.Count;
                }

                _receiveClient.BeginReceive(OnReceive, null);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ByteTrackClient] Receive error: {e.Message}");
                _receiveClient.BeginReceive(OnReceive, null);
            }
        }

        private List<TrackedObject> ParseTracks(string json)
        {
            var result = new List<TrackedObject>();
            try
            {
                var wrapper = JsonUtility.FromJson<TrackResponse>(json);
                if (wrapper?.tracks != null)
                    result.AddRange(wrapper.tracks);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ByteTrackClient] Parse error: {e.Message}");
            }
            return result;
        }

        void OnDestroy()
        {
            _sendClient?.Close();
            _receiveClient?.Close();
        }
    }

    [Serializable]
    public class TrackedObject
    {
        public int    track_id;
        public int    cls;
        public string class_name;
        public float  confidence;
        public float  x1, y1, x2, y2;
        public float  wx, wy, wz;
        public string state;

        public Vector3 WorldPosition => new Vector3(wx, wy, wz);
        public bool IsTracked => state == "tracked";
    }

    [Serializable]
    internal class TrackResponse
    {
        public int                 frame_id;
        public List<TrackedObject> tracks;
    }
}