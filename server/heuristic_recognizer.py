"""
heuristic_recognizer.py — Heuristic Action Recognizer
==========================================================
Turns raw grab/hold/release/poke events from Unity (MRTK) into simple
actions like pour, place, stir, press, look — using distance and tilt
rules, no ML.

Input CSV columns:
    Timestamp,Event,Hand,ObjectName,ObjX,ObjY,ObjZ,HandX,HandY,HandZ,
    TiltUp,TiltFwd,Curl,PinchDist,Dist,SceneObjects,UnityTime

How each action is detected:
    - pour : held object is a "pourable" and tilts past a threshold
    - stir : held object is a "stirrable", released near a container,
             and the hand moved in circles (checked via motion stats)
    - place: default when released — dropped near the nearest object
    - press: a poke event
    - look : gaze stayed on an object past a dwell threshold

It feeds the
recognizer directly from UDP in the run_udp_realtime.py.

"""

from __future__ import annotations
import os
import time
import threading
import math
from dataclasses import dataclass, field
from typing import Optional, Dict, List, Tuple, Callable, Any, Set
from datetime import datetime

try:
    from step_tracker import Action, StepTracker, OutcomeEvent
except ImportError:
    @dataclass
    class Action:
        action_type: str
        object_id: str
        target_id: Optional[str] = None
        timestamp: float = field(default_factory=time.time)
        unity_time: float = 0.0
        def __str__(self):
            if self.target_id:
                return f"{self.action_type}({self.object_id} → {self.target_id})"
            return f"{self.action_type}({self.object_id})"


# =============================================================================
# DATA STRUCTURES
# =============================================================================

@dataclass
class MRTKEvent:
    timestamp: datetime
    event_type: str
    hand: str
    object_name: str
    obj_position: Tuple[float, float, float]
    hand_position: Tuple[float, float, float]
    tilt_up_deg: float
    tilt_fwd_deg: float
    curl: float
    pinch_dist: float
    dist: float
    scene_objects: List[Dict[str, Any]] = field(default_factory=list)
    unity_time: float = 0.0

    @property
    def timestamp_float(self) -> float:
        return self.timestamp.timestamp()


@dataclass
class GrabSession:
    hand: str
    grabbed_object: str
    grab_time: datetime
    grab_position: Tuple[float, float, float]
    grab_tilt_up: float
    grab_tilt_fwd: float
    position_history: List[Tuple[float, float, float]] = field(default_factory=list)
    tilt_history: List[float] = field(default_factory=list)
    max_tilt_during_grab: float = 90.0
    scene_objects: List[Dict[str, Any]] = field(default_factory=list)
    pour_detected: bool = False


# =============================================================================
# UTILITY FUNCTIONS
# =============================================================================

def parse_timestamp(ts_str: str) -> datetime:
    ts_str = ts_str.rstrip('Z')
    if '.' in ts_str:
        date_part, frac_part = ts_str.rsplit('.', 1)
        frac_part = frac_part[:6]
        ts_str = f"{date_part}.{frac_part}"
    try:
        return datetime.fromisoformat(ts_str)
    except ValueError:
        date_part = ts_str.split('.')[0]
        return datetime.fromisoformat(date_part)


def parse_scene_objects(scene_str: str) -> List[Dict[str, Any]]:
    if not scene_str or scene_str.strip() == "":
        return []
    result = []
    for part in scene_str.split("|"):
        if ":" not in part:
            continue
        name, coords = part.split(":", 1)
        xyz = coords.split(";")
        if len(xyz) == 3:
            try:
                result.append({
                    "name": name.strip(),
                    "position": (float(xyz[0]), float(xyz[1]), float(xyz[2]))
                })
            except ValueError:
                continue
    return result


