# Changelog

## [0.1.0] - 2026-11-10

### Added

- Record XR sessions on the headset: head and controller poses, buttons, analog inputs, tracking state, screenshot checkpoints and notes (`.xrsc` format).
- Replay through virtual Input System devices (`ReplayHMD`, `ReplayController`) in the Editor or on the headset, frame-locked (deterministic game time) or real time.
- Screenshot comparison with diff images, and an HTML/JSON report with recording vs replay performance.
- Per-frame metrics (frame, CPU, GPU and XR runtime times, dropped frames, memory, GC, battery, temperature, thermal status, controller battery) in CSV, with a summary.
- In-headset metrics overlay with a frame-time graph, on the left wrist or following the head.
- Window > XR Session Capture: browse captures, pull them from the headset with adb, replay in the Editor or on the headset, open reports.
- Sample PlayMode regression test.
- Step-by-step guide in Spanish (`Documentation~/guia-paso-a-paso.md`): PC and headset setup, installation, recording, pulling and replaying a session.

[0.1.0]: https://github.com/cesared19/re-Play-vibe-session-recording/releases/tag/v0.1.0
