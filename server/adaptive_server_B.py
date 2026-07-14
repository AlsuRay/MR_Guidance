"""
adaptive_server_B.py — Adaptive LLM Server
======================================================
FastAPI server that generates the AR lab assistant's spoken guidance.
Takes procedural and user-state context as input, builds a prompt for Qwen2.5 7B (via Ollama),
and returns a short spoken answer plus which object to highlight.
Port: 8000.

Endpoints:
    POST /adaptive - main guidance endpoint. Classifies stable/struggling
                     from p_struggling (or uses user_state if already given),
                     decides whether to highlight an object, builds the
                     prompt, and returns {answer, highlight_object, user_state}
    POST /summary  - generates a short end-of-session summary from stats
    GET  /         - health check
    startup event  - warms up the model with a dummy request on boot

Key pieces:
    - build_prompt(): assembles the full LLM prompt from student state
      (current step, last outcome, sub-steps, repeat/error counts, question)
    - ask_llm(): calls Ollama, tries several ways to parse JSON out of the
      response (models don't always return clean JSON)
    - _strip_filler() / _strip_leaks(): deterministic cleanup applied to the
      answer text — removes chatty openers (proactive replies only) and any
      leaked "highlight: ..." field names, so TTS doesn't say them
    - needs_visual_help(): keyword check for whether the student seems lost
      and needs an object highlighted
"""

from fastapi import FastAPI
from pydantic import BaseModel
from typing import Optional
import requests
import json
import re

app = FastAPI()

OLLAMA_URL = "http://localhost:11434/api/generate"
MODEL_NAME = "qwen2.5:7b-instruct"

STRUGGLING_HIGH = 0.65

OUTCOME_EXPLANATIONS = {
    "EXPECTED":                "correct action at the right time",
    "ALTERNATIVE":             "valid but different approach",
    "PREMATURE":               "correct action but dependencies not met",
    "REPEAT":                  "action already completed",
    "HARMLESS":                "action not in protocol, no effect",
    "INCORRECT_RECOVERABLE":   "wrong action, but can continue",
    "INCORRECT_UNRECOVERABLE": "critical error, must restart",
}

SIMPLE_OBJECTS  = "bottle,cup,keyboard,mouse,spoon,tv"
CHEMLAB_OBJECTS = "beaker,stir_rod,water_bottle,dye_bottle,test_tube,rack"

SIMPLE_OBJECT_DICT = """Objects in the workspace:
- bottle: contains liquid to pour into the cup
- cup: receiving container for the liquid
- spoon: stirring tool
- keyboard: flat surface for placing the mouse
- mouse: small device to place on the keyboard
- tv: panel to press for experiment confirmation"""

CHEMLAB_OBJECT_DICT = """Objects in the workspace:
- water_bottle: contains clear diluent (water)
- dye_bottle: contains colored liquid (food coloring)
- beaker: mixing container for preparing the solution
- stir_rod: glass rod used for stirring
- test_tube: small receiving container for the final sample
- rack: holder that keeps the test tube upright"""

_FILLER_RE = re.compile(
    r'^\s*(great question|good question|sure thing|sure|absolutely|'
    r'of course|certainly|no problem|happy to help|got it|'
    r'alright)[!,.\s-]+',
    re.IGNORECASE
)


def _strip_filler(text: str) -> str:
    if not text:
        return text
    prev = None
    while text != prev:
        prev = text
        text = _FILLER_RE.sub("", text, count=1).lstrip()
    return text[0].upper() + text[1:] if text else text


_LEAK_RE = re.compile(r'(?im)^\s*highlight[^:\n]*:\s*')


def _strip_leaks(text: str) -> str:
    if not text:
        return text
    # Drop a leading "Highlight ...:" prefix wherever it appears, then tidy.
    text = _LEAK_RE.sub("", text)
    text = re.sub(r"\s*\n+\s*", " ", text).strip()   # collapse newlines to spaces
    text = re.sub(r"\s{2,}", " ", text)
    return text[0].upper() + text[1:] if text else text


