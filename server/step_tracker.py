"""
step_tracker.py — Protocol-based Action Validation
====================================================
Checks user actions against a protocol (a list of steps) and classifies
each one as:
    EXPECTED                : right action at the right time
    ALTERNATIVE              : a valid different way to do the same step
    PREMATURE                : right action but too early (deps not done)
    REPEAT                   : step already completed
    HARMLESS                 : off-protocol but doesn't hurt anything
    INCORRECT_RECOVERABLE    : wrong action, can still continue
    INCORRECT_UNRECOVERABLE  : critical error — experiment halts and needs reset

Two tiers of logic:
    - Runtime tier (active): gates steps only on which actions were observed
      and an id-based dependency graph (DAG). This is what actually runs.
    - Object-state tier (inactive by default): richer prerequisites based on
      object states (e.g. "beaker must contain colorant"). Off by default
      because we can't reliably read real object states yet — flip
      USE_OBJECT_STATE_MACHINE to True once that's possible.

Also defines the two lab protocols used in the study
(LIQUID_TRANSFER_EXPERIMENT, COLORIMETRIC_SAMPLE_PREPARATION) and
get_protocol() to fetch one by name ('simple' | 'chemlab').
"""

from __future__ import annotations
import json
import time
from dataclasses import dataclass, field
from typing import Optional, Dict, List, Set, Tuple
from enum import Enum


# =============================================================================
# GLOBAL SWITCHES
# =============================================================================

# When False, the object-state prerequisites below are stored but NEVER used to
# gate availability -> the tracker relies purely on observed actions + the id
# dependency graph. Keep False for on-device runtime. Set True only if/when a
# reliable object-state source becomes available.
USE_OBJECT_STATE_MACHINE = False


# =============================================================================
# DATA STRUCTURES
# =============================================================================

class OutcomeType(Enum):
    EXPECTED = "expected"
    ALTERNATIVE = "alternative"
    PREMATURE = "premature"
    REPEAT = "repeat"
    HARMLESS = "harmless"
    INCORRECT_RECOVERABLE = "incorrect_recoverable"
    INCORRECT_UNRECOVERABLE = "incorrect_unrecoverable"


class Severity(Enum):
    NONE = "none"
    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"
    CRITICAL = "critical"


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

    def matches(self, other: 'Action', fuzzy: bool = True) -> bool:
        if self.action_type != other.action_type:
            return False
        if fuzzy:
            def obj_eq(a: str, b: str) -> bool:
                a, b = a.lower(), b.lower()
                return a in b or b in a

            obj_match = obj_eq(self.object_id, other.object_id)

            if other.target_id:
                target_match = self.target_id and obj_eq(self.target_id, other.target_id)
            else:
                target_match = True

            if obj_match and target_match:
                return True

            # Symmetric match for "place" — cup near bottle == bottle near cup
            if self.action_type == "place" and other.target_id and self.target_id:
                swapped_obj    = obj_eq(self.object_id, other.target_id)
                swapped_target = obj_eq(self.target_id, other.object_id)
                return swapped_obj and swapped_target

            return False
        else:
            return self.object_id == other.object_id and self.target_id == other.target_id


@dataclass
class ProtocolStep:
    id: int
    action: Action
    description: str = ""
    dependencies: List[int] = field(default_factory=list)
    alternatives: List[int] = field(default_factory=list)
    error_severity: Severity = Severity.MEDIUM
    is_optional: bool = False
    is_alternative: bool = False           # True => this step is the non-primary
                                           #         branch of an either/or choice
    sub_steps: List[str] = field(default_factory=list)

    # --- OBJECT-STATE tier (data only; ignored at runtime unless the global
    #     switch is on). Maps object_id -> required/resulting state label. ------
    required_object_states: Dict[str, str] = field(default_factory=dict)
    resulting_object_states: Dict[str, str] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, data: dict) -> 'ProtocolStep':
        action = Action(
            action_type=data.get("action", ""),
            object_id=data.get("object", ""),
            target_id=data.get("target")
        )
        severity_str = data.get("error_severity", "medium").lower()
        severity_map = {
            "none": Severity.NONE, "low": Severity.LOW,
            "medium": Severity.MEDIUM, "high": Severity.HIGH,
            "critical": Severity.CRITICAL
        }
        return cls(
            id=data.get("id", 0),
            action=action,
            description=data.get("description", ""),
            dependencies=data.get("dependencies", []),
            alternatives=data.get("alternatives", []),
            error_severity=severity_map.get(severity_str, Severity.MEDIUM),
            is_optional=data.get("is_optional", False),
            is_alternative=data.get("is_alternative", False),
            sub_steps=data.get("sub_steps", []),
            required_object_states=data.get("required_object_states", {}),
            resulting_object_states=data.get("resulting_object_states", {}),
        )


