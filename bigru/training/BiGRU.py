"""
BiGRU training (2-class: stable / struggling)
==================================================================
Loads pre-built event-centered windows (X_seq.npy, y.npy, groups.npy —
produced by data_processing/build_eventcentered_dataset.py) and trains a
BiGRU classifier.

Pipeline:
    - Session-based train/val/test split (80/10/10) — split by session id
      (groups), not by window, so no session leaks across splits
    - Class weights computed from the train split to handle imbalance
    - Model: 2-layer Bidirectional GRU (64→32 units) + dense head, softmax
      output (2 classes)
    - MacroF1Callback saves best_model.keras whenever val macro F1 improves
      (used instead of val_loss/restore_best_weights, since macro F1 is the
      metric that matters for imbalanced classes)
    - Final evaluation on held-out test sessions: macro F1, classification
      report, confusion matrix

Input shape: (120, 30) — 120 frames per window, 30 raw gaze/head/hand
channels.

Outputs: best_model.keras
"""

import os
import json
import numpy as np
import pandas as pd
from collections import Counter
from sklearn.metrics import f1_score, classification_report, confusion_matrix
import tensorflow as tf
from tensorflow.keras import layers, models, callbacks

# ==========================
# CONFIG
# ==========================

DATA_DIR    = r"D:\HoloAssist_eventcentered_all"
RESULTS_DIR = os.path.join(DATA_DIR, "bigru_results")
os.makedirs(RESULTS_DIR, exist_ok=True)

CLASS_NAMES = ['stable', 'struggling']
BATCH_SIZE  = 64
EPOCHS      = 50
DROPOUT     = 0.3
RANDOM_SEED = 42

# ==========================
# LOAD
# ==========================

X      = np.load(os.path.join(DATA_DIR, 'X_seq.npy'))
y      = np.load(os.path.join(DATA_DIR, 'y.npy'))
groups = np.load(os.path.join(DATA_DIR, 'groups.npy'))

print(f"X shape: {X.shape}")
print(f"y shape: {y.shape}")
print(f"Sessions: {len(np.unique(groups))}")
print(f"Class distribution: { {cn: int(np.sum(y==i)) for i,cn in enumerate(CLASS_NAMES)} }\n")

# ==========================
# SESSION-BASED SPLIT
# ==========================

unique_sessions = np.unique(groups)
np.random.seed(RANDOM_SEED)
np.random.shuffle(unique_sessions)

n = len(unique_sessions)
n_test = max(1, int(n * 0.10))
n_val  = max(1, int(n * 0.10))

test_sessions  = set(unique_sessions[:n_test])
val_sessions   = set(unique_sessions[n_test:n_test + n_val])
train_sessions = set(unique_sessions[n_test + n_val:])

train_mask = np.array([g in train_sessions for g in groups])
val_mask   = np.array([g in val_sessions   for g in groups])
test_mask  = np.array([g in test_sessions  for g in groups])

X_train, y_train = X[train_mask], y[train_mask]
X_val,   y_val   = X[val_mask],   y[val_mask]
X_test,  y_test  = X[test_mask],  y[test_mask]

print(f"Train: {len(X_train)} windows ({len(train_sessions)} sessions)")
print(f"Val:   {len(X_val)}   windows ({len(val_sessions)} sessions)")
print(f"Test:  {len(X_test)}  windows ({len(test_sessions)} sessions)\n")

# ==========================
# CLASS WEIGHTS
# ==========================

counts = Counter(y_train)
total  = len(y_train)
class_weight = {
    cls: total / (len(counts) * cnt)
    for cls, cnt in counts.items()
}
print(f"Class weights: { {CLASS_NAMES[k]: round(v,2) for k,v in class_weight.items()} }\n")

# ==========================
# MODEL
# ==========================

