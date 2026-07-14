"""
HoloAssist Event-Centered Dataset Builder
=============================================================================
Builds a binary dataset (stable=0 vs struggling=1) from HoloAssist session
recordings + their annotation JSON, for training the BiGRU struggling
detector. Uses all tasks except known-bad ones. The model learns a task-agnostic behavioral
signal instead of memorizing per-task patterns.

Session matching:
    Each session folder name is matched to its annotations by `video_name`
    in the JSON. The task name is read from the last '-'-separated part of
    the folder name (e.g. "R123-abc-pour_water" -> task "pour_water");
    "partN" suffixes and known-bad tasks are skipped.

Label assignment (from the "Action Correctness" / "Conversation Purpose"
annotation fields):
    struggling (1) - "wrong action, corrected by instructor (verbally)",
                      "wrong action, not corrected", or an instructor
                      conversation that corrects a wrong action
    stable (0)     - "correct action" moments that are NOT within
                      ±CLEAN_RADIUS seconds of any wrong-action event
                      (of any kind), then undersampled per session to at
                      most MAX_STABLE_RATIO x (that session's struggling
                      count)
    dropped        - "wrong action, corrected by student" (the student
                      recovered on their own — ambiguous signal, so it's
                      used only to mark nearby time as "dirty" for the
                      stable filter, never labeled itself) and anything
                      else that doesn't match one of the categories above
                      (other annotation types, unrecognized correctness
                      values) — these fall through silently and aren't
                      included in the dataset

Windowing: struggling windows are cut ±DELTA_BEFORE/AFTER seconds around
the event span; stable windows are cut around the midpoint of a clean
"correct action" span. Windows shorter than MIN_FRAMES or with more than
MAX_ZERO_RATIO of missing hand-tracking data are dropped.

For each kept window, also computes 11 derived behavioral features (hand
velocity/acceleration/jerk, pause ratio, path efficiency, hover
proportion, head stability, gaze velocity stats).

Output (saved to SAVE_DIR): X_seq.npy (N, 120, 30) raw sequences,
X_feat.npy (N, 11) derived features, y.npy labels, groups.npy session ids
(for session-based train/val/test splitting), tasks.npy, and
feature_names.json.
"""

import os
import json
import numpy as np
import pandas as pd
from collections import Counter
from scipy.stats import entropy as scipy_entropy
from sklearn.preprocessing import StandardScaler

# ==========================
# CONFIG
# ==========================

BASE       = r"\\10.126.34.71\XRlab-Nas-2\000.테스트(임시)\700.개인\Alsu"
ANNOT_PATH = r"\\10.126.34.71\XRlab-Nas-2\000.테스트(임시)\700.개인\Alsu\data-annotation-trainval-v1_1.json"
SAVE_DIR   = r"D:\HoloAssist_eventcentered_all"

DELTA_BEFORE     = 2.0
DELTA_AFTER      = 2.0
CLEAN_RADIUS     = 5.0
TARGET_LEN       = 120   # ~4s at 30fps
MIN_FRAMES       = 30
MAX_ZERO_RATIO   = 0.25
MAX_STABLE_RATIO = 3

STABLE     = 0
STRUGGLING = 1

# Exclude only known bad/broken sessions
BAD_TASKS = {'bad', 'rashult_assemble_broken'}
# 'ram & graphicscard' has space — will be caught by split logic naturally

STRUGGLING_ACTION = {
    'wrong action, corrected by instructor verbally',
    'wrong action, not corrected',
}

STRUGGLING_CONV = {
    'instructor-start-conversation_correct the wrong action',
    'instructor-reply-to-student_correct the wrong action',
}

STABLE_ACTION = 'correct action'

ANY_WRONG = {
    'wrong action, corrected by instructor verbally',
    'wrong action, not corrected',
    'wrong action, corrected by student',
}

JOINT_PALM     = 0
JOINT_WRIST    = 1
JOINT_INDEXTIP = 10

def joint_pos_cols(joint_idx):
    base = 3 + joint_idx * 16
    return base + 3, base + 7, base + 11

# ==========================
# SESSION SELECTION
# ==========================

