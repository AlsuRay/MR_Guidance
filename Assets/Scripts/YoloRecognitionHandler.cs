using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;
using MixedReality.Toolkit;
using MixedReality.Toolkit.Subsystems;
using MixedReality.Toolkit.UX;

namespace Assets.Scripts
{
    public class YoloRecognitionHandler : MonoBehaviour
    {
        private readonly List<DisplayedItem> yoloItems = new();

        [SerializeField]
        private GameObject labelObject;

        private YoloDebugOutput          yoloDebugOutput;
        private ByteTrackClient          byteTrackClient;
        private HandsAggregatorSubsystem _hands;

        [Header("Hand Proxy Settings")]
        public float grabProximityThreshold = 0.15f;  // было 0.25f

        // ── Experiment type switching ─────────────────────────────────────

        private static readonly HashSet<ObjectClass> SimpleExperimentClasses = new()
        {
            ObjectClass.Keyboard,
            ObjectClass.Mouse,
            ObjectClass.Cup,
            ObjectClass.Bottle,
            ObjectClass.Tv,
            ObjectClass.Spoon,
        };

        private static readonly HashSet<ObjectClass> ChemLabClasses = new()
        {
            ObjectClass.Beaker,
            ObjectClass.DyeBottle,
            ObjectClass.Rack,
            //ObjectClass.StirRod,
            ObjectClass.TestTube,
            //ObjectClass.WaterBottle,
        };

        private static readonly HashSet<ObjectClass> UngrabbableClasses = new()
        {
            ObjectClass.Rack,
        };

        private static HashSet<ObjectClass> AllowedClasses = SimpleExperimentClasses;

        public void SetExperimentType(bool isChemLab)
        {
            // Сбросить ByteTrack сервер синхронно с очисткой Unity
            byteTrackClient?.SendReset();

            AllowedClasses = isChemLab ? ChemLabClasses : SimpleExperimentClasses;

            var executor = gameObject.GetComponent<YoloModelExecutor>();
            executor?.SwitchModel(isChemLab);

            foreach (var item in yoloItems)
                if (item.TrackingMarker != null) Destroy(item.TrackingMarker);
            yoloItems.Clear();
            Debug.Log($"[YoloRecognitionHandler] Experiment: {(isChemLab ? "ChemLab" : "Simple")}");
        }

        // ── Highlight ─────────────────────────────────────────────────────

        public void SetHighlight(string objectName, bool highlighted)
{
    string normalized = objectName.Replace("_", "").ToLower();
    foreach (var item in yoloItems)
    {
        string className = item.YoloItem.MostLikelyClass.ToString().ToLower();
        if (className.Contains(normalized) || normalized.Contains(className))
        {
            item.ShowLabel = highlighted;
        }
    }
}

        // ── System event CSV log ──────────────────────────────────────────

        private static string _systemLogPath;
        private static bool   _systemLogInitialized = false;

        private void InitSystemLog()
        {
            if (_systemLogInitialized) return;
            _systemLogPath = System.IO.Path.Combine(Application.persistentDataPath,
                $"system_log_{DateTime.Now:yyyyMMdd_HHmm}.csv");
            using (var sw = new System.IO.StreamWriter(_systemLogPath, false))
                sw.WriteLine("Timestamp,Event,ObjectClass,TrackId,TimesSeen,TrackingMode,Details,UnityTime");
            Debug.Log($"[YoloRecognitionHandler] System log: {_systemLogPath}");
            _systemLogInitialized = true;
        }

        private void LogSystem(string evt, string objClass, int trackId, int timesSeen, string mode, string details = "")
        {
            if (!_systemLogInitialized) return;
            string line = $"{DateTime.UtcNow:o},{evt},{objClass},{trackId},{timesSeen},{mode},{details},{Time.realtimeSinceStartup:F4}";
            Debug.Log($"[System] {line}");
            try { using (var sw = new System.IO.StreamWriter(_systemLogPath, true)) sw.WriteLine(line); }
            catch (Exception) { }
        }