def get_highlight_objects(protocol_name: str) -> str:
    name = protocol_name.lower()
    if "colorimetric" in name or "chem" in name or "sample" in name:
        return CHEMLAB_OBJECTS
    return SIMPLE_OBJECTS


def get_object_dict(protocol_name: str) -> str:
    name = protocol_name.lower()
    if "colorimetric" in name or "chem" in name or "sample" in name:
        return CHEMLAB_OBJECT_DICT
    return SIMPLE_OBJECT_DICT



class StudentState(BaseModel):
    question: str
    current_step: str = "unknown"
    p_struggling: float = 0.0
    user_state: str = ""
    last_action: str = ""
    last_outcome: str = ""
    time_on_step: float = 0.0
    repeat_count: int = 0
    error_count: int = 0
    available_next_steps: list[str] = []
    current_sub_steps: list[str] = []
    prev_question: str = ""
    prev_answer: str = ""
    protocol_name: str = "Lab Experiment"
    proactive: bool = False  # ← добавить


class AdaptiveAnswer(BaseModel):
    answer: str
    highlight_object: str = ""
    user_state: str


class SessionSummary(BaseModel):
    total_steps:        int
    completed_steps:    int
    total_actions:      int
    errors:             int
    voice_questions:    int
    proactive_triggers: int
    protocol_name:      str



def classify_state(p: float) -> str:
    return "struggling" if p >= STRUGGLING_HIGH else "stable"


def get_max_tokens(user_state: str, is_lost: bool = False) -> int:
    if user_state == "struggling": return 100
    if is_lost:                    return 100
    return 70


def get_step_status(last_action: str, last_outcome: str) -> str:
    if not last_outcome:
        return "READY TO EXECUTE"
    if last_outcome == "INCORRECT_UNRECOVERABLE":
        return "CRITICAL ERROR — MUST RESTART"
    if last_outcome in ("INCORRECT_RECOVERABLE", "PREMATURE"):
        return "IN PROGRESS — NEEDS CORRECTION"
    return "READY TO EXECUTE"


def needs_visual_help(question: str) -> bool:
    q = question.lower()
    triggers = [
        "where is", "where do i", "where should",
        "what is", "which one", "which object",
        "show me", "point to", "highlight",
        "i don't know", "dont know", "not sure",
        "what should i use", "which one should",
        "where's", "where are",
        "find", "look like", "help me find",
        "can you point", "show",
    ]
    return any(t in q for t in triggers)


def ask_llm(prompt: str, max_tokens: int = 120, strip_filler: bool = False) -> dict:
    def _ans(raw: str) -> str:
        raw = _strip_leaks(str(raw).strip())          # always: kill leaked field names
        return _strip_filler(raw) if strip_filler else raw

    payload = {
        "model": MODEL_NAME,
        "keep_alive": "30m",
        "prompt": prompt,
        "stream": False,
        "options": {
            "temperature": 0.3,
            "num_predict": max_tokens,
            "num_ctx": 1024,
            "repeat_penalty": 1.1,
            "num_gpu": 99,
        }
    }

    try:
        resp = requests.post(OLLAMA_URL, json=payload, timeout=20.0)
        if resp.status_code != 200:
            return {"answer": "Error contacting model", "highlight_object": ""}

        data     = resp.json()
        response = data.get("response", "").strip()
        print("[RAW MODEL OUTPUT]", response[:150])

        try:
            parsed = json.loads(response)
            return {
                "answer":           _ans(parsed.get("answer", "")),
                "highlight_object": str(parsed.get("highlight_object", "")).strip()
            }
        except:
            pass

        try:
            start = response.find("{")
            end   = response.rfind("}")
            if start != -1 and end != -1:
                parsed = json.loads(response[start:end + 1])
                return {
                    "answer":           _ans(parsed.get("answer", "")),
                    "highlight_object": str(parsed.get("highlight_object", "")).strip()
                }
        except:
            pass

        try:
            match = re.search(r"\{.*\}", response, re.DOTALL)
            if match:
                parsed = json.loads(match.group(0))
                return {
                    "answer":           _ans(parsed.get("answer", "")),
                    "highlight_object": str(parsed.get("highlight_object", "")).strip()
                }
        except:
            pass

        return {"answer": _ans(response), "highlight_object": ""}

    except Exception as e:
        print("[LLM ERROR]", e)
        return {"answer": "Error contacting model", "highlight_object": ""}