@dataclass
class OutcomeEvent:
    action: Action
    outcome: OutcomeType
    severity: Severity
    matched_step: Optional[ProtocolStep] = None
    message: str = ""
    timestamp: float = field(default_factory=time.time)

    def __str__(self):
        if self.matched_step:
            return f"{self.action} → {self.outcome.value} (Step {self.matched_step.id})"
        return f"{self.action} → {self.outcome.value}"


@dataclass
class Protocol:
    name: str
    description: str
    steps: List[ProtocolStep]
    error_rules: Dict[str, Severity] = field(default_factory=dict)
    dangerous_combos: List[tuple] = field(default_factory=list)

    @classmethod
    def from_dict(cls, data: dict) -> 'Protocol':
        steps = [ProtocolStep.from_dict(s) for s in data.get("steps", [])]
        error_rules = {}
        for rule in data.get("error_rules", []):
            action_type = rule.get("action", "")
            severity_str = rule.get("severity", "medium").lower()
            severity_map = {"low": Severity.LOW, "medium": Severity.MEDIUM,
                            "high": Severity.HIGH, "critical": Severity.CRITICAL}
            error_rules[action_type] = severity_map.get(severity_str, Severity.MEDIUM)

        dangerous_combos = []
        for combo in data.get("dangerous_combos", []):
            dangerous_combos.append((
                combo.get("action", ""),
                combo.get("object", ""),
                combo.get("target", "")
            ))

        return cls(
            name=data.get("name", "Unnamed"),
            description=data.get("description", ""),
            steps=steps,
            error_rules=error_rules,
            dangerous_combos=dangerous_combos
        )

    @classmethod
    def from_json(cls, json_str: str) -> 'Protocol':
        return cls.from_dict(json.loads(json_str))

    @classmethod
    def from_file(cls, filepath: str) -> 'Protocol':
        with open(filepath, 'r', encoding='utf-8') as f:
            return cls.from_dict(json.load(f))

    def get_steps_text(self) -> str:
        lines = []
        for s in self.steps:
            deps = f" (requires steps {s.dependencies})" if s.dependencies else ""
            tags = []
            if s.is_optional:
                tags.append("optional")
            if s.is_alternative:
                tags.append("alternative")
            tag_str = f" [{', '.join(tags)}]" if tags else ""
            lines.append(f"{s.id}. {s.description}{deps}{tag_str}")
        return "\n".join(lines)


# =============================================================================
# STEP TRACKER
# =============================================================================