        private void Start()
        {
            InitSystemLog();
            yoloDebugOutput = gameObject.GetComponent<YoloDebugOutput>();
            byteTrackClient = gameObject.GetComponent<ByteTrackClient>();
            _hands = XRSubsystemHelpers.GetFirstRunningSubsystem<HandsAggregatorSubsystem>();

            if (byteTrackClient == null)
                Debug.LogWarning("[YoloRecognitionHandler] ByteTrackClient not found");
            if (_hands == null)
                Debug.LogWarning("[YoloRecognitionHandler] HandsAggregatorSubsystem not found");

            MRTKGrab.OnGrab    += HandleGrab;
            MRTKGrab.OnRelease += HandleRelease;
            MRTKGrab.OnPoke    += HandlePoke;
        }

        private void OnDestroy()
        {
            MRTKGrab.OnGrab    -= HandleGrab;
            MRTKGrab.OnRelease -= HandleRelease;
            MRTKGrab.OnPoke    -= HandlePoke;
        }

        // ── Grab / Release ────────────────────────────────────────────────

        private void HandleGrab(string objName, string handName, Vector3 handPos)
        {
            // Block ungrabbable
            if (System.Enum.TryParse<ObjectClass>(objName, out ObjectClass cls) &&
                UngrabbableClasses.Contains(cls))
            {
                Debug.Log($"[HandleGrab] {objName} is ungrabbable — ignored");
                return;
            }

            MRTKGrab.SetSceneObjects(BuildSceneObjectsString(objName));

            // Case 1: second hand grabs same object — mark as potential transfer
            var heldItem = yoloItems.FirstOrDefault(i =>
                i.YoloItem.MostLikelyClass.ToString() == objName &&
                i.TrackingMode == TrackingMode.HandProxy &&
                i.GrabbedByHand != handName &&
                Vector3.Distance(i.PositionInSpace, handPos) < 0.50f);

            if (heldItem != null)
            {
                heldItem.SecondHandName     = handName;
                heldItem.SecondHandGrabTime = Time.time;
                LogSystem("potential_transfer", objName, heldItem.TrackId,
                          heldItem.TimesSeen, "HandProxy", $"second_hand={handName}");
                Debug.Log($"[HandleGrab] Potential transfer {objName} — waiting for release");
                return;
            }

            // Case 2: normal grab
            var item = yoloItems
                .Where(i => i.YoloItem.MostLikelyClass.ToString() == objName &&
                            i.TrackingMode != TrackingMode.HandProxy)
                .OrderBy(i => Vector3.Distance(i.PositionInSpace, handPos))
                .FirstOrDefault();

            if (item != null && Vector3.Distance(item.PositionInSpace, handPos) < 0.50f)
            {
                item.StartHandProxy(handName, handPos);
                LogSystem("grab_proxy", objName, item.TrackId, item.TimesSeen, "HandProxy",
                          $"hand={handName} dist={Vector3.Distance(item.PositionInSpace, handPos):F3}");
                Debug.Log($"[HandleGrab] → HandProxy: {objName} {handName}");
            }
            else
            {
                string distInfo = item != null
                    ? Vector3.Distance(item.PositionInSpace, handPos).ToString("F2")
                    : "no item";
                LogSystem("grab_ignored", objName, item?.TrackId ?? -1, item?.TimesSeen ?? 0,
                          "N/A", $"hand={handName} dist={distInfo}");
                Debug.Log($"[HandleGrab] Ignored — no {objName} near {handName} (dist={distInfo})");
            }
        }

