"""
ByteTrack Server for HoloLens YOLO Object Tracking
====================================================
Receives YOLO detections from HoloLens Unity app,
runs ByteTrack to maintain stable object IDs,
returns tracked results back to Unity.

Port 5020: receive detections from HoloLens
Port 5021: send tracked results back to Unity
"""

import json
import socket
import time
import numpy as np
from boxmot import ByteTrack

# ─── CONFIG ───────────────────────────────────────────
RECEIVE_PORT = 5020
SEND_PORT    = 5021
HOLOLENS_IP  = "192.168.1.146"

TRACK_THRESH   = 0.4
MATCH_THRESH   = 0.8
TRACK_BUFFER   = 10
RESET_TIMEOUT  = 10.0  # seconds of no packets → reset tracker
# ──────────────────────────────────────────────────────

CLASS_NAMES = {
    0: "Beaker",
    1: "DyeBottle",
    2: "Rack",
    3: "StirRod",
    4: "TestTube",
    5: "WaterBottle",
    39: "Bottle",
    41: "Cup",
    44: "Spoon",
    62: "Tv",
    64: "Mouse",
    66: "Keyboard",
}


class ByteTrackServer:
    def __init__(self):
        self.tracker = self._make_tracker()
        self.track_world_pos = {}
        self.frame_count = 0
        self.last_packet_time = time.time()

        self.recv_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.recv_sock.bind(("0.0.0.0", RECEIVE_PORT))
        self.recv_sock.settimeout(1.0)

        self.send_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        print(f"ByteTrack server ready")
        print(f"Listening on UDP:{RECEIVE_PORT}")
        print(f"Sending to {HOLOLENS_IP}:{SEND_PORT}")

    def _make_tracker(self):
        return ByteTrack(
            track_thresh=TRACK_THRESH,
            match_thresh=MATCH_THRESH,
            track_buffer=TRACK_BUFFER,
            min_hits=1,
            per_class=True,
        )

    def _reset_tracker(self):
        self.tracker = self._make_tracker()
        self.track_world_pos = {}
        self.frame_count = 0
        print("Tracker reset — app reconnected")

    def run(self):
        print("Running...")
        while True:
            try:
                data, addr = self.recv_sock.recvfrom(65535)
                self.last_packet_time = time.time()

                result = self._handle_packet(data, addr)
                if result and result.get("tracks"):
                    response = json.dumps(result).encode("utf-8")
                    print(f"Sending: {json.dumps(result)[:200]}")
                    print(f"JSON length: {len(response)} bytes")
                    self.send_sock.sendto(response, (addr[0], SEND_PORT))

            except socket.timeout:
                if time.time() - self.last_packet_time > RESET_TIMEOUT:
                    print(f"No packets for {RESET_TIMEOUT}s — resetting tracker")
                    self._reset_tracker()
                    self.last_packet_time = time.time()
                continue

            except KeyboardInterrupt:
                print("Shutting down")
                break

            except Exception as e:
                print(f"Error: {e}")

    def _handle_packet(self, data, addr):
        try:
            packet = json.loads(data.decode("utf-8"))
        except Exception as e:
            print(f"JSON parse error: {e}")
            return None

        # ── Reset signal from Unity (sent on SetExperimentType) ──────────
        if packet.get("reset"):
            self._reset_tracker()
            return None
        # ─────────────────────────────────────────────────────────────────

        frame_id = packet.get("frame_id", self.frame_count)
        detections = packet.get("detections", [])
        self.frame_count += 1

        if not detections:
            empty = np.empty((0, 6))
            img_dummy = np.zeros((640, 640, 3), dtype=np.uint8)
            self.tracker.update(empty, img_dummy)
            return {"frame_id": frame_id, "tracks": []}

        det_array = np.array([
            [d["x1"], d["y1"], d["x2"], d["y2"], d["confidence"], d["class"]]
            for d in detections
        ], dtype=np.float32)

        det_world_pos = []
        for d in detections:
            det_world_pos.append((d["wx"], d["wy"], d["wz"]))

        try:
            img_dummy = np.zeros((640, 640, 3), dtype=np.uint8)
            tracks = self.tracker.update(det_array, img_dummy)
        except Exception as e:
            print(f"ByteTrack error: {e}")
            return {"frame_id": frame_id, "tracks": []}

        result_tracks = []

        if tracks is not None and len(tracks) > 0:
            for track in tracks:
                x1, y1, x2, y2 = float(track[0]), float(track[1]), \
                                  float(track[2]), float(track[3])
                track_id = int(track[4])
                conf     = float(track[5])
                cls      = int(track[6]) if len(track) > 6 else 0

                wx, wy, wz = 0.0, 0.0, 0.0
                track_cx = (x1 + x2) / 2
                track_cy = (y1 + y2) / 2

                best_dist = float("inf")
                best_idx  = -1
                for i, d in enumerate(detections):
                    if int(d["class"]) != cls:
                        continue
                    det_cx = (d["x1"] + d["x2"]) / 2
                    det_cy = (d["y1"] + d["y2"]) / 2
                    dist = (track_cx - det_cx) ** 2 + (track_cy - det_cy) ** 2
                    if dist < best_dist:
                        best_dist = dist
                        best_idx  = i

                if best_idx >= 0:
                    wx, wy, wz = det_world_pos[best_idx]
                    self.track_world_pos[(cls, track_id)] = (wx, wy, wz)
                elif (cls, track_id) in self.track_world_pos:
                    wx, wy, wz = self.track_world_pos[(cls, track_id)]

                class_name = CLASS_NAMES.get(cls, f"Class_{cls}")

                result_tracks.append({
                    "track_id": track_id,
                    "cls":      cls,
                    "class_name": class_name,
                    "confidence": conf,
                    "x1": x1, "y1": y1,
                    "x2": x2, "y2": y2,
                    "wx": wx, "wy": wy, "wz": wz,
                    "state": "tracked"
                })

                print(f"  track_id={track_id} class={class_name} "
                      f"conf={conf:.2f} pos=({wx:.2f},{wy:.2f},{wz:.2f})")

        return {"frame_id": frame_id, "tracks": result_tracks}


if __name__ == "__main__":
    server = ByteTrackServer()
    server.run()