class StepTracker:
    """Tracks progress through protocol and classifies user actions."""

    def __init__(self, protocol: Protocol, on_outcome: Optional[callable] = None):
        self.protocol = protocol
        self.on_outcome = on_outcome
        self.completed_steps: Set[int] = set()
        self.action_history: List[OutcomeEvent] = []
        self._step_by_id: Dict[int, ProtocolStep] = {s.id: s for s in protocol.steps}

        # Terminal state: set True after an unrecoverable action. Once aborted,
        # the run is halted and no further steps are accepted until reset().
        self.aborted: bool = False

        # OBJECT-STATE tier working memory (only consulted when the global switch
        # is on). Written on every completion, read only if enabled.
        self.object_states: Dict[str, str] = {}

    def reset(self):
        self.completed_steps.clear()
        self.action_history.clear()
        self.object_states.clear()
        self.aborted = False

    def is_aborted(self) -> bool:
        return self.aborted

    def process_action(self, action: Action) -> OutcomeEvent:
        # ---- Terminal short-circuit: experiment already halted --------------
        if self.aborted:
            halted = OutcomeEvent(
                action=action,
                outcome=OutcomeType.INCORRECT_UNRECOVERABLE,
                severity=Severity.CRITICAL,
                message="🛑 Experiment halted after a critical error. "
                        "Please reset before continuing."
            )
            if self.on_outcome:
                self.on_outcome(halted)
            return halted

        outcome = self._classify_action(action)

        # ---- Trip the terminal state on an unrecoverable outcome ------------
        if outcome.outcome == OutcomeType.INCORRECT_UNRECOVERABLE:
            self.aborted = True
            outcome.message += " 🛑 The experiment has been stopped and must be reset."

        self.action_history.append(outcome)
        if self.on_outcome:
            self.on_outcome(outcome)
        return outcome

    def _classify_action(self, action: Action) -> OutcomeEvent:
        matching_steps = self._find_matching_steps(action)

        if not matching_steps:
            return self._handle_unknown_action(action)

        for step in matching_steps:
            # Already done directly
            if step.id in self.completed_steps:
                return OutcomeEvent(
                    action=action,
                    outcome=OutcomeType.REPEAT,
                    severity=Severity.LOW,
                    matched_step=step,
                    message=f"Step {step.id} already completed"
                )

            # Goal already achieved through an interchangeable alternative:
            # doing the other branch now is redundant, not an error.
            if any(alt_id in self.completed_steps for alt_id in step.alternatives):
                return OutcomeEvent(
                    action=action,
                    outcome=OutcomeType.REPEAT,
                    severity=Severity.LOW,
                    matched_step=step,
                    message="Already accomplished via an alternative approach — no need to repeat."
                )

            missing_deps = self._get_missing_dependencies(step)
            if missing_deps:
                next_available = self.get_next_steps()
                if next_available:
                    available_hints = [s.description.lower() for s in next_available[:2]]
                    hint = f"First, {' or '.join(available_hints)}"
                else:
                    missing_descriptions = [
                        self._step_by_id[dep_id].description.lower()
                        for dep_id in missing_deps
                        if dep_id in self._step_by_id
                    ]
                    hint = f"First, {', then '.join(missing_descriptions)}" if missing_descriptions else "Complete previous steps first"

                return OutcomeEvent(
                    action=action,
                    outcome=OutcomeType.PREMATURE,
                    severity=Severity.MEDIUM,
                    matched_step=step,
                    message=f"⏰ Not yet! {hint}."
                )

            # ---- Step accepted --------------------------------------------
            self.completed_steps.add(step.id)
            # Record resulting object states (harmless when the switch is off).
            self.object_states.update(step.resulting_object_states)

            next_steps = self.get_next_steps()
            if next_steps:
                next_hints = [s.description.lower() for s in next_steps[:2]]
                next_hint = f" Next: {' or '.join(next_hints)}."
            elif self.is_complete():
                next_hint = " Protocol complete! 🎉"
            else:
                next_hint = ""

            if step.is_alternative:
                return OutcomeEvent(
                    action=action,
                    outcome=OutcomeType.ALTERNATIVE,
                    severity=Severity.NONE,
                    matched_step=step,
                    message=f"✓ Alternative approach accepted: {step.description}.{next_hint}"
                )

            return OutcomeEvent(
                action=action,
                outcome=OutcomeType.EXPECTED,
                severity=Severity.NONE,
                matched_step=step,
                message=f"✓ {step.description}.{next_hint}"
            )

        return self._handle_unknown_action(action)

    def _find_matching_steps(self, action: Action) -> List[ProtocolStep]:
        return [s for s in self.protocol.steps if action.matches(s.action, fuzzy=True)]

    def _get_missing_dependencies(self, step: ProtocolStep) -> List[int]:
        missing = []
        for dep_id in step.dependencies:
            if dep_id not in self.completed_steps:
                dep_step = self._step_by_id.get(dep_id)
                if dep_step:
                    # A dependency is satisfied if the dep itself OR any of its
                    # interchangeable alternatives has been completed.
                    alt_done = any(a in self.completed_steps for a in dep_step.alternatives)
                    if not alt_done:
                        missing.append(dep_id)
                else:
                    missing.append(dep_id)
        return missing

    # ------------------------------------------------------------------ #
    # OBJECT-STATE tier (disabled unless USE_OBJECT_STATE_MACHINE is True)
    # ------------------------------------------------------------------ #
    def _object_states_satisfied(self, step: ProtocolStep) -> bool:
        """
        Returns True if the world currently matches this step's required object
        states. NOTE: only consulted when USE_OBJECT_STATE_MACHINE is True. On
        device this is off, so physical object states are never relied upon.
        """
        if not USE_OBJECT_STATE_MACHINE:
            return True
        for obj, needed in step.required_object_states.items():
            if self.object_states.get(obj) != needed:
                return False
        return True

    def _handle_unknown_action(self, action: Action) -> OutcomeEvent:
        if action.action_type == "pour" and not action.target_id:
            next_steps = self.get_next_steps()
            hint = next_steps[0].description.lower() if next_steps else "follow the protocol"
            return OutcomeEvent(
                action=action,
                outcome=OutcomeType.INCORRECT_UNRECOVERABLE,
                severity=Severity.HIGH,
                message=f"⚠️ CAREFUL! Pouring without a container is unsafe. Try: {hint}."
            )

        for danger_action, danger_obj, danger_target in self.protocol.dangerous_combos:
            if (action.action_type == danger_action and
                danger_obj.lower() in action.object_id.lower() and
                action.target_id and danger_target.lower() in action.target_id.lower()):
                return OutcomeEvent(
                    action=action,
                    outcome=OutcomeType.INCORRECT_UNRECOVERABLE,
                    severity=Severity.CRITICAL,
                    message=f"🚨 STOP! {action.action_type}({action.object_id} → {action.target_id}) is dangerous!"
                )

        severity = self.protocol.error_rules.get(action.action_type, Severity.LOW)
        next_steps = self.get_next_steps()
        if next_steps:
            suggestions = [s.description.lower() for s in next_steps[:2]]
            hint_text = f"Try: {' or '.join(suggestions)}"
        else:
            hint_text = "Check what's next in the protocol"

        if severity == Severity.CRITICAL:
            return OutcomeEvent(action=action, outcome=OutcomeType.INCORRECT_UNRECOVERABLE,
                                severity=severity, message="❌ STOP! This action is dangerous.")
        elif severity == Severity.HIGH:
            return OutcomeEvent(action=action, outcome=OutcomeType.INCORRECT_UNRECOVERABLE,
                                severity=severity, message=f"⚠️ CAREFUL! This could be dangerous. {hint_text}.")
        elif severity == Severity.MEDIUM:
            return OutcomeEvent(action=action, outcome=OutcomeType.INCORRECT_RECOVERABLE,
                                severity=severity, message=f"⚠️ That's not quite right. {hint_text}.")
        else:
            return OutcomeEvent(action=action, outcome=OutcomeType.HARMLESS,
                                severity=Severity.NONE, message=f"No problem. {hint_text}.")

    def get_next_steps(self) -> List[ProtocolStep]:
        if self.aborted:
            return []

        def _alt_already_done(s: ProtocolStep) -> bool:
            # If an interchangeable alternative is already completed, this step's
            # goal is met — it must NOT be offered as a next step anymore.
            return any(alt_id in self.completed_steps for alt_id in s.alternatives)

        avail = [s for s in self.protocol.steps
                 if s.id not in self.completed_steps
                 and not _alt_already_done(s)
                 and not self._get_missing_dependencies(s)]
        # Object-state gating is a no-op while the global switch is off.
        if USE_OBJECT_STATE_MACHINE:
            avail = [s for s in avail if self._object_states_satisfied(s)]
        return avail

    def get_current_step(self) -> Optional[ProtocolStep]:
        """Returns the earliest uncompleted step in protocol order."""
        for step in self.protocol.steps:
            if step.id not in self.completed_steps:
                return step
        return None

    def get_progress(self) -> Tuple[int, int]:
        # Alternative variants are not counted as separate required work — their
        # goal is covered by the primary branch (or by the alternative itself).
        required = [s for s in self.protocol.steps
                    if not s.is_optional and not s.is_alternative]
        completed = len([s for s in required if s.id in self.completed_steps])
        return completed, len(required)

    def is_complete(self) -> bool:
        if self.aborted:
            return False
        for step in self.protocol.steps:
            if step.is_optional or step.is_alternative:
                continue
            if step.id not in self.completed_steps:
                if not any(a in self.completed_steps for a in step.alternatives):
                    return False
        return True

    def get_summary(self) -> str:
        completed, total = self.get_progress()
        lines = [f"Protocol: {self.protocol.name}", f"Progress: {completed}/{total}"]
        if self.aborted:
            lines.append("Status: 🛑 HALTED (critical error) — reset required.")
            return "\n".join(lines)
        next_steps = self.get_next_steps()
        if next_steps:
            lines.append("Next steps:")
            for s in next_steps[:3]:
                tag = " (optional)" if s.is_optional else ""
                lines.append(f"  {s.id}. {s.description}{tag}")
        elif self.is_complete():
            lines.append("Status: complete 🎉")
        return "\n".join(lines)

    def get_last_outcome(self) -> str:
        if not self.action_history:
            return ""
        return self.action_history[-1].outcome.value

    def get_last_action(self) -> str:
        if not self.action_history:
            return ""
        return str(self.action_history[-1].action)

    def get_last_repeat_count(self) -> int:
        count = 0
        for event in reversed(self.action_history):
            if event.outcome in (OutcomeType.REPEAT, OutcomeType.PREMATURE):
                count += 1
            else:
                break
        return count


