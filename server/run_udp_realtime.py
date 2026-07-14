"""
 UDP Listener (Type B: LLM + BiGRU + voice bridge)
==============================================================================
The main session driver. Listens for MRTK/gaze events from Unity on UDP,
turns them into actions via heuristic_recognizer, checks them against the
protocol via step_tracker, and sends outcomes back to Unity. Also runs the
voice bridge (student questions -> adaptive_server -> spoken answer) and
proactive nudges when the BiGRU signal says the student is struggling.

Key pieces:
    - _user_state_listener(): background thread receiving p_struggling /
      ema_smoothed / user_state from inference_listener on UDP 5008
    - VoiceBridge: handles voice questions (UDP 5010->5011) and proactive
      nudges — triggers after PROACTIVE_DURATION_SECONDS of continuous
      struggling, gated by a cooldown and a "student not currently
      talking/listening" check. Tracks proactive_repeat so the LLM knows
      it's re-nudging on the same step.
    - Main loop: receives raw UDP lines (gaze ENTER/EXIT or MRTK grab/hold/
      release/poke), converts to actions, runs them through the tracker,
      and sends the outcome + any spoken message back to Unity.
    - Experiment-over handling: on INCORRECT_UNRECOVERABLE the run freezes
      (gentle panel + voice message, no summary) until a new protocol/
      EXPERIMENT_START resets it.

"""

import argparse
import re
import socket
import time
import json
import threading
import requests
from typing import Optional
from datetime import datetime

import step_tracker  # module handle so we can set USE_OBJECT_STATE_MACHINE
from heuristic_recognizer import (
    HeuristicActionRecognizer,
    MRTKEvent,
    parse_timestamp,
)
from step_tracker import (
    StepTracker,
    Protocol,
    OutcomeType,
    Action,
    get_protocol,
)
from session_logger import SessionLogger

# =============================================================================
# CONFIGURATION
# =============================================================================

UDP_RECEIVE_IP   = "0.0.0.0"
UDP_RECEIVE_PORT = 5005

UNITY_IP        = "192.168.1.146"
UNITY_SEND_PORT = 5006

USER_STATE_PORT    = 5008
VOICE_RECEIVE_PORT = 5010
VOICE_SEND_PORT    = 5011
LLM_SERVER_URL     = "http://localhost:8000/adaptive"
LLM_SUMMARY_URL    = "http://localhost:8000/summary"

LOOK_THRESHOLD = 2.0
IGNORE_LOOK    = False

PROACTIVE_DURATION_SECONDS    = 5.0
PROACTIVE_COOLDOWN_SECONDS    = 25.0
PROACTIVE_MIN_SILENCE_SECONDS = 8.0

# --- Object-state machine: single source of truth, pushed into step_tracker.
# False on device (physical object states not reliably observable). Can be
# overridden at launch with --object-states.
USE_OBJECT_STATE_MACHINE = False

# --- Wording shown when the experiment ends on an unrecoverable action.
EXPERIMENT_OVER_PANEL_TITLE = "Experiment over"
EXPERIMENT_OVER_PANEL_MSG   = ("This step can't be undone, so the experiment is over. "
                               "Ask your facilitator to reset it to try again.")
EXPERIMENT_OVER_VOICE_MSG   = ("That action can't be undone, so the experiment is over now. "
                               "Ask your facilitator to reset it if you'd like to try again.")

# --- Outcomes whose short message should also be SPOKEN (panel shows them too).
# HARMLESS is intentionally excluded (too frequent -> chatty). EXPECTED and
# INCORRECT_UNRECOVERABLE are handled separately below.
SPOKEN_OUTCOMES = {
    OutcomeType.PREMATURE,
    OutcomeType.REPEAT,
    OutcomeType.INCORRECT_RECOVERABLE,
    OutcomeType.ALTERNATIVE,
}

