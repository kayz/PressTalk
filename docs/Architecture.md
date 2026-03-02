# PressTalk Architecture (MVP-First)

## Intent

Define module boundaries to keep MVP delivery fast and TSF migration low risk.

## Runtime Topology

1. `PressTalk.App` starts and supervises MVP runtime services.
2. `PressTalk.Engine` orchestrates one voice session lifecycle.
3. `PressTalk.Audio` captures audio from selected microphone.
4. `PressTalk.Asr` performs local ASR inference.
5. `PressTalk.Normalize` transforms raw text into final text.
6. `PressTalk.Commit` writes final text into focused app.
7. `PressTalk.Data` records utterance history.
8. `PressTalk.Tsf` is isolated for post-MVP IME mode.

## Data Flow

1. Key down event starts audio capture.
2. Engine receives audio and calls ASR backend.
3. ASR returns raw text.
4. Normalizer produces commit-ready text.
5. Commit module writes text to target app.
6. Data module persists `(raw, normalized, metadata)` to SQLite.

## Interface Rules

1. Cross-module communication uses interfaces in `PressTalk.Contracts`.
2. Implementations in feature modules must not depend on UI module.
3. TSF module must communicate with engine via IPC only.
4. LLM rewrite interface is optional and must have deterministic fallback.

## Reliability Rules

1. Session timeout is enforced by engine.
2. Any stage failure must not crash process.
3. Commit failure must be observable in logs with target app metadata.
4. History writes must be best-effort and non-blocking to commit path.