# =============================================================================
# PROTOCOL DEFINITIONS
# =============================================================================

LIQUID_TRANSFER_EXPERIMENT = {
    "name": "Liquid Transfer Experiment",
    "description": "Basic liquid transfer and mixing procedure with parallel setup steps",
    "steps": [
        {
            "id": 1,
            "action": "place",
            "object": "Bottle",
            "target": "Cup",
            "description": "Place bottle next to cup",
            "dependencies": [],
            "error_severity": "low",
            "is_optional": False,
            "sub_steps": [
                "Pick up the bottle with one hand",
                "Move it next to the cup on the table",
                "Set it down so both objects are side by side"
            ]
        },
        {
            "id": 2,
            "action": "place",
            "object": "Mouse",
            "target": "Keyboard",
            "description": "Place mouse on keyboard",
            "dependencies": [],
            "error_severity": "low",
            "is_optional": False,
            "sub_steps": [
                "Pick up the mouse",
                "Place it flat on top of the keyboard"
            ]
        },
        {
            "id": 3,
            "action": "pour",
            "object": "Bottle",
            "target": "Cup",
            "description": "Pour liquid from bottle into cup",
            "dependencies": [1, 2],
            "error_severity": "high",
            "is_optional": False,
            "sub_steps": [
                "Pick up the bottle with your dominant hand",
                "Hold the cup steady with your other hand",
                "Tilt the bottle slowly over the cup opening",
                "Pour until the cup is about half full",
                "Set the bottle back down on the table"
            ]
        },
        {
            "id": 4,
            "action": "stir",
            "object": "Spoon",
            "target": "Cup",
            "description": "Stir the contents of the cup with spoon",
            "dependencies": [3],
            "error_severity": "medium",
            "is_optional": False,
            "sub_steps": [
                "Pick up the spoon",
                "Insert it into the cup",
                "Stir in a circular motion several times",
                "Remove the spoon and set it down"
            ]
        },
        {
            "id": 5,
            "action": "press",
            "object": "Tv",
            "description": "Press the TV to confirm completion",
            "dependencies": [4],
            "error_severity": "low",
            "is_optional": False,
            "sub_steps": [
                "Look at the TV panel",
                "Reach out and press it with your index finger"
            ]
        }
    ],
    "error_rules": [
        {"action": "pour",  "severity": "high"},
        {"action": "place", "severity": "low"},
        {"action": "stir",  "severity": "medium"},
        {"action": "press", "severity": "low"}
    ],
    "dangerous_combos": [
        {"action": "pour", "object": "bottle", "target": "keyboard"},
        {"action": "pour", "object": "bottle", "target": "tv"},
        {"action": "pour", "object": "bottle", "target": "spoon"},
    ]
}