# Strip emoji / pictographs so the TTS voice doesn't try to read them.
_EMOJI_RE = re.compile(
    "[\U0001F000-\U0001FAFF"      # emoji / pictographs / symbols
    "\U00002190-\U000021FF"       # arrows
    "\U00002300-\U000023FF"       # misc technical (⏰ ⌛ etc.)
    "\U00002460-\U000024FF"       # enclosed alphanumerics
    "\U00002500-\U00002BFF"       # box drawing, geometric shapes, misc symbols (✓ ✔ ⚠ ❌ ⭐ ...)
    "\U00002600-\U000027BF"       # misc symbols + dingbats
    "\U0000FE00-\U0000FE0F"       # variation selectors
    "\U00002000-\U0000206F]+"     # general punctuation (incl. narrow no-break space after emoji)
)


def _clean_for_tts(text: str) -> str:
    # Drop emoji/pictographs, then collapse any doubled spaces they leave behind.
    cleaned = _EMOJI_RE.sub("", text or "")
    return re.sub(r"\s{2,}", " ", cleaned).strip()

# =============================================================================
# EXPERIMENT STATE
# =============================================================================

_experiment_started = False
_experiment_over    = False          # set True after an unrecoverable action
_experiment_lock    = threading.Lock()

_tracker_ref:  Optional[StepTracker] = None
_protocol_ref: Optional[Protocol]    = None
_tracker_lock  = threading.Lock()

_logger: Optional[SessionLogger] = None


def _set_protocol(experiment_type: str):
    global _tracker_ref, _protocol_ref, _experiment_over
    proto = Protocol.from_dict(get_protocol(experiment_type))
    with _tracker_lock:
        _tracker_ref  = StepTracker(proto)
        _protocol_ref = proto
    with _experiment_lock:
        _experiment_over = False     # fresh protocol clears any prior over-state
    print(f"[Coordinator B] 🔬 Protocol set: {proto.name}")


# =============================================================================
# p_struggling + user_state shared state
# =============================================================================

_p_struggling      = 0.0
_ema_smoothed      = 0.0
_user_state        = ""
_p_struggling_lock = threading.Lock()


def _user_state_listener():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("0.0.0.0", USER_STATE_PORT))
    sock.settimeout(1.0)
    print(f"[UserState] Listening on :{USER_STATE_PORT}")
    global _p_struggling, _ema_smoothed, _user_state
    while True:
        try:
            data, _ = sock.recvfrom(4096)
            msg   = json.loads(data.decode("utf-8"))
            raw   = float(msg.get("p_struggling", 0.0))
            ema   = float(msg.get("p_struggling_ema", raw))
            state = msg.get("user_state", "")
            with _p_struggling_lock:
                _p_struggling = raw
                _ema_smoothed = ema
                _user_state   = state
        except socket.timeout:
            continue
        except Exception as e:
            print(f"[UserState] Error: {e}")


# =============================================================================
# VOICE BRIDGE
# =============================================================================

REPEAT_KEYWORDS = ["repeat", "again", "say that again", "what did you say", "one more time"]


