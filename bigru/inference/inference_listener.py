"""
User State Inference Server (BiGRU) with Baseline Calibration
==============================================================
Protocol:
- Phase 1 (calibrating): accumulate P(struggling) values during calm interaction
- Phase 2 (running):     threshold = percentile(baseline, 75), normal inference

Calibration trigger:
- Start: POST http://localhost:8001/calibrate/start
- End:   POST http://localhost:8001/calibrate/end
  (or auto-end after CALIBRATION_TIMEOUT seconds)

Session logging:
- Start: POST http://localhost:8001/session/start  {participant, system_type, experiment}
- End:   POST http://localhost:8001/session/end

UDP ports:
- 5007: receive raw sensor frames from Unity
- 5008: send predictions to Unity

Response to Unity:
{
    "t_unity": 123.45,
    "user_state": "calibrating" | "stable" | "struggling",
    "p_struggling": 0.82,
    "p_struggling_ema": 0.74
}
"""

import csv
import socket
import json
import threading
import time
import numpy as np
from collections import deque
from datetime import datetime
from pathlib import Path
import tensorflow as tf
from http.server import HTTPServer, BaseHTTPRequestHandler

# =============================================================================
# CONFIGURATION
# =============================================================================

UDP_RECEIVE_IP   = "0.0.0.0"
UDP_RECEIVE_PORT = 5007

UNITY_IP        = "192.168.1.146"
UNITY_SEND_PORT = 5008

CONTROL_PORT = 8001

MODEL_PATH = Path(__file__).parent / "deployment" / "user_state" / "best_model.keras"
LOGS_DIR   = Path(__file__).parent / "logs"

BUFFER_SIZE         = 120
DEFAULT_THRESHOLD   = 0.5
EMA_ALPHA           = 0.3
CALIBRATION_TIMEOUT = 180.0

FRAME_KEYS = [
    'gaze_ox', 'gaze_oy',
    'gaze_dx', 'gaze_dy', 'gaze_dz',
    'head_rx', 'head_ry', 'head_rz', 'head_rw',
    'head_px', 'head_py', 'head_pz',
    'lp_x', 'lp_y', 'lp_z',
    'lw_x', 'lw_y', 'lw_z',
    'li_x', 'li_y', 'li_z',
    'rp_x', 'rp_y', 'rp_z',
    'rw_x', 'rw_y', 'rw_z',
    'ri_x', 'ri_y', 'ri_z',
]  # 30 features

BIGRU_LOG_FIELDS = [
    'session_id', 'participant_id', 'system_type', 'experiment_type',
    'wall_time', 't_unity',
    'p_struggling', 'ema_smoothed', 'user_state',
    'threshold', 'phase',  # 'calibrating' | 'running'
]

# =============================================================================
# SESSION LOG WRITER
# =============================================================================

class BiGRUSessionLogger:
    def __init__(self):
        self._csv_file   = None
        self._writer     = None
        self._lock       = threading.Lock()
        self.session_id  = None

    def start_session(self, participant: str, system_type: str, experiment: str):
        with self._lock:
            if self._csv_file is not None:
                self._csv_file.close()

            LOGS_DIR.mkdir(parents=True, exist_ok=True)
            ts = datetime.now().strftime('%Y%m%d_%H%M%S')
            self.session_id = f"{participant}_{system_type}_{experiment}_{ts}"
            path = LOGS_DIR / f"bigru_{self.session_id}.csv"

            self._csv_file = open(path, 'w', newline='', encoding='utf-8')
            self._writer   = csv.DictWriter(self._csv_file, fieldnames=BIGRU_LOG_FIELDS)
            self._writer.writeheader()
            self._csv_file.flush()

            print(f"[BiGRULogger] Session started → {path.name}")

    def end_session(self):
        with self._lock:
            if self._csv_file is not None:
                self._csv_file.close()
                self._csv_file = None
                self._writer   = None
                print(f"[BiGRULogger] Session ended: {self.session_id}")
                self.session_id = None

    def log(self, t_unity: float, prediction: dict, threshold: float, phase: str,
            participant: str, system_type: str, experiment: str):
        with self._lock:
            if self._writer is None:
                return
            self._writer.writerow({
                'session_id':       self.session_id or '',
                'participant_id':   participant,
                'system_type':      system_type,
                'experiment_type':  experiment,
                'wall_time':        datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3],
                't_unity':          round(t_unity, 4),
                'p_struggling':     prediction['p_struggling'],
                'ema_smoothed':     prediction['p_struggling_ema'],
                'user_state':       prediction['user_state'],
                'threshold':        round(threshold, 4),
                'phase':            phase,
            })
            self._csv_file.flush()