# -----------------------------------------------------------------------------
# COLORIMETRIC SAMPLE PREPARATION
# -----------------------------------------------------------------------------
# Demonstrates, on the runtime (action-only) tier:
#   * a real EITHER/OR alternative  (step 2 primary  vs  step 21 alternative)
#   * an OPTIONAL step               (step 3 stir — recommended, not required)
#   * an unrecoverable -> HALT path  (dangerous_combos below)
#
# The `required_object_states` / `resulting_object_states` keys encode the
# richer AMMA-style prerequisites. They are DATA ONLY: with
# USE_OBJECT_STATE_MACHINE = False they are never used to gate anything, so the
# protocol runs purely on observed actions. Flip the switch to activate them.
#
# YOLO object classes (must match exactly): Beaker, DyeBottle, Rack, StirRod,
# TestTube, WaterBottle.  (Rack is ungrabbable -> never appears as a user step.)
# -----------------------------------------------------------------------------
COLORIMETRIC_SAMPLE_PREPARATION = {
    "name": "Colorimetric Sample Preparation and Transfer",
    "description": "Mock chemistry lab: prepare a colored solution and transfer it to a test tube",
    "steps": [
        {
            "id": 1,
            "action": "place",
            "object": "Beaker",
            "target": None,
            "description": "Position the mixing beaker in the preparation area",
            "dependencies": [],
            "error_severity": "low",
            "is_optional": False,
            "sub_steps": [
                "Pick up the beaker",
                "Place it on the flat surface in the preparation area in front of you",
                "Make sure it is stable and within reach"
            ],
            # OBJECT-STATE tier (ignored at runtime while the switch is off):
            "required_object_states": {},
            "resulting_object_states": {"Beaker": "positioned"}
        },

        # ---- Colorant introduction: EITHER step 2 OR step 21 ----------------
        # Both are valid, interchangeable ways to get the indicator into the
        # beaker. Do ONE. The primary (concentrated dye) reports EXPECTED; the
        # alternative (pre-diluted colored stock) reports ALTERNATIVE. Doing the
        # second after the first reports REPEAT.
        {
            "id": 2,
            "action": "pour",
            "object": "DyeBottle",
            "target": "Beaker",
            "description": "Introduce the concentrated color indicator into the beaker",
            "dependencies": [1],
            "alternatives": [21],
            "error_severity": "high",
            "is_optional": False,
            "is_alternative": False,          # primary branch
            "sub_steps": [
                "Pick up the dye bottle",
                "Hold it over the beaker",
                "Add a small amount of concentrated dye into the beaker",
                "Set the dye bottle back down"
            ],
            "required_object_states": {"Beaker": "positioned"},
            "resulting_object_states": {"Beaker": "has_colorant"}
        },
        {
            "id": 21,
            "action": "pour",
            "object": "WaterBottle",
            "target": "Beaker",
            "description": "Introduce pre-diluted colored stock into the beaker (alternative colorant source)",
            "dependencies": [1],
            "alternatives": [2],
            "error_severity": "high",
            "is_optional": False,
            "is_alternative": True,           # non-primary, interchangeable branch
            "sub_steps": [
                "Pick up the bottle of pre-diluted colored stock",
                "Hold it over the beaker",
                "Pour the pre-mixed colored solution into the beaker",
                "Set the bottle back down"
            ],
            "required_object_states": {"Beaker": "positioned"},
            "resulting_object_states": {"Beaker": "has_colorant"}
        },

        # ── Commented out for DEMO: homogenization via StirRod ────────────────
        # (no StirRod available in this demo set; it was optional anyway, and
        #  step 4 depends on step 2, not on this, so removing it changes nothing)
        # {
        #     "id": 3,
        #     "action": "stir",
        #     "object": "StirRod",
        #     "target": "Beaker",
        #     "description": "Homogenize the colored solution with the stir rod",
        #     "dependencies": [2],
        #     "error_severity": "medium",
        #     "is_optional": True,
        #     "sub_steps": [
        #         "Pick up the stir rod",
        #         "Insert it into the beaker",
        #         "Stir in a circular motion until the color is uniform",
        #         "Remove the stir rod"
        #     ],
        #     "required_object_states": {"Beaker": "has_colorant"},
        #     "resulting_object_states": {"Beaker": "homogenized"}
        # },
        {
            "id": 4,
            "action": "pour",
            "object": "Beaker",
            "target": "TestTube",
            "description": "Transfer the prepared sample into the receiving test tube",
            "dependencies": [2],              # needs colorant added; stir is optional
            "error_severity": "high",
            "is_optional": False,
            "sub_steps": [
                "Pick up the beaker carefully",
                "Pick up the test tube from the rack carefully",
                "Slowly pour the colored solution into the test tube",
                "Fill the test tube to about three quarters",
                "Set the beaker back down"
            ],
            "required_object_states": {"Beaker": "has_colorant"},
            "resulting_object_states": {"TestTube": "filled", "Beaker": "emptied"}
        }
    ],
    "error_rules": [
        {"action": "pour",  "severity": "high"},
        {"action": "place", "severity": "low"},
        {"action": "stir",  "severity": "medium"}
    ],
    # Any of these -> INCORRECT_UNRECOVERABLE -> the experiment HALTS.
    "dangerous_combos": [
        {"action": "pour", "object": "dye",    "target": "testtube"},
        {"action": "pour", "object": "water",  "target": "testtube"},
        {"action": "pour", "object": "beaker", "target": "rack"},
    ]
}