def build_bigru(input_shape, n_classes):
    inp = layers.Input(shape=input_shape)

    x = layers.Bidirectional(layers.GRU(64, return_sequences=True))(inp)
    x = layers.Dropout(DROPOUT)(x)

    x = layers.Bidirectional(layers.GRU(32, return_sequences=False))(x)
    x = layers.Dropout(DROPOUT)(x)

    x = layers.Dense(32, activation='relu')(x)
    x = layers.Dropout(DROPOUT)(x)

    out = layers.Dense(n_classes, activation='softmax')(x)

    model = models.Model(inp, out)
    return model

input_shape = (X_train.shape[1], X_train.shape[2])  # (120, 30)
model = build_bigru(input_shape, n_classes=2)
model.summary()

model.compile(
    optimizer=tf.keras.optimizers.Adam(learning_rate=1e-3),
    loss='sparse_categorical_crossentropy',
    metrics=['accuracy'],
)

# ==========================
# CALLBACKS
# ==========================

class MacroF1Callback(callbacks.Callback):
    def __init__(self, val_data):
        super().__init__()
        self.X_val, self.y_val = val_data
        self.best_f1    = 0.0
        self.best_epoch = 0

    def on_epoch_end(self, epoch, logs=None):
        preds = np.argmax(self.model.predict(self.X_val, verbose=0), axis=1)
        f1    = f1_score(self.y_val, preds, average='macro', zero_division=0)
        print(f"  val_macro_f1: {f1:.4f}", end="")
        if f1 > self.best_f1:
            self.best_f1    = f1
            self.best_epoch = epoch
            self.model.save(os.path.join(RESULTS_DIR, 'best_model.keras'))
            print(f"  ← saved", end="")
        print()

early_stop = callbacks.EarlyStopping(
    monitor='val_loss',
    patience=8,
    restore_best_weights=False,  # we use MacroF1Callback for best model
)

reduce_lr = callbacks.ReduceLROnPlateau(
    monitor='val_loss',
    factor=0.5,
    patience=4,
    min_lr=1e-5,
    verbose=1,
)

f1_callback = MacroF1Callback(val_data=(X_val, y_val))

# ==========================
# TRAIN
# ==========================

history = model.fit(
    X_train, y_train,
    validation_data=(X_val, y_val),
    epochs=EPOCHS,
    batch_size=BATCH_SIZE,
    class_weight=class_weight,
    callbacks=[f1_callback, early_stop, reduce_lr],
    verbose=1,
)

# ==========================
# EVALUATE ON TEST SET
# ==========================

print(f"\nBest val macro F1: {f1_callback.best_f1:.4f} (epoch {f1_callback.best_epoch + 1})")
print("\nLoading best model for test evaluation...")

best_model = models.load_model(os.path.join(RESULTS_DIR, 'best_model.keras'))
test_preds = np.argmax(best_model.predict(X_test, verbose=0), axis=1)

macro_f1 = f1_score(y_test, test_preds, average='macro', zero_division=0)
print(f"\nTest Macro F1: {macro_f1:.4f}")
print("\nClassification Report:")
print(classification_report(y_test, test_preds, target_names=CLASS_NAMES, zero_division=0))

cm = confusion_matrix(y_test, test_preds)
cm_df = pd.DataFrame(cm, index=CLASS_NAMES, columns=CLASS_NAMES)
print("Confusion Matrix (rows=true, cols=pred):")
print(cm_df)

# ==========================
# SAVE RESULTS
# ==========================

cm_df.to_csv(os.path.join(RESULTS_DIR, 'confusion_matrix.csv'))

results_summary = {
    'test_macro_f1': round(macro_f1, 4),
    'best_val_macro_f1': round(f1_callback.best_f1, 4),
    'best_epoch': f1_callback.best_epoch + 1,
    'per_class_f1': {
        cn: round(f1_score(y_test, test_preds, labels=[i], average='macro', zero_division=0), 4)
        for i, cn in enumerate(CLASS_NAMES)
    },
    'n_train': int(len(X_train)),
    'n_val':   int(len(X_val)),
    'n_test':  int(len(X_test)),
}
with open(os.path.join(RESULTS_DIR, 'results_summary.json'), 'w') as f:
    json.dump(results_summary, f, indent=2)

print(f"\nSaved to: {RESULTS_DIR}")
print("Done.")