class VoiceBridge:
    def __init__(self, logger: SessionLogger):
        self._logger              = logger
        self._prev_question       = ""
        self._prev_answer         = ""
        self._user_busy           = False
        self._processing          = False
        self._last_answer_time    = -999.0
        self._struggling_since    = None
        self._last_proactive_time = -999.0
        self._last_proactive_step = ""      # current_step of the last proactive call
        self._proactive_repeat    = 0       # consecutive proactive calls on the SAME step

        self._recv_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._recv_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._recv_sock.bind(("0.0.0.0", VOICE_RECEIVE_PORT))
        self._recv_sock.settimeout(1.0)

        self._send_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

        threading.Thread(target=self._loop,           daemon=True).start()
        threading.Thread(target=self._proactive_loop, daemon=True).start()

        print(f"[VoiceBridge] Listening :{VOICE_RECEIVE_PORT} → sending :{VOICE_SEND_PORT}")

    def _loop(self):
        global _experiment_started
        while True:
            try:
                data, _ = self._recv_sock.recvfrom(65536)
                message = data.decode("utf-8").strip()
                if not message:
                    continue

                if message.startswith("EXPERIMENT_TYPE:"):
                    _set_protocol(message.split(":", 1)[1].strip().lower())
                    continue
                if message == "EXPERIMENT_START":
                    with _experiment_lock:
                        _experiment_started = True
                    print("[VoiceBridge] 🟢 EXPERIMENT_START")
                    continue
                if message == "LISTENING_START":
                    self._user_busy = True;  continue
                if message == "LISTENING_END":
                    self._user_busy = False; continue
                if message == "SPEAKING_START":
                    self._user_busy = True;  continue
                if message == "SPEAKING_END":
                    self._user_busy = False
                    self._last_answer_time = time.time()
                    continue

                with _experiment_lock:
                    if not _experiment_started:
                        continue
                    over = _experiment_over

                # Once the experiment is over, don't run the LLM — just say so.
                if over:
                    self._send_answer(EXPERIMENT_OVER_VOICE_MSG, "")
                    continue

                print(f"[VoiceBridge] Question: {message}")
                threading.Thread(target=self._handle, args=(message,), daemon=True).start()

            except socket.timeout:
                continue
            except Exception as e:
                print(f"[VoiceBridge] Error: {e}")

    def _proactive_loop(self):
        while True:
            time.sleep(1.0)
            try:
                with _experiment_lock:
                    if not _experiment_started or _experiment_over:
                        continue

                with _p_struggling_lock:
                    user_state = _user_state

                now = time.time()

                if user_state == "struggling":
                    if self._struggling_since is None:
                        self._struggling_since = now
                        print("[Proactive] struggling — timer started")

                    elapsed     = now - self._struggling_since
                    cooldown_ok = (now - self._last_proactive_time) >= PROACTIVE_COOLDOWN_SECONDS
                    silence_ok  = not self._user_busy and \
                                  not self._processing and \
                                  (now - self._last_answer_time) >= PROACTIVE_MIN_SILENCE_SECONDS

                    if elapsed >= PROACTIVE_DURATION_SECONDS and cooldown_ok and silence_ok:
                        print(f"[Proactive] Triggering — {elapsed:.1f}s struggling")
                        self._last_proactive_time = now
                        self._struggling_since    = None
                        threading.Thread(
                            target=self._handle,
                            args=("What should I do now?",),
                            kwargs={"proactive": True},
                            daemon=True
                        ).start()
                    elif self._user_busy or self._processing:
                        print("[Proactive] Skipped — user busy")
                    elif not silence_ok:
                        print(f"[Proactive] Skipped — recent answer {now - self._last_answer_time:.1f}s ago")
                else:
                    if self._struggling_since is not None:
                        print("[Proactive] stable — timer reset")
                    self._struggling_since = None

            except Exception as e:
                print(f"[Proactive] Error: {e}")

    def _handle(self, question: str, proactive: bool = False):
        # Guard: if the experiment ended between trigger and handling, bail out.
        with _experiment_lock:
            if _experiment_over:
                if not proactive:
                    self._send_answer(EXPERIMENT_OVER_VOICE_MSG, "")
                return

        if not proactive:
            self._processing = True
        try:
            if not proactive and any(kw in question.lower() for kw in REPEAT_KEYWORDS):
                self._send_answer(self._prev_answer or "I have nothing to repeat yet.", "")
                return

            with _tracker_lock:
                tracker  = _tracker_ref
                protocol = _protocol_ref

            if tracker is None or protocol is None:
                self._send_error("Experiment type not selected yet.")
                return

            current_steps    = tracker.get_next_steps()
            current_step_obj = current_steps[0] if current_steps else None
            current_step     = current_step_obj.description if current_step_obj else "unknown"

            # Count consecutive proactive nudges on the SAME step (0 for voice).
            proactive_repeat = 0
            if proactive:
                if current_step == self._last_proactive_step:
                    self._proactive_repeat += 1
                else:
                    self._proactive_repeat = 1
                    self._last_proactive_step = current_step
                proactive_repeat = self._proactive_repeat

            with _p_struggling_lock:
                p          = _p_struggling
                ema        = _ema_smoothed
                user_state = _user_state

            repeat_count  = tracker.get_last_repeat_count()
            use_sub_steps = (user_state == "struggling" or repeat_count >= 2)
            sub_steps     = (current_step_obj.sub_steps
                             if current_step_obj and use_sub_steps
                             and hasattr(current_step_obj, "sub_steps")
                             else [])

            payload = {
                "question":             "What should I do now?" if proactive else question,
                "current_step":         current_step,
                "p_struggling":         p,
                "user_state":           user_state,
                "last_action":          tracker.get_last_action()  or "",
                "last_outcome":         tracker.get_last_outcome() or "",
                "time_on_step":         0.0,
                "repeat_count":         repeat_count,
                "error_count":          0,
                "available_next_steps": [s.description for s in current_steps[1:]],
                "current_sub_steps":    sub_steps,
                "prev_question":        self._prev_question,
                "prev_answer":          self._prev_answer,
                "protocol_name":        protocol.name,
                "proactive": proactive,  # ← добавить
                "proactive_repeat": proactive_repeat,
            }

            tag = "[PROACTIVE]" if proactive else "[Voice]"
            print(f"{tag} → LLM  p={p:.3f}  step='{current_step[:40]}'")

            t_start = time.time()
            resp    = requests.post(LLM_SERVER_URL, json=payload, timeout=15.0)
            llm_ms  = (time.time() - t_start) * 1000
            print(f"[LLM LATENCY] {llm_ms:.0f}ms")
            resp.raise_for_status()
            result = resp.json()

            answer           = result.get("answer", "")
            highlight_object = result.get("highlight_object", "")

            self._logger.log_llm(
                trigger='proactive' if proactive else 'voice',
                question=question,
                current_step=current_step,
                user_state=user_state,
                p_struggling=p,
                repeat_count=repeat_count,
                error_count=0,
                answer=answer,
                highlight_object=highlight_object,
                llm_latency_ms=llm_ms,
            )

            if not proactive:
                self._prev_question = question
            self._prev_answer = answer

            self._send_answer(answer, highlight_object)
            self._last_answer_time = time.time()
            print(f"{tag} Answer: {answer[:60]}...")

        except requests.exceptions.Timeout:
            self._send_error("Sorry, the server took too long to respond.")
        except Exception as e:
            print(f"[VoiceBridge] Handle error: {e}")
            self._send_error("Sorry, something went wrong.")
        finally:
            if not proactive:
                self._processing = False

    def send_dangerous_action_alert(self, message: str):
        print(f"[VoiceBridge] ⚠️ Dangerous: {message}")
        self._send_answer(message, "")
        self._last_answer_time = time.time()

    def _send_answer(self, answer: str, highlight_object: str):
        out = json.dumps({"answer": answer, "highlight_object": highlight_object}).encode("utf-8")
        self._send_sock.sendto(out, (UNITY_IP, VOICE_SEND_PORT))

    def _send_error(self, message: str):
        self._send_answer(message, "")

    def close(self):
        self._recv_sock.close()
        self._send_sock.close()