# =============================================================================
# ACTIVE PROTOCOL — change this to switch experiments
# =============================================================================

ACTIVE_PROTOCOL = LIQUID_TRANSFER_EXPERIMENT
# ACTIVE_PROTOCOL = COLORIMETRIC_SAMPLE_PREPARATION


def get_protocol(experiment_type: str) -> dict:
    """
    Returns protocol dict by experiment type string.
    Used by unified_coordinator to switch protocols at runtime.
    experiment_type: 'simple' | 'chemlab'
    """
    if experiment_type == "chemlab":
        return COLORIMETRIC_SAMPLE_PREPARATION
    return LIQUID_TRANSFER_EXPERIMENT


if __name__ == "__main__":
    import sys
    proto_key = sys.argv[1] if len(sys.argv) > 1 else "chemlab"
    protocol  = Protocol.from_dict(get_protocol(proto_key))
    tracker   = StepTracker(protocol)
    print(f"Protocol: {protocol.name}")
    print(f"Steps: {len(protocol.steps)}")
    print(f"\nAll steps:\n{protocol.get_steps_text()}")
    print("\nAvailable first steps:")
    for s in tracker.get_next_steps():
        print(f"  {s.id}. {s.description}")

    # --- Demo: alternative colorant path + abort-on-unrecoverable ----------
    if proto_key == "chemlab":
        def run(a):
            ev = tracker.process_action(a)
            print(f"  {a}  ->  {ev.outcome.value}: {ev.message}")

        print("\n--- Demo A: alternative colorant path ---")
        tracker.reset()
        run(Action("place", "Beaker"))
        run(Action("pour", "WaterBottle", "Beaker"))   # alternative -> ALTERNATIVE
        run(Action("pour", "DyeBottle", "Beaker"))     # other branch -> REPEAT
        run(Action("pour", "Beaker", "TestTube"))      # transfer -> EXPECTED
        print("  complete:", tracker.is_complete())

        print("\n--- Demo B: unrecoverable halts the run ---")
        tracker.reset()
        run(Action("place", "Beaker"))
        run(Action("pour", "DyeBottle", "Beaker"))     # EXPECTED
        run(Action("pour", "Beaker", "Rack"))          # dangerous -> UNRECOVERABLE -> HALT
        run(Action("pour", "Beaker", "TestTube"))      # blocked: experiment halted
        print("  aborted:", tracker.is_aborted())