        private void HandleRelease(string objName, string handName, Vector3 handPos)
        {
            var item = yoloItems.FirstOrDefault(i =>
                i.YoloItem.MostLikelyClass.ToString() == objName &&
                i.TrackingMode == TrackingMode.HandProxy &&
                i.GrabbedByHand == handName);

            if (item != null)
            {
                MRTKGrab.SetSceneObjects(BuildSceneObjectsString(objName));

                // Check if second hand grabbed within window — confirmed transfer
                if (!string.IsNullOrEmpty(item.SecondHandName) &&
                    Time.time - item.SecondHandGrabTime < 1.5f)
                {
                    string newHand          = item.SecondHandName;
                    item.SecondHandName     = null;
                    item.SecondHandGrabTime = 0f;
                    item.GrabbedByHand      = newHand;
                    item.HandProxyOffset    = item.PositionInSpace - handPos;
                    LogSystem("transfer_confirmed", objName, item.TrackId, item.TimesSeen,
                              "HandProxy", $"from={handName} to={newHand}");
                    Debug.Log($"[HandleRelease] Transfer confirmed: {objName} → {newHand}");
                    return;
                }

                // Normal release
                item.SecondHandName     = null;
                item.SecondHandGrabTime = 0f;
                item.OnRelease();
                LogSystem("release_proxy", objName, item.TrackId, item.TimesSeen,
                          "Lost", $"hand={handName}");
                Debug.Log($"[HandleRelease] → Lost: {objName} {handName}");
            }
            else
            {
                LogSystem("release_ignored", objName, -1, 0, "N/A", $"hand={handName}");
                Debug.Log($"[HandleRelease] Ignored — {objName} not held by {handName}");
            }
        }

        private void HandlePoke(string objName, string handName, Vector3 pokePos)
        {
            LogSystem("poke", objName, -1, 0, "N/A", $"hand={handName}");
            Debug.Log($"[HandlePoke] {objName} poked by {handName}");
        }

        // ── Scene Objects String ──────────────────────────────────────────

        private string BuildSceneObjectsString(string excludeObject)
        {
            var parts = new List<string>();
            foreach (var item in yoloItems)
            {
                if (item.TrackingMode == TrackingMode.HandProxy) continue;
                if (item.YoloItem.MostLikelyClass.ToString() == excludeObject) continue;
                if (item.TimesSeen < ANCHOR_THRESHOLD) continue;
                var p = item.PositionInSpace;
                parts.Add($"{item.YoloItem.MostLikelyClass}:{p.x:F3};{p.y:F3};{p.z:F3}");
            }
            return string.Join("|", parts);
        }

        private string BuildSceneObjectsStringAll()
        {
            var parts = new List<string>();
            foreach (var item in yoloItems)
            {
                if (item.TimesSeen < ANCHOR_THRESHOLD && item.TrackingMode != TrackingMode.HandProxy) continue;
                var p = item.PositionInSpace;
                parts.Add($"{item.YoloItem.MostLikelyClass}:{p.x:F3};{p.y:F3};{p.z:F3}");
            }
            return string.Join("|", parts);
        }

        // ── Update ────────────────────────────────────────────────────────

        private void Update()
        {
            UpdateHandProxyItems();
            UpdateIndicators();
            ClearExpiredSecondHand();
        }

        private void ClearExpiredSecondHand()
        {
            foreach (var item in yoloItems)
            {
                if (!string.IsNullOrEmpty(item.SecondHandName) &&
                    Time.time - item.SecondHandGrabTime > 1.5f)
                {
                    Debug.Log($"[ClearExpiredSecondHand] Timeout — not a transfer: {item.YoloItem.MostLikelyClass}");
                    item.SecondHandName     = null;
                    item.SecondHandGrabTime = 0f;
                }
            }
        }

        private void UpdateIndicators()
        {
            foreach (var item in yoloItems)
            {
                if (item.TrackingMarker == null) continue;

                var grab   = item.TrackingMarker.GetComponent<MRTKGrab>();
                var sphere = item.TrackingMarker.transform.Find("IndicatorSphere");

                if (sphere == null)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "IndicatorSphere";
                    go.transform.SetParent(item.TrackingMarker.transform, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localScale    = new Vector3(0.02f, 0.02f, 0.02f);
                    Destroy(go.GetComponent<Collider>());
                    var rend = go.GetComponent<Renderer>();
                    var mat  = new Material(Shader.Find("Graphics Tools/Standard"));
                    mat.color    = Color.white;
                    rend.material = mat;
                    sphere = go.transform;
                }

                var sphereRenderer = sphere.GetComponent<Renderer>();

                if (item.TrackingMode == TrackingMode.Lost)
                {
                    sphere.gameObject.SetActive(false);
                    continue;
                }

                if (item.TimesSeen < ANCHOR_THRESHOLD && item.TrackingMode != TrackingMode.HandProxy)
                {
                    sphere.gameObject.SetActive(false);
                    continue;
                }

                sphere.gameObject.SetActive(true);

                if (item.ShowLabel)
                {
                    // LLM highlight — gold, pulsing
                    float pulse = 0.03f + 0.012f * Mathf.Sin(Time.time * 4f);
                    sphereRenderer.material.color = new Color(1f, 0.84f, 0f);
                    sphere.localScale = new Vector3(pulse, pulse, pulse);
                }
                else if (item.TrackingMode == TrackingMode.HandProxy)
                {
                    // Grabbed — green
                    sphereRenderer.material.color = Color.green;
                    sphere.localScale = new Vector3(0.025f, 0.025f, 0.025f);
                }
                else if (grab != null && grab.IsGazed)
                {
                    // Gazed — magenta
                    sphereRenderer.material.color = Color.magenta;
                    sphere.localScale = new Vector3(0.025f, 0.025f, 0.025f);
                }
                else
                {
                    // Anchored — white
                    sphereRenderer.material.color = Color.white;
                    sphere.localScale = new Vector3(0.02f, 0.02f, 0.02f);
                }
            }
        }

