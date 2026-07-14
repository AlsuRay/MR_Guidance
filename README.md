# MR Guidance

Unity/HoloLens 2 client for a multimodal adaptive guidance system for procedural task assistance in mixed reality. Built as part of a master's thesis at Pusan National University, eXtended Reality Lab.

📄 **Full project page (architecture, evaluation, demo video, paper):**
[alsuray.github.io/AlsuRay](https://alsuray.github.io/AlsuRay/)

## What this is

This is an adaptive guidance system: real-time object detection (YOLOv10n via Unity Sentis), MRTK3-based hand/gaze interaction, semantic step tracking, a BiGRU-based user-state estimator, and LLM-based (Qwen2.5 7B) adaptive spoken guidance — all running in a closed loop between the HoloLens 2 and a Python backend over UDP.

The companion Python backend lives at: `server`

## Acknowledgments

Object detection pipeline adapted and extended from [dangberg/HoloLens-YOLO-Object-Detection](https://github.com/dangberg/HoloLens-YOLO-Object-Detection).
