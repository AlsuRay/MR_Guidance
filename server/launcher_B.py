"""
session_launcher.py
====================
Launches all 4 backend processes needed for a Type B study session, each
in its own venv, and keeps them running until Ctrl+C:
    1. bytetrack_server.py  - object tracking
    2. adaptive_server_B    - LLM guidance server (uvicorn, :8000)
    3. inference_listener   - BiGRU struggling-state signal (UDP 5007→5008)
    4. run_udp_realtime     - step tracker + voice bridge (main driver)

Prompts for participant ID and experiment type (simple/chemlab), then starts
each process with a short delay so dependencies are ready in time. Ctrl+C
stops all four together.
"""

import subprocess
import time

BYTETRACK_DIR   = r"C:\Users\taeyeon\PycharmProjects\ByteTrack"
COORDINATOR_DIR = r"C:\Users\taeyeon\PycharmProjects\heuristicrecognizer\LLM_assistant\launchers"

BYTETRACK_PYTHON   = r"C:\Users\taeyeon\PycharmProjects\ByteTrack\venv\Scripts\python.exe"
COORDINATOR_PYTHON = r"C:\Users\taeyeon\PycharmProjects\heuristicrecognizer\LLM_assistant\venv\Scripts\python.exe"

# ── Session info ──────────────────────────────────────────────────────────────
participant = input("Participant ID (e.g. P01): ").strip()
experiment  = input("Experiment (simple/chemlab): ").strip()

processes = []

# 1. ByteTrack
processes.append(subprocess.Popen(
    [BYTETRACK_PYTHON, "bytetrack_server.py"],
    cwd=BYTETRACK_DIR,
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
))
print("✅ ByteTrack started")
time.sleep(1)

# 2. Adaptive LLM server (Type B)
processes.append(subprocess.Popen(
    [COORDINATOR_PYTHON, "-m", "uvicorn", "adaptive_server_B:app",
     "--host", "0.0.0.0", "--port", "8000"],
    cwd=COORDINATOR_DIR,
))
print("✅ Adaptive LLM server started (port 8000)")
time.sleep(3)

# 3. BiGRU inference listener
processes.append(subprocess.Popen(
    [COORDINATOR_PYTHON, "inference_listener.py"],
    cwd=COORDINATOR_DIR,
))
print("✅ BiGRU inference listener started (port 5007 → 5008)")
time.sleep(1)

# 4. Step tracker + voice bridge
processes.append(subprocess.Popen(
    [COORDINATOR_PYTHON, "run_udp_realtime.py",
     "--participant", participant,
     "--experiment", experiment],
    cwd=COORDINATOR_DIR,
))
print(f"✅ Step tracker B started (participant={participant} experiment={experiment})")

print("\nCtrl+C to stop all.\n")

try:
    for p in processes:
        p.wait()
except KeyboardInterrupt:
    print("\n🛑 Stopping...")
    for p in processes:
        p.terminate()
    print("✅ Done!")