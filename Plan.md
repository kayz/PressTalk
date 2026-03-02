# PressTalk Development Plan

## 0. Goal and Strategy

Target: Windows-only voice input product, MIT open source.

Timeline convention:

1. `Week N` means Nth week from project kickoff date `T0`.
2. Milestone completion is judged by listed deliverables and quality gates.
3. Canonical terms are defined in `docs/Terminology.md`.

Execution strategy:

1. Deliver an MVP first to prove feasibility and latency.
2. Keep architecture ready for IME integration.
3. Ship TSF-based real IME after MVP data validates stability.

## 1. MVP Definition (`v0.1.0-mvp`)

MVP scope:

1. Hold-to-talk, release-to-commit loop.
2. Local ASR for Chinese + English.
3. Rule-based cleanup for filler removal and normalization.
4. Microphone selection and persistent settings.
5. Local history storage in SQLite.
6. Works in mainstream apps including terminals via commit fallback path.

MVP non-goals:

1. TSF registration as system IME.
2. LLM-based advanced rewriting in default path.
3. Complex style templates and command grammar.

Definition of done for MVP:

1. All M3 deliverables are complete.
2. All quality gates in section 6 pass on the MVP compatibility matrix.
3. Known limitations are documented in release notes.

## 2. Module Breakdown

`PressTalk.Engine`

1. Session state machine (`idle -> recording -> recognizing -> commit`).
2. Hotkey hold/release handling.
3. Pipeline orchestration and timeout control.

`PressTalk.Audio`

1. WASAPI capture.
2. Near-field microphone preference strategy.
3. Capture gain boost and basic VAD controls.

`PressTalk.Asr`

1. `qwen-asr` runtime adapter.
2. Default model profile (`Qwen/Qwen3-ASR-1.7B`).
3. CPU fallback profile (`Qwen/Qwen3-ASR-0.6B`).
4. Warm-up and runtime metrics.

`PressTalk.Normalize`

1. Rule-based cleanup (filler words, repetition, punctuation).
2. Chinese/English normalization rules.
3. Optional semantic rewrite plugin (`Qwen/Qwen3-0.6B`, timeout-guarded).

`PressTalk.Commit`

1. Primary text commit path.
2. Compatibility fallback path for legacy controls/terminals.
3. App-level compatibility policy.

`PressTalk.Data`

1. SQLite schema and migrations.
2. Utterance history write/read/export/delete.
3. Privacy controls and retention config.

`PressTalk.App`

1. Tray + settings UI.
2. Hotkey, microphone, language, and model settings.
3. Health/status indicators and logs entry points.

`PressTalk.Tsf` (Post-MVP)

1. TSF TIP implementation.
2. IME registration and language bar integration.
3. IPC bridge to engine process.

## 3. Milestones

## M0 - Bootstrap (Week 1)

1. Repository structure, coding conventions, and CI baseline.
2. Logging/config system and error reporting hooks.
3. ASR runtime smoke test with local model loading.

Deliverables:

1. Buildable dev branch.
2. Initial docs and issue templates.

Implementation details:

1. See `docs/M0-Implementation.md`.

## M1 - Core Loop Prototype (Week 2)

1. Hold/release state machine complete.
2. Audio capture + streaming ASR complete.
3. Raw text commit in target apps.

Deliverables:

1. End-to-end demo in editor and browser.
2. First latency benchmark report.

Implementation details:

1. See `docs/M1-Implementation.md`.

## M2 - MVP Functional Complete (Week 3-4)

1. Rule cleanup pipeline v1 complete.
2. Qwen ASR cutover complete (`Qwen3-ASR-1.7B`, fallback `Qwen3-ASR-0.6B`).
3. Hold-time live caption preview complete (best effort).
4. Microphone selection and settings persistence complete.
5. SQLite history complete.
6. Compatibility fallback path complete.

Deliverables:

1. `v0.1.0-mvp-rc1` internal release.
2. App compatibility matrix v1 (editor/browser/PowerShell/SSH terminal).

## M3 - MVP Stabilization (Week 5)

1. Crash recovery and watchdog.
2. Hotkey conflict handling and device hot-plug resilience.
3. Performance tuning and startup/model warm-up optimization.

Deliverables:

1. `v0.1.0-mvp` tag.
2. Known issues and operational limits list.

## M4 - IME Mode (TSF) (Week 6-9)

1. TSF TIP implementation and registration flow.
2. IPC between TIP and speech engine.
3. TSF-first commit and fallback policy hardening.

Deliverables:

1. `v0.2.0-beta` with selectable PressTalk IME.
2. Regression checklist for legacy app scenarios.

## M5 - Intelligent Enhancement (Week 10-12)

1. Context router (app-aware style profile).
2. Optional tiny-LLM rewrite mode (default off).
3. Command interpreter for tasks like list/email/todo formatting.

Deliverables:

1. `v0.3.0-alpha` with optional AI rewrite features.
2. Quality report comparing rules-only vs rules+LLM.

## 4. Technology Decisions

1. Windows core integration: C++/ATL for TSF TIP (post-MVP).
2. Speech engine and settings app: C#/.NET 8.
3. ASR runtime target: `qwen-asr` + `Qwen/Qwen3-ASR-1.7B` local model.
4. Data: SQLite.
5. IPC: Named Pipes.
6. Observability: structured logs + performance counters.

## 5. Model and LLM Policy

1. ASR model download is required for local inference.
2. Default ASR model is fixed to `Qwen/Qwen3-ASR-1.7B`.
3. CPU-only fallback ASR model is fixed to `Qwen/Qwen3-ASR-0.6B`.
4. Semantic rewrite model is fixed to `Qwen/Qwen3-0.6B` and disabled by default.
5. Multi-model compatibility is post-MVP via backend interface.
6. Default path remains deterministic rules to protect latency and stability.

Model packaging policy:

1. Repository does not commit large model binaries.
2. Models are downloaded into `models/` or user-local cache by script/config.
3. Model checksum validation is mandatory before first use.

## 6. Quality Gates

1. P95 release-to-commit latency (short utterance): <= 800 ms in MVP targets.
2. Crash-free session rate: >= 99%.
3. Core scenario pass rate in compatibility matrix: >= 95%.
4. No data loss in normal shutdown and restart.

## 7. Risks and Mitigations

1. TSF complexity risk.
Mitigation: complete MVP first and keep TSF isolated in separate module/process.
2. Legacy app compatibility risk.
Mitigation: dual-path commit policy and explicit compatibility matrix.
3. Model performance variance risk.
Mitigation: quantized model profile, warm-up, and profiling-driven tuning.
4. Over-rewrite risk from small LLM.
Mitigation: rules-first pipeline, strict output schema, and timeout fallback.