        // ── Main entry point ──────────────────────────────────────────────

        public void ShowRecognitions(List<YoloItem> recognitions, CameraTransform cameraTransform)
        {
            if (byteTrackClient != null)
            {
                var tracks = byteTrackClient.GetLatestTracks();
                if (tracks.Count > 0)
                    ApplyByteTrackResults(tracks, recognitions, cameraTransform);
                else
                    AddNewlyRecognizedObjects(recognitions, cameraTransform);
            }
            else
            {
                AddNewlyRecognizedObjects(recognitions, cameraTransform);
            }

            RemoveOutdatedObjects();
            TriggerDetectionActions();
        }

        // ── Hand position ─────────────────────────────────────────────────

        private Vector3? GetHandPosition(XRNode hand)
        {
            if (_hands == null) return null;
            if (_hands.TryGetJoint(TrackedHandJoint.Palm, hand, out HandJointPose pose))
                return pose.Position;
            return null;
        }

        // ── ByteTrack integration ─────────────────────────────────────────

        private void ApplyByteTrackResults(List<TrackedObject> tracks,
                                            List<YoloItem> rawDetections,
                                            CameraTransform cameraTransform)
        {
            var matchedKeys        = new HashSet<(int, ObjectClass)>();
            var activeTrackClasses = new HashSet<ObjectClass>();

            foreach (var track in tracks)
            {
                var objectClass = (ObjectClass)track.cls;
                if (!AllowedClasses.Contains(objectClass)) continue;
                activeTrackClasses.Add(objectClass);

                var matchingYolo = FindMatchingYoloItem(rawDetections, track);

                Vector3? worldPos = null;
                if (matchingYolo.HasValue)
                    worldPos = PositionCalculator.CalculatePointInSpace(matchingYolo.Value, cameraTransform);

                var existing = yoloItems.FirstOrDefault(i =>
                    i.TrackId == track.track_id && i.TrackClass == objectClass);

                if (existing != null)
                {
                    if (existing.TrackingMode != TrackingMode.HandProxy)
                    {
                        if (matchingYolo.HasValue && worldPos.HasValue)
                            existing.UpdateItem(matchingYolo.Value, worldPos.Value);
                        else
                        {
                            existing.TimeLastSeen   = Time.time;
                            existing.IsInCameraView = true;
                        }
                    }
                    matchedKeys.Add((track.track_id, objectClass));
                }
                else if (matchingYolo.HasValue && worldPos.HasValue)
                {
                    var handProxyItem = yoloItems.FirstOrDefault(i =>
                        i.YoloItem.MostLikelyClass == objectClass &&
                        i.TrackingMode == TrackingMode.HandProxy);

                    if (handProxyItem != null)
                    {
                        handProxyItem.UpdateTrackId(track.track_id, objectClass);
                        matchedKeys.Add((track.track_id, objectClass));
                        continue;
                    }

                    var sameClass = yoloItems.FirstOrDefault(i =>
                        i.YoloItem.MostLikelyClass == objectClass &&
                        i.TrackId != track.track_id &&
                        i.TrackingMode != TrackingMode.HandProxy);

                    if (sameClass != null)
                    {
                        sameClass.UpdateTrackId(track.track_id, objectClass);
                        sameClass.UpdateItem(matchingYolo.Value, worldPos.Value);
                        matchedKeys.Add((track.track_id, objectClass));
                    }
                    else
                    {
                        var untracked = yoloItems.FirstOrDefault(i =>
                            i.TrackId == -1 &&
                            i.YoloItem.MostLikelyClass == objectClass &&
                            i.TrackingMode != TrackingMode.HandProxy);

                        if (untracked != null)
                        {
                            untracked.UpdateTrackId(track.track_id, objectClass);
                            untracked.UpdateItem(matchingYolo.Value, worldPos.Value);
                            matchedKeys.Add((track.track_id, objectClass));
                        }
                        else
                        {
                            var newItem = new DisplayedItem(matchingYolo.Value, worldPos.Value);
                            newItem.UpdateTrackId(track.track_id, objectClass);
                            yoloItems.Add(newItem);
                            matchedKeys.Add((track.track_id, objectClass));
                        }
                    }
                }
            }

            for (int i = yoloItems.Count - 1; i >= 0; i--)
            {
                var item = yoloItems[i];
                if (item.TrackId == -1) continue;
                if (item.TrackingMode == TrackingMode.HandProxy) continue;
                if (matchedKeys.Contains((item.TrackId, item.TrackClass))) continue;

                if (activeTrackClasses.Contains(item.TrackClass))
                {
                    if (item.TimesSeen >= ANCHOR_THRESHOLD) continue;
                    LogSystem("remove_stale", item.YoloItem.MostLikelyClass.ToString(), item.TrackId, item.TimesSeen, item.TrackingMode.ToString(), "not_anchored");
                    Debug.Log($"[ApplyByteTrack] Removing stale {item.YoloItem.MostLikelyClass} track_id={item.TrackId} s={item.TimesSeen}");
                    Destroy(item.TrackingMarker);
                    yoloItems.RemoveAt(i);
                    continue;
                }
                // TryActivateHandProxy(item); // disabled — proximity grab caused false positives
            }
        }