# =============================================================================
# PROMPT
# =============================================================================

def build_prompt(state: StudentState, user_state: str, use_highlight: bool, proactive: bool = False) -> str:
    step_status = get_step_status(state.last_action, state.last_outcome)

    runtime_lines = []
    if state.last_action and state.last_outcome:
        outcome_exp = OUTCOME_EXPLANATIONS.get(state.last_outcome, state.last_outcome)
        # Only show last action context if it was an error — otherwise LLM explains the completed step
        if state.last_outcome not in ("EXPECTED", "ALTERNATIVE", "HARMLESS"):
            runtime_lines.append(f"Last: {state.last_action} → {outcome_exp}")
    if state.repeat_count > 0:
        runtime_lines.append(f"Repeats: {state.repeat_count}")
    if state.error_count > 0:
        runtime_lines.append(f"Errors: {state.error_count}")
    runtime_block = (" | ".join(runtime_lines) + "\n") if runtime_lines else ""

    if state.prev_answer:
        history = (
            f"You already said: \"{state.prev_answer[:100]}\"\n"
            f"Do NOT repeat this. Try a different angle, sub-step, or object to focus on.\n"
        )
    else:
        history = ""

    hl_objects  = get_highlight_objects(state.protocol_name)
    object_dict = get_object_dict(state.protocol_name)

    if use_highlight:
        if user_state == "struggling":
            hl = (f'highlight_object MUST be one or more objects from: {hl_objects}. '
                  f'Always highlight when student is struggling.')
        else:
            hl = f'highlight relevant objects from: {hl_objects}, or "" if not applicable.'
    else:
        hl = '""'

    needs_detail = (
        user_state == "struggling"
        or state.error_count > 1
        or state.repeat_count > 1
        or needs_visual_help(state.question)
    )

    sub_block = ""
    sub_instruction = ""
    if state.current_sub_steps and needs_detail:
        steps = " / ".join(f"{i+1}. {s}" for i, s in enumerate(state.current_sub_steps[:4]))
        sub_block = f"Sub-steps available: {steps}\n"
        sub_instruction = " Use the sub-steps above to explain."

    is_lost = needs_visual_help(state.question)

    if user_state == "struggling":
        tone_instruction = (
            "You are a calm, supportive lab partner. Speak naturally and clearly. "
            "Be warm but not over-reassuring — avoid repeating 'don't worry' or 'you've got this'. "
            "Focus on giving concrete next action rather than emotional support."
        )
        task_rule = f"Give 1-2 clear, concrete suggestions.{sub_instruction}"
    else:
        tone_instruction = (
            "You are a helpful lab partner. Speak naturally and briefly, "
            "like a colleague giving a quick tip."
        )
        if is_lost:
            task_rule = f"Explain the full step clearly: what to pick up, where to move it, and how to place it.{sub_instruction}"
        else:
            task_rule = "Give a short, clear answer. Keep it brief."

    start_rule = ('Start your answer directly with the action or the fact — begin with a verb or '
                  'the object name (e.g. "Pick up...", "Pour...", "Look at...", "The beaker..."). '
                  'Do not open with pleasantries.') if proactive else ""

    if state.available_next_steps:
        alt_list = "; ".join(state.available_next_steps)
        steps_block = (f'The current step is the ONLY thing to do right now: "{state.current_step}".\n'
                       f'Other valid steps that may come later (do NOT bring them up unless asked): {alt_list}.\n')
    else:
        steps_block = (f'The current step is the ONLY thing to do right now: "{state.current_step}". '
                       f'It is the last remaining step.\n')
    scope_rule = ('Talk ONLY about the current step. Do NOT mention, preview, or say "next" about any '
                  'later step. Do NOT invent steps or objects that are not in the workspace list above.')

    return f"""You are a friendly AR lab assistant helping a student in real time. {tone_instruction}

Protocol: {state.protocol_name}
{object_dict}
Current step: "{state.current_step}" | Status: {step_status} | Student feeling: {user_state}
{steps_block}{runtime_block}{sub_block}{history}Student said: "{state.question}"

Respond naturally, like a real person would. Keep it brief and warm.
Choose exactly one case:
- Student asks if their action was correct ("is it ok", "did I do it right", "was that correct") → check last action "{state.last_action}" with outcome "{state.last_outcome}" and give direct feedback: yes/no + what to do next.
- Student explicitly asks to show or find a specific object ("show me X", "where is X", "find X") → name that EXACT object and give one sentence about it. Do NOT redirect to current step.
- Student asks what to do or how to proceed → {task_rule}
- Student asks about a lab object → one sentence: what it is + how it relates to the step.
- Student describes an unsafe action → one short warning + redirect.
- Question is off-topic → answer exactly: "Let's stay focused on the lab for now."

{scope_rule}
The word "highlight" is an internal field name — NEVER write it in the answer text. The answer must read as plain spoken guidance only.
highlight_object: {hl}
IMPORTANT: highlight_object must contain ONLY objects from the allowed list. Use "" for anything else. Put objects to highlight ONLY in the highlight_object JSON field, never in the answer text.
{start_rule}
Reply with JSON only: {{"answer":"...","highlight_object":"..."}}"""