# =============================================================================
# OUTCOME SENDER
# =============================================================================

class OutcomeSender:
    def __init__(self, unity_ip: str, unity_port: int):
        self.unity_ip   = unity_ip
        self.unity_port = unity_port
        self.socket     = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        print(f"📤 Outcome sender → {unity_ip}:{unity_port}")

    def send_outcome(self, t_unity, outcome, step_id, message,
                     action_type, object_id, target_id, progress, step_description=""):
        data = {
            "t_unity": t_unity, "outcome": outcome.value, "step_id": step_id or 0,
            "message": _clean_for_tts(message), "step_description": _clean_for_tts(step_description),
            "action_type": action_type, "object_id": object_id,
            "target_id": target_id or "", "progress_done": progress[0], "progress_total": progress[1],
        }
        self.socket.sendto(json.dumps(data).encode('utf-8'), (self.unity_ip, self.unity_port))

    def close(self): self.socket.close()


def get_current_step_description(tracker: StepTracker) -> str:
    ns = tracker.get_next_steps()
    return ns[0].description if ns else "Protocol complete! 🎉"


# =============================================================================
# EXPERIMENT-OVER PANEL
# =============================================================================

def _send_experiment_over(outcome_sender: "OutcomeSender",
                          tracker: StepTracker,
                          voice_bridge: VoiceBridge):
    """
    Freeze the run in the UI: show a gentle 'Experiment over' panel and speak a
    matching line. No celebratory summary is sent, because the run ended on an
    unrecoverable action.
    """
    progress = tracker.get_progress()
    outcome_sender.send_outcome(
        t_unity=0.0,
        outcome=OutcomeType.INCORRECT_UNRECOVERABLE,
        step_id=0,
        message=EXPERIMENT_OVER_PANEL_MSG,
        action_type="over", object_id="", target_id=None,
        progress=progress,
        step_description=EXPERIMENT_OVER_PANEL_TITLE,
    )
    voice_bridge._send_answer(EXPERIMENT_OVER_VOICE_MSG, "")
    print(f"[Coordinator B] 🔚 {EXPERIMENT_OVER_PANEL_TITLE} — panel + voice sent")