        private YoloItem? FindMatchingYoloItem(List<YoloItem> detections, TrackedObject track)
        {
            var objectClass = (ObjectClass)track.cls;
            YoloItem? best  = null;
            float bestIoU   = 0.3f;
            float W = Parameters.ModelImageResolution.x;
            float H = Parameters.ModelImageResolution.y;

            foreach (var det in detections)
            {
                if (det.MostLikelyClass != objectClass) continue;
                float detX1 = det.TopLeft.x     / W;
                float detY1 = det.TopLeft.y     / H;
                float detX2 = det.BottomRight.x / W;
                float detY2 = det.BottomRight.y / H;
                float iou   = ComputeIoU(detX1, detY1, detX2, detY2,
                                         track.x1, track.y1, track.x2, track.y2);
                if (iou > bestIoU) { bestIoU = iou; best = det; }
            }
            return best;
        }

        private float ComputeIoU(float ax1, float ay1, float ax2, float ay2,
                                  float bx1, float by1, float bx2, float by2)
        {
            float ix1    = Mathf.Max(ax1, bx1), iy1 = Mathf.Max(ay1, by1);
            float ix2    = Mathf.Min(ax2, bx2), iy2 = Mathf.Min(ay2, by2);
            float inter  = Mathf.Max(0, ix2 - ix1) * Mathf.Max(0, iy2 - iy1);
            float unionA = (ax2-ax1)*(ay2-ay1) + (bx2-bx1)*(by2-by1) - inter;
            return unionA > 0 ? inter / unionA : 0f;
        }

        // ── Hand Proxy ────────────────────────────────────────────────────

        private const float ReleaseBlockSeconds = 1.5f;