# =============================================================================
# USER STATE ESTIMATOR
# =============================================================================

class UserStateEstimator:
    def __init__(self, model_path: Path):
        print(f"Loading BiGRU model from: {model_path}")
        self.model = tf.keras.models.load_model(str(model_path))

        self.buffer         = deque(maxlen=BUFFER_SIZE)
        self.session_frames = []
        self.ema_value      = 0.0
        self.n_inferences   = 0

        # calibration state
        self.calibrating       = False
        self.calibration_done  = False
        self.baseline_probs    = []
        self.threshold         = DEFAULT_THRESHOLD
        self.calibration_start = None

        print(f"✓ Buffer size:         {BUFFER_SIZE} frames")
        print(f"✓ EMA alpha:           {EMA_ALPHA}")
        print(f"✓ Default threshold:   {DEFAULT_THRESHOLD}")
        print(f"✓ Calibration timeout: {CALIBRATION_TIMEOUT}s")

    def start_calibration(self):
        self.calibrating       = True
        self.calibration_done  = False
        self.baseline_probs    = []
        self.ema_value         = 0.0
        self.calibration_start = time.time()
        print("🔵 Calibration started — accumulating baseline P(struggling)...")

    def end_calibration(self):
        if not self.baseline_probs:
            print("⚠️  No baseline data — using default threshold.")
            self.threshold = DEFAULT_THRESHOLD
        else:
            self.threshold = float(np.percentile(self.baseline_probs, 75))
            print(f"✅ Calibration complete.")
            print(f"   Baseline samples: {len(self.baseline_probs)}")
            print(f"   P(struggling) — mean: {np.mean(self.baseline_probs):.3f} "
                  f"std: {np.std(self.baseline_probs):.3f}")
            print(f"   New threshold (75th percentile): {self.threshold:.3f}")

        self.calibrating      = False
        self.calibration_done = True

    def add_frame(self, frame_dict: dict):
        frame_vec = np.array(
            [frame_dict.get(k, 0.0) for k in FRAME_KEYS],
            dtype=np.float32
        )
        self.buffer.append(frame_vec)
        self.session_frames.append(frame_vec)

        if len(self.buffer) < BUFFER_SIZE:
            return None

        if self.calibrating and self.calibration_start is not None:
            if time.time() - self.calibration_start >= CALIBRATION_TIMEOUT:
                print("⏰ Calibration timeout — ending automatically.")
                self.end_calibration()

        return self._infer()

    def _infer(self) -> dict:
        buffer_array = np.stack(list(self.buffer), axis=0)  # (120, 30)

        session_array = np.stack(self.session_frames, axis=0)  # (N, 30)
        mean = session_array.mean(axis=0)
        std  = session_array.std(axis=0) + 1e-8
        buffer_normalized = (buffer_array - mean) / std

        X = buffer_normalized[np.newaxis, ...]  # (1, 120, 30)

        probs        = self.model.predict(X, verbose=0)[0]
        p_struggling = float(probs[1])

        if self.calibrating:
            self.baseline_probs.append(p_struggling)
            return {
                "user_state":       "calibrating",
                "p_struggling":     round(p_struggling, 4),
                "p_struggling_ema": round(p_struggling, 4),
            }

        self.ema_value = EMA_ALPHA * p_struggling + (1 - EMA_ALPHA) * self.ema_value
        self.n_inferences += 1

        user_state = "struggling" if self.ema_value >= self.threshold else "stable"

        if self.n_inferences % 30 == 0:
            ts = datetime.now().strftime("%H:%M:%S")
            print(f"[{self.n_inferences}] {ts} p={p_struggling:.3f} "
                  f"ema={self.ema_value:.3f} threshold={self.threshold:.3f} → {user_state}")

        return {
            "user_state":       user_state,
            "p_struggling":     round(p_struggling, 4),
            "p_struggling_ema": round(self.ema_value, 4),
        }


# =============================================================================
# HTTP CONTROL SERVER
# =============================================================================

estimator_ref   = None
session_logger  = None
_session_info   = {"participant": "P00", "system_type": "A", "experiment": "simple"}


class ControlHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path == "/calibrate/start":
            estimator_ref.start_calibration()
            self._respond(200, {"status": "calibration started"})

        elif self.path == "/calibrate/end":
            estimator_ref.end_calibration()
            self._respond(200, {
                "status":    "calibration ended",
                "threshold": estimator_ref.threshold,
                "n_samples": len(estimator_ref.baseline_probs),
            })

        elif self.path == "/session/start":
            length = int(self.headers.get("Content-Length", 0))
            body   = json.loads(self.rfile.read(length)) if length > 0 else {}
            _session_info["participant"]  = body.get("participant",  "P00")
            _session_info["system_type"]  = body.get("system_type",  "A")
            _session_info["experiment"]   = body.get("experiment",   "simple")
            session_logger.start_session(
                _session_info["participant"],
                _session_info["system_type"],
                _session_info["experiment"],
            )
            self._respond(200, {"status": "session started", "session_id": session_logger.session_id})

        elif self.path == "/session/end":
            session_logger.end_session()
            self._respond(200, {"status": "session ended"})

        else:
            self._respond(404, {"error": "unknown endpoint"})

    def do_GET(self):
        if self.path == "/status":
            self._respond(200, {
                "calibrating":      estimator_ref.calibrating,
                "calibration_done": estimator_ref.calibration_done,
                "threshold":        estimator_ref.threshold,
                "n_baseline":       len(estimator_ref.baseline_probs),
                "n_inferences":     estimator_ref.n_inferences,
                "n_session_frames": len(estimator_ref.session_frames),
                "session_id":       session_logger.session_id,
            })
        else:
            self._respond(404, {"error": "unknown endpoint"})

    def _respond(self, code, data):
        body = json.dumps(data).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args):
        pass


# =============================================================================
# SENDER
# =============================================================================

class PredictionSender:
    def __init__(self, unity_ip: str, unity_port: int):
        self.unity_ip   = unity_ip
        self.unity_port = unity_port
        self.sock       = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        print(f"📤 Sender ready → {unity_ip}:{unity_port}")

    def send(self, t_unity: float, prediction: dict):
        data = {
            "t_unity":          t_unity,
            "user_state":       prediction["user_state"],
            "p_struggling":     prediction["p_struggling"],
            "p_struggling_ema": prediction["p_struggling_ema"],
        }
        payload = json.dumps(data).encode('utf-8')
        self.sock.sendto(payload, (self.unity_ip, self.unity_port))
        self.sock.sendto(payload, ("127.0.0.1", self.unity_port))  # local relay

    def close(self):
        self.sock.close()


# =============================================================================
# MAIN
# =============================================================================

def main():
    global estimator_ref, session_logger

    estimator      = UserStateEstimator(MODEL_PATH)
    estimator_ref  = estimator
    session_logger = BiGRUSessionLogger()
    sender         = PredictionSender(UNITY_IP, UNITY_SEND_PORT)

    http_server = HTTPServer(("0.0.0.0", CONTROL_PORT), ControlHandler)
    http_thread = threading.Thread(target=http_server.serve_forever, daemon=True)
    http_thread.start()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((UDP_RECEIVE_IP, UDP_RECEIVE_PORT))

    print("\n" + "=" * 60)
    print("USER STATE INFERENCE SERVER READY")
    print("=" * 60)
    print(f"📥 Frames from Unity:    {UDP_RECEIVE_IP}:{UDP_RECEIVE_PORT}")
    print(f"📤 Predictions to Unity: {UNITY_IP}:{UNITY_SEND_PORT}")
    print(f"🔧 Control API:          http://localhost:{CONTROL_PORT}")
    print(f"   POST /calibrate/start  — begin calibration")
    print(f"   POST /calibrate/end    — end calibration + set threshold")
    print(f"   POST /session/start    — register session {{participant, system_type, experiment}}")
    print(f"   POST /session/end      — close session log")
    print(f"   GET  /status           — current state")
    print(f"⏳ Buffering {BUFFER_SIZE} frames before first inference...")
    print("=" * 60 + "\n")

    try:
        while True:
            data, _ = sock.recvfrom(65536)
            try:
                message = json.loads(data.decode('utf-8'))
            except Exception as e:
                print(f"❌ Parse error: {e}")
                continue

            t_unity    = message.get('t', 0.0)
            prediction = estimator.add_frame(message)
            if prediction is not None:
                sender.send(t_unity, prediction)

                # Log every inference to CSV
                phase = "calibrating" if estimator.calibrating else "running"
                session_logger.log(
                    t_unity     = t_unity,
                    prediction  = prediction,
                    threshold   = estimator.threshold,
                    phase       = phase,
                    participant = _session_info["participant"],
                    system_type = _session_info["system_type"],
                    experiment  = _session_info["experiment"],
                )

    except KeyboardInterrupt:
        print("\n🛑 Stopped")
    finally:
        session_logger.end_session()
        http_server.shutdown()
        sender.close()
        sock.close()


if __name__ == "__main__":
    main()