def get_task_name(session_name):
    parts = session_name.lower().split('-')
    last  = parts[-1]
    if last in ['part1', 'part2', 'part3']:
        return None
    if last in BAD_TASKS:
        return None
    return last

def select_sessions():
    all_dirs = [
        d for d in os.listdir(BASE)
        if os.path.isdir(os.path.join(BASE, d))
    ]
    selected = []
    for d in all_dirs:
        task = get_task_name(d)
        if task is not None:
            selected.append(d)

    print(f"Total sessions in BASE: {len(all_dirs)}")
    print(f"Selected sessions:      {len(selected)}")
    return selected

# ==========================
# ANNOTATIONS
# ==========================

def load_annotations(annot_path, selected_sessions):
    with open(annot_path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    session_set    = set(selected_sessions)
    session_events = {}
    for record in data:
        vname = record.get('video_name', '')
        if vname in session_set:
            session_events[vname] = record.get('events', [])

    print(f"Annotations found for {len(session_events)} sessions")
    return session_events

# ==========================
# EVENT EXTRACTION
# ==========================

def extract_events(events):
    struggling_intervals = []
    stable_candidates    = []
    all_bad_times        = []

    for ann in events:
        label_type = ann['label'].lower()
        attrs      = ann.get('attributes', {})
        t_start    = ann['start']
        t_end      = ann['end']

        if label_type == 'fine grained action':
            correctness = attrs.get('Action Correctness', '').lower()

            if correctness in STRUGGLING_ACTION:
                struggling_intervals.append((t_start, t_end))
                all_bad_times.append((t_start, t_end))

            elif correctness in ANY_WRONG:
                all_bad_times.append((t_start, t_end))

            elif correctness == STABLE_ACTION:
                stable_candidates.append((t_start, t_end))

        elif label_type == 'conversation':
            purpose = attrs.get('Conversation Purpose', '').lower()
            if purpose in STRUGGLING_CONV:
                struggling_intervals.append((t_start, t_end))
                all_bad_times.append((t_start, t_end))

    return struggling_intervals, stable_candidates, all_bad_times

def is_clean(t_center, all_bad_times, radius):
    for t_start, t_end in all_bad_times:
        if t_start - radius <= t_center <= t_end + radius:
            return False
    return True

# ==========================
# RAW DATA PARSING
# ==========================

def parse_eyes(session_path):
    path = os.path.join(session_path, 'Export_py', 'Eyes', 'Eyes_sync.txt')
    df = pd.read_csv(path, sep='\t', header=None,
                     names=['time','timestamp','origin_x','origin_y',
                            'gaze_dir_x','gaze_dir_y','gaze_dir_z','col7','valid'])
    df = df[df['valid'] == 1][['time','origin_x','origin_y',
                                'gaze_dir_x','gaze_dir_y','gaze_dir_z']]
    df['time'] = df['time'].round(3)
    return df

def parse_head(session_path):
    path = os.path.join(session_path, 'Export_py', 'Head', 'Head_sync.txt')
    df = pd.read_csv(path, sep='\t', header=None,
                     names=['time','timestamp','rot_x','rot_y','rot_z','rot_w',
                            'forward_x','forward_y','forward_z',
                            'pos_x','pos_y','pos_z','up_x','up_y','up_z',
                            'c15','c16','c17'])
    df = df[['time','rot_x','rot_y','rot_z','rot_w','pos_x','pos_y','pos_z']]
    df['time'] = df['time'].round(3)
    return df

def parse_hands(session_path, side='Left'):
    path = os.path.join(session_path, 'Export_py', 'Hands', f'{side}_sync.txt')
    df = pd.read_csv(path, sep='\t', header=None)
    df = df[df[2] == 1].copy()

    palm_x,  palm_y,  palm_z  = joint_pos_cols(JOINT_PALM)
    wrist_x, wrist_y, wrist_z = joint_pos_cols(JOINT_WRIST)
    index_x, index_y, index_z = joint_pos_cols(JOINT_INDEXTIP)

    return pd.DataFrame({
        'time':                    df[0].round(3).values,
        f'{side.lower()}_palm_x':  df[palm_x].values,
        f'{side.lower()}_palm_y':  df[palm_y].values,
        f'{side.lower()}_palm_z':  df[palm_z].values,
        f'{side.lower()}_wrist_x': df[wrist_x].values,
        f'{side.lower()}_wrist_y': df[wrist_y].values,
        f'{side.lower()}_wrist_z': df[wrist_z].values,
        f'{side.lower()}_index_x': df[index_x].values,
        f'{side.lower()}_index_y': df[index_y].values,
        f'{side.lower()}_index_z': df[index_z].values,
    })

HAND_FEATURE_COLS = [
    'left_palm_x',  'left_palm_y',  'left_palm_z',
    'left_wrist_x', 'left_wrist_y', 'left_wrist_z',
    'left_index_x', 'left_index_y', 'left_index_z',
    'right_palm_x',  'right_palm_y',  'right_palm_z',
    'right_wrist_x', 'right_wrist_y', 'right_wrist_z',
    'right_index_x', 'right_index_y', 'right_index_z',
]

RAW_FEATURE_COLS = [
    'origin_x', 'origin_y', 'gaze_dir_x', 'gaze_dir_y', 'gaze_dir_z',
    'rot_x', 'rot_y', 'rot_z', 'rot_w', 'pos_x', 'pos_y', 'pos_z',
] + HAND_FEATURE_COLS  # 30 total

def parse_session(session_path):
    eyes  = parse_eyes(session_path)
    head  = parse_head(session_path)
    left  = parse_hands(session_path, 'Left')
    right = parse_hands(session_path, 'Right')

    merged = eyes.merge(head,  on='time', how='inner')
    merged = merged.merge(left,  on='time', how='left')
    merged = merged.merge(right, on='time', how='left')

    hand_cols = [c for c in merged.columns if any(
        x in c for x in ['palm', 'wrist', 'index']
    )]
    merged[hand_cols] = merged[hand_cols].fillna(0.0)
    merged = merged.dropna().sort_values('time').reset_index(drop=True)
    return merged

# ==========================
# DERIVED FEATURES (no task_family)
# ==========================

def compute_velocity(pos, time):
    diff = np.diff(pos, axis=0)
    mag  = np.linalg.norm(diff, axis=1)
    dt   = np.diff(time) + 1e-8
    return mag / dt

def compute_derived_features(window_data):
    """
    11 task-agnostic behavioral features:
      hand: velocity_mean, velocity_std, acceleration_std, jerk_std
      behavior: pause_time_ratio, path_efficiency, hover_proportion
      head: head_stability
      gaze: gaze_velocity_entropy, gaze_velocity_mean, gaze_velocity_std
    """
    features = {}
    time = window_data['time'].values

    left_pos  = window_data[['left_palm_x',  'left_palm_y',  'left_palm_z']].values
    right_pos = window_data[['right_palm_x', 'right_palm_y', 'right_palm_z']].values

    lv = compute_velocity(left_pos,  time)
    rv = compute_velocity(right_pos, time)
    hand_vel = (lv + rv) / 2.0

    features['hand_velocity_mean'] = float(np.mean(hand_vel))
    features['hand_velocity_std']  = float(np.std(hand_vel))

    la = np.diff(lv)
    ra = np.diff(rv)
    hand_acc  = (np.abs(la) + np.abs(ra)) / 2.0 if len(la) > 0 else np.array([0.0])
    features['hand_acceleration_std'] = float(np.std(hand_acc))

    lj = np.diff(la) if len(la) > 1 else np.array([0.0])
    rj = np.diff(ra) if len(ra) > 1 else np.array([0.0])
    hand_jerk = (np.abs(lj) + np.abs(rj)) / 2.0
    features['hand_jerk_std'] = float(np.std(hand_jerk))

    mean_vel        = float(np.mean(hand_vel))
    pause_threshold = 0.1 * mean_vel if mean_vel > 0 else 1e-4
    pause_mask      = hand_vel < pause_threshold
    features['pause_time_ratio'] = float(np.mean(pause_mask))

    def path_efficiency(pos):
        actual   = np.sum(np.linalg.norm(np.diff(pos, axis=0), axis=1))
        straight = np.linalg.norm(pos[-1] - pos[0]) if len(pos) > 1 else 0.0
        return straight / (actual + 1e-8)

    features['path_efficiency'] = float(
        (path_efficiency(left_pos) + path_efficiency(right_pos)) / 2.0
    )

    left_present  = np.any(left_pos  != 0, axis=1)
    right_present = np.any(right_pos != 0, axis=1)
    hand_present  = left_present | right_present
    hover_mask    = pause_mask & hand_present[1:]
    features['hover_proportion'] = float(np.mean(hover_mask)) if len(hover_mask) > 0 else 0.0

    head_pos = window_data[['pos_x', 'pos_y', 'pos_z']].values
    features['head_stability'] = float(np.std(head_pos, axis=0).mean())

    gaze_dir = window_data[['gaze_dir_x', 'gaze_dir_y', 'gaze_dir_z']].values
    gaze_vel = compute_velocity(gaze_dir, time)

    hist, _ = np.histogram(gaze_vel, bins=20, density=True)
    hist    = hist + 1e-8
    features['gaze_velocity_entropy'] = float(scipy_entropy(hist))
    features['gaze_velocity_mean']    = float(np.mean(gaze_vel))
    features['gaze_velocity_std']     = float(np.std(gaze_vel))

    return np.array(list(features.values()), dtype=np.float32), list(features.keys())

# ==========================
# WINDOW EXTRACTION
# ==========================

def extract_window(merged_raw, merged_scaled, t_start, t_end):
    mask          = (merged_raw['time'] >= t_start) & (merged_raw['time'] <= t_end)
    window_raw    = merged_raw[mask]
    window_scaled = merged_scaled[mask]
    return window_raw, window_scaled

# ==========================
# DATASET ASSEMBLY
# ==========================

def build_dataset(selected_sessions, session_events):
    os.makedirs(SAVE_DIR, exist_ok=True)

    all_windows          = []
    skipped              = 0
    filtered_zeros_total = 0
    struggling_total     = 0
    struggling_kept      = 0
    struggling_filtered  = 0
    struggling_short     = 0
    feature_names        = None

    np.random.seed(42)

    for i, session_name in enumerate(selected_sessions):
        if session_name not in session_events:
            skipped += 1
            continue

        session_path = os.path.join(BASE, session_name)

        try:
            merged = parse_session(session_path)
        except Exception as e:
            print(f"  [SKIP] {session_name}: {e}")
            skipped += 1
            continue

        if len(merged) < MIN_FRAMES:
            skipped += 1
            continue

        merged_raw    = merged.copy()
        merged_scaled = merged.copy()
        scaler        = StandardScaler()
        merged_scaled[RAW_FEATURE_COLS] = scaler.fit_transform(
            merged_scaled[RAW_FEATURE_COLS]
        )

        events = session_events[session_name]
        struggling_intervals, stable_candidates, all_bad_times = extract_events(events)

        session_struggling = []
        stable_pool        = []

        # --- STRUGGLING windows ---
        for (t_start, t_end) in struggling_intervals:
            struggling_total += 1
            w_start = t_start - DELTA_BEFORE
            w_end   = t_end   + DELTA_AFTER

            window_raw, window_scaled = extract_window(merged_raw, merged_scaled, w_start, w_end)

            if len(window_raw) < MIN_FRAMES:
                struggling_short += 1
                continue

            hand_data  = window_raw[HAND_FEATURE_COLS].values
            zero_ratio = (hand_data == 0).mean()
            if zero_ratio > MAX_ZERO_RATIO:
                filtered_zeros_total += 1
                struggling_filtered  += 1
                continue

            derived, f_names = compute_derived_features(window_raw)
            if feature_names is None:
                feature_names = f_names

            session_struggling.append({
                'session': session_name,
                'task':    get_task_name(session_name),
                'raw':     window_scaled[RAW_FEATURE_COLS].values.astype(np.float32),
                'derived': derived,
                'label':   STRUGGLING,
            })
            struggling_kept += 1

        # --- STABLE windows ---
        for (t_start, t_end) in stable_candidates:
            t_center = (t_start + t_end) / 2.0

            if not is_clean(t_center, all_bad_times, CLEAN_RADIUS):
                continue

            w_start = t_center - DELTA_BEFORE
            w_end   = t_center + DELTA_AFTER

            window_raw, window_scaled = extract_window(merged_raw, merged_scaled, w_start, w_end)

            if len(window_raw) < MIN_FRAMES:
                continue

            hand_data  = window_raw[HAND_FEATURE_COLS].values
            zero_ratio = (hand_data == 0).mean()
            if zero_ratio > MAX_ZERO_RATIO:
                filtered_zeros_total += 1
                continue

            derived, f_names = compute_derived_features(window_raw)
            if feature_names is None:
                feature_names = f_names

            stable_pool.append({
                'session': session_name,
                'task':    get_task_name(session_name),
                'raw':     window_scaled[RAW_FEATURE_COLS].values.astype(np.float32),
                'derived': derived,
                'label':   STABLE,
            })

        # undersample stable per session
        max_stable = max(1, len(session_struggling) * MAX_STABLE_RATIO)
        if len(stable_pool) > max_stable:
            idx         = np.random.choice(len(stable_pool), max_stable, replace=False)
            stable_pool = [stable_pool[j] for j in idx]

        all_windows.extend(session_struggling)
        all_windows.extend(stable_pool)

        if (i + 1) % 50 == 0:
            print(f"  {i+1}/{len(selected_sessions)} sessions processed...")

    print(f"\nSkipped sessions:          {skipped}")
    print(f"Filtered (>{MAX_ZERO_RATIO*100:.0f}% zeros): {filtered_zeros_total}")
    print(f"\nStruggling diagnostics:")
    print(f"  total events:     {struggling_total}")
    print(f"  too short:        {struggling_short}")
    print(f"  filtered (zeros): {struggling_filtered}")
    print(f"  kept:             {struggling_kept}")
    print(f"\nTotal windows kept: {len(all_windows)}")

    if not all_windows:
        print("ERROR: No windows collected!")
        return

    counts = Counter(w['label'] for w in all_windows)
    total  = len(all_windows)
    print(f"\nClass distribution:")
    print(f"  stable(0):     {counts[0]} ({counts[0]/total*100:.1f}%)")
    print(f"  struggling(1): {counts[1]} ({counts[1]/total*100:.1f}%)")

    # per-task counts
    task_counts = Counter(w['task'] for w in all_windows if w['label'] == STRUGGLING)
    print(f"\nStruggling windows per task:")
    for task, cnt in task_counts.most_common():
        print(f"  {task:<30} {cnt}")

    N = len(all_windows)

    X_seq = np.zeros((N, TARGET_LEN, len(RAW_FEATURE_COLS)), dtype=np.float32)
    for idx, w in enumerate(all_windows):
        n = min(len(w['raw']), TARGET_LEN)
        X_seq[idx, :n, :] = w['raw'][:n]

    X_feat  = np.stack([w['derived']  for w in all_windows])
    y       = np.array([w['label']    for w in all_windows], dtype=np.int64)
    groups  = np.array([w['session']  for w in all_windows])
    tasks   = np.array([w['task']     for w in all_windows])

    print(f"\nX_seq shape:   {X_seq.shape}")
    print(f"X_feat shape:  {X_feat.shape}")
    print(f"y shape:       {y.shape}")
    print(f"Features:      {feature_names}")

    np.save(os.path.join(SAVE_DIR, 'X_seq.npy'),  X_seq)
    np.save(os.path.join(SAVE_DIR, 'X_feat.npy'), X_feat)
    np.save(os.path.join(SAVE_DIR, 'y.npy'),      y)
    np.save(os.path.join(SAVE_DIR, 'groups.npy'), groups)
    np.save(os.path.join(SAVE_DIR, 'tasks.npy'),  tasks)

    with open(os.path.join(SAVE_DIR, 'feature_names.json'), 'w') as f:
        json.dump(feature_names, f, indent=2)

    print(f"\nSaved to: {SAVE_DIR}")


# ==========================
# MAIN
# ==========================

if __name__ == '__main__':
    print("=== Step 1: Selecting sessions ===")
    selected = select_sessions()

    print("\n=== Step 2: Loading annotations ===")
    session_events = load_annotations(ANNOT_PATH, selected)

    print("\n=== Step 3: Building dataset ===")
    build_dataset(selected, session_events)