        private void TryActivateHandProxy(DisplayedItem item)
        {
            Vector3? leftPos  = GetHandPosition(XRNode.LeftHand);
            Vector3? rightPos = GetHandPosition(XRNode.RightHand);

            foreach (var (pos, name) in new[] { (leftPos, "LeftHand"), (rightPos, "RightHand") })
            {
                if (pos == null) continue;
                if (item.LastReleasedByHand == name &&
                    Time.time - item.LastReleaseTime < ReleaseBlockSeconds) continue;
                float dist = Vector3.Distance(item.PositionInSpace, pos.Value);
                if (dist < grabProximityThreshold)
                {
                    item.StartHandProxy(name, pos.Value);
                    Debug.Log($"[TryActivateHandProxy] {item.YoloItem.MostLikelyClass} → {name}");
                    return;
                }
            }
        }

        private void UpdateHandProxyItems()
        {
            Vector3? leftPos  = GetHandPosition(XRNode.LeftHand);
            Vector3? rightPos = GetHandPosition(XRNode.RightHand);
            bool anyHandProxy = false;

            foreach (var item in yoloItems)
            {
                if (item.TrackingMode != TrackingMode.HandProxy) continue;
                anyHandProxy = true;
                var handPos = (item.GrabbedByHand == "LeftHand") ? leftPos : rightPos;
                if (handPos == null) continue;
                item.UpdateHandProxy(handPos.Value);
                if (item.TrackingMarker != null)
                    item.TrackingMarker.transform.position = item.PositionInSpace;
            }

            if (anyHandProxy)
                MRTKGrab.SetSceneObjects(BuildSceneObjectsStringAll());
        }

        // ── YOLO-only fallback ────────────────────────────────────────────

        private void AddNewlyRecognizedObjects(List<YoloItem> recognitions, CameraTransform cameraTransform)
        {
            List<DisplayedItem> unmatched = new(yoloItems);

            foreach (YoloItem newItem in recognitions)
            {
                if (!AllowedClasses.Contains(newItem.MostLikelyClass)) continue;
                Vector3? pos = PositionCalculator.CalculatePointInSpace(newItem, cameraTransform);
                if (pos == null) continue;

                DisplayedItem lostItem = unmatched.FirstOrDefault(i =>
                    i.YoloItem.MostLikelyClass == newItem.MostLikelyClass &&
                    i.TrackingMode == TrackingMode.Lost);

                if (lostItem != null)
                {
                    unmatched.Remove(lostItem);
                    lostItem.UpdateItem(newItem, pos.Value);
                    LogSystem("reanchor", newItem.MostLikelyClass.ToString(), lostItem.TrackId, lostItem.TimesSeen, "Lost→ByteTrack", "");
                    continue;
                }

                DisplayedItem item = GetClosestExistingItem(unmatched, newItem, pos.Value);
                if (item == null)
                {
                    item = new DisplayedItem(newItem, pos.Value);
                    yoloItems.Add(item);
                }
                else
                {
                    unmatched.Remove(item);
                    item.UpdateItem(newItem, pos.Value);
                }
            }
        }

        private DisplayedItem GetClosestExistingItem(List<DisplayedItem> oldItems,
                                                      YoloItem item, Vector3 positionInSpace)
        {
            DisplayedItem closest     = null;
            float         closestDist = float.MaxValue;

            foreach (DisplayedItem old in oldItems)
            {
                if (!old.YoloItem.MostLikelyClass.Equals(item.MostLikelyClass)) continue;
                float d = Vector3.Distance(old.PositionInSpace, positionInSpace);
                if (d > Parameters.MaxIdenticalObject || d >= closestDist) continue;
                closest     = old;
                closestDist = d;
            }
            return closest;
        }

        // ── Remove outdated ───────────────────────────────────────────────

        private const int ANCHOR_THRESHOLD = 1;  // было 3