def find_nearest_object(
    source_pos: Tuple[float, float, float],
    candidates: List[Dict[str, Any]],
    filter_fn: Optional[Callable[[str], bool]] = None,
    max_distance: float = 0.35
) -> Optional[str]:
    best_name = None
    best_dist = float("inf")
    sx, sy, sz = source_pos
    for obj in candidates:
        name = obj.get("name")
        pos  = obj.get("position")
        if not name or pos is None:
            continue
        if filter_fn and not filter_fn(name):
            continue
        ox, oy, oz = pos
        d = math.sqrt((ox-sx)**2 + (oy-sy)**2 + (oz-sz)**2)
        if d < best_dist and d <= max_distance:
            best_dist = d
            best_name = name
    return best_name


def calculate_total_distance(positions: List[Tuple[float, float, float]]) -> float:
    if len(positions) < 2:
        return 0.0
    total = 0.0
    for i in range(1, len(positions)):
        p1, p2 = positions[i-1], positions[i]
        total += math.sqrt((p2[0]-p1[0])**2 + (p2[1]-p1[1])**2 + (p2[2]-p1[2])**2)
    return total


def calculate_movement_variance(positions: List[Tuple[float, float, float]]) -> float:
    if len(positions) < 2:
        return 0.0
    n  = len(positions)
    cx = sum(p[0] for p in positions) / n
    cy = sum(p[1] for p in positions) / n
    cz = sum(p[2] for p in positions) / n
    return sum((p[0]-cx)**2 + (p[1]-cy)**2 + (p[2]-cz)**2 for p in positions) / n


def calculate_straight_line_distance(positions: List[Tuple[float, float, float]]) -> float:
    if len(positions) < 2:
        return 0.0
    p1, p2 = positions[0], positions[-1]
    return math.sqrt((p2[0]-p1[0])**2 + (p2[1]-p1[1])**2 + (p2[2]-p1[2])**2)


def calculate_max_radius(positions: List[Tuple[float, float, float]]) -> float:
    if len(positions) < 2:
        return 0.0
    n  = len(positions)
    cx = sum(p[0] for p in positions) / n
    cy = sum(p[1] for p in positions) / n
    cz = sum(p[2] for p in positions) / n
    return max(math.sqrt((p[0]-cx)**2 + (p[1]-cy)**2 + (p[2]-cz)**2) for p in positions)


# =============================================================================
# HEURISTIC ACTION RECOGNIZER
# =============================================================================