# =============================================================================
# SUMMARY
# =============================================================================

def _send_summary(tracker: StepTracker, voice_bridge: VoiceBridge):
    try:
        progress = tracker.get_progress()
        history  = tracker.action_history
        errors   = sum(1 for e in history
                       if e.outcome in (OutcomeType.INCORRECT_RECOVERABLE,
                                        OutcomeType.INCORRECT_UNRECOVERABLE))

        with _tracker_lock:
            protocol = _protocol_ref

        # count voice and proactive from logger
        voice_qs   = voice_bridge._logger._llm_count
        proactive  = 0  # tracked separately if needed

        payload = {
            "total_steps":        progress[1],
            "completed_steps":    progress[0],
            "total_actions":      len(history),
            "errors":             errors,
            "voice_questions":    voice_qs,
            "proactive_triggers": proactive,
            "protocol_name":      protocol.name if protocol else "Lab Experiment",
        }

        resp    = requests.post(LLM_SUMMARY_URL, json=payload, timeout=15.0)
        summary = resp.json().get("summary", "Well done completing the experiment!")
        voice_bridge._send_answer(summary, "")
        print(f"[Summary] Sent: {summary[:80]}...")
    except Exception as e:
        print(f"[Summary] Error: {e}")
        voice_bridge._send_answer("Well done! You've completed the experiment successfully.", "")


# =============================================================================
# PARSING
# =============================================================================

def parse_mrtk_line(line: str) -> MRTKEvent:
    p = [x.strip() for x in line.split(',')]
    from heuristic_recognizer import parse_scene_objects
    return MRTKEvent(
        timestamp=parse_timestamp(p[0]), event_type=p[1], hand=p[2], object_name=p[3],
        obj_position=(float(p[4]), float(p[5]), float(p[6])),
        hand_position=(float(p[7]), float(p[8]), float(p[9])),
        tilt_up_deg=float(p[10])  if len(p) > 10 else 90.0,
        tilt_fwd_deg=float(p[11]) if len(p) > 11 else 90.0,
        curl=float(p[12])         if len(p) > 12 else 0.0,
        pinch_dist=float(p[13])   if len(p) > 13 else 0.0,
        dist=float(p[14])         if len(p) > 14 else 0.0,
        scene_objects=parse_scene_objects(p[15] if len(p) > 15 else ""),
        unity_time=float(p[16])   if len(p) > 16 else 0.0
    )


def parse_gaze_line_to_action(line: str) -> Optional[Action]:
    p = [x.strip() for x in line.split(',')]
    if len(p) < 3: return None
    dwell      = float(p[3]) if len(p) > 3 and p[3] != "" else 0.0
    unity_time = float(p[7]) if len(p) > 7 and p[7] != "" else 0.0
    if p[1] == "EXIT" and dwell >= LOOK_THRESHOLD:
        return Action(action_type="look", object_id=p[2], target_id=None,
                      timestamp=parse_timestamp(p[0]).timestamp(), unity_time=unity_time)
    return None


# =============================================================================
# MAIN
# =============================================================================