        private void RemoveOutdatedObjects()
        {
            for (int i = yoloItems.Count - 1; i >= 0; i--)
            {
                var item = yoloItems[i];
                if (item.TrackingMode == TrackingMode.HandProxy) continue;
                if (item.TimesSeen >= ANCHOR_THRESHOLD) continue;

                bool isInCameraView = item.TrackId != -1
                    ? true
                    : PositionCalculator.IsObjectInCameraView(item.PositionInSpace);

                if (!isInCameraView)
                {
                    item.TimeLastSeen   = Time.time;
                    item.IsInCameraView = false;
                    continue;
                }

                item.IsInCameraView = true;

                if (Time.time - item.TimeLastSeen > Parameters.ObjectTimeOut)
                {
                    LogSystem("remove_timeout", item.YoloItem.MostLikelyClass.ToString(), item.TrackId, item.TimesSeen, item.TrackingMode.ToString(), $"age={Time.time - item.TimeLastSeen:F1}s");
                    Debug.Log($"[RemoveOutdated] Removing {item.YoloItem.MostLikelyClass} " +
                              $"track_id={item.TrackId} s={item.TimesSeen} " +
                              $"mode={item.TrackingMode} age={Time.time - item.TimeLastSeen:F1}s");
                    Destroy(item.TrackingMarker);
                    yoloItems.RemoveAt(i);
                }
            }
        }

        // ── Trigger actions ───────────────────────────────────────────────

        private void TriggerDetectionActions()
        {
            foreach (DisplayedItem item in yoloItems.Where(
                item => item.IsInCameraView &&
                        (item.TimesSeen >= Parameters.MinTimesSeen || item.TrackId != -1)))
            {
                ManageTrackingMarker(item);
                yoloDebugOutput.ShowDebugInformationForItem(item);
            }
        }

        private const float LabelHeightOffset = 0.08f;

        private void ManageTrackingMarker(DisplayedItem item)
        {
            if (item.TrackingMarker == null)
            {
                Vector3 spawnPos = item.PositionInSpace + Vector3.up * LabelHeightOffset;
                item.TrackingMarker = Instantiate(labelObject, spawnPos, Quaternion.identity);

                var collider = item.TrackingMarker.GetComponent<BoxCollider>();
                float distance    = Vector3.Distance(Camera.main.transform.position, item.PositionInSpace);
                float worldWidth  = Mathf.Clamp(EstimateWorldSize(item.YoloItem.Size.x, 1920f, 64f, distance), 0.03f, 0.25f);
                float worldHeight = Mathf.Clamp(EstimateWorldSize(item.YoloItem.Size.y, 1080f, 40f, distance), 0.03f, 0.30f);
                collider.size   = new Vector3(worldWidth, worldHeight, 0.10f);
                collider.center = new Vector3(0f, 0f, -0.03f);

                var interactable = item.TrackingMarker.GetComponent<StatefulInteractable>();
                if (interactable != null)
                {
                    interactable.allowGazeInteraction = true;
                    interactable.colliders.Clear();
                    interactable.colliders.Add(collider);
                }

                var logger = item.TrackingMarker.AddComponent<YoloGazeLogger>();
                logger.objectName = item.YoloItem.MostLikelyClass.ToString();

                var handLogger = item.TrackingMarker.AddComponent<MRTKGrab>();
                handLogger.objectName = item.YoloItem.MostLikelyClass.ToString();

                // Set ungrabbable flag for objects in UngrabbableClasses
                handLogger.isUngrabbable = UngrabbableClasses.Contains(item.YoloItem.MostLikelyClass);

                if (interactable != null)
                {
                    interactable.hoverEntered.AddListener(args => {
                        logger.OnGazeEntered(args);
                        handLogger.OnGazeEnter();
                    });
                    interactable.hoverExited.AddListener(args => {
                        logger.OnGazeExited(args);
                        handLogger.OnGazeExit();
                    });
                }

                var initLabelCtrl = item.TrackingMarker.GetComponent<ObjectLabelController>();
                if (initLabelCtrl.LineRenderer != null)
                    initLabelCtrl.LineRenderer.enabled = false;
                if (initLabelCtrl.ContentParent != null)
                    initLabelCtrl.ContentParent.SetActive(false);
            }

            item.TrackingMarker.transform.position = item.PositionInSpace + Vector3.up * LabelHeightOffset;
        }
        private float EstimateWorldSize(float bboxPixels, float imagePixels, float fovDegrees, float distance)
{
    float ratio    = bboxPixels / imagePixels;
    float angleRad = ratio * (fovDegrees * Mathf.Deg2Rad);
    return 2f * distance * Mathf.Tan(angleRad / 2f);
}
    }
}