# =============================================================================
# ENDPOINTS
# =============================================================================

@app.post("/adaptive", response_model=AdaptiveAnswer)
def adaptive_guidance(state: StudentState):
    if state.user_state in ("stable", "struggling"):
        user_state = state.user_state
    else:
        user_state = classify_state(state.p_struggling)

    use_highlight = (
        user_state == "struggling"
        or needs_visual_help(state.question)
        or state.error_count > 2
    )

    is_lost    = needs_visual_help(state.question)
    prompt = build_prompt(state, user_state, use_highlight, proactive=state.proactive)
    max_tokens = get_max_tokens(user_state, is_lost=is_lost)
    llm_output = ask_llm(prompt, max_tokens=max_tokens, strip_filler=state.proactive)

    highlight = llm_output.get("highlight_object", "")
    highlight = ",".join([h.strip().lower() for h in highlight.split(",") if h.strip()])

    print(
        f"[LLM] protocol='{state.protocol_name[:20]}' p={state.p_struggling:.2f} ({user_state}) "
        f"step='{state.current_step[:30]}' "
        f"→ {llm_output.get('answer', '')[:60]}..."
    )

    return AdaptiveAnswer(
        answer=llm_output.get("answer", ""),
        highlight_object=highlight,
        user_state=user_state,
    )


@app.post("/summary")
def session_summary(data: SessionSummary):
    prompt = f"""You are a friendly lab instructor. A student just completed a lab experiment.

Protocol: {data.protocol_name}
Results:
- Completed {data.completed_steps} out of {data.total_steps} steps
- Total actions performed: {data.total_actions}
- Errors made: {data.errors}
- Times asked for help: {data.voice_questions}
- Times the system helped proactively: {data.proactive_triggers}

Give a brief, warm, encouraging summary (2-3 sentences) of their performance.
Be specific — mention what went well. If errors occurred, mention it gently.
Reply with plain text only, no JSON, no formatting."""

    result = ask_llm(prompt, max_tokens=150)
    answer = result.get("answer", "Great job completing the experiment!")
    print(f"[Summary] {answer}")
    return {"summary": answer}


@app.get("/")
def root():
    return {"status": "Adaptive LLM server ready (TypeB)", "model": MODEL_NAME}


@app.on_event("startup")
async def warmup():
    print("🔥 Warming up LLM...")
    try:
        adaptive_guidance(StudentState(
            question="what should I do",
            current_step="place bottle next to cup",
            p_struggling=0.2,
            protocol_name="Liquid Transfer Experiment"
        ))
        print("✅ LLM ready!")
    except Exception as e:
        print(f"⚠️ Warmup failed: {e}")