def main():
    global _tracker_ref, _protocol_ref, _experiment_started, _experiment_over, _logger

    parser = argparse.ArgumentParser()
    parser.add_argument('--participant', type=str, default='P00')
    parser.add_argument('--experiment',  type=str, default='simple',
                        choices=['simple', 'chemlab'])
    parser.add_argument('--object-states', action='store_true',
                        help="Enable the object-state machine in step_tracker "
                             "(off by default; only use with a reliable state source).")
    args = parser.parse_args()

    # --- Push the object-state switch into step_tracker (single source of truth)
    effective_osm = USE_OBJECT_STATE_MACHINE or args.object_states
    step_tracker.USE_OBJECT_STATE_MACHINE = effective_osm

    _logger = SessionLogger(
        participant_id=args.participant,
        system_type='B',
        experiment_type=args.experiment,
        output_dir='./logs',
    )

    _set_protocol(args.experiment)

    recognizer     = HeuristicActionRecognizer()
    outcome_sender = OutcomeSender(UNITY_IP, UNITY_SEND_PORT)

    threading.Thread(target=_user_state_listener, daemon=True).start()
    voice_bridge = VoiceBridge(logger=_logger)

    print("=" * 70)
    print("UDP LISTENER — Type B (LLM + BiGRU)")
    print(f"   participant={args.participant}  experiment={args.experiment}")
    print(f"   object_state_machine={effective_osm}")
    print("⏸️  Waiting for EXPERIMENT_TYPE and EXPERIMENT_START from Unity")
    print("=" * 70)

    outcome_emoji = {
        OutcomeType.EXPECTED:                "✅",
        OutcomeType.ALTERNATIVE:             "🔄",
        OutcomeType.PREMATURE:               "⏰",
        OutcomeType.REPEAT:                  "🔁",
        OutcomeType.HARMLESS:                "💭",
        OutcomeType.INCORRECT_RECOVERABLE:   "⚠️",
        OutcomeType.INCORRECT_UNRECOVERABLE: "❌",
    }

    recent_events         = {}
    DUPLICATE_WINDOW      = 0.3
    sock                  = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((UDP_RECEIVE_IP, UDP_RECEIVE_PORT))
    protocol_completed    = False
    first_action_received = [False]

    def resend_first_step():
        while not first_action_received[0]:
            with _experiment_lock:
                started = _experiment_started
            if not started:
                time.sleep(0.5); continue
            with _tracker_lock:
                tracker = _tracker_ref
            if tracker is None:
                time.sleep(0.5); continue
            ns = tracker.get_next_steps()
            if not ns:
                time.sleep(0.5); continue
            first_step = ns[0]
            progress   = tracker.get_progress()
            outcome_sender.send_outcome(
                t_unity=0.0, outcome=OutcomeType.HARMLESS, step_id=first_step.id,
                message="", action_type="init", object_id="", target_id=None,
                progress=progress, step_description=first_step.description
            )
            print(f"📤 Initial step: {first_step.description}")
            time.sleep(2.0)

    threading.Thread(target=resend_first_step, daemon=True).start()

    try:
        while True:
            data, addr = sock.recvfrom(8192)
            t_received = time.time()
            line = data.decode("utf-8").strip()
            if not line:
                continue

            parts = [p.strip() for p in line.split(',')]
            if len(parts) < 2:
                continue

            with _experiment_lock:
                if not _experiment_started:
                    continue
                # Once over, ignore further actions until reset / new protocol.
                if _experiment_over:
                    continue

            with _tracker_lock:
                tracker = _tracker_ref

            if tracker is None:
                continue

            first_action_received[0] = True

            # ── 1) GAZE ───────────────────────────────────────────────────────
            if parts[1] in ("ENTER", "EXIT"):
                if IGNORE_LOOK or len(parts) < 3 or not parts[2].strip():
                    continue
                look_action = parse_gaze_line_to_action(line)
                if look_action and look_action.object_id:
                    outcome  = tracker.process_action(look_action)
                    step_id  = outcome.matched_step.id if outcome.matched_step else None
                    progress = tracker.get_progress()

                    with _p_struggling_lock:
                        p, ema, us = _p_struggling, _ema_smoothed, _user_state

                    _logger.log_action(
                        unity_time=look_action.unity_time,
                        action_type=look_action.action_type,
                        object_id=look_action.object_id,
                        target_id=look_action.target_id or '',
                        outcome=outcome.outcome.value,
                        step_id=step_id,
                        progress_done=progress[0],
                        progress_total=progress[1],
                        t_received=t_received,
                        p_struggling=p,
                        ema_smoothed=ema,
                        user_state=us,
                    )

                    if outcome.outcome == OutcomeType.HARMLESS:
                        continue

                    outcome_sender.send_outcome(
                        t_unity=look_action.unity_time, outcome=outcome.outcome,
                        step_id=step_id, message=outcome.message,
                        action_type=look_action.action_type, object_id=look_action.object_id,
                        target_id=look_action.target_id, progress=progress,
                        step_description=get_current_step_description(tracker)
                    )
                    print(f"👁️ LOOK: {look_action.object_id} → {outcome.outcome.value}")
                continue

            # ── 2) MRTK ───────────────────────────────────────────────────────
            try:
                event = parse_mrtk_line(line)
            except Exception as e:
                print(f"[UDP] Parse error: {e}"); continue

            if not event.object_name or not event.object_name.strip():
                continue

            event_key = (event.event_type, event.object_name)
            if event_key in recent_events and \
                    event.timestamp_float - recent_events[event_key] < DUPLICATE_WINDOW:
                continue
            recent_events[event_key] = event.timestamp_float

            if event.event_type in ["grab", "release", "poke"]:
                print(f"📍 {event.event_type}: {event.object_name} | tilt={event.tilt_up_deg:.1f}°")

            action = recognizer.process_event(event)
            if action:
                outcome  = tracker.process_action(action)
                step_id  = outcome.matched_step.id if outcome.matched_step else None
                progress = tracker.get_progress()

                with _p_struggling_lock:
                    p, ema, us = _p_struggling, _ema_smoothed, _user_state

                _logger.log_action(
                    unity_time=action.unity_time,
                    action_type=action.action_type,
                    object_id=action.object_id,
                    target_id=action.target_id or '',
                    outcome=outcome.outcome.value,
                    step_id=step_id,
                    progress_done=progress[0],
                    progress_total=progress[1],
                    t_received=t_received,
                    p_struggling=p,
                    ema_smoothed=ema,
                    user_state=us,
                )

                # Speak short messages for the soft outcomes (panel shows them too).
                if outcome.outcome in SPOKEN_OUTCOMES and outcome.message:
                    voice_bridge._send_answer(_clean_for_tts(outcome.message), "")

                # ── Unrecoverable → end the experiment (gentle panel) ─────────
                if outcome.outcome == OutcomeType.INCORRECT_UNRECOVERABLE:
                    print(f"  ❌ EXPERIMENT OVER: {outcome.message}")
                    with _experiment_lock:
                        already_over = _experiment_over
                        _experiment_over = True
                    if not already_over:
                        _send_experiment_over(outcome_sender, tracker, voice_bridge)
                    # No generic outcome send, no celebratory summary.
                    continue

                if outcome.outcome == OutcomeType.EXPECTED:
                    ns    = tracker.get_next_steps()
                    notif = f"Step complete! Next: {ns[0].description.lower()}." \
                            if ns else "All steps complete! Well done."
                    voice_bridge._send_answer(notif, "")

                if outcome.outcome == OutcomeType.HARMLESS:
                    continue

                outcome_sender.send_outcome(
                    t_unity=action.unity_time, outcome=outcome.outcome,
                    step_id=step_id, message=outcome.message,
                    action_type=action.action_type, object_id=action.object_id,
                    target_id=action.target_id, progress=progress,
                    step_description=get_current_step_description(tracker)
                )

                emoji      = outcome_emoji.get(outcome.outcome, "?")
                action_str = (f"{action.action_type.upper()}: {action.object_id} → {action.target_id}"
                              if action.target_id
                              else f"{action.action_type.upper()}: {action.object_id}")
                print(f"  {emoji} {action_str}")
                print(f"     └─ {outcome.outcome.value}: {outcome.message}")
                print(f"     └─ Progress: {progress[0]}/{progress[1]}")
                print(f"     └─ p={p:.3f} ema={ema:.3f} state={us}")

                if tracker.is_complete() and not protocol_completed:
                    protocol_completed = True
                    print("\n🎉 PROTOCOL COMPLETED!\n")
                    _logger.print_summary()
                    threading.Thread(
                        target=_send_summary,
                        args=(tracker, voice_bridge),
                        daemon=True
                    ).start()
                    print("\n[Press Ctrl+C to exit]")

    except KeyboardInterrupt:
        print("\n🛑 Interrupted")
        _logger.print_summary()
        outcome_sender.close()
        voice_bridge.close()
        print("✅ Done!")


if __name__ == "__main__":
    main()