class HeuristicActionRecognizer:

    def __init__(self, on_action_callback: Optional[Callable[[Action], None]] = None):
        self.on_action_callback = on_action_callback
        self._grab_sessions: Dict[str, GrabSession] = {}

        # Object categories
        self.pourables: Set[str] = {
            "bottle", "beaker", "flask", "graduated_cylinder",
            "erlenmeyer", "volumetric", "pitcher", "cup",
            "testtube", "test_tube",
            "waterbottle", "water_bottle",
            "dyebottle", "dye_bottle",
        }
        self.containers: Set[str] = {
            "cup", "beaker", "flask", "container", "bowl",
            "graduated_cylinder", "test_tube", "testtube",
            "petri_dish",
            # rack removed — only a place_target
        }
        self.stirrables: Set[str] = {
            "stirrod", "stir_rod", "rod", "spatula", "spoon",
            "glass_rod", "stirrer", "stick", "stirringrod",
        }
        self.pressables: Set[str] = {"tv", "button", "scale", "switch"}
        self.place_targets: Set[str] = {
            "keyboard", "scale", "hotplate", "table",
            "cup", "rack",
        }
        self.non_grabbable: Set[str] = {
            "rack",
        }

        # Thresholds
        self.pour_tilt_threshold  = 35.0
        self.stir_min_duration    = 0.5
        self.stir_min_movement    = 0.03
        self.stir_min_variance    = 0.001
        self.stir_min_path_ratio  = 1.8
        self.stir_max_radius      = 0.30

        self.action_cooldown_seconds = 0.75
        self._last_emitted: Dict[Tuple[str, str, str], float] = {}

    def _is_category(self, obj_name: str, category: Set[str]) -> bool:
        if not obj_name:
            return False
        obj_lower = obj_name.lower()
        return any(item in obj_lower or obj_lower in item for item in category)

    def _emit_with_cooldown(self, action: Action) -> Optional[Action]:
        key = (
            action.action_type,
            action.object_id.lower(),
            (action.target_id or "").lower()
        )
        last_t = self._last_emitted.get(key, -1e9)
        now_t  = action.unity_time if action.unity_time > 0 else action.timestamp
        if now_t - last_t < self.action_cooldown_seconds:
            return None
        self._last_emitted[key] = now_t
        return action

    def process_event(self, event: MRTKEvent) -> Optional[Action]:
        obj_lower = event.object_name.lower()

        # Block grab for non-grabbable objects
        if event.event_type == "grab" and any(ng in obj_lower for ng in self.non_grabbable):
            return None

        action = None

        if event.event_type == "grab":
            self._handle_grab(event)
        elif event.event_type == "hold":
            action = self._handle_hold(event)
        elif event.event_type == "release":
            action = self._handle_release(event)
        elif event.event_type == "poke":
            action = self._handle_poke(event)

        if action:
            action = self._emit_with_cooldown(action)
        if action and self.on_action_callback:
            self.on_action_callback(action)
        return action

    def _handle_poke(self, event: MRTKEvent) -> Optional[Action]:
        return Action("press", event.object_name, None,
                      event.timestamp_float, event.unity_time)

    def _handle_grab(self, event: MRTKEvent):
        self._grab_sessions[event.hand] = GrabSession(
            hand=event.hand,
            grabbed_object=event.object_name,
            grab_time=event.timestamp,
            grab_position=event.hand_position,
            grab_tilt_up=event.tilt_up_deg,
            grab_tilt_fwd=event.tilt_fwd_deg,
            position_history=[event.hand_position],
            tilt_history=[event.tilt_up_deg],
            max_tilt_during_grab=event.tilt_up_deg,
            scene_objects=event.scene_objects
        )

    def _handle_hold(self, event: MRTKEvent) -> Optional[Action]:
        session = self._grab_sessions.get(event.hand)
        if not session:
            return None

        session.position_history.append(event.hand_position)
        session.tilt_history.append(event.tilt_up_deg)
        session.max_tilt_during_grab = min(
            session.max_tilt_during_grab,
            event.tilt_up_deg
        )

        if event.scene_objects:
            session.scene_objects = event.scene_objects

        if not session.pour_detected and self._is_category(session.grabbed_object, self.pourables):
            if event.tilt_up_deg < self.pour_tilt_threshold:
                session.pour_detected = True

                target = None
                if session.scene_objects:
                    target = find_nearest_object(
                        event.hand_position, session.scene_objects,
                        filter_fn=lambda n: n != session.grabbed_object,
                        max_distance=0.35
                    )

                return Action("pour", session.grabbed_object, target,
                              event.timestamp_float, event.unity_time)

        return None

    def _handle_release(self, event: MRTKEvent) -> Optional[Action]:
        session = self._grab_sessions.pop(event.hand, None)
        if session is None:
            return None

        grabbed_obj = session.grabbed_object
        session.position_history.append(event.hand_position)

        scene = event.scene_objects if event.scene_objects else session.scene_objects

        nearest_container = find_nearest_object(
            event.hand_position, scene,
            filter_fn=lambda n: self._is_category(n, self.containers) and n != grabbed_obj,
            max_distance=0.35
        ) if scene else None

        nearest_any = find_nearest_object(
            event.hand_position, scene,
            filter_fn=lambda n: n != grabbed_obj,
            max_distance=0.35
        ) if scene else None

        duration         = (event.timestamp - session.grab_time).total_seconds()
        total_movement   = calculate_total_distance(session.position_history)
        movement_variance = calculate_movement_variance(session.position_history)
        straight_dist    = calculate_straight_line_distance(session.position_history)
        max_radius       = calculate_max_radius(session.position_history)
        path_ratio       = total_movement / straight_dist if straight_dist > 1e-6 else float("inf")

        # 1. Pour already detected during hold
        if session.pour_detected:
            return None

        # 2. Stir
        if self._is_category(grabbed_obj, self.stirrables) and nearest_container:
            print(f"[STIR DEBUG] obj={grabbed_obj} nearest_container={nearest_container}")
            print(f"  duration={duration:.2f}s  movement={total_movement:.4f}"
                  f"  variance={movement_variance:.6f}  path_ratio={path_ratio:.2f}"
                  f"  max_radius={max_radius:.4f}  history={len(session.position_history)}")
            if (duration         >= self.stir_min_duration  and
                total_movement   >= self.stir_min_movement   and
                movement_variance >= self.stir_min_variance  and
                path_ratio       >= self.stir_min_path_ratio):
                return Action("stir", grabbed_obj, nearest_container,
                              event.timestamp_float, event.unity_time)

        # 3. Place (default)
        return Action("place", grabbed_obj, nearest_any,
                      event.timestamp_float, event.unity_time)


# =============================================================================
# LOG WATCHER
# =============================================================================

class MRTKLogWatcher:

    def __init__(self, log_file: str, recognizer: HeuristicActionRecognizer,
                 tracker=None, poll_interval: float = 0.1,
                 on_raw_event=None, on_semantic_action=None):
        self.log_file         = log_file
        self.recognizer       = recognizer
        self.tracker          = tracker
        self.poll_interval    = poll_interval
        self.on_raw_event     = on_raw_event
        self.on_semantic_action = on_semantic_action
        self._running         = False
        self._thread          = None
        self._last_position   = 0
        self._header_parsed   = False
        self._column_indices  = {}
        self.events_processed = 0
        self.actions_detected = 0

    def start(self, blocking=False):
        self._running = True
        if blocking:
            self._watch_loop()
        else:
            self._thread = threading.Thread(target=self._watch_loop, daemon=True)
            self._thread.start()
            print(f"[MRTKLogWatcher] Watching: {self.log_file}")

    def stop(self):
        self._running = False
        if self._thread:
            self._thread.join(timeout=2.0)

    def _watch_loop(self):
        while self._running:
            try:
                if os.path.exists(self.log_file):
                    self._process_new_lines()
            except Exception as e:
                print(f"[MRTKLogWatcher] Error: {e}")
            time.sleep(self.poll_interval)

    def _process_new_lines(self):
        with open(self.log_file, 'r') as f:
            f.seek(self._last_position)
            lines = f.readlines()
            self._last_position = f.tell()
        for line in lines:
            line = line.strip()
            if not line:
                continue
            if not self._header_parsed:
                if "timestamp" in line.lower() and "event" in line.lower():
                    cols = [c.strip() for c in line.split(',')]
                    self._column_indices = {c: i for i, c in enumerate(cols)}
                    self._header_parsed  = True
                    continue
            event = self._parse_line(line)
            if event:
                self._process_event(event)

    def _parse_line(self, line: str) -> Optional[MRTKEvent]:
        try:
            values = [v.strip() for v in line.split(',')]

            def get(col, default=""):
                idx = self._column_indices.get(col)
                if idx is not None and idx < len(values):
                    return values[idx]
                return default

            return MRTKEvent(
                timestamp   = parse_timestamp(get("Timestamp")),
                event_type  = get("Event"),
                hand        = get("Hand"),
                object_name = get("ObjectName"),
                obj_position  = (float(get("ObjX","0")), float(get("ObjY","0")), float(get("ObjZ","0"))),
                hand_position = (float(get("HandX","0")), float(get("HandY","0")), float(get("HandZ","0"))),
                tilt_up_deg = float(get("TiltUp","90")),
                tilt_fwd_deg = float(get("TiltFwd","90")),
                curl        = float(get("Curl","0")),
                pinch_dist  = float(get("PinchDist","0")),
                dist        = float(get("Dist","0")),
                scene_objects = parse_scene_objects(get("SceneObjects","")),
                unity_time  = float(get("UnityTime","0")),
            )
        except Exception as e:
            print(f"[MRTKLogWatcher] Parse error: {e} | {line[:60]}...")
            return None

    def _process_event(self, event: MRTKEvent):
        self.events_processed += 1
        if self.on_raw_event:
            self.on_raw_event(event)
        action = self.recognizer.process_event(event)
        if action:
            self.actions_detected += 1
            tracker_result = None
            if self.tracker:
                tracker_result = self.tracker.process_action(action)
            if self.on_semantic_action:
                self.on_semantic_action(action, tracker_result)

    def process_file_once(self):
        self._last_position = 0
        self._header_parsed = False
        self.events_processed = 0
        self.actions_detected = 0
        if os.path.exists(self.log_file):
            self._process_new_lines()
        return self.events_processed, self.actions_detected


# =============================================================================
# GAZE LOG WATCHER
# =============================================================================

@dataclass
class GazeEvent:
    timestamp: datetime
    event_type: str
    object_name: str
    dwell_time_seconds: float
    obj_position: Tuple[float, float, float]

    @property
    def timestamp_float(self) -> float:
        return self.timestamp.timestamp()


class GazeLogWatcher:

    def __init__(self, log_file, look_threshold=2.0, poll_interval=0.1,
                 on_gaze_event=None, on_look_action=None):
        self.log_file         = log_file
        self.look_threshold   = look_threshold
        self.poll_interval    = poll_interval
        self.on_gaze_event    = on_gaze_event
        self.on_look_action   = on_look_action
        self._running         = False
        self._thread          = None
        self._last_position   = 0
        self._header_parsed   = False
        self._column_indices  = {}
        self.events_processed = 0
        self.look_actions_detected = 0

    def start(self, blocking=False):
        self._running = True
        if blocking:
            self._watch_loop()
        else:
            self._thread = threading.Thread(target=self._watch_loop, daemon=True)
            self._thread.start()

    def stop(self):
        self._running = False
        if self._thread:
            self._thread.join(timeout=2.0)

    def _watch_loop(self):
        while self._running:
            try:
                if os.path.exists(self.log_file):
                    self._process_new_lines()
            except Exception as e:
                print(f"[GazeLogWatcher] Error: {e}")
            time.sleep(self.poll_interval)

    def _process_new_lines(self):
        with open(self.log_file, 'r') as f:
            f.seek(self._last_position)
            lines = f.readlines()
            self._last_position = f.tell()
        for line in lines:
            line = line.strip()
            if not line:
                continue
            if not self._header_parsed:
                if "timestamp" in line.lower() and "objectname" in line.lower():
                    cols = [c.strip() for c in line.split(',')]
                    self._column_indices = {c: i for i, c in enumerate(cols)}
                    self._header_parsed  = True
                    continue
            event = self._parse_line(line)
            if event:
                self._process_event(event)

    def _parse_line(self, line):
        try:
            values = [v.strip() for v in line.split(',')]
            def get(col, default=""):
                idx = self._column_indices.get(col)
                if idx is not None and idx < len(values):
                    return values[idx]
                return default
            return GazeEvent(
                timestamp          = parse_timestamp(get("Timestamp")),
                event_type         = get("Event"),
                object_name        = get("ObjectName"),
                dwell_time_seconds = float(get("DwellTimeSeconds","0")),
                obj_position       = (float(get("ObjPosX","0")), float(get("ObjPosY","0")), float(get("ObjPosZ","0")))
            )
        except Exception:
            return None

    def _process_event(self, event):
        self.events_processed += 1
        if self.on_gaze_event:
            self.on_gaze_event(event)
        if event.event_type == "EXIT" and event.dwell_time_seconds >= self.look_threshold:
            action = Action("look", event.object_name, None, event.timestamp_float)
            self.look_actions_detected += 1
            if self.on_look_action:
                self.on_look_action(action)


if __name__ == "__main__":
    print("Heuristic Action Recognizer v2")
    print("CSV: Timestamp,Event,Hand,ObjectName,ObjX,ObjY,ObjZ,HandX,HandY,HandZ,TiltUp,TiltFwd,Curl,PinchDist,Dist,SceneObjects,UnityTime")
    print("Actions: pour